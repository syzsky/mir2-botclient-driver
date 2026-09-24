using System.Text;
using BotClient.Protocol;

namespace BotClient.Script;

/// <summary>天骥角色数据目录(DATA\角色名_x_y\)的一整套文件。
/// 之所以照它的布局读而不是自己造一套:老配置和老脚本是配套的,能直接拿来跑才算"兼容";
/// 写回去时天骥本体还要能继续读,所以未知行一律保留。</summary>
public sealed class CharData
{
    public string Dir { get; }
    public GbkIni Setup { get; }

    /// <summary>角色名(目录名去掉尾巴上的 _x_y)。</summary>
    public string CharacterName { get; }

    public NameList Bosses { get; }
    public NameList NoKillNpc { get; }
    public NameList TrustPlayers { get; }
    public NameList KillPlayers { get; }
    public ItemSetTable ItemSet { get; }
    public NpcFuncTable NpcFunc { get; }
    public DoorLinkGraph DoorLinks { get; }
    public GoodTable Good { get; }
    public SkillTable Skills { get; }
    public StdItemTable StdItems { get; }
    public LineLog Whispers { get; }
    public LineLog DeathRecords { get; }

    /// <summary>主脚本固定叫 脚本.txt;装入脚本[挖矿] 读的就是同目录的 挖矿.txt。
    /// 脚本层只按行原文读写,解析在 ScriptEngine。</summary>
    public TextLines MainScript => new(At(Dir, "脚本.txt"));

    /// <summary>按 装入脚本[名] 的名字取脚本文件(名不带 .txt)。</summary>
    public TextLines Script(string name) => new(At(Dir, name + ".txt"));

    private CharData(string dir, string charName)
    {
        Dir = dir;
        CharacterName = charName;
        Setup = GbkIni.Load(At(dir, "setup.ini"));
        Bosses = new NameList(At(dir, "Boss.ini"));
        NoKillNpc = new NameList(At(dir, "noKillNpc.ini"));
        TrustPlayers = new NameList(At(dir, "BelivePlayer.ini"));
        KillPlayers = new NameList(At(dir, "killPlayer.ini"));
        ItemSet = new ItemSetTable(At(dir, "itemset.ini"));
        NpcFunc = new NpcFuncTable(At(dir, "npcFunc.ini"));
        DoorLinks = new DoorLinkGraph(At(dir, "doorlink.ini"));
        Good = new GoodTable(At(dir, "good.ini"));
        Skills = new SkillTable(At(dir, "skills.ini"));
        StdItems = new StdItemTable(At(dir, "stditem.ini"));
        Whispers = new LineLog(At(dir, "whisper.ini"));
        DeathRecords = new LineLog(At(dir, "DeathRec.txt"));
    }

    private static string At(string dir, string file) => System.IO.Path.Combine(dir, file);

    /// <summary>在 DATA 根目录里找角色的数据目录(天骥的命名是 角色名_序号_序号,序号含义作者没留文档,
    /// 所以只按"角色名打头 + 两个下划线段"匹配,取第一个)。</summary>
    public static string? FindCharDir(string dataRoot, string charName)
    {
        if (!Directory.Exists(dataRoot) || charName.Length == 0) return null;
        try
        {
            foreach (var d in Directory.GetDirectories(dataRoot).OrderBy(x => x, StringComparer.Ordinal))
            {
                string n = System.IO.Path.GetFileName(d);
                if (n == charName || n.StartsWith(charName + "_", StringComparison.Ordinal)) return d;
            }
        }
        catch (Exception ex)
        {
            BotLog.Warn($"[char] 扫描 {dataRoot} 失败: {ex.Message}");
        }
        return null;
    }

    /// <summary>目录不存在时按天骥的布局新建(空的名单文件也一并建出来,方便天骥和人工追加)。</summary>
    public static CharData Open(string dir, string charName)
    {
        Directory.CreateDirectory(dir);
        var c = new CharData(dir, charName);
        c.Bosses.EnsureCreated();
        c.NoKillNpc.EnsureCreated();
        c.TrustPlayers.EnsureCreated();
        c.KillPlayers.EnsureCreated();
        return c;
    }

    public void Save()
    {
        Setup.Save();
        ItemSet.Save();
        NpcFunc.Save();
        DoorLinks.Save();
        Bosses.Save();
        NoKillNpc.Save();
        TrustPlayers.Save();
        KillPlayers.Save();
    }

    // ---- setup.ini 里脚本层要用的几组值(键名一字不改照天骥) ----

    public string NpcCommand(string key, string fallback) => Setup.Get("Command", key, fallback);
    public string BuyCommand => NpcCommand("购买", "@buy");
    public string SellCommand => NpcCommand("售物", "@sell");
    public string StorageCommand => NpcCommand("存物", "@storage");
    public string GetBackCommand => NpcCommand("取物", "@getback");
    public string RepairCommand => NpcCommand("修理", "@repair");
    public string SpecialRepairCommand => NpcCommand("特修", "@s_repair");

    /// <summary>[拣物]传送命令 形如 "@move X,Y",脚本 传送[x,y] 时替换占位后当聊天命令发。</summary>
    public string TeleportCommand => Setup.Get("拣物", "传送命令", "@move X,Y");

    public int FightRangeCells => Setup.GetInt("其它", "堵门攻击半径", 6);
    public bool NoExpireReconnect => Setup.GetBool("其它", "无数据重连");
    public int MiningToolUses => Setup.GetInt("矿工", "挖矿次数", 40);
    public string MiningTool => Setup.Get("矿工", "挖矿道具", "鹤嘴锄");

    /// <summary>[自动发言]发言内容 用 '|' 分隔轮播。</summary>
    public List<string> AutoChatLines
    {
        get
        {
            string v = Setup.Get("自动发言", "发言内容");
            return v.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
    }

    public int AutoChatIntervalSec => Setup.GetInt("自动发言", "发言间隔", 120);

    /// <summary>逃跑档位:(触发百分比, 物品名, 是否用物品代替喝药)。天骥有 HP/MP 各两档。</summary>
    public IEnumerable<(int Percent, string Item, bool UseItem)> EscapeRules(bool mp)
    {
        for (int i = 1; i <= 2; i++)
        {
            int pct = Setup.GetInt("逃跑方式", mp ? $"逃跑MP{i}" : $"逃跑HP{i}", 0);
            string item = Setup.Get("逃跑方式", mp ? $"MP逃跑物品{i}" : $"逃跑物品{i}");
            bool useItem = Setup.GetBool("逃跑方式", mp ? $"MP是否逃跑使用物品{i}" : $"是否逃跑使用物品{i}");
            if (pct > 0 || item.Length > 0) yield return (pct, item, useItem);
        }
    }

    // ============================================================
    //  下面几个是"行式文件"的小表:全部按原始字段数组存,写回时不认识的列照抄,
    //  因为实测同一份文件里字段数会变(大哥的 npcSet 一行 3 个字段,小明的是 15 个)。
    // ============================================================

    /// <summary>一行一条名字的名单文件(Boss.ini / noKillNpc.ini / BelivePlayer.ini / killPlayer.ini)。
    /// 天骥是追加式写入、不去重,所以这里判重只用于"要不要新增",不清洗旧行。</summary>
    public sealed class NameList
    {
        private readonly string _path;
        private readonly List<string> _lines = new();
        private bool _dirty;

        public NameList(string path)
        {
            _path = path;
            if (!File.Exists(path)) return;
            foreach (var l in ReadGbkLines(path)) if (l.Trim().Length > 0) _lines.Add(l.Trim());
        }

        public IReadOnlyList<string> Lines => _lines;
        public bool Contains(string name) => _lines.Any(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase));

        public void EnsureCreated()
        {
            if (!File.Exists(_path)) { _dirty = true; Save(); }
        }

        public bool Add(string name)
        {
            if (name.Length == 0 || Contains(name)) return false;
            _lines.Add(name);
            _dirty = true;
            return true;
        }

        public void Save()
        {
            if (!_dirty) return;
            try
            {
                File.WriteAllBytes(_path, GbkEncoding.Gbk.GetBytes(string.Join("\r\n", _lines) + (_lines.Count > 0 ? "\r\n" : "")));
                _dirty = false;
            }
            catch (Exception ex) { BotLog.Warn($"[char] 写入 {_path} 失败: {ex.Message}"); }
        }
    }

    /// <summary>逐行原文的 GBK 文本文件(脚本、以及以后要写回天骥目录的任何文本)。
    /// 读的时候保留空行和注释行(它们对脚本行号有意义),写的时候统一 CRLF。</summary>
    public sealed class TextLines
    {
        public string Path { get; }
        public bool Exists { get; private set; }
        public List<string> Lines { get; } = new();

        private Encoding _enc = GbkEncoding.Gbk;

        public TextLines(string path)
        {
            Path = path;
            Exists = File.Exists(path);
            if (Exists)
            {
                try { (string text, _enc) = ReadScriptText(path); Lines.AddRange(SplitLines(text)); }
                catch (Exception ex) { BotLog.Warn($"[char] 读取 {path} 失败: {ex.Message}"); _enc = GbkEncoding.Gbk; }
            }
            TrimTrailingBlanks();
        }

        public string Text => string.Join("\r\n", Lines);

        public void SetText(string text)
        {
            Lines.Clear();
            Lines.AddRange(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
            TrimTrailingBlanks();
        }

        /// <summary>文件末尾那个换行不是空行:不去掉的话每存一次就多长一行,读回来也和原文对不上。</summary>
        private void TrimTrailingBlanks()
        {
            while (Lines.Count > 0 && Lines[^1].Trim().Length == 0) Lines.RemoveAt(Lines.Count - 1);
        }

        public void Save()
        {
            try
            {
                // 按读进来时认出的编码写回:UTF-8 的脚本被人改一次就悄悄转成 GBK,天骥那边就读回去了乱码。
                // Encoding.UTF8 这个静态实例的 GetBytes 会带 BOM,所以要过一道无 BOM 的。
                byte[] body = _enc.CodePage == 65001
                    ? NoBomUtf8.GetBytes(string.Join("\r\n", Lines) + "\r\n")
                    : GbkEncoding.Gbk.GetBytes(string.Join("\r\n", Lines) + "\r\n");
                File.WriteAllBytes(Path, body);
                Exists = true;
            }
            catch (Exception ex) { BotLog.Warn($"[char] 写入 {Path} 失败: {ex.Message}"); }
        }
    }

    /// <summary>whisper.ini / DeathRec.txt 这类只追加的流水。天骥的死亡记录是新条目插在最前。</summary>
    public sealed class LineLog
    {
        private readonly string _path;
        public LineLog(string path) { _path = path; }
        public IReadOnlyList<string> Read() => File.Exists(_path) ? ReadGbkLines(_path) : Array.Empty<string>();
        public void Append(string line, bool atTop = false)
        {
            try
            {
                var lines = Read().ToList();
                if (atTop) lines.Insert(0, line); else lines.Add(line);
                File.WriteAllBytes(_path, GbkEncoding.Gbk.GetBytes(string.Join("\r\n", lines) + "\r\n"));
            }
            catch (Exception ex) { BotLog.Warn($"[char] 写流水 {_path} 失败: {ex.Message}"); }
        }
    }

    /// <summary>itemset.ini:`物品名,类别,已判定,档次,0,字段6,字段7,FALSE`。
    /// 类别是脚本 [待售衣服][数量] 这类聚合量的依据,档次是"值不值得留"的依据。</summary>
    public sealed class ItemSetTable : LineTable
    {
        public ItemSetTable(string path) : base(path) { }
        public string? Category(string itemName) => Find(itemName) is string[] r && r.Length > 1 ? r[1] : null;
        public int Grade(string itemName) => Find(itemName) is string[] r && r.Length > 3 && int.TryParse(r[3], out int g) ? g : 0;
        public IEnumerable<string> NamesInCategory(string category)
            => Rows.Where(r => r.Length > 1 && r[1] == category).Select(r => r[0]);
        /// <summary>没见过的新物品记一行(天骥的"自动记录新物品")。</summary>
        public bool Learn(string itemName, string category = "其它")
        {
            if (itemName.Length == 0 || Find(itemName) != null) return false;
            Add(new[] { itemName, category, "0", "3", "0", "无", "无", "FALSE" });
            return true;
        }
    }

    /// <summary>npcFunc.ini:`NPC名,地图名(地图编号),X,Y,None`。脚本 找到NPC[名,地图,X,Y] 的坐标来源。</summary>
    public sealed class NpcFuncTable : LineTable
    {
        public NpcFuncTable(string path) : base(path) { }

        /// <summary>地图编号大小写并存(实测 b347 与 B347 同现),所以比较一律忽略大小写。</summary>
        public (string Name, string MapCode, int X, int Y)? Find(string npcName, string? mapCode = null)
        {
            foreach (var r in Rows)
            {
                if (r.Length < 4 || !string.Equals(r[0], npcName, StringComparison.Ordinal)) continue;
                string code = NpcFuncTable.SplitMap(r[1]).Code;
                if (mapCode != null && !string.Equals(code, mapCode, StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(r[2], out int x) && int.TryParse(r[3], out int y)) return (r[0], code, x, y);
            }
            return null;
        }

        public bool Learn(string npcName, string mapName, string mapCode, int x, int y)
        {
            string display = mapName.Length > 0 ? $"{mapName}({mapCode})" : mapCode;
            if (Rows.Any(r => r.Length >= 4 && r[0] == npcName && r[1] == display && r[2] == x.ToString() && r[3] == y.ToString()))
                return false;
            Add(new[] { npcName, display, x.ToString(), y.ToString(), "None" });
            return true;
        }

        internal static (string Name, string Code) SplitMap(string s)
        {
            int i = s.IndexOf('(');
            if (i < 0 || !s.EndsWith(')')) return (s, s);
            return (s[..i], s[(i + 1)..^1]);
        }
    }

    /// <summary>doorlink.ini:`地图名[编号](X,Y)-地图名[编号](X,Y)`,一行一条**有向**边。
    /// 脚本 走到门点[..]到达[..] 与跨图寻路都靠它;它是角色自己探明累积出来的,所以每份不一样。</summary>
    public sealed class DoorLinkGraph : LineTable
    {
        public sealed record Edge(string FromMap, int FromX, int FromY, string ToMap, int ToX, int ToY, string ToMapName);

        private readonly List<Edge> _edges = new();

        public DoorLinkGraph(string path) : base(path)
        {
            // 门点行里没有节,但坐标里的逗号会被基类切开,所以必须拼回整行再解析。
            foreach (var r in Rows)
            {
                var e = Parse(Raw(r));
                if (e != null) _edges.Add(e);
            }
        }

        public IReadOnlyList<Edge> Edges => _edges;

        public static Edge? Parse(string line)
        {
            // 地图名本身可能含 '-',所以按"第一个节点的 ')'"后面切,而不是 IndexOf('-')。
            // dash 落在 ')' 上:左段要带上这个右括号,右段从它后面第二个字符(跳过 '-')开始。
            int dash = line.IndexOf(")-", StringComparison.Ordinal);
            if (dash < 0) return null;
            if (!TryNode(line[..(dash + 1)], out string m1, out int x1, out int y1, out _)) return null;
            if (!TryNode(line[(dash + 2)..], out string m2, out int x2, out int y2, out string name2)) return null;
            return new Edge(m1, x1, y1, m2, x2, y2, name2);
        }

        private static bool TryNode(string node, out string mapCode, out int x, out int y, out string mapName)
        {
            mapCode = ""; x = y = 0; mapName = "";
            int lb = node.IndexOf('['), rb = node.IndexOf(']');
            int p1 = node.IndexOf('('), p2 = node.IndexOf(')');
            if (lb < 0 || rb < lb || p1 < rb || p2 < p1) return false;
            mapName = node[..lb];
            mapCode = node[(lb + 1)..rb];
            string[] xy = node[(p1 + 1)..p2].Split(',');
            if (xy.Length != 2 || !int.TryParse(xy[0], out x) || !int.TryParse(xy[1], out y)) return false;
            return true;
        }

        public IEnumerable<Edge> From(string mapCode)
            => _edges.Where(e => string.Equals(e.FromMap, mapCode, StringComparison.OrdinalIgnoreCase));

        /// <summary>按地图编号做 BFS(边权相等,只要一条能走通的门链)。同图自环(祖玛阁)也算一步。</summary>
        public List<Edge> FindPath(string fromMap, string toMap)
        {
            if (string.Equals(fromMap, toMap, StringComparison.OrdinalIgnoreCase)) return new List<Edge>();
            var prev = new Dictionary<string, Edge>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fromMap };
            var queue = new Queue<string>();
            queue.Enqueue(fromMap);
            while (queue.Count > 0)
            {
                string cur = queue.Dequeue();
                foreach (var e in From(cur))
                {
                    if (!seen.Add(e.ToMap)) continue;
                    prev[e.ToMap] = e;
                    if (string.Equals(e.ToMap, toMap, StringComparison.OrdinalIgnoreCase))
                    {
                        var path = new List<Edge>();
                        for (string s = toMap; !string.Equals(s, fromMap, StringComparison.OrdinalIgnoreCase); s = prev[s].FromMap)
                            path.Insert(0, prev[s]);
                        return path;
                    }
                    queue.Enqueue(e.ToMap);
                }
            }
            return new List<Edge>();   // 空表 = 这张图没探到过那条路,脚本层要报"无门点记录"
        }

        public bool Learn(string fromName, string fromMap, int fx, int fy, string toName, string toMap, int tx, int ty)
        {
            if (_edges.Any(e => string.Equals(e.FromMap, fromMap, StringComparison.OrdinalIgnoreCase)
                                && e.FromX == fx && e.FromY == fy
                                && string.Equals(e.ToMap, toMap, StringComparison.OrdinalIgnoreCase)
                                && e.ToX == tx && e.ToY == ty)) return false;
            string line = $"{fromName}[{fromMap}]({fx},{fy})-{toName}[{toMap}]({tx},{ty})";
            Add(new[] { line });
            var parsed = Parse(line);
            if (parsed != null) _edges.Add(parsed);
            return true;
        }
    }

    /// <summary>good.ini:`原名,用途码,打包后名,包类型码,是否启用`。
    /// 用途码 0红药 1蓝药 2卷类 3黄粉 4灰粉 5护身符,正好对上 setup.ini [拣物] 的 扔红药/扔蓝药/扔卷类/扔护身符。</summary>
    public sealed class GoodTable : LineTable
    {
        public GoodTable(string path) : base(path) { }

        public int UseOf(string itemName)
        {
            // 同一件物品可能同时在 0(红药)和 1(蓝药)两行出现(实测 新人药),取第一条启用的。
            foreach (var r in Rows)
                if (r.Length >= 5 && r[0] == itemName && r[4] == "1" && int.TryParse(r[1], out int u)) return u;
            return -1;
        }

        public bool IsKind(string itemName, int use) => UseOf(itemName) == use;
        public IEnumerable<string> NamesOfKind(int use)
            => Rows.Where(r => r.Length >= 5 && r[4] == "1" && r[1] == use.ToString()).Select(r => r[0]);
    }

    /// <summary>skills.ini:`技能名,目标类型,已启用,需目标`。目标类型实测 1=自身/坐标,6=需选中对象。</summary>
    public sealed class SkillTable : LineTable
    {
        public SkillTable(string path) : base(path) { }
        public int TargetTypeOf(string skillName)
        {
            foreach (var r in Rows)
                if (r.Length >= 2 && r[0] == skillName && int.TryParse(r[1], out int t)) return t;
            return 0;
        }
    }

    /// <summary>stditem.ini:`物品名,` + 10 个数值(行尾多一个逗号),是服务端物品属性的本地镜像,
    /// 用来在买之前判断等级/职业需求(对应 setup.ini [系统设置]装备条件忽略)。</summary>
    public sealed class StdItemTable : LineTable
    {
        public StdItemTable(string path) : base(path) { }
        public int[]? Stats(string itemName)
        {
            foreach (var r in Rows)
            {
                if (r.Length < 2 || r[0] != itemName) continue;
                var nums = new List<int>();
                for (int i = 1; i < r.Length; i++)
                    nums.Add(int.TryParse(r[i], out int v) ? v : 0);
                return nums.ToArray();
            }
            return null;
        }
    }

    /// <summary>逗号分隔行式表的基类:整行按 ',' 切,原始字段全留着,写回时未改动的行一字不差。</summary>
    public abstract class LineTable
    {
        private readonly string _path;
        protected readonly List<string[]> Rows = new();
        private bool _dirty;

        protected LineTable(string path)
        {
            _path = path;
            if (!File.Exists(path)) return;
            foreach (var l in ReadGbkLines(path))
                if (l.Trim().Length > 0) Rows.Add(l.Split(','));
        }

        public int Count => Rows.Count;

        /// <summary>把拆开的字段拼回原始整行(Split(',') 的逆运算,无损)。</summary>
        protected static string Raw(string[] row) => string.Join(",", row);

        protected string[]? Find(string firstField)
            => Rows.FirstOrDefault(r => r.Length > 0 && string.Equals(r[0], firstField, StringComparison.Ordinal));

        protected void Add(string[] fields)
        {
            Rows.Add(fields);
            _dirty = true;
        }

        public void Save()
        {
            if (!_dirty) return;
            try
            {
                var sb = new StringBuilder();
                foreach (var r in Rows) sb.Append(string.Join(",", r)).Append("\r\n");
                File.WriteAllBytes(_path, GbkEncoding.Gbk.GetBytes(sb.ToString()));
                _dirty = false;
            }
            catch (Exception ex) { BotLog.Warn($"[char] 写入 {_path} 失败: {ex.Message}"); }
        }
    }

    /// <summary>GBK 读文本:天骥的文件 CRLF 和裸 LF 混用(挖矿.txt 里 461 个 CRLF + 188 个裸 LF),
    /// 所以先按 \n 切再剥行尾 \r,两种都能吃。</summary>
    internal static IReadOnlyList<string> ReadGbkLines(string path)
    {
        try
        {
            return SplitLines(ReadScriptText(path).Text);
        }
        catch (Exception ex)
        {
            BotLog.Warn($"[char] 读取 {path} 失败: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    internal static List<string> SplitLines(string text)
        => text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    /// <summary>Encoding.UTF8 的 GetBytes 会先吐三个 BOM 字节,写文件要用这个。</summary>
    private static readonly UTF8Encoding NoBomUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>天骥本体写出来的是 GBK,但用户拿现代编辑器改过的脚本会存成 UTF-8
    /// (参考语料 D:\Mir2tianjiV1209\DATA\2.txt、3.txt 就是 UTF-8)。
    /// 按 GBK 硬解 UTF-8 得到的是"那么→閭ｄ箞"这种看着仍像汉字的乱码:解析器照样切得出动词、
    /// 跑起来才整份都是未实现命令,所以这里先严格试 UTF-8(GBK 的中文几乎不可能同时是合法 UTF-8),
    /// 不行再回 GBK。编码要一起返回:存回去时必须按原编码写,否则等于偷偷把别人的文件转码了。</summary>
    internal static (string Text, Encoding Enc) ReadScriptText(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        try
        {
            return (NoBomUtf8.GetString(raw).TrimStart('\uFEFF'), Encoding.UTF8);
        }
        catch (DecoderFallbackException)
        {
            return (GbkEncoding.Gbk.GetString(raw), GbkEncoding.Gbk);
        }
    }
}
