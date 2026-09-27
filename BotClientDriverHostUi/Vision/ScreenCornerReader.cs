using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BotClient.Vision;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace BotClientDriverHostUi.Vision;

/// <summary>
/// 左下角"当前地图 + 坐标"的只读视觉读数：GDI 截客户区左下角一小块 → 本地 Windows OCR（WinRT）。
///
/// 合规边界（刻意为之，改代码前先读这段）：
///   · 只做**屏幕像素拷贝**（CopyFromScreen → GDI BitBlt），不发任何窗口消息、不用 PrintWindow、
///     不设钩子、不注入、不读客户端内存、不碰客户端文件。客户端从输入队列/消息钩子角度看，
///     本进程和"用户自己看屏幕"没有区别 —— 这是"不被检测到非法获取"的底线。
///   · 触发时机：换图后一次 + 低频周期（默认 30s 一次，可关），单次只截 1 小块，开销可忽略。
///   · 结果**只写日志与状态栏**，不参与任何动作决策：挂机动作始终只由嗅探数据驱动。
///     它是一条"嗅探是否可信"的独立校验通道，不是第二个数据源。
/// </summary>
public sealed class ScreenCornerReader
{
    private readonly OcrEngine? _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ScreenCornerReader()
    {
        _engine = TryCreateEngine(out string note);
        EngineNote = note;
    }

    /// <summary>OCR 引擎状态说明（未装语言包时给出处理办法），界面/日志直接显示。</summary>
    public string EngineNote { get; }

    /// <summary>最近一次失败原因（成功时清空）。</summary>
    public string? LastError { get; private set; }

    /// <summary>最近一次 OCR 原始文本（排障用）。</summary>
    public string? LastText { get; private set; }

    public bool Available => _engine != null;

    /// <summary>截取客户区左下角一块并识别。任何异常都转成"不可读 + 原因"，不向上抛。</summary>
    public async Task<CornerReading> ReadAsync(
        long hwnd,
        int bandWidth,
        int bandHeight,
        int leftOffset,
        int bottomOffset,
        int scale,
        CancellationToken ct = default)
    {
        LastError = null;
        LastText = null;

        if (_engine == null)
        {
            LastError = EngineNote;
            return CornerReading.Unreadable;
        }
        if (hwnd == 0)
        {
            LastError = "未绑定客户端窗口";
            return CornerReading.Unreadable;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] png;
            int w, h;
            try
            {
                using Bitmap bmp = Capture(hwnd, bandWidth, bandHeight, leftOffset, bottomOffset, scale);
                w = bmp.Width;
                h = bmp.Height;
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                png = ms.ToArray();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return CornerReading.Unreadable;
            }

            uint maxDim = OcrEngine.MaxImageDimension;
            if (w > maxDim || h > maxDim)
            {
                LastError = $"截取区域过大（{w}×{h}），OCR 上限 {maxDim}";
                return CornerReading.Unreadable;
            }

            try
            {
                string text = await RecognizeAsync(png, ct).ConfigureAwait(false);
                LastText = text;
                CornerReading reading = MapCornerParser.Parse(text);
                if (!reading.Readable)
                    LastError = "区域里没认到地图/坐标文字（可调大 Ocr 区域或确认图例在左下角）";
                return reading;
            }
            catch (Exception ex)
            {
                LastError = "OCR 失败: " + ex.Message;
                return CornerReading.Unreadable;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ 截屏（只读像素）

    private static Bitmap Capture(long hwnd, int bandWidth, int bandHeight, int leftOffset, int bottomOffset, int scale)
    {
        var h = new IntPtr(hwnd);
        if (!IsWindow(h)) throw new InvalidOperationException("窗口句柄已失效");
        if (IsIconic(h)) throw new InvalidOperationException("客户端窗口处于最小化，屏幕上看不到图例");
        if (!GetClientRect(h, out RECT rc)) throw new InvalidOperationException("GetClientRect 失败");

        int cw = rc.Right - rc.Left;
        int ch = rc.Bottom - rc.Top;
        if (cw <= 8 || ch <= 8) throw new InvalidOperationException("客户区尺寸异常");

        int w = Math.Max(32, Math.Min(bandWidth <= 0 ? 520 : bandWidth, cw));
        int hh = Math.Max(16, Math.Min(bandHeight <= 0 ? 48 : bandHeight, ch));

        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(h, ref origin)) throw new InvalidOperationException("ClientToScreen 失败");

        int x = origin.X + (leftOffset < 0 ? 0 : leftOffset);
        int y = origin.Y + ch - hh - (bottomOffset < 0 ? 0 : bottomOffset);
        if (y < origin.Y) y = origin.Y;

        var shot = new Bitmap(w, hh, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(shot))
            g.CopyFromScreen(x, y, 0, 0, new Size(w, hh), CopyPixelOperation.SourceCopy);

        int sc = scale < 1 ? 1 : (scale > 4 ? 4 : scale);
        if (sc == 1) return shot;

        // 图例字很小，放大后再喂 OCR 命中率明显更高（用户不动窗口，缩放是内部行为）
        var big = new Bitmap(w * sc, hh * sc, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(big))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(shot, 0, 0, big.Width, big.Height);
        }
        shot.Dispose();
        return big;
    }

    // ------------------------------------------------------------------ OCR

    private async Task<string> RecognizeAsync(byte[] png, CancellationToken ct)
    {
        using var ras = new InMemoryRandomAccessStream();
        var writer = new DataWriter(ras);
        try
        {
            writer.WriteBytes(png);
            await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
            await writer.FlushAsync().AsTask(ct).ConfigureAwait(false);
            writer.DetachStream();
        }
        finally
        {
            writer.Dispose();
        }

        ras.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(ras).AsTask(ct).ConfigureAwait(false);
        using SoftwareBitmap sb = await decoder
            .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask(ct).ConfigureAwait(false);

        OcrResult result = await _engine!.RecognizeAsync(sb).AsTask(ct).ConfigureAwait(false);
        return result.Text ?? string.Empty;
    }

    private static OcrEngine? TryCreateEngine(out string note)
    {
        try
        {
            OcrEngine? zh = OcrEngine.TryCreateFromLanguage(new Language("zh-Hans"));
            if (zh != null)
            {
                note = "中文简体 OCR 已就绪";
                return zh;
            }

            OcrEngine? any = OcrEngine.TryCreateFromUserProfileLanguages();
            if (any != null)
            {
                note = "未装中文简体 OCR，已回退到用户语言：" + any.RecognizerLanguage.LanguageTag;
                return any;
            }
        }
        catch (Exception ex)
        {
            note = "OCR 初始化异常：" + ex.Message;
            return null;
        }

        note = "系统未安装 OCR 语言包：设置 → 时间和语言 → 语言和区域 → 中文(简体) → 语言选项 → "
             + "可选语言功能里勾选「光学字符识别」，装完重启本程序即可";
        return null;
    }

    // ------------------------------------------------------------------ P/Invoke（只读）

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
}
