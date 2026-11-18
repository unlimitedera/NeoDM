using System.Globalization;

namespace NDM.Core.Captcha;

// 1.16.2: what the loopback page reports back about the widget it drew. The page measures itself (widget box, viewport,
/// its window's place and size, the pixel ratio) and posts the numbers; the app turns them into a screen point.
/// The field names are short because they travel in a form body: x,y,w,h widget box; vw,vh viewport; dpr pixel ratio;
/// eleft,etop,ew,eh the browser window's own left, top, width and height.
public sealed record CaptchaReport(CssRect Widget, CssRect Viewport, double Dpr, PixelRect BrowserWindow)
{
    public const int MaxEdge = 40_000;

    public bool IsUsable => !Widget.IsEmpty && !Viewport.IsEmpty && Dpr > 0 && BrowserWindow.W > 0 && BrowserWindow.H > 0;

    /// <summary>Reads the posted fields. False with a short reason when a number is missing, unreadable or out of range.</summary>
    public static bool TryParse(IReadOnlyDictionary<string, string> form, out CaptchaReport report, out string why)
    {
        report = null!;
        var numbers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "x", "y", "w", "h", "vw", "vh", "dpr", "eleft", "etop", "ew", "eh" })
        {
            if (!form.TryGetValue(name, out var text) || text.Trim().Length == 0) { why = "missing-" + name; return false; }
            if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value) || double.IsInfinity(value)) { why = "unreadable-" + name; return false; }
            numbers[name] = value;
        }
        var widget = new CssRect(numbers["x"], numbers["y"], numbers["w"], numbers["h"]);
        var viewport = new CssRect(0, 0, numbers["vw"], numbers["vh"]);
        double dpr = numbers["dpr"];
        var window = new PixelRect((int)numbers["eleft"], (int)numbers["etop"], (int)numbers["ew"], (int)numbers["eh"]);
        if (widget.IsEmpty) { why = "widget-box"; return false; }
        if (viewport.IsEmpty) { why = "viewport"; return false; }
        if (dpr is < 0.3 or > 8) { why = "pixel-ratio"; return false; }
        if (window.W <= 0 || window.H <= 0) { why = "window"; return false; }
        // a widget scrolled out of the page is reported with a negative or oversized box; the geometry clamps it later
        if (Math.Abs(widget.X) > MaxEdge || Math.Abs(widget.Y) > MaxEdge || widget.W > MaxEdge || widget.H > MaxEdge) { why = "range"; return false; }
        report = new CaptchaReport(widget, viewport, dpr, window); why = ""; return true;
    }

}
