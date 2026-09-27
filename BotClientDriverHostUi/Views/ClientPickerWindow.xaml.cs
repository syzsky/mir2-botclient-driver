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

    private void Rescan(bool? includeAllWindows = null)
    {
        _all = _runner.ScanClients(includeAllWindows: includeAllWindows ?? AllWindowsChk.IsChecked == true);
        ApplyFilter();
    }

    /// <summary>按"只看疑似游戏客户端"勾选状态刷新列表；默认把疑似登录器/未知行也列出来（方便对照）。</summary>
    private void ApplyFilter()
    {
        bool gameOnly = GameOnlyChk.IsChecked == true;
        var shown = gameOnly ? _all.Where(c => c.LikelyGame).ToList() : _all;

        CandidateList.ItemsSource = shown;

        int launcherLike = _all.Count(c => !c.LikelyGame);
        int notConnected = _all.Count(c => c.ServerPort == 0);
        CountText.Text = $"候选 {_all.Count} 条（游戏 {_all.Count - launcherLike} · 疑似登录器/未知 {launcherLike} · 尚未连接 {notConnected}）" +
            (_runner.Driver.TargetHwnd != 0
                ? $"（已绑定 句柄=0x{_runner.Driver.TargetHwnd:X} / pid={_runner.Driver.TargetPid}）"
                : _runner.Driver.TargetPid > 0 ? $"（已锁定 pid={_runner.Driver.TargetPid}，未绑定句柄）" : "（自动挑选）");

        // 已经钉过的那条自动选中：重扫/切筛选后不用再手点一次
        if (CandidateList.SelectedIndex < 0 && shown.Count > 0)
        {
            int pinned = shown.FindIndex(c => _runner.Driver.TargetHwnd != 0 && c.Hwnd == _runner.Driver.TargetHwnd);
            if (pinned < 0) pinned = shown.FindIndex(c => _runner.Driver.TargetPid > 0 && c.Pid == _runner.Driver.TargetPid);
            CandidateList.SelectedIndex = pinned >= 0 ? pinned : 0;
        }

        if (shown.Count == 0 && _all.Count > 0 && gameOnly)
            HintText.Text = "候选有 " + _all.Count + " 条，但当前勾着「只看疑似游戏客户端」全被过滤掉了：" +
                            "取消该勾选看全部候选，或勾上「显示全部窗口（兜底）」把本机所有可见窗口都列出来手工挑。";
        else if (_all.Count == 0)
            HintText.Text = "扫描结果为空（0 条候选）。" + ClientDiscovery.LastDiagnostics +
                            " 兜底办法：勾上「显示全部窗口（兜底）」，把本机所有可见顶层窗口都列出来直接挑。";
        else
            HintText.Text = $"候选 {_all.Count} 条；已按分数排序，默认选中的就是自动挑选的那条（第 1 条）。" +
                            "「尚未连接」的行说明客户端停在登录界面 —— 现在就能选它，进游戏后抓包会自动跟随。";
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    /// <summary>
    /// 「显示全部窗口（兜底）」：切换候选来源到"本机所有可见顶层窗口"后重扫。
    /// 个别引擎的客户端即使用硬结构判据也没认出来时，用这个把全部窗口列出来人工挑 —— 保证"能连上"。
    /// </summary>
    private void OnAllWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        // 只作用于本次扫描（见 ClientDiscovery.Discover 的说明）：
        // 以前这里写的是静态开关，勾一次就永久生效，会污染自动跟随路径。
        Rescan(AllWindowsChk.IsChecked == true);
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
