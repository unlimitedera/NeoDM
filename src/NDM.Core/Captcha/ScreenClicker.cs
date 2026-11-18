namespace NDM.Core.Captcha;

// 1.16.2: the real press. The pointer is moved over the whole virtual desktop (so a second monitor placed left of the
// first still works), a short settle time lets the page's own hover and shadow study see the pointer, then the left
// button goes down and up. Nothing is dispatched into the page, so the widget sees a mouse press as a person makes it.

/// <summary>Presses the real mouse. The point is a physical screen point; the window handle, when given, is brought forward first.</summary>
public sealed class ScreenClicker : IRealClicker
{
    public const int MinSettleMs = 60, MaxSettleMs = 140;
    /// <summary>How long the pointer rests before the press, so the widget can react to the pointer being over it.</summary>
    public const int TapMs = 30;

    public IWindowFocus Focus { get; set; } = new SystemWindowFocus();
    public Func<PixelRect> Screen { get; set; } = CaptchaGeometry.VirtualScreen;
    public Action<int> Wait { get; set; } = Thread.Sleep;
    /// <summary>The one system event sender: (x, y, flags) returns false when the system refused. Tests record the sequence.</summary>
    public Func<int, int, uint, bool> Send { get; set; } = Native.Send;
    /// <summary>True when the system accepted the last event (false means nothing was pressed).</summary>
    public bool LastAccepted { get; private set; }

    /// <summary>A stable settle between the two limits, picked from the point so the timing is not the same every time.</summary>
    public static int SettleFor(int x, int y) => MinSettleMs + (int)((uint)(x * 31 + y * 17) % (uint)(MaxSettleMs - MinSettleMs + 1));

    public void Click(int x, int y) => Click(new ClickTarget(PixelRect.Of(x, y), 1, IntPtr.Zero));

    /// <summary>Focus, move, settle, press, release. The point is clamped to the virtual desktop first.</summary>
    public void Click(ClickTarget target)
    {
        if (!target.IsUsable || !OperatingSystem.IsWindows()) { LastAccepted = false; return; }
        var screen = Screen();
        var (x, y) = CaptchaGeometry.ClampPoint(target.X, target.Y, screen);
        if (!screen.Contains(x, y)) { LastAccepted = false; return; }
        if (target.Window != IntPtr.Zero) Focus.Restore(target.Window);
        var (nx, ny) = Normalise(x, y, screen);
        LastAccepted = Send(nx, ny, Native.MoveFlags);
        if (!LastAccepted) return;                       // the system refused the move: a press would land elsewhere
        Wait(SettleFor(x, y));
        if (!Send(0, 0, Native.DownFlags)) { LastAccepted = false; return; }
        Wait(TapMs);
        LastAccepted = Send(0, 0, Native.UpFlags);
    }

    /// <summary>Turns a screen point into the 0 to 65535 range an absolute mouse event wants, over the whole virtual desktop.</summary>
    public static (int Nx, int Ny) Normalise(int x, int y, PixelRect screen)
    {
        int w = Math.Max(1, screen.W - 1), h = Math.Max(1, screen.H - 1);
        long nx = (long)(x - screen.X) * 65535 / w, ny = (long)(y - screen.Y) * 65535 / h;
        return ((int)Math.Clamp(nx, 0, 65535), (int)Math.Clamp(ny, 0, 65535));
    }
}
