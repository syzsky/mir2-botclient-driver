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
