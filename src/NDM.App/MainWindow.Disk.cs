using System.Windows.Controls;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>Disk usage in the status bar (1.9.0): used and free space of one disk with a thin bar. It starts on the disk of the default download folder, a click (or Enter/Space) shows the next disk, Escape returns.</summary>
public partial class MainWindow
{
    private readonly DiskCycler _disks = new();
    private DispatcherTimer? _diskTimer;
    private int _diskProbing, _diskProbeCount;
    /// <summary>How often the disks are read while the window is shown.</summary>
    public static TimeSpan DiskPollInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Number of disk reads started so far (tests check that polling stops while minimised).</summary>
    public int DiskProbeCount => Volatile.Read(ref _diskProbeCount);
    public bool DiskPollingActive => _diskTimer?.IsEnabled == true;
    public DiskInfo? ShownDisk => _disks.Current;

    // ---- 1.10.0: disk activity beside the size ----
    private readonly DiskActivityMonitor _activity = new();
    private readonly DiskBarActivity _barActivity = new();
    private DispatcherTimer? _activityTimer, _glideTimer;
    private int _activityPolling, _activityPollCount;
    private string _activityLang = "";
    /// <summary>How often the disk activity is read while the window is shown (at most once a second).</summary>
    public static TimeSpan DiskActivityInterval { get; set; } = TimeSpan.FromSeconds(1);
    public int ActivityPollCount => Volatile.Read(ref _activityPollCount);
    public bool ActivityPollingActive => _activityTimer?.IsEnabled == true;
    public bool ActivityGlideActive => _glideTimer?.IsEnabled == true;
    public DiskActivityMonitor ActivityMonitor => _activity;
    /// <summary>Which parts of the disk block fit the window now.</summary>
    public DiskBarLevel DiskLevel { get; private set; } = DiskBarLevel.Full;
    private static double NowSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void InitDiskStatus()
    {
        _diskTimer = new DispatcherTimer(DiskPollInterval, DispatcherPriority.Background, (_, _) => _ = ProbeDisksNow(), Dispatcher);
        _diskTimer.Stop();
        _activityTimer = new DispatcherTimer(DiskActivityInterval, DispatcherPriority.Background, (_, _) => _ = PollActivityNow(), Dispatcher);
        _activityTimer.Stop();
        _glideTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => GlideActivity(), Dispatcher);
        _glideTimer.Stop();
        StateChanged += (_, _) => SyncDiskPolling();
        IsVisibleChanged += (_, _) => SyncDiskPolling();
        Loc.LanguageChanged += DrawDisk;
        ThemeManager.Applied += DrawDisk;
        Closed += (_, _) => { _diskTimer?.Stop(); _activityTimer?.Stop(); _glideTimer?.Stop(); _activity.Dispose(); Loc.LanguageChanged -= DrawDisk; ThemeManager.Applied -= DrawDisk; };
        DrawDisk();
    }

    /// <summary>Polling runs only while the window is on screen: no timer ticks or disk reads while it is minimised or hidden in the tray.</summary>
    private void SyncDiskPolling()
    {
        if (_diskTimer is null || _closed) return;
        bool on = IsVisible && WindowState != WindowState.Minimized;
        if (on && !_diskTimer.IsEnabled) { _diskTimer.Interval = DiskPollInterval; _diskTimer.Start(); _ = ProbeDisksNow(); }
        else if (!on && _diskTimer.IsEnabled) _diskTimer.Stop();
        if (_activityTimer is not null)
        {
            if (on && !_activityTimer.IsEnabled) { _activityTimer.Interval = DiskActivityInterval; _activityTimer.Start(); _ = PollActivityNow(); }
            else if (!on) { _activityTimer.Stop(); _glideTimer?.Stop(); }
        }
    }

    /// <summary>Reads the disks off the UI thread, one read at a time, and redraws. Awaitable so tests can wait for it.</summary>
    public async Task ProbeDisksNow()
    {
        if (_closed || Interlocked.CompareExchange(ref _diskProbing, 1, 0) != 0) return;
        Interlocked.Increment(ref _diskProbeCount);
        try
        {
            string folder = _settings.DownloadDirectory;
            var (all, def) = await Task.Run(() => (DiskUsage.Enumerate(), DiskUsage.Resolve(folder))).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => { if (_closed) return; _disks.Update(all, def); DrawDisk(); }); // always on the UI thread, whatever context the caller had
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TaskCanceledException or System.ComponentModel.Win32Exception) { }
        finally { Interlocked.Exchange(ref _diskProbing, 0); }
    }

    /// <summary>Reads the disk activity off the UI thread (one read at a time) and redraws the shown disk. Awaitable so tests can wait for it.</summary>
    public async Task PollActivityNow()
    {
        if (_closed || Interlocked.CompareExchange(ref _activityPolling, 1, 0) != 0) return;
        Interlocked.Increment(ref _activityPollCount);
        try
        {
            _activity.SetDisks(_disks.Disks);
            await Task.Run(() => _activity.Poll()).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => { if (_closed) return; ShowActivity(true); });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TaskCanceledException or InvalidOperationException) { }
        finally { Interlocked.Exchange(ref _activityPolling, 0); }
    }

    private void ShowActivity(bool animate)
    {
        var disk = _disks.Current;
        var reading = _activity.Get(disk?.Root);
        _barActivity.Update(disk?.Root, reading?.Sample ?? default, NowSeconds, animate && Motion.Allowed);
        DiskSpark.Values = reading?.Plot;
        DrawActivityText();
        if (_barActivity.Active && _glideTimer is { IsEnabled: false } && _activityTimer is { IsEnabled: true }) _glideTimer.Start();
        DrawDiskTip();
        ApplyDiskBarLayout();
    }
    private void DrawActivityText() { DiskActivityText.Text = _barActivity.Text(NowSeconds); }
    private void GlideActivity()
    {
        if (_closed) return;
        DrawActivityText();
        if (!_barActivity.Active) _glideTimer?.Stop();
    }

    private void StatusHost_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyDiskBarLayout();

    /// <summary>Fits the disk block to the window: the sparkline goes first, then the throughput text; the size text and bar stay. Never overlaps the other status items (they keep their place, the notice text shrinks first).</summary>
    private void ApplyDiskBarLayout()
    {
        if (_closed || StatusHost is null || StatusHost.ActualWidth <= 0) return;
        bool has = DiskActivityText.Text.Length > 0;
        var lang = Loc.Language;
        if (_activityLang != lang)
        {
            _activityLang = lang;
            var probe = new TextBlock { Text = Loc.T("disk.activity_rw", Loc.Speed(99.9 * 1024 * 1024), Loc.Speed(99.9 * 1024 * 1024)) + " · " + Loc.T("disk.activity_busy", 100), FontSize = DiskActivityText.FontSize, FontFamily = DiskActivityText.FontFamily };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            DiskActivityText.MinWidth = Math.Ceiling(probe.DesiredSize.Width); // a fixed width: gliding numbers never move the items to the right
        }
        if (!has) { DiskActivityText.Visibility = DiskSpark.Visibility = Visibility.Collapsed; DiskLevel = DiskBarLevel.SizeOnly; return; }
        // widths are summed from the parts (a button keeps its measured size until its content is arranged again)
        DiskText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double chrome = DiskButton.Padding.Left + DiskButton.Padding.Right + 2;
        double baseWidth = chrome + 20 /* icon and its gap */ + DiskText.DesiredSize.Width + (DiskBar.Visibility == Visibility.Visible ? DiskBar.Width + DiskBar.Margin.Left : 0);
        double textWidth = DiskActivityText.Margin.Left + Math.Max(DiskActivityText.MinWidth, 0), sparkWidth = DiskSpark.Margin.Left + DiskSpark.Width;
        Summary.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); RateLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double others = Summary.DesiredSize.Width + Summary.Margin.Left + Summary.Margin.Right + RateLabel.DesiredSize.Width;
        double inner = StatusHost.ActualWidth - StatusHost.Padding.Left - StatusHost.Padding.Right - 60 /* the notice keeps a small minimum and shortens with an ellipsis */ - others - baseWidth;
        var level = DiskBarLayout.Choose(inner, textWidth + sparkWidth, textWidth, DiskLevel);
        DiskLevel = level;
        DiskActivityText.Visibility = level == DiskBarLevel.SizeOnly ? Visibility.Collapsed : Visibility.Visible;
        DiskSpark.Visibility = level == DiskBarLevel.Full ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Disk_Click(object sender, RoutedEventArgs e) { _disks.Next(); DrawDisk(); ShowActivity(false); }
    private void Disk_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _disks.IsDefault) return;
        _disks.Reset(); DrawDisk(); e.Handled = true; // Escape returns to the disk of the download folder
    }

    private void DrawDisk()
    {
        if (_closed) return;
        var disk = _disks.Current;
        DiskText.Text = DiskUsage.StatusText(disk);
        DiskBar.Value = disk is { Ready: true } ? disk.UsedFraction * 100 : 0;
        DiskBar.Visibility = disk is { Ready: true } ? Visibility.Visible : Visibility.Collapsed;
        double used = disk?.UsedFraction ?? 0;
        string brush = disk is not { Ready: true } ? "MutedBrush" : used >= 0.97 ? "ErrorBrush" : used >= 0.90 ? "WarningBrush" : "AccentBrush";
        DiskBar.Foreground = (Brush?)TryFindResource(brush) ?? Brushes.SteelBlue;
        DrawDiskTip();
    }

    /// <summary>Tooltip: every disk with its size and, when its counters can be read, its activity.</summary>
    private void DrawDiskTip()
    {
        var disk = _disks.Current;
        var lines = new List<string>();
        foreach (var d in _disks.Disks)
        {
            lines.Add((disk is not null && d.SameDisk(disk) ? "▸ " : "   ") + DiskUsage.TipLine(d));
            if (_activity.Get(d.Root) is { Sample.HasAny: true } r && DiskBarActivity.TipText(r.Sample) is { Length: > 0 } tip) lines.Add(tip);
        }
        lines.Add(Loc.T("disk.tip_hint"));
        DiskButton.ToolTip = string.Join(Environment.NewLine, lines);
        System.Windows.Automation.AutomationProperties.SetHelpText(DiskButton, (DiskText.Text + " " + DiskActivityText.Text).Trim());
    }
}
