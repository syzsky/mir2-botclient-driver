using System.Text;
using BotClient.Assets;
using BotClient.Session;

// ============================================================================
// 地图链路离线自测。
//
// 为什么要有它：BotRuntime.IsWalkable 是设计好的可走性注入点，MirMapFile 也把静态障碍
// 解析得很完整，但两者之间长期**没有接线** —— 寻路把墙当可走，撞墙时服务端不给本人回包，
// 我方坐标从此永久超前，之后所有按坐标的判断全部跟着错，而且一条错误日志都没有。
//
// 本自测用**合成**的 .map 文件覆盖这条链路，不依赖本机是否装了服务端，CI 上也能跑。
// ============================================================================

int pass = 0, fail = 0;

void Check(bool ok, string name, string? detail = null)
{
    if (ok) { pass++; Console.WriteLine($"  [PASS] {name}" + (detail is null ? "" : $" — {detail}")); }
    else { fail++; Console.WriteLine($"  [FAIL] {name}" + (detail is null ? "" : $" — {detail}")); }
}

string work = Path.Combine(Path.GetTempPath(), "mapselftest-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);

try
{
    // ---------------------------------------------------------------- 合成一张 .map
    //
    // 版式按 MirMapFile.TryOpen 的读法：52 字节表头，然后 w*h 个 cell，
    // cellSize 由表头 [28] 决定（6=V6→36 字节，2=V2→14，其它=12）。
    // cell 内偏移：0..1 = BkImg，4..5 = FrImg，6 = btDoorIndex。
    const int w = 4, h = 3, cellSize = 36, headerSize = 52;
    var buf = new byte[headerSize + w * h * cellSize];
    buf[0] = w; buf[1] = 0;                 // width  (LE ushort)
    buf[2] = h; buf[3] = 0;                 // height (LE ushort)
    buf[28] = 6;                            // 格式 = V6

    int Off(int x, int y) => headerSize + (x * h + y) * cellSize;

    // (1,1) 放一堵墙：BkImg 的 $8000 位置位 ⇒ 不可走
    buf[Off(1, 1) + 1] = 0x80;

    // (2,1) 放一扇门：btDoorIndex 高位 ⇒ 按"关闭"算，不可走
    buf[Off(2, 1) + 6] = 0x80;

    // (3,2) 用 FrImg 的 $8000（另一种遮挡写法）⇒ 同样不可走
    buf[Off(3, 2) + 5] = 0x80;

    string mapDir = Path.Combine(work, "Map");
    Directory.CreateDirectory(mapDir);
    File.WriteAllBytes(Path.Combine(mapDir, "t1.map"), buf);

    // ---------------------------------------------------------------- 1. 地图解析
    Console.WriteLine("== 1. MirMapFile 解析与可走性 ==");

    var map = MirMapFile.TryOpen(Path.Combine(mapDir, "t1.map"));
    Check(map is not null, "能打开合成地图");
    if (map is null)
    {
        // 解析失败就到此为止（后面的断言都依赖地图内容），且必须算作失败，不能让测试假绿
        Console.WriteLine("解析失败：后续依赖地图内容的断言无法执行");
        Console.WriteLine($"结果：{pass} 通过, {fail + 1} 失败");
        return 1;
    }

    Check(map.Width == w && map.Height == h, "宽高解析正确", $"{map.Width}×{map.Height}");
    Check(map.Format == MirMapFormat.V6, "格式识别为 V6", map.Format.ToString());

    Check(map.IsWalkable(0, 0), "空地可走");
    Check(!map.IsWalkable(1, 1), "BkImg 高位（墙）不可走");
    Check(!map.IsWalkable(2, 1), "带门的格子按关闭算，不可走");
    Check(!map.IsWalkable(3, 2), "FrImg 高位（遮挡）不可走");

    Check(!map.IsWalkable(w, 0), "越界（x=宽度）返回不可走");
    Check(!map.IsWalkable(0, h), "越界（y=高度）返回不可走");
    Check(!map.IsWalkable(-1, 0), "负坐标返回不可走");

    // 索引约定：x*Height+y。故意让"按行优先读"会得出相反结论的格子来锁住这个约定。
    // (1,1) 是墙、(1,0) 是空地：若把索引写成 y*Width+x，(1,1)→idx 5 仍非墙，
    // 而 (0,1)→idx 1 会命中墙，下面的断言就会红。
    Check(map.IsWalkable(0, 1), "索引按 x*Height+y（不是 y*Width+x）");

    // ---------------------------------------------------------------- 2. 地图库
    Console.WriteLine();
    Console.WriteLine("== 2. MirMapLibrary 目录解析与缓存 ==");

    var lib = MirMapLibrary.TryCreate(mapDir);
    Check(lib is not null, "能在指定目录建库");
    if (lib is not null)
    {
        Check(lib.MapDir == mapDir, "生效目录正确", lib.MapDir);

        var a = lib.Get("t1");
        var b = lib.Get("t1");
        Check(a is not null, "按代码取到地图");
        Check(ReferenceEquals(a, b), "同一张图命中缓存（同一实例）");
        Check(a!.IsWalkable(0, 0) && !a.IsWalkable(1, 1), "取出的地图可走性与直接解析一致");

        Check(lib.Get("不存在的图") is null, "找不到的代码返回 null");
        Check(lib.Get(null) is null, "null 代码返回 null");
        Check(lib.Get("  ") is null, "空白代码返回 null");

        // 换图之后旧图仍可取（缓存上限 2）：连续取两张不同的图不应抛异常
        File.Copy(Path.Combine(mapDir, "t1.map"), Path.Combine(mapDir, "t2.map"), true);
        File.Copy(Path.Combine(mapDir, "t1.map"), Path.Combine(mapDir, "t3.map"), true);
        _ = lib.Get("t2");
        _ = lib.Get("t3");
        Check(lib.Get("t3") is not null, "缓存淘汰后仍能重新加载", "淘汰路径未抛异常");
    }

    // 注意：这里**不能**断言"返回 null"。MirMapLibrary 有兜底候选目录
    // （exe 同目录 / D:\MirServer\Mir200\Map），在装了服务端的机器上它会兜到真实地图目录，
    // 那是设计行为。要锁住的不变量是：要么 null，要么返回的目录里**确实有** .map 文件。
    void CheckDegradesOrResolves(string hint, string name)
    {
        var lib = MirMapLibrary.TryCreate(hint);
        if (lib is null) { Check(true, name, "返回 null（本机没有兜底地图目录）"); return; }
        bool hasMaps = Directory.EnumerateFiles(lib.MapDir, "*.map").Any();
        Check(hasMaps, name, $"兜底到 {lib.MapDir}");
    }

    CheckDegradesOrResolves(Path.Combine(work, "no-such-dir"), "目录不存在：要么 null，要么兜底到真有 .map 的目录");

    string emptyDir = Path.Combine(work, "Empty");
    Directory.CreateDirectory(emptyDir);
    CheckDegradesOrResolves(emptyDir, "空目录：要么 null，要么兜底到真有 .map 的目录");

    // ---------------------------------------------------------------- 3. 源码护栏
    Console.WriteLine();
    Console.WriteLine("== 3. 源码护栏：接线确实存在 ==");

    string? root = FindRepoRoot();
    Check(root is not null, "定位到仓库根目录", root);
    if (root is not null)
    {
        string host = File.ReadAllText(Path.Combine(root, "BotClient.ClientDriver", "ClientDriverHost.cs"));
        Check(host.Contains("MirMapLibrary.TryCreate"), "宿主创建了地图库");
        Check(host.Contains("SetStaticWalkable"), "宿主把可走性接进了运行时");
        Check(host.Contains("SyncWalkableMap"), "宿主跟随换图重新加载可走性");
        Check(host.Contains("IsWalkable"), "可走性落到 BotRuntime.IsWalkable");

        string cli = File.ReadAllText(Path.Combine(root, "BotClientDriverHost", "Program.cs"));
        string ui = File.ReadAllText(Path.Combine(root, "BotClientDriverHostUi", "Host", "UiAttachment.cs"));
        Check(cli.Contains("SetStaticWalkable") && cli.Contains("_runtime.IsWalkable"),
              "无界面宿主的 Attachment 实现了接线");
        Check(ui.Contains("SetStaticWalkable") && ui.Contains("_runtime.IsWalkable"),
              "图形宿主的 UiAttachment 实现了接线");

        // MapWidth/MapHeight 的注释写着"只在真的读到 .map 时才非 0"，但长期无人赋值 ——
        // 于是 PathfindWidth/Height 永远退回默认边长 1024，在 700×700 的图上偏大。
        Check(cli.Contains("_runtime.MapWidth = mapWidth") && cli.Contains("_runtime.MapHeight = mapHeight"),
              "地图尺寸也接进了运行时（PathfindWidth/Height 才有精确值）");
        Check(ui.Contains("_runtime.MapWidth = mapWidth") && ui.Contains("_runtime.MapHeight = mapHeight"),
              "图形宿主同样接了地图尺寸");

        string runtime = File.ReadAllText(Path.Combine(root, "BotClient.Core", "Session", "BotRuntime.cs"));
        Check(runtime.Contains("public Func<int, int, bool>? IsWalkable;"),
              "运行时保留了可走性注入点");

        string finder = File.ReadAllText(Path.Combine(root, "BotClient.Core", "Session", "BotPathFinder.cs"));
        Check(finder.Contains("ArrayPool<int>.Shared.Rent"), "寻路用池化缓冲（不再每次 new 大数组）");
    }

    // ---------------------------------------------------------------- 4. 真实地图（本机装了服务端才跑）
    //
    // 这一节把解析器放到**真实的 .map 文件**上验一遍：合成数据只能证明"我按自己的理解读对了"，
    // 真实文件才能证明这个理解本身没错（尺寸合理、掩码确实生效、比例不像全通或全堵）。
    Console.WriteLine();
    Console.WriteLine("== 4. 真实 .map（本机有则验证，无则跳过）==");

    string realDir = @"D:\MirServer\Mir200\Map";
    if (!Directory.Exists(realDir))
    {
        Console.WriteLine($"  跳过：本机没有 {realDir}");
    }
    else
    {
        var real = MirMapFile.TryOpen(Path.Combine(realDir, "0.map"));
        Check(real is not null, "能打开真实 0.map");
        if (real is not null)
        {
            Check(real.Width > 50 && real.Height > 50, "真实地图尺寸合理", $"{real.Width}×{real.Height}");

            int walkable = 0;
            long total = (long)real.Width * real.Height;
            for (int x = 0; x < real.Width; x++)
                for (int y = 0; y < real.Height; y++)
                    if (real.IsWalkable(x, y)) walkable++;

            double ratio = (double)walkable / total;
            // 全可走 ⇒ 掩码没生效（寻路会把墙当可走）；全不可走 ⇒ 判据写反了。
            // 真实地图两者都不是。
            Check(ratio > 0.10 && ratio < 0.95, "可走比例合理（说明障碍掩码确实生效）", $"{ratio:P1}");
        }
    }

    // ---------------------------------------------------------------- 5. 真实地图上的寻路
    //
    // 这一节直接回答"接通地图可走性到底有没有用"：
    // 在真实的 0.map 上找一对**直线被墙挡住、但确实存在绕行路径**的起终点，
    // 然后对比"给了地图数据"与"没给地图数据"两种情况的输出。
    // 这正是改动前必然失败、改动后才会成功的情形 —— 不是纸面推演。
    Console.WriteLine();
    Console.WriteLine("== 5. 真实地图上的寻路：墙要能绕，不是走直线 ==");

    if (!Directory.Exists(realDir))
    {
        Console.WriteLine($"  跳过：本机没有 {realDir}");
    }
    else
    {
        var realMap = MirMapFile.TryOpen(Path.Combine(realDir, "0.map"));
        if (realMap is null)
        {
            Check(false, "打开真实 0.map 供寻路验证", "打不开，无法验证");
        }
        else
        {
            var (start, target, path) = FindDetourCase(realMap);
            Check(path is not null, "能在 0.map 上找到「需要绕墙」的起终点并求出路径",
                  path is null ? "没找到（地图太平坦？）" : $"{start} → {target}，{path!.Count - 1} 步");

            if (path is not null)
            {
                int cheb = Math.Max(Math.Abs(target.X - start.X), Math.Abs(target.Y - start.Y));
                Check(path.Count - 1 > cheb, "路径比直线长 —— 确实绕了墙",
                      $"步数 {path.Count - 1} vs 直线 {cheb}");

                Check(path.All(p => realMap.IsWalkable(p.X, p.Y)), "路径上每一格都是可走的（没穿墙）");

                bool adjacent = true;
                for (int i = 1; i < path.Count; i++)
                {
                    int dx = Math.Abs(path[i].X - path[i - 1].X);
                    int dy = Math.Abs(path[i].Y - path[i - 1].Y);
                    if (Math.Max(dx, dy) != 1) { adjacent = false; break; }
                }
                Check(adjacent, "路径每一步都是 8 邻接的单步移动");

                // 对照组：不给地图数据时，同样的起终点只会得到一条直线
                // 注意这里必须用 realMap 的尺寸 —— 用第一节那张 4×3 合成图的尺寸会让坐标越界、直接返回 null。
                var straight = BotPathFinder.FindPath(start.X, start.Y, target.X, target.Y,
                    realMap.Width, realMap.Height, (_, _) => true);
                Check(straight is not null && straight.Count - 1 == cheb,
                      "对照组：没有地图数据时只会走直线（步数 = 切比雪夫距离）",
                      straight is null
                          ? $"返回 null（起点 {start}、终点 {target}、图 {realMap.Width}×{realMap.Height}）"
                          : $"{straight.Count - 1} 步");

                Check(straight is not null && straight.Any(p => !realMap.IsWalkable(p.X, p.Y)),
                      "对照组那条直线确实穿过了墙 —— 即：没有地图数据就会撞墙");
            }
        }
    }
}

finally
{
    try { Directory.Delete(work, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"结果：{pass} 通过, {fail} 失败");
return fail == 0 ? 0 : 1;

// 从输出目录往上找仓库根（有 BotClient.Core 目录的那一层）
static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 10 && dir != null; i++)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "BotClient.Core"))) return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

// 在图上找一对"直线被挡、但存在绕行路径"的起终点。
// 采样而不是全图遍历：0.map 有 49 万格，全遍历会慢到没法当自测跑。
static ((int X, int Y) Start, (int X, int Y) Target, List<(int X, int Y)>? Path) FindDetourCase(MirMapFile map)
{
    int[] dx8 = { 0, 1, 1, 1, 0, -1, -1, -1 };
    int[] dy8 = { -1, -1, 0, 1, 1, 1, 0, -1 };

    int step = Math.Max(1, Math.Min(map.Width, map.Height) / 40);
    for (int sy = 0; sy < map.Height; sy += step)
    {
        for (int sx = 0; sx < map.Width; sx += step)
        {
            if (!map.IsWalkable(sx, sy)) continue;

            for (int dist = 20; dist <= 60; dist += 10)
            {
                for (int d = 0; d < 8; d++)
                {
                    int tx = sx + dx8[d] * dist;
                    int ty = sy + dy8[d] * dist;
                    if (!map.IsWalkable(tx, ty)) continue;
                    if (!LineBlocked(map, sx, sy, tx, ty)) continue;   // 直线没被挡，不是我们要的用例

                    var path = BotPathFinder.FindPath(sx, sy, tx, ty, map.Width, map.Height, map.IsWalkable);
                    if (path is not null && path.Count - 1 > dist + 2)
                        return ((sx, sy), (tx, ty), path);
                }
            }
        }
    }
    return ((0, 0), (0, 0), null);
}

// 沿直线逐格采样，只要有不可走的格子就算"被挡住"
static bool LineBlocked(MirMapFile map, int x0, int y0, int x1, int y1)
{
    int cheb = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
    if (cheb == 0) return false;
    for (int i = 1; i <= cheb; i++)
    {
        int x = x0 + (int)Math.Round((x1 - x0) * (double)i / cheb);
        int y = y0 + (int)Math.Round((y1 - y0) * (double)i / cheb);
        if (!map.IsWalkable(x, y)) return true;
    }
    return false;
}
