namespace BotClient.Protocol;

/// <summary>NPC 对话菜单里的一项:显示文字 + 真正要回传给服务端的命令(@开头)。</summary>
public readonly record struct NpcOption(string Display, string Command);

/// <summary>把 NPC 对话正文解成可点击选项。原版客户端只把 '&lt;'..'&gt;' / '{'..'}' 包起来的内容当选项,
/// 组内用 '/' 分段:以 '@' 开头的那段是命令,其余是显示文字;组外的正文不是选项。
/// 本服真机抓到的 6 个村口 NPC(2026-09-23)一律写成 "\&lt;买/@buy&gt; 武器" 这种"显示与命令分两段"的形式
/// (选项后面那段附属文字并入显示),老引擎还有 "&lt;我要买东西=@buy_ok&gt;"(同段用 '=' 分),两种都认。
/// 真机基准锚在 LoginProbe 的 --npc-selftest。</summary>
public static class NpcDialogOptions
{
    public static List<NpcOption> Parse(string? text)
    {
        var list = new List<NpcOption>();
        if (string.IsNullOrEmpty(text)) return list;
        for (int i = 0; i < text.Length; i++)
        {
            char close = text[i] switch { '<' => '>', '{' => '}', _ => '\0' };
            if (close == '\0') continue;
            int end = text.IndexOf(close, i + 1);
            if (end < 0) continue;          // 开括号没闭合(长对话被截断时常见):跳过它,后面的选项还要

            var group = new List<NpcOption>();
            AddGroup(group, text[(i + 1)..end]);
            if (group.Count > 0)
            {
                // 原版菜单一行以 '\' 结尾,所以 '>' 之后紧跟到 '\' 之前的正文属于这条选项("<卖/@sell> 武器");
                // 若后面直接是下一个开括号(传送员用 '┃' 串在一行),那段就是装饰文字,并进显示会变成"城市传送 ┃"。
                int after = end + 1;
                int stop = SuffixEnd(text, after);
                bool ownsFollowing = stop >= text.Length || text[stop] is '\\' or '\n' or '\r';
                if (ownsFollowing)
                {
                    string trimmed = text[after..stop].Trim('/', ' ', '\t');
                    int last = group.Count - 1;
                    if (trimmed.Length > 0 && group[last].Display.Length > 0)
                        group[last] = group[last] with { Display = group[last].Display + " " + trimmed };
                }
                list.AddRange(group);
            }
            i = end;
        }
        return list;
    }

    /// <summary>'&gt;' 之后附属文字的终点:遇到下一个开括号或行分隔符就停。</summary>
    static int SuffixEnd(string text, int from)
    {
        int i = from;
        while (i < text.Length && text[i] != '<' && text[i] != '{' && text[i] != '\\'
               && text[i] != '\n' && text[i] != '\r') i++;
        return i;
    }

    /// <summary>把一个选项组("买/@buy" 或 "我要买东西=@buy_ok")解成一条 显示→命令。</summary>
    static void AddGroup(List<NpcOption> list, string group)
    {
        string display = string.Empty, command = string.Empty;
        foreach (string raw in group.Split('/', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0) continue;
            if (raw.StartsWith("@@", StringComparison.Ordinal)) continue;   // @@N 是图片标记
            int eq = raw.IndexOf('=');
            if (eq >= 0)
            {
                string left = raw[..eq].Trim(), right = raw[(eq + 1)..].Trim();
                if (right.StartsWith('@')) { list.Add(new NpcOption(left.Length > 0 ? left : right, right)); continue; }
                if (left.StartsWith('@')) { list.Add(new NpcOption(right.Length > 0 ? right : left, left)); continue; }
            }
            if (raw.StartsWith('@')) { command = raw; continue; }
            display = display.Length == 0 ? raw : display + " " + raw;
        }
        if (command.Length > 0) list.Add(new NpcOption(display.Length > 0 ? display : command, command));
    }
}
