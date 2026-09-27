// 「识别客户端 / 等待客户端 / 自动跟上」离线自测。
// 目标：把客户投诉的那条死循环钉死在测试里 ——
//   "获取客户端时必须先点开始挂机，客户端都没连接怎么挂机"。
// 全部为纯逻辑断言 + 一次源码护栏，不连客户端、不抓包、不碰 Windows API。

using System.Text;
using BotClient.Core.Host;

int pass = 0, fail = 0;
var sb = new StringBuilder();

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { pass++; Console.WriteLine($"  [PASS] {name}"); }
    else { fail++; Console.WriteLine($"  [FAIL] {name}{(detail == null ? "" : "  <- " + detail)}"); }
}

void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine("== " + title);
}

Console.WriteLine("ClientFollowSelfTest —— 客户端识别/跟随状态决策（离线，不依赖客户端与网络）");

// ---------------------------------------------------------------- 1. 核心投诉：未挂机也要能识别
Section("1. 「识别客户端」不再要求先点开始挂机（客户投诉的死循环）");

Check("未挂机(host 未建) → 动作必须是只读识别 IdentifyOffline",
    ClientFollowPolicy.DecideRefresh(hostBound: false) == ClientRefreshAction.IdentifyOffline,
    "实际=" + ClientFollowPolicy.DecideRefresh(hostBound: false));

Check("已挂机(host 已建) → 动作是覆盖式重识别 Reconfigure",
    ClientFollowPolicy.DecideRefresh(hostBound: true) == ClientRefreshAction.Reconfigure);

Check("识别动作与「是否已在挂机」无因果：两种状态都能拿到可执行动作（无拒绝分支）",
    Enum.IsDefined(ClientFollowPolicy.DecideRefresh(false)) && Enum.IsDefined(ClientFollowPolicy.DecideRefresh(true)));

// ---------------------------------------------------------------- 2. 启动后的等待状态
Section("2. 没识别到目标 → 进入「等待客户端」，不假装在挂机");

Check("识别到候选 → Bound", ClientFollowPolicy.DecideAfterStart(identified: true, hasHostFallback: false) == ClientBindState.Bound);
Check("未识别到候选、无 Host 兜底 → WaitingClient",
    ClientFollowPolicy.DecideAfterStart(identified: false, hasHostFallback: false) == ClientBindState.WaitingClient);
Check("未识别到候选、有 Host 兜底 → WaitingClient（仍是等待，不是 Bound）",
    ClientFollowPolicy.DecideAfterStart(identified: false, hasHostFallback: true) == ClientBindState.WaitingClient);

// ---------------------------------------------------------------- 3. 等待中的只读重试节流
Section("3. 等待中每约 5 秒只读重试一次（节流正确）");

bool anyEarly = false;
for (int t = 1; t < ClientFollowPolicy.RetryEveryTicks; t++)
    if (ClientFollowPolicy.ShouldRetryFollow(true, true, t)) anyEarly = true;
Check($"tick 1..{ClientFollowPolicy.RetryEveryTicks - 1} 都不触发（不每帧全量扫进程）", !anyEarly);
Check($"tick {ClientFollowPolicy.RetryEveryTicks} 触发", ClientFollowPolicy.ShouldRetryFollow(true, true, ClientFollowPolicy.RetryEveryTicks));
Check($"tick {ClientFollowPolicy.RetryEveryTicks * 2} 触发", ClientFollowPolicy.ShouldRetryFollow(true, true, ClientFollowPolicy.RetryEveryTicks * 2));
Check("非等待态不重试", !ClientFollowPolicy.ShouldRetryFollow(waiting: false, running: true, tick: 10));
Check("未挂机不重试", !ClientFollowPolicy.ShouldRetryFollow(waiting: true, running: false, tick: 10));
Check("tick=0（未开始计数）不触发", !ClientFollowPolicy.ShouldRetryFollow(true, true, 0));

Check("重试扫到候选 → 自动跟上", ClientFollowPolicy.ShouldAdoptOnRetry(1) && ClientFollowPolicy.ShouldAdoptOnRetry(3));
Check("重试没扫到候选 → 继续等（不误判、不抛错）", !ClientFollowPolicy.ShouldAdoptOnRetry(0));

// ---------------------------------------------------------------- 4. 界面可见性
Section("4. 界面能一眼看出「到底连上客户端没有」");

Check("已绑定 PID → 芯片 pid=1234", ClientFollowPolicy.ChipText(1234, 0, false) == "pid=1234");
Check("只绑句柄 → 芯片显示句柄", ClientFollowPolicy.ChipText(0, 0x1A2B, false) == "句柄 0x1A2B");
Check("等待客户端 → 芯片「等待连接…」", ClientFollowPolicy.ChipText(0, 0, true) == "等待连接…");
Check("既未绑定也不等待 → 芯片「未绑定」", ClientFollowPolicy.ChipText(0, 0, false) == "未绑定");

string descWait = ClientFollowPolicy.DescribeTarget("mir2", 0, 0, waiting: true);
Check("等待中未绑定：说明每约 5 秒只读重试、登录后自动跟上",
    descWait.Contains("每约 5 秒只读重试") && descWait.Contains("自动跟上"), descWait);
string descIdle = ClientFollowPolicy.DescribeTarget("mir2", 0, 0, waiting: false);
Check("未绑定且未等待：提示启动时自动挑候选", descIdle.Contains("自动挑选"), descIdle);
string descBound = ClientFollowPolicy.DescribeTarget("mir2", 4321, 0x2B, waiting: false);
Check("已绑定：给出进程名/pid/句柄", descBound.Contains("mir2") && descBound.Contains("pid=4321") && descBound.Contains("0x2B"), descBound);
Check("已绑定但仍在等该客户端登录 → 有明确标注",
    ClientFollowPolicy.DescribeTarget("mir2", 4321, 0x2B, waiting: true).Contains("等待该客户端登录"));

Check("运行状态：未启动就是「未启动」，不挂等待标注",
    ClientFollowPolicy.RunningStateNote(false, false, true) == "未启动");
string noteWait = ClientFollowPolicy.RunningStateNote(true, false, true);
Check("运行中 + 等待客户端：明确「尚未产生任何动作」（不假装在挂机）",
    noteWait.Contains("等待客户端连接") && noteWait.Contains("尚未产生任何动作"), noteWait);
Check("运行中 + 仅嗅探：标注为「仅嗅探」", ClientFollowPolicy.RunningStateNote(true, true, false) == "仅嗅探");
Check("运行中正常态：标注为「挂机中」", ClientFollowPolicy.RunningStateNote(true, false, false) == "挂机中");

// ---------------------------------------------------------------- 5. 源码护栏：被测策略就是宿主真实调用的
Section("5. 源码护栏：宿主确实走这份策略，且识别路径里没有「先点开始挂机」");

string? repoRoot = FindRepoRoot(AppContext.BaseDirectory);
if (repoRoot == null)
{
    Console.WriteLine("  [SKIP] 未定位到仓库根目录（脱离仓库运行），跳过源码护栏");
}
else
{
    string hostRunner = Path.Combine(repoRoot, "BotClientDriverHostUi", "Host", "HostRunner.cs");
    if (!File.Exists(hostRunner))
    {
        Check("找到 HostRunner.cs", false, hostRunner);
    }
    else
    {
        Check("找到 HostRunner.cs", true);
        string src = File.ReadAllText(hostRunner);

        Check("宿主调用了 DecideRefresh（识别入口决策走被测策略）", src.Contains("ClientFollowPolicy.DecideRefresh("));
        Check("宿主调用了 ShouldRetryFollow（重试节流走被测策略）", src.Contains("ClientFollowPolicy.ShouldRetryFollow("));
        Check("宿主调用了 ChipText / DescribeTarget（界面可见性走被测策略）",
            src.Contains("ClientFollowPolicy.ChipText(") && src.Contains("ClientFollowPolicy.DescribeTarget("));

        string? body = ExtractMethodBody(src, "public void ReDiscover()");
        if (body == null)
        {
            Check("提取 ReDiscover 方法体", false);
        }
        else
        {
            Check("提取 ReDiscover 方法体", true);
            bool hasOldGate = body.Contains("先点") || body.Contains("尚未启动");
            Check("ReDiscover 方法体内不再有「先点开始挂机 / 尚未启动」这类拒绝分支",
                !hasOldGate, hasOldGate ? "仍存在旧门槛文案" : null);
            Check("ReDiscover 未挂机时走 IdentifyClientOffline（只读识别）",
                body.Contains("IdentifyClientOffline()"));
        }
    }
}

// ---------------------------------------------------------------- 6. 源码护栏：密码应答与掉线提示确实被接上
//
// 这两条都属于"写好了但没人订阅"的类型，而且**编译器不会报警**（private 方法未被调用不产生警告），
// 所以必须用护栏钉住 —— 它们各自对应一个会让挂机静默失效的后果：
//   · 不应答 SM_PASSWORD → 服务端把 m_boCanWalk/Hit/Spell/UseItem 全置 False，所有动作整包丢弃，
//     界面仍显示"挂机中"、角色却一动不动；
//   · 不订阅 Disconnected → 掉线后界面停在最后一帧，用户以为还在挂机。
Section("6. 源码护栏：密码应答 / 掉线提示已接线");

{
    string? root = FindRepoRoot(AppContext.BaseDirectory);
    if (root is null)
    {
        Console.WriteLine("  [SKIP] 未定位到仓库根目录（脱离仓库运行），跳过源码护栏");
    }
    else
    {
        string cliPath = Path.Combine(root, "BotClientDriverHost", "Program.cs");
        string uiPath = Path.Combine(root, "BotClientDriverHostUi", "Host", "HostRunner.cs");

        Check("找到无界面宿主 Program.cs", File.Exists(cliPath), cliPath);
        Check("找到图形宿主 HostRunner.cs", File.Exists(uiPath), uiPath);

        if (File.Exists(cliPath))
        {
            string cli = File.ReadAllText(cliPath);
            Check("无界面宿主订阅了 PasswordRequested", cli.Contains("PasswordRequested +="));
            Check("无界面宿主订阅了 Disconnected", cli.Contains("Disconnected +="));
            Check("无界面宿主实现了密码应答（密码取自 --password / BOT_PASSWORD）",
                cli.Contains("AnswerPasswordAsync") && cli.Contains("BOT_PASSWORD"));
            // 复用模式与登录模式是两条独立入口，各自都要接
            int hooks = cli.Split("PasswordRequested +=").Length - 1;
            Check("两个入口（复用模式 / 登录模式）都接了密码应答", hooks >= 2, $"实际 {hooks} 处");
        }

        if (File.Exists(uiPath))
        {
            string ui = File.ReadAllText(uiPath);
            Check("图形宿主订阅了 PasswordRequested", ui.Contains("PasswordRequested +="));
            Check("图形宿主订阅了 Disconnected", ui.Contains("Disconnected +="));
            Check("图形宿主有密码输入弹窗", ui.Contains("PasswordPromptWindow"));

            string dlg = Path.Combine(root, "BotClientDriverHostUi", "Views", "PasswordPromptWindow.xaml");
            Check("密码弹窗的 XAML 存在", File.Exists(dlg), dlg);
        }

        // "配置项被真正读取" —— 这一批是审查时逐个核对 HostSettings 属性引用数发现的：
        // VerbosePacketLog / LanHost / AutoRelogin* / CharDataDir 在 HostSettings 之外零引用，
        // 也就是界面上的开关点了没反应。
        string hsPath = Path.Combine(root, "BotClientDriverHost", "HostSettings.cs");
        Check("找到 HostSettings.cs", File.Exists(hsPath), hsPath);
        if (File.Exists(hsPath))
        {
            string hs = File.ReadAllText(hsPath);
            Check("逐包报文日志开关真的被应用（BotLog.VerbosePackets 有赋值点）",
                hs.Contains("BotLog.VerbosePackets = VerbosePacketLog"));
            Check("未实现的开关会给出启动警告（UnimplementedOptions）",
                hs.Contains("UnimplementedOptions"));
            Check("自动重连在字段注释里明确标注了未实现",
                hs.Contains("当前尚未实现") && hs.Contains("AutoReloginOnDeath"));
        }

        if (File.Exists(cliPath))
        {
            string cli = File.ReadAllText(cliPath);
            Check("无界面宿主打印了未实现开关的警告", cli.Contains("UnimplementedOptions()"));
        }
        if (File.Exists(uiPath))
        {
            string ui = File.ReadAllText(uiPath);
            Check("图形宿主打印了未实现开关的警告", ui.Contains("UnimplementedOptions()"));
        }
    }
}

Console.WriteLine();
Console.WriteLine($"—— ClientFollowSelfTest 结果: {pass} 通过 / {fail} 失败 ——");
if (fail > 0)
{
    Console.WriteLine("存在失败断言：识别/跟随链路不可发布。");
    return 1;
}
Console.WriteLine("全部断言通过：未挂机也能识别客户端；未识别到进入「等待客户端」并自动跟上；界面状态可见。");
return 0;

// ------------------------------------------------------------------ helpers

static string? FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    for (int i = 0; i < 12 && dir != null; i++, dir = dir!.Parent)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "BotClientDriverHostUi"))) return dir.FullName;
    }
    return null;
}

static string? ExtractMethodBody(string src, string signature)
{
    int at = src.IndexOf(signature, StringComparison.Ordinal);
    if (at < 0) return null;
    int open = src.IndexOf('{', at);
    if (open < 0) return null;
    int depth = 0;
    for (int i = open; i < src.Length; i++)
    {
        if (src[i] == '{') depth++;
        else if (src[i] == '}')
        {
            depth--;
            if (depth == 0) return src.Substring(open, i - open + 1);
        }
    }
    return null;
}
