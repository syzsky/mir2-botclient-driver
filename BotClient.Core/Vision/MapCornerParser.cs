using System.Text;
using System.Text.RegularExpressions;

namespace BotClient.Vision;

/// <summary>左下角"当前地图/坐标"视觉读数的比对结论。</summary>
public enum CornerVerdict
{
    /// <summary>与嗅探一致（或嗅探侧没有可比对的中文名、坐标一致）。</summary>
    Consistent,

    /// <summary>地图名不一致。</summary>
    MapMismatch,

    /// <summary>坐标不一致。</summary>
    PosMismatch,

    /// <summary>画面读数不可用（未绑定窗口 / 未装 OCR 语言包 / 区域没有文字）。</summary>
    NotReadable,
}

/// <summary>从画面左下角识别出来的"地图名 + 坐标"。纯数据，不含任何平台依赖，便于离线自测。</summary>
public sealed record CornerReading(bool Readable, string MapName, bool HasPos, int X, int Y, string Raw)
{
    public static readonly CornerReading Unreadable = new(false, "", false, 0, 0, "");

    /// <summary>单行摘要，用于状态栏与日志。</summary>
    public string Describe()
    {
        if (!Readable) return "不可读";
        string map = MapName.Length > 0 ? MapName : "（未读到地图名）";
        string pos = HasPos ? $"({X},{Y})" : "（未读到坐标）";
        return $"{map}｜{pos}";
    }
}

/// <summary>
/// 一次视觉核对的完整结论。<see cref="MapChecked"/> 为 false 表示嗅探侧没有中文地图名表
/// （MapInfo.txt 未读到，此时 <c>CurrentMap</c>/<c>MapName</c> 只是编号），地图名无法比对，
/// 只能比坐标 —— 界面上要如实说明，不能假装已核对。
/// </summary>
public sealed record CornerCheck(
    CornerVerdict Verdict,
    CornerReading Screen,
    bool MapChecked,
    string ExpectMap,
    int ExpectX,
    int ExpectY,
    string Note = "")
{
    public bool Ok => Verdict == CornerVerdict.Consistent;

    /// <summary>单行结论（不含标签前缀），日志/状态栏直接可用。</summary>
    public string Describe()
    {
        switch (Verdict)
        {
            case CornerVerdict.NotReadable:
                return "画面读数不可用" + (Note.Length > 0 ? "：" + Note : "");
            case CornerVerdict.MapMismatch:
                return $"不一致：地图 嗅探={ExpectMap} / 画面={Screen.MapName}（仅记录，不改动作）";
            case CornerVerdict.PosMismatch:
                return $"不一致：坐标 嗅探=({ExpectX},{ExpectY}) / 画面=({Screen.X},{Screen.Y})（仅记录，不改动作）";
            default:
                string mapPart = MapChecked
                    ? $"地图={Screen.MapName}"
                    : (Screen.MapName.Length > 0 ? $"地图={Screen.MapName}(嗅探侧无中文名，未比对)" : "地图=未比对");
                string posPart = Screen.HasPos ? $"坐标=({Screen.X},{Screen.Y})" : "坐标=未读到";
                return $"一致：{mapPart} {posPart}（画面左下角）";
        }
    }
}

/// <summary>
/// 左下角读数解析 + 与嗅探数据比对。**纯逻辑**：不截屏、不 OCR、不碰任何 Windows API，
/// 单独编译即可离线自测（tools/CornerReadSelfTest）。
///
/// 为什么要有它：地图/坐标此前只有一条数据源（嗅探包）。嗅探一旦错位（TCP 跨段、魔法数写错、
/// 过滤设备选错），程序会"安静地"按错数据挂机，用户只能靠肉眼盯左下角。这里给出一条**独立**的
/// 只读校验通道：画面读出来的东西和嗅探对不上就报警，数据源是否可信一眼可见。
/// </summary>
public static class MapCornerParser
{
    // 坐标的三种常见版式：带括号 (330,330)、坐标标签后跟数字、裸数字（多数客户端图例去掉了括号）
    private static readonly Regex PosBracket = new(
        @"[（(【\[{]\s*(\d{1,4})\s*[,，:：、/]\s*(\d{1,4})\s*[)）】\]}]",
        RegexOptions.Compiled);

    private static readonly Regex PosLabel = new(
        @"(?:坐标|位置|方位)\s*[:：]?\s*(\d{1,4})\s*[,，:：、/]\s*(\d{1,4})",
        RegexOptions.Compiled);

    // 兜底版式：多数客户端图例不带括号，形如 "比奇省 330,330"。
    //
    // 必须**锚定在文本末尾**：图例的坐标永远在行尾，而"两位数字+逗号+两位数字"这个形状
    // 在任何文本里都很常见（"在线 12,34 人"、"第 3 页 12,34"）。不锚定的话 Parse 会给出
    // HasPos=true 的**错误坐标**，进而在 Compare 里报出假的"坐标不符"——
    // 而这个功能的价值恰恰是"只在有依据时报差异"，假警比不报更糟。
    private static readonly Regex PosBare = new(
        @"(?<!\d)(\d{2,4})\s*[,，、]\s*(\d{2,4})\s*[)）】\]}]?\s*$",
        RegexOptions.Compiled);

    /// <summary>把 OCR 文本解析成读数。解析不出任何有效内容时返回 <see cref="CornerReading.Unreadable"/>。</summary>
    public static CornerReading Parse(string? ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText)) return CornerReading.Unreadable;

        string raw = ocrText.Replace('\r', ' ').Replace('\n', ' ').Trim();
        int x = 0, y = 0;
        bool hasPos = false;
        Match m = PosBracket.Match(raw);
        if (!m.Success) m = PosLabel.Match(raw);
        if (!m.Success) m = PosBare.Match(raw);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int px) && int.TryParse(m.Groups[2].Value, out int py))
        {
            x = px;
            y = py;
            hasPos = true;
        }

        string rest = m.Success ? raw.Remove(m.Index, m.Length) : raw;
        string name = ExtractName(rest);

        if (!hasPos && name.Length == 0) return CornerReading.Unreadable;
        return new CornerReading(true, name, hasPos, x, y, raw);
    }

    /// <summary>
    /// 与嗅探侧数据比对。
    /// </summary>
    /// <param name="expectMap">嗅探侧地图中文名（MapInfo.txt 未读到时会等于编号）。</param>
    /// <param name="expectMapCode">嗅探侧地图编号（如 D1101），画面若显示编号也能比对。</param>
    /// <param name="posTolerance">坐标容差：画面图例刷新有延迟，默认允许差 1 格。</param>
    /// <param name="checkPos">是否比对坐标。</param>
    public static CornerCheck Compare(
        CornerReading screen,
        string? expectMap,
        string? expectMapCode,
        int expectX,
        int expectY,
        int posTolerance = 1,
        bool checkPos = true)
    {
        string em = Normalize(expectMap ?? "");
        string ec = Normalize(expectMapCode ?? "");
        int tol = posTolerance < 0 ? 0 : posTolerance;

        if (!screen.Readable)
            return new CornerCheck(CornerVerdict.NotReadable, screen, false, em, expectX, expectY);

        bool nameKnown = em.Length > 0;          // 嗅探侧有中文名（MapInfo.txt 读到了）才敢判"地图不对"
        bool mapChecked = nameKnown;
        if (screen.MapName.Length > 0)
        {
            string sn = Normalize(screen.MapName);
            bool eqName = nameKnown && (sn.Contains(em, StringComparison.Ordinal) || em.Contains(sn, StringComparison.Ordinal));
            bool eqCode = ec.Length > 0 && sn.Contains(ec, StringComparison.OrdinalIgnoreCase);

            // 只有在"我们手里有中文名"时才把不一致当差异报出来：
            // 只拿到编号（MapInfo 没读到）而画面显示中文名，是没有能力判定的，不能误报。
            if (nameKnown && !eqName && !eqCode)
                return new CornerCheck(CornerVerdict.MapMismatch, screen, true, em, expectX, expectY);

            mapChecked = nameKnown || eqCode;
        }

        if (checkPos && screen.HasPos
            && (Math.Abs(screen.X - expectX) > tol || Math.Abs(screen.Y - expectY) > tol))
            return new CornerCheck(CornerVerdict.PosMismatch, screen, mapChecked, em, expectX, expectY);

        return new CornerCheck(CornerVerdict.Consistent, screen, mapChecked, em, expectX, expectY);
    }

    /// <summary>
    /// 从"去掉坐标"的残余文本里挑出地图名：按空白切词，丢掉装饰符/纯数字/标签词，
    /// 取第一个含中文或字母的词。图例版式（名字在左、坐标在右）下即地图名。
    /// </summary>
    internal static string ExtractName(string rest)
    {
        foreach (string token in rest.Split(new[] { ' ', '\t', '　' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = StripDecoration(token);
            if (t.Length == 0) continue;
            if (t is "坐标" or "位置" or "方位" or "地图" or "当前地图") continue;
            if (IsAllDigits(t) || IsAllPunctuation(t)) continue;
            if (HasCjk(t) || HasLetter(t)) return t;
        }
        return string.Empty;
    }

    /// <summary>去掉图例常见的装饰与括号备注：'┃ 比奇省 ┃' → '比奇省'；'比奇省(推荐)' → '比奇省'。</summary>
    internal static string StripDecoration(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool inBracket = false;
        foreach (char c in s)
        {
            if (c is '[' or '【' or '〖' or '〔') { inBracket = true; continue; }
            if (c is ']' or '】' or '〗' or '〕') { inBracket = false; continue; }
            if (c is '┃' or '│' or '|' or '丨' or '｜' or '　' or '"' or '“' or '”' or '·' or '★' or '☆') continue;
            if (inBracket) continue;
            sb.Append(c);
        }
        string t = sb.ToString().Trim();
        int cut = t.IndexOfAny(new[] { '(', '（' });   // "比奇省(推荐)" → "比奇省"
        if (cut > 0) t = t[..cut];
        return t.Trim();
    }

    /// <summary>与 MapInfoFile 保持同一套归一化规则：去装饰/空白，"比奇省(推荐)" → "比奇省"。</summary>
    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '┃' or '│' or '|' or '　' || char.IsWhiteSpace(c)) continue;
            sb.Append(c);
        }
        string t = sb.ToString();
        int cut = t.IndexOfAny(new[] { '(', '（', '[', '【' });
        return cut > 0 ? t[..cut] : t;
    }

    private static bool IsAllDigits(string s)
    {
        foreach (char c in s) if (!char.IsDigit(c)) return false;
        return s.Length > 0;
    }

    private static bool IsAllPunctuation(string s)
    {
        foreach (char c in s) if (!char.IsPunctuation(c) && !char.IsSymbol(c)) return false;
        return s.Length > 0;
    }

    private static bool HasCjk(string s)
    {
        foreach (char c in s) if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return false;
    }

    private static bool HasLetter(string s)
    {
        foreach (char c in s) if (char.IsLetter(c)) return true;
        return false;
    }
}
