using BotClient.Net;
using BotClient.Human;
using BotClient.Protocol;
using BotClient.Session.Combat;

namespace BotClient.Session;

/// <summary>
/// AI 战斗引擎 — 自动寻怪 → 走近 → 攻击 → 喝药 → 逃跑。
/// 使用修正后的协议: CM_HIT(3014)攻击, CM_WALK(3011)行走, 坐标打包到Recog。
/// </summary>
public sealed class BotCombatAI
{
    private readonly BotSession _session;
    private readonly BotRuntime _runtime;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public bool Enabled { get; set; }
    public bool FightAtPoint { get; set; }
    public int FightPointX { get; set; }
    public int FightPointY { get; set; }
    public int FightRange { get; set; } = 12;

    /// <summary>法术条配置:勾选"魔法攻击"并填法术ID后,攻击自动改用对应魔法(CM_SPELL),否则物理(CM_HIT)。</summary>
    public bool MagicAttackEnabled { get; set; }
    public ushort MagicId { get; set; }

    /// <summary>
    /// 技能循环（多技能按优先级+条件选用）。null / Enabled=false = 退回改造前的"单一 MagicId"行为。
    /// 打开后每轮攻击先问它一遍：按优先级找第一个条件满足且冷却已到的技能，全不满足才退回物理/单法术。
    /// </summary>
    public SkillRotationPlan? SkillPlan { get; set; }

    /// <summary>
    /// 拟人化档。对本类的影响只有一处：攻击/施法的实际间隔在下限之上叠加随机冗余。
    /// 鼠标轨迹、按键时长、停顿由 ClientDriver 的 InputSimulator 读同一份配置（按分辨率/进程存档）。
    /// </summary>
    public HumanTuning Human { get; set; } = new();

    private SkillRotation? _rotation;

    // 保护设置
    public int HpPotionPercent { get; set; } = 60;
    public int MpPotionPercent { get; set; } = 40;
    public int EscapeHpPercent { get; set; } = 20;

    // 命令节流间隔(ms) —— 服务端 SpeedControl=1 时按 !Setup.txt 的间隔判定,不满足整包丢弃(不是排队),
    // 连续超速还会在 M2 控制台打 [攻击超速] 并重置计数。判定式:移动/攻击是 距上次 >= 间隔,
    // 使用物品是 距上次 > 间隔(且所有物品共用同一个 m_dwClientUseItemsTick)。
    // 本机现值: WalkIntervalTime/RunIntervalTime=490、TurnIntervalTime=500、HitIntervalTime=900、
    // ActionIntervalTime=400 但 ControlActionInterval=0(跨动作切换不限制),
    // 用药: 普通药(StdMode=0,Shape=0)300 / 特殊药(Shape∈{1,101,102},如太阳水)1000 / 其余 UseItemIntervalTime=500。
    // HitIntervalTime 还会减去 dwIncSpeedDecInterval*m_nAttackSpeed(攻速加成),没攻速的号就是 900。
    public int WalkIntervalMs { get; set; } = 650;
    public int AttackIntervalMs { get; set; } = 950;
    public int PotionIntervalMs { get; set; } = 1050;
    public int PickupIntervalMs { get; set; } = 500;

    // 魔法限速是单独一档:服务端 ClientSpellXY(ObjPlayer.pas:19068) 比的是
    //   距上次施法 >= m_dwMagicAttackInterval,而该值在每次施法成功后按
    //   MagicInfo.dwMagicDelayTime + g_Config.dwMagicHitIntervalTime 重算(:19220),
    //   本机 !Setup.txt MagicHitIntervalTime=1150。不足值且差额 > 1150/3 时整包静默丢弃,
    //   连续 4 次才在 M2 控制台打 [魔法超速] —— 所以法系按物理的 950ms 打会白丢一半。
    // Delay 服务端会用 SM_SENDMYMAGIC 的 TClientMagic 发下来(Runtime.MagicDelayMs),优先用它;
    // 技能列表还没收到时退回 SpellIntervalMs(1750 ≈ Delay 600 的攻击魔法)。
    public int MagicHitIntervalMs { get; set; } = 1150;
    public int SpellIntervalMs { get; set; } = 1750;

    /// <summary>当前攻击方式应遵守的发包间隔(法术按各自 Delay 自适应，再叠加拟人随机冗余)。</summary>
    public int CurrentAttackIntervalMs()
    {
        int baseInterval;
        if (!MagicAttackEnabled || MagicId == 0)
        {
            baseInterval = AttackIntervalMs;
        }
        else
        {
            int interval = _runtime.MagicDelayMs.TryGetValue(MagicId, out int delay)
                ? delay + MagicHitIntervalMs
                : SpellIntervalMs;
            baseInterval = Math.Max(interval, AttackIntervalMs);
        }
        return baseInterval + HumanJitterMs(baseInterval);
    }

    /// <summary>
    /// 拟人化节奏抖动：在服务端限速**下限之上**再叠加一段随机等待。
    ///
    /// 为什么只能加不能减：下限（HitIntervalTime / MagicDelay+MagicHitIntervalTime）是服务端的
    /// 硬判定，低于它整包被静默丢弃 —— 那不是"更像人"，那是白丢输出。
    /// 为什么用右偏长尾而不是均匀随机：均匀随机的"每次都在固定区间里等一段"本身就是可统计特征
    /// （区间上下界会从数据里浮出来）；右偏分布才是真人节奏的形状（多数很快、偶尔慢一拍）。
    /// </summary>
    private int HumanJitterMs(int baseInterval)
    {
        if (Human is null || !Human.Enabled || Human.CombatJitterRatio <= 0) return 0;
        int span = Math.Max(1, (int)Math.Round(baseInterval * Human.CombatJitterRatio));
        return Math.Min(HumanTiming.LongTail(0, span), span * 3);   // 封顶，别把节奏拖失衡
    }

    /// <summary>给日志用的一行间隔说明:法术间隔要能看出它取的是哪个来源,不然真机上没法判断为什么慢。</summary>
    public string IntervalSettingsText()
    {
        string spell = "未启用";
        if (MagicAttackEnabled && MagicId != 0)
        {
            string src = _runtime.MagicDelayMs.TryGetValue(MagicId, out int d) ? $"Delay={d}" : "Delay未知";
            spell = $"{CurrentAttackIntervalMs()}({src}+{MagicHitIntervalMs})";
        }
        return $"移动:{WalkIntervalMs} 攻击:{AttackIntervalMs} 用药:{PotionIntervalMs} 拾取:{PickupIntervalMs} 法术:{spell}";
    }

    // 事件
    public event Action<string>? Log;
    public event Action? Escaped;
    /// <summary>逃跑失败 → 已经发了 CM_SOFTCLOSE 主动下线。上层据此在重连进世界后把挂机重新按上
    /// (回城卷那一路不发这个,因为角色已经安全到家、不再需要被拉回战场)。</summary>
    public event Action? EscapeSoftClosed;
    /// <summary>发出一次 CM_PICKUP。击杀/经验信号在 BotRuntime 上(MonsterKilled/ExpGained)。</summary>
    public event Action<string>? PickupSent;

    private DateTime _lastAttack = DateTime.MinValue;
    private DateTime _lastWalk = DateTime.MinValue;
    private DateTime _lastPotion = DateTime.MinValue;
    private DateTime _lastPickup = DateTime.MinValue;
    /// <summary>拾取没有"失败"回包:物品属于别人时服务端只回一条红字(ObjPlayer.pas:20503),
    /// 走不到时更是零反馈 —— 唯一能观测到的信号是"对着同一件物品、人一步都没挪"。
    /// 连击到 PickupStuckTries 次就给这件物品一段冷却,否则挂机时会永远卡在最捡不走的那件上。</summary>
    private const int PickupStuckTries = 3;
    private readonly Dictionary<long, DateTime> _pickupCooldown = new();
    private long _stuckItemId;
    private int _stuckX = int.MinValue, _stuckY = int.MinValue;
    private int _stuckTries;
    private long _lastLoggedTargetId = -1;

    /// <summary>同一件地面物品被判定"捡不走"后搁置多久(毫秒)。</summary>
    public int PickupCooldownMs { get; set; } = 15000;

    //  0=还能捡 1=背包格已满 2=负重已到顶。和武器持久那条一样,只在状态变化时记一行:
    //  这一轮循环 50ms 一次,每轮都写就把日志刷爆了。
    private int _pickupRoomState;
    private bool _wasDead;

    private void ReportPickupRoom()
    {
        int state = _runtime.BagIsFull ? 1 : (_runtime.WeightIsMaxed ? 2 : 0);
        if (state == _pickupRoomState) return;
        _pickupRoomState = state;
        Log?.Invoke(state switch
        {
            1 => $"[pickup] 背包 {_runtime.BagItems.Count}/{_runtime.MaxBagCount} 格已满,只捡金币:" +
                 "物品服务端会整包丢弃且零回包,先卖掉或存仓库再挂机",
            2 => $"[pickup] 负重 {_runtime.Player.Weight}/{_runtime.Player.MaxWeight} 已到顶,只捡金币:" +
                 "再加重量服务端就会整包丢弃且零回包,先丢掉或存仓库里的重物",
            _ => "[pickup] 背包腾出了空间,恢复捡物品"
        });
    }

    /// <summary>追击也是同一类循环:服务端不会告诉我们"这只怪走不到"(隔着一面墙时 BFS 直接返回空,
    /// 而 FindNearestMonster 永远把最近的那只挑出来)⇒ 不换目标就是整轮挂机原地站着。
    /// 判据用"还剩几步路有没有创出新低":直线距离会骗人(绕墙时它忽大忽小),
    /// 剩余路径步数只在真的靠近时才减。原地不动和绕圈打转都会撞上,而怪自己走开也算没进展。</summary>
    private const int ChaseStuckTries = 8;
    private readonly Dictionary<long, DateTime> _chaseBlocked = new();
    private long _chaseTargetId;
    private int _chaseBestRemain = int.MaxValue;
    private int _chaseStuck;
    /// <summary>连着几只怪都"追不上"的次数(出刀一次就清零)。冷却是逐只的,视野里只要还有
    /// 没进冷却的怪就永远有目标 ⇒ "target==null"这条判据在怪多的地图根本触发不了。
    /// 真机实测(2026-09-23 新手村,90 秒):13 只怪在视野里,4 只轮流进 20s 冷却、
    /// 换目标换到永远轮不完,全程零移动零输出 —— 靠这个计数才能认出"整片都隔着墙"。</summary>
    private int _chaseGiveUps;

    /// <summary>追一只怪追了 ChaseStuckTries 轮都没再靠近过 ⇒ 冷却它一段时间并换目标。
    /// 但"没靠近"有两种完全不同的原因:①真的隔着墙/被怪群挤不进去;②本地坐标是幻影 ——
    /// 走步被服务端整包丢弃时不给任何回包(踩到活对象那一格,见 BotRuntime 里 WalkTo 的注释),
    /// 可 SendWalkAsync 已经乐观把本地坐标往前挪了,于是客户端以为自己贴着怪、算出来的路径却从
    /// 幻影格出发,越走越偏。真机实测(2026-09-23):100 秒里 313 个移动/拾取/转身包、
    /// 服务端却始终说我们在出生点,距离"停在 3~5 格没再缩短"就是这个形状。
    /// 所以判"追不上"之前先花一个原地 CM_TURN 把真实坐标问回来:问回来越说明是②,
    /// 本轮作废按真值重算(不记一次追不上);问不回来越说明两边一致,才是真的①。</summary>
    private async Task<bool> NoteChaseProgressAsync(long targetId, int dist, int remain, CancellationToken ct)
    {
        if (targetId != _chaseTargetId)
        {
            _chaseTargetId = targetId;
            _chaseBestRemain = int.MaxValue;
            _chaseStuck = 0;
        }
        bool noPath = remain < 0;
        if (!noPath && remain < _chaseBestRemain)
        {
            _chaseBestRemain = remain;
            _chaseStuck = 0;
            return false;
        }
        // 寻路直接判死(目标四周全被占/全是墙)⇒ 不需要再白攒 8 轮,当场认定追不上
        if (!noPath && ++_chaseStuck < ChaseStuckTries) return false;
        _chaseStuck = 0;
        // 攒满一轮"没进展"先对表:坐标被服务端改写过 ⇒ 之前那些距离量的是幻影,这一轮不算追不上
        if (await _runtime.ResyncPositionAsync(ct).ConfigureAwait(false))
        {
            _chaseBestRemain = int.MaxValue;
            return true;
        }
        _chaseBestRemain = int.MaxValue;
        _chaseBlocked[targetId] = DateTime.UtcNow.AddMilliseconds(ChaseCooldownMs);
        _chaseGiveUps++;
        foreach (var kv in _chaseBlocked.Where(kv => kv.Value <= DateTime.UtcNow).ToList())
            _chaseBlocked.Remove(kv.Key);
        Log?.Invoke($"[战斗] 追不上 id={targetId}({(noPath
                ? $"({dist} 格外)无路可走:目标那格连四周都不可走"
                : $"绕了 {ChaseStuckTries} 步还剩 {remain} 步,一点没缩短")})," +
                    $"{ChaseCooldownMs / 1000}s 内不再追它,先换目标");
        return false;
    }

    /// <summary>连续追不上几只怪就认定"这一片都隔着墙",挪位置换视野。</summary>
    public int RelocateGiveUpTries { get; set; } = 3;

    /// <summary>追不上某只怪之后搁置多久(毫秒)。</summary>
    public int ChaseCooldownMs { get; set; } = 20000;

    /// <summary>"视野里有怪却一只都追不上"时,隔多久挪一次位置。</summary>
    public int RelocateIntervalMs { get; set; } = 6000;
    private DateTime _lastRelocateUtc = DateTime.MinValue;

    /// <summary>站在墙根下的两种形状:①视野里的怪全进了"追不上"冷却 ⇒ 找不到目标;
    /// ②怪多到冷却轮不完(13 只里有 4 只被冷却,永远还剩 9 只可追)⇒ 目标一直在、人却一动不动。
    /// ①靠 target==null 触发,②靠连续 RelocateGiveUpTries 次追不上触发。真机实测(2026-09-23 新手村)
    /// 是②:90 秒里 4 只怪轮流冷却、零移动零输出。视野里没怪则一律不挪 —— 那会走出用户要的范围。</summary>
    private async Task RelocateIfBoxedInAsync(DateTime now, CancellationToken ct)
    {
        // 视野里根本没怪是另一回事("没得打"),不该满地图乱晃:那会走出用户要的范围,还会把怪引远。
        int inView = 0;
        foreach (var d in _runtime.Dots.Values)
        {
            if (d.Kind != DotKind.Monster) continue;
            if (_runtime.ObjectHpMap.TryGetValue(d.Id, out var h) && h.Hp <= 0) continue;   // 尸体不算
            inView++;
        }
        if (inView == 0) return;
        if ((now - _lastRelocateUtc).TotalMilliseconds < RelocateIntervalMs) return;
        _lastRelocateUtc = now;
        // 一轮"连续追不上"只挪一次身:计数清零,再攒满才挪下一次(否则每 50ms 换方向乱逛)
        int giveUps = Math.Max(1, _chaseGiveUps);
        _chaseGiveUps = 0;
        // 挪之前先对表:随机格是按本地坐标取的,本地是幻影就会往服务端那侧的墙里钻(真机那次
        // "换个位置"连报两遍、人却原地不动,正是因为这个)。
        await _runtime.ResyncPositionAsync(ct).ConfigureAwait(false);

        Func<int, int, bool> isWalk = _runtime.EffectiveWalkable(_runtime.IsWalkable);
        int px = _runtime.Player.PosX, py = _runtime.Player.PosY;
        (int X, int Y)? cell = null;
        for (int tryN = 0; tryN < 6 && cell == null; tryN++)
        {
            int tx = px + _rnd.Next(-6, 7), ty = py + _rnd.Next(-6, 7);
            if (Math.Abs(tx - px) < 2 && Math.Abs(ty - py) < 2) continue;      // 挪 2 格以内等于没挪
            if (isWalk(tx, ty)) cell = (tx, ty);
        }
        if (cell == null) return;
        Log?.Invoke($"[战斗] 视野里 {inView} 只怪,连着 {giveUps} 只追不上(隔着障碍或它们自己走开了)," +
                    $"往 ({cell.Value.X},{cell.Value.Y}) 挪几步换个位置");
        for (int step = 0; step < 3 && !ct.IsCancellationRequested; step++)
        {
            // 这个随机格也走不到就到此为止,下一轮换个格再挪(别对着到不了的格子空发指令)
            if (await MoveTowardAsync(cell.Value.X, cell.Value.Y, ct).ConfigureAwait(false) < 0) return;
            await Task.Delay(WalkIntervalMs, ct);
        }
    }

    /// <summary>武器持久低于该比例先提醒一次。</summary>
    public int WeaponWarnDuraPercent { get; set; } = 25;

    /// <summary>武器持久低于该比例就停手(不出刀也不追击)。</summary>
    public int WeaponStopDuraPercent { get; set; } = 8;

    //  0=够用 1=该修理了 2=停手。只报状态变化,循环 50ms 一轮,每轮都写就把日志刷爆了。
    private int _weaponDuraState;

    /// <summary>穿上去被服务端拒绝过的武器 MakeIndex,本轮不再试第二遍。</summary>
    private readonly HashSet<int> _weaponSwapFailed = new();

    /// <summary>空手时"从背包穿一把武器"的节流时点与上一次失败原因(原因没变就不重复刷日志)。</summary>
    private DateTime _nextBareHandTryUtc;
    private string? _bareHandLastWhy;

    /// <summary>空手穿武器的重试间隔秒数。</summary>
    public int BareHandEquipRetrySeconds { get; set; } = 20;

    /// <summary>"在哪把身上武器上试过换装并且全失败"。换人(修理/手动换)之前不再重复尝试,
    /// 否则 500ms 一轮会把 CM_TAKEONITEM 刷到服务端限速闸门上。</summary>
    private int _swapGaveUpOnMakeIndex;

    /// <summary>等 SM_TAKEON_OK/FAIL(615/616)的超时。服务端大多数失败都会回 616,但死亡/动作锁
    /// 之类是整包丢弃零回包,不能死等。</summary>
    public int TakeOnAckTimeoutMs { get; set; } = 3000;

    /// <summary>服务端 !Setup.txt 里 DeleteItemDuraZero=1 ⇒ 装备扣到 0 持久那一刀之后就直接
    /// SendDelItem + wIndex:=0(ObjBase.pas:28271-28312),不是留在身上等修理;DamageItemDuraRate=100
    /// 意味着每砍一刀扣的持久≈这一刀的伤害,挂机几千刀足够把武器磨没。
    /// 所以见底之前先从背包换一把还能用的武器(服务端是整槽替换,旧武器自动回背包),
    /// 换不到才停手把决定权交回给人(去 NPC 修理)。</summary>
    private async Task<bool> WeaponDullBlockingAsync(CancellationToken ct)
    {
        // 快照:收包线程随时在增删装备槽(AI 线程直接索引它不会抛,但会读到 Clear 后的空表)
        var items = _runtime.SnapshotUseItems();
        // 注意"没有 U_WEAPON 这一格"也算空手:全新号一件装备都没穿时 UseItems 是空表
        // (真机 1 级号 装备=0),按旧写法在这里直接 return 就永远轮不到下面的穿武器逻辑。
        var w = Grobal2.U_WEAPON < items.Length ? items[Grobal2.U_WEAPON] : default;
        // 空手或没武器:没有可损耗的持久,不该拦下战斗 —— 但空手打不动怪,得先把背包里的武器穿上
        if (string.IsNullOrEmpty(w.Name) || w.DuraMax <= 0)
        {
            _weaponDuraState = 0;
            // 空手也必须能自己把武器穿上:1 级号出生装备槽是空的、服务端给的 DC 只有 1,
            // 每刀 1 血追不上怪的回血,"等身上武器持久见底再换装"这条路永远走不到。
            // 节流是必须的:这个循环 50ms 一轮,不拦就把 CM_TAKEONITEM 刷进服务端限速闸门。
            if (DateTime.UtcNow >= _nextBareHandTryUtc)
            {
                _nextBareHandTryUtc = DateTime.UtcNow.AddSeconds(BareHandEquipRetrySeconds);
                string? bareWhy = await TrySwapWeaponAsync(default, bareHand: true, ct).ConfigureAwait(false);
                if (bareWhy != null && bareWhy != _bareHandLastWhy)
                    Log?.Invoke($"[战斗] 空手作战(穿上武器失败):{bareWhy}");
                _bareHandLastWhy = bareWhy;
            }
            return false;
        }

        // 换过武器了(人修好装回来、或 UI 里手动换):失败名单和"已放弃"标记都作废,重新给一次机会
        if (_swapGaveUpOnMakeIndex != 0 && _swapGaveUpOnMakeIndex != w.MakeIndex)
        {
            _swapGaveUpOnMakeIndex = 0;
            _weaponSwapFailed.Clear();
        }

        int percent = w.DuraCount * 100 / w.DuraMax;
        if (percent > WeaponStopDuraPercent)
        {
            // 武器又够用了(修理过/换过好的):上一轮"换不出"的结论作废,下次见底重新试一次。
            // 只按 MakeIndex 判会漏掉"原地修好同一把"这种最常见的情况 —— 那把武器编号没变。
            if (_swapGaveUpOnMakeIndex != 0)
            {
                _swapGaveUpOnMakeIndex = 0;
                _weaponSwapFailed.Clear();
            }
            int state = percent >= WeaponWarnDuraPercent ? 0 : 1;
            if (state != _weaponDuraState)
            {
                _weaponDuraState = state;
                Log?.Invoke(state == 1
                    ? $"[战斗] 武器 {w.Name} 持久 {w.DuraCount}/{w.DuraMax}(剩 {percent}%),该修理了:" +
                      $"服务端只修背包里的物品(ObjPlayer.pas:23101 在 m_ItemList 里找 MakeIndex),先卸下再找商人"
                    : $"[战斗] 武器 {w.Name} 持久 {w.DuraCount}/{w.DuraMax},恢复战斗");
            }
            return false;
        }

        string? why = _swapGaveUpOnMakeIndex != w.MakeIndex
            ? await TrySwapWeaponAsync(w, bareHand: false, ct).ConfigureAwait(false)
            : "这把武器上已经试过一轮换装,不再刷包(修好或换一把才重试)";
        if (why == null)
        {
            _weaponSwapFailed.Clear();
            _weaponDuraState = 0;    // 换上了,下一轮按新武器重新判定
            return false;
        }
        _swapGaveUpOnMakeIndex = w.MakeIndex;

        if (_weaponDuraState != 2)
        {
            _weaponDuraState = 2;
            Log?.Invoke($"[战斗] 武器 {w.Name} 持久只剩 {w.DuraCount}/{w.DuraMax},停止出刀:{why};" +
                        $"继续砍会被服务端删除(!Setup.txt DeleteItemDuraZero=1)");
        }
        return true;
    }

    /// <summary>从背包里挑一把还穿得上的武器换上去。服务端准入:CheckUserItems(M2Share.pas:11034)
    /// 要求 U_WEAPON 的 StdMode∈[5,6];整槽替换时旧件走 AddItemToBag+SendAddItem(:21056),
    /// 背包满又碰上绑定旧件则直接失败 n18=-1(:21022),所以满包先别白费一次发包。
    /// 名字必须和物品名一致(:20755 CompareText),等级/职业/负重这些服务端要求客户端看不到,只能靠回包判定。
    /// 返回 null=换上了,否则是"为什么换不上"的说明(停手日志要能自证原因)。
    /// bareHand=true 表示"空手往上穿"而不是替换:不需要给旧件腾背包格,所以背包满也照穿。</summary>
    private async Task<string?> TrySwapWeaponAsync(BagItemInfo worn, bool bareHand, CancellationToken ct)
    {
        if (_runtime.BagIsFull && !bareHand)
            return $"背包 {_runtime.BagItems.Count}/{_runtime.MaxBagCount} 已满,旧武器没格子回包(服务端 :21022 直接拒),先腾格子";
        var candidates = _runtime.SnapshotBag()
            .Where(i => i.MakeIndex > 0 && !_weaponSwapFailed.Contains(i.MakeIndex)
                        && (i.StdMode == 5 || i.StdMode == 6)
                        && i.DuraMax > 0 && i.DuraCount > 0
                        && i.DuraCount * 100 / i.DuraMax > WeaponStopDuraPercent)
            .OrderByDescending(i => i.DuraCount * 100 / i.DuraMax)
            .ThenByDescending(i => i.DuraCount)
            .Take(3)
            .ToList();
        foreach (var cand in candidates)
        {
            bool ok = await SendTakeOnAndWaitAsync(cand, Grobal2.U_WEAPON, ct).ConfigureAwait(false);
            if (ok)
            {
                Log?.Invoke($"[战斗] 已换上背包里的 {cand.Name} {cand.DuraCount}/{cand.DuraMax}" +
                            (bareHand ? "(空手穿上),开始出刀" : $"(顶替 {worn.Name}),继续出刀"));
                return null;
            }
            _weaponSwapFailed.Add(cand.MakeIndex);
            Log?.Invoke($"[战斗] 换上 {cand.Name} 没成功(原因见上一条 [equip] 日志),换下一把试");
        }
        return candidates.Count == 0
            ? "背包里没有换得动的备用武器(要 StdMode 5/6 且持久没见底;刚修好的那把先卸下修理)"
            : $"{candidates.Count} 把备用武器都被服务端拒了";
    }

    /// <summary>发 CM_TAKEONITEM 并等 615/616 回包认账:回包里的 MakeIndex 必须是这一件才算数,
    /// 否则(超时、被别的穿戴抢了待确认槽)一律按失败处理。</summary>
    private async Task<bool> SendTakeOnAndWaitAsync(BagItemInfo item, int slot, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnResult(int makeIndex, int resultSlot, bool success)
        {
            if (makeIndex == item.MakeIndex && resultSlot == slot) tcs.TrySetResult(success);
        }
        _runtime.TakeOnResult += OnResult;
        try
        {
            await _runtime.SendTakeOnItemAsync(item.MakeIndex, slot, item.Name, ct).ConfigureAwait(false);
            Task ack = await Task.WhenAny(tcs.Task, Task.Delay(TakeOnAckTimeoutMs, ct)).ConfigureAwait(false);
            if (ack != tcs.Task)
            {
                ct.ThrowIfCancellationRequested();
                Log?.Invoke($"[战斗] {item.Name} 穿戴 {TakeOnAckTimeoutMs}ms 没回包(服务端静默丢弃:死亡/动作锁?)");
                return false;
            }
            return tcs.Task.Result;
        }
        finally
        {
            _runtime.TakeOnResult -= OnResult;
        }
    }

    /// <summary>本轮准备处理 itemId 这件物品:和上一轮是同一件、而且人没挪过地方就算一次"无进展"。</summary>
    private void NotePickupAttempt(long itemId, DateTime now)
    {
        var p = _runtime.Player;
        if (itemId == _stuckItemId && p.PosX == _stuckX && p.PosY == _stuckY) _stuckTries++;
        else _stuckTries = 0;
        _stuckItemId = itemId;
        _stuckX = p.PosX;
        _stuckY = p.PosY;
        foreach (var kv in _pickupCooldown.Where(kv => kv.Value <= now).ToList())
            _pickupCooldown.Remove(kv.Key);
    }

    /// <summary>手动操作(点地图寻路/点怪攻击/点 NPC 对话)的引用计数,>0 时 AI 让开走位控制流。
    /// 必须是计数而不是布尔:一次点击的寻路和下一次点击可以重叠,布尔会被先结束的那次提前解开。</summary>
    private int _manualOverride;
    private readonly Random _rnd = new();

    /// <summary>喝药时按顺序在背包里找(先命中的先喝)。名字做"包含"匹配,所以一条
    /// "金创药" 就能覆盖 金创药(小量)/(中量)/(大量)。
    /// 早期这张表写的是"特大红/特大蓝"—— 本服 DB 里没有这种物品,角色血掉到底也喝不上一瓶药。</summary>
    public readonly List<string> Potions = new() { "太阳水", "金创药", "魔法药" };

    /// <summary>"背包里没药"只报一次用的去重标记。</summary>
    private bool _potionMissingLogged;

    // 物品拾取
    public ItemFilterConfig ItemFilter { get; set; } = new();
    public bool AutoPickupEnabled { get; set; } = true;
    private readonly HashSet<string> _pickupFilteredLogged = new();

    public BotCombatAI(BotSession session, BotRuntime runtime)
    {
        _session = session;
        _runtime = runtime;
    }

    public void Start()
    {
        if (_loopTask != null) return;
        Enabled = true;
        // 每次启动用新的 CTS,避免 Stop 后再 Start 时 token 已取消导致循环立即退出
        var cts = new CancellationTokenSource();
        _cts = cts;
        _loopTask = Task.Run(() => AiLoopAsync(cts.Token));
        Log?.Invoke("[AI] 战斗引擎启动");
        Log?.Invoke($"[AI] 发包间隔 {IntervalSettingsText()}");
        Log?.Invoke($"[AI] 拟人化 {HumanTiming.Describe(Human)}");

        // 技能循环：只在配置里明确 Enabled 时才建，否则保持改造前的单法术行为
        _rotation = SkillPlan is { Enabled: true }
            ? new SkillRotation(
                SkillPlan,
                ResolveMagicIdByName,
                id => _runtime.MagicDelayMs.TryGetValue(id, out int d) ? d : 0)
              { MagicHitIntervalMs = MagicHitIntervalMs }
            : null;
        Log?.Invoke(_rotation != null
            ? $"[AI] 技能循环 开: {_rotation.Describe(DateTime.UtcNow)}"
            : "[AI] 技能循环 关（单一法术/物理攻击）");
    }

    /// <summary>
    /// 按技能名从服务端下发的 TClientMagic 列表里反查魔法 ID。
    /// 列表条目格式为 "名称/ID"（见 BotRuntime 的 MyMagicList 填充处）。
    /// 配置名允许是服务端名的子串 —— 客户端名字常带 "(Lv3)" 之类后缀，写全反而难维护。
    /// 查不到返回 -1（该槽位本轮跳过，不报错、不影响其它技能）。
    /// </summary>
    private int ResolveMagicIdByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        foreach (string item in _runtime.SnapshotMagics())
        {
            int slash = item.LastIndexOf('/');
            if (slash <= 0 || slash == item.Length - 1) continue;
            string magicName = item[..slash].Trim();
            if (!magicName.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(item[(slash + 1)..].Trim(), out int id)) return id;
        }
        return -1;
    }

    /// <summary>
    /// 按当前场面挑技能。返回 null = 这一轮没有可用技能 → 调用方退回物理/单法术。
    /// </summary>
    private SkillChoice? PickSkill(int targetX, int targetY)
    {
        if (_rotation is null || !_rotation.Enabled) return null;

        int monsters = 0;
        foreach (var dot in _runtime.Dots.Values)
            if (dot.Kind == DotKind.Monster) monsters++;

        int px = _runtime.Player.PosX;
        int py = _runtime.Player.PosY;
        int dist = Math.Max(Math.Abs(targetX - px), Math.Abs(targetY - py));
        int mpPercent = _runtime.Player.MaxMp > 0 ? _runtime.Player.Mp * 100 / _runtime.Player.MaxMp : 100;

        var query = new SkillQuery(mpPercent, monsters, dist, targetX, targetY, px, py);
        return _rotation.Choose(query, DateTime.UtcNow);
    }

    public void Stop()
    {
        Enabled = false;
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        _loopTask = null;
        cts?.Dispose();
        Log?.Invoke("[AI] 战斗引擎停止");
    }

    /// <summary>
    /// 手动接管走位:返回的 IDisposable 存活期间,AI 不再发 CM_WALK/CM_HIT/CM_SPELL/CM_PICKUP,
    /// 只保留喝药和残血逃跑(这两条是保命,不能因为用户点了下地图就停)。
    ///
    /// 为什么必须有:AI 的 MoveTowardAsync 和 UI 的 WalkToAsync 各自独立做乐观坐标更新
    /// (SendWalkAsync 本地 +1 格,不等服务端确认),NewMoveToken 只能取消 UI 自己的移动任务、管不到 AI。
    /// 两条流同时写 Player.PosX/PosY ⇒ 本地坐标比服务端 m_nCurrX/Y 超前 ⇒ 人物原地抽搐,
    /// 而且 CM_HIT/CM_PICKUP 都带 nX==m_nCurrX && nY==m_nCurrY 校验(见 ResyncPositionAsync 注释),
    /// 坐标一偏就被服务端整包静默丢弃 —— 用户看到的就是"点了没反应"。
    /// </summary>
    public IDisposable BeginManualOverride()
    {
        Interlocked.Increment(ref _manualOverride);
        return new ManualOverride(this);
    }

    public bool IsManualOverrideActive => Volatile.Read(ref _manualOverride) > 0;

    private sealed class ManualOverride : IDisposable
    {
        private BotCombatAI? _owner;
        public ManualOverride(BotCombatAI owner) => _owner = owner;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner != null) Interlocked.Decrement(ref owner._manualOverride);
        }
    }

    private async Task AiLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Enabled)
        {
            try
            {
                if (!_session.IsConnected) { await Task.Delay(1000, ct); continue; }

                // 0. 死亡:整轮停手。服务端对 m_boDeath 的走路/跑/攻击/施法/喝药上行全部整包丢弃
                //    且零回包(ObjPlayer.pas:17277/17476/18002/18638/19028/21438),而引擎里
                //    根本没有"客户端请求复活"的上行包 ⇒ 继续跑就是永无止境地空转,
                //    还伴随本地坐标乐观更新飘走。复活只在服务端发生(脚本 NPC/GM/复活术),
                //    或者断开重连 —— HP<=0 时服务端会在家点把角色以 14 HP 拉起。
                if (_runtime.SelfIsDead)
                {
                    if (!_wasDead)
                    {
                        _wasDead = true;
                        Log?.Invoke("[AI] 角色已死亡,挂机停摆:走路/攻击/喝药都会被服务端零回包丢弃," +
                                    "请复活(脚本 NPC/GM)或断开重连(服务端会在家点以 14 HP 拉起)后自动恢复");
                    }
                    await Task.Delay(1000, ct);
                    continue;
                }
                if (_wasDead)
                {
                    _wasDead = false;
                    Log?.Invoke("[AI] 已复活,恢复挂机");
                }

                var now = DateTime.UtcNow;

                // 1. HP/MP 保护
                await CheckPotionAsync(ct);

                // 2. 自动拾取(手动接管期间让路:拾取要先走到物品格上,会和 UI 的寻路抢坐标)
                if (AutoPickupEnabled && ItemFilter.AutoPickup && !IsManualOverrideActive)
                {
                    await AutoPickupAsync(ct);
                }

                // 3. 血量过低逃跑
                if (ShouldEscape())
                {
                    await EscapeAsync(ct);
                    continue;
                }

                // 4. 找最近的怪物
                //    喝药和逃跑已经在上面处理过了(保命不让路);这里起凡是"会挪动人或改变朝向"的
                //    动作全部暂停,把走位控制流交给用户点击触发的那条 WalkToAsync。
                if (IsManualOverrideActive)
                {
                    await Task.Delay(100, ct);
                    continue;
                }
                // 武器快磨没了:先从背包换一把顶上,换不出来才停手(喝药/拾取/逃跑照旧,只是不打):
                // 继续砍下去装备会被服务端删掉
                if (await WeaponDullBlockingAsync(ct).ConfigureAwait(false))
                {
                    await Task.Delay(500, ct);
                    continue;
                }
                var target = FindNearestMonster();
                // 只在换目标时记一条:循环每 50ms 跑一次,每轮都写日志会把 BotClient.log 刷爆,
                // 真正有用的攻击/拾取/喝药记录反而找不到。
                long targetId = target?.Id ?? 0;
                if (targetId != _lastLoggedTargetId)
                {
                    _lastLoggedTargetId = targetId;
                    BotLog.Info(target is { } t
                        ? $"[AI] 目标切换: {t.Name} id={t.Id} @({t.X},{t.Y}) 玩家=({_runtime.Player.PosX},{_runtime.Player.PosY})"
                        : $"[AI] 无目标 玩家=({_runtime.Player.PosX},{_runtime.Player.PosY}) 视野怪物数={_runtime.Dots.Count(d => d.Value.Kind == DotKind.Monster)}");
                }
                if (target == null)
                {
                    // 拟人化：真人在空场地里会偶尔愣一下、而不是把"巡逻/换位"跑成 500ms 整的机械循环。
                    // 只影响等待时长，不影响任何发包决策（照样巡逻、照样换位，只是节奏带毛刺）。
                    if (Human is { Enabled: true } && HumanTiming.Chance(Human.IdleWanderChance))
                    {
                        int idleMs = HumanTiming.Next(Human.IdlePauseMinMs,
                            Math.Max(Human.IdlePauseMinMs + 1, Human.IdlePauseMaxMs + 1));
                        await Task.Delay(idleMs, ct);
                    }
                    if (FightAtPoint)
                    {
                        await PatrolAsync(ct);
                        Log?.Invoke($"[战斗] 无怪物,回定点 ({FightPointX},{FightPointY}) 巡逻");
                    }
                    else
                    {
                        await RelocateIfBoxedInAsync(now, ct);
                    }
                    await Task.Delay(500, ct);
                    continue;
                }
                // 有目标但连着几只都追不上 ⇒ 目标一直在换、人却没动过一步:同样按"站在墙根下"处理,
                // 否则怪多的地图永远凑不出 target==null,挂机就变成原地站着(真机 90 秒零输出的形状)。
                if (_chaseGiveUps >= RelocateGiveUpTries && !FightAtPoint)
                {
                    await RelocateIfBoxedInAsync(now, ct);
                    continue;
                }

                // 5. 判断距离,走近或攻击
                int px = _runtime.Player.PosX;
                int py = _runtime.Player.PosY;
                int dx = Math.Abs(target.Value.X - px);
                int dy = Math.Abs(target.Value.Y - py);
                int dist = Math.Max(dx, dy);
                if (dist <= 1)
                {
                    // 目标血量已归零(受击记录显示 ≤0)→ 换目标,不再攻击尸体
                    if (_runtime.ObjectHpMap.TryGetValue(target.Value.Id, out var tHp) && tHp.Hp <= 0)
                    {
                        Log?.Invoke($"[战斗] {target.Value.Name} 血量 {tHp.Hp}/{tHp.MaxHp} 已归零,换目标");
                        await Task.Delay(150, ct);
                        continue;
                    }
                    if (dist == 0)
                    {
                        // 怪不可能和我占同一格 —— 出现 dist==0 只有一种解释:我方乐观坐标超前了
                        // (走步被服务端拒掉,或被网关【移动并发】假成功丢掉)。这时 GetDirection 会退化成
                        // 固定方向,CM_HIT 砍向空 cell,服务端按方向找不到目标 ⇒ 白挥一刀还不报错。
                        // 所以这一格不砍,先发一条原地 CM_TURN 把真实坐标问回来。
                        Log?.Invoke($"[战斗] 与 {target.Value.Name} 重叠同格 ({px},{py}),先按服务端真值回正坐标");
                        await _runtime.ResyncPositionAsync(ct);
                    }
                    // 近身 → 攻击(法术单独一档限速,见 CurrentAttackIntervalMs)
                    else if ((now - _lastAttack).TotalMilliseconds >= CurrentAttackIntervalMs())
                    {
                        byte dir = GetDirection(px, py, target.Value.X, target.Value.Y);
                        await AttackAsync(target.Value.Id, target.Value.X, target.Value.Y, dir, ct);
                        Log?.Invoke($"[战斗] 攻击 {target.Value.Name} id={target.Value.Id} @({target.Value.X},{target.Value.Y})");
                        _lastAttack = now;
                        // 已经贴到脸上出刀了 ⇒ "追不上"的账清掉,别刚砍两刀就以为自己在墙根下乱逛
                        _chaseGiveUps = 0;
                    }
                }
                else
                {
                    // 未近身 → 走近(任意距离都追击,否则远处的怪物会一直干等,点了开始战斗却没反应)
                    if ((now - _lastWalk).TotalMilliseconds >= WalkIntervalMs)
                    {
                        int remain = await MoveTowardAsync(target.Value.X, target.Value.Y, ct);
                        Log?.Invoke($"[战斗] 追击 {target.Value.Name} id={target.Value.Id} 距离={dist} 还差 {remain} 步"
                                    + (remain < 0 ? " (无路可走:目标那格连四周全被占/是墙)" : ""));
                        _lastWalk = now;
                        // 本地坐标被服务端回正过 ⇒ 这一轮量出来的距离全是幻影,直接重来一轮(别记成追不上)
                        if (await NoteChaseProgressAsync(target.Value.Id, dist, remain, ct).ConfigureAwait(false))
                        {
                            Log?.Invoke($"[战斗] 追击没靠近是因为本地坐标超前了,已按服务端真值重算距离");
                            continue;
                        }
                    }
                }

                await Task.Delay(50, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"[AI] 循环异常: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>计算从 (fx,fy) 到 (tx,ty) 的方向(0-7)</summary>
    public static byte GetDirection(int fx, int fy, int tx, int ty)
    {
        int dx = tx - fx;
        int dy = ty - fy;
        if (dx == 0 && dy < 0) return Grobal2.DR_UP;
        if (dx > 0 && dy < 0) return Grobal2.DR_UPRIGHT;
        if (dx > 0 && dy == 0) return Grobal2.DR_RIGHT;
        if (dx > 0 && dy > 0) return Grobal2.DR_DOWNRIGHT;
        if (dx == 0 && dy > 0) return Grobal2.DR_DOWN;
        if (dx < 0 && dy > 0) return Grobal2.DR_DOWNLEFT;
        if (dx < 0 && dy == 0) return Grobal2.DR_LEFT;
        return Grobal2.DR_UPLEFT;
    }

    /// <summary>找最近的怪物</summary>
    private MapDot? FindNearestMonster()
    {
        MapDot? nearest = null;
        int bestDist = int.MaxValue;
        var px = _runtime.Player.PosX;
        var py = _runtime.Player.PosY;
        var nowT = DateTime.UtcNow;

        foreach (var dot in _runtime.Dots.Values)
        {
            if (dot.Kind != DotKind.Monster) continue;
            // 血量已归零的怪物跳过(服务端可能不发 SM_DISAPPEAR,死怪仍残留 Dots)
            if (_runtime.ObjectHpMap.TryGetValue(dot.Id, out var h) && h.Hp <= 0) continue;
            // 追不上的那只先搁置一段时间,否则会永远占着"最近"这个位置,视野里其他怪一只都打不到
            if (_chaseBlocked.TryGetValue(dot.Id, out DateTime freeAt))
            {
                if (freeAt > nowT) continue;
                _chaseBlocked.Remove(dot.Id);
            }
            if (FightAtPoint)
            {
                int fromPoint = Math.Max(Math.Abs(dot.X - FightPointX), Math.Abs(dot.Y - FightPointY));
                if (fromPoint > FightRange) continue;
            }
            int d = Math.Max(Math.Abs(dot.X - px), Math.Abs(dot.Y - py));
            if (d < bestDist)
            {
                bestDist = d;
                nearest = dot;
            }
        }
        return nearest;
    }

    /// <summary>离我最近的活怪在几格外(尸体不算,"追不上"的冷却也不算 —— 贴脸的怪不管追不追得上都是威胁)。
    /// 和 FindNearestMonster 的区别就是"要看全部",拾取让不让路取决于身边有没有怪,不取决于能不能追到它。</summary>
    private int NearestMonsterDist()
    {
        int px = _runtime.Player.PosX, py = _runtime.Player.PosY;
        int best = int.MaxValue;
        foreach (var dot in _runtime.Dots.Values)
        {
            if (dot.Kind != DotKind.Monster) continue;
            if (_runtime.ObjectHpMap.TryGetValue(dot.Id, out var h) && h.Hp <= 0) continue;
            int d = Math.Max(Math.Abs(dot.X - px), Math.Abs(dot.Y - py));
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>几格以内的怪算"这架正在打",拾取要让路;更远的那片怪隔着距离,顺手捡个药不冲突。</summary>
    public int PickupThreatRadius { get; set; } = 4;

    /// <summary>配置表里的这个名字算蓝药(MP)吗?本服物品名:魔法药(小量) / 金创药(小量)。</summary>
    private static bool IsMpPotion(string name) => name.Contains("魔法药") || name.Contains("蓝");

    /// <summary>从背包里按 Potions 的配置顺序找出第一件真实存在的红药/蓝药。
    /// 必须"先看背包里有什么"再决定喝哪档:旧写法是先拿配置表里的名字去背包找,
    /// 名字对不上就永远报"未找到药品"—— 真机上角色 HP 掉到阈值以下就是这么一瓶药都没喝的。</summary>
    private (BagItemInfo Hp, BagItemInfo Mp) PickPotions(IReadOnlyList<BagItemInfo> bag)
    {
        var hp = default(BagItemInfo);
        var mp = default(BagItemInfo);
        foreach (var name in Potions)
        {
            bool wantMp = IsMpPotion(name);
            if ((wantMp ? mp : hp).MakeIndex > 0) continue;      // 这一类已经找到了,继续找另一类
            var found = bag.FirstOrDefault(i => i.MakeIndex > 0 && i.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (wantMp) mp = found; else hp = found;
        }
        return (hp, mp);
    }

    /// <summary>检查 HP/MP 是否需要使用药品</summary>
    private async Task CheckPotionAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastPotion).TotalMilliseconds < PotionIntervalMs) return;

        var p = _runtime.Player;
        if (p.MaxHp <= 0) return;

        int hpPct = p.Hp * 100 / p.MaxHp;
        int mpPct = p.MaxMp > 0 ? p.Mp * 100 / p.MaxMp : 100;

        if (hpPct > HpPotionPercent && mpPct > MpPotionPercent)
            return;

        var (hpPot, mpPot) = PickPotions(_runtime.SnapshotBag());
        // 一轮只喝一瓶:服务端 ClientUseItems 有 UseItemIntervalTime(普通药 300ms / 特殊药 1000ms)限速,
        // 同一轮连发两瓶会被后发的那瓶整包丢掉,还白扣一次限速窗口。红药优先 —— 血没了什么都干不了。
        var (drug, reason) = hpPct <= HpPotionPercent && hpPot.MakeIndex > 0 ? (hpPot, $"HP {hpPct}%≤{HpPotionPercent}%")
                      : mpPct <= MpPotionPercent && mpPot.MakeIndex > 0 ? (mpPot, $"MP {mpPct}%≤{MpPotionPercent}%")
                      : (default(BagItemInfo), string.Empty);
        if (drug.MakeIndex <= 0)
        {
            // 没药这件事只报一次:AI 循环里这条最容易被刷成噪音,真机上"一直没药"往往就是挂机几十秒的常态
            string missing = hpPct <= HpPotionPercent
                ? (mpPct <= MpPotionPercent ? "红药和蓝药" : "红药")
                : "蓝药";
            if (!_potionMissingLogged)
            {
                _potionMissingLogged = true;
                Log?.Invoke($"[AI] 未找到药品 {missing}(HP={hpPct}% MP={mpPct}%),配置名:{string.Join("/", Potions)}");
            }
            return;
        }
        _potionMissingLogged = false;
        Log?.Invoke($"[AI] 使用药品: {drug.Name}({reason})");
        await _runtime.SendEatAsync(drug.MakeIndex, drug.Name, ct);
        _lastPotion = now;
    }

    private bool ShouldEscape()
    {
        var p = _runtime.Player;
        if (p.MaxHp <= 0) return false;
        return p.Hp * 100 / p.MaxHp <= EscapeHpPercent;
    }

    private async Task EscapeAsync(CancellationToken ct)
    {
        Log?.Invoke($"[AI] 血量低于{EscapeHpPercent}%, 逃跑!");
        Escaped?.Invoke();
        int[] dxs = { -1, 0, 1, 0 };
        int[] dys = { 0, 1, 0, -1 };
        for (int i = 0; i < 3; i++)
        {
            int dir = _rnd.Next(4);
            int nx = _runtime.Player.PosX + dxs[dir];
            int ny = _runtime.Player.PosY + dys[dir];
            await MoveToAsync(nx, ny, (byte)dir, ct);
            await Task.Delay(300, ct);
        }
        if (!ShouldEscape()) return;

        // 跑不掉就必须离开战斗,而且一定要 Stop:这个循环 50ms 一轮,只要血还低就会
        // 一遍遍重发逃跑包,小退(登出角色)更会被刷成连续几十次。
        var scroll = _runtime.SnapshotBag().FirstOrDefault(i => i.Name.Contains("回城"));
        if (scroll.MakeIndex > 0)
        {
            // 回城卷是 StdMode=3 物品,服务端对它不吃用药间隔(ObjPlayer.pas:21448),
            // 传回来必定改地图或改坐标(SM_CHANGEMAP / SM_POSTIONMOVE),否则视为没传送成功。
            Log?.Invoke($"[AI] 使用 {scroll.Name} 回城");
            // 比编号不比中文名:MapInfo 里"密室"这种名字被 0123/0128/0129 共用,按名字会漏判换图。
            string map0 = _runtime.CurrentMap;
            int x0 = _runtime.Player.PosX, y0 = _runtime.Player.PosY;
            await _runtime.SendEatAsync(scroll.MakeIndex, scroll.Name, ct);
            if (await WaitTeleportedAsync(map0, x0, y0, ct))
            {
                Log?.Invoke("[AI] 已回城,战斗引擎停止");
                Stop();
                return;
            }
            Log?.Invoke("[AI] 回城卷未生效(地图禁用/禁足),改为小退");
        }
        Log?.Invoke("[AI] 小退下线,战斗引擎停止");
        await _runtime.SendSoftCloseAsync(ct);
        EscapeSoftClosed?.Invoke();
        Stop();
    }

    /// <summary>等传送落地:地图变了或坐标跳开 >3 格即认为回城成功,最多等 1.5 秒。</summary>
    private async Task<bool> WaitTeleportedAsync(string map0, int x0, int y0, CancellationToken ct)
    {
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(50, ct);
            var p = _runtime.Player;
            if (_runtime.CurrentMap != map0) return true;
            if (Math.Max(Math.Abs(p.PosX - x0), Math.Abs(p.PosY - y0)) > 3) return true;
        }
        return false;
    }

    /// <summary>向目标方向移动一格,自动避开不可走格子(单步寻路)。
    /// 返回"走完这一步之后离目标还剩几格",-1 = 这一步根本迈不出去(目标格连同它的 8 个邻格
    /// 全不可走/被活对象占住,或 BFS 找不到路)。调用方要拿 -1 当"够不着"的硬证据:真机实测
    /// (2026-09-23)追击反复卡在 4 格,日志里一条寻路失败都没有 —— 因为这里过去是静默 return 的,
    /// 于是"走不通"被当成"再试试",白攒 8 轮才换目标,看起来就像原地罚站。
    /// 返回步数而不是布尔值,是因为"直线距离有没有变小"根本不能当进展判据:新手村(0.map 274,631
    /// 一带)是树和房子拼出来的迷宫,直线 3 格的怪实际要走 65 步、绕出去 24 格,期间直线距离
    /// 一点都不降 —— 拿它判进展就会把"正在绕路"误判成"追不上"。</summary>
    private async Task<int> MoveTowardAsync(int tx, int ty, CancellationToken ct)
    {
        int px = _runtime.Player.PosX;
        int py = _runtime.Player.PosY;
        int mw = _runtime.PathfindWidth;
        int mh = _runtime.PathfindHeight;
        // 通行性 = 静态地图 + 视野里站着的对象(服务端不让踩到别的 Actor 身上,撞了不回包)
        Func<int, int, bool> isWalk = _runtime.EffectiveWalkable(_runtime.IsWalkable);
        if (!isWalk(tx, ty))
        {
            // 追怪时目标格就是怪自己占着 → 改成离我方最近的空着相邻格,否则 BFS 永远找不到路径
            var free = NearestFreeNeighborOf(tx, ty, px, py, isWalk);
            if (free == null) return -1;
            (tx, ty) = free.Value;
        }

        // 先尝试直走方向(Math.Clamp 风格)
        int cdx = Math.Clamp(tx - px, -1, 1);
        int cdy = Math.Clamp(ty - py, -1, 1);
        if (cdx != 0 || cdy != 0)
        {
            int nx = px + cdx, ny = py + cdy;
            if (isWalk(nx, ny))
            {
                byte dir = GetDirection(px, py, nx, ny);
                await MoveToAsync(nx, ny, dir, ct);
                return Math.Max(Math.Abs(tx - nx), Math.Abs(ty - ny));
            }
        }

        // 直走被挡 → BFS 找最近的可走格子(向目标方向探索)。
        // 预算必须给够:同上那条 65 步的绕路要 5,195 个节点,原先写死的 512 在这张图上必然失败,
        // 于是"能到的怪"也被判成"走不通"。FindPath 现在按起终点开窗取内存,多花预算不再多花内存。
        var path = BotPathFinder.FindPath(px, py, tx, ty, mw, mh, isWalk, maxVisitedNodes: 12_000);
        if (path != null && path.Count > 1)
        {
            var (nx, ny) = path[1];
            byte dir = GetDirection(px, py, nx, ny);
            await MoveToAsync(nx, ny, dir, ct);
            return path.Count - 2;
        }
        return -1;
    }

    /// <summary>目标格周围 8 格里离玩家最近的一个可走格;全堵返回 null。</summary>
    private static (int X, int Y)? NearestFreeNeighborOf(int tx, int ty, int px, int py, Func<int, int, bool> isWalk)
    {
        (int X, int Y)? best = null;
        int bestD = int.MaxValue;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = tx + dx, ny = ty + dy;
                if (!isWalk(nx, ny)) continue;
                int d = Math.Max(Math.Abs(nx - px), Math.Abs(ny - py));
                if (d < bestD) { bestD = d; best = (nx, ny); }
            }
        }
        return best;
    }

    /// <summary>CM_WALK 的唯一出口:追击/拾取/换位置/巡逻/逃跑全走这一条,所以限速只能设在这里。
    /// 真机实测(2026-09-23 新手村,100 秒):追击按自己的 650ms 闸、拾取另有一套 500ms 闸,
    /// 两条单看都合规、叠起来 ~3.5 包/秒,超过服务端 490ms 的人头闸门 ⇒ 多出来的那一步整包丢弃,
    /// 而 SendWalkAsync 已经把本地坐标乐观往前挪过一格(服务端不给本人回包)。本地越跑越靠前,
    /// 于是"距离永远创不了新低"被判定成追不上、攻击按本地坐标算出来永远还差 3~4 格 —— 击杀 0。</summary>
    private DateTime _lastWalkSendUtc = DateTime.MinValue;

    /// <summary>使用正确的 CM_WALK(3011) 行走, Recog=(y&lt;&lt;16)|x, Tag=dir。
    /// 走完乐观更新本地坐标(服务端不给本人回包),否则 AI 的 Player.Pos 永远不变,怪物追不上。</summary>
    private async Task MoveToAsync(int x, int y, byte dir, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;

        // 拟人化：走路的节拍不能是等距脉冲。同样只加不减 —— 下限仍是服务端/驱动的走路限速，
        // 抖动只往上叠（真人是"一阵快一阵慢"，不是"Sensor 每 400ms 准时点一格"）。
        int interval = WalkIntervalMs;
        if (Human is { Enabled: true } && Human.WalkJitterRatio > 0)
        {
            int span = Math.Max(1, (int)Math.Round(WalkIntervalMs * Human.WalkJitterRatio));
            interval += Math.Min(HumanTiming.LongTail(0, span), span * 3);
        }

        if ((nowUtc - _lastWalkSendUtc).TotalMilliseconds < interval) return;
        _lastWalkSendUtc = nowUtc;
        await _runtime.SendWalkAsync(x, y, dir, ct);
    }

    /// <summary>根据法术条配置选择攻击方式:魔法攻击(CM_SPELL)或物理攻击(CM_HIT)。
    /// CM_HIT 打包玩家自身坐标(服务端 ClientHitXY 要求),方向指向目标。
    ///
    /// 技能循环开启时先走 <see cref="PickSkill"/>：它按场面（怪物数量/距离/自身MP/冷却）挑技能，
    /// 挑中了就用那个技能，挑不中才退回配置里的单法术或物理攻击 —— 也就是说
    /// **技能循环只是"多给一层选择"，不会让原本能打的号变哑**。</summary>
    private async Task AttackAsync(long targetId, int targetX, int targetY, byte dir, CancellationToken ct)
    {
        var skill = PickSkill(targetX, targetY);
        if (skill is { } chosen)
        {
            // 地面魔法（火墙/地雷）没有目标对象，Recog 传 0、落点用配置的偏移格
            int spellTargetId = chosen.GroundCast ? 0 : (int)targetId;
            await _runtime.SendSpellAsync(spellTargetId, chosen.X, chosen.Y,
                (ushort)chosen.MagicId, chosen.SpellSlot, ct);
            _rotation!.MarkCast(chosen.MagicId, DateTime.UtcNow);
            Log?.Invoke($"[战斗] 技能 {chosen.Name} id={chosen.MagicId} " +
                        $"槽{(chosen.SpellSlot >= 0 ? chosen.SpellSlot.ToString() : "auto")} → ({chosen.X},{chosen.Y})" +
                        (chosen.GroundCast ? " [地面]" : ""));
            return;
        }

        if (MagicAttackEnabled && MagicId > 0)
        {
            await _runtime.SendSpellAsync(targetId, targetX, targetY, MagicId, ct);
        }
        else
        {
            await _runtime.SendHitAsync(targetId, targetX, targetY, dir, ct);
        }
    }

    /// <summary>定点战斗:回到定点附近</summary>
    private async Task PatrolAsync(CancellationToken ct)
    {
        int dx = Math.Abs(_runtime.Player.PosX - FightPointX);
        int dy = Math.Abs(_runtime.Player.PosY - FightPointY);
        if (dx > FightRange || dy > FightRange)
        {
            await MoveTowardAsync(FightPointX, FightPointY, ct);
        }
    }

    /// <summary>自动拾取附近物品, 使用 CM_PICKUP(1001)</summary>
    private async Task AutoPickupAsync(CancellationToken ct)
    {
        ReportPickupRoom();
        var now = DateTime.UtcNow;
        if ((now - _lastPickup).TotalMilliseconds < PickupIntervalMs) return;

        // 从 DropItems 找最近的地面物品
        int bestDist = ItemFilter.PickupRange + 1;
        DropItemInfo? bestItem = null;

        foreach (var item in _runtime.DropItems)
        {
            // 检查名称是否允许拾取
            if (!ItemFilter.ShouldPickup(item.Name))
            {
                // 拾取过滤器没有 UI 可改(WPF 只有总开关),而它默认是"白名单外一律不捡"。
                // 静默跳过的话,"怪杀了一地东西却一样没捡"根本查不出是被谁挡的 —— 每个名字只报一次。
                if (_pickupFilteredLogged.Add(item.Name))
                    Log?.Invoke($"[pickup] 按过滤规则不捡 {item.Name}(白名单 {ItemFilter.PickupKeywords.Count} 条/黑名单 {ItemFilter.IgnoreKeywords.Count} 条,同类只报一次)");
                continue;
            }
            if (_pickupCooldown.TryGetValue(item.ItemId, out DateTime freeAt) && freeAt > now) continue;
            //  背包满/超重时服务端把物品整包丢掉且零回包(ObjPlayer.pas:20583/:20592)——
            //  还往物品那走就是永无止境地绕圈,而金币走 IncGold 那条分支,不受这两道闸门影响。
            if (!item.IsGold && !_runtime.CanPickupItems) continue;

            int px = _runtime.Player.PosX;
            int py = _runtime.Player.PosY;
            int d = Math.Max(Math.Abs(item.X - px), Math.Abs(item.Y - py));
            if (d < bestDist)
            {
                bestDist = d;
                bestItem = item;
            }
        }

        if (bestItem == null) return;

        var bi = bestItem.Value;
        int dist = Math.Max(Math.Abs(bi.X - _runtime.Player.PosX), Math.Abs(bi.Y - _runtime.Player.PosY));
        // 拾取是"带着角色走开"的动作,和打怪抢同一帧控制流:真机 A/B(同一条号、60 秒)
        // 关拾取 击杀=3,开拾取 击杀=0、怪物受击包=0 —— 一路去捡 5~10 格外的药,砍怪永远排不上。
        // 药本来就是杀怪掉出来的,所以只有"物品不比贴脸的怪更近"才值得为它挪窝;
        // 已经踩在身上的(dist=0)不算挪窝,照捡。
        // 威胁半径必须存在而不能"只要有怪就不捡":真机实测(2026-09-23 新手村)视野常驻 13 只怪、
        // 全在 5~8 格外,按"视野里有怪"判定的话地面 6 件东西 90 秒里一条 CM_PICKUP 都发不出去。
        if (dist > 0)
        {
            int monDist = NearestMonsterDist();
            if (monDist <= PickupThreatRadius && monDist <= dist) return;
        }

        NotePickupAttempt(bi.ItemId, now);
        if (_stuckTries >= PickupStuckTries)
        {
            _pickupCooldown[bi.ItemId] = now.AddMilliseconds(PickupCooldownMs);
            _stuckTries = 0;
            Log?.Invoke($"[pickup] {bi.Name} @({bi.X},{bi.Y}) 连 {PickupStuckTries} 次毫无进展," +
                        $"冷却 {PickupCooldownMs / 1000}s 改捡别的");
            _lastPickup = now;
            return;
        }

        if (dist > 0)
        {
            // 走近物品
            await MoveTowardAsync(bi.X, bi.Y, ct);
            _lastPickup = now;
            return;
        }

        // 服务端 ClientPickUpItem 开头就要求 Param/Tag == 本人当前格,再取该格上的物品
        // (ObjPlayer.pas:20471),所以只能踩上去捡,坐标必须发自己的位置。
        // 这条闸门还是所有静默丢弃里最狠的一条:坐标对不上时它连 110 都不回,拾取会永远发不出去又不报错
        // (攻击/走路被拒至少还有 SM_ACTION_RET 回正)。所以发之前先拿一条原地 CM_TURN 问服务端要真实坐标;
        // 真被回正了就本轮作废,下一轮按真值重新算距离。
        if (await _runtime.ResyncPositionAsync(ct))
        {
            _lastPickup = now;
            return;
        }
        await _runtime.SendPickupAsync(bi.X, bi.Y, ct);
        _lastPickup = now;
        PickupSent?.Invoke(bi.Name);
        Log?.Invoke($"[pickup] 拾取物品 {bi.Name} at ({bi.X},{bi.Y})");
    }

    public void Dispose()
    {
        Stop();
    }
}
