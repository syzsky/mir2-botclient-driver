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

### 1.1 什么样的客户端能联通（适用范围）

嗅探解析层绑定 **Mir2 血统网关协议**，操作层（键鼠）与协议无关。判定条件：

| 维度 | 要求 | 说明 |
| --- | --- | --- |
| 协议血统 | **必须 Mir2** | RunGate 魔数 `DDCCBBAA` + 三网关（LoginGate / SelGate / RunGate）、22 字符登录密钥包、上行明文 EdCode |
| 框架骨架 | Mir2 血统同源 | GXX / GOM / GEE / 996 / 绿盟 / HGE 都用「三网关 + RunGate 头 + 22 字符登录包」这套骨架 |
| 具体协议 | **每服都可能被改** | 帧定界 / 命令码 / 包体结构 / 加密链四层各服不同，**必须按服适配**（见第十节） |
| 传输 | 明文 TCP | 无 TLS / 私有加密壳；登录包被二次加密时凭据探测会失效（可退 `--login` 老路径） |
| 窗口 | 独立进程 + 可激活主窗口 | 不能是 DirectInput 独占输入、或被反外挂驱动保护输入通道的客户端 |
| 反外挂 | 必须无内核级 | 有内核反外挂的客户端直接放弃本方案 |

**不能用的**：传奇3 / 传世（非 Mir2 帧格式）、改过加密链或私有封包的私服、Unity / 网页版（无窗口键鼠通路）。

判定的唯一实操方法：先跑 `--sniff-only`——日志里能看到「进程识别 + 服务端锁定 + 帧解析 / 状态刷新」三项，就是能联通。

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
| `Sniff/WindowBinder.cs` | 窗口句柄绑定（只读）：挑"像游戏本体"的顶层窗口、校验已绑定 HWND 是否仍有效（防句柄复用） |
| `Sniff/LoginCredentialProbe.cs` | 从客户端自己的登录流量里解出账号密码（会话密钥 + 上行 CM_IDPASSWORD），仅存内存 |
| `Input/InputSimulator.cs` | SendInput 鼠标键盘、窗口定位（句柄绑定优先）、前台校验、人味抖动 |
| `Input/ScreenMapper.cs` | 世界格 ⇄ 屏幕像素（等距 45° 视图 / 小地图 / 背包 / 对话框） |
| `Input/ActionGate.cs` | 所有点击的唯一入口：串行 + 节流 + 熔断 |
| `Input/UiStateProbe.cs` | UI 场景态机（自由 / 对话 / 换图加载），拦掉非法点击 |
| `Input/MapWalkController.cs` | 移动控制：近距点格 / 远距点小地图，卡死自动换策略 |
| `Input/ClientActionDriver.cs` | 意图 → 具体操作（点击/按键），含动作确认 |
| `Input/ClientInputBridge.cs` | 兜底通道：未接管动作**只计数不发送**，把漏洞变可见 |
| `CmdCatalog.cs` | 反射取 Core 的 CM_/SM_ 命令码，取不到可手工覆盖 |
| `CorePatch.md` | **Core 侧改造说明（必读，改完才能跑）** |
| `BotClient.Core/Human/HumanTiming.cs` | 拟人时序：高斯 / 右偏长尾 / 概率停顿 / 疲劳曲线（随机只做加法，不破服务端下限） |
| `BotClient.Core/Human/HumanMousePath.cs` | 鼠标轨迹：贝塞尔 + smoothstep 加减速 + 随机弯曲 + 概率过冲回修 |
| `BotClient.Core/Human/SkillRotation.cs` | 技能循环：多技能按优先级 + 条件（怪数/距离/MP/冷却）择一释放，挑不出即安全退回 |

---

## 三、前置条件

1. **Npcap**（必须勾 WinPcap 兼容模式）：分发包已随带官方安装器 `npcap\npcap-x.xx.exe`，
   宿主首次运行检测到缺失会**弹出官方交互式安装向导**（免费版 Npcap 拒绝 `/S` 静默安装），
   勾上 WinPcap 兼容模式、装完即自动继续，不必重开；
   也可手动执行 `BotClientDriverHost.exe --install-npcap`。原理与边界见第十一节；
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

#### 4.1.1 窗口绑定：PID + 句柄（多开、改分辨率都不怕）

图形界面的「扫描客户端」列表里选定某一条后，会往 `clientdriver.json` 写回**两个**绑定值：

| 字段 | 作用 | 失效条件 |
| --- | --- | --- |
| `TargetPid` | 钉住选中的客户端进程（多开时区分实例） | 客户端重启 → PID 变化 |
| `TargetHwnd` | 钉住选中的**顶层窗口句柄**（HWND），操作通道优先用它 | 窗口关闭 / 客户端重启 |

定位优先级：`TargetHwnd`（校验通过）→ `TargetPid` → `ProcessName` + `WindowTitleKeyword` → 自动评分挑选。

为什么句柄比"按尺寸/标题匹配"稳：分辨率改了、窗口标题变了、切成全屏，HWND 仍指向同一个窗口；
同名多开时句柄天然分得清。**注意 HWND 会被系统复用**，所以每次使用前都做三重校验
（`IsWindow` + 归属 PID 与绑定时一致 + 仍可见），任一不满足即判定失效，自动清零并退回 PID → 名称匹配，
日志里会打 `[窗口] 绑定的句柄 0x… 已失效…`，重扫一次即可重新绑定。

窗口本身也不是 `.NET` 的 `MainWindowHandle` 猜出来的，而是走 `Sniff/WindowBinder.cs`：
枚举该进程的可见顶层窗口，按"可缩放 / 自绘（几乎无子控件）/ 占屏 ≥50% / 类名像引擎渲染窗"打分挑最像游戏本体的那个
（判据与分辨率无关，和客户端识别同源）。取消绑定用界面上的「取消 PID/句柄锁定」。

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

### 4.5 一次完整挂机流程（照单执行）

**阶段 0 · 一次性准备（只做一次）**

| 步骤 | 动作 | 验收 |
| --- | --- | --- |
| 1 | 抓包驱动：官方 `npcap-x.xx.exe` 放进 exe 同目录即可（首次运行弹出安装向导，也可 `--install-npcap` 手动） | 自检报告出现 `Npcap: 已安装`，且不报抓包初始化失败 |
| 2 | 跑 `CalibrationTool`，量 F1–F8（视图 / 小地图 / 背包 / 对话框） | 逐格验证：走相邻一格，方向与距离正确（第五节） |
| 3 | 按 `CorePatch.md` 完成 Core 3 处改造 + 宿主 15 行接线 | `SelfCheck()` 输出无缺失项 |

**阶段 1 · 每次开挂**

```
①  开官方客户端 → 手动登录 → 进角色 → 走到挂机点站好
②  管理员运行 BotClientDriverHost.exe --sniff-only     ← 首跑先"只看不点"
      日志应出现：客户端进程识别 / 服务端地址锁定 / 帧解析与状态刷新
③  确认无误 → 去掉 --sniff-only 正式启动（零配置自动跟随连接）
④  挂机方式二选一：
      · 定点挂机：--fight-x <格X> --fight-y <格Y>
      · 跟随挂机：不带坐标启动，AI 按当前状态自行索敌
```

**阶段 2 · 运行中自动做什么（宿主内置）**

| 行为 | 触发 / 参数 |
| --- | --- |
| 索敌 → 走近 → 攻击 | `FightRange=12`；走 650 ms / 攻 950 ms（节流由客户端冷却天然承接，**不要再压低**） |
| 自动拾取 | `AutoPickup=true`，`PickupIntervalMs=500`，支持物品过滤 |
| 自动喝药 | HP ≤ 60% / MP ≤ 40% |
| 低血逃跑 | HP ≤ 20% |
| 死亡处理 | 默认暂停等你处理；`AutoReloginOnDeath=true` 可自动重连重进（默认最多 3 次、间隔 60 s） |
| 卡位自解 | 近距点格失败自动切小地图远点点法 |

**阶段 3 · 能力边界（v1 未内置的部分）**

- 动作层已支持 `NpcInteract / DialogSelect / Talk / Turn / DropItem`，`NpcTransferRunner` 也实现了
  「靠近 → 点 NPC → 选菜单 → 等换图 → 等就绪」，但**宿主未提供脚本 / 任务链入口**——
  即"自动跑 NPC 任务、自动换图练级"需要你自己在 Core 侧把 `ScriptEngine` 接进 `BotRuntime` 驱动。
- 开箱即用的是：**定点 / 跟随打怪 + 自动拾取 + 自动喝药 + 低血逃跑 + 死亡恢复**，够日常刷怪升级。

**阶段 4 · 停止**：`Ctrl+C` 退出宿主，内存中的明文凭据随之释放。


---

### 4.6 拟真操作（默认开，配置在 botsettings.json）

所有点击/按键最终都落到 `Input/InputSimulator.cs`（唯一 SendInput 调用方），拟真就挂在这条链路上：

- **鼠标轨迹**：`HumanMousePath.Build` 逐点 `SendInput` 移动（6–18 步、含曲线与概率过冲回修），
  不再是"瞬移 + 按一下"；移动到位后有一次 18–40ms 的落点确认停顿。
- **按压时长 / 偶发停顿**：`HumanTiming.HoldMs`（高斯 + 疲劳放大）与 `MaybePause`（概率长停顿）。
- **战斗与走路节拍**：`BotCombatAI` 在**服务端限速下限之上**叠加右偏抖动（`CombatJitterRatio` / `WalkJitterRatio`）。
- **技能循环**：`SkillRotation.PickSkill` 按优先级 + 条件选技能；选不出（冷却/MP/射程/未学会）退回原物理或单法术。
- **一键对照**：`--no-human` 本次退回固定时序、`--skills` 本次强制开技能循环（都不改配置文件）。

完整字段说明、示例配置与离线自测见仓库根 [`README.md`](../README.md) 的「拟真操作」一节。

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

---

## 十、跨服适配清单（每服的 M2 与网关都可能不一样）

**核心结论：操作层通用，状态层每服适配一次。** 差异按层拆开后，改动点收敛到两个文件。

| 层 | 内容 | 各服差异 | 改动位置 |
| --- | --- | --- | --- |
| L1 传输 | 明文 TCP、三网关分端口 | 多数一致；少数合流或改端口 | `clientdriver.json` 端口留 0 自动判定 |
| L2 帧定界 | RunGate 头 `DD CC BB AA`（小端）+ 24 字节头；登录 22 字符密钥包 | **常被改**：长度前缀、自定义魔数、结构体自行扩展 | `clientdriver.json` 的 `framing` 段（跑一次 `--autoframe` 自动写回，**不改代码**）；探测逻辑在 `Sniff/Framing.cs` + `Sniff/SplitterAutoDetector.cs` |
| L3 命令码 | CM_ / SM_ 编号（Grobal2） | **几乎每服 / 每引擎都不同**，同引擎不同版本也会变 | `clientdriver.json` 的 `cmdOverrides` 手填（key 用 `Grobal2` 常量名），已持久化 |
| L4 包体结构 | 字段偏移（Delphi record 对齐） | 可能不同 | Core 的 `MirPacketDecoder`（本模块只透传） |
| L5 加密链 | 上行明文 EdCode、登录 DES 变体 | 引擎同源一致；被二次加密即失效 | `LoginCredentialProbe`（失败退 `--login`） |
| L6 视觉操作 | 键鼠 + 坐标换算 | **与协议无关，换服不改** | 校准常量 |

**新服适配五步**

1. 抓样本：登录 → 走一步 → 打一下 → 捡一件，覆盖三网关；
2. **自动定界**：客户端登录进游戏后跑一次 `BotClientDriverHost.exe --autoframe`（只读、零点击）。
   探测器会枚举魔数/长度字段/头长并打分，命中就写回 `clientdriver.json` 的 `framing` 段，**不用改代码重编译**；
   报告里会给出候选表与结论，可据此人工复核。找不到可信定界时看报告：
   - 高熵 + 无自洽候选 → 该服下行被加密/压缩，定界自动化救不了，需先拿到解密链；
   - 中低熵但无候选 → 加长采样（多走几步/多打几下）重跑，或按报告手工填 `framing` 段
     （`magic` / `lenOffset` / `lenSize` / `bigEndian` / `headerSize`）；经典文本帧服会自动判为 `ClassicMir`；
3. 对命令码：启动看 `CmdCatalog.Missing`，按抓包逐条补 `clientdriver.json` 的 `cmdOverrides`。下行至少要有
   **坐标移动 / HP・MP / 对象出现 / 地图切换**，上行至少要有 **动作返回 / 心跳**；
4. 验凭据：能解出账号密码就走零配置；解不出（加密链被改）用 `--login` 手填，不影响挂机；
5. 校准一次 → 先 `--sniff-only` → 再开点击。

**换服自测（离线，不用连游戏）**

```powershell
dotnet run --project tools/FramingSelfTest/FramingSelfTest.csproj
```

5 个用例：经典 RunGate 魔数 / **换服变体魔数（证明换服免改码）** / 经典文本帧 / 高熵随机流如实否决 /
跨 TCP 分段的帧重组一致性。CI 每次 push 都会跑，全绿才出 exe。

**值不值得适配的判断**

- 客户端能被键鼠驱动 + 下行能解出坐标与 HP / 目标 → 能挂；
- 服务端对整条流做了压缩或强加密（嗅探看到高熵乱码）→ 状态层成本极高，唯一出路是"纯视觉挂机"
  （读画面而非读包，**目前未实现**）。

**当前已知不足（如实）**

- 自动定界只解决 **L2（帧定界）**；命令码 **L3** 仍需按服填 `cmdOverrides`（已持久化进 `clientdriver.json`，
  换服改 json 即可）；上行加密链被改过的服仍要 `--login` 手填账号；
- 适配只需打通该服的关键 `SM_` / `CM_`，其余命令码记入 `Missing` 属正常，不影响挂机。

---

## 十一、Npcap：为什么不能内置，以及"免手动装"怎么落地

### 11.1 硬约束（绕不过去的三条）

| 事实 | 后果 |
| --- | --- |
| Npcap 是**内核驱动**（`npcap.sys` + NPF 服务）+ 用户态 `wpcap.dll` / `Packet.dll` | 驱动必须注册进系统、用户态按名字加载，**无法像普通 DLL 那样内嵌进单文件 exe** |
| 驱动安装需要管理员 + 驱动签名校验 | 至少要过一次 UAC，做不到严格意义的"零安装" |
| Npcap 免费版许可 | 自用 / 随包原样分发可以；作为商业产品 OEM 再分发需向 Nmap Project 取得授权 |

### 11.2 落地形态：随包安装器 + 首次运行自动补装

```
BotClientDriverHost.exe
clientdriver.json / botsettings.json
npcap\
  npcap-1.xx.exe        ← 官方安装器原样附带
```

宿主抓包前的行为：

1. **探测**：查 `System32\Npcap\wpcap.dll`（Npcap 默认布局）或 `System32\wpcap.dll`（WinPcap 兼容模式），
   32 位视图 `SysWOW64` 一并认，避免"装了但位数不匹配"被误判；
2. **已装** → 直接抓包，日志 `抓包环境: Npcap 已就绪`；
3. **未装** → 在 exe 同目录 / `npcap\` / `redist\` / `tools\` 里找 `npcap-*.exe`；
4. **找到** → 拉起**官方交互式安装向导**（免费版 Npcap 拒绝 `/S` 静默安装：会弹框
   "Silent installation is only supported in Npcap OEM" 后退出——旧版本宿主在真机上就是因此"闪退"）；
   向导里勾上 `WinPcap API-compatible Mode`，装完复检 `wpcap.dll`，成功则**本进程直接继续**；
5. **没找到或装失败** → 打印可照做的提示（官网地址、该放哪、`--install-npcap` 用法），退出码 3，
   不再把 `DllNotFoundException` 直接甩给用户。

为什么"闸门探测"装完能直接继续、而"抓包失败后的兜底补装"仍建议重开一次：闸门探测发生在首次抓包**之前**，
此时还没碰过 SharpPcap 的静态初始化，装完可直接用；一旦抓包已经失败过，.NET 对静态构造失败的缓存会让同进程
重试不可靠，所以兜底路径装完会提示重开。

### 11.3 相关命令行

| 参数 | 作用 |
| --- | --- |
| （默认） | 抓包前检测到 Npcap 缺失就拉起安装向导补装 |
| `--install-npcap [安装器路径]` | 只装 / 修 Npcap 后退出；路径可省（自动用随包安装器） |
| `--no-auto-install` | 关闭自动补装（想自己管控驱动时用） |

自检报告新增一行：`Npcap: 已安装 / 未安装（随包安装器: npcap-x.xx.exe）`。

### 11.4 CI 行为

`build-win-x64` 在发布完成后尝试从 `https://npcap.com/dist/` 拉取最新官方安装器放进
`publish/npcap/`（`continue-on-error`：拉不到不阻塞出包，宿主降级为提示用户手动放置）。
冒烟新增用例：`--install-npcap` 指向不存在的安装器时必须友好失败（不得抛未处理异常）。
