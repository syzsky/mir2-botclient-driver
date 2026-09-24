namespace BotClient.ClientDriver.Input;

/// <summary>一次动作的执行结果。</summary>
public enum ActionOutcome
{
    /// <summary>点击已发出，且从上行嗅探到客户端确实把对应命令发出去了。</summary>
    Confirmed,
    /// <summary>点击已发出，但超时没看到对应上行 —— 大概率点空了（被 UI 遮挡 / 窗口不在前台 / 格子不可达）。</summary>
    NoAck,
    /// <summary>点击根本没发出（窗口不在前台、未校准、被闸门拦住）。</summary>
    Rejected,
}

/// <summary>
/// 动作闸门：所有点击的唯一入口。
///
/// 它负责三件事，缺一不可：
///   ① **串行化**：同一时刻只允许一个动作在飞，避免"上一个点击的效果还没回来就点下一个"
///      导致的相位错乱（这是操作通道最容易出的 bug）；
///   ② **节流**：点击之间留人化间隔，别让输入轨迹成为一串精确等距的脉冲；
///   ③ **熔断**：连续 N 次动作没被确认就停机。
///      这一条是保命的 —— C 方案里 Bot 看不见画面，一旦 UI 状态判断错了，
///      它会对着错误的面板疯狂点击，可能点出你完全不想发生的操作（买卖、丢弃、传送）。
///      宁可停下来报错，也不能瞎点。
/// </summary>
public sealed class ActionGate
{
    private readonly ClientDriverConfig _cfg;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Random _rng = new();
    private DateTime _lastActionUtc = DateTime.MinValue;

    public ActionGate(ClientDriverConfig cfg) => _cfg = cfg;

    public event Action<string>? Log;
    /// <summary>熔断触发（连续未确认超限）。调用方应暂停挂机并提醒人工介入。</summary>
    public event Action<string>? Tripped;

    /// <summary>连续未确认次数。</summary>
    public int ConsecutiveUnacked { get; private set; }

    /// <summary>是否已熔断（熔断后所有动作一律拒绝，直到 <see cref="Reset"/>）。</summary>
    public bool IsTripped => ConsecutiveUnacked >= Math.Max(1, _cfg.Behavior.MaxUnackedActions);

    /// <summary>总动作计数（诊断）。</summary>
    public long TotalActions { get; private set; }
    public long ConfirmedActions { get; private set; }

    /// <summary>
    /// 执行一次动作。<paramref name="action"/> 内部完成"点击 + 等确认"，返回是否被确认。
    /// 闸门只负责调度与记账，不关心具体是点地图还是点 NPC。
    /// </summary>
    public async Task<ActionOutcome> ExecuteAsync(string tag, Func<CancellationToken, Task<bool>> action, CancellationToken ct)
    {
        if (_cfg.Behavior.Paused) return ActionOutcome.Rejected;
        if (IsTripped)
        {
            Log?.Invoke($"[gate] 已熔断（连续 {ConsecutiveUnacked} 次动作未被确认），拒绝执行 {tag}");
            return ActionOutcome.Rejected;
        }

        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 节流：距上次动作的最小间隔
            double since = (DateTime.UtcNow - _lastActionUtc).TotalMilliseconds;
            int minGap = _cfg.Behavior.MinClickIntervalMs;
            if (since < minGap)
                await Task.Delay((int)(minGap - since) + _rng.Next(0, 40), ct).ConfigureAwait(false);

            TotalActions++;
            bool confirmed;
            try
            {
                confirmed = await action(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log?.Invoke($"[gate] {tag} 执行异常: {ex.Message}");
                confirmed = false;
            }
            finally
            {
                _lastActionUtc = DateTime.UtcNow;
            }

            if (confirmed)
            {
                ConsecutiveUnacked = 0;
                ConfirmedActions++;
                return ActionOutcome.Confirmed;
            }

            ConsecutiveUnacked++;
            if (IsTripped)
            {
                _cfg.Behavior.Paused = true;
                Tripped?.Invoke($"连续 {ConsecutiveUnacked} 次动作未被客户端确认，已暂停挂机。" +
                                $"常见原因：游戏窗口不在前台 / UI 校准失效 / 角色被卡在不可达位置。");
            }
            return ActionOutcome.NoAck;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>人工确认问题已解决后复位。</summary>
    public void Reset()
    {
        ConsecutiveUnacked = 0;
        _cfg.Behavior.Paused = false;
        Log?.Invoke("[gate] 已复位");
    }
}
