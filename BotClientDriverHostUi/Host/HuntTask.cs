using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotClient.ClientDriver.Hunt;

namespace BotClientDriverHostUi.Host;

/// <summary>
/// 一条"挂机任务"的可编辑配置（界面用，落盘为 hunt_tasks.json）。
///
/// 与无界面宿主的 <see cref="HuntPlan"/> 一一对应：界面编辑保存后交给 HuntTaskRunner 执行，
/// 因此"界面里配的"和"命令行 --hunt 配的"是同一套语义，不会长出第二套行为。
///
/// 字段刻意只保留 HuntPlan 里用户真正会改的那几项（目标图 / NPC 关键词 / 等级门槛 / 层数 / 时长），
/// 其余（DescendKeywords 等）沿用 HuntPlan 的默认值 —— 减少界面噪音，也不给出"看起来能配其实没用"的旋钮。
/// </summary>
public sealed class HuntTask
{
    /// <summary>任务标识（列表里用来定位；新建自动生成）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>任务名（列表显示用；留空时用目标图名顶替）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>目标地图中文名（如 "僵尸洞"），也可填地图代码（如 "D1101"）。</summary>
    public string TargetMapText { get; set; } = string.Empty;

    /// <summary>传送员 NPC 的匹配关键词（默认 "传送"）。</summary>
    public string NpcKeyword { get; set; } = "传送";

    /// <summary>进入目标图前要先点开的上级菜单路径（如 ["传送"]）；空 = 当前层直接是地图名。</summary>
    public List<string> MenuPath { get; set; } = new();

    /// <summary>进入目标图的最低等级门槛；0 = 不检查。</summary>
    public int MinLevel { get; set; }

    /// <summary>本层清完怪后是否继续往下层走。</summary>
    public bool Descend { get; set; } = true;

    /// <summary>最多下探层数（含第一层）。</summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>判"本层已清完"的持续空场秒数。</summary>
    public int FloorClearIdleSeconds { get; set; } = 8;

    /// <summary>每层最长战斗秒数（到时即便还有怪也收工）。</summary>
    public int FloorMaxSeconds { get; set; } = 300;

    /// <summary>一轮结束到下轮开始之间的间隔秒数。</summary>
    public int LoopIntervalSeconds { get; set; } = 5;

    /// <summary>true = 一轮接一轮地跑；false = 只跑一轮就停。</summary>
    public bool Loop { get; set; } = true;

    /// <summary>备注（给自己看的，如"20 级前在这刷"）。</summary>
    public string Note { get; set; } = string.Empty;

    // ---------------------------------------------------------------- 显示用（不落盘）

    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? TargetMapText : Name;

    [JsonIgnore] public string LevelText => MinLevel > 0 ? "≥" + MinLevel + "级" : "不限等级";

    [JsonIgnore] public string DepthText => Descend ? $"最多 {MaxDepth} 层" : "不下探";

    [JsonIgnore] public string LoopText => Loop ? "循环" : "只跑一轮";

    [JsonIgnore] public string MenuText => MenuPath.Count > 0 ? string.Join(" → ", MenuPath) : "（当前层直接是地图名）";

    [JsonIgnore] public string ClearText => $"每层 {FloorMaxSeconds}s（空场 {FloorClearIdleSeconds}s 判清完）";

    /// <summary>转成执行层认识的 HuntPlan。所有数值在这里做一次下限保护，避免手滑填 0 导致死循环。</summary>
    public HuntPlan ToPlan() => new()
    {
        TargetMapText = TargetMapText.Trim(),
        NpcKeyword = string.IsNullOrWhiteSpace(NpcKeyword) ? "传送" : NpcKeyword.Trim(),
        MenuPath = MenuPath.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray(),
        MinLevel = Math.Max(0, MinLevel),
        Descend = Descend,
        MaxDepth = Math.Max(1, MaxDepth),
        FloorClearIdleMs = Math.Max(1, FloorClearIdleSeconds) * 1000,
        FloorMaxMs = Math.Max(10, FloorMaxSeconds) * 1000,
        LoopIntervalMs = Math.Max(1, LoopIntervalSeconds) * 1000,
    };

    public HuntTask Clone()
    {
        HuntTask copy = (HuntTask)MemberwiseClone();
        copy.MenuPath = new List<string>(MenuPath);
        return copy;
    }
}

/// <summary>
/// 任务清单的落盘读写（hunt_tasks.json，与 botsettings.json / clientdriver.json 同目录）。
///
/// 刻意"读失败不抛、写失败要报"：
///   · 读：文件缺失/被手改坏 → 返回空清单 + LastError，界面照常能用（不能让一个坏 json 卡住整个程序）；
///   · 写：写盘失败（目录只读/被占用）必须如实告知，否则用户会以为"加了任务但没生效"。
/// 写入采用"先写 .tmp 再替换"，避免写一半断电留下半截 json。
/// </summary>
public sealed class HuntTaskStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    public HuntTaskStore(string path) => Path = path;

    public string Path { get; }

    /// <summary>最近一次读/写失败的原因（成功时清空）。</summary>
    public string? LastError { get; private set; }

    public List<HuntTask> Load()
    {
        LastError = null;
        try
        {
            if (!File.Exists(Path)) return new List<HuntTask>();

            string json = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(json)) return new List<HuntTask>();

            List<HuntTask>? list = JsonSerializer.Deserialize<List<HuntTask>>(json, Options);
            return list ?? new List<HuntTask>();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return new List<HuntTask>();
        }
    }

    public bool Save(IReadOnlyList<HuntTask> tasks)
    {
        LastError = null;
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(tasks, Options));
            File.Move(tmp, Path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }
}
