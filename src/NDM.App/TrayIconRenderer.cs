using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace NDM.App;

/// <summary>Fill and text colour of the count badge (the theme accent and the readable text colour for it).</summary>
public readonly record struct TrayBadgeColors(Color Fill, Color Text);

/// <summary>
/// Draws the tray icon: the NeoDM icon, and while downloads are active a slightly accent-tinted icon with a round badge in the upper right corner
/// that shows how many downloads are active (9+ above nine). Rendering is a pure function of its inputs, so it is tested without a tray.
/// </summary>
public static class TrayIconRenderer
{
    /// <summary>Share of the icon's width taken by the badge, ring included.</summary>
    private const double BadgeShare = 0.58;
    /// <summary>How far the icon pixels move toward the accent while downloads are active.</summary>
    private const double ActiveTint = 0.18;

    /// <summary>Text on the badge: nothing for 0, 1 to 9, then 9+.</summary>
    public static string BadgeText(int count) => count <= 0 ? "" : count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);

    /// <summary>Counts that look the same share a bucket (0 to 9, and 10 for every higher count).</summary>
    public static int Bucket(int count) => Math.Clamp(count, 0, 10);

    /// <summary>Badge ring width in pixels for an icon size.</summary>
    public static int RingWidth(int size) => Math.Max(1, (int)Math.Round(size / 20.0));

    /// <summary>Outer diameter (ring included) of the badge in pixels.</summary>
    public static int BadgeDiameter(int size) => Math.Max(11, (int)Math.Round(size * BadgeShare));

    /// <summary>Small icon size of the primary display at the current system DPI (16 at 100%, 20 at 125%, 24 at 150%, 32 at 200%).</summary>
    public static int SystemIconSize()
    {
        try { int size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, NativeMethods.GetDpiForSystem()); return size is >= 16 and <= 64 ? size : 16; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { int size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON); return size is >= 16 and <= 64 ? size : 16; }
    }

    /// <summary>The application icon (Assets/NDM.ico), every frame; the renderer picks the closest one per size.</summary>
    public static IReadOnlyList<BitmapSource> LoadAppIconFrames()
    {
        var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/NeoDM;component/Assets/NDM.ico", UriKind.Absolute), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames.Select(f => (BitmapSource)f).ToList();
    }

    /// <summary>The plain icon at <paramref name="size"/> pixels: the smallest frame that is at least that big, scaled down with high quality when needed.</summary>
    public static BitmapSource RenderBase(IReadOnlyList<BitmapSource> frames, int size)
    {
        var frame = frames.Where(f => f.PixelWidth >= size).OrderBy(f => f.PixelWidth).FirstOrDefault() ?? frames.OrderByDescending(f => f.PixelWidth).First();
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var context = visual.RenderOpen()) context.DrawImage(frame, new Rect(0, 0, size, size));
        return Freeze(Rasterize(visual, size));
    }

    /// <summary>The icon for <paramref name="count"/> active downloads. Zero returns the plain icon.</summary>
    public static BitmapSource Render(IReadOnlyList<BitmapSource> frames, int count, TrayBadgeColors colors, int size)
    {
        var plain = RenderBase(frames, size);
        if (count <= 0) return plain;
        var tinted = Tint(plain, colors.Fill, ActiveTint);
        var visual = new DrawingVisual();
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(tinted, new Rect(0, 0, size, size));
            DrawBadge(context, BadgeText(count), colors, size);
        }
        return Freeze(Rasterize(visual, size));
    }

    /// <summary>Fill of the finished badge. White on it reads at 5.3:1, and the white ring separates it from any accent.</summary>
    public static readonly Color CheckFill = Color.FromRgb(0x1E, 0x7B, 0x34);
    public static readonly Color CheckMark = Colors.White;

    /// <summary>The icon with a small green check mark badge in the same place as the count badge: finished downloads the user has not looked at yet, nothing active.</summary>
    public static BitmapSource RenderCheck(IReadOnlyList<BitmapSource> frames, int size)
    {
        var plain = RenderBase(frames, size);
        var visual = new DrawingVisual();
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(plain, new Rect(0, 0, size, size));
            int outer = BadgeDiameter(size), ring = RingWidth(size);
            double radius = outer / 2.0, cx = size - radius, cy = radius, inner = outer - 2 * ring;
            var ringBrush = new SolidColorBrush(CheckMark); ringBrush.Freeze();
            var fillBrush = new SolidColorBrush(CheckFill); fillBrush.Freeze();
            context.DrawEllipse(ringBrush, null, new Point(cx, cy), radius, radius);
            context.DrawEllipse(fillBrush, null, new Point(cx, cy), inner / 2.0, inner / 2.0);
            var pen = new Pen(ringBrush, Math.Max(1.5, inner * 0.17)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            var tick = new StreamGeometry();
            using (var g = tick.Open())
            {
                g.BeginFigure(new Point(cx - inner * 0.24, cy + inner * 0.02), false, false);
                g.LineTo(new Point(cx - inner * 0.06, cy + inner * 0.21), true, true);
                g.LineTo(new Point(cx + inner * 0.26, cy - inner * 0.18), true, true);
            }
            tick.Freeze();
            context.DrawGeometry(null, pen, tick);
        }
        return Freeze(Rasterize(visual, size));
    }

    private static void DrawBadge(DrawingContext context, string text, TrayBadgeColors colors, int size)
    {
        int outer = BadgeDiameter(size), ring = RingWidth(size);
        double radius = outer / 2.0, cx = size - radius, cy = radius, inner = outer - 2 * ring;
        var ringBrush = new SolidColorBrush(colors.Text); ringBrush.Freeze();
        var fillBrush = new SolidColorBrush(colors.Fill); fillBrush.Freeze();
        context.DrawEllipse(ringBrush, null, new Point(cx, cy), radius, radius);
        context.DrawEllipse(fillBrush, null, new Point(cx, cy), inner / 2.0, inner / 2.0);
        double em = text.Length == 1 ? inner * 0.95 : inner * 0.74;
        var glyphs = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), em, ringBrush, null, TextFormattingMode.Display, 1.0);
        // Digits and the plus sign are about 0.7 em tall: centre that height on the badge, not the line box.
        double baselineY = cy + em * 0.35;
        context.DrawText(glyphs, new Point(cx - glyphs.WidthIncludingTrailingWhitespace / 2.0 + (text.Length == 2 ? 0.2 : 0), baselineY - glyphs.Baseline));
    }

    private static RenderTargetBitmap Rasterize(Visual visual, int size)
    {
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static BitmapSource Freeze(BitmapSource source) { if (source.CanFreeze) source.Freeze(); return source; }

    /// <summary>Moves every visible pixel a fixed share of the way to <paramref name="toward"/> (alpha untouched), so the active state shows even without reading the number.</summary>
    private static BitmapSource Tint(BitmapSource source, Color toward, double share)
    {
        int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4; var data = new byte[stride * h];
        source.CopyPixels(data, stride, 0); // Pbgra32: colour channels are premultiplied by alpha
        for (int i = 0; i < data.Length; i += 4)
        {
            int a = data[i + 3]; if (a == 0) continue;
            data[i] = Mix(data[i], toward.B * a / 255.0, share); data[i + 1] = Mix(data[i + 1], toward.G * a / 255.0, share); data[i + 2] = Mix(data[i + 2], toward.R * a / 255.0, share);
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, data, stride);
        return Freeze(result);
    }
    private static byte Mix(byte from, double to, double share) => (byte)Math.Round(from + (to - from) * share);

    /// <summary>Pixels as BGRA bytes (not premultiplied), row by row.</summary>
    public static byte[] ToBgra(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var data = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(data, converted.PixelWidth * 4, 0);
        return data;
    }

    public static byte[] ToPng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    /// <summary>Makes a Windows icon from an image. The handle belongs to the result and is destroyed by Dispose.</summary>
    public static TrayIconHandle CreateIcon(BitmapSource image)
    {
        int w = image.PixelWidth, h = image.PixelHeight; var bgra = ToBgra(image);
        var pinned = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new System.Drawing.Bitmap(w, h, w * 4, System.Drawing.Imaging.PixelFormat.Format32bppArgb, pinned.AddrOfPinnedObject());
            return new TrayIconHandle(bitmap.GetHicon());
        }
        finally { pinned.Free(); }
    }

    /// <summary>Tray tooltip: "NeoDM" while idle, otherwise "NeoDM, 2 downloading, 1 seeding, 1 finished, 5.4 MB/s" (finished = unseen finished downloads). Windows cuts tooltips at 127 characters.</summary>
    public const int TooltipLimit = 127;
    public static string Tooltip(int downloading, int seeding, double bytesPerSecond, Func<double, string>? format = null, int unseenFinished = 0)
    {
        format ??= DownloadRow.FormatBytes;
        var parts = new List<string> { "NeoDM" };
        if (downloading > 0) parts.Add($"{downloading} downloading");
        if (seeding > 0) parts.Add($"{seeding} seeding");
        if (unseenFinished > 0) parts.Add($"{unseenFinished} finished");
        if (downloading > 0 && bytesPerSecond >= 1 && double.IsFinite(bytesPerSecond)) parts.Add(format(bytesPerSecond) + "/s");
        string text = string.Join(", ", parts);
        return text.Length <= TooltipLimit ? text : text[..TooltipLimit];
    }
}

/// <summary>An HICON made by <see cref="TrayIconRenderer.CreateIcon"/>. Dispose destroys the handle, so replaced tray icons do not leak GDI objects.</summary>
public sealed class TrayIconHandle : IDisposable
{
    private IntPtr _handle;
    public System.Drawing.Icon Icon { get; }
    public TrayIconHandle(IntPtr handle) { _handle = handle; Icon = System.Drawing.Icon.FromHandle(handle); }
    public bool IsDisposed => _handle == IntPtr.Zero;
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle == IntPtr.Zero) return;
        Icon.Dispose();
        NativeMethods.DestroyIcon(handle);
    }
}

internal static class NativeMethods
{
    internal const int SM_CXSMICON = 49;
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr handle);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] internal static extern uint GetDpiForSystem();
}
