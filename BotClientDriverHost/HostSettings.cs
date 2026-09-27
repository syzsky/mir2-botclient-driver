using System.Text.Json;
using System.Text.Json.Serialization;
using BotClient;
using BotClient.Human;
using BotClient.Session;
using BotClient.Session.Combat;

namespace BotClientDriverHost;

/// <summary>
/// 无界面宿主的配置：字段与 WPF 版 <c>AppSettings</c>（botsettings.json）保持一致，
/// 这样可以**直接复用同一份配置文件**；额外增加 <see cref="ServerName"/> /
/// <see cref="CharacterName"/> 两个脚本化选择字段（留空则自动取服务端返回的第一个）。
///
/// 密码不进这个文件 —— 用 <c>--password</c> 或环境变量 <c>BOT_PASSWORD</c> 传入。
/// </summary>
public sealed class HostSettings
{
    // ---- 连接 ----
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7000;
    public string Account { get; set; } = string.Empty;

    // ---- 自动登录（本宿主新增）----
    /// <summary>要进的区服名；空 = 取服务端下发的第一个。</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>要上的角色名；空 = 取角色列表第一个（20 级以下可先用 CreateCharacterAsync 建号）。</summary>
    public string CharacterName { get; set; } = string.Empty;

    // ---- 战斗/AI 参数（与 AppSettings 对齐）----
    public int WalkIntervalMs { get; set; } = 650;
    public int AttackIntervalMs { get; set; } = 950;
    public int PotionIntervalMs { get; set; } = 1050;
    public int PickupIntervalMs { get; set; } = 500;
    public int FightRange { get; set; } = 12;
    public int HpPotionPercent { get; set; } = 60;
    public int MpPotionPercent { get; set; } = 40;
    public int EscapeHpPercent { get; set; } = 20;
    public bool AutoPickup { get; set; } = true;

    // ---- 拟真操作（新增）----
    /// <summary>
    /// 拟人化档：鼠标轨迹、右键/按键时长、偶发停顿、疲劳与战斗节奏抖动。
    /// 默认开启；写 <c>"Enabled": false</c> 可一键退回固定时序做对照。
    /// </summary>
    public HumanTuning Human { get; set; } = new();

    /// <summary>
    /// 技能循环：多技能按优先级 + 条件（怪物数/距离/自身MP/冷却）选用。
    /// 默认关闭 = 与改造前的"单一 MagicId"行为完全一致；打开后示例可先抄 <see cref="SkillRotationPlan.Sample"/>。
    /// </summary>
    public SkillRotationPlan Skills { get; set; } = new();

    // ---- 其它（沿用 AppSettings 名字，便于共用文件）----
    public bool VerbosePacketLog { get; set; }

    /// <summary>旧版本遗留项，当前**没有任何实现**读取它（服务端地址走 ServerIp / 自动跟随）。</summary>
    public string LanHost { get; set; } = "192.168.1.5";

    /// <summary>
    /// 角色死亡后自动断开重连（服务端会在家点以 14 HP 拉起）。
    ///
    /// **当前尚未实现**：<see cref="DeathRecoveryPolicy"/> 的判定规则写得很完整，
    /// 但没有任何地方构造它、也没有地方发起重连。而且 C 方案（客户端复用模式，即推荐模式）下
    /// **本程序不持有连接**，无法主动断开重连 —— 要实现只能落在"宿主自己登录"的老路径上。
    /// 设成 true 目前不会改变任何行为，启动时会打印一条明确警告。
    /// </summary>
    public bool AutoReloginOnDeath { get; set; }

    /// <summary>见 <see cref="AutoReloginOnDeath"/>：当前未生效。</summary>
    public int AutoReloginMaxAttempts { get; set; } = 3;

    /// <summary>见 <see cref="AutoReloginOnDeath"/>：当前未生效。</summary>
    public int AutoReloginIntervalSec { get; set; } = 60;

    public string MapDir { get; set; } = string.Empty;

    /// <summary>未接入的脚本子系统的数据目录，当前**没有任何实现**读取它。</summary>
    public string CharDataDir { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>读配置；文件不存在时返回默认值（并落一份样本，方便照抄改）。</summary>
    public static HostSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                var fresh = new HostSettings();
                try
                {
                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, JsonSerializer.Serialize(fresh, Options));
                    Console.WriteLine($"[host] 未找到配置，已生成样本: {path}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[host] 生成样本失败（忽略）: {ex.Message}");
                }
                return fresh;
            }

            string json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<HostSettings>(json, Options) ?? new HostSettings();
            Console.WriteLine($"[host] 已加载配置: {path}");
            return loaded;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[host] 配置解析失败({ex.Message})，改用默认值");
            return new HostSettings();
        }
    }

    public void Save(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    /// <summary>把参数灌进 AI（对应 WPF 版 AppSettings.ApplyTo）。</summary>
    public void ApplyTo(BotCombatAI ai)
    {
        ai.WalkIntervalMs = WalkIntervalMs;
        ai.AttackIntervalMs = AttackIntervalMs;
        ai.PotionIntervalMs = PotionIntervalMs;
        ai.PickupIntervalMs = PickupIntervalMs;
        ai.FightRange = FightRange;
        ai.HpPotionPercent = HpPotionPercent;
        ai.MpPotionPercent = MpPotionPercent;
        ai.EscapeHpPercent = EscapeHpPercent;
        ai.AutoPickupEnabled = AutoPickup;
        ai.ItemFilter.AutoPickup = AutoPickup;
        ai.ItemFilter.PickupRange = FightRange;
        ai.ItemFilter.PickupIntervalMs = PickupIntervalMs;

        // 拟真操作：技能循环与拟人档一并灌进 AI（驱动层的鼠标/键盘读的是 ClientDriverConfig）
        ai.SkillPlan = Skills;
        ai.Human = Human ?? new HumanTuning();

        // 逐包报文日志。原来 BotLog.VerbosePackets **从来没有任何赋值点**，
        // 于是设置里这个开关（以及界面上的复选框）点了完全不生效 ——
        // 排障时最需要的那份原始字节日志根本不会落盘。
        BotLog.VerbosePackets = VerbosePacketLog;
    }

    /// <summary>
    /// 报告"配置文件里有、但当前没有任何实现"的开关。
    ///
    /// 这一批是审查时逐个核对"配置项是否真的被读取"发现的 —— 它们在 HostSettings 之外零引用。
    /// 与其让用户对着一个没反应的开关反复试，不如启动时一次说清楚。
    /// </summary>
    public IEnumerable<string> UnimplementedOptions()
    {
        if (AutoReloginOnDeath)
        {
            yield return "[settings] AutoReloginOnDeath=true，但**自动重连尚未实现**：本程序不会在角色死亡后"
                       + "断开重连。死透之后请在游戏客户端里手动重新登录（服务端会在家点以 14 HP 拉起）。"
                       + $"AutoReloginMaxAttempts={AutoReloginMaxAttempts} / AutoReloginIntervalSec={AutoReloginIntervalSec} 同样未生效";
        }

        if (!string.IsNullOrWhiteSpace(LanHost))
            yield return $"[settings] LanHost=\"{LanHost}\" 未被使用（旧版本遗留项；服务端地址走 ServerIp 或自动跟随）";

        if (!string.IsNullOrWhiteSpace(CharDataDir))
            yield return $"[settings] CharDataDir=\"{CharDataDir}\" 未被使用（角色脚本数据目录属于尚未接入的脚本子系统）";
    }
}
