using System.Text.Json;
using System.Text.Json.Serialization;
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
    public string LanHost { get; set; } = "192.168.1.5";
    public bool AutoReloginOnDeath { get; set; }
    public int AutoReloginMaxAttempts { get; set; } = 3;
    public int AutoReloginIntervalSec { get; set; } = 60;
    public string MapDir { get; set; } = string.Empty;
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
    }
}
