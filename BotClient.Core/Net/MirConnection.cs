using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using BotClient.Protocol;

namespace BotClient.Net;

public enum MirGateMode
{
    /// <summary>登录网关: 连接后先收 22 字符裸密钥包, 之后 '#'…'!' 文本帧。</summary>
    LoginGate,
    /// <summary>选角网关: 纯 '#'…'!' 文本帧, 无密钥包。</summary>
    SelGate,
    /// <summary>游戏网关: 上行文本帧, 下行二进制 TRungateMsgHeader($AABBCCDD)。</summary>
    RunGate,
}

/// <summary>连接层收到的完整帧。</summary>
public readonly record struct MirIncomingFrame(MirFrameKind Kind, string? Text, CmdPack Header, string? BodyEncoded);

public enum MirFrameKind
{
    /// <summary>Text='22字符头+编码body'(含可选前导序号位, 由解码器处理)。</summary>
    Text,
    /// <summary>LoginGate 连接后的 22 字符密钥包。</summary>
    KeyPacket,
    /// <summary>RunGate 二进制帧, Header/BodyEncoded 已拆出。</summary>
    RunGatePacket,
}

/// <summary>
/// gxx 三模式 TCP 帧引擎。
/// 上行统一 '#'+1位序号+payload+'!' (序号不校验); 下行按模式解析:
/// LoginGate/SelGate 为文本帧; RunGate 为 24 字节二进制头 + DataLen 字节 body。
/// 不对 '*' 做任何应答 (gxx 对 &lt;22 字节包直接踢线)。
/// </summary>
public sealed class MirConnection : IAsyncDisposable
{
    private const int KeyPacketChars = 22; // 16 字节 TDefaultMessage 编码后 22 字符
    private static readonly byte[] RunGateMagic = [(byte)0xDD, (byte)0xCC, (byte)0xBB, (byte)0xAA]; // $AABBCCDD LE

    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _receiveLoop;
    private CancellationTokenSource? _disposeCts;
    private byte _sendCode;
    private int _cleanupOnce;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<MirIncomingFrame> _incoming =
        Channel.CreateUnbounded<MirIncomingFrame>(new UnboundedChannelOptions { SingleReader = true });
    private MirGateMode _mode;

    public bool IsConnected => _client?.Connected == true;
    public ChannelReader<MirIncomingFrame> Reader => _incoming.Reader;
    public MirGateMode Mode => _mode;

    public async Task ConnectAsync(string host, int port, MirGateMode mode, CancellationToken ct)
    {
        BotLog.Net("connect", host, port, $"开始连接 mode={mode}");
        _mode = mode;
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        _stream = _client.GetStream();
        _sendCode = 0;
        _cleanupOnce = 0;
        BotLog.Net("connect", host, port, "已连接");

        _disposeCts = new CancellationTokenSource();
        var token = _disposeCts.Token;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(token));
    }

    /// <summary>发送 #code&lt;payload&gt;! 格式的包。</summary>
    public async Task SendPayloadAsync(string payload, CancellationToken ct)
    {
        var stream = _stream;
        if (stream == null) throw new InvalidOperationException("Not connected.");

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte code = _sendCode;
            _sendCode++;
            if (_sendCode >= 10) _sendCode = 1;

            string framed = $"#{code}{payload}!";
            BotLog.Trace($"发送[{code}] mode={_mode} payloadLen={payload.Length} full={payload}");
            byte[] bytes = AsciiGetBytes(framed);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static byte[] AsciiGetBytes(string s)
    {
        byte[] buf = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) buf[i] = (byte)s[i];
        return buf;
    }

    private static string AsciiGetString(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(b.Length);
        foreach (byte x in b) sb.Append((char)x);
        return sb.ToString();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var remote = _client?.Client?.RemoteEndPoint?.ToString() ?? "?";
        var buf = new byte[16384];
        // 累积缓冲: [0, writePos) 为未消费数据
        var acc = new List<byte>(32768);
        bool keyPacketPending = _mode == MirGateMode.LoginGate;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stream = _stream;
                if (stream == null) break;

                int read;
                try
                {
                    read = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (IOException ioEx)
                {
                    BotLog.Warn($"[{remote}] 接收 IO 异常,连接断开: {ioEx.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    BotLog.Warn($"[{remote}] 接收异常,连接断开: {ex.Message}");
                    return;
                }

                if (read <= 0)
                {
                    BotLog.Info($"[{remote}] 对端关闭连接(ReadAsync 返回 {read})");
                    break;
                }

                acc.AddRange(buf.AsSpan(0, read).ToArray());

                if (BotLog.VerbosePackets)
                    BotLog.Trace($"原始接收 {read} 字节 mode={_mode}: [{AsciiGetString(buf.AsSpan(0, Math.Min(48, read)))}]");

                // ---- LoginGate 首包: 22 字符裸密钥包 ----
                if (keyPacketPending)
                {
                    if (acc.Count < KeyPacketChars) continue;
                    string key = AsciiGetString(acc.Take(KeyPacketChars).ToArray());
                    acc.RemoveRange(0, KeyPacketChars);
                    keyPacketPending = false;
                    BotLog.Info($"[logingate] 收到密钥包: {key}");
                    await _incoming.Writer.WriteAsync(new MirIncomingFrame(MirFrameKind.KeyPacket, key, default, null), ct).ConfigureAwait(false);
                }

                // ---- 帧提取 ----
                if (_mode == MirGateMode.RunGate)
                {
                    while (ExtractRunGateFrame(acc, out var frame))
                        await _incoming.Writer.WriteAsync(frame, ct).ConfigureAwait(false);
                }
                else
                {
                    while (ExtractTextFrame(acc, out var frame))
                        await _incoming.Writer.WriteAsync(frame, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            BotLog.Warn($"[{remote}] 接收循环异常: {ex.Message}");
        }
        finally
        {
            _incoming.Writer.TryComplete();
            Cleanup();
        }
    }

    /// <summary>从缓冲提取 '#'…'!' 文本帧; 丢弃帧间噪声字节。</summary>
    private static bool ExtractTextFrame(List<byte> acc, out MirIncomingFrame frame)
    {
        frame = default;
        int hashIdx = acc.IndexOf((byte)'#');
        if (hashIdx < 0) { acc.Clear(); return false; }
        if (hashIdx > 0) acc.RemoveRange(0, hashIdx);

        int bangIdx = acc.IndexOf((byte)'!');
        if (bangIdx < 0) return false; // 等更多数据

        string body = AsciiGetString(acc.GetRange(1, bangIdx - 1).ToArray());
        acc.RemoveRange(0, bangIdx + 1);
        if (body.Length == 0) return true; // 空帧继续找下一帧
        frame = new MirIncomingFrame(MirFrameKind.Text, body, default, null);
        return true;
    }

    /// <summary>从缓冲提取 RunGate 二进制帧; 兼容偶发文本帧。</summary>
    private bool ExtractRunGateFrame(List<byte> acc, out MirIncomingFrame frame)
    {
        frame = default;
        if (acc.Count == 0) return false;

        // 文本帧兜底 (如 gate 直发 '#'…'!')
        if (acc[0] == (byte)'#')
        {
            int bangIdx = acc.IndexOf((byte)'!');
            if (bangIdx < 0) return false;
            string body = AsciiGetString(acc.GetRange(1, bangIdx - 1).ToArray());
            acc.RemoveRange(0, bangIdx + 1);
            if (body.Length > 0) frame = new MirIncomingFrame(MirFrameKind.Text, body, default, null);
            return true;
        }

        if (acc.Count < 4)
        {
            // 数据不足, 但已能判断不是魔数前缀则重同步
            bool prefix = true;
            for (int i = 0; i < acc.Count; i++)
                if (acc[i] != RunGateMagic[i]) { prefix = false; break; }
            if (!prefix) acc.Clear();
            return false;
        }

        if (!CollectionsEqual(acc))
        {
            // 噪声重同步: 丢到下一个可能的魔数或 '#'
            int next = NextFrameStart(acc);
            if (next < 0) { acc.Clear(); return false; }
            acc.RemoveRange(0, next);
            return false;
        }

        if (acc.Count < 8) return false;
        uint dataLen = ReadDataLen(acc);
        if (dataLen > 1_000_000)
        {
            BotLog.Warn($"[rungate] 异常 DataLen={dataLen}, 丢弃缓冲重同步");
            acc.Clear();
            return false;
        }

        int total = Grobal2.RUN_GATE_HEADER_SIZE + (int)dataLen;
        if (acc.Count < total) return false;

        CmdPack header = MemoryMarshal.Read<CmdPack>(CollectionsMarshal.AsSpan(acc).Slice(8, 16));
        string bodyEncoded = dataLen > 0 ? AsciiGetString(CollectionsMarshal.AsSpan(acc).Slice(Grobal2.RUN_GATE_HEADER_SIZE, (int)dataLen)) : string.Empty;
        acc.RemoveRange(0, total);
        frame = new MirIncomingFrame(MirFrameKind.RunGatePacket, null, header, bodyEncoded);
        return true;
    }

    private static bool CollectionsEqual(List<byte> acc)
    {
        for (int i = 0; i < 4; i++)
            if (acc[i] != RunGateMagic[i]) return false;
        return true;
    }

    private static uint ReadDataLen(List<byte> acc) =>
        (uint)(acc[4] | (acc[5] << 8) | (acc[6] << 16) | (acc[7] << 24));

    private static int NextFrameStart(List<byte> acc)
    {
        for (int i = 1; i < acc.Count; i++)
        {
            if (acc[i] == (byte)'#') return i;
            if (acc[i] == RunGateMagic[0] && i + 4 <= acc.Count && MatchMagicFrom(acc, i)) return i;
        }
        return -1;
    }

    private static bool MatchMagicFrom(List<byte> acc, int i)
    {
        for (int j = 0; j < 4; j++)
            if (acc[i + j] != RunGateMagic[j]) return false;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts?.Cancel();
        Cleanup();
        if (_receiveLoop != null)
        {
            try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None); } catch { }
        }
        _disposeCts?.Dispose();
        _incoming.Writer.TryComplete();
    }

    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleanupOnce, 1) != 0) return;
        _stream?.Close();
        _client?.Close();
        _stream = null;
        _client = null;
    }
}
