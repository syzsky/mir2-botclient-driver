using System.Buffers.Binary;
using System.Reflection;
using BotClient.Protocol;   // Grobal2（反射已知命令码）

namespace BotClient.ClientDriver.Sniff;

/// <summary>
/// 自动定界探测器：**只读**一段已归并排序的下行字节流，反推出该服用的是哪种帧定界。
///
/// 思路（不猜、只看证据）：
///   1. 熵检查——高熵整流说明是加密/压缩，定界再准也读不出内容，直接如实否决；
///   2. 候选枚举——魔数(高频 4 元组) × 长度字段位置/位宽/字节序 × 头长，加上经典文本帧作对照；
///   3. 三条判据打分——切出的帧能不能首尾自洽地连成链（自洽帧数/覆盖率）、
///      帧内 CmdPack.Ident 落在已知命令码表里的比例。
///
/// 输出是一份 <see cref="FramingProfile"/> 候选 + 人类可读报告，写回配置的动作交给调用方。
/// </summary>
public sealed class SplitterAutoDetector
{
    public sealed record Candidate(
        FramingProfile Profile, int Score, int Frames, double Coverage, int KnownHits, string Detail);

    public sealed class Result
    {
        public bool Ok { get; init; }
        public FramingProfile? Best { get; init; }
        public double Entropy { get; init; }
        public IReadOnlyList<Candidate> Candidates { get; init; } = Array.Empty<Candidate>();
        public string Report { get; init; } = "";
    }

    /// <summary>至少切出这么多帧才认。</summary>
    public const int MinFrames = 8;

    private const int MaxMagicCandidates = 8;

    /// <summary>CmdPack.Ident 在 CmdPack 结构内的偏移（Core 的结构体布局，见 CmdPack.cs）。</summary>
    private const int IdentInCmdPack = 8;
    private const double EncryptedEntropy = 7.6;

    public static Result Detect(byte[] sample)
    {
        double entropy = Entropy(sample);
        var cands = new List<Candidate>();

        // ---- 对照项：经典 Mir2 文本帧 '#' + 序号 + payload + '!'
        int pairs = CountTextFrames(sample, out int textBytes);
        if (pairs >= MinFrames)
        {
            cands.Add(new Candidate(
                FramingProfile.Classic,
                70 + Math.Min(20, pairs / 2),
                pairs,
                sample.Length == 0 ? 0 : Math.Min(1.0, (double)textBytes / sample.Length),
                0,
                $"经典文本帧 '#'..'!' ×{pairs}"));
        }

        // ---- 魔数 + 长度 候选
        var known = KnownIdents();
        if (sample.Length >= 256)
        {
            foreach (var magic in MagicCandidates(sample))
            {
                foreach (int lenOffset in new[] { magic.Length, magic.Length + 2, magic.Length + 4 })
                foreach (int lenSize in new[] { 4, 2 })
                foreach (bool be in new[] { false, true })
                foreach (int header in new[] { 24, 16, 32 })
                {
                    if (header < lenOffset + lenSize) continue;
                    var p = new FramingProfile
                    {
                        Mode = FramingMode.MagicLength,
                        Magic = ToHex(magic),
                        LengthOffset = lenOffset,
                        LengthSize = lenSize,
                        BigEndian = be,
                        HeaderSize = header,
                        CmdOffset = 8,
                    };
                    if (!p.IsUsable) continue;
                    var c = Evaluate(sample, p, known);
                    if (c != null) cands.Add(c);
                }
            }
        }

        // ---- 同一 magic 的多个参数只留最高分，避免报告被同族候选淹没
        var top = cands
            .GroupBy(c => c.Profile.Mode == FramingMode.ClassicMir
                ? "classic"
                : $"m:{c.Profile.Magic}|o:{c.Profile.LengthOffset}|s:{c.Profile.LengthSize}|e:{c.Profile.BigEndian}|h:{c.Profile.HeaderSize}")
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score)
            .Take(6)
            .ToList();

        var best = top.FirstOrDefault(c => c.Score >= 60 && c.Coverage >= 0.6);
        bool encrypted = entropy >= EncryptedEntropy && best == null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"样本 {sample.Length} 字节，熵 {entropy:F2} bits/byte"
                      + (entropy >= EncryptedEntropy ? "（高熵：疑似加密/压缩）" : "（业务流特征）"));
        if (top.Count == 0)
        {
            sb.AppendLine("候选：无（没有一种定界能把样本首尾自洽地切开）");
        }
        else
        {
            sb.AppendLine("候选（分数 = 自洽性 40 + 覆盖率 30 + 命令码命中 30）：");
            for (int i = 0; i < top.Count; i++)
            {
                var c = top[i];
                sb.AppendLine($"  {(i == 0 ? "①" : i == 1 ? "②" : i == 2 ? "③" : "④")} {c.Score} 分｜{c.Profile.Describe()}"
                              + $"｜自洽 {c.Frames} 帧｜覆盖 {c.Coverage:P0}｜命中已知命令码 {c.KnownHits}");
            }
        }

        if (best != null)
        {
            best.Profile.Note = $"自动探测 score={best.Score} 覆盖={best.Coverage:P0} 命中={best.KnownHits}";
            sb.AppendLine($"结论：采用「{best.Profile.Describe()}」。");
        }
        else if (encrypted)
        {
            sb.AppendLine("结论：整流高熵且无候选同时满足自洽与覆盖率阈值——该服对下行做了加密/压缩，"
                          + "定界自动化无能为力；需要拿到该服的解密链才能继续（此路不通，如实报错）。");
        }
        else
        {
            sb.AppendLine("结论：未能自动定出可靠帧界。可加大采样（多走几步/多打几下）后重试，"
                          + "或人工填 clientdriver.json 的 framing 段（magic / lenOffset / lenSize / header）。");
        }

        return new Result
        {
            Ok = best != null,
            Best = best?.Profile,
            Entropy = entropy,
            Candidates = top,
            Report = sb.ToString().TrimEnd(),
        };
    }

    // ------------------------------------------------------------------ 打分

    private static Candidate? Evaluate(byte[] sample, FramingProfile p, HashSet<ushort> known)
    {
        var slicer = new ProfileFrameSlicer(p);
        var frames = slicer.Feed(sample);
        if (frames.Count < MinFrames) return null;

        long consumed = 0;
        int hits = 0;
        foreach (var f in frames)
        {
            consumed += f.Length;
            int identAt = p.CmdOffset + IdentInCmdPack;
            if (f.Length >= identAt + 2)
            {
                ushort ident = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(identAt, 2));
                if (ident != 0 && known.Contains(ident)) hits++;
            }
        }

        double coverage = (double)consumed / sample.Length;
        if (coverage < 0.5) return null;             // 覆盖率低说明帧界是巧合

        int score = (int)Math.Round(40 * Math.Min(1.0, frames.Count / 32.0))
                    + (int)Math.Round(30 * Math.Min(1.0, coverage))
                    + Math.Min(30, hits * 3);

        return new Candidate(p, score, frames.Count, coverage, hits,
            $"magic={p.Magic} len@{p.LengthOffset}×{p.LengthSize}{(p.BigEndian ? "BE" : "LE")} header={p.HeaderSize}");
    }

    /// <summary>已知命令码表：反射 Core 的 Grobal2（CM_/SM_ 开头的 const）。</summary>
    public static HashSet<ushort> KnownIdents()
    {
        var set = new HashSet<ushort>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        foreach (var f in typeof(Grobal2).GetFields(flags))
        {
            if (!f.Name.StartsWith("CM_", StringComparison.Ordinal)
                && !f.Name.StartsWith("SM_", StringComparison.Ordinal)) continue;
            try
            {
                object? v = f.GetValue(null);
                if (v == null) continue;
                int n = Convert.ToInt32(v);
                if (n > 0 && n < 65536) set.Add((ushort)n);
            }
            catch { /* 个别字段取不到值不影响整体 */ }
        }
        return set;
    }

    // ------------------------------------------------------------------ 样本特征

    public static double Entropy(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;
        Span<int> hist = stackalloc int[256];
        foreach (byte b in data) hist[b]++;
        double n = data.Length, e = 0;
        foreach (int c in hist)
            if (c > 0) { double pr = c / n; e -= pr * Math.Log2(pr); }
        return e;
    }

    private static int CountTextFrames(byte[] s, out int coveredBytes)
    {
        coveredBytes = 0;
        int frames = 0, i = 0;
        while (i < s.Length)
        {
            int start = Array.IndexOf(s, (byte)'#', i);
            if (start < 0) break;
            int end = Array.IndexOf(s, (byte)'!', start + 1);
            if (end < 0) break;
            frames++;
            coveredBytes += end - start + 1;
            i = end + 1;
        }
        return frames;
    }

    /// <summary>找出可作帧首魔数的高频 4 元组：既要频繁出现，间隔又要"规律"（真魔数按帧长周期出现）。</summary>
    private static List<byte[]> MagicCandidates(byte[] s)
    {
        var freq = new Dictionary<uint, (int Count, List<int> Pos)>();
        for (int i = 0; i + 4 <= s.Length; i++)
        {
            uint key = (uint)(s[i] | (s[i + 1] << 8) | (s[i + 2] << 16) | (s[i + 3] << 24));
            freq.TryGetValue(key, out var e);
            e.Count++;
            e.Pos ??= new List<int>();
            if (e.Pos.Count < 1024) e.Pos.Add(i);
            freq[key] = e;
        }

        return freq
            .Where(kv => kv.Value.Count >= MinFrames)
            .Select(kv => new
            {
                kv.Key,
                kv.Value.Count,
                Score = IntervalScore(kv.Value.Pos) * Math.Log(1 + kv.Value.Count),
            })
            .Where(x => x.Score > 0.4)
            .OrderByDescending(x => x.Score)
            .Take(MaxMagicCandidates)
            .Select(x => new[]
            {
                (byte)(x.Key), (byte)(x.Key >> 8), (byte)(x.Key >> 16), (byte)(x.Key >> 24),
            })
            .ToList();
    }

    private static double IntervalScore(List<int> pos)
    {
        if (pos.Count < MinFrames) return 0;
        int ok = 0, total = 0;
        for (int i = 1; i < pos.Count; i++)
        {
            int gap = pos[i] - pos[i - 1];
            if (gap < 12) continue;                  // 密集命中：文本噪声（如全 0 填充）
            total++;
            if (gap <= 8192) ok++;
        }
        return total == 0 ? 0 : (double)ok / total;
    }

    private static string ToHex(byte[] b) => string.Concat(b.Select(x => x.ToString("X2")));
}
