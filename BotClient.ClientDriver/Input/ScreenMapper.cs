using BotClient.ClientDriver;

namespace BotClient.ClientDriver.Input;

/// <summary>
/// 世界格坐标 ↔ 窗口客户区像素 的换算。
///
/// 传奇主视图是等距 45° 斜视（isometric）：X 轴向右下、Y 轴向左下，
/// 因此一格 (dx, dy) 在屏幕上的位移是：
///     screenDx = (dx - dy) * CellWidth  / 2
///     screenDy = (dx + dy) * CellHeight / 2
/// CellWidth=48 / CellHeight=32 是绝大多数 GXX 客户端的默认值，但仍然做成配置项，
/// 因为改了分辨率或用了高清补丁这个值会变 —— 校准流程就是实测它。
///
/// 所有输出都是"窗口客户区坐标"，由 <see cref="InputSimulator"/> 负责加上客户区原点。
/// </summary>
public sealed class ScreenMapper
{
    private readonly ClientDriverConfig _cfg;

    public ScreenMapper(ClientDriverConfig cfg) => _cfg = cfg;

    private ViewCalibration V => _cfg.View;
    private MiniMapCalibration M => _cfg.MiniMap;

    public bool ViewReady => V.IsCalibrated;
    public bool MiniMapReady => M.IsCalibrated;

    // ---------------------------------------------------------------- 主视图

    /// <summary>
    /// 把"以玩家为中心的世界位移"换算成主视图内的客户区像素。
    /// 玩家在视图中的位置固定（<see cref="ViewCalibration.PlayerAlwaysCentered"/>），
    /// 这是传奇视图的常规行为；若客户端使用了边缘滚动视图，需要在此处按摄像机偏移修正。
    /// </summary>
    public (int X, int Y) WorldDeltaToViewPixel(int dx, int dy)
    {
        int sx = V.PlayerScreenX + (dx - dy) * V.CellWidth / 2;
        int sy = V.PlayerScreenY + (dx + dy) * V.CellHeight / 2;
        return (sx, sy);
    }

    /// <summary>世界格 → 主视图客户区像素（需要传入当前玩家格）。</summary>
    public (int X, int Y) WorldToViewPixel(int worldX, int worldY, int playerX, int playerY)
        => WorldDeltaToViewPixel(worldX - playerX, worldY - playerY);

    /// <summary>主视图像素 → 该像素对应的世界格（反算，用于诊断"我点到了哪一格"）。</summary>
    public (int X, int Y, double ResidualPx) ViewPixelToWorld(int clientX, int clientY, int playerX, int playerY)
    {
        double halfW = V.CellWidth / 2.0;
        double halfH = V.CellHeight / 2.0;
        double a = (clientX - V.PlayerScreenX) / halfW;    // = dx - dy
        double b = (clientY - V.PlayerScreenY) / halfH;    // = dx + dy
        double dx = (a + b) / 2.0;
        double dy = (b - a) / 2.0;

        int rx = (int)Math.Round(dx);
        int ry = (int)Math.Round(dy);

        // 反算残差：偏离格心多少像素。用于判断"这一下会不会打偏到隔壁格"
        var (cx, cy) = WorldDeltaToViewPixel(rx, ry);
        double residual = Math.Sqrt((cx - clientX) * (cx - clientX) + (cy - clientY) * (cy - clientY));

        return (playerX + rx, playerY + ry, residual);
    }

    /// <summary>主视图内的点是否落在可点击的视图矩形内（超出会被客户端忽略或点到 UI 上）。</summary>
    public bool IsInView(int clientX, int clientY)
    {
        if (!ViewReady) return false;
        return clientX >= V.ViewLeft && clientX < V.ViewLeft + V.ViewWidth
            && clientY >= V.ViewTop && clientY < V.ViewTop + V.ViewHeight;
    }

    /// <summary>
    /// 目标格是否在"能直接点"的范围内。传奇视野约 15 格半径，但**点得到的**远小于此：
    /// 超出视图矩形的点会被裁掉，所以这里用像素矩形做硬约束，越界就返回 false，
    /// 由上层改走"小地图寻路"。
    /// </summary>
    public bool TryGetViewClickPoint(int worldX, int worldY, int playerX, int playerY, out (int X, int Y) point)
    {
        point = default;
        if (!ViewReady) return false;
        var (x, y) = WorldToViewPixel(worldX, worldY, playerX, playerY);
        if (!IsInView(x, y)) return false;
        // 再留一点边距，避免点到视图边缘的滚动感应区
        const int margin = 6;
        if (x < V.ViewLeft + margin || x > V.ViewLeft + V.ViewWidth - margin) return false;
        if (y < V.ViewTop + margin || y > V.ViewTop + V.ViewHeight - margin) return false;
        point = (x, y);
        return true;
    }

    // ---------------------------------------------------------------- 小地图

    /// <summary>世界格 → 小地图客户区像素。返回 null 表示该格不在小地图显示范围内。</summary>
    public (int X, int Y)? WorldToMiniMapPixel(int worldX, int worldY, int playerX, int playerY)
    {
        if (!MiniMapReady) return null;

        double cx, cy;
        if (M.Mode == MiniMapMode.Full)
        {
            cx = M.Left + (worldX - M.OriginMapX) * M.PixelPerCell;
            cy = M.Top + (worldY - M.OriginMapY) * M.PixelPerCell;
        }
        else
        {
            double centerX = M.Left + M.Width / 2.0;
            double centerY = M.Top + M.Height / 2.0;
            cx = centerX + (worldX - playerX) * M.PixelPerCell;
            cy = centerY + (worldY - playerY) * M.PixelPerCell;
        }

        if (cx < M.Left || cx > M.Left + M.Width) return null;
        if (cy < M.Top || cy > M.Top + M.Height) return null;
        return ((int)Math.Round(cx), (int)Math.Round(cy));
    }

    // ---------------------------------------------------------------- 背包 / 对话框

    public bool BagReady => _cfg.Bag.IsCalibrated;
    public bool DialogReady => _cfg.Dialog.IsCalibrated;

    /// <summary>背包 makeIndex → 客户区点击点。</summary>
    public (int X, int Y) BagSlotCenter(int makeIndex) => _cfg.Bag.SlotCenter(makeIndex);

    /// <summary>NPC 对话菜单第 index 行（0-based）的点击点。</summary>
    public (int X, int Y) DialogLineCenter(int index) => _cfg.Dialog.MenuLineCenter(index);

    /// <summary>
    /// 给定"要选第 index 行"，判断需不需要先翻页，并返回目标行在**当前页**内的行号。
    /// </summary>
    public (int PageTurns, int RowInPage) ResolveDialogPage(int index)
    {
        int per = Math.Max(1, _cfg.Dialog.MaxLinesPerPage);
        return (index / per, index % per);
    }

    public (int X, int Y) NextPageButton() => (_cfg.Dialog.NextPageButtonX, _cfg.Dialog.NextPageButtonY);
}
