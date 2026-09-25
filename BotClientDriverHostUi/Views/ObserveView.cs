using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BotClient.Session;

namespace BotClientDriverHostUi.Views;

/// <summary>
/// 自绘观测区：以角色所在格为中心，画出附近实体（自己/怪物/NPC/玩家/地面物品）。
///
/// 为什么自绘而不是用图片：C 方案下客户端画面来自真机客户端本身，界面里不需要再复制一遍画面；
/// 但“挂机到底看见了什么”必须能被肉眼核对（尤其排障“坐标不对/精灵列表为空”时），
/// 所以这里画的是**状态通道的真实数据**，而不是游戏画面的投影。
/// </summary>
public sealed class ObserveView : FrameworkElement
{
    private static readonly Brush BgBrush = Frozen(Color.FromRgb(0x12, 0x12, 0x12));
    private static readonly Brush SelfBrush = Frozen(Colors.White);
    private static readonly Brush MonsterBrush = Frozen(Color.FromRgb(0xFF, 0x5A, 0x5A));
    private static readonly Brush NpcBrush = Frozen(Color.FromRgb(0xFF, 0xD1, 0x4D));
    private static readonly Brush PlayerBrush = Frozen(Color.FromRgb(0x6E, 0xC8, 0xFF));
    private static readonly Brush ItemBrush = Frozen(Color.FromRgb(0x54, 0xE0, 0xC8));
    private static readonly Brush GoldBrush = Frozen(Color.FromRgb(0xFF, 0xE0, 0x8A));
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0x24, 0x24, 0x24), 1);
    private static readonly Pen AxisPen = FrozenPen(Color.FromRgb(0x33, 0x33, 0x33), 1);
    private static readonly Pen SelfPen = FrozenPen(Color.FromRgb(0x70, 0xB0, 0x70), 1.5);
    private static readonly Typeface Face = new("Consolas");

    private MapDot[] _dots = Array.Empty<MapDot>();
    private int _playerX;
    private int _playerY;
    private int _range = 14;

    /// <summary>由界面定时器每 0.5s 灌一次快照（UI 线程调用）。</summary>
    public void Update(int playerX, int playerY, IEnumerable<MapDot>? dots, int range)
    {
        _playerX = playerX;
        _playerY = playerY;
        _range = Math.Max(4, Math.Min(40, range));

        if (dots == null)
        {
            _dots = Array.Empty<MapDot>();
        }
        else
        {
            var list = new List<MapDot>();
            foreach (MapDot dot in dots)
            {
                list.Add(dot);
                if (list.Count >= 4000) break;
            }

            _dots = list.ToArray();
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 2 || height <= 2) return;

        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, width, height));

        double cell = Math.Min(width, height) / (2.0 * _range + 1);
        if (cell < 2) return;

        double cx = width / 2;
        double cy = height / 2;

        for (int i = -_range; i <= _range; i++)
        {
            double x = cx + i * cell;
            double y = cy + i * cell;
            if (x >= 0 && x <= width) dc.DrawLine(i == 0 ? AxisPen : GridPen, new Point(x, 0), new Point(x, height));
            if (y >= 0 && y <= height) dc.DrawLine(i == 0 ? AxisPen : GridPen, new Point(0, y), new Point(width, y));
        }

        foreach (MapDot dot in _dots)
        {
            int dx = dot.X - _playerX;
            int dy = dot.Y - _playerY;
            if (Math.Abs(dx) > _range || Math.Abs(dy) > _range) continue;

            double x = cx + dx * cell;
            double y = cy + dy * cell;
            Brush brush = BrushFor(dot.Kind);

            if (dot.Kind == DotKind.Self)
            {
                dc.DrawEllipse(brush, SelfPen, new Point(x, y), cell * 0.5, cell * 0.5);
            }
            else
            {
                double r = Math.Max(1.8, cell * 0.34);
                dc.DrawEllipse(brush, null, new Point(x, y), r, r);
            }
        }

        string label = $"中心 ({_playerX},{_playerY})  ±{_range} 格   实体 {_dots.Length}";
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 12, LabelBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(8, Math.Max(4, height - 20)));
    }

    private static Brush BrushFor(DotKind kind) => kind switch
    {
        DotKind.Self => SelfBrush,
        DotKind.Monster => MonsterBrush,
        DotKind.Npc => NpcBrush,
        DotKind.Player => PlayerBrush,
        DotKind.Item => ItemBrush,
        DotKind.Gold => GoldBrush,
        _ => ItemBrush,
    };

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
