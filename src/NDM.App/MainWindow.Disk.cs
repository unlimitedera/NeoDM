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

    private void InitDiskStatus()
    {
        _diskTimer = new DispatcherTimer(DiskPollInterval, DispatcherPriority.Background, (_, _) => _ = ProbeDisksNow(), Dispatcher);
        _diskTimer.Stop();
        StateChanged += (_, _) => SyncDiskPolling();
        IsVisibleChanged += (_, _) => SyncDiskPolling();
        Loc.LanguageChanged += DrawDisk;
        ThemeManager.Applied += DrawDisk;
        Closed += (_, _) => { _diskTimer?.Stop(); Loc.LanguageChanged -= DrawDisk; ThemeManager.Applied -= DrawDisk; };
        DrawDisk();
    }

    /// <summary>Polling runs only while the window is on screen: no timer ticks or disk reads while it is minimised or hidden in the tray.</summary>
    private void SyncDiskPolling()
    {
        if (_diskTimer is null || _closed) return;
        bool on = IsVisible && WindowState != WindowState.Minimized;
        if (on && !_diskTimer.IsEnabled) { _diskTimer.Interval = DiskPollInterval; _diskTimer.Start(); _ = ProbeDisksNow(); }
        else if (!on && _diskTimer.IsEnabled) _diskTimer.Stop();
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

    private void Disk_Click(object sender, RoutedEventArgs e) { _disks.Next(); DrawDisk(); }
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
        var lines = new List<string>();
        foreach (var d in _disks.Disks) lines.Add((disk is not null && d.SameDisk(disk) ? "▸ " : "   ") + DiskUsage.TipLine(d));
        lines.Add(Loc.T("disk.tip_hint"));
        DiskButton.ToolTip = string.Join(Environment.NewLine, lines);
        System.Windows.Automation.AutomationProperties.SetHelpText(DiskButton, DiskText.Text);
    }
}
