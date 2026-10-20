namespace NDM.Core;

/// <summary>TargetDirectory (optional): the exact folder to save into, chosen by the user; null = the engine's own folder (category rules). It is created if
/// missing, must be writable, and is kept for the job's lifetime (also across restarts and name refinement).
/// ExpectedSize / ExpectedMime (optional): what the browser reported for the download; the engine refuses a web page sent in its place.
/// FileName null or AutoName true = NeoDM may still refine the name from the server (Content-Disposition, redirects, Content-Type, file signature); an explicit name is kept as is.</summary>
public record DownloadRequest(string Url, string? FileName = null, string? Referer = null, string? Cookie = null, bool AutoName = false, string? TargetDirectory = null, long? ExpectedSize = null, string? ExpectedMime = null);

/// <summary>A detached snapshot. Poll Jobs again for fresh progress.</summary>
public sealed class DownloadJob
{
    public string Id { get; internal set; } = "";
    public string Url { get; internal set; } = "";
    public string FileName { get; internal set; } = "";
    public string FilePath { get; internal set; } = "";
    public string? Referer { get; internal set; }
    /// <summary>When the job was added; null for jobs saved before this was recorded.</summary>
    public DateTimeOffset? AddedAt { get; internal set; }
    public string Status { get; internal set; } = "Queued";
    public long TotalBytes { get; internal set; }
    public long DownloadedBytes { get; internal set; }
    public double SpeedBytesPerSecond { get; internal set; }
    public double Progress => Status == "Completed" ? 100 : TotalBytes <= 0 ? 0 : Math.Clamp(100.0 * DownloadedBytes / TotalBytes, 0, 100);
    public string? Error { get; internal set; }
    /// <summary>Category of a failure (for example <see cref="ContentGuard.WrongContent"/>); null for ordinary errors.</summary>
    public string? ErrorCode { get; internal set; }
    /// <summary>For <c>wrong-content</c>: size of what the site sent and the size that was expected, when known.</summary>
    public long? ErrorReceivedBytes { get; internal set; }
    public long? ErrorExpectedBytes { get; internal set; }
    public DateTimeOffset? ScheduledAt { get; internal set; }
    public TimeSpan? RemainingTime { get; internal set; }
    public bool? ResumeSupported { get; internal set; }
    public int ActiveConnectionCount { get; internal set; }
    public int ConnectionLimit { get; internal set; }
    /// <summary>The per-download speed limit in bytes per second; null = unlimited.</summary>
    public long? SpeedLimitBytesPerSecond { get; internal set; }
    public IReadOnlyList<DownloadSegmentSnapshot> Segments { get; internal set; } = Array.Empty<DownloadSegmentSnapshot>();
    internal DownloadJob Snapshot() => (DownloadJob)MemberwiseClone();
}

/// <summary>Detached byte-range telemetry; Length includes bytes already downloaded.</summary>
public sealed record DownloadSegmentSnapshot(long Start, long Length, long DownloadedBytes, bool IsActive);

internal static class SnapshotTime
{
    public static TimeSpan? Remaining(DownloadJob job)
    {
        if (job.Status == "Completed") return TimeSpan.Zero;
        if (job.Status != "Downloading" || job.TotalBytes <= 0 || job.SpeedBytesPerSecond <= 0) return null;
        var seconds = Math.Max(0, job.TotalBytes - job.DownloadedBytes) / job.SpeedBytesPerSecond;
        return double.IsFinite(seconds) ? TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds - 1)) : null;
    }
}

public sealed class DownloadOptions
{
    public int MaxConcurrentDownloads { get; init; } = 3;
    public int MaxSegments { get; init; } = 4;
    public bool EnableDynamicSegmentation { get; init; } = true;
    public long MinimumSegmentSize { get; init; } = 256 * 1024;
    public int RetryCount { get; init; } = 3;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Optional proxy for all HTTP(S) transfers; null or disabled means a direct connection.</summary>
    public ProxyOptions? Proxy { get; init; }
    /// <summary>Write the Zone.Identifier (Mark of the Web) stream on every finished download. Best effort.</summary>
    public bool MarkOfTheWeb { get; init; } = true;
    /// <summary>Free bytes of the drive that holds a path; null = unknown (skip the check). Default: DriveInfo. A seam for tests.</summary>
    public Func<string, long?>? FreeSpace { get; init; }
    /// <summary>For a download without a declared length: how many bytes are written between two looks at the free space.</summary>
    public long SpaceCheckIntervalBytes { get; init; } = 16L * 1024 * 1024;
    internal void Validate()
    {
        if (MaxConcurrentDownloads is < 1 or > 16 || MaxSegments is < 1 or > 32 ||
            MinimumSegmentSize < 65536 || RetryCount is < 0 or > 8 ||
            SpaceCheckIntervalBytes < 4096 ||
            RequestTimeout < TimeSpan.FromMilliseconds(100) || RequestTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(DownloadOptions));
        Proxy?.Validate();
    }
}

public static class DownloadFileNames
{
    public static string Sanitize(string? value)
    {
        value ??= "download.bin";
        value = ReservedNames.StripInvisible(value);
        var chars = value.Select(c => c < 32 || c == 127 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray();
        var name = new string(chars).Trim().Trim('.');
        if (name.Length == 0) name = "download.bin";
        if (ReservedNames.IsReserved(name)) name = "_" + name;
        if (name.Length > 180)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length > 20) extension = "";
            name = name[..(180 - extension.Length)] + extension;
        }
        return name.TrimEnd(' ', '.');
    }
}

public enum FileDeleteOutcome { NotRequested, Deleted, Missing, Failed }

/// <summary>Result of removing a job; <c>Problem</c> explains a failed file deletion (the job is removed regardless).</summary>
public sealed record RemoveResult(bool JobRemoved, FileDeleteOutcome File, string? Problem);
