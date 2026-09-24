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
win-x64 自包含单文件，产物以 artifact 形式提供。

## 本地运行前提

1. **管理员权限**（抓包与模拟输入都需要；manifest 已声明 `requireAdministrator`）
2. **Npcap** 已安装，安装时勾选 *WinPcap API-compatible Mode*
3. 游戏客户端已启动并**进入游戏**（本程序不改客户端任何文件）
4. 首次运行先校准：`BotClientDriverHost.exe --calibrate`

## 用法

```powershell
# 先只验证抓包与状态镜像，不产生任何点击
$env:BOT_PASSWORD = "你的密码"
.\BotClientDriverHost.exe --account user01 --server 一区 --character 测试道 --sniff-only

# 确认链路正常后正式挂机（定点打怪）
.\BotClientDriverHost.exe --account user01 --server 一区 --character 测试道 --fight-x 330 --fight-y 330
```

## 免责

仅供**自建/授权测试服**的技术研究与自动化实验使用。请勿用于商业运营服务器；使用前请自行确认游戏服务条款与当地法律法规，因使用产生的任何后果由使用者承担。
