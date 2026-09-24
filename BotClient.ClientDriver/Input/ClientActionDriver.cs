using BotClient.ClientDriver.Sniff;
using BotClient.Net;
using BotClient.Protocol;

namespace BotClient.ClientDriver.Input;

/// <summary>
/// 操作通道的翻译器：把 Core 的**语义动作意图**翻译成真机上的鼠标/键盘操作。
///
/// 它实现 Core 侧新增的 <see cref="IClientDriver"/>，被 <c>BotSession.Driver</c> 持有。
/// BotRuntime 里那些 <c>SendWalkAsync / SendHitAsync / ...</c> 方法在 Driver 附着后
/// 不再向 socket 写字节，而是走这里 —— AI、脚本引擎、状态机一行都不用改。
///
/// 设计要点：
///   • **每个动作都要能被证伪**。点完不是"假定成功"，而是去上行流里找对应命令；
///     找不到就判定 NoAck，交给 ActionGate 记账，连续失败熔断停机。
///   • **不做自己不该做的决定**。意图里说"去 (X,Y)"就去，不在这里做寻路算法或战斗判断，
///     那些属于 Core 的 AI 层。这一层只负责"如何让真客户端照做"。
/// </summary>
public sealed class ClientActionDriver : IClientDriver
{
    private readonly ClientDriverConfig _cfg;
    private readonly ScreenMapper _mapper;
    private readonly InputSimulator _input;
    private readonly ActionGate _gate;
    private readonly UiStateProbe _ui;
    private readonly MapWalkController _walk;
    private readonly UpstreamCommandProbe _probe;
    private readonly ICmdCatalog _cmds;
    private readonly Func<(int X, int Y)> _playerPos;

    public ClientActionDriver(
        ClientDriverConfig cfg,
        ScreenMapper mapper,
        InputSimulator input,
        ActionGate gate,
        UiStateProbe ui,
        MapWalkController walk,
        UpstreamCommandProbe probe,
        ICmdCatalog cmds,
        Func<(int X, int Y)> playerPos)
    {
        _cfg = cfg;
        _mapper = mapper;
        _input = input;
        _gate = gate;
        _ui = ui;
        _walk = walk;
        _probe = probe;
        _cmds = cmds;
        _playerPos = playerPos;
    }

    public event Action<string>? Log;

    /// <summary>是否已附着到客户端（窗口找到 + 必要校准项就绪）。</summary>
    public bool IsAttached { get; private set; }

    /// <summary>NPC 交互距离闸门：必须在这么多格以内才能点到 NPC（服务端限制，见 !Setup.txt / 天骥脚本）。</summary>
    public int NpcInteractRange { get; set; } = 15;

    public void Attach()
    {
        if (!_input.TryLocateWindow(out string why))
        {
            Log?.Invoke($"[driver] 附着失败: {why}");
            IsAttached = false;
            return;
        }
        IsAttached = true;
        Log?.Invoke("[driver] 已附着到客户端窗口");
    }

    // ---------------------------------------------------------------- 入口

    public async Task DispatchAsync(ClientActionIntent intent, CancellationToken ct)
    {
        if (!IsAttached)
        {
            Log?.Invoke($"[driver] 未附着，忽略 {intent.Kind}");
            return;
        }

        try
        {
            switch (intent.Kind)
            {
                case ClientActionKind.Walk:
                    await _walk.WalkToAsync(intent.X, intent.Y, ct).ConfigureAwait(false);
                    break;

                case ClientActionKind.Hit:
                    await AttackAsync(intent.X, intent.Y, ct).ConfigureAwait(false);
                    break;

                case ClientActionKind.Spell:
                    await CastAsync(intent.X, intent.Y, intent.Param, ct).ConfigureAwait(false);
                    break;

                case ClientActionKind.Eat:
                    await EatAsync(intent.Param, intent.Extra, ct).ConfigureAwait(false);
                    break;

                case ClientActionKind.Pickup:
                    await ClickCellAsync("拾取", intent.X, intent.Y, _cmds.CmPickUp, ct).ConfigureAwait(false);
                    break;

                case ClientActionKind.NpcInteract:
                    await InteractNpcAsync(intent.X, intent.Y, ct).ConfigureAwait(false);
                    break;

                case ClientActionKind.DialogSelect:
                    await SelectDialogAsync(intent.Param, intent.Text, ct).ConfigureAwait(false);
                    break;

                case ClientActionKind.Turn:
                case ClientActionKind.Talk:
                case ClientActionKind.DropItem:
                    // 这一版不提供对应操作：转向靠移动自然完成；说话/丢物风险高不自动做。
                    Log?.Invoke($"[driver] 忽略动作 {intent.Kind}（本版本不转换为操作）");
                    break;

                default:
                    Log?.Invoke($"[driver] 未知动作 {intent.Kind}（cmd={intent.RawCommand}）");
                    break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log?.Invoke($"[driver] 执行 {intent.Kind} 异常: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 各动作实现

    /// <summary>点一个世界格（攻击 / 拾取都是"点那一格"）。</summary>
    private async Task<float> ClickCellAsync(string tag, int tx, int ty, ushort expectCmd, CancellationToken ct)
    {
        var outcome = await _gate.ExecuteAsync(tag, async c =>
        {
            var (px, py) = _playerPos();
            if (!_mapper.TryGetViewClickPoint(tx, ty, px, py, out var pt))
            {
                Log?.Invoke($"[{tag}] ({tx},{ty}) 不在可点击范围内");
                return false;
            }

            var t0 = DateTime.UtcNow;
            if (!_input.ClickClient(pt.X, pt.Y)) return false;
            return await WaitForUpstreamAsync(expectCmd, t0, c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return outcome == ActionOutcome.Confirmed ? 1f : 0f;
    }

    private async Task AttackAsync(int tx, int ty, CancellationToken ct)
    {
        // 目标不在视野内：先贴近（AI 下一轮会重新决策，所以只走一段就返回）
        var (px, py) = _playerPos();
        if (!_mapper.TryGetViewClickPoint(tx, ty, px, py, out _))
        {
            var (nx, ny) = MapWalkController.IntermediateCell(px, py, tx, ty, _walk.MaxClickStepCells);
            await _walk.WalkToAsync(nx, ny, ct).ConfigureAwait(false);
            return;
        }
        await ClickCellAsync("攻击", tx, ty, _cmds.CmHit, ct).ConfigureAwait(false);
    }

    private async Task CastAsync(int tx, int ty, int spellSlot, CancellationToken ct)
    {
        if (_cfg.Keys.UseSpellHotkey)
        {
            var key = _cfg.Keys.ResolveSpellKey(spellSlot);
            if (key == null)
            {
                Log?.Invoke($"[施法] 法术槽 {spellSlot} 未绑定快捷键，跳过（请在 Keys.SpellKeys 配置）");
                return;
            }

            await _gate.ExecuteAsync($"选法术槽{spellSlot}", async c =>
            {
                var t0 = DateTime.UtcNow;
                _input.KeyPress(key.Value);
                // 选法术本身不一定发上行包（客户端本地切换），所以只用极短的等待，
                // 真正的确认交给随后的"点目标"一步。
                await Task.Delay(60, c).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);
        }

        // 法术已选中，点目标格施放
        await ClickCellAsync("施法", tx, ty, _cmds.CmSpell, ct).ConfigureAwait(false);
    }

    private async Task EatAsync(int makeIndex, int extra, CancellationToken ct)
    {
        // 优先走快捷键：不受背包是否打开、格子是否被遮挡影响
        if (_cfg.Keys.UsePotionHotkey)
        {
            var key = _cfg.Keys.ResolvePotionKey(makeIndex);
            if (key != null)
            {
                await _gate.ExecuteAsync($"喝药#{makeIndex}", async c =>
                {
                    var t0 = DateTime.UtcNow;
                    _input.KeyPress(key.Value);
                    return await WaitForUpstreamAsync(_cmds.CmEat, t0, c).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
                return;
            }
        }

        // 退化路径：双击背包格
        if (!_cfg.Bag.IsCalibrated)
        {
            Log?.Invoke("[喝药] 未配置药水快捷键且背包未校准，无法用药");
            return;
        }

        await _gate.ExecuteAsync($"双击背包格#{makeIndex}", async c =>
        {
            if (!_ui.AllowsClick) return false;

            // 有些客户端需要先按 F9 打开背包面板
            if (_cfg.Bag.MustOpenPanel && _cfg.Keys.ToggleBag != 0)
            {
                _input.KeyPress(_cfg.Keys.ToggleBag);
                await Task.Delay(180, c).ConfigureAwait(false);
            }

            var (sx, sy) = _mapper.BagSlotCenter(makeIndex);
            var t0 = DateTime.UtcNow;
            if (!_input.ClickClient(sx, sy, doubleClick: true)) return false;

            bool ok = await WaitForUpstreamAsync(_cmds.CmEat, t0, c).ConfigureAwait(false);

            if (_cfg.Bag.MustOpenPanel && _cfg.Keys.ToggleBag != 0)
            {
                _input.KeyPress(_cfg.Keys.ToggleBag);      // 关回去，别把面板留在屏幕上
                await Task.Delay(120, c).ConfigureAwait(false);
            }
            return ok;
        }, ct).ConfigureAwait(false);
    }

    private async Task InteractNpcAsync(int npcX, int npcY, CancellationToken ct)
    {
        var (px, py) = _playerPos();
        int dist = Math.Max(Math.Abs(npcX - px), Math.Abs(npcY - py));

        // 先满足距离闸门，否则点上去服务端不响应（或客户端压根不发包）
        if (dist > NpcInteractRange - 2)
        {
            var (nx, ny) = MapWalkController.IntermediateCell(px, py, npcX, npcY, Math.Min(6, NpcInteractRange - 3));
            var r = await _walk.WalkToAsync(nx, ny, ct).ConfigureAwait(false);
            Log?.Invoke($"[NPC] 靠近至 ({nx},{ny})，结果 {r}（距 NPC {dist} 格）");
            return;   // 靠近之后由上层下一轮再发起交互
        }

        await ClickCellAsync("点NPC", npcX, npcY, _cmds.CmNpcInteract, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 选择对话菜单项。
    /// 优先用**文本**匹配（行号会随翻页/首页变化，文本不会）；
    /// 文本拿不到时才退回行号，并按 MaxLinesPerPage 自动处理翻页。
    ///
    /// 跨服菜单的一大坑：不同服同一张图的名字可能带后缀/前缀（"比奇省" vs "比奇省·新手村"），
    /// 因此包含匹配**只在唯一命中时才允许点击**；多命中一律拒点 —— 菜单点错是真操作。
    /// 返回值：是否真的发出了这次点击（供上层判定"没点中"与"点了但没生效"）。
    /// </summary>
    public Task<bool> TrySelectDialogAsync(int lineIndex, string? text, CancellationToken ct)
        => SelectDialogAsync(lineIndex, text, ct);

    private async Task<bool> SelectDialogAsync(int lineIndex, string? text, CancellationToken ct)
    {
        if (!_cfg.Dialog.IsCalibrated)
        {
            Log?.Invoke("[对话] 对话框未校准，无法选择菜单项");
            return false;
        }

        int effectiveIndex = lineIndex;
        if (_ui.DialogLines.Count == 0)
        {
            // 没有菜单文本 = 只能猜行号，而猜错会真的触发买/卖/传送等操作。
            // 宁可不点：宿主需把 Core 的 NpcMessage 转发到 host.FeedNpcDialog()。
            Log?.Invoke("[对话] 菜单文本为空（宿主是否漏了 NpcMessage → FeedNpcDialog 转发？），拒绝盲点");
            return false;
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            // ① 精确命中（显示文本 / 回传命令完全相等）：无歧义，直接用
            int found = _ui.DialogOptions.FindIndex(o =>
                string.Equals(o.Display, text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(o.Command, text, StringComparison.OrdinalIgnoreCase));

            // ② 退一档做包含匹配，但必须**唯一命中**
            if (found < 0)
            {
                var hits = new List<int>();
                if (_ui.DialogOptions.Count == _ui.DialogLines.Count)
                {
                    for (int i = 0; i < _ui.DialogOptions.Count; i++)
                        if (_ui.DialogOptions[i].Display.Contains(text, StringComparison.OrdinalIgnoreCase)) hits.Add(i);
                }
                if (hits.Count == 0)
                    for (int i = 0; i < _ui.DialogLines.Count; i++)
                        if (_ui.DialogLines[i].Contains(text, StringComparison.OrdinalIgnoreCase)) hits.Add(i);

                if (hits.Count > 1)
                {
                    Log?.Invoke($"[对话] \"{text}\" 命中 {hits.Count} 个候选（{string.Join(" | ", hits.Select(i => _ui.DialogLines[i]))}），"
                              + "无法确定点哪个，拒绝点击（跨服菜单常见同名子图，需上层给更精确的文本）");
                    return false;
                }
                if (hits.Count == 1) found = hits[0];
            }

            if (found < 0)
            {
                Log?.Invoke($"[对话] 菜单里没有 \"{text}\"，拒绝盲点。当前菜单：{string.Join(" | ", _ui.DialogLines)}");
                return false;
            }
            effectiveIndex = found;
        }
        else if (effectiveIndex < 0 || effectiveIndex >= _ui.DialogLines.Count)
        {
            Log?.Invoke($"[对话] 行号 {effectiveIndex} 超出菜单范围（0..{_ui.DialogLines.Count - 1}），拒绝点击");
            return false;
        }

        var (turns, rowInPage) = _mapper.ResolveDialogPage(effectiveIndex);

        var outcome = await _gate.ExecuteAsync($"对话选行{effectiveIndex}", async c =>
        {
            if (_ui.State != UiState.NpcDialog)
            {
                Log?.Invoke($"[对话] 当前不是对话态（{_ui.State}），拒绝点击");
                return false;
            }

            // 翻页
            for (int i = 0; i < turns; i++)
            {
                var (bx, by) = _mapper.NextPageButton();
                if (bx == 0 && by == 0)
                {
                    Log?.Invoke("[对话] 需要翻页但未配置翻页按钮位置");
                    return false;
                }
                _input.ClickClient(bx, by);
                await Task.Delay(220, c).ConfigureAwait(false);
            }

            var (lx, ly) = _mapper.DialogLineCenter(rowInPage);
            var t0 = DateTime.UtcNow;
            if (!_input.ClickClient(lx, ly)) return false;

            // 选完之后服务端会回下一个窗口或换图，因此用"收到任意新上行"作为轻量确认
            return await WaitForAnyUpstreamAsync(t0, c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // 只有"确认点中了"才算真的选了这一项。点空（NoAck）/ 被闸门拦（Rejected）都返回 false，
        // 让上层（NpcTransferRunner / MapEntryProbe）据此判负，而不是误以为已经点过了。
        return outcome == ActionOutcome.Confirmed;
    }

    // ---------------------------------------------------------------- 确认

    /// <summary>等待上行出现指定命令（证明客户端真的接受了这次操作）。</summary>
    private async Task<bool> WaitForUpstreamAsync(ushort cmd, DateTime after, CancellationToken ct)
    {
        if (cmd == 0) return true;     // 该命令码未解析到：无法确认，不做判定（不阻塞流程）

        var deadline = DateTime.UtcNow.AddMilliseconds(_cfg.Behavior.ActionAckTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_probe.ObservedSince(cmd, after)) return true;
            await Task.Delay(45, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>等待任意上行命令（菜单选择这类动作无法预知其命令码）。</summary>
    private async Task<bool> WaitForAnyUpstreamAsync(DateTime after, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(_cfg.Behavior.ActionAckTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_probe.AnyObservedSince(after)) return true;
            await Task.Delay(45, ct).ConfigureAwait(false);
        }
        return false;
    }
}
