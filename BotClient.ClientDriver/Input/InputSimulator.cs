using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BotClient.ClientDriver.Human;
using BotClient.Human;

namespace BotClient.ClientDriver.Input;

/// <summary>
/// 真机输入模拟（操作通道）。
///
/// 为什么用 SendInput 而不是 PostMessage / 内存调用：
///   ① SendInput 走的是系统输入队列，客户端收到的消息与真人点击**无法区分**；
///   ② PostMessage 可以后台投递，但部分客户端会校验消息来源或缺失鼠标状态；
///   ③ 内存调用（CallWindowProc / 直接改客户端状态）有注入痕迹，本方案的底线是不碰客户端进程。
///
/// 代价：SendInput 是全局输入，**游戏窗口必须在前台且未被遮挡**，
/// 所以每次操作前都要 <see cref="EnsureTargetForeground"/> 校验，校验不过就直接失败并停，
/// 绝不"盲点"——盲点的后果是点到桌面上别的东西，比不动更糟。
/// </summary>
public sealed class InputSimulator
{
    private readonly ClientDriverConfig _cfg;
    private readonly Random _rng = new();

    public InputSimulator(ClientDriverConfig cfg) => _cfg = cfg;

    public event Action<string>? Log;

    private IntPtr _hwnd;
    private uint _pid;

    // ---------------------------------------------------------------- 窗口定位

    /// <summary>定位客户端主窗口并记录。反复调用是廉价的（找不到才报错）。</summary>
    public bool TryLocateWindow(out string reason)
    {
        reason = "";
        var procs = Process.GetProcessesByName(_cfg.ProcessName);
        if (procs.Length == 0)
        {
            reason = $"未找到进程 {_cfg.ProcessName}（请确认客户端已启动，名称不带 .exe）";
            return false;
        }

        foreach (var p in procs)
        {
            IntPtr h = p.MainWindowHandle;
            if (h == IntPtr.Zero) continue;
            if (!string.IsNullOrWhiteSpace(_cfg.WindowTitleKeyword) &&
                (p.MainWindowTitle ?? "").IndexOf(_cfg.WindowTitleKeyword, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            _hwnd = h;
            _pid = (uint)p.Id;
            return true;
        }

        reason = $"进程 {_cfg.ProcessName} 存在但没有可用的主窗口（多开时请配置 WindowTitleKeyword 区分）";
        return false;
    }

    public IntPtr Handle => _hwnd;

    /// <summary>客户区在屏幕上的左上角。所有"相对客户区"的校准坐标都要加上它才是屏幕坐标。</summary>
    public (int X, int Y) GetClientOrigin()
    {
        if (_hwnd == IntPtr.Zero && !TryLocateWindow(out _)) return (0, 0);
        GetClientRect(_hwnd, out RECT client);
        var pt = new POINT { X = 0, Y = 0 };
        ClientToScreen(_hwnd, ref pt);
        return (pt.X, pt.Y);
    }

    /// <summary>客户区尺寸（有些客户端窗口有边框，用这个而不是 WindowRect）。</summary>
    public (int W, int H) GetClientSize()
    {
        if (_hwnd == IntPtr.Zero) return (0, 0);
        GetClientRect(_hwnd, out RECT r);
        return (r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>
    /// 主窗口标题（原样返回）。用于多开标识推断：多开时每个客户端窗口标题一般带服务器/区/角色信息，
    /// 宿主从这里解析出"服务器名-区名-角色名"，把日志前缀与窗口标题分开，便于分辨实例。
    /// 定位不到窗口时返回空串（不抛异常）。
    /// </summary>
    public string GetWindowTitle()
    {
        if (_hwnd == IntPtr.Zero && !TryLocateWindow(out _))
        {
            return string.Empty;
        }

        int len = GetWindowTextLength(_hwnd);
        if (len <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(len + 2);
        GetWindowText(_hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>确保游戏窗口在前台；返回 false 表示"现在不能操作"。</summary>
    public bool EnsureTargetForeground(out string reason)
    {
        reason = "";
        if (!_cfg.EnsureForeground) return true;

        if (_hwnd == IntPtr.Zero && !TryLocateWindow(out reason)) return false;
        if (!IsWindow(_hwnd)) { _hwnd = IntPtr.Zero; if (!TryLocateWindow(out reason)) return false; }

        IntPtr fg = GetForegroundWindow();
        if (fg == _hwnd) return true;

        // 目标窗口是否属于我们的进程（子窗口/渲染窗口也算合格）
        GetWindowThreadProcessId(fg, out uint fgPid);
        if (fgPid == _pid) return true;

        // 尝试激活
        if (IsIconic(_hwnd)) ShowWindow(_hwnd, SW_RESTORE);
        SetForegroundWindow(_hwnd);
        Thread.Sleep(60);

        fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out fgPid);
        if (fgPid != _pid)
        {
            reason = "游戏窗口不在前台，且自动激活失败（Windows 前台锁定）。请手动把游戏窗口切到最前，或关闭其他全屏程序。";
            return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- 鼠标

    /// <summary>
    /// 点击窗口客户区坐标（含人化抖动与鼠标轨迹）。
    ///
    /// 与改造前的区别：
    ///   ① 鼠标不再是"瞬移到位"，而是从当前光标位置沿贝塞尔轨迹逐点移动到目标（见 <see cref="MoveHumanTo"/>）；
    ///   ② 落位到按下的反应时间、按下时长、双击间隔全部改成右偏长尾/截断高斯分布，
    ///      不再是"均匀随机但区间固定"的等距脉冲；
    ///   ③ 小概率在动手前先停一下（走神/犹豫），并随连续运行时长（疲劳）放大。
    /// 抖动量仍然受 ClickJitterPx 约束，保证不会点到相邻格子。
    /// </summary>
    public bool ClickClient(int clientX, int clientY, bool doubleClick = false)
    {
        if (!EnsureTargetForeground(out string why))
        {
            Log?.Invoke($"[input] 拒绝点击: {why}");
            return false;
        }

        var (ox, oy) = GetClientOrigin();
        var human = _cfg.Human;

        int jitter = JitterRadiusPx(human);
        int jx = HumanTiming.Next(-jitter, jitter + 1);
        int jy = HumanTiming.Next(-jitter, jitter + 1);

        int sx = ox + clientX + jx;
        int sy = oy + clientY + jy;

        HumanTiming.MaybePause(human);                        // 动手前的偶发停顿
        MoveHumanTo(sx, sy);                                  // 人化轨迹移动

        // 落位到按下：真人有反应时间，且是右偏长尾（多数很快、偶尔顿一下）
        Thread.Sleep(human.Enabled ? HumanTiming.LongTail(18, 40) : _rng.Next(12, 34));

        PressLeft();
        Thread.Sleep(HoldMs());
        ReleaseLeft();

        if (doubleClick)
        {
            Thread.Sleep(human.Enabled ? (int)HumanTiming.Gauss(95, 26, 42, 240) : _rng.Next(45, 90));
            PressLeft();
            Thread.Sleep(HoldMs());
            ReleaseLeft();
        }
        return true;
    }

    /// <summary>本次点击的抖动半径：人化档用高斯（多数偏小、偶尔偏大），否则用配置固定值。</summary>
    private int JitterRadiusPx(HumanTuning h)
    {
        int basePx = Math.Max(0, _cfg.Behavior.ClickJitterPx);
        if (!h.Enabled || basePx == 0) return basePx;
        return (int)Math.Round(HumanTiming.Gauss(basePx * 0.6, basePx * 0.45, 0, basePx * 1.6));
    }

    /// <summary>单次按下的持续时长（人化：高斯 + 疲劳放大）。</summary>
    private int HoldMs()
    {
        var b = _cfg.Behavior;
        int lo = Math.Max(10, b.ClickHoldMinMs);
        int hi = Math.Max(lo + 1, b.ClickHoldMaxMs);
        if (!_cfg.Human.Enabled) return _rng.Next(lo, hi);
        return HumanTiming.HoldMs(lo, hi, HumanTiming.FatigueFactor(_cfg.Human));
    }

    /// <summary>
    /// 人化移动：从当前光标位置沿贝塞尔轨迹逐点移动。
    /// 轨迹只决定"怎么到"，终点严格是调用方算出的目标点 —— 不会点偏格子。
    /// </summary>
    private void MoveHumanTo(int screenX, int screenY)
    {
        if (!_cfg.Human.Enabled)
        {
            MoveAbsolute(screenX, screenY);
            return;
        }
        if (!GetCursorPos(out POINT cur))
        {
            MoveAbsolute(screenX, screenY);
            return;
        }

        foreach (var p in HumanMousePath.Build(cur.X, cur.Y, screenX, screenY, _cfg.Human))
        {
            MoveAbsolute(p.X, p.Y);
            if (p.DelayMs > 0) Thread.Sleep(p.DelayMs);
        }
    }

    private static void MoveAbsolute(int screenX, int screenY)
    {
        int vw = GetSystemMetrics(SM_CXSCREEN);
        int vh = GetSystemMetrics(SM_CYSCREEN);
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)((long)screenX * 65535 / Math.Max(1, vw - 1)),
                    dy = (int)((long)screenY * 65535 / Math.Max(1, vh - 1)),
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void PressLeft() => SendMouse(MOUSEEVENTF_LEFTDOWN);
    private static void ReleaseLeft() => SendMouse(MOUSEEVENTF_LEFTUP);

    private static void SendMouse(uint flag)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // ---------------------------------------------------------------- 键盘

    /// <summary>
    /// 按一个键（如魔法快捷键 F1~F8、药水快捷键）。
    /// 人化档下键程时长用高斯+疲劳放大，并在按键前带小概率停顿；
    /// 真人按键时长普遍在 60~150ms，改造前的固定 40~90ms 偏"急促"。
    /// </summary>
    public void KeyPress(ushort virtualKey)
    {
        if (!EnsureTargetForeground(out string why))
        {
            Log?.Invoke($"[input] 拒绝按键: {why}");
            return;
        }
        var human = _cfg.Human;
        HumanTiming.MaybePause(human);
        SendKey(virtualKey, false);
        Thread.Sleep(human.Enabled ? HumanTiming.HoldMs(60, 150, HumanTiming.FatigueFactor(human)) : _rng.Next(40, 90));
        SendKey(virtualKey, true);
    }

    private static void SendKey(ushort vk, bool up)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = up ? KEYEVENTF_KEYUP : 0,
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // ---------------------------------------------------------------- P/Invoke

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    /// <summary>取当前光标屏幕坐标（人化轨迹的起点）。</summary>
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
