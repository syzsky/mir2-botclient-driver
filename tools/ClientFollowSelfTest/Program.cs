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
