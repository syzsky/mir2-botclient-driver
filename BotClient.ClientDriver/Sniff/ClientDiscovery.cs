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

    /// <summary>父进程名（登录器拉起游戏客户端时，这里就是登录器）。</summary>
    public string ParentName { get; init; } = string.Empty;

    /// <summary>这条连接的性质：含游戏端口 / 仅HTTP。</summary>
    public string PortKind { get; init; } = string.Empty;

    public string WindowSize => HasWindow && WindowWidth > 0 ? $"{WindowWidth}×{WindowHeight}" : "—";

    /// <summary>是否"看起来就是游戏本体"：大窗口 + 非 HTTP 连接，且不像登录器。</summary>
    public bool LikelyGame => Kind == "游戏";

    public override string ToString()
        => $"pid={Pid} {ProcessName} [{Kind}] " +
           (HasWindow ? $"(窗口: \"{WindowTitle}\" {WindowSize} 类={WindowClass})" : "(无窗口)") +
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
/// 窗口探针（只读）：拿主窗口的类名、尺寸、可见性。
/// 用途：把「游戏本体窗口」与「登录器/更新器窗口」分开 —— 登录器通常是个小尺寸窗口，
/// 类名也不是引擎的渲染窗口；游戏客户端窗口一般 1024×720 起步或接近全屏。
/// </summary>
internal static class WindowProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public static (string Class, int W, int H, bool Visible) Describe(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return (string.Empty, 0, 0, false);

        string cls = string.Empty;
        int w = 0, h = 0;
        bool visible = false;
        try
        {
            var sb = new StringBuilder(256);
            if (GetClassName(hWnd, sb, sb.Capacity) > 0) cls = sb.ToString();
            if (GetWindowRect(hWnd, out var r))
            {
                w = Math.Max(0, r.Right - r.Left);
                h = Math.Max(0, r.Bottom - r.Top);
            }
            visible = IsWindowVisible(hWnd);
        }
        catch
        {
            // 窗口已销毁 / 跨会话无权限：按"无信息"处理，不影响其它判据
        }

        return (cls, w, h, visible);
    }

    /// <summary>判定"像游戏本体的大窗口"：≥1024×720，且面积达主屏 1/4 以上（取不到屏幕尺寸时只看绝对尺寸）。</summary>
    public static bool IsBigWindow(int w, int h)
    {
        if (w < 1024 || h < 720) return false;

        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        if (sw <= 0 || sh <= 0) return true;
        return (long)w * h >= (long)sw * sh / 4;
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
///   ② 界面里钉住的 TargetPid → 压倒性优先；
///   ③ **游戏本体特征**：有可见的"大窗口"（≥1024×720 且占屏 1/4 以上）、窗口类名像引擎渲染窗、
///      连接端口不是 HTTP —— 三条里凑得越多越像游戏；
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
                hWnd = p.MainWindowHandle;
                hasWindow = hWnd != IntPtr.Zero;
                title = hasWindow ? SafeTitle(p) : string.Empty;
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

            // ---- 窗口侧：大窗口 + 窗口类，用来区分游戏本体窗口与登录器小窗 ----
            var (winClass, winW, winH, winVisible) = hasWindow
                ? WindowProbe.Describe(hWnd)
                : (string.Empty, 0, 0, false);

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
            string lowerClass = winClass.ToLowerInvariant();
            string lowerParent = parentName.ToLowerInvariant();

            // ---- 连接侧：是自有 TCP 网关端口，还是只有 Web（HTTP/HTTPS）连接 ----
            bool anyGamePort = remote.Any(ep => !IsHttpPort(ep.RemotePort));
            string portKind = anyGamePort ? "含游戏端口" : "仅HTTP";

            // ---- 登录器特征 ----
            bool launcherWord = LauncherWords.Any(w =>
                lowerName.Contains(w) || lowerTitle.Contains(w) || lowerClass.Contains(w));
            bool parentLauncher = LauncherWords.Any(w => lowerParent.Contains(w));

            // ---- 游戏本体特征 ----
            bool bigWindow = hasWindow && winVisible && WindowProbe.IsBigWindow(winW, winH);
            bool nameLikeGame = HintWords.Any(h => lowerName.Contains(h));
            bool titleLikeGame = HintWords.Any(h => lowerTitle.Contains(h));
            bool classLikeGame = EngineClassWords.Any(w => lowerClass.Contains(w));

            bool likelyGame = !launcherWord &&
                              ((bigWindow && anyGamePort) ||
                               (bigWindow && (nameLikeGame || classLikeGame)) ||
                               (nameLikeGame && anyGamePort && hasWindow) ||
                               (classLikeGame && anyGamePort));

            string kind = likelyGame
                ? "游戏"
                : (launcherWord || !anyGamePort) ? "疑似登录器" : "未知";

            int score = 0;
            if (cfg.TargetPid > 0 && pid == cfg.TargetPid) score += 1000;   // 界面里手动选中的那个，最优先
            if (preferConfigured && name.Equals(prefer, StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (preferConfigured && lowerName.Contains(preferLower)) score += 60;

            if (nameLikeGame) score += 50;
            if (hasWindow) score += 25;
            if (titleLikeGame) score += 25;

            // 游戏本体优先；登录器/更新器压下去，自动挑选时不会再选错
            if (likelyGame) score += 120;
            if (bigWindow) score += 60;
            if (hasWindow && !bigWindow) score -= 20;      // 小窗口更像登录器/工具窗
            if (classLikeGame) score += 30;
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
                    WindowTitle = title,
                    ServerIp = ep.RemoteAddress,
                    ServerPort = ep.RemotePort,
                    LocalPort = ep.LocalPort,
                    Score = score,
                    Kind = kind,
                    WindowClass = winClass,
                    WindowWidth = winW,
                    WindowHeight = winH,
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
