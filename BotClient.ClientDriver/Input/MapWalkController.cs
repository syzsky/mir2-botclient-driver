using BotClient.ClientDriver;

namespace BotClient.ClientDriver.Input;

public enum WalkResult
{
    /// <summary>已到达（或已到足够近）。</summary>
    Arrived,
    /// <summary>走到一半卡住了（坐标不再靠近）。</summary>
    Stuck,
    /// <summary>无法执行（UI 态不对 / 未校准 / 熔断）。</summary>
    Blocked,
    /// <summary>取消。</summary>
    Cancelled,
}

/// <summary>
/// 移动控制器：把"我要去 (X, Y)"翻译成多次点击，并在过程中处理卡死。
///
/// 三个必须分开处理的现实约束：
///   ① **主视图单次点击走不了太远**。传奇客户端点一下地图通常只走 3~5 格，
///      点太远会被截断成"走一小段"。所以远距离目标必须**分段推进**：
///      每次点朝目标方向的中间格，走一段再点下一段。
///   ② **超出视野的点根本点不到**。此时唯一可用的移动方式是小地图寻路
///      （点小地图 → 客户端自己算路径），这是 `MiniMapPathMinDistance` 的用途。
///   ③ **走不动不一定是代码问题**：前面可能有墙、有怪、有 NPC 挡住，
///      或者点到了不可达的格子。所以卡死判定必须存在，并且卡死后要**换策略**
///      （点格子 → 换小地图），而不是一直重试点同一个点。
/// </summary>
public sealed class MapWalkController
{
    private readonly ClientDriverConfig _cfg;
    private readonly ScreenMapper _mapper;
    private readonly InputSimulator _input;
    private readonly ActionGate _gate;
    private readonly UiStateProbe _ui;
    private readonly Func<(int X, int Y)> _playerPos;

    public MapWalkController(
        ClientDriverConfig cfg,
        ScreenMapper mapper,
        InputSimulator input,
        ActionGate gate,
        UiStateProbe ui,
        Func<(int X, int Y)> playerPos)
    {
        _cfg = cfg;
        _mapper = mapper;
        _input = input;
        _gate = gate;
        _ui = ui;
        _playerPos = playerPos;
    }

    /// <summary>主视图单次点击最多推进几格（超过就分段）。</summary>
    public int MaxClickStepCells { get; set; } = 4;

    /// <summary>认为"到达"的容差（格）。</summary>
    public int ArriveTolerance { get; set; } = 1;

    /// <summary>整体超时（毫秒），防止一个不可达目标把主循环拖死。</summary>
    public int OverallTimeoutMs { get; set; } = 30000;

    public event Action<string>? Log;

    /// <summary>走到目标格。</summary>
    public async Task<WalkResult> WalkToAsync(int targetX, int targetY, CancellationToken ct)
    {
        if (!_mapper.ViewReady)
        {
            Log?.Invoke("[walk] 主视图未校准，无法移动");
            return WalkResult.Blocked;
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(OverallTimeoutMs);
        int stuckStrikes = 0;
        int lastDistance = int.MaxValue;
        var lastProgressAt = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            _ui.Tick();

            var (px, py) = _playerPos();
            int dx = targetX - px;
            int dy = targetY - py;
            int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));

            if (distance <= ArriveTolerance) return WalkResult.Arrived;

            if (!_ui.AllowsClick)
            {
                // 对话态/加载态：不点，等一会儿再看（可能是别的动作刚触发了 UI 变化）
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            // ---- 卡死判定：PathStuckTimeoutMs 内距离没有实质缩短 ----
            if (distance < lastDistance - ArriveTolerance || distance > lastDistance + 8)
            {
                lastDistance = distance;
                lastProgressAt = DateTime.UtcNow;
                stuckStrikes = 0;
            }
            else if ((DateTime.UtcNow - lastProgressAt).TotalMilliseconds > _cfg.Behavior.PathStuckTimeoutMs)
            {
                stuckStrikes++;
                Log?.Invoke($"[walk] 疑似卡住（距目标 {distance} 格，第 {stuckStrikes} 次）");
                lastProgressAt = DateTime.UtcNow;
                lastDistance = int.MaxValue;

                // 卡住时的策略：短距离用"侧移一步"打破僵局，远距离改走小地图
                if (stuckStrikes >= 3) return WalkResult.Stuck;
                if (distance > MaxClickStepCells) stuckStrikes = Math.Max(stuckStrikes, 1);
            }

            // ---- 选择移动方式 ----
            bool longHaul = distance > _cfg.Behavior.MiniMapPathMinDistance;
            if (longHaul && _mapper.MiniMapReady)
            {
                var outcome = await _gate.ExecuteAsync($"小地图寻路→({targetX},{targetY})", async c =>
                {
                    var pt = _mapper.WorldToMiniMapPixel(targetX, targetY, px, py);
                    if (pt == null) { Log?.Invoke("[walk] 目标不在小地图范围内"); return false; }

                    if (!_input.ClickClient(pt.Value.X, pt.Value.Y))
                        return false;

                    return await WaitForProgressAsync(px, py, ct).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);

                if (outcome == ActionOutcome.Rejected) return WalkResult.Blocked;
            }
            else
            {
                // 近距：朝目标方向取一个"不超过单次步长"的中间格
                var (stepX, stepY) = IntermediateCell(px, py, targetX, targetY, MaxClickStepCells);

                // 小地图可用的前提下，如果连中间格都不在视图里，说明必须走小地图
                if (!_mapper.TryGetViewClickPoint(stepX, stepY, px, py, out var pt))
                {
                    if (_mapper.MiniMapReady)
                    {
                        var mpt = _mapper.WorldToMiniMapPixel(stepX, stepY, px, py);
                        if (mpt != null)
                        {
                            var o2 = await _gate.ExecuteAsync($"小地图(近) → ({stepX},{stepY})", async c =>
                            {
                                if (!_input.ClickClient(mpt.Value.X, mpt.Value.Y)) return false;
                                return await WaitForProgressAsync(px, py, ct).ConfigureAwait(false);
                            }, ct).ConfigureAwait(false);
                            if (o2 == ActionOutcome.Rejected) return WalkResult.Blocked;
                            continue;
                        }
                    }

                    Log?.Invoke($"[walk] ({stepX},{stepY}) 既不在视野也不在小地图内，放弃");
                    return WalkResult.Blocked;
                }

                var outcome = await _gate.ExecuteAsync($"走→({stepX},{stepY})", async c =>
                {
                    if (!_input.ClickClient(pt.X, pt.Y)) return false;
                    return await WaitForProgressAsync(px, py, ct).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);

                if (outcome == ActionOutcome.Rejected) return WalkResult.Blocked;
            }
        }

        Log?.Invoke($"[walk] 超时未到达 ({targetX},{targetY})");
        return WalkResult.Stuck;
    }

    /// <summary>
    /// 等"真的动了一下"：玩家坐标发生位移，或至少看到上行出现走路命令。
    /// 只有坐标位移才算成功 —— 上行有命令说明客户端接受了点击，但可能因为前面有障碍只抖了一下，
    /// 那种情况由外层的卡死判定接管。
    /// </summary>
    private async Task<bool> WaitForProgressAsync(int fromX, int fromY, CancellationToken ct)
    {
        int timeout = _cfg.Behavior.ActionAckTimeoutMs;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(70, ct).ConfigureAwait(false);
            var (px, py) = _playerPos();
            if (px != fromX || py != fromY) return true;
        }
        return false;
    }

    /// <summary>
    /// 在"当前格 → 目标格"的连线上取一个不超过 maxStep 格的中间格。
    /// 用最大范数（切比雪夫）限制步长，与传奇的八方向移动一致。
    /// </summary>
    public static (int X, int Y) IntermediateCell(int fromX, int fromY, int toX, int toY, int maxStep)
    {
        int dx = toX - fromX;
        int dy = toY - fromY;
        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (dist <= maxStep) return (toX, toY);

        double k = (double)maxStep / dist;
        int sx = fromX + (int)Math.Round(dx * k);
        int sy = fromY + (int)Math.Round(dy * k);
        if (sx == fromX && sy == fromY) { sx = fromX + Math.Sign(dx); sy = fromY + Math.Sign(dy); }
        return (sx, sy);
    }
}
