using System.Diagnostics;
using System.Runtime.InteropServices;

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

    /// <summary>点击窗口客户区坐标（含人化抖动）。</summary>
    public bool ClickClient(int clientX, int clientY, bool doubleClick = false)
    {
        if (!EnsureTargetForeground(out string why))
        {
            Log?.Invoke($"[input] 拒绝点击: {why}");
            return false;
        }

        var (ox, oy) = GetClientOrigin();
        int jx = _rng.Next(-_cfg.Behavior.ClickJitterPx, _cfg.Behavior.ClickJitterPx + 1);
        int jy = _rng.Next(-_cfg.Behavior.ClickJitterPx, _cfg.Behavior.ClickJitterPx + 1);

        int sx = ox + clientX + jx;
        int sy = oy + clientY + jy;

        MoveAbsolute(sx, sy);
        Thread.Sleep(_rng.Next(12, 34));                      // 先落位再按下，别一帧内完成（像机器人）

        PressLeft();
        Thread.Sleep(_rng.Next(_cfg.Behavior.ClickHoldMinMs, _cfg.Behavior.ClickHoldMaxMs));
        ReleaseLeft();

        if (doubleClick)
        {
            Thread.Sleep(_rng.Next(45, 90));
            PressLeft();
            Thread.Sleep(_rng.Next(_cfg.Behavior.ClickHoldMinMs, _cfg.Behavior.ClickHoldMaxMs));
            ReleaseLeft();
        }
        return true;
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

    /// <summary>按一个键（如魔法快捷键 F1~F8、药水快捷键）。</summary>
    public void KeyPress(ushort virtualKey)
    {
        if (!EnsureTargetForeground(out string why))
        {
            Log?.Invoke($"[input] 拒绝按键: {why}");
            return;
        }
        SendKey(virtualKey, false);
        Thread.Sleep(_rng.Next(40, 90));
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
}
