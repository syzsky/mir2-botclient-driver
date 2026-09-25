using System.Linq;
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
/// 界面自己不做协议判断：候选与"游戏 / 疑似登录器"的类型判定全部来自 ClientDiscovery
/// （渲染窗特征 + 非 HTTP 端口 + 进程父子关系，判据与窗口分辨率无关），这里只负责呈现与筛选。
/// </summary>
public partial class ClientPickerWindow : Window
{
    private readonly HostRunner _runner;
    private List<ClientCandidate> _all = new();
    private int _warnedPid;
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
        _all = _runner.ScanClients();
        ApplyFilter();
    }

    /// <summary>按"只看疑似游戏客户端"勾选状态刷新列表；默认把疑似登录器/未知行也列出来（方便对照）。</summary>
    private void ApplyFilter()
    {
        bool gameOnly = GameOnlyChk.IsChecked == true;
        var shown = gameOnly ? _all.Where(c => c.LikelyGame).ToList() : _all;

        CandidateList.ItemsSource = shown;

        int launcherLike = _all.Count(c => !c.LikelyGame);
        CountText.Text = $"候选 {_all.Count} 条（疑似登录器/未知 {launcherLike} 条）" +
            (_runner.Driver.TargetHwnd != 0
                ? $"（已绑定 句柄=0x{_runner.Driver.TargetHwnd:X} / pid={_runner.Driver.TargetPid}）"
                : _runner.Driver.TargetPid > 0 ? $"（已锁定 pid={_runner.Driver.TargetPid}，未绑定句柄）" : "（自动挑选）");

        if (shown.Count > 0 && CandidateList.SelectedIndex < 0) CandidateList.SelectedIndex = 0;
        if (shown.Count == 0 && _all.Count > 0)
            HintText.Text = "按当前筛选没有『游戏』类型的候选——说明只有登录器/更新器的连接，" +
                            "请把游戏客户端启动并登录进游戏后再点「重新扫描」。";
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

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

        // 防呆：选到"疑似登录器"时先提示一次，再点一次才真正应用
        if (!cand.LikelyGame && _warnedPid != cand.Pid)
        {
            _warnedPid = cand.Pid;
            var gameLike = _all.Where(c => c.LikelyGame).OrderByDescending(c => c.Score).FirstOrDefault();
            HintText.Text =
                $"注意：pid={cand.Pid} {cand.ProcessName} 判为「{cand.Kind}」（{cand.PortKind}，窗口 {cand.HwndText} {cand.WindowSize}）。" +
                (gameLike != null
                    ? $"更像游戏本体的是 pid={gameLike.Pid} {gameLike.ProcessName}（句柄 {gameLike.HwndText}，窗口 {gameLike.WindowSize}，评分 {gameLike.Score}）。"
                    : "当前列表里没有更像游戏本体的行——请确认客户端已登录进游戏。") +
                " 确认仍要跟随这条，请再点一次「选定并跟随」。";
            return;
        }
        _warnedPid = 0;

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
        HintText.Text = "已取消 PID/句柄绑定，恢复自动识别（重新扫描后按评分挑）。";
        Rescan();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
