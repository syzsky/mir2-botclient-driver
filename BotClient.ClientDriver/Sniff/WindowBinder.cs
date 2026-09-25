using System.Runtime.InteropServices;
using System.Text;

namespace BotClient.ClientDriver.Sniff;

/// <summary>
/// 窗口句柄绑定（只读）：找出某个进程的"像游戏本体"的顶层窗口，并校验一个已绑定的 HWND 是否仍然有效。
///
/// 为什么需要这一层：
///   ① <c>Process.MainWindowHandle</c> 是 .NET 的猜测（取该进程第一个有标题的可见顶层窗口），
///      同一进程存在多个窗口（登录窗体 + 游戏渲染窗）或标题动态变化时会拿错，且每次调用结果可能不一致；
///   ② 玩家窗口**分辨率不固定**（全屏 / 1920×1080 / 800×600 / 玩到一半改），按尺寸或标题匹配都会失准，
///      而句柄在窗口存活期间是稳定的：改分辨率、改标题、切全屏，句柄照样指向同一个窗口；
///   ③ 多开时同一进程名有多个客户端，句柄绑定天然区分实例。
///
/// 安全前提：HWND 会被系统复用，**绝不能盲信**。<see cref="IsAlive"/> 同时要求
///   IsWindow 为真 + 该窗口所属进程 PID 与绑定时记录的一致 + 窗口仍然可见。
/// 句柄失效（客户端重启 / 窗口重建）不报错，调用方退回按 PID → 进程名+标题匹配。
/// </summary>
public static class WindowBinder
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    /// <summary>引擎渲染窗口常见的窗口类名关键字（与 ClientDiscovery 的判据保持一致）。</summary>
    private static readonly string[] EngineClassWords =
    {
        "mir", "legend", "m2", "engine", "render", "d3d", "direct", "game",
    };

    /// <summary>该 PID 的所有可见顶层窗口句柄（只读枚举，不触碰窗口）。</summary>
    public static List<IntPtr> TopWindows(int pid)
    {
        var list = new List<IntPtr>();
        if (!OperatingSystem.IsWindows() || pid <= 0) return list;

        try
        {
            EnumWindows((h, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(h, out uint owner);
                    if (owner == (uint)pid && IsWindowVisible(h)) list.Add(h);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败：按"无信息"处理，调用方退回 MainWindowHandle
        }

        return list;
    }

    /// <summary>
    /// 挑该进程"最像游戏本体"的窗口：可缩放主窗 / 自绘（无子控件）/ 占屏过半 / 类名像引擎渲染窗 得分高，
    /// 小工具窗扣分。**不看绝对分辨率**，与 ClientDiscovery 的判据同源。挑不出时返回 IntPtr.Zero。
    /// </summary>
    public static IntPtr PickTopWindow(int pid, out string why)
    {
        why = "";
        IntPtr best = IntPtr.Zero;
        int bestScore = int.MinValue;

        foreach (IntPtr h in TopWindows(pid))
        {
            WindowInfo w = WindowProbe.Describe(h);
            int score = 0;
            if (w.Sizeable) score += 25;
            if (w.SelfDrawn) score += 25;
            if (w.Covers) score += w.NearlyFullscreen ? 50 : 30;
            if (w.Tiny) score -= 30;
            if (EngineClassWords.Any(k => w.Class.ToLowerInvariant().Contains(k))) score += 25;
            if (!string.IsNullOrEmpty(TitleOf(h))) score += 5;

            if (score > bestScore)
            {
                bestScore = score;
                best = h;
            }
        }

        if (best != IntPtr.Zero) why = $"{Describe(best)}（结构分 {bestScore}）";
        else why = "该进程没有可见的顶层窗口";
        return best;
    }

    /// <summary>
    /// 校验绑定的句柄是否还能用：IsWindow + 归属进程 PID 一致（防 HWND 被系统复用）+ 仍然可见。
    /// <paramref name="expectedPid"/> 传 0 表示不校验归属进程（仅用于纯只读展示）。
    /// </summary>
    public static bool IsAlive(IntPtr hwnd, int expectedPid = 0)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return false;

        try
        {
            if (!IsWindow(hwnd)) return false;
            if (expectedPid > 0 && OwnerPid(hwnd) != expectedPid) return false;   // 句柄被复用 → 判失效
            return IsWindowVisible(hwnd);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>句柄所属进程 PID；失败返回 0。</summary>
    public static int OwnerPid(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return 0;
        try
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            return (int)owner;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>窗口标题（取不到返回空串，不抛异常）。</summary>
    public static string TitleOf(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return string.Empty;
        try
        {
            var sb = new StringBuilder(512);
            return GetWindowTextW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>一句人话描述该句柄当前状态，用于日志（含 0x 十六进制句柄、标题、结构与占屏）。</summary>
    public static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "hwnd=0";
        WindowInfo w = WindowProbe.Describe(hwnd);
        string title = TitleOf(hwnd);
        var tags = new List<string>();
        if (w.Sizeable) tags.Add("可缩放");
        if (w.SelfDrawn) tags.Add("自绘");
        if (w.NearlyFullscreen) tags.Add("准全屏");
        else if (w.Covers) tags.Add("占屏过半");
        if (w.Tiny) tags.Add("小窗");

        return $"hwnd=0x{hwnd.ToInt64():X} pid={OwnerPid(hwnd)} 类={w.Class} {w.SizeText}" +
               (string.IsNullOrEmpty(title) ? string.Empty : $" 标题=\"{title}\"") +
               (tags.Count > 0 ? " [" + string.Join("/", tags) + "]" : string.Empty);
    }
}
