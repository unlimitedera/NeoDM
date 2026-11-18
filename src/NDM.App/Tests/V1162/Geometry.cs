using NDM.Core.Captcha;

internal static partial class V1162Tests
{
    private static readonly CssRect Box = new(100, 50, 30, 20);
    private static readonly CssRect View = new(0, 0, 800, 600);

    /// <summary>The page's pixel ratio is applied in full: a widget is where the page says it is, however the screen scales.</summary>
    private static void Dpi()
    {
        var r100 = CaptchaGeometry.ToPhysical(Box, 1.0, View);
        var r125 = CaptchaGeometry.ToPhysical(Box, 1.25, View);
        var r150 = CaptchaGeometry.ToPhysical(Box, 1.5, View);
        var r200 = CaptchaGeometry.ToPhysical(Box, 2.0, View);
        Check(r100 == new PixelRect(100, 50, 30, 20), "100%: the box is unchanged");
        Check(r125 == new PixelRect(125, 63, 38, 25), "125%: every edge scales and rounds away from zero");
        Check(r150 == new PixelRect(150, 75, 45, 30), "150%: every edge scales");
        Check(r200 == new PixelRect(200, 100, 60, 40), "200%: every edge scales");
        Check(r100.CenterX == 115 && r200.CenterX == 230, "the press point doubles with the ratio");
        var half = CaptchaGeometry.ToPhysical(new CssRect(-10, -10, 30, 20), 1.0, View);
        Check(half == new PixelRect(0, 0, 20, 10), "a box hanging off the page top left is cut at the page edge");
        var big = CaptchaGeometry.ToPhysical(new CssRect(790, 590, 40, 40), 1.0, View);
        Check(big == new PixelRect(790, 590, 10, 10) && big.X + big.W == 800, "a box running past the page edge stops at the edge");
        Check(CaptchaGeometry.Ratio(0) == 1 && CaptchaGeometry.Ratio(double.NaN) == 1 && CaptchaGeometry.Ratio(50) == 1, "a nonsense ratio is replaced by 1");
        Check(CaptchaGeometry.Ratio(1.75) == 1.75, "a real fractional ratio is kept");
        Check(CaptchaGeometry.ToPhysical(Box, 0, View).W == 30, "ratio 0 still gives a usable box");
    }

    /// <summary>Several screens: the page area's corner comes from the window, the window may sit left of and above the first screen, and the press point is kept on the virtual screen.</summary>
    private static void Screens()
    {
        var twoScreens = new PixelRect(-1920, 0, 3840, 1080);
        var onLeft = new PixelRect(-1920, 100, 1000, 800);
        Check(CaptchaGeometry.PageOrigin(onLeft, 1, new CssRect(0, 0, 1000, 800)) == (-1920, 100), "a window at a negative address keeps its place");
        var leftReport = new CaptchaReport(new CssRect(10, 20, 30, 20), new CssRect(0, 0, 1000, 800), 1, onLeft);
        var leftTarget = CaptchaGeometry.Target(leftReport, twoScreens, new IntPtr(7));
        Check(leftTarget is { X: -1895, Y: 130, Window: var h } && h == new IntPtr(7), "negative multi screen: the press point follows the window, not an assumed origin");
        var above = new PixelRect(-1950, -60, 900, 700);
        var aboveReport = new CaptchaReport(new CssRect(0, 0, 20, 20), new CssRect(0, 0, 900, 700), 1, above);
        var aboveTarget = CaptchaGeometry.Target(aboveReport, twoScreens, IntPtr.Zero);
        Check(aboveTarget is { X: -1920, Y: 0 }, "a window partly off the virtual screen: the press point is pulled back onto it");
        var chrome = CaptchaGeometry.PageOrigin(new PixelRect(0, 0, 800, 700), 1, new CssRect(0, 0, 800, 600));
        Check(chrome == (0, 100), "the strip above the page is taken from the window height, so the page corner is below the toolbar");
        var border = CaptchaGeometry.PageOrigin(new PixelRect(0, 0, 820, 600), 1, new CssRect(0, 0, 800, 600));
        Check(border == (10, 0), "a side border is taken from the width difference");
        Check(CaptchaGeometry.Clamp(new PixelRect(1800, 1000, 300, 300), new PixelRect(0, 0, 1920, 1080)) == new PixelRect(1620, 780, 300, 300), "clamp shifts a box back inside instead of squashing it");
        Check(CaptchaGeometry.ClampPoint(5000, -20, new PixelRect(0, 0, 1920, 1080)) == (1919, 0), "clamp point keeps single pixels inside");
        Check(CaptchaGeometry.Target(new CaptchaReport(new CssRect(1, 1, 1, 1), new CssRect(0, 0, 10, 10), 2, new PixelRect(0, 0, 100, 100)), new PixelRect(0, 0, 1920, 1080), IntPtr.Zero) is not null, "a tiny widget still gives a point");
        Check(CaptchaGeometry.Target(new CaptchaReport(new CssRect(0, 0, 0, 0), View, 1, new PixelRect(0, 0, 800, 600)), new PixelRect(0, 0, 1920, 1080), IntPtr.Zero) is null, "an empty widget gives no point");
    }
}
