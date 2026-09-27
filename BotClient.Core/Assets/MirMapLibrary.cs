namespace BotClient.Assets;

/// <summary>
/// .map 文件库：按地图代码加载 <see cref="MirMapFile"/>，并缓存最近用过的几张。
///
/// 为什么需要它：<c>BotRuntime.IsWalkable</c> 是设计好的可走性注入点，
/// <see cref="MirMapFile"/> 也把静态障碍（BkImg/FrImg 的 $8000 掩码 + 地图里的门）解析得很完整，
/// 但两者之间**一直没有接线**。结果是 staticWalkable 恒为 null，
/// <c>BotRuntime.EffectiveWalkable</c> 把每个格子都判成可走，BFS 只会走直线：
/// 墙一挡就撞墙，而服务端对撞墙**不给本人任何回包**，我方坐标从此永久超前，
/// 之后所有按坐标的判断（攻击距离、NPC 15 格、拾取踩格）全部跟着错。
///
/// 目录解析与 <see cref="MapInfoFile"/> 保持同一套候选顺序，多给几个兜底：
/// 配置里的 .map 目录 → 它的上级 Map 兄弟目录 → 它下面的 Map 子目录 →
/// exe 同目录的 Map → exe 同目录 → D:\MirServer\Mir200\Map。
/// 一份都找不到就返回 null，调用方退回"全可走"（至少不会让 bot 原地不动）。
/// </summary>
public sealed class MirMapLibrary
{
    /// <summary>
    /// 同时缓存几张地图。V6 地图的格子数组可能好几 MB（本机 0.map 有 5.88MB），
    /// 所以缓存刻意开得很小 —— 换图是低频事件，重载一次的代价远小于常驻几十 MB。
    /// </summary>
    private const int CacheLimit = 2;

    private readonly string _mapDir;
    private readonly Dictionary<string, MirMapFile?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    private MirMapLibrary(string mapDir) => _mapDir = mapDir;

    /// <summary>实际生效的 .map 目录（诊断用）。</summary>
    public string MapDir => _mapDir;

    /// <summary>能定位到含 .map 文件的目录就返回实例，否则 null。</summary>
    public static MirMapLibrary? TryCreate(string? mapDirHint)
    {
        foreach (string dir in CandidateDirs(mapDirHint))
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                // 目录里至少得有一张 .map，否则可能是个空的占位目录
                if (!Directory.EnumerateFiles(dir, "*.map").Any()) continue;
                return new MirMapLibrary(dir);
            }
            catch
            {
                // 权限/路径异常：换下一个候选
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirs(string? mapDirHint)
    {
        if (!string.IsNullOrWhiteSpace(mapDirHint))
        {
            yield return mapDirHint;
            // MapInfo.txt 在 <Envir> 下、.map 在 <Envir> 的兄弟目录 Map 下；
            // 用户填了 Envir 或 Map 任一个，这里都能兜住。
            yield return Path.Combine(mapDirHint, "..", "Map");
            yield return Path.Combine(mapDirHint, "Map");
        }
        yield return Path.Combine(AppContext.BaseDirectory, "Map");
        yield return AppContext.BaseDirectory;
        yield return @"D:\MirServer\Mir200\Map";
    }

    /// <summary>按地图代码取地图（如 <c>0</c>、<c>D1101</c>）。文件不存在返回 null。</summary>
    public MirMapFile? Get(string? mapCode)
    {
        if (string.IsNullOrWhiteSpace(mapCode)) return null;

        string code = mapCode.Trim();
        if (_cache.TryGetValue(code, out var cached)) return cached;

        MirMapFile? map = null;
        foreach (string name in new[] { code + ".map", code + ".MAP" })
        {
            map = MirMapFile.TryOpen(Path.Combine(_mapDir, name));
            if (map != null) break;
        }

        _cache[code] = map;
        _order.Add(code);

        // 只淘汰"不在最近 CacheLimit 次里"的条目。注意 null（找不到）也进缓存：
        // 地图代码是有限集合，负缓存能避免每次都去碰一次文件系统。
        while (_order.Count > CacheLimit)
        {
            string evict = _order[0];
            _order.RemoveAt(0);
            if (evict.Equals(code, StringComparison.OrdinalIgnoreCase)) continue;   // 别把刚放进去的淘汰掉
            if (_cache.Remove(evict, out var old)) old?.Dispose();
        }
        return map;
    }
}
