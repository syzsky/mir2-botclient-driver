using System.Text.Json;
using System.Text.Json.Serialization;
using BotClient.ClientDriver.Sniff;
using BotClient.Human;
using BotClient.Session.Combat;

namespace BotClient.ClientDriver;

/// <summary>
/// C 方案全部可调参数与校准数据。
/// 校准项（View/MiniMap/Bag/Dialog）必须先在真机上实测一次，写进 clientdriver.json；
/// 未校准的项保持 0，运行时会拒绝执行依赖它的动作并给出明确提示，而不是乱点。
/// </summary>
public sealed class ClientDriverConfig
{
    // ---------------------------------------------------------------- 进程与窗口

    /// <summary>客户端进程名（不带 .exe）。用于定位窗口、校验前台窗口。例：MirClient / LegendClient。</summary>
    public string ProcessName { get; set; } = "MirClient";

    /// <summary>窗口标题关键字；留空则只用进程名匹配（多开时用标题区分）。</summary>
    public string WindowTitleKeyword { get; set; } = "";

    /// <summary>
    /// 锁定的客户端 PID（0 = 不锁定）。由图形界面的「扫描客户端」列表选定后写入：
    /// 同名多开（同一引擎开了好几个客户端）时，光靠进程名分不出该跟哪一个，这里直接钉住选中的那个。
    /// PID 每次重启会变，属于"本次会话提示值"——进程退出后该值自动失效，识别会退回按进程名+评分挑选。
    /// </summary>
    public int TargetPid { get; set; }

    /// <summary>每次点击前把客户端窗口切到前台并校验；关掉它就只能靠人工保证窗口在最前，风险自负。</summary>
    public bool EnsureForeground { get; set; } = true;

    // ---------------------------------------------------------------- 服务端与抓包

    /// <summary>服务端 IP（客户端实际连的那个）。</summary>
    public string ServerIp { get; set; } = "";

    /// <summary>三段网关端口；用于按端口识别 TCP 流属于哪一段。只有一个端口也能跑（自动判定）。</summary>
    public int LoginGatePort { get; set; }
    public int SelGatePort { get; set; }
    public int RunGatePort { get; set; }

    /// <summary>抓包网卡名（SharpPcap 的设备名，如 "\Device\NPF_{GUID}"）。留空=自动挑第一个有 IP 的网卡。</summary>
    public string CaptureDevice { get; set; } = "";

    /// <summary>抓包只读模式：永远 true。存在这个字段是为了在评审时一眼看到"不介入连接"。</summary>
    public bool ReadOnlySniff { get; set; } = true;

    /// <summary>
    /// 地图目录提示（可选）：填了才能把 NPC 菜单里的中文地名反查成地图代码，
    /// 从而校验"传送之后到的是不是目标图"（见 MapEntryProbe）。
    /// 例：<c>D:\MirServer\Mir200\Map</c>（其上级 Envir 目录里就有 MapInfo.txt）。
    /// 留空也能跑：退化为"换图成功即认定可进入"的探测式判定，并把实测结果写进缓存。
    /// </summary>
    public string MapDirHint { get; set; } = "";

    // ---------------------------------------------------------------- 跨服适配

    /// <summary>
    /// 帧定界档 —— **换服不用改代码**的地方。
    /// 留空 = 经典 Mir2 定界；不同服/引擎填 magic + 长度字段位置/位宽/头长即可。
    /// 也可用 <c>--autoframe</c> 只读采样后自动探测并写回本字段（探测结果含置信度 Note）。
    /// </summary>
    public FramingProfile? Framing { get; set; }

    /// <summary>
    /// 命令码手工覆盖：语义名 → 该服的 CM_/SM_ 数值。换服后若状态解不对，
    /// 对着抓包核一遍命令码填这里即可，**持久化在 json**，不必再改 Core 代码。
    /// </summary>
    public Dictionary<string, ushort> CmdOverrides { get; set; } = new();

    // ---------------------------------------------------------------- 校准数据

    public ViewCalibration View { get; set; } = new();
    public MiniMapCalibration MiniMap { get; set; } = new();
    public BagCalibration Bag { get; set; } = new();
    public DialogCalibration Dialog { get; set; } = new();

    // ---------------------------------------------------------------- 行为参数

    public BehaviorTuning Behavior { get; set; } = new();

    /// <summary>
    /// 拟人化操作档（轨迹/停顿/疲劳/战斗节奏抖动）。默认全开，
    /// 出问题时把 <c>Enabled</c> 置 false 可一键退回固定时序做对照。
    /// </summary>
    public HumanTuning Human { get; set; } = new();

    /// <summary>
    /// 技能循环（多技能按优先级+条件选用）。默认关闭 = 与改造前的"单一 MagicId"行为完全一致；
    /// 打开后由战斗 AI 每轮按场面挑技能，全不满足时退回物理攻击。
    /// </summary>
    public SkillRotationPlan Skills { get; set; } = new();

    public KeyBindingConfig Keys { get; set; } = new();

    // ---------------------------------------------------------------- 多开标识

    /// <summary>
    /// 多开标识：服务器名-区名-角色名。只用于**日志前缀 / 控制台窗口标题 / 自检报告**，
    /// 让多开的每一份实例一眼能认出来；不参与任何协议逻辑。
    /// 命令行 --server-name / --zone / --character 会覆盖并写回这里。
    /// </summary>
    public InstanceIdentity Identity { get; set; } = new();

    // ---------------------------------------------------------------- 持久化

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static ClientDriverConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var fresh = new ClientDriverConfig();
            fresh.Save(path);
            return fresh;
        }
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ClientDriverConfig>(json, JsonOpts) ?? new ClientDriverConfig();
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
}

/// <summary>
/// 主视图（游戏画面）校准。
/// 传奇是等距 45° 斜视图：格宽 48、格高 32 是通用值，但不同分辨率/客户端皮肤会变，
/// 所以这里全部做成参数，由校准流程实测填入。
/// 玩家在视图中的屏幕位置在多数客户端里是固定的（视图中心），除非走到地图边缘。
/// </summary>
public sealed class ViewCalibration
{
    /// <summary>视图区在窗口客户区内的矩形（像素）。用于把"客户区坐标"换算成"屏幕坐标"。</summary>
    public int ViewLeft { get; set; }
    public int ViewTop { get; set; }
    public int ViewWidth { get; set; }
    public int ViewHeight { get; set; }

    /// <summary>玩家所在格的屏幕坐标（相对窗口客户区原点）。校准方式见 README。</summary>
    public int PlayerScreenX { get; set; }
    public int PlayerScreenY { get; set; }

    /// <summary>单格像素尺寸。传奇通用 48×32。</summary>
    public int CellWidth { get; set; } = 48;
    public int CellHeight { get; set; } = 32;

    /// <summary>视图是否随玩家滚动：true 时玩家恒在 PlayerScreenX/Y（默认）；false 时按 EdgeScroll 处理。</summary>
    public bool PlayerAlwaysCentered { get; set; } = true;

    public bool IsCalibrated => ViewWidth > 0 && ViewHeight > 0 && PlayerScreenX > 0 && PlayerScreenY > 0;
}

/// <summary>小地图校准。用于"远距离移动：点小地图 → 客户端自动寻路"。</summary>
public sealed class MiniMapCalibration
{
    /// <summary>小地图区域（相对窗口客户区）。</summary>
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>小地图上一格对应的像素数（小地图缩放比）。</summary>
    public double PixelPerCell { get; set; }

    /// <summary>小地图左上角像素对应的大地图格坐标。传奇小地图通常显示"当前地图全图"或"以玩家为中心的窗口"。</summary>
    public int OriginMapX { get; set; }
    public int OriginMapY { get; set; }

    /// <summary>小地图显示模式：Full=全图固定（OriginMapX/Y 是地图左上角格）；Centered=以玩家为中心滚动。</summary>
    public MiniMapMode Mode { get; set; } = MiniMapMode.Full;

    public bool IsCalibrated => Width > 0 && Height > 0 && PixelPerCell > 0;
}

public enum MiniMapMode
{
    /// <summary>固定显示整张地图，左上角对应 OriginMapX/Y。</summary>
    Full,
    /// <summary>以玩家为中心滚动，中心即玩家格。</summary>
    Centered,
}

/// <summary>背包/物品栏校准。用于"喝药 = 双击对应物品格"。</summary>
public sealed class BagCalibration
{
    /// <summary>第一格左上角（相对窗口客户区）。</summary>
    public int FirstSlotX { get; set; }
    public int FirstSlotY { get; set; }
    public int SlotWidth { get; set; } = 36;
    public int SlotHeight { get; set; } = 32;
    public int Columns { get; set; } = 6;
    public int Rows { get; set; } = 4;

    /// <summary>是否打开背包面板才能操作（多数客户端是）。</summary>
    public bool MustOpenPanel { get; set; } = true;

    public bool IsCalibrated => SlotWidth > 0 && SlotHeight > 0 && Columns > 0;

    /// <summary>背包索引 → 该格的点击点（相对客户区）。传奇 makeIndex 从 0 起，按行优先排列。</summary>
    public (int X, int Y) SlotCenter(int makeIndex)
    {
        int col = makeIndex % Columns;
        int row = makeIndex / Columns;
        return (FirstSlotX + col * SlotWidth + SlotWidth / 2,
                FirstSlotY + row * SlotHeight + SlotHeight / 2);
    }
}

/// <summary>NPC 对话框校准。菜单行位置固定，一次校准后按索引推算。</summary>
public sealed class DialogCalibration
{
    /// <summary>对话框内第一行菜单文字的左端中点坐标（相对窗口客户区）。</summary>
    public int MenuFirstLineX { get; set; }
    public int MenuFirstLineY { get; set; }

    /// <summary>行高（像素）。</summary>
    public int LineHeight { get; set; } = 18;

    /// <summary>一页最多显示多少行（超过要翻页；天骥脚本同样受此限制）。</summary>
    public int MaxLinesPerPage { get; set; } = 8;

    /// <summary>"下一页"按钮位置（相对客户区），用于翻页。</summary>
    public int NextPageButtonX { get; set; }
    public int NextPageButtonY { get; set; }

    public bool IsCalibrated => LineHeight > 0 && MenuFirstLineX > 0 && MenuFirstLineY > 0;

    public (int X, int Y) MenuLineCenter(int index)
        => (MenuFirstLineX, MenuFirstLineY + index * LineHeight);
}

/// <summary>行为参数：抖动、节流、超时阈值。</summary>
public sealed class BehaviorTuning
{
    // ---- 人化抖动（点击坐标不要每次都打在格心）----
    public int ClickJitterPx { get; set; } = 3;

    // ---- 节流（毫秒）----
    /// <summary>两次点击之间的最小间隔。真客户端自己受 !Setup.txt 限制，这里只是别让它点太快显得机械。</summary>
    public int MinClickIntervalMs { get; set; } = 180;
    public int MaxClickIntervalMs { get; set; } = 420;

    /// <summary>鼠标按下到抬起的持续时长。</summary>
    public int ClickHoldMinMs { get; set; } = 60;
    public int ClickHoldMaxMs { get; set; } = 130;

    // ---- 动作确认 ----
    /// <summary>点完等"动作被客户端接受"的时间上限（上行出现对应 CM 或下行状态变化）。</summary>
    public int ActionAckTimeoutMs { get; set; } = 1200;

    /// <summary>连续多少次动作未被确认就暂停挂机（防止在错误状态下一直瞎点）。</summary>
    public int MaxUnackedActions { get; set; } = 5;

    // ---- 寻路 ----
    /// <summary>超过这个格距就用小地图寻路，否则逐格点。</summary>
    public int MiniMapPathMinDistance { get; set; } = 14;

    /// <summary>寻路卡死判定：这段时间内与目标的距离没有缩短，就认为卡住。</summary>
    public int PathStuckTimeoutMs { get; set; } = 4000;

    // ---- 杂项 ----
    /// <summary>是否禁止一切点击（紧急暂停）。</summary>
    public bool Paused { get; set; }

    /// <summary>黑屏/加载期禁止点击的额外等待（毫秒），用于传送后。</summary>
    public int MapLoadGuardMs { get; set; } = 1500;
}

/// <summary>
/// 快捷键绑定。操作通道里"用药"和"施法"最可靠的做法不是点背包格 / 点魔法栏，
/// 而是走客户端自带的快捷键（游戏里按一下就生效，不依赖面板是否打开、位置是否被遮挡）。
///
/// 值用 Win32 虚拟键码（VK）：F1=0x70 … F8=0x77，数字键 1=0x31 … 6=0x36，ESC=0x1B。
/// 填 0 表示"该客户端没有这个快捷键"，对应的动作会退化为"点背包格/点魔法栏"。
/// </summary>
public sealed class KeyBindingConfig
{
    /// <summary>打开/关闭背包（传奇常见 F9=0x78）。</summary>
    public ushort ToggleBag { get; set; } = 0x78;

    /// <summary>关闭当前窗口 / 取消（ESC）。</summary>
    public ushort Cancel { get; set; } = 0x1B;

    /// <summary>药水快捷键：makeIndex → VK。按数组下标对应物品栏的 makeIndex。</summary>
    public ushort[] PotionKeys { get; set; } = { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36 };

    /// <summary>魔法快捷键：法术槽 → VK。默认 F1~F8。</summary>
    public ushort[] SpellKeys { get; set; } = { 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77 };

    /// <summary>是否使用快捷键喝药（false 则双击背包格，需要 Bag 校准）。</summary>
    public bool UsePotionHotkey { get; set; } = true;

    /// <summary>是否使用快捷键施法（false 则点魔法栏按钮，需要额外校准，本版本未支持）。</summary>
    public bool UseSpellHotkey { get; set; } = true;

    public ushort? ResolvePotionKey(int makeIndex)
        => makeIndex >= 0 && makeIndex < PotionKeys.Length && PotionKeys[makeIndex] != 0 ? PotionKeys[makeIndex] : null;

    public ushort? ResolveSpellKey(int spellSlot)
        => spellSlot >= 0 && spellSlot < SpellKeys.Length && SpellKeys[spellSlot] != 0 ? SpellKeys[spellSlot] : null;
}
