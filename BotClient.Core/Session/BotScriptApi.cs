using System.Text;
using BotClient.Protocol;
using BotClient.Script;

namespace BotClient.Session;

/// <summary>ScriptEngine 的真机落地端:把天骥的脚本命令一个个发到 gxx 服务端。
/// 三条从真机基准里学到的硬规矩,整个文件都围着它们转:
/// ① NPC 交互超过 15 格服务端整包丢弃且零回包 ⇒ 每个 NPC 动作前必须先走到 14 格内;
/// ② 服务端每点一个菜单项就翻页(ObjNpc.pas 的 LableIsCanJmp),同一页上连点两项第二项无回包
///    ⇒ 每次进商店/仓库都重新 CM_CLICKNPC 取一页,菜单缓存只撑一次操作;
/// ③ 走路 490ms/步、慢速发送保护会掐掉连发的交易包 ⇒ 每步 700ms、每个发包动作之间留 400ms。
/// 取值函数(IValueSource)和命令(IScriptApi)放在同一个对象上:引擎只拿一个 Src,少一处对不上。
/// </summary>
public sealed class BotScriptApi : IScriptApi, IValueSource, IDisposable
{
    private const int WalkStepMs = 700;
    private const int NpcNear = 14;        // 服务端闸门 15 格,留 1 格余量
    private const int ReplyMs = 6000;      // 等一个对话/交易回包的上限
    private const int SendGapMs = 400;
    private const int MapSwitchMs = 2500;  // 换图后服务端整批重推视野,要等这一批到齐

    private readonly BotRuntime _rt;
    private readonly BotCombatAI? _ai;
    private readonly CharData? _ch;
    private readonly object _sync = new();

    // ---- 回包台账:命令一律用"取标记 → 发包 → 等到数量变大"判断这一发确实有回音。
    //      不能只看"某个快照非空":那可能是上一次操作留下的。 ----
    private readonly List<(long Id, string Text)> _says = new();
    private readonly List<(long Id, string Text)> _goods = new();
    private readonly List<(long Id, int Count)> _details = new();
    private readonly List<long> _sellMode = new();
    private readonly List<long> _repairMode = new();
    private readonly List<int> _sellPrices = new();
    private readonly List<int> _repairCosts = new();
    private readonly List<string> _buyResults = new();
    private readonly List<string> _sellResults = new();
    private readonly List<string> _repairResults = new();
    private readonly List<string> _storageResults = new();
    private readonly List<string> _eatResults = new();
    private readonly List<string> _takeOnResults = new();
    private readonly List<string> _sysMsgs = new();
    private int _storageTicks;
    private (long Id, IReadOnlyList<GxxPayload.DetailGoods> Items, int Start)? _lastDetail;

    /// <summary>NPC 菜单缓存(对象ID → 选项)。换图后作废:服务端对象ID 和新图上的 NPC 全无对应关系。</summary>
    private readonly Dictionary<long, (List<NpcOption> Opts, long AtMs)> _menus = new();

    /// <summary>最后一次点开对话框的 NPC:选择/选择位置/[存在选择内容] 都只对着这一页说话。</summary>
    private long _curNpcId;
    private readonly Dictionary<long, (List<GxxPayload.MerchantGoods> Goods, long At)> _goodsCache = new();

    /// <summary>脚本里 系统显示[..] 的内容。WPF 面板订阅它(和 聊天/系统消息 分屏显示)。</summary>
    public event Action<string>? Displayed;

    /// <summary>脚本自己管的开关(挖矿/闪避/穿怪/编组… 服务端没有对应上行包的那些)。</summary>
    public Dictionary<string, bool> Flags { get; } = new(StringComparer.Ordinal);

    public int UnsupportedCount { get; private set; }
    public long LastAttackTargetId { get; private set; }

    /// <summary>脚本文件的读法。默认从角色目录取(天骥布局);WPF 可以换成"当前编辑器里的那份"。</summary>
    public Func<string, IReadOnlyList<string>?>? ScriptReader { get; set; }

    /// <summary>小退/重连的请求:脚本只说"下线 N 秒",真正断开重连的是 BotLoginFlow。</summary>
    public event Action<int>? ReloginRequested;

    public BotScriptApi(BotRuntime runtime, BotCombatAI? ai = null, CharData? ch = null)
    {
        _rt = runtime;
        _ai = ai;
        _ch = ch;
        Src = this;
        _rt.NpcMessage += OnSay;
        _rt.NpcGoodsList += OnGoods;
        _rt.NpcDetailGoodsList += OnDetail;
        _rt.SellModeEntered += OnSellMode;
        _rt.RepairModeEntered += OnRepairMode;
        _rt.SellPriceQuoted += OnSellPrice;
        _rt.RepairCostQuoted += OnRepairCost;
        _rt.BuyResult += OnBuy;
        _rt.SellResult += OnSell;
        _rt.RepairResult += OnRepair;
        _rt.StorageResult += OnStorage;
        _rt.EatResult += OnEat;
        _rt.TakeOnResult += OnTakeOn;
        _rt.SystemMessage += OnSys;
        _rt.StorageItemsChanged += OnStorageList;
    }

    public IValueSource Src { get; }

    public void Dispose()
    {
        // 随机移动是后台循环:脚本被停掉时它必须一起停,否则角色会一直自己乱走。
        StopRandomMove();
        _rt.NpcMessage -= OnSay;
        _rt.NpcGoodsList -= OnGoods;
        _rt.NpcDetailGoodsList -= OnDetail;
        _rt.SellModeEntered -= OnSellMode;
        _rt.RepairModeEntered -= OnRepairMode;
        _rt.SellPriceQuoted -= OnSellPrice;
        _rt.RepairCostQuoted -= OnRepairCost;
        _rt.BuyResult -= OnBuy;
        _rt.SellResult -= OnSell;
        _rt.RepairResult -= OnRepair;
        _rt.StorageResult -= OnStorage;
        _rt.EatResult -= OnEat;
        _rt.TakeOnResult -= OnTakeOn;
        _rt.SystemMessage -= OnSys;
        _rt.StorageItemsChanged -= OnStorageList;
    }

    // 收包线程是在 BotRuntime 的 _itemListLock 里触发这些回调的,所以它们只准往台账里塞一条:
    // 在回调里再拿 _sync 去调 Snapshot*()(它要 _itemListLock)就和"脚本线程持 _sync 等 _itemListLock"反向成环。
    private void OnSay(long id, string txt) { lock (_sync) _says.Add((id, txt)); }
    private void OnGoods(long id, string raw) { lock (_sync) _goods.Add((id, raw)); }
    private void OnDetail(long id, IReadOnlyList<GxxPayload.DetailGoods> items, int start)
    {
        lock (_sync)
        {
            _details.Add((id, items.Count));
            _lastDetail = (id, items, start);
        }
    }
    private void OnSellMode(long id) { lock (_sync) _sellMode.Add(id); }
    private void OnRepairMode(long id) { lock (_sync) _repairMode.Add(id); }
    private void OnSellPrice(int p) { lock (_sync) _sellPrices.Add(p); }
    private void OnRepairCost(int c) { lock (_sync) _repairCosts.Add(c); }
    private void OnBuy(bool ok, string m) { lock (_sync) _buyResults.Add((ok ? "买成功 " : "买失败 ") + m); }
    private void OnSell(bool ok, string m) { lock (_sync) _sellResults.Add((ok ? "卖成功 " : "卖失败 ") + m); }
    private void OnRepair(bool ok, string m) { lock (_sync) _repairResults.Add((ok ? "修好 " : "修失败 ") + m); }
    private void OnStorage(bool ok, string m) { lock (_sync) _storageResults.Add((ok ? "存成功 " : "存失败 ") + m); }
    private void OnEat(bool ok, string m) { lock (_sync) _eatResults.Add((ok ? "用成功 " : "用失败 ") + m); }
    private void OnTakeOn(int makeIndex, int slot, bool ok) { lock (_sync) _takeOnResults.Add((ok ? "穿上槽" : "穿不上槽") + slot); }
    private void OnSys(string msg) { lock (_sync) _sysMsgs.Add(msg); }
    private void OnStorageList() { lock (_sync) _storageTicks++; }

    private int Mark<T>(List<T> list) { lock (list) return list.Count; }
    private int Count<T>(List<T> list) { lock (list) return list.Count; }
    private T[] Take<T>(List<T> list, int from) { lock (list) return list.Skip(from).ToArray(); }
    private int CountGoods(long merchantId) { lock (_goods) return _goods.Count(g => g.Id == merchantId); }
    private string LastGoodsText(long merchantId, int from)
    {
        lock (_goods)
        {
            var rows = _goods.Where(g => g.Id == merchantId).Skip(from).ToArray();
            return rows.Length > 0 ? rows[^1].Text : string.Empty;
        }
    }

    /// <summary>同一条日志在 TraceWindowMs 内只落一次。脚本一轮只有几百毫秒,而失败原因(视野里没这个
    /// NPC、doorlink 里没有门点链…)会一轮报一遍,真机实测一秒上万行,日志文件和界面队列都撑不住。
    /// 逐条对拍细节时把窗口设成 0 就恢复全量。</summary>
    internal static int TraceWindowMs = 5000;
    private readonly Dictionary<string, long> _traceAt = new(StringComparer.Ordinal);

    /// <summary>被窗口内去重咽掉的日志行数(整个进程累计)。自检拿它确认"脚本确实在反复撞同一个失败,
    /// 而日志只落了一条" —— 没有这个数,刷屏为 0 也可能是脚本压根没跑。</summary>
    public static long SuppressedTraces;

    private void Trace(string s)
    {
        int win = TraceWindowMs;
        if (win > 0)
        {
            long now = Environment.TickCount64;
            lock (_traceAt)
            {
                if (_traceAt.TryGetValue(s, out long at) && now - at < win)
                {
                    Interlocked.Increment(ref SuppressedTraces);
                    return;
                }
                if (_traceAt.Count > 800) _traceAt.Clear();
                _traceAt[s] = now;
            }
        }
        BotLog.Info(s);
    }

    // ============================================================
    //  引擎要的最小三件
    // ============================================================
    public long NowMs => Environment.TickCount64;

    public void Display(string text)
    {
        Trace($"[脚本显示] {text}");
        Displayed?.Invoke(text);
    }

    public async Task SayAsync(string text, CancellationToken ct)
    {
        // 天骥的 说话[..] 什么前缀都往里塞:@命令 / //通知 / 普通聊天,全部走 CM_SAY(服务端 ClientSay 自己分流)
        await _rt.SendChatAsync(text, ct).ConfigureAwait(false);
        Trace($"[脚本] 说话: {text}");
        await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
    }

    public Task WaitAsync(int ms, CancellationToken ct)
        => ms <= 0 ? Task.CompletedTask : Task.Delay(ms, ct);

    // ============================================================
    //  移动
    // ============================================================
    public async Task WalkToAsync(string map, int x, int y, bool fight, bool near, CancellationToken ct)
    {
        if (near)
        {
            // "到附近就行":停在目标格靠我方一侧 2 格 —— 最后一 step 要踩到 NPC/门点那一格时服务端会丢
            x -= Math.Sign(x - _rt.Player.PosX) * 2;
            y -= Math.Sign(y - _rt.Player.PosY) * 2;
        }
        if (map.Length > 0 && !SameMap(map))
        {
            await RouteCrossMapAsync(map, ct).ConfigureAwait(false);
            if (!SameMap(map)) { Trace($"[脚本] 走到 {map} 失败:doorlink.ini 里没有可用的门点链,停在原地"); return; }
        }
        if (fight) { await WalkFightingAsync(x, y, ct).ConfigureAwait(false); return; }
        using (_ai?.BeginManualOverride())
        {
            bool ok = await _rt.WalkToAsync(x, y, _rt.IsWalkable ?? ((_, _) => true), WalkStepMs, ct).ConfigureAwait(false);
            Trace($"[脚本] 走到 ({x},{y}) => {ok},现在 ({_rt.Player.PosX},{_rt.Player.PosY})");
        }
    }

    public async Task WalkToNpcAsync(string npc, string map, int x, int y, CancellationToken ct)
    {
        var dot = await ResolveNpcAsync(npc, map, x, y, ct).ConfigureAwait(false);
        if (dot == null) { Trace($"[脚本] 走到NPC附近:找不到 {npc}"); return; }
        await NearAsync(dot.Value, ct).ConfigureAwait(false);
    }

    public async Task DoorToDoorAsync(string mapA, int x1, int y1, string mapB, int x2, int y2, bool fight, CancellationToken ct)
    {
        string oldMap = _rt.CurrentMap;
        if (SameMap(mapA))
        {
            using (_ai?.BeginManualOverride())
                await _rt.WalkToAsync(x1, y1, _rt.IsWalkable ?? ((_, _) => true), WalkStepMs, ct).ConfigureAwait(false);
        }
        // 服务端把"踩上连通格"当作过关门点(地图的 CONNECT 表),所以走过去之后还要朝 8 个方向各试一步。
        for (int round = 0; round < 3 && SameMap(oldMap) && !ct.IsCancellationRequested; round++)
        {
            for (int d = 0; d < 8 && SameMap(oldMap); d++)
            {
                var (dx, dy) = DirDelta(d);
                await _rt.SendWalkAsync(x1 + dx, y1 + dy, (byte)d, ct).ConfigureAwait(false);
                await Task.Delay(WalkStepMs, ct).ConfigureAwait(false);
                await WaitMapChangeAsync(oldMap, 1200, ct).ConfigureAwait(false);
            }
            if (SameMap(oldMap)) await _rt.ResyncPositionAsync(ct).ConfigureAwait(false);   // 坐标回正后再试下一轮
        }
        if (SameMap(oldMap)) { Trace($"[脚本] 门点 ({mapA},{x1},{y1}) 走过去没换图,后面一段跳过"); return; }
        Trace($"[脚本] 过门点 → {_rt.CurrentMap},目标 ({x2},{y2})");
        await Task.Delay(MapSwitchMs, ct).ConfigureAwait(false);      // 等新图整批视野包到齐
        if (fight) await WalkFightingAsync(x2, y2, ct).ConfigureAwait(false);
        else if (x2 > 0 || y2 > 0)
        {
            using (_ai?.BeginManualOverride())
                await _rt.WalkToAsync(x2, y2, _rt.IsWalkable ?? ((_, _) => true), WalkStepMs, ct).ConfigureAwait(false);
        }
    }

    private CancellationTokenSource? _randomMove;

    public Task RandomMoveAsync(string map, int x, int y, CancellationToken ct)
    {
        StopRandomMove();
        _randomMove = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linked = _randomMove;
        int radius = Math.Clamp(x > 0 ? x : 8, 2, 40);
        Trace($"[脚本] 随机移动开始(以当前点为中心 {radius} 格,随机移动停止 结束)");
        _ = Task.Run(async () =>
        {
            var tok = linked.Token;
            try
            {
                using (_ai?.BeginManualOverride())
                {
                    while (!tok.IsCancellationRequested)
                    {
                        int cx = _rt.Player.PosX, cy = _rt.Player.PosY;
                        int nx = cx + Random.Shared.Next(-radius, radius + 1);
                        int ny = cy + Random.Shared.Next(-radius, radius + 1);
                        if (_rt.IsWalkable != null && !_rt.IsWalkable(nx, ny)) continue;
                        await _rt.WalkToAsync(nx, ny, _rt.IsWalkable ?? ((_, _) => true), WalkStepMs, tok).ConfigureAwait(false);
                        await Task.Delay(300, tok).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally { Trace("[脚本] 随机移动已停止"); }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task RandomMoveStopAsync(CancellationToken ct)
    {
        StopRandomMove();
        return Task.CompletedTask;
    }

    private void StopRandomMove()
    {
        var r = _randomMove;
        _randomMove = null;
        if (r != null) { try { r.Cancel(); } catch (ObjectDisposedException) { } r.Dispose(); }
    }

    public async Task TeleportAsync(int x, int y, CancellationToken ct)
    {
        // 天骥的 传送[X,Y] 不是客户端能力,是发一条服务端脚本命令(角色配置 Command 节 "传送命令",默认 @move X,Y)
        string tpl = _ch?.TeleportCommand ?? "@move X,Y";
        string cmd = tpl.Replace("X", x.ToString()).Replace("Y", y.ToString());
        string oldMap = _rt.CurrentMap;
        int ox = _rt.Player.PosX, oy = _rt.Player.PosY;
        await _rt.SendChatAsync(cmd, ct).ConfigureAwait(false);
        Trace($"[脚本] 传送 → {cmd}");
        for (int i = 0; i < 12 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(300, ct).ConfigureAwait(false);
            if (_rt.Player.PosX != ox || _rt.Player.PosY != oy || _rt.CurrentMap != oldMap) break;
        }
    }

    /// <summary>跨图走路:按角色自己攒出来的 doorlink.ini 逐道门穿过去。没有链路就原地报告,
    /// 绝不硬发别的图上的坐标 —— 服务端会把整步丢掉,而本地坐标是乐观更新的,会越算越超前。</summary>
    private async Task RouteCrossMapAsync(string targetMap, CancellationToken ct)
    {
        var links = _ch?.DoorLinks;
        if (links == null) { Trace("[脚本] 跨图:没挂角色目录,读不到 doorlink.ini"); return; }
        var path = links.FindPath(_rt.CurrentMap, targetMap);
        if (path.Count == 0) { Trace($"[脚本] 跨图 {_rt.CurrentMap}→{targetMap}:doorlink.ini 里没有门点链"); return; }
        Trace($"[脚本] 跨图 {_rt.CurrentMap}→{targetMap},要过 {path.Count} 道门");
        foreach (var e in path)
        {
            if (ct.IsCancellationRequested) return;
            string oldMap = _rt.CurrentMap;
            using (_ai?.BeginManualOverride())
                await _rt.WalkToAsync(e.FromX, e.FromY, _rt.IsWalkable ?? ((_, _) => true), WalkStepMs, ct).ConfigureAwait(false);
            for (int round = 0; round < 2 && string.Equals(_rt.CurrentMap, oldMap, StringComparison.OrdinalIgnoreCase); round++)
            {
                for (int d = 0; d < 8 && string.Equals(_rt.CurrentMap, oldMap, StringComparison.OrdinalIgnoreCase); d++)
                {
                    var (dx, dy) = DirDelta(d);
                    await _rt.SendWalkAsync(e.FromX + dx, e.FromY + dy, (byte)d, ct).ConfigureAwait(false);
                    await Task.Delay(WalkStepMs, ct).ConfigureAwait(false);
                    await WaitMapChangeAsync(oldMap, 1200, ct).ConfigureAwait(false);
                }
                if (string.Equals(_rt.CurrentMap, oldMap, StringComparison.OrdinalIgnoreCase))
                    await _rt.ResyncPositionAsync(ct).ConfigureAwait(false);
            }
            if (string.Equals(_rt.CurrentMap, oldMap, StringComparison.OrdinalIgnoreCase))
            {
                Trace($"[脚本] 门点 ({oldMap},{e.FromX},{e.FromY}) 走不过去,跨图中断");
                return;
            }
            await Task.Delay(MapSwitchMs, ct).ConfigureAwait(false);   // 等新图整批视野包到齐
        }
    }

    private async Task WaitMapChangeAsync(string oldMap, int ms, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested
               && string.Equals(_rt.CurrentMap, oldMap, StringComparison.OrdinalIgnoreCase))
            await Task.Delay(120, CancellationToken.None).ConfigureAwait(false);
    }

    private bool SameMap(string mapOrName)
        => string.IsNullOrEmpty(mapOrName)
           || string.Equals(_rt.CurrentMap, mapOrName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(_rt.Player.MapName, mapOrName, StringComparison.OrdinalIgnoreCase);

    private static (int, int) DirDelta(int dir) => dir switch
    {
        0 => (0, -1), 1 => (1, -1), 2 => (1, 0), 3 => (1, 1),
        4 => (0, 1), 5 => (-1, 1), 6 => (-1, 0), _ => (-1, -1),
    };

    /// <summary>边打边走到:分段走路 + 每段开头清掉身边的怪。
    /// 不能整段交给 WalkToAsync 再指望挂机 AI 顺手打 —— 手动接管期间 AI 只喝药,走路/攻击全部让路(见 BotCombatAI)。</summary>
    private async Task WalkFightingAsync(int tx, int ty, CancellationToken ct)
    {
        using (_ai?.BeginManualOverride())
        {
            for (int leg = 0; leg < 200 && !ct.IsCancellationRequested; leg++)
            {
                await KillNearbyAsync(6, ct).ConfigureAwait(false);
                int px = _rt.Player.PosX, py = _rt.Player.PosY;
                int remain = Math.Max(Math.Abs(tx - px), Math.Abs(ty - py));
                if (remain == 0) return;
                // 每段最多走 8 格就回来查一次身边有没有怪:一段 30 格要 20 秒,怪贴脸了也没人管
                int step = Math.Min(8, remain);
                int gx = px + Math.Sign(tx - px) * step, gy = py + Math.Sign(ty - py) * step;
                await _rt.WalkToAsync(gx, gy, _rt.IsWalkable ?? ((_, _) => true), WalkStepMs, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>把 range 内的怪逐只打死(纯用上行原语,不借 AI 的手)。</summary>
    private async Task KillNearbyAsync(int range, CancellationToken ct)
    {
        for (int guard = 0; guard < 8 && !ct.IsCancellationRequested; guard++)
        {
            var target = NearestMonster(range).Dot;
            if (target == null) return;
            LastAttackTargetId = target.Value.Id;
            var until = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                if (!_rt.Dots.TryGetValue(target.Value.Id, out var live)) break;      // 死了/离开视野
                if (_rt.ObjectHpMap.TryGetValue(live.Id, out var dead) && dead.Hp <= 0) break;
                int px = _rt.Player.PosX, py = _rt.Player.PosY;
                int d = Math.Max(Math.Abs(live.X - px), Math.Abs(live.Y - py));
                if (d > 1)
                {
                    await _rt.WalkToAsync(live.X, live.Y, _rt.IsWalkable ?? ((_, _) => true), WalkStepMs, ct).ConfigureAwait(false);
                    continue;
                }
                byte dir = BotCombatAI.GetDirection(px, py, live.X, live.Y);
                await _rt.SendHitAsync(live.Id, live.X, live.Y, dir, ct).ConfigureAwait(false);
                await Task.Delay(Math.Max(600, _ai?.CurrentAttackIntervalMs() ?? 950), ct).ConfigureAwait(false);
            }
            await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private (MapDot? Dot, int Dist) NearestMonster(int range)
    {
        MapDot? best = null;
        int bd = int.MaxValue;
        int px = _rt.Player.PosX, py = _rt.Player.PosY;
        foreach (var dot in _rt.Dots.Values)
        {
            if (dot.Kind != DotKind.Monster) continue;
            if (_rt.ObjectHpMap.TryGetValue(dot.Id, out var h) && h.Hp <= 0) continue;
            int d = Math.Max(Math.Abs(dot.X - px), Math.Abs(dot.Y - py));
            if (d <= range && d < bd) { bd = d; best = dot; }
        }
        return (best, bd == int.MaxValue ? -1 : bd);
    }

    // ============================================================
    //  NPC:找、走近、开菜单、选项
    // ============================================================
    public async Task FindNpcAsync(string name, string map, int x, int y, CancellationToken ct)
    {
        var dot = await ResolveNpcAsync(name, map, x, y, ct).ConfigureAwait(false);
        if (dot == null) { Trace($"[脚本] 找到NPC {name} 失败"); return; }
        int dist = Math.Max(Math.Abs(dot.Value.X - _rt.Player.PosX), Math.Abs(dot.Value.Y - _rt.Player.PosY));
        Trace($"[脚本] 找到NPC {dot.Value.Name}#{dot.Value.Id} @({dot.Value.X},{dot.Value.Y}) 距离={dist}");
        // 顺手把坐标记进角色的 npcFunc.ini:下次直接照着走,不用再满视野找
        _ch?.NpcFunc.Learn(dot.Value.Name, _rt.Player.MapName, _rt.CurrentMap, dot.Value.X, dot.Value.Y);
    }

    public async Task TalkNpcAsync(string name, CancellationToken ct)
    {
        var dot = await ResolveNpcAsync(name, "", 0, 0, ct).ConfigureAwait(false);
        if (dot == null) { Trace($"[脚本] 对话:视野里/记录里都没有 NPC \"{name}\""); return; }
        await OpenMenuAsync(dot.Value, true, ct).ConfigureAwait(false);
    }

    public async Task TalkNpcAtAsync(int x, int y, CancellationToken ct)
    {
        var dot = NpcAt(x, y);
        if (dot == null) { Trace($"[脚本] 对话坐标:({x},{y}) 附近没有 NPC"); return; }
        if (!await NearAsync(dot.Value, ct).ConfigureAwait(false)) return;
        await OpenMenuAsync(dot.Value, true, ct).ConfigureAwait(false);
    }

    public async Task AttackNpcAsync(string name, int times, CancellationToken ct)
    {
        var dot = await ResolveNpcAsync(name, "", 0, 0, ct).ConfigureAwait(false);
        if (dot == null) { Trace($"[脚本] 攻击NPC:找不到 {name}"); return; }
        if (!await NearAsync(dot.Value, ct).ConfigureAwait(false)) return;
        int n = Math.Clamp(times <= 0 ? 1 : times, 1, 50);
        LastAttackTargetId = dot.Value.Id;
        for (int i = 0; i < n && !ct.IsCancellationRequested; i++)
        {
            byte dir = BotCombatAI.GetDirection(_rt.Player.PosX, _rt.Player.PosY, dot.Value.X, dot.Value.Y);
            await _rt.SendHitAsync(dot.Value.Id, dot.Value.X, dot.Value.Y, dir, ct).ConfigureAwait(false);
            await Task.Delay(Math.Max(600, _ai?.CurrentAttackIntervalMs() ?? 950), ct).ConfigureAwait(false);
        }
        Trace($"[脚本] 攻击NPC {name} {n} 次");
    }

    private List<MapDot> NpcsInView()
        => _rt.Dots.Values.Where(d => d.Kind == DotKind.Npc)
            .OrderBy(d => Math.Max(Math.Abs(d.X - _rt.Player.PosX), Math.Abs(d.Y - _rt.Player.PosY)))
            .ToList();

    /// <summary>攻击坐标[X,Y]:朝那一格出一刀。服务端 ClientHit 走 GetFrontObject(按方向取正前方一格),
    /// 所以"打谁"取决于我们站的位置和朝向 —— 先站到那格旁边、转身对着它,再出刀。</summary>
    public async Task AttackAtAsync(int x, int y, CancellationToken ct)
    {
        int dist = Math.Max(Math.Abs(x - _rt.Player.PosX), Math.Abs(y - _rt.Player.PosY));
        if (dist > 1)
        {
            using (_ai?.BeginManualOverride())
                await _rt.WalkToAsync(x, y, _rt.IsWalkable ?? ((_, _) => true), WalkStepMs, ct).ConfigureAwait(false);
        }
        byte dir = BotCombatAI.GetDirection(_rt.Player.PosX, _rt.Player.PosY, x, y);
        long victim = 0;
        foreach (var d in _rt.Dots.Values)
            if (d.Kind is DotKind.Monster or DotKind.Player && d.X == x && d.Y == y) { victim = d.Id; break; }
        LastAttackTargetId = victim;
        await _rt.SendHitAsync(victim, x, y, dir, ct).ConfigureAwait(false);
        Trace($"[脚本] 攻击坐标({x},{y}) 朝向={dir} 目标={(victim == 0 ? "该格无人(空挥)" : $"#{victim}")}");
        await Task.Delay(Math.Max(600, _ai?.CurrentAttackIntervalMs() ?? 950), ct).ConfigureAwait(false);
    }

    /// <summary>选择[内容] / 选择_加强[内容]:点"当前显示的那一页"对话里的某一项。
    /// 绝不能在这里重新 CM_CLICKNPC —— 那会让服务端跳回 @main,这一页就没了(见类头的翻页规则),
    /// 而这条命令存在的意义正是"对话内容经常变、按文字挑一项"。</summary>
    public async Task ChooseAsync(string text, bool strong, CancellationToken ct)
    {
        var page = CurrentPage();
        if (page == null) { Trace($"[脚本] 选择[{text}]:现在没有打开着的 NPC 对话(先用 对话[..] 点开一个)"); return; }
        NpcOption? hit = null;
        foreach (var o in page)
            if (o.Display.Contains(text, StringComparison.Ordinal)) { hit = o; break; }
        // 天骥里 选择 只认下划线内那段,选择_加强 连下划线外的字一起认。我们的 NpcOption.Display
        // 把">'之后的附属文字并进了显示,所以两种写法在这里是同一个匹配 —— 差别只在原文有没有下划线。
        if (hit == null)
        {
            Trace($"[脚本] 选择[{text}]:这一页里没有这一项,现有 {string.Join("/", page.Select(o => o.Display))}");
            return;
        }
        await ClickCurrentAsync(hit.Value, ct).ConfigureAwait(false);
    }

    /// <summary>选择位置[N]:按 1 起的序号点当前这一页。</summary>
    public async Task ChooseAtAsync(int index, CancellationToken ct)
    {
        var page = CurrentPage();
        if (page == null) { Trace($"[脚本] 选择位置[{index}]:现在没有打开着的 NPC 对话"); return; }
        if (index < 1 || index > page.Count)
        {
            Trace($"[脚本] 选择位置[{index}]:这一页只有 {page.Count} 项,现有 {string.Join("/", page.Select(o => o.Display))}");
            return;
        }
        await ClickCurrentAsync(page[index - 1], ct).ConfigureAwait(false);
    }

    private List<NpcOption>? CurrentPage()
        => _curNpcId != 0 && _menus.TryGetValue(_curNpcId, out var c) && c.Opts.Count > 0 ? c.Opts : null;

    private async Task ClickCurrentAsync(NpcOption opt, CancellationToken ct)
    {
        MapDot? npc = null;
        foreach (var d in _rt.Dots.Values) if (d.Id == _curNpcId) { npc = d; break; }
        if (npc == null) { Trace($"[脚本] 选 \"{opt.Display}\":NPC #{_curNpcId} 已不在视野里(换图了?),缓存作废"); return; }
        if (!await NearAsync(npc.Value, ct).ConfigureAwait(false)) return;
        int mark = Mark(_says);
        await _rt.SendMerchantDlgSelectAsync(npc.Value.Id, opt.Command, ct).ConfigureAwait(false);
        await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
        bool turned = Count(_says) > mark;
        if (turned) _menus[npc.Value.Id] = (NpcDialogOptions.Parse(Take(_says, mark)[^1].Text), NowMs);
        Trace($"[脚本] 选 \"{opt.Display}\"({opt.Command}) => {(turned ? "对话已翻页,新菜单已缓存" : "无新正文(可能直接开了商店或执行了命令)")}");
    }

    private MapDot? NpcAt(int x, int y)
    {
        foreach (var d in NpcsInView())
            if (Math.Max(Math.Abs(d.X - x), Math.Abs(d.Y - y)) <= 2) return d;
        return null;
    }

    /// <summary>按名字定位一个 NPC:视野里先找;找不到就拿角色记的坐标(npcFunc.ini / 脚本自带的 X,Y)走过去。
    /// 视野里有没有它只取决于我们站得够不够近 —— 服务端每 1000ms 推一次 SearchViewRange,不用反复点它问。</summary>
    private async Task<MapDot?> ResolveNpcAsync(string name, string map, int x, int y, CancellationToken ct)
    {
        foreach (var d in NpcsInView()) if (MatchName(d.Name, name)) return d;
        if (name.Length == 0) return null;

        int tx = x, ty = y;
        string tmap = map;
        var known = _ch?.NpcFunc.Find(name, map.Length > 0 ? map : null);
        if (known != null) { tmap = known.Value.MapCode; tx = known.Value.X; ty = known.Value.Y; }
        if (tx == 0 && ty == 0)
        {
            Trace($"[脚本] NPC \"{name}\" 不在视野里,也没有坐标记录(npcFunc.ini),找不着");
            return null;
        }
        if (tmap.Length > 0 && !SameMap(tmap)) await RouteCrossMapAsync(tmap, ct).ConfigureAwait(false);
        if (tmap.Length > 0 && !SameMap(tmap)) { Trace($"[脚本] 去不了 {tmap},找不到 \"{name}\""); return null; }
        using (_ai?.BeginManualOverride())
            await _rt.WalkToAsync(tx, ty, _rt.IsWalkable ?? ((_, _) => true), WalkStepMs, ct).ConfigureAwait(false);
        await Task.Delay(MapSwitchMs, CancellationToken.None).ConfigureAwait(false);
        foreach (var d in NpcsInView()) if (MatchName(d.Name, name)) return d;
        // 坐标是角色自己探路记下来的,门点边上的记录经常差一两格:再就近绕一圈试一次
        for (int ring = 0; ring < 8; ring++)
        {
            var (dx, dy) = DirDelta(ring);
            await _rt.WalkToAsync(tx + dx * 2, ty + dy * 2, _rt.IsWalkable ?? ((_, _) => true), WalkStepMs, ct).ConfigureAwait(false);
            await Task.Delay(1200, CancellationToken.None).ConfigureAwait(false);
            foreach (var d in NpcsInView()) if (MatchName(d.Name, name)) return d;
        }
        return null;
    }

    private static bool MatchName(string have, string want)
        => want.Length > 0 && have.Length > 0
           && (string.Equals(have, want, StringComparison.OrdinalIgnoreCase)
               || have.Contains(want, StringComparison.OrdinalIgnoreCase)
               || want.Contains(have, StringComparison.OrdinalIgnoreCase));

    /// <summary>走到 NPC 的 15 格闸门内。真机踩过的坑:超距时服务端整包丢弃且零回包,
    /// 所以这里走不到就让调用方放弃,绝不能"发完包再等 6 秒"当正常路径。</summary>
    private async Task<bool> NearAsync(MapDot npc, CancellationToken ct)
    {
        int dist = Math.Max(Math.Abs(npc.X - _rt.Player.PosX), Math.Abs(npc.Y - _rt.Player.PosY));
        if (dist < NpcNear) return true;
        bool arrived;
        using (_ai?.BeginManualOverride())
            arrived = await _rt.ApproachNpcAsync(npc.Id, _rt.IsWalkable, WalkStepMs, ct).ConfigureAwait(false);
        int now = Math.Max(Math.Abs(npc.X - _rt.Player.PosX), Math.Abs(npc.Y - _rt.Player.PosY));
        Trace($"[脚本] 走近 {npc.Name}(原距离 {dist}) => {arrived},现在 {now} 格");
        return arrived && now < NpcNear;
    }

    /// <summary>点 NPC 取这一页菜单(CM_CLICKNPC → 对话正文)。fresh=false 时可用 20 秒内的缓存。</summary>
    private async Task<List<NpcOption>> OpenMenuAsync(MapDot npc, bool fresh, CancellationToken ct)
    {
        if (!fresh && _menus.TryGetValue(npc.Id, out var cache) && NowMs - cache.AtMs < 20_000)
        {
            if (cache.Opts.Count > 0) _curNpcId = npc.Id;
            return cache.Opts;
        }
        if (!await NearAsync(npc, ct).ConfigureAwait(false)) return new List<NpcOption>();
        int mark = Mark(_says);
        await _rt.SendNpcInteractAsync(npc.Id, ct).ConfigureAwait(false);
        if (!await WaitUntil(() => Count(_says) > mark, ReplyMs, ct).ConfigureAwait(false))
        {
            Trace($"[脚本] {npc.Name}#{npc.Id} 对话 6 秒无回包(可能被服务端按功能拦下)");
            return new List<NpcOption>();
        }
        var say = Take(_says, mark);
        var opts = NpcDialogOptions.Parse(say.Length > 0 ? say[^1].Text : "");
        _menus[npc.Id] = (opts, NowMs);
        if (opts.Count > 0) _curNpcId = npc.Id;
        Trace($"[脚本] {npc.Name} 菜单: {string.Join(" | ", opts.Select(o => $"{o.Display}=>{o.Command}"))}");
        return opts;
    }

    private static NpcOption? Match(List<NpcOption> opts, string[] words, string? command)
    {
        if (command != null)
            foreach (var o in opts)
                if (string.Equals(o.Command, command, StringComparison.OrdinalIgnoreCase)) return o;
        foreach (var o in opts)
            if (words.Any(w => w.Length > 0 && (o.Display.Contains(w, StringComparison.Ordinal)
                                                 || o.Command.Contains(w, StringComparison.OrdinalIgnoreCase))))
                return o;
        return null;
    }

    /// <summary>在视野里的所有 NPC 中找一家有某类功能的店(卖/修/仓库…),顺便带回它的菜单项。
    /// 按距离近→远问,超 14 格直接跳过:对超距的 NPC 等回包纯属烧预算。</summary>
    private async Task<(MapDot Npc, NpcOption Opt)?> FindServiceAsync(string[] words, string? command, CancellationToken ct)
    {
        foreach (var npc in NpcsInView())
        {
            if (Math.Max(Math.Abs(npc.X - _rt.Player.PosX), Math.Abs(npc.Y - _rt.Player.PosY)) >= NpcNear) continue;
            var opts = await OpenMenuAsync(npc, false, ct).ConfigureAwait(false);
            var hit = Match(opts, words, command);
            if (hit != null) return (npc, hit.Value);
        }
        return null;
    }

    /// <summary>点一个菜单项。ack 为 null 表示这一类操作没有可等的回包(或回包由调用方下一步自己收),
    /// 那就只按发包间隔留白;给了 ack 就等它变真,最多 6 秒。</summary>
    private async Task<bool> SelectAsync(MapDot npc, NpcOption opt, Func<bool>? ack, CancellationToken ct)
    {
        if (!await NearAsync(npc, ct).ConfigureAwait(false)) return false;
        int mark = ack == null ? 0 : Mark(_says);
        await _rt.SendMerchantDlgSelectAsync(npc.Id, opt.Command, ct).ConfigureAwait(false);
        if (ack == null)
        {
            await Task.Delay(SendGapMs * 2, ct).ConfigureAwait(false);
            return true;
        }
        Trace($"[脚本] {npc.Name}: 选 \"{opt.Display}\"({opt.Command})");
        bool ok = await WaitUntil(ack, ReplyMs, ct).ConfigureAwait(false);
        if (!ok) Trace($"[脚本] {opt.Command} 6 秒无回包");
        return ok;
    }

    private static async Task<bool> WaitUntil(Func<bool> ready, int ms, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (ready()) return true;
            if (ct.IsCancellationRequested) return ready();
            await Task.Delay(120, CancellationToken.None).ConfigureAwait(false);
        }
        return ready();
    }

    // ============================================================
    //  背包/装备查询
    // ============================================================
    private BagItemInfo[] Bag() => _rt.SnapshotBag();

    private List<BagItemInfo> FindBag(string name)
        => Bag().Where(b => b.MakeIndex > 0 && MatchName(b.Name, name)).OrderBy(b => b.DuraCount).ToList();

    /// <summary>装备槽 → 该槽上穿的东西。UseItems 的下标就是服务端 U_* 常量。</summary>
    private BagItemInfo WornAt(int slot)
    {
        if (slot < 0) return default;
        var use = _rt.SnapshotUseItems();
        return slot < use.Length ? use[slot] : default;
    }

    // ============================================================
    //  物品:使用 / 穿卸 / 丢弃
    // ============================================================
    public async Task UseItemAsync(string name, CancellationToken ct)
    {
        var it = FindBag(name).FirstOrDefault();
        if (it.MakeIndex == 0) { Trace($"[脚本] 使用[{name}]:背包里没有"); return; }
        int mark = Mark(_eatResults);
        await _rt.SendEatAsync(it.MakeIndex, it.Name, ct).ConfigureAwait(false);
        bool ok = await WaitUntil(() => Count(_eatResults) > mark, ReplyMs, ct).ConfigureAwait(false);
        Trace($"[脚本] 使用[{name}] idx={it.MakeIndex} => {(ok ? Take(_eatResults, mark)[^1] : "6 秒无回包")}");
        await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
    }

    public async Task EquipAsync(string item, string slot, CancellationToken ct)
    {
        var it = FindBag(item).FirstOrDefault();
        if (it.MakeIndex == 0) { Trace($"[脚本] 装备[{item}]:背包里没有"); return; }
        int where = SlotIndex(slot);
        if (where < 0) where = StdModeSlot(it.StdMode);
        if (where < 0) { Trace($"[脚本] 装备[{item}] 到[{slot}]:认不出这个部位"); return; }
        int mark = Mark(_takeOnResults);
        await _rt.SendTakeOnItemAsync(it.MakeIndex, where, it.Name, ct).ConfigureAwait(false);
        // 服务端只在成功时把物品从 BagItems 搬进 UseItems(本地按回包同步),所以"穿上了"的判据是槽里出现它
        bool ok = await WaitUntil(() => WornAt(where).MakeIndex == it.MakeIndex, ReplyMs, ct).ConfigureAwait(false);
        Trace($"[脚本] 装备[{item}]→部位{where}({SlotLabel(where)}) => {ok}" +
              (Count(_takeOnResults) > mark ? $" 回包 {Take(_takeOnResults, mark)[^1]}" : ""));
        await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
    }

    public async Task TakeOffAsync(string slot, CancellationToken ct)
    {
        int where = SlotIndex(slot);
        var it = WornAt(where);
        if (it.MakeIndex == 0)
        {
            var use = _rt.SnapshotUseItems();
            for (int i = 0; i < use.Length && where < 0; i++)
                if (use[i].MakeIndex > 0 && MatchName(use[i].Name, slot)) where = i;
            if (where >= 0) it = use[where];
        }
        if (it.MakeIndex == 0) { Trace($"[脚本] 卸下[{slot}]:那个槽本来是空的"); return; }
        await _rt.SendTakeOffItemAsync(it.MakeIndex, where, it.Name, ct).ConfigureAwait(false);
        bool ok = await WaitUntil(() => WornAt(where).MakeIndex != it.MakeIndex, ReplyMs, ct).ConfigureAwait(false);
        Trace($"[脚本] 卸下[{it.Name}](部位 {where}/{SlotLabel(where)}) => {ok}");
        await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
    }

    public async Task DropAsync(string item, int count, CancellationToken ct)
    {
        var it = FindBag(item).FirstOrDefault();
        if (it.MakeIndex == 0) { Trace($"[脚本] 丢弃[{item}]:背包里没有"); return; }
        await _rt.SendDropItemAsync(it.MakeIndex, it.Name, count, ct).ConfigureAwait(false);
        Trace($"[脚本] 丢弃[{it.Name}](现有 {(it.Stackable ? it.Count : 1)} 件,请求 {count})idx={it.MakeIndex}");
        await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
    }

    public async Task DropGoldAsync(int amount, CancellationToken ct)
    {
        // 服务端 ClientDropGold(ObjPlayer.pas:22827) 在 nGold >= m_nGold 时直接 Exit —— 想"全丢"
        // 只能留 1 金币,否则这条命令看起来发了、实际一分没动,而且没有任何失败回包。
        int gold = _rt.Player.Gold;
        if (amount >= gold) amount = gold - 1;
        if (amount <= 0) { Trace($"[脚本] 丢弃金币[{amount}]:现有金币 {gold},不够留余量丢"); return; }
        await _rt.SendDropGoldAsync(amount, ct).ConfigureAwait(false);
        await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
        // 服务端对这道命令没有任何失败回包,只有金币真的掉了才算成。
        Trace($"[脚本] 丢弃金币[{amount}]:金币 {gold}→{_rt.Player.Gold}" +
              $"{(_rt.Player.Gold < gold ? "" : ",未减少 —— 安全区/nCanDropGold/m_boCanDrop 拒收")}");
    }

    // ============================================================
    //  商店:买 / 卖 / 修 / 仓库
    // ============================================================
    public Task BuyAsync(string item, int count, string? slot, string? repair, bool force, CancellationToken ct)
        => BuyCoreAsync(item, count, force, async () =>
        {
            if (!string.IsNullOrEmpty(slot)) await EquipAsync(item, slot!, ct).ConfigureAwait(false);
            if (repair != null) await RepairAsync(item, repair, true, slot, ct).ConfigureAwait(false);
        }, ct);

    public Task BuyUntilAsync(string item, int count, bool force, CancellationToken ct)
        => BuyCoreAsync(item, count, force, null, ct);

    /// <summary>买 N 件(0=有多少买多少)。逐件商品(子菜单=1)要先取明细才有各自的 MakeIndex,
    /// 而 子菜单=0 时货架第 4 段就是那叠货的 MakeIndex —— 传 0 一件都买不到(服务端按名字+MakeIndex 双匹配)。</summary>
    private async Task BuyCoreAsync(string item, int count, bool force, Func<Task>? after, CancellationToken ct)
    {
        var shop = await FindServiceAsync(new[] { "买", "购", "shop", "buy" }, _ch?.BuyCommand, ct).ConfigureAwait(false);
        if (shop == null) { Trace($"[脚本] 购买[{item}]:视野里没有能买的商店"); return; }
        var npc = shop.Value.Npc;
        var buyOpt = shop.Value.Opt;
        if (!await NearAsync(npc, ct).ConfigureAwait(false)) return;

        int have = BuyCount(item);
        int target = count <= 0 ? have + 20 : have + count;
        for (int attempts = 0; have < target && attempts < 12 && !ct.IsCancellationRequested; attempts++)
        {
            var goods = await LoadGoodsAsync(npc, buyOpt, ct).ConfigureAwait(false);
            var g = FindGoods(goods, item);
            if (g == null) { Trace($"[脚本] 购买[{item}]:{npc.Name} 的货架上没有这样东西"); break; }
            int made = await BuyOneAsync(npc, g.Value, item, ct).ConfigureAwait(false);
            have += made;
            if (made == 0 && !force) break;
            await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
        }
        Trace($"[脚本] 购买[{item}] 结束:现有 {BuyCount(item)} 件(目标 {target})");
        if (after != null && FindBag(item).Count > 0) await after().ConfigureAwait(false);
    }

    private int BuyCount(string item) => FindBag(item).Sum(b => b.Stackable ? Math.Max(1, b.Count) : 1);

    /// <summary>点开商店页并等 SM_SENDGOODSLIST(645)。没等到新包就退回 30 秒内的缓存:
    /// 服务端只在第一次进入商店时发整张货架,再点一次 @buy 常常只是重发对话。</summary>
    private async Task<List<GxxPayload.MerchantGoods>> LoadGoodsAsync(MapDot npc, NpcOption buyOpt, CancellationToken ct)
    {
        int mark = CountGoods(npc.Id);
        await SelectAsync(npc, buyOpt, null, ct).ConfigureAwait(false);
        await WaitUntil(() => CountGoods(npc.Id) > mark, ReplyMs, ct).ConfigureAwait(false);
        var list = GxxPayload.ParseMerchantGoods(LastGoodsText(npc.Id, mark));
        if (list.Count > 0) _goodsCache[npc.Id] = (list, NowMs);
        else if (_goodsCache.TryGetValue(npc.Id, out var old) && NowMs - old.At < 30_000) list = old.Goods;
        return list;
    }

    private static GxxPayload.MerchantGoods? FindGoods(List<GxxPayload.MerchantGoods> goods, string item)
    {
        foreach (var g in goods) if (string.Equals(g.Name, item, StringComparison.OrdinalIgnoreCase)) return g;
        foreach (var g in goods) if (MatchName(g.Name, item)) return g;
        return null;
    }

    /// <summary>下一单,并等到"背包里这件多了一件"或"钱少了"。服务端可能只回 SM_BUYFAIL 而什么都不发,
    /// 所以判据用事实(数量/金币)而不是回包文本。</summary>
    private async Task<int> BuyOneAsync(MapDot npc, GxxPayload.MerchantGoods g, string item, CancellationToken ct)
    {
        int before = BuyCount(item);
        int gold = _rt.Player.Gold;
        if (!g.Buyable)
        {
            int dmark = Count(_details);
            await _rt.SendGetDetailItemAsync(npc.Id, 0, g.Name, ct).ConfigureAwait(false);
            if (!await WaitUntil(() => Count(_details) > dmark, ReplyMs, ct).ConfigureAwait(false)) return 0;
            var pick = PickDetail(npc.Id, g.Name);
            if (pick == null) return 0;
            await _rt.SendBuyItemAsync(npc.Id, pick.Value.MakeIndex, 1, pick.Value.Name, ct).ConfigureAwait(false);
        }
        else
        {
            await _rt.SendBuyItemAsync(npc.Id, g.StockOrMakeIndex, 1, g.Name, ct).ConfigureAwait(false);
        }
        var until = DateTime.UtcNow.AddMilliseconds(ReplyMs);
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested
               && BuyCount(item) == before && _rt.Player.Gold == gold)
            await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
        int got = BuyCount(item) - before;
        if (got == 0 && _rt.Player.Gold < gold) got = 1;    // 不可叠加物品:背包件数不变但钱扣了,算买到
        Trace($"[脚本] 买 {g.Name}:+{got} 件,金币 {gold}→{_rt.Player.Gold}" +
              (Count(_buyResults) > 0 ? $" 最近回包 {_buyResults[^1]}" : ""));
        return got;
    }

    private GxxPayload.DetailGoods? PickDetail(long merchantId, string itemName)
    {
        var last = _lastDetail;
        if (last == null || last.Value.Id != merchantId) return null;
        int? best = null;
        foreach (var x in last.Value.Items)
        {
            if (!MatchName(x.Name, itemName)) continue;
            if (best == null) { best = x.MakeIndex; continue; }
            int bi = best.Value;
            var b = last.Value.Items.First(i => i.MakeIndex == bi);
            double oldScore = b.DuraMax > 0 ? (double)b.Dura / b.DuraMax : 1;
            double newScore = x.DuraMax > 0 ? (double)x.Dura / x.DuraMax : 1;
            if (newScore > oldScore || (newScore == oldScore && x.Price < b.Price)) best = x.MakeIndex;
        }
        if (best == null) return null;
        return last.Value.Items.First(i => i.MakeIndex == best.Value);
    }

    public async Task SellAsync(string item, CancellationToken ct)
    {
        var bag = FindBag(item);
        if (bag.Count == 0) { Trace($"[脚本] 卖物[{item}]:背包里没有"); return; }
        foreach (var it in bag)
            if (!await SellOneAsync(it, ct).ConfigureAwait(false)) break;
    }

    public async Task SellCategoryAsync(string category, CancellationToken ct)
    {
        var names = CategoryNames(category).ToHashSet();
        if (names.Count == 0) { Trace($"[脚本] 自动售物[{category}]:物品设置里没有这个类别"); return; }
        var bag = Bag().Where(b => b.MakeIndex > 0 && names.Contains(b.Name) && b.StdMode > 4).ToList();
        int sold = 0;
        foreach (var it in bag)
        {
            if (ct.IsCancellationRequested) break;
            if (await SellOneAsync(it, ct).ConfigureAwait(false)) sold++;
        }
        Trace($"[脚本] 自动售物[{category}] 卖出 {sold}/{bag.Count} 件");
    }

    /// <summary>逐家商店试卖:服务端 TMerchant.GetItemPrice 先查专属价表、再回落 StdItem.Price,
    /// 而回落前有一道 CheckItemType(StdMode) 类别闸门 ⇒ 在铁匠铺卖布衣拿到 0 是服务端正确行为。
    /// 一家不出价就换下一家。</summary>
    private async Task<bool> SellOneAsync(BagItemInfo it, CancellationToken ct)
    {
        foreach (var npc in NpcsInView())
        {
            if (ct.IsCancellationRequested) return false;
            if (Math.Max(Math.Abs(npc.X - _rt.Player.PosX), Math.Abs(npc.Y - _rt.Player.PosY)) >= NpcNear) continue;
            var opts = await OpenMenuAsync(npc, false, ct).ConfigureAwait(false);
            var sell = Match(opts, new[] { "卖", "出售", "收" }, _ch?.SellCommand);
            if (sell == null) continue;
            int modeMark = Count(_sellMode);
            if (!await SelectAsync(npc, sell.Value, () => Count(_sellMode) > modeMark, ct).ConfigureAwait(false)) continue;

            int pMark = Count(_sellPrices);
            await _rt.SendQuerySellPriceAsync(npc.Id, it.MakeIndex, it.Name, ct).ConfigureAwait(false);
            bool quoted = await WaitUntil(() => Count(_sellPrices) > pMark, ReplyMs, ct).ConfigureAwait(false);
            int price = quoted ? Take(_sellPrices, pMark)[^1] : 0;
            await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
            if (price <= 0) { Trace($"[脚本] {npc.Name} 不收 {it.Name}(报价 {price})"); continue; }

            int rMark = Count(_sellResults);
            int gold = _rt.Player.Gold;
            await _rt.SendSellItemAsync(npc.Id, it.MakeIndex, it.Name, ct).ConfigureAwait(false);
            bool done = await WaitUntil(() => Count(_sellResults) > rMark, ReplyMs, ct).ConfigureAwait(false);
            Trace($"[脚本] 卖 {it.Name} idx={it.MakeIndex} 报价 {price} => " +
                  $"{(done ? Take(_sellResults, rMark)[^1] : "6 秒无回包")},金币 {gold}→{_rt.Player.Gold}");
            await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
            return true;
        }
        Trace($"[脚本] 视野里没有肯收 {it.Name} 的商店");
        return false;
    }

    public async Task StoreAsync(string item, CancellationToken ct)
    {
        var it = FindBag(item).FirstOrDefault();
        if (it.MakeIndex == 0) { Trace($"[脚本] 存物[{item}]:背包里没有"); return; }
        var keeper = await FindKeeperAsync(ct).ConfigureAwait(false);
        if (keeper == null) return;
        var npc = keeper.Value.Npc;
        if (!await SelectAsync(npc, keeper.Value.Opt, null, ct).ConfigureAwait(false)) return;
        int rMark = Mark(_storageResults);
        await _rt.SendStorageItemAsync(npc.Id, it.MakeIndex, it.Name, 0, ct).ConfigureAwait(false);
        bool ok = await WaitUntil(() => Count(_storageResults) > rMark, ReplyMs, ct).ConfigureAwait(false);
        // 服务端存物只 Delete 不发 SM_DELITEM,"本地必须自删"在 BotRuntime 里做了,这里以背包件数为准
        Trace($"[脚本] 存入 {it.Name} => {(ok ? Take(_storageResults, rMark)[^1] : "6 秒无回包")},背包 {Bag().Length} 件");
        await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
    }

    public async Task StoreAllAsync(string category, CancellationToken ct)
    {
        var names = CategoryNames(category).ToHashSet();
        var bag = Bag().Where(b => b.MakeIndex > 0 && !b.Stackable
                                   && (names.Count == 0 || names.Contains(b.Name)) && Storable(b, "")).ToList();
        if (bag.Count == 0) { Trace($"[脚本] 自动存物[{category}]:没有该存的物品"); return; }
        var keeper = await FindKeeperAsync(ct).ConfigureAwait(false);
        if (keeper == null) return;
        var npc = keeper.Value.Npc;
        int doneCnt = 0;
        foreach (var it in bag)
        {
            if (ct.IsCancellationRequested) break;
            // 每存一件都要重新点一次 NPC:服务端按"当前显示的那一页"校验菜单项(ObjNpc.pas LableIsCanJmp),
            // 翻过页之后再点第二项是直接 Exit —— 零回包零报错。
            var fresh = await OpenMenuAsync(npc, true, ct).ConfigureAwait(false);
            var again = Match(fresh, new[] { "存", "仓库", "寄存" }, _ch?.StorageCommand) ?? keeper.Value.Opt;
            if (!await SelectAsync(npc, again, null, ct).ConfigureAwait(false)) continue;
            int rMark = Mark(_storageResults);
            await _rt.SendStorageItemAsync(npc.Id, it.MakeIndex, it.Name, 0, ct).ConfigureAwait(false);
            bool ok = await WaitUntil(() => Count(_storageResults) > rMark, ReplyMs, ct).ConfigureAwait(false);
            if (ok) doneCnt++;
            Trace($"[脚本] 存 {it.Name} => {(ok ? Take(_storageResults, rMark)[^1] : "6 秒无回包")}");
            await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
        }
        Trace($"[脚本] 自动存物[{category}] 存入 {doneCnt}/{bag.Count} 件");
    }

    public async Task TakeOutAsync(string item, bool force, CancellationToken ct)
    {
        var keeper = await FindKeeperAsync(ct).ConfigureAwait(false);
        if (keeper == null) return;
        var npc = keeper.Value.Npc;
        // 仓库列表只有点 @getback 时才由服务端 SendSaveItemList 下发(704),不点就永远看不到
        var menu = await OpenMenuAsync(npc, true, ct).ConfigureAwait(false);
        var back = Match(menu, new[] { "取", "取回", "拿" }, _ch?.GetBackCommand);
        if (back == null) { Trace("[脚本] 取物:仓库菜单里没有取回项"); return; }
        int tickMark = Volatile.Read(ref _storageTicks);
        if (!await SelectAsync(npc, back.Value, () => Volatile.Read(ref _storageTicks) > tickMark, ct).ConfigureAwait(false))
            Trace("[脚本] 取物:等了 6 秒没收到仓库列表(704),仍按现有缓存试取");
        var st = default(BagItemInfo);
        foreach (var s in _rt.SnapshotStorage()) if (MatchName(s.Name, item)) { st = s; break; }
        if (st.MakeIndex == 0) { Trace($"[脚本] 取物[{item}]:仓库列表里没有"); return; }
        // default(BagItemInfo) 的 Name 是 null,而服务端要的名字必须与 MakeIndex 同时对得上,
        // 所以兜底用脚本里写的物品名(704 的 Name 偶尔是空,天骥那边同样靠名字匹配)。
        string stName = st.Name ?? item;
        int rMark = Mark(_storageResults);
        await _rt.SendTakeBackStorageItemAsync(npc.Id, st.MakeIndex, stName, Math.Max(1, st.Count), ct).ConfigureAwait(false);
        bool ok = await WaitUntil(() => Count(_storageResults) > rMark, ReplyMs, ct).ConfigureAwait(false);
        Trace($"[脚本] 取出 {stName} => {(ok ? Take(_storageResults, rMark)[^1] : "6 秒无回包")}");
        await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
    }

    /// <summary>仓库保管员不是商店:它的菜单里得写 @storage/@getback。视野里没有就按 npcFunc.ini 走过去。</summary>
    private async Task<(MapDot Npc, NpcOption Opt)?> FindKeeperAsync(CancellationToken ct)
    {
        var hit = await FindServiceAsync(new[] { "仓库", "寄存", "存", "取回" }, _ch?.StorageCommand, ct).ConfigureAwait(false);
        if (hit != null) return hit;
        var npc = await ResolveNpcAsync("仓库", "", 0, 0, ct).ConfigureAwait(false);
        if (npc == null) { Trace("[脚本] 视野里和 npcFunc.ini 里都没有仓库保管员"); return null; }
        var opts = await OpenMenuAsync(npc.Value, true, ct).ConfigureAwait(false);
        var opt = Match(opts, new[] { "存", "仓库", "取" }, _ch?.StorageCommand);
        return opt == null ? null : (npc.Value, opt.Value);
    }

    public async Task RepairAsync(string target, string kind, bool force, string? equipTo, CancellationToken ct)
    {
        // 服务端 ClientUserRepairItem 只在背包 m_ItemList 里找物品 ⇒ 穿着的东西必须先卸下(这条是修理链最容易错的)
        int fromSlot = -1;
        var it = ResolveRepairable(target);
        if (it.MakeIndex == 0)
        {
            fromSlot = SlotIndex(target);
            var w = WornAt(fromSlot);
            if (w.MakeIndex > 0)
            {
                await TakeOffAsync(target, ct).ConfigureAwait(false);
                it = FindBag(w.Name).FirstOrDefault();
            }
        }
        if (it.MakeIndex == 0) { Trace($"[脚本] {kind}[{target}]:背包和身上都没有这样东西"); return; }

        string[] words = kind.Contains("特修") ? new[] { "特修", "修" } : new[] { "修" };
        string? wantCmd = kind.Contains("特修") ? _ch?.SpecialRepairCommand : _ch?.RepairCommand;
        foreach (var npc in NpcsInView())
        {
            if (ct.IsCancellationRequested) return;
            if (Math.Max(Math.Abs(npc.X - _rt.Player.PosX), Math.Abs(npc.Y - _rt.Player.PosY)) >= NpcNear) continue;
            var opts = await OpenMenuAsync(npc, false, ct).ConfigureAwait(false);
            var op = Match(opts, words, wantCmd);
            if (op == null) continue;
            int modeMark = Count(_repairMode);
            if (!await SelectAsync(npc, op.Value, () => Count(_repairMode) > modeMark, ct).ConfigureAwait(false)) continue;
            int cMark = Count(_repairCosts);
            await _rt.SendQueryRepairCostAsync(npc.Id, it.MakeIndex, it.Name, ct).ConfigureAwait(false);
            bool costed = await WaitUntil(() => Count(_repairCosts) > cMark, ReplyMs, ct).ConfigureAwait(false);
            int cost = costed ? Take(_repairCosts, cMark)[^1] : 0;
            await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
            // 报价 -1 是服务端明说"这件我修不了"(类别不匹配);0 是压根没出价。强行修理就照发。
            if (cost <= 0 && !force)
            {
                Trace($"[脚本] {npc.Name} 不给 {it.Name} 修理(报价 {(cost == -1 ? "不可修理" : cost.ToString())})");
                continue;
            }
            int rMark = Count(_repairResults);
            int gold = _rt.Player.Gold;
            await _rt.SendRepairItemAsync(npc.Id, it.MakeIndex, it.Name, ct).ConfigureAwait(false);
            bool done = await WaitUntil(() => Count(_repairResults) > rMark, ReplyMs, ct).ConfigureAwait(false);
            var after = Bag().FirstOrDefault(b => b.MakeIndex == it.MakeIndex);
            Trace($"[脚本] {kind} {it.Name} 报价 {cost} => {(done ? Take(_repairResults, rMark)[^1] : "6 秒无回包")}" +
                  $",持久 {after.DuraCount}/{after.DuraMax}(修前 {it.DuraCount}/{it.DuraMax}),金币 {gold}→{_rt.Player.Gold}");
            string? wear = equipTo ?? (fromSlot >= 0 && fromSlot < SlotNames.Length ? SlotNames[fromSlot] : null);
            if (wear != null) await EquipAsync(it.Name, wear, ct).ConfigureAwait(false);
            await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
            return;
        }
        Trace($"[脚本] 视野里没有肯修 {it.Name} 的商店(类别闸门:武器店只收 StdMode 5/6,杂货店只收消耗品那一档)");
    }

    /// <summary>修理对象可以写部位("武器")也可以写物品名。部位先取身上那件,再退到背包里同类最旧的一件。</summary>
    private BagItemInfo ResolveRepairable(string target)
    {
        var bag = Bag().Where(b => b.MakeIndex > 0 && b.StdMode > 4 && !b.Stackable && b.DuraMax > 0).ToList();
        foreach (var b in bag.OrderBy(b => b.DuraCount))
            if (MatchName(b.Name, target)) return b;
        int slot = SlotIndex(target);
        if (slot >= 0)
        {
            var w = WornAt(slot);
            if (w.MakeIndex > 0) return w;
            return bag.Count > 0 ? bag.OrderBy(b => b.DuraCount).First() : default;
        }
        return default;
    }

    // ============================================================
    //  技能 / 目标 / 宝宝 / 开关
    // ============================================================
    public async Task SkillAsync(string skill, int x, int y, CancellationToken ct)
    {
        int magicId = MagicIdOf(skill);
        if (magicId < 0) { Trace($"[脚本] 使用技能[{skill}]:技能列表里没有(名字要和服务端下发的 sMagicName 一致)"); return; }
        long target = 0;
        if (x == 0 && y == 0)
        {
            var dot = NearestMonster(12).Dot;
            if (dot != null) { target = dot.Value.Id; x = dot.Value.X; y = dot.Value.Y; }
        }
        if (target != 0) LastAttackTargetId = target;
        await _rt.SendSpellAsync(target, Math.Max(0, x), Math.Max(0, y), (ushort)magicId, ct).ConfigureAwait(false);
        // 服务端按 Magic.DB 的 Delay 限速,快于它的那一发会被整包吞掉(不发、也不回包)
        int gap = 900 + (_rt.MagicDelayMs.TryGetValue(magicId, out int d) ? d : 0);
        Trace($"[脚本] 使用技能[{skill}](id={magicId}) " + (target != 0 ? $"目标 #{target}@({x},{y})" : "地面坐标") +
              $",下一发间隔≥{gap}ms");
        await Task.Delay(gap, ct).ConfigureAwait(false);
    }

    private int MagicIdOf(string skill)
    {
        foreach (string s in _rt.SnapshotMagics())       // 形如 "灵魂火符/4"
        {
            int slash = s.LastIndexOf('/');
            if (slash <= 0) continue;
            if (MatchName(s[..slash], skill) && int.TryParse(s[(slash + 1)..], out int id)) return id;
        }
        return -1;
    }

    public Task AbandonTargetAsync(CancellationToken ct)
    {
        LastAttackTargetId = 0;
        // 挂机 AI 每轮自己按距离重挑目标,脚本能做的"放弃"就是别再给它留粘性目标
        Trace("[脚本] 放弃攻击目标");
        return Task.CompletedTask;
    }

    public Task PetAsync(string cmd, CancellationToken ct)
    {
        // 本服的上行包里没有"宝宝模式"这一档(Grobal2.pas 无 CM_BABY/CM_PET 攻击模式,只有宝宝背包/掉落),
        // 狗狗的行为只能靠技能设置,所以这条命令在这里到顶了。
        Unsupported("宝宝" + cmd, "宝宝" + cmd);
        return Task.CompletedTask;
    }

    public Task FlagAsync(string flag, bool on, CancellationToken ct)
    {
        bool wired = true;
        switch (flag)
        {
            case "战斗":
                if (_ai != null) { _ai.Enabled = on; if (on) _ai.Start(); else _ai.Stop(); }
                break;
            case "拣物":
            case "拾物":
            case "拾取":
                if (_ai != null) _ai.AutoPickupEnabled = on;
                break;
            case "穿怪":
                _rt.WalkThroughMonsters = on;
                break;
            case "穿人":
                _rt.WalkThroughPlayers = on;
                break;
            case "挖矿":
                // 本机 M2Engine 源码里根本没有 MineSet/挖矿这条通道(只有怪掉矿),所以只记开关、不发任何包。
                Flags["挖矿"] = on;
                wired = false;
                break;
            default:
                Flags[flag] = on;
                wired = false;
                break;
        }
        // 天骥的开关命令里有一半是它自己客户端的行为(闪避/引怪/移动刺杀/躲避PKER/字符标准化…),
        // 本机没有对应动作可接。日志必须说清"只记了一笔",否则脚本作者会以为参数生效了。
        Trace($"[脚本] {flag}{(on ? "开启" : "关闭")}" + (wired ? "" : "(只记下开关,本机没有对应行为)"));
        return Task.CompletedTask;
    }

    public Task RangeAsync(string which, int n, CancellationToken ct)
    {
        if (_ai == null) { Flags[which + "范围"] = n > 0; return Task.CompletedTask; }
        if (which.Contains("怪物")) _ai.FightRange = Math.Clamp(n, 1, 40);
        else _ai.PickupThreatRadius = Math.Clamp(n, 0, 20);
        Trace($"[脚本] {which}范围 = {n} 格");
        return Task.CompletedTask;
    }

    public Task GroupTalkAsync(string to, string text, CancellationToken ct)
        => IpcAsync($"组队通信[{to}]:{text}", ct);

    public Task GroupModeAsync(string mode, CancellationToken ct)
    {
        Flags["组队模式"] = mode.Contains("雷锋");
        Trace($"[脚本] 组队模式[{mode}](只记录;自动让怪/让经验的策略层还没接)");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>?> ReadScriptAsync(string name, CancellationToken ct)
    {
        var custom = ScriptReader?.Invoke(name);
        if (custom != null) return Task.FromResult<IReadOnlyList<string>?>(custom);
        if (_ch == null) return Task.FromResult<IReadOnlyList<string>?>(null);
        var t = _ch.Script(name);
        return Task.FromResult<IReadOnlyList<string>?>(t.Exists ? t.Lines : null);
    }

    public async Task LogOutAsync(int ms, CancellationToken ct)
    {
        // 天骥的"小退"是下线一段时间再上线(躲刷新/避检测)。断开重连的编排不在这一层:
        // 引擎线程还在跑,这里直接 SendSoftCloseAsync 会让脚本对着死连接发一堆包。
        Trace($"[脚本] 小退 {ms / 1000} 秒:交给重连流程" + (ReloginRequested == null ? "(当前宿主没接重连,先只等这么长时间)" : ""));
        ReloginRequested?.Invoke(Math.Max(1000, ms));
        await Task.Delay(Math.Min(2000, Math.Max(0, ms)), ct).ConfigureAwait(false);
    }

    public async Task SendAsync(string text, bool backdoor, CancellationToken ct)
    {
        await _rt.SendChatAsync(text, ct).ConfigureAwait(false);
        Trace($"[脚本] {(backdoor ? "后门命令" : "发送命令")}: {text}");
        await Task.Delay(SendGapMs, ct).ConfigureAwait(false);
    }

    public string PlayerSpot(string name)
    {
        // 只认视野里的人(服务端从不下发"按名字查任意玩家坐标"的查询包),没见过就返回空串,
        // 脚本里 走到跟前[$当前位置[某某]] 会因参数不成立而原地不动,而不是走到 (0,0)。
        foreach (var d in _rt.Dots.Values)
            if (d.Kind == DotKind.Player && MatchName(d.Name, name))
                return $"{_rt.CurrentMap},{d.X},{d.Y}";
        Trace($"[脚本] 当前位置[{name}]:视野里没有这个人");
        return string.Empty;
    }

    public Task IpcAsync(string verb, CancellationToken ct)
    {
        Trace($"[脚本] {verb}:主控/被控/策略那一套要多个进程互通,单机版只在日志留一行");
        return Task.CompletedTask;
    }

    public void Unsupported(string verb, string raw)
    {
        UnsupportedCount++;
        Trace($"[脚本] 命令未实现: {raw}");
    }

    // ============================================================
    //  IValueSource
    // ============================================================
    /// <summary>[存在选择内容][XX]:当前这一页对话里有没有 XX。天骥拿它判断"这个 NPC 今天给不给这条选项"。</summary>
    public int ChoiceExists(string text)
    {
        var page = CurrentPage();
        if (page == null) return 0;
        foreach (var o in page)
            if (o.Display.Contains(text, StringComparison.Ordinal)) return 1;
        return 0;
    }

    public string MapCode => _rt.CurrentMap;
    public string MapName => _rt.Player.MapName;
    public int X => _rt.Player.PosX;
    public int Y => _rt.Player.PosY;

    public string ActorField(string who, string field)
    {
        if (who is "攻击目标" or "宝宝攻击目标") return TargetField(field);
        var p = _rt.Player;
        return field switch
        {
            "HP" or "生命" => p.Hp.ToString(),
            "MAXHP" => p.MaxHp.ToString(),
            "MP" => p.Mp.ToString(),
            "MAXMP" => p.MaxMp.ToString(),
            "等级" => p.Level.ToString(),
            "名称" => p.Name,
            "性别" => p.Sex == 0 ? "男" : "女",
            "职业" => p.Job switch { 1 => "法师", 2 => "道士", _ => "战士" },
            "负重" => p.Weight.ToString(),
            "最大负重" => p.MaxWeight.ToString(),
            "背包空位" => Math.Max(0, _rt.MaxBagCount - Bag().Length).ToString(),
            // 服务端从不下发 PK 值(没有对应 SM_*),脚本里的红名判断只能恒为 0
            "红名" => "0",
            "金币" => p.Gold.ToString(),
            _ => "0",
        };
    }

    private string TargetField(string field)
    {
        long id = LastAttackTargetId;
        if (id == 0 || !_rt.Dots.TryGetValue(id, out var dot)) return field.Contains("名") ? string.Empty : "0";
        if (field.Contains("名")) return dot.Name;
        if (_rt.ObjectHpMap.TryGetValue(id, out var hp))
            return field.Contains("MAXHP") ? hp.MaxHp.ToString() : hp.Hp.ToString();
        return "0";
    }

    public string EquipField(string slot, string metric)
    {
        var it = ResolveWornOrBag(slot);
        if (it.MakeIndex == 0) return metric.Contains("名") ? string.Empty : "0";
        if (metric.Contains("名")) return it.Name;
        if (metric.Contains("最大持久")) return it.DuraMax.ToString();
        if (metric.Contains("持久")) return it.DuraCount.ToString();
        if (metric.Contains("使用次数")) return it.Count.ToString();
        return "0";
    }

    private BagItemInfo ResolveWornOrBag(string slot)
    {
        int where = SlotIndex(slot);
        var it = WornAt(where);
        if (it.MakeIndex > 0) return it;
        var use = _rt.SnapshotUseItems();
        for (int i = 0; i < use.Length; i++)
            if (use[i].MakeIndex > 0 && MatchName(use[i].Name, slot)) return use[i];
        return FindBag(slot).FirstOrDefault();
    }

    public int EquipStat(string slot, string stat)
    {
        // 装备加值在 TClientItem 的 HP..SC2 那 12 个 DWORD 里,而 #49 那批解析没取它们。
        // 这里给 0 而不是猜一个数:脚本里的 [武器][攻击] 只会因此判假,不会做出错误动作。
        return 0;
    }

    public string StatusField(string status)
    {
        // 中毒/隐身/开盾 在服务端是 m_nCharStatus 的位,客户端从 CharDesc 的第二个 DWORD 收到;
        // 现在 MapDot 只存了 Feature、没存 Status,所以这里一律给 0(假)。
        // 要接上得先给 MapDot 加 Status 并在 SM_* 里填,别拿 Feature 硬凑。
        return "0";
    }

    public int DirectionCount(string directions, string countWord, int range)
    {
        var wanted = DirectionSet(directions);
        int px = _rt.Player.PosX, py = _rt.Player.PosY;
        int face = _rt.Player.Direction;
        int n = 0;
        foreach (var dot in _rt.Dots.Values)
        {
            if (!KindMatches(dot, countWord)) continue;
            int d = Math.Max(Math.Abs(dot.X - px), Math.Abs(dot.Y - py));
            if (d > range || d == 0) continue;
            int rel = (BotCombatAI.GetDirection(px, py, dot.X, dot.Y) - face + 8) % 8;
            if (wanted.Contains(rel)) n++;
        }
        return n;
    }

    /// <summary>方向词 → 相对朝向的偏移(服务端方向顺时针:0 上、2 右、4 下、6 左)。</summary>
    private static HashSet<int> DirectionSet(string directions)
    {
        var set = new HashSet<int>();
        foreach (string raw in directions.Split(',', '，'))
        {
            string s = raw.Trim();
            if (s == "所有方向" || s == "全部") { for (int i = 0; i < 8; i++) set.Add(i); continue; }
            int off = s switch
            {
                "前" => 0, "右前" => 1, "右" => 2, "右后" => 3, "后" => 4, "左后" => 5, "左" => 6, "左前" => 7,
                _ => -1,
            };
            if (off >= 0) set.Add(off);
        }
        if (set.Count == 0) for (int i = 0; i < 8; i++) set.Add(i);
        return set;
    }

    private bool KindMatches(MapDot dot, string countWord)
    {
        switch (countWord)
        {
            case "怪物": return dot.Kind == DotKind.Monster;
            case "玩家": return dot.Kind == DotKind.Player;
            case "NPC":
            case "npc": return dot.Kind == DotKind.Npc;
            case "物品": return dot.Kind == DotKind.Item;
            default: return dot.Kind is DotKind.Monster or DotKind.Player && MatchName(dot.Name, countWord);
        }
    }

    public int CountAt(string countWord, int x, int y)
        => _rt.Dots.Values.Count(d => KindMatches(d, countWord) && Math.Max(Math.Abs(d.X - x), Math.Abs(d.Y - y)) <= 1);

    public int ItemCount(string nameOrKind)
    {
        var bag = Bag();
        switch (nameOrKind)
        {
            case "金币": return _rt.Player.Gold;
            case "背包空位": return Math.Max(0, _rt.MaxBagCount - bag.Length);
        }
        if (nameOrKind.StartsWith("待售")) return bag.Count(b => Sellable(b, nameOrKind[2..]));
        if (nameOrKind.StartsWith("待存")) return bag.Count(b => Storable(b, nameOrKind[2..]));
        if (nameOrKind.Contains("空位")) return Math.Max(0, _rt.MaxBagCount - bag.Length);
        int redBlue = PotionCount(nameOrKind, bag);
        if (redBlue >= 0) return redBlue;
        int pieces = bag.Where(b => MatchName(b.Name, nameOrKind))
                        .Sum(b => b.Stackable ? Math.Max(1, b.Count) : 1);
        if (pieces > 0) return pieces;
        // 包里一件都没有时,天骥把这个位置交给"视野里的东西":[蜈蚣][数量] = 视野里的蜈蚣只数
        return _rt.Dots.Values.Count(d => KindMatches(d, nameOrKind));
    }

    /// <summary>[红药][数量] / [蓝药][数量]:按角色 good.ini 的用途码归类(0 红药 1 蓝药)。</summary>
    private int PotionCount(string kind, BagItemInfo[] bag)
    {
        bool red = kind.Contains("红") || kind.Contains("金创");
        bool blue = kind.Contains("蓝") || kind.Contains("魔法");
        if (!red && !blue) return -1;
        var names = _ch?.Good.NamesOfKind(blue && !red ? 1 : 0).ToHashSet();
        int n = 0;
        foreach (var b in bag)
        {
            bool hit = (names != null && names.Contains(b.Name))
                       || (MatchName(b.Name, kind) && (b.Name.Contains("药") || kind.Length <= 2));
            if (hit) n += b.Stackable ? Math.Max(1, b.Count) : 1;
        }
        return n;
    }

    /// <summary>物品设置(itemset.ini)第 4 列是"档次",天骥没写文档。这里按
    /// "档次&gt;=1 可卖、&gt;=2 该存仓"解释 —— 真机若发现某类判错,只改这两个谓词即可。</summary>
    private bool Sellable(BagItemInfo b, string category)
    {
        if (b.MakeIndex <= 0) return false;
        string? cat = CategoryOf(b.Name);
        if (cat == null || cat is "药品" or "书籍" or "金钱") return false;
        if (category.Length > 0 && !cat.Contains(category)) return false;
        return _ch == null ? b.StdMode > 4 : _ch.ItemSet.Grade(b.Name) >= 1;
    }

    private bool Storable(BagItemInfo b, string category)
    {
        if (b.MakeIndex <= 0 || b.Stackable) return false;
        string? cat = CategoryOf(b.Name);
        if (cat == null || cat is "药品" or "金钱") return false;
        if (category.Length > 0 && !cat.Contains(category)) return false;
        return _ch == null || _ch.ItemSet.Grade(b.Name) >= 2;
    }

    private string? CategoryOf(string name) => _ch?.ItemSet.Category(name);

    private IEnumerable<string> CategoryNames(string category)
    {
        if (_ch == null) return Array.Empty<string>();
        string cat = category.Replace("待售", "").Replace("待存", "").Trim();
        return cat.Length == 0 ? Array.Empty<string>() : _ch.ItemSet.NamesInCategory(cat).ToList();
    }

    public int ItemMetric(string itemName, string metric)
    {
        var bag = Bag().Where(b => MatchName(b.Name, itemName) && b.MakeIndex > 0).ToList();
        if (bag.Count == 0) return 0;
        if (metric.Contains("最大持久")) return bag.Max(b => b.DuraMax);
        bool stackableOnly = bag.All(b => b.Stackable);
        if (metric.Contains("使用次数"))
        {
            // 护身符/卷轴这类"次数型"物品:Dura 就是用剩的次数(没有 DuraMax);叠加物压根没有次数这回事
            var uses = bag.Where(b => !b.Stackable && b.DuraMax <= 1).Select(b => b.DuraCount).ToList();
            return uses.Count > 0 ? uses.Max() : 0;
        }
        // 天骥文档:多件同名取"持久最低"的那件(脚本正是拿它当"该修了"的判据)
        var duras = bag.Where(b => !b.Stackable).Select(b => b.DuraCount).ToList();
        return duras.Count > 0 ? duras.Min() : bag.Min(b => b.DuraCount);
    }

    public int Distance(string mapCode, int x, int y)
    {
        // 异图没法用格数算(要按门点链估),给一个必然超过任何阈值的大数:
        // 真脚本用 [3,330,330][距离]>200 做"回城判断",人在别的图上时本来就该判真。
        if (mapCode.Length > 0 && !SameMap(mapCode)) return 9999;
        return Math.Max(Math.Abs(x - _rt.Player.PosX), Math.Abs(y - _rt.Player.PosY));
    }

    public int StorageCount(string itemName)
    {
        var st = _rt.SnapshotStorage();
        if (itemName.Length == 0 || itemName.Contains("物品")) return st.Length;
        if (itemName.Contains("空位")) return Math.Max(0, 46 - st.Length);
        return st.Where(s => MatchName(s.Name, itemName)).Sum(s => s.Stackable ? Math.Max(1, s.Count) : 1);
    }

    // ============================================================
    //  部位名 ↔ 服务端 U_* 槽号
    // ============================================================
    /// <summary>下标=Grobal2 的 U_* 值(0..17),和真机装备包一致。</summary>
    private static readonly string[] SlotNames =
    {
        "衣服", "武器", "照明", "项链", "头盔", "左手镯", "右手镯", "左戒指", "右戒指", "符",
        "腰带", "靴子", "宝石", "斗笠", "鼓", "马", "盾牌", "灵玉",
    };

    private static string SlotLabel(int slot) => slot >= 0 && slot < SlotNames.Length ? SlotNames[slot] : slot.ToString();

    private static int SlotIndex(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return -1;
        s = s.Trim();
        if (int.TryParse(s, out int n) && n >= 0 && n < Grobal2.MAX_USE_ITEM_COUNT) return n;
        for (int i = 0; i < SlotNames.Length; i++)
            if (s == SlotNames[i] || (SlotNames[i].Length > 1 && s.Contains(SlotNames[i]))) return i;
        return s switch
        {
            "马牌" or "坐骑" => Grobal2.U_HORSE,
            "符毒" or "护身符" => Grobal2.U_BUJUK,
            "戒指" => Grobal2.U_RINGL,
            "手镯" => Grobal2.U_ARMRINGL,
            _ => -1,
        };
    }

    /// <summary>没写部位时按 StdMode 猜服务端会接受的槽:和 WPF 的 ResolveEquipSlot 同一套映射。</summary>
    private static int StdModeSlot(byte stdMode) => stdMode switch
    {
        10 => Grobal2.U_DRESS,
        5 or 6 => Grobal2.U_WEAPON,
        7 => Grobal2.U_HELMET,
        8 or 9 => Grobal2.U_NECKLACE,
        20 or 21 or 22 or 23 or 24 or 26 => Grobal2.U_RINGL,
        27 => Grobal2.U_BUJUK,
        28 => Grobal2.U_BOOTS,
        29 => Grobal2.U_BELT,
        _ => -1,
    };
}
