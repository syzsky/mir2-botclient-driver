using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace BotClient.ClientDriver.Sniff;

/// <summary>
/// 一个候选客户端（正在与某个服务端保持连接的进程）。
///
/// 用途：把"进程名 / 服务端 IP / 服务端端口"这三项从**人工填写**变成**自动反查**——
/// 只需要玩家自己打开客户端登录进游戏，宿主从本机 TCP 连接表就能读懂：
///   • 哪个进程是游戏客户端（进程名 + 是否有窗口 + 窗口标题）；
///   • 它连的是哪个服务端地址（ServerIp / ServerPort）；
///   • 连接用的本地端口（动态过滤用）。
/// </summary>
public sealed class ClientCandidate
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public bool HasWindow { get; init; }

    /// <summary>选定的主窗口句柄 HWND（0 = 无窗口）。钉住它即可不受分辨率/标题变化影响地绑定窗口。</summary>
    public long Hwnd { get; init; }

    /// <summary>句柄的十六进制展示（如 0x001A2B3C），仅用于界面与日志。</summary>
    public string HwndText => Hwnd != 0 ? $"0x{Hwnd:X}" : "—";
    public string ServerIp { get; init; } = string.Empty;
    public int ServerPort { get; init; }
    public int LocalPort { get; init; }
    public int Score { get; init; }

    /// <summary>行类型：游戏 / 疑似登录器 / 未知。只用于排序与人眼判断，不参与抓包决策。</summary>
    public string Kind { get; init; } = "未知";

    /// <summary>主窗口类名（登录器一般不是引擎渲染窗口）。</summary>
    public string WindowClass { get; init; } = string.Empty;

    public int WindowWidth { get; init; }
    public int WindowHeight { get; init; }

    /// <summary>窗口面积 / 所在显示器面积（相对值，不受分辨率影响）。</summary>
    public double WindowAreaRatio { get; init; }

    /// <summary>可见子控件数量（自绘渲染窗 ≈ 0，登录器窗体一堆）。</summary>
    public int WindowChildCount { get; init; }

    /// <summary>命中的渲染模块（ddraw/d3d9/opengl32…），为空表示拿不到或未加载。</summary>
    public string RenderModule { get; init; } = string.Empty;

    /// <summary>父进程名（登录器拉起游戏客户端时，这里就是登录器）。</summary>
    public string ParentName { get; init; } = string.Empty;

    /// <summary>这条连接的性质：含游戏端口 / 仅HTTP。</summary>
    public string PortKind { get; init; } = string.Empty;

    /// <summary>窗口尺寸 + 占屏比例（尺寸仅供参考，判定靠比例与结构特征）。</summary>
    public string WindowSize => HasWindow && WindowWidth > 0
        ? $"{WindowWidth}×{WindowHeight} · {(int)Math.Round(WindowAreaRatio * 100)}%"
        : "—";

    /// <summary>子控件数量展示：— / 0 / n 个控件。</summary>
    public string ChildCountText => HasWindow ? (WindowChildCount <= 0 ? "0" : WindowChildCount.ToString()) : "—";

    /// <summary>是否"看起来就是游戏本体"：渲染窗结构 + 非 HTTP 连接，且不像登录器（不看固定分辨率）。</summary>
    public bool LikelyGame => Kind == "游戏";

    public override string ToString()
        => $"pid={Pid} {ProcessName} [{Kind}] " +
           (HasWindow
               ? $"(句柄={HwndText} 窗口: \"{WindowTitle}\" {WindowSize} 类={WindowClass} 子控件={ChildCountText}" +
                 (string.IsNullOrEmpty(RenderModule) ? string.Empty : $" 渲染={RenderModule}") + ")"
               : "(无窗口)") +
           (string.IsNullOrEmpty(ParentName) ? string.Empty : $" 父={ParentName}") +
           $" → {ServerIp}:{ServerPort} 本地端口={LocalPort} {PortKind} 评分={Score}";
}

/// <summary>本机一条 IPv4 TCP 连接（来自 iphlpapi，无需管理员以外依赖、不装 netstat 解析器）。</summary>
public readonly record struct TcpEndpoint(
    string LocalAddress, int LocalPort,
    string RemoteAddress, int RemotePort,
    int Pid, int State);

/// <summary>
/// Windows TCP 连接表读取器：P/Invoke <c>GetExtendedTcpTable</c>（TCP_TABLE_OWNER_PID_ALL）。
/// 只读系统表，不建立/修改任何连接 —— 与嗅探同级的"纯观察"手段。
/// </summary>
public static class TcpTableReader
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int MIB_TCP_STATE_ESTAB = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    /// <summary>抓一份当前连接表快照；非 Windows 或调用失败时返回空列表（调用方自行降级）。</summary>
    public static List<TcpEndpoint> Snapshot(bool establishedOnly = true)
    {
        var list = new List<TcpEndpoint>();
        if (!OperatingSystem.IsWindows()) return list;

        int size = 0;
        uint ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return list;

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            ret = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != 0) return list;

            int count = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            IntPtr rowPtr = IntPtr.Add(buf, 4);

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rowPtr, i * rowSize));
                int state = (int)row.State;
                if (establishedOnly && state != MIB_TCP_STATE_ESTAB) continue;

                list.Add(new TcpEndpoint(
                    AddrToString(row.LocalAddr), Ntohs(row.LocalPort),
                    AddrToString(row.RemoteAddr), Ntohs(row.RemotePort),
                    (int)row.OwningPid, state));
            }
        }
        catch
        {
            // 表结构异常时静默降级：调用方会得到不完整结果，而不是崩溃
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        return list;
    }

    private static string AddrToString(uint addr)
    {
        // MIB 表里的 DWORD 是网络字节序，按内存顺序取字节即可还原
        byte[] b = BitConverter.GetBytes(addr);
        return new IPAddress(b).ToString();
    }

    private static int Ntohs(uint portField)
    {
        ushort p = (ushort)(portField & 0xFFFF);
        return ((p >> 8) | ((p & 0xFF) << 8)) & 0xFFFF;
    }

    /// <summary>判断地址是否为本机/回环（这类连接不是"客户端 ↔ 外网服务端"）。</summary>
    public static bool IsLocalOrLoopback(string addr)
    {
        if (!IPAddress.TryParse(addr, out var ip)) return true;
        if (IPAddress.IsLoopback(ip)) return true;
        if (addr == "0.0.0.0") return true;

        try
        {
            var locals = Dns.GetHostAddresses(Dns.GetHostName());
            foreach (var l in locals)
            {
                if (l.Equals(ip)) return true;
            }
        }
        catch
        {
            // DNS 不可用时退化为"只判回环"
        }

        return false;
    }
}

/// <summary>
/// 窗口探针（只读）：拿主窗口的类名、尺寸、可见性、窗口样式、子控件数量与所在显示器尺寸。
///
/// 关键：**判据必须与分辨率无关** —— 传奇客户端的窗口分辨率不固定（全屏、1920×1080、
/// 1024×768、800×600，甚至玩到一半改分辨率），所以这里不拿绝对像素当门槛，而是算
/// 「占所在显示器的比例」和「窗口结构特征」（可缩放/可最大化、几乎没有子控件的自绘渲染窗）。
/// 登录器/更新器一般是**固定尺寸的普通窗体**（一堆按钮/输入框子控件、不可缩放、占屏很小）。
/// </summary>
internal static class WindowProbe
{
    public const uint WS_THICKFRAME = 0x00040000;    // 可拖拽边框（可缩放）
    public const uint WS_MAXIMIZEBOX = 0x00010000;   // 有最大化按钮

    private const int GWL_STYLE = -16;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public static WindowInfo Describe(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return WindowInfo.Empty;

        try
        {
            var sb = new StringBuilder(256);
            string cls = GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;

            int w = 0, h = 0;
            if (GetWindowRect(hWnd, out var r))
            {
                w = Math.Max(0, r.Right - r.Left);
                h = Math.Max(0, r.Bottom - r.Top);
            }

            bool visible = IsWindowVisible(hWnd);
            uint style = unchecked((uint)GetWindowLong(hWnd, GWL_STYLE));

            // 所在显示器尺寸（多屏时用窗口自己那块屏，而不是主屏）
            int sw = 0, sh = 0;
            IntPtr mon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfoW(mon, ref mi))
                {
                    sw = Math.Max(0, mi.rcMonitor.Right - mi.rcMonitor.Left);
                    sh = Math.Max(0, mi.rcMonitor.Bottom - mi.rcMonitor.Top);
                }
            }
            if (sw <= 0 || sh <= 0) { sw = GetSystemMetrics(0); sh = GetSystemMetrics(1); }

            // 可见子控件数量：自绘渲染窗几乎为 0，登录器窗体通常一堆按钮/输入框
            int children = 0;
            try
            {
                EnumChildWindows(hWnd, (ch, _) =>
                {
                    try { if (IsWindowVisible(ch)) children++; } catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            double ratio = (sw > 0 && sh > 0) ? (double)w * h / ((double)sw * sh) : 0;
            return new WindowInfo(cls, w, h, visible, style, ratio, children, sw, sh);
        }
        catch
        {
            // 窗口已销毁 / 跨会话无权限：按"无信息"处理，不影响其它判据
            return WindowInfo.Empty;
        }
    }
}

/// <summary>窗口信息快照。所有判定都基于比例与结构，不依赖固定分辨率。</summary>
internal sealed class WindowInfo
{
    public static readonly WindowInfo Empty = new(string.Empty, 0, 0, false, 0, 0, 0, 0, 0);

    public WindowInfo(string cls, int w, int h, bool visible, uint style, double areaRatio,
                      int childCount, int screenW, int screenH)
    {
        Class = cls;
        W = w;
        H = h;
        Visible = visible;
        Style = style;
        AreaRatio = areaRatio;
        ChildCount = childCount;
        ScreenW = screenW;
        ScreenH = screenH;
    }

    public string Class { get; }
    public int W { get; }
    public int H { get; }
    public bool Visible { get; }
    public uint Style { get; }

    /// <summary>窗口面积 / 所在显示器面积。</summary>
    public double AreaRatio { get; }

    /// <summary>可见子控件数量。</summary>
    public int ChildCount { get; }

    public int ScreenW { get; }
    public int ScreenH { get; }

    /// <summary>可拖拽边框或有最大化按钮 —— 主窗口特征；固定尺寸小窗（登录器）通常都没有。</summary>
    public bool Sizeable => (Style & WindowProbe.WS_THICKFRAME) != 0 || (Style & WindowProbe.WS_MAXIMIZEBOX) != 0;

    /// <summary>自绘渲染窗特征：几乎没有可见子控件。</summary>
    public bool SelfDrawn => W > 0 && ChildCount <= 2;

    /// <summary>占屏过半（窗口化大窗或准全屏）。</summary>
    public bool Covers => AreaRatio >= 0.5;

    /// <summary>准全屏（≥85% 屏幕）。</summary>
    public bool NearlyFullscreen => AreaRatio >= 0.85;

    /// <summary>小工具窗：<br/>400×300 以下或占屏不到 12%。</summary>
    public bool Tiny => W > 0 && (W < 400 || H < 300 || AreaRatio < 0.12);

    public string SizeText => W > 0 ? $"{W}×{H} · {(int)Math.Round(AreaRatio * 100)}%" : "—";
}

/// <summary>
/// 模块探针（只读）：看该进程是否加载了 DirectDraw / Direct3D / OpenGL 等渲染库 —— 这是
/// **与分辨率无关**的硬信号：传奇客户端本体（各引擎）几乎都会加载 ddraw/d3d9 之类，
/// 登录器/更新器一般是普通 GDI 窗体，不加载。
/// 注意 64 位宿主枚举 32 位（WOW64）目标时拿不到对方的 32 位模块，此时返回空串，
/// 调用方按"无信息"处理（只做加分，绝不因为拿不到就扣分）。
/// </summary>
internal static class ModuleProbe
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;

    private static readonly string[] RenderModules =
    {
        "ddraw", "d3d8", "d3d9", "d3d10", "d3d11", "d3d12", "dxgi", "opengl32", "glide", "d3dim",
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModules(IntPtr hProcess, [Out] IntPtr[] lphModule, int cb, out int lpcbNeeded);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    private static extern int GetModuleBaseNameW(IntPtr hProcess, IntPtr hModule, StringBuilder lpBaseName, int nSize);

    /// <summary>命中的渲染模块名（如 ddraw.dll）；无命中或取不到时返回空串。</summary>
    public static string RenderModule(int pid)
    {
        if (!OperatingSystem.IsWindows()) return string.Empty;

        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
            if (h == IntPtr.Zero) return string.Empty;

            var mods = new IntPtr[1024];
            if (!EnumProcessModules(h, mods, mods.Length * IntPtr.Size, out int needed)) return string.Empty;

            int count = Math.Min(needed / IntPtr.Size, mods.Length);
            var sb = new StringBuilder(260);
            for (int i = 0; i < count; i++)
            {
                sb.Clear();
                if (GetModuleBaseNameW(h, mods[i], sb, sb.Capacity) <= 0) continue;

                string mod = sb.ToString().ToLowerInvariant();
                foreach (string r in RenderModules)
                {
                    if (mod.Contains(r)) return mod;
                }
            }
        }
        catch
        {
            // 权限不足 / 位数不一致：按"无信息"处理
        }
        finally
        {
            if (h != IntPtr.Zero) CloseHandle(h);
        }

        return string.Empty;
    }
}

/// <summary>
/// 进程父子关系（Toolhelp 快照，只读，不注入不调试）：
/// 登录器启动游戏客户端时，游戏进程的父进程就是登录器 —— 这是区分「登录器 vs 游戏本体」最硬的信号。
/// </summary>
internal static class ProcessTree
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>pid → (父进程 pid, 进程名)；非 Windows 或失败时返回空表（调用方按"无信息"处理）。</summary>
    public static Dictionary<int, (int ParentPid, string Name)> Snapshot()
    {
        var map = new Dictionary<int, (int, string)>();
        if (!OperatingSystem.IsWindows()) return map;

        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;

        try
        {
            int size = Marshal.SizeOf<PROCESSENTRY32W>();
            var e = new PROCESSENTRY32W { dwSize = (uint)size };
            if (!Process32FirstW(snap, ref e)) return map;

            do
            {
                map[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, e.szExeFile ?? string.Empty);
                e.dwSize = (uint)size;
            }
            while (Process32NextW(snap, ref e));
        }
        catch
        {
            // 快照读取异常：按无信息处理
        }
        finally
        {
            CloseHandle(snap);
        }

        return map;
    }
}

/// <summary>
/// 客户端自动发现：把"要不要手填 ProcessName / ServerIp"这件事彻底去掉。
///
/// 判定思路（从强到弱）：
///   ① 配置里已经写了 ProcessName → 只认这个进程的连接；
///   ② 界面里钉住的 TargetHwnd / TargetPid → 压倒性优先（句柄最稳：分辨率与标题变化都不影响）；
///   ③ **游戏本体特征（与分辨率无关）**：窗口加载了 ddraw/d3d9/opengl32 等渲染库、窗口可缩放/可最大化
///      （主窗口而非固定尺寸小窗）、几乎没有子控件（引擎自绘渲染窗）、面积占所在显示器 ≥50%、
///      窗口类名像引擎渲染窗、连接端口不是 HTTP —— 凑够 ≥2 项独立信号才认作游戏本体。
///      注意：**不拿固定分辨率当门槛**，游戏窗口可能是全屏，也可能 800×600 或玩到一半改尺寸；
///   ④ **登录器特征**：进程名/窗口标题/窗口类里出现「登录器 / launcher / update / 补丁」等字眼，
///      或该进程**只有 HTTP(S) 连接**（登录器、更新器走 Web；游戏本体走自有 TCP 网关端口）；
///      命中则降权并标成"疑似登录器"；由登录器**拉起**的子进程（父进程像登录器）反而加权，因为那才是游戏本体。
/// 同时把浏览器、聊天工具、开发工具等明显无关进程直接排除。
/// 排序后 "游戏" 排在 "疑似登录器" 之前，自动挑选时也不会再选到登录器。
/// </summary>
public static class ClientDiscovery
{
    /// <summary>明显不是游戏客户端的进程名关键字（小写匹配）。</summary>
    private static readonly string[] Blacklist =
    {
        "chrome", "msedge", "firefox", "iexplore", "opera", "brave", "360se", "qQBrowser", "sogouexplorer",
        "qq", "wechat", "weixin", "telegram", "discord", "dingtalk", "wemeet", "zoom",
        "steam", "steamwebhelper", "epicgameslauncher", "battle.net", "wegame",
        "explorer", "svchost", "system", "idle", "lsass", "csrss", "winlogon", "dwm", "rundll32",
        "code", "devenv", "pycharm", "idea64", "java", "node", "python", "pythonw", "dotnet",
        "botclient", "botclientdriverhost",            // 自己也排除（宿主不是客户端）
        "windowsterminal", "conhost", "cmd", "powershell", "pwsh",
        "nvidia", "rivatuner", "msiafterburner", "obs64", "obs32",
    };

    private static readonly string[] HintWords = { "mir", "legend", "client", "game", "传奇" };

    /// <summary>登录器/更新器特征字眼：出现在进程名、窗口标题或窗口类里 → 判为"疑似登录器"。</summary>
    private static readonly string[] LauncherWords =
    {
        "登录器", "登陆器", "launcher", "loader", "logon", "更新", "补丁", "patch",
        "update", "updater", "setup", "install", "startgame", "gamebox", "盒子",
    };

    /// <summary>引擎渲染窗口常见的窗口类名关键字（命中则更像游戏本体窗口）。</summary>
    private static readonly string[] EngineClassWords =
    {
        "mir", "legend", "m2", "engine", "render", "d3d", "direct", "game",
    };

    /// <summary>HTTP/HTTPS 及常见 Web 端口：登录器/更新器走这里；游戏本体一般走自有 TCP 网关端口。</summary>
    private static readonly int[] HttpPorts =
    {
        80, 81, 443, 800, 1080, 3000, 5000, 8000, 8080, 8081, 8443, 8888, 9000,
    };

    private static bool IsHttpPort(int port) => HttpPorts.Contains(port);

    /// <summary>
    /// 反查当前正在运行的客户端候选，按评分从高到低返回。
    /// <paramref name="cfg"/> 里若已填 ProcessName，则该进程获得压倒性加分。
    /// </summary>
    public static List<ClientCandidate> Discover(ClientDriverConfig cfg, int max = 8)
    {
        var endpoints = TcpTableReader.Snapshot();
        if (endpoints.Count == 0) return new List<ClientCandidate>();

        var byPid = new Dictionary<int, List<TcpEndpoint>>();
        foreach (var ep in endpoints)
        {
            if (!byPid.TryGetValue(ep.Pid, out var l)) byPid[ep.Pid] = l = new List<TcpEndpoint>();
            l.Add(ep);
        }

        var candidates = new List<ClientCandidate>();
        string prefer = (cfg.ProcessName ?? string.Empty).Trim();
        string preferLower = prefer.ToLowerInvariant();
        bool preferConfigured = !string.IsNullOrWhiteSpace(prefer);
        var tree = ProcessTree.Snapshot();

        foreach (var (pid, eps) in byPid)
        {
            string name;
            bool hasWindow;
            string title;
            IntPtr hWnd = IntPtr.Zero;
            try
            {
                using var p = Process.GetProcessById(pid);
                name = p.ProcessName;

                // 句柄绑定：优先挑"像游戏本体"的顶层窗口（可缩放/自绘/占屏过半/类名像引擎渲染窗），
                // 而不是 .NET 猜的 MainWindowHandle（多窗口进程里它经常指到登录窗体）。
                hWnd = WindowBinder.PickTopWindow(pid, out _);
                if (hWnd == IntPtr.Zero) hWnd = p.MainWindowHandle;   // 枚举不到就退回旧行为

                hasWindow = hWnd != IntPtr.Zero;
                title = hasWindow ? WindowBinder.TitleOf(hWnd) : string.Empty;
                if (hasWindow && title.Length == 0) title = SafeTitle(p);
            }
            catch
            {
                continue; // 进程已退出 / 无权限
            }

            if (IsBlacklisted(name)) continue;

            // 只保留"客户端 ↔ 外部服务端"的连接：本机/回环连接不算
            var remote = new List<TcpEndpoint>();
            foreach (var ep in eps)
            {
                if (!TcpTableReader.IsLocalOrLoopback(ep.RemoteAddress)) remote.Add(ep);
            }
            if (remote.Count == 0) continue;

            // ---- 窗口侧：拿窗口结构特征（可缩放/子控件数/占屏比例）与渲染模块 ----
            // 判据与分辨率无关：游戏窗口分辨率不固定（全屏~800×600 皆可能），不看绝对像素
            WindowInfo win = hasWindow ? WindowProbe.Describe(hWnd) : WindowInfo.Empty;
            string renderModule = ModuleProbe.RenderModule(pid);

            // ---- 进程侧：父进程名（登录器拉起的那个才是游戏本体）----
            string parentName = string.Empty;
            if (tree.TryGetValue(pid, out var self) && self.ParentPid > 0
                && tree.TryGetValue(self.ParentPid, out var par))
            {
                parentName = par.Name ?? string.Empty;
                if (parentName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    parentName = parentName[..^4];
            }

            string lowerName = name.ToLowerInvariant();
            string lowerTitle = title.ToLowerInvariant();
            string lowerClass = win.Class.ToLowerInvariant();
            string lowerParent = parentName.ToLowerInvariant();

            // ---- 连接侧：是自有 TCP 网关端口，还是只有 Web（HTTP/HTTPS）连接 ----
            bool anyGamePort = remote.Any(ep => !IsHttpPort(ep.RemotePort));
            string portKind = anyGamePort ? "含游戏端口" : "仅HTTP";

            // ---- 登录器特征 ----
            bool launcherWord = LauncherWords.Any(w =>
                lowerName.Contains(w) || lowerTitle.Contains(w) || lowerClass.Contains(w));
            bool parentLauncher = LauncherWords.Any(w => lowerParent.Contains(w));

            // ---- 游戏本体特征（全部与分辨率无关）----
            bool winUsable = hasWindow && win.Visible;
            bool hasRender = renderModule.Length > 0;         // 加载了 ddraw/d3d9/opengl32 等渲染库
            bool sizeable = win.Sizeable;                     // 可缩放/可最大化：主窗口，登录器多为固定尺寸小窗
            bool selfDrawn = win.SelfDrawn;                   // 几乎无子控件：引擎自绘渲染窗
            bool covers = win.Covers;                         // 占屏 ≥50%
            bool tiny = win.Tiny;                             // 占屏 <12% 或 <400×300：小工具/启动器
            bool nameLikeGame = HintWords.Any(h => lowerName.Contains(h));
            bool titleLikeGame = HintWords.Any(h => lowerTitle.Contains(h));
            bool classLikeGame = EngineClassWords.Any(w => lowerClass.Contains(w));

            // 至少两项独立信号才认作游戏本体，避免"随便一个联网的有窗口程序"被误判
            int gameSignals = 0;
            if (hasRender) gameSignals += 2;
            if (anyGamePort) gameSignals++;
            if (sizeable) gameSignals++;
            if (selfDrawn && winUsable) gameSignals++;
            if (covers) gameSignals++;
            if (classLikeGame) gameSignals++;
            if (nameLikeGame) gameSignals++;
            if (titleLikeGame) gameSignals++;
            if (parentLauncher) gameSignals++;

            bool likelyGame = !launcherWord && winUsable && anyGamePort && gameSignals >= 2;

            string kind = likelyGame
                ? "游戏"
                : (launcherWord || !anyGamePort) ? "疑似登录器" : "未知";

            int score = 0;
            if (cfg.TargetPid > 0 && pid == cfg.TargetPid) score += 1000;   // 界面里手动选中的那个，最优先
            if (cfg.TargetHwnd != 0 && hWnd.ToInt64() == cfg.TargetHwnd) score += 2000;   // 句柄绑定命中：最优先（不受分辨率/标题变化影响）
            if (preferConfigured && name.Equals(prefer, StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (preferConfigured && lowerName.Contains(preferLower)) score += 60;

            if (hasWindow) score += 25;
            if (titleLikeGame) score += 25;
            if (nameLikeGame) score += 50;

            // 游戏本体优先；登录器/更新器压下去，自动挑选时不会再选错
            if (likelyGame) score += 120;
            if (hasRender) score += 80;                    // 与分辨率无关的硬信号
            if (sizeable) score += 25;
            if (selfDrawn && winUsable) score += 25;
            if (covers) score += win.NearlyFullscreen ? 50 : 30;   // 相对屏幕占比，而非固定像素
            if (classLikeGame) score += 30;
            if (tiny) score -= 30;                         // 小工具窗/启动器（相对判据）
            score += anyGamePort ? 40 : -80;               // 只有 Web 连接 → 登录器/更新器特征
            if (launcherWord) score -= 90;
            if (parentLauncher) score += 30;               // 由登录器拉起 → 更像游戏本体

            if (score <= 0) continue;

            // 同一进程可能有多条连接：逐条列出，让"连到哪个服务端"一目了然
            foreach (var ep in remote)
            {
                candidates.Add(new ClientCandidate
                {
                    Pid = pid,
                    ProcessName = name,
                    HasWindow = hasWindow,
                    Hwnd = hWnd.ToInt64(),
                    WindowTitle = title,
                    ServerIp = ep.RemoteAddress,
                    ServerPort = ep.RemotePort,
                    LocalPort = ep.LocalPort,
                    Score = score,
                    Kind = kind,
                    WindowClass = win.Class,
                    WindowWidth = win.W,
                    WindowHeight = win.H,
                    WindowAreaRatio = win.AreaRatio,
                    WindowChildCount = win.ChildCount,
                    RenderModule = renderModule,
                    ParentName = parentName,
                    PortKind = portKind,
                });
            }
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Pid)
            .Take(max)
            .ToList();
    }

    /// <summary>给动态过滤用：按进程名找 PID（大小写不敏感，允许带/不带 .exe）。</summary>
    public static List<int> FindPidsByProcessName(string processName)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(processName)) return result;

        string want = processName.Trim();
        if (want.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            want = want[..^4];

        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.ProcessName.Equals(want, StringComparison.OrdinalIgnoreCase))
                        result.Add(p.Id);
                }
                catch { /* 单个进程访问失败忽略 */ }
                finally { p.Dispose(); }
            }
        }
        catch { }

        return result;
    }

    /// <summary>该 PID 当前所有已建立连接（用于"客户端本地端口集合"动态过滤）。</summary>
    public static List<TcpEndpoint> EstablishedOfPids(ICollection<int> pids)
    {
        if (pids.Count == 0) return new List<TcpEndpoint>();
        return TcpTableReader.Snapshot()
            .Where(ep => pids.Contains(ep.Pid))
            .ToList();
    }

    private static bool IsBlacklisted(string processName)
    {
        string n = processName.ToLowerInvariant();
        return Blacklist.Any(b => n.Contains(b.ToLowerInvariant()));
    }

    private static string SafeTitle(Process p)
    {
        try { return p.MainWindowTitle ?? string.Empty; }
        catch { return string.Empty; }
    }
}
