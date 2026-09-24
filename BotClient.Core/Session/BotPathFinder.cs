using System.Collections.Generic;

namespace BotClient.Session;

/// <summary>
/// BFS 寻路:8 方向移动,避开不可走格子。
/// 服务端 CM_WALK 是单步移动(WalkTo 只按方向走 1 格),
/// 因此客户端必须自己算出一条到目标点的路径,再逐格发 CM_WALK。
/// </summary>
public static class BotPathFinder
{
    // 8 方向:DR_UP=0, DR_UPRIGHT=1, DR_RIGHT=2, DR_DOWNRIGHT=3,
    //        DR_DOWN=4, DR_DOWNLEFT=5, DR_LEFT=6, DR_UPLEFT=7
    private static readonly int[] DX = { 0, 1, 1, 1, 0, -1, -1, -1 };
    private static readonly int[] DY = { -1, -1, 0, 1, 1, 1, 0, -1 };

    /// <summary>
    /// 寻找从 (startX,startY) 到 (targetX,targetY) 的路径。
    /// 返回的路径包含起点和终点;若不可达返回 null。
    /// isWalkable 判断格子是否可走(边界外视为不可走)。
    /// </summary>
    public static List<(int X, int Y)>? FindPath(
        int startX,
        int startY,
        int targetX,
        int targetY,
        int mapWidth,
        int mapHeight,
        System.Func<int, int, bool> isWalkable,
        int maxVisitedNodes = 40_000)
    {
        if (mapWidth <= 0 || mapHeight <= 0) return null;
        if ((uint)startX >= (uint)mapWidth || (uint)startY >= (uint)mapHeight) return null;
        if ((uint)targetX >= (uint)mapWidth || (uint)targetY >= (uint)mapHeight) return null;
        if (!isWalkable(startX, startY) || !isWalkable(targetX, targetY)) return null;
        if (startX == targetX && startY == targetY)
            return new List<(int, int)> { (startX, startY) };

        // 只在"起点—终点"外扩一圈的窗口里搜:整张图 700x700,按整图开 cameFrom 每次调用就是
        // 2MB 大对象堆,挂机每 650ms 寻一次路能把 GC 拖爆。窗口半径按直线距离给足余量,
        // 绕墙的路径仍在窗口内(maxVisitedNodes 才是真上限)。
        int straight = Math.Max(Math.Abs(targetX - startX), Math.Abs(targetY - startY));
        int half = Math.Max(32, straight + 16);
        int x0 = Math.Max(0, startX - half), x1 = Math.Min(mapWidth - 1, startX + half);
        int y0 = Math.Max(0, startY - half), y1 = Math.Min(mapHeight - 1, startY + half);
        if ((uint)targetX - (uint)x0 > (uint)(x1 - x0) || (uint)targetY - (uint)y0 > (uint)(y1 - y0)) return null;
        int winW = x1 - x0 + 1, winH = y1 - y0 + 1;

        int[] cameFrom = new int[winW * winH];
        int sidx = (startY - y0) * winW + (startX - x0);
        cameFrom[sidx] = -1;

        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((startX, startY));
        int visited = 1;
        bool found = false;

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            if (cx == targetX && cy == targetY) { found = true; break; }
            if (visited >= maxVisitedNodes) break;

            for (int d = 0; d < 8; d++)
            {
                int nx = cx + DX[d];
                int ny = cy + DY[d];
                if (nx < x0 || nx > x1 || ny < y0 || ny > y1) continue;
                int nidx = (ny - y0) * winW + (nx - x0);
                if (cameFrom[nidx] != 0) continue;          // 已访问
                if (!isWalkable(nx, ny)) continue;           // 不可走
                // 对角移动要求两侧格子都可走(避免穿墙角)
                if (d == 1 || d == 3 || d == 5 || d == 7)
                {
                    if (!isWalkable(cx + DX[d], cy) || !isWalkable(cx, cy + DY[d]))
                        continue;
                }
                cameFrom[nidx] = d + 1;                      // 1-based 方向
                queue.Enqueue((nx, ny));
                visited++;
            }
        }

        if (!found) return null;

        // 回溯路径
        var path = new List<(int X, int Y)>();
        int x = targetX, y = targetY;
        while (!(x == startX && y == startY))
        {
            path.Add((x, y));
            int d = cameFrom[(y - y0) * winW + (x - x0)] - 1;
            x -= DX[d];
            y -= DY[d];
        }
        path.Add((startX, startY));
        path.Reverse();
        return path;
    }
}
