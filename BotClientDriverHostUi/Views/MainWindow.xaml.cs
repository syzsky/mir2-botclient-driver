using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BotClient.ClientDriver;
using BotClient.Session;
using BotClientDriverHostUi.Host;

namespace BotClientDriverHostUi.Views;

/// <summary>
/// 主界面：C 方案（客户端驱动挂机）唯一的人工操作台。
///
/// 数据来源全部是**现有宿主的数据源**，界面不做任何协议解析：
///   · ClientDriverHost —— 嗅探/注入/AutoConfigure/SelfCheck/Input/Ui/Identity
///   · BotRuntime       —— 角色状态、地图、视野内实体（Dots）
///   · BotCombatAI      —— 挂机参数与日志
/// 这样界面与无界面宿主看到的是同一份事实，出问题能直接对着控制台版排障。
/// </summary>
public partial class MainWindow : Window
{
    private const int ObserveRange = 14;
    private const int MaxLogLines = 800;
    private const int MaxListRows = 200;

    private readonly HostRunner _runner;
    private readonly ObservableCollection<LogLine> _logs = new();
    private readonly ObservableCollection<EntityRow> _monsters = new();
    private readonly ObservableCollection<EntityRow> _npcs = new();
    private readonly ObservableCollection<EntityRow> _items = new();
    private readonly ObservableCollection<EntityRow> _players = new();
    private readonly DispatcherTimer _timer;

    private bool _busy;
    private int _tick;

    public MainWindow()
    {
        InitializeComponent();

        _runner = new HostRunner(AppContext.BaseDirectory);
        _runner.Log += OnRunnerLog;
        _runner.StatusChanged += () => Ui(RefreshStatusText);

        LogList.ItemsSource = _logs;
        MonsterList.ItemsSource = _monsters;
        NpcList.ItemsSource = _npcs;
        ItemList.ItemsSource = _items;
        PlayerList.ItemsSource = _players;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, OnTick, Dispatcher);
        _timer.Start();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ------------------------------------------------------------------ 启动/关闭

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        ApplyTitle();
        HintText.Text = BuildHint();
        RefreshStatusText();
        AppendLog("[ui] BotClientDriverHostUi 已启动，工作目录 " + AppContext.BaseDirectory);
        AppendLog("[ui] 若出现“device is not open”：先确认 Npcap 已安装（勾 WinPcap 兼容模式）并已用管理员运行；" +
                  "本版本已改为先打开抓包设备再设 BPF 过滤，失败时会给出具体提示而不是直接退出。");

        await StartAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        try
        {
            _runner.RequestStop();
        }
        catch
        {
            // 忽略
        }

        try
        {
            // 在后台线程释放抓包/会话，避免界面线程被同步等待卡住；最多等 2.5s，随后进程直接退出
            Task.Run(() => _runner.StopAsync()).Wait(2500);
        }
        catch
        {
            // 忽略
        }
    }

    private async Task StartAsync()
    {
        if (_busy)
        {
            AppendLog("[ui] 上一条指令还在执行，请稍候");
            return;
        }

        _busy = true;
        SetButtons();
        try
        {
            _runner.SniffOnly = SniffOnlyCheck.IsChecked == true;
            AppendLog(_runner.SniffOnly
                ? "[ui] 启动（仅嗅探）：只验证抓包→状态镜像链路，不产生任何点击"
                : "[ui] 启动：复用模式，请在客户端里登录并进入角色");
            await _runner.StartAsync();
        }
        catch (Exception ex)
        {
            AppendLog("[错误] 启动异常: " + ex.Message);
        }
        finally
        {
            _busy = false;
            SetButtons();
            RefreshStatusText();
        }
    }

    private async Task StopAsync()
    {
        if (_busy)
        {
            AppendLog("[ui] 上一条指令还在执行，请稍候");
            return;
        }

        _busy = true;
        SetButtons();
        try
        {
            await Task.Run(() => _runner.StopAsync());
        }
        catch (Exception ex)
        {
            AppendLog("[错误] 停止异常: " + ex.Message);
        }
        finally
        {
            _busy = false;
            SetButtons();
            RefreshStatusText();
        }
    }

    // ------------------------------------------------------------------ 定时刷新

    private void OnTick(object? sender, EventArgs e)
    {
        _tick++;

        BotRuntime? runtime = _runner.Runtime;
        ClientDriverHost? host = _runner.Host;

        if (runtime != null)
        {
            BotPlayerState p = runtime.Player;
            ChipChar.Text = string.IsNullOrWhiteSpace(p.Name) ? "（等待角色信息）" : p.Name;
            ChipLevel.Text = p.Level.ToString();
            string map = string.IsNullOrWhiteSpace(runtime.CurrentMap) ? p.MapName : runtime.CurrentMap;
            ChipMap.Text = string.IsNullOrWhiteSpace(map) ? "—" : map;
            ChipPos.Text = $"({p.PosX},{p.PosY})";
            ChipHp.Text = p.MaxHp > 0 ? $"{p.Hp}/{p.MaxHp}" : p.Hp.ToString();
            ChipMp.Text = p.MaxMp > 0 ? $"{p.Mp}/{p.MaxMp}" : p.Mp.ToString();
            ChipGold.Text = p.Gold.ToString();
            Observe.Update(p.PosX, p.PosY, runtime.Dots.Values, ObserveRange);
        }
        else
        {
            Observe.Update(0, 0, null, ObserveRange);
        }

        ChipSniff.Text = _runner.SniffState;
        ChipUi.Text = host?.Ui?.State.ToString() ?? "—";
        ChipCal.Text = _runner.Driver.View.IsCalibrated ? "已校准" : "未校准";

        if (_tick % 2 == 0) RefreshLists();
        if (_tick % 6 == 0) RefreshStatusText();
    }

    private void RefreshLists()
    {
        BotRuntime? runtime = _runner.Runtime;
        if (runtime == null)
        {
            _monsters.Clear();
            _npcs.Clear();
            _items.Clear();
            _players.Clear();
            return;
        }

        int px = runtime.Player.PosX;
        int py = runtime.Player.PosY;

        var monsters = new List<EntityRow>();
        var npcs = new List<EntityRow>();
        var items = new List<EntityRow>();
        var players = new List<EntityRow>();

        foreach (MapDot dot in runtime.Dots.Values)
        {
            if (dot.Kind == DotKind.Self) continue;

            int distance = Math.Max(Math.Abs(dot.X - px), Math.Abs(dot.Y - py));
            var row = new EntityRow(dot.Name, distance, dot.X, dot.Y);

            switch (dot.Kind)
            {
                case DotKind.Monster:
                    monsters.Add(row);
                    break;
                case DotKind.Npc:
                    npcs.Add(row);
                    break;
                case DotKind.Player:
                    players.Add(row);
                    break;
                default:
                    items.Add(row);
                    break;
            }
        }

        Fill(_monsters, monsters);
        Fill(_npcs, npcs);
        Fill(_items, items);
        Fill(_players, players);
    }

    private static void Fill(ObservableCollection<EntityRow> target, List<EntityRow> source)
    {
        source.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (source.Count > MaxListRows) source.RemoveRange(MaxListRows, source.Count - MaxListRows);

        bool same = target.Count == source.Count;
        if (same)
        {
            for (int i = 0; i < source.Count; i++)
            {
                if (!ReferenceEquals(target[i], source[i]))
                {
                    same = false;
                    break;
                }
            }
        }

        if (same) return;

        target.Clear();
        foreach (EntityRow row in source) target.Add(row);
    }

    private void RefreshStatusText()
    {
        try
        {
            StatusText.Text = _runner.BuildStatusText();
        }
        catch (Exception ex)
        {
            StatusText.Text = "状态摘要生成失败: " + ex.Message;
        }
    }

    private void ApplyTitle()
    {
        string suffix = _runner.Identity.IsEmpty ? string.Empty : "  " + _runner.Identity.Prefix;
        Title = "传奇 · 客户端驱动模式 · 主界面" + suffix;
        SubTitleText.Text = _runner.Identity.IsEmpty
            ? "复用模式：账号/密码/服务端地址取自你自己的客户端登录过程"
            : "实例标识 " + _runner.Identity.Describe() + "（多开时用来区分不同窗口/角色）";
    }

    private string BuildHint()
    {
        return "F8 设置 · 需管理员 + Npcap（首次运行会拉起安装向导）· 视图校准：BotClientDriverHost.exe --calibrate --quick，"
             + "自动收敛：--autocalibrate · 配置文件目录：" + _runner.ConfigDir;
    }

    private void SetButtons()
    {
        bool running = _runner.IsRunning;
        StartBtn.IsEnabled = !_busy && !running;
        StopBtn.IsEnabled = !_busy && running;
        RediscoverBtn.IsEnabled = !_busy;
        ScanClientsBtn.IsEnabled = !_busy;
        SelfCheckBtn.IsEnabled = !_busy;
        NpcapBtn.IsEnabled = !_busy;
    }

    // ------------------------------------------------------------------ 日志

    private void OnRunnerLog(string message) => Ui(() => AppendLog(message));

    private void AppendLog(string message)
    {
        _logs.Add(new LogLine(message));
        while (_logs.Count > MaxLogLines) _logs.RemoveAt(0);

        if (AutoScrollCheck.IsChecked == true && _logs.Count > 0)
        {
            LogList.ScrollIntoView(_logs[_logs.Count - 1]);
        }
    }

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    // ------------------------------------------------------------------ 按钮

    private async void OnStartClick(object sender, RoutedEventArgs e) => await StartAsync();

    private async void OnStopClick(object sender, RoutedEventArgs e) => await StopAsync();

    private async void OnRediscoverClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SetButtons();
        try
        {
            await Task.Run(() => _runner.ReDiscover());
        }
        catch (Exception ex)
        {
            AppendLog("[错误] 重新识别异常: " + ex.Message);
        }
        finally
        {
            _busy = false;
            SetButtons();
            ApplyTitle();
            RefreshStatusText();
        }
    }

    private void OnSelfCheckClick(object sender, RoutedEventArgs e) => _runner.RunSelfCheck();

    /// <summary>
    /// 扫描客户端：一次列出本机所有候选客户端进程（各引擎通用；同名多开逐条列出各自连接），
    /// 由人工选定要跟随的那一条，选定后钉住 PID。比"自动挑评分最高的"更适合多开/多引擎场景。
    /// </summary>
    private void OnScanClientsClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            var picker = new ClientPickerWindow(_runner) { Owner = this };
            if (picker.ShowDialog() == true && picker.Selected != null)
            {
                AppendLog($"[选择] 目标客户端：{picker.Selected.ProcessName}(pid={picker.Selected.Pid}) → " +
                          $"{picker.Selected.ServerIp}:{picker.Selected.ServerPort}");
                ApplyTitle();
                RefreshStatusText();
            }
        }
        catch (Exception ex)
        {
            AppendLog("[错误] 打开客户端选择器失败: " + ex.Message);
        }
    }

    private async void OnNpcapClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SetButtons();
        try
        {
            await Task.Run(() => _runner.CheckNpcap());
        }
        catch (Exception ex)
        {
            AppendLog("[错误] 抓包检测异常: " + ex.Message);
        }
        finally
        {
            _busy = false;
            SetButtons();
            RefreshStatusText();
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        _logs.Clear();
        AppendLog("[ui] 日志已清空（仅清界面，不影响文件）");
    }

    private void OnApplyFightPointClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(FightXBox.Text.Trim(), out int x) || !int.TryParse(FightYBox.Text.Trim(), out int y))
        {
            AppendLog("[ui] 定点坐标必须是整数");
            return;
        }

        _runner.SetFightPoint(x, y);
    }

    private void OnClearFightPointClick(object sender, RoutedEventArgs e) => _runner.ClearFightPoint();

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "C 方案 · 客户端驱动挂机（图形宿主）\n\n" +
            "1) 正常启动你自己的传奇客户端，登录并进入角色所在地图；\n" +
            "2) 本程序自动识别客户端进程/服务端连接，并把动作接管到鼠标键盘；\n" +
            "3) 首次使用请先跑一次视图校准：\n" +
            "   BotClientDriverHost.exe --calibrate --quick（量一次即可，之后自动套用）\n\n" +
            "注意：\n" +
            "· 需要管理员权限（抓包 + 注入按键）；\n" +
            "· 需要 Npcap（安装时勾选 WinPcap API-compatible Mode），缺失时首次运行会拉起随包安装向导；\n" +
            "· 本程序不登录、不写 socket：登录凭据只在内存中复用，不落盘；\n" +
            "· 想先只验证抓包链路：勾上“仅嗅探”，此模式不产生任何点击。\n\n" +
            "配置文件（与本目录下无界面宿主共用）：\n" +
            _runner.SettingsPath + "\n" + _runner.DriverPath,
            "帮助",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(_runner.Settings) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _runner.ApplySettings();
            RefreshStatusText();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F8)
        {
            OpenSettings();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
