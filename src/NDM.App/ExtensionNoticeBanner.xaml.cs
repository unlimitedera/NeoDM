using System.Windows;
using System.Windows.Controls;

namespace NDM.App;

/// <summary>The "your browser extension and NeoDM have different versions" notice. Never blocks anything: it only offers the install guide or goes away.</summary>
public partial class ExtensionNoticeBanner : UserControl
{
    private Action<string?>? _mute;
    private bool _muted;
    public ExtensionCheck? Check { get; private set; }
    public string Message => MessageText.Text;
    /// <summary>Raised after Dismiss or after the guide was opened.</summary>
    public event EventHandler? Dismissed;
    /// <summary>Test seam; the default opens (or raises) the install guide window.</summary>
    public Action OpenGuide { get; set; } = () => ExtensionGuideWindow.ShowOrActivate();
    /// <summary>Test seam; the default opens the extension's Chrome Web Store page (for the store build the banner offers this instead of the unpacked install guide).</summary>
    public Action OpenStore { get; set; } = () => UrlOpener.Open(ExtensionGuide.StoreUrl);
    public bool IsStoreNotice => Check?.Channel == ExtensionChannel.Store;
    public string ActionButtonText => GuideButton.Content?.ToString() ?? "";
    public string CloseButtonText => DismissButton.Content?.ToString() ?? "";

    public ExtensionNoticeBanner() { InitializeComponent(); Visibility = Visibility.Collapsed; }

    public void Bind(ExtensionCheck check, Action<string?> mute)
    {
        Check = check; _mute = mute; _muted = false; MuteBox.IsChecked = false;
        MessageText.Text = check.Message;
        // The store build updates itself: no unpacked-install guide for it, a store-specific message with 'Open the store page' and 'Close'.
        bool store = check.Channel == ExtensionChannel.Store && check.Status == ExtensionStatus.Older;
        GuideButton.Content = Localization.Loc.T(store ? "notice.btn.open_store" : "notice.btn.show_install_guide");
        DismissButton.Content = Localization.Loc.T(store ? "common.btn.close" : "notice.btn.dismiss");
        Visibility = Visibility.Visible;
    }

    /// <summary>Stores "don't remind me" once, when the box is ticked. Called on Dismiss, on opening the guide and when the host window closes.</summary>
    public void CommitMute()
    {
        if (MuteBox.IsChecked != true || _muted || Check is null) return;
        _muted = true; _mute?.Invoke(Check.ExtensionVersion);
    }

    private void Guide_Click(object sender, RoutedEventArgs e) { CommitMute(); if (Check is { Channel: ExtensionChannel.Store, Status: ExtensionStatus.Older }) OpenStore(); else OpenGuide(); Hide(); }
    private void Dismiss_Click(object sender, RoutedEventArgs e) { CommitMute(); Hide(); }
    private void Hide() { Visibility = Visibility.Collapsed; Dismissed?.Invoke(this, EventArgs.Empty); }
}
