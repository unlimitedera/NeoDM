// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NDM.App;

/// <summary>2.0.0: questions raised while the user is working in the browser (Add to queue, busy queue, duplicate and low-space warnings, choice and text prompts) are real windows:
/// owned by the main window only when it is visible and not minimised, listed in the taskbar and Alt+Tab, kept above other windows until answered (a click on the browser
/// cannot bury them) and, when Windows refuses to give them the focus, flashing on the taskbar.</summary>
public static class PromptWindows
{
    private const int GwlExStyle = -20, WsExAppWindow = 0x00040000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    /// <summary>The owner a prompt may use: the window must be visible and not minimised, otherwise the prompt stands alone.</summary>
    public static Window? OwnerFor(Window? owner) => owner is { IsVisible: true, WindowState: not WindowState.Minimized } ? owner : null;

    /// <summary>Call before ShowDialog/Show. Sets owner, taskbar entry, topmost and the activation of the window when it first renders.</summary>
    public static void Prepare(Window window, Window? owner)
    {
        window.Owner = OwnerFor(owner) is { } o && !ReferenceEquals(o, window) ? o : null;
        if (window.Owner is null) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowInTaskbar = true;
        window.Topmost = true;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            try { SetWindowLongPtr(handle, GwlExStyle, (IntPtr)(GetWindowLongPtr(handle, GwlExStyle).ToInt64() | WsExAppWindow)); } // listed in Alt+Tab even when owned
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        };
        window.ContentRendered += (_, _) => { if (window.IsVisible) WindowActivation.BringToFront(window, null, dropTopmost: false); }; // flashes the taskbar button when Windows refuses the focus
    }
}
