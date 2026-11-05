using System.Runtime.InteropServices;

namespace NDM.App;


/// <summary>What Windows says about the user's current notification state (SHQUERY_USER_NOTIFICATION_STATE).</summary>
public enum UserNotificationState { NotPresent = 1, Busy = 2, FullScreenApp = 3, PresentationMode = 4, AcceptsNotifications = 5, QuietTime = 6, WindowsStoreApp = 7, Unknown = 0 }

/// <summary>Everything the toast policy asks the machine. Tests supply a fake; nothing here shows anything.</summary>
public interface IShellNotificationState
{
    UserNotificationState State { get; }
    bool ForegroundIsFullScreen { get; }
    TimeSpan IdleFor { get; }
}

public enum ToastDecision { Show, NotQuiet, NotIdle, Busy, FullScreen, AlreadyShown, Disabled }

/// <summary>Decides if a single "update available" notice may appear now. The status bar pill never depends on this.</summary>
public static class UpdateToastPolicy
{
    public static readonly TimeSpan IdleNeeded = TimeSpan.FromSeconds(60);

    public static ToastDecision Decide(UpdateNotifyMode mode, IShellNotificationState shell, string version, string lastToastVersion)
    {
        if (mode != UpdateNotifyMode.WhenIdle) return ToastDecision.Disabled;
        if (string.Equals(version, lastToastVersion, StringComparison.Ordinal)) return ToastDecision.AlreadyShown;
        if (shell.State != UserNotificationState.AcceptsNotifications) return shell.State == UserNotificationState.QuietTime ? ToastDecision.NotQuiet : ToastDecision.Busy;
        if (shell.ForegroundIsFullScreen) return ToastDecision.FullScreen;
        if (shell.IdleFor < IdleNeeded) return ToastDecision.NotIdle;
        return ToastDecision.Show;
    }
}

/// <summary>Real answers from Windows (SHQueryUserNotificationState, the foreground window against its monitor, last input time). Read only.</summary>
public sealed class SystemNotificationState : IShellNotificationState
{
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int count);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public int Size; public int Time; }

    public UserNotificationState State
    {
        get
        {
            try { return SHQueryUserNotificationState(out var s) == 0 && Enum.IsDefined(typeof(UserNotificationState), s) ? (UserNotificationState)s : UserNotificationState.Busy; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return UserNotificationState.Busy; } // cannot tell: stay quiet
        }
    }

    public bool ForegroundIsFullScreen
    {
        get
        {
            try
            {
                var window = GetForegroundWindow();
                if (window == IntPtr.Zero || !GetWindowRect(window, out var r)) return false;
                var name = new System.Text.StringBuilder(64); GetClassName(window, name, name.Capacity);
                if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false; // the desktop and the taskbar cover the screen too
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) return false;
                return r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top && r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return true; }
        }
    }

    public TimeSpan IdleFor
    {
        get
        {
            try
            {
                var info = new LastInput { Size = Marshal.SizeOf<LastInput>() };
                return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - (uint)info.Time)) : TimeSpan.Zero;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return TimeSpan.Zero; }
        }
    }
}
