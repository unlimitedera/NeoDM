using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

public partial class MainWindow
{
    private ClipboardLinkWatcher? _clipboardWatcher;
    private DispatcherTimer? _clipboardTimer;
    private LinkGrabberWindow? _grabber;
    /// <summary>Called with the link grabber right before it is shown, so tests can drive it.</summary>
    public Action<LinkGrabberWindow>? GrabberCreated { get; set; }
    /// <summary>The clipboard counter and text reader the watcher uses; tests replace them. Real defaults read the Windows clipboard.</summary>
    public Func<uint> ClipboardSequence { get; set; } = ClipboardLinkWatcher.SystemSequence;
    public Func<string?> ClipboardText { get; set; } = () =>
    {
        try { var t = Clipboard.ContainsText() ? Clipboard.GetText() : null; return t == SafeClipboard.LastWritten ? null : t; }
        catch (System.Runtime.InteropServices.ExternalException) { return null; }
    };
    public ClipboardLinkWatcher? ClipboardWatcher => _clipboardWatcher;

    private void InitGrabberFeatures()
    {
        AllowDrop = true;
        DragEnter += MainWindow_Drag; DragOver += MainWindow_Drag; DragLeave += (_, _) => SetDropHighlight(false);
        Drop += MainWindow_Drop;
        _clipboardWatcher = new ClipboardLinkWatcher(() => _settings.WatchClipboardForLinks && !_adding && _grabber is null, () => ClipboardSequence(), () => ClipboardText(), text => Dispatcher.BeginInvoke(new Action(() => OfferClipboardLinks(text))));
        Closed += (_, _) => _clipboardTimer?.Stop();
        ApplyClipboardWatch();
    }

    /// <summary>Starts or stops the clipboard timer to match Options. The timer exists only while the option is on.</summary>
    public void ApplyClipboardWatch()
    {
        if (_clipboardWatcher is null) return;
        if (_settings.WatchClipboardForLinks)
        {
            _clipboardWatcher.Poll(); // primes: the current clipboard content is ignored
            _clipboardTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) => { if (!_closed) _clipboardWatcher.Poll(); }, Dispatcher);
            _clipboardTimer.Start();
        }
        else { _clipboardTimer?.Stop(); _clipboardWatcher.Poll(); }
    }

    private void OfferClipboardLinks(string text)
    {
        if (_closed || _grabber is not null || _adding) return;
        Notice.Text = Loc.T("grab.clipboard_offer");
        OpenGrabber(text);
    }

    private void Grabber_Click(object sender, RoutedEventArgs e) => OpenGrabber(null);

    /// <summary>Ctrl+Shift+V: the link grabber with whatever text is on the clipboard.</summary>
    private void GrabberFromClipboard()
    {
        string? text = null;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch (System.Runtime.InteropServices.ExternalException) { Notice.Text = Loc.T("main.notice.clipboard_unavailable"); }
        OpenGrabber(text);
    }

    public void OpenGrabber(string? text)
    {
        if (_adding || _grabber is not null) { _grabber?.Activate(); return; }
        // One YouTube link (pasted, dropped or on the clipboard) goes straight to its dialog: no list to confirm first.
        if (YoutubeRouting.SingleLink(text) is { } single) { _ = RouteYoutubeGuardedAsync(new[] { single.Url }); return; }
        IReadOnlyList<string> youtubeLinks = Array.Empty<string>();
        _adding = true;
        try
        {
            var window = new LinkGrabberWindow(_manager, _settings, text, _saveSettings) { Owner = this };
            _grabber = window;
            GrabberCreated?.Invoke(window);
            var added = window.ShowDialog() == true;
            if (added) youtubeLinks = window.YoutubeLinks;
            if (added && window.Outcome is { Added: > 0 } outcome)
                Notice.Text = outcome.Package is { } package ? Loc.T("grab.notice.added_package", outcome.Added, package) : Loc.T("grab.notice.added", outcome.Added);
            Refresh(true);
        }
        finally { _grabber = null; _adding = false; }
        if (youtubeLinks.Count > 0) _ = RouteYoutubeGuardedAsync(youtubeLinks);
    }

    // ---- drag and drop ----
    private void MainWindow_Drag(object sender, DragEventArgs e)
    {
        e.Effects = EffectsFor(e.Data);
        SetDropHighlight(e.Effects != DragDropEffects.None);
        e.Handled = true;
    }
    /// <summary>Copy when the dragged data holds a .torrent file, a list file, a link or any text; otherwise none.</summary>
    public DragDropEffects EffectsFor(IDataObject? data) => DropPlan.Read(data).HasContent && !_adding ? DragDropEffects.Copy : DragDropEffects.None;
    /// <summary>Draws the dashed frame of the empty list as active (accent colour, light fill) while something is dragged over the window.</summary>
    public void SetDropHighlight(bool on)
    {
        if (DropZoneFrame is null) return;
        DropZoneFrame.Stroke = (Brush)FindResource(on ? "AccentBrush" : "BorderBrush");
        DropZoneFrame.Fill = on ? (Brush)FindResource("AccentSoftBrush") : Brushes.Transparent;
    }
    private async void MainWindow_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        e.Handled = true;
        await HandleDropAsync(DropPlan.Read(e.Data));
    }
    /// <summary>Applies a drop: torrents first (each opens the torrent window), then the text in the link grabber.</summary>
    public async Task HandleDropAsync(DropPlan plan)
    {
        if (_adding || !plan.HasContent) return;
        foreach (var path in plan.Torrents) await AddTorrentPathAsync(path);
        if (!string.IsNullOrWhiteSpace(plan.Text)) OpenGrabber(plan.Text);
        else if (plan.Torrents.Count == 0 && plan.Skipped > 0) Notice.Text = Loc.T("main.drop.unsupported");
    }

    // The Package column takes room only while some download belongs to a package.
    private void UpdatePackageColumn()
    {
        bool any = Rows.Any(r => !string.IsNullOrEmpty(r.Package));
        double want = any ? 110 : 0;
        if (PackageColumn.Width != want) PackageColumn.Width = want;
    }
}
