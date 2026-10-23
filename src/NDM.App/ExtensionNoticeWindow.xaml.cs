using System.Windows;

namespace NDM.App;

/// <summary>Where a capture's version notice goes: into the capture dialog when there is one, else into a small standalone window.</summary>
public static class ExtensionNoticeFlow
{
    /// <summary>Null when nothing needs to be said (versions match, already shown this session, or muted).</summary>
    public static ExtensionCheck? Present(ExtensionNotice? notice, string? extensionVersion, DownloadOptionsDialog? dialog, string? extensionId = null)
    {
        var check = notice?.Evaluate(extensionVersion, extensionId);
        if (check is null) return null;
        if (dialog is not null) dialog.ShowExtensionNotice(check, notice!.Mute);
        else ExtensionNoticeWindow.Show(check, notice!.Mute);
        return check;
    }
}

/// <summary>Small standalone window for the extension version notice, used when no capture dialog is on screen. It does not take the focus.</summary>
public partial class ExtensionNoticeWindow : Window
{
    public ExtensionNoticeBanner NoticeBanner => Banner;
    public ExtensionNoticeWindow(ExtensionCheck check, Action<string?> mute)
    {
        InitializeComponent();
        Banner.Bind(check, mute);
        Banner.Dismissed += (_, _) => Close();
        Closed += (_, _) => Banner.CommitMute();
    }

    public static ExtensionNoticeWindow Show(ExtensionCheck check, Action<string?> mute)
    {
        var window = new ExtensionNoticeWindow(check, mute);
        window.Show();
        return window;
    }
}
