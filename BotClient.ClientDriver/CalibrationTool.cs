using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using BotClient.ClientDriver.Input;

namespace BotClient.ClientDriver;

/// <summary>
/// 校准辅助工具（控制台）—— v2：采样热键改为 Ctrl+Alt 组合，并用低级键盘钩子**吞掉按键**。
///
/// 为什么必须改（v1 的真坑）：
///   v1 用裸 F1–F8 触发采样，而且只是 GetAsyncKeyState **轮询**，并不消费按键。
///   但传奇客户端的 F1–F8 本身就是技能快捷栏（F9 是背包），按键会被游戏同时收到 ——
///   量一个坐标就顺带放一发技能、弹一个面板；面板一弹，客户区里所有坐标的含义全变，
///   刚量的数据直接作废；技能打到怪还会让角色位移/掉血，鼠标底下那一格已经不是要量的那一格。
///
/// v2 的做法（三点）：
///   ① 采样键换成 Ctrl+Alt+1..8，保存 Ctrl+Alt+S，放弃 Ctrl+Alt+Q（ESC 在游戏里也有菜单含义，一并避开）；
///   ② 装 WH_KEYBOARD_LL 低级键盘钩子，命中组合键时返回 1 **吞掉该事件**：
///      按键在进入游戏输入队列之前就被拦下，游戏侧既不施法也不开面板；
///   ③ 全程不动鼠标、不发点击、不改窗口 —— 只读鼠标位置，量的是"你自己把鼠标挪过去的那个点"。
///
/// 兼容：--legacy-keys 可回到 v1 的裸 F1–F8 + ESC（仅当你已在游戏内把这些快捷键清空）。
///
/// v2.1 补的两件事（自动化）：
///   ① --quick 快捷档：只量视图区左上/右下两点即可保存，玩家格像素取视图区中心、格尺寸取 48×32 当初值；
///   ② 量完自动把实测写进"校准档"（clientdriver.json 同目录的 calibration_profiles.json），
///      档的身份 = 分辨率_进程_客户区尺寸 —— 宿主每次启动自动命中套用，换服/重启/挪窗口都不用重量。
///
/// 注意：建议与宿主一样以管理员身份运行 —— 游戏若以管理员权限运行，
///       同权限等级的钩子才能稳定拦截它的输入。
/// </summary>
public static class CalibrationTool
{
    // ------------------------------------------------------------------ 采样项

    private static readonly string[] SlotNames =
    {
        "player", "viewTL", "viewBR", "miniTL", "miniBR", "bag", "dlgLine", "dlgNext",
    };

    private static readonly string[] SlotLabels =
    {
        "玩家所在格中心", "视图区左上角", "视图区右下角", "小地图左上角",
        "小地图右下角", "背包第一格中心", "对话框第一行", "对话框翻页按钮",
    };

    /// <summary>
    /// 每一项的"分步引导"：鼠标到底该移到哪。控制台没有图形界面，
    /// 所以引导必须写成"相对游戏画面哪个位置"的可执行描述，而不是坐标数字（数字正是要量出来的东西）。
    /// </summary>
    private static readonly string[] SlotGuides =
    {
        "把鼠标移到游戏画面里【你自己角色所站那一格的正中心】—— 一般就在画面中央附近、小人脚底下",
        "把鼠标移到游戏画面（主视图）的【最左上角】—— 贴住画面左上内侧 1~2 像素",
        "把鼠标移到游戏画面的【最右下角】—— 贴住画面右下内侧 1~2 像素（右下角被状态栏挡住时，取可见画面的最右下处）",
        "把鼠标移到【小地图的左上角】—— 小地图是画面角落那块缩略地图，取它最左上边缘",
        "把鼠标移到【小地图的右下角】—— 取缩略地图的最右下边缘",
        "把鼠标移到【背包面板里第 1 行第 1 格的正中心】—— 是第一个格子的中心，不是背包窗口的边缘",
        "把鼠标移到【对话/菜单窗第一行文字】的中间偏左位置（文字起点右侧十几像素即可）",
        "把鼠标移到对话框的【下一页/继续】按钮上（一般是对话框右下角那个小箭头）；没有翻页按钮就量对话框右下角内侧",
    };

    /// <summary>量之前要先做的动作（不开面板，量的位置根本不存在）。</summary>
    private static readonly string[] SlotPreludes =
    {
        "角色先站定别跑动（跑动中量到的是旧位置）",
        "",
        "",
        "",
        "",
        "先在游戏里按快捷键打开背包（多数服是 B 或 F9，以你服设置为准）",
        "先在游戏里点一下 NPC，让对话/菜单窗跳出来",
        "保持对话框打开",
    };

    /// <summary>量完之后建议做的收尾（不然面板会压住后面要量的位置）。</summary>
    private static readonly string[] SlotAfterNotes =
    {
        "",
        "",
        "",
        "",
        "",
        "量完按同一个键把背包关掉，别让它压住后面要量的位置",
        "量完把对话框关掉再继续",
        "",
    };

    private const int SlotSave = 100;
    private const int SlotAbort = 101;

    private const int VK_F1 = 0x70;
    private const int VK_ESCAPE = 0x1B;

    private static readonly ConcurrentQueue<(int Slot, int ScreenX, int ScreenY)> Pending = new();
    private static bool _saveRequested;
    private static bool _abortRequested;
    private static bool _quick;

    /// <summary>分步引导开关（--no-guide 关闭）。开启时：启动打印分步清单、每项记录前说明鼠标怎么摆、记录后提示下一步。</summary>
    private static bool _guide = true;

    private static bool _legacy;

    /// <summary>快捷档只要求视图区两点（左上/右下）；完整档要求八项全量。</summary>
    private static IEnumerable<string> RequiredNames()
        => _quick ? new[] { "viewTL", "viewBR" } : SlotNames;

    private static int RequiredLeft(Dictionary<string, (int X, int Y)> recorded)
        => RequiredNames().Count(n => !recorded.ContainsKey(n));

    /// <summary>该项在本次档位下是不是必量项（快捷档只有视图区两点是必量）。</summary>
    private static bool IsRequired(string name) => RequiredNames().Contains(name);

    /// <summary>
    /// 运行校准会话；返回 0 表示正常结束（已保存或用户主动放弃）。
    /// <paramref name="quick"/> = 快捷档：只量视图区四角两点（Ctrl+Alt+2/3），
    /// 玩家格像素取视图区中心、格尺寸取 48×32 作为**自动初值**，其余交给 --autocalibrate 闭环收敛。
    /// 量完自动收录进 A 档校准档（calibration_profiles.json），换服/重启自动套用。
    /// </summary>
    public static int Run(string configPath, bool legacyKeys = false, bool quick = false, bool guide = true)
    {
        var cfg = ClientDriverConfig.Load(configPath);
        var win = new InputSimulator(cfg);
        if (!win.TryLocateWindow(out string why))
        {
            Console.WriteLine($"[校准] 找不到游戏窗口: {why}");
            return 1;
        }

        _quick = quick;
        _guide = guide;
        _legacy = legacyKeys;
        var (cw, ch) = win.GetClientSize();
        PrintBanner(cw, ch, legacyKeys);
        PrintIdentity(cfg, win);

        // A 档：先看这个环境有没有现成的校准档，命中就提示（保存后会覆盖同名档）
        if (CalibrationArchives.TryFind(cfg, configPath, win, out string key, out var existProfile))
        {
            Console.WriteLine();
            Console.WriteLine($"[校准档] 命中同环境存档：{existProfile!.Describe()}");
            Console.WriteLine("         保存后会用本次实测覆盖该档；不想覆盖可以直接 Ctrl+Alt+Q 退出。");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine($"[校准档] 当前环境（{key}）还没有存档，本次量完会自动存一份。");
        }

        PrintStepPlan();

        if (!legacyKeys) InstallHook();

        var recorded = new Dictionary<string, (int X, int Y)>();

        try
        {
            while (true)
            {
                if (legacyKeys) PollLegacyKeys();
                else PumpMessages();

                while (Pending.TryDequeue(out var item))
                {
                    if (item.Slot == SlotSave)
                    {
                        if (RequiredLeft(recorded) > 0)
                        {
                            int next = NextRequiredSlot(recorded);
                            Console.Write("\r" + new string(' ', 78) + "\r");
                            Console.WriteLine($"[校准] ✗ 不能保存：还差 {RequiredLeft(recorded)} 项" +
                                              $"（{(_quick ? "快捷档至少要视图区左上/右下两点" : "完整档要八项全量")}）。");
                            if (next >= 0)
                            {
                                Console.WriteLine($"       下一步 [{next + 1}] {SlotLabels[next]}：{SlotGuides[next]}");
                                if (SlotPreludes[next].Length > 0) Console.WriteLine($"       准备：{SlotPreludes[next]}");
                                Console.WriteLine($"       摆好后按 {HotKeyLabel(next)} 记录；全齐后再按 {SaveKeyLabel()} 保存。");
                            }

                            continue;
                        }

                        Console.WriteLine($"[校准] ✓ 已量齐，正在写入配置…");
                        _saveRequested = true;
                        break;
                    }
                    if (item.Slot == SlotAbort) { _abortRequested = true; break; }
                    Record(win, recorded, item.Slot, item.ScreenX, item.ScreenY);
                }

                if (_saveRequested || _abortRequested) break;

                DrawCursorLine(win, recorded);
                if (legacyKeys) Thread.Sleep(40);
                else MsgWaitForMultipleObjects(0, null, false, 15, QS_ALLINPUT);
            }
        }
        finally
        {
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }

        if (_abortRequested)
        {
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("[校准] 已放弃，未写入配置文件。");
            return 0;
        }

        Console.WriteLine();
        if (_guide) PrintSanityReport(recorded, win);
        ApplyConfig(cfg, recorded, quick);
        cfg.Save(configPath);
        Console.WriteLine($"[校准] 已写入 {configPath}");
        Console.WriteLine(BuildSummary(cfg));

        // A 档：把这次实测收录成"本环境"的校准档，下次同分辨率/同进程/同客户区尺寸直接套用
        if (CalibrationArchives.Remember(cfg, configPath, win, quick ? "quick" : "hand",
                quick ? "快捷档：视图区两点 + 初值" : "人工量取八点", out string memo))
            Console.WriteLine($"[校准档] {memo}");
        else
            Console.WriteLine($"[校准档] {memo}");

        return 0;
    }

    // ------------------------------------------------------------------ 界面

    /// <summary>
    /// 打印本次校准的多开标识（服务器名-区名-角色名）。
    /// 多开时必须能确认"现在量的到底是哪个客户端窗口"，所以标识与窗口标题一起打出来；
    /// 标题解析结果只作提示，不写进配置 —— 配置里的标识由宿主与命令行维护（这里只保证量的是同一个窗口）。
    /// </summary>
    private static void PrintIdentity(ClientDriverConfig cfg, InputSimulator win)
    {
        string title = win.GetWindowTitle();
        var fromTitle = InstanceIdentity.FromWindowTitle(title);

        Console.WriteLine();
        Console.WriteLine($"[标识] 本次校准的实例: {cfg.Identity.Describe()}");
        if (!string.IsNullOrWhiteSpace(title))
        {
            Console.WriteLine($"       客户端窗口标题: {title}");
        }

        if (!fromTitle.IsEmpty)
        {
            Console.WriteLine($"       从标题看出: {fromTitle.Describe()}（仅提示；要写进配置请用 --server-name / --zone / --character）");
        }

        if (!cfg.Identity.IsComplete)
        {
            Console.WriteLine("       提示: 多开时建议把三项填全，日志和窗口标题才分得清 —— " +
                              "BotClientDriverHost.exe --server-name 服务器名 --zone 区名 --character 角色名");
        }
    }

    /// <summary>启动时的分步清单：这一档要量哪几项、每项鼠标该摆到哪、按哪个键。</summary>
    private static void PrintStepPlan()
    {
        if (!_guide)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("分步引导（鼠标摆好后按对应热键，工具会立刻告诉你对/不对）：");
        int step = 0;
        for (int i = 0; i < SlotNames.Length; i++)
        {
            if (!IsRequired(SlotNames[i])) continue;
            step++;
            Console.WriteLine($"  {step}. [{SlotLabels[i]}] {SlotGuides[i]}");
            if (SlotPreludes[i].Length > 0) Console.WriteLine($"     准备: {SlotPreludes[i]}");
            if (SlotAfterNotes[i].Length > 0) Console.WriteLine($"     收尾: {SlotAfterNotes[i]}");
            Console.WriteLine($"     热键: {HotKeyLabel(i)}");
        }

        if (_quick)
        {
            Console.WriteLine("  （1/4/5/6/7/8 可选：补量后面更省事，不补也能用初值 + --autocalibrate 收敛）");
        }

        Console.WriteLine("  量错不要紧：同一项可反复重按覆盖；按键后立即给出成功/失败与失败原因。");
    }

    private static string HotKeyLabel(int slot) => _legacy ? $"F{slot + 1}" : $"Ctrl+Alt+{slot + 1}";

    private static string SaveKeyLabel() => _legacy ? "ESC" : "Ctrl+Alt+S";

    private static string AbortKeyLabel() => _legacy ? "（无）" : "Ctrl+Alt+Q";

    private static string LabelOf(string name)
    {
        int i = Array.IndexOf(SlotNames, name);
        return i >= 0 ? SlotLabels[i] : name;
    }

    private static void PrintBanner(int cw, int ch, bool legacyKeys)
    {
        Console.WriteLine("======================================================");
        Console.WriteLine(" 传奇客户端驱动模式 · 校准工具 (v2.1)");
        Console.WriteLine("======================================================");
        Console.WriteLine($" 客户区尺寸: {cw} × {ch}");
        Console.WriteLine(_quick
            ? " 档位: 快捷档 —— 只量视图区左上/右下两点；玩家格与格尺寸先取初值，"
            : " 档位: 完整档 —— 量八点（视图区/小地图/背包/对话框全量）");
        if (_quick) Console.WriteLine("       之后由 --autocalibrate 用走格反馈自动收敛，无需人工量像素");
        Console.WriteLine();
        Console.WriteLine(" 请把游戏窗口切到最前，鼠标移到目标位置后按对应键：");
        Console.WriteLine();

        if (legacyKeys)
        {
            for (int i = 0; i < SlotNames.Length; i++)
                Console.WriteLine($"   F{i + 1}   记录【{SlotLabels[i]}】{(IsRequired(SlotNames[i]) ? "" : "（快捷档可跳过）")}");
            Console.WriteLine("   ESC  保存并退出");
            Console.WriteLine();
            Console.WriteLine(" ⚠ legacy 模式：直接使用裸 F1–F8，会被游戏当成技能/背包快捷键一起响应。");
            Console.WriteLine("   仅当你已在游戏里清空这些快捷键时才可用；否则请去掉 --legacy-keys。");
        }
        else
        {
            for (int i = 0; i < SlotNames.Length; i++)
                Console.WriteLine($"   Ctrl+Alt+{i + 1}   记录【{SlotLabels[i]}】{(IsRequired(SlotNames[i]) ? "" : "（快捷档可跳过）")}");
            Console.WriteLine("   Ctrl+Alt+S   保存并退出");
            Console.WriteLine("   Ctrl+Alt+Q   放弃退出（不写盘）");
            Console.WriteLine();
            Console.WriteLine(" 这些组合键由本工具独占：按下时按键被钩子吞掉，游戏不会收到，");
            Console.WriteLine(" 所以既不会放技能也不会弹背包；同一项可反复重按覆盖。");
        }

        Console.WriteLine();
        if (_quick)
        {
            Console.WriteLine(" 快捷档最少只量 2/3（视图区左上角 + 右下角）即可保存：");
            Console.WriteLine("   玩家格像素 = 视图区中心，格尺寸 = 48×32 —— 这是**初值**，");
            Console.WriteLine("   随后跑 ClientDriverHost.exe --autocalibrate，用几次走格把格宽/格高收敛准。");
            Console.WriteLine(" 顺手的话 1（玩家格）/4/5（小地图）/6（背包）/7/8（对话）也可以补量，补了更省事。");
        }
        else
        {
            Console.WriteLine(" 提示: 先量 2/3（视图区四角），再量 1（玩家格）；");
            Console.WriteLine("      量 6（背包）前先用游戏自己的快捷键打开背包，量 7/8 前先打开 NPC 对话窗。");
            Console.WriteLine("      量完后把面板关掉再继续，别让面板盖住后面的测量位置。");
        }
        Console.WriteLine("------------------------------------------------------");
    }

    private static void DrawCursorLine(InputSimulator win, Dictionary<string, (int X, int Y)> recorded)
    {
        var (sx, sy) = GetCursorPos();
        var (ox, oy) = win.GetClientOrigin();
        int cx = sx - ox;
        int cy = sy - oy;
        int need = RequiredNames().Count();
        int have = RequiredNames().Count(recorded.ContainsKey);
        string mode = _quick ? "快捷档" : "完整档";
        Console.Write($"\r 鼠标: 屏幕({sx},{sy})  客户区({cx},{cy})   {mode}已量 {have}/{need}" +
                      $"（共 {recorded.Count}/8 项非空）      ");
    }

    private static void Record(InputSimulator win, Dictionary<string, (int X, int Y)> recorded,
        int slot, int screenX, int screenY)
    {
        var (ox, oy) = win.GetClientOrigin();
        int cx = screenX - ox;
        int cy = screenY - oy;

        string name = SlotNames[slot];
        var (cw, ch) = win.GetClientSize();
        var (ok, why) = Check(name, cx, cy, (cw, ch), recorded);

        Console.Write("\r" + new string(' ', 78) + "\r");

        if (!ok)
        {
            // 失败不落记录：错值留在配置里比没量更糟（挂机会照着错坐标点）
            Console.WriteLine($"  ✗ 失败：[{slot + 1}] {SlotLabels[slot]} 未记录 —— {why}");
            Console.WriteLine($"     已量过的项不受影响；按上面说的把鼠标摆好，再按一次 {HotKeyLabel(slot)}。");
            if (_guide) Console.WriteLine($"     本步目标：{SlotGuides[slot]}");
            return;
        }

        recorded[name] = (cx, cy);
        Console.WriteLine($"  ✓ 正确：[{slot + 1}] {SlotLabels[slot]} ({name}) = 客户区({cx},{cy})");

        if (SlotAfterNotes[slot].Length > 0)
        {
            Console.WriteLine($"     收尾：{SlotAfterNotes[slot]}");
        }

        int left = RequiredLeft(recorded);
        if (left > 0)
        {
            int next = NextRequiredSlot(recorded);
            if (_guide && next >= 0)
            {
                Console.WriteLine($"     → 下一步 [{next + 1}] {SlotLabels[next]}：{SlotGuides[next]}");
                if (SlotPreludes[next].Length > 0) Console.WriteLine($"       准备：{SlotPreludes[next]}");
                Console.WriteLine($"       摆好后按 {HotKeyLabel(next)}（存盘：{SaveKeyLabel()}）");
            }
            else
            {
                var sb = new StringBuilder("     还差: ");
                for (int i = 0; i < SlotNames.Length; i++)
                    if (IsRequired(SlotNames[i]) && !recorded.ContainsKey(SlotNames[i]))
                        sb.Append($"[{HotKeyLabel(i)}] {SlotLabels[i]}  ");
                Console.WriteLine(sb.ToString());
            }
        }
        else
        {
            Console.WriteLine(_quick
                ? $"     快捷档已量齐（视图区两点）—— {SaveKeyLabel()} 保存退出；想补量 1/4/5/6/7/8 也可以。"
                : $"     八项已齐 —— {SaveKeyLabel()} 保存退出。");
        }
    }

    /// <summary>下一项还没量的必量项（返回槽位号；-1 = 已量齐）。</summary>
    private static int NextRequiredSlot(Dictionary<string, (int X, int Y)> recorded)
    {
        for (int i = 0; i < SlotNames.Length; i++)
            if (IsRequired(SlotNames[i]) && !recorded.ContainsKey(SlotNames[i]))
                return i;
        return -1;
    }

    /// <summary>
    /// 记录前的几何体检：把"量错位置"挡在写盘之前。
    /// 只拦**确定错**的（越界、两点量反、与已量项重合、明显落在错误区域）；
    /// 拿不准的一律放行 —— 校准最终以用户看到的实际画面为准，工具不做过度判断。
    /// </summary>
    private static (bool Ok, string Msg) Check(string name, int cx, int cy, (int W, int H) size,
        Dictionary<string, (int X, int Y)> recorded)
    {
        if (cx < 0 || cy < 0 || cx >= size.W || cy >= size.H)
        {
            return (false, $"鼠标不在游戏客户区里：计出的客户区坐标 ({cx},{cy}) 超出 0~{size.W - 1} × 0~{size.H - 1}。" +
                           "先点一下游戏窗口让它到最前，再把鼠标移到目标位置");
        }

        foreach (var kv in recorded)
        {
            if (kv.Key == name) continue;
            if (Math.Abs(kv.Value.X - cx) <= 2 && Math.Abs(kv.Value.Y - cy) <= 2)
            {
                return (false, $"与已量的【{LabelOf(kv.Key)}】几乎重合（都是 ({cx},{cy})）—— 鼠标没真正挪到新位置");
            }
        }

        if (name == "viewTL" && recorded.TryGetValue("viewBR", out var brTL) && (cx >= brTL.X || cy >= brTL.Y))
        {
            return (false, $"视图区左上角必须比右下角更靠左上；右下角已量在 ({brTL.X},{brTL.Y})，这个点却在它右下方 —— 两点量反了");
        }

        if (name == "viewBR" && recorded.TryGetValue("viewTL", out var tlBR))
        {
            if (cx <= tlBR.X || cy <= tlBR.Y)
            {
                return (false, $"视图区右下角必须比左上角已量的 ({tlBR.X},{tlBR.Y}) 更靠右下 —— 两点量反了");
            }

            if (cx - tlBR.X < 100 || cy - tlBR.Y < 100)
            {
                return (false, $"视图区只有 {cx - tlBR.X}×{cy - tlBR.Y} 像素，太小了 —— 两点很可能量到同一处，请重量左上/右下");
            }
        }

        if (name == "miniTL" && recorded.TryGetValue("miniBR", out var mbrTL) && (cx >= mbrTL.X || cy >= mbrTL.Y))
        {
            return (false, $"小地图左上角必须比右下角更靠左上；右下角已量在 ({mbrTL.X},{mbrTL.Y})");
        }

        if (name == "miniBR" && recorded.TryGetValue("miniTL", out var mtlBR))
        {
            if (cx <= mtlBR.X || cy <= mtlBR.Y)
            {
                return (false, $"小地图右下角必须比左上角已量的 ({mtlBR.X},{mtlBR.Y}) 更靠右下");
            }

            if (cx - mtlBR.X < 40 || cy - mtlBR.Y < 40)
            {
                return (false, $"小地图只有 {cx - mtlBR.X}×{cy - mtlBR.Y} 像素，太小了 —— 多半量到同一处");
            }
        }

        if (TryViewRect(recorded, out var view))
        {
            bool inside = cx > view.X + 4 && cx < view.X + view.W - 4 &&
                          cy > view.Y + 4 && cy < view.Y + view.H - 4;

            if (name == "player" && !inside)
            {
                return (false, $"玩家格中心必须落在主视图区内（已量的视图区是 ({view.X},{view.Y}) {view.W}×{view.H}），" +
                               "这个点在视图区外 —— 要么视图区两点量错了，要么鼠标没停在角色脚下那一格");
            }

            if (name == "bag" && inside)
            {
                return (false, $"这个点落在主视图区内（({view.X},{view.Y}) {view.W}×{view.H}) 是游戏画面）；" +
                               "背包是独立面板，一般贴着主视图右侧或下方 —— 请先按快捷键打开背包再量第一格中心");
            }
        }

        return (true, string.Empty);
    }

    /// <summary>从已量的两点推出视图区矩形（缺任一点、或顺序不对则视为无效）。</summary>
    private static bool TryViewRect(Dictionary<string, (int X, int Y)> recorded, out (int X, int Y, int W, int H) rect)
    {
        rect = default;
        if (!recorded.TryGetValue("viewTL", out var tl) || !recorded.TryGetValue("viewBR", out var br)) return false;
        if (br.X <= tl.X || br.Y <= tl.Y) return false;
        rect = (tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
        return true;
    }

    /// <summary>
    /// 保存前的整体体检：单项都对、整体却不成比例的情况（视图区量太小、玩家格/背包点落在画面外等）在这里指出。
    /// 只提示、不拦保存 —— 用户看到的实际画面才是最终依据。
    /// </summary>
    private static void PrintSanityReport(Dictionary<string, (int X, int Y)> r, InputSimulator win)
    {
        var (cw, ch) = win.GetClientSize();
        Console.WriteLine($"[体检] 保存前复核（客户区 {cw}×{ch}）：");

        if (TryViewRect(r, out var view))
        {
            double ratio = (double)view.W / Math.Max(1, view.H);
            Console.WriteLine($"       主视图: ({view.X},{view.Y}) {view.W}×{view.H}，宽高比 {ratio:0.00}");
            if (ratio is < 1.1 or > 2.0)
                Console.WriteLine("       ⚠ 宽高比不太像游戏主画面（常见 1.2~1.7）—— 不拦保存，挂机前建议复核");

            if (r.TryGetValue("player", out var p))
            {
                bool inside = p.X > view.X && p.X < view.X + view.W && p.Y > view.Y && p.Y < view.Y + view.H;
                Console.WriteLine(inside
                    ? "       玩家格中心: 在视图区内 OK"
                    : $"       ⚠ 玩家格中心 ({p.X},{p.Y}) 不在视图区内 —— 移动/攻击会点到画面外");
            }
            else
            {
                Console.WriteLine("       玩家格中心: 未量（快捷档用视图区中心当初值，随后 --autocalibrate 收敛）");
            }
        }
        else
        {
            Console.WriteLine("       ⚠ 没有成对的视图区左上/右下两点 —— 主视图无法确定，挂机前必须补上");
        }

        if (r.TryGetValue("bag", out var bag) && TryViewRect(r, out var v2))
        {
            bool inView = bag.X > v2.X && bag.X < v2.X + v2.W && bag.Y > v2.Y && bag.Y < v2.Y + v2.H;
            Console.WriteLine(inView
                ? "       ⚠ 背包点落在主视图区内 —— 喝药/找格子可能点到画面里，建议重量"
                : "       背包第一格: 在主视图外 OK");
        }

        if (!r.ContainsKey("miniTL") || !r.ContainsKey("miniBR"))
            Console.WriteLine("       小地图: 未量（远距离移动不可用；近距移动不受影响）");
        if (!r.ContainsKey("dlgLine"))
            Console.WriteLine("       对话框: 未量（NPC 菜单选择不可用）");
    }

    // ------------------------------------------------------------------ 键盘

    /// <summary>安装低级键盘钩子；失败时退回"裸键轮询"并明确告知。</summary>
    private static void InstallHook()
    {
        _proc = HookProc;                                     // 存字段，防止被 GC
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine();
            Console.WriteLine($" ⚠ 低级键盘钩子安装失败 (Win32 错误 {err})，无法拦截游戏按键。");
            Console.WriteLine("   请以管理员身份重新运行本工具；或改用 --legacy-keys 并在游戏内清空 F1–F8。");
            Console.WriteLine();
        }
    }

    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode == HC_ACTION)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    int vk = (int)data.vkCode;
                    if (IsDown(VK_CONTROL) && IsDown(VK_MENU))
                    {
                        int slot = HotKeySlot(vk);
                        if (slot >= 0)
                        {
                            var (sx, sy) = GetCursorPos();        // 近似零开销，必须在钩子里取当场坐标
                            Pending.Enqueue((slot, sx, sy));
                            return (IntPtr)1;                     // 吞掉：游戏永远收不到这个键
                        }
                    }
                }
            }
        }
        catch
        {
            // 钩子回调里绝不抛异常
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>把虚拟键映射到采样槽；返回 -1 表示不是本工具的热键。</summary>
    private static int HotKeySlot(int vk)
    {
        if (vk >= 0x31 && vk <= 0x38) return vk - 0x31;    // 主键盘 1..8
        if (vk == 0x53) return SlotSave;                   // S
        if (vk == 0x51) return SlotAbort;                  // Q
        return -1;
    }

    private static void PumpMessages()
    {
        while (PeekMessage(out MSG m, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref m);
            DispatchMessage(ref m);
        }
    }

    /// <summary>v1 兼容：裸 F1–F8 轮询 + ESC 保存。按键不被消费，游戏会一起响应。</summary>
    private static void PollLegacyKeys()
    {
        var pressed = LegacyPressed;
        for (int i = 0; i < 8; i++)
        {
            int vk = VK_F1 + i;
            if (IsDown(vk) && !pressed.Contains(vk))
            {
                pressed.Add(vk);
                var (sx, sy) = GetCursorPos();
                Pending.Enqueue((i, sx, sy));
            }
            else if (!IsDown(vk))
            {
                pressed.Remove(vk);
            }
        }

        if (IsDown(VK_ESCAPE)) _saveRequested = true;
    }

    private static readonly HashSet<int> LegacyPressed = new();

    // ------------------------------------------------------------------ 配置写入

    /// <summary>把量到的点写进配置；<paramref name="quick"/> 档下没量的项用自动初值补齐（交给 --autocalibrate 收敛）。</summary>
    private static void ApplyConfig(ClientDriverConfig cfg, Dictionary<string, (int X, int Y)> r, bool quick)
    {
        if (r.TryGetValue("viewTL", out var tl) && r.TryGetValue("viewBR", out var br))
        {
            cfg.View.ViewLeft = tl.X;
            cfg.View.ViewTop = tl.Y;
            cfg.View.ViewWidth = br.X - tl.X;
            cfg.View.ViewHeight = br.Y - tl.Y;
        }
        if (r.TryGetValue("player", out var p))
        {
            cfg.View.PlayerScreenX = p.X;
            cfg.View.PlayerScreenY = p.Y;
        }
        if (r.TryGetValue("miniTL", out var mtl) && r.TryGetValue("miniBR", out var mbr))
        {
            cfg.MiniMap.Left = mtl.X;
            cfg.MiniMap.Top = mtl.Y;
            cfg.MiniMap.Width = mbr.X - mtl.X;
            cfg.MiniMap.Height = mbr.Y - mtl.Y;
            // 小地图缩放比：默认按"整张地图铺满小地图"估算，用户在配置里按实际地图尺寸微调
            if (cfg.MiniMap.PixelPerCell <= 0 && cfg.MiniMap.Width > 0)
                cfg.MiniMap.PixelPerCell = Math.Round(cfg.MiniMap.Width / 300.0, 3);   // 常见地图约 300 格宽
        }
        if (r.TryGetValue("bag", out var b))
        {
            cfg.Bag.FirstSlotX = b.X - cfg.Bag.SlotWidth / 2;
            cfg.Bag.FirstSlotY = b.Y - cfg.Bag.SlotHeight / 2;
        }
        if (r.TryGetValue("dlgLine", out var d))
        {
            cfg.Dialog.MenuFirstLineX = d.X;
            cfg.Dialog.MenuFirstLineY = d.Y;
        }
        if (r.TryGetValue("dlgNext", out var n))
        {
            cfg.Dialog.NextPageButtonX = n.X;
            cfg.Dialog.NextPageButtonY = n.Y;
        }

        if (quick && !r.ContainsKey("player"))
        {
            // 快捷档：玩家格像素先取"视图区中心"当自动初值，精度交给 --autocalibrate 走格收敛
            cfg.View.PlayerScreenX = cfg.View.ViewLeft + cfg.View.ViewWidth / 2;
            cfg.View.PlayerScreenY = cfg.View.ViewTop + cfg.View.ViewHeight / 2;
            cfg.View.PlayerAlwaysCentered = true;
            Console.WriteLine($"[校准] 快捷档初值: 玩家格 ({cfg.View.PlayerScreenX},{cfg.View.PlayerScreenY})（视图区中心）");
        }

        if (quick)
        {
            if (cfg.View.CellWidth <= 0) cfg.View.CellWidth = 48;
            if (cfg.View.CellHeight <= 0) cfg.View.CellHeight = 32;
            Console.WriteLine($"[校准] 格尺寸初值: {cfg.View.CellWidth}×{cfg.View.CellHeight}");
            Console.WriteLine("[校准] 下一步: ClientDriverHost.exe --autocalibrate —— 角色站在空地上，自动走几步把格宽/格高收敛准");
        }
    }

    private static string BuildSummary(ClientDriverConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("当前校准状态:");
        sb.AppendLine($"  主视图: {(cfg.View.IsCalibrated ? "OK" : "缺失")} " +
                      $"[区域 {cfg.View.ViewLeft},{cfg.View.ViewTop} {cfg.View.ViewWidth}×{cfg.View.ViewHeight}，" +
                      $"玩家格 ({cfg.View.PlayerScreenX},{cfg.View.PlayerScreenY})，" +
                      $"格尺寸 {cfg.View.CellWidth}×{cfg.View.CellHeight}]");
        sb.AppendLine($"  小地图: {(cfg.MiniMap.IsCalibrated ? "OK" : "缺失")} " +
                      $"[区域 {cfg.MiniMap.Left},{cfg.MiniMap.Top} {cfg.MiniMap.Width}×{cfg.MiniMap.Height}，" +
                      $"缩放 {cfg.MiniMap.PixelPerCell} px/格，模式 {cfg.MiniMap.Mode}]");
        sb.AppendLine($"  背包:   {(cfg.Bag.IsCalibrated ? "OK" : "缺失")}");
        sb.AppendLine($"  对话框: {(cfg.Dialog.IsCalibrated ? "OK" : "缺失")}");
        sb.AppendLine();
        sb.AppendLine("坐标是**客户区相对坐标**，所以之后移动窗口不影响；本次实测已收录进 A 档校准档，");
        sb.AppendLine("换服/重启会自动套用；改分辨率或窗口尺寸会落到另一个档，那时再量一次或跑 --autocalibrate。");
        sb.AppendLine();
        sb.AppendLine("下一步: 自动收敛格尺寸 —— 让角色站到空地上（四周至少一格无遮挡），跑");
        sb.AppendLine("       ClientDriverHost.exe --autocalibrate");
        sb.AppendLine("       它会点几次走格，按\"点 1 格 → 是否恰好走 1 格\"闭环收敛 CellWidth/CellHeight 与玩家格像素；");
        sb.AppendLine("       也可以手工逐格验证：发一次 Walk 到相邻格，确认角色真的朝那一格走了 1 格；");
        sb.AppendLine("       走错方向 → 调 CellWidth/CellHeight，走偏半格 → 微调 PlayerScreenX/Y。");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Win32

    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION = 0;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const uint PM_REMOVE = 0x0001;
    private const uint QS_ALLINPUT = 0x04FF;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static LowLevelKeyboardProc? _proc;
    private static IntPtr _hook = IntPtr.Zero;

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint nCount, IntPtr[]? pHandles, bool bWaitAll, uint dwMilliseconds, uint dwWakeMask);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private static (int X, int Y) GetCursorPos()
        => GetCursorPos(out POINT p) ? (p.X, p.Y) : (0, 0);

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
