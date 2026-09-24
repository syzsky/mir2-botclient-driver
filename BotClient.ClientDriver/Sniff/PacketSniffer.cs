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
    private readonly IPAddress _serverIp;
    private readonly Dictionary<string, SniffFlow> _flows = new();
    private readonly object _gateLock = new();
    private ICaptureDevice? _device;
    private bool _disposed;

    public event Action<SniffFlow, FlowDirection, byte[]>? BytesArrived;
    public event Action<string>? Log;
    public event Action<SniffFlow>? FlowOpened;

    /// <summary>抓包层统计。</summary>
    public long PacketsSeen { get; private set; }
    public long PacketsMatched { get; private set; }

    public PacketSniffer(ClientDriverConfig cfg)
    {
        _cfg = cfg;
        if (!IPAddress.TryParse(cfg.ServerIp, out var ip))
            throw new ArgumentException("ClientDriverConfig.ServerIp 未配置或不是合法 IP。");
        _serverIp = ip;
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("抓包与输入模拟仅支持 Windows。");

        _device = OpenDevice();
        Emit($"[sniff] 使用网卡 {_device.Name} ({_device.Description})");

        // BPF：只跟这台服务器之间的 TCP，其余流量在内核过滤掉（对性能影响最小）
        _device.Filter = $"tcp and host {_serverIp}";
        _device.OnPacketArrival += OnPacketArrival;
        _device.Open(new DeviceConfiguration
        {
            Mode = DeviceModes.Promiscuous,
            ReadTimeout = 1000,
            BufferSize = 4 * 1024 * 1024,
        });
        _device.StartCapture();
        Emit("[sniff] 已开始只读抓包（不介入连接，客户端无感）");
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
            bool fromServer = src.Equals(_serverIp);          // BPF 已保证只与本服务器通信
            if (!fromServer && !dst.Equals(_serverIp)) return;

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
