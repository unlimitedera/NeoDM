namespace NDM.Core.Captcha;

// 1.16.2: putting the browser back in front before the press. Windows often refuses the first request (the caller is not the
// foreground process any more), so the request is repeated a few times and checked each time afterwards.

/// <summary>Brings a window forward, asks permission first (a background process may not otherwise take the foreground) and checks the result.</summary>
public static class WindowFocus
{
    public const int DefaultAttempts = 3, SettleMs = 40;

    /// <summary>The real thing: asks the system, sets the window, waits a moment, looks again. False after the last attempt.</summary>
    public static bool Restore(IntPtr window) => Restore(window, Native.Foreground, w => Native.SetForeground(w), Native.AllowAnyForeground, DefaultAttempts, Thread.Sleep);

    /// <summary>Testable form: all four system calls and the wait are given.</summary>
    public static bool Restore(IntPtr window, Func<IntPtr> foreground, Action<IntPtr> setForeground, Func<bool> allow, int attempts, Action<int> wait)
    {
        if (window == IntPtr.Zero) return false;
        for (int i = 0; i < Math.Max(1, attempts); i++)
        {
            if (foreground() == window) return true;
            try { allow(); } catch (Exception ex) when (ex is InvalidOperationException or EntryPointNotFoundException) { }
            setForeground(window);
            wait(SettleMs);
            if (foreground() == window) return true;
        }
        return false;
    }
}

/// <summary>The real focus restore behind <see cref="IWindowFocus"/>.</summary>
public sealed class SystemWindowFocus : IWindowFocus
{
    public bool Restore(IntPtr window) => WindowFocus.Restore(window);
}
