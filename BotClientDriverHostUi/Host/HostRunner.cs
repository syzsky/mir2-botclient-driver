using System.IO;
using BotClient.ClientDriver;
using BotClient.ClientDriver.Sniff;
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
            Emit($"[地图] {runtime.CurrentMap}｜{runtime.Player.MapName}｜({runtime.Player.PosX},{runtime.Player.PosY})");
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
            if (host.AutoConfigure())
            {
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
            }
            else
            {
                Emit("[ui] 未识别到客户端连接：确认客户端已启动并已登录，必要时点“重新识别客户端”");
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
        if (host == null)
        {
            Emit("[ui] 尚未启动，先点“开始挂机”");
            return;
        }

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
            Emit($"[扫描] 候选客户端 {cands.Count} 条" +
                 (Driver.TargetPid > 0 ? $"（当前已锁定 pid={Driver.TargetPid}）" : "（当前为自动挑选）"));
            return cands;
        }
        catch (Exception ex)
        {
            Emit("[错误] 扫描客户端失败: " + ex.Message);
            return new List<ClientCandidate>();
        }
    }

    /// <summary>
    /// 应用列表中选中的那条候选：钉住 PID + 进程名 + 服务端地址，写回 clientdriver.json。
    /// 运行中的话立即用新目标重新识别一次。
    /// </summary>
    public void ApplyCandidate(ClientCandidate cand)
    {
        Driver.ProcessName = cand.ProcessName;
        Driver.TargetPid = cand.Pid;
        if (!string.IsNullOrWhiteSpace(cand.ServerIp)) Driver.ServerIp = cand.ServerIp;

        Emit($"[选择] 目标客户端：{cand.ProcessName}(pid={cand.Pid}) → {cand.ServerIp}:{cand.ServerPort}" +
             (cand.HasWindow ? $"｜窗口=\"{cand.WindowTitle}\"" : "｜（无可见窗口）"));

        try
        {
            Driver.Save(_driverPath);
            Emit($"[选择] 已写回 {_driverPath}（TargetPid={Driver.TargetPid}，进程重启后 PID 变化会自动退回按名称匹配）");
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
        try
        {
            Driver.Save(_driverPath);
            Emit("[选择] 已取消 PID 锁定，恢复自动识别");
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

    /// <summary>状态摘要（左侧“状态/定点”页显示，排障时一眼看清当前配置）。</summary>
    public string BuildStatusText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("—— 实例标识（服务器名-区名-角色名，多开时用来区分）——");
        sb.AppendLine("   " + (Identity.IsEmpty ? "未设置（运行中会从窗口标题/服务端角色信息自动补全）" : Identity.Describe()));
        sb.AppendLine();
        sb.AppendLine("—— 客户端驱动（clientdriver.json）——");
        sb.AppendLine("   进程名        : " + Driver.ProcessName);
        sb.AppendLine("   服务端 IP     : " + (string.IsNullOrWhiteSpace(Driver.ServerIp) ? "（未识别）" : Driver.ServerIp));
        sb.AppendLine("   窗口标题关键词: " + (string.IsNullOrWhiteSpace(Driver.WindowTitleKeyword) ? "（不限）" : Driver.WindowTitleKeyword));
        sb.AppendLine("   抢前台        : " + (Driver.EnsureForeground ? "开" : "关"));
        sb.AppendLine("   视图校准      : " + (Driver.View.IsCalibrated ? $"已校准（格 {Driver.View.CellWidth}x{Driver.View.CellHeight}）" : "未校准（先跑 --calibrate --quick）"));
        sb.AppendLine();
        sb.AppendLine("—— 抓包 / 运行 ——");
        sb.AppendLine("   抓包状态      : " + SniffState);
        sb.AppendLine("   运行状态      : " + (IsRunning ? (SniffOnly ? "仅嗅探" : "挂机中") : "未启动"));
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
