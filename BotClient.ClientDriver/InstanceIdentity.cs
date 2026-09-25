using System.Text;
using System.Text.RegularExpressions;

namespace BotClient.ClientDriver;

/// <summary>
/// 多开标识：**服务器名-区名-角色名**。
///
/// 用途只有一个：多开时把"这个进程在跑哪个角色"写清楚 ——
///   ① 每行日志前面带 [服务器名-区名-角色名] 前缀（多开的几份日志混在一个终端里也分得清）；
///   ② 控制台窗口标题用它，任务栏/窗口列表一眼能区分是哪个实例；
///   ③ 自检报告里带一行，出问题时不用猜是哪一份。
///
/// 它**不参与任何协议逻辑**：不改封包、不做路由、不碰账号密码，纯粹是给人看的标签。
///
/// 取值优先级（前者覆盖后者）：
///   命令行（--server-name / --zone / --character）
///     → clientdriver.json 的 identity 段（上次记住的，换服/重启继续用）
///     → 客户端窗口标题解析（尽力而为，解析不准就用命令行政正）
///     → 运行时从状态通道补全角色名（服务端广播的角色信息）
/// 任何一项都允许缺失（显示 "?"），缺项不影响挂机。
/// </summary>
public sealed class InstanceIdentity
{
    /// <summary>服务器名（如 "s1"、"二区-雷霆"）。可空。</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>区名（如 "电信一区"）。可空。</summary>
    public string ZoneName { get; set; } = string.Empty;

    /// <summary>角色名。可空 —— 复用模式下通常是运行时才补上。</summary>
    public string CharacterName { get; set; } = string.Empty;

    public bool IsEmpty => !Has(ServerName) && !Has(ZoneName) && !Has(CharacterName);

    public bool IsComplete => Has(ServerName) && Has(ZoneName) && Has(CharacterName);

    public bool HasCharacter => Has(CharacterName);

    /// <summary>形如 "s1-电信一区-测试小号"；缺失项显示 "?"。</summary>
    public string Describe() => $"{Show(ServerName)}-{Show(ZoneName)}-{Show(CharacterName)}";

    /// <summary>日志/窗口标题前缀，形如 "[s1-电信一区-测试小号]"；三项全空时返回空串（不污染旧日志格式）。</summary>
    public string Prefix => IsEmpty ? string.Empty : "[" + Describe() + "]";

    public InstanceIdentity Clone() => new()
    {
        ServerName = ServerName,
        ZoneName = ZoneName,
        CharacterName = CharacterName,
    };

    /// <summary>只填空缺项，已有值一律不覆盖。返回是否有变化。</summary>
    public bool FillFrom(InstanceIdentity? other)
    {
        if (other == null)
        {
            return false;
        }

        bool changed = false;
        if (!Has(ServerName) && Has(other.ServerName)) { ServerName = other.ServerName.Trim(); changed = true; }
        if (!Has(ZoneName) && Has(other.ZoneName)) { ZoneName = other.ZoneName.Trim(); changed = true; }
        if (!Has(CharacterName) && Has(other.CharacterName)) { CharacterName = other.CharacterName.Trim(); changed = true; }
        return changed;
    }

    /// <summary>把 <paramref name="other"/> 里**确实有值**的项列出来（用于"从窗口标题补全了哪些项"的日志）。</summary>
    public string DescribeAvailable(InstanceIdentity other)
    {
        var sb = new StringBuilder();
        if (!Has(ServerName) && Has(other.ServerName)) sb.Append($"服务器名={other.ServerName} ");
        if (!Has(ZoneName) && Has(other.ZoneName)) sb.Append($"区名={other.ZoneName} ");
        if (!Has(CharacterName) && Has(other.CharacterName)) sb.Append($"角色名={other.CharacterName}");
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 从客户端窗口标题尽力解析（多开时每个客户端标题一般带服务器/区/角色信息）。
    /// 解析规则刻意保守：宁可少填（留 "?"）也不乱填 —— 填错会把人误导到别的实例上。
    /// </summary>
    public static InstanceIdentity FromWindowTitle(string? title)
    {
        var id = new InstanceIdentity();
        if (string.IsNullOrWhiteSpace(title))
        {
            return id;
        }

        string work = title.Trim();

        // 角色名：先看方括号/圆括号里的短词（很多客户端形如 "[小号] 服务器-区"）
        var bracket = Regex.Match(work, "[\\[【（(]([^\\[\\]【】（）()]{1,16})[\\]】）)]");
        if (bracket.Success)
        {
            string cand = Clean(bracket.Groups[1].Value);
            if (cand.Length > 0 && !IsNoise(cand))
            {
                id.CharacterName = cand;
            }

            work = work.Replace(bracket.Value, " ");
        }

        var parts = work
            .Split(new[] { " - ", "-", "—", "|", "_", "·", " ", "\t" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Clean)
            .Where(s => s.Length is > 0 and <= 24 && !IsNoise(s))
            .Where(s => !Regex.IsMatch(s, "^[\\d\\.vV]+$"))          // 纯数字/版本号段（1.80、v3 之类）
            .ToList();

        if (parts.Count >= 2)
        {
            id.ServerName = parts[0];
            id.ZoneName = parts[1];
            if (!Has(id.CharacterName) && parts.Count >= 3)
            {
                id.CharacterName = parts[2];
            }
        }
        else if (parts.Count == 1)
        {
            if (parts[0].Contains('区') || parts[0].Contains('服'))
            {
                id.ZoneName = parts[0];
            }
            else
            {
                id.ServerName = parts[0];
            }
        }

        return id;
    }

    private static string Clean(string s) => s.Trim().Trim('[', ']', '【', '】', '(', ')', '（', '）').Trim();

    private static bool IsNoise(string s)
    {
        string t = s.ToLowerInvariant();
        foreach (string w in NoiseWords)
        {
            if (t.Contains(w))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] NoiseWords =
    {
        "传奇", "客户端", "登录器", "官方", "游戏", "窗口",
        "client", "legend", "mir", "launcher", "window", "game", "exe",
    };

    private static bool Has(string? s) => !string.IsNullOrWhiteSpace(s);

    private static string Show(string? s) => Has(s) ? s!.Trim() : "?";
}
