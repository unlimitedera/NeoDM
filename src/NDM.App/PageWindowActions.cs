using System.Diagnostics;
using NDM.Core;

namespace NDM.App;

/// <summary>1.16.1: what the resolver windows offer next to the page: open the address in the person's own browser, copy it. Both are plain web addresses only; tests replace the launcher and the clipboard.</summary>
public static class PageWindowActions
{
    public static Action<string> Launcher { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    public static Action<string> ClipboardSetter { get; set; } = text => System.Windows.Clipboard.SetText(text);

    public static bool OpenInBrowser(string? url)
    {
        if (!ResolverSafety.IsWeb(url ?? "") || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        try { Launcher(u.AbsoluteUri); return true; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) { return false; }
    }
    public static bool CopyAddress(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try { ClipboardSetter(url); return true; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException) { return false; }
    }
}

/// <summary>What a session with a real page can do for the window buttons (a scripted test page need not).</summary>
public interface IPageNavigation
{
    string? CurrentAddress { get; }
    bool IsClosed { get; }
    /// <summary>The person closed the window: the link is given up (skipped).</summary>
    event Action? UserClosed;
    event Action? NavigationChanged;
    bool CanGoBack { get; } bool CanGoForward { get; } bool Loading { get; }
    void GoBack(); void GoForward(); void Reload(); void StopLoading();
}
