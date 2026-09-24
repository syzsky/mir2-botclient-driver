# Core 侧改造补丁说明（C 方案 · 客户端驱动挂机）

> 本文件描述对现有 `BotClient.Core` 的**最小侵入改造**。
> 目标：让 `BotRuntime` 的 AI / 脚本引擎 / 状态机**完全不动**，只把"发字节"这一个出口
> 换成"操作系统输入"，把"收字节"这一个入口换成"嗅探注入"。
>
> 改造总计：**新增 1 个文件 + 修改 2 个文件，约 30 行**。所有改动都可以整体回滚。

---

## 0. 为什么改这两个点就够

现有 Core 的两个关键结构（已确认）：

| 位置 | 现有行为 | C 方案下需要变成 |
| --- | --- | --- |
| `BotSession.SendPayloadAsync(payload, ct)` | 唯一的发包出口，把 `#`+序号+payload+`!` 写进 socket | 转交给 `ClientInputBridge`（不写任何字节） |
| `BotRuntime.SendWalkAsync / SendHitAsync / ...` | 把坐标打进 `CmdPack` 再调 `SendPayloadAsync` | 转交 `IClientDriver`，附上**语义化**的坐标与目标 |
| `BotSession` 的接收泵（`PumpLoop` → `_pending`） | 从 `MirConnection` 收帧写进 `_pending` | 多一条注入路径：嗅探器把帧写进同一个 `_pending` |

关键点：**`_pending` 是共享信道，所以"注入"和"直连"对下游完全等价**。
`BotRuntime.ReceiveLoopAsync` 不需要知道帧是从哪来的 —— 这就是状态通道零改造的原因。

关于 `SendWalkAsync` 为什么不能只靠 `SendPayloadAsync` 兜底：
`ClientInputBridge` 拿到的是**已经编码好的字符串**，要还原出 x/y 就得去猜 `CmdPack` 里
哪个字段是 x、哪个是 y。字段顺序猜错不会报错，只会让角色朝错误的方向走 ——
所以驱动接口走**语义化意图**（`ClientActionIntent`），坐标由 `BotRuntime` 自己按原有语义填，
零猜测。

---

## 1. 新增文件：`BotClient.Core/Net/ClientDriven.cs`

```csharp
using BotClient.Protocol;   // CmdPack

namespace BotClient.Net;

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
}

/// <summary>动作通道：把意图变成真机操作。</summary>
public interface IClientDriver
{
    bool IsAttached { get; }
    Task DispatchAsync(ClientActionIntent intent, CancellationToken ct);
}

/// <summary>兜底上行通道：未被 Driver 接管的 payload 会到这里，绝不能写字节。</summary>
public interface IUpstreamSink
{
    bool IsConnected { get; }
    Task SendAsync(string payload, CancellationToken ct);
}
```

---

## 2. 修改 `BotSession.cs`（4 处）

### 2.1 新增三个成员

```csharp
// ---- C 方案（客户端驱动）挂接点 ----
/// <summary>动作驱动。非 null 时本会话不再直接写 socket 发动作。</summary>
public IClientDriver? Driver { get; set; }

/// <summary>兜底上行通道。非 null 时本会话不再直接写 socket。</summary>
public IUpstreamSink? Sink { get; set; }

private long _injectedPackets;
private long _injectedDropped;
public long InjectedPackets => Interlocked.Read(ref _injectedPackets);
public long InjectedDropped => Interlocked.Read(ref _injectedDropped);
```

### 2.2 `IsConnected` 支持外部连接

```csharp
// 改前
public bool IsConnected => _connection?.IsConnected == true;

// 改后
public bool IsConnected => Sink?.IsConnected ?? (_connection?.IsConnected == true);
```
> 不改这里的话，`BotCombatAI` 主循环里的 `if (!IsConnected) continue;` 会一直空转，
> 表现为"日志正常但角色不动"，是个很难查的坑。

### 2.3 `SendPayloadAsync` 首行分流

```csharp
public async Task SendPayloadAsync(string payload, CancellationToken ct = default)
{
    // ===== C 方案：动作不再走 socket =====
    if (Sink != null)
    {
        await Sink.SendAsync(payload, ct).ConfigureAwait(false);
        return;
    }
    // ===== 以下为原有直连逻辑，保持不变 =====
    if (_connection == null) throw new InvalidOperationException("未连接");
    ...
}
```

### 2.4 新增注入入口 + 关闭心跳自动应答

```csharp
/// <summary>
/// 把外部（嗅探）得到的服务端帧注入接收队列。
/// 与 PumpLoop 写入的是同一个信道，因此下游 BotRuntime 无法区分来源 —— 这正是我们要的。
/// </summary>
public void InjectPacket(MirIncomingFrame frame)
{
    if (_pending.Writer.TryWrite(frame))     // 信道满时丢帧而不是阻塞抓包线程
        Interlocked.Increment(ref _injectedPackets);
    else
        Interlocked.Increment(ref _injectedDropped);
}
```
> 若你的 `_pending` 不是 `Channel`，改用实际的写入方法即可（语义一致：**追加一帧，不阻塞**）。

心跳应答处加一个条件：

```csharp
// 改前：收到 SM_RUNGATE_HEARTBEAT 就自动回 CM_RUNGATE_HEARTBEAT
// 改后：C 模式下不回 —— 真客户端自己会回，我们再回一个就是多余的注入
if (Sink == null && 判定为心跳包)
{
    await SendPayloadAsync(...).ConfigureAwait(false);
}
```

---

## 3. 修改 `BotRuntime.cs`：给动作方法加分流

对每一个 `Send*` 动作方法，在**方法体第一行**插入分流。模板（以 `SendWalkAsync` 为例）：

```csharp
public async Task SendWalkAsync(int x, int y, int dir, CancellationToken ct = default)
{
    // ===== C 方案分流 =====
    if (_session.Driver is { IsAttached: true } drv)
    {
        await drv.DispatchAsync(new ClientActionIntent
        {
            Kind = ClientActionKind.Walk,
            X = x, Y = y, Dir = dir,
            RawCommand = Grobal2.CM_WALK,
        }, ct).ConfigureAwait(false);
        return;    // 关键：C 模式下不做乐观坐标更新（见下方说明）
    }
    // ===== 以下为原有逻辑，保持不变 =====
    ...
}
```

### 各方法的映射表

| 方法（按你项目实际命名核对） | Kind | 字段映射 |
| --- | --- | --- |
| `SendWalkAsync(x, y, dir)` | `Walk` | X=x, Y=y, Dir=dir |
| `SendHitAsync(x, y, targetId)` | `Hit` | X, Y, TargetId |
| `SendSpellAsync(x, y, spellSlot/targetId)` | `Spell` | X, Y, Param=法术槽, TargetId |
| `SendEatAsync(makeIndex)` | `Eat` | Param=makeIndex |
| `SendPickupAsync(x, y)` | `Pickup` | X, Y |
| NPC 交互方法 | `NpcInteract` | X, Y, TargetId |
| NPC 菜单选择方法 | `DialogSelect` | Param=行号, Text=菜单文本 |

> **不确定的方法名怎么办**：不用猜。先把上面确定能对上的接上，跑起来之后
> `ClientInputBridge` 会把所有**没接上**的命令码统计出来（`DescribeDropped()`），
> 缺哪个一目了然。这比通读 3200 行的 `BotRuntime.cs` 找方法高效得多。

### ⚠ 必须关掉"乐观坐标更新"

现有 `SendWalkAsync` 会在发包成功后**立刻把本地坐标改成目标坐标**（因为直连模式下
"发出去 = 走了"）。C 模式下这会导致本地坐标与真实坐标打架：

- 真值来自服务端广播（`SM_POSTIONMOVE` / `SM_ACTION_RET`），嗅探得到；
- 若本地先乐观改成目标格，`MapWalkController` 会误判"已经到了"，从而停止推进；
- 实际角色可能一步没走。

所以分流分支里**必须在 return 之前不要执行乐观更新**（模板里已经体现了：分流放在最前面）。
如果你的项目把乐观更新写在方法尾部，简单加个 `if (_session.Driver == null)` 包住即可。

---

## 4. 宿主侧接线（示例）

在你启动 Bot 的地方（WPF 的 ViewModel 或独立宿主）加约 15 行：

```csharp
internal sealed class SessionAttachment : ISessionAttachment
{
    private readonly BotSession _session;
    private readonly BotRuntime _runtime;

    public SessionAttachment(BotSession session, BotRuntime runtime)
    {
        _session = session;
        _runtime = runtime;
    }

    public void SetDriver(IClientDriver driver) => _session.Driver = driver;
    public void SetSink(IUpstreamSink sink)     => _session.Sink = sink;

    // 与直连模式使用完全相同的转换逻辑，保证下游无感
    public void Inject(MirIncomingFrame frame)  => _session.InjectPacket(frame);

    public void OnServerCommand(ushort cmd, CmdPack pack) { /* 需要时在此埋点 */ }

    // ⚠ 字段名按你的 Player 模型实际命名调整
    public (int X, int Y) PlayerPosition => (_runtime.Player.PosX, _runtime.Player.PosY);
}
```

启动顺序：

```csharp
var cfg  = ClientDriverConfig.Load("clientdriver.json");   // 先用 CalibrationTool 校准
var host = new ClientDriverHost(cfg);
host.Log += msg => logger.Info(msg);

host.Attach(new SessionAttachment(session, runtime));   // 1. 挂驱动与兜底
host.StartSniffing();                                   // 2. 启动只读抓包（需管理员 + Npcap）

// 3. 把 NPC 对话正文转发给 UI 态机 —— 换图/传送选菜单依赖它，漏了就只能按行号盲点
runtime.NpcMessage    += (id, text) => host.FeedNpcDialog(id, text);   // SM_MERCHANTSAY
runtime.SystemMessage += text       => host.FeedSystemMessage(text);   // SM_MENU_OK：仅续期对话态

// 4. 正常启动 BotRuntime（原有的启动流程，一行不用改）
runtime.StartReceiveLoop();
combatAi.Start();
```

启动后先看 `host.SelfCheck()` 的输出，确认没有"缺失"项再进入挂机。

---

## 5. 验证清单（按顺序做，每步都能独立证伪）

| # | 检查项 | 期望现象 | 不符时的排查方向 |
| --- | --- | --- | --- |
| 1 | `SelfCheck()` | 无"缺失"项，命令码缺失列表为空 | `ReflectionCmdCatalog.Overrides` 手工填；或核对 Core 常量类名 |
| 2 | 抓包是否拿到帧 | `PacketsMatched > 0`，日志出现"新连接 → 判定为 RunGate" | 网卡选错 / Npcap 未装 / 非管理员 / `ServerIp` 填错 |
| 3 | 状态是否注入成功 | 不点任何东西，Bot 日志里能看到角色坐标、周围物件刷新 | 帧解析错位（先看 `DiscardedBytes` 是否暴涨） |
| 4 | 单步移动 | 指令走相邻一格，角色真的走 1 格 | 方向相反→调 CellWidth/Height；偏半格→调 PlayerScreenX/Y |
| 5 | 动作确认 | 点击后日志出现 `Confirmed` | 一直 `NoAck` → 游戏窗口没在前台 / 坐标点到了 UI 上 |
| 6 | 熔断 | 手动把窗口切走，连续 5 次后自动暂停并提示 | 若能一直瞎点，说明 Gate 没接上 |
| 7 | 换图 | `TransferAsync` 能走完"靠近→点NPC→选菜单→等换图" | 距离闸门 / 菜单文本匹配 / 黑屏拦截 |
| 8 | 抓包启动时机 | 必须先启抓包、**再登录客户端** | 角色名/ID 为空 → 说明 `SM_LOGON` 与初始视野已错过，重启客户端重来 |
| 9 | NPC 菜单文本 | 点到 NPC 后日志出现 `[ui] NPC 菜单已更新（N 项）：…` | 无此日志 → 宿主漏接 `runtime.NpcMessage` |

---

## 6. 回滚

三处改动全部是**追加式**的：

- 新增的 `ClientDriven.cs` 直接删除；
- `BotSession` 的 `Driver` / `Sink` 置 `null`，`InjectPacket` 不再被调用；
- `BotRuntime` 的分流分支因为 `Driver == null` 自然短路。

即：**不需要改回任何原有代码行**（除了心跳条件），关掉 `ClientDriverHost` 就回到直连模式。
