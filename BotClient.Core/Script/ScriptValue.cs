namespace BotClient.Script;

/// <summary>脚本求值要问的东西全在这里:引擎只提供事实,表达式层负责把它们拼成天骥的取值函数。
/// 之所以做成接口而不是直接传 BotRuntime:解析/求值要能脱离连接离线跑(拿天骥原始脚本做回归),
/// 而真机那半边再拿一个包着 BotRuntime 的实现顶上。</summary>
public interface IValueSource
{
    /// <summary>[自己][HP|MP|MAXHP|MAXMP|等级|名称|性别|职业|负重|最大负重|背包空位|红名],
    /// who 是"自己/攻击目标/宝宝攻击目标"。</summary>
    string ActorField(string who, string field);

    /// <summary>[自己][部位][名称|持久|最大持久|使用次数]</summary>
    string EquipField(string slot, string metric);

    /// <summary>[部位][防御|攻击|魔法|道术|魔防|幸运|准确|魔法躲避]</summary>
    int EquipStat(string slot, string stat);

    /// <summary>[自己][状态][中毒|隐身|开盾] → "1"/"0"</summary>
    string StatusField(string status);

    /// <summary>[自己][方向][怪物数量|玩家数量|蜈蚣数量][N格]。countWord 是"数量"前那个词。</summary>
    int DirectionCount(string directions, string countWord, int range);

    /// <summary>[物品名][数量] / [红药|蓝药|金币|待售物品|待存物品|待售衣服…][数量]</summary>
    int ItemCount(string nameOrKind);

    /// <summary>[物品名][持久] / [物品名][使用次数](同种多件时天骥取持久最低/次数最高)</summary>
    int ItemMetric(string itemName, string metric);

    /// <summary>[地图编号,X,Y][距离]</summary>
    int Distance(string mapCode, int x, int y);

    /// <summary>[仓库][物品数量] / [仓库][XX数量]</summary>
    int StorageCount(string itemName);

    /// <summary>[存在选择内容][XX]:当前这一页 NPC 对话里有没有 XX 这一项(1/0)。</summary>
    int ChoiceExists(string text);

    /// <summary>[当前位置][玩家名]:视野里那个玩家在哪,返回 "地图编号,X,Y";没见过就返回空串。
    /// 天骥的被控端用它做"队友在哪我就走到哪",不用事先知道坐标。</summary>
    string PlayerSpot(string name);

    /// <summary>当前地图编号(天骥的 [当前地图名] 返回的是编号,[地图名] 才是中文名)</summary>
    string MapCode { get; }
    string MapName { get; }
    int X { get; }
    int Y { get; }

    /// <summary>[玩家数量][X,Y] / [怪物数量][X,Y]</summary>
    int CountAt(string countWord, int x, int y);
}

/// <summary>脚本里的一个值。天骥的变量既能装数字也能装文本(名字),比较时先试数字再退回文本,
/// 所以这里两个字段都留着,不做提前决定。</summary>
public readonly struct Sv
{
    public readonly double Num;
    public readonly string Text;

    private Sv(double num, string text) { Num = num; Text = text; }
    public static Sv Of(double n) => new(n, n.ToString("0.####"));
    public static Sv Of(string s) => new(ParseNum(s), s ?? string.Empty);

    private static double ParseNum(string s) => double.TryParse(s, out double v) ? v : 0;

    public bool IsNumeric => double.TryParse(Text, out _);
    public int Int => (int)Num;
    public bool Truthy => Text.Length > 0 && Num != 0 || (Text.Length > 0 && !IsNumeric);
    public override string ToString() => Text;

    /// <summary>两侧都能当数字就用数字比,否则按文本序比(天骥的 [当前地图名]=D611 走文本这条路)。</summary>
    public static int Compare(in Sv a, in Sv b)
    {
        if (a.IsNumeric && b.IsNumeric) return a.Num.CompareTo(b.Num);
        return string.CompareOrdinal(a.Text, b.Text);
    }
}

/// <summary>表达式:字面量 / 变量 / 取值函数链 / 四则。条件里和赋值右边都用它。</summary>
public abstract class Expr
{
    public abstract Sv Eval(ScriptVars vars, IValueSource src);

    public sealed class Lit : Expr
    {
        public readonly Sv Value;
        public Lit(Sv v) { Value = v; }
        public override Sv Eval(ScriptVars vars, IValueSource src) => Value;
    }

    /// <summary>带 $变量 插值的文本("布衣(男)"、"[$武器]")。天骥在引号和方括号里都会替换变量。</summary>
    public sealed class Text : Expr
    {
        public readonly string Raw;
        public Text(string raw) { Raw = raw; }
        public override Sv Eval(ScriptVars vars, IValueSource src) => Sv.Of(ScriptExpr.Interpolate(Raw, vars));
    }

    public sealed class Var : Expr
    {
        public readonly string Name;
        public Var(string name) { Name = name; }
        // $_当前X 这类环境变量名以 '_' 打头($ 已在切分时吃掉),路由在 ScriptVars.Get 里。
        public override Sv Eval(ScriptVars vars, IValueSource src) => vars.Get(Name);
    }

    /// <summary>一串 [方括号] 段:一段是函数名([当前时间]),多段是"对象+度量"([自己][HP]、[$武器][数量])。</summary>
    public sealed class Chain : Expr
    {
        public readonly List<string> Segments;
        public Chain(List<string> segs) { Segments = segs; }
        public override Sv Eval(ScriptVars vars, IValueSource src) => ScriptValue.Resolve(this, vars, src);
        public string Seg(int i, ScriptVars vars) => ScriptExpr.Interpolate(Segments[i], vars);
    }

    public sealed class Bin : Expr
    {
        public readonly char Op;
        public readonly Expr A, B;
        public Bin(char op, Expr a, Expr b) { Op = op; A = a; B = b; }
        public override Sv Eval(ScriptVars vars, IValueSource src)
        {
            double x = A.Eval(vars, src).Num, y = B.Eval(vars, src).Num;
            return Sv.Of(Op switch
            {
                '+' => x + y,
                '-' => x - y,
                '*' => x * y,
                '/' => y == 0 ? 0 : x / y,     // 天骥没有报错机制,除零给 0 比抛异常让脚本停掉好
                _ => 0,
            });
        }
    }
}

/// <summary>脚本变量表。天骥的变量不分作用域、全程共享,且 $_当前X 这类环境变量只读。
/// 环境变量和真变量都在 Get 里出口,这样 插值($_当前X 写在方括号参数里) 和 表达式求值 走同一条路。</summary>
public sealed class ScriptVars
{
    private readonly Dictionary<string, Sv> _v = new(StringComparer.Ordinal);

    /// <summary>引擎装进来即可;为 null 时(纯离线解析自检)环境变量一律给 0。</summary>
    public IValueSource? Src;

    public Sv Get(string name)
    {
        if (name.Length > 1 && name[0] == '_') return Env(name[1..]);
        return _v.TryGetValue(name, out Sv v) ? v : Sv.Of(0);
    }

    public void Set(string name, Sv value) => _v[name] = value;
    public void Clear() => _v.Clear();
    public IEnumerable<KeyValuePair<string, Sv>> All() => _v;

    /// <summary>$_当前X / $_当前Y / $_当前地图 / $_HP / $_MP —— 每次现取,不缓存。</summary>
    public Sv Env(string name)
    {
        IValueSource? src = Src;
        if (src == null) return Sv.Of(0);
        return name switch
        {
            "当前X" => Sv.Of(src.X),
            "当前Y" => Sv.Of(src.Y),
            "当前地图" => Sv.Of(src.MapCode),
            "HP" => Sv.Of(src.ActorField("自己", "HP")),
            "MP" => Sv.Of(src.ActorField("自己", "MP")),
            _ => Sv.Of(0),
        };
    }
}

/// <summary>一个"值函数"的名字加它的参数段怎么落到 IValueSource 上,全在这一个函数里,
/// 好对着天骥的函数清单逐条核对。</summary>
public static class ScriptValue
{
    public static Sv Resolve(Expr.Chain chain, ScriptVars vars, IValueSource src)
    {
        int n = chain.Segments.Count;
        string Seg(int i) => chain.Seg(i, vars);
        string first = Seg(0);

        if (n == 1)
        {
            switch (first)
            {
                case "当前时间": return Sv.Of(Environment.TickCount64);
                case "本机时间": return Sv.Of(DateTime.Now.ToString("HH:mm:ss"));
                case "本机日期": return Sv.Of(DateTime.Now.ToString("yyyy-M-d"));
                case "当前地图名": return Sv.Of(src.MapCode);
                case "地图名": return Sv.Of(src.MapName);
                case "背包空位": return Sv.Of(src.ActorField("自己", "背包空位"));
                case "平均经验": return Sv.Of(0);
            }
            if (first.Length > 0 && first[0] == '_') return vars.Get(first);   // [_当前X] 等价于 $_当前X
            // 光一段 [某某] 在天骥里就是"这东西的数量":怪物按只数、物品按件数、背包空位按格数。
            return Sv.Of(src.ItemCount(first));
        }

        string second = Seg(1);

        // [自己][HP] / [自己][衣服][持久] / [自己][状态][中毒] / [自己][左前,右前][蜈蚣数量][6格]
        if (first is "自己" or "我" or "攻击目标" or "宝宝攻击目标")
        {
            string who = first == "我" ? "自己" : first;
            if (n == 2) return Sv.Of(src.ActorField(who, second));
            string third = Seg(2);
            if (second == "状态") return Sv.Of(src.StatusField(third));
            if (IsDirectionList(second)) return Sv.Of(src.DirectionCount(second, TrimCountWord(third), RangeOf(chain, 3, vars)));
            return Sv.Of(src.EquipField(second, third));
        }

        // [左前,右前][怪物数量][6格] —— 省掉 [自己] 的写法
        if (IsDirectionList(first) && second.EndsWith("数量"))
            return Sv.Of(src.DirectionCount(first, TrimCountWord(second), RangeOf(chain, 2, vars)));

        // [玩家数量][X,Y] / [怪物数量][X,Y]
        if (n == 2 && first.EndsWith("数量") && second.Contains(','))
        {
            string[] xy = second.Split(',');
            if (xy.Length == 2) return Sv.Of(src.CountAt(TrimCountWord(first), Int(xy[0]), Int(xy[1])));
        }

        // [地图编号,X,Y][距离]
        if (n == 2 && second == "距离")
        {
            string[] xy = first.Split(',');
            if (xy.Length == 3) return Sv.Of(src.Distance(xy[0].Trim(), Int(xy[1]), Int(xy[2])));
            if (xy.Length == 2) return Sv.Of(src.Distance(src.MapCode, Int(xy[0]), Int(xy[1])));
            return Sv.Of(0);
        }

        // [仓库][物品数量] / [仓库][天尊戒指数量]
        if (first == "仓库") return Sv.Of(src.StorageCount(TrimCountWord(second)));

        // [存在选择内容][安全区域] —— 问的是"这一页 NPC 对话里有没有这一项",和物品无关
        if (first == "存在选择内容") return Sv.Of(src.ChoiceExists(second));

        // $当前位置[小明哥哥] / [当前位置][小明哥哥] → "地图编号,X,Y"(没见过就是空串)
        if (first == "当前位置") return Sv.Of(src.PlayerSpot(second));

        // [取左字符][ABC][2] / [取字符][ABC][2,3] / [随机数][1,9]
        if (first is "取左字符" or "取右字符" or "取字符" or "随机数" or "大写转换" or "小写转换")
            return BuiltinFunc(first, chain, vars);

        // [衣服][防御] —— 装备属性查询
        if (IsStatWord(second)) return Sv.Of(src.EquipStat(first, second));

        // [物品名][数量|持久|最大持久|使用次数],以及 [红药][数量] 这类类别聚合
        switch (second)
        {
            case "数量": return Sv.Of(src.ItemCount(first));
            case "持久": return Sv.Of(src.ItemMetric(first, "持久"));
            case "最大持久": return Sv.Of(src.ItemMetric(first, "最大持久"));
            case "使用次数": return Sv.Of(src.ItemMetric(first, "使用次数"));
            default:
                if (second.EndsWith("数量")) return Sv.Of(src.ItemCount(TrimCountWord(second)));
                // 剩下的是没认出来的组合:学天骥给 0,但留一句诊断,别静默吞掉。
                BotLog.Warn($"[脚本] 不认识的取值函数 [{first}][{second}]");
                return Sv.Of(0);
        }
    }

    private static Sv BuiltinFunc(string name, Expr.Chain chain, ScriptVars vars)
    {
        string Arg(int i) => i < chain.Segments.Count ? chain.Seg(i, vars) : string.Empty;
        switch (name)
        {
            case "取左字符": { string s = Arg(1); int k = Int(Arg(2)); return Sv.Of(k >= s.Length ? s : s[..k]); }
            case "取右字符": { string s = Arg(1); int k = Int(Arg(2)); return Sv.Of(k <= 0 ? s : s[Math.Max(0, s.Length - k)..]); }
            case "取字符":
                {
                    string s = Arg(1);
                    string[] ab = Arg(2).Split(',');
                    int start = ab.Length > 0 ? Int(ab[0]) : 1;
                    int len = ab.Length > 1 ? Int(ab[1]) : s.Length;
                    int from = Math.Clamp(start - 1, 0, s.Length);
                    return Sv.Of(s[from..Math.Min(s.Length, from + Math.Max(0, len))]);
                }
            case "大写转换": return Sv.Of(Arg(1).ToUpperInvariant());
            case "小写转换": return Sv.Of(Arg(1).ToLowerInvariant());
            case "随机数":
                {
                    string[] ab = Arg(1).Split(',');
                    int lo = ab.Length > 0 ? Int(ab[0]) : 0, hi = ab.Length > 1 ? Int(ab[1]) : lo;
                    return Sv.Of(hi <= lo ? lo : Random.Shared.Next(lo, hi + 1));
                }
            default: return Sv.Of(0);
        }
    }

    /// <summary>[..][怪物数量][6格] → 6;没写就按天骥默认的 13 格。</summary>
    private static int RangeOf(Expr.Chain chain, int idx, ScriptVars vars)
    {
        if (idx >= chain.Segments.Count) return 13;
        string s = chain.Seg(idx, vars).TrimEnd('格');
        return int.TryParse(s, out int v) ? v : 13;
    }

    private static bool IsDirectionWord(string s)
        => s is "所有方向" or "前" or "后" or "左" or "右" or "左前" or "右前" or "左后" or "右后";

    /// <summary>方向段可能是"左前,右前,左后,右后"这种逗号串:每一段都得是方向词才算。</summary>
    private static bool IsDirectionList(string s)
    {
        if (s.Length == 0) return false;
        foreach (string part in s.Split(',', '，'))
            if (!IsDirectionWord(part.Trim())) return false;
        return true;
    }

    private static bool IsStatWord(string s)
        => s is "防御" or "攻击" or "魔法" or "道术" or "魔防" or "幸运" or "准确" or "魔法躲避";

    private static string TrimCountWord(string s) => s.EndsWith("数量") ? s[..^2] : s;

    private static int Int(string s) => int.TryParse(s.Trim(), out int v) ? v : 0;
}
