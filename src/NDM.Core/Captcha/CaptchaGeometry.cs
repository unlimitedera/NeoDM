// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
namespace NDM.Core.Captcha;

// 1.16.2: the arithmetic between a measured widget and a screen point. Plain functions so every pixel-ratio and
// multi screen case can be checked without a mouse. Screen pixels grow with the page's pixel ratio, the page area sits
// inset in the browser window (the side border and the strip above it), and the result is kept on the virtual screen.

public static class CaptchaGeometry
{
    public const double MinDpr = 0.5, MaxDpr = 8;

    /// <summary>The pixel ratio is taken from the page; a nonsense value is replaced by 1.</summary>
    public static double Ratio(double dpr) => double.IsNaN(dpr) || dpr < MinDpr || dpr > MaxDpr ? 1 : dpr;

    /// <summary>Keeps a rectangle inside the bounds: it is moved back in whole, and only shrunk when it is larger than the bounds.</summary>
    public static PixelRect Clamp(PixelRect rect, PixelRect bounds)
    {
        if (bounds.IsEmpty) return rect;
        int w = Math.Max(1, Math.Min(rect.W, bounds.W)), h = Math.Max(1, Math.Min(rect.H, bounds.H));
        int x = rect.X, y = rect.Y;
        if (x + w > bounds.Right) x = bounds.Right - w;
        if (y + h > bounds.Bottom) y = bounds.Bottom - h;
        if (x < bounds.X) x = bounds.X;
        if (y < bounds.Y) y = bounds.Y;
        return new PixelRect(x, y, w, h);
    }

    /// <summary>Keeps a point inside the bounds.</summary>
    public static (int X, int Y) ClampPoint(int x, int y, PixelRect bounds)
    {
        if (bounds.IsEmpty) return (x, y);
        return (Math.Clamp(x, bounds.X, Math.Max(bounds.X, bounds.Right - 1)), Math.Clamp(y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - 1)));
    }

    /// <summary>A page rectangle in physical pixels, as the page area counts them: scaled by the pixel ratio and kept inside the viewport it was measured in.</summary>
    public static PixelRect ToPhysical(CssRect rect, double dpr, CssRect viewport)
    {
        double r = Ratio(dpr);
        double vw = viewport.W > 0 ? viewport.W : rect.Right, vh = viewport.H > 0 ? viewport.H : rect.Bottom;
        double left = Math.Clamp(rect.X, 0, vw), top = Math.Clamp(rect.Y, 0, vh);
        double right = Math.Clamp(rect.Right, left, vw), bottom = Math.Clamp(rect.Bottom, top, vh);
        int x = Round(left * r), y = Round(top * r);
        return new PixelRect(x, y, Math.Max(1, Round((right - left) * r)), Math.Max(1, Round((bottom - top) * r)));
    }

    /// <summary>Where the page area's own top left corner is on the screen: the browser window's corner plus its left border and the strip above the page.</summary>
    public static (int X, int Y) PageOrigin(PixelRect browserWindow, double dpr, CssRect viewport)
    {
        double r = Ratio(dpr);
        int border = (int)Math.Max(0, (browserWindow.W - viewport.W * r) / 2);
        int chrome = (int)Math.Max(0, browserWindow.H - viewport.H * r);
        return (browserWindow.X + border, browserWindow.Y + chrome);
    }

    /// <summary>The press point for a reported widget: the widget's middle in screen pixels, kept on the virtual screen. Null when the numbers do not fit together.</summary>
    public static ClickTarget? Target(CaptchaReport report, PixelRect virtualScreen, IntPtr window)
    {
        if (report is null || !report.IsUsable) return null;
        double r = Ratio(report.Dpr);
        var box = ToPhysical(report.Widget, r, report.Viewport);
        var (ox, oy) = PageOrigin(report.BrowserWindow, r, report.Viewport);
        int x = ox + box.CenterX, y = oy + box.CenterY;
        if (!virtualScreen.IsEmpty) (x, y) = ClampPoint(x, y, virtualScreen);
        return new ClickTarget(PixelRect.Of(x, y), r, window);
    }

    /// <summary>The rectangle a whole widget covers on the screen (for logging and for the extension's own report shape).</summary>
    public static PixelRect OnScreen(CaptchaReport report, PixelRect virtualScreen)
    {
        var box = ToPhysical(report.Widget, report.Dpr, report.Viewport);
        var (ox, oy) = PageOrigin(report.BrowserWindow, report.Dpr, report.Viewport);
        var screen = new PixelRect(ox + box.X, oy + box.Y, box.W, box.H);
        return virtualScreen.IsEmpty ? screen : Clamp(screen, virtualScreen);
    }

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>Reads the virtual screen from the system (all monitors together, so a second monitor on the left gives negative coordinates).</summary>
    public static PixelRect VirtualScreen()
    {
        if (!OperatingSystem.IsWindows()) return new PixelRect(0, 0, 1920, 1080);
        return new PixelRect(Native.Metrics(Native.SmXVirtualScreen), Native.Metrics(Native.SmYVirtualScreen),
            Native.Metrics(Native.SmCxVirtualScreen), Native.Metrics(Native.SmCyVirtualScreen));
    }
}
