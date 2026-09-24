using System.Text;
using BotClient.Protocol;

namespace BotClient.Script;

/// <summary>天骥(Mir2tianji)角色目录里的 INI:节名和键名都是中文,值里没有逗号也没有引号。
/// 按"顺序保留 + 未知键原样回写"实现 —— 同一个 setup.ini 往往还要给天骥本体读,
/// 我们只认其中几十个键,写回去不能把不认识的设置抹掉。
/// 行尾跟随原文件(实测 小明哥哥 的 setup.ini 全 CRLF,而 挖矿.txt 混用裸 LF)。</summary>
public sealed class GbkIni
{
    private sealed class Section
    {
        public string Name = string.Empty;
        public readonly List<KeyValuePair<string, string>> Items = new();
    }

    private readonly List<Section> _sections = new();
    private bool _dirty;

    public string Path { get; private set; } = string.Empty;
    public bool Exists { get; private set; }
    public IReadOnlyList<string> SectionNames => _sections.Select(s => s.Name).ToList();
    public bool Dirty => _dirty;

    private string _newLine = "\r\n";

    private GbkIni() { }

    /// <summary>读不到文件不算错:返回一个空表,后续 Set 会把它标脏并在新建目录时写出去。</summary>
    public static GbkIni Load(string path)
    {
        var ini = new GbkIni { Path = path };
        if (!File.Exists(path)) return ini;

        string text;
        try
        {
            text = GbkEncoding.Gbk.GetString(File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            BotLog.Warn($"[ini] 读取 {path} 失败: {ex.Message}");
            return ini;
        }
        ini.Exists = true;
        if (text.Contains("\r\n", StringComparison.Ordinal)) ini._newLine = "\r\n";
        else if (text.Contains('\n')) ini._newLine = "\n";

        Section? cur = null;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                string name = line[1..^1].Trim();
                cur = GetSection(ini._sections, name);
                if (cur == null)
                {
                    cur = new Section { Name = name };
                    ini._sections.Add(cur);
                }
                continue;
            }
            if (cur == null) continue;   // 节之前的散行:天骥文件里没有,遇到就丢
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            cur.Items.Add(new KeyValuePair<string, string>(line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
        return ini;
    }

    private static Section? GetSection(List<Section> list, string name)
        => list.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    private static Section GetOrAdd(List<Section> list, string name)
        => GetSection(list, name) ?? Add(list, name);

    private static Section Add(List<Section> list, string name)
    {
        var s = new Section { Name = name };
        list.Add(s);
        return s;
    }

    public IReadOnlyList<KeyValuePair<string, string>> Items(string section)
    {
        var s = GetSection(_sections, section);
        return s == null ? Array.Empty<KeyValuePair<string, string>>() : s.Items;
    }

    public string Get(string section, string key, string fallback = "")
    {
        var s = GetSection(_sections, section);
        if (s == null) return fallback;
        // 重复键取第一个:天骥写文件时是覆盖式的,后面出现的是历史残留。
        foreach (var kv in s.Items)
            if (string.Equals(kv.Key, key, StringComparison.Ordinal)) return kv.Value;
        return fallback;
    }

    public int GetInt(string section, string key, int fallback)
        => int.TryParse(Get(section, key), out int v) ? v : fallback;

    /// <summary>天骥的布尔写法有两种:0/1(绝大多数)和 TRUE/FALSE(itemset/npcSet 的判定列)。</summary>
    public bool GetBool(string section, string key, bool fallback = false)
    {
        string v = Get(section, key);
        if (v.Length == 0) return fallback;
        if (v is "1" or "TRUE" or "true") return true;
        if (v is "0" or "FALSE" or "false") return false;
        return fallback;
    }

    /// <summary>值列表:天骥用逗号分隔(如 面攻击怪物列表、杀堵门喊话),空串当空表。</summary>
    public List<string> GetList(string section, string key)
    {
        string v = Get(section, key);
        if (v.Trim().Length == 0) return new List<string>();
        return v.Split(',', '，').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }

    public void Set(string section, string key, string value)
    {
        var s = GetOrAdd(_sections, section);
        for (int i = 0; i < s.Items.Count; i++)
        {
            if (!string.Equals(s.Items[i].Key, key, StringComparison.Ordinal)) continue;
            if (s.Items[i].Value == value) return;
            s.Items[i] = new KeyValuePair<string, string>(key, value);
            _dirty = true;
            return;
        }
        s.Items.Add(new KeyValuePair<string, string>(key, value));
        _dirty = true;
    }

    public void Save()
    {
        if (!_dirty || Path.Length == 0) return;
        var sb = new StringBuilder();
        foreach (var s in _sections)
        {
            sb.Append('[').Append(s.Name).Append(']').Append(_newLine);
            foreach (var kv in s.Items)
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append(_newLine);
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllBytes(Path, GbkEncoding.Gbk.GetBytes(sb.ToString()));
            Exists = true;
            _dirty = false;
        }
        catch (Exception ex)
        {
            BotLog.Warn($"[ini] 写入 {Path} 失败: {ex.Message}");
        }
    }
}
