using BotClient.ClientDriver;
using BotClient.Net;
using BotClient.Protocol;
using BotClient.Session;

namespace BotClientDriverHostUi.Host;

/// <summary>
/// <see cref="ISessionAttachment"/> 的界面版实现：与无界面宿主里的 Attachment 完全等价 ——
/// 把嗅探解析出的服务端帧注入 BotRuntime（状态镜像），并把动作驱动 / 兜底通道挂到 BotSession 上。
/// 唯一区别是日志走事件而不是控制台，并且它**不含 any 登录逻辑**（复用模式不需要）。
/// </summary>
internal sealed class UiAttachment : ISessionAttachment
{
    private readonly BotSession _session;
    private readonly BotRuntime _runtime;

    public UiAttachment(BotSession session, BotRuntime runtime)
    {
        _session = session;
        _runtime = runtime;
    }

    /// <summary>界面侧日志出口（由 HostRunner 接到 UI 日志区）。</summary>
    public event Action<string>? Log;

    /// <summary>最近一条服务端命令码（诊断用）。</summary>
    public ushort LastServerCommand { get; private set; }

    public void SetDriver(IClientDriver driver)
    {
        _session.Driver = driver;
        Log?.Invoke("动作通道已接管：BotRuntime 的 Send* 将转为鼠标/键盘操作，不再写 socket");
    }

    public void SetSink(IUpstreamSink sink)
    {
        _session.Sink = sink;
        Log?.Invoke("兜底通道已挂上：未被驱动接管的 payload 只计数、不发送");
    }

    public void Inject(MirIncomingFrame frame) => _session.InjectPacket(frame);

    public void OnServerCommand(ushort cmd, CmdPack pack) => LastServerCommand = cmd;

    public (int X, int Y) PlayerPosition => (_runtime.Player.PosX, _runtime.Player.PosY);

    public string CurrentMap => _runtime.CurrentMap;
}
