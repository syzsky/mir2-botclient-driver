namespace BotClient.ClientDriver.Hunt;

/// <summary>
/// 一次"挂机任务"的配置：去哪张图、从哪一级菜单进、要不要逐层下探、每层打多久。
///
/// 设计目标是把"换图挂机"变成**只改一个地图名**的事：
///   换地图 = 改 <see cref="TargetMapText"/>（如 "僵尸洞" → "祖玛寺庙"）；
///   逐层下探 = <see cref="Descend"/> 打开后自动识别本层的"下一层/二层/三层"入口；
///   等级门槛 = <see cref="MinLevel"/>，低于门槛直接不点（避免拿真角色去撞服务端回绝）。
///
/// 这里不写任何"点第几行"的硬编码：菜单文本由服务端下发，逐级按文本点（见 NpcTransferRunner）。
/// </summary>
public sealed class HuntPlan
{
    /// <summary>目标地图中文名（如 "僵尸洞"）。也可填地图代码（如 "D1101"），反查逻辑在 MapEntryProbe。</summary>
    public string TargetMapText { get; set; } = string.Empty;

    /// <summary>
    /// 进入目标图前要先点开的上级菜单路径，例如 ["传送"]。
    /// 留空表示"点开 NPC 后当前层菜单里直接就是地图名"（很多服是这样，故默认空）。
    /// </summary>
    public IReadOnlyList<string> MenuPath { get; set; } = Array.Empty<string>();

    /// <summary>传送员 NPC 的匹配关键词（按功能关键词做归一化包含匹配，如 "传送" 命中 "玛法大陆传送员"）。</summary>
    public string NpcKeyword { get; set; } = "传送";

    /// <summary>进入目标图的最低等级门槛；&lt;=0 表示不检查。低于门槛不会做任何点击。</summary>
    public int MinLevel { get; set; }

    /// <summary>是否在本图清完怪后继续往下层走。</summary>
    public bool Descend { get; set; } = true;

    /// <summary>最多下探层数（含第一层），防止"传送员互链"导致无限下钻。</summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>识别"下一层入口"的菜单关键词。命中即点，未命中就判定本层没有下层。</summary>
    public IReadOnlyList<string> DescendKeywords { get; set; } = new[] { "下一层", "下层", "二层", "三层", "四层", "地下", "深入" };

    /// <summary>
    /// 判"本层已清完"的持续空场时长：视野内怪物为 0 且持续这么久才算清完
    /// （偶尔一瞬 0 怪是掉包/刷新间隙，直接判清会提前下钻）。
    /// </summary>
    public int FloorClearIdleMs { get; set; } = 8000;

    /// <summary>每层最长战斗时长；到时即便还有怪也收工（避免被一只打不动的怪锁死）。</summary>
    public int FloorMaxMs { get; set; } = 300_000;

    /// <summary>一轮结束到下轮开始之间的间隔。</summary>
    public int LoopIntervalMs { get; set; } = 5000;

    public string Describe()
    {
        string menu = MenuPath.Count > 0 ? string.Join(" → ", MenuPath) + " → " : string.Empty;
        string level = MinLevel > 0 ? $"≥{MinLevel}级" : "不限等级";
        string descend = Descend ? $"最多 {MaxDepth} 层下探" : "不下探";
        string clear = $"每层清怪上限 {FloorMaxMs / 1000}s（空场 {FloorClearIdleMs / 1000}s 判定清完）";
        return $"目标图 {menu}{TargetMapText}｜NPC 关键词 \"{NpcKeyword}\"｜{level}｜{descend}｜{clear}";
    }
}
