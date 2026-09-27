using System;

namespace BotClient.Core.Host;

/// <summary>「重新识别客户端」按钮在当前宿主状态下应该做什么。</summary>
public enum ClientRefreshAction
{
    /// <summary>宿主还没启动（未挂机）：纯只读识别 + 绑定，不要求先点开始挂机。</summary>
    IdentifyOffline,

    /// <summary>宿主已启动：按运行中的目标重新扫描并覆盖配置。</summary>
    Reconfigure,
}

/// <summary>客户端绑定状态（界面据此显示「客户端」芯片与状态摘要）。</summary>
public enum ClientBindState
{
    Unbound,
    WaitingClient,
    Bound,
}

/// <summary>
/// 「识别客户端 / 等待客户端 / 自动跟上」的状态决策 —— 纯逻辑，不碰 Windows API、不碰网络。
/// 之所以单独抽出来：这段决策就是客户投诉的那个死循环本身
/// （"获取客户端时必须先点开始挂机，客户端都没连接怎么挂机"），必须能被离线自测直接验证。
/// 抽出来之后 <c>tools/ClientFollowSelfTest</c> 可以跨平台跑，CI 每次 push 都会验。
/// </summary>
public static class ClientFollowPolicy
{
    /// <summary>等待客户端时的只读重试间隔（界面 tick 数；500ms × 10 ≈ 5s）。</summary>
    public const int RetryEveryTicks = 10;

    /// <summary>
    /// 「重新识别客户端」的入口决策：**未挂机也能识别**。
    /// 识别客户端是只读动作（读 TCP 表 ∪ 顶层窗口），和"是否已开始挂机"没有因果关系；
    /// 旧实现把它锁在"已启动"之后（Host==null 就提示"先点开始挂机"），顺序是反的，
    /// 于是形成"想选客户端→得先挂机；没客户端→挂机无从谈起"的死循环。此处从结构上禁掉。
    /// </summary>
    public static ClientRefreshAction DecideRefresh(bool hostBound)
        => hostBound ? ClientRefreshAction.Reconfigure : ClientRefreshAction.IdentifyOffline;

    /// <summary>
    /// 启动流程判定：识别到目标 → 已绑定；否则（含配置里有 Host 的兜底情形）一律进入
    /// 「等待客户端连接」—— 不假装已经在挂机，等客户端登录后自动跟上。
    /// </summary>
    public static ClientBindState DecideAfterStart(bool identified, bool hasHostFallback)
        => identified ? ClientBindState.Bound : ClientBindState.WaitingClient;

    /// <summary>
    /// 等待状态下是否该做一次只读重试跟随：只在"等待中 + 已挂机"且到达节流点时重试
    /// （避免每 500ms 全量扫一遍进程）。tick 从 1 开始；第 10、20… 次触发。
    /// </summary>
    public static bool ShouldRetryFollow(bool waiting, bool running, int tick, int retryEveryTicks = RetryEveryTicks)
        => waiting && running && retryEveryTicks > 0 && tick > 0 && tick % retryEveryTicks == 0;

    /// <summary>重试时扫到候选就退出等待、自动跟上。</summary>
    public static bool ShouldAdoptOnRetry(int candidateCount) => candidateCount > 0;

    /// <summary>顶部「客户端」芯片短文本。</summary>
    public static string ChipText(int targetPid, long targetHwnd, bool waiting)
    {
        if (targetPid > 0) return $"pid={targetPid}";
        if (targetHwnd != 0) return $"句柄 0x{targetHwnd:X}";
        return waiting ? "等待连接…" : "未绑定";
    }

    /// <summary>目标客户端的一句话描述（状态摘要里的「目标客户端」行）。</summary>
    public static string DescribeTarget(string processName, int targetPid, long targetHwnd, bool waiting)
    {
        if (targetPid <= 0 && targetHwnd == 0)
        {
            return waiting
                ? "等待客户端连接（未绑定：每约 5 秒只读重试，客户端登录后自动跟上）"
                : "未绑定（启动时自动挑选评分最高的候选）";
        }

        string window = targetHwnd != 0 ? $"句柄=0x{targetHwnd:X}" : "未绑定句柄";
        return $"{processName}(pid={targetPid})｜{window}" + (waiting ? "｜等待该客户端登录" : string.Empty);
    }

    /// <summary>
    /// 状态摘要里的「运行状态」文本：等待客户端时明确标注"尚未产生任何动作"，
    /// 让"到底有没有真的在挂机"一眼可见（不制造"看起来在挂机"的假象）。
    /// </summary>
    public static string RunningStateNote(bool running, bool sniffOnly, bool waiting)
    {
        string baseText = running ? (sniffOnly ? "仅嗅探" : "挂机中") : "未启动";
        return waiting && running ? baseText + "（等待客户端连接：尚未产生任何动作）" : baseText;
    }
}
