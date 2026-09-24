using BotClient.Script;

namespace BotClient.Assets;

/// <summary>服务端 Envir\MapInfo.txt:一行 <c>[地图编号 中文名] 标志…</c>(本机实测
/// <c>D:\MirServer\Mir200\Envir\MapInfo.txt</c> 第一行就是 <c>[0 比奇省]</c>)。
/// 换图包 SM_NEWMAP 的包体只有编号,而天骥脚本的 [地图名] 要中文名(手册:返回角色当前所处地图的中文名称),
/// 名字只能从这张表查。查不到就退回编号:[当前地图名] 本来就是编号,读到空串比读到编号更糟。</summary>
public sealed class MapInfoFile
{
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>真正读到的那份文件路径,没读到时为空串。日志和 CharCheck 要靠它说明名字从哪来。</summary>
    public string FilePath = string.Empty;
    public int Count => _names.Count;

    public string NameOf(string mapCode)
        => mapCode.Length > 0 && _names.TryGetValue(mapCode, out string? n) ? n : mapCode;

    /// <summary>全部"地图代码 → 名字"。传送菜单探测要用它做"这个名字属于哪张图"的反查。</summary>
    public IReadOnlyDictionary<string, string> Codes => _names;

    /// <summary>
    /// 反查：把 NPC 菜单里出现的地名（或地图代码本身）翻成地图代码。
    ///
    /// 三级匹配，从严到宽：
    ///   ① 精确：菜单文字 == 地图名（去掉空白/装饰符后）；
    ///   ② 包含：菜单文字包含地图名，或地图名包含菜单文字（菜单常写成"比奇省(推荐)"）；
    ///   ③ 代码：菜单文字本身就是代码（"0" / "0101"）。
    /// 歧义时取**名字最短**的那个：长名字往往是"比奇省·新手村"这类分支，短名字才是主图。
    /// </summary>
    public string? TryFindCode(string menuText)
    {
        if (string.IsNullOrWhiteSpace(menuText)) return null;
        string needle = Normalize(menuText);
        if (needle.Length == 0) return null;

        if (_names.TryGetValue(needle, out _)) return needle;   // 菜单直接写代码

        string? best = null; int bestLen = int.MaxValue;
        foreach (var (code, name) in _names)
        {
            string clean = Normalize(name);
            if (clean.Length == 0) continue;
            bool hit = clean.Equals(needle, StringComparison.OrdinalIgnoreCase)
                    || clean.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || needle.Contains(clean, StringComparison.OrdinalIgnoreCase);
            if (!hit) continue;
            if (clean.Length < bestLen) { bestLen = clean.Length; best = code; }
        }
        return best;
    }

    /// <summary>去掉菜单里的装饰字符与空白：'┃'、全角空格、括号备注等。</summary>
    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '┃' or '│' or '|' or '　' || char.IsWhiteSpace(c)) continue;
            sb.Append(c);
        }
        string t = sb.ToString();
        int cut = t.IndexOfAny(new[] { '(', '（', '[', '【' });   // "比奇省(推荐)" → "比奇省"
        return cut > 0 ? t[..cut] : t;
    }

    /// <summary>mapDirHint 是宿主已知的 .map 目录(设置里可填);服务端本机部署时它的上级 Envir 里就有这张表。
    /// 一份都读不到就返回 null,由调用方退回编号。</summary>
    public static MapInfoFile? TryLoad(string? mapDirHint)
    {
        foreach (string dir in CandidateDirs(mapDirHint))
        {
            string file = Path.Combine(dir, "MapInfo.txt");
            try
            {
                if (!File.Exists(file)) continue;
                var m = new MapInfoFile();
                if (m.ReadFile(file) == 0) continue;
                m.FilePath = file;
                return m;
            }
            catch (Exception ex) { BotLog.Warn($"[mapinfo] 读 {file} 失败: {ex.Message}"); }
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirs(string? mapDirHint)
    {
        if (!string.IsNullOrWhiteSpace(mapDirHint))
        {
            yield return mapDirHint;
            yield return Path.Combine(mapDirHint, "..", "Envir");
        }
        yield return Path.Combine(AppContext.BaseDirectory, "Map");
        yield return AppContext.BaseDirectory;
        yield return @"D:\MirServer\Mir200\Envir";
    }

    private int ReadFile(string file)
    {
        int added = 0;
        foreach (string raw in CharData.ReadScriptText(file).Text.Split('\n'))
        {
            string s = raw.Trim();
            int close = s.IndexOf(']');
            if (s.Length < 4 || s[0] != '[' || s.StartsWith(';') || close < 2) continue;
            string head = s[1..close].Trim();
            int space = head.IndexOf(' ');
            if (space <= 0 || space == head.Length - 1) continue;   // 只写编号的行没有名字可给
            _names[head[..space].Trim()] = head[(space + 1)..].Trim();
            added++;
        }
        return added;
    }
}
