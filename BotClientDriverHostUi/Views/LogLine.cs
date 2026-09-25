using System.Windows.Media;

namespace BotClientDriverHostUi.Views;

/// <summary>
/// 日志区一行：界面只做“按标签着色”，不做过滤，保证排障时看到的是完整时间线。
/// </summary>
public sealed class LogLine
{
    private static readonly Brush Normal = Frozen(Color.FromRgb(0xCF, 0xCF, 0xCF));
    private static readonly Brush Error = Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly Brush Warn = Frozen(Color.FromRgb(0xE0, 0xC4, 0x6C));
    private static readonly Brush Ai = Frozen(Color.FromRgb(0x8F, 0xD3, 0xFF));
    private static readonly Brush EventBrush = Frozen(Color.FromRgb(0x9A, 0xE6, 0x6E));
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A));

    public LogLine(string message)
    {
        Display = DateTime.Now.ToString("HH:mm:ss.fff") + " " + message;
        Brush = Pick(message);
    }

    public string Display { get; }

    public Brush Brush { get; }

    private static Brush Pick(string message)
    {
        if (message.Contains("[错误]") || message.Contains("失败") || message.Contains("未就绪"))
        {
            return Error;
        }

        if (message.Contains("[危险]") || message.Contains("[系统]") || message.Contains("[地图]")
            || message.Contains("[成长]") || message.Contains("[提示]") || message.Contains("[聊天]"))
        {
            return Warn;
        }

        if (message.StartsWith("[ai]", StringComparison.Ordinal) || message.StartsWith("[runtime]", StringComparison.Ordinal))
        {
            return Ai;
        }

        if (message.Contains("击杀") || message.StartsWith("[战斗]", StringComparison.Ordinal)
            || message.StartsWith("[拟真]", StringComparison.Ordinal))
        {
            return EventBrush;
        }

        if (message.StartsWith("[标识]", StringComparison.Ordinal) || message.StartsWith("[npcap]", StringComparison.Ordinal))
        {
            return Dim;
        }

        return Normal;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
