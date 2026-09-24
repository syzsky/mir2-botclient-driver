using System.Text.Json;
using System.Text.Json.Serialization;
using BotClient.Assets;
using BotClient.ClientDriver;

namespace BotClient.ClientDriver.Input;

/// <summary>一次"点某个子地图选项"的结局。</summary>
public enum MapEntryStatus
{
    /// <summary>成功进入目标图（图代码与期望一致）。</summary>
    Entered,
    /// <summary>确实换图了，但到的不是期望的那张图 —— 选项与目标图对不上。</summary>
    EnteredOtherMap,
    /// <summary>服务端回绝，没换图。回绝原因（等级/金币/物品/负重不足）在提示正文里。</summary>
    Rejected,
    /// <summary>点下去了，但既没换图也没有提示：多半只是展开了下一级菜单，或该选项无实际效果。</summary>
    NoEffect,
    /// <summary>菜单里没有匹配项（或候选不唯一），没点。</summary>
    NotFound,
    /// <summary>前置步骤失败：没靠近 / 没弹出对话 / 拿不到菜单文本。</summary>
    Aborted,
}

/// <summary>探测结果。无论成功失败都带上证据（到达图代码 + 服务端提示），便于落日志与缓存。</summary>
public sealed record MapEntryOutcome(
    MapEntryStatus Status,
    string MenuText,
    string? ExpectMapCode,
    string ArrivedMapCode,
    string ArrivedMapName,
    string Reason)
{
    public bool Success => Status == MapEntryStatus.Entered;
}

/// <summary>
/// 菜单项 → 目标图的**实测**结论，持久化后下次直接复用，不再拿真实角色去试。
/// </summary>
public sealed class MapEntryCacheEntry
{
    [JsonPropertyName("text")] public string MenuText { get; set; } = string.Empty;
    [JsonPropertyName("code")] public string? MapCode { get; set; }
    [JsonPropertyName("entered")] public bool Entered { get; set; }
    [JsonPropertyName("arrived")] public string ArrivedMapCode { get; set; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("at")] public DateTime At { get; set; }
}

internal sealed class NpcEntryCache
{
    [JsonPropertyName("map")] public string Map { get; set; } = string.Empty;
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("options")] public Dictionary<string, MapEntryCacheEntry> Options { get; set; } = new();
}

/// <summary>
/// 子地图可进入性探测器 —— 专治"跨服 NPC 名称不同 + 点开后一堆子地图，不知道哪个能进"。
///
/// 判定原则（很关键，不要用菜单文字去猜）：
///   **菜单文字只说明"这里有个选项"，能不能进是服务端当场决定的。**
///   所以"可进入"的唯一可信证据是：点下去之后真的收到了换图包，且到达图代码 == 期望代码。
///   被回绝时服务端会回一条提示（SM_MENU_OK，转发进 UiStateProbe.LastSystemMessage），
///   那条提示就是"为什么进不去"（等级不够 / 金币不够 / 需要凭证 / 不是这个时间段）。
///
/// 跨服差异的两道处理：
///   ① NPC 名不写死：按**功能关键词**匹配（"传送" 命中 "传送员/传送使者/传送人"），
///      匹配不到就把当前图所有 NPC 名打出来，别猜；
///   ② 地图名不写死：MapInfo.txt 反查 名字→代码（<see cref="MapInfoFile.TryFindCode"/>），
///      反查不到就退化成"探测式"—— 换图成功即认定可进入，并把 菜单文字→到达代码 写进缓存，
///      下次这个服就不用再探。
///
/// 安全底线：探测会**真实点菜单**（点错就是真传送/真购买），因此
///   只点唯一命中的文本；候选不唯一一律拒点；已知被回绝的选项走缓存不再重复试。
/// </summary>
public sealed class MapEntryProbe
{
    private readonly ClientDriverConfig _cfg;
    private readonly ClientActionDriver _driver;
    private readonly UiStateProbe _ui;
    private readonly NpcTransferRunner _runner;
    private readonly Func<string> _currentMap;
    private readonly MapInfoFile? _mapInfo;
    private readonly string _cachePath;

    private readonly Dictionary<string, NpcEntryCache> _cache = new(StringComparer.OrdinalIgnoreCase);

    public MapEntryProbe(
        ClientDriverConfig cfg,
        ClientActionDriver driver,
        UiStateProbe ui,
        NpcTransferRunner runner,
        Func<string> currentMap,
        MapInfoFile? mapInfo,
        string? cachePath = null)
    {
        _cfg = cfg;
        _driver = driver;
        _ui = ui;
        _runner = runner;
        _currentMap = currentMap;
        _mapInfo = mapInfo;
        _cachePath = cachePath ?? Path.Combine(AppContext.BaseDirectory, "map-entry-cache.json");
        LoadCache();
    }

    public event Action<string>? Log;

    /// <summary>每次点选后观察"换图 / 回绝"的窗口。服务端回绝通常很快，换图稍慢。</summary>
    public int ObserveMs { get; set; } = 2500;

    /// <summary>换图后等新图就绪的最长时间。</summary>
    public int MapSettleMs { get; set; } = 15000;

    /// <summary>被回绝的缓存有效期（分钟）。超时后愿意再试一次：等级/金币/任务状态是会变的。</summary>
    public int RejectedCacheMinutes { get; set; } = 30;

    /// <summary>单轮探测的选项上限，防止菜单特别长时没完没了地真实点击。</summary>
    public int MaxProbesPerRound { get; set; } = 8;

    /// <summary>明确不是"进图"的菜单项关键词（用于筛选候选，避免点到买/卖/修理）。</summary>
    private static readonly string[] NotMapKeywords =
    {
        "购买", "买", "出售", "卖", "修理", "仓库", "存放", "取出", "回收",
        "兑换", "合成", "升级", "学习", "技能", "任务", "接受", "放弃", "取消",
        "关闭", "离开", "返回", "上页", "下页", "下一页", "上一页", "结束", "再见", "谢谢",
    };

    // ================================================================ 一、问一个目标图能不能进

    /// <summary>
    /// 进入（或探测）目标子地图。targetText 可以是地图中文名（"比奇省"）或地图代码（"0"）。
    /// </summary>
    /// <param name="menuPath">
    /// 上级菜单路径，例如 ["传送"]。层级不确定时留空：会在当前级菜单里直接找目标文本。
    /// </param>
    public async Task<MapEntryOutcome> EnterAsync(
        int npcX, int npcY, string targetText, IReadOnlyList<string>? menuPath, CancellationToken ct)
    {
        string? expectCode = _mapInfo?.TryFindCode(targetText);
        if (expectCode is null)
            Log?.Invoke($"[进图] \"{targetText}\" 反查不到地图代码（MapInfo.txt 没读到或没有这张图），"
                      + "退化为探测式判定：只要换图成功就算可进入");

        // 已知被回绝且还在冷却期：直接给结论，不拿真角色再试一次。
        if (TryGetRejected(npcX, npcY, targetText, out var cached))
            return new MapEntryOutcome(MapEntryStatus.Rejected, targetText, expectCode,
                                       cached.ArrivedMapCode, NameOf(cached.ArrivedMapCode),
                                       $"{cached.Reason}（缓存 {cached.At:HH:mm}，{RejectedCacheMinutes} 分钟内不重复尝试）");

        if (!await _runner.EnsureDialogOpenAsync(npcX, npcY, ct).ConfigureAwait(false))
            return new MapEntryOutcome(MapEntryStatus.Aborted, targetText, expectCode, _currentMap(), NameOf(_currentMap()),
                                       "没能和 NPC 说上话（未靠近 / 未弹出对话 / 菜单文本缺失）");

        // 逐级进入上级菜单
        if (menuPath is { Count: > 0 })
        {
            foreach (string level in menuPath)
            {
                if (!await SelectAndWaitRefreshAsync(level, ct).ConfigureAwait(false))
                    return new MapEntryOutcome(MapEntryStatus.NotFound, level, expectCode, _currentMap(), NameOf(_currentMap()),
                                               $"上级菜单 \"{level}\" 没能点开");
            }
        }

        int index = FindOptionIndex(targetText, expectCode);
        if (index < 0)
            return new MapEntryOutcome(MapEntryStatus.NotFound, targetText, expectCode, _currentMap(), NameOf(_currentMap()),
                                       $"当前菜单里没有 \"{targetText}\"（或不唯一）。菜单：{string.Join(" | ", _ui.DialogLines)}");

        var outcome = await ClickAndClassifyAsync(index, _ui.DialogLines[index], expectCode, ct).ConfigureAwait(false);
        SaveOutcome(npcX, npcY, outcome);
        return outcome;
    }

    // ================================================================ 二、把菜单里的候选探一遍

    /// <summary>
    /// 把当前 NPC 菜单里所有"疑似子地图"的选项逐个探一遍，回答"这里面哪些能进"。
    ///
    /// 每个候选都是一次**真实点击**，所以：
    ///   · 只探能反查出地图代码的项（拿不到 MapInfo.txt 时才退化为按关键词排除法）；
    ///   · 已被回绝且仍在冷却期的项跳过（不算点击）；
    ///   · 数量受 <see cref="MaxProbesPerRound"/> 限制。
    /// 探测一个选项后菜单通常已经变了（进图了 / 被关掉了），因此每个候选前都重新把 NPC 点开一遍。
    /// </summary>
    public async Task<List<MapEntryOutcome>> ProbeCandidatesAsync(
        int npcX, int npcY, IReadOnlyList<string>? menuPath, CancellationToken ct)
    {
        var results = new List<MapEntryOutcome>();

        if (!await _runner.EnsureDialogOpenAsync(npcX, npcY, ct).ConfigureAwait(false))
        {
            results.Add(new MapEntryOutcome(MapEntryStatus.Aborted, string.Empty, null, _currentMap(), NameOf(_currentMap()),
                                            "没能和 NPC 说上话"));
            return results;
        }

        if (menuPath is { Count: > 0 })
        {
            foreach (string level in menuPath)
            {
                if (!await SelectAndWaitRefreshAsync(level, ct).ConfigureAwait(false))
                {
                    results.Add(new MapEntryOutcome(MapEntryStatus.NotFound, level, null, _currentMap(), NameOf(_currentMap()),
                                                    "上级菜单没能点开"));
                    return results;
                }
            }
        }

        // 先把候选名单抄下来：点任何一个之后菜单都会变，不能边点边读。
        var candidates = PickCandidates().Take(MaxProbesPerRound).ToList();
        Log?.Invoke($"[进图] 本次候选 {candidates.Count} 项：{string.Join(" | ", candidates)}");

        foreach (string text in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (TryGetRejected(npcX, npcY, text, out var cached))
            {
                results.Add(new MapEntryOutcome(MapEntryStatus.Rejected, text, cached.MapCode,
                                                cached.ArrivedMapCode, NameOf(cached.ArrivedMapCode),
                                                $"{cached.Reason}（缓存）"));
                continue;
            }

            // 每个候选前重新打开对话（上一个候选可能已经把我们送到别的图 / 关掉了对话）
            if (_ui.State != UiState.NpcDialog)
            {
                if (!await _runner.EnsureDialogOpenAsync(npcX, npcY, ct).ConfigureAwait(false))
                {
                    results.Add(new MapEntryOutcome(MapEntryStatus.Aborted, text, null, _currentMap(), NameOf(_currentMap()),
                                                    "重新打开对话失败"));
                    break;
                }
                if (menuPath is { Count: > 0 })
                {
                    bool ok = true;
                    foreach (string level in menuPath)
                        ok &= await SelectAndWaitRefreshAsync(level, ct).ConfigureAwait(false);
                    if (!ok) break;
                }
            }

            string? expect = _mapInfo?.TryFindCode(text);
            int idx = FindOptionIndex(text, expect);
            if (idx < 0)
            {
                results.Add(new MapEntryOutcome(MapEntryStatus.NotFound, text, expect, _currentMap(), NameOf(_currentMap()),
                                                "候选已不在当前菜单里"));
                continue;
            }

            var outcome = await ClickAndClassifyAsync(idx, _ui.DialogLines[idx], expect, ct).ConfigureAwait(false);
            SaveOutcome(npcX, npcY, outcome);
            results.Add(outcome);
            Log?.Invoke($"[进图] {text} → {outcome.Status}"
                      + (string.IsNullOrWhiteSpace(outcome.ArrivedMapCode) ? "" : $"（到达 {outcome.ArrivedMapCode}）")
                      + (string.IsNullOrWhiteSpace(outcome.Reason) ? "" : $" 说明：{outcome.Reason}"));
        }

        return results;
    }

    // ================================================================ 三、候选筛选与文本匹配

    /// <summary>
    /// 挑出"疑似子地图"的菜单项。
    /// 优先用 MapInfo.txt 反查：能反查出地图代码的才算地图项（最准）；
    /// 反查表拿不到时退化为关键词排除法 —— 排除买/卖/仓库/修理这类明确不是进图的项。
    /// </summary>
    public List<string> PickCandidates()
    {
        var list = new List<string>();
        foreach (string line in _ui.DialogLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (_mapInfo is not null && _mapInfo.TryFindCode(line) is not null) { list.Add(line); continue; }
            if (_mapInfo is null && !NotMapKeywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
                list.Add(line);
        }
        // 全是非地图项时不硬凑：宁可空手，也别把"买药"当子地图点。
        return list.Distinct().ToList();
    }

    /// <summary>
    /// 在菜单里定位目标项。顺序：精确相等 → 反查代码相同 → 归一化包含且**唯一**命中。
    /// 多个都命中的情况直接返回 -1：跨服菜单里"比奇省"和"比奇省·新手村"会同时命中，
    /// 猜一个就是一次真实传送。
    /// </summary>
    public int FindOptionIndex(string targetText, string? expectCode)
    {
        for (int i = 0; i < _ui.DialogLines.Count; i++)
            if (string.Equals(_ui.DialogLines[i].Trim(), targetText.Trim(), StringComparison.OrdinalIgnoreCase))
                return i;

        if (expectCode is not null && _mapInfo is not null)
        {
            for (int i = 0; i < _ui.DialogLines.Count; i++)
            {
                var code = _mapInfo.TryFindCode(_ui.DialogLines[i]);
                if (code is not null && NpcTransferRunner.MapCodeEquals(code, expectCode))
                    return i;
            }
        }

        var hits = new List<int>();
        for (int i = 0; i < _ui.DialogLines.Count; i++)
            if (_ui.DialogLines[i].Contains(targetText, StringComparison.OrdinalIgnoreCase)) hits.Add(i);
        if (hits.Count == 1) return hits[0];

        if (hits.Count > 1)
            Log?.Invoke($"[进图] \"{targetText}\" 命中 {hits.Count} 项（{string.Join(" | ", hits.Select(i => _ui.DialogLines[i]))}），不确定点哪个，放弃");

        return -1;
    }

    // ================================================================ 四、点选 + 结果判定

    private async Task<MapEntryOutcome> ClickAndClassifyAsync(int index, string menuText, string? expectCode, CancellationToken ct)
    {
        string before = _currentMap();
        var t0 = _ui.ClearSystemMessage();     // 这条时刻之后出现的提示才算本次点选的结果

        bool clicked = await _driver.TrySelectDialogAsync(index, null, ct).ConfigureAwait(false);
        if (!clicked)
            return new MapEntryOutcome(MapEntryStatus.NotFound, menuText, expectCode, before, NameOf(before),
                                       "点击未发出（对话态不满足 / 未校准 / 多候选）");

        var deadline = DateTime.UtcNow.AddMilliseconds(ObserveMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            _ui.Tick();

            bool changed = _ui.State == UiState.MapLoading
                        || (!string.IsNullOrWhiteSpace(before) && !NpcTransferRunner.MapCodeEquals(_currentMap(), before));

            if (changed)
                return await OnMapChangedAsync(menuText, expectCode, ct).ConfigureAwait(false);

            // 回绝：服务端回了一条提示，且已不在对话态（对话框被关掉 = 这次交互结束）
            if (_ui.LastSystemMessageAt > t0 && _ui.State == UiState.Free)
                return new MapEntryOutcome(MapEntryStatus.Rejected, menuText, expectCode, _currentMap(), NameOf(_currentMap()),
                                           _ui.LastSystemMessage);

            await Task.Delay(60, ct).ConfigureAwait(false);
        }

        // 观察窗内既没换图也没提示
        if (_ui.State == UiState.NpcDialog)
            return new MapEntryOutcome(MapEntryStatus.NoEffect, menuText, expectCode, _currentMap(), NameOf(_currentMap()),
                                       $"菜单已变为：{string.Join(" | ", _ui.DialogLines)}（可能是下级菜单）");

        string pending = _ui.LastSystemMessage;
        return new MapEntryOutcome(MapEntryStatus.NoEffect, menuText, expectCode, _currentMap(), NameOf(_currentMap()),
                                   string.IsNullOrWhiteSpace(pending) ? "无换图、无提示" : pending);
    }

    /// <summary>换图已经确认发生：等新图就绪，再拿到达图代码与期望代码比对。</summary>
    private async Task<MapEntryOutcome> OnMapChangedAsync(string menuText, string? expectCode, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(MapSettleMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            _ui.Tick();
            if (_ui.State == UiState.Free) break;
            await Task.Delay(80, ct).ConfigureAwait(false);
        }
        await Task.Delay(_cfg.Behavior.MapLoadGuardMs, ct).ConfigureAwait(false);

        string arrived = _currentMap();
        string arrivedName = NameOf(arrived);

        if (expectCode is null)
            return new MapEntryOutcome(MapEntryStatus.Entered, menuText, null, arrived, arrivedName,
                                       "探测式：换图成功即认定可进入（无 MapInfo.txt 无法校验是否目标图）");

        if (NpcTransferRunner.MapCodeEquals(arrived, expectCode))
            return new MapEntryOutcome(MapEntryStatus.Entered, menuText, expectCode, arrived, arrivedName, "到达目标图");

        return new MapEntryOutcome(MapEntryStatus.EnteredOtherMap, menuText, expectCode, arrived, arrivedName,
                                   $"实际到达 {arrived}（{arrivedName}），与期望 {expectCode} 不符");
    }

    /// <summary>点一个上级菜单项并等菜单刷新（服务端回下一个窗口）。</summary>
    private async Task<bool> SelectAndWaitRefreshAsync(string text, CancellationToken ct)
    {
        string snapshot = string.Join("|", _ui.DialogLines);   // 点之前先拍快照，否则菜单一变就比不出来

        if (!await _driver.TrySelectDialogAsync(-1, text, ct).ConfigureAwait(false)) return false;

        var deadline = DateTime.UtcNow.AddMilliseconds(2500);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            _ui.Tick();
            if (_ui.State is UiState.MapLoading or UiState.Free) return true;
            if (_ui.DialogLines.Count > 0 && string.Join("|", _ui.DialogLines) != snapshot) return true;
            await Task.Delay(60, ct).ConfigureAwait(false);
        }
        return _ui.State == UiState.NpcDialog;   // 菜单没变但还在对话态：当作成功，交给下一步再判断
    }

    private string NameOf(string code) => _mapInfo?.NameOf(code) ?? code;

    // ================================================================ 五、缓存（把实测结论固化下来）

    private string KeyFor(int npcX, int npcY)
    {
        string map = _currentMap();
        return string.IsNullOrWhiteSpace(map) ? $"@{npcX},{npcY}" : $"{map}@{npcX},{npcY}";
    }

    private bool TryGetRejected(int npcX, int npcY, string text, out MapEntryCacheEntry entry)
    {
        entry = new MapEntryCacheEntry();
        if (!_cache.TryGetValue(KeyFor(npcX, npcY), out var npc)) return false;
        if (!npc.Options.TryGetValue(text, out var hit)) return false;
        if (hit.Entered) { entry = hit; return false; }                       // 可进入：不拦，正常流程会再点一次
        if ((DateTime.UtcNow - hit.At).TotalMinutes > RejectedCacheMinutes) return false;
        entry = hit;
        return true;
    }

    private void SaveOutcome(int npcX, int npcY, MapEntryOutcome o)
    {
        if (string.IsNullOrWhiteSpace(o.MenuText)) return;

        string key = KeyFor(npcX, npcY);
        if (!_cache.TryGetValue(key, out var npc))
            _cache[key] = npc = new NpcEntryCache { Map = _currentMap(), X = npcX, Y = npcY };

        npc.Options[o.MenuText] = new MapEntryCacheEntry
        {
            MenuText = o.MenuText,
            MapCode = o.ExpectMapCode,
            Entered = o.Status is MapEntryStatus.Entered or MapEntryStatus.EnteredOtherMap,
            ArrivedMapCode = o.ArrivedMapCode,
            Reason = o.Reason,
            At = DateTime.UtcNow,
        };
        SaveCache();
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            string json = File.ReadAllText(_cachePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, NpcEntryCache>>(json);
            if (data is null) return;
            foreach (var (k, v) in data) _cache[k] = v;
            Log?.Invoke($"[进图] 已载入子地图缓存 {_cache.Count} 个 NPC（{_cachePath}）");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[进图] 读缓存失败（忽略，不影响运行）：{ex.Message}");
        }
    }

    private void SaveCache()
    {
        try
        {
            string dir = Path.GetDirectoryName(_cachePath) ?? ".";
            Directory.CreateDirectory(dir);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[进图] 写缓存失败（忽略，不影响运行）：{ex.Message}");
        }
    }
}

/// <summary>
/// 跨服 NPC 名匹配。
///
/// 各服的 NPC 显示名不一样（"传送员" / "传送使者" / "传送人"），但**功能核心词**是一致的。
/// 因此不要按完整名字找 NPC，而是按功能关键词做归一化包含匹配；匹配不到时把该图全部 NPC 名
/// 打出来（日志就是诊断），而不是退化成"随便找一个最近的 NPC 点"。
/// </summary>
public static class NpcNameMatcher
{
    /// <summary>去掉空白与常见装饰符，便于"传 送 员"这类带空格的显示名也能匹配。</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            if (!char.IsWhiteSpace(c) && c is not ('┃' or '│' or '|' or '　'))
                sb.Append(c);
        return sb.ToString();
    }

    /// <summary>功能关键词命中（"传送员" 命中关键词 "传送"）。</summary>
    public static bool Matches(string? npcName, string keyword)
    {
        string n = Normalize(npcName);
        string k = Normalize(keyword);
        return n.Length > 0 && k.Length > 0 && n.Contains(k, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从一份 NPC 名单里挑出匹配关键词的（名字 + 坐标）。返回多个时由调用方按距离择优 ——
    /// 同一张图可能有两个传送员（一个去主城、一个去副本），随便挑一个会走错分支。
    /// </summary>
    public static List<(string Name, int X, int Y)> FindMatches(
        IEnumerable<(string Name, int X, int Y)> npcs, string keyword)
        => npcs.Where(n => Matches(n.Name, keyword)).ToList();
}
