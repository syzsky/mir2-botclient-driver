using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BotClient.ClientDriver.Sniff;
using BotClientDriverHostUi.Host;

namespace BotClientDriverHostUi.Views;

/// <summary>
/// 客户端选择器：一次扫描列出本机**所有**候选传奇客户端（不限某一个引擎/某一个进程名），
/// 由人工选定要跟随的那一条。选定后钉住 PID（<c>TargetPid</c>），并可选做一次只读的
/// 帧定界自动探测（换引擎/换服时用，全程零点击）。
///
/// 界面自己不做任何协议判断，候选与探测全部来自 ClientDiscovery / ClientDriverHost。
/// </summary>
public partial class ClientPickerWindow : Window
{
    private readonly HostRunner _runner;
    private bool _busy;

    public ClientPickerWindow(HostRunner runner)
    {
        InitializeComponent();
        _runner = runner;
        Loaded += (_, _) => Rescan();
    }

    /// <summary>用户最终选中的候选（未选为 null）。</summary>
    public ClientCandidate? Selected { get; private set; }

    private void Rescan()
    {
        var list = _runner.ScanClients();
        CandidateList.ItemsSource = list;
        CountText.Text = $"候选 {list.Count} 条" + (_runner.Driver.TargetPid > 0
            ? $"（已锁定 pid={_runner.Driver.TargetPid}）"
            : "（自动挑选）");
        if (list.Count > 0 && CandidateList.SelectedIndex < 0) CandidateList.SelectedIndex = 0;
    }

    private void SetButtons()
    {
        RescanBtn.IsEnabled = !_busy;
        AutoFrameBtn.IsEnabled = !_busy;
        UnpinBtn.IsEnabled = !_busy;
        ApplyBtn.IsEnabled = !_busy;
        CandidateList.IsEnabled = !_busy;
        Cursor = _busy ? Cursors.Wait : null;
    }

    private void OnRescanClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Rescan();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e) => Apply();

    private void OnApplyClick(object sender, RoutedEventArgs e) => Apply();

    private async void Apply()
    {
        if (_busy) return;
        if (CandidateList.SelectedItem is not ClientCandidate cand)
        {
            HintText.Text = "先在上面选一条候选（双击该行也可以）。候选为空 = 客户端还没启动或还没点登录。";
            return;
        }

        _busy = true;
        SetButtons();
        try
        {
            Selected = cand;
            await Task.Run(() => _runner.ApplyCandidate(cand));
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            HintText.Text = "应用失败: " + ex.Message;
        }
        finally
        {
            _busy = false;
            SetButtons();
        }
    }

    private async void OnAutoFrameClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SetButtons();
        try
        {
            HintText.Text = "只读采样中（15 秒，零点击）…期间请在客户端里走两步、开个背包，让数据包出现。";
            int rc = await _runner.AutoFrameAsync(15);
            HintText.Text = rc == 0
                ? "定界探测成功，已写回 clientdriver.json（细节见主界面日志的 [framing] 行）。"
                : $"探测未成功：返回码 {rc}（4=样本不足，5=未得到可信定界）——细节见主界面日志。";
        }
        catch (Exception ex)
        {
            HintText.Text = "自动定界异常: " + ex.Message;
        }
        finally
        {
            _busy = false;
            SetButtons();
            Rescan();
        }
    }

    private void OnUnpinClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _runner.ClearCandidatePin();
        HintText.Text = "已取消 PID 锁定，恢复自动识别（重新扫描后按评分挑）。";
        Rescan();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
