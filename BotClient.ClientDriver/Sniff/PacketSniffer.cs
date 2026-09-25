using System.Net;
using BotClient.Net;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace BotClient.ClientDriver.Sniff;

/// <summary>嗅探到的一条 TCP 流（按四元组归并）在某一段网关上的方向。</summary>
public enum FlowDirection
{
    /// <summary>服务端 → 客户端（状态来源）。</summary>
    Downstream,
    /// <summary>客户端 → 服务端（动作确认来源）。</summary>
    Upstream,
}

/// <summary>一条被识别的网关连接。</summary>
public sealed class SniffFlow
{
    public required IPEndPoint Server { get; init; }
    public required IPEndPoint Client { get; init; }
    public MirGateMode Gate { get; set; } = MirGateMode.RunGate;

    public TcpReassembler Downstream { get; } = new();
    public TcpReassembler Upstream { get; } = new();
}

/// <summary>
/// 只读抓包：监听本机与游戏服务器之间的 TCP 流量，按四元组归并成若干条 <see cref="SniffFlow"/>，
/// 每条流再做 TCP 重组，最后把有序字节交给上面的帧解析层。
///
/// 三件必须说清楚的事：
///   ① **纯只读**：不加载 WinDivert、不改路由、不代理连接，客户端完全无感（Npcap 只收不发）；
///   ② **需要管理员权限 + 已安装 Npcap**（安装时勾选 "WinPcap API 兼容模式"）；
///   ③ 只按服务端 IP 过滤，端口靠配置 + 自动判定（登录链路里 SelGate/RunGate 的地址是
///      登录过程中由服务端下发的，未必等于预配置端口）。
/// </summary>
public sealed class PacketSniffer : IDisposable
{
    private readonly ClientDriverConfig _cfg;
    private readonly Dictionary<string, SniffFlow> _flows = new();
    private readonly object _gateLock = new();
    private ICaptureDevice? _device;
    private bool _disposed;

    /// <summary>服务端 IP：配置为空时进入"自动跟随"模式，由客户端真实连接反查得到。</summary>
    private IPAddress? _serverIp;

    // ---- 自动跟随（ServerIp 未配置时启用）----
    private readonly HashSet<int> _clientLocalPorts = new();
    private readonly HashSet<int> _notClientPorts = new();   // 已确认不是客户端端口的本地端口（避免重复查表）
    private readonly List<int> _clientPids = new();
    private Timer? _endpointTimer;
    private bool _portFilterActive;
    private bool _autoFollow;
    private int _discoverTick;

    public event Action<SniffFlow, FlowDirection, byte[]>? BytesArrived;
    public event Action<string>? Log;
    public event Action<SniffFlow>? FlowOpened;

    /// <summary>自动跟随模式下锁定服务端地址时触发（宿主可回写配置）。</summary>
    public event Action<string, int>? ServerEndpointLocked;

    /// <summary>抓包层统计。</summary>
    public long PacketsSeen { get; private set; }
    public long PacketsMatched { get; private set; }

    public PacketSniffer(ClientDriverConfig cfg)
    {
        _cfg = cfg;
        // ServerIp 允许留空：留空 = 自动跟随（按客户端进程的真实连接反查服务端地址）。
        // 填了但格式不对时给出警告并退回自动模式，而不是直接拒绝启动。
        if (!string.IsNullOrWhiteSpace(cfg.ServerIp))
        {
            if (IPAddress.TryParse(cfg.ServerIp.Trim(), out var ip)) _serverIp = ip;
            else Emit($"[sniff] 配置的 ServerIp \"{cfg.ServerIp}\" 不是合法 IP，改为自动跟随");
        }
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("抓包与输入模拟仅支持 Windows。");

        _device = OpenDevice();
        Emit($"[sniff] 使用网卡 {_device.Name} ({_device.Description})");

        _autoFollow = _serverIp == null;

        _device.OnPacketArrival += OnPacketArrival;

        // 关键顺序：必须先 Open（内部 pcap_activate 激活句柄），之后才能设置 BPF Filter 与 StartCapture。
        // SharpPcap 6.x 在设备未打开时读写 Filter 会直接抛 "device is not open"，导致宿主一启动就失败。
        try
        {
            _device.Open(new DeviceConfiguration
            {
                Mode = DeviceModes.Promiscuous,
                ReadTimeout = 1000,
                BufferSize = 4 * 1024 * 1024,
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"抓包设备打开失败（{_device.Name}）：{ex.Message}。" +
                "请依次确认：① 以管理员身份运行本程序；② Npcap 已装好（安装后建议重启一次系统）；" +
                "③ 该网卡未被 Wireshark 等抓包软件独占；④ 可在 clientdriver.json 用 CaptureDevice 指定其它网卡。", ex);
        }

        if (_autoFollow)
        {
            // 还不知道服务端在哪：先抓全部 TCP，在用户态按"客户端进程的本地端口集合"过滤，
            // 首个外部连接一出现就锁定并收窄过滤器（见 RefreshEndpoints / LockServerEndpoint）。
            _device.Filter = "tcp";
            _portFilterActive = true;
            _endpointTimer = new Timer(_ => RefreshEndpoints(), null, 0, 1000);
            Emit("[sniff] ServerIp 未配置 —— 自动跟随模式：请启动客户端登录，宿主会自行识别进程与服务端地址");
        }
        else
        {
            // BPF：只跟这台服务器之间的 TCP，其余流量在内核过滤掉（对性能影响最小）
            _device.Filter = $"tcp and host {_serverIp}";
        }

        _device.StartCapture();
        Emit("[sniff] 已开始只读抓包（不介入连接，客户端无感）");
    }

    // ------------------------------------------------------------------ 自动跟随

    /// <summary>
    /// 每秒刷新一次"客户端进程的本地端口集合"，并（在未锁定时）用它反查服务端地址。
    /// 只看系统 TCP 连接表，不碰任何连接本身。
    /// </summary>
    private void RefreshEndpoints()
    {
        if (_disposed || !_autoFollow) return;
        try
        {
            if (_clientPids.Count == 0 || !_clientPids.Any(IsProcessAlive))
            {
                _clientPids.Clear();
                _clientPids.AddRange(ClientDiscovery.FindPidsByProcessName(_cfg.ProcessName));
            }

            // 还没有 PID 时每 2 秒重试一次识别 —— 允许"先启动宿主、后启动客户端"的顺序，
            // 也允许进程名不是配置里那个默认值（此时靠自动发现挑最像客户端的进程）。
            if (_clientPids.Count == 0 && ++_discoverTick % 2 == 1)
            {
                var cands = ClientDiscovery.Discover(_cfg, 3);
                if (cands.Count > 0)
                {
                    var best = cands[0];
                    Emit($"[sniff] 自动识别到客户端进程 {best.ProcessName} (pid={best.Pid})" +
                         (best.HasWindow ? $" 窗口=\"{best.WindowTitle}\"" : string.Empty));
                    _cfg.ProcessName = best.ProcessName;
                    _clientPids.Clear();
                    _clientPids.AddRange(ClientDiscovery.FindPidsByProcessName(best.ProcessName));
                    TryLockServerEndpoint(best.ServerIp, best.ServerPort);
                }
            }

            var eps = ClientDiscovery.EstablishedOfPids(_clientPids);
            if (eps.Count == 0) return;

            var ports = new HashSet<int>();
            var remoteVotes = new Dictionary<string, (int Count, int Port)>();

            foreach (var ep in eps)
            {
                ports.Add(ep.LocalPort);
                if (TcpTableReader.IsLocalOrLoopback(ep.RemoteAddress)) continue;

                remoteVotes.TryGetValue(ep.RemoteAddress, out var cur);
                remoteVotes[ep.RemoteAddress] = (cur.Count + 1, cur.Port != 0 ? cur.Port : ep.RemotePort);
            }

            lock (_clientLocalPorts)
            {
                _clientLocalPorts.Clear();
                foreach (int p in ports) _clientLocalPorts.Add(p);
                _notClientPorts.Clear();   // 端口会被系统复用，负缓存每秒重置一次
            }

            if (_serverIp == null && remoteVotes.Count > 0)
            {
                var top = remoteVotes.OrderByDescending(kv => kv.Value.Count).First();
                TryLockServerEndpoint(top.Key, top.Value.Port);
            }
        }
        catch (Exception ex)
        {
            Emit($"[sniff] 自动跟随刷新异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 自动跟随期的归属判定：先查"客户端本地端口集合"（快路径，每秒刷新一次）；
    /// 集合里没有时再实时查一次本机 TCP 连接表（慢路径）。
    ///
    /// 慢路径不是可选项 —— 客户端刚建立的连接可能还没被那一秒的快照收录，
    /// 而"登录包"恰好就发生在建连后的第一时间，漏掉它就等于丢掉了账号密码。
    /// </summary>
    private bool AcceptAutoFollow(TcpPacket tcp, out bool fromServer)
    {
        fromServer = false;

        lock (_clientLocalPorts)
        {
            if (_clientLocalPorts.Contains(tcp.SourcePort)) return true;
            if (_clientLocalPorts.Contains(tcp.DestinationPort)) { fromServer = true; return true; }
            if (_notClientPorts.Contains(tcp.SourcePort) && _notClientPorts.Contains(tcp.DestinationPort)) return false;
        }

        int clientPort = 0;
        try
        {
            foreach (var ep in ClientDiscovery.EstablishedOfPids(_clientPids))
            {
                if (ep.LocalPort == tcp.SourcePort && ep.RemotePort == tcp.DestinationPort)
                {
                    clientPort = tcp.SourcePort;
                    fromServer = false;
                    break;
                }
                if (ep.LocalPort == tcp.DestinationPort && ep.RemotePort == tcp.SourcePort)
                {
                    clientPort = tcp.DestinationPort;
                    fromServer = true;
                    break;
                }
            }
        }
        catch
        {
            return false;
        }

        lock (_clientLocalPorts)
        {
            if (clientPort != 0)
            {
                _clientLocalPorts.Add(clientPort);
                return true;
            }
            _notClientPorts.Add(tcp.SourcePort);
            _notClientPorts.Add(tcp.DestinationPort);
            return false;
        }
    }

    private void TryLockServerEndpoint(string ip, int port)
    {
        if (_serverIp != null || string.IsNullOrWhiteSpace(ip)) return;
        if (!IPAddress.TryParse(ip, out var addr)) return;

        _serverIp = addr;
        _cfg.ServerIp = ip;
        _portFilterActive = false;
        Emit($"[sniff] 已锁定服务端地址 {ip}:{port}（来自客户端真实连接；配置里 ServerIp 现可留空）");

        try { if (_device is { Started: true }) _device.Filter = $"tcp and host {ip}"; }
        catch (Exception ex) { Emit($"[sniff] 收窄过滤器失败（忽略，继续在用户态过滤）: {ex.Message}"); }

        if (_cfg.LoginGatePort == 0 && port != 0)
        {
            // 只作为"最可能是哪个网关"的提示，不写死：实际判定仍走 GuessGate
            Emit($"[sniff] 提示：该连接端口 = {port}");
        }

        ServerEndpointLocked?.Invoke(ip, port);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private ICaptureDevice OpenDevice()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.CaptureDevice))
        {
            var named = CaptureDeviceList.Instance.FirstOrDefault(d => d.Name == _cfg.CaptureDevice);
            if (named != null) return named;
            Emit($"[sniff] 配置的网卡 {_cfg.CaptureDevice} 未找到，回退自动选择");
        }

        var list = CaptureDeviceList.Instance;
        if (list.Count == 0)
            throw new InvalidOperationException("未发现任何抓包设备：请确认已安装 Npcap（勾选 WinPcap 兼容模式）并以管理员运行。");

        // 优先挑已分配 IPv4 的非回环网卡
        foreach (var d in list)
        {
            try
            {
                // Addresses 是 LibPcapLiveDevice 上的 ReadOnlyCollection<PcapAddress>（ILiveDevice 接口上没有），
                // 每个 PcapAddress.Addr 是 Sockaddr，其 ipAddress 为公开字段。
                var addrs = (d as LibPcapLiveDevice)?.Addresses;
                if (addrs == null || addrs.Count == 0) continue;
                bool usable = addrs.Any(a => a?.Addr?.ipAddress != null
                                             && a.Addr.ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                             && !IPAddress.IsLoopback(a.Addr.ipAddress));
                if (usable) return d;
            }
            catch { /* 某些虚拟网卡枚举会抛，跳过 */ }
        }
        return list[0];
    }

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        if (_disposed) return;
        try
        {
            PacketsSeen++;
            var raw = e.GetPacket();
            var tcp = Packet.ParsePacket(raw.LinkLayerType, raw.Data).Extract<TcpPacket>();
            if (tcp == null) return;

            var ip = tcp.ParentPacket?.Extract<IPPacket>();
            if (ip == null) return;

            var src = ip.SourceAddress;
            var dst = ip.DestinationAddress;

            bool fromServer;
            if (_portFilterActive)
            {
                // 自动跟随期：按"这条包属不属于客户端进程的连接"判定
                if (!AcceptAutoFollow(tcp, out fromServer)) return;
            }
            else
            {
                if (_serverIp == null) return;                // 还没锁定任何端点
                bool srcIsServer = src.Equals(_serverIp);
                bool dstIsServer = dst.Equals(_serverIp);
                if (!srcIsServer && !dstIsServer) return;
                fromServer = srcIsServer;
            }

            PacketsMatched++;
            var serverEp = new IPEndPoint(fromServer ? src : dst, fromServer ? tcp.SourcePort : tcp.DestinationPort);
            var clientEp = new IPEndPoint(fromServer ? dst : src, fromServer ? tcp.DestinationPort : tcp.SourcePort);

            var flow = GetOrCreateFlow(serverEp, clientEp);
            if (flow == null) return;

            var reassembler = fromServer ? flow.Downstream : flow.Upstream;

            // PacketDotNet 1.4.7 里 SYN 标志叫 Synchronize（不是 Synchronized / Syn）
            if (tcp.Synchronize) reassembler.OnSyn(tcp.SequenceNumber);
            if (tcp.Finished || tcp.Reset) reassembler.OnFin();

            byte[] payload = tcp.PayloadData;
            if (payload is { Length: > 0 })
                reassembler.OnSegment(tcp.SequenceNumber, payload);   // 事件已在 GetOrCreateFlow 里挂好，切勿在此重复订阅
        }
        catch (Exception ex)
        {
            Emit($"[sniff] 处理包异常: {ex.Message}");
        }
    }

    private SniffFlow? GetOrCreateFlow(IPEndPoint server, IPEndPoint client)
    {
        string key = $"{client.Address}:{client.Port}-{server.Address}:{server.Port}";
        lock (_gateLock)
        {
            if (_flows.TryGetValue(key, out var exist)) return exist;

            var flow = new SniffFlow { Server = server, Client = client, Gate = GuessGate(server.Port) };
            flow.Downstream.DataAvailable += data => BytesArrived?.Invoke(flow, FlowDirection.Downstream, data);
            flow.Upstream.DataAvailable += data => BytesArrived?.Invoke(flow, FlowDirection.Upstream, data);
            _flows[key] = flow;
            Emit($"[sniff] 新连接 {key} → 判定为 {flow.Gate}");
            FlowOpened?.Invoke(flow);
            return flow;
        }
    }

    /// <summary>按端口判定网关；未配置或未命中时先按 RunGate 处理（进入游戏后绝大多数流量都是它）。</summary>
    private MirGateMode GuessGate(int serverPort)
    {
        if (_cfg.LoginGatePort != 0 && serverPort == _cfg.LoginGatePort) return MirGateMode.LoginGate;
        if (_cfg.SelGatePort != 0 && serverPort == _cfg.SelGatePort) return MirGateMode.SelGate;
        if (_cfg.RunGatePort != 0 && serverPort == _cfg.RunGatePort) return MirGateMode.RunGate;
        return MirGateMode.RunGate;
    }

    private void Emit(string msg) => Log?.Invoke(msg);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _endpointTimer?.Dispose(); } catch { }
        _endpointTimer = null;
        try
        {
            if (_device != null)
            {
                _device.OnPacketArrival -= OnPacketArrival;
                if (_device.Started) _device.StopCapture();
                _device.Close();
            }
        }
        catch { }
        _device = null;
    }
}
