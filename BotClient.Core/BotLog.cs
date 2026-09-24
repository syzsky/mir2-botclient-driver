using System.Runtime.CompilerServices;
using System.Text;

namespace BotClient;

/// <summary>
/// 简单文件日志,写到 BotClient.log,方便调试。
/// 日志包含时间戳和调用来源。
/// </summary>
public static class BotLog
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "BotClient.log");
    /// <summary>单文件上限:挂机一夜按包能写几百 MB,超限就滚成 .old 重开。</summary>
    private const long MaxBytes = 8L * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static StreamWriter? _writer;
    private static long _bytes;
    private static long _seq;

    /// <summary>累计写了多少行(跨滚动一直加)。滚动会把文件截断,所以查"是不是有东西在刷屏"只能看这个数,
    /// 不能拿文件大小当差值 —— 一秒几万行时文件早就滚过了。</summary>
    public static long WrittenLines => System.Threading.Interlocked.Read(ref _writtenLines);
    private static long _writtenLines;

    /// <summary>逐包报文日志开关。默认关闭:进世界后其他玩家/怪物的动作广播很密,
    /// 四层(原始接收/解码/会话/接收循环)每包各写一行,挂机一小时能写出几百 MB,
    /// 真正有用的状态日志会被冲掉。排查协议时再打开(设置窗口 → 逐包报文日志)。</summary>
    public static bool VerbosePackets { get; set; }

    static BotLog()
    {
        try
        {
            // 启动清除旧日志,写入带时间标记的起始行
            File.WriteAllText(LogPath, $"=== BotClient start at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n", Utf8NoBom);
            _writer = OpenWriter();
        }
        catch
        {
            // 写不了就算了
        }
    }

    /// <summary>复用同一个文件句柄。原来每行都走 File.AppendAllText(开-写-关),AI 循环每秒几十行时
    /// 句柄翻转加锁竞争会拖住调用线程;AutoFlush=true 保证崩溃也不丢已写内容。
    /// FileShare.ReadWrite 让日志窗口/外部编辑器能同时打开这个文件。</summary>
    private static StreamWriter OpenWriter()
    {
        var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var w = new StreamWriter(fs, Utf8NoBom) { AutoFlush = true };
        _bytes = fs.Length;
        return w;
    }

    public static void Info(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
        => Write("I", message, file, line, member);

    public static void Warn(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
        => Write("W", message, file, line, member);

    public static void Error(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
        => Write("E", message, file, line, member);

    public static void Packet(string direction, ushort ident, string detail, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!VerbosePackets) return;
        Interlocked.Increment(ref _seq);
        Write("P", $"[{direction}] Ident={ident} {detail}", file, line, "");
    }

    /// <summary>逐包报文级日志(原始字节/解码结果),只在 VerbosePackets 打开时落盘。</summary>
    public static void Trace(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
    {
        if (!VerbosePackets) return;
        Write("T", message, file, line, member);
    }

    public static void Net(string direction, string host, int port, string detail, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => Write("N", $"[{direction}] {host}:{port} {detail}", file, line, "");

    private static void Write(string level, string message, string file, int line, string member)
    {
        try
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string source = string.IsNullOrEmpty(member)
                ? Path.GetFileNameWithoutExtension(file)
                : $"{Path.GetFileNameWithoutExtension(file)}.{member}";
            string lineStr = line > 0 ? $"({line})" : "";
            string text = $"[{timestamp}][{level}][{source}{lineStr}] {message}\r\n";
            Interlocked.Increment(ref _writtenLines);

            lock (Gate)
            {
                _writer ??= OpenWriter();
                if (_bytes > MaxBytes)
                {
                    _writer.Dispose();
                    try { File.Move(LogPath, LogPath + ".old", true); } catch { }
                    _writer = OpenWriter();
                }
                _writer.Write(text);
                _bytes += Utf8NoBom.GetByteCount(text);
            }
        }
        catch
        {
            // 日志不能影响运行
        }
    }
}
