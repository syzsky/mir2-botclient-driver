using System.IO;
using BotClient.ClientDriver;
using BotClient.ClientDriver.Hunt;
using BotClient.ClientDriver.Sniff;
using BotClient.Core.Host;
using BotClient.Human;
using BotClient.Net;
using BotClient.Session;
using BotClient.Session.Combat;
using BotClientDriverHost;

namespace BotClientDriverHostUi.Host;

/// <summary>
/// 界面版宿主：把 BotClientDriverHost 的“复用模式”流程（识别客户端 → 跟随连接 → 挂载动作驱动 →
/// 嗅探回注 → AI 挂机）搬进 WPF 进程，且**不自己做登录** —— 账号/密码/服务端地址全部取自
/// 用户自己客户端的登录过程。
///
/// 与无界面宿主共用同一份 botsettings.json / clientdriver.json，因此两边可以交替运行、互相对照排障。
/// </summary>
public sealed class HostRunner
{
    private readonly string _baseDir;
    private readonly string _settingsPath;
    private readonly string _driverPath;

    private BotSession? _session;
    private CancellationTokenSource? _cts;

    /// <summary>启动时没识别到客户端 → 置位；界面定时器据此周期性只读重试跟随（不产生任何点击）。</summary>
    private bool _awaitClientFollow;

    /// <summary>"等待客户端连接"状态下，上一次真正尝试跟随的 tick 计数（避免每 500ms 都全量扫进程）。</summary>
    private int _followTicks;

    /// <summary>左下角图例的只读视觉读数（截屏 + 系统 OCR）。懒加载：第一次用到才建 OCR 引擎。</summary>
    private Vision.ScreenCornerReader? _cornerReader;

    /// <summary>视觉核对互斥：同一时刻只跑一次（周期 + 换图 + 手动可能撞车）。</summary>
    private int _cornerBusy;

    /// <summary>上一次核对时间，用于按 IntervalSeconds 节流。</summary>
    private DateTime _lastCornerUtc = DateTime.MinValue;

    public HostRunner(string baseDir)
    {
        _baseDir = baseDir;
        _settingsPath = Path.Combine(baseDir, "botsettings.json");
        _driverPath = Path.Combine(baseDir, "clientdriver.json");

        Settings = HostSettings.Load(_settingsPath);
        try
        {
            Driver = ClientDriverConfig.Load(_driverPath);
        }
        catch (Exception ex)
        {
            Log?.Invoke("[ui] 读取驱动配置失败（用默认值）: " + ex.Message);
            Driver = new ClientDriverConfig();
        }

        Identity = Driver.Identity?.Clone() ?? new InstanceIdentity();
    }

    /// <summary>界面日志出口：所有 [标签] 前缀的消息都从这里出去。</summary>
    public event Action<string>? Log;

    /// <summary>状态有变化（角色/地图/嗅探/运行中…）时触发，界面据此刷新状态栏与配置摘要。</summary>
    public event Action? StatusChanged;

    public HostSettings Settings { get; private set; }

    public ClientDriverConfig Driver { get; private set; }

    public InstanceIdentity Identity { get; private set; } = new();

    public BotRuntime? Runtime { get; private set; }

    public ClientDriverHost? Host { get; private set; }

    public BotCombatAI? Ai { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>嗅探状态：未开始 / 抓包中 / 环境未就绪 / 启动失败。状态栏直接显示它。</summary>
    public string SniffState { get; private set; } = "未开始";

    /// <summary>仅嗅探：只做状态镜像，不产生任何点击（首次验证抓包链路用）。</summary>
    public bool SniffOnly { get; set; }

    public string SettingsPath => _settingsPath;

    public string DriverPath => _driverPath;

    public string ConfigDir => _baseDir;

    // ------------------------------------------------------------------ 启动

    /// <summary>启动（后台线程执行，界面不阻塞）。已启动时直接返回 true。</summary>
    public Task<bool> StartAsync() => Task.Run(StartCore);

    private bool StartCore()
    {
        if (_session != null)
        {
            Emit("[ui] 已经在运行中，无需重复启动");
            return true;
        }

        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        Settings = HostSettings.Load(_settingsPath);
        try
        {
            Driver = ClientDriverConfig.Load(_driverPath);
        }
        catch (Exception ex)
        {
            Emit("[ui] 驱动配置读取失败（用默认值）: " + ex.Message);
        }

        Identity = Driver.Identity?.Clone() ?? new InstanceIdentity();

        // 拟真配置合一：botsettings.json 是唯一入口（与无界面宿主完全一致，避免“看起来开了实际没生效”）
        Driver.Human = Settings.Human ?? new HumanTuning();
        Driver.Skills = Settings.Skills ?? new SkillRotationPlan();
        try
        {
            Emit("[拟真] 拟人化 " + HumanTiming.Describe(Driver.Human));
            Emit(Driver.Skills.Enabled
                ? $"[拟真] 技能循环 开（{Driver.Skills.Slots?.Count ?? 0} 个槽位）"
                : "[拟真] 技能循环 关（使用配置的单一法术或物理攻击）");
        }
        catch (Exception ex)
        {
            Emit("[拟真] 参数摘要输出跳过: " + ex.Message);
        }

        var session = new BotSession();
        var runtime = new BotRuntime(session);
        _session = session;
        Runtime = runtime;

        runtime.Log += m => Emit("[runtime] " + m);
        runtime.SystemMessage += m => Emit("[系统] " + m);
        runtime.ChatMessage += m => Emit("[聊天] " + m);
        runtime.MapChanged += () =>
        {
            Emit($"[地图] {runtime.CurrentMap}｜{runtime.Player.MapName}｜({runtime.Player.PosX},{runtime.Player.PosY})");
            // 换图是"数据源最容易出错"的时刻（编号变了、图例重画），顺手做一次只读视觉核对：
            // 画面还没重画完（有淡入），延后 800ms 再读。
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(800).ConfigureAwait(false); }
                catch { /* 忽略 */ }
                await CrossCheckCornerAsync("换图", force: false).ConfigureAwait(false);
            });
        };
        runtime.Died += () => Emit("[危险] 角色死亡");
        runtime.LevelUp += lv => Emit($"[成长] 等级 → {lv}");
        runtime.StateChanged += OnPlayerStateChanged;
        runtime.StartReceiveLoop(ct);

        var host = new ClientDriverHost(Driver);
        Host = host;
        host.Log += m => Emit(m);
        host.Identity = Identity;

        Emit("[ui] 复用模式：不登录，账号/密码/服务端地址全部取自你自己的客户端登录过程");
        Emit("[ui] 自动识别客户端进程与服务端地址…");
        try
        {
            bool identified = host.AutoConfigure();
            // 状态判定集中在 ClientFollowPolicy：识别到才算已绑定，没识别到一律进入「等待客户端」。
            _awaitClientFollow = ClientFollowPolicy.DecideAfterStart(identified, !string.IsNullOrWhiteSpace(Settings.Host))
                                 == ClientBindState.WaitingClient;
            if (identified)
            {
                Emit($"[ui] 已锁定目标客户端：进程={Driver.ProcessName} / pid={Driver.TargetPid} / " +
                     $"句柄={(Driver.TargetHwnd != 0 ? "0x" + Driver.TargetHwnd.ToString("X") : "—")} / " +
                     $"服务端={(string.IsNullOrWhiteSpace(Driver.ServerIp) ? "（等连接自行跟随）" : Driver.ServerIp)}");
                try
                {
                    Driver.Save(_driverPath);
                    Emit($"[ui] 识别结果已写回 {_driverPath}（下次启动可直接复用）");
                }
                catch (Exception ex)
                {
                    Emit("[ui] 写回配置失败（不影响本次运行）: " + ex.Message);
                }
            }
            else if (!string.IsNullOrWhiteSpace(Settings.Host))
            {
                Driver.ServerIp = Settings.Host;
                Emit($"[ui] 自动识别未命中，退回 botsettings.json 的 Host={Settings.Host}");
                Emit("[ui] 本次启动处于“等待客户端”状态：客户端登录进游戏后会自动跟上");
            }
            else
            {
                // 没识别到目标时**不假装已经在挂机**：AI 在会话未连接时不发任何包，
                // 客户端登录后由界面定时器自动重试跟随；也可随时用「扫描客户端」人工指定。
                Emit("[ui] 尚未识别到客户端目标：本次启动不产生任何动作，处于“等待客户端连接”状态。");
                Emit("[ui] 收尾方式：① 启动客户端并登录，宿主自动跟上；② 点「扫描客户端」人工选定目标后即跟随。");
            }
        }
        catch (Exception ex)
        {
            Emit("[ui] 自动识别异常: " + ex.Message);
        }

        try
        {
            MergeIdentity(InstanceIdentity.FromWindowTitle(host.Input.GetWindowTitle()), "客户端窗口标题");
        }
        catch (Exception ex)
        {
            Emit("[标识] 窗口标题解析跳过: " + ex.Message);
        }

        host.Credentials.Captured += cred =>
            Emit($"[ui] 已复用客户端登录凭据：{cred.Describe()}（仅存内存，不写入任何文件）");

        var attachment = new UiAttachment(session, runtime);
        attachment.Log += m => Emit("[ui] " + m);
        host.Attach(attachment);
        runtime.NpcMessage += (id, text) => host.FeedNpcDialog(id, text);
        runtime.SystemMessage += text => host.FeedSystemMessage(text);

        if (!EnsureNpcap())
        {
            SniffState = "环境未就绪";
            Emit("[错误] 抓包环境未就绪：挂机不会生效。处理后可点“抓包检测”重试，或重启本程序。");
        }
        else if (!TryStartSniffing(host))
        {
            SniffState = "启动失败";
        }
        else
        {
            SniffState = "抓包中";
            try
            {
                CalibrationArchives.TryAutoApply(Driver, _driverPath, host.Input, out string calMsg);
                Emit("[ui] " + calMsg);
            }
            catch (Exception ex)
            {
                Emit("[ui] 校准档检查跳过: " + ex.Message);
            }
        }

        try
        {
            foreach (string raw in host.SelfCheck().Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length > 0) Emit(line);
            }
        }
        catch (Exception ex)
        {
            Emit("[ui] 自检报告生成失败: " + ex.Message);
        }

        var ai = new BotCombatAI(session, runtime);
        Settings.ApplyTo(ai);
        ai.Log += m => Emit("[ai] " + m);
        Ai = ai;

        if (!SniffOnly)
        {
            ai.Start();
            Emit("[ui] AI 已启动（坐标/地图由客户端真实封包提供）");
        }
        else
        {
            Emit("[ui] 仅嗅探模式：只做状态镜像，不产生任何点击");
        }

        IsRunning = true;
        StatusChanged?.Invoke();
        return true;
    }

    // ------------------------------------------------------------------ 停止

    /// <summary>停止：先停 AI，再拆驱动/会话（在后台线程调用，界面上只做“发出一条停止指令”）。</summary>
    public async Task StopAsync()
    {
        // 先撤任务再停宿主：任务循环里的每一次点击都要靠 host/ai 还活着，反过来会把循环卡在半途
        if (IsHunting) StopHuntTask();
        else HuntState = "未运行";

        try
        {
            Ai?.Stop();
        }
        catch (Exception ex)
        {
            Emit("[ui] 停止 AI 时出错（忽略）: " + ex.Message);
        }

        ClientDriverHost? host = Host;
        BotSession? session = _session;
        Host = null;
        _session = null;

        if (host != null)
        {
            try
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Emit("[ui] 释放驱动失败（忽略）: " + ex.Message);
            }
        }

        if (session != null)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Emit("[ui] 释放会话失败（忽略）: " + ex.Message);
            }
        }

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _cts = null;
        Ai = null;
        Runtime = null;
        IsRunning = false;
        SniffState = "未开始";
        Emit("[ui] 已停止");
        StatusChanged?.Invoke();
    }

    /// <summary>关窗时调用：只发取消信号，不等异步释放（进程随后就退，避免界面卡在关闭上）。</summary>
    public void RequestStop()
    {
        try
        {
            Ai?.Stop();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 忽略
        }
    }

    // ------------------------------------------------------------------ 抓包环境

    /// <summary>Npcap 环境闸门：缺失就拉起随包安装器的交互式向导补装（与无界面宿主一致）。</summary>
    private bool EnsureNpcap()
    {
        NpcapProbeResult probe = NpcapEnvironment.Probe(_baseDir, autoInstall: true, announce: m => Emit("[npcap] " + m));
        switch (probe.Status)
        {
            case NpcapProbeStatus.Ready:
                Emit("[ui] 抓包环境：Npcap 已就绪");
                return true;

            case NpcapProbeStatus.InstalledNow:
                Emit("[ui] " + probe.Message + " 继续启动。");
                return true;

            default:
                Emit("[错误] 抓包环境未就绪: " + probe.Message);
                Emit("[提示] ① 安装 Npcap 时勾选 “WinPcap API-compatible Mode”；② 以管理员身份运行；" +
                     "③ 机器至少有一块已分配 IPv4 的网卡；④ 或把官方 npcap-x.xx.exe 放到本程序同目录后点“抓包检测”");
                return false;
        }
    }

    /// <summary>启动嗅探；失败时给“能照着做”的提示，而不是把底层异常直接甩给用户。</summary>
    private bool TryStartSniffing(ClientDriverHost host)
    {
        try
        {
            host.StartSniffing();
            Emit("[ui] 抓包已启动");
            return true;
        }
        catch (Exception ex) when (IsPcapFailure(ex))
        {
            Emit("[错误] 抓包初始化失败: " + ex.Message);
            Emit("[提示] 请确认：Npcap 已装（WinPcap 兼容模式）＋ 管理员运行 ＋ 客户端已进图且窗口可定位");
            return false;
        }
        catch (Exception ex)
        {
            Emit("[错误] 抓包启动异常: " + ex.Message);
            return false;
        }
    }

    /// <summary>判定异常是否属于“抓包环境问题”（而不是业务逻辑错误），内层异常一并检查。</summary>
    private static bool IsPcapFailure(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) return true;
            if (e.Message.Contains("抓包设备")) return true;
            if (e.GetType().Name.Contains("Pcap")) return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ 界面按钮入口

    /// <summary>重新识别客户端进程与连接（覆盖式），结果写回 clientdriver.json。</summary>
    public void ReDiscover()
    {
        ClientDriverHost? host = Host;
        // 决策集中在 ClientFollowPolicy（纯逻辑，离线自测覆盖）：
        // 未挂机 → 只读识别；已挂机 → 覆盖式重识别。识别客户端**不依赖**挂机是否已经开始。
        if (ClientFollowPolicy.DecideRefresh(host != null) == ClientRefreshAction.IdentifyOffline)
        {
            Emit("[ui] 当前未挂机：按只读方式扫描并识别客户端（选好目标后再点“开始挂机”即跟随）");
            IdentifyClientOffline();
            StatusChanged?.Invoke();
            return;
        }

        if (host == null) return;   // 不可达：DecideRefresh 已在 host==null 时分流到只读识别分支

        try
        {
            host.DumpClientCandidates();
            bool hit = host.AutoConfigure(overwrite: true);
            Emit(hit ? "[ui] 客户端识别完成，结果已更新" : "[ui] 未识别到客户端进程（确认客户端已启动）");
            Driver.Save(_driverPath);
            Emit($"[ui] 结果已写回 {_driverPath}");
        }
        catch (Exception ex)
        {
            Emit("[错误] 重新识别失败: " + ex.Message);
        }

        StatusChanged?.Invoke();
    }

    /// <summary>
    /// 未挂机也能识别客户端：只读扫描本机候选（TCP 连接 ∪ 顶层窗口），挑“最像游戏本体”的一条
    /// 钉住 PID/句柄/进程名并写回 clientdriver.json。
    ///
    /// 这是"先连客户端 → 再挂机"的正确入口：识别与绑定**不依赖** <see cref="IsRunning"/>，
    /// 也不要求客户端已经登录（未登录时靠窗口侧候选同样能认出并绑定句柄）。
    /// 全过程零点击、不碰网络。
    /// </summary>
    public bool IdentifyClientOffline()
    {
        try
        {
            List<ClientCandidate> cands = ClientDiscovery.Discover(Driver, 12);
            if (!string.IsNullOrEmpty(ClientDiscovery.LastDiagnostics)) Emit(ClientDiscovery.LastDiagnostics);

            if (cands.Count == 0)
            {
                Emit("[识别] 本机没有可选候选：先启动游戏客户端（未登录也能被窗口侧认出）再点此处；" +
                     "仍为空请点「扫描客户端」勾「显示全部窗口（兜底）」人工指定。");
                return false;
            }

            ClientCandidate best = cands.FirstOrDefault(c => c.LikelyGame) ?? cands[0];
            ApplyCandidate(best);
            Emit($"[识别] 已选定目标：{best.ProcessName}(pid={best.Pid})｜{best.Kind}｜" +
                 (best.HasWindow ? $"窗口={best.HwndText} \"{best.WindowTitle}\"" : "（无可见窗口）"));
            return true;
        }
        catch (Exception ex)
        {
            Emit("[错误] 识别客户端失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>是否处于“等待客户端连接”状态（启动时没识别到目标，挂机在等客户端）。</summary>
    public bool AwaitClientFollow => _awaitClientFollow;

    /// <summary>状态栏"视觉核对"芯片文案：一致 / 有差异 / 不可读 / 未绑定 / 已关闭。</summary>
    public string CornerChipText { get; private set; } = "—";

    /// <summary>最近一次核对结论（悬浮提示/日志用）。</summary>
    public string? LastCornerCheck { get; private set; }

    // ------------------------------------------------------------------ 视觉核对（只读）

    /// <summary>
    /// 界面定时器每 500ms 调一次：按配置间隔做周期核对（内部节流，未到点直接返回）。
    /// 只在已开始挂机后自动跑 —— 等待客户端阶段由用户点按钮手动核对。
    /// </summary>
    public void MaybePeriodicCornerCheck()
    {
        CornerOcrOptions o = Driver.Ocr ?? new CornerOcrOptions();
        if (!o.Enabled || o.IntervalSeconds <= 0) return;
        if (Runtime == null) return;
        if ((DateTime.UtcNow - _lastCornerUtc).TotalSeconds < o.IntervalSeconds) return;
        _ = Task.Run(() => CrossCheckCornerAsync("周期", force: false));
    }

    /// <summary>手动核对（界面按钮）：无视间隔与总开关，立刻跑一次。</summary>
    public void RequestCornerCheck() => _ = Task.Run(() => CrossCheckCornerAsync("手动", force: true));

    /// <summary>
    /// 读一次画面左下角并与嗅探数据比对。**只写日志与状态栏，不改任何动作**。
    /// 失败一律降级成"不可读 + 原因"，绝不抛异常影响挂机主流程。
    /// </summary>
    public async Task CrossCheckCornerAsync(string reason, bool force)
    {
        CornerOcrOptions o = Driver.Ocr ?? new CornerOcrOptions();
        if (!o.Enabled && !force)
        {
            CornerChipText = "已关闭";
            return;
        }
        if (Interlocked.Exchange(ref _cornerBusy, 1) == 1) return;

        try
        {
            long hwnd = Driver.TargetHwnd;
            if (hwnd == 0)
            {
                CornerChipText = "未绑定";
                LastCornerCheck = "未绑定客户端窗口，视觉核对跳过";
                if (force) Emit("[核对] 未绑定客户端窗口：先「扫描客户端」或让宿主跟随上客户端后再试");
                return;
            }

            _cornerReader ??= new Vision.ScreenCornerReader();
            if (!_cornerReader.Available)
            {
                CornerChipText = "无 OCR";
                LastCornerCheck = _cornerReader.EngineNote;
                Emit("[核对] 无法启用视觉核对：" + _cornerReader.EngineNote);
                return;
            }

            _lastCornerUtc = DateTime.UtcNow;
            BotClient.Vision.CornerReading reading = await _cornerReader.ReadAsync(
                hwnd, o.BandWidth, o.BandHeight, o.LeftOffset, o.BottomOffset, o.Scale).ConfigureAwait(false);

            if (!reading.Readable)
            {
                CornerChipText = "不可读";
                LastCornerCheck = _cornerReader.LastError ?? "区域无文字";
                Emit($"[核对] ({reason}) 画面读数不可用：{LastCornerCheck}");
                return;
            }

            BotRuntime? rt = Runtime;
            if (rt == null)
            {
                CornerChipText = reading.Describe();
                LastCornerCheck = reading.Describe();
                Emit($"[核对] ({reason}) 仅画面读数：{reading.Describe()}（尚未开始挂机，无嗅探数据可比对）");
                return;
            }

            BotPlayerState p = rt.Player;
            string expectMap = p.MapName;   // MapInfo 没读到时它就是编号，比对逻辑里两者都会试
            BotClient.Vision.CornerCheck check = BotClient.Vision.MapCornerParser.Compare(
                reading, expectMap, rt.CurrentMap, p.PosX, p.PosY, o.PosTolerance);

            CornerChipText = check.Ok ? "一致" : (check.Verdict == BotClient.Vision.CornerVerdict.NotReadable ? "不可读" : "有差异");
            LastCornerCheck = check.Describe();
            Emit($"[核对] ({reason}) {check.Describe()}");
        }
        catch (Exception ex)
        {
            CornerChipText = "异常";
            LastCornerCheck = ex.Message;
            Emit("[核对] 视觉核对异常: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _cornerBusy, 0);
        }
    }

    /// <summary>
    /// 界面定时器调用（500ms 一次）：处于“等待客户端”状态时，每约 5 秒做一次**只读**重试跟随，
    /// 客户端一登录（或窗口一出现）就自动接上，避免"没先挂机就连不上、连不上又没法挂机"的死循环。
    /// 全程零点击、不改任何交互。
    /// </summary>
    public void RetryFollowClient()
    {
        // 节流决策集中在 ClientFollowPolicy：等待中 + 已挂机 + 每约 5 秒一次（500ms × 10）。
        if (!ClientFollowPolicy.ShouldRetryFollow(_awaitClientFollow, IsRunning, ++_followTicks)) return;

        try
        {
            List<ClientCandidate> cands = ClientDiscovery.Discover(Driver, 8);
            if (!ClientFollowPolicy.ShouldAdoptOnRetry(cands.Count)) return;
            ClientCandidate? best = cands.FirstOrDefault(c => c.LikelyGame) ?? cands[0];

            _awaitClientFollow = false;
            Emit($"[ui] 客户端已出现，自动跟上：{best.ProcessName}(pid={best.Pid})" +
                 (best.HasWindow ? $"｜窗口={best.HwndText} \"{best.WindowTitle}\"" : string.Empty) +
                 (string.IsNullOrWhiteSpace(best.ServerIp)
                     ? "（尚未连服务端，登录后抓包侧自行跟随）"
                     : $"｜服务端={best.ServerIp}:{best.ServerPort}"));

            if (Host != null) ReDiscover();
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Emit("[错误] 自动跟随客户端失败: " + ex.Message);
        }
    }


    // ------------------------------------------------------------------ 扫描并选择客户端（跨引擎）

    /// <summary>
    /// 扫描本机**所有**候选传奇客户端进程及其到服务端的连接，逐条列出（不只看评分最高的一个）。
    /// 纯只读：不动配置、不碰网络。引擎无关 —— 只要是持有外部 TCP 连接的客户端进程都会进候选，
    /// 包含黑名单过滤（浏览器/聊天工具等）与评分排序。
    /// </summary>
    public List<ClientCandidate> ScanClients(int max = 12)
    {
        try
        {
            List<ClientCandidate> cands = ClientDiscovery.Discover(Driver, max);
            int gameLike = cands.Count(c => c.LikelyGame);
            Emit($"[扫描] 候选客户端 {cands.Count} 条（判为游戏本体 {gameLike} 条）" +
                 (Driver.TargetHwnd != 0
                     ? $"（当前已绑定 句柄=0x{Driver.TargetHwnd:X} / pid={Driver.TargetPid}）"
                     : Driver.TargetPid > 0 ? $"（当前已锁定 pid={Driver.TargetPid}，未绑定句柄）" : "（当前为自动挑选）"));

            var best = cands.FirstOrDefault();
            if (best != null) Emit($"[扫描] 建议跟随：{best}");
            // 把"为什么是这些候选"原样打出来（读表失败 / 命中排除名单 / 只有回环连接 / 评分不足）
            if (!string.IsNullOrEmpty(ClientDiscovery.LastDiagnostics)) Emit(ClientDiscovery.LastDiagnostics);
            if (cands.Count > 0 && gameLike == 0)
                Emit("[扫描] 注意：没有一行像『游戏本体』（渲染窗特征 + 非 HTTP 端口）——列表里多为登录器/更新器的 " +
                     "HTTP 连接或小工具窗。请把游戏客户端启动并**登录进游戏**后再点「重新扫描」。（判据与分辨率无关，全屏/800×600 都能认出）");

            return cands;
        }
        catch (Exception ex)
        {
            Emit("[错误] 扫描客户端失败: " + ex.Message);
            return new List<ClientCandidate>();
        }
    }

    /// <summary>
    /// 应用列表中选中的那条候选：钉住 PID + 窗口句柄 + 进程名 + 服务端地址，写回 clientdriver.json。
    /// 运行中的话立即用新目标重新识别一次。
    /// </summary>
    public void ApplyCandidate(ClientCandidate cand)
    {
        // 人工选定目标 = 已经"连上客户端"，退出等待状态（若挂机在等，下一次嗅探/识别即跟上）。
        _awaitClientFollow = false;
        _followTicks = 0;

        Driver.ProcessName = cand.ProcessName;
        Driver.TargetPid = cand.Pid;
        Driver.TargetHwnd = cand.Hwnd;   // 句柄绑定：分辨率/标题变化不影响，失效时自动退回按 PID 匹配
        if (!string.IsNullOrWhiteSpace(cand.ServerIp)) Driver.ServerIp = cand.ServerIp;

        Emit($"[选择] 目标客户端：{cand.ProcessName}(pid={cand.Pid}) [{cand.Kind}] → {cand.ServerIp}:{cand.ServerPort}" +
             (cand.HasWindow
                 ? $"｜窗口={cand.HwndText} \"{cand.WindowTitle}\" {cand.WindowSize}"
                 : "｜（无可见窗口，未绑定句柄）"));
        if (!cand.LikelyGame)
            Emit($"[选择] 注意：这条判为「{cand.Kind}」（{cand.PortKind}）——若随后抓不到游戏流量，" +
                 "请重新扫描并选『类型=游戏』那一条（渲染窗特征 + 非 HTTP 端口）。");

        try
        {
            Driver.Save(_driverPath);
            Emit($"[选择] 已写回 {_driverPath}（TargetPid={Driver.TargetPid} / 句柄=0x{Driver.TargetHwnd:X}；" +
                 "进程重启后 PID 与句柄都会失效，届时自动退回按名称匹配）");
        }
        catch (Exception ex)
        {
            Emit("[错误] 写回 clientdriver.json 失败: " + ex.Message);
        }

        if (Host != null) ReDiscover();
        StatusChanged?.Invoke();
    }

    /// <summary>清除锁定，退回"自动挑评分最高的候选"。</summary>
    public void ClearCandidatePin()
    {
        Driver.TargetPid = 0;
        Driver.TargetHwnd = 0;
        try
        {
            Driver.Save(_driverPath);
            Emit("[选择] 已取消 PID/句柄绑定，恢复自动识别");
        }
        catch (Exception ex)
        {
            Emit("[错误] 写回 clientdriver.json 失败: " + ex.Message);
        }
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// 自动探测帧定界档（换引擎/换服时用）：只读采样 N 秒，跑 SplitterAutoDetector，
    /// 有把握才写回 Framing，并把置信度理由打进日志。全程零点击、不介入连接。
    /// </summary>
    public async Task<int> AutoFrameAsync(int seconds = 15)
    {
        ClientDriverHost? host = Host;
        if (host == null)
        {
            Emit("[定界] 尚未启动：先在客户端里登录进游戏，再点“开始挂机”，然后才能采样探测");
            return -1;
        }

        try
        {
            Emit($"[定界] 开始只读采样 {seconds} 秒（零点击）。期间请在客户端里走两步、开个背包，让数据包出现…");
            int rc = await host.AutoFrameAsync(seconds);
            if (rc == 0)
            {
                try
                {
                    Driver.Save(_driverPath);
                    Emit($"[定界] 探测结果已写回 {_driverPath}");
                }
                catch (Exception ex)
                {
                    Emit("[定界] 写回配置失败（本次运行仍生效）: " + ex.Message);
                }
                await Task.Run(() => ReDiscover());
            }
            else
            {
                Emit("[定界] 未探测出可信的定界参数，保持原样（可在设置里手工填魔术字/长度偏移）");
            }
            return rc;
        }
        catch (Exception ex)
        {
            Emit("[错误] 自动定界失败: " + ex.Message);
            return -2;
        }
    }

    /// <summary>抓包环境检测（缺失时按需补装 Npcap）。</summary>
    public void CheckNpcap()
    {
        Emit("[npcap] 开始检测抓包环境…");
        try
        {
            NpcapProbeResult probe = NpcapEnvironment.Probe(_baseDir, autoInstall: true, announce: m => Emit("[npcap] " + m));
            Emit($"[npcap] 结果: {probe.Status}｜{probe.Message}");
            Emit($"[npcap] 驱动在位={NpcapEnvironment.IsDriverPresent()}｜{NpcapEnvironment.Describe()}");

            if (probe.Status is NpcapProbeStatus.Ready or NpcapProbeStatus.InstalledNow && Host != null && SniffState != "抓包中")
            {
                if (TryStartSniffing(Host)) SniffState = "抓包中";
                StatusChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Emit("[错误] 抓包环境检测失败: " + ex.Message);
        }
    }

    /// <summary>环境自检报告（窗口/身份/校准/嗅探链路）。</summary>
    public void RunSelfCheck()
    {
        ClientDriverHost? host = Host;
        if (host == null)
        {
            Emit("[ui] 尚未启动，先点“开始挂机”");
            return;
        }

        try
        {
            foreach (string raw in host.SelfCheck().Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length > 0) Emit(line);
            }
        }
        catch (Exception ex)
        {
            Emit("[错误] 自检失败: " + ex.Message);
        }
    }

    /// <summary>保存 botsettings.json，并把新参数立刻应用到正在跑的 AI。</summary>
    public void ApplySettings()
    {
        try
        {
            Settings.Save(_settingsPath);
            Emit($"[ui] 设置已保存: {_settingsPath}");
        }
        catch (Exception ex)
        {
            Emit("[错误] 设置保存失败: " + ex.Message);
        }

        if (Ai != null)
        {
            try
            {
                Settings.ApplyTo(Ai);
                Emit("[ui] 新参数已应用到运行中的 AI");
            }
            catch (Exception ex)
            {
                Emit("[错误] 参数应用失败: " + ex.Message);
            }
        }

        StatusChanged?.Invoke();
    }

    /// <summary>定点挂机：只打指定格。</summary>
    public void SetFightPoint(int x, int y)
    {
        if (Ai == null)
        {
            Emit("[ui] 尚未启动，先点“开始挂机”");
            return;
        }

        Ai.FightAtPoint = true;
        Ai.FightPointX = x;
        Ai.FightPointY = y;
        Emit($"[ui] 定点挂机已启用: ({x},{y})");
        StatusChanged?.Invoke();
    }

    public void ClearFightPoint()
    {
        if (Ai == null) return;
        Ai.FightAtPoint = false;
        Emit("[ui] 已取消定点挂机（回到 AI 默认行为）");
        StatusChanged?.Invoke();
    }

    // ------------------------------------------------------------------ 挂机任务（任务编辑器驱动）

    /// <summary>是否有界面任务正在跑（“执行选中任务/停止任务”按钮据此切换）。</summary>
    public bool IsHunting { get; private set; }

    /// <summary>任务状态一句话：未运行 / 运行中：xx / 已结束（n 轮，累计击杀 m 只）…</summary>
    public string HuntState { get; private set; } = "未运行";

    private CancellationTokenSource? _huntCts;

    /// <summary>
    /// 执行一条在任务编辑器里配好的挂机任务。
    ///
    /// 刻意**不另起一套执行逻辑**：编排层仍是 HuntTaskRunner（与命令行 --hunt 同一条链），
    /// 界面/命令行两边排障时看到的是同一批日志格式与同一套判定，不会出现"界面能跑命令行不能跑"。
    /// 本方法只负责三件事：查前置（宿主在跑 / 不在仅嗅探）、起一个可取消的后台循环、把每轮结论吐到日志。
    /// </summary>
    public bool StartHuntTask(HuntTask task)
    {
        if (task == null) return false;

        if (string.IsNullOrWhiteSpace(task.TargetMapText))
        {
            Emit("[任务] 未填目标地图名，无法执行（点“编辑”补上）");
            return false;
        }

        BotRuntime? runtime = Runtime;
        ClientDriverHost? host = Host;
        if (!IsRunning || runtime == null || host == null)
        {
            Emit("[任务] 尚未启动：先点“开始挂机”，等角色信息上来后再执行任务");
            return false;
        }

        if (IsHunting)
        {
            Emit("[任务] 已有任务在跑，先点“停止任务”");
            return false;
        }

        if (SniffOnly)
        {
            Emit("[任务] 仅嗅探模式不产生任何点击，任务不会执行（取消勾选后再试）");
            return false;
        }

        BotCombatAI? ai = Ai;
        HuntPlan plan = task.ToPlan();
        var cts = new CancellationTokenSource();
        _huntCts = cts;
        IsHunting = true;
        HuntState = "运行中：" + task.DisplayName;

        Emit($"[任务] 开始执行「{task.DisplayName}」：{plan.Describe()}");
        if (plan.MinLevel > 0)
        {
            int level = runtime.Player.Level;
            Emit($"[任务] 等级门槛 {plan.MinLevel}，当前角色 {level} 级"
                 + (level < plan.MinLevel ? "（不满足，本轮不会动手）" : "（满足）"));
        }

        StatusChanged?.Invoke();

        CancellationToken ct = cts.Token;
        _ = Task.Run(async () =>
        {
            var hunter = new HuntTaskRunner(runtime, host, ai);
            hunter.Log += m => Emit("[任务] " + m);
            int round = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    round++;
                    Emit($"[任务] ===== 第 {round} 轮开始 =====");
                    HuntReport report = await hunter.RunAsync(plan, ct).ConfigureAwait(false);
                    Emit($"[任务] 第 {round} 轮结束：{report.Summary}");
                    foreach (HuntFloorReport floor in report.Floors) Emit("[任务]   " + floor);

                    if (!task.Loop) break;

                    Emit($"[任务] {plan.LoopIntervalMs / 1000}s 后开始下一轮（点“停止任务”结束）");
                    await Task.Delay(plan.LoopIntervalMs, ct).ConfigureAwait(false);
                }

                HuntState = $"已结束（{round} 轮，累计击杀 {hunter.TotalKilled} 只）";
            }
            catch (OperationCanceledException)
            {
                HuntState = $"已停止（{round} 轮，累计击杀 {hunter.TotalKilled} 只）";
            }
            catch (Exception ex)
            {
                HuntState = "异常结束：" + ex.Message;
                Emit("[任务] 执行异常: " + ex.Message);
            }
            finally
            {
                IsHunting = false;
                _huntCts = null;
                try { cts.Dispose(); } catch { /* 忽略 */ }
                Emit($"[任务] {HuntState}");
                StatusChanged?.Invoke();
            }
        });

        return true;
    }

    /// <summary>停止当前任务：只发取消信号，等循环自己收尾（不硬杀线程，避免点一次卡一次）。</summary>
    public void StopHuntTask()
    {
        if (!IsHunting)
        {
            Emit("[任务] 当前没有任务在跑");
            return;
        }

        Emit("[任务] 正在停止任务…");
        try
        {
            _huntCts?.Cancel();
        }
        catch (Exception ex)
        {
            Emit("[任务] 发送停止信号失败: " + ex.Message);
        }
    }

    /// <summary>顶部「客户端」芯片的短文本：未绑定 / 等待连接… / pid=xx(已绑定)。</summary>
    public string ClientChipText
        => ClientFollowPolicy.ChipText(Driver.TargetPid, Driver.TargetHwnd, _awaitClientFollow);

    /// <summary>
    /// 目标客户端的一句话描述：未绑定时提示"自动挑选"，绑定时给出 pid/句柄，等待客户端时给出等待提示。
    /// 用于状态摘要 —— 让"到底有没有连上客户端"一眼可见。
    /// </summary>
    public string DescribeClientTarget()
        => ClientFollowPolicy.DescribeTarget(Driver.ProcessName, Driver.TargetPid, Driver.TargetHwnd, _awaitClientFollow);

    /// <summary>状态摘要（左侧“状态/定点”页显示，排障时一眼看清当前配置）。</summary>
    public string BuildStatusText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("—— 实例标识（服务器名-区名-角色名，多开时用来区分）——");
        sb.AppendLine("   " + (Identity.IsEmpty ? "未设置（运行中会从窗口标题/服务端角色信息自动补全）" : Identity.Describe()));
        sb.AppendLine();
        sb.AppendLine("—— 客户端驱动（clientdriver.json）——");
        sb.AppendLine("   目标客户端    : " + DescribeClientTarget());
        sb.AppendLine("   进程名        : " + Driver.ProcessName);
        sb.AppendLine("   服务端 IP     : " + (string.IsNullOrWhiteSpace(Driver.ServerIp) ? "（未识别）" : Driver.ServerIp));
        sb.AppendLine("   窗口标题关键词: " + (string.IsNullOrWhiteSpace(Driver.WindowTitleKeyword) ? "（不限）" : Driver.WindowTitleKeyword));
        sb.AppendLine("   抢前台        : " + (Driver.EnsureForeground ? "开" : "关"));
        sb.AppendLine("   视图校准      : " + (Driver.View.IsCalibrated ? $"已校准（格 {Driver.View.CellWidth}x{Driver.View.CellHeight}）" : "未校准（先跑 --calibrate --quick）"));
        sb.AppendLine();
        sb.AppendLine("—— 抓包 / 运行 ——");
        sb.AppendLine("   抓包状态      : " + SniffState);
        sb.AppendLine("   运行状态      : " + ClientFollowPolicy.RunningStateNote(IsRunning, SniffOnly, _awaitClientFollow));
        sb.AppendLine("   挂机任务      : " + HuntState);
        BotRuntime? rt = Runtime;
        if (rt != null)
        {
            sb.AppendLine("   角色          : " + (string.IsNullOrWhiteSpace(rt.Player.Name) ? "（等待服务端角色信息）" : rt.Player.Name));
            sb.AppendLine("   当前地图      : " + (string.IsNullOrWhiteSpace(rt.CurrentMap) ? "（未识别）" : rt.CurrentMap));
            sb.AppendLine("   视野内实体    : " + rt.Dots.Count + " 个");
        }

        sb.AppendLine();
        sb.AppendLine("—— 配置文件 ——");
        sb.AppendLine("   " + _settingsPath);
        sb.AppendLine("   " + _driverPath);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 内部

    private void OnPlayerStateChanged()
    {
        BotRuntime? rt = Runtime;
        if (rt == null) return;

        if (!Identity.HasCharacter && !string.IsNullOrWhiteSpace(rt.Player.Name))
        {
            MergeIdentity(new InstanceIdentity { CharacterName = rt.Player.Name }, "状态通道（服务端角色信息）");
        }
    }

    /// <summary>用新拿到的信息补齐标识空缺项（已有值不覆盖），补齐就写回配置。</summary>
    private void MergeIdentity(InstanceIdentity other, string source)
    {
        if (other == null || !Identity.FillFrom(other)) return;

        Emit($"[标识] 已从{source}补全: {Identity.Describe()}");

        try
        {
            ClientDriverConfig cfg = ClientDriverConfig.Load(_driverPath);
            cfg.Identity = Identity.Clone();
            cfg.Save(_driverPath);
        }
        catch (Exception ex)
        {
            Emit("[标识] 写回配置失败（不影响本次运行）: " + ex.Message);
        }

        StatusChanged?.Invoke();
    }

    private void Emit(string message) => Log?.Invoke(message);
}
