using BotClient.ClientDriver.Input;

namespace BotClient.ClientDriver;

/// <summary>B 档自校正参数。</summary>
public sealed class AutoCalibrateOptions
{
    /// <summary>每个轴最多迭代轮数。</summary>
    public int MaxRounds { get; set; } = 6;

    /// <summary>首轮试探用几格（格数越大、对格宽的解析度越高；3 够用且不容易撞墙）。</summary>
    public int ProbeCells { get; set; } = 3;

    /// <summary>一次点击后等坐标变化的上限。</summary>
    public int StepTimeoutMs { get; set; } = 2600;

    /// <summary>等"角色已进图、能读到坐标"的上限。</summary>
    public int PositionWaitMs { get; set; } = 20000;

    /// <summary>格尺寸的合法区间（px），防脏数据把配置带飞。</summary>
    public int MinCell { get; set; } = 8;

    public int MaxCell { get; set; } = 160;
}

/// <summary>
/// B 档：自动初值 + 闭环自校正。
///
/// 思路（不需要人工量任何一个像素）：
///   ① 自动初值：视图区没校准时，先假设"视图区 ≈ 整个客户区、玩家格 = 客户区中心、格尺寸 = 48×32"；
///   ② 闭环反馈：用**走格协议**当量具 —— 朝正东点一格的理论像素，看角色实际走了几格；
///      走了 n 格就说明真实格宽 = 点击像素 / n。迭代几轮，直到"按当前格宽点一格 → 恰好走 1 格"；
///      点东却斜着走，说明"玩家格像素"这个锚点在另一轴上有偏差，按偏差方向修锚点后重试；
///      墙/树挡住走不动就换方向再试，两个方向都不动则提示换到空地重跑。
///   ③ 收敛后写回 clientdriver.json 的 View 段（格宽/格高/玩家格像素），并自动收录进 A 档校准档。
///
/// 它**不碰鼠标之外的东西**：全部动作就是几次左键点击（走格），不做任何网络请求、不改客户端文件。
/// 全程只读坐标来算偏差，所以叫"闭环"。
///
/// 注意：小地图 / 背包 / 对话框这三块不走格就能得到反馈，仍沿用人工量一次（Ctrl+Alt+4..8）。
/// </summary>
public static class AutoCalibrator
{
    public static async Task<int> RunAsync(ClientDriverHost host, string driverConfigPath,
        AutoCalibrateOptions? options, CancellationToken ct, Action<string>? log = null)
    {
        var opt = options ?? new AutoCalibrateOptions();
        var cfg = host.Config;
        var win = host.Input;
        void Say(string m) => log?.Invoke(m);

        Say("自动校正开始：会用几次走格来反推格宽/格高，请把角色停在**空地**上，不要开背包/对话面板");

        if (!win.TryLocateWindow(out string why))
        {
            Say($"找不到游戏窗口: {why}");
            return 1;
        }

        var (cw, ch) = win.GetClientSize();
        bool viewEstimated = !cfg.View.IsCalibrated;
        if (viewEstimated)
        {
            cfg.View.ViewLeft = 0;
            cfg.View.ViewTop = 0;
            cfg.View.ViewWidth = cw;
            cfg.View.ViewHeight = ch;
            cfg.View.PlayerAlwaysCentered = true;
            Say($"视图区未校准 → 自动初值：视图区取整个客户区 {cw}×{ch}");
        }

        if (cfg.View.PlayerAlwaysCentered || cfg.View.PlayerScreenX <= 0 || cfg.View.PlayerScreenY <= 0)
        {
            cfg.View.PlayerScreenX = cfg.View.ViewLeft + cfg.View.ViewWidth / 2;
            cfg.View.PlayerScreenY = cfg.View.ViewTop + cfg.View.ViewHeight / 2;
        }

        if (cfg.View.CellWidth <= 0) cfg.View.CellWidth = 48;
        if (cfg.View.CellHeight <= 0) cfg.View.CellHeight = 32;
        Say($"自动初值：玩家格锚点 ({cfg.View.PlayerScreenX},{cfg.View.PlayerScreenY})，格尺寸 {cfg.View.CellWidth}×{cfg.View.CellHeight}（接下来靠走格收敛）");

        if (!await WaitForPositionAsync(host, opt.PositionWaitMs, ct))
        {
            Say($"等不到角色坐标（{opt.PositionWaitMs}ms 内没收到带坐标的封包）：请确认已登录并进入游戏地图、抓包正常，再重跑 --autocalibrate");
            return 6;
        }

        var (px, py) = host.PlayerPosition;
        Say($"当前坐标反馈正常：({px},{py}) —— 开始收敛");

        int savedJitter = cfg.Behavior.ClickJitterPx;   // 校正期把点击抖动收窄到 1px，避免"点一格"被抖到邻格
        cfg.Behavior.ClickJitterPx = 1;

        var (okW, cellW, logW) = await ConvergeAxisAsync(host, opt, horizontal: true, ct);
        foreach (string l in logW) Say(l);
        if (!okW)
        {
            Say("格宽未收敛：请把角色移到四周至少一格无遮挡的空地后重跑；若提示锚点偏差过大，请先用 --calibrate 量一次视图区四角（Ctrl+Alt+2/3）");
            return 7;
        }

        var (okH, cellH, logH) = await ConvergeAxisAsync(host, opt, horizontal: false, ct);
        foreach (string l in logH) Say(l);
        if (!okH)
        {
            Say("格高未收敛：同上，换到空地重跑");
            return 7;
        }

        cfg.View.CellWidth = cellW;
        cfg.View.CellHeight = cellH;
        cfg.Behavior.ClickJitterPx = savedJitter;      // 收敛完成，恢复人化抖动
        try
        {
            cfg.Save(driverConfigPath);
            Say($"已写回 {driverConfigPath} 的 View 段：格 {cellW}×{cellH}，玩家格 ({cfg.View.PlayerScreenX},{cfg.View.PlayerScreenY})");
        }
        catch (Exception ex)
        {
            Say($"写回配置失败: {ex.Message}");
            return 1;
        }

        if (CalibrationArchives.Remember(cfg, driverConfigPath, win, "auto", "走格反馈自校正", out string memo))
            Say(memo);
        else
            Say(memo);

        Say($"收敛完成：格宽 {cellW}px、格高 {cellH}px、玩家格像素 ({cfg.View.PlayerScreenX},{cfg.View.PlayerScreenY})");
        Say("下一步可以直接挂机；小地图/背包/对话框若仍缺，用 --calibrate 补量 Ctrl+Alt+4..8");
        return 0;
    }

    // ------------------------------------------------------------------ 单轴收敛

    private static async Task<(bool Ok, int Cell, List<string> Log)> ConvergeAxisAsync(
        ClientDriverHost host, AutoCalibrateOptions opt, bool horizontal, CancellationToken ct)
    {
        var logs = new List<string>();
        var v = host.Config.View;
        string axisName = horizontal ? "格宽" : "格高";
        string axisTag = horizontal ? "X" : "Y";
        int seed = Math.Clamp(horizontal ? v.CellWidth : v.CellHeight, opt.MinCell, opt.MaxCell);
        double scale = 1.0;
        int anchorFix = 0;

        for (int round = 1; round <= opt.MaxRounds; round++)
        {
            int cell = Math.Clamp((int)Math.Round(seed * scale), opt.MinCell, opt.MaxCell);
            int probeCells = round == 1 ? Math.Max(1, opt.ProbeCells) : 1;
            int offset = cell * probeCells;

            var r = await ProbeAsync(host, opt, horizontal, offset, ct);
            if (!r.Ok && probeCells > 1)
            {
                logs.Add($"  [{axisName}] 第{round}轮：{r.Why} → 退成单格试探");
                probeCells = 1;
                offset = cell;
                r = await ProbeAsync(host, opt, horizontal, offset, ct);
            }

            if (!r.Ok)
            {
                // 正方向走不动，多半是墙：换反方向再试一次（反方向照样能算出每格像素）
                var back = await ProbeAsync(host, opt, horizontal, -offset, ct);
                if (!back.Ok)
                {
                    logs.Add($"  [{axisName}] 第{round}轮：正反两向都没走动（{back.Why}）");
                    continue;
                }

                logs.Add($"  [{axisName}] 第{round}轮：正方向被挡，改用反方向 {offset}px 试探");
                r = back;
            }

            int mainDelta = horizontal ? r.Dx : r.Dy;
            int offDelta = horizontal ? r.Dy : r.Dx;
            int moved = Math.Abs(mainDelta);

            // 斜着走：锚点在另一轴上有偏差 → 先修锚点
            if (offDelta != 0)
            {
                if (anchorFix >= 3)
                {
                    logs.Add($"  [{axisName}] 反复斜走（Δx={r.Dx},Δy={r.Dy}）：视图区估算偏差过大，改用 --calibrate 量一次四角更稳");
                    return (false, cell, logs);
                }

                int fix = Math.Sign(offDelta) * Math.Max(2, (horizontal ? v.CellHeight : v.CellWidth) / 2);
                if (horizontal) v.PlayerScreenY -= fix; else v.PlayerScreenX -= fix;
                anchorFix++;
                logs.Add($"  [{axisName}] 第{round}轮：点{(horizontal ? "东/西" : "南/北")}却斜走 (Δx={r.Dx},Δy={r.Dy}) → " +
                         $"锚点偏差，{(horizontal ? "PlayerScreenY" : "PlayerScreenX")} 修正 {-fix}px 后重试");
                scale = 1.0;
                continue;
            }

            if (moved == 0)
            {
                logs.Add($"  [{axisName}] 第{round}轮：坐标没变（{r.Why}）");
                continue;
            }

            // 点东走西 = 方向相反：说明锚点像素与真实玩家像素差了不止一个试探距离
            if (Math.Sign(offset) != Math.Sign(mainDelta))
            {
                if (anchorFix >= 3)
                {
                    logs.Add($"  [{axisName}] 方向相反（点 {offset}px，实测 Δ{axisTag}={mainDelta}）：锚点偏差过大，请用 --calibrate 量一次");
                    return (false, cell, logs);
                }

                int shift = (int)(offset * 1.5);
                if (horizontal) v.PlayerScreenX += shift; else v.PlayerScreenY += shift;
                anchorFix++;
                logs.Add($"  [{axisName}] 第{round}轮：方向相反（点 {offset}px、走 Δ{axisTag}={mainDelta}）→ 锚点估计偏了，修正 {shift}px 后重试");
                scale = 1.0;
                continue;
            }

            if (moved == 1 && probeCells == 1)
            {
                logs.Add($"  [{axisName}] 第{round}轮：按 {cell}px 点一格 → 实测正好 1 格 ✓ 收敛（{axisName}={cell}px）");
                return (true, cell, logs);
            }

            double measured = (double)offset / moved;
            double newScale = measured / seed;
            logs.Add($"  [{axisName}] 第{round}轮：{offset}px × {probeCells} 格试探 → 实测 {moved} 格 → " +
                     $"推定每格 ≈ {measured:0.0}px");

            if (Math.Abs(newScale - scale) < 0.02)
            {
                // 已经不变化但还没通过"一格"终检：直接用推定值做终检
                logs.Add($"  [{axisName}] 第{round}轮：估计已稳定，改用 {cell}px 做单格终检");
            }

            scale = Math.Clamp(newScale, 0.1, 10.0);
        }

        logs.Add($"  [{axisName}] {opt.MaxRounds} 轮内未收敛：可能是周围地形挡住、或视图区估算偏差过大");
        return (false, Math.Clamp((int)Math.Round(seed * scale), opt.MinCell, opt.MaxCell), logs);
    }

    // ------------------------------------------------------------------ 走格与坐标反馈

    /// <summary>
    /// 点一格：朝指定方向点击"玩家格像素 + 偏移"的位置，等坐标变化并稳定下来，返回实际位移。
    /// </summary>
    private static async Task<(bool Ok, int Dx, int Dy, string Why)> ProbeAsync(
        ClientDriverHost host, AutoCalibrateOptions opt, bool horizontal, int offset, CancellationToken ct)
    {
        if (host.Ui.State != UiState.Free || !host.Ui.AllowsClick)
            return (false, 0, 0, $"当前 UI 态 = {host.Ui.State}（面板/对话/加载中），先关掉面板再重跑");

        var v = host.Config.View;
        int clickX = horizontal ? v.PlayerScreenX + offset : v.PlayerScreenX;
        int clickY = horizontal ? v.PlayerScreenY : v.PlayerScreenY + offset;

        var before = host.PlayerPosition;
        if (!host.Input.ClickClient(clickX, clickY))
            return (false, 0, 0, $"点击 ({clickX},{clickY}) 失败（窗口未前台或坐标越界）");

        var deadline = DateTime.UtcNow.AddMilliseconds(opt.StepTimeoutMs);
        bool changed = false;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(70, ct);
            var now = host.PlayerPosition;
            if (now.X != before.X || now.Y != before.Y) { changed = true; break; }
        }

        if (!changed)
            return (false, 0, 0, $"点击 ({clickX},{clickY}) 后 {opt.StepTimeoutMs}ms 内坐标没变（前方是墙/障碍或不是可行走方向）");

        var settled = await WaitStableAsync(host, 1800, ct);
        return (true, settled.X - before.X, settled.Y - before.Y, "");
    }

    private static async Task<bool> WaitForPositionAsync(ClientDriverHost host, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var p = host.PlayerPosition;
            if (p.X > 0 && p.Y > 0) return true;
            await Task.Delay(200, ct);
        }

        return false;
    }

    /// <summary>等坐标稳定：连续几次读数一致才认账（角色可能要走好几格才停）。</summary>
    private static async Task<(int X, int Y)> WaitStableAsync(ClientDriverHost host, int timeoutMs, CancellationToken ct)
    {
        var last = host.PlayerPosition;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        int stable = 0;
        while (DateTime.UtcNow < deadline && stable < 3)
        {
            await Task.Delay(80, ct);
            var now = host.PlayerPosition;
            if (now.X == last.X && now.Y == last.Y)
            {
                stable++;
            }
            else
            {
                stable = 0;
                last = now;
            }
        }

        return last;
    }
}
