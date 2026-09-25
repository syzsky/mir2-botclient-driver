# BotClient · 客户端驱动挂机（C 方案）宿主

传奇2（Mir2 系）客户端的**只读嗅探 + 真机操作**挂机模块。

## 这是什么

| 方案 | 上行包谁来发 | 特征 |
|------|--------------|------|
| A 直连模拟 | 本程序自己造 socket | 服务端容易识别，登录/协议改动会失效 |
| B 内存注入 | 改客户端进程内存 | 侵入客户端，易被反外挂检测 |
| **C 客户端驱动（本项目）** | **真机客户端自己发** | 本程序只读嗅探（Npcap）+ 模拟人类点击 |

数据流（全部单向、无回写）：

```
真机客户端 ⇄ 游戏服务端          ← 唯一的真实连接，本模块从不介入
     │  (Npcap 只读镜像)
     ├─ 下行字节 → TcpReassembler → MirFrameCodec → BotRuntime（状态镜像）
     └─ 上行字节 → UpstreamCommandProbe（动作确认）

BotRuntime（原样不动）→ IClientDriver → 鼠标/键盘 → 真机客户端 → 上行
```

## 工程结构

| 工程 | 说明 |
|------|------|
| `BotClient.Core` | 协议解析、登录流程、状态运行时（`BotRuntime`）、战斗 AI；已打入 C 方案分流补丁（见 `Net/ClientDriven.cs`） |
| `BotClient.ClientDriver` | 嗅探 / 规则化点击 / 进图判定（`MapEntryProbe`）/ 校准工具 |
| `BotClientDriverHost` | 无界面宿主：自动登录 → 接管动作通道 → 起 AI |

## 云端打包

`.github/workflows/build-win-x64.yml`：在 GitHub Actions（windows-latest）上还原 → 编译 → 发布
win-x64 自包含单文件，产物以 artifact 形式提供。工作流还会从 `npcap.com/dist/` 抓一份官方
Npcap 安装器放进 `publish/npcap/`（拉取失败不阻塞出包），使 artifact 解压即用。

**也可以本地直接出 exe**（Linux/macOS 交叉编译同样可行）：

```bash
dotnet publish BotClientDriverHost/BotClientDriverHost.csproj -r win-x64 -c Release \
  -p:EnableWindowsTargeting=true -o publish
# → publish/BotClientDriverHost.exe（约 35 MB，单文件自包含）
```

## 本地运行前提

1. **管理员权限**（抓包与模拟输入都需要；manifest 已声明 `requireAdministrator`）
2. **Npcap**：分发包随带官方安装器（`npcap\npcap-x.xx.exe`）。首次运行检测到未安装时，
   宿主会**自动静默补装**（`/S /winpcap_mode=yes`）并提示重跑一次；也可手动
   `BotClientDriverHost.exe --install-npcap`。安装必须带 WinPcap 兼容模式
   （Npcap 是内核驱动，无法内嵌进单文件 exe，详见 `BotClient.ClientDriver/README.md` 第十一节）
3. 游戏客户端已启动并**进入游戏**（本程序不改客户端任何文件）
4. 首次运行先校准：`BotClientDriverHost.exe --calibrate`

## 用法

### 推荐：零配置复用模式（默认）

**账号、密码、服务端地址、客户端进程名全部自动获取**，你只需要打开客户端登录、创建角色：

```powershell
# 什么都不用填，直接以管理员身份运行
.\BotClientDriverHost.exe

# 想先只验证链路（不产生任何点击）
.\BotClientDriverHost.exe --sniff-only
```

宿主会自动认出客户端进程、跟随它对服务端的真实连接，并从客户端**自己发出的登录包**里
还原账号密码（**只解不注入、仅存内存、不落盘**）。详见
[`BotClient.ClientDriver/README.md`](BotClient.ClientDriver/README.md) 第 4.1 节。

### 可选：宿主自己登录（需显式指定账号）

```powershell
# 先只验证抓包与状态镜像，不产生任何点击
$env:BOT_PASSWORD = "你的密码"
.\BotClientDriverHost.exe --account user01 --server 一区 --character 测试道 --sniff-only

# 确认链路正常后正式挂机（定点打怪）
.\BotClientDriverHost.exe --account user01 --server 一区 --character 测试道 --fight-x 330 --fight-y 330
```

### 挂机任务：只给地图名，自动进图 + 逐层下探

定点打怪只能打"当前站的这一格"，换图要人手动传。挂机任务把整条链交给程序：
**在当前地图找传送员 NPC → 点开菜单按地图名点进去 → 清怪 → 有下层就一路往下钻**。

```powershell
# 20 级去僵尸洞挂机（最常用形态：只给地图名和等级门槛）
.\BotClientDriverHost.exe --hunt 僵尸洞 --hunt-level 20

# 只想跑一轮看看流程（不循环）
.\BotClientDriverHost.exe --hunt 僵尸洞 --hunt-level 20 --hunt-once
```

**换地图只改一个词**，流程不用动：

```powershell
.\BotClientDriverHost.exe --hunt 祖玛寺庙 --hunt-level 35
.\BotClientDriverHost.exe --hunt 猪洞     --hunt-level 25 --hunt-depth 5
```

| 参数 | 默认 | 作用 |
| --- | --- | --- |
| `--hunt <地图名>` | 不启用 | 目标地图中文名（也接受地图代码）。**给了它才启用挂机任务** |
| `--hunt-npc <关键词>` | `传送` | 传送员 NPC 的匹配关键词。跨服叫法不同（如"洞穴向导"）就改这里 |
| `--hunt-menu <路径>` | 空 | 上级菜单路径，逗号分隔（如 `传送,白日门`）。留空=点开 NPC 后当前层直接是地图名 |
| `--hunt-level <等级>` | `0` | 进图最低等级。不够级**一次点击都不做**，只在日志说明原因 |
| `--hunt-depth <层数>` | `3` | 最多下探几层（含第一层），防止传送员互链无限下钻 |
| `--hunt-clear <秒>` | `300` | 每层清怪时长上限。视野内**持续 8s 无怪**即判清完，不必等满时限 |
| `--hunt-no-descend` | 关 | 只打当前层，不下去 |
| `--hunt-once` | 关 | 只跑一轮；默认循环，掉线回城后**下一轮自己走回去** |

运行时会看到每层一行战报，结束时给出本轮汇总：

```
[任务] ===== 第 1 轮开始 =====
[任务] 当前在 比奇省(D1001)，准备进 僵尸洞
[任务] 第1层 僵尸洞(D1101) 击杀 37 只，耗时 96s
[任务] 发现下层入口 "下一层"，尝试下探
[任务] 第2层 僵尸洞二层(D1102) 击杀 41 只，耗时 118s｜结束原因：已达下探层数上限 3
[任务] 第 1 轮结束：本轮合计 2 层，击杀 78 只，最深 僵尸洞二层(D1102)
```

两条实用说明：

- **点不中不会点错**：进图统一走 `MapEntryProbe`（菜单文本 → 反查 `MapInfo.txt` 地图代码 → 换图后校验
  到达的是不是目标图）。所以跨服菜单文字不一致时表现是"进不去并说明原因"，而不是"进了别的图还以为成功"。
- **卡住先看日志**：找不到 NPC 会把**当前视野所有 NPC 名字打出来**，照着改 `--hunt-npc` 即可；
  菜单里没有下层入口也会把**整页菜单项打出来**，照着配 `--hunt-menu`。

### 拟真操作：默认全开（像真人一样点、按、打）

挂机动作全部走"真机客户端的鼠标/键盘"，所以**操作像不像人**直接决定被不被认出来。默认档：

| 维度 | 默认行为 | 关掉它的代价 |
|------|----------|--------------|
| 鼠标移动 | 贝塞尔轨迹 + smoothstep 加减速 + 随机弯曲 + 概率过冲回修，分 6–18 步送达 | 瞬移式点击：系统里只有"从 A 跳到 B"一条记录，是最显眼的机器特征 |
| 点击/按键时长 | 按下时长走高斯抖动（不是固定 50ms），偶发长停顿 | 等距脉冲，可被统计出上界 |
| 战斗节奏 | 攻击/施法间隔在**服务端限速下限之上**叠加右偏长尾抖动（默认最多 +25%） | 每刀间隔一模一样 |
| 走路节奏 | 点地图格的间隔在下限之上叠加抖动（默认最多 +20%） | "每 400ms 准时一格"的赶路 |
| 空场发呆 | 视野无怪时概率性原地愣一下再巡逻 | 反复机械挪位置 |
| 疲劳 | 连续运行越久，停顿概率与时长越大（默认 3h 到顶 1.6×） | 挂 10 小时的节奏与刚上线时完全一致 |
| 技能循环 | 多技能按**优先级 + 条件**选用（怪数 / 距离 / 自身MP / 冷却） | 只会用一个 `MagicId` 死磕，法师不会按场面切技能 |

**硬约束（重要）**：所有随机都只做"加法"，永远叠在服务端限速下限之上 —— 压到阈值以下会被服务端
整包静默丢弃（那不是像人，是白丢输出）。技能循环挑不出技能时（冷却没到 / MP 不够 / 超射程 / 未学会），
一律退回原来的物理或单法术行为，**不会让原本能打的号变哑**。

配置在 `botsettings.json`（宿主启动时会把它灌进驱动配置，**只改这一处即可**）：

```json
{
  "Human": { "Enabled": true, "CombatJitterRatio": 0.25, "WalkJitterRatio": 0.20, "PauseChance": 0.06 },
  "Skills": {
    "Enabled": true,
    "Slots": [
      { "Name": "火墙",     "SpellSlot": 3, "Priority": 30, "MinMonsters": 3, "MaxDistance": 6, "MinSelfMpPercent": 30, "GroundCast": true, "GroundOffsetY": -1, "ExtraCooldownMs": 2500 },
      { "Name": "冰咆哮",   "SpellSlot": 2, "Priority": 20, "MinMonsters": 2, "MaxDistance": 7, "MinSelfMpPercent": 20, "ExtraCooldownMs": 800 },
      { "Name": "灵魂火符", "SpellSlot": 0, "Priority": 10, "MinMonsters": 1, "MaxDistance": 8, "MinSelfMpPercent": 5 }
    ]
  }
}
```

- 技能名按**包含匹配**服务端下发的技能名（`火墙` 能匹配到 `火墙(Lv3)`）；没学会的技能自动跳过。
  `MagicId` 也可以直接填，填了就不看名字；`SpellSlot` 是法术槽下标（0 = F1），填 `-1` 交给驱动层兜底。
- 服务端 `Magic.DB` 的 `Delay` 会被自动取来当冷却下限（从 `SM_SENDMYMAGIC` 拿），
  所以 `ExtraCooldownMs` 只用来给"服务端不管、但你不想连放"的技能额外限流。
- 排障用命令行覆盖，**不改配置文件**：
  ```powershell
  .\BotClientDriverHost.exe --no-human    # 本次退回固定时序（对照用）
  .\BotClientDriverHost.exe --skills      # 本次强制开技能循环（没写技能表就用内置示例表）
  ```
- 启动日志与自检报告里会直接打出生效档位（`[拟真] 拟人化 开[...]`、`技能循环 开 → 火墙 > 冰咆哮 > 灵魂火符`），
  改完配置不用靠猜。

离线验证：`dotnet run --project tools/HumanSelfTest/HumanSelfTest.csproj`
（30 个用例：拟人时序恒不破下限 / 呈右偏 / 鼠标轨迹终点精确落在目标格 / 技能循环条件与冷却 /
未学会静默跳过 / 关掉拟人一键回退；CI 每次 push 都会跑）。

### 换服：一条命令重定界（不需要改代码）

每个服的 M2 与网关协议都可能不一样，差异按层拆开：**L2 帧定界**和 **L3 命令码**是最常被改的两层。
本项目把 L2 定界做成了"参数化描述 + 自动探测"，所以换客户端、换服**不用改代码、不用重编译**：

```powershell
# 客户端已登录进游戏、在线状态（管理员运行）
.\BotClientDriverHost.exe --autoframe
```

- 探测期**只读采样 15 秒、零点击、零发包**（期间在游戏里走一步、打一下，样本更全）；
- 探测器对同一段下行流量并行枚举"魔数 × 长度字段位置/位宽/字节序 × 头长"，按三条判据打分：
  切出的帧能否**首尾自洽**连成链、覆盖率、帧内命令码命中已知表的比例；
- 命中即把定界档写回 `clientdriver.json` 的 `framing` 段，**下次启动自动生效**；换回原来的服时
  把该段删掉即回到默认经典定界；
- 整流高熵（熵 ≥ 7.6 bits/byte）且无候选自洽 → 说明该服下行做了加密/压缩，探测器**如实否决**，
  不会瞎猜一个定界让你后面查半天；
- 命令码（L3）差异属于语义层，自动定界不解决：跑起来看 `CmdCatalog.Missing`，按抓包填
  `clientdriver.json` 的 `cmdOverrides` 即可（`{"SM_POSITIONMOVE": 1234}` 这种形式，key 用 `Grobal2` 里的常量名）。

离线验证：`dotnet run --project tools/FramingSelfTest/FramingSelfTest.csproj`
（5 个用例：经典魔数 / **换服变体魔数** / 经典文本帧 / 高熵随机流如实否决 / 跨 TCP 段重组；
CI 每次 push 都会跑，全绿才出 exe）。

### 抓包驱动：随包安装器 + 首次运行自动补装

Npcap 是内核驱动 + 用户态 DLL，**无法内嵌进单文件 exe**，所以做法是"随包带官方安装器、
宿主自己补装"，用户侧体感等同于内置：

```
解压后：BotClientDriverHost.exe + clientdriver.json + npcap\npcap-x.xx.exe
首次运行（管理员）：
  未装 Npcap → 自动静默安装（/S /winpcap_mode=yes）→ 提示"重新运行本程序" → 再跑即开挂
  已装 Npcap → 直接进入挂机，无需任何手动步骤
```

| 参数 | 作用 |
| --- | --- |
| （默认） | 抓包前检测缺失就自动静默补装 |
| `--install-npcap [安装器]` | 只装 / 修 Npcap 后退出（路径可省，自动用随包安装器） |
| `--no-auto-install` | 关闭自动补装 |

原理、边界与许可注意事项见 [`BotClient.ClientDriver/README.md`](BotClient.ClientDriver/README.md) 第十一节。

## 免责

仅供**自建/授权测试服**的技术研究与自动化实验使用。请勿用于商业运营服务器；使用前请自行确认游戏服务条款与当地法律法规，因使用产生的任何后果由使用者承担。
