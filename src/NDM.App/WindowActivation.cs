using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace NDM.App;

/// <summary>The few Win32 calls the activation helper needs; faked in tests.</summary>
public interface IWindowNative
{
    IntPtr ForegroundWindow();
    uint ThreadOf(IntPtr window);
    uint CurrentThread();
    bool IsMinimized(IntPtr window);
    void Restore(IntPtr window);
    bool AttachThreadInput(uint from, uint to, bool attach);
    bool SetForeground(IntPtr window);
    void BringToTop(IntPtr window);
    void TapAlt();
    void Flash(IntPtr window);
    /// <summary>Lets this process take the foreground (AllowSetForegroundWindow with its own id). Default: nothing.</summary>
    void AllowForeground() { }
    /// <summary>True while a full-screen app, a full-screen Direct3D game or presentation mode owns the screen (SHQueryUserNotificationState). Default: false.</summary>
    bool IsFullscreenBusy() => false;
}

/// <summary>Deferred = a full-screen app is in front; the window was not shown and will be presented when the user leaves it.</summary>
public enum ActivationResult { Foreground, Flashed, NoHandle, Deferred }

/// <summary>
/// Brings a NeoDM window above the browser and everything else. Windows only lets the foreground process (or one it has allowed) take focus,
/// and NeoDM usually runs in the background when a browser download is captured. The steps, in order:
/// restore if minimised; attach to the foreground window's input queue and SetForegroundWindow; if refused, the classic Alt key tap and a second
/// try; if Windows still refuses, flash the taskbar button so the dialog is never hidden without notice. The native host also calls
/// AllowSetForegroundWindow for the app process before it sends the request.
/// </summary>
public static class WindowActivation
{
    public static IWindowNative Native { get; set; } = new Win32Native();

    /// <summary>How long a finished download's window stays topmost when Windows refuses it the foreground.</summary>
    public static TimeSpan TopmostFor { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>How often a deferred pop-up checks whether the full-screen app has been left.</summary>
    public static TimeSpan BusyPoll { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Starts a one-shot timer on the UI thread. Replaceable by tests.</summary>
    public static Func<TimeSpan, Action, IDisposable> Schedule { get; set; } = DispatcherSchedule;
    private static readonly Dictionary<Window, Action> Leases = new();
    /// <summary>Pop-ups waiting for the user to leave a full-screen app.</summary>
    public static int DeferredCount { get; private set; }

    private sealed class TimerHandle(DispatcherTimer timer) : IDisposable { public void Dispose() => timer.Stop(); }
    private static IDisposable DispatcherSchedule(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = delay };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
        return new TimerHandle(timer);
    }

    public static bool IsBusy(IWindowNative? native = null) => (native ?? Native).IsFullscreenBusy();
    /// <summary>True while <paramref name="window"/> is held topmost by <see cref="Present"/>.</summary>
    public static bool HasTopmostLease(Window window) => Leases.ContainsKey(window);

    /// <summary>Runs <paramref name="action"/> now, or, while a full-screen app is in front, as soon as the user has left it (checked every <see cref="BusyPoll"/>).</summary>
    public static void WhenClear(Action action, IWindowNative? native = null)
    {
        native ??= Native;
        if (!native.IsFullscreenBusy()) { action(); return; }
        DeferredCount++;
        void Poll() => Schedule(BusyPoll, () =>
        {
            if (native.IsFullscreenBusy()) { Poll(); return; }
            DeferredCount--;
            action();
        });
        Poll();
    }

    /// <summary>
    /// Shows a finished download's window above everything, even when NeoDM is not the foreground app. Layers: restore and show (never without activation);
    /// Topmost, which draws the window above other windows without needing foreground rights; the foreground sequence of <see cref="Activate"/>
    /// (AllowSetForegroundWindow, input-queue attach, SetForegroundWindow, Alt tap); a taskbar flash if Windows still refuses.
    /// Topmost is dropped after <see cref="TopmostFor"/> or at the first mouse, key or activation event. While a full-screen app is in front
    /// nothing is shown (<see cref="ActivationResult.Deferred"/>); the window appears when the user leaves it.
    /// </summary>
    public static ActivationResult Present(Window window, IWindowNative? native = null)
    {
        native ??= Native;
        if (native.IsFullscreenBusy())
        {
            bool closed = false; window.Closed += (_, _) => closed = true;
            WhenClear(() => { if (!closed) Present(window, native); }, native);
            return ActivationResult.Deferred;
        }
        EndLease(window);
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!window.IsVisible) window.Show(); // plain Show: never ShowActivated=false for a finished download
        window.Topmost = true;
        var result = Activate(new WindowInteropHelper(window).Handle, native);
        try { window.Activate(); } catch (InvalidOperationException) { }
        if (result == ActivationResult.Foreground) window.Topmost = false; // already in front: nothing to hold
        else BeginLease(window);
        return result;
    }

    private static void BeginLease(Window window)
    {
        IDisposable? timer = null;
        MouseEventHandler onMouse = null!; KeyEventHandler onKey = null!; MouseButtonEventHandler onClick = null!; EventHandler onActivated = null!, onClosed = null!;
        Action end = () =>
        {
            Leases.Remove(window);
            window.MouseEnter -= onMouse; window.PreviewKeyDown -= onKey; window.PreviewMouseDown -= onClick; window.Activated -= onActivated; window.Closed -= onClosed;
            timer?.Dispose();
            if (window.Topmost) window.Topmost = false;
        };
        onMouse = (_, _) => end(); onKey = (_, _) => end(); onClick = (_, _) => end(); onActivated = (_, _) => end(); onClosed = (_, _) => end();
        window.MouseEnter += onMouse; window.PreviewKeyDown += onKey; window.PreviewMouseDown += onClick; window.Activated += onActivated; window.Closed += onClosed;
        Leases[window] = end;
        timer = Schedule(TopmostFor, end);
    }

    private static void EndLease(Window window) { if (Leases.TryGetValue(window, out var end)) end(); }

    /// <summary>Makes <paramref name="window"/> topmost for a moment (so it opens above other windows) and activates it.</summary>
    public static ActivationResult BringToFront(Window window, IWindowNative? native = null, bool dropTopmost = true)
    {
        native ??= Native;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!window.IsVisible) window.Show();
        window.Topmost = true;
        var result = Activate(new WindowInteropHelper(window).Handle, native);
        try { window.Activate(); } catch (InvalidOperationException) { }
        if (dropTopmost) window.Dispatcher.BeginInvoke(new Action(() => window.Topmost = false), DispatcherPriority.ApplicationIdle); // above the browser now, an ordinary window afterwards
        return result;
    }

    /// <summary>The Win32 part, separated from WPF so it can be tested with a fake.</summary>
    public static ActivationResult Activate(IntPtr handle, IWindowNative native)
    {
        if (handle == IntPtr.Zero) return ActivationResult.NoHandle;
        if (native.IsMinimized(handle)) native.Restore(handle);
        native.AllowForeground();
        var foreground = native.ForegroundWindow();
        if (foreground == handle) return ActivationResult.Foreground;
        uint ours = native.CurrentThread(), theirs = foreground == IntPtr.Zero ? 0 : native.ThreadOf(foreground);
        bool attached = theirs != 0 && theirs != ours && native.AttachThreadInput(ours, theirs, true);
        try
        {
            native.BringToTop(handle);
            if (!native.SetForeground(handle) || native.ForegroundWindow() != handle)
            {
                native.TapAlt();
                native.SetForeground(handle);
            }
        }
        finally { if (attached) native.AttachThreadInput(ours, theirs, false); }
        if (native.ForegroundWindow() == handle) return ActivationResult.Foreground;
        native.Flash(handle); // Windows refused: the taskbar button flashes instead
        return ActivationResult.Flashed;
    }

    private sealed class Win32Native : IWindowNative
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", EntryPoint = "AttachThreadInput")] private static extern bool AttachInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [StructLayout(LayoutKind.Sequential)] private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags; public uint Count; public uint Timeout; }
        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
        public IntPtr ForegroundWindow() => GetForegroundWindow();
        public uint ThreadOf(IntPtr window) => GetWindowThreadProcessId(window, out _);
        public uint CurrentThread() => GetCurrentThreadId();
        public bool IsMinimized(IntPtr window) => IsIconic(window);
        public void Restore(IntPtr window) => ShowWindow(window, 9); // SW_RESTORE
        public bool AttachThreadInput(uint from, uint to, bool attach) => AttachInput(from, to, attach);
        public bool SetForeground(IntPtr window) => SetForegroundWindow(window);
        public void BringToTop(IntPtr window) => BringWindowToTop(window);
        public void TapAlt() { keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); } // VK_MENU down/up unlocks SetForegroundWindow
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
        [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
        public void AllowForeground() { try { AllowSetForegroundWindow((uint)Environment.ProcessId); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { } }
        public bool IsFullscreenBusy()
        {
            try { return SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4; } // QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
        public void Flash(IntPtr window)
        {
            var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window, Flags = 3 | 0xC, Count = 5, Timeout = 0 }; // FLASHW_ALL | FLASHW_TIMERNOFG
            FlashWindowEx(ref info);
        }
    }
}
