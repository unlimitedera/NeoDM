using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

public sealed partial class DownloadRow : INotifyPropertyChanged
{
    public string Id { get; }
    public string Url { get; private set; } = "";
    public string FileName { get; private set; } = "";
    public string FilePath { get; private set; } = "";
    public string? Referer { get; private set; }
    public DateTimeOffset? AddedAt { get; private set; }
    public int TorrentFileCount { get; private set; }
    public string Status { get; private set; } = "";
    public string? Error { get; private set; }
    public DateTimeOffset? ScheduledAt { get; private set; }
    public long TotalBytes { get; private set; }
    public long DownloadedBytes { get; private set; }
    public double Speed { get; private set; }
    public double Progress { get; private set; }
    public TimeSpan? RemainingTime { get; private set; }
    public bool? ResumeSupported { get; private set; }
    public int ActiveConnectionCount { get; private set; }
    public int ConnectionLimit { get; private set; }
    public IReadOnlyList<DownloadSegmentSnapshot> Segments { get; private set; } = Array.Empty<DownloadSegmentSnapshot>();
    /// <summary>One row per connection for the progress window; a job that is still connecting shows one placeholder row.</summary>
    public IReadOnlyList<DownloadSegmentRow> ConnectionRows => Segments.Count == 0 && Status is "Connecting" or "Downloading"
        ? new[] { new DownloadSegmentRow(1, new DownloadSegmentSnapshot(0, 0, 0, false), true) }
        : Segments.Select((s, i) => new DownloadSegmentRow(i + 1, s, Status is "Downloading" or "Connecting")).ToArray();
    public IReadOnlyList<DownloadSegmentRow> SegmentRows => Segments.Select((s, i) => new DownloadSegmentRow(i + 1, s)).ToArray();
    // Torrent adapter: a torrent snapshot is projected onto the same columns; Id is "bt:<info hash>" so it can never collide with a job id.
    public bool IsTorrent { get; private set; }
    public string TorrentId { get; private set; } = "";
    public string SavePath { get; private set; } = "";
    public int Seeds { get; private set; }
    public int Peers { get; private set; }
    public double Ratio { get; private set; }
    public double UploadSpeed { get; private set; }
    public static string TorrentRowId(string infoHash) => "bt:" + infoHash;
    public bool IsComplete => Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) || (IsTorrent && Status == "Seeding");
    public bool CanResume => Status is "Paused" or "Failed" or "Canceled" || (IsTorrent && Status == "Completed");
    public bool CanPause => Status is "Downloading" or "Queued" or "Connecting" or "Scheduled" || (IsTorrent && Status is "Checking" or "Seeding");
    /// <summary>Doing work that should block 'quit' prompts and 'when downloads finish'. Seeding does not count as busy.</summary>
    public bool IsBusy => Status is "Downloading" or "Queued" or "Connecting" or "Scheduled" or "Checking";
    public string StatusText => Loc.Status(Status);
    public string TorrentSummary => Loc.T("row.torrent_summary", Seeds, Peers, Ratio, Loc.Speed(Speed), Loc.Speed(UploadSpeed));
    public string DisplayUrl => IsTorrent ? Loc.T("row.torrent_id", TorrentId) : Url.Length == 0 ? Loc.T("row.download_url") : Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : Loc.T("row.download_url");
    public string Percent => TotalBytes > 0 ? $"{Progress:0}%" : IsComplete ? "100%" : "—";
    public string Size => TotalBytes > 0 ? FormatBytes(TotalBytes) : IsComplete ? FormatBytes(DownloadedBytes) : Loc.T("row.unknown");
    public string Downloaded => FormatBytes(DownloadedBytes);
    public string SpeedText => Status == "Downloading" && Speed > 0 ? Loc.Speed(Speed) : IsTorrent && Status == "Seeding" && UploadSpeed > 0 ? "↑ " + Loc.Speed(UploadSpeed) : "—";
    public string Remaining => IsComplete ? Loc.T("row.done") : IsTorrent && Status == "Checking" ? Loc.Status("Checking") : RemainingTime is { } time && Status == "Downloading" ? FormatTime(time) : "—";
    public string ResumeSupport => ResumeSupported switch { true => Loc.T("row.resume_supported"), false => Loc.T("row.resume_unsupported"), _ => Loc.T("row.resume_unknown") };
    public string Connections => Loc.T("row.connections", ActiveConnectionCount, ConnectionLimit);
    public string Detail
    {
        get
        {
            string attention = !string.IsNullOrWhiteSpace(Error) ? "  ·  " + Loc.T("row.needs_attention") : "";
            if (IsTorrent) return $"{Loc.T("row.detail_sizes", Downloaded, Size)}  ·  {TorrentSummary}" + attention;
            return Loc.T("row.detail_sizes", Downloaded, Size) + (Speed > 0 ? $"  ·  {SpeedText}" : "") + (RemainingTime.HasValue ? "  ·  " + Loc.T("row.detail_remaining", Remaining) : "")
                + (ScheduledAt.HasValue ? "  ·  " + Loc.T("row.detail_scheduled", Loc.DateTime(ScheduledAt.Value)) : "") + attention;
        }
    }
    public Brush StatusBrush => (Brush?)Application.Current?.TryFindResource(IsComplete ? "SuccessBrush" : Status == "Failed" ? "ErrorBrush" : CanResume ? "WarningBrush" : "AccentBrush") ?? Brushes.Gray;
    public DownloadRow(DownloadJob job) { Id = job.Id; Update(job); }
    /// <summary>Returns true only when observable snapshot values change.</summary>
    public bool Update(DownloadJob job)
    {
        if (job.Id != Id) throw new ArgumentException("Snapshot belongs to another download.", nameof(job));
        var speed = double.IsFinite(job.SpeedBytesPerSecond) ? Math.Max(0, job.SpeedBytesPerSecond) : 0;
        var progress = double.IsFinite(job.Progress) ? Math.Clamp(job.Progress, 0, 100) : 0;
        bool changed = Referer != job.Referer || AddedAt != job.AddedAt || Url != job.Url || FileName != job.FileName || FilePath != job.FilePath || Status != job.Status || Error != job.Error || ScheduledAt != job.ScheduledAt || TotalBytes != job.TotalBytes || DownloadedBytes != job.DownloadedBytes || Speed != speed || Progress != progress || RemainingTime != job.RemainingTime || ResumeSupported != job.ResumeSupported || ActiveConnectionCount != job.ActiveConnectionCount || ConnectionLimit != job.ConnectionLimit || !Segments.SequenceEqual(job.Segments);
        if (!changed) return false;
        Url = job.Url; FileName = job.FileName; FilePath = job.FilePath; Status = job.Status; Referer = job.Referer; AddedAt = job.AddedAt;
        TotalBytes = job.TotalBytes; DownloadedBytes = job.DownloadedBytes; Speed = speed; Progress = progress;
        Error = job.Error; ScheduledAt = job.ScheduledAt; RemainingTime = job.RemainingTime; ResumeSupported = job.ResumeSupported;
        ActiveConnectionCount = job.ActiveConnectionCount; ConnectionLimit = job.ConnectionLimit; Segments = job.Segments.ToArray();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        return true;
    }
    public static string FormatTime(TimeSpan time) => Loc.Duration(time);
    public static string FormatBytes(double bytes) => Loc.Size(bytes);
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>The language changed: every computed text of the row is read again.</summary>
    public void RaiseAllChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

public sealed record DownloadSegmentRow(int Number, DownloadSegmentSnapshot Snapshot, bool JobRunning = false)
{
    public string Downloaded => Snapshot.DownloadedBytes > 0 || Snapshot.IsActive ? DownloadRow.FormatBytes(Snapshot.DownloadedBytes) : "";
    public string Info => Snapshot.IsActive ? Loc.T("segment.receiving") : Snapshot.Length > 0 && Snapshot.DownloadedBytes >= Snapshot.Length ? Loc.T("segment.complete") : JobRunning ? Loc.T("segment.connecting") : Loc.T("segment.disconnected");
    public string Range => Snapshot.Length > 0 ? Loc.T("segment.range", Snapshot.Start, Snapshot.Start + Snapshot.Length - 1) : Loc.T("row.unknown");
    public string Bytes => $"{DownloadRow.FormatBytes(Snapshot.DownloadedBytes)} / {DownloadRow.FormatBytes(Snapshot.Length)}";
    public double Progress => Snapshot.Length > 0 ? Math.Clamp(100d * Snapshot.DownloadedBytes / Snapshot.Length, 0, 100) : 0;
    public string State => Snapshot.DownloadedBytes >= Snapshot.Length && Snapshot.Length > 0 ? Loc.T("segment.state_complete") : Snapshot.IsActive ? Loc.T("segment.state_receiving") : Loc.T("segment.state_waiting");
}

