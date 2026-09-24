namespace BotClient.Script;

/// <summary>天骥脚本的公共扫描器:方括号组、引号串、顶层分隔符、表达式。
/// 解析器、条件、命令行切分都从这里走,免得三处对 "[]" 和引号的理解各写一遍再慢慢跑偏。</summary>
public static class ScriptExpr
{
    /// <summary>把参数文本里的 $变量 换成当前值。天骥在引号串、方括号参数、系统显示 里都做替换。</summary>
    public static string Interpolate(string text, ScriptVars vars)
    {
        if (text.Length == 0 || text.IndexOf('$') < 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length;)
        {
            if (text[i] != '$') { sb.Append(text[i]); i++; continue; }
            int j = i + 1;
            while (j < text.Length && IsNameChar(text[j])) j++;
            if (j == i + 1) { sb.Append('$'); i++; continue; }
            sb.Append(vars.Get(text[(i + 1)..j]).Text);
            i = j;
        }
        return sb.ToString();
    }

    public static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c > 0x7F;

    /// <summary>s[i] 必须是 '[';返回配对的 ']' 下标,找不到返回 -1。
    /// 嵌套要管:实测 组队通信[小小][$请求加血[]] 里就有套了一层的中括号。</summary>
    public static int EndOfBracket(string s, int i)
    {
        int depth = 0;
        for (int k = i; k < s.Length; k++)
        {
            if (s[k] == '[') depth++;
            else if (s[k] == ']') { depth--; if (depth == 0) return k; }
        }
        return -1;
    }

    /// <summary>按分隔符切,但方括号里和引号里的分隔符不算(条件里的 [黄色药粉(大量)] 这类)。</summary>
    public static List<string> SplitTopLevel(string s, string sep)
    {
        var outList = new List<string>();
        int depth = 0; bool inQuote = false;
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') inQuote = !inQuote;
            else if (!inQuote && c == '[') depth++;
            else if (!inQuote && c == ']' && depth > 0) depth--;
            if (depth == 0 && !inQuote && i + sep.Length <= s.Length && string.CompareOrdinal(s, i, sep, 0, sep.Length) == 0)
            {
                outList.Add(s[start..i]);
                i += sep.Length - 1;
                start = i + 1;
            }
        }
        outList.Add(s[start..]);
        return outList;
    }

    /// <summary>找顶层比较符(方括号/引号里的不算)。返回下标,op 带出符号;没有则 -1。
    /// 顺序有讲究:&lt;&gt; 必须比 &lt; 先试,否则 &lt;&gt; 会被切成 "&lt;" + "&gt;"。</summary>
    public static int IndexOfTopLevelComparator(string s, out string op)
    {
        op = string.Empty;
        int depth = 0; bool inQuote = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') inQuote = !inQuote;
            else if (!inQuote && c == '[') depth++;
            else if (!inQuote && c == ']' && depth > 0) depth--;
            if (inQuote || depth > 0) continue;
            if (i + 1 < s.Length)
            {
                string two = s.Substring(i, 2);
                if (two is "<>" or ">=" or "<=") { op = two; return i; }
            }
            if (c is '<' or '>' or '=' || c == '＝') { op = c == '＝' ? "=" : c.ToString(); return i; }
        }
        return -1;
    }

    /// <summary>赋值行的 '=':不能在方括号/引号里,也不能是 &lt;&gt; / &gt;= / &lt;= 的一部分。</summary>
    public static int IndexOfTopLevelAssign(string s)
    {
        int depth = 0; bool inQuote = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') inQuote = !inQuote;
            else if (!inQuote && c == '[') depth++;
            else if (!inQuote && c == ']' && depth > 0) depth--;
            if (inQuote || depth > 0) continue;
            if (c != '=' && c != '＝') continue;
            if (i > 0 && (s[i - 1] == '<' || s[i - 1] == '>' || s[i - 1] == '!')) continue;
            if (i + 1 < s.Length && s[i + 1] == '=') i++;
            return i;
        }
        return -1;
    }

    /// <summary>解析一个表达式(带 + - * / 与比较符右侧的值)。天骥条件里实测出现过 [当前时间] -$脱困尝试时间&gt;10000。</summary>
    public static bool TryParse(string s, out Expr? expr, out string? error)
    {
        expr = null; error = null;
        int pos = 0;
        s = s.Trim();
        if (s.Length == 0) { error = "空表达式"; return false; }
        try
        {
            expr = ParseAdditive(s, ref pos);
            string rest = s[pos..].Trim();
            if (rest.Length > 0) { error = $"多余内容 {rest}"; return false; }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static Expr ParseAdditive(string s, ref int pos)
    {
        Expr a = ParseMul(s, ref pos);
        while (true)
        {
            SkipSpaces(s, ref pos);
            if (pos >= s.Length) return a;
            char c = s[pos];
            if (c != '+' && c != '-' && c != '*' && c != '/') return a;
            pos++;
            Expr b = c is '*' or '/' ? ParseMul(s, ref pos) : ParseAdditiveTail(s, ref pos);
            a = new Expr.Bin(c, a, b);
        }
    }

    /// <summary>'-' 既可能是减号也可能是右操作数的开头,所以加减法的右侧只解析一层乘法,不再递归吃减号。</summary>
    private static Expr ParseAdditiveTail(string s, ref int pos) => ParseMul(s, ref pos);

    private static Expr ParseMul(string s, ref int pos)
    {
        Expr a = ParsePrimary(s, ref pos);
        while (true)
        {
            SkipSpaces(s, ref pos);
            if (pos >= s.Length) return a;
            char c = s[pos];
            if (c != '*' && c != '/') return a;
            pos++;
            a = new Expr.Bin(c, a, ParsePrimary(s, ref pos));
        }
    }

    private static void SkipSpaces(string s, ref int pos)
    {
        while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t')) pos++;
    }

    private static Expr ParsePrimary(string s, ref int pos)
    {
        SkipSpaces(s, ref pos);
        if (pos >= s.Length) throw new FormatException("缺少操作数");
        char c = s[pos];

        if (c == '"')
        {
            int q = s.IndexOf('"', pos + 1);
            if (q < 0) throw new FormatException("引号没闭合");
            string t = s[(pos + 1)..q];
            pos = q + 1;
            return new Expr.Text(t);
        }

        if (c == '$')
        {
            int j = pos + 1;
            while (j < s.Length && IsNameChar(s[j])) j++;
            string name = s[(pos + 1)..j];
            pos = j;
            // $当前位置[小明哥哥] 是天骥"带参数的系统变量"写法,和 [当前位置][小明哥哥] 同一个东西
            if (pos < s.Length && s[pos] == '[')
            {
                var segs = new List<string> { name };
                while (pos < s.Length && s[pos] == '[')
                {
                    int end = EndOfBracket(s, pos);
                    if (end < 0) throw new FormatException("方括号没闭合");
                    segs.Add(s[(pos + 1)..end]);
                    pos = end + 1;
                }
                return new Expr.Chain(segs);
            }
            return new Expr.Var(name);
        }

        if (c == '[')
        {
            var segs = new List<string>();
            while (pos < s.Length && s[pos] == '[')
            {
                int end = EndOfBracket(s, pos);
                if (end < 0) throw new FormatException("方括号没闭合");
                segs.Add(s[(pos + 1)..end]);
                pos = end + 1;
            }
            return new Expr.Chain(segs);
        }

        int start = pos;
        while (pos < s.Length && !char.IsWhiteSpace(s[pos])
               && s[pos] is not ('&' or '<' or '>' or '=' or '+' or '-' or '*' or '/')) pos++;
        if (pos == start) throw new FormatException($"无法识别的字符 {c}");
        return new Expr.Lit(Sv.Of(s[start..pos]));
    }
}
