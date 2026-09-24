using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BotClient.ClientDriver.Input;

namespace BotClient.ClientDriver;

/// <summary>
/// 校准辅助工具（控制台）。
///
/// 为什么必须有它：C 方案的每一个"世界格 → 屏幕像素"换算都依赖几个实测常量，
/// 这些常量**没法从代码里推出来**（取决于分辨率、客户端皮肤、是否高清补丁）。
/// 靠肉眼估、靠试错猜，是最容易让整套方案显得"时灵时不灵"的地方 ——
/// 所以把它做成一个测量工具，量一次，写进 clientdriver.json，之后就不再依赖手感。
///
/// 用法：把游戏窗口切到前台，按提示把鼠标移到指定位置，按对应功能键记录。
/// 工具会实时显示"鼠标当前在窗口客户区里的坐标"，方便确认测量对象。
/// </summary>
public static class CalibrationTool
{
    private const int VK_F1 = 0x70;
    private const int VK_F2 = 0x71;
    private const int VK_F3 = 0x72;
    private const int VK_F4 = 0x73;
    private const int VK_F5 = 0x74;
    private const int VK_F6 = 0x75;
    private const int VK_F7 = 0x76;
    private const int VK_F8 = 0x77;
    private const int VK_ESCAPE = 0x1B;

    /// <summary>运行校准会话；返回 true 表示已把结果写入配置文件。</summary>
    public static int Run(string configPath)
    {
        var cfg = ClientDriverConfig.Load(configPath);
        var win = new InputSimulator(cfg);
        if (!win.TryLocateWindow(out string why))
        {
            Console.WriteLine($"[校准] 找不到游戏窗口: {why}");
            return 1;
        }

        var (cw, ch) = win.GetClientSize();
        Console.WriteLine("======================================================");
        Console.WriteLine(" 传奇客户端驱动模式 · 校准工具");
        Console.WriteLine("======================================================");
        Console.WriteLine($" 客户区尺寸: {cw} × {ch}");
        Console.WriteLine();
        Console.WriteLine(" 请把游戏窗口切到最前，然后按提示操作：");
        Console.WriteLine($"   F1  记录【玩家所在格的中心】        （角色脚下那一格的中心）");
        Console.WriteLine($"   F2  记录【视图区域左上角】          （主画面区域的左上边界）");
        Console.WriteLine($"   F3  记录【视图区域右下角】          （主画面区域的右下边界）");
        Console.WriteLine($"   F4  记录【小地图左上角】");
        Console.WriteLine($"   F5  记录【小地图右下角】");
        Console.WriteLine($"   F6  记录【背包第一格的中心】");
        Console.WriteLine($"   F7  记录【对话框第一行文字的点击点】");
        Console.WriteLine($"   F8  记录【对话框翻页按钮】");
        Console.WriteLine($"   ESC 保存并退出");
        Console.WriteLine();
        Console.WriteLine(" 提示: 先用 F2/F3 框出视图区，再量玩家格；");
        Console.WriteLine("      游戏里可以按 F9 打开背包、找 NPC 打开对话框以确定后几项。");
        Console.WriteLine("------------------------------------------------------");

        var recorded = new Dictionary<string, (int X, int Y)>();
        var pressed = new HashSet<int>();

        while (true)
        {
            var (sx, sy) = GetCursorPos();
            var (ox, oy) = win.GetClientOrigin();
            int cx = sx - ox;
            int cy = sy - oy;

            Console.Write($"\r 鼠标: 屏幕({sx},{sy})  客户区({cx},{cy})        ");

            foreach (int vk in new[] { VK_F1, VK_F2, VK_F3, VK_F4, VK_F5, VK_F6, VK_F7, VK_F8 })
            {
                if (IsKeyDown(vk) && !pressed.Contains(vk))
                {
                    pressed.Add(vk);
                    string name = vk switch
                    {
                        VK_F1 => "player",
                        VK_F2 => "viewTL",
                        VK_F3 => "viewBR",
                        VK_F4 => "miniTL",
                        VK_F5 => "miniBR",
                        VK_F6 => "bag",
                        VK_F7 => "dlgLine",
                        _ => "dlgNext",
                    };
                    recorded[name] = (cx, cy);
                    Console.WriteLine();
                    Console.WriteLine($"  ✓ {name} = 客户区({cx},{cy})");
                }
                else if (!IsKeyDown(vk))
                {
                    pressed.Remove(vk);
                }
            }

            if (IsKeyDown(VK_ESCAPE)) break;
            Thread.Sleep(60);
        }

        Console.WriteLine();
        ApplyConfig(cfg, recorded);
        cfg.Save(configPath);
        Console.WriteLine($"[校准] 已写入 {configPath}");
        Console.WriteLine(BuildSummary(cfg));
        return 0;
    }

    private static void ApplyConfig(ClientDriverConfig cfg, Dictionary<string, (int X, int Y)> r)
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
        sb.AppendLine("下一步: 逐格验证 —— 在游戏里让角色走到一个空地上，");
        sb.AppendLine("       用 ClientDriverHost 发一次 Walk 到相邻格，确认角色真的朝那一格走了 1 格。");
        sb.AppendLine("       走错方向 → 调整 CellWidth/CellHeight；走偏半格 → 微调 PlayerScreenX/Y。");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private static (int X, int Y) GetCursorPos()
        => GetCursorPos(out POINT p) ? (p.X, p.Y) : (0, 0);

    private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
