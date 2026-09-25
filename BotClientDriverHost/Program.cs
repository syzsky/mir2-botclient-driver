using System.Text;
using BotClient.ClientDriver;
using BotClient.ClientDriver.Hunt;
using BotClient.ClientDriver.Sniff;
using BotClient.Human;
using BotClient.Net;
using BotClient.Protocol;
using BotClient.Session;
using BotClient.Session.Combat;

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

    /// <summary>程序所在目录：随包 Npcap 安装器、配置文件都以此为准。</summary>
    private static readonly string BaseDir = AppContext.BaseDirectory;

    /// <summary>
    /// 多开标识（服务器名-区名-角色名）：启动时装配，运行时随角色名补全。
    /// 只用于日志前缀 / 控制台窗口标题 / 自检报告 —— 让多开的每个实例一眼认得出来，不参与任何协议逻辑。
    /// </summary>
    private static InstanceIdentity Identity { get; set; } = new();

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

        string baseDir = BaseDir;
        string settingsPath = cli.SettingsPath ?? Path.Combine(baseDir, "botsettings.json");
        string driverPath = cli.DriverPath ?? Path.Combine(baseDir, "clientdriver.json");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Say("[host] 收到 Ctrl+C，正在退出…");
            try { Shutdown.Cancel(); } catch { }
        };

        // 多开标识先装配：日志前缀、窗口标题都依赖它（校准模式下也生效，便于确认量的是哪个客户端窗口）
        LoadIdentity(cli, driverPath);

        if (cli.Calibrate)
        {
            Say(cli.Quick
                ? "[host] 校准模式（快捷档 --quick）：只量视图区左上/右下两点，其余交给 --autocalibrate 自动收敛"
                : "[host] 校准模式：只量窗口与界面坐标，不登录、不嗅探");
            if (cli.Guide)
            {
                Say("[host] 分步引导已开启：每一步都会说明鼠标该移到哪、按哪个键，按键后立即提示正确/失败及原因");
            }

            return CalibrationTool.Run(driverPath, cli.LegacyKeys, cli.Quick, cli.Guide);
        }

        if (cli.InstallNpcap != null)
        {
            Say("[host] Npcap 安装模式：只处理抓包驱动，不登录、不挂机");
            return InstallNpcap(cli.InstallNpcap);
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

        // 拟真配置合一：主配置(botsettings.json)是唯一入口
        MergeHumanSettings(cli, settings, cfg);

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

                return await RunAsync(cli, settings, cfg, driverPath, account, password, Shutdown.Token);
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

    // ------------------------------------------------------------------ 多开标识

    /// <summary>
    /// 装配多开标识（服务器名-区名-角色名）：
    ///   ① 先读 clientdriver.json 的 identity 段（上次记住的，换服/重启继续用）；
    ///   ② 命令行 --server-name / --zone / --character 覆盖，有变化就写回配置。
    /// 窗口标题解析与运行时角色名补全在后面各自拿到窗口/角色名时再做（见 <see cref="MergeIdentity"/>）。
    /// 全程只影响日志前缀 / 窗口标题 / 自检报告，不参与任何协议逻辑。
    /// </summary>
    /// <summary>
    /// 把主配置(botsettings.json)里的拟真参数灌进驱动配置(clientdriver.json)。
    ///
    /// 为什么要合一：拟人档同时被两处读取 —— 战斗 AI（攻击节奏）与驱动层（鼠标轨迹/按键时长），
    /// 若两份文件各存一份，用户改了 A 没改 B 就会变成"看起来开了、实际没生效"，
    /// 这类"配置谜题"在真机排障时最难查。所以统一以主配置为准，只保留一个入口。
    /// 技能表同理（AI 读的是 HostSettings.Skills）。
    /// </summary>
    private static void MergeHumanSettings(Cli cli, HostSettings settings, ClientDriverConfig cfg)
    {
        cfg.Human = settings.Human ?? new HumanTuning();
        cfg.Skills = settings.Skills ?? new SkillRotationPlan();

        // 命令行是"本次运行的临时覆盖"，不改盘上 json —— 排障时要对比"拟真开/关"两种情况，
        // 不必反复编辑配置文件（改配置文件容易忘了改回去，那才是真麻烦）。
        if (cli.NoHuman) cfg.Human.Enabled = false;
        if (cli.EnableSkills)
        {
            cfg.Skills.Enabled = true;
            if (cfg.Skills.Slots is null || cfg.Skills.Slots.Count == 0)
            {
                cfg.Skills.Slots = SkillRotationPlan.Sample().Slots;
                Say("[拟真] 配置里没有技能表 → 用内置示例表（火墙/冰咆哮/灵魂火符）；" +
                    "名字按包含匹配，没学会的自动跳过、不会误发。");
            }
        }

        Say($"[拟真] 拟人化 {HumanTiming.Describe(cfg.Human)}");
        if (cfg.Skills.Enabled)
        {
            int n = cfg.Skills.Slots?.Count ?? 0;
            Say($"[拟真] 技能循环 开（{n} 个槽位，按优先级+条件选用；全不满足时退回物理/单法术）");
        }
        else
        {
            Say("[拟真] 技能循环 关（使用配置的单一法术或物理攻击）");
        }
    }

    /// <summary>
    /// 装配多开标识（服务器名-区名-角色名）：
    ///   ① 先读 clientdriver.json 的 identity 段（上次记住的，换服/重启继续用）；
    ///   ② 命令行 --server-name / --zone / --character 覆盖，有变化就写回配置。
    /// 窗口标题解析与运行时角色名补全在后面各自拿到窗口/角色名时再做（见 <see cref="MergeIdentity"/>）。
    /// 全程只影响日志前缀 / 窗口标题 / 自检报告，不参与任何协议逻辑。
    /// </summary>
    private static void LoadIdentity(Cli cli, string driverPath)
    {
        try
        {
            var cfg = ClientDriverConfig.Load(driverPath);
            Identity = cfg.Identity?.Clone() ?? new InstanceIdentity();

            bool changed = false;
            changed |= Assign(cli.ServerName, Identity.ServerName, v => Identity.ServerName = v);
            changed |= Assign(cli.Zone, Identity.ZoneName, v => Identity.ZoneName = v);
            changed |= Assign(cli.Character, Identity.CharacterName, v => Identity.CharacterName = v);

            if (changed)
            {
                cfg.Identity = Identity.Clone();
                cfg.Save(driverPath);
                Say($"[标识] 已按命令行写入并保存到 {driverPath}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[标识] 装配失败（只影响日志前缀，不影响挂机）: {ex.Message}");
        }

        ApplyConsoleTitle();
        if (!Identity.IsEmpty)
        {
            Say($"[标识] 本次实例: {Identity.Describe()}（服务器名-区名-角色名，用于多开时区分实例）");
        }
        else
        {
            Say("[标识] 未设置多开标识（三项都是 ?）—— 多开时建议指定：--server-name 服务器名 --zone 区名 --character 角色名");
        }
    }

    private static bool Assign(string? given, string current, Action<string> setter)
    {
        if (string.IsNullOrWhiteSpace(given)) return false;
        string v = given.Trim();
        if (v == current) return false;
        setter(v);
        return true;
    }

    /// <summary>把控制台窗口标题改成"标识 + 程序名"，多开时在任务栏/窗口列表里一眼能分开。</summary>
    private static void ApplyConsoleTitle()
    {
        try
        {
            Console.Title = Identity.IsEmpty ? "BotClientDriverHost" : $"{Identity.Prefix} BotClientDriverHost";
        }
        catch
        {
            // 输出被重定向（无控制台）时忽略
        }
    }

    /// <summary>
    /// 用新拿到的信息补齐标识的空缺项（已有值不覆盖），补齐就写回 clientdriver.json 并刷新窗口标题。
    /// 两个调用点：复用模式解析客户端窗口标题、运行时从状态通道拿到角色名、老路径拿到区服/角色名。
    /// </summary>
    private static void MergeIdentity(InstanceIdentity other, string driverPath, string source)
    {
        if (!Identity.FillFrom(other)) return;

        Say($"[标识] 已从{source}补全: {Identity.Describe()}");
        ApplyConsoleTitle();

        try
        {
            var cfg = ClientDriverConfig.Load(driverPath);
            cfg.Identity = Identity.Clone();
            cfg.Save(driverPath);
        }
        catch (Exception ex)
        {
            Say($"[标识] 写回配置失败（不影响本次运行）: {ex.Message}");
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
        catch (Exception ex) when (IsPcapFailure(ex))
        {
            Console.Error.WriteLine($"[host] 抓包初始化失败: {ex.Message}");

            // 兜底补装：万一启动闸门那次探测被安全软件干扰（或用户跳过了探测），
            // 这里再自动装一次，装完让用户重跑即可，不必自己去官网找安装器。
            if (!NpcapEnvironment.IsDriverPresent())
            {
                var probe = NpcapEnvironment.Probe(BaseDir, autoInstall: true);
                if (probe.Status == NpcapProbeStatus.InstalledNow)
                {
                    Say("[host] " + probe.Message);
                    Console.Error.WriteLine("[host] 请重新运行本程序（管理员）即可挂机。");
                    return false;
                }

                if (probe.Status is NpcapProbeStatus.InstallFailed or NpcapProbeStatus.Missing)
                {
                    Console.Error.WriteLine("[host] 自动补装未成功: " + probe.Message);
                }
            }

            Console.Error.WriteLine("[host] 请依次确认：");
            Console.Error.WriteLine("        ① 已安装 Npcap，且安装时勾选 \"WinPcap API-compatible Mode\"（装了 WinPcap 不算）；");
            Console.Error.WriteLine("        ② 以管理员身份运行（抓包必须）；");
            Console.Error.WriteLine("        ③ 机器至少有一块已分配 IPv4 的网卡（虚拟机请确认网卡桥接/NAT 正常）；");
            Console.Error.WriteLine("        ④ 或把官方 npcap-x.xx.exe 放到本程序同目录（npcap\\ 子目录也行），再执行 --install-npcap。");
            return false;
        }
    }

    /// <summary>
    /// Npcap 环境闸门（抓包前调用）：缺失就用随包安装器静默补装。
    /// 目的是让用户看到的不是 DllNotFoundException，而是"下一步该做什么"。
    /// </summary>
    private static bool EnsureNpcap(bool autoInstall)
    {
        var probe = NpcapEnvironment.Probe(BaseDir, autoInstall);
        switch (probe.Status)
        {
            case NpcapProbeStatus.Ready:
                Say("[host] 抓包环境: Npcap 已就绪");
                return true;

            case NpcapProbeStatus.InstalledNow:
                Say("[host] " + probe.Message);
                Console.Error.WriteLine("[host] 驱动已装好，请重新运行本程序（管理员），之后不用再管这一步。");
                return false;

            default:
                Console.Error.WriteLine("[host] 抓包环境未就绪: " + probe.Message);
                return false;
        }
    }

    /// <summary>
    /// 只装/修 Npcap（--install-npcap [安装器路径]）。
    /// 路径可省：缺省取 exe 同目录（含 npcap\ 子目录）里的 npcap-*.exe。
    /// </summary>
    private static int InstallNpcap(string installerArg)
    {
        if (NpcapEnvironment.IsDriverPresent())
        {
            Say("[host] 检测到 Npcap 已安装，无需重复安装（要卸载/换版本请直接用官方安装器）。");
            return 0;
        }

        string? installer = string.IsNullOrWhiteSpace(installerArg)
            ? NpcapEnvironment.FindInstaller(BaseDir)
            : installerArg;

        if (installer == null)
        {
            Console.Error.WriteLine("[host] 未找到安装器：请把 npcap-x.xx.exe 放到本程序同目录（或 npcap\\ 子目录），");
            Console.Error.WriteLine("        或显式指定：BotClientDriverHost.exe --install-npcap D:\\npcap-1.80.exe");
            return 2;
        }

        Say($"[host] 使用安装器: {installer}");
        if (!NpcapEnvironment.TryInstall(installer, out string detail))
        {
            Console.Error.WriteLine($"[host] Npcap 安装失败: {detail}");
            return 3;
        }

        Say("[host] Npcap 安装成功（WinPcap 兼容模式已开），现在可以直接运行本程序挂机了");
        return 0;
    }

    /// <summary>判定异常是否属于"抓包环境问题"（而不是业务逻辑错误），内层异常一并检查。</summary>
    private static bool IsPcapFailure(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return true;
            }

            if (e.Message.Contains("抓包设备"))
            {
                return true;
            }

            if (e.GetType().Name.Contains("Pcap"))
            {
                return true;
            }
        }

        return false;
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
        host.Identity = Identity;   // 自检报告里带上多开标识，多开时报告之间能对号入座

        // 运行时从状态通道拿到角色名后，把标识的缺项补上（多开时用来分辨"这个进程跑的是哪个角色"）
        runtime.StateChanged += () =>
        {
            if (Identity.HasCharacter || string.IsNullOrWhiteSpace(runtime.Player.Name)) return;
            MergeIdentity(new InstanceIdentity { CharacterName = runtime.Player.Name }, driverPath, "状态通道（服务端角色信息）");
        };

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

        // 复用模式：客户端窗口标题一般带服务器/区/角色信息，用来补齐标识的缺项（解析保守，命令行优先级更高）
        try
        {
            MergeIdentity(InstanceIdentity.FromWindowTitle(host.Input.GetWindowTitle()), driverPath, "客户端窗口标题");
        }
        catch (Exception ex)
        {
            Say($"[标识] 窗口标题解析跳过: {ex.Message}");
        }

        host.Credentials.Captured += cred =>
            Say($"[host] 已复用客户端登录凭据：{cred.Describe()}（仅存内存，不写入任何文件）");

        host.Attach(new Attachment(session, runtime));
        runtime.NpcMessage += (id, text) => host.FeedNpcDialog(id, text);
        runtime.SystemMessage += text => host.FeedSystemMessage(text);
        if (!EnsureNpcap(cli.AutoInstallNpcap)) return 3;
        if (!TryStartSniffing(host)) return 3;

        // A 档：校准档自动匹配 —— 身份 = 分辨率_进程_客户区尺寸；命中即套用，换服/重启/挪窗口都不用重量
        try
        {
            CalibrationArchives.TryAutoApply(cfg, driverPath, host.Input, out string calMsg);
            Say("[host] " + calMsg);
        }
        catch (Exception ex)
        {
            Say($"[host] 校准档检查跳过: {ex.Message}");
        }

        if (cli.AutoCalibrate)
        {
            Say("[host] --autocalibrate：自动初值 + 走格反馈自校正（角色会短距离走动，先站到空地上）");
            int arc = await AutoCalibrator.RunAsync(host, driverPath, null, ct, m => Say(m));
            if (arc != 0)
            {
                await host.DisposeAsync();
                await session.DisposeAsync();
                return arc;
            }

            Say("[host] 自动校正完成，继续正常流程");
        }

        Say(host.SelfCheck());

        if (cli.AutoFrame)
        {
            int rc = await host.AutoFrameAsync(15, ct);
            if (rc == 0)
            {
                try
                {
                    cfg.Save(driverPath);
                    Say($"[host] 定界档已写回 {driverPath} 的 framing 段：下次启动自动生效（换服只需重跑本参数）");
                }
                catch (Exception ex)
                {
                    Say($"[host] 定界已在本进程生效，但写回配置失败: {ex.Message}");
                }
            }
            else
            {
                Say("[host] 自动定界未成功：本次仍按现有定界运行；可多走几步/多打几下后重跑 --autoframe");
            }
        }

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

            if (!string.IsNullOrWhiteSpace(cli.HuntMap))
            {
                await RunHuntAsync(cli, runtime, host, ai, ct);
            }
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

    /// <summary>
    /// 挂机任务循环：把命令行给的"目标地图名 / 等级门槛 / 层数"装配成 <see cref="HuntPlan"/>，
    /// 一轮一轮跑（找传送员 → 进图 → 清怪 → 下探）。战斗照旧由 BotCombatAI 执行，本方法只做编排。
    ///
    /// 每轮都重新判定"我是不是已经在目标图"，因此掉线重连、被传送回城、打完一层回到原地，
    /// 下一轮都会自己走回去 —— 不需要用户重启程序。
    /// </summary>
    private static async Task RunHuntAsync(Cli cli, BotRuntime runtime, ClientDriverHost host, BotCombatAI ai, CancellationToken ct)
    {
        var plan = new HuntPlan
        {
            TargetMapText = cli.HuntMap!,
            NpcKeyword = cli.HuntNpc,
            MenuPath = SplitMenuPath(cli.HuntMenu),
            MinLevel = cli.HuntLevel,
            Descend = !cli.HuntNoDescend,
            MaxDepth = Math.Max(1, cli.HuntDepth),
            FloorMaxMs = Math.Max(10, cli.HuntClearSeconds) * 1000,
        };

        var hunter = new HuntTaskRunner(runtime, host, ai);
        hunter.Log += m => Say("[任务] " + m);

        Say($"[host] 挂机任务已装配：{plan.Describe()}");
        if (plan.MinLevel > 0)
        {
            Say($"[host] 等级门槛 {plan.MinLevel}，当前角色 {runtime.Player.Level} 级"
                + (runtime.Player.Level < plan.MinLevel ? "（不满足，本轮不会动手）" : "（满足）"));
        }
        Say("[host] 换地图只需改 --hunt 后的地图名，流程不用改");

        int round = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                round++;
                Say($"[任务] ===== 第 {round} 轮开始 =====");
                var report = await hunter.RunAsync(plan, ct).ConfigureAwait(false);
                Say($"[任务] 第 {round} 轮结束：{report.Summary}");
                foreach (var floor in report.Floors) Say($"[任务]   {floor}");

                if (cli.HuntOnce) break;

                Say($"[任务] {plan.LoopIntervalMs / 1000}s 后开始下一轮（Ctrl+C 退出）");
                await Task.Delay(plan.LoopIntervalMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Say($"[任务] 已停止（共 {round} 轮，累计击杀 {hunter.TotalKilled} 只）");
        }
    }

    /// <summary>把 --hunt-menu 的逗号分隔路径拆成菜单层级；空串拆成空数组（=当前层直接是地图名）。</summary>
    private static IReadOnlyList<string> SplitMenuPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(new[] { ',', '，', '>' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim())
                  .Where(s => s.Length > 0)
                  .ToArray();
    }

    private static async Task<int> RunAsync(Cli cli, HostSettings settings, ClientDriverConfig cfg,
        string driverPath, string account, string password, CancellationToken ct)
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

        // 老路径下区服名与角色名都是这里确定的，直接补进多开标识（服务器名取 botsettings.json 的 Host）
        MergeIdentity(new InstanceIdentity
        {
            ServerName = FirstNonEmpty(settings.Host, cfg.ServerIp),
            ZoneName = server,
            CharacterName = character,
        }, driverPath, "登录流程（区服/角色选择）");

        runtime.Log += m => Say("[runtime] " + m);
        runtime.SystemMessage += m => Say("[系统] " + m);
        runtime.MapChanged += () =>
            Say($"[地图] {runtime.CurrentMap}｜{runtime.Player.MapName}｜({runtime.Player.PosX},{runtime.Player.PosY})");
        runtime.Died += () => Say("[危险] 角色死亡");
        runtime.LevelUp += lv => Say($"[成长] 等级 → {lv}");
        runtime.StartReceiveLoop(ct);

        var host = new ClientDriverHost(cfg);
        host.Log += Say;
        host.Identity = Identity;
        host.Attach(new Attachment(session, runtime));
        runtime.NpcMessage += (id, text) => host.FeedNpcDialog(id, text);
        runtime.SystemMessage += text => host.FeedSystemMessage(text);
        if (!EnsureNpcap(cli.AutoInstallNpcap)) return 3;
        if (!TryStartSniffing(host)) return 3;

        // A 档：校准档自动匹配 —— 身份 = 分辨率_进程_客户区尺寸；命中即套用，换服/重启/挪窗口都不用重量
        try
        {
            CalibrationArchives.TryAutoApply(cfg, driverPath, host.Input, out string calMsg);
            Say("[host] " + calMsg);
        }
        catch (Exception ex)
        {
            Say($"[host] 校准档检查跳过: {ex.Message}");
        }

        if (cli.AutoCalibrate)
        {
            Say("[host] --autocalibrate：自动初值 + 走格反馈自校正（角色会短距离走动，先站到空地上）");
            int arc = await AutoCalibrator.RunAsync(host, driverPath, null, ct, m => Say(m));
            if (arc != 0)
            {
                await host.DisposeAsync();
                await session.DisposeAsync();
                return arc;
            }

            Say("[host] 自动校正完成，继续正常流程");
        }

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

    /// <summary>
    /// 控制台输出：时间戳 + [多开标识]（已配置时）+ 正文。
    /// 多开时每个实例的日志都自带"服务器名-区名-角色名"前缀，重定向到不同文件后不会串。
    /// </summary>
    internal static void Say(string message)
    {
        string tag = Identity.Prefix;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {(tag.Length > 0 ? tag + " " : string.Empty)}{message}");
    }
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

    /// <summary>换服适配：只读采样后自动探测帧定界，并写回 clientdriver.json 的 framing 段。</summary>
    public bool AutoFrame { get; private set; }

    /// <summary>--calibrate 的快捷档：只量视图区两点，其余用自动初值 + --autocalibrate 闭环收敛。</summary>
    public bool Quick { get; private set; }

    /// <summary>自动校正：视图区四角起量 + 走格反馈迭代，把格宽/格高/玩家格像素收敛准，并写回配置。</summary>
    public bool AutoCalibrate { get; private set; }

    /// <summary>校准热键退回 v1 的裸 F1–F8（仅当游戏内已清空这些快捷键）。</summary>
    public bool LegacyKeys { get; private set; }

    /// <summary>--calibrate 的分步引导（默认开启；--no-guide 回到只报坐标的极简输出）。</summary>
    public bool Guide { get; private set; } = true;

    /// <summary>--no-human：一键关掉全部拟真（固定时序），用于对照与排障。</summary>
    public bool NoHuman { get; private set; }

    /// <summary>--skills：本次强制开启技能循环（用配置里的表；表为空则用内置示例表）。</summary>
    public bool EnableSkills { get; private set; }

    /// <summary>多开标识——服务器名（--server-name）。纯标签，不参与协议与登录。</summary>
    public string? ServerName { get; private set; }

    /// <summary>多开标识——区名（--zone）。纯标签；与老路径参与登录的 --server（区服名）是两回事。</summary>
    public string? Zone { get; private set; }

    /// <summary>只处理 Npcap 后退出。null = 未指定；空串 = 指定了但没给路径（自动找随包安装器）。</summary>
    public string? InstallNpcap { get; private set; }

    /// <summary>抓包前检测到 Npcap 缺失时，是否用随包安装器自动静默补装（默认开启）。</summary>
    public bool AutoInstallNpcap { get; private set; } = true;

    public int FightPointX { get; private set; } = -1;

    public int FightPointY { get; private set; } = -1;

    // ---------------------------------------------------------------- 挂机任务（--hunt）

    /// <summary>目标地图中文名（--hunt）。给了就启用"挂机任务"：找传送员 → 进图 → 清怪 → 逐层下探。</summary>
    public string? HuntMap { get; private set; }

    /// <summary>传送员 NPC 的匹配关键词（--hunt-npc，默认 "传送"）。</summary>
    public string HuntNpc { get; private set; } = "传送";

    /// <summary>进图前要点开的上级菜单路径（--hunt-menu，逗号分隔，默认空=当前层直接是地图名）。</summary>
    public string HuntMenu { get; private set; } = string.Empty;

    /// <summary>进入目标图的最低等级（--hunt-level，默认 0=不检查）。</summary>
    public int HuntLevel { get; private set; }

    /// <summary>最多下探层数（--hunt-depth，默认 3）。</summary>
    public int HuntDepth { get; private set; } = 3;

    /// <summary>每层清怪时长上限，秒（--hunt-clear，默认 300）。</summary>
    public int HuntClearSeconds { get; private set; } = 300;

    /// <summary>--hunt-no-descend：只打当前层，不清完往下走。</summary>
    public bool HuntNoDescend { get; private set; }

    /// <summary>--hunt-once：只跑一轮，不循环。</summary>
    public bool HuntOnce { get; private set; }

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
                case "--hunt":
                    cli.HuntMap = Next();
                    break;
                case "--hunt-npc":
                    cli.HuntNpc = Next();
                    break;
                case "--hunt-menu":
                    cli.HuntMenu = Next();
                    break;
                case "--hunt-level":
                    cli.HuntLevel = int.Parse(Next());
                    break;
                case "--hunt-depth":
                    cli.HuntDepth = int.Parse(Next());
                    break;
                case "--hunt-clear":
                    cli.HuntClearSeconds = int.Parse(Next());
                    break;
                case "--hunt-no-descend":
                    cli.HuntNoDescend = true;
                    break;
                case "--hunt-once":
                    cli.HuntOnce = true;
                    break;
                case "--calibrate":
                    cli.Calibrate = true;
                    break;
                case "--sniff-only":
                    cli.SniffOnly = true;
                    break;
                case "--autoframe":
                    cli.AutoFrame = true;
                    break;
                case "--quick":
                    cli.Quick = true;
                    break;
                case "--autocalibrate":
                    cli.AutoCalibrate = true;
                    break;
                case "--legacy-keys":
                    cli.LegacyKeys = true;
                    break;
                case "--no-guide":
                    cli.Guide = false;
                    break;
                case "--no-human":
                    cli.NoHuman = true;
                    break;
                case "--skills":
                    cli.EnableSkills = true;
                    break;
                case "--server-name":
                    cli.ServerName = Next();
                    break;
                case "--zone":
                    cli.Zone = Next();
                    break;
                case "--install-npcap":
                    // 路径可省：后面跟着的不是另一个选项时才算路径
                    cli.InstallNpcap = i + 1 < args.Length && !args[i + 1].StartsWith('-')
                        ? args[++i]
                        : string.Empty;
                    break;
                case "--no-auto-install":
                    cli.AutoInstallNpcap = false;
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

            多开标识（可选，只用于区分多开实例）:
              --server-name <服务器名>  服务器名（如 s1）
              --zone <区名>             区名（如 电信一区）
              --character <角色名>      角色名（同上；复用模式会自动从服务端角色信息补全）
              说明: 三项组成 "服务器名-区名-角色名"，只用在日志前缀 / 控制台窗口标题 / 自检报告，
                    不参与协议与登录。缺项显示 "?"，下次启动自动记住，可用命令行覆盖。

            文件:
              --settings <路径>       botsettings.json 路径（缺省为 exe 同目录）
              --driver-config <路径>  clientdriver.json 路径（缺省为 exe 同目录）

            模式:
              --calibrate             校准模式：量取视口/小地图/背包/对话框坐标并写回配置，不登录
              --quick                 配合 --calibrate：快捷档，只量视图区左上/右下两点即可保存
              --legacy-keys           配合 --calibrate：热键退回裸 F1–F8（仅当游戏内已清空这些快捷键）
              --no-guide              配合 --calibrate：关闭分步引导（默认开启：逐步说明鼠标该移到哪、
                                      按哪个键，并在每次按键后提示正确/失败及原因）
              --autocalibrate         自动校正：视图区四角起量 + 走格反馈迭代，收敛格宽/格高/玩家格像素
                                      并写回 clientdriver.json（成功后可继续挂机，等价于"校准+挂机"一步）
              --sniff-only            只嗅探 + 状态镜像，不做任何点击（先验证抓包链路用）
              --autoframe             换服适配：只读采样 15 秒自动探测帧定界（魔数/长度字段/头长），
                                      成功则写回 clientdriver.json 的 framing 段，**换服不必再改代码**

            拟真操作（默认全开，配置在 botsettings.json 的 Human / Skills 段）:
              --no-human              本次关闭全部拟真（鼠标轨迹/按键时序/停顿/节奏抖动），退回固定时序；
                                      只影响本次运行、不改配置文件，用于对照与排障
              --skills                本次强制开启技能循环（多技能按优先级+条件选用）。配置里没写技能表时
                                      用内置示例表（火墙/冰咆哮/灵魂火符）；没学会的技能自动跳过，不会误发
              说明: 拟真只做"加法"——所有随机都叠在服务端限速下限之上，绝不把间隔压到阈值以下；
                    技能循环挑不出技能时仍退回物理/单法术，不会让原本能打的号变哑。

            挂机:
              --fight-x <格> --fight-y <格>   定点挂机坐标（都不给则跟随 AI 默认行为）

            挂机任务（换图挂机，给地图名即可）:
              --hunt <地图名>          目标地图中文名（如 僵尸洞）。启用后自动：
                                       当前图找"传送员"NPC → 按地图名点进目标图 → 清怪 → 有下层则逐层下探
              --hunt-npc <关键词>     传送员 NPC 的匹配关键词（默认 传送；跨服异名就改这里）
              --hunt-menu <路径>      进图前要点开的上级菜单，逗号分隔（如 传送,白日门；默认空=当前层直接是地图名）
              --hunt-level <等级>     进入该图的最低等级（默认 0=不检查；不够级不做任何点击）
              --hunt-depth <层数>     最多下探层数（默认 3）
              --hunt-clear <秒>       每层清怪时长上限（默认 300；视野内无怪持续 8s 判定清完，提前下探）
              --hunt-no-descend       只打当前层，不清完往下走
              --hunt-once             只跑一轮，不循环（默认循环，掉线回城后下一轮自动走回去）

              例：BotClientDriverHost.exe --hunt 僵尸洞 --hunt-level 20
                  换地图：把"僵尸洞"改成别的图名即可，其余不用动

            抓包环境（Npcap）:
              --install-npcap [安装器]  只装/修 Npcap 后退出。安装器路径可省：缺省用本程序同目录
                                        （含 npcap\ 子目录）里的 npcap-*.exe
              --no-auto-install         关闭"抓包前检测到 Npcap 缺失就自动静默补装"（默认开启）

            运行前提:
              1) 以管理员身份运行（manifest 已声明 requireAdministrator）
              2) 抓包驱动 Npcap（安装时勾选 WinPcap 兼容模式）；
                 未装时只要把官方 npcap-x.xx.exe 放在本程序同目录，首次运行会自动静默补装
              3) 游戏客户端已启动并登录（本宿主不改客户端文件、不改 IP、不介入连接）
              4) 校准自动化的推荐顺序：
                 a. 首次（或换了分辨率/窗口尺寸/皮肤）：
                    BotClientDriverHost.exe --calibrate --quick    （只量视图区左上、右下两点）
                 b. 角色站到空地：BotClientDriverHost.exe --autocalibrate
                    （自动走几次格，把格宽/格高收敛准；量完自动存进 calibration_profiles.json）
                 c. 之后正常挂机：直接运行，宿主按"分辨率_进程_客户区尺寸"自动命中校准档，无需再量

              说明：clientdriver.json 同目录会生成 calibration_profiles.json（A 档校准档），
              一条档 = 一个"分辨率_进程名_客户区尺寸"身份；换服、重启、挪窗口都继续有效。
            """);
    }
}
