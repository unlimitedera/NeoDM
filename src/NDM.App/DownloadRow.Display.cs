using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using NDM.App.Localization;

namespace NDM.App;

public sealed partial class DownloadRow
{
    private readonly NumericTransition shownBytes = new(), shownTotal = new(), shownSpeed = new(), shownPercent = new(), shownEta = new();
    private static readonly List<WeakReference<DownloadRow>> animated = new();
    private static DispatcherTimer? animationTimer;
    private bool registered, hasDisplay;
    private string? displayPhase;
    private bool motionEnabled = true;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public bool MotionEnabled
    {
        get => motionEnabled;
        set { motionEnabled = value; if (!value) RetargetDisplay(false); }
    }
    /// <summary>Cache of the sidebar category of this row (see MainWindow.Categories): valid while the file name, address and category setup are unchanged.</summary>
    internal string CatKey = ""; internal int CatRevision = -1; internal string? CatName, CatUrl;
    /// <summary>Extension shown inside the file icon (lower case, no dot; empty = generic page or folder).</summary>
    public string IconExtension => IsTorrent && TorrentFiles.Count != 1 ? "" : FileTypeIcons.DisplayExtension(IsTorrent && TorrentFiles.Count == 1 ? TorrentFiles[0].Path : FileName, IsStream, IsYoutube);
    /// <summary>A torrent of several files (or none yet) shows a folder; a torrent of one file shows that file's type.</summary>
    public bool IconFolder => IsTorrent && IconExtension.Length == 0;
    /// <summary>Bytes are moving right now: the progress bar may show its sweep.</summary>
    public bool IsTransferring => Status == "Downloading" && Speed > 0;
    /// <summary>Downloading with no known total: the bar is a full bar with the sweep instead of an empty one.</summary>
    public bool IsUnknownTotal => Status == "Downloading" && TotalBytes <= 0;
    public double DisplayProgress => shownPercent.Value(Now);
    private double DisplayBytes => shownBytes.Value(Now);
    private double DisplayTotal => shownTotal.Value(Now);
    private double DisplaySpeed => shownSpeed.Value(Now);
    public string DisplaySize => TotalBytesEstimated && !IsComplete ? Size : TotalBytes > 0 ? Loc.T("complete.size_exact", FormatBytes(DisplayTotal), TotalBytes) : IsComplete ? FormatBytes(DownloadedBytes) : Loc.T("progress.unknown");
    public string DisplayDownloadedSummary => TotalBytes > 0 ? Loc.T("props.downloaded_value", FormatBytes(DisplayBytes), Percent) : FormatBytes(DisplayBytes);
    public string DisplayRate => Status == "Downloading" ? Loc.T("progress.rate", FormatBytes(DisplaySpeed)) : Loc.T("progress.rate_idle");
    public string DisplayTimeLeft => IsComplete ? Loc.T("row.done") : RemainingTime is not null && Status == "Downloading" ? FormatTime(TimeSpan.FromSeconds(Math.Max(0, shownEta.Value(Now)))) : Loc.T("progress.unknown");
    private void RetargetDisplay(bool allowAnimation = true)
    {
        var phase = Status + ":" + StreamPhase + ":" + YoutubePhase;
        var animate = allowAnimation && hasDisplay && PropertyChanged is not null && motionEnabled && SystemParameters.ClientAreaAnimation && Status == "Downloading" && phase == displayPhase;
        var now = Now;
        shownBytes.Set(DownloadedBytes, now, animate, true);
        shownTotal.Set(TotalBytes, now, animate && TotalBytes > 0 && DisplayTotal > 0);
        shownSpeed.Set(Speed, now, animate);
        shownPercent.Set(Progress, now, animate && TotalBytes > 0);
        shownEta.Set(RemainingTime?.TotalSeconds ?? 0, now, animate && RemainingTime is not null);
        hasDisplay = true; displayPhase = phase;
        if (animate && !registered && HasAnimation)
        {
            registered = true; animated.Add(new(this));
            animationTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, TickAnimation, Dispatcher.CurrentDispatcher);
            animationTimer.Start();
        }
    }
    private bool HasAnimation => shownBytes.Active || shownTotal.Active || shownSpeed.Active || shownPercent.Active || shownEta.Active;
    private static void TickAnimation(object? sender, EventArgs e)
    {
        for (int i = animated.Count - 1; i >= 0; i--)
        {
            if (!animated[i].TryGetTarget(out var row)) { animated.RemoveAt(i); continue; }
            var now = Now;
            _ = row.shownBytes.Value(now); _ = row.shownTotal.Value(now); _ = row.shownSpeed.Value(now); _ = row.shownPercent.Value(now); _ = row.shownEta.Value(now);
            row.RaiseDisplayChanged();
            if (!row.HasAnimation) { row.registered = false; animated.RemoveAt(i); }
        }
        if (animated.Count == 0) animationTimer?.Stop();
    }
    private void RaiseDisplayChanged()
    {
        foreach (var property in DisplayProperties) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
    private static readonly string[] DisplayProperties = { nameof(DisplayProgress), nameof(DisplaySize), nameof(DisplayDownloadedSummary), nameof(DisplayRate), nameof(DisplayTimeLeft), nameof(Percent), nameof(Size), nameof(Downloaded), nameof(SpeedText), nameof(Remaining), nameof(Detail) };
}
