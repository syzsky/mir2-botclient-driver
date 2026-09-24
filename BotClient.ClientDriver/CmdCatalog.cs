using System.Reflection;

namespace BotClient.ClientDriver;

/// <summary>
/// 协议命令码目录。
///
/// 为什么不直接引用 <c>Grobal2.SM_XXX</c>：
///   本工程是"附着在成熟 Core 上"的外挂式模块，一旦 Core 的常量名有出入（同名不同写法、
///   大小写、重构改名），硬编码的引用会直接编译失败，而反射 + 候选名能在运行期优雅降级
///   并给出清晰告警，不影响 Core 本身的编译。
///
/// 所有取不到的命令码返回 0；调用方对 0 必须按"该能力不可用"处理（跳过该判断分支），
/// **绝不能**当成某个具体命令去匹配。这也是本方案不硬编码魔数的原因：
/// 猜错一个数不会报错，只会静默走错分支——那比不工作更危险。
/// </summary>
public interface ICmdCatalog
{
    // ---- 客户端 → 服务端（动作确认：点了之后有没有真发出去）----
    ushort CmWalk { get; }
    ushort CmTurn { get; }
    ushort CmHit { get; }
    ushort CmSpell { get; }
    ushort CmEat { get; }
    ushort CmPickUp { get; }
    ushort CmNpcInteract { get; }
    ushort CmMerchantSelect { get; }
    ushort CmSoftClose { get; }
    ushort CmHeartbeat { get; }

    // ---- 服务端 → 客户端（状态驱动：现在该处于什么 UI 态）----
    ushort SmNpcDialog { get; }
    ushort SmMapChange { get; }
    ushort SmMapDescription { get; }
    ushort SmPositionMove { get; }
    ushort SmActionRet { get; }
    ushort SmHeartbeat { get; }

    /// <summary>取不到（返回 0）的语义项名称清单，用于启动自检。</summary>
    IReadOnlyList<string> Missing { get; }
}

/// <summary>基于 <c>Grobal2</c> 反射的默认实现；名字对不上时用 <see cref="Overrides"/> 手工兜底。</summary>
public sealed class ReflectionCmdCatalog : ICmdCatalog
{
    private static readonly Type? Constants = ResolveConstantsType();
    private readonly List<string> _missing = new();

    /// <summary>手工覆盖表：语义项名 → 命令码数值。反射取不到时从这里取。</summary>
    public Dictionary<string, ushort> Overrides { get; } = new();

    public ReflectionCmdCatalog()
    {
        CmWalk = Find(nameof(CmWalk), "CM_WALK", "CM_WALKING", "CM_MOVETO");
        CmTurn = Find(nameof(CmTurn), "CM_TURN", "CM_TURNING");
        CmHit = Find(nameof(CmHit), "CM_HIT", "CM_ATTACK", "CM_HIT1", "CM_USEMAGIC_HIT");
        CmSpell = Find(nameof(CmSpell), "CM_SPELL", "CM_MAGIC", "CM_SPELL1", "CM_USEMAGIC");
        CmEat = Find(nameof(CmEat), "CM_EAT", "CM_USEITEM", "CM_USE", "CM_USEMEDICINE");
        CmPickUp = Find(nameof(CmPickUp), "CM_PICKUP", "CM_PICK", "CM_PICKITEM", "CM_TAKEON");
        CmNpcInteract = Find(nameof(CmNpcInteract), "CM_NPCINTERACT", "CM_CLICKNPC", "CM_NPC");
        CmMerchantSelect = Find(nameof(CmMerchantSelect), "CM_MERCHANTDLGSELECT", "CM_NPCMENUSELECT", "CM_NPC_DLG_SELECT");
        CmSoftClose = Find(nameof(CmSoftClose), "CM_SOFTCLOSE", "CM_SOFTCLOSEDLG", "CM_CLOSEDLG");
        CmHeartbeat = Find(nameof(CmHeartbeat), "CM_RUNGATE_HEARTBEAT", "CM_HEARTBEAT");

        SmNpcDialog = Find(nameof(SmNpcDialog), "SM_NPCINTERACT", "SM_NPC_DIALOG", "SM_MERCHANTDLG", "SM_SENDMSG", "SM_NPCWINDOW");
        SmMapChange = Find(nameof(SmMapChange), "SM_CHANGEMAP", "SM_MAPCHANGE");
        SmMapDescription = Find(nameof(SmMapDescription), "SM_MAPDESCRIPTION", "SM_MAPDESC");
        SmPositionMove = Find(nameof(SmPositionMove), "SM_POSTIONMOVE", "SM_POSITIONMOVE", "SM_MOVE");
        SmActionRet = Find(nameof(SmActionRet), "SM_ACTION_RET", "SM_ACTIONRET");
        SmHeartbeat = Find(nameof(SmHeartbeat), "SM_RUNGATE_HEARTBEAT", "SM_HEARTBEAT");
    }

    public ushort CmWalk { get; }
    public ushort CmTurn { get; }
    public ushort CmHit { get; }
    public ushort CmSpell { get; }
    public ushort CmEat { get; }
    public ushort CmPickUp { get; }
    public ushort CmNpcInteract { get; }
    public ushort CmMerchantSelect { get; }
    public ushort CmSoftClose { get; }
    public ushort CmHeartbeat { get; }
    public ushort SmNpcDialog { get; }
    public ushort SmMapChange { get; }
    public ushort SmMapDescription { get; }
    public ushort SmPositionMove { get; }
    public ushort SmActionRet { get; }
    public ushort SmHeartbeat { get; }

    public IReadOnlyList<string> Missing => _missing;

    private ushort Find(string semantic, params string[] candidates)
    {
        if (Overrides.TryGetValue(semantic, out ushort ovr)) return ovr;

        if (Constants != null)
        {
            foreach (string name in candidates)
            {
                var f = Constants.GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                if (f == null) continue;
                try
                {
                    object? v = f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(null);
                    if (v != null) return Convert.ToUInt16(v);
                }
                catch { /* 类型不匹配，继续下一个候选 */ }
            }
        }

        _missing.Add($"{semantic}({string.Join("|", candidates)})");
        return 0;
    }

    private static Type? ResolveConstantsType()
    {
        // Grobal2 可能放在 BotClient.Protocol 下，也可能是别的命名空间，这里按全名逐个试。
        var asm = typeof(BotClient.Protocol.CmdPack).Assembly;
        foreach (var t in asm.GetTypes())
        {
            if (t.Name == "Grobal2" || t.Name == "MirConstants" || t.Name == "Commands") return t;
        }
        return null;
    }
}
