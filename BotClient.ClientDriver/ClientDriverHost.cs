using System.Linq;
using BotClient.Assets;
using BotClient.ClientDriver.Input;
using BotClient.ClientDriver.Sniff;
using BotClient.Net;
using BotClient.Protocol;

namespace BotClient.ClientDriver;

/// <summary>
/// 宿主需要提供给本模块的挂接点。
///
/// 之所以做成接口而不是直接引用 <c>BotSession</c> / <c>BotRuntime</c>：
///   ① 本工程不需要知道 Core 的确切命名空间与方法签名，宿主那边只要写十来行适配；
///   ② 所有会话状态都通过这唯一的窄接口进出，评审时一眼就能看清
///      "客户端驱动模式到底对 Core 做了什么" —— 只有三个动词：设驱动、设兜底、注入帧。
/// </summary>
public interface ISessionAttachment
{
    /// <summary>把动作驱动挂到会话上（此后 BotRuntime 的 Send* 不再写 socket）。</summary>
    void SetDriver(IClientDriver driver);

    /// <summary>把兜底通道挂上（未被 Driver 接管的动作会被丢弃并计数，绝不写字节）。</summary>
    void SetSink(IUpstreamSink sink);

    /// <summary>
    /// 把嗅探到的服务端帧注入状态机 —— 用与直连模式**完全相同**的转换逻辑，
    /// 这样 BotRuntime 分不出区别（这是"状态通道与直连同构"的落点）。
    /// </summary>
    void Inject(MirIncomingFrame frame);

    /// <summary>服务端命令（供 UI 态机判断）。</summary>
    void OnServerCommand(ushort cmd, CmdPack pack);

    /// <summary>当前角色坐标（状态通道维护，来自真实服务端广播）。</summary>
    (int X, int Y) PlayerPosition { get; }

    /// <summary>
    /// 当前地图代码（接 Core 的 <c>BotRuntime.CurrentMap</c>）。
    /// 传送/进图流程靠它判定"换图之后到底到了哪张图"—— 只看"换过图了"是不够的，
    /// 走错图会让后续所有坐标与 NPC 全错位。
    /// </summary>
    string CurrentMap { get; }
}

/// <summary>
/// C 方案的组装根：把抓包 → 重组 → 帧解析 → 状态注入串起来，
/// 并把动作驱动 / 兜底通道挂到会话上。
///
/// 数据流（全部单向、无回写）：
///
///   真机客户端 ⇄ 游戏服务端        ← 唯一的真实连接，本模块从不介入
///         │  (Npcap 只读镜像)
///         ├─ 下行字节 ─→ TcpReassembler ─→ MirFrameCodec ─→ ISessionAttachment.Inject
///         │                                              └→ UiStateProbe.OnServerCommand
///         └─ 上行字节 ─→ TcpReassembler ─→ UpstreamCommandProbe（动作确认）
///                                              └→ UiStateProbe.OnClientCommand
///
///   BotRuntime（原样不动）─→ IClientDriver ─→ 鼠标/键盘 ─→ 真机客户端 ─→ 上行
/// </summary>
public sealed class ClientDriverHost : IAsyncDisposable
{
    private readonly Dictionary<string, MirFrameCodec> _downCodecs = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    private readonly Timer _tickTimer;
    private PacketSniffer? _sniffer;
    private readonly Dictionary<string, List<byte[]>> _samples = new(StringComparer.Ordinal);
    private int _sampleBytes;
    private volatile bool _sampling;
    private ISessionAttachment? _attachment;
    private bool _disposed;

    public ClientDriverHost(ClientDriverConfig config)
    {
        Config = config;
        CmdCatalog = new ReflectionCmdCatalog();
        // 跨服命令码覆盖（持久化在 clientdriver.json）：换服时核一遍命令码填那里即可，不必改 Core 代码
        if (config.CmdOverrides is { Count: > 0 } && CmdCatalog is ReflectionCmdCatalog rc)
            foreach (var kv in config.CmdOverrides)
                rc.Overrides[kv.Key] = kv.Value;

        Input = new InputSimulator(config);
        Mapper = new ScreenMapper(config);
        Gate = new ActionGate(config);
        Ui = new UiStateProbe(CmdCatalog);
        Upstream = new UpstreamCommandProbe();
        Bridge = new ClientInputBridge();
        Credentials = new LoginCredentialProbe();

        _walk = new MapWalkController(config, Mapper, Input, Gate, Ui, () => PlayerPos);
        _driver = new ClientActionDriver(config, Mapper, Input, Gate, Ui, _walk, Upstream, CmdCatalog, () => PlayerPos);
        Transfer = new NpcTransferRunner(config, _driver, Ui, () => PlayerPos, () => CurrentMap);

        // NPC 菜单里的中文地名要能反查成地图代码，才能校验"传送之后到的是不是目标图"。
        // 读不到 MapInfo.txt 不影响运行：MapEntryProbe 会退化成探测式判定（换图即算可进入），
        // 并把实测到的 菜单文字→地图代码 写进缓存，下次这个服直接复用。
        MapInfo = MapInfoFile.TryLoad(config.MapDirHint);
        MapEntry = new MapEntryProbe(config, _driver, Ui, Transfer, () => CurrentMap, MapInfo);

        WireLogging();
        _tickTimer = new Timer(_ => { try { Ui.Tick(); } catch { } }, null, 500, 250);
    }

    private readonly MapWalkController _walk;
    private readonly ClientActionDriver _driver;

    public ClientDriverConfig Config { get; }
    public ICmdCatalog CmdCatalog { get; }
    public InputSimulator Input { get; }
    public ScreenMapper Mapper { get; }
    public ActionGate Gate { get; }
    public UiStateProbe Ui { get; }
    public UpstreamCommandProbe Upstream { get; }
    public ClientInputBridge Bridge { get; }
    public NpcTransferRunner Transfer { get; }

    /// <summary>
    /// 客户端登录凭据嗅探器：账号/密码由玩家自己在客户端登录时产生，
    /// 本模块只从**客户端自己的上行登录包**里还原，不需要用户手填、也不会让 Bot 再登录一次。
    /// 事件：<see cref="LoginCredentialProbe.Captured"/>；最近一次结果：<see cref="LoginCredentialProbe.Latest"/>。
    /// </summary>
    public LoginCredentialProbe Credentials { get; }

    /// <summary>MapInfo.txt（地图代码 ↔ 中文地名）。读不到为 null：进图判定退化为探测式。</summary>
    public MapInfoFile? MapInfo { get; }

    /// <summary>
    /// 子地图可进入性探测器。跨服 NPC 异名 + "点开后一堆子地图不知道哪个能进"的落点：
    /// 先 <see cref="MapEntryProbe.EnterAsync"/> 问单个目标，或
    /// <see cref="MapEntryProbe.ProbeCandidatesAsync"/> 把菜单候选逐个探一遍。
    /// </summary>
    public MapEntryProbe MapEntry { get; }

    private int _npcRange = 15;

    /// <summary>NPC 交互距离闸门（同时同步到动作驱动与换图流程，避免两处配置不一致）。</summary>
    public int NpcRange
    {
        get => _npcRange;
        set
        {
            _npcRange = value;
            _driver.NpcInteractRange = value;
            Transfer.InteractRange = value;
        }
    }

    /// <summary>所有日志的唯一出口。</summary>
    public event Action<string>? Log;

    private (int X, int Y) PlayerPos
        => _attachment?.PlayerPosition ?? (0, 0);

    /// <summary>
    /// 当前地图代码（走状态通道，来自真实服务端包）。
    /// 传送流程用它确认"到的是不是目标图"，而不是只看有没有换过图。
    /// </summary>
    public string CurrentMap => _attachment?.CurrentMap ?? string.Empty;

    // ---------------------------------------------------------------- 状态喂入（宿主转发）

    /// <summary>
    /// 把 Core 解出的 NPC 对话正文喂给 UI 态机（含菜单选项解析）。
    ///
    /// 接线（宿主侧一行）：
    /// <code>runtime.NpcMessage += (id, text) => host.FeedNpcDialog(id, text);</code>
    ///
    /// 不接这一步的后果：<see cref="UiStateProbe.DialogLines"/> 永远为空，
    /// 菜单只能按行号盲点 —— 首页/翻页一变就点错项。
    /// </summary>
    public void FeedNpcDialog(long merchantId, string? rawText) => Ui.FeedNpcDialog(merchantId, rawText);

    /// <summary>
    /// SM_MENU_OK 这类提示/确认框正文。只用于对话态续期，**不当作菜单**解析，
    /// 避免把"你身上钱不够"这种提示文字误判成可点选项。
    ///
    /// 正文会存进 <see cref="UiStateProbe.LastSystemMessage"/>：传送点选失败时，
    /// 服务端就是在这条提示里回绝（等级/金币/物品/负重不足），日志里能直接看到原因。
    ///
    /// 接线：<code>runtime.SystemMessage += text => host.FeedSystemMessage(text);</code>
    /// </summary>
    public void FeedSystemMessage(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) Ui.NoteDialogActivity(text);
    }

    // ---------------------------------------------------------------- 挂接与启动

    /// <summary>
    /// 挂接到宿主会话。调用前请确保客户端已进入游戏（角色已站到地图上），
    /// 因为动作驱动需要窗口可定位、状态需要真实坐标。
    /// </summary>
    public void Attach(ISessionAttachment attachment)
    {
        _attachment = attachment;

        _driver.Attach();
        attachment.SetDriver(_driver);
        attachment.SetSink(Bridge);

        if (CmdCatalog.Missing.Count > 0)
            Log?.Invoke("[host] 以下命令码未解析到，对应能力将被禁用：" + string.Join("、", CmdCatalog.Missing) +
                        "（可在 ReflectionCmdCatalog.Overrides 里手工填写）");

        Log?.Invoke(MapInfo != null
            ? $"[host] 已加载 MapInfo：{MapInfo.Count} 条地名映射（{MapInfo.FilePath}）—— 进图判定可以比对到达图代码"
            : $"[host] 未读到 MapInfo.txt（MapDirHint=\"{Config.MapDirHint}\"，留空则按默认位置找）："
              + "进图判定退化为「换过图就算进入」，地名反查与候选清单会变弱");

        ValidateCalibration();
    }

    // ---------------------------------------------------------------- 零配置自动识别

    /// <summary>
    /// 自动识别"用户在玩哪个客户端、它连的是哪个服务端"，并把结果写回 <see cref="Config"/>。
    ///
    /// 触发时机：客户端**已经启动并登录进游戏**之后（哪怕刚连上登录服也够）。
    /// 取值优先级：进程名（能唯一命中就取最强候选）→ 该进程当前已建立的外部连接（服务端 IP/端口）。
    ///
    /// <paramref name="overwrite"/> = false 时只填"空着的项"，人工填过的值不会被抹掉。
    /// 持久化由宿主负责（本类不知道配置文件路径）：识别成功后宿主自行 <c>Config.Save(path)</c>。
    /// </summary>
    public bool AutoConfigure(bool overwrite = false)
    {
        var cands = ClientDiscovery.Discover(Config, 5);
        if (cands.Count == 0)
        {
            Log?.Invoke("[host] 自动识别失败：没找到像游戏客户端的进程连接。" +
                        "请确认客户端已启动、并已点过登录（此时才有到服务端的 TCP 连接）。");
            return false;
        }

        var best = cands[0];
        bool changed = false;

        if (overwrite || string.IsNullOrWhiteSpace(Config.ProcessName) || Config.ProcessName == "MirClient")
        {
            if (!string.Equals(Config.ProcessName, best.ProcessName, StringComparison.Ordinal))
            {
                Config.ProcessName = best.ProcessName;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(best.ServerIp) &&
            (overwrite || string.IsNullOrWhiteSpace(Config.ServerIp)))
        {
            Config.ServerIp = best.ServerIp;
            changed = true;
        }

        Log?.Invoke($"[host] 自动识别客户端：进程={Config.ProcessName}(pid={best.Pid}) " +
                    $"服务端={Config.ServerIp}:{best.ServerPort} " +
                    (best.HasWindow ? $"窗口=\"{best.WindowTitle}\"" : "（无可见窗口）"));

        if (cands.Count > 1)
        {
            var others = cands.Skip(1).Take(3).Select(c => $"{c.ProcessName}→{c.ServerIp}:{c.ServerPort}");
            Log?.Invoke("[host] 其他候选（若上面挑错了，可在 clientdriver.json 显式指定 ProcessName）：" +
                        string.Join("；", others));
        }

        return changed;
    }

    /// <summary>只打印候选，不改配置 —— 排查"为什么识别不到/识别错"时用。</summary>
    public void DumpClientCandidates()
    {
        var cands = ClientDiscovery.Discover(Config, 8);
        if (cands.Count == 0)
        {
            Log?.Invoke("[host] 候选为空：没有任何进程持有外部 TCP 连接。");
            return;
        }
        Log?.Invoke("[host] 客户端候选：" + Environment.NewLine +
                    string.Join(Environment.NewLine, cands.Select((c, i) => $"  {i + 1}. {c}")));
    }

    /// <summary>启动只读抓包。需要管理员权限 + 已安装 Npcap。</summary>
    public void StartSniffing()
    {
        if (_attachment == null)
            throw new InvalidOperationException("请先调用 Attach()。");

        // ServerIp 留空时，先尽量把进程名认出来（认不出也照跑：抓包侧会自行跟随连接）
        if (string.IsNullOrWhiteSpace(Config.ServerIp) || string.IsNullOrWhiteSpace(Config.ProcessName))
            AutoConfigure();

        Credentials.Log += m => Log?.Invoke(m);

        _sniffer = new PacketSniffer(Config);
        _sniffer.Log += m => Log?.Invoke(m);
        _sniffer.BytesArrived += OnBytes;
        _sniffer.ServerEndpointLocked += (ip, port) =>
            Log?.Invoke($"[host] 服务端地址已自动锁定 {ip}:{port}（可留空，宿主每次自行识别）");
        _sniffer.Start();
    }

    private void ValidateCalibration()
    {
        if (!Config.View.IsCalibrated)
            Log?.Invoke("[host] ⚠ 主视图未校准：所有移动/攻击/拾取都不会执行。请先按 README 完成校准。");
        if (!Config.MiniMap.IsCalibrated)
            Log?.Invoke("[host] ⚠ 小地图未校准：远距离移动（> MiniMapPathMinDistance 格）无法执行。");
        if (!Config.Dialog.IsCalibrated)
            Log?.Invoke("[host] ⚠ 对话框未校准：NPC 菜单选择无法执行。");
    }

    // ---------------------------------------------------------------- 数据流

    private void OnBytes(SniffFlow flow, FlowDirection dir, byte[] data)
    {
        if (_disposed || _attachment == null) return;

        string key = FlowKey(flow);
        try
        {
            // 登录段流量不参与状态注入，但它正是"账号密码从哪来"的答案：
            // 客户端自己发出的 CM_IDPASSWORD 上行 + 服务端下发的会话密钥，两边一起喂给凭据嗅探器。
            if (flow.Gate != MirGateMode.RunGate)
                Credentials.Feed(key, flow.Gate, dir, data);

            if (dir == FlowDirection.Downstream)
            {
                if (_sampling && flow.Gate == MirGateMode.RunGate) AppendSample(key, data);
                var codec = GetDownCodec(key, flow.Gate);
                foreach (var (frame, _raw) in codec.Feed(data))
                {
                    if (frame.Kind == MirFrameKind.RunGatePacket && frame.Header.Ident != 0)
                    {
                        _attachment.OnServerCommand(frame.Header.Ident, frame.Header);
                        Ui.OnServerCommand(frame.Header.Ident, frame.Header);
                    }
                    _attachment.Inject(frame);
                }
            }
            else
            {
                // 只有 RunGate 的上行是明文；登录/选角段是密文，与 Bot 无关（登录由真客户端负责）
                if (flow.Gate != MirGateMode.RunGate) return;
                Upstream.Feed(key, data);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[host] 处理 {key} 数据异常: {ex.Message}");
        }
    }

    private MirFrameCodec GetDownCodec(string key, MirGateMode gate)
    {
        lock (_lock)
        {
            if (!_downCodecs.TryGetValue(key, out var codec))
            {
                codec = new MirFrameCodec(gate, Config.Framing);
                _downCodecs[key] = codec;
            }
            return codec;
        }
    }

    private static string FlowKey(SniffFlow flow) => $"{flow.Client.Address}:{flow.Client.Port}-{flow.Server.Port}";

    // ---------------------------------------------------------------- 自动定界（换服适配）

    private const int SampleLimit = 262144;

    private void AppendSample(string key, byte[] data)
    {
        lock (_lock)
        {
            if (_sampleBytes >= SampleLimit) return;
            if (!_samples.TryGetValue(key, out var list)) { list = new List<byte[]>(); _samples[key] = list; }
            list.Add(data);
            _sampleBytes += data.Length;
        }
    }

    /// <summary>
    /// 自动定界：只读采样 N 秒（**零点击**），跑 <see cref="SplitterAutoDetector"/>；
    /// 命中就把定界档写进 <see cref="Config"/>（由调用方持久化），并让旧解码器作废重建。
    /// 返回 0 = 成功，4 = 样本不足，5 = 未得到可信定界（原因见日志报告）。
    /// </summary>
    public async Task<int> AutoFrameAsync(int seconds, CancellationToken ct = default)
    {
        lock (_lock) { _samples.Clear(); _sampleBytes = 0; }
        _sampling = true;
        Log?.Invoke($"[framing] 自动定界：只读采样 {seconds}s（不点击、不改包）；" +
                    "期间请让客户端在线并做几个动作（走一步/打一下），样本越全越准…");
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, seconds)), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _sampling = false;

        var buckets = new List<(string Key, byte[] Data)>();
        lock (_lock)
        {
            foreach (var kv in _samples)
            {
                int total = kv.Value.Sum(b => b.Length);
                if (total < 256) continue;
                var buf = new byte[total];
                int off = 0;
                foreach (var b in kv.Value) { Buffer.BlockCopy(b, 0, buf, off, b.Length); off += b.Length; }
                buckets.Add((kv.Key, buf));
            }
        }

        if (buckets.Count == 0)
        {
            Log?.Invoke("[framing] 样本不足（下行 RunGate 流量 < 256 字节）：请确认已登录进游戏、抓包有管理员权限，再重试");
            return 4;
        }

        SplitterAutoDetector.Result? best = null;
        string bestKey = "";
        int bestScore = -1;
        foreach (var (key, data) in buckets)
        {
            var r = SplitterAutoDetector.Detect(data);
            Log?.Invoke($"[framing] 流 {key} 探测结果:{Environment.NewLine}{r.Report}");
            int sc = r.Candidates.Count > 0 ? r.Candidates[0].Score : -1;
            if (r.Ok && r.Best != null && sc > bestScore) { best = r; bestKey = key; bestScore = sc; }
        }

        if (best?.Best == null)
        {
            Log?.Invoke("[framing] 未得到可信定界：本次仍按现有定界运行（原因见上面的报告）");
            return 5;
        }

        Config.Framing = best.Best;
        lock (_lock) _downCodecs.Clear();
        Log?.Invoke($"[framing] 已采用定界（流 {bestKey}）: {Config.Framing.Describe()}");
        return 0;
    }

    // ---------------------------------------------------------------- 上行命令 → UI 态机

    private void WireLogging()
    {
        Input.Log += m => Log?.Invoke(m);
        Gate.Log += m => Log?.Invoke(m);
        Gate.Tripped += m => Log?.Invoke("[gate] " + m);
        _walk.Log += m => Log?.Invoke(m);
        _driver.Log += m => Log?.Invoke(m);
        Transfer.Log += m => Log?.Invoke(m);
        MapEntry.Log += m => Log?.Invoke(m);
        Bridge.Log += m => Log?.Invoke(m);
        Ui.Log += m => Log?.Invoke(m);

        Upstream.CommandObserved += (cmd, _) => Ui.OnClientCommand(cmd);
    }

    // ---------------------------------------------------------------- 自检与收尾

    /// <summary>启动自检：把"能不能跑"的结论一次性说清楚，而不是等到用户发现角色站着不动。</summary>
    public string SelfCheck()
    {
        var lines = new List<string>();
        lines.Add($"窗口: {(_driver.IsAttached ? "已附着" : "未附着")}");
        lines.Add($"主视图校准: {(Config.View.IsCalibrated ? "OK" : "缺失")}");
        lines.Add($"小地图校准: {(Config.MiniMap.IsCalibrated ? "OK" : "缺失")}");
        lines.Add($"对话框校准: {(Config.Dialog.IsCalibrated ? "OK" : "缺失")}");
        lines.Add($"背包校准: {(Config.Bag.IsCalibrated ? "OK" : "缺失（使用快捷键喝药时不需要）")}");
        lines.Add($"帧定界: {Config.Framing?.Describe() ?? "经典 Mir2（DDCCBBAA+长度 / '#'..'!'）"}");
        lines.Add($"命令码缺失: {(CmdCatalog.Missing.Count == 0 ? "无" : string.Join("、", CmdCatalog.Missing))}");
        lines.Add($"未接管动作: {Bridge.DescribeDropped()}");
        if (_sniffer != null)
            lines.Add($"抓包: 收到 {_sniffer.PacketsSeen} 包，匹配 {_sniffer.PacketsMatched} 包");

        string report = string.Join(Environment.NewLine, lines);
        Log?.Invoke("[host] 自检报告:" + Environment.NewLine + report);
        return report;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _tickTimer.DisposeAsync().ConfigureAwait(false);
        try { _sniffer?.Dispose(); } catch { }
        _sniffer = null;
        _downCodecs.Clear();
    }
}
