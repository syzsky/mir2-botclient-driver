using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

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

    public override string ToString()
        => $"pid={Pid} {ProcessName} {(HasWindow ? $"(窗口: {WindowTitle})" : "(无窗口)")} " +
           $"→ {ServerIp}:{ServerPort} 本地端口={LocalPort} 评分={Score}";
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
/// 客户端自动发现：把"要不要手填 ProcessName / ServerIp"这件事彻底去掉。
///
/// 判定思路（从强到弱）：
///   ① 配置里已经写了 ProcessName → 只认这个进程的连接；
///   ② 进程名像传奇客户端（含 mir / legend / client / game）；
///   ③ 该连接所属进程有可见主窗口（命令行/后台服务进程没有窗口）；
///   ④ 窗口标题像传奇（含 传奇 / mir / legend）。
/// 同时把浏览器、聊天工具、开发工具等明显无关进程直接排除。
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

    /// <summary>
    /// 反查当前正在运行的客户端候选，按评分从高到低返回。
    /// <paramref name="cfg"/> 里若已填 ProcessName，则该进程获得压倒性加分。
    /// </summary>
    public static List<ClientCandidate> Discover(ClientDriverConfig cfg, int max = 8)
    {
        var endpoints = TcpTableReader.Snapshot();
        if (endpoints.Count == 0) return new List<ClientCandidate>();

        var procCache = new Dictionary<int, (string Name, bool HasWindow, string Title)>();

        var byPid = new Dictionary<int, List<TcpEndpoint>>();
        foreach (var ep in endpoints)
        {
            if (!byPid.TryGetValue(ep.Pid, out var l)) byPid[ep.Pid] = l = new List<TcpEndpoint>();
            l.Add(ep);
        }

        var candidates = new List<ClientCandidate>();
        string prefer = (cfg.ProcessName ?? string.Empty).Trim();
        bool preferConfigured = !string.IsNullOrWhiteSpace(prefer);

        foreach (var (pid, eps) in byPid)
        {
            string name;
            bool hasWindow;
            string title;
            try
            {
                using var p = Process.GetProcessById(pid);
                name = p.ProcessName;
                hasWindow = p.MainWindowHandle != IntPtr.Zero;
                title = hasWindow ? SafeTitle(p) : string.Empty;
            }
            catch
            {
                continue; // 进程已退出 / 无权限
            }

            if (IsBlacklisted(name)) continue;

            int score = 0;
            if (preferConfigured && name.Equals(prefer, StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (preferConfigured && name.Contains(prefer, StringComparison.OrdinalIgnoreCase)) score += 60;

            string lowerName = name.ToLowerInvariant();
            string lowerTitle = title.ToLowerInvariant();
            if (HintWords.Any(h => lowerName.Contains(h))) score += 50;
            if (hasWindow) score += 25;
            if (HintWords.Any(h => lowerTitle.Contains(h))) score += 25;

            if (score <= 0) continue;

            // 同一进程可能有多条连接：逐条列出，让"连到哪个服务端"一目了然
            foreach (var ep in eps)
            {
                if (TcpTableReader.IsLocalOrLoopback(ep.RemoteAddress)) continue;
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
