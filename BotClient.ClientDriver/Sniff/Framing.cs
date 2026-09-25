using System.Globalization;
using System.Text.Json.Serialization;

namespace BotClient.ClientDriver.Sniff;

/// <summary>
/// 帧定界模式。**每个服的 M2 与网关协议都可能不一样**，所以定界不做成代码分支，
/// 而成做成"参数化的描述"：换服只改 <c>clientdriver.json</c> 的 <c>framing</c> 段，不重编译。
/// </summary>
public enum FramingMode
{
    /// <summary>经典 Mir2：文本帧 <c>'#'+序号+payload+'!'</c>；RunGate 帧 <c>DD CC BB AA</c> + uint32 长度 + 24 字节头。</summary>
    ClassicMir = 0,

    /// <summary>魔数 + 长度字段：帧首固定魔数，随后某偏移处是 body 长度。覆盖绝大多数"换过头"的服。</summary>
    MagicLength = 1,
}

/// <summary>
/// 一份定界档（对应一个服/一个引擎）。既可手工填写，也可由
/// <see cref="SplitterAutoDetector"/> 自动探测后写回配置。
/// </summary>
public sealed class FramingProfile
{
    public FramingMode Mode { get; set; } = FramingMode.MagicLength;

    /// <summary>帧首魔数（十六进制，可带空格）。留空表示不校验魔数（只靠长度字段定界）。</summary>
    public string Magic { get; set; } = "DDCCBBAA";

    /// <summary>长度字段在帧内的偏移。经典 RunGate 为 4（魔数之后）。</summary>
    public int LengthOffset { get; set; } = 4;

    /// <summary>长度字段位宽：1 / 2 / 4 字节。</summary>
    public int LengthSize { get; set; } = 4;

    /// <summary>长度字段字节序；false = 小端（Mir2 血统默认）。</summary>
    public bool BigEndian { get; set; }

    /// <summary>body 起始偏移（相对帧首）。经典 RunGate 为 24（4 魔数 + 4 长度 + 16 CmdPack）。</summary>
    public int HeaderSize { get; set; } = 24;

    /// <summary>CmdPack 在帧内的偏移；解不出命令码时只做诊断，不注入状态机。</summary>
    public int CmdOffset { get; set; } = 8;

    /// <summary>单帧上限（防御污染流）。</summary>
    public int MaxFrameSize { get; set; } = 1_048_576;

    /// <summary>来源说明：自动探测时写入置信度与依据，便于人工复核。</summary>
    public string Note { get; set; } = "";

    [JsonIgnore] private byte[]? _magicBytes;

    [JsonIgnore]
    public byte[] MagicBytes => _magicBytes ??= ParseHex(Magic);

    [JsonIgnore]
    public bool IsUsable =>
        Mode == FramingMode.MagicLength
        && LengthSize is 1 or 2 or 4
        && LengthOffset >= 0
        && HeaderSize > LengthOffset + LengthSize - 1
        && HeaderSize <= 4096
        && CmdOffset >= 0
        && MaxFrameSize > 0;

    public static FramingProfile Classic => new()
    {
        Mode = FramingMode.ClassicMir,
        Magic = "",
        Note = "经典 Mir2 定界",
    };

    public string Describe() => Mode == FramingMode.ClassicMir
        ? "经典 Mir2（'#'..'!' / DDCCBBAA+长度）"
        : $"魔数定界 magic={Magic} len@{LengthOffset}×{LengthSize}{(BigEndian ? "BE" : "LE")} header={HeaderSize}"
          + (string.IsNullOrWhiteSpace(Note) ? "" : $"｜{Note}");

    private static byte[] ParseHex(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<byte>();
        var sb = new List<byte>(8);
        int hi = -1;
        foreach (char c in s)
        {
            int v = HexVal(c);
            if (v < 0) { hi = -1; continue; }        // 空格 / 冒号 / 0x 前缀一律跳过
            if (hi < 0) hi = v;
            else { sb.Add((byte)((hi << 4) | v)); hi = -1; }
        }
        return sb.ToArray();
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}

/// <summary>
/// 参数化切帧器：按 <see cref="FramingProfile"/> 从有序字节流里切出完整帧（原字节）。
///
/// 与 <c>MirFrameCodec</c> 的分工：本类只负责"边界在哪"，不问"内容是什么"；
/// 帧内容如何变成 Core 的 <c>MirIncomingFrame</c> 仍由 <c>MirFrameCodec</c> 负责。
///
/// 重同步策略：魔数对不上就往后找下一个魔数；长度字段非法只丢 1 字节重找，
/// 避免"一帧错、整段死"。
/// </summary>
public sealed class ProfileFrameSlicer
{
    private readonly FramingProfile _p;
    private readonly byte[] _magic;
    private readonly List<byte> _acc = new(65536);

    public ProfileFrameSlicer(FramingProfile profile)
    {
        _p = profile;
        _magic = profile.MagicBytes;
    }

    /// <summary>累计被丢弃的噪声字节（持续增长说明定界档不对）。</summary>
    public long DiscardedBytes { get; private set; }

    private enum Step { Frame, NeedMore, Retry }

    /// <summary>喂入一段按序字节，返回本次能切出的完整帧（原字节）。</summary>
    public List<byte[]> Feed(ReadOnlySpan<byte> chunk)
    {
        var frames = new List<byte[]>();
        if (chunk.Length > 0) _acc.AddRange(chunk.ToArray());

        while (true)
        {
            var step = TryExtract(out var frame);
            if (step == Step.Frame) { frames.Add(frame); continue; }
            if (step == Step.Retry) continue;
            break;
        }
        return frames;
    }

    private Step TryExtract(out byte[] frame)
    {
        frame = Array.Empty<byte>();

        if (_magic.Length > 0)
        {
            int idx = IndexOf(_acc, _magic);
            if (idx < 0)
            {
                // 尾部可能是被 TCP 段切开的半个魔数，保留
                int keep = Math.Min(_acc.Count, _magic.Length - 1);
                int drop = _acc.Count - keep;
                if (drop > 0) { DiscardedBytes += drop; _acc.RemoveRange(0, drop); }
                return Step.NeedMore;
            }
            if (idx > 0) { DiscardedBytes += idx; _acc.RemoveRange(0, idx); }
        }

        int need = _p.LengthOffset + _p.LengthSize;
        if (_acc.Count < need) return Step.NeedMore;

        long len = ReadLength();
        int total = _p.HeaderSize + (int)len;
        if (len < 0 || len > _p.MaxFrameSize || total < _p.HeaderSize + 1 || total > _p.MaxFrameSize + _p.HeaderSize)
        {
            DiscardedBytes++;                     // 这个位置不是真帧首：丢 1 字节继续找
            _acc.RemoveRange(0, 1);
            return Step.Retry;
        }
        if (_acc.Count < total) return Step.NeedMore;

        frame = _acc.GetRange(0, total).ToArray();
        _acc.RemoveRange(0, total);
        return Step.Frame;
    }

    private long ReadLength()
    {
        long v = 0;
        if (_p.BigEndian)
            for (int i = 0; i < _p.LengthSize; i++) v = (v << 8) | _acc[_p.LengthOffset + i];
        else
            for (int i = _p.LengthSize - 1; i >= 0; i--) v = (v << 8) | _acc[_p.LengthOffset + i];
        return v;
    }

    private static int IndexOf(List<byte> hay, byte[] needle)
    {
        if (needle.Length == 0) return 0;
        int last = hay.Count - needle.Length;
        for (int i = 0; i <= last; i++)
        {
            if (hay[i] != needle[0]) continue;
            int j = 1;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
