namespace BotClient.ClientDriver.Sniff;

/// <summary>
/// 单条 TCP 方向流的重组器：把抓到的段（可能乱序、重传、重叠）还原成有序字节流。
///
/// 为什么必须做：嗅探拿到的是一个个 TCP 段，直接拿每段 payload 喂帧解析器，
/// 跨段的帧（RunGate 的 body 经常跨段）必然错位。而帧解析一旦错位就会进入"重同步"，
/// 表现为大面积丢帧或解析出垃圾 Ident。
///
/// 实现取舍：这是只读旁路，不承担任何可靠性责任——乱序缓存超限就丢弃（丢包不重传，
/// 本机同机通信几乎不会乱序），只需要保证"顺序流不污染"。
/// </summary>
public sealed class TcpReassembler
{
    private const int MaxPendingSegments = 128;

    private readonly SortedDictionary<uint, byte[]> _pending = new();
    private uint _nextExpected;
    private bool _started;
    private bool _synSeen;

    /// <summary>丢掉的字节数（重传/超限/无 SYN 起流），用于诊断。</summary>
    public long DroppedBytes { get; private set; }
    public long DeliveredBytes { get; private set; }

    /// <summary>按序交付的字节。订阅方（帧解析）应立即消费，不要缓存引用。</summary>
    public event Action<byte[]>? DataAvailable;

    /// <summary>被对端关闭 / RST（诊断用）。</summary>
    public bool Closed { get; private set; }

    public void OnSyn(uint seq)
    {
        if (_started) return;
        _synSeen = true;
        _nextExpected = unchecked(seq + 1);   // SYN 占 1 个序号
    }

    public void OnFin()
    {
        Closed = true;
    }

    public void OnSegment(uint seq, byte[] payload)
    {
        if (payload.Length == 0) return;

        if (!_started)
        {
            // 没抓到 SYN（抓包起点在连接建立之后，或网卡丢包）：以首个数据段为基准起流。
            _nextExpected = seq;
            _started = true;
        }

        int delta = unchecked((int)(seq - _nextExpected));

        if (delta == 0)
        {
            Deliver(payload);
            _nextExpected = unchecked(_nextExpected + (uint)payload.Length);
            DrainPending();
        }
        else if (delta < 0)
        {
            // 重传或重叠：只看尾部还没交付的部分（重叠部分直接丢）
            int overlap = -delta;
            if (overlap < payload.Length)
            {
                var tail = payload[overlap..];
                Deliver(tail);
                _nextExpected = unchecked(_nextExpected + (uint)tail.Length);
                DrainPending();
            }
            else
            {
                DroppedBytes += payload.Length;      // 完全重复
            }
        }
        else
        {
            // 乱序：先缓存，等缺口补上
            if (!_pending.ContainsKey(seq))
            {
                _pending[seq] = payload;
                if (_pending.Count > MaxPendingSegments)
                {
                    // 缓存爆了：放弃最早的一个缺口，从它的位置强行续流（只读场景可接受）
                    var first = _pending.First();
                    _pending.Remove(first.Key);
                    DroppedBytes += _pending.Values.Sum(v => (long)v.Length);
                    _pending.Clear();
                    _nextExpected = unchecked(first.Key + (uint)first.Value.Length);
                    Deliver(first.Value);
                }
            }
            else
            {
                DroppedBytes += payload.Length;
            }
        }
    }

    private void DrainPending()
    {
        while (_pending.Count > 0)
        {
            var first = _pending.First();
            int delta = unchecked((int)(first.Key - _nextExpected));
            if (delta < 0) { _pending.Remove(first.Key); DroppedBytes += first.Value.Length; continue; }
            if (delta > 0) break;                     // 还有缺口

            _pending.Remove(first.Key);
            Deliver(first.Value);
            _nextExpected = unchecked(_nextExpected + (uint)first.Value.Length);
        }
    }

    private void Deliver(byte[] data)
    {
        DeliveredBytes += data.Length;
        DataAvailable?.Invoke(data);
    }
}
