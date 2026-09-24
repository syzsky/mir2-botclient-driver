using System.Text;

namespace BotClient.Assets;

public sealed class MirMapFile : IDisposable
{
    private const int HeaderSize = 52;

    private readonly byte[] _cells;
    private readonly int _cellSize;
    private readonly bool[] _walkable;

    private MirMapFile(string mapPath, ushort width, ushort height, MirMapFormat format, byte[] cells, int cellSize, bool[] walkable)
    {
        MapPath = mapPath;
        Width = width;
        Height = height;
        Format = format;
        _cells = cells;
        _cellSize = cellSize;
        _walkable = walkable;
    }

    public string MapPath { get; }
    public ushort Width { get; }
    public ushort Height { get; }
    public MirMapFormat Format { get; }
    public ReadOnlySpan<byte> CellData => _cells;

    public static MirMapFile? TryOpen(string mapPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(mapPath) || !File.Exists(mapPath))
                return null;

            using var fs = new FileStream(mapPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[HeaderSize];
            fs.ReadExactly(header);

            ushort w = BitConverter.ToUInt16(header);
            ushort h = BitConverter.ToUInt16(header[2..]);
            byte fmtByte = header[28];
            long totalCells = (long)w * h;
            if (totalCells <= 0) return null;

            MirMapFormat fmt = fmtByte switch { 6 => MirMapFormat.V6, 2 => MirMapFormat.V2, _ => MirMapFormat.Old };
            int cellSize = fmt switch { MirMapFormat.V6 => 36, MirMapFormat.V2 => 14, _ => 12 };
            int expected = (int)(totalCells * cellSize);
            if (expected <= 0 || expected > int.MaxValue) return null;

            var cells = new byte[expected];
            fs.ReadExactly(cells);

            var walkable = new bool[w * h];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    // 索引/掩码与客户端 MapUnit.pas:911/966 一致:x*Height+y,
                    // (wBkImg & $8000) 或 (wFrImg & $8000) 即禁止通行(服务端 Envir.pas:48 同一条注释)
                    int idx = x * h + y;
                    int off = idx * cellSize;   // cells 从表头之后开始,不再含 HeaderSize 偏移
                    // 直接按小端索引读,避免切片分配(BitConverter.ToUInt16(cells[off..]) 每次分配 Span,49万次非常慢)
                    ushort bk = (ushort)(cells[off] | (cells[off + 1] << 8));
                    ushort fr = (ushort)(cells[off + 4] | (cells[off + 5] << 8));
                    // btDoorIndex 高位有门(MapUnit.pas:968):地图数据里的门一律按关闭算,
                    // 静态判成可走会让 bot 撞门,而服务端不给本人回包,坐标从此失同步
                    bool hasDoor = (cells[off + 6] & 0x80) != 0;
                    walkable[idx] = (bk & 0x8000) == 0 && (fr & 0x8000) == 0 && !hasDoor;
                }

            return new MirMapFile(mapPath, w, h, fmt, cells, cellSize, walkable);
        }
        catch { return null; }
    }

    public bool IsWalkable(int x, int y) =>
        (uint)x < Width && (uint)y < Height && _walkable[x * Height + y];

    public void Dispose() { }
}
