namespace BotClient.Script;

/// <summary>命令行的切分结果:动词 + 若干 [方括号] 段 + 中间/末尾的裸文本。
/// 天骥的命令形态是 "动词[参数]裸文本[参数]裸文本",参数序号就够用:
/// 购买[X]装备到[Y] → Brackets[0]=物品、Brackets[1]=部位。</summary>
public sealed class ScriptCommand
{
    public string Verb = string.Empty;
    public readonly List<string> Brackets = new();
    public readonly List<string> Literals = new();

    /// <summary>第一个 &lt;尖括号&gt; 里的内容(调用/跳转到/等待出发的目标名)。</summary>
    public string Angle = string.Empty;
    public string Tail = string.Empty;
    public int Line;
    public string Raw = string.Empty;

    public string Bracket(int i) => i >= 0 && i < Brackets.Count ? Brackets[i] : string.Empty;
    public int ArgCount => Brackets.Count;
}

public enum NodeKind { Label, Command, Assign, If }

/// <summary>一条语句。If 节点自带 Then/Else 两条命令表(天骥没有嵌套如果,所以不需要递归块)。</summary>
public sealed class ScriptNode
{
    public NodeKind Kind;
    public int Line;
    public string Raw = string.Empty;

    public string Name = string.Empty;      // Label 名 / Assign 变量名 / Command 动词
    public Expr? Value;                     // Assign 的右值
    public ScriptCommand? Cmd;              // Command
    public Condition? Cond;                 // If
    public readonly List<ScriptNode> Then = new();
    public readonly List<ScriptNode> Else = new();
}

/// <summary>条件:若干子句用 && 连接;子句可以是 "表达式 比较符 表达式",也可以是光秃秃一个表达式(非 0 即真)。</summary>
public sealed partial class Condition
{
    public readonly List<Clause> Clauses = new();

    public bool Eval(ScriptVars vars, IValueSource src)
    {
        foreach (var c in Clauses)
        {
            Sv a = c.Left.Eval(vars, src);
            bool ok;
            if (c.Op.Length == 0) ok = a.Truthy;
            else
            {
                Sv b = c.Right!.Eval(vars, src);
                ok = c.Op switch
                {
                    "=" => Sv.Compare(a, b) == 0 || a.Text == b.Text,
                    "<>" => !(Sv.Compare(a, b) == 0 || a.Text == b.Text),
                    ">" => Sv.Compare(a, b) > 0,
                    "<" => Sv.Compare(a, b) < 0,
                    ">=" => Sv.Compare(a, b) >= 0,
                    "<=" => Sv.Compare(a, b) <= 0,
                    _ => false,
                };
            }
            if (!ok) return false;
        }
        return true;
    }

    public sealed class Clause
    {
        public Expr Left = null!;
        public string Op = string.Empty;
        public Expr? Right;
    }
}

/// <summary>一份脚本:一条扁平语句表 + 标签索引 + 全局/挂机两段的位置。
/// 全局段是独立轮询循环,挂机段是主流程,两边共用变量和标签(真脚本里全局段确实会 跳转到 挂机段的标签)。</summary>
public sealed class ScriptProgram
{
    public readonly List<ScriptNode> Body = new();
    public readonly Dictionary<string, int> Labels = new(StringComparer.Ordinal);
    public readonly List<string> Diagnostics = new();

    /// <summary>触发器块(天骥的跨进程协作:收到匹配的 组队通信 才跑的那段子程序)。
    /// 单独存着而不是进 Body:它们是"以后可能被叫到"的动作,混进主流程会让 bot 一开机
    /// 把所有响应动作(走到某处、施法)全做一遍。</summary>
    public readonly List<TriggerDef> Triggers = new();

    public int GlobalStart = -1, GlobalEnd = -1;
    public int EntryPoint;
    public string SourceName = string.Empty;

    public bool HasGlobalSection => GlobalStart >= 0;
    public int IndexOf(string label) => Labels.TryGetValue(label, out int i) ? i : -1;
}

/// <summary>触发器:加血,%$X%,%$Y% …… 触发器结束</summary>
public sealed class TriggerDef
{
    public string Name = string.Empty;
    /// <summary>%$X% 里的 $X:来消息时按位置塞进这些变量。</summary>
    public readonly List<string> Params = new();
    public readonly List<ScriptNode> Body = new();
    public int Line;
}

/// <summary>天骥脚本前端。语法完全按 D:\Mir2tianjiV1209 真角色的 脚本.txt/挖矿.txt 反推,
/// 规则只有五条:整行 &lt;标签&gt;、如果 后连续的 那么/否则 归属它、* 开头是注释、$变量 在任何参数里插值、
/// 认不出来的东西只记诊断不中断。</summary>
public static class ScriptParser
{
    private const string GlobalBegin = "全局脚本开始", GlobalEndTag = "全局脚本结束";
    private const string MainBegin = "挂机脚本开始", MainEnd = "挂机脚本结束";
    private const string TriggerBegin = "触发器:", TriggerBeginFull = "触发器：", TriggerEnd = "触发器结束";

    /// <summary>吃掉一整块 触发器:名字[,参数] …… 触发器结束:块内语句交给本函数自己递归解析,
    /// 只登记不挂进主流程。ref lineNo 走到 触发器结束 那一行,外层循环 ++ 之后正好接上。</summary>
    private static void ReadTrigger(ScriptProgram p, string[] src, ref int lineNo, string head)
    {
        var t = new TriggerDef { Line = lineNo };
        // 头一行 "加血,%$X%,%$Y%":第一段是匹配名,后面 %$变量% 是来消息要塞的参数
        foreach (string piece in head.Split(','))
        {
            string w = piece.Trim();
            if (w.Length == 0) continue;
            if (w.Length > 2 && w[0] == '%' && w[^1] == '%')
            {
                string v = w[1..^1].Trim();
                t.Params.Add(v.Length > 0 && v[0] == '$' ? v[1..] : v);
            }
            else if (t.Name.Length == 0) t.Name = w;
            else p.Diagnostics.Add($"第{lineNo}行 触发器:{head} 的第二个参数 {w} 不是 %$变量% 形态,忽略");
        }

        int start = lineNo + 1;                          // 块体从下一行开始
        int end = -1;
        for (int i = start; i <= src.Length; i++)
            if (src[i - 1].Trim() == TriggerEnd) { end = i; break; }

        if (end < 0)
        {
            p.Diagnostics.Add($"第{lineNo}行 触发器:{t.Name} 后面没有 {TriggerEnd},整块按到文件末尾收");
            end = src.Length + 1;
        }
        var body = new string[end - start];
        Array.Copy(src, start - 1, body, 0, body.Length);
        var sub = Parse(body, $"触发器:{t.Name}");
        foreach (string d in sub.Diagnostics) p.Diagnostics.Add($"第{lineNo}行 触发器:{t.Name} {d}");
        t.Body.AddRange(sub.Body);
        p.Triggers.Add(t);
        lineNo = end;
    }

    public static ScriptProgram Parse(IEnumerable<string> lines, string sourceName = "脚本")
    {
        var p = new ScriptProgram { SourceName = sourceName };
        ScriptNode? openIf = null;
        string[] src = lines.ToArray();

        for (int lineNo = 1; lineNo <= src.Length; lineNo++)
        {
            string s = src[lineNo - 1].Trim();
            if (s.Length == 0 || s[0] == '*' || s[0] == ';') continue;

            // ---- 触发器块:整块从主流程里摘出去(见 ScriptProgram.Triggers) ----
            if (s.StartsWith(TriggerBegin, StringComparison.Ordinal) || s.StartsWith(TriggerBeginFull, StringComparison.Ordinal))
            {
                openIf = null;
                ReadTrigger(p, src, ref lineNo, s[TriggerBegin.Length..].Trim());
                continue;
            }
            if (s == TriggerEnd)
            {
                openIf = null;
                p.Diagnostics.Add($"第{lineNo}行 {TriggerEnd} 前面没有 触发器:,忽略");
                continue;
            }

            // ---- 整行标签 / 段标记 ----
            if (s.Length > 2 && s[0] == '<' && s[^1] == '>' && s.IndexOf('<', 1) < 0)
            {
                string tag = s[1..^1].Trim();
                switch (tag)
                {
                    case GlobalBegin:
                        openIf = null;
                        p.GlobalStart = p.Body.Count;
                        continue;
                    case GlobalEndTag:
                        openIf = null;
                        p.GlobalEnd = p.Body.Count;      // 不含端点:全局段就是 [GlobalStart, GlobalEnd) 这一段
                        continue;
                    case MainBegin:
                        openIf = null;
                        p.EntryPoint = p.Body.Count;
                        continue;
                    case MainEnd:
                        openIf = null;
                        continue;
                }
                openIf = null;
                Add(p, new ScriptNode { Kind = NodeKind.Label, Name = tag, Line = lineNo, Raw = s });
                continue;
            }

            // ---- 如果:开一个新的条件块,后面连续的 那么/否则 都归它 ----
            if (s.StartsWith("如果", StringComparison.Ordinal))
            {
                openIf = new ScriptNode { Kind = NodeKind.If, Line = lineNo, Raw = s };
                string condText = s[2..].Trim();
                if (Condition.TryParse(condText, out Condition? cond, out string? err)) openIf.Cond = cond;
                else
                {
                    // 条件认不出来时按"假"处理并留话:宁可少跑几条命令,也别把 gated 的买卖全做一遍。
                    p.Diagnostics.Add($"第{lineNo}行 条件解析失败({err}),整块按不成立跳过: {condText}");
                    openIf.Cond = Condition.AlwaysFalse;
                }
                Add(p, openIf);
                continue;
            }

            bool isThen = s.StartsWith("那么", StringComparison.Ordinal);
            bool isElse = s.StartsWith("否则", StringComparison.Ordinal);
            string body = isThen || isElse ? s[2..].Trim() : s;
            if (body.Length == 0) continue;

            ScriptNode node = BuildStatement(body, lineNo, s, p);
            if (isThen && openIf != null) { openIf.Then.Add(node); continue; }
            if (isElse && openIf != null) { openIf.Else.Add(node); continue; }
            if (isThen || isElse)
            {
                // 真脚本里有这种写法:上一句是裸命令(把如果块关掉了)后面还接着写那么。
                // 天骥照样执行,所以这里当成无条件语句,只在诊断里留一句。
                p.Diagnostics.Add($"第{lineNo}行 {(isThen ? "那么" : "否则")}没有配对的如果,按普通命令执行: {s}");
                Add(p, node);
                continue;
            }

            // 裸命令/赋值/标签都会终结上一个如果块 —— 天骥没有嵌套如果,所以只需要记住"最近一个"。
            openIf = null;
            Add(p, node);
        }

        if (p.GlobalStart >= 0 && p.GlobalEnd < p.GlobalStart) p.GlobalEnd = p.Body.Count;   // 忘了写 <全局脚本结束>
        if (p.Body.Count == 0) p.Diagnostics.Add("脚本为空");
        return p;
    }

    private static void Add(ScriptProgram p, ScriptNode n)
    {
        if (n.Kind == NodeKind.Label)
        {
            // 同名标签后写的会覆盖前面的(Boss 刷新点和总控偶尔重名),记一句免得排错时找不到人。
            if (p.Labels.ContainsKey(n.Name)) p.Diagnostics.Add($"第{n.Line}行 标签 <{n.Name}> 重复,后定义的生效");
            p.Labels[n.Name] = p.Body.Count;
        }
        p.Body.Add(n);
    }

    private static ScriptNode BuildStatement(string body, int lineNo, string raw, ScriptProgram p)
    {
        int eq = ScriptExpr.IndexOfTopLevelAssign(body);
        if (body.Length > 1 && body[0] == '$' && eq > 0)
        {
            string varName = body[1..eq].Trim();
            string rhs = body[(eq + 1)..].Trim();
            if (!ScriptExpr.TryParse(rhs, out Expr? e, out string? err))
            {
                p.Diagnostics.Add($"第{lineNo}行 赋值右值解析失败({err}): {rhs}");
                e = new Expr.Lit(Sv.Of(0));
            }
            return new ScriptNode { Kind = NodeKind.Assign, Name = varName, Value = e, Line = lineNo, Raw = raw };
        }
        return new ScriptNode { Kind = NodeKind.Command, Cmd = ParseCommand(body, lineNo, raw), Line = lineNo, Raw = raw };
    }

    public static ScriptCommand ParseCommand(string s, int lineNo, string raw)
    {
        var c = new ScriptCommand { Line = lineNo, Raw = raw.Length > 120 ? raw[..120] : raw };
        // 尖括号参数(调用/跳转到/开启NPC对话校验[..]<跳转地址>)先整段摘出来,后面的切分只管方括号。
        int lt = s.IndexOf('<');
        if (lt >= 0)
        {
            int gt = s.IndexOf('>', lt + 1);
            if (gt > lt)
            {
                c.Angle = s[(lt + 1)..gt].Trim();
                s = s[..lt] + s[(gt + 1)..];
            }
        }
        var lit = new System.Text.StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '[')
            {
                int end = ScriptExpr.EndOfBracket(s, i);
                FlushLiteral(c, lit);
                if (end < 0) break;                      // 方括号没闭合:后半截丢掉,不让整行解析炸掉
                c.Brackets.Add(s[(i + 1)..end]);
                i = end + 1;
                continue;
            }
            lit.Append(s[i]);
            i++;
        }
        FlushLiteral(c, lit);
        if (c.Literals.Count > 0) c.Tail = c.Literals[^1].Trim();
        return c;
    }

    private static void FlushLiteral(ScriptCommand c, System.Text.StringBuilder lit)
    {
        string t = lit.ToString().Trim();
        lit.Clear();
        if (t.Length == 0) return;
        c.Literals.Add(t);
        if (c.Verb.Length == 0) c.Verb = t;
    }
}

/// <summary>条件文本的解析入口(和表达式共用扫描器,不然两边对 "[]"、引号 的处理会慢慢跑偏)。</summary>
public sealed partial class Condition
{
    public static readonly Condition AlwaysFalse = new() { Clauses = { new Clause { Left = new Expr.Lit(Sv.Of(0)) } } };

    public static bool TryParse(string text, out Condition? cond, out string? error)
    {
        cond = null; error = null;
        var c = new Condition();
        foreach (string piece in ScriptExpr.SplitTopLevel(text, "&&"))
        {
            string t = piece.Trim();
            if (t.Length == 0) continue;
            int pos = ScriptExpr.IndexOfTopLevelComparator(t, out string op);
            var clause = new Clause();
            if (pos < 0)
            {
                if (!ScriptExpr.TryParse(t, out Expr? l, out error)) return false;
                clause.Left = l!;
            }
            else
            {
                if (!ScriptExpr.TryParse(t[..pos], out Expr? l, out error)) return false;
                if (!ScriptExpr.TryParse(t[(pos + op.Length)..], out Expr? r, out error)) return false;
                clause.Left = l!; clause.Op = op; clause.Right = r!;
            }
            c.Clauses.Add(clause);
        }
        if (c.Clauses.Count == 0) { error = "空条件"; return false; }
        cond = c;
        return true;
    }
}
