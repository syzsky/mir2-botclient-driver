using System.Text;
using BotClient.ClientDriver;
using BotClient.Net;
using BotClient.Protocol;
using BotClient.Session;

namespace BotClientDriverHost;

/// <summary>
/// C 方案（客户端驱动挂机）无界面宿主。
///
/// 职责边界（刻意保持极窄）：
///   1. 自动登录走的是**原有直连通道**（BotLoginFlow）—— 这一步不伪造客户端，只把人物送进地图；
///   2. 登录进图后立刻把动作通道交给 ClientDriver（嗅探 + 真机客户端点击），
///      此后 BotRuntime 的所有 Send* 都不再写任何字节到 socket；
///   3. AI 用的是原封不动的 BotCombatAI。
///
/// 运行前提：管理员权限 + 已安装 Npcap + 游戏客户端已进图且窗口可被定位。
/// </summary>
internal static class Program
{
    private static readonly CancellationTokenSource Shutdown = new();

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 某些终端不支持 */ }
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { /* 已注册 */ }

        Cli cli;
        try
        {
            cli = Cli.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[host] 参数错误: {ex.Message}");
            Cli.PrintHelp();
            return 2;
        }

        if (cli.ShowHelp)
        {
            Cli.PrintHelp();
            return 0;
        }

        string baseDir = AppContext.BaseDirectory;
        string settingsPath = cli.SettingsPath ?? Path.Combine(baseDir, "botsettings.json");
        string driverPath = cli.DriverPath ?? Path.Combine(baseDir, "clientdriver.json");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Say("[host] 收到 Ctrl+C，正在退出…");
            try { Shutdown.Cancel(); } catch { }
        };

        if (cli.Calibrate)
        {
            Say("[host] 校准模式：只量窗口与界面坐标，不登录、不嗅探");
            return CalibrationTool.Run(driverPath);
        }

        var settings = HostSettings.Load(settingsPath);

        ClientDriverConfig cfg;
        try
        {
            cfg = ClientDriverConfig.Load(driverPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[host] 读取驱动配置失败({driverPath}): {ex.Message}");
            return 1;
        }

        // 有账号（命令行或 --login）→ 老路径：宿主自己直连登录后接管。
        // 没有任何账号信息 → 默认走"复用模式"：不登录，一切从你自己的客户端登录过程里取。
        bool directLogin = cli.Login || !string.IsNullOrWhiteSpace(cli.Account);

        try
        {
            if (directLogin)
            {
                string account = cli.Account ?? settings.Account;
                string password = cli.Password
                                  ?? Environment.GetEnvironmentVariable("BOT_PASSWORD")
                                  ?? string.Empty;

                if (string.IsNullOrWhiteSpace(account))
                {
                    Console.Error.WriteLine("[host] 缺少账号：用 --account，或在 botsettings.json 里填 Account");
                    return 2;
                }

                if (string.IsNullOrWhiteSpace(password))
                {
                    Console.Error.WriteLine("[host] 缺少密码：用 --password，或设环境变量 BOT_PASSWORD（不落盘，推荐后者）");
                    return 2;
                }

                if (string.IsNullOrWhiteSpace(cfg.ServerIp))
                {
                    cfg.ServerIp = settings.Host;
                }

                return await RunAsync(cli, settings, cfg, account, password, Shutdown.Token);
            }

            Say("[host] 复用模式（零配置）：不自己登录 —— 账号、密码、服务端地址全部取自你自己的客户端登录过程");
            return await RunReuseAsync(cli, settings, cfg, driverPath, Shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            Say("[host] 已取消");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[host] 运行失败: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 启动嗅探。抓包是"环境依赖型"动作（要 Npcap + 管理员 + 有网卡），
    /// 失败时给出能照着做的提示，而不是把底层 DllNotFoundException 直接甩给用户。
    /// </summary>
    private static bool TryStartSniffing(ClientDriverHost host)
    {
        try
        {
            host.StartSniffing();
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException
                                   || ex.Message.Contains("抓包设备")
                                   || ex.GetType().Name.Contains("Pcap"))
        {
            Console.Error.WriteLine($"[host] 抓包初始化失败: {ex.Message}");
            Console.Error.WriteLine("[host] 请依次确认：");
            Console.Error.WriteLine("        ① 已安装 Npcap，且安装时勾选 \"WinPcap API-compatible Mode\"（装了 WinPcap 不算）；");
            Console.Error.WriteLine("        ② 以管理员身份运行（抓包必须）；");
            Console.Error.WriteLine("        ③ 机器至少有一块已分配 IPv4 的网卡（虚拟机请确认网卡桥接/NAT 正常）。");
            return false;
        }
    }

    /// <summary>
    /// 复用模式：宿主**不登录、不碰账号**，只做三件事：
    ///   ① 认出哪个进程是客户端（进程名 / 窗口）；
    ///   ② 跟随该客户端真实的服务端连接（ServerIp 与端口无需手填）；
    ///   ③ 从客户端自己的登录包里还原账号密码（用户下一次点"登录"时抓到，仅存内存）。
    /// 用户侧唯一要做的事：打开客户端 → 登录 → 创建/选择角色。
    /// </summary>
    private static async Task<int> RunReuseAsync(Cli cli, HostSettings settings, ClientDriverConfig cfg,
        string driverPath, CancellationToken ct)
    {
        var session = new BotSession();
        var runtime = new BotRuntime(session);

        runtime.Log += m => Say("[runtime] " + m);
        runtime.SystemMessage += m => Say("[系统] " + m);
        runtime.MapChanged += () =>
            Say($"[地图] {runtime.CurrentMap}｜{runtime.Player.MapName}｜({runtime.Player.PosX},{runtime.Player.PosY})");
        runtime.Died += () => Say("[危险] 角色死亡");
        runtime.LevelUp += lv => Say($"[成长] 等级 → {lv}");
        runtime.StartReceiveLoop(ct);

        var host = new ClientDriverHost(cfg);
        host.Log += Say;

        Say("[host] 自动识别客户端进程与服务端地址…");
        if (host.AutoConfigure())
        {
            try
            {
                cfg.Save(driverPath);
                Say($"[host] 识别结果已写回 {driverPath}（下次启动可直接复用，仍可留空）");
            }
            catch (Exception ex)
            {
                Say($"[host] 写回配置失败（不影响本次运行）: {ex.Message}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(settings.Host))
        {
            cfg.ServerIp = settings.Host;
            Say($"[host] 自动识别未命中，退回 botsettings.json 的 Host={settings.Host}");
        }

        host.Credentials.Captured += cred =>
            Say($"[host] 已复用客户端登录凭据：{cred.Describe()}（仅存内存，不写入任何文件）");

        host.Attach(new Attachment(session, runtime));
        runtime.NpcMessage += (id, text) => host.FeedNpcDialog(id, text);
        runtime.SystemMessage += text => host.FeedSystemMessage(text);
        if (!TryStartSniffing(host)) return 3;

        Say(host.SelfCheck());

        if (host.Credentials.Latest == null)
        {
            Say("[host] 提示：账号密码在你**下一次点登录**（含掉线重连、切角色回登录界面）时抓取；" +
                "本次若已是登录状态，不影响挂机，只是这次拿不到历史登录包。");
        }

        var ai = new BotCombatAI(session, runtime);
        settings.ApplyTo(ai);
        ai.Log += m => Say("[ai] " + m);
        if (cli.FightPointX >= 0 && cli.FightPointY >= 0)
        {
            ai.FightAtPoint = true;
            ai.FightPointX = cli.FightPointX;
            ai.FightPointY = cli.FightPointY;
            Say($"[host] 定点挂机: ({cli.FightPointX},{cli.FightPointY})");
        }

        if (cli.SniffOnly)
        {
            Say("[host] --sniff-only：只做嗅探与状态镜像，不产生任何点击（用于先验证抓包链路）");
        }
        else
        {
            ai.Start();
            Say("[host] AI 已启动（坐标/地图由客户端真实封包提供）；Ctrl+C 退出");
        }

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // 正常退出路径
        }

        ai.Stop();
        await host.DisposeAsync();
        await session.DisposeAsync();
        Say("[host] 已退出");
        return 0;
    }

    private static async Task<int> RunAsync(Cli cli, HostSettings settings, ClientDriverConfig cfg,
        string account, string password, CancellationToken ct)
    {
        var session = new BotSession();
        var runtime = new BotRuntime(session);
        var flow = new BotLoginFlow(session);
        flow.Log += Say;
        flow.Progress += Say;

        Say($"[host] 连接并登录 {account}@{settings.Host}:{settings.Port}");
        var passOk = await flow.ConnectAndLoginAsync(settings.Host, settings.Port, account, password, ct);
        Say("[host] 账号校验通过，开始选区服");

        var servers = BotLoginFlow.ParseServerList(passOk);
        string server = FirstNonEmpty(cli.Server, settings.ServerName);
        if (string.IsNullOrWhiteSpace(server))
        {
            server = servers.FirstOrDefault()
                     ?? throw new InvalidOperationException("服务端未返回区服列表，且配置里没有 ServerName");
        }
        else if (servers.Count > 0 && !servers.Contains(server))
        {
            Say($"[host] 警告：区服列表（{string.Join("、", servers)}）里没有 \"{server}\"，仍按该名字尝试");
        }

        Say($"[host] 选择区服: {server}");
        await flow.SelectServerAsync(passOk, server, ct);

        var characters = await flow.WaitForCharacterListAsync(ct);
        string character = FirstNonEmpty(cli.Character, settings.CharacterName);
        if (string.IsNullOrWhiteSpace(character))
        {
            if (characters.Count == 0)
            {
                throw new InvalidOperationException("服务端未返回角色列表：该账号可能还没有角色，需要先建号");
            }

            character = characters[0];
            Say($"[host] 未指定角色，取第一个: {character}（可选: {string.Join("、", characters)}）");
        }
        else if (characters.Count > 0 && !characters.Contains(character))
        {
            throw new InvalidOperationException($"角色 \"{character}\" 不在列表里（可选: {string.Join("、", characters)}）");
        }

        Say($"[host] 选择角色: {character}");
        await flow.SelectCharacterAsync(character, ct);

        // 进图后：状态镜像 + 动作接管
        runtime.Player.Name = character;
        runtime.Log += m => Say("[runtime] " + m);
        runtime.SystemMessage += m => Say("[系统] " + m);
        runtime.MapChanged += () =>
            Say($"[地图] {runtime.CurrentMap}｜{runtime.Player.MapName}｜({runtime.Player.PosX},{runtime.Player.PosY})");
        runtime.Died += () => Say("[危险] 角色死亡");
        runtime.LevelUp += lv => Say($"[成长] 等级 → {lv}");
        runtime.StartReceiveLoop(ct);

        var host = new ClientDriverHost(cfg);
        host.Log += Say;
        host.Attach(new Attachment(session, runtime));
        runtime.NpcMessage += (id, text) => host.FeedNpcDialog(id, text);
        runtime.SystemMessage += text => host.FeedSystemMessage(text);
        if (!TryStartSniffing(host)) return 3;

        Say(host.SelfCheck());

        var ai = new BotCombatAI(session, runtime);
        settings.ApplyTo(ai);
        ai.Log += m => Say("[ai] " + m);
        if (cli.FightPointX >= 0 && cli.FightPointY >= 0)
        {
            ai.FightAtPoint = true;
            ai.FightPointX = cli.FightPointX;
            ai.FightPointY = cli.FightPointY;
            Say($"[host] 定点挂机: ({cli.FightPointX},{cli.FightPointY})");
        }

        if (cli.SniffOnly)
        {
            Say("[host] --sniff-only：只做嗅探与状态镜像，不产生任何点击（用于先验证抓包链路）");
        }
        else
        {
            ai.Start();
            Say("[host] AI 已启动；Ctrl+C 退出");
        }

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // 正常退出路径
        }

        ai.Stop();
        await host.DisposeAsync();
        await session.DisposeAsync();
        Say("[host] 已退出");
        return 0;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }

        return string.Empty;
    }

    internal static void Say(string message) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
}

/// <summary>
/// ISessionAttachment 实现：把抓包解析出的服务端帧注入 BotRuntime（状态镜像），
/// 并把动作驱动 / 兜底通道挂到 BotSession 上。
/// </summary>
internal sealed class Attachment : ISessionAttachment
{
    private readonly BotSession _session;
    private readonly BotRuntime _runtime;

    public Attachment(BotSession session, BotRuntime runtime)
    {
        _session = session;
        _runtime = runtime;
    }

    /// <summary>最近一条服务端命令码（诊断用）。</summary>
    public ushort LastServerCommand { get; private set; }

    public void SetDriver(IClientDriver driver)
    {
        _session.Driver = driver;
        Program.Say("[host] 动作通道已接管：BotRuntime 的 Send* 将转为鼠标/键盘操作，不再写 socket");
    }

    public void SetSink(IUpstreamSink sink)
    {
        _session.Sink = sink;
        Program.Say("[host] 兜底通道已挂上：未被驱动接管的 payload 只计数、不发送");
    }

    public void Inject(MirIncomingFrame frame) => _session.InjectPacket(frame);

    public void OnServerCommand(ushort cmd, CmdPack pack) => LastServerCommand = cmd;

    public (int X, int Y) PlayerPosition => (_runtime.Player.PosX, _runtime.Player.PosY);

    public string CurrentMap => _runtime.CurrentMap;
}

/// <summary>命令行参数。</summary>
internal sealed class Cli
{
    public string? Account { get; private set; }

    public string? Password { get; private set; }

    public string? Server { get; private set; }

    public string? Character { get; private set; }

    public string? SettingsPath { get; private set; }

    public string? DriverPath { get; private set; }

    public bool Calibrate { get; private set; }

    public bool SniffOnly { get; private set; }

    /// <summary>强制走"宿主自己直连登录"的老路径（需要账号密码）。</summary>
    public bool Login { get; private set; }

    public bool ShowHelp { get; private set; }

    public int FightPointX { get; private set; } = -1;

    public int FightPointY { get; private set; } = -1;

    public static Cli Parse(string[] args)
    {
        var cli = new Cli();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            string Next()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{arg} 后面缺少取值");
                }

                return args[++i];
            }

            switch (arg)
            {
                case "-h":
                case "--help":
                    cli.ShowHelp = true;
                    break;
                case "--account":
                    cli.Account = Next();
                    break;
                case "--password":
                    cli.Password = Next();
                    break;
                case "--server":
                    cli.Server = Next();
                    break;
                case "--character":
                    cli.Character = Next();
                    break;
                case "--settings":
                    cli.SettingsPath = Path.GetFullPath(Next());
                    break;
                case "--driver-config":
                    cli.DriverPath = Path.GetFullPath(Next());
                    break;
                case "--fight-x":
                    cli.FightPointX = int.Parse(Next());
                    break;
                case "--fight-y":
                    cli.FightPointY = int.Parse(Next());
                    break;
                case "--calibrate":
                    cli.Calibrate = true;
                    break;
                case "--sniff-only":
                    cli.SniffOnly = true;
                    break;
                case "--login":
                case "--direct":
                    cli.Login = true;
                    break;
                default:
                    throw new ArgumentException($"未知参数: {arg}");
            }
        }

        return cli;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            BotClientDriverHost —— C 方案（客户端驱动挂机）无界面宿主

            用法:
              BotClientDriverHost.exe [选项]

            默认（复用模式，推荐）:
              你只要自己打开官方客户端 → 登录 → 创建/进入角色，然后运行本程序即可。
              不需要填账号、密码、服务端 IP、进程名 —— 宿主会自动：
                · 认出正在运行的客户端进程（ProcessName）
                · 跟随它对服务端的真实 TCP 连接（ServerIp / 端口）
                · 从客户端自己的登录包里还原账号密码（你下一次点"登录"时抓到，仅存内存）

            可选（老路径，宿主自己登录）:
              --login                 强制宿主直连登录（等价于显式提供了账号）
              --account <账号>        账号（提供即视为走老路径）
              --password <密码>       密码（建议改用环境变量 BOT_PASSWORD，避免落进命令行历史）
              --server <区服名>       区服（老路径用；缺省取 botsettings.json 的 ServerName，再缺省取第一个）
              --character <角色名>    角色（老路径用；缺省取 botsettings.json 的 CharacterName，再缺省取第一个）

            文件:
              --settings <路径>       botsettings.json 路径（缺省为 exe 同目录）
              --driver-config <路径>  clientdriver.json 路径（缺省为 exe 同目录）

            模式:
              --calibrate             校准模式：量取视口/小地图/背包/对话框坐标并写回配置，不登录
              --sniff-only            只嗅探 + 状态镜像，不做任何点击（先验证抓包链路用）

            挂机:
              --fight-x <格> --fight-y <格>   定点挂机坐标（都不给则跟随 AI 默认行为）

            运行前提:
              1) 以管理员身份运行（manifest 已声明 requireAdministrator）
              2) 已安装 Npcap（安装时勾选 WinPcap 兼容模式）
              3) 游戏客户端已启动并登录（本宿主不改客户端文件、不改 IP、不介入连接）
            """);
    }
}
