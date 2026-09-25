# ClientDriver —— 客户端驱动式挂机（C 方案）实现

真机官方客户端作媒介，Bot 只做**决策 + 操作**：读状态（只读嗅探）、模拟输入（SendInput），
全程不碰游戏连接的任何一个字节。

```
                    ┌──────────────── 真机官方客户端 ────────────────┐
                    │                                              │
   鼠标/键盘 ◄───────┤  游戏画面 / UI                                │
   (SendInput)      │                                              │
                    └───────────────┬──────────────────────────────┘
                                    │ 真实 TCP 连接（本模块从不介入）
                                    ▼
                            游戏服务端 115.190.x.x
                                    │
                        ┌───────────┴───────────┐
                        │  Npcap 只读镜像        │
                        ▼                       ▼
            下行字节 → 重组 → 帧解析      上行字节 → 重组 → 命令探针
                        │                       │
                        ▼                       ▼
        注入 BotSession（与直连同构）      动作确认 / UI 态机
                        │
                        ▼
        BotRuntime / 脚本引擎 / AI（原样不动）
                        │
                        ▼
                 IClientDriver  ← 只在这里分叉
```

---

## 一、这个模块解决了什么

脱机挂失效的根因是**服务端丢弃超速上行包**（走 490 / 攻 900 / 特殊药 1000 ms 阈值），
包一丢，本地坐标就成了幻影，`dist <= 1` 永远不满足，攻击条件永不触发。

C 方案不再试图"把包发得刚刚好"，而是**把发包这件事整个交还给真客户端**：
Bot 只负责点地图、按键，节流由游戏自身的行走/攻击冷却天然实现，
服务端看到的节流特征与真人操作**完全一致**（因为上下行都是真客户端在跑）。

代价是必须操作窗口，所以：

- 单开（SendInput 是全局输入，多开会互相干扰）；
- 游戏窗口必须前台可见（不能最小化），但**可以挂在副屏或后台窗口位置**，只要不遮挡即可；
- 需要管理员权限 + Npcap。

---

## 二、文件清单

| 文件 | 职责 |
| --- | --- |
| `BotClient.ClientDriver.csproj` | net8.0-windows；引用 Core；引入 SharpPcap / PacketDotNet |
| `ClientDriverConfig.cs` | 全部配置与校准常量，JSON 读写 |
| `ClientDriverHost.cs` | 组装根：抓包→重组→解析→注入；挂接 Driver / Sink |
| `NpcTransferRunner.cs` | 换图流程（靠近→点NPC→选菜单→等换图→等就绪） |
| `CalibrationTool.cs` | 校准辅助工具（控制台，量屏幕坐标） |
| `Sniff/MirFrameCodec.cs` | 帧切分（三网关）+ 上行命令探针 |
| `Sniff/TcpReassembler.cs` | 按四元组重组 TCP 段（乱序 / 重传 / 半包） |
| `Sniff/PacketSniffer.cs` | SharpPcap 只读抓包，自动选网卡；`ServerIp` 留空时自动跟随客户端连接锁定服务端 |
| `Sniff/ClientDiscovery.cs` | 零配置识别：读本机 TCP 连接表（iphlpapi）反查「客户端进程名 + 服务端地址」 |
| `Sniff/LoginCredentialProbe.cs` | 从客户端自己的登录流量里解出账号密码（会话密钥 + 上行 CM_IDPASSWORD），仅存内存 |
| `Input/InputSimulator.cs` | SendInput 鼠标键盘、窗口定位、前台校验、人味抖动 |
| `Input/ScreenMapper.cs` | 世界格 ⇄ 屏幕像素（等距 45° 视图 / 小地图 / 背包 / 对话框） |
| `Input/ActionGate.cs` | 所有点击的唯一入口：串行 + 节流 + 熔断 |
| `Input/UiStateProbe.cs` | UI 场景态机（自由 / 对话 / 换图加载），拦掉非法点击 |
| `Input/MapWalkController.cs` | 移动控制：近距点格 / 远距点小地图，卡死自动换策略 |
| `Input/ClientActionDriver.cs` | 意图 → 具体操作（点击/按键），含动作确认 |
| `Input/ClientInputBridge.cs` | 兜底通道：未接管动作**只计数不发送**，把漏洞变可见 |
| `CmdCatalog.cs` | 反射取 Core 的 CM_/SM_ 命令码，取不到可手工覆盖 |
| `CorePatch.md` | **Core 侧改造说明（必读，改完才能跑）** |

---

## 三、前置条件

1. **Npcap** 已安装（WinPcap 兼容模式勾选）；
2. 以**管理员**身份运行（抓包需要）；
3. 已按 `CorePatch.md` 完成 Core 的 3 处改造；
4. 游戏客户端已登录、角色已站到地图上（本模块**不负责登录**——登录由你手动完成，
   这也顺带绕开了所有登录态与验证码问题）。

**`clientdriver.json` 里基本不用手填了**（见下节"零配置复用模式"）：

| 配置项 | 是否还要手填 | 说明 |
| --- | --- | --- |
| `ProcessName` | 否 | 自动识别正在运行、且连着服务端的客户端进程（含窗口标题佐证） |
| `ServerIp` | 否 | 留空即"自动跟随"：从客户端真实 TCP 连接反查，并自动收窄抓包过滤器 |
| `LoginGatePort` 等三段端口 | 否 | 留 0 自动判定 |
| `View` / `MiniMap` / `Bag` / `Dialog` 校准 | **要** | 世界格↔屏幕像素的换算是纯视觉量，必须实测一次（第五节） |
| `MapDirHint` | 可选 | 填了才能把中文地名反查成图代码，进图判定更准 |

---

## 四、快速开始

### 4.1 零配置复用模式（默认，推荐）

宿主**不自己登录**——账号、密码、服务端地址全部取自你自己客户端的登录过程：

```
①  打开官方客户端 → 登录 → 创建/进入角色
②  以管理员身份运行 BotClientDriverHost.exe（不带任何参数）
③  宿主自动完成三件事：
      · 认出客户端进程（ProcessName）
      · 跟随它对服务端的真实连接（ServerIp / 端口）
      · 从它自己发出的 CM_IDPASSWORD 登录包里还原账号密码（仅存内存，不落盘）
④  看日志确认识别结果 → 开始挂机
```

要点：

- **顺序无所谓**：先开宿主后开客户端也行，宿主每秒复查一次（进程出现、连接建立后自动补上）。
- **登录包什么时候抓**：客户端**下一次点"登录"**的那一刻（含掉线重连、退回登录界面重登、
  换角色）。如果宿主启动时客户端已经登录完成，本次拿不到历史登录包——
  不影响挂机，只是这次复用不到账号密码；下次登录即会抓到。
- **只读**：账号密码是从客户端自己发出的包**解**出来的，宿主不注入、不改包、不碰客户端文件。
- 命令：`--sniff-only`（只嗅探不点击，先验证链路）、`--fight-x N --fight-y N`（定点挂机）。

### 4.2 老路径：宿主自己登录（可选）

需要显式指定账号时才用（比如客户端不方便重登）：

```
BotClientDriverHost.exe --login --account <账号>
   # 密码优先读环境变量 BOT_PASSWORD（不落盘、不进命令行历史）
```

老路径下 `botsettings.json` 的 `Host` / `Port` 必填，`clientdriver.json` 的 `ServerIp` 可留空继承 `Host`。

### 4.3 首次运行前的准备

```
①  编译 ClientDriver 工程，产出 BotClient.ClientDriver.dll
②  跑一次校准：  CalibrationTool.Run("clientdriver.json")
③  在宿主里接线（15 行，见 CorePatch.md 第 4 节）
④  启动 → 看 SelfCheck() 输出 → 无缺失项即可挂机
```

### 4.4 构建与打包（一键出 exe）

宿主是 win-x64 单文件，**在 Linux 上也能直接交叉编译出 Windows exe**，不必等 CI：

```bash
dotnet restore BotClientDriverHost/BotClientDriverHost.csproj -r win-x64 -p:EnableWindowsTargeting=true
dotnet publish BotClientDriverHost/BotClientDriverHost.csproj -r win-x64 -c Release \
  -p:EnableWindowsTargeting=true -o publish
# → publish/BotClientDriverHost.exe
#   约 35 MB，self-contained 单文件，目标机免装 .NET 运行时
```

- 关键开关是 `-p:EnableWindowsTargeting=true`：**不加会报 `NETSDK1100`**（认为你不该在非 Windows 上编 windows 目标）；
- 需要 .NET 8 SDK（`dotnet --list-sdks` 应有 `8.0.x`），首次 restore 需联网拉 NuGet；
- 验收标准：三个工程（Core / ClientDriver / DriverHost）编译 **0 warning / 0 error**；
- `.github/workflows/build-win-x64.yml` 仍然保留：push 到 `main` 会在 windows-latest 上
  重跑同一套流程，并额外做 PE 架构（必须是 x64）+ 启动冒烟自检，产出 artifact。

---

## 五、校准指南（决定成败的一步）

所有"世界格 → 屏幕像素"的换算都依赖实测常量，量一次即可长期复用。

| 键 | 记录内容 | 怎么量 |
| --- | --- | --- |
| F2 / F3 | 视图区左上 / 右下 | 鼠标移到主画面区域的左上边界、右下边界 |
| F1 | 玩家所在格中心 | 鼠标移到**角色脚下那一格**的正中心 |
| F4 / F5 | 小地图左上 / 右下 | 鼠标移到小地图控件的左上、右下角 |
| F6 | 背包第一格中心 | 按 F9 打开背包，鼠标移到第一格中心 |
| F7 | 对话框第一行文字 | 找 NPC 打开对话窗，鼠标移到第一行文字上 |
| F8 | 对话框翻页按钮 | 同上，移到"下一页 ▸"上 |

量完之后**必须做逐格验证**：让 Bot 走相邻一格，看角色是否真的朝那一格走了 1 格。

- 走成**反方向** → `CellWidth` / `CellHeight` 符号或取值不对；
- 走偏**半格** → 微调 `PlayerScreenX` / `PlayerScreenY`；
- 只在某个方向偏 → 等距视图的横纵比不准（`CellWidth : CellHeight` 通常为 2:1）。

小地图的 `PixelPerCell` 先用校准工具给的估值，然后**用一张已知大小的地图反推**：
从地图这头点到那头，看落点差多少格，按比例修正。

---

## 六、与直连模式的行为差异

| 维度 | 直连（原 BotClient） | C 方案（本模块） |
| --- | --- | --- |
| 上行包 | Bot 自己构造并发送 | **由真客户端发送**，Bot 只是操作它 |
| 节流 | 靠 `!Setup.txt` 阈值硬卡 | 游戏自身冷却天然生效 |
| 服务端视角 | 一个和客户端并存的第二连接 | **只有一个连接**，就是真客户端 |
| 状态来源 | 自己那条连接的收包 | 嗅探真客户端连接（同一份数据） |
| 坐标更新 | 发包后乐观更新 | **必须关掉**，只信广播 |
| 多开 | 支持 | 不支持（SendInput 全局） |
| 窗口 | 可最小化 | 必须前台 |

---

## 七、残余风险（如实列出）

1. **行为层规律性**：Bot 的走位/攻击节奏若过于工整，仍可能被行为分析识别。
   本模块在 `InputSimulator` 里做了像素抖动与时长抖动，但**这只能降低特征强度，不能消除**。
2. **UI 态误判**：新增 UI 元素（活动弹窗、公告）会让点击落到错误位置。
   `UiStateProbe` 做了保守拦截，遇到未知态会暂停而不是硬点。
3. **焦点抢占**：任何前台弹出的窗口（QQ 消息、系统通知）都会让后续点击打偏。
   `ActionGate` 的前台校验会拒绝执行，但**建议挂机时关闭通知**。
4. **像素规律性**：点击坐标虽加了抖动，长周期统计下仍可能呈现分布特征。
5. **账号密码在宿主进程内存中短暂驻留**：零配置复用模式下，宿主从客户端自己的
   `CM_IDPASSWORD` 包里解出明文凭据，**只放在内存、不写配置文件、不落盘**，
   进程退出即消失；但挂机期间该进程内存中确实存在明文，请勿在共享机器上长时间挂机，
   也不要把宿主进程的内存转储（dump）交给他人。
6. **不保证绝对安全**：本方案把"能不能被检测"的问题从**协议层**移到了**行为层**，
   是风险性质的改变，不是风险归零。

---

## 八、落地顺序建议（按风险递增）

1. 先只开**抓包 + 注入**（不挂 Driver），确认 Bot 能正确感知状态 —— 这一步零风险；
2. 再只开**移动**，手动指定目标点，确认走动正常、坐标同步正确；
3. 再开**战斗**，观察动作确认率；
4. 最后才开**换图与全自动脚本**。

每步都能独立回滚，不要一次全开。

---

## 九、需要与你的 Core 实际命名核对的点

代码里对 Core 的引用集中在少数几处，命名不一致时逐个改 `using` 或字段名即可，
**逻辑本身不受影响**：

| 本模块假设 | 出现位置 | 核对方式 |
| --- | --- | --- |
| `BotClient.Net.MirGateMode` / `MirFrameKind` / `MirIncomingFrame` | `Sniff/MirFrameCodec.cs`（**唯一协议接触点**，文件头已列出全部 5 处待核对项） | 看 `MirConnection.cs` 的命名空间 |
| `MirIncomingFrame` 构造签名 = `(Kind, Text, Header, BodyEncoded)` | 同上 | 看 Core 里该类型的定义 |
| `BotClient.Protocol.MirPacketDecoder` / `CmdPack` | `Sniff/MirFrameCodec.cs`、`CmdCatalog.cs` | 看协议层文件 |
| `BotClient.Net.IUpstreamSink` / `IClientDriver` / `ClientActionIntent` | `CorePatch.md` 里是你自己新增的，命名随你 | — |
| `Grobal2` 常量类名与 `CM_*` / `SM_*` 字段名 | `CmdCatalog.cs`（反射，取不到会记入 `Missing`） | 跑一次看 `Missing` 列表 |
| `runtime.Player.PosX / PosY` | `CorePatch.md` 第 4 节 | 看你的角色模型 |
| `BotSession._pending` 的类型与写入方法 | `CorePatch.md` 第 2.4 节 | 看会话的接收信道定义 |
