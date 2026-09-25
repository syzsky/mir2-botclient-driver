using BotClient.ClientDriver.Input;
using BotClient.Session;

namespace BotClient.ClientDriver.Hunt;

/// <summary>一层挂机的结果。</summary>
public sealed class HuntFloorReport
{
    public int Depth { get; init; }
    public string MapCode { get; init; } = string.Empty;
    public string MapName { get; init; } = string.Empty;
    public int Killed { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string Note { get; set; } = string.Empty;
    public bool Descended { get; set; }

    public override string ToString()
        => $"第{Depth}层 {MapName}({MapCode}) 击杀 {Killed} 只，耗时 {Elapsed.TotalSeconds:F0}s"
           + (string.IsNullOrWhiteSpace(Note) ? string.Empty : $"｜{Note}");
}

/// <summary>一轮挂机任务的汇总。</summary>
public sealed class HuntReport
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<HuntFloorReport> Floors { get; } = new();
}

/// <summary>
/// 挂机任务编排层：把"找传送员 → 按地图名进图 → 清怪 → 逐层下探"串成一条链。
///
/// 职责边界（刻意划清）：
///   · **战斗**不在这里 —— 清怪由 BotCombatAI 照常执行（本层只负责观察"场上还有没有怪"）；
///   · **换图**不在这里 —— 交给 NpcTransferRunner / MapEntryProbe，本层只负责选 NPC、选目标图名；
///   · 本层只做三件事：按等级门槛放行、按名字找传送 NPC、按"菜单文本"决定进哪张图 / 下哪一层。
///
/// 换地图只需换 <see cref="HuntPlan.TargetMapText"/>，**不需要改代码**：
/// 进图判定统一走 MapEntryProbe（菜单文本 → 图代码反查 → 换图后校验到的是不是目标图），
/// 因此跨服菜单文字不同也只是"点不中"，不会变成"点错图还不知道"。
///
/// 与 AI 的互斥：传送 / 下钻期间用 <see cref="BotCombatAI.BeginManualOverride"/> 让 AI 让出点击权，
/// 否则 AI 的追怪点击会和对话菜单点击抢同一只手（真机上是同一个鼠标）。
/// </summary>
public sealed class HuntTaskRunner
{
    private readonly BotRuntime _runtime;
    private readonly ClientDriverHost _host;
    private readonly BotCombatAI? _ai;

    private int _killed;
    private bool _aiWarned;

    public HuntTaskRunner(BotRuntime runtime, ClientDriverHost host, BotCombatAI? ai = null)
    {
        _runtime = runtime;
        _host = host;
        _ai = ai;
    }

    public event Action<string>? Log;

    /// <summary>找 NPC 时的最大尝试个数（同一张图可能有多个传送员，按距离从近到远试）。</summary>
    public int MaxNpcTries { get; set; } = 3;

    /// <summary>本层清怪结束后，等 UI 从对话态恢复自由态的上限（探测菜单不点选时要收回对话）。</summary>
    public int DialogRecoverTimeoutMs { get; set; } = 12_000;

    public int TotalKilled => _killed;

    // ================================================================ 主流程

    /// <summary>跑一轮：进目标图 → 清当前层 → 若有下层则下探，直到层数上限或没有下层。</summary>
    public async Task<HuntReport> RunAsync(HuntPlan plan, CancellationToken ct)
    {
        var report = new HuntReport();

        if (string.IsNullOrWhiteSpace(plan.TargetMapText))
        {
            report.Summary = "未指定目标地图名，无法挂机";
            Emit(report.Summary);
            return report;
        }

        // ---- 等级门槛：不够级就不点。拿真角色去撞服务端回绝既浪费一次换图，也可能被踢 ----
        int level = _runtime.Player.Level;
        if (plan.MinLevel > 0 && level > 0 && level < plan.MinLevel)
        {
            report.Summary = $"当前 {level} 级，低于 {plan.TargetMapText} 的门槛 {plan.MinLevel} 级，本轮不执行";
            Emit(report.Summary);
            return report;
        }

        _runtime.MonsterKilled += OnMonsterKilled;

        try
        {
            // ---- 第一步：确保站在目标图 ----
            if (!AlreadyAtTarget(plan, out string here))
            {
                Emit($"当前在 {here}，准备进 {plan.TargetMapText}");
                var entry = await EnterTargetAsync(plan, ct).ConfigureAwait(false);
                if (!entry.Ok)
                {
                    report.Summary = $"进 {plan.TargetMapText} 失败：{entry.Note}";
                    Emit(report.Summary);
                    return report;
                }
                report.Success = true;
            }
            else
            {
                Emit($"已在目标图 {here}，直接开始清怪");
                report.Success = true;
            }

            // ---- 第二步：逐层清怪 + 下探 ----
            int maxDepth = Math.Max(1, plan.Descend ? plan.MaxDepth : 1);
            for (int depth = 1; depth <= maxDepth; depth++)
            {
                ct.ThrowIfCancellationRequested();

                if (_runtime.SelfIsDead)
                {
                    report.Summary = $"第{depth}层前角色已死亡，本轮结束（等复活后再跑）";
                    Emit(report.Summary);
                    return report;
                }

                string mapCode = _runtime.CurrentMap;
                string mapName = _runtime.Player.MapName;
                var started = DateTime.UtcNow;
                int killed = await FightFloorAsync(plan, ct).ConfigureAwait(false);

                var floor = new HuntFloorReport
                {
                    Depth = depth,
                    MapCode = mapCode,
                    MapName = string.IsNullOrWhiteSpace(mapName) ? mapCode : mapName,
                    Killed = killed,
                    Elapsed = DateTime.UtcNow - started,
                };
                report.Floors.Add(floor);
                Emit(floor.ToString());

                if (!plan.Descend)
                {
                    floor.Note = "按配置不下探";
                    break;
                }

                if (depth >= maxDepth)
                {
                    floor.Note = $"已达下探层数上限 {maxDepth}";
                    break;
                }

                var down = await TryDescendAsync(plan, ct).ConfigureAwait(false);
                if (!down.Ok)
                {
                    floor.Note = down.Note;
                    break;
                }

                floor.Descended = true;
                floor.Note = $"已下探至 {down.MapName}({down.MapCode})，入口 \"{down.MenuText}\"";
            }

            report.Summary = BuildSummary(report, plan);
            return report;
        }
        finally
        {
            _runtime.MonsterKilled -= OnMonsterKilled;
        }
    }

    private string BuildSummary(HuntReport report, HuntPlan plan)
    {
        if (report.Floors.Count == 0) return $"未进入任何一层（目标图 {plan.TargetMapText}）";
        var last = report.Floors[^1];
        int total = report.Floors.Sum(g => g.Killed);
        return $"本轮合计 {report.Floors.Count} 层，击杀 {total} 只，最深 {last.MapName}({last.MapCode})"
               + (string.IsNullOrWhiteSpace(last.Note) ? string.Empty : $"｜结束原因：{last.Note}");
    }

    // ================================================================ 进目标图

    private (bool Ok, string Note) _lastEntry;

    private async Task<(bool Ok, string Note)> EnterTargetAsync(HuntPlan plan, CancellationToken ct)
    {
        var npcs = FindTransferNpcs(plan);
        if (npcs.Count == 0)
        {
            return (false, $"当前地图视野内没有名字含 \"{plan.NpcKeyword}\" 的 NPC{AllNpcNames()}"
                         + "（若刚换图，稍等小地图对象到齐；若在野外，需先回城或走到传送员附近）");
        }

        foreach (var npc in npcs)
        {
            ct.ThrowIfCancellationRequested();
            Emit($"尝试传送员 {npc.Name}({npc.X},{npc.Y})");

            // 换图期间 AI 必须让出点击权：黑屏加载中乱点会被客户端丢弃，甚至点到别的地方
            using (_ai?.BeginManualOverride())
            {
                var outcome = await _host.MapEntry
                    .EnterAsync(npc.X, npc.Y, plan.TargetMapText, plan.MenuPath, ct)
                    .ConfigureAwait(false);

                if (outcome.Success)
                {
                    Emit($"进图成功：{outcome.ArrivedMapName}({outcome.ArrivedMapCode})");
                    return (true, string.Empty);
                }

                Emit($"传送员 {npc.Name} 未能进入 {plan.TargetMapText}：[{outcome.Status}] {outcome.Reason}");
                _lastEntry = (false, outcome.Reason);
            }

            if (ct.IsCancellationRequested) break;
        }

        string note = _lastEntry.Note;
        if (string.IsNullOrWhiteSpace(note)) note = "所有候选传送员都未能进入目标图";
        return (false, note);
    }

    private bool AlreadyAtTarget(HuntPlan plan, out string here)
    {
        string code = _runtime.CurrentMap;
        string name = _runtime.Player.MapName;
        here = string.IsNullOrWhiteSpace(name)
            ? (string.IsNullOrWhiteSpace(code) ? "未知地图（还没读到封包）" : code)
            : $"{name}({code})";

        if (string.IsNullOrWhiteSpace(code)) return false;

        string? expectCode = _host.MapInfo?.TryFindCode(plan.TargetMapText);
        if (expectCode is not null && NpcTransferRunner.MapCodeEquals(code, expectCode)) return true;

        // 反查不到代码（没读 MapInfo.txt）时退化为名称包含判断：名字已对上就别再传送一遍
        return !string.IsNullOrWhiteSpace(name)
               && name.Contains(plan.TargetMapText, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ 清怪一层的观察

    /// <summary>
    /// 观察一层的清怪过程。战斗本身由 BotCombatAI 执行，这里只做两件判定：
    ///   · 视野内怪物为 0 且持续 <see cref="HuntPlan.FloorClearIdleMs"/> → 判清完；
    ///   · 到达 <see cref="HuntPlan.FloorMaxMs"/> → 收工（不硬等某只打不动的怪）。
    /// </summary>
    private async Task<int> FightFloorAsync(HuntPlan plan, CancellationToken ct)
    {
        if (_ai is { Enabled: false } && !_aiWarned)
        {
            _aiWarned = true;
            Emit("提示：战斗 AI 未启动（--sniff-only？），本层只会空等 —— 清怪需要 AI 在跑");
        }

        int before = _killed;
        var deadline = DateTime.UtcNow.AddMilliseconds(plan.FloorMaxMs);
        DateTime? emptySince = null;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();

            if (_runtime.SelfIsDead) break;

            int monsters = CountMonsters();
            if (monsters == 0)
            {
                emptySince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - emptySince.Value >= TimeSpan.FromMilliseconds(plan.FloorClearIdleMs))
                {
                    Emit("本层视野内已无怪物，判定清完");
                    break;
                }
            }
            else
            {
                emptySince = null;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return _killed - before;
    }

    private int CountMonsters()
    {
        int n = 0;
        foreach (var d in _runtime.Dots.Values)
            if (d.Kind == DotKind.Monster) n++;
        return n;
    }

    // ================================================================ 下探下层

    private async Task<(bool Ok, string Note, string MenuText, string MapCode, string MapName)> TryDescendAsync(
        HuntPlan plan, CancellationToken ct)
    {
        var npcs = FindTransferNpcs(plan);
        if (npcs.Count == 0)
            return Fail($"本层视野内没有 \"{plan.NpcKeyword}\" 类 NPC，无法判断是否有下层{AllNpcNames()}");

        foreach (var npc in npcs)
        {
            ct.ThrowIfCancellationRequested();

            using (_ai?.BeginManualOverride())
            {
                if (!await _host.Transfer.EnsureDialogOpenAsync(npc.X, npc.Y, ct).ConfigureAwait(false))
                {
                    Emit($"传送员 {npc.Name} 没能打开对话（可能不在视野/超距），换下一个");
                    continue;
                }

                var found = await FindDescendEntryAsync(plan, ct).ConfigureAwait(false);
                if (found.Option.Length == 0)
                {
                    Emit($"传送员 {npc.Name} 的菜单里没有下层入口：{string.Join(" | ", _host.Ui.DialogLines)}");
                    await RecoverFromDialogAsync(ct).ConfigureAwait(false);
                    continue;
                }

                var steps = found.Prefix.Concat(new[] { found.Option }).ToArray();
                string beforeMap = _runtime.CurrentMap;
                Emit($"发现下层入口 \"{string.Join(" → ", steps)}\"，尝试下探");

                bool ok = await _host.Transfer
                    .TransferAsync(npc.X, npc.Y, steps, null, ct)
                    .ConfigureAwait(false);

                if (!ok)
                {
                    Emit($"下层入口 \"{string.Join(" → ", steps)}\" 没能进入，换下一个候选");
                    continue;
                }

                string afterMap = _runtime.CurrentMap;
                if (NpcTransferRunner.MapCodeEquals(beforeMap, afterMap))
                {
                    // 换了个名字相同的图 / 只是重载：不算下探成功，否则会在同一层反复空转
                    Emit($"入口 \"{string.Join(" → ", steps)}\" 点选后地图未变化（{afterMap}），不视为下探成功");
                    continue;
                }

                string name = _runtime.Player.MapName;
                return (true, string.Empty, string.Join(" → ", steps), afterMap, string.IsNullOrWhiteSpace(name) ? afterMap : name);
            }
        }

        return Fail("候选传送员都没有可用的下层入口，挂机到最深一层结束");
    }

    private static (bool Ok, string Note, string MenuText, string MapCode, string MapName) Fail(string note)
        => (false, note, string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// 找"下层入口"，支持"分类 → 子地图"这种两级菜单：
    ///   ① 当前层菜单里就直接含 下一层/二层 这类关键词 → 命中即返回（路径最短，风险最低）；
    ///   ② 没命中就按 <see cref="HuntPlan.MenuPath"/> 逐级点开分类（只点分类、不点地图），点开后重新读菜单再找关键词。
    /// 返回 Prefix = 已点开的分类路径；调用方用 Prefix + Option 完整走一遍传送流程
    /// （传送流程会重新点 NPC 从头走，路径一致，所以中途点开的分类不会成为残留状态）。
    /// </summary>
    private async Task<(string Option, List<string> Prefix)> FindDescendEntryAsync(HuntPlan plan, CancellationToken ct)
    {
        var prefix = new List<string>();

        for (int level = 0; level <= plan.MenuPath.Count; level++)
        {
            string option = PickDescendOption(plan);
            if (option.Length > 0) return (option, prefix);

            if (level >= plan.MenuPath.Count) break;

            string category = plan.MenuPath[level];
            if (!MenuContains(category))
            {
                if (level == 0) Emit($"本层菜单里没有分类 \"{category}\"（若分类名不同，用 --hunt-menu 调整）");
                break;
            }

            if (!await _host.SelectDialogAsync(category, ct).ConfigureAwait(false))
            {
                Emit($"分类 \"{category}\" 没能点开，不再往下找");
                break;
            }

            prefix.Add(category);
            await Task.Delay(800, ct).ConfigureAwait(false);   // 等服务端把下一级菜单发回来
        }

        return (string.Empty, prefix);
    }

    private bool MenuContains(string text)
    {
        string want = text.Trim();
        if (want.Length == 0) return false;

        var lines = _host.Ui.DialogLines;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i]?.Trim() ?? string.Empty;
            if (line.Length == 0) continue;
            if (string.Equals(line, want, StringComparison.OrdinalIgnoreCase)) return true;
            if (line.Contains(want, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>在当前菜单里挑"下层入口"。命中多个取最靠前的（菜单顺序通常由浅到深）。</summary>
    private string PickDescendOption(HuntPlan plan)
    {
        var lines = _host.Ui.DialogLines;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i]?.Trim() ?? string.Empty;
            if (line.Length == 0) continue;

            foreach (string kw in plan.DescendKeywords)
            {
                if (string.IsNullOrWhiteSpace(kw)) continue;
                if (line.Contains(kw, StringComparison.OrdinalIgnoreCase)) return line;
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// 探测完菜单却没点（本层没有下层）时把对话收回来。
    /// 不点选时客户端对话窗会一直占着，而 UiStateProbe 在非自由态会拦住 AI 的所有点击 ——
    /// 所以这里必须等它自己超时回自由态，否则"没有下层"会变成"挂机卡死"。
    /// </summary>
    private async Task RecoverFromDialogAsync(CancellationToken ct)
    {
        if (_host.Ui.State == UiState.Free) return;

        var deadline = DateTime.UtcNow.AddMilliseconds(DialogRecoverTimeoutMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_host.Ui.State == UiState.Free) return;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        Emit("对话窗仍在，后续点击可能被拦截（可手动关一下对话窗）");
    }

    // ================================================================ NPC 检索

    private List<MapDot> FindTransferNpcs(HuntPlan plan)
    {
        int px = _runtime.Player.PosX;
        int py = _runtime.Player.PosY;

        return _runtime.Dots.Values
            .Where(d => d.Kind == DotKind.Npc && NpcNameMatcher.Matches(d.Name, plan.NpcKeyword))
            .OrderBy(d => Math.Max(Math.Abs(d.X - px), Math.Abs(d.Y - py)))
            .Take(Math.Max(1, MaxNpcTries))
            .ToList();
    }

    /// <summary>找不到 NPC 时把当前图所有 NPC 名打出来 —— 日志就是诊断，省得用户猜关键词该怎么填。</summary>
    private string AllNpcNames()
    {
        var names = _runtime.Dots.Values
            .Where(d => d.Kind == DotKind.Npc && !string.IsNullOrWhiteSpace(d.Name))
            .Select(d => d.Name)
            .Distinct()
            .ToList();

        return names.Count == 0
            ? "（当前视野内一个小地图 NPC 都没有）"
            : $"；本图已知 NPC：{string.Join(" | ", names)}";
    }

    private void OnMonsterKilled(long id, string name) => _killed++;

    private void Emit(string message) => Log?.Invoke(message);
}
