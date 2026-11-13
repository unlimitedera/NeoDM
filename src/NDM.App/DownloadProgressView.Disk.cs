using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>Disk rows of the progress window (1.9.0): the disk the file is saved to, its free space and the live write activity.</summary>
public partial class DownloadProgressView
{
    private readonly DiskActivityView diskView = new();
    private IDiskBusySource? busy; private string? busyLabel;
    private DiskInfo? diskNow; private double? busyNow;
    private DateTime lastProbe = DateTime.MinValue;
    private int probing;
    private DispatcherTimer? diskAnimation;
    private static double Clock => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    /// <summary>How often the disk is read again. The read runs off the UI thread, one at a time.</summary>
    public static TimeSpan DiskProbeInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Reads the disk now (off the UI thread) and shows it; tests await the task.</summary>
    public Task ProbeDiskNow()
    {
        if (Interlocked.CompareExchange(ref probing, 1, 0) != 0) return Task.CompletedTask;
        lastProbe = DateTime.UtcNow;
        string? folder = Row.FilePath is { Length: > 0 } p ? (Row.IsTorrent ? p : Path.GetDirectoryName(p)) : null;
        return Task.Run(() =>
        {
            DiskInfo? disk = null; double? load = null;
            try
            {
                disk = DiskUsage.Resolve(folder);
                if (disk is { Ready: true } && (busy is null || busyLabel != disk.Label))
                {
                    busy?.Dispose(); busy = PdhDiskBusy.Factory(disk.Label); busyLabel = disk.Label;
                }
                load = disk is { Ready: true } ? busy?.Read() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { disk = null; load = null; }
            return (disk, load);
        }).ContinueWith(t =>
        {
            Interlocked.Exchange(ref probing, 0);
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.BeginInvoke(() => { diskNow = t.Result.disk; busyNow = t.Result.load; UpdateDisk(); });
        }, TaskScheduler.Default);
    }

    /// <summary>Called from the 500 ms refresh: starts a probe when one is due and redraws the rows.</summary>
    private void RefreshDisk()
    {
        if (DateTime.UtcNow - lastProbe >= DiskProbeInterval && IsVisible && !HostMinimized) _ = ProbeDiskNow();
        UpdateDisk();
    }

    private void UpdateDisk()
    {
        double? rate = Row.Status == "Downloading" && Row.Speed > 0 ? Row.Speed : null; // this job's own transfer counter; unknown stays unknown
        bool animate = Row.MotionEnabled && SystemParameters.ClientAreaAnimation && IsVisible;
        diskView.Update(diskNow, rate, busyNow, Clock, animate);
        DrawDisk();
        long remaining = Row.TotalBytes > 0 && !Row.IsComplete ? Math.Max(0, Row.TotalBytes - Row.DownloadedBytes) : 0;
        string warning = DiskUsage.LowSpaceWarning(diskNow, remaining);
        DiskWarning.Text = warning; DiskWarning.Visibility = warning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (diskView.Active && diskAnimation is null)
        {
            diskAnimation = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => { DrawDisk(); if (!diskView.Active || !IsVisible || HostMinimized) StopDiskAnimation(); }, Dispatcher);
            diskAnimation.Start();
        }
    }

    private void DrawDisk()
    {
        double now = Clock;
        DiskText.Text = diskView.DiskText(now);
        DiskActivityText.Text = diskView.ActivityText(now);
    }
    private void StopDiskAnimation() { diskAnimation?.Stop(); diskAnimation = null; }
    private void DisposeDisk() { StopDiskAnimation(); var b = busy; busy = null; b?.Dispose(); }
}
