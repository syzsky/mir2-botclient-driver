namespace BotClient.Net;

// =====================================================================================
//  C 方案（客户端驱动式挂机）在 Core 侧的唯一新增文件 —— 见 ClientDriver/CorePatch.md
//
//  这里只有四个类型，作用是给 Core 与 ClientDriver 模块之间划一条**窄缝**：
//    • 动作出去：BotRuntime 把"想做什么"塞进 ClientActionIntent，交给 IClientDriver；
//    • 字节回来：嗅探到的服务端帧经 BotSession.InjectPacket 进入原有收包队列；
//    • 漏网之鱼：没接管的 payload 落进 IUpstreamSink（只计数、绝不写 socket）。
//
//  三项都不改变 AI / 脚本 / 状态机的任何行为 —— Driver 为 null 时全是死代码。
// =====================================================================================

/// <summary>客户端驱动模式下，Bot 想做的动作的语义分类。</summary>
public enum ClientActionKind
{
    Walk,          // 移动到 (X, Y)
    Turn,          // 转向 Dir
    Hit,           // 物理攻击 (X, Y) 处的目标
    Spell,         // 对 (X, Y) 施放法术槽 Param
    Eat,           // 使用物品栏第 Param 个物品
    Pickup,        // 拾取 (X, Y) 处的物品
    NpcInteract,   // 点击 (X, Y) 处的 NPC
    DialogSelect,  // 选择对话菜单：Text 为菜单文本，Param 为行号
    Talk,          // 说话（本版本不转换）
    DropItem,      // 丢弃物品（本版本不转换）
    Other,
}

/// <summary>
/// 一次动作意图。坐标是**世界格坐标**，语义与 BotRuntime 内部使用的一致，
/// 由 BotRuntime 在调用 Send* 时填报，驱动层只负责"让真客户端照做"。
///
/// 为什么不复用已编码的 payload：驱动层若要从 CmdPack 里反推 x/y，
/// 猜错字段顺序不会报错、只会让角色往错误方向走 —— 坐标系由发送方自己填，零猜测。
/// </summary>
public readonly record struct ClientActionIntent
{
    public ClientActionKind Kind { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Dir { get; init; }
    public int TargetId { get; init; }
    public int Param { get; init; }
    public int Extra { get; init; }
    public string? Text { get; init; }

    /// <summary>原始协议命令码，仅用于日志与自检。</summary>
    public ushort RawCommand { get; init; }

    public override string ToString()
        => $"{Kind}({X},{Y}) dir={Dir} tgt={TargetId} param={Param} extra={Extra} cmd={RawCommand}";
}

/// <summary>动作通道：把意图变成真机操作（鼠标/键盘）。</summary>
public interface IClientDriver
{
    /// <summary>是否已附着到客户端窗口。false 时 BotRuntime 的分流分支自动短路，退回直连行为。</summary>
    bool IsAttached { get; }

    Task DispatchAsync(ClientActionIntent intent, CancellationToken ct);
}

/// <summary>
/// 兜底上行通道：未被 Driver 接管的 payload 会到这里。
/// 实现方**绝不能写字节** —— 它的职责是把"漏接的动作"变成可见的待办，而不是替 Bot 发包。
/// </summary>
public interface IUpstreamSink
{
    /// <summary>驱动接管期间这里恒为 true，否则 BotCombatAI 主循环会因 !IsConnected 空转。</summary>
    bool IsConnected { get; }

    Task SendAsync(string payload, CancellationToken ct);
}
