using System.Windows;
using BotClientDriverHostUi.Host;

namespace BotClientDriverHostUi.Views;

/// <summary>
/// 挂机任务编辑窗口（新增 / 修改共用）。
///
/// 只做“读进来 → 改 → 校验 → 写回 <see cref="Result"/>”，落盘由主界面统一负责，
/// 这样“取消”永远是纯取消，不会留下半改的文件。
/// 校验放在最后一刻（点保存时）：地图名必填、数值字段必须是正整数，
/// 不合规直接标红并停在本窗口，绝不把坏值写进 hunt_tasks.json 之后再报错。
/// </summary>
public partial class HuntTaskWindow : Window
{
    public HuntTaskWindow(HuntTask? task)
    {
        InitializeComponent();

        Result = task?.Clone() ?? new HuntTask();
        Title = task == null ? "添加挂机任务" : "编辑挂机任务";
        LoadFrom(Result);
    }

    /// <summary>保存后可供主界面读取的任务（取消时不代表任何已生效改动）。</summary>
    public HuntTask Result { get; }

    private void LoadFrom(HuntTask t)
    {
        NameBox.Text = t.Name;
        MapBox.Text = t.TargetMapText;
        NpcBox.Text = t.NpcKeyword;
        MenuBox.Text = string.Join(", ", t.MenuPath);
        LevelBox.Text = t.MinLevel.ToString();
        DescendCheck.IsChecked = t.Descend;
        DepthBox.Text = t.MaxDepth.ToString();
        FloorMaxBox.Text = t.FloorMaxSeconds.ToString();
        ClearIdleBox.Text = t.FloorClearIdleSeconds.ToString();
        LoopIntervalBox.Text = t.LoopIntervalSeconds.ToString();
        LoopCheck.IsChecked = t.Loop;
        NoteBox.Text = t.Note;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        string map = MapBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(map))
        {
            Fail("目标地图名必填：填中文名（如 僵尸洞）或地图代码（如 D1101）。");
            return;
        }

        if (!TryPositive(LevelBox.Text, 0, "最低等级", out int level)) return;
        if (!TryPositive(DepthBox.Text, 1, "最多层数", out int depth)) return;
        if (!TryPositive(FloorMaxBox.Text, 10, "每层最长秒数", out int floorMax)) return;
        if (!TryPositive(ClearIdleBox.Text, 1, "空场判定秒数", out int clearIdle)) return;
        if (!TryPositive(LoopIntervalBox.Text, 1, "轮间间隔秒数", out int loopInterval)) return;

        Result.Name = NameBox.Text.Trim();
        Result.TargetMapText = map;
        Result.NpcKeyword = NpcBox.Text.Trim();
        Result.MenuPath = MenuBox.Text
            .Split(new[] { ',', '，', '>', '→' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        Result.MinLevel = level;
        Result.Descend = DescendCheck.IsChecked == true;
        Result.MaxDepth = depth;
        Result.FloorMaxSeconds = floorMax;
        Result.FloorClearIdleSeconds = clearIdle;
        Result.LoopIntervalSeconds = loopInterval;
        Result.Loop = LoopCheck.IsChecked == true;
        Result.Note = NoteBox.Text.Trim();

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private bool TryPositive(string raw, int min, string label, out int value)
    {
        if (!int.TryParse((raw ?? string.Empty).Trim(), out value))
        {
            Fail($"{label}必须是数字。");
            return false;
        }

        if (value < min)
        {
            Fail($"{label}不能小于 {min}。");
            return false;
        }

        return true;
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
    }
}
