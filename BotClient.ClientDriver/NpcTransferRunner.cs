using BotClient.ClientDriver;
using BotClient.Net;

namespace BotClient.ClientDriver.Input;

/// <summary>
/// NPC 传送 / 换图流程。
///
/// 传奇换图不是"点传送员就完事"，它是一条有严格顺序的链，任何一步跳步都会卡住：
///     靠近 NPC（≤15 格闸门）
///       → 点 NPC
///       → 等客户端弹出对话窗（服务端下发后才会有）
///       → 点菜单项（可能要翻页、多级菜单）
///       → 等换图包（SM_CHANGEMAP）
///       → 等新地图就绪（黑屏期间**绝不能**点任何东西）
///
/// 这里把每一步都做成"等到条件成立再往下走"，而不是靠固定 sleep —— 固定 sleep 在
/// 网络抖动或加载慢的机器上必然出问题，而且出了问题很难复现。
///
/// 与"跨服 NPC 名称不同、子地图选项多"的两条应对（本次补强）：
///   ① 步骤 ①~③b 抽成 <see cref="EnsureDialogOpenAsync"/>，供传送/买药/探测共用；
///   ② 换图后**校验到达图代码**（<paramref name="targetMapCode"/>）：只看"换过图了"
///      会把进错图当成功，后面所有坐标与 NPC 全部错位。
/// </summary>
public sealed class NpcTransferRunner
{
    private readonly ClientDriverConfig _cfg;
    private readonly ClientActionDriver _driver;
    private readonly UiStateProbe _ui;
    private readonly Func<(int X, int Y)> _playerPos;
    private readonly Func<string> _currentMap;

    public NpcTransferRunner(
        ClientDriverConfig cfg,
        ClientActionDriver driver,
        UiStateProbe ui,
        Func<(int X, int Y)> playerPos,
        Func<string>? currentMap = null)
    {
        _cfg = cfg;
        _driver = driver;
        _ui = ui;
        _playerPos = playerPos;
        _currentMap = currentMap ?? (() => string.Empty);
    }

    /// <summary>当前地图代码（用于校验"传送之后到的是不是目标图"）。</summary>
    public string CurrentMap => _currentMap();

    /// <summary>等换图包的时长。</summary>
    public int MapLoadObserveTimeoutMs { get; set; } = 3000;

    /// <summary>换图完整流程（含加载就绪）的最长等待。</summary>
    public int MapSettleTimeoutMs { get; set; } = 15000;

    public event Action<string>? Log;

    /// <summary>NPC 交互距离闸门（与服务端限制一致）。</summary>
    public int InteractRange { get; set; } = 15;

    /// <summary>
    /// 执行一次传送（不校验到达图；兼容旧调用）。
    /// </summary>
    public Task<bool> TransferAsync(int npcX, int npcY, IReadOnlyList<string> menuTexts, CancellationToken ct)
        => TransferAsync(npcX, npcY, menuTexts, null, ct);

    /// <summary>
    /// 执行一次传送，并在换图后校验"到的确实是目标图"。
    /// </summary>
    /// <param name="npcX">传送 NPC 所在格。</param>
    /// <param name="npcY">同上。</param>
    /// <param name="menuTexts">按顺序要点击的菜单文本，例如 ["传送", "比奇省"]。支持多级菜单。</param>
    /// <param name="targetMapCode">
    /// 目标地图代码（如 "0" / "0101"）。为空表示不校验。
    /// 跨服菜单文字五花八门，同一句话在不同服可能通往不同图，所以"进对了没有"只能拿代码验。
    /// </param>
    public async Task<bool> TransferAsync(int npcX, int npcY, IReadOnlyList<string> menuTexts, string? targetMapCode, CancellationToken ct)
    {
        if (menuTexts.Count == 0)
        {
            Log?.Invoke("[传送] 未提供菜单文本，拒绝执行（不知道要点哪个选项就不能点）");
            return false;
        }

        // ---- ① 靠近 → ② 点 NPC → ③ 等对话 → ③b 等菜单文本 ----
        if (!await EnsureDialogOpenAsync(npcX, npcY, ct).ConfigureAwait(false))
            return false;

        string beforeMap = CurrentMap;
        var tSelect = _ui.ClearSystemMessage();   // 之后出现的提示才是"本次点选"的结果

        // ---- ④ 逐级点菜单 ----
        foreach (string step in menuTexts)
        {
            ct.ThrowIfCancellationRequested();

            if (_ui.State != UiState.NpcDialog)
            {
                Log?.Invoke($"[传送] 准备选 \"{step}\" 时已不在对话态（{_ui.State}），流程中断");
                return false;
            }

            bool clicked = await _driver.TrySelectDialogAsync(-1, step, ct).ConfigureAwait(false);
            if (!clicked)
            {
                Log?.Invoke($"[传送] 菜单项 \"{step}\" 没能点下去（没匹配到 / 多候选 / 未校准），中止以免点错");
                return false;
            }

            // 等下一个窗口出现或进入加载态；这一步是为了让服务端有时间回包
            await Task.Delay(600, ct).ConfigureAwait(false);
        }

        // ---- ⑤ 等换图 ----
        var wait = await WaitForMapChangeAsync(beforeMap, tSelect, MapLoadObserveTimeoutMs, ct).ConfigureAwait(false);
        if (wait == MapWaitResult.Rejected)
        {
            Log?.Invoke($"[传送] 服务端回绝，未换图；提示：{_ui.LastSystemMessage}");
            return false;
        }
        if (wait == MapWaitResult.Timeout)
        {
            string why = _ui.LastSystemMessage;
            Log?.Invoke(string.IsNullOrWhiteSpace(why)
                ? "[传送] 没有观察到换图包（可能是不换图的选项，或菜单没点中）"
                : $"[传送] 没有观察到换图包；服务端提示：{why}");
            return false;
        }

        // ---- ⑥ 等新地图就绪；加载期间一切点击都被 UiStateProbe 拦住 ----
        if (!await WaitForStateAsync(UiState.Free, MapSettleTimeoutMs, ct).ConfigureAwait(false))
        {
            Log?.Invoke("[传送] 新地图长时间未就绪");
            return false;
        }

        // 额外保险：有些客户端在换图后先渲染画面再解锁操作
        await Task.Delay(_cfg.Behavior.MapLoadGuardMs, ct).ConfigureAwait(false);

        string arrived = CurrentMap;
        if (!string.IsNullOrWhiteSpace(targetMapCode) && !MapCodeEquals(arrived, targetMapCode))
        {
            Log?.Invoke($"[传送] 到了 {arrived}，目标却是 {targetMapCode} —— 这个菜单项并不通往目标图");
            return false;
        }

        Log?.Invoke($"[传送] 完成，已进入 {arrived}");
        return true;
    }

    /// <summary>
    /// 前置三步：靠近 NPC（≤<see cref="InteractRange"/>）→ 点开对话 → 等菜单文本到齐。
    ///
    /// 抽出来是因为"跟 NPC 说上话"是传送、买药、探测子地图等所有交互的共同起点；
    /// 判定标准只写一份，才能保证各处行为一致（尤其是超距上行会被服务端静默丢弃这一条）。
    /// </summary>
    public async Task<bool> EnsureDialogOpenAsync(int npcX, int npcY, CancellationToken ct)
    {
        // ---- ① 靠近 ----
        for (int i = 0; i < 10; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (px, py) = _playerPos();
            int d = Math.Max(Math.Abs(npcX - px), Math.Abs(npcY - py));
            if (d <= InteractRange - 3) break;

            Log?.Invoke($"[传送] 距 NPC {d} 格，继续靠近");
            await _driver.DispatchAsync(new ClientActionIntent
            {
                Kind = ClientActionKind.NpcInteract,
                X = npcX,
                Y = npcY,
            }, ct).ConfigureAwait(false);

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        // ---- ② 点 NPC ----
        await _driver.DispatchAsync(new ClientActionIntent
        {
            Kind = ClientActionKind.NpcInteract,
            X = npcX,
            Y = npcY,
        }, ct).ConfigureAwait(false);

        // ---- ③ 等对话态 ----
        if (!await WaitForStateAsync(UiState.NpcDialog, 4000, ct).ConfigureAwait(false))
        {
            Log?.Invoke("[传送] 点击后没有等到对话窗口（可能距离不够 / NPC 不在视野 / 点空了）");
            return false;
        }

        // ---- ③b 等菜单文本到齐 ----
        // 拿不到文本就只能猜行号，而猜错会真的触发一次传送/购买等真实操作。
        // 所以这里宁可中止，也不盲点；宿主漏接 NpcMessage → FeedNpcDialog 时日志能直接指出来。
        var textDeadline = DateTime.UtcNow.AddMilliseconds(1500);
        while (_ui.DialogLines.Count == 0 && DateTime.UtcNow < textDeadline && !ct.IsCancellationRequested)
            await Task.Delay(60, ct).ConfigureAwait(false);

        if (_ui.DialogLines.Count == 0)
        {
            Log?.Invoke("[传送] 对话已弹出但拿不到菜单文本（宿主是否漏了 NpcMessage → FeedNpcDialog 转发？），中止以免点错");
            return false;
        }
        Log?.Invoke($"[传送] 菜单 {_ui.DialogLines.Count} 项：{string.Join(" | ", _ui.DialogLines)}");
        return true;
    }

    // ---------------------------------------------------------------- 内部等待

    private enum MapWaitResult { Changed, Rejected, Timeout }

    /// <summary>
    /// 等换图。三种出口：
    ///   Changed —— 看到加载态或地图代码已变；
    ///   Rejected —— 回到自由态且服务端刚回了提示（等级/金币/物品不足这类硬回绝），
    ///               这时再等下去只是白等，早点返回能让上层立刻换下一个候选；
    ///   Timeout —— 什么都没发生。
    /// </summary>
    private async Task<MapWaitResult> WaitForMapChangeAsync(string before, DateTime afterSelect, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            _ui.Tick();

            if (_ui.State == UiState.MapLoading) return MapWaitResult.Changed;
            if (!string.IsNullOrWhiteSpace(before) && !MapCodeEquals(CurrentMap, before)) return MapWaitResult.Changed;

            if (_ui.LastSystemMessageAt > afterSelect && _ui.State == UiState.Free)
                return MapWaitResult.Rejected;

            await Task.Delay(60, ct).ConfigureAwait(false);
        }
        return MapWaitResult.Timeout;
    }

    private async Task<bool> WaitForStateAsync(UiState want, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            _ui.Tick();
            if (_ui.State == want) return true;
            await Task.Delay(60, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// 地图代码比较：容忍前导零与大小写差异（"0" / "00"、"0101" / "101"）。
    /// 服务端下发的代码与 MapInfo.txt 里的编号写法不总是完全一致，直接比字符串会误判成"走错图"。
    /// </summary>
    public static bool MapCodeEquals(string? a, string? b)
    {
        static string Norm(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            string t = s.Trim().TrimStart('0');
            return t.Length == 0 ? "0" : t.ToUpperInvariant();
        }
        return Norm(a) == Norm(b);
    }
}
