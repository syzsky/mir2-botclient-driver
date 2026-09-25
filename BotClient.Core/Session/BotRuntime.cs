using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BotClient.Assets;
using BotClient.Net;
using BotClient.Protocol;

namespace BotClient.Session;

/// <summary>
/// 运行时角色状态(由 SM_ABILITY / SM_SENDUSERSTATE / SM_LOGON 等包填充)。
/// </summary>
public sealed class BotPlayerState
{
    public string Name = string.Empty;
    public int Level = 1;
    public int Job;             // 0=战士 1=法师 2=道士
    public byte Sex;
    public int Hp;
    public int MaxHp;
    public int Mp;
    public int MaxMp;
    public int Exp;
    public int MaxExp;
    public int Gold;
    public int GameGold;
    public int PosX;
    public int PosY;
    public ushort Direction;
    public string MapName = string.Empty;
    public int Weight;
    public int MaxWeight;
    public int WearWeight;
    public int MaxWearWeight;
    public int HandWeight;
    public int MaxHandWeight;
    public int AC, MAC;         // 防御/魔防
    public int DC, MC, SC;      // 攻击/魔法/道术
    public int HitPoint;        // 准确
    public int SpeedPoint;      // 敏捷
    public DateTime LastUpdate = DateTime.MinValue;
}

/// <summary>小地图上的一个点(角色/怪物/NPC/物品)。</summary>
public readonly record struct MapDot(long Id, int X, int Y, DotKind Kind, string Name, int Feature = 0);

public enum DotKind
{
    Self,
    Monster,
    Npc,
    Player,
    Item,        // 地面物品
    Gold         // 地面金币
}

/// <summary>地面掉落物品标记。</summary>
public readonly record struct DropItemInfo(long ItemId, string Name, int X, int Y, int Feature, bool IsGold, int GoldAmount);

/// <summary>主界面收包分发器:把 SM_* 包拆给状态/小地图。</summary>
public sealed class BotRuntime
{
    public readonly BotPlayerState Player = new();
    public readonly ConcurrentDictionary<long, MapDot> Dots = new();
    /// <summary>对象名字缓存(对象ID → 名字):服务端从不发 WITHINRANGE(56),
    /// 换图后 Dots.Clear() 会丢失名字,但对象 ID 通常不变,缓存可让换图后新对象复用已知名字,
    /// 避免列表显示 #+数字。仅当动作包再次带名字时更新缓存。</summary>
    public readonly ConcurrentDictionary<long, string> DotNameCache = new();
    public readonly List<BagItemInfo> BagItems = new();
    /// <summary>装备槽数组,下标=服务端 U_* 常量(Grobal2.pas:99,0..29 含斗笠/盾牌/灵玉/时装)。</summary>
    public readonly List<BagItemInfo> UseItems = new(Grobal2.MAX_USE_ITEM_COUNT);
    public readonly List<BagItemInfo> StorageItems = new(); // 仓库物品列表
    /// <summary>背包/装备/仓库/技能/组队/行会这几张 List 的并发锁。写方只有收包线程
    /// (HandlePacket 整段在这把锁里跑,见 ReceiveLoopAsync),但读方是 UI 线程(背包面板、商店、
    /// 仓库、交易)和挂机 AI 线程(找药、挑备用武器)—— 直接在 List 上 foreach/FindIndex/LINQ
    /// 撞上收包线程的一次 Add/Remove 就抛"集合已修改",而这条崩溃只在长时间挂机时偶发。
    /// 跨线程读一律用 Snapshot*(),确需注入(仅测试)必须先拿住这把锁。</summary>
    public object ItemListSync => _itemListLock;
    private readonly object _itemListLock = new();
    public BagItemInfo[] SnapshotBag() { lock (_itemListLock) return BagItems.ToArray(); }
    public BagItemInfo[] SnapshotUseItems() { lock (_itemListLock) return UseItems.ToArray(); }
    public BagItemInfo[] SnapshotStorage() { lock (_itemListLock) return StorageItems.ToArray(); }
    public BagItemInfo[] SnapshotDealMyItems() { lock (_itemListLock) return DealMyItems.ToArray(); }
    public BagItemInfo[] SnapshotDealRemoteItems() { lock (_itemListLock) return DealRemoteItems.ToArray(); }
    public string[] SnapshotMagics() { lock (_itemListLock) return MyMagicList.ToArray(); }
    public string[] SnapshotGroupMembers() { lock (_itemListLock) return GroupMembers.ToArray(); }
    public string[] SnapshotGuildMembers() { lock (_itemListLock) return GuildMembers.ToArray(); }

    /// <summary>当前仓库 NPC 的对象 ID(收到 SM_SAVEITEMLIST 时记录,存取物品需带)。</summary>
    public long CurrentStorageMerchantId;
    private readonly List<DropItemInfo> _dropItems = new();
    private readonly object _dropItemsLock = new();
    /// <summary>地面物品快照。写方是收包线程,读方是 AI 轮询线程和 UI,
    /// 直接暴露 List 会让 AI 的 foreach 撞上 Add/RemoveAll 抛"集合已修改",拾取循环当场停。</summary>
    public IReadOnlyList<DropItemInfo> DropItems
    {
        get { lock (_dropItemsLock) return _dropItems.ToArray(); }
    }

    private void UpsertDropItem(DropItemInfo item)
    {
        lock (_dropItemsLock)
        {
            _dropItems.RemoveAll(d => d.ItemId == item.ItemId);
            _dropItems.Add(item);
        }
    }

    private int RemoveDropItem(long itemId)
    {
        lock (_dropItemsLock) return _dropItems.RemoveAll(d => d.ItemId == itemId);
    }

    private void ClearDropItems()
    {
        lock (_dropItemsLock) _dropItems.Clear();
    }

    /// <summary>该格上的地面物品是不是金币(服务端一格只放一件物品)。</summary>
    private bool IsGoldOnTile(int x, int y)
    {
        var items = DropItems;
        for (int i = 0; i < items.Count; i++)
            if (items[i].X == x && items[i].Y == y) return items[i].IsGold;
        return false;
    }

    // ============================================================
    //  背包容量闸门(拾取的第二类静默丢弃)
    //  服务端 ClientPickUpItem 在坐标/归属闸门之后还有两条,而且**都不回包**:
    //    ObjPlayer.pas:20583  if IsEnoughBag then ...   —— 为假时整段跳过,连 boResult 都不置
    //    ObjPlayer.pas:20592  if ... not IsAddWeightAvailable(AddWeight) then Exit
    //  IsEnoughBag = m_ItemList.Count < GetMaxBagCount(ObjBase.pas:13694),
    //  GetMaxBagCount = 46 + 扩展格(ObjPlayer.pas:13677),扩展格只在 NPC 脚本开通时经
    //  SM_EXT_BAG_COUNT_CHANGE 下发,登录时不发 ⇒ 默认按 46。
    //  IsAddWeightAvailable = Weight + 物品重量 <= MaxWeight(ObjBase.pas:41968),
    //  Weight/MaxWeight 由 SM_ABILITY + SM_WEIGHTCHANGED 维护。
    //  金币走 :20525 的 IncGold 分支,既不看格子也不看负重 ⇒ 满仓时金币照捡。
    // ============================================================

    /// <summary>背包格数上限(46 + 服务端下发的扩展格)。</summary>
    public int MaxBagCount { get; private set; } = Grobal2.DEF_MAX_BAG_ITEM;

    /// <summary>没有空余格子装新物品了(叠加物并入已有那格不算)。</summary>
    public bool BagIsFull => BagItems.Count >= MaxBagCount;

    /// <summary>负重已到上限。判据取 >= :服务端要 Weight+物品重量<=MaxWeight,而物品重量发包前不可知,
    /// 所以只剩 0 重量的物品可能捡得动 —— 与其对每件物品空跑一趟,不如停下。</summary>
    public bool WeightIsMaxed => Player.MaxWeight > 0 && Player.Weight >= Player.MaxWeight;

    /// <summary>还能不能捡非金币物品。</summary>
    public bool CanPickupItems => !BagIsFull && !WeightIsMaxed;

    public event Action? StateChanged;
    public event Action? MapChanged;
    public event Action? ItemsChanged;
    public event Action? StorageItemsChanged;   // 仓库物品列表变化
    /// <summary>服务端发来 SM_PASSWORD 要密码(仓库/动作保护解锁)。密码不落盘,由 UI 现问现发。</summary>
    public event Action? PasswordRequested;
    public event Action<bool, string>? StorageResult;  // (成功?, 提示) 存/取仓库结果
    public event Action? EquipChanged;         // 装备变化
    public event Action<int, int>? ExpGained;       // (总经验, 获得量)
    public event Action<int>? LevelUp;              // 升级
    public event Action<int, int, int>? Struck;     // (伤害, HP, MaxHP)
    public event Action? Died;                      // 死亡
    public event Action<long, string>? ItemDropped;  // (itemId, name) 地面掉落
    /// <summary>视野内的怪物流血致死。SM_WINEXP(44) 经验包紧随其后,统计面板据此把经验归到本次击杀。</summary>
    public event Action<long, string>? MonsterKilled;

    /// <summary>已报过击杀的怪 ID:血量归零和 SM_DEATH 两条路都会想到报一次,不能报两遍
    /// (统计面板按这条加计数)。换图/清场时跟着 Dots 一起清。</summary>
    private readonly HashSet<long> _killReported = new();

    private void ReportMonsterKilled(long id, string name)
    {
        if (!_killReported.Add(id)) return;
        MonsterKilled?.Invoke(id, name);
    }
    public string CurrentMap = string.Empty;
    /// <summary>地图尺寸,只在真的读到 .map 时才非 0。默认必须是 0(=未知):
    /// 出生点就在 (289,618) 这种量级,任何"保守"的小默认值都会把整张图判成不可走。</summary>
    public int MapWidth;
    public int MapHeight;
    /// <summary>地图数据未知时寻路用的边长(Mir2 的图最宽 1000 格上下)。</summary>
    private const int UnknownMapExtent = 1024;
    public int PathfindWidth => MapWidth > 0 ? MapWidth : UnknownMapExtent;
    public int PathfindHeight => MapHeight > 0 ? MapHeight : UnknownMapExtent;
    /// <summary>地图可走性回调,由 MainForm 在地图加载后设置。null 时视为全可走。</summary>
    public Func<int, int, bool>? IsWalkable;
    /// <summary>编号→中文名(服务端 MapInfo.txt)。换图时还没读到就再找一遍;宿主(设置里指定了 .map 目录的 WPF、
    /// 探针)读到地图数据后可以递一份带自己目录的那份进来,当前图的名字立刻跟着换。</summary>
    public MapInfoFile? MapInfo
    {
        get => _mapInfo;
        set
        {
            if (ReferenceEquals(_mapInfo, value)) return;
            _mapInfo = value;
            if (CurrentMap.Length > 0) Player.MapName = value?.NameOf(CurrentMap) ?? CurrentMap;
        }
    }
    private MapInfoFile? _mapInfo;
    public long MyRecogId;                           // 自己的人物ID(SM_LOGON)
    public bool MyRecogIdSet;

    private readonly BotSession _session;
    public event Action<string>? Log;

    public BotRuntime(BotSession session) => _session = session;

    /// <summary>启动收包循环,在后台 Task 里跑,直到断开。</summary>
    public void StartReceiveLoop(CancellationToken ct)
    {
        BotLog.Info("[runtime] StartReceiveLoop 已调用,启动后台接收循环");
        _ = Task.Run(() => ReceiveLoopAsync(ct), ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        int pktCount = 0;
        BotLog.Info("[runtime] ReceiveLoopAsync 开始执行");
        try
        {
            await foreach (var pkt in _session.ReadAllPacketsAsync(ct).ConfigureAwait(false))
            {
                pktCount++;
                BotLog.Trace($"[runtime] 读到第 {pktCount} 个包 ident={pkt.Header.Ident} Recog={pkt.Header.Recog}");
                try
                {
                    // 整段处理都在 _itemListLock 里:handler 会往 BagItems/UseItems/StorageItems/
                    // MyMagicList 里增删,而 UI 线程和挂机 AI 线程正在枚举它们(见 SnapshotBag 的说明)。
                    lock (_itemListLock) HandlePacket(pkt);
                    if (pktCount <= 3 || pktCount % 50 == 0)
                        EmitLog($"[runtime] 已处理 {pktCount} 个包, ident={pkt.Header.Ident}");
                }
                catch (Exception ex)
                {
                    BotLog.Error($"[runtime] HandlePacket 异常 ident={pkt.Header.Ident}: {ex}");
                    EmitLog($"[runtime] 解析异常: {ex.Message}");
                }
            }
            BotLog.Warn($"[runtime] ReadAllPacketsAsync 枚举结束(共 {pktCount} 个包),可能连接已断或 Writer 完成");
        }
        catch (Exception ex)
        {
            BotLog.Error($"[runtime] ReceiveLoopAsync 异常: {ex.Message}");
        }
        finally
        {
            BotLog.Info($"[runtime] 接收循环结束, 共处理 {pktCount} 个包");
            // UI 里只写"接收循环结束"会让人回头去查协议:本服进世界后 0.6s 被踢是服务端
            // QManage.txt [@授权] 块的 KICK(服务器名不等于授权名),零上行同样被踢。
            // 把方向直接说清楚,避免每次都重查一遍。
            string hint = MyRecogIdSet
                ? "已进世界后被断开 —— 服务端主动踢线(本机 M2 的 QManage.txt [@授权] KICK),与客户端发包无关"
                : "未进世界即断开 —— 检查登录/选角链路";
            string connState = _session.IsConnected ? "连接仍在" : "连接已断";
            EmitLog($"[runtime] 接收循环结束, 共处理 {pktCount} 个包; {connState}; {hint}{UnknownIdentSummary()}");
        }
    }

    private void HandlePacket(MirServerPacket pkt)
    {
        switch (pkt.Header.Ident)
        {
            case Grobal2.SM_LOGON:          HandleLogon(pkt); break;
            case Grobal2.SM_NEWMAP:         HandleNewMap(pkt); break;
            case Grobal2.SM_CHANGEMAP:      HandleNewMap(pkt); break;
            case Grobal2.SM_ABILITY:        HandleAbility(pkt); break;
            case Grobal2.SM_SENDUSERSTATE:  HandleUserState(pkt); break;
            case Grobal2.SM_POSTIONMOVE:    HandlePosMove(pkt); break;
            case Grobal2.SM_WITHINRANGE:    HandleWithinRange(pkt); break;
            case Grobal2.SM_SENDNOTICE:     HandleNotice(pkt); break;
            case Grobal2.SM_BAGITEMS:       HandleBagItems(pkt); break;
            case Grobal2.SM_SENDUSEITEMS:   HandleUseItems(pkt); break;
            case Grobal2.SM_ADDITEM:        HandleAddItem(pkt); break;
            case Grobal2.SM_DELITEM:        HandleDelItem(pkt); break;
            case Grobal2.SM_DELITEMS:       HandleDelItems(pkt); break;
            case Grobal2.SM_UPDATEITEM:     HandleUpdateItem(pkt); break;
            case Grobal2.SM_WINEXP:         HandleWinExp(pkt); break;
            case Grobal2.SM_LEVELUP:        HandleLevelUp(pkt); break;
            case Grobal2.SM_STRUCK:         HandleStruck(pkt); break;
            case Grobal2.SM_HEALTHSPELLCHANGED: HandleHealthSpellChanged(pkt); break;
            case Grobal2.SM_EAT_OK:         HandleEatResult(pkt, success: true); break;
            case Grobal2.SM_EAT_FAIL:       HandleEatResult(pkt, success: false); break;
            case Grobal2.SM_DEATH:          HandleDeath(pkt); break;
            case Grobal2.SM_ITEMSHOW:       HandleItemShow(pkt); break;
            case Grobal2.SM_ITEMHIDE:       HandleItemHide(pkt); break;
            case Grobal2.SM_CLEAROBJECTS:   HandleClearObjects(pkt); break;
            case Grobal2.SM_GOLDCHANGED:    HandleGoldChanged(pkt); break;
            case Grobal2.SM_WEIGHTCHANGED:  HandleWeightChanged(pkt); break;
            case Grobal2.SM_FEATURECHANGED: HandleFeatureChanged(pkt); break;
            case Grobal2.SM_DISAPPEAR:      HandleDisappear(pkt); break;
            case Grobal2.SM_USERNAME:       HandleUserName(pkt); break;
            case Grobal2.SM_SUBABILITY:     HandleSubAbility(pkt); break;
            case Grobal2.SM_CHARSTATUSCHANGED: HandleCharStatusChanged(pkt); break;
            case Grobal2.SM_MERCHANTSAY:    HandleMerchantSay(pkt); break;
            case Grobal2.SM_MENU_OK:        HandleMenuOk(pkt); break;
            case Grobal2.SM_PASSWORD:       HandlePasswordRequest(pkt); break;
            case Grobal2.SM_TAKEON_OK:      HandleEquipResult(pkt, success: true,  takeOn: true);  break;
            case Grobal2.SM_TAKEON_FAIL:    HandleEquipResult(pkt, success: false, takeOn: true);  break;
            case Grobal2.SM_TAKEOFF_OK:     HandleEquipResult(pkt, success: true,  takeOn: false); break;
            case Grobal2.SM_TAKEOFF_FAIL:   HandleEquipResult(pkt, success: false, takeOn: false); break;
            case Grobal2.SM_SAVEITEMLIST:   HandleStorageItems(pkt); break;
            case Grobal2.SM_STORAGE_OK:     HandleStorageResult(pkt, true, "存入成功", dropFromBag: true); break;
            case Grobal2.SM_STORAGE_FULL:   HandleStorageResult(pkt, false, "仓库已满"); break;
            case Grobal2.SM_STORAGE_FAIL:   HandleStorageResult(pkt, false, "存入失败"); break;
            case Grobal2.SM_TAKEBACKSTORAGEITEM_OK: HandleStorageResult(pkt, true, "取出成功"); break;
            case Grobal2.SM_TAKEBACKSTORAGEITEM_FAIL: HandleStorageResult(pkt, false, "取出失败"); break;
            case Grobal2.SM_TAKEBACKSTORAGEITEM_FULLBAG: HandleStorageResult(pkt, false, "背包已满"); break;
            case Grobal2.SM_DEALMENU:         HandleDealMenu(pkt); break;
            case Grobal2.SM_DEALTRY_FAIL:     HandleDealTryFail(pkt); break;
            case Grobal2.SM_DEALADDITEM_OK:   HandleDealAddItemResult(pkt, true); break;
            case Grobal2.SM_DEALADDITEM_FAIL: HandleDealAddItemResult(pkt, false); break;
            case Grobal2.SM_DEALDELITEM_OK:   HandleDealAddItemResult(pkt, true); break;
            case Grobal2.SM_DEALDELITEM_FAIL: HandleDealAddItemResult(pkt, false); break;
            case Grobal2.SM_DEALCANCEL:       HandleDealCancel(pkt); break;
            case Grobal2.SM_DEALREMOTEADDITEM: HandleDealRemoteAddItem(pkt); break;
            case Grobal2.SM_DEALREMOTEDELITEM: HandleDealRemoteDelItem(pkt); break;
            case Grobal2.SM_DEALCHGGOLD_OK:   HandleDealGoldResult(pkt, true); break;
            case Grobal2.SM_DEALCHGGOLD_FAIL: HandleDealGoldResult(pkt, false); break;
            case Grobal2.SM_DEALREMOTECHGGOLD: HandleDealRemoteGold(pkt); break;
            case Grobal2.SM_DEALSUCCESS:      HandleDealSuccess(pkt); break;
            case Grobal2.SM_GROUPMODECHANGED: HandleGroupModeChanged(pkt); break;
            case Grobal2.SM_CREATEGROUP_OK:   HandleGroupResult(pkt, "建组成功"); break;
            case Grobal2.SM_CREATEGROUP_FAIL: HandleGroupResult(pkt, "建组失败"); break;
            case Grobal2.SM_GROUPADDMEM_OK:   HandleGroupResult(pkt, "加人成功"); break;
            case Grobal2.SM_GROUPDELMEM_OK:   HandleGroupResult(pkt, "踢人成功"); break;
            case Grobal2.SM_GROUPADDMEM_FAIL: HandleGroupResult(pkt, "加人失败"); break;
            case Grobal2.SM_GROUPDELMEM_FAIL: HandleGroupResult(pkt, "踢人失败"); break;
            case Grobal2.SM_GROUPCANCEL:      HandleGroupCancel(pkt); break;
            case Grobal2.SM_GROUPMEMBERS:     HandleGroupMembers(pkt); break;
            case Grobal2.SM_OPENGUILDDLG:     HandleGuildDlg(pkt); break;
            case Grobal2.SM_OPENGUILDDLG_FAIL: HandleGuildResult(pkt, "打开行会面板失败"); break;
            case Grobal2.SM_SENDGUILDMEMBERLIST: HandleGuildMembers(pkt); break;
            case Grobal2.SM_GUILDADDMEMBER_OK: HandleGuildResult(pkt, "行会加人成功"); break;
            case Grobal2.SM_GUILDADDMEMBER_FAIL: HandleGuildResult(pkt, "行会加人失败"); break;
            case Grobal2.SM_GUILDDELMEMBER_OK: HandleGuildResult(pkt, "行会踢人成功"); break;
            case Grobal2.SM_GUILDDELMEMBER_FAIL: HandleGuildResult(pkt, "行会踢人失败"); break;
            case Grobal2.SM_BUILDGUILD_OK:    HandleGuildResult(pkt, "建行会成功"); break;
            case Grobal2.SM_BUILDGUILD_FAIL:  HandleGuildResult(pkt, "建行会失败"); break;
            case Grobal2.SM_GUILDMAKEALLY_OK: HandleGuildResult(pkt, "结盟成功"); break;
            case Grobal2.SM_GUILDMAKEALLY_FAIL: HandleGuildResult(pkt, "结盟失败"); break;
            case Grobal2.SM_GUILDBREAKALLY_OK: HandleGuildResult(pkt, "解除结盟成功"); break;
            case Grobal2.SM_GUILDBREAKALLY_FAIL: HandleGuildResult(pkt, "解除结盟失败"); break;
            case Grobal2.SM_SENDGOODSLIST:  HandleSendGoodsList(pkt); break;
            case Grobal2.SM_SENDDETAILGOODSLIST: HandleSendDetailGoodsList(pkt); break;
            case Grobal2.SM_BUYITEM_SUCCESS: HandleBuyItemResult(pkt, success: true); break;
            case Grobal2.SM_BUYITEM_FAIL:    HandleBuyItemResult(pkt, success: false); break;
            case Grobal2.SM_SENDUSERSELL:   HandleSendUserSell(pkt); break;
            case Grobal2.SM_SENDBUYPRICE:   HandleSellPrice(pkt); break;
            case Grobal2.SM_USERSELLITEM_OK:  HandleSellItemResult(pkt, success: true); break;
            case Grobal2.SM_USERSELLITEM_FAIL: HandleSellItemResult(pkt, success: false); break;
            case Grobal2.SM_SENDUSERREPAIR:   HandleUserRepair(pkt); break;
            case Grobal2.SM_SENDREPAIRCOST:   HandleRepairCost(pkt); break;
            case Grobal2.SM_USERREPAIRITEM_OK: HandleRepairResult(pkt, success: true); break;
            case Grobal2.SM_USERREPAIRITEM_FAIL: HandleRepairResult(pkt, success: false); break;
            case Grobal2.SM_MERCHANTDLGCLOSE: HandleMerchantDlgClose(pkt); break;
            case Grobal2.SM_HEAR:           HandleHear(pkt); break;
            case Grobal2.SM_SYSMESSAGE:     HandleSysMessage(pkt); break;
            // 96/97/98/99 与 100 同源:服务端 SendSocket 原样 GBK 文本,只是显示位置不同。
            case Grobal2.SM_NEWLINEMESSAGE:
            case Grobal2.SM_SUPERMOVEMESSAGE:
            case Grobal2.SM_SCREENMESSAGE:
            case Grobal2.SM_MOVEMESSAGE:
            case Grobal2.SM_CRY:            HandleSysMessage(pkt); break;
            case Grobal2.SM_WHISPER:        HandleChat(pkt, "私聊"); break;
            case Grobal2.SM_GROUPMESSAGE:   HandleChat(pkt, "组队"); break;
            case Grobal2.SM_GUILDMESSAGE:   HandleChat(pkt, "行会"); break;
            case Grobal2.SM_DROPITEM_SUCCESS: HandleDropItemResult(pkt, true); break;
            case Grobal2.SM_DROPITEM_FAIL:    HandleDropItemResult(pkt, false); break;
            case Grobal2.SM_DURACHANGE:     HandleDuraChange(pkt); break;
            case Grobal2.SM_UPDATEITEM_DURA: HandleUpdateItemDura(pkt, false); break;
            case Grobal2.SM_UPDATEITEM_DURAMAX: HandleUpdateItemDura(pkt, true); break;
            case Grobal2.SM_EXT_BAG_COUNT_CHANGE: HandleExtBagCount(pkt); break;
            case Grobal2.SM_SENDMYMAGIC:    HandleSendMyMagic(pkt); break;
            case Grobal2.SM_MAGIC_LVEXP:    HandleMagicLvExp(pkt); break;
            case Grobal2.SM_OPENDOOR_OK:    HandleDoor(pkt, "开"); break;
            case Grobal2.SM_OPENDOOR_LOCK:  HandleDoor(pkt, "锁"); break;
            case Grobal2.SM_CLOSEDOOR:      HandleDoor(pkt, "关"); break;
            // 动作包:服务端通过这些包"顺便"创建/更新视野对象(BotClient 之前漏处理)
            // 注意:SM_DEATH(32)/SM_NOWDEATH(34) 都归 HandleDeath,SM_TURN 另有 HandleTurn,此处不重复
            case Grobal2.SM_TURN:           HandleTurn(pkt); break;
            case Grobal2.SM_WALK:           HandleActorAction(pkt); break;
            case Grobal2.SM_RUN:            HandleActorAction(pkt); break;
            case Grobal2.SM_HIT:            HandleActorAction(pkt); break;
            case Grobal2.SM_HEAVYHIT:       HandleActorAction(pkt); break;
            // 死亡/复活必须按 ident 单独处理(理由见 HandleDeath 注释):
            // 34 才是"刚刚倒下"那一条,而且死者自己也收 34;27 是 ReAlive 的复活广播。
            case Grobal2.SM_NOWDEATH:       HandleDeath(pkt); break;
            case Grobal2.SM_ALIVE:          HandleAlive(pkt); break;
            case Grobal2.SM_BACKSTEP:       HandleActorAction(pkt); break;
            // SM_MOVEFAIL 携带服务端真实坐标(Param=X, Tag=Y, Series=方向),
            // 必须用它校正乐观更新超前了的本地坐标,否则后续 CM_HIT 因坐标
            // 与服务端 m_nCurrX/m_nCurrY 不一致被全部拒绝(攻击打不出)。
            // gxx 里这条已经死了(没有任何发送点),活着的回正通道是 SM_ACTION_RET。
            case Grobal2.SM_MOVEFAIL:       HandleMoveFail(pkt); break;
            case Grobal2.SM_ACTION_RET:     HandleActionRet(pkt); break;
            default:
                NoteUnknownIdent(pkt);
                break;
        }
    }

    // ============================================================
    //  未处理的下行包统计
    //  真机上一旦某个状态没同步(例如对象位置、属性加成),日志里过去什么痕迹都没有,
    //  只能靠回源码逐个 ident 猜。这里把"收到但没处理"的 ident 计数留到断开时一行汇总,
    //  每种只打一次首包,避免刷屏。
    // ============================================================
    private readonly Dictionary<ushort, int> _unknownIdents = new();

    private void NoteUnknownIdent(MirServerPacket pkt)
    {
        ushort ident = pkt.Header.Ident;
        if (_unknownIdents.TryGetValue(ident, out int n)) { _unknownIdents[ident] = n + 1; return; }
        _unknownIdents[ident] = 1;
        BotLog.Info($"[runtime] 未处理下行包 ident={ident} 包体={pkt.BodyEncoded.Length}B " +
                    $"R={pkt.Header.Recog} P={pkt.Header.Param} T={pkt.Header.Tag} S={pkt.Header.Series}");
    }

    public string UnknownIdentSummary()
    {
        if (_unknownIdents.Count == 0) return "";
        string detail = string.Join(" ", _unknownIdents.OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key).Take(10).Select(kv => $"{kv.Key}x{kv.Value}"));
        return $"; 未处理下行 ident(共{_unknownIdents.Count}种): {detail}";
    }

    // ============================================================
    //  地图
    // ============================================================
    private void HandleNewMap(MirServerPacket pkt)
    {
        string decoded = EdCode.DecodeString(pkt.BodyEncoded);
        CurrentMap = decoded.Trim();
        MapInfo ??= MapInfoFile.TryLoad(null);
        Player.MapName = MapInfo?.NameOf(CurrentMap) ?? CurrentMap;
        // 换图包自己就带着落地坐标:ObjPlayer.pas:38028 (SM_NEWMAP) 和 :38533 (SM_CHANGEMAP) 都是
        // MakeDefaultMsg(ident, NativeInt(Self), m_nCurrX, m_nCurrY, 亮度),原版客户端
        // ClMain.pas:24394 也按 param{x}/tag{y} 用。原来只读地图名 ⇒ 真机坐传送员到比奇城后
        // 本地坐标还停在旧图的 (286,613),新图所有 NPC 被算成 340 格外,
        // 商店/仓库的每条上行都被服务端"同图 + <15 格"闸门静默丢掉(2026-09-23 probe_npc15)。
        int landX = pkt.Header.Param, landY = pkt.Header.Tag;
        if ((!MyRecogIdSet || pkt.Header.Recog == MyRecogId) && landX > 0 && landY > 0)
        {
            Player.PosX = landX;
            Player.PosY = landY;
            SafeLog($"[map] 落地坐标=({landX},{landY})");
        }
        Dots.Clear();
        ClearDropItems();
        // 换图/重登后旧地图的对象血量记录全部作废:若不清理,FindNearestMonster 的
        // "血量≤0跳过"过滤会用旧记录把新地图的活怪误判为死怪,自动战斗找不到目标。
        ObjectHpMap.Clear();
        _killReported.Clear();      // 对象 ID 会被服务端复用,不清就等于把新怪的击杀吞掉
        BotLog.Info($"[map] HandleNewMap 地图={CurrentMap} bodyLen={pkt.BodyEncoded.Length}");
        SafeLog($"[map] 地图={CurrentMap}");
        // 事件触发可能抛 UI 异常(BeginInvoke 在窗口未就绪时),保护起来不打断接收循环
        try { MapChanged?.Invoke(); } catch (Exception ex) { BotLog.Warn($"[map] MapChanged 异常已吞: {ex.Message}"); }
        try { StateChanged?.Invoke(); } catch (Exception ex) { BotLog.Warn($"[map] StateChanged 异常已吞: {ex.Message}"); }

        // 传送(NPC 传送/小退重连等)换图后重新查询背包,否则新地图上看不到物品
        RequestBagRefresh(200, "map");
    }

    // ============================================================
    //  SM_LOGON (50) — 进入游戏
    //  Header: Recog=玩家ID, Param=X, Tag=Y, Series=方向
    //  Body: EdCode(MessageBodyWL) 可选
    // ============================================================
    private void HandleLogon(MirServerPacket pkt)
    {
        MyRecogId = pkt.Header.Recog;
        MyRecogIdSet = true;
        SelfIsDead = false;     // 服务端每次登录新建 TPlayObject,m_boDeath 必为 False(见 SelfIsDead 注释)
        Player.PosX = pkt.Header.Param;
        Player.PosY = pkt.Header.Tag;
        Player.Direction = pkt.Header.Series;
        Player.LastUpdate = DateTime.Now;
        BotLog.Info($"[logon] HandleLogon ID={MyRecogId} pos=({Player.PosX},{Player.PosY}) dir={Player.Direction}");
        SafeLog($"[logon] ID={MyRecogId} pos=({Player.PosX},{Player.PosY}) dir={Player.Direction}");

        // 进入游戏后请求刷新背包。服务端每次登录新建 TPlayObject,m_boQueryBagItemsOK 初始为 False,
        // 第一条查询必定被受理(不受 3 秒窗口约束),所以这里把窗口计时清零 —— 小退重连时
        // 上一条查询可能就在几秒前,不清的话首次进世界会空等一个窗口。
        lock (_bagQueryLock) _lastBagQueryUtc = DateTime.MinValue;
        RequestBagRefresh(100, "logon");

        try { StateChanged?.Invoke(); } catch (Exception ex) { BotLog.Warn($"[logon] StateChanged 异常已吞: {ex.Message}"); }
    }

    // ============================================================
    //  SM_ABILITY (52) — 角色属性
    //  Header: Recog=金币, Param=MakeWord(job,99), Tag/Series=GameGold 低/高 16 位
    //  Body: zLibCompressBuffer(TAbility) — 对照 ClMain.pas:26404 ProcessMessageAbility
    //        和 ObjPlayer.pas:38213 ServerSendAbility
    // ============================================================
    private void HandleAbility(MirServerPacket pkt)
    {
        int gold = pkt.Header.RecogI;
        byte job = (byte)(pkt.Header.Param & 0xFF);
        uint gameGold = ((uint)pkt.Header.Series << 16) | pkt.Header.Tag;

        byte[] body = pkt.BodyInflated;
        if (!GxxPayload.TryReadAbility(body, out GxxPayload.AbilityInfo ab))
        {
            BotLog.Warn($"[ability] SM_ABILITY 包体无法解析 解压后={body.Length}B 需要>={GxxPayload.AbilityMinSize}B");
            return;
        }

        Player.Level = ab.Level;
        Player.Hp = ab.Hp;
        Player.MaxHp = ab.MaxHp;
        Player.Mp = ab.Mp;
        Player.MaxMp = ab.MaxMp;
        Player.Exp = ab.Exp;
        Player.MaxExp = ab.MaxExp;
        Player.AC = ab.Ac;
        Player.MAC = ab.Mac;
        Player.DC = ab.Dc;
        Player.MC = ab.Mc;
        Player.SC = ab.Sc;
        Player.Weight = ab.Weight;
        Player.MaxWeight = ab.MaxWeight;
        Player.WearWeight = ab.WearWeight;
        Player.MaxWearWeight = ab.MaxWearWeight;
        Player.HandWeight = ab.HandWeight;
        Player.MaxHandWeight = ab.MaxHandWeight;
        Player.Gold = gold;
        Player.GameGold = (int)gameGold;
        Player.Job = job;
        Player.LastUpdate = DateTime.Now;
        EmitLog($"[ability] Lv={ab.Level} HP={ab.Hp}/{ab.MaxHp} MP={ab.Mp}/{ab.MaxMp} Exp={ab.Exp}/{ab.MaxExp} Job={job} 包体={body.Length}B");
        if (_levelUpPending)
        {
            // SM_LEVELUP 自己不带等级(见 HandleLevelUp 的注释),真等级是这条包给的
            _levelUpPending = false;
            EmitLog($"[levelup] 现在 Lv={ab.Level}");
            LevelUp?.Invoke(ab.Level);
        }
        StateChanged?.Invoke();
    }

    // ============================================================
    //  SM_SENDUSERSTATE (751) — 角色完整状态 (二进制 UserStateInfoHeader)
    //  注意: 不是SM_USERSTATE(660), BotClient早期版本用错了
    // ============================================================
    private void HandleUserState(MirServerPacket pkt)
    {
        if (EdCode.TryDecodeBuffer(pkt.BodyEncoded, out UserStateInfoHeader us))
        {
            string name = us.UserNameString.TrimStart('*');
            if (!string.IsNullOrEmpty(name)) Player.Name = name;
            Player.Sex = us.Gender;
            Player.LastUpdate = DateTime.Now;
            EmitLog($"[userstate] {Player.Name} Gender={us.Gender}");
            StateChanged?.Invoke();
        }
        else
        {
            // 回退: 某些服务器可能用字符串格式
            string decoded = EdCode.DecodeString(pkt.BodyEncoded);
            var parts = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 1) Player.Name = parts[0].TrimStart('*');
            if (parts.Length >= 3 && byte.TryParse(parts[2], out var sex)) Player.Sex = sex;
            EmitLog($"[userstate-string] {Player.Name}");
            StateChanged?.Invoke();
        }
    }

    // ============================================================
    //  SM_POSTIONMOVE (57) — 位置移动 (注意: 不是60!)
    //  Header: Recog=对象ID, Param=X, Tag=Y, Series=方向
    //  Body: EdCode(PositionMoveMsg) 二进制
    // ============================================================
    private void HandlePosMove(MirServerPacket pkt)
    {
        long recogId = pkt.Header.Recog;
        int x = pkt.Header.Param;
        int y = pkt.Header.Tag;
        ushort dir = pkt.Header.Series;

        if (recogId == MyRecogId)
        {
            Player.PosX = x;
            Player.PosY = y;
            Player.Direction = dir;
            Player.LastUpdate = DateTime.Now;
            StateChanged?.Invoke();
            // 注:服务端走步成功后不给自己发 SM_POSTIONMOVE 回包,
            // 本地坐标靠 SendWalkAsync 乐观更新,此处仅处理服务端强制拉回坐标的情况。
        }
        else if (Dots.TryGetValue(recogId, out var dot))
        {
            Dots[recogId] = dot with { X = x, Y = y };
            StateChanged?.Invoke();
        }
    }

    // ============================================================
    //  SM_WITHINRANGE (56) — 对象进入视野
    //  Header: Recog=对象ID, Param=X, Tag=Y, Series=?
    //  Body: EdCode(CharDesc或CharDesc2) + 可选名字
    // ============================================================
    private void HandleWithinRange(MirServerPacket pkt)
    {
        long id = pkt.Header.Recog;
        int x = pkt.Header.Param;
        int y = pkt.Header.Tag;

        // 先尝试解码 CharDesc (12字节)
        if (EdCode.TryDecodeBuffer(pkt.BodyEncoded, out CharDesc desc))
        {
            // 从Feature提取类型信息
            DotKind kind = ClassifyFeature(desc.Feature);
            string name = ExtractNameFromBodyTail(pkt.BodyEncoded, EdCode.GetEncodedLength(Unsafe.SizeOf<CharDesc>()));
            if (string.IsNullOrEmpty(name) && Dots.TryGetValue(id, out var old))
            {
                name = old.Name;
            }
            Dots[id] = new MapDot(id, x, y, kind, name, desc.Feature);
            StateChanged?.Invoke();
        }
        // 再尝试 CharDesc2 (8字节)
        else if (EdCode.TryDecodeBuffer(pkt.BodyEncoded, out CharDesc2 desc2))
        {
            DotKind kind = ClassifyFeature(desc2.Feature);
            string name = ExtractNameFromBodyTail(pkt.BodyEncoded, EdCode.GetEncodedLength(Unsafe.SizeOf<CharDesc2>()));
            if (string.IsNullOrEmpty(name) && Dots.TryGetValue(id, out var old))
            {
                name = old.Name;
            }
            Dots[id] = new MapDot(id, x, y, kind, name, desc2.Feature);
            StateChanged?.Invoke();
        }
        // 回退: 字符串解析
        else
        {
            string decoded = EdCode.DecodeString(pkt.BodyEncoded);
            var parts = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3) return;
            if (!long.TryParse(parts[0], out id) || !int.TryParse(parts[1], out x) || !int.TryParse(parts[2], out y)) return;
            DotKind kind = parts.Length >= 4 ? parts[3] switch
            {
                "Merchant" or "50" => DotKind.Npc,
                "Guard" or "12" => DotKind.Npc,
                "UserHuman" or "0" => DotKind.Player,
                _ => DotKind.Monster
            } : DotKind.Monster;
            string name = parts.Length >= 5 ? parts[4] : string.Empty;
            Dots[id] = new MapDot(id, x, y, kind, name);
            StateChanged?.Invoke();
        }
    }

    // ============================================================
    //  SM_OUTOF_RANGE (57) — 对象离开视野
    // ============================================================
    private void HandleOutOfRange(MirServerPacket pkt)
    {
        // Body可能是字符串"id"或二进制，兼容处理
        if (long.TryParse(EdCode.DecodeString(pkt.BodyEncoded).Trim(), out long id))
        {
            Dots.TryRemove(id, out _);
            StateChanged?.Invoke();
        }
        else if (EdCode.TryDecodeBuffer(pkt.BodyEncoded, out long rawId))
        {
            Dots.TryRemove(rawId, out _);
            StateChanged?.Invoke();
        }
    }

    // ============================================================
    //  SM_TURN (10) — gxx 的"视野对象登场/转身"包。进图时服务端为视野内每个对象
    //  各发一条(2026-09 抓包实测一次进世界 12 条),这是 Bot 拿到怪物/玩家列表的唯一来源。
    //  包体 = TCharDesc(13B) + Feature 缓冲(怪 19B=TMonFeature / 人=THumFeature)
    //         + EncodeString(显示名 + '/' + 名字颜色)   —— ObjPlayer.pas:36854-36884
    //  头部: Recog=对象ID, Param=X, Tag=Y, Series=MakeWord(方向, 灯光)
    //  解析规则对照原版客户端 ClMain.pas:25479-25533 ProcessMessageTurn。
    // ============================================================
    private void HandleTurn(MirServerPacket pkt)
    {
        long id = pkt.Header.Recog;
        int x = pkt.Header.Param;
        int y = pkt.Header.Tag;
        if (MyRecogIdSet && id == MyRecogId)
        {
            // 自己的转身:Series=MakeWord(方向,灯光),只取方向
            byte selfDir = (byte)(pkt.Header.Series & 0xFF);
            if (selfDir <= 7) Player.Direction = selfDir;
            return;
        }

        byte[] body = pkt.BodyInflated;
        int featureLen = body.Length < Grobal2.CHAR_DESC_SIZE ? 0 : body[0];
        if (featureLen > body.Length - Grobal2.CHAR_DESC_SIZE)
            featureLen = 0;   // 客户端同样在装不下时放弃外观(ClMain.pas:25492)

        if (featureLen == 0)
        {
            // ServerSendTurnEx:只有坐标,对象已知就移动,未知不凭空创建
            if (Dots.TryGetValue(id, out var existing))
            {
                Dots[id] = existing with { X = x, Y = y };
                StateChanged?.Invoke();
            }
            return;
        }

        // 怪物外观是 TMonFeature(取 btRace),人物外观是 THumFeature(wRace 即服务端的 m_btRaceServer)
        int race = featureLen == Grobal2.MON_FEATURE_SIZE
            ? body[Grobal2.CHAR_DESC_SIZE + Grobal2.MON_FEATURE_RACE_OFF]
            : BitConverter.ToUInt16(body, Grobal2.CHAR_DESC_SIZE);
        string name = ExtractTurnNameTail(body, Grobal2.CHAR_DESC_SIZE + featureLen);
        UpsertDotWithRace(id, x, y, race, name);
    }

    /// <summary>SM_TURN 尾部的 6bit 名字段。抓包里有被截断成二进制的尾,先整段校验
    /// 6bit 字符集(0x3C..0x7E)再解码,否则会把乱码当名字。取 '/' 前、'\' 前为显示名。</summary>
    private static string ExtractTurnNameTail(byte[] body, int offset)
    {
        if (offset >= body.Length) return string.Empty;
        for (int i = offset; i < body.Length; i++)
            if (body[i] < 0x3C || body[i] > 0x7E) return string.Empty;
        string decoded = EdCode.DecodeString(System.Text.Encoding.Latin1.GetString(body, offset, body.Length - offset));
        int slash = decoded.IndexOf('/');
        if (slash >= 0) decoded = decoded[..slash];
        int sep = decoded.IndexOf('\\');
        if (sep >= 0) decoded = decoded[..sep];
        return decoded.Trim();
    }

    // ============================================================
    //  动作包统一处理(SM_WALK/SM_RUN/SM_HIT 等;SM_TURN 另有 HandleTurn)
    //  服务端通过这些包"顺便"创建/更新视野对象:
    //    Header.Recog=对象ID, Param=X, Tag=Y, Series=方向
    //    Body = EdCode(CharDesc2 8字节 | CharDesc 12字节) + 可选名字尾
    //  原版客户端 OnActorAction 同样从 body 解码 CharDesc 创建 actor。
    // ============================================================
    private void HandleActorAction(MirServerPacket pkt)
    {
        long id = pkt.Header.Recog;
        int x = pkt.Header.Param;
        int y = pkt.Header.Tag;

        // 自己的动作包不进 Dots(避免把自己当怪物/玩家显示)
        if (MyRecogIdSet && id == MyRecogId) return;

        // 先尝试 CharDesc2 (8字节) —— 动作包最常见格式
        if (EdCode.TryDecodeBuffer(pkt.BodyEncoded, out CharDesc2 desc2))
        {
            UpsertDot(id, x, y, desc2.Feature, pkt.BodyEncoded, EdCode.GetEncodedLength(Unsafe.SizeOf<CharDesc2>()));
            return;
        }
        // 再尝试 CharDesc (12字节)
        if (EdCode.TryDecodeBuffer(pkt.BodyEncoded, out CharDesc desc))
        {
            UpsertDot(id, x, y, desc.Feature, pkt.BodyEncoded, EdCode.GetEncodedLength(Unsafe.SizeOf<CharDesc>()));
            return;
        }
        // body 解码失败:若是已知对象,仅更新坐标
        if (Dots.TryGetValue(id, out var existing))
        {
            Dots[id] = existing with { X = x, Y = y };
            StateChanged?.Invoke();
        }
    }

    // ============================================================
    //  SM_MOVEFAIL (28) — 服务端拒绝移动/攻击,回传真实坐标
    //  Header: Recog=玩家ID, Param=服务端真实X, Tag=服务端真实Y, Series=方向
    //  注意:gxx 全树只有这条 handler 的注册(ObjPlayer.pas:2292),**没有任何地方发 RM_MOVEFAIL**,
    //  所以别指望它兜底 —— 活着的那条是下面的 SM_ACTION_RET。
    //  另外 ServerSendMoveFail(:37949)的 Recog 是对象指针而不是玩家ID,将来真机上若收到不能按 Recog 过滤。
    // ============================================================
    private void HandleMoveFail(MirServerPacket pkt)
        => CorrectPosition(pkt.Header.Param, pkt.Header.Tag, pkt.Header.Series, "movefail");

    // ============================================================
    //  SM_ACTION_RET (110) — 每个动作包(走/跑/攻击/施法)的回执,也是唯一的坐标回正通道
    //  服务端 ObjPlayer.pas:17637 ClientWalk 成功后 SendDefMessage(SM_ACTION_RET, 跑步许可标志, 1, 1, 0, ''),
    //  失败且非延时(ClientWalkXY 里超速/被挡/冻结/死亡等)时走 SendSocketStatusFail(:48452):
    //      MakeDefaultMsg(SM_ACTION_RET, m_nCurrX, 0, 1, m_btDirection) + 包体=IntToStr(m_nCurrY)
    //  即 Param=0 且 Tag=1 时 Recog/包体/Series 就是服务端的真实 X/Y/方向(原版客户端
    //  ClMain.pas:20434-20468 正是这么解析的:Param=1 只解动作锁,Param=0 才调 ActionFailed)。
    //  RunGate 自己也会插一条 110(MirClientContext.pas:3235,Param=0/1、Tag=0、Recog=0),
    //  那条只表示"网关把这个动作拦下了",没有坐标,绝不能拿 Recog=0 去改位置。
    //  更要小心网关的【移动并发】(uFrmMain.pas:1226 强制开启、ini 关不掉):同一 Ident 的包在网关
    //  队列里积压时,ClearConcurrentPacket(:9599)会把后面的**全丢掉**,每丢一个回一条假的"成功"110
    //  (:9606,Param=1)⇒ 服务端根本没收到那几步,而我们读到的全是受理,没有任何回正。
    //  所以:动作包必须"一次只在途一个",而且真丢了只能靠 ResyncPositionAsync 主动问回来。
    // ============================================================
    private void HandleActionRet(MirServerPacket pkt)
    {
        Interlocked.Increment(ref _actionRetSeq);          // 受理/拒绝都算一个回音,ResyncPositionAsync 等它
        if (pkt.Header.Param != 0) return;                 // 受理回执(跑步许可标志位对我们没意义:只发 CM_WALK)
        if (pkt.Header.Tag != 1)
        {
            BotLog.Info("[actionret] 网关拦下一个动作包(不含坐标,无需回正)");
            return;
        }
        // 包体是服务端 SendSocket 原样送出的十进制 Y(TPlayObject.SendSocket:3546 不做 EncodeString)
        if (!int.TryParse(pkt.BodyGbk.Trim(), out int serverY))
        {
            BotLog.Warn($"[actionret] 坐标回正包体解析失败: '{pkt.BodyGbk}'");
            return;
        }
        int serverX = (int)pkt.Header.Recog;
        // 越界的"坐标"只会是坏包或被改写过的主字,拿它回正等于把人物瞬移出图,后续寻路全废。
        if (serverX < 0 || serverY < 0 || (MapWidth > 0 && (serverX >= MapWidth || serverY >= MapHeight)))
        {
            BotLog.Warn($"[actionret] 回正坐标越界 ({serverX},{serverY}) 地图={MapWidth}x{MapHeight},忽略");
            return;
        }
        CorrectPosition(serverX, serverY, pkt.Header.Series, "actionret");
    }

    /// <summary>用服务端回传的真实坐标强制校正乐观更新超前了的本地坐标。
    /// 本地坐标只要比服务端超前,后续 CM_HIT(nX!=m_nCurrX 被 ClientHitXY 拒)、
    /// CM_PICKUP(ObjPlayer.pas:20472 要求踩在物品格上)、NPC 15 格判断会全部跟着错。</summary>
    private void CorrectPosition(int serverX, int serverY, int dir, string src)
    {
        if (serverX == Player.PosX && serverY == Player.PosY)
        {
            return; // 本地坐标与服务端一致,无需校正
        }
        BotLog.Info($"[{src}] 校正坐标 本地=({Player.PosX},{Player.PosY}) -> 服务端=({serverX},{serverY})");
        EmitLog($"[{src}] 坐标已按服务端回正 ({Player.PosX},{Player.PosY}) -> ({serverX},{serverY})");
        Player.PosX = serverX;
        Player.PosY = serverY;
        if (dir >= 0 && dir <= 7)
        {
            Player.Direction = (ushort)dir;
        }
        Player.LastUpdate = DateTime.Now;
        Interlocked.Increment(ref _posCorrectedSeq);
        try { StateChanged?.Invoke(); } catch (Exception ex) { BotLog.Warn($"[{src}] StateChanged 异常已吞: {ex.Message}"); }
    }

    /// <summary>坐标被服务端回正过几次。WalkToAsync 用它判断"路径的起点假设还成不成立"。</summary>
    private long _posCorrectedSeq;

    /// <summary>收到过多少条 SM_ACTION_RET(受理与拒绝都算)。ResyncPositionAsync 拿它等回音。</summary>
    private long _actionRetSeq;

    /// <summary>ResyncPositionAsync 等回音的上限(ms)。110 和上行走同一个 RTT,几百毫秒足够。</summary>
    public int ActionRetWaitMs { get; set; } = 800;

    private DateTime _lastResyncUtc = DateTime.MinValue;

    /// <summary>
    /// 主动问服务端"我到底在哪格":发一条**原地不动**的 CM_TURN(坐标=我方认为的当前格,方向=当前朝向)。
    /// 服务端 ClientChangeDir(ObjPlayer.pas:17294)要求 nX/nY 必须等于它记的 m_nCurrX/Y,
    /// 对不上就整个 if 跳过 ⇒ Result=False 且 dwDelayTime=0 ⇒ :17369 SendSocketStatusFail 回真值;
    /// 对得上但方向没变,也会在 :17319 直接 Exit ⇒ 同样回真值。
    /// 两种情况都不会真的移动或转向,所以这条包可以放心发;唯一要守的是转向限速
    /// (本机 TurnIntervalTime=500ms,发太密会落进 dwDelayTime&gt;0 的分支)。
    /// 返回 true 表示坐标被改写过(我方原来是错的)。等不到回音(被动作锁吃掉)返回 false。
    /// </summary>
    public async Task<bool> ResyncPositionAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastResyncUtc).TotalMilliseconds < 520) return false;
        _lastResyncUtc = now;

        long since = Volatile.Read(ref _actionRetSeq);
        int px = Player.PosX, py = Player.PosY;
        await SendTurnAsync((byte)Player.Direction, ct).ConfigureAwait(false);

        var deadline = now.AddMilliseconds(ActionRetWaitMs);
        while (Volatile.Read(ref _actionRetSeq) == since && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        return Player.PosX != px || Player.PosY != py;
    }

    /// <summary>老协议路径:从二进制 CharDesc 的 feature 值 + body 尾部名字建立 Dot。</summary>
    private void UpsertDot(long id, int x, int y, int feature, string bodyEncoded, int structEncodedLen)
        => UpsertDotWithRace(id, x, y, feature & 0xFF, ExtractNameFromBodyTail(bodyEncoded, structEncodedLen));

    /// <summary>按 race + 名字建立/更新 Dot。
    /// 注意:动作包大多不带名字尾,若每次都用空串覆盖,已有名字会丢失、列表显示成 #id。
    /// 因此名字为空时依次取:旧 Dots 名字 → DotNameCache 缓存名字(SM_USERNAME 与 SM_TURN 都会写入)。</summary>
    private void UpsertDotWithRace(long id, int x, int y, int race, string name)
    {
        // 已知死亡(血量归零)且已不在 Dots 的对象:忽略后续动作包,不复活尸体
        // (服务端在 SM_STRUCK 血量归零后仍会发动作包,若不加此判断死怪会反复"复活",红点不消失)
        if (ObjectHpMap.TryGetValue(id, out var hpInfo) && hpInfo.Hp <= 0 && !Dots.ContainsKey(id))
        {
            return;
        }
        DotKind kind = ClassifyFeature(race);
        if (string.IsNullOrEmpty(name) && Dots.TryGetValue(id, out var old))
        {
            name = old.Name;
        }
        if (string.IsNullOrEmpty(name) && DotNameCache.TryGetValue(id, out var cached))
        {
            name = cached;
        }
        if (!string.IsNullOrEmpty(name))
        {
            DotNameCache[id] = name;   // 缓存已知名字,换图后仍可复用
            BotLog.Info($"[dot] id={id} name={name} race={race} kind={kind}");
        }
        else
        {
            BotLog.Info($"[dot] id={id} name=<空> race={race} kind={kind}");
        }
        Dots[id] = new MapDot(id, x, y, kind, name, race);
        StateChanged?.Invoke();
    }

    // ============================================================
    //  SM_SENDNOTICE (658) — 游戏公告
    // ============================================================
    private void HandleNotice(MirServerPacket pkt)
    {
        string noticeText = pkt.BodyGbk;   // 真正的公告是 zlib 原始字节;当前空公告时长度为 0
        BotLog.Info($"[notice] HandleNotice 进入,公告长度={noticeText.Length}");
        SafeLog($"[notice] 收到公告({noticeText.Length}字)");
        _ = Task.Run(async () =>
        {
            try
            {
                // 服务端首包公告为空(Recog=0),此时 FRecbNoticeCode 仍为 0,
                // 校验值是 MakeLong(Param,Tag),必须回 0;分辨率/UI 版本按原版 ClMain.pas:29507 填。
                var noticeOk = CmdPack.MakeLoginNoticeOk(0);
                string payload = EdCode.EncodeMessage(noticeOk);
                BotLog.Info($"[notice] 准备发送 CM_LOGINNOTICEOK payloadLen={payload.Length}");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _session.SendPayloadAsync(payload, cts.Token);
                BotLog.Info($"[notice] -> CM_LOGINNOTICEOK 已发送");
                SafeLog($"[notice] -> CM_LOGINNOTICEOK");
            }
            catch (Exception ex)
            {
                BotLog.Error($"[notice] 确认公告异常: {ex.Message}");
                SafeLog($"[notice] 确认公告异常: {ex.Message}");
            }
        });
    }

    /// <summary>协议事件日志:同时进 UI 和 BotClient.log,便于事后按包排查。</summary>
    private void EmitLog(string msg)
    {
        BotLog.Info($"[ui] {msg}");
        try { Log?.Invoke(msg); }
        catch (Exception ex) { BotLog.Warn($"[ui] 日志回调异常(窗口未就绪?),已忽略: {ex.Message}"); }
    }

    private void SafeLog(string msg) => EmitLog(msg);

    // ============================================================
    //  物品下行包(gxx):TClientItem 是上千字节的原样结构体数组,老服务端那套
    //  '/' 分隔 + EdCode 文本格式已不存在。各包格式(行号见 M2Engine\ObjPlayer.pas):
    //    SM_BAGITEMS(201)        zlib(nCount × TClientItem)     Series=nCount  :8073
    //    SM_SENDUSEITEMS(621)    nC × [槽位字节 + TClientItem]   Series=nC      :10079
    //    SM_ADDITEM(200) / SM_UPDATEITEM(203)  单条 TClientItem  Series=1  :3385 / :12704
    //    SM_DELITEM(202)         Recog=MakeIndex, body=物品名 GBK 原文          :12654
    //    SM_DELITEMS(709)        body=EncodeString("MakeIndex/…")              :12633
    //    SM_SAVEITEMLIST(704)    nC × TClientItem 原样           Series=nC      :12409
    //  记录长度一律用 包体长度 / nCount 反推,不硬编码 SizeOf(TClientItem)。
    // ============================================================

    /// <summary>按 Series(nCount) 把包体切成等长 TClientItem 记录;0 表示不可解析。</summary>
    private static int ItemRecordStride(MirServerPacket pkt, byte[] body, string what)
        => ItemRecordStride(pkt.Header.Series, body, what);

    private static int ItemRecordStride(int nCount, byte[] body, string what)
    {
        if (body.Length == 0) return 0;
        int n = nCount;
        if (n <= 0 || body.Length % n != 0) n = 1;
        int stride = body.Length / n;
        if (stride < GxxPayload.StdItemSize + 10)
        {
            BotLog.Warn($"[item] {what}: 包体 {body.Length}B / nCount={nCount} 得到记录长 {stride}B,不是 TClientItem");
            return 0;
        }
        return stride;
    }

    private static List<BagItemInfo> ReadItemRecords(MirServerPacket pkt, byte[] body)
    {
        var list = new List<BagItemInfo>();
        int stride = ItemRecordStride(pkt, body, "items");
        if (stride == 0) return list;
        for (int off = 0; off + stride <= body.Length; off += stride)
        {
            var ci = GxxPayload.ReadClientItem(body.AsSpan(off, stride));
            if (ci != null) list.Add(ToBagItem(ci.Value));
        }
        return list;
    }

    private static BagItemInfo? ReadSingleItem(MirServerPacket pkt, byte[] body, string what)
    {
        int stride = ItemRecordStride(pkt, body, what);
        if (stride == 0) return null;
        var ci = GxxPayload.ReadClientItem(body.AsSpan(0, stride));
        return ci == null ? null : ToBagItem(ci.Value);
    }

    private static BagItemInfo ToBagItem(GxxPayload.ClientItemInfo v)
    {
        // 服务端 CheckOverLapItem(M2Share.pas:11028)= OverLap>0 且 StdMode∈{0,2,3,31,40,41,42,46,47} 且 DuraMax>1。
        // 这类物品的 Dura 存的是"件数-1":建 1 个时 Dura:=0(UsrEngn.pas:5585),统计/卖出/显示一律 Dura+1
        // (ObjBase.pas:2186、Grobal2.pas:6841、原版客户端 FState.pas:19247)。
        bool stack = IsStackableItem(v);
        return new BagItemInfo
        {
            MakeIndex = v.MakeIndex,
            Name = v.Name,
            DuraCount = v.Dura,
            DuraMax = v.DuraMax,
            Count = stack ? v.Dura + 1 : 1,
            Stackable = stack,
            StdMode = v.StdMode,
        };
    }

    private static bool IsStackableItem(in GxxPayload.ClientItemInfo v) =>
        v.OverLap > 0 && v.DuraMax > 1 &&
        v.StdMode is 0 or 2 or 3 or 31 or 40 or 41 or 42 or 46 or 47;

    private void HandleBagItems(MirServerPacket pkt)
    {
        BagItems.Clear();
        byte[] body = pkt.BodyInflated;
        BagItems.AddRange(ReadItemRecords(pkt, body));
        EmitLog($"[bag] 背包物品: {BagItems.Count} 个 (nCount={pkt.Header.Series} 解压后={body.Length}B)");
        ItemsChanged?.Invoke();
    }

    private void HandleUseItems(MirServerPacket pkt)
    {
        UseItems.Clear();
        // 必须铺满 30 槽:服务端 MAX_USE_ITEM_COUNT=30,斗笠(13)/盾牌(16)/灵玉(17) 也会下发
        for (int i = 0; i < Grobal2.MAX_USE_ITEM_COUNT; i++) UseItems.Add(default);
        byte[] body = pkt.BodyInflated;
        int stride = ItemRecordStride(pkt, body, "equip");
        int got = 0;
        if (stride > 0)
        {
            for (int off = 0; off + stride <= body.Length; off += stride)
            {
                int slot = body[off];
                if ((uint)slot >= (uint)UseItems.Count) continue;
                var ci = GxxPayload.ReadClientItem(body.AsSpan(off + 1, stride - 1));
                if (ci == null) continue;
                UseItems[slot] = ToBagItem(ci.Value);
                got++;
            }
        }
        EmitLog($"[equip] 装备 {got} 件 (nCount={pkt.Header.Series} 包体={body.Length}B)");
        EquipChanged?.Invoke();
    }

    private void HandleAddItem(MirServerPacket pkt)
    {
        var item = ReadSingleItem(pkt, pkt.BodyInflated, "additem");
        if (item == null) return;
        BagItems.Add(item.Value);
        EmitLog($"[bag] +物品 {item.Value.Name} MakeIdx={item.Value.MakeIndex}");
        ItemsChanged?.Invoke();
    }

    private void HandleDelItem(MirServerPacket pkt)
    {
        int makeIdx = pkt.Header.RecogI;
        int removed = BagItems.RemoveAll(i => i.MakeIndex == makeIdx);
        if (removed > 0)
        {
            EmitLog($"[bag] -物品 {pkt.BodyGbk} MakeIdx={makeIdx}");
            ItemsChanged?.Invoke();
        }
    }

    private void HandleDelItems(MirServerPacket pkt)
    {
        string s = EdCode.DecodeString(pkt.BodyEncoded);
        int removed = 0;
        var run = new System.Text.StringBuilder();
        for (int i = 0; i <= s.Length; i++)
        {
            char c = i < s.Length ? s[i] : '\0';
            if (c is >= '0' and <= '9') { run.Append(c); continue; }
            if (run.Length > 0 && int.TryParse(run.ToString(), out int makeIdx))
                removed += BagItems.RemoveAll(x => x.MakeIndex == makeIdx);
            run.Clear();
        }
        if (removed > 0)
        {
            EmitLog($"[bag] -批量删除 {removed} 件");
            ItemsChanged?.Invoke();
        }
    }

    private void HandleUpdateItem(MirServerPacket pkt)
    {
        var item = ReadSingleItem(pkt, pkt.BodyInflated, "updateitem");
        if (item == null) return;
        for (int i = 0; i < BagItems.Count; i++)
        {
            if (BagItems[i].MakeIndex == item.Value.MakeIndex)
            {
                BagItems[i] = item.Value;
                ItemsChanged?.Invoke();
                return;
            }
        }
        BagItems.Add(item.Value);
        ItemsChanged?.Invoke();
    }

    // ============================================================
    //  SM_WINEXP (44) — 获得经验
    //  Header: Recog=总经验, Param=获得经验低16, Tag=获得经验高16
    // ============================================================
    private void HandleWinExp(MirServerPacket pkt)
    {
        int totalExp = pkt.Header.RecogI;
        uint gained = ((uint)pkt.Header.Tag << 16) | (uint)pkt.Header.Param;
        Player.Exp = totalExp;
        Player.LastUpdate = DateTime.Now;
        EmitLog($"[exp] total={totalExp} gain={gained}");
        ExpGained?.Invoke(totalExp, (int)gained);
        StateChanged?.Invoke();
    }

    // ============================================================
    //  SM_LEVELUP (45) — 升级。**包里没有等级!**
    //  服务端:SendRefMsg(RM_LEVELUP, m_btDirection, m_nCurrX, m_nCurrY, 0)(ObjBase.pas:13524),
    //  SendRefMsg 的形参序是 (wIdent, wParam, nParam1, nParam2, nParam3)(ObjBase.pas:30980),
    //  再到 ServerSendLevelUp 摆成 MakeDefaultMsg(SM_LEVELUP, 对象ID, nParam1, nParam2, wParam)
    //  (ObjPlayer.pas:37857)⇒ Param=X、Tag=Y、Series=方向。
    //  真等级只在它**紧跟着下发的那条 SM_ABILITY** 里(:37862-37866 同一段直接重发 m_Abil),
    //  所以这里只立个"该报升级了"的标记,等 Ability 落地再把真实等级发出去。
    //  (旧实现把 Param 当等级,真机日志就写着 [levelup] Lv=289 —— 那是我们的 X 坐标。)
    // ============================================================
    private bool _levelUpPending;

    private void HandleLevelUp(MirServerPacket pkt)
    {
        _levelUpPending = true;
        Player.LastUpdate = DateTime.Now;
        EmitLog($"[levelup] 升级:服务端给的是坐标 ({pkt.Header.Param},{pkt.Header.Tag}) 方向={pkt.Header.Series},等级看随后的 SM_ABILITY");
    }

    // ============================================================
    //  SM_STRUCK (31) — 受击
    //  Header: Recog=受击者ID, Param/Tag=伤害的 LoWord/HiWord, Series=MakeWord(是否魔法, 是否)
    //  Body:   TNewMessageBodyWL —— HP/MaxHP/伤害/等级/MaxMP/攻击者ID 全在这里
    //  (ObjPlayer.pas:37516 ServerSendStruck;原版客户端 ClMain.pas:26916 同样只从包体取血量)
    // ============================================================
    /// <summary>任意对象(玩家/怪物)血量变化: (对象ID, 当前HP, 最大HP)。用于怪物信息条。</summary>
    public event Action<long, int, int>? ObjectHpChanged;

    /// <summary>最近一次记录的各对象血量, key=对象ID, value=(HP, MaxHP)。
    /// 用 ConcurrentDictionary:收包线程在换图时 Clear、战斗中不停写,而怪物信息条(UI 线程)
    /// 和挂机 AI 选目标都在读 —— Dictionary 遇到并发的 Clear/新增不会像 List 那样抛"集合已修改",
    /// 而是可能读到撕裂的桶,所以这里不能只靠收包线程那把列表锁。</summary>
    public readonly ConcurrentDictionary<long, (int Hp, int MaxHp)> ObjectHpMap = new();

    private void HandleStruck(MirServerPacket pkt)
    {
        long recogId = pkt.Header.Recog;
        int damage = (ushort)pkt.Header.Param | (pkt.Header.Tag << 16);

        if (!GxxPayload.TryReadHealthWl(pkt.BodyBytes, out var wl))
        {
            // 包体不是 41 字节的 TNewMessageBodyWL 就没有血量可更新 —— 这时候绝不能拿
            // Param/Tag 当 HP:那是伤害。按错了会把本地血量改成个位数,而 AI 的喝药/逃跑
            // 判据全按本地血量算(MaxHp<=0 时更是直接不逃跑)。
            BotLog.Warn($"[struck] 包体 {pkt.BodyBytes.Length}B 不是 TNewMessageBodyWL,只记伤害={damage} 不动血量");
            return;
        }

        // 记录所有对象的血量(怪物被攻击时也会收到 SM_STRUCK,用于怪物信息条显示)
        ObjectHpMap[recogId] = (wl.Hp, wl.MaxHp);
        ObjectHpChanged?.Invoke(recogId, wl.Hp, wl.MaxHp);

        // 怪物血量归零 → 主动从 Dots 移除(服务端可能不发 SM_DISAPPEAR,死怪红点会残留)
        if (recogId != MyRecogId && wl.Hp <= 0)
        {
            if (Dots.TryRemove(recogId, out var dead))
            {
                BotLog.Info($"[dot] 血量归零移除 #{recogId} name={dead.Name} HP={wl.Hp}/{wl.MaxHp}");
                // 红点在这一步就摘了,随后的 SM_DEATH/SM_NOWDEATH 反而"认不出"这是只怪。
                // 击杀必须在这里报一次,否则血量见底、经验到手,统计面板却永远 击杀=0(真机踩过)。
                if (dead.Kind == DotKind.Monster) ReportMonsterKilled(recogId, dead.Name);
                StateChanged?.Invoke();
            }
        }

        if (recogId == MyRecogId)
        {
            Player.Hp = wl.Hp;
            Player.MaxHp = wl.MaxHp;
            Player.LastUpdate = DateTime.Now;
            EmitLog($"[struck] 受到伤害={damage} HP={wl.Hp}/{wl.MaxHp} 攻击者={wl.AttackerId}");
            Struck?.Invoke(damage, wl.Hp, wl.MaxHp);
            StateChanged?.Invoke();
        }
    }

    // ============================================================
    //  SM_HEALTHSPELLCHANGED (53) — HP/MP 变化(喝药/被加血/扣蓝/被击打后同步)
    //  Header: Recog=对象ID, Param/Tag=MP 的 LoWord/HiWord, Series=MakeWord(职业, 1=被击打形态)
    //  Body:   TNewMessageBodyWL(41B,含 HP/MaxHP/MaxMP)或 TMessageHealthSpellChangedInfo(5B,只有 HP)
    //  服务端: ObjPlayer.pas:37506 / :38227 ServerSendHealthSpellChanged
    // ============================================================
    private void HandleHealthSpellChanged(MirServerPacket pkt)
    {
        long recogId = pkt.Header.Recog;
        int mp = (ushort)pkt.Header.Param | (pkt.Header.Tag << 16);
        byte[] body = pkt.BodyBytes;

        if (GxxPayload.TryReadHealthWl(body, out var wl))
        {
            ObjectHpMap[recogId] = (wl.Hp, wl.MaxHp);
            ObjectHpChanged?.Invoke(recogId, wl.Hp, wl.MaxHp);
            if (recogId != MyRecogId) return;
            Player.Hp = wl.Hp;
            Player.MaxHp = wl.MaxHp;
            Player.MaxMp = wl.MaxMp;
            Player.Mp = mp;
        }
        else if (recogId == MyRecogId)
        {
            // 5 字节的轻量形态:包体只给 HP,MP 在包头
            if (GxxPayload.TryReadHealthChangedInfo(body, out int changeHp)) Player.Hp = changeHp;
            Player.Mp = mp;
        }
        if (recogId != MyRecogId) return;
        Player.LastUpdate = DateTime.Now;
        EmitLog($"[hp] HP/MP 变化: {Player.Hp}/{Player.MaxHp} MP={Player.Mp}/{Player.MaxMp}");
        StateChanged?.Invoke();
    }

    // ============================================================
    //  SM_EAT_OK (635) / SM_EAT_FAIL (636) — 使用物品结果
    // ============================================================
    public event Action<bool, string>? EatResult;   // (成功?, 提示)
    private void HandleEatResult(MirServerPacket pkt, bool success)
    {
        if (success)
        {
            EmitLog("[eat] 使用物品成功");
            EatResult?.Invoke(true, "使用成功");
        }
        else
        {
            EmitLog($"[eat] 使用物品失败 code={pkt.Header.Recog}");
            EatResult?.Invoke(false, $"使用失败(code={pkt.Header.Recog})");
        }
    }

    // ============================================================
    //  SM_DEATH (32) / SM_NOWDEATH (34) — 死亡
    //  服务端 TBaseObject.Die 发的是 SendRefMsg(RM_DEATH, 方向, X, Y, 1, '')(ObjBase.pas:42849),
    //  nParam3=1 ⇒ ServerSendDeath 摆成 **SM_NOWDEATH** 发给视野里的每一个对象,**包括死者本人**
    //  (ObjPlayer.pas:37959-37964;SendRefMsg 的广播循环没有 `<> Self` 排除)。
    //  SM_DEATH(nParam3≠1)只在"有人刚看见一具已经倒下的对象"时补发给那个新观众
    //  (ObjPlayer.pas:15940-15955)。
    //  ⇒ 之前只把 32 当死亡包 = 自己死亡永远检测不到(Died 事件从没触发过),
    //    而 34 走 HandleActorAction:对自己是 `id == MyRecogId → return`(整包扔掉),
    //    对怪是 UpsertDot —— 把刚倒下的怪重新点亮成活靶子,挂机于是反复攻击尸体。
    // ============================================================
    /// <summary>自己是否处于服务端 m_boDeath 状态。
    /// 死亡状态下 ObjPlayer.pas 的上行闸门一律直接 Exit 且零回包:
    /// ClientTurn:17277、ClientWalk:17476、ClientRun:18002、ClientHit:18638、
    /// ClientSpell:19028、ClientUseItems:21438(只有 StdMode=31 的卷类放行);
    /// 唯一没有死亡判断的是 ClientPickUpItem(:20417)。
    /// 引擎里没有任何"客户端请求复活"的上行包(Grobal2.pas 无 CM_*REBIRTH/REVIVE,
    /// ObjPlayer.pas:1940-2180 上行注册表里也没有)⇒ 复活只能发生在服务端:
    /// 脚本 NPC 的 RELIVE/REALIVE(NpcCommon.pas:2450 → NpcActionCmd.pas:12055)、
    /// GM @ReAlive、复活术(Magic.pas:3946),或者**断开重连** ——
    /// HP&lt;=0 时服务端把角色搬回回家点并强设 HP=14(UsrEngn.pas:1212-1238),
    /// m_boDeath 因为对象重建自然为 False(ObjBase.pas:11267)。</summary>
    public bool SelfIsDead { get; private set; }

    /// <summary>自己复活(SM_ALIVE)。</summary>
    public event Action? Revived;

    private void HandleDeath(MirServerPacket pkt)
    {
        long recogId = pkt.Header.Recog;
        if (recogId == MyRecogId)
        {
            if (SelfIsDead) return;     // 同一场死亡的重复广播不重复报
            SelfIsDead = true;
            Player.Hp = 0;
            Player.LastUpdate = DateTime.Now;
            EmitLog("[death] 角色死亡");
            Died?.Invoke();
            StateChanged?.Invoke();
            return;
        }
        // 其他对象:移除红点并把血量钉在 0 —— 否则随后的动作包会经 UpsertDot 把尸体重新点亮
        bool wasKnown = Dots.TryGetValue(recogId, out var dead);
        if (wasKnown && dead.Kind == DotKind.Monster)
            ReportMonsterKilled(recogId, dead.Name);
        Dots.TryRemove(recogId, out _);
        ObjectHpMap[recogId] = (0, ObjectHpMap.TryGetValue(recogId, out var h) ? h.MaxHp : 0);
        if (wasKnown) StateChanged?.Invoke();
    }

    // ============================================================
    //  SM_ALIVE (27) — 复活广播(服务端 TBaseObject.ReAlive:ObjBase.pas:34686-34699)
    //  ReAlive 只清 m_boDeath、把 HP 填满并原地站着,不传送(回城是脚本另一条 ActionOfGoHome);
    //  紧跟的还有 RM_ABILITY(NpcActionCmd.pas:12059)和 RM_HEALTHSPELLCHANGED 补 HP/MP。
    // ============================================================
    private void HandleAlive(MirServerPacket pkt)
    {
        long recogId = pkt.Header.Recog;
        ObjectHpMap.TryRemove(recogId, out _);    // 撤掉 HandleDeath 的尸体标记,否则对象再也进不了 Dots
        if (recogId == MyRecogId)
        {
            if (!SelfIsDead) return;
            SelfIsDead = false;
            // ReAlive 里服务端就是 m_WAbil.HP := m_WAbil.MaxHP(ObjBase.pas:34691-34692),
            // SM_ALIVE 包体只有 TCharDesc 不带血量,所以按同一条规则本地回写。
            // 不写的话 HP 还停在 0 ⇒ 刚复活就被"血量低于 20% → 逃跑/小退"接管(实测踩过)。
            // 随后服务端还会发 RM_ABILITY / RM_HEALTHSPELLCHANGED 兜底校正。
            if (Player.MaxHp > 0) Player.Hp = Player.MaxHp;
            Player.LastUpdate = DateTime.Now;
            EmitLog($"[death] 已复活 HP={Player.Hp}/{Player.MaxHp}");
            Revived?.Invoke();
            StateChanged?.Invoke();
            return;
        }
        HandleActorAction(pkt);         // 别人/怪复活:按普通动作包重建位置
    }

    // ============================================================
    //  SM_ITEMSHOW (610) — 地面显示物品
    // ============================================================
    // id/坐标/外观都在包头(Recog/Param/Tag/Series),包体只有"颜色/叠加数/是否价值品/名字"。
    // 服务端: SendRefMsg(RM_ITEMSHOW, looks, NativeInt(ItemObject), nX, nY, sMsg)  ObjBase.pas:12995
    // 金币包体: 颜色 + '/0/0/金币' (ObjBase.pas:13794) —— 金额不在封包里,拾取后才由包变化得知。
    private void HandleItemShow(MirServerPacket pkt)
    {
        long itemId = pkt.Header.Recog;
        int x = pkt.Header.Param;
        int y = pkt.Header.Tag;
        int looks = pkt.Header.Series;
        string decoded = EdCode.DecodeString(pkt.BodyEncoded);
        // 客户端用 GetValidStr3 逐段切,空段也占位,所以这里不能把空项丢掉
        var parts = decoded.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
        {
            EmitLog($"[item] SM_ITEMSHOW 包体字段不足: {decoded[..Math.Min(60, decoded.Length)]}");
            return;
        }
        int count = int.TryParse(parts[1], out int c) ? c : 0;
        int overlap = count & 0xFFFF;
        string itemName = parts[3];
        string dbName = parts.Length > 4 && parts[4].Length > 0 ? parts[4] : itemName;
        bool isGold = itemName.Contains(Grobal2.sSTRING_GOLDNAME);
        UpsertDropItem(new DropItemInfo(itemId, itemName, x, y, looks, isGold, 0));
        Dots[itemId] = new MapDot(itemId, x, y, isGold ? DotKind.Gold : DotKind.Item, itemName);
        EmitLog($"[item] 掉落: {itemName} x{overlap} at ({x},{y}) looks={looks} db={dbName}");
        ItemDropped?.Invoke(itemId, itemName);
        StateChanged?.Invoke();
    }

    // ============================================================
    //  SM_ITEMHIDE (611) — 地面隐藏物品
    // ============================================================
    // 服务端: SendRefMsg(RM_ITEMHIDE, 0, NativeInt(ItemObject), nX, nY, '') (ObjBase.pas:16782)
    // 包体恒为空,物品 id 在 Recog。按旧写法解析包体会永远删不掉地面物品。
    private void HandleItemHide(MirServerPacket pkt)
    {
        long itemId = pkt.Header.Recog;
        int removed = RemoveDropItem(itemId);
        bool hadDot = Dots.TryRemove(itemId, out _);
        if (removed > 0 || hadDot) StateChanged?.Invoke();
    }

    // ============================================================
    //  SM_CLEAROBJECTS (633) — 清空视野对象
    // ============================================================
    private void HandleClearObjects(MirServerPacket pkt)
    {
        EmitLog("[map] SM_CLEAROBJECTS");
        Dots.Clear();
        ClearDropItems();
        StateChanged?.Invoke();
    }

    // ============================================================
    //  SM_GOLDCHANGED (653) — 金币变化
    // ============================================================
    private void HandleGoldChanged(MirServerPacket pkt)
    {
        int gold = pkt.Header.RecogI;
        ushort gameGoldLo = pkt.Header.Param;
        ushort gameGoldHi = pkt.Header.Tag;
        uint gameGold = ((uint)gameGoldHi << 16) | gameGoldLo;
        Player.Gold = gold;
        Player.GameGold = (int)gameGold;
        EmitLog($"[gold] Gold={gold} GameGold={gameGold}");
        StateChanged?.Invoke();
    }

    // ============================================================
    //  SM_WEIGHTCHANGED (622) — 负重变化
    // ============================================================
    private void HandleWeightChanged(MirServerPacket pkt)
    {
        Player.Weight = pkt.Header.RecogI;
        Player.WearWeight = pkt.Header.Param;
        Player.HandWeight = pkt.Header.Tag;
        EmitLog($"[weight] W={Player.Weight}/{Player.MaxWeight} Wear={Player.WearWeight} Hand={Player.HandWeight}");
        StateChanged?.Invoke();
    }

    // ============================================================
    //  SM_DISAPPEAR (30) — 对象消失(死亡/离开视野),从 Dots 移除
    //  Header: Recog=对象ID
    // ============================================================
    private void HandleDisappear(MirServerPacket pkt)
    {
        long recogId = pkt.Header.Recog;
        if (recogId == MyRecogId) return;
        if (Dots.TryRemove(recogId, out var removed))
        {
            BotLog.Info($"[dot] 对象消失 #{recogId} name={removed.Name}");
            StateChanged?.Invoke();
        }
    }

    // ============================================================
    //  SM_USERNAME (42) — 用户名查询结果(其他玩家的名字)
    //  Header: Recog=对象ID, Param=名字颜色
    // ============================================================
    /// <summary>SM_USERNAME(42):包体是服务端原样送出的 GBK 文本
    /// (ObjPlayer.pas:20203-20204 SendSocket(@Def, Target.GetShowName(...)) 未经 EncodeString;
    /// 抓包实测 "阿柯\【明天网络】(沙巴克)\"),显示名取第一个 '\' 之前(对照 ClMain.pas:27451)。</summary>
    private void HandleUserName(MirServerPacket pkt)
    {
        long recogId = pkt.Header.Recog;
        string raw = pkt.BodyGbk.Trim();
        if (raw.Length == 0) return;
        int sep = raw.IndexOf('\\');
        string name = (sep < 0 ? raw : raw[..sep]).Trim();
        if (string.IsNullOrEmpty(name)) return;
        // 服务端进图时也会给自己发一条 SM_USERNAME(抓包 rec10: Recog=自身ID, "阿柯\【明天网络】(沙巴克)\")。
        // 本服从不主动发 SM_SENDUSERSTATE(751),所以这是角色名的唯一来源。
        if (MyRecogIdSet && recogId == MyRecogId)
        {
            Player.Name = name;
            StateChanged?.Invoke();
            return;
        }
        // 服务端可能先发包后建对象,名字一律缓存,对象出现时再取用
        DotNameCache[recogId] = name;
        if (Dots.TryGetValue(recogId, out var dot))
        {
            Dots[recogId] = dot with { Name = name };
            BotLog.Info($"[dot] 名字 #{recogId} -> {name}");
            StateChanged?.Invoke();
        }
    }

    // ============================================================
    //  SM_SUBABILITY (752) — 准确/敏捷/抗魔
    //  服务端: ObjPlayer.pas:37876 SendDefMessage(SM_SUBABILITY,
    //          MakeLong(MakeWord(AntiMagic,0), SpeedPoint), HitPoint,
    //          MakeWord(AntiPoison,PoisonRecover), MakeWord(HealthRecover,SpellRecover), body)
    // ============================================================
    private void HandleSubAbility(MirServerPacket pkt)
    {
        Player.SpeedPoint = (int)(pkt.Header.Recog >> 16);
        Player.HitPoint = pkt.Header.Param;
        BotLog.Info($"[abil] SM_SUBABILITY 准确={Player.HitPoint} 敏捷={Player.SpeedPoint} 抗魔={pkt.Header.Recog & 0xFFFF}");
        StateChanged?.Invoke();
    }

    // ============================================================
    //  SM_FEATURECHANGED (41) — 外观变化
    // ============================================================
    private void HandleFeatureChanged(MirServerPacket pkt)
    {
        long recogId = pkt.Header.Recog;
        int feature = (pkt.Header.Tag << 16) | pkt.Header.Param;
        if (recogId == MyRecogId)
        {
            // 自身外观变化 (换装备等)
        }
        else if (Dots.TryGetValue(recogId, out var dot))
        {
            Dots[recogId] = dot with { Feature = feature };
            StateChanged?.Invoke();
        }
    }

    // ============================================================
    //  SM_TAKEON_OK (615) / SM_TAKEON_FAIL (616)
    //  SM_TAKEOFF_OK (619) / SM_TAKEOFF_FAIL (620) — 穿戴/卸下结果
    //  服务端穿戴成功后只回本包+RM_ABILITY,不发 SM_SENDUSEITEMS,
    //  因此这里主动重查背包/装备/属性,让 UI 跟随服务器状态刷新。
    //  同时做本地乐观更新:发送时记录 (makeIndex, slot),成功回包后立即
    //  把物品在 BagItems/UseItems 之间搬移并触发事件,不等查询回包。
    // ============================================================
    private int _pendingTakeOnMakeIndex;
    private int _pendingTakeOnSlot = -1;
    private int _pendingTakeOffMakeIndex;
    private int _pendingTakeOffSlot = -1;

    private void HandleEquipResult(MirServerPacket pkt, bool success, bool takeOn)
    {
        // 事件要带"这次问的是哪件装备",AI 换武器时靠 MakeIndex 认回自己那一刀(616 的 Recog 是失败原因码)。
        int makeIndex = takeOn ? _pendingTakeOnMakeIndex : _pendingTakeOffMakeIndex;
        int slot = takeOn ? _pendingTakeOnSlot : _pendingTakeOffSlot;
        string what = takeOn ? "穿戴" : "卸下";
        EmitLog(success ? $"[equip] {what}成功 idx={makeIndex} 槽={slot}"
                        : $"[equip] {what}失败 idx={makeIndex} 槽={slot} code={pkt.Header.Recog}");
        BotLog.Info($"[equip] result takeOn={takeOn} success={success} idx={makeIndex} slot={slot} Recog={pkt.Header.Recog}");
        if (success) ApplyPendingEquipChange(takeOn);
        if (takeOn)
        {
            // 成败都要清这条待确认记录:留着的话下一次"卸下成功"会拿它去搬背包里的物品。
            _pendingTakeOnSlot = -1;
            TakeOnResult?.Invoke(makeIndex, slot, success);
        }
        if (!success) return;
        RequestBagRefresh(150, "equip");
    }

    /// <summary>穿戴结果(makeIndex, 部位, 是否成功)。服务端只回 615/616,失败原因码在 Recog 里
    /// (ObjPlayer.pas:20715 DoExit 的 n18:-1=背包放不下/-4=被锁住取不下)。</summary>
    public event Action<int, int, bool>? TakeOnResult;

    /// <summary>穿戴/卸下成功的本地乐观更新:直接把物品在背包与装备之间搬移并触发事件,
    /// UI 立即刷新,不再依赖服务端 SM_SENDUSEITEMS 回包(服务端穿戴后不回装备列表)。</summary>
    private void ApplyPendingEquipChange(bool takeOn)
    {
        // 穿戴:BagItems[makeIndex] → UseItems[slot]。
        // ⚠ 同槽已有装备时服务端是"整槽替换"(ObjPlayer.pas:20976-21071):旧件先 m_UseItems[btWhere].wIndex:=0,
        // 再 AddItemToBag + SendAddItem(:21056-21059,背包满则 DropItemDown 落到脚下 :21068),
        // 而"旧件是绑定的且背包满"时直接穿戴失败(n18=-1,:21022-21027)。
        // 新件那一边服务端只回 SM_TAKEON_OK 不发 SM_DELITEM(DelBagItem 本身不回包,ObjBase.pas:41707),
        // 所以新件必须本地搬;旧件那一边服务端会自己发 SM_ADDITEM,本地再 Add 一次就重复了 —— 只清槽。
        if (takeOn && _pendingTakeOnSlot >= 0)
        {
            int makeIndex = _pendingTakeOnMakeIndex;
            int slot = _pendingTakeOnSlot;
            int bagIdx = BagItems.FindIndex(x => x.MakeIndex == makeIndex);
            if (bagIdx >= 0 && (uint)slot < 13)
            {
                var item = BagItems[bagIdx];
                BagItems.RemoveAt(bagIdx);
                var displaced = slot < UseItems.Count ? UseItems[slot] : default;
                while (UseItems.Count <= slot) UseItems.Add(default);
                // 整槽替换:直接覆盖,旧件由服务端 SM_ADDITEM 送回背包(或落到脚下),本地不搬
                UseItems[slot] = item;
                EmitLog($"[equip] 乐观穿戴 {item.Name} → 槽{slot}");
                if (displaced.MakeIndex > 0)
                    EmitLog($"[equip] 被换下的 {displaced.Name} 等服务端 SM_ADDITEM 回背包,本地不重复加");
                ItemsChanged?.Invoke();
                EquipChanged?.Invoke();
            }
            _pendingTakeOnSlot = -1;
        }
        // 卸下:UseItems[slot] → BagItems
        if (!takeOn && _pendingTakeOffSlot >= 0)
        {
            int makeIndex = _pendingTakeOffMakeIndex;
            int slot = _pendingTakeOffSlot;
            if ((uint)slot < UseItems.Count && UseItems[slot].MakeIndex == makeIndex)
            {
                var item = UseItems[slot];
                UseItems[slot] = default;
                BagItems.Add(item);
                EmitLog($"[equip] 乐观卸下 {item.Name} ← 槽{slot}");
                ItemsChanged?.Invoke();
                EquipChanged?.Invoke();
            }
            _pendingTakeOffSlot = -1;
        }
    }

    // ============================================================
    //  SM_SAVEITEMLIST (704) — 仓库物品列表
    //  服务端 SendSaveItemList: SendSocketEx 原样 nC × TClientItem,Series=nC(ObjPlayer.pas:12409)
    // ============================================================
    private void HandleStorageItems(MirServerPacket pkt)
    {
        StorageItems.Clear();
        CurrentStorageMerchantId = pkt.Header.Recog;   // 记录仓库 NPC ID,存取物品需要
        StorageItems.AddRange(ReadItemRecords(pkt, pkt.BodyInflated));
        EmitLog($"[storage] 仓库物品 {StorageItems.Count} 个");
        BotLog.Info($"[storage] SM_SAVEITEMLIST count={StorageItems.Count} recog={pkt.Header.Recog}");
        StorageItemsChanged?.Invoke();
    }

    // ============================================================
    //  SM_STORAGE_OK/FULL/FAIL / SM_TAKEBACKSTORAGEITEM_OK/FAIL/FULLBAG
    //  — 仓库存取结果
    // ============================================================
    private int _pendingStorageMakeIndex;

    private void HandleStorageResult(MirServerPacket pkt, bool success, string hint, bool dropFromBag = false)
    {
        EmitLog($"[storage] {hint} (recog={pkt.Header.Recog})");
        // 存入方向:服务端把物品从 m_ItemList 里 Delete,但**不给客户端发 SM_DELITEM**
        // (真机 2026-09-23 比奇城仓库:存完本地仍是 22 件,取回时服务端又发 SM_ADDITEM ⇒ 界面短暂出现两件)。
        // 取回方向相反 —— 服务端主动 SM_ADDITEM,本地不能再加第二遍。
        if (success && dropFromBag && _pendingStorageMakeIndex != 0)
        {
            int removed = BagItems.RemoveAll(i => i.MakeIndex == _pendingStorageMakeIndex);
            if (removed > 0)
            {
                StateChanged?.Invoke();
                ItemsChanged?.Invoke();
            }
        }
        _pendingStorageMakeIndex = 0;
        StorageResult?.Invoke(success, hint);
        if (success)
        {
            // 成功后再拉一次背包,保证列表实时同步。连存多件时这里只会发出
            // "第一条立即 + 窗口后一条(反映最后一次改动)",中间的重复请求被合并。
            RequestBagRefresh(120, "storage");
        }
    }

    // ============================================================
    //  CM_USERSTORAGEITEM (1031) — 存物品进仓库
    //  服务端: ClientUserStorageItem(:23548) nParam1=仓库NPC对象ID,
    //          MakeLong(nParam2,nParam3)=物品MakeIndex, wParam=Series=目标页(未开启的页会被拒),
    //          sMsg=物品名(还要 CompareText)
    // ============================================================
    public async Task SendStorageItemAsync(long merchantId, int makeIndex, string itemName, int page, CancellationToken ct)
    {
        if (!NpcInRange(merchantId, $"存入仓库 {itemName}")) return;
        _pendingStorageMakeIndex = makeIndex;
        // Recog=merchantId, Param=LoWord(makeIndex), Tag=HiWord(makeIndex), Series=页号, body=物品名
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_USERSTORAGEITEM, merchantId,
            (ushort)(makeIndex & 0xFFFF), (ushort)((makeIndex >> 16) & 0xFFFF), (ushort)page);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(itemName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    // ============================================================
    //  CM_USERTAKEBACKSTORAGEITEM (1032) — 从仓库取物品
    //  服务端: ClientUserTakebackStorageItem(:23733) nParam1=NPC对象ID,
    //          MakeLong(nParam2,nParam3)=物品MakeIndex, wParam=Series=取出数量
    //          (:23770 `if nCount <= 0 then nCount := 1`,传 0 只会拿 1 个,叠加物品要自己填数量)
    // ============================================================
    public async Task SendTakeBackStorageItemAsync(long merchantId, int makeIndex, string itemName, int count, CancellationToken ct)
    {
        if (!NpcInRange(merchantId, $"取出仓库 {itemName}")) return;
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_USERTAKEBACKSTORAGEITEM, merchantId,
            (ushort)(makeIndex & 0xFFFF), (ushort)((makeIndex >> 16) & 0xFFFF), (ushort)count);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(itemName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    // ============================================================
    //  交易(Deal)状态与处理
    // ============================================================
    public bool InDeal;
    public string? DealPartnerName;             // 交易对象玩家名
    public readonly List<BagItemInfo> DealMyItems = new();      // 我方放入交易栏的物品
    public readonly List<BagItemInfo> DealRemoteItems = new();  // 对方放入交易栏的物品
    public int DealMyGold;
    public int DealRemoteGold;
    public bool DealMyReady;                    // 我方是否点了"确定"
    public event Action? DealChanged;           // 交易窗口内容变化(刷新 UI)
    public event Action<string>? DealMessage;   // 交易提示(成功/失败/取消)
    public event Action? DealClosed;            // 交易结束(关闭窗口)

    private void ResetDeal()
    {
        InDeal = false;
        DealPartnerName = null;
        DealMyItems.Clear();
        DealRemoteItems.Clear();
        DealMyGold = 0;
        DealRemoteGold = 0;
        DealMyReady = false;
    }

    // SM_DEALMENU (673): 交易窗口打开(body=对方玩家名)
    private void HandleDealMenu(MirServerPacket pkt)
    {
        InDeal = true;
        DealPartnerName = EdCode.DecodeString(pkt.BodyEncoded).Trim();
        DealMyItems.Clear();
        DealRemoteItems.Clear();
        DealMyGold = 0;
        DealRemoteGold = 0;
        DealMyReady = false;
        BotLog.Info($"[deal] 交易开始,对方={DealPartnerName}");
        EmitLog($"[deal] 与 {DealPartnerName} 交易");
        DealChanged?.Invoke();
    }

    private void HandleDealTryFail(MirServerPacket pkt)
    {
        EmitLog($"[deal] 交易请求被拒绝");
        DealMessage?.Invoke("交易请求被拒绝或对方忙碌");
    }

    private void HandleDealAddItemResult(MirServerPacket pkt, bool ok)
    {
        EmitLog(ok ? "[deal] 放入物品成功" : $"[deal] 放入物品失败 code={pkt.Header.Recog}");
        if (!ok) DealMessage?.Invoke($"放入物品失败(code={pkt.Header.Recog})");
    }

    private void HandleDealCancel(MirServerPacket pkt)
    {
        EmitLog("[deal] 交易取消");
        ResetDeal();
        DealMessage?.Invoke("交易已取消");
        DealClosed?.Invoke();
    }

    // SM_DEALREMOTEADDITEM (682): 对方放入物品(body=单条 TClientItem,ObjPlayer.pas:14301)
    private void HandleDealRemoteAddItem(MirServerPacket pkt)
    {
        var item = ReadSingleItem(pkt, pkt.BodyInflated, "dealadd");
        if (item == null) return;
        DealRemoteItems.Add(item.Value);
        EmitLog($"[deal] 对方放入 {item.Value.Name}");
        DealChanged?.Invoke();
    }

    // SM_DEALREMOTEDELITEM (683): 对方取回物品(Recog=MakeIndex,body=物品名)
    private void HandleDealRemoteDelItem(MirServerPacket pkt)
    {
        int makeIdx = pkt.Header.RecogI;
        int removed = DealRemoteItems.RemoveAll(i => i.MakeIndex == makeIdx);
        if (removed > 0)
        {
            EmitLog($"[deal] 对方取回 {pkt.BodyGbk}");
            DealChanged?.Invoke();
        }
    }

    private void HandleDealGoldResult(MirServerPacket pkt, bool ok)
    {
        EmitLog(ok ? "[deal] 放入金币成功" : $"[deal] 放入金币失败 code={pkt.Header.Recog}");
        if (!ok) DealMessage?.Invoke($"放入金币失败(code={pkt.Header.Recog})");
    }

    // SM_DEALREMOTECHGGOLD (686): 对方放入金币(Recog=金币数)
    private void HandleDealRemoteGold(MirServerPacket pkt)
    {
        DealRemoteGold = pkt.Header.RecogI;
        EmitLog($"[deal] 对方放入金币 {DealRemoteGold}");
        DealChanged?.Invoke();
    }

    private void HandleDealSuccess(MirServerPacket pkt)
    {
        EmitLog("[deal] 交易成功");
        ResetDeal();
        DealMessage?.Invoke("交易成功");
        DealClosed?.Invoke();
        // 交易后重新拉背包/金币
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150);
                // 背包刷新可能落在服务端窗口内被顺延,不要拿它去挡住状态查询(异常已在内部处理)
                _ = QueryBagItemsAsync("deal");
                // CM_QUERYUSERSTATE 语义是"查看指定对象",Recog 必须填对象 ID (ClMain.pas:14177)
                var qState = CmdPack.MakeDefaultMsg(Grobal2.CM_QUERYUSERSTATE, MyRecogId, 0, 0, 0);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _session.SendPayloadAsync(EdCode.EncodeMessage(qState), cts.Token);
            }
            catch (Exception ex) { BotLog.Error($"[deal] 交易后刷新查询异常: {ex.Message}"); }
        });
    }

    // ============================================================
    //  交易发包方法
    // ============================================================
    /// <summary>发起交易请求(CM_DEALTRY)。
    /// 服务端 TPlayObject.ClientDealTry(ObjPlayer.pas:23192) 桌面端走 else 分支
    /// BaseObject := GetPoseCreate() —— 只取"我正前方那一格"的对象,包体里的名字根本不读,
    /// 而且要求双方互相面向(BaseObject.GetPoseCreate = Self)。原版客户端也不发 body
    /// (ClMain.pas:17848 MakeDefaultMsg(CM_DEALTRY,0,0,0,0))。
    /// 所以正确做法是先站到相邻一格、转身面向对方再发包;距离>1 时发出去只会对着空地/路人开交易。</summary>
    public async Task SendDealTryAsync(string targetName, CancellationToken ct)
    {
        MapDot? target = null;
        foreach (var dot in Dots.Values)
        {
            if (dot.Kind != DotKind.Player || dot.Id == MyRecogId) continue;
            if (!string.Equals(dot.Name, targetName, StringComparison.OrdinalIgnoreCase)) continue;
            target = dot;
            break;
        }
        if (target == null)
        {
            EmitLog($"[deal] 视野内找不到玩家 {targetName},未发起交易");
            return;
        }
        int dx = target.Value.X - Player.PosX;
        int dy = target.Value.Y - Player.PosY;
        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1)
        {
            EmitLog($"[deal] {targetName} 不在相邻一格(偏移 {dx},{dy}),先走到身边再交易");
            return;
        }
        byte dir = BotCombatAI.GetDirection(Player.PosX, Player.PosY, target.Value.X, target.Value.Y);
        if (dir != Player.Direction) await SendTurnAsync(dir, ct).ConfigureAwait(false);
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_DEALTRY, 0, 0, 0, 0);
        await _session.SendPayloadAsync(EdCode.EncodeMessage(msg), ct).ConfigureAwait(false);
    }

    // CM_DEALADDITEM (1026): 放入物品(Recog=MakeIndex, body=物品名)
    public async Task SendDealAddItemAsync(int makeIndex, string itemName, CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_DEALADDITEM, makeIndex, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(itemName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    // CM_DEALDELITEM (1027): 取回物品
    public async Task SendDealDelItemAsync(int makeIndex, string itemName, CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_DEALDELITEM, makeIndex, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(itemName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    // CM_DEALCHGGOLD (1029): 放入金币(Recog=金币数)
    public async Task SendDealChangeGoldAsync(int gold, CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_DEALCHGGOLD, gold, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    // CM_DEALEND (1030): 确认交易
    public async Task SendDealEndAsync(CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_DEALEND, 0, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    // CM_DEALCANCEL (1028): 取消交易
    public async Task SendDealCancelAsync(CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_DEALCANCEL, 0, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    // ============================================================
    //  组队(Group)状态与处理
    // ============================================================
    public bool InGroup;
    public bool IsGroupLeader;
    public readonly List<string> GroupMembers = new();   // 成员名(含自己)
    public event Action? GroupChanged;                   // 组队状态变化(刷新面板)
    public event Action<string>? GroupMessage;           // 组队提示

    // SM_GROUPMODECHANGED (659): 组队模式变化(1=允许组队, 0=拒绝)
    private void HandleGroupModeChanged(MirServerPacket pkt)
    {
        EmitLog($"[group] 组队模式 Recog={pkt.Header.Recog}");
        GroupChanged?.Invoke();
    }

    private void HandleGroupResult(MirServerPacket pkt, string hint)
    {
        EmitLog($"[group] {hint}");
        GroupMessage?.Invoke(hint);
        GroupChanged?.Invoke();
    }

    private void HandleGroupCancel(MirServerPacket pkt)
    {
        EmitLog("[group] 组队解散");
        InGroup = false;
        IsGroupLeader = false;
        GroupMembers.Clear();
        GroupMessage?.Invoke("组队解散");
        GroupChanged?.Invoke();
    }

    // SM_GROUPMEMBERS (667): 成员列表(body 为成员名,以 / 或 \ 分隔)
    private void HandleGroupMembers(MirServerPacket pkt)
    {
        string body = EdCode.DecodeString(pkt.BodyEncoded).Trim();
        GroupMembers.Clear();
        if (!string.IsNullOrEmpty(body))
        {
            foreach (var name in body.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
            {
                string n = name.Trim();
                if (n.Length > 0) GroupMembers.Add(n);
            }
        }
        if (GroupMembers.Count > 0 && GroupMembers[0] == Player.Name) IsGroupLeader = true;
        InGroup = GroupMembers.Count > 0;
        EmitLog($"[group] 成员 {GroupMembers.Count} 人: {string.Join(",", GroupMembers)}");
        BotLog.Info($"[group] SM_GROUPMEMBERS body={body}");
        GroupChanged?.Invoke();
    }

    // ============================================================
    //  组队发包方法
    // ============================================================
    // CM_CREATEGROUP (1020): 建组/邀请组队(body=对方玩家名)
    public async Task SendCreateGroupAsync(string targetName, CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_CREATEGROUP, 0, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(targetName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    // CM_ADDGROUPMEMBER (1021): 加人进组(body=玩家名)
    public async Task SendAddGroupMemberAsync(string targetName, CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_ADDGROUPMEMBER, 0, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(targetName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    // CM_DELGROUPMEMBER (1022): 踢人出组(body=玩家名)
    public async Task SendDelGroupMemberAsync(string targetName, CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_DELGROUPMEMBER, 0, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(targetName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    // ============================================================
    //  行会(Guild)状态与处理
    // ============================================================
    public bool InGuild;
    public bool IsGuildLeader;
    public string GuildName = string.Empty;
    public string GuildNotice = string.Empty;
    public readonly List<string> GuildMembers = new();
    public event Action? GuildChanged;                   // 行会状态变化(刷新面板)
    public event Action<string>? GuildMessage;           // 行会提示

    // SM_OPENGUILDDLG (753): 打开行会面板(body=行会信息)
    private void HandleGuildDlg(MirServerPacket pkt)
    {
        string body = EdCode.DecodeString(pkt.BodyEncoded);
        InGuild = true;
        GuildName = string.Empty;
        GuildNotice = string.Empty;
        BotLog.Info($"[guild] SM_OPENGUILDDLG body={body}");
        var lines = body.Split('\r', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 0) GuildName = lines[0].Trim();
        // 找到 <Notice> 之后的文本作为公告
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().StartsWith("<Notice>", StringComparison.OrdinalIgnoreCase))
            {
                GuildNotice = string.Join("\n", lines.Skip(i + 1).Where(l => !l.Trim().StartsWith("<"))
                    .Take(20).Select(l => l.Trim())).Trim();
                break;
            }
        }
        EmitLog($"[guild] 行会: {GuildName}");
        GuildChanged?.Invoke();
        // 打开后自动拉取成员列表
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await SendGuildMemberListAsync(cts.Token);
            }
            catch (Exception ex) { BotLog.Error($"[guild] 拉取成员列表异常: {ex.Message}"); }
        });
    }

    // SM_SENDGUILDMEMBERLIST (756): 行会成员列表
    private void HandleGuildMembers(MirServerPacket pkt)
    {
        string body = EdCode.DecodeString(pkt.BodyEncoded);
        GuildMembers.Clear();
        if (!string.IsNullOrEmpty(body))
        {
            // 格式: #rankNo/*rankName/成员/成员/...#rankNo/*rankName/成员/...
            foreach (var seg in body.Split('#', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = seg.Split('/', StringSplitOptions.RemoveEmptyEntries);
                // parts[0] 形如 "1/*行会掌门", 成员从 parts[1] 起
                for (int i = 1; i < parts.Length; i++)
                {
                    string n = parts[i].Trim();
                    if (n.Length > 0) GuildMembers.Add(n);
                }
            }
        }
        if (GuildMembers.Count == 0) InGuild = false;
        EmitLog($"[guild] 行会成员 {GuildMembers.Count} 人");
        BotLog.Info($"[guild] SM_SENDGUILDMEMBERLIST count={GuildMembers.Count}");
        GuildChanged?.Invoke();
    }

    private void HandleGuildResult(MirServerPacket pkt, string hint)
    {
        EmitLog($"[guild] {hint}");
        GuildMessage?.Invoke(hint);
        GuildChanged?.Invoke();
    }

    // ============================================================
    //  行会发包方法
    // ============================================================
    // CM_OPENGUILDDLG (1035): 打开行会面板
    public async Task SendOpenGuildDlgAsync(CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_OPENGUILDDLG, 0, 0, 0, 0);
        await _session.SendPayloadAsync(EdCode.EncodeMessage(msg), ct).ConfigureAwait(false);
    }

    // CM_GUILDMEMBERLIST (1037): 拉取成员列表
    public async Task SendGuildMemberListAsync(CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_GUILDMEMBERLIST, 0, 0, 0, 0);
        await _session.SendPayloadAsync(EdCode.EncodeMessage(msg), ct).ConfigureAwait(false);
    }

    // CM_GUILDADDMEMBER (1038): 行会加人(body=玩家名)
    public async Task SendGuildAddMemberAsync(string targetName, CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_GUILDADDMEMBER, 0, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(targetName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    // CM_GUILDDELMEMBER (1039): 行会踢人(body=玩家名)
    public async Task SendGuildDelMemberAsync(string targetName, CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_GUILDDELMEMBER, 0, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(targetName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    // ============================================================
    //  SM_CHARSTATUSCHANGED (657) — 角色状态变化(中毒/防等)
    // ============================================================
    private void HandleCharStatusChanged(MirServerPacket pkt)
    {
        long recogId = pkt.Header.Recog;
        int state = (pkt.Header.Tag << 16) | pkt.Header.Param;
    }

    // ============================================================
    //  SM_MERCHANTSAY (643) — NPC对话
    //  包体是服务端原样送出的 GBK 文本 (ObjPlayer.pas:37744-37747
    //  ServerSendMerchantSay: SendSocket(@m_DefMsg, ProcessMsg.sMsg), 未经 EncodeString)。
    //  对照客户端 ClMain.pas:28731 先以 #13 取出脚本名,其余是对话正文(选项以 '/' 分隔)。
    // ============================================================
    public event Action<long, string>? NpcMessage;   // (merchantId, 原始正文)
    private void HandleMerchantSay(MirServerPacket pkt)
    {
        string raw = pkt.BodyGbk;
        string text = raw;
        int cr = raw.IndexOf('\r');
        if (cr >= 0)
        {
            string npcName = raw[..cr].Trim();
            text = raw[(cr + 1)..];
            if (npcName.Length > 0) text = npcName + "/" + text;
        }
        EmitLog($"[npc] NPC说话: {text}");
        NpcMessage?.Invoke(pkt.Header.Recog, text);
    }

    // ============================================================
    //  SM_BUYITEM_SUCCESS(650)/SM_BUYITEM_FAIL(651) — 购买结果
    //  成功: Recog=剩余金币, Param|Tag=物品ID; 失败: Recog=代码(1失败/2负重不足/3金币不足)
    // ============================================================
    public event Action<bool, string>? BuyResult;   // (成功?, 提示文本)
    private void HandleBuyItemResult(MirServerPacket pkt, bool success)
    {
        if (success)
        {
            Player.Gold = pkt.Header.RecogI;
            StateChanged?.Invoke();
            EmitLog($"[shop] 购买成功 剩余金币={pkt.Header.Recog}");
            BuyResult?.Invoke(true, $"购买成功,剩余金币 {pkt.Header.Recog}");
        }
        else
        {
            string text = pkt.Header.Recog switch
            {
                1 => "购买失败",
                2 => "背包/负重不足,无法购买",
                3 => "金币不足,无法购买",
                _ => $"购买失败(code={pkt.Header.Recog})"
            };
            EmitLog($"[shop] 购买失败: {text}");
            BuyResult?.Invoke(false, text);
        }
    }

    // ============================================================
    //  SM_SENDUSERSELL (646) — 服务端进入出售模式(选项选了"卖物品"后)
    //  此时应弹出出售窗口,让玩家从背包选物品出售。
    // ============================================================
    public event Action<long>? SellModeEntered;   // merchantId
    private void HandleSendUserSell(MirServerPacket pkt)
    {
        EmitLog($"[shop] 进入出售模式 merchant={pkt.Header.Recog}");
        SellModeEntered?.Invoke(pkt.Header.Recog);
    }

    // ============================================================
    //  SM_SENDBUYPRICE (647) — 出售价格查询结果(Recog=价格)
    // ============================================================
    public event Action<int>? SellPriceQuoted;   // 价格
    private void HandleSellPrice(MirServerPacket pkt)
    {
        EmitLog($"[shop] 出售价格={pkt.Header.Recog}");
        SellPriceQuoted?.Invoke(pkt.Header.RecogI);
    }

    // ============================================================
    //  SM_USERSELLITEM_OK(648)/SM_USERSELLITEM_FAIL(649) — 出售结果
    // ============================================================
    public event Action<bool, string>? SellResult;
    private int _pendingSellMakeIndex;
    private void HandleSellItemResult(MirServerPacket pkt, bool success)
    {
        if (success)
        {
            Player.Gold = pkt.Header.RecogI;
            // 服务端把卖掉的东西从它的 m_ItemList 里 Delete 掉,但**不会**给客户端发 SM_DELITEM
            // (ObjPlayer.pas:22684 只有 Delete+WeightChanged,回包仅 RM_USERSELLITEM_OK),
            // 所以本地背包模型必须自己把这件摘掉,否则挂机逻辑会一直以为它还在包里。
            int removed = BagItems.RemoveAll(i => i.MakeIndex == _pendingSellMakeIndex && _pendingSellMakeIndex != 0);
            _pendingSellMakeIndex = 0;
            StateChanged?.Invoke();
            if (removed > 0) ItemsChanged?.Invoke();
            EmitLog($"[shop] 出售成功 剩余金币={pkt.Header.Recog}(本地背包删除 {removed} 件)");
            SellResult?.Invoke(true, $"出售成功,剩余金币 {pkt.Header.Recog}");
        }
        else
        {
            _pendingSellMakeIndex = 0;
            EmitLog($"[shop] 出售失败 code={pkt.Header.Recog}");
            SellResult?.Invoke(false, $"出售失败(code={pkt.Header.Recog})");
        }
    }

    // ============================================================
    //  修理链 (SM_SENDUSERREPAIR 668 / SM_SENDREPAIRCOST 671 /
    //          SM_USERREPAIRITEM_OK 669 / FAIL 670)
    //  服务端 ObjNpc.pas:4065 ClientRepairItem 只把新 Dura/DuraMax 放在
    //  SM_USERREPAIRITEM_OK 里,而且不带 MakeIndex —— 必须自己记住刚修哪件。
    //  普通修理先扣上限 (DuraMax-Dura)/30 再补满(ObjNpc.pas:4145),
    //  所以 Dura 与 DuraMax 两个值都要按回包写回。
    // ============================================================
    public event Action<long>? RepairModeEntered;   // merchantId
    private void HandleUserRepair(MirServerPacket pkt)
    {
        EmitLog($"[repair] 进入修理模式 merchant={pkt.Header.Recog}");
        RepairModeEntered?.Invoke(pkt.Header.Recog);
    }

    // ============================================================
    //  SM_SENDREPAIRCOST (671) — 修理费用(Recog=-1 表示 NPC 不给修)
    // ============================================================
    public event Action<int>? RepairCostQuoted;
    private void HandleRepairCost(MirServerPacket pkt)
    {
        int cost = pkt.Header.RecogI;
        EmitLog(cost >= 0 ? $"[repair] 修理费用={cost}" : "[repair] 该物品不可修理");
        RepairCostQuoted?.Invoke(cost);
    }

    // ============================================================
    //  SM_USERREPAIRITEM_OK(669)/FAIL(670) — 修理结果
    // ============================================================
    public event Action<bool, string>? RepairResult;
    private int _pendingRepairMakeIndex;
    private void HandleRepairResult(MirServerPacket pkt, bool success)
    {
        if (!success)
        {
            _pendingRepairMakeIndex = 0;
            EmitLog("[repair] 修理失败");
            RepairResult?.Invoke(false, "NPC 不给修这件");
            return;
        }
        Player.Gold = pkt.Header.RecogI;
        int idx = _pendingRepairMakeIndex == 0 ? -1 : BagItems.FindIndex(i => i.MakeIndex == _pendingRepairMakeIndex);
        if (idx >= 0)
        {
            var bi = BagItems[idx];
            bi.DuraCount = pkt.Header.Param;
            bi.DuraMax = pkt.Header.Tag;
            BagItems[idx] = bi;
            EmitLog($"[repair] {bi.Name} 持久 → {bi.DuraCount}/{bi.DuraMax},剩余金币 {Player.Gold}");
        }
        _pendingRepairMakeIndex = 0;
        StateChanged?.Invoke();
        ItemsChanged?.Invoke();
        RepairResult?.Invoke(true, $"剩余金币 {pkt.Header.Recog}");
    }

    // ============================================================
    //  SM_SENDGOODSLIST (645) — NPC商品列表
    //  正文格式:每条 6 段 "名字/子菜单/价格/库存或MakeIndex/每叠数量/Looks/",见 GxxPayload.ParseMerchantGoods
    // ============================================================
    public event Action<long, string>? NpcGoodsList;   // (merchantId, 原始正文)
    private void HandleSendGoodsList(MirServerPacket pkt)
    {
        string decoded = EdCode.DecodeString(pkt.BodyEncoded);
        EmitLog($"[npc] NPC商品列表: {decoded[..Math.Min(60, decoded.Length)]}");
        NpcGoodsList?.Invoke(pkt.Header.Recog, decoded);
    }

    // ============================================================
    //  SM_SENDDETAILGOODSLIST (652) — "逐件商品"明细(装备类,子菜单=1 的那类)
    //  服务端 ObjNpc.pas:3750:SendMsg(RM_SENDDETAILGOODSLIST, wParam=是否摆摊框,
    //    nParam1=merchant, nParam2=nCount, nParam3=本页起始下标, 包体=nCount×TClientItem)
    //  ⇒ 头 Recog=NPC、Param=件数、Tag=本页起始下标(翻页要回传)、Series=0 才是商店明细。
    //  每条最多 10 件(OnePageCount),成交价在 TStdItem.Expand1(ObjNpc.pas:3735 覆写)。
    // ============================================================
    public event Action<long, IReadOnlyList<GxxPayload.DetailGoods>, int>? NpcDetailGoodsList;  // (merchantId, 明细, 起始下标)
    private void HandleSendDetailGoodsList(MirServerPacket pkt)
    {
        if (pkt.Header.Series != 0)
        {
            EmitLog($"[shop] 收到摆摊/交易行明细 {pkt.Header.Param} 件,未接界面");
            return;
        }
        byte[] body = pkt.BodyInflated;
        int stride = ItemRecordStride(pkt.Header.Param, body, "detailGoods");
        var items = GxxPayload.ParseDetailGoods(body, stride);
        EmitLog($"[shop] 逐件明细 {items.Count} 件 (nCount={pkt.Header.Param} 记录长={stride} 起始下标={pkt.Header.Tag})");
        NpcDetailGoodsList?.Invoke(pkt.Header.Recog, items, pkt.Header.Tag);
    }

    // ============================================================
    //  SM_MERCHANTDLGCLOSE (644) — NPC对话关闭
    // ============================================================
    public event Action? NpcDialogClosed;
    private void HandleMerchantDlgClose(MirServerPacket pkt)
    {
        EmitLog("[npc] NPC对话关闭");
        NpcDialogClosed?.Invoke();
    }

    // ============================================================
    //  SM_HEAR (40) — 听到聊天
    //  包体是服务端原样送出的 GBK 文本 (ObjPlayer.pas:37695
    //  ServerSendHear: SendSocket(@m_DefMsg, ProcessMsg.sMsg)),
    //  文本里已带 "名字:" 前缀,颜色在 Header.Param 的高低字节。
    // ============================================================
    public event Action<string>? ChatMessage;
    private void HandleHear(MirServerPacket pkt)
    {
        string text = pkt.BodyGbk.Trim();
        EmitLog($"[hear] {text}");
        ChatMessage?.Invoke(text);
    }

    // ============================================================
    //  SM_WHISPER (103) / SM_GROUPMESSAGE (101) / SM_GUILDMESSAGE (104)
    //  — 私聊/组队/行会消息(Body 为消息文本,原版客户端显示到聊天框)
    // ============================================================
    private void HandleChat(MirServerPacket pkt, string kind)
    {
        string text = pkt.BodyGbk.Trim();
        if (string.IsNullOrEmpty(text)) return;
        EmitLog($"[{kind}] {text}");
        ChatMessage?.Invoke($"[{kind}] {text}");
    }

    // ============================================================
    //  SM_DROPITEM_SUCCESS (600) / SM_DROPITEM_FAIL (601) — 丢弃物品结果
    // ============================================================
    public event Action<bool, string>? DropItemResult;
    private void HandleDropItemResult(MirServerPacket pkt, bool success)
    {
        string name = EdCode.DecodeString(pkt.BodyEncoded).Trim();
        EmitLog(success ? $"[bag] 丢弃成功 {name}" : $"[bag] 丢弃失败 {name}");
        DropItemResult?.Invoke(success, name);
    }

    // ============================================================
    //  SM_DURACHANGE (642) — 身上装备的持久变化
    //  服务端: SendMsg(RM_DURACHANGE, 槽位, Dura, DuraMax)(ObjBase.pas:28339 每砍一刀扣武器持久、
    //  :39573 每挨一下扣防具持久)→ ObjPlayer.pas:38666 转成包头 Recog=Dura、Param=槽位、
    //  DuraMax=MakeLong(Tag,Series)。原版客户端 ClMain.pas:28665 就是拿 Param 当装备槽用的,
    //  不是 MakeIndex —— 背包/身上物品的持久按 MakeIndex 回写是另一条 10325。
    //  这里不打日志:每次攻击都会来一条,刷成每秒一行会把有用的行埋掉。
    // ============================================================
    private void HandleDuraChange(MirServerPacket pkt)
    {
        int slot = pkt.Header.Param;
        // >=30 是首饰盒(30-35)/神佑袋(40-51)槽,客户端没有建模这两组容器。
        if ((uint)slot >= (uint)UseItems.Count) return;
        var it = UseItems[slot];
        if (string.IsNullOrEmpty(it.Name)) return;
        it.DuraCount = pkt.Header.RecogI;
        it.DuraMax = pkt.Header.Tag | (pkt.Header.Series << 16);
        UseItems[slot] = it;
        EquipChanged?.Invoke();
    }

    // ============================================================
    //  SM_UPDATEITEM_DURA (10325) / SM_UPDATEITEM_DURAMAX (10326) — 按 MakeIndex 回写持久
    //  服务端 ObjPlayer.pas:12728: MakeDefaultMsg(10325, MakeIndex, nWhere, IsHero<<1|IsBoxItem, 值),
    //  即 Recog=MakeIndex、Param=nWhere(背包为 -1)、Tag=标志位、Series=新值(原版 ClMain.pas:44630)。
    //  英雄和仓库页的物品客户端不建模,必须按标志位跳过,否则同一条包会把耐久写到别的容器上。
    // ============================================================
    private void HandleUpdateItemDura(MirServerPacket pkt, bool setMax)
    {
        ushort flags = pkt.Header.Tag;
        if ((flags & 3) != 0) return;
        int makeIdx = pkt.Header.RecogI;
        int value = pkt.Header.Series;
        if (makeIdx == 0) return;

        int where = unchecked((short)pkt.Header.Param);
        if (where >= 0 && where < UseItems.Count && UseItems[where].MakeIndex == makeIdx)
        {
            var ui = UseItems[where];
            if (setMax) ui.DuraMax = value; else ui.DuraCount = value;
            UseItems[where] = ui;
            EquipChanged?.Invoke();
            return;
        }
        int idx = BagItems.FindIndex(i => i.MakeIndex == makeIdx);
        if (idx < 0) return;
        var bi = BagItems[idx];
        if (setMax) bi.DuraMax = value; else bi.DuraCount = value;
        if (bi.Stackable && !setMax) bi.Count = bi.DuraCount + 1;   // 叠加物的 Dura 就是"件数-1"
        BagItems[idx] = bi;
        ItemsChanged?.Invoke();
    }

    //  SM_EXT_BAG_COUNT_CHANGE (10409) — NPC 脚本开通背包扩展格
    //  服务端: SendDefMessage(10409, 0, m_btExtBagPageCount, m_btExtBagOpenItemCount, 0, '')(NpcActionCmd.pas:41427)
    //  原版客户端: g_ExtBagOpenItemCount := DefMsg.Tag,格数上限 = 46 + Tag(ClMain.pas:35117 / MShare.pas:11762)
    private void HandleExtBagCount(MirServerPacket pkt)
    {
        MaxBagCount = Grobal2.DEF_MAX_BAG_ITEM + pkt.Header.Tag;
        EmitLog($"[bag] 背包格上限 {MaxBagCount}(已有 {BagItems.Count} 件)");
        ItemsChanged?.Invoke();
    }

    // ============================================================
    //  SM_SENDMYMAGIC (211) — 技能列表
    //  服务端: MakeDefaultMsg(SM_SENDMYMAGIC, 0, 0, 0, 魔法数), Body=魔法描述
    // ============================================================
    public readonly List<string> MyMagicList = new();
    /// <summary>技能ID → Magic.DB 的 Delay(ms)。服务端限速按它算,见 BotCombatAI.MagicHitIntervalMs。
    /// 收包线程整表 Clear+重填(SM_SENDMYMAGIC 会重复来),施法判定在 AI 线程每轮读 ⇒ 用并发字典。</summary>
    public readonly ConcurrentDictionary<int, int> MagicDelayMs = new();
    private void HandleSendMyMagic(MirServerPacket pkt)
    {
        MyMagicList.Clear();
        MagicDelayMs.Clear();
        // gxx: zlib(nC × TClientMagic),Series=nC(ObjPlayer.pas:10233)。
        // 元素仍按老客户端约定输出 "技能名/技能ID",上层按此解析。
        byte[] body = pkt.BodyInflated;
        int n = pkt.Header.Series;
        if (body.Length > 0 && n > 0 && body.Length % n == 0)
        {
            int stride = body.Length / n;
            for (int off = 0; off + stride <= body.Length; off += stride)
            {
                var m = GxxPayload.ReadClientMagic(body.AsSpan(off, stride));
                if (m == null) continue;
                MyMagicList.Add($"{m.Value.Name}/{m.Value.MagicId}");
                if (m.Value.DelayMs >= 0) MagicDelayMs[m.Value.MagicId] = m.Value.DelayMs;
            }
        }
        else if (body.Length > 0)
        {
            BotLog.Warn($"[magic] SM_SENDMYMAGIC 包体 {body.Length}B 无法按 nCount={n} 切分");
        }
        // Delay 打出来是为了真机能一眼看出记录布局解对了没:错的话这里是乱数(会被当成未知)或全空。
        string delays = MagicDelayMs.Count > 0
            ? "  Delay(ms): " + string.Join(",", MagicDelayMs.Select(kv => $"{kv.Key}={kv.Value}"))
            : "";
        EmitLog($"[magic] 技能列表 {MyMagicList.Count} 个: {string.Join(",", MyMagicList)}{delays}");
        StateChanged?.Invoke();
    }

    // ============================================================
    //  SM_MAGIC_LVEXP (640) — 技能经验变化
    // ============================================================
    private void HandleMagicLvExp(MirServerPacket pkt)
    {
        BotLog.Info($"[magic] SM_MAGIC_LVEXP Recog={pkt.Header.Recog} Param={pkt.Header.Param} Tag={pkt.Header.Tag} Series={pkt.Header.Series}");
    }

    // ============================================================
    //  SM_OPENDOOR_OK (612) / SM_OPENDOOR_LOCK (613) / SM_CLOSEDOOR (614) — 门状态
    // ============================================================
    private void HandleDoor(MirServerPacket pkt, string action)
    {
        BotLog.Info($"[door] 门{action} nX={pkt.Header.Param} nY={pkt.Header.Tag}");
        EmitLog($"[door] 门{action} ({pkt.Header.Param},{pkt.Header.Tag})");
    }

    // ============================================================
    //  SM_SYSMESSAGE (100) — 系统消息
    // ============================================================
    public event Action<string>? SystemMessage;
    private void HandleSysMessage(MirServerPacket pkt)
    {
        string text = pkt.BodyGbk;
        EmitLog($"[sys] {text}");
        TryRequestUnlock(text);
        SystemMessage?.Invoke(text);
    }

    // ============================================================
    //  登录即被"动作保护"锁住 → 自动补发解锁命令
    //  ObjPlayer.pas:9010-9031: boPasswordLockSystem 且角色设过密码(m_boPasswordLocked)时,
    //  登录过程会发一条 SM_SYSMESSAGE(100) = String.ini [String] ActionIsLockedMsg
    //  + " 开锁命令: @" + UnLock 命令名,同时把 m_boCanWalk/Run/Hit/Spell/UseItem/Deal/Drop
    //  全置 False。此后我方上行包被服务端**整包丢弃且没有任何错误回包**(UsrEngn.pas
    //  ClientWalk/ClientHit 直接 Exit),AI 只会原地空转,用户看到的就是一行红字提示。
    //  命令名是服务端 Command.ini 里可改的(本机 PasswordUnLock=开锁),所以从提示文本里取。
    //  发出后服务端 → RM_PASSWORD → SM_PASSWORD,接手的是已有的密码输入框链路。
    // ============================================================
    private const int UnlockCmdDebounceMs = 15000;
    private DateTime _lastUnlockCmdUtc = DateTime.MinValue;

    private void TryRequestUnlock(string sysText)
    {
        const string marker = "开锁命令: @";
        int at = sysText.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return;

        // '@' 开头的那一段就是命令名:提示语以它结尾,后面即使还有文字也只取到空格为止
        int start = at + marker.Length - 1;
        int end = start;
        while (end < sysText.Length && !char.IsWhiteSpace(sysText[end])) end++;
        string command = sysText.Substring(start, end - start);
        if (command.Length == 0) return;

        DateTime now = DateTime.UtcNow;
        if ((now - _lastUnlockCmdUtc).TotalMilliseconds < UnlockCmdDebounceMs) return;
        _lastUnlockCmdUtc = now;

        SafeLog($"[password] 角色被动作保护锁定,自动发送 {command} 请求解锁(随后服务端会弹密码框)");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300).ConfigureAwait(false);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await SendChatAsync(command, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                BotLog.Error($"[password] 发送解锁命令失败: {ex.Message}");
                SafeLog($"[password] 自动解锁命令发送失败,请在聊天框手动输入 {command}");
            }
        });
    }

    // ============================================================
    //  SM_MENU_OK (767) — NPC 脚本菜单结果/提示文本
    //  点击 NPC 选项后服务端返回脚本执行结果,如"等级达到11级才可以进入庄园"。
    //  原版客户端把它当作聊天提示显示;BotClient 之前未处理导致用户看不到任何反馈。
    // ============================================================
    private void HandleMenuOk(MirServerPacket pkt)
    {
        string text = EdCode.DecodeString(pkt.BodyEncoded);
        EmitLog($"[npc] 提示: {text}");
        SystemMessage?.Invoke(text);
    }

    // ============================================================
    //  辅助方法
    // ============================================================

    /// <summary>从Feature中推断Dot类型。
    /// 参考原版客户端(MerchantDialogSystem.TryPickMerchantNpcAtCell): 唯一判定是
    ///   FeatureCodec.Race(feature) == RCC_MERCHANT(50) → 商人/可对话NPC;
    ///   其他 race 一律按怪物处理(玩家 race=0 除外)。
    /// 注意: 不能用 wAppr 区分商人与动物 —— LocalDB.LoadNpcs 会用 Npcs.txt 配置覆盖
    ///   商人的 m_wAppr(任意外观编号), 而动物的 wAppr 也是数据库外观编号, 两者数值重叠。
    /// 实测(2026-08-11 日志): 商人 race=50、卫士 race=12、动物(鸡/鹿/羊) race=11。
    ///   race=11 恰好等于 RC_GUARD 常量值, 但本服动物的数据库 RaceImg=11, 必须判为怪物;
    ///   守卫实际发送 race=12(RCC_GUARD), 判为 NPC。</summary>
    private static DotKind ClassifyFeature(int feature)
    {
        byte raceImg = (byte)(feature & 0xFF);
        if (raceImg == Grobal2.RC_PLAYOBJECT || raceImg == Grobal2.RC_HEROOBJECT)
            return DotKind.Player;
        if (raceImg == Grobal2.RCC_MERCHANT)
            return DotKind.Npc;
        return raceImg switch
        {
            // 注意: 不含 RC_GUARD(11) —— 本服动物的数据库 RaceImg=11, 应判怪物
            Grobal2.RC_NPC or Grobal2.RCC_GUARD or Grobal2.RC_PEACENPC
                or Grobal2.RC_ARCHERGUARD => DotKind.Npc,
            _ => DotKind.Monster
        };
    }

    /// <summary>从Body尾部提取名字(二进制CharDesc后跟的名字字符串)</summary>
    private static string ExtractNameFromBodyTail(string bodyEncoded, int structEncodedLen)
    {
        if (bodyEncoded.Length <= structEncodedLen) return string.Empty;
        string tail = bodyEncoded[structEncodedLen..];
        string decoded = EdCode.DecodeString(tail);
        // 名字可能包含 '/' 分隔的颜色等信息
        int slash = decoded.IndexOf('/');
        if (slash >= 0) decoded = decoded[..slash];
        return decoded.Trim();
    }

    // ============================================================
    //  背包查询(CM_QUERYBAGITEMS)
    // ============================================================

    /// <summary>服务端自己也在节流这条查询:!Setup.txt QueryBagItemsTime=3(秒) ⇒ 距上次被受理
    /// 不足窗口的查询,ObjPlayer.pas:26185 整包丢弃且**一个回包都不发**,客户端只会看到列表停在
    /// 旧数据上。穿戴/交易/仓库连续操作时最容易撞上(连存 5 件物品 = 4 条刷新被吞),
    /// 所以这里顺延到窗口外再发,而不是丢掉。多留 200ms 是因为服务端判的是 `> 窗口`。
    /// 计时用我方上次发送时刻:服务端只在接受时才更新 m_dwQueryBagItemsTick,两者等价。</summary>
    public int BagQueryWindowMs { get; set; } = 3200;
    /// <summary>同一批请求的并窗时间:请求先到、服务端回包后到时,连续几次操作会挤在同一瞬间,
    /// 等一下让它们合并成一条查询(状态改动都在服务端先落地,所以合并后那次查询一定看得到)。</summary>
    private const int BagQueryCoalesceMs = 60;
    private DateTime _lastBagQueryUtc = DateTime.MinValue;
    private bool _bagQueryPending;
    private Task _bagQueryLoop = Task.CompletedTask;
    private readonly object _bagQueryLock = new();

    /// <summary>请求刷新背包(进世界/换图/穿戴/交易/仓库之后都要发一次)。
    /// 同一瞬间的请求合并成一条;窗口内的新请求顺延到窗口外补发,保证最后一次改动的状态一定被拉到。</summary>
    public Task QueryBagItemsAsync(string why)
    {
        Task loop;
        lock (_bagQueryLock)
        {
            _bagQueryPending = true;
            if (!_bagQueryLoop.IsCompleted) return _bagQueryLoop;
            BotLog.Info($"[runtime] 请求背包刷新 why={why}");
            loop = _bagQueryLoop = Task.Run(BagQueryLoopAsync);
        }
        return loop;
    }

    private async Task BagQueryLoopAsync()
    {
        while (true)
        {
            int wait;
            lock (_bagQueryLock)
            {
                if (!_bagQueryPending) return;
                // 登录时 _lastBagQueryUtc 被有意重置成 DateTime.MinValue(让首条查询立刻放行),
                // 所以这里的差值是 7.9e14 毫秒 —— 直接 (int) 强转会溢出,必须先按 double 判窗口。
                double elapsedMs = (DateTime.UtcNow - _lastBagQueryUtc).TotalMilliseconds;
                wait = elapsedMs >= BagQueryWindowMs ? BagQueryCoalesceMs : BagQueryWindowMs - (int)elapsedMs;
                if (wait <= 0) wait = BagQueryCoalesceMs;
            }
            await Task.Delay(wait).ConfigureAwait(false);
            bool go;
            lock (_bagQueryLock)
            {
                go = _bagQueryPending;
                if (go) { _bagQueryPending = false; _lastBagQueryUtc = DateTime.UtcNow; }
            }
            if (!go) continue;
            if (!await SendBagQueryAsync().ConfigureAwait(false)) return;
        }
    }

    private async Task<bool> SendBagQueryAsync()
    {
        // 原版客户端 SM_LOGON 处理里只发这一条: ClMain.pas:24441
        // SendClientMessage(CM_QUERYBAGITEMS, 0, MakeWord(0, FCurrentBagPage), 0, 0, '')
        // (CM_QUERYUSERSTATE 是"查看他人状态",Recog 必须填对方 ID;CM_WANTVIEWRANGE 在 gxx 不存在)
        var qBag = CmdPack.MakeDefaultMsg(Grobal2.CM_QUERYBAGITEMS, 0, CmdPack.MakeWord(0, 0), 0, 0);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _session.SendPayloadAsync(EdCode.EncodeMessage(qBag), cts.Token).ConfigureAwait(false);
            lock (_bagQueryLock) _lastBagQueryUtc = DateTime.UtcNow;
            EmitLog("[runtime] -> CM_QUERYBAGITEMS");
            return true;
        }
        catch (Exception ex)
        {
            BotLog.Warn($"[runtime] CM_QUERYBAGITEMS 发送失败: {ex.Message}");
            SafeLog($"[runtime] 背包刷新失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>延迟一点再请求刷新:进世界时服务端连着推 SM_LOGON/SM_NEWMAP,抢在这两步落地前查询没意义。</summary>
    private void RequestBagRefresh(int delayMs, string why)
    {
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delayMs); }
            catch (Exception ex) { BotLog.Error($"[{why}] 初始查询延时异常: {ex.Message}"); return; }
            await QueryBagItemsAsync(why);
        });
    }

    // ============================================================
    //  玩家操作(UI / AI 调用)
    // ============================================================

    /// <summary>点击寻路规划结果:规划成功传路径(含起点终点),失败/取消传空列表。</summary>
    public event Action<IReadOnlyList<(int X, int Y)>>? WalkPathChanged;

    // ==================================================================================
    //  C 方案（客户端驱动）分流辅助 · 见 ClientDriver/CorePatch.md
    //  Driver 为 null 或未 Attached 时，下面每处分流都自然短路 —— 行为与改造前逐字一致。
    // ==================================================================================

    /// <summary>把动作意图交给真机操作通道；返回 true 表示调用方应就此收工（本次不写任何字节）。</summary>
    private async Task<bool> DispatchToDriverAsync(ClientActionIntent intent, CancellationToken ct)
    {
        if (_session.Driver is not { IsAttached: true } drv) return false;
        await drv.DispatchAsync(intent, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>按对象 ID 取当前视野格坐标。NPC 交互/菜单选择只带 ID，而驱动层点的是格子。</summary>
    private (int X, int Y) FindDotPos(long id)
        => Dots.TryGetValue(id, out var d) ? (d.X, d.Y) : (-1, -1);

    /// <summary>把协议命令码映射成动作分类，用于 SendActionAsync 的兜底分流。</summary>
    private static ClientActionKind CmdToActionKind(ushort cmd) => cmd switch
    {
        Grobal2.CM_WALK => ClientActionKind.Walk,
        Grobal2.CM_TURN => ClientActionKind.Turn,
        Grobal2.CM_HIT => ClientActionKind.Hit,
        Grobal2.CM_SPELL => ClientActionKind.Spell,
        Grobal2.CM_EAT => ClientActionKind.Eat,
        Grobal2.CM_PICKUP => ClientActionKind.Pickup,
        Grobal2.CM_CLICKNPC => ClientActionKind.NpcInteract,
        Grobal2.CM_MERCHANTDLGSELECT => ClientActionKind.DialogSelect,
        _ => ClientActionKind.Other,
    };

    /// <summary>统一动作发送。原版 ClMain.pas:17027 的 MakeDefaultMsg(ident, target, X, dir, Y)
    /// 即 Recog=目标对象ID(走路/转向为 0)、Param=X、Tag=方向、Series=Y。
    /// 服务端 ClientWalkXY/ClientRunXY 从 Param/Series 取坐标,从 Tag 取方向。</summary>
    public async Task SendActionAsync(ushort cmd, int x, int y, byte dir, CancellationToken ct, long targetId = 0)
    {
        // ===== C 方案兜底分流：上层 SendWalk/SendTurn/SendHit 已各自分流，
        // 这里接住的是"直调本方法的漏网动作"（脚本或其它调用点）。
        if (await DispatchToDriverAsync(new ClientActionIntent
            {
                Kind = CmdToActionKind(cmd),
                X = x, Y = y, Dir = dir, TargetId = (int)targetId,
                RawCommand = cmd,
            }, ct).ConfigureAwait(false)) return;

        var msg = CmdPack.MakeDefaultMsg(cmd, targetId, (ushort)x, dir, (ushort)y);
        string payload = EdCode.EncodeMessage(msg);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>死亡状态的上行守卫:服务端对 m_boDeath 的走路/攻击/施法整包丢弃、零回包
    /// (ObjPlayer.pas:17476/18638/19028)。照发不误的害处不只是白费 —— SendWalkAsync 会乐观更新
    /// 本地坐标,死人"走"几步之后本地坐标就和服务端差出去,复活后每步都被坐标校验拒掉。
    /// 返回 true 表示这条上行已经被拦下并记了一行(手动点出来的动作才会走到这里,不会刷屏)。</summary>
    private bool RefuseWhileDead(string what)
    {
        if (!SelfIsDead) return false;
        EmitLog($"[{what}] 取消:角色已死亡,服务端零回包丢弃 —— 只能等服务端复活" +
                "(脚本 NPC/GM/复活术),或断开重连:HP<=0 时服务端会在家点把角色以 14 HP 拉起");
        return true;
    }

    public async Task SendWalkAsync(int x, int y, byte dir, CancellationToken ct)
    {
        if (RefuseWhileDead("walk")) return;

        // ===== C 方案分流 =====
        // 关键点：这里 return 之后**不再做乐观坐标更新**。C 模式下角色走没走只有一个证据 ——
        // 服务端广播（嗅探注入）。本地先改成目标格会让 MapWalkController 误判"已到"而停止推进。
        if (await DispatchToDriverAsync(new ClientActionIntent
            {
                Kind = ClientActionKind.Walk, X = x, Y = y, Dir = dir,
                RawCommand = Grobal2.CM_WALK,
            }, ct).ConfigureAwait(false)) return;
        // 服务端走步成功后不给玩家本人发 SM_POSTIONMOVE/SM_WALK 回包,
        // 因此客户端必须乐观更新本地坐标(原版客户端同样做本地预测)。
        // 服务端拒绝这一步时(超速/格子被占/状态不允许)回的是 SM_ACTION_RET
        // Param=0+Tag=1,由 HandleActionRet 把坐标按服务端真实值回正。
        await SendActionAsync(Grobal2.CM_WALK, x, y, dir, ct);
        Player.PosX = x;
        Player.PosY = y;
        Player.Direction = dir;
        StateChanged?.Invoke();
    }

    /// <summary>传送门那一类 NPC 外观可以穿过去(Envir.pas:2021)。</summary>
    private static bool IsGateAppearance(int feature)
        => (feature >= 54 && feature <= 58) || (feature >= 94 && feature <= 98);

    /// <summary>脚本 穿怪开启/穿人开启:寻路时是否还把视野里的怪/玩家算成障碍。
    /// 默认 false 才符合服务端规则(MoveToMovingObject:活对象占一格就不让走);开着的时候我方会照发 CM_WALK,
    /// 服务端不认就靠 SM_ACTION_RET(110) 把坐标拉回真值。天骥文档的说法一致:
    /// "此功能只有在开放了穿怪的服务器上才能使用,否则会引发频繁的走路错误"。</summary>
    public bool WalkThroughMonsters { get; set; }
    public bool WalkThroughPlayers { get; set; }

    /// <summary>这个视野对象会不会挡住一格(照 MoveToMovingObject:2009-2060 的例外列:
    /// 我方自己、无实体宠物(我们不分得清,按会挡处理=偏保守)、传送门外观的 NPC、尸体)。</summary>
    private bool BlocksCell(MapDot d)
    {
        if (d.Id == MyRecogId) return false;
        if (d.Kind == DotKind.Monster && WalkThroughMonsters) return false;
        if (d.Kind == DotKind.Player && WalkThroughPlayers) return false;
        if (d.Kind != DotKind.Monster && d.Kind != DotKind.Player && d.Kind != DotKind.Npc) return false;
        if (d.Kind == DotKind.Npc && IsGateAppearance(d.Feature)) return false;
        if (ObjectHpMap.TryGetValue(d.Id, out var hp) && hp.Hp <= 0) return false;      // 尸体不挡路
        return true;
    }

    /// <summary>当场再看一眼这格有没有被对象占住(寻路快照之后对象还会移动)。</summary>
    private bool CellBlockedNow(int x, int y)
    {
        foreach (var d in Dots.Values)
            if (d.X == x && d.Y == y && BlocksCell(d)) return true;
        return false;
    }

    /// <summary>把静态通行性和"视野里站着的对象"合成一张寻路用的地图。
    /// 服务端 TEnvirnoment.MoveToMovingObject(Envir.pas:2000-2068)在目标格只要有一个活着的 Actor
    /// (怪/玩家/NPC;传送门外观、无实体宠物、我方自己除外)就返回 False,
    /// 而 TBaseObject.WalkTo(:13642)拿 False 时**连坐标都不改、也不给移动者任何回包**
    /// (Walk 的 else 分支 ObjPlayer.pas:17598 只把超速计数清零)。
    /// 我方坐标是乐观更新的,撞一次就永久比服务端超前,之后所有按坐标的判断
    /// (攻击距离、NPC 15 格、拾取踩格)全部跟着错 —— 所以挡路对象必须算成障碍。</summary>
    public Func<int, int, bool> EffectiveWalkable(Func<int, int, bool>? staticWalkable)
    {
        var blocked = new HashSet<(int, int)>();
        foreach (var d in Dots.Values)
            if (BlocksCell(d)) blocked.Add((d.X, d.Y));
        int sx = Player.PosX, sy = Player.PosY;
        return (x, y) =>
        {
            if (x == sx && y == sy) return staticWalkable == null || staticWalkable(x, y);  // 自己脚下永远可走
            // 没有地图数据时按"可走"放行:服务端拒绝一步会回 SM_ACTION_RET 把坐标拉回真值,
            // 而自己判成"全不可走"是让 bot 原地永久不动、一个包都不发(没有任何反馈可自愈)。
            bool ok = staticWalkable == null || staticWalkable(x, y);
            return ok && !blocked.Contains((x, y));
        };
    }

    /// <summary>
    /// 多步寻路移动到 (targetX,targetY)。
    /// 服务端 CM_WALK 是单步移动(WalkTo 只按方向走 1 格),
    /// 因此客户端先用 BFS 算出完整路径,再逐格发 CM_WALK。
    /// stepDelayMs 为每步最小间隔,必须大于服务端 !Setup.txt 的 WalkIntervalTime
    /// (本机 490ms;SpeedControl 默认开,快于该值的 CM_WALK 会被静默丢弃,
    ///  累计超速还会触发 SendSocketStatusFail 把我方坐标拉回)。
    /// </summary>
    public async Task<bool> WalkToAsync(
        int targetX,
        int targetY,
        Func<int, int, bool> isWalkable,
        int stepDelayMs,
        CancellationToken ct)
    {
        // 寻路统一走"静态地图 + 视野对象"的合成通行性,见 EffectiveWalkable 的说明。
        Func<int, int, bool> statWalk = isWalkable;
        isWalkable = EffectiveWalkable(statWalk);
        int sx = Player.PosX;
        int sy = Player.PosY;
        int mw = PathfindWidth;
        int mh = PathfindHeight;
        BotLog.Info($"[move] WalkToAsync 起点=({sx},{sy}) 目标=({targetX},{targetY}) 地图={MapWidth}x{MapHeight}" +
                    (MapWidth > 0 ? "" : "(无地图数据,按未知尺寸寻路)"));
        if ((uint)targetX >= (uint)mw || (uint)targetY >= (uint)mh)
        {
            WalkPathChanged?.Invoke(Array.Empty<(int, int)>());
            return false;
        }
        if (sx == targetX && sy == targetY)
        {
            WalkPathChanged?.Invoke(Array.Empty<(int, int)>());
            return true;
        }

        // 点击目标不可走时,就近吸附到可走格子(最多 3 格),与原版客户端行为一致
        if (!isWalkable(targetX, targetY))
        {
            int originX = targetX, originY = targetY;
            bool snapped = false;
            for (int r = 1; r <= 3 && !snapped; r++)
            {
                for (int dy = -r; dy <= r && !snapped; dy++)
                {
                    for (int dx = -r; dx <= r && !snapped; dx++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                        int nx = targetX + dx;
                        int ny = targetY + dy;
                        if ((uint)nx >= (uint)mw || (uint)ny >= (uint)mh) continue;
                        if (!isWalkable(nx, ny)) continue;
                        targetX = nx;
                        targetY = ny;
                        snapped = true;
                    }
                }
            }
            if (snapped)
            {
                BotLog.Info($"[move] 目标 ({originX},{originY}) 不可走,吸附到 ({targetX},{targetY})");
                EmitLog($"[move] 目标 ({originX},{originY}) 不可走,吸附到 ({targetX},{targetY})");
            }
            else
            {
                BotLog.Info($"[move] 目标 ({originX},{originY}) 附近 3 格内无可走格子,放弃");
                EmitLog($"[move] 目标 ({originX},{originY}) 附近 3 格内无可走格子,放弃");
            }
        }
        if (sx == targetX && sy == targetY)
        {
            WalkPathChanged?.Invoke(Array.Empty<(int, int)>());
            return true;
        }

        int replans = 0;
        long corrSeen = Volatile.Read(ref _posCorrectedSeq);
        while (true)
        {
            var path = BotPathFinder.FindPath(sx, sy, targetX, targetY, mw, mh, isWalkable);
            if (path == null || path.Count <= 1)
            {
                BotLog.Info($"[move] 寻路失败 ({sx},{sy})->({targetX},{targetY})");
                EmitLog($"[move] 寻路失败 ({sx},{sy})->({targetX},{targetY})");
                WalkPathChanged?.Invoke(Array.Empty<(int, int)>());
                return false;
            }

            // 通知 UI 画出规划路线(虚线)
            WalkPathChanged?.Invoke(path);
            EmitLog($"[move] 寻路成功 {path.Count} 步,开始走到 ({targetX},{targetY})");

            // path[0] 是起点,从 path[1] 开始走;每步乐观更新本地坐标并间隔 stepDelayMs
            bool replanned = false;
            for (int i = 1; i < path.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    WalkPathChanged?.Invoke(Array.Empty<(int, int)>());
                    return false;
                }
                // 服务端把某一步判失败并用 SM_ACTION_RET 回正了坐标(超速/格子被占/冻结…),
                // 这条路径的起点假设已经不成立 —— 从真实坐标重新规划,别接着照着旧路径发。
                long corrNow = Volatile.Read(ref _posCorrectedSeq);
                if (corrNow != corrSeen)
                {
                    corrSeen = corrNow;
                    sx = Player.PosX;
                    sy = Player.PosY;
                    if (++replans > 12)
                    {
                        EmitLog($"[move] 服务端反复回正坐标,放弃走到 ({targetX},{targetY})");
                        WalkPathChanged?.Invoke(Array.Empty<(int, int)>());
                        return false;
                    }
                    BotLog.Info($"[move] 坐标被服务端回正到 ({sx},{sy}),重新规划");
                    isWalkable = EffectiveWalkable(statWalk);
                    replanned = true;
                    break;
                }
                var (nx, ny) = path[i];
                // 规划之后对象还会挪进来(怪追着我们走就是这种情形)。服务端对已被占用的目标格
                // 连坐标都不改、也不回任何包,硬发只是把本地坐标往错的方向推 —— 当场重规划。
                if (i > 1 && CellBlockedNow(nx, ny))
                {
                    if (++replans > 12)
                    {
                        EmitLog($"[move] 下一步 ({nx},{ny}) 反复被移动对象挡死,放弃走到 ({targetX},{targetY})");
                        WalkPathChanged?.Invoke(Array.Empty<(int, int)>());
                        return false;
                    }
                    BotLog.Info($"[move] 第 {i} 步格子 ({nx},{ny}) 已被占,从 ({sx},{sy}) 重新规划");
                    isWalkable = EffectiveWalkable(statWalk);
                    replanned = true;
                    break;
                }
                byte dir = BotCombatAI.GetDirection(sx, sy, nx, ny);
                await SendWalkAsync(nx, ny, dir, ct).ConfigureAwait(false);
                sx = nx;
                sy = ny;
                // 每走一步,把"剩余路线"(当前位置到终点)重新通知 UI,
                // 让虚线始终反映实际正在执行的剩余规划,而不是初始全路径。
                var remaining = path.GetRange(i, path.Count - i);
                // remaining[0] 是刚刚抵达的当前位置,作为虚线起点
                WalkPathChanged?.Invoke(remaining);
                if (stepDelayMs > 0 && i < path.Count - 1)
                    await Task.Delay(stepDelayMs, ct).ConfigureAwait(false);
            }
            if (!replanned || (sx == targetX && sy == targetY)) break;
        }
        // 走完清除规划线
        WalkPathChanged?.Invoke(Array.Empty<(int, int)>());
        return true;
    }

    public async Task SendTurnAsync(byte dir, CancellationToken ct)
    {
        // ===== C 方案分流（同样跳过本地的 Player.Direction 更新）=====
        if (await DispatchToDriverAsync(new ClientActionIntent
            {
                Kind = ClientActionKind.Turn, X = Player.PosX, Y = Player.PosY, Dir = dir,
                RawCommand = Grobal2.CM_TURN,
            }, ct).ConfigureAwait(false)) return;

        await SendActionAsync(Grobal2.CM_TURN, Player.PosX, Player.PosY, dir, ct);
        Player.Direction = dir;
        StateChanged?.Invoke();
    }

    public async Task SendHitAsync(long targetId, int targetX, int targetY, byte dir, CancellationToken ct)
    {
        if (RefuseWhileDead("hit")) return;

        // ===== C 方案分流：攻击 = 点目标所在那一格 =====
        if (await DispatchToDriverAsync(new ClientActionIntent
            {
                Kind = ClientActionKind.Hit, X = Player.PosX, Y = Player.PosY, Dir = dir, TargetId = (int)targetId,
                RawCommand = Grobal2.CM_HIT,
            }, ct).ConfigureAwait(false)) return;
        // 原版 Actor.pas:4850 发 CM_HIT 时 Recog=效果等级(普攻 0),Param/Series=自身坐标,Tag=朝向,
        // 服务端 ClientHitXY 校验 nX==m_nCurrX && nY==m_nCurrY 后按方向打面前的目标,不吃目标对象ID。
        await SendActionAsync(Grobal2.CM_HIT, Player.PosX, Player.PosY, dir, ct);
    }

    /// <summary>兼容旧调用：不指定法术槽（驱动层按 magicId 兜底，等价于改造前行为）。</summary>
    public Task SendSpellAsync(long targetId, int targetX, int targetY, ushort magicId, CancellationToken ct)
        => SendSpellAsync(targetId, targetX, targetY, magicId, -1, ct);

    /// <summary>
    /// 施法。C 方案下先由驱动层按快捷键选中法术槽，再点目标格。
    /// </summary>
    /// <param name="spellSlot">
    /// 法术槽下标（0 = F1）。由技能循环给出；&lt;0 时驱动层退回"按 magicId 当槽位"的旧行为。
    /// </param>
    public async Task SendSpellAsync(long targetId, int targetX, int targetY, ushort magicId, int spellSlot, CancellationToken ct)
    {
        if (RefuseWhileDead("spell")) return;

        // ===== C 方案分流：先按快捷键选法术槽（Extra），再点目标格（X/Y） =====
        if (await DispatchToDriverAsync(new ClientActionIntent
            {
                Kind = ClientActionKind.Spell, X = targetX, Y = targetY, Param = magicId, Extra = spellSlot,
                TargetId = (int)targetId,
                RawCommand = Grobal2.CM_SPELL,
            }, ct).ConfigureAwait(false)) return;
        // 原版 ClMain.pas:17067 SendSpellMsg: MakeDefaultMsg(CM_SPELL, target, X, dir=魔法ID, Y)
        // 即 Recog=目标对象指针(64 位原样回传)、Param=目标X、Tag=魔法ID、Series=目标Y。
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_SPELL, targetId, (ushort)targetX, magicId, (ushort)targetY);
        string payload = EdCode.EncodeMessage(msg);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    public async Task SendChatAsync(string text, CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_SAY, 0, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(text);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    // ============================================================
    //  SM_PASSWORD (1105) —— 服务端要密码框(仓库/动作保护)
    //  本机 !Setup.txt PasswordLockSystem=1 且 Lock*Action 全 1:角色设过密码就登录上锁,
    //  m_boCanWalk/Run/Hit/Spell/UseItem/Deal/Drop 全 False,所有动作被**整包丢弃**(不排队)。
    //  流程:玩家打 @开锁(Command.ini PasswordUnLock=开锁)→ HandleCommands.pas:7749 发 RM_PASSWORD
    //  → ObjPlayer.pas ServerSendPassword 发 SM_PASSWORD(全 0 包头,无包体)→ 客户端弹输入框
    //  → 答案用 CM_PASSWORD(Param=1,包体=密码)回(UsrEngn.pas:5721 把 Param 当 wParam,
    //  ObjPlayer.pas:25831 wParam=0 会被当成"重新问一遍")。
    // ============================================================
    private void HandlePasswordRequest(MirServerPacket pkt)
    {
        BotLog.Info("[password] 服务端索要密码(仓库/动作保护),需要输入密码才能解锁");
        EmitLog("[password] 服务端弹出密码框:请输入仓库密码解锁(否则行走/攻击/喝药都会被静默丢弃)");
        PasswordRequested?.Invoke();
    }

    /// <summary>回答 SM_PASSWORD 的密码框。Param 必须非 0(=1),否则服务端当成"再问一遍"而不是答案。</summary>
    public async Task SendPasswordAsync(string password, CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_PASSWORD, 0, 1, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(password);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>拾取(CM_PICKUP)。原版:MakeDefaultMsg(CM_PICKUP, 0, 本人X, 本人Y, 0)(ClMain.pas:17186)。
    /// 服务端 ObjPlayer.pas:20472 第一件事就是拿包里的 X/Y 和 m_nCurrX/Y 比,不等整包丢弃 ——
    /// 这一条被拒时连 SM_ACTION_RET 都不回,是全部静默闸门里最难发现的一种,所以在这里拦住。</summary>
    public async Task SendPickupAsync(int mapX, int mapY, CancellationToken ct)
    {
        if (mapX != Player.PosX || mapY != Player.PosY)
        {
            EmitLog($"[pickup] 取消:包必须带本人当前格({Player.PosX},{Player.PosY})," +
                    $"不是物品格({mapX},{mapY}) —— 服务端按坐标整包丢弃且零回包,先走到那一格再拾");
            return;
        }
        //  满仓/超重也同样是零回包(见上面的"背包容量闸门"),但金币走的是 IncGold 那条分支,不受影响。
        if (!IsGoldOnTile(mapX, mapY))
        {
            if (BagIsFull)
            {
                EmitLog($"[pickup] 取消:背包 {BagItems.Count}/{MaxBagCount} 格已满," +
                        "服务端会整包丢弃且零回包 —— 先卖掉或存仓库再捡");
                return;
            }
            if (WeightIsMaxed)
            {
                EmitLog($"[pickup] 取消:负重 {Player.Weight}/{Player.MaxWeight} 已到顶," +
                        "服务端会整包丢弃且零回包 —— 先丢掉或存仓库里的重物(金币不受影响)");
                return;
            }
        }
        // ===== C 方案分流：闸门全部通过后才轮到"点那一格" =====
        if (await DispatchToDriverAsync(new ClientActionIntent
            {
                Kind = ClientActionKind.Pickup, X = mapX, Y = mapY,
                RawCommand = Grobal2.CM_PICKUP,
            }, ct).ConfigureAwait(false)) return;

        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_PICKUP, 0, (ushort)mapX, (ushort)mapY, 0);
        string payload = EdCode.EncodeMessage(msg);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    // ============================================================
    //  NPC 距离闸门
    //  服务端每一条 NPC/商店/仓库/修理上行处理函数开头都有同图 + 距离判断,不满足直接 Exit:
    //    ClientClickNpc        :22519  abs(dx)<=15 and abs(dy)<=15
    //    ClientMerchantDlgSelect :22555 abs(dx)<15
    //    ClientUserBuyItem     :22760  abs(dx)>15 → Exit   (明细 :22785 同)
    //    ClientMerchantQuerySellPrice :22648  abs(dx)<15
    //    ClientUserSellItem    :22679  abs(dx)<15
    //    ClientUserRepairItem  :23119  abs(dx)<15
    //    ClientUserStorageItem :23560  abs(dx)<15 (g_FunctionNPC/g_MissionNPC 例外)
    //    ClientUserTakebackStorageItem :23743 同上
    //  全部是**整包丢弃且零回包**,和背包查询的 3 秒窗口同一类缺陷:界面点了没反应,也没有错误。
    // ============================================================

    /// <summary>服务端最严的一档是 `abs < 15`,所以距离 ≥15 的包必定被丢。</summary>
    public const int NpcRangeTiles = 15;

    /// <summary>玩家到视野对象的切比雪夫距离(服务端 abs 两轴分别判,切比雪夫等价)。
    /// 对象不在视野表里(未收到 SM_SHOW / 已被 SM_DISAPPEAR 清掉)时返回 false。</summary>
    public bool TryGetViewDistance(long objId, out int dist)
    {
        dist = 0;
        if (!Dots.TryGetValue(objId, out var dot)) return false;
        dist = Math.Max(Math.Abs(dot.X - Player.PosX), Math.Abs(dot.Y - Player.PosY));
        return true;
    }

    /// <summary>NPC 请求前的距离闸门。距离未知时放行(让服务端裁决,也可能是功能NPC那种例外);
    /// 确定超距时把原因写进界面日志并返回 false,调用方必须取消发送。</summary>
    private bool NpcInRange(long npcId, string what)
    {
        if (!TryGetViewDistance(npcId, out int dist)) return true;
        if (dist < NpcRangeTiles) return true;
        EmitLog($"[npc] {what}: NPC id={npcId} 距离 {dist} 格 ≥ {NpcRangeTiles} 格" +
                "(服务端只认同图 15 格内),整包丢弃且零回包,已取消发送 —— 先走近再操作");
        return false;
    }

    /// <summary>走到某 NPC 的可操作距离内(服务端 abs(dx)<15)。已在范围内直接返回 true。
    /// 目标是 NPC 靠我方一侧 2 格:踩到 NPC 身上那一步服务端会当成阻挡丢掉,
    /// 而我方坐标是乐观更新的,会越算越超前。</summary>
    public async Task<bool> ApproachNpcAsync(long npcId, Func<int, int, bool>? isWalkable, int stepDelayMs, CancellationToken ct)
    {
        if (!Dots.TryGetValue(npcId, out var dot))
        {
            EmitLog($"[npc] 视野里没有 id={npcId},无法走近");
            return false;
        }
        int tx = dot.X, ty = dot.Y;
        int px = Player.PosX, py = Player.PosY;
        if (Math.Max(Math.Abs(tx - px), Math.Abs(ty - py)) < NpcRangeTiles) return true;
        int nx = Math.Sign(px - tx), ny = Math.Sign(py - ty);
        if (nx == 0 && ny == 0) ny = 1;
        tx += nx * 2;
        ty += ny * 2;
        return await WalkToAsync(tx, ty, isWalkable ?? ((_, _) => true),
            stepDelayMs, ct).ConfigureAwait(false);
    }

    public async Task SendNpcInteractAsync(long npcId, CancellationToken ct)
    {
        if (!NpcInRange(npcId, "对话(CM_CLICKNPC)")) return;

        // ===== C 方案分流：NPC 交互 = 点 NPC 那一格（坐标从视野表取，包体里没有）=====
        var (npcX, npcY) = FindDotPos(npcId);
        if (await DispatchToDriverAsync(new ClientActionIntent
            {
                Kind = ClientActionKind.NpcInteract, X = npcX, Y = npcY, TargetId = (int)npcId,
                RawCommand = Grobal2.CM_CLICKNPC,
            }, ct).ConfigureAwait(false)) return;
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_CLICKNPC, npcId, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>选择 NPC 对话菜单项(CM_MERCHANTDLGSELECT)。
    /// 对照原版:Recog=merchantId, body=选项命令文本。</summary>
    public async Task SendMerchantDlgSelectAsync(long merchantId, string command, CancellationToken ct)
    {
        if (!NpcInRange(merchantId, $"菜单项\"{command}\"")) return;

        // ===== C 方案分流：菜单选择 = 按 UiStateProbe 解析出的菜单文本点对应行 =====
        var (merX, merY) = FindDotPos(merchantId);
        if (await DispatchToDriverAsync(new ClientActionIntent
            {
                Kind = ClientActionKind.DialogSelect, X = merX, Y = merY, TargetId = (int)merchantId,
                Text = command, RawCommand = Grobal2.CM_MERCHANTDLGSELECT,
            }, ct).ConfigureAwait(false)) return;
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_MERCHANTDLGSELECT, merchantId, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(command);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>购买 NPC 商品(CM_USERBUYITEM)。
    /// 对照原版:Recog=merchantId, Param=itemIndex低16位, Tag=itemIndex高16位, Series=数量, body=物品名。</summary>
    public async Task SendBuyItemAsync(long merchantId, int itemIndex, int count, string itemName, CancellationToken ct)
    {
        if (!NpcInRange(merchantId, $"购买 {itemName}")) return;
        ushort lo = unchecked((ushort)(itemIndex & 0xFFFF));
        ushort hi = unchecked((ushort)((itemIndex >> 16) & 0xFFFF));
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_USERBUYITEM, merchantId, lo, hi, (ushort)count);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(itemName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>请求"逐件商品"明细(CM_USERGETDETAILITEM)。子菜单=1 的装备类只能先取明细才能拿到每件 MakeIndex。
    /// 对照原版 ClMain.pas:17776:Recog=merchant, Param=本页起始下标(0/10/20…), Tag=是否摆摊框(商店填 0), body=物品名。
    /// 服务端 ObjPlayer.pas:22776 → TMerchant.ClientGetDetailGoodsList(ObjNpc.pas:3690):
    /// 只认 **名字相同** 的那一组货、每页最多 10 件、下标越界会被夹到最后一页,要求 NPC 距离 ≤15 格。</summary>
    public async Task SendGetDetailItemAsync(long merchantId, int startIndex, string itemName, CancellationToken ct)
    {
        if (!NpcInRange(merchantId, $"商品明细 {itemName}")) return;
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_USERGETDETAILITEM, merchantId, (ushort)startIndex, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(itemName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>查询出售价格(CM_MERCHANTQUERYSELLPRICE)。
    /// 对照原版:Recog=merchantId, Param=MakeIndex低16位, Tag=MakeIndex高16位, body=物品名。</summary>
    public async Task SendQuerySellPriceAsync(long merchantId, int makeIndex, string itemName, CancellationToken ct)
    {
        if (!NpcInRange(merchantId, $"查卖价 {itemName}")) return;
        ushort lo = unchecked((ushort)(makeIndex & 0xFFFF));
        ushort hi = unchecked((ushort)((makeIndex >> 16) & 0xFFFF));
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_MERCHANTQUERYSELLPRICE, merchantId, lo, hi, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(itemName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>出售物品(CM_USERSELLITEM)。
    /// 对照原版:Recog=merchantId, Param=MakeIndex低16位, Tag=MakeIndex高16位, body=物品名。</summary>
    public async Task SendSellItemAsync(long merchantId, int makeIndex, string itemName, CancellationToken ct)
    {
        if (!NpcInRange(merchantId, $"出售 {itemName}")) return;
        _pendingSellMakeIndex = makeIndex;
        ushort lo = unchecked((ushort)(makeIndex & 0xFFFF));
        ushort hi = unchecked((ushort)((makeIndex >> 16) & 0xFFFF));
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_USERSELLITEM, merchantId, lo, hi, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(itemName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>查询修理费用(CM_MERCHANTQUERYREPAIRCOST)。
    /// 对照原版 ClMain.pas:17712:Recog=merchant, Param=MakeIndex 低16位, Tag=高16位, Series=0, body=物品名。
    /// 服务端 ObjPlayer.pas:23134 用 MakeLong(Param,Tag) 与 sMsg 名字**同时**匹配背包物品,
    /// 名字写错就静默无回包;NPC 距玩家须 <15 格。</summary>
    public async Task SendQueryRepairCostAsync(long merchantId, int makeIndex, string itemName, CancellationToken ct)
    {
        if (!NpcInRange(merchantId, $"查修理费 {itemName}")) return;
        ushort lo = unchecked((ushort)(makeIndex & 0xFFFF));
        ushort hi = unchecked((ushort)((makeIndex >> 16) & 0xFFFF));
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_MERCHANTQUERYREPAIRCOST, merchantId, lo, hi, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(itemName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>修理物品(CM_USERREPAIRITEM)。字段布局同上,原版 ClMain.pas:17736。
    /// 服务端 ObjPlayer.pas:23100 只在背包 m_ItemList 里查 —— 装备中的物品必须先卸下。</summary>
    public async Task SendRepairItemAsync(long merchantId, int makeIndex, string itemName, CancellationToken ct)
    {
        if (!NpcInRange(merchantId, $"修理 {itemName}")) return;
        _pendingRepairMakeIndex = makeIndex;
        ushort lo = unchecked((ushort)(makeIndex & 0xFFFF));
        ushort hi = unchecked((ushort)((makeIndex >> 16) & 0xFFFF));
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_USERREPAIRITEM, merchantId, lo, hi, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(itemName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>使用物品/喝药(CM_EAT)。Recog=MakeIndex, body=物品名。
    /// 对照原版 ClMain.pas:17222 SendEat:字段用 0 填充即可,服务端只读 Recog。</summary>
    public async Task SendEatAsync(int makeIndex, string itemName, CancellationToken ct)
    {
        // ===== C 方案分流：喝药优先走快捷键，其次按背包格子点（见 ClientActionDriver）=====
        if (await DispatchToDriverAsync(new ClientActionIntent
            {
                Kind = ClientActionKind.Eat, Param = makeIndex, Text = itemName,
                RawCommand = Grobal2.CM_EAT,
            }, ct).ConfigureAwait(false)) return;

        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_EAT, makeIndex, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(itemName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>穿戴装备(CM_TAKEONITEM)。
    /// 对照原版:Recog=MakeIndex, Param=穿戴部位(U_*), Tag=0, Series=0, body=物品名。</summary>
    public async Task SendTakeOnItemAsync(int makeIndex, int slot, string itemName, CancellationToken ct)
    {
        _pendingTakeOnMakeIndex = makeIndex;
        _pendingTakeOnSlot = slot;
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_TAKEONITEM, makeIndex, (ushort)slot, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(itemName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>卸下装备(CM_TAKEOFFITEM)。对照原版:Param=部位, Recog=MakeIndex, body=物品名。</summary>
    public async Task SendTakeOffItemAsync(int makeIndex, int slot, string itemName, CancellationToken ct)
    {
        _pendingTakeOffMakeIndex = makeIndex;
        _pendingTakeOffSlot = slot;
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_TAKEOFFITEM, makeIndex, (ushort)slot, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(itemName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>丢弃物品到地面(CM_DROPITEM)。Recog=MakeIndex, Param=数量(0=整叠/全部), body=物品名。
    /// 服务端 ClientDropItem(ObjPlayer.pas:20225) 要求 MakeIndex 与物品名同时匹配,缺名字必定失败。
    /// count>0 且是可叠加物品时只丢 count 个并从原堆扣除(ObjPlayer.pas:20284),原版客户端同样
    /// 把数量放在 Param(ClMain.pas:17130 MakeDefaultMsg(CM_DROPITEM, nItemIndex, nCount, 0, 0))。</summary>
    public async Task SendDropItemAsync(int makeIndex, string itemName, int count, CancellationToken ct)
    {
        // 不可叠加物品或服务端判定 count >= 总数时会走整件丢弃分支,所以数量填 1 也安全。
        ushort n = (ushort)Math.Clamp(count, 0, ushort.MaxValue);
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_DROPITEM, makeIndex, n, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(itemName);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    public async Task SendDropItemAsync(int makeIndex, string itemName, CancellationToken ct)
        => await SendDropItemAsync(makeIndex, itemName, 0, ct).ConfigureAwait(false);

    /// <summary>丢弃金币(CM_DROPGOLD)。服务端 ClientDropGold(ObjPlayer.pas:22798) 只读 nParam1(=Recog),
    /// 且 nGold >= m_nGold 时直接 Exit(一分不给、也无回包),所以次数上必须留余量;
    /// 还会被 m_PEnvir.m_boNOTHROWITEM / 安全区 / nCanDropGold / m_boCanDrop 四道闸门拒掉,
    /// 拒绝时统一走 RM_MENU_OK 系统提示(不是专用失败包),因此这里只能"发完再看金币"。</summary>
    public async Task SendDropGoldAsync(int amount, CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_DROPGOLD, amount, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>发送 CM_SOFTCLOSE 小退/大退指令。服务端 TPlayObject.ClientSoftClose
    /// (ObjPlayer.pas:26264) 不读任何字段,只 DoClientClose + 回 SM_SOFTCLOSE,
    /// 所以 wParam 只是给原版客户端留的形似参数,放 Param 位不影响结果。</summary>
    public async Task SendSoftCloseAsync(ushort wParam, CancellationToken ct)
    {
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_SOFTCLOSE, 0, wParam, 0, 0);
        string payload = EdCode.EncodeMessage(msg);
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);
    }

    public async Task SendSoftCloseAsync(CancellationToken ct) => await SendSoftCloseAsync(0, ct).ConfigureAwait(false);

    /// <summary>清理超出玩家视野范围的对象。
    /// 服务端不会为离开视野的对象发删除包(之前只靠 SM_WITHINRANGE 创建),
    /// 动作包又只在进入视野时推送,离开视野后对象会残留,名字为空时显示成 #id 乱码。
    /// 这里按与玩家的切比雪夫距离清理,保证视野外对象正常"消失"。</summary>
    public void PruneOutOfRangeDots(int maxRange = 40)
    {
        int px = Player.PosX;
        int py = Player.PosY;
        bool changed = false;
        foreach (var kv in Dots)
        {
            int d = Math.Max(Math.Abs(kv.Value.X - px), Math.Abs(kv.Value.Y - py));
            if (d > maxRange)
            {
                Dots.TryRemove(kv.Key, out _);
                changed = true;
            }
        }
        if (changed)
            StateChanged?.Invoke();
    }
}

// ============================================================
//  数据结构
// ============================================================

/// <summary>角色描述(12字节, SM_WITHINRANGE用)。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CharDesc
{
    public int Feature;
    public int Status;
    public int StatusEx;
}

/// <summary>角色描述精简版(8字节)。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CharDesc2
{
    public int Feature;
    public int Status;
}

/// <summary>用户状态头(用于 SM_SENDUSERSTATE, 二进制)。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct UserStateInfoHeader
{
    public int Feature;
    // UserName: PascalString(1字节长度 + 15字节GBK = 16字节)
    public int NameP1;
    public int NameP2;
    public int NameP3;
    public int NameP4;
    public int NameColor;
    // GuildName: PascalString(1+14=15)
    public int GuildP1;
    public int GuildP2;
    public int GuildP3;
    public int GuildP4;
    // GuildRankName: PascalString(1+15=16)
    public int RankP1;
    public int RankP2;
    public int RankP3;
    public int RankP4;
    public byte Gender;
    public byte HumAttr;
    public byte Reserved1;
    public byte Reserved2;

    public string UserNameString => ReadPascalString(NameP1, NameP2, NameP3, NameP4, 15);
    public string GuildNameString => ReadPascalString(GuildP1, GuildP2, GuildP3, GuildP4, 14);
    public string GuildRankNameString => ReadPascalString(RankP1, RankP2, RankP3, RankP4, 15);

    private static unsafe string ReadPascalString(int p1, int p2, int p3, int p4, int maxContentLen)
    {
        Span<byte> buf = stackalloc byte[16];
        BitConverter.TryWriteBytes(buf, p1);
        BitConverter.TryWriteBytes(buf[4..], p2);
        BitConverter.TryWriteBytes(buf[8..], p3);
        BitConverter.TryWriteBytes(buf[12..], p4);

        int len = buf[0];
        if (len <= 0 || len > maxContentLen) return string.Empty;
        return GbkEncoding.Instance.GetString(buf.Slice(1, len));
    }
}

/// <summary>背包中的单个物品信息。</summary>
public record struct BagItemInfo
{
    public int MakeIndex;
    public string Name;
    public int DuraCount;
    public int DuraMax;
    public int Count;
    /// <summary>服务端 CheckOverLapItem 判定的可叠加物品:Dura 存"件数-1",显示与发包数量都要 +1。</summary>
    public bool Stackable;
    /// <summary>StdMode(装备类型,用于区分药品/书/装备)。0-3 药品,4 书,5+ 装备。</summary>
    public byte StdMode;
}
