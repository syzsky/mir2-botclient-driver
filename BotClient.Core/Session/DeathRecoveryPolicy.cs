namespace BotClient.Session;

/// <summary>是否该发起一次"重连取回家点复活"。见 <see cref="DeathRecoveryPolicy.Decide"/>。</summary>
public enum RecoveryDecision
{
    /// <summary>开关关着(默认):死亡/掉线只写日志,绝不自动重连。</summary>
    Disabled,
    /// <summary>距上次尝试太近,还要等 <see cref="DeathRecoveryPolicy.CooldownRemaining"/>。</summary>
    WaitCooldown,
    /// <summary>本轮配额用尽:再刷下去只会把账号白烧在一次比一次快的死亡里。</summary>
    Exhausted,
    /// <summary>可以发起。</summary>
    Proceed
}

/// <summary>
/// 无人值守挂机的自我恢复判定:角色死后(或逃跑小退掉线后)断开重连,让服务端在家点把角色以
/// 14 HP 重新拉起 —— 死人没有 CM_复活包可发(ObjPlayer.pas 上行注册表里没有),重连是唯一客户端侧手段。
///
/// 只做判定,不碰网络,这样这套计数规则能在 RunGateSim 里离线跑断言(真机复活的时序我们控制不了)。
/// 时间一律由调用方传入,内部不读时钟。
/// </summary>
public sealed class DeathRecoveryPolicy
{
    /// <summary>总开关,默认关:自动重连会在被踢/封号场景下反复登录,必须用户明确开启。</summary>
    public bool Enabled { get; set; }

    /// <summary>连续尝试上限。超过后只能人工处理。</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>两次尝试的最小间隔。服务端下线角色要时间落盘并释放"该角色已在游戏中"的占用,
    /// 秒重连会撞准入复用;同时它也是"死亡→立刻又死"的降速阀。</summary>
    public int MinIntervalSeconds { get; set; } = 60;

    /// <summary>重进世界后连续活过这么久,才认为真的救回来了,把配额清零。</summary>
    public int SuccessResetAfterSeconds { get; set; } = 600;

    public int AttemptsUsed { get; private set; }

    private DateTime _lastAttemptAt = DateTime.MinValue;
    private DateTime _reenteredAt = DateTime.MinValue;

    /// <summary>这次尝试之前还要等多久(<see cref="RecoveryDecision.WaitCooldown"/> 时用)。</summary>
    public TimeSpan CooldownRemaining(DateTime now)
    {
        var left = TimeSpan.FromSeconds(MinIntervalSeconds) - (now - _lastAttemptAt);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    /// <summary>为什么不允许:直接可写进界面日志,免得用户只看到一个"没反应"。</summary>
    public string Reason(RecoveryDecision d) => d switch
    {
        RecoveryDecision.Disabled => "未开启自动重连,需要手动重新登录",
        RecoveryDecision.WaitCooldown => $"距上次自动重连不足 {MinIntervalSeconds} 秒",
        RecoveryDecision.Exhausted => $"自动重连已用尽 {MaxAttempts} 次,不再刷登录(账号可能已被封/被踢,需人工处理)",
        _ => "可以自动重连"
    };

    /// <summary>上次重进世界之后,到现在为止一直活着(≥ SuccessResetAfterSeconds)。</summary>
    private bool SurvivedLongEnough(DateTime now) =>
        _reenteredAt != DateTime.MinValue && (now - _reenteredAt).TotalSeconds >= SuccessResetAfterSeconds;

    /// <summary>判定本次是否发起,<b>不改任何状态</b> —— 发起方失败时不该白扣配额,所以计数在
    /// <see cref="NotifyAttempt"/>。</summary>
    public RecoveryDecision Decide(DateTime now)
    {
        if (!Enabled) return RecoveryDecision.Disabled;
        if (AttemptsUsed >= MaxAttempts && !SurvivedLongEnough(now)) return RecoveryDecision.Exhausted;
        if ((now - _lastAttemptAt).TotalSeconds < MinIntervalSeconds) return RecoveryDecision.WaitCooldown;
        return RecoveryDecision.Proceed;
    }

    /// <summary>登记"我已经发起了一次重连",扣配额并起间隔计时。</summary>
    public void NotifyAttempt(DateTime now)
    {
        // 上一次真的活下来了就把配额重新给满:否则一个晚上死 4 次(每次都救活了)的第 4 次就再也不恢复。
        if (SurvivedLongEnough(now)) AttemptsUsed = 0;
        AttemptsUsed++;
        _lastAttemptAt = now;
    }

    /// <summary>重进世界成功。只记时间,不立刻清配额 —— 要等活满 <see cref="SuccessResetAfterSeconds"/>。</summary>
    public void NotifyReentered(DateTime now) => _reenteredAt = now;
}
