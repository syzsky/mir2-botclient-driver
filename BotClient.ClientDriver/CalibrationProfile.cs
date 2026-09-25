using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotClient.ClientDriver.Input;

namespace BotClient.ClientDriver;

/// <summary>
/// A 档：校准档。
///
/// 为什么可以存档复用：校准量的是**客户区相对坐标**（视图区矩形、玩家格像素、格宽格高……），
/// 它只跟三件事有关 —— 屏幕分辨率、客户端进程、窗口客户区尺寸。移动窗口、换服、重启客户端、
/// 换账号都不改变这三件事，所以同一套"身份"下的校准数据可以反复套用，不必每次重重。
///
/// 身份（<see cref="Key"/>）= <c>分辨率_进程名_客户区宽x高</c>，例如
/// <c>1920x1080_MirClient_1024x768</c>。改分辨率 / 改窗口尺寸 / 换皮肤（客户区变了）
/// 才会落到另一个档上，那时才需要重新量一次（或用 --autocalibrate 自动收敛）。
/// </summary>
public sealed class CalibrationProfile
{
    /// <summary>身份：分辨率_进程_客户区尺寸。</summary>
    public string Key { get; set; } = "";

    /// <summary>桌面分辨率，形如 1920x1080。</summary>
    public string Resolution { get; set; } = "";

    public string ProcessName { get; set; } = "";

    public int WindowWidth { get; set; }

    public int WindowHeight { get; set; }

    /// <summary>来源：hand=人工量一次 / quick=只量视图区两点（其余取初值） / auto=走格反馈自校正。</summary>
    public string Source { get; set; } = "hand";

    public string Note { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public ViewCalibration View { get; set; } = new();

    public MiniMapCalibration MiniMap { get; set; } = new();

    public BagCalibration Bag { get; set; } = new();

    public DialogCalibration Dialog { get; set; } = new();

    public string Describe() => CalibrationArchives.Describe(this);
}

/// <summary>
/// 校准档仓库：与 clientdriver.json 同目录的 calibration_profiles.json。
/// 一个身份一条档，重复保存则覆盖（并更新 UpdatedAt），不会无限膨胀。
/// </summary>
public sealed class CalibrationProfileStore
{
    [JsonIgnore]
    public string FilePath { get; set; } = "";

    public int Version { get; set; } = 1;

    public string Note { get; set; } = "A 档校准档：key = 分辨率_进程_客户区尺寸；同一 key 自动套用，量一次长期复用。";

    public List<CalibrationProfile> Profiles { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string StorePathFor(string driverConfigPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(driverConfigPath)) ?? ".";
        return Path.Combine(dir, "calibration_profiles.json");
    }

    public static CalibrationProfileStore LoadWith(string driverConfigPath)
    {
        string path = StorePathFor(driverConfigPath);
        var store = new CalibrationProfileStore { FilePath = path };
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<CalibrationProfileStore>(File.ReadAllText(path), JsonOpts);
                if (loaded != null)
                {
                    loaded.FilePath = path;
                    loaded.Profiles ??= new List<CalibrationProfile>();
                    return loaded;
                }
            }
        }
        catch
        {
            // 存档损坏不影响主流程：当作空仓库，下一次保存时重建
        }

        return store;
    }

    public void Save()
    {
        if (string.IsNullOrWhiteSpace(FilePath)) return;
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public CalibrationProfile? Find(string key)
        => Profiles.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>按身份写档：有则覆盖数据并更新时间，无则新增。</summary>
    public CalibrationProfile Upsert(string key, string source, string note)
    {
        var exist = Find(key);
        if (exist == null)
        {
            exist = new CalibrationProfile { Key = key, CreatedAt = DateTime.Now };
            Profiles.Add(exist);
        }

        exist.Source = source;
        exist.Note = note;
        exist.UpdatedAt = DateTime.Now;
        return exist;
    }

    public string Describe()
    {
        if (Profiles.Count == 0) return "校准档: 暂无";
        var sb = new StringBuilder($"校准档: 共 {Profiles.Count} 条");
        foreach (var p in Profiles.OrderByDescending(p => p.UpdatedAt).Take(5))
            sb.Append(Environment.NewLine + "  · " + p.Describe());
        return sb.ToString();
    }
}

/// <summary>校准档的构造 / 套用 / 收录。宿主启动、校准工具、自动校正三处共用同一套口径。</summary>
public static class CalibrationArchives
{
    /// <summary>深拷贝：存档与运行配置必须彻底分开，避免互相改到。</summary>
    private static T Clone<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    /// <summary>当前环境身份：分辨率_进程_客户区尺寸。</summary>
    public static string BuildKey(string processName, int windowWidth, int windowHeight)
    {
        var (sw, sh) = ScreenMetrics.DesktopSize();
        string res = sw > 0 && sh > 0 ? $"{sw}x{sh}" : "unknown";
        string proc = string.IsNullOrWhiteSpace(processName) ? "unknown" : processName.Trim();
        return $"{res}_{proc}_{windowWidth}x{windowHeight}";
    }

    /// <summary>把当前配置里的四块校准数据收成一条档。</summary>
    public static CalibrationProfile Capture(ClientDriverConfig cfg, string key, string source, string note,
        int windowWidth = 0, int windowHeight = 0)
    {
        var (sw, sh) = ScreenMetrics.DesktopSize();
        return new CalibrationProfile
        {
            Key = key,
            Resolution = sw > 0 && sh > 0 ? $"{sw}x{sh}" : "unknown",
            ProcessName = cfg.ProcessName,
            WindowWidth = windowWidth,
            WindowHeight = windowHeight,
            Source = source,
            Note = note,
            View = Clone(cfg.View),
            MiniMap = Clone(cfg.MiniMap),
            Bag = Clone(cfg.Bag),
            Dialog = Clone(cfg.Dialog),
        };
    }

    /// <summary>把档里的四块校准数据套回运行配置。</summary>
    public static void Apply(ClientDriverConfig cfg, CalibrationProfile p)
    {
        cfg.View = Clone(p.View);
        cfg.MiniMap = Clone(p.MiniMap);
        cfg.Bag = Clone(p.Bag);
        cfg.Dialog = Clone(p.Dialog);
    }

    public static string Describe(CalibrationProfile p)
    {
        string src = p.Source switch
        {
            "auto" => "走格自校正",
            "quick" => "视图区两点+初值",
            "hand" => "人工量取",
            _ => p.Source,
        };
        return $"{p.Key}（{src}，更新于 {p.UpdatedAt:yyyy-MM-dd HH:mm}）" +
               $" 视图 {p.View.ViewLeft},{p.View.ViewTop} {p.View.ViewWidth}×{p.View.ViewHeight}" +
               $"，玩家格 ({p.View.PlayerScreenX},{p.View.PlayerScreenY})" +
               $"，格 {p.View.CellWidth}×{p.View.CellHeight}" +
               $"｜小地图 {(p.MiniMap.IsCalibrated ? "有" : "无")}" +
               $"，背包 {(p.Bag.IsCalibrated ? "有" : "无")}" +
               $"，对话框 {(p.Dialog.IsCalibrated ? "有" : "无")}";
    }

    /// <summary>当前环境命中哪条档（未命中返回 false）。</summary>
    public static bool TryFind(ClientDriverConfig cfg, string driverConfigPath, InputSimulator win,
        out string key, out CalibrationProfile? profile)
    {
        key = "";
        profile = null;
        if (!win.TryLocateWindow(out _)) return false;

        var (cw, ch) = win.GetClientSize();
        key = BuildKey(cfg.ProcessName, cw, ch);
        profile = CalibrationProfileStore.LoadWith(driverConfigPath).Find(key);
        return profile != null;
    }

    /// <summary>
    /// 宿主启动时调用：命中校准档就套用，返回说明文本（未命中也给一句可照做的提示）。
    /// </summary>
    public static bool TryAutoApply(ClientDriverConfig cfg, string driverConfigPath, InputSimulator win,
        out string message)
    {
        if (!win.TryLocateWindow(out string why))
        {
            message = $"校准档未检查（未找到窗口: {why}）";
            return false;
        }

        var (cw, ch) = win.GetClientSize();
        string key = BuildKey(cfg.ProcessName, cw, ch);
        var hit = CalibrationProfileStore.LoadWith(driverConfigPath).Find(key);
        if (hit == null)
        {
            message = $"未命中校准档（{key}）：本环境还没量过 —— 跑 --calibrate 量一次，或 --autocalibrate 自动收敛一次，量完会自动存档";
            return false;
        }

        Apply(cfg, hit);
        message = $"校准档命中：{hit.Describe()}" + Environment.NewLine +
                  "         （换服/重启/挪窗口都继续有效；改分辨率或窗口尺寸会落到另一个档）";
        return true;
    }

    /// <summary>
    /// 量完 / 自动收敛成功后调用：把当前配置收录成本环境的校准档。
    /// </summary>
    public static bool Remember(ClientDriverConfig cfg, string driverConfigPath, InputSimulator win,
        string source, string note, out string message)
    {
        if (!win.TryLocateWindow(out string why))
        {
            message = $"校准档未写入（未找到窗口: {why}）";
            return false;
        }

        try
        {
            var (cw, ch) = win.GetClientSize();
            string key = BuildKey(cfg.ProcessName, cw, ch);
            var store = CalibrationProfileStore.LoadWith(driverConfigPath);
            var profile = store.Upsert(key, source, note);
            var fresh = Capture(cfg, key, source, note, cw, ch);
            profile.Resolution = fresh.Resolution;
            profile.ProcessName = fresh.ProcessName;
            profile.WindowWidth = cw;
            profile.WindowHeight = ch;
            profile.View = fresh.View;
            profile.MiniMap = fresh.MiniMap;
            profile.Bag = fresh.Bag;
            profile.Dialog = fresh.Dialog;
            store.Save();
            message = $"已收录校准档 {key}（来源 {source}）：{Path.GetFileName(CalibrationProfileStore.StorePathFor(driverConfigPath))}，下次同环境自动套用";
            return true;
        }
        catch (Exception ex)
        {
            message = $"校准档写入失败（不影响本次校准结果）: {ex.Message}";
            return false;
        }
    }
}

/// <summary>桌面分辨率（A 档身份的一部分）。非 Windows 上取不到就返回 0，不影响其余逻辑。</summary>
public static class ScreenMetrics
{
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public static (int W, int H) DesktopSize()
    {
        try
        {
            return (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        }
        catch
        {
            return (0, 0);
        }
    }
}
