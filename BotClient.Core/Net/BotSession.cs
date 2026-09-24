using System.Threading.Channels;
using BotClient.Protocol;

namespace BotClient.Net;

/// <summary>
/// 会话状态机:封装 LoginGate → SelectServer → SelGate → RunGate 切换。
/// 维护当前连接、阶段、收包队列、待确认凭证。
/// </summary>
public sealed class BotSession : IAsyncDisposable
{
    private MirConnection? _connection;
    private CancellationTokenSource? _pumpCts;
    // 主动断开标记:DisconnectAsync 里 DisposeAsync 会先把收包泵结束掉,
    // 那一刻 _connection 还没置 null,不加分辨就会在每次切阶段/小退/大退时误报"被踢"。
    private bool _closing;
    private readonly Channel<MirServerPacket> _pending = Channel.CreateUnbounded<MirServerPacket>(new UnboundedChannelOptions { SingleReader = false });

    // ================= C 方案（客户端驱动）挂接点 · 见 Net/ClientDriven.cs =================
    // 两个属性都为 null 时，本类行为与改造前完全一致（直连模式）。

    /// <summary>动作驱动。非 null 时 BotRuntime 的 Send* 不再写 socket，改由它翻译成真机操作。</summary>
    public IClientDriver? Driver { get; set; }

    /// <summary>兜底上行通道。非 null 时本会话不再直接写 socket（未接管的动作只计数、不外发）。</summary>
    public IUpstreamSink? Sink { get; set; }

    private long _injectedPackets;
    private long _injectedDropped;

    /// <summary>嗅探注入成功的服务端包数（自检用）。</summary>
    public long InjectedPackets => Interlocked.Read(ref _injectedPackets);

    /// <summary>嗅探帧被丢弃数（信道满 / 解析失败 / 登录段帧）。</summary>
    public long InjectedDropped => Interlocked.Read(ref _injectedDropped);

    public MirSessionStage Stage { get; private set; } = MirSessionStage.None;
    public string? LoginGateHost { get; private set; }
    public int LoginGatePort { get; private set; }
    public string? SelGateHost { get; private set; }
    public int SelGatePort { get; private set; }
    public string? RunGateHost { get; private set; }
    public int RunGatePort { get; private set; }
    public string? Account { get; private set; }
    public int Certification { get; private set; }

    /// <summary>LoginGate 协议密码 (22 字符密钥包解出), CM_IDPASSWORD 等加密用。</summary>
    public uint ProtocolPassword { get; private set; }

    private TaskCompletionSource<uint> _pwTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>等待 LoginGate 密钥包到达并解出 ProtocolPassword。</summary>
    public Task<uint> WaitForProtocolPasswordAsync(TimeSpan timeout, CancellationToken ct)
        => _pwTcs.Task.WaitAsync(timeout, ct);

    /// <summary>连接可用性。C 方案下没有自己的 socket，改由 Sink 代言 ——
    /// 不改这里的话 BotCombatAI 主循环会一直在 !IsConnected 分支空转（日志正常但角色不动）。</summary>
    public bool IsConnected => Sink?.IsConnected ?? (_connection?.IsConnected ?? false);

    /// <summary>对端把连接关掉时触发(自己 DisconnectAsync/切阶段不触发)。
    /// 小退、被服务端踢、网络掉线都走这条路 —— 没有这个事件时收包泵只是静静结束,
    /// UI 冻在最后一帧,AI 在 !IsConnected 分支里一秒秒空转,没人知道角色已经不在线了。</summary>
    public event Action? Disconnected;

    public async Task ConnectAsync(string host, int port, MirGateMode mode, CancellationToken ct)
    {
        BotLog.Net("session", host, port, $"阶段={Stage} mode={mode}");
        await DisconnectAsync().ConfigureAwait(false);
        if (mode == MirGateMode.LoginGate)
            _pwTcs = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _closing = false;
        _connection = new MirConnection();
        await _connection.ConnectAsync(host, port, mode, ct).ConfigureAwait(false);
        LoginGateHost = host;
        LoginGatePort = port;
        _pumpCts = new CancellationTokenSource();
        var pumpToken = _pumpCts.Token;
        _ = Task.Run(() => PumpIncomingAsync(pumpToken), pumpToken);
    }

    private async Task PumpIncomingAsync(CancellationToken ct)
    {
        var conn = _connection;
        if (conn == null) return;
        try
        {
            await foreach (var frame in conn.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                MirServerPacket pkt;
                switch (frame.Kind)
                {
                    case MirFrameKind.KeyPacket:
                        try
                        {
                            uint pw = LoginGateCrypto.DecodeKeyPacket(frame.Text!);
                            ProtocolPassword = pw;
                            BotLog.Info($"[logingate] ProtocolPassword={pw} (0x{pw:X8})");
                            _pwTcs.TrySetResult(pw);
                        }
                        catch (Exception ex)
                        {
                            _pwTcs.TrySetException(ex);
                        }
                        continue;

                    case MirFrameKind.RunGatePacket:
                        pkt = new MirServerPacket(frame.BodyEncoded ?? string.Empty, frame.Header, frame.BodyEncoded ?? string.Empty);
                        break;

                    default: // Text
                        if (!MirPacketDecoder.TryDecode(frame.Text!, out pkt))
                        {
                            var raw = frame.Text!;
                            BotLog.Warn($"收到无法解析的包: 前40字={raw[..Math.Min(40, raw.Length)]}");
                            continue;
                        }
                        break;
                }

                BotLog.Packet("recv", pkt.Header.Ident, $"Recog={pkt.Header.Recog} Param={pkt.Header.Param} BodyLen={pkt.BodyEncoded.Length}");

                // RunGate 空闲探测 SM=112 → 立刻回 CM=500 保活 (60s 无数据会被踢)
                // C 方案下**不回**：真客户端自己会应答，我们再回一个就是往真连接上多注入一份上行。
                if (Sink == null &&
                    pkt.Header.Ident == Grobal2.SM_RUNGATE_HEARTBEAT && conn.Mode == MirGateMode.RunGate)
                {
                    try
                    {
                        await conn.SendPayloadAsync(LoginGateCrypto.BuildPlainPayload((ushort)Grobal2.CM_RUNGATE_HEARTBEAT), ct).ConfigureAwait(false);
                        BotLog.Info("[rungate] 已自动应答 CM_RUNGATE_HEARTBEAT(500)");
                    }
                    catch (Exception ex)
                    {
                        BotLog.Warn($"[rungate] 保活应答失败: {ex.Message}");
                    }
                }

                await _pending.Writer.WriteAsync(pkt, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            BotLog.Error($"接收循环异常: {ex.Message}");
        }
        // 注意:这里不能 TryComplete _pending!
        // _pending 是全局共享的,连接切换(LoginGate→SelGate→RunGate)时旧的泵会完成它,
        // 导致新连接发来的包无法写入。由 DisposeAsync 统一清理。
        // 只有"这条连接还是当前连接、且不是我们自己要断的"时才算掉线。
        if (!_closing && ReferenceEquals(_connection, conn)) Disconnected?.Invoke();
    }

    public async Task SendPayloadAsync(string payload, CancellationToken ct)
    {
        // ===== C 方案：上行不再写 socket（真客户端的连接才是唯一真实连接）=====
        // 兜底通道只计数、不转发：任何走到这里的动作都说明某个 Send* 方法还没接入 Driver，
        // 它会在 ClientInputBridge 的统计里现身，而不是悄悄注入到真客户端的连接上。
        if (Sink != null)
        {
            await Sink.SendAsync(payload, ct).ConfigureAwait(false);
            return;
        }

        // ===== 以下为原有直连逻辑，保持不变 =====
        if (_connection == null) throw new InvalidOperationException("Not connected.");
        await _connection.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把外部（Npcap 嗅探）得到的服务端帧注入接收队列。
    ///
    /// 写入的是与 <see cref="PumpIncomingAsync"/> 完全相同的 <c>_pending</c> 信道，
    /// 且转换规则逐条对齐 —— 因此下游 BotRuntime 分不出来源，这正是"状态通道零改造"的落点。
    ///
    /// 两点与直连路径不同（都是有意的）：
    ///   ① 不处理 KeyPacket：登录段由真客户端自己走，与 Bot 无关；
    ///   ② 不做心跳应答：真客户端自己会回，我们再回一个就是多余注入。
    /// </summary>
    public void InjectPacket(MirIncomingFrame frame)
    {
        MirServerPacket pkt;
        switch (frame.Kind)
        {
            case MirFrameKind.KeyPacket:
                Interlocked.Increment(ref _injectedDropped);
                return;

            case MirFrameKind.RunGatePacket:
                pkt = new MirServerPacket(frame.BodyEncoded ?? string.Empty, frame.Header, frame.BodyEncoded ?? string.Empty);
                break;

            default: // Text
                if (!MirPacketDecoder.TryDecode(frame.Text ?? string.Empty, out pkt))
                {
                    long dropped = Interlocked.Increment(ref _injectedDropped);
                    if (dropped <= 5)
                    {
                        var raw = frame.Text ?? string.Empty;
                        BotLog.Warn($"[inject] 无法解析的注入帧(丢弃): 前40字={raw[..Math.Min(40, raw.Length)]}");
                    }
                    return;
                }
                break;
        }

        // 信道满时丢帧而不是阻塞抓包线程（无界信道实际不会满，留作兜底）
        if (_pending.Writer.TryWrite(pkt))
            Interlocked.Increment(ref _injectedPackets);
        else
            Interlocked.Increment(ref _injectedDropped);
    }

    public async Task<MirServerPacket> WaitForPacketAsync(Func<MirServerPacket, bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
        var token = linked.Token;

        // 先扫已有队列:不匹配的包放回队列尾部,避免静默丢弃
        var skipped = new List<MirServerPacket>();
        while (_pending.Reader.TryRead(out var existing))
        {
            if (predicate(existing))
            {
                foreach (var s in skipped) await _pending.Writer.WriteAsync(s, token).ConfigureAwait(false);
                return existing;
            }
            skipped.Add(existing);
        }
        foreach (var s in skipped) await _pending.Writer.WriteAsync(s, token).ConfigureAwait(false);

        // 再等新包
        await foreach (var pkt in _pending.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            if (predicate(pkt)) return pkt;
        }

        throw new InvalidOperationException("Disconnected while waiting for server response.");
    }

    /// <summary>等待匹配的包但不消费它——包留在队列里给后续的 BotRuntime 处理。
    /// 用于登录流程末尾:确认 RunGate 回了首包(公告/LOGON 等),但首包本身要留给 BotRuntime。
    /// 仅用 peek 判断是否有匹配包,不改变队列内容。</summary>
    public async Task<bool> PeekPacketAsync(Func<MirServerPacket, bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
        var token = linked.Token;

        // peek 已有队列:读到不匹配的包放回尾部,匹配的也放回尾部,都不消费
        var peeked = new List<MirServerPacket>();
        while (_pending.Reader.TryRead(out var existing))
        {
            peeked.Add(existing);
            if (predicate(existing))
            {
                foreach (var s in peeked) await _pending.Writer.WriteAsync(s, token).ConfigureAwait(false);
                return true;
            }
        }
        foreach (var s in peeked) await _pending.Writer.WriteAsync(s, token).ConfigureAwait(false);

        // 等新包:匹配就放回并返回 true,不匹配的也放回
        await foreach (var pkt in _pending.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            await _pending.Writer.WriteAsync(pkt, token).ConfigureAwait(false);
            if (predicate(pkt)) return true;
        }

        return false;
    }

    public void SetStage(MirSessionStage stage) => Stage = stage;
    public void SetAccount(string account) => Account = account;
    public void SetCertification(int cert) => Certification = cert;
    public void SetSelGate(string host, int port) { SelGateHost = host; SelGatePort = port; }
    public void SetRunGate(string host, int port) { RunGateHost = host; RunGatePort = port; }

    /// <summary>枚举所有新到的包(BotRuntime 用)。每包只派一次。
    /// 带外层 while 循环——即使 Writer 被意外完成也能继续等待新连接写入。</summary>
    public async IAsyncEnumerable<MirServerPacket> ReadAllPacketsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await foreach (var pkt in _pending.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return pkt;
        }
    }

    public async Task DisconnectAsync()
    {
        _closing = true;
        // 先取消旧 pump,确保不再持有对旧 MirConnection.Reader 的引用,
        // 让旧 MirConnection 的 socket 能被 GC 回收/彻底关闭。
        if (_pumpCts != null)
        {
            _pumpCts.Cancel();
            _pumpCts.Dispose();
            _pumpCts = null;
        }

        if (_connection != null)
            await _connection.DisposeAsync().ConfigureAwait(false);
        _connection = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _pending.Writer.TryComplete();
    }
}
