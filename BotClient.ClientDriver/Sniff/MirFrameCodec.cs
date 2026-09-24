using System.Runtime.InteropServices;
using System.Text;
using BotClient.Net;        // MirIncomingFrame / MirFrameKind / MirGateMode
using BotClient.Protocol;   // CmdPack / Grobal2

// ============================================================================
// 【本文件是 ClientDriver 工程与 Core 的唯一"协议接触点"】
//
// 除了这里，工程内其它文件都不直接构造 Core 的协议类型。如果你的 Core 版本里
// 下列名字/签名不一致，**只需要改这一个文件**：
//
//   1. MirGateMode        —— 三个枚举值应覆盖 LoginGate / SelGate / RunGate
//   2. MirFrameKind       —— 需要 KeyPacket / Text / RunGatePacket 三种取值
//   3. MirIncomingFrame   —— 构造签名假设为 (Kind, Text, Header, BodyEncoded)
//   4. MirPacketDecoder.TryDecode(string payload, out CmdPack pkt)
//   5. CmdPack.Header.Ident —— 命令码字段
//
// 这五处对不上时，客户端驱动的逻辑完全不受影响，把名字改对即可。
// ============================================================================

namespace BotClient.ClientDriver.Sniff;

/// <summary>
/// 从"重组后的 TCP 字节流"提取 gxx 帧，产出与 <c>MirConnection.ReceiveLoopAsync</c> 完全同构的
/// <see cref="MirIncomingFrame"/>，同时把这一帧的原始字节一并给出（<c>MirConnection</c> 不保留原始字节，
/// 嗅探侧需要它做诊断与"字节保真"核对）。
///
/// 与 MirConnection 的差异只有两点，都是为了嗅探场景：
///   ① 产出 Raw（原字节区间）；
///   ② 无 socket，纯函数式喂数据（Feed(chunk)）。
///
/// 【重要】魔法数必须与 MirConnection 一致：RunGate 头是 $AABBCCDD 的**小端序** DD CC BB AA。
/// 写成 AA BB CC DD 会导致所有 RunGate 帧被当成噪声丢弃 —— 这是最容易犯的错。
/// </summary>
public sealed class MirFrameCodec
{
    private static readonly byte[] RunGateMagic = { 0xDD, 0xCC, 0xBB, 0xAA };
    private const int KeyPacketChars = 22;

    private readonly List<byte> _acc = new(65536);
    private readonly bool _runGate;
    private bool _keyPacketPending;

    /// <summary>被丢弃的噪声字节数（诊断用）。持续增长说明流被污染或端口识别错误。</summary>
    public long DiscardedBytes { get; private set; }

    public MirFrameCodec(MirGateMode mode)
    {
        _runGate = mode == MirGateMode.RunGate;
        _keyPacketPending = mode == MirGateMode.LoginGate;
    }

    /// <summary>喂入一段**按序**的下行字节，返回本次能切出的所有帧。</summary>
    public List<(MirIncomingFrame Frame, byte[] Raw)> Feed(ReadOnlySpan<byte> chunk)
    {
        var output = new List<(MirIncomingFrame, byte[])>();
        if (chunk.Length > 0) _acc.AddRange(chunk.ToArray());

        // ---- LoginGate 首包：22 字符裸密钥包（不是 '#'…'!' 帧，但必须原样识别，否则后续全错位）----
        if (_keyPacketPending)
        {
            if (_acc.Count < KeyPacketChars) return output;
            byte[] rawKey = _acc.GetRange(0, KeyPacketChars).ToArray();
            _acc.RemoveRange(0, KeyPacketChars);
            _keyPacketPending = false;
            string key = Ascii(rawKey);
            output.Add((new MirIncomingFrame(MirFrameKind.KeyPacket, key, default, null), rawKey));
        }

        if (_runGate)
        {
            while (ExtractRunGateFrame(_acc, out var f, out var raw)) output.Add((f, raw));
        }
        else
        {
            while (ExtractTextFrame(_acc, out var f, out var raw)) output.Add((f, raw));
        }
        return output;
    }

    /// <summary>'#'…'!' 文本帧；帧间噪声直接丢弃。</summary>
    private bool ExtractTextFrame(List<byte> acc, out MirIncomingFrame frame, out byte[] raw)
    {
        frame = default; raw = Array.Empty<byte>();
        int hashIdx = acc.IndexOf((byte)'#');
        if (hashIdx < 0)
        {
            if (acc.Count > 0) { DiscardedBytes += acc.Count; acc.Clear(); }
            return false;
        }
        if (hashIdx > 0) { DiscardedBytes += hashIdx; acc.RemoveRange(0, hashIdx); }

        int bangIdx = acc.IndexOf((byte)'!');
        if (bangIdx < 0) return false;                       // 等更多数据

        raw = acc.GetRange(0, bangIdx + 1).ToArray();        // 先截 raw，再消费
        string body = Ascii(acc.GetRange(1, bangIdx - 1).ToArray());
        acc.RemoveRange(0, bangIdx + 1);

        if (body.Length == 0) return true;                   // 空帧，继续下一帧
        frame = new MirIncomingFrame(MirFrameKind.Text, body, default, null);
        return true;
    }

    /// <summary>RunGate 二进制帧：4 字节魔数 + uint32 DataLen + 24 字节头 + body；兼容偶发文本帧。</summary>
    private bool ExtractRunGateFrame(List<byte> acc, out MirIncomingFrame frame, out byte[] raw)
    {
        frame = default; raw = Array.Empty<byte>();
        if (acc.Count == 0) return false;

        // 文本帧兜底
        if (acc[0] == (byte)'#')
        {
            int bangIdx = acc.IndexOf((byte)'!');
            if (bangIdx < 0) return false;
            raw = acc.GetRange(0, bangIdx + 1).ToArray();
            string body = Ascii(acc.GetRange(1, bangIdx - 1).ToArray());
            acc.RemoveRange(0, bangIdx + 1);
            if (body.Length > 0) frame = new MirIncomingFrame(MirFrameKind.Text, body, default, null);
            return true;
        }

        if (acc.Count < 4)
        {
            // 数据不足，但已能判断不是魔数前缀则重同步
            bool prefix = true;
            for (int i = 0; i < acc.Count; i++)
                if (acc[i] != RunGateMagic[i]) { prefix = false; break; }
            if (!prefix) { DiscardedBytes += acc.Count; acc.Clear(); }
            return false;
        }

        if (!MatchMagic(acc, 0))
        {
            int next = NextFrameStart(acc);
            if (next < 0) { DiscardedBytes += acc.Count; acc.Clear(); return false; }
            DiscardedBytes += next;
            acc.RemoveRange(0, next);
            return false;
        }

        if (acc.Count < 8) return false;
        uint dataLen = (uint)(acc[4] | (acc[5] << 8) | (acc[6] << 16) | (acc[7] << 24));
        if (dataLen > 1_000_000)                       // 异常长度：流被污染，整段丢弃重同步
        {
            DiscardedBytes += acc.Count;
            acc.Clear();
            return false;
        }

        int total = Grobal2.RUN_GATE_HEADER_SIZE + (int)dataLen;
        if (acc.Count < total) return false;

        var span = CollectionsMarshal.AsSpan(acc);
        CmdPack header = MemoryMarshal.Read<CmdPack>(span.Slice(8, 16));
        string bodyEncoded = dataLen > 0
            ? Ascii(span.Slice(Grobal2.RUN_GATE_HEADER_SIZE, (int)dataLen).ToArray())
            : string.Empty;

        raw = acc.GetRange(0, total).ToArray();        // 整帧原字节（含 24 字节头）
        acc.RemoveRange(0, total);
        frame = new MirIncomingFrame(MirFrameKind.RunGatePacket, null, header, bodyEncoded);
        return true;
    }

    private static bool MatchMagic(List<byte> acc, int i)
    {
        if (i + 4 > acc.Count) return false;
        for (int j = 0; j < 4; j++) if (acc[i + j] != RunGateMagic[j]) return false;
        return true;
    }

    private static int NextFrameStart(List<byte> acc)
    {
        for (int i = 1; i < acc.Count; i++)
        {
            if (acc[i] == (byte)'#') return i;
            if (acc[i] == RunGateMagic[0] && MatchMagic(acc, i)) return i;
        }
        return -1;
    }

    private static string Ascii(byte[] b)
    {
        var sb = new StringBuilder(b.Length);
        foreach (byte x in b) sb.Append((char)x);
        return sb.ToString();
    }
}

/// <summary>
/// 上行（客户端 → 服务端）帧的**识别器**，只做一件事：告诉我们"客户端刚才发了什么命令"。
///
/// 为什么需要：C 方案里 Bot 不自己发包，动作是否被客户端接受，只能从上行看。
/// 点了地图却迟迟没有 CM_WALK 上行，就说明这一下点空了（被 UI 遮挡 / 窗口没在前台）。
/// 这比任何本地推测都可靠。
///
/// 注意：只解 RunGate 的上行（明文 EdCode）。登录/选角段的上行是密文，
/// 而 C 方案下登录由真客户端负责，Bot 不需要理解它。
/// </summary>
public sealed class UpstreamCommandProbe
{
    private sealed record Entry(ushort Cmd, DateTime At);

    private const int MaxRecent = 512;

    private readonly Dictionary<string, MirFrameCodec> _codecs = new();
    private readonly Queue<Entry> _recent = new();
    private readonly object _lock = new();

    /// <summary>最近一次识别到的上行命令与时间（用于动作确认）。</summary>
    public (ushort Cmd, DateTime At) LastCommand { get; private set; }

    public event Action<ushort, CmdPack>? CommandObserved;

    /// <summary>默认流（单连接场景）。</summary>
    public void Feed(ReadOnlySpan<byte> chunk) => Feed("default", chunk);

    /// <summary>
    /// 按流喂入上行字节。**必须带 flowKey**：帧解码器是有状态的（跨段半帧要缓存），
    /// 多条连接（LoginGate/SelGate/RunGate）混用一个解码器会互相污染，
    /// 表现就是偶发的垃圾命令码，且极难排查。
    /// </summary>
    public void Feed(string flowKey, ReadOnlySpan<byte> chunk)
    {
        MirFrameCodec codec;
        lock (_lock)
        {
            if (!_codecs.TryGetValue(flowKey, out codec!))
            {
                codec = new MirFrameCodec(MirGateMode.RunGate);
                _codecs[flowKey] = codec;
            }
        }

        foreach (var (frame, _) in codec.Feed(chunk))
        {
            if (frame.Kind != MirFrameKind.Text || string.IsNullOrEmpty(frame.Text)) continue;
            // 上行 payload = 1 位序号 + EdCode(16字符头) + body，用 Core 的解码器即可
            if (!MirPacketDecoder.TryDecode(frame.Text!, out var pkt)) continue;

            ushort cmd = pkt.Header.Ident;
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                LastCommand = (cmd, now);
                _recent.Enqueue(new Entry(cmd, now));
                while (_recent.Count > MaxRecent) _recent.Dequeue();
            }
            CommandObserved?.Invoke(cmd, pkt.Header);
        }
    }

    /// <summary>距上次收到指定命令过了多久；未收到过返回 null。</summary>
    public TimeSpan? SinceCommand(ushort cmd)
    {
        lock (_lock)
            return LastCommand.Cmd == cmd ? DateTime.UtcNow - LastCommand.At : null;
    }

    /// <summary>
    /// 在 <paramref name="after"/> 之后是否收到过指定命令。
    /// 这是动作确认的判据：点完地图后它必须是 true，否则说明那一下点空了。
    /// </summary>
    public bool ObservedSince(ushort cmd, DateTime after)
    {
        lock (_lock)
            return _recent.Any(e => e.Cmd == cmd && e.At > after);
    }

    /// <summary>在 after 之后是否收到过**任何**上行命令（用于无法预知命令码的动作，如菜单选择）。</summary>
    public bool AnyObservedSince(DateTime after)
    {
        lock (_lock)
            return _recent.Any(e => e.At > after);
    }
}
