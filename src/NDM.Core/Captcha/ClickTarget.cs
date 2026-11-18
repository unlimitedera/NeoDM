namespace NDM.Core.Captcha;

// 1.16.2: the facts a real mouse press needs. A widget is measured in the page (CSS pixels), turned into physical screen
// pixels with the page's own pixel ratio, moved by the browser window's place on the virtual screen, and finally pressed
// at the middle of the resulting rectangle. Nothing here touches the mouse itself: see IRealClicker.

/// <summary>A rectangle in physical screen pixels, as the virtual screen counts them (its top left corner may be negative).</summary>
public readonly record struct PixelRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool IsEmpty => W <= 0 || H <= 0;
    public int CenterX => X + W / 2;
    public int CenterY => Y + H / 2;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
    public bool ContainsPoint(PixelRect other) => other.CenterX >= X && other.CenterX < Right && other.CenterY >= Y && other.CenterY < Bottom;
    public static PixelRect FromEdges(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);
    public static PixelRect Of(int x, int y) => new(x, y, 1, 1);
    public override string ToString() => X + "," + Y + " " + W + "x" + H;
}

/// <summary>A rectangle in the page's own pixels (what the page measures with getBoundingClientRect).</summary>
public readonly record struct CssRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public bool IsEmpty => W <= 0 || H <= 0;
    public override string ToString() => X.ToString("0.##") + "," + Y.ToString("0.##") + " " + W.ToString("0.##") + "x" + H.ToString("0.##");
}

/// <summary>Where to press: the widget's rectangle in physical screen pixels (a single pixel rectangle when it comes from the geometry), the page's pixel ratio it was measured with, and the window it belongs to.
/// <see cref="X"/> and <see cref="Y"/> are the middle of the rectangle, which is the point a press uses.</summary>
public sealed record ClickTarget(PixelRect OnScreen, double Dpr, IntPtr Window)
{
    public int X => OnScreen.CenterX;
    public int Y => OnScreen.CenterY;
    public bool IsUsable => !OnScreen.IsEmpty;
    public override string ToString() => X + "," + Y + " (" + OnScreen + ", dpr " + Dpr.ToString("0.##") + ")";
}

/// <summary>Presses the real mouse at a physical screen point. Tests use a fake that only records the points.</summary>
public interface IRealClicker
{
    void Click(int x, int y);
    /// <summary>A press that also names the window to bring forward first.</summary>
    void Click(ClickTarget target);
}

/// <summary>Brings a window to the front and says whether it got there. Tests use a fake that answers at once.</summary>
public interface IWindowFocus
{
    bool Restore(IntPtr window);
}
