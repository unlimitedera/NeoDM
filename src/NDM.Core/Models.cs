namespace NDM.Core;

/// <summary>TargetDirectory (optional): the exact folder to save into, chosen by the user; null = the engine's own folder (category rules). It is created if
/// missing, must be writable, and is kept for the job's lifetime (also across restarts and name refinement).
/// ExpectedSize / ExpectedMime (optional): what the browser reported for the download; the engine refuses a web page sent in its place.
/// UserAgent / Accept / AcceptLanguage (optional, 1.5.7): the request identity the browser itself used for this download (printable ASCII, 512 / 512 / 128 characters at most); null = the engine's own defaults.
/// StreamKind / StreamVariantId / StreamAudioId / PreferMp4 (optional, 1.6.0): a streaming download (HLS or DASH): the manifest address is Url; the ids come from <see cref="StreamManifest"/> (null = best quality and its default sound); PreferMp4 asks for an MP4 file when the pieces are transport streams (needs the helper tool).
/// FileName null or AutoName true = NeoDM may still refine the name from the server (Content-Disposition, redirects, Content-Type, file signature); an explicit name is kept as is.</summary>
public record DownloadRequest(string Url, string? FileName = null, string? Referer = null, string? Cookie = null, bool AutoName = false, string? TargetDirectory = null, long? ExpectedSize = null, string? ExpectedMime = null, string? UserAgent = null, string? Accept = null, string? AcceptLanguage = null, StreamKind StreamKind = StreamKind.None, string? StreamVariantId = null, string? StreamAudioId = null, bool PreferMp4 = false);

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
    /// <summary>For <c>wrong-content</c>: the site answered with a redirect to one of its own web pages instead of the file (often a page that needs the browser session).</summary>
    public bool ErrorRedirected { get; internal set; }
    /// <summary>For <c>http-status</c>: the HTTP status the server answered with.</summary>
    public int? ErrorStatus { get; internal set; }
    /// <summary>The attempt trail of the last run, for example "Attempt 1: 403, Attempt 2 (without Range): 200"; null when the first request was accepted.</summary>
    public string? Attempts { get; internal set; }
    public DateTimeOffset? ScheduledAt { get; internal set; }
    public TimeSpan? RemainingTime { get; internal set; }
    public bool? ResumeSupported { get; internal set; }
    public int ActiveConnectionCount { get; internal set; }
    public int ConnectionLimit { get; internal set; }
    /// <summary>The per-download speed limit in bytes per second; null = unlimited.</summary>
    public long? SpeedLimitBytesPerSecond { get; internal set; }
    public IReadOnlyList<DownloadSegmentSnapshot> Segments { get; internal set; } = Array.Empty<DownloadSegmentSnapshot>();
    /// <summary>Streaming downloads (1.6.0): pieces finished and pieces in all (0 for ordinary files).</summary>
    public int SegmentsDone { get; internal set; }
    public int SegmentsTotal { get; internal set; }
    /// <summary>Streaming downloads: HLS or DASH; None for ordinary files.</summary>
    public StreamKind Stream { get; internal set; }
    /// <summary>Optional note on a finished or running stream, for example <see cref="StreamCodes.NeedsMuxer"/> or <see cref="StreamCodes.MuxFailed"/>.</summary>
    public string? StreamNote { get; internal set; }
    /// <summary>For <c>wrong-content</c>: what the site sent is a streaming playlist or manifest, so the streaming path can be offered. Kind in <see cref="DetectedStream"/>.</summary>
    public bool StreamDetected { get; internal set; }
    public StreamKind DetectedStream { get; internal set; }
    /// <summary>For <c>stream-segment-missing</c>: the piece (counted from 1; 0 = the initialization section).</summary>
    public int? ErrorSegment { get; internal set; }
    /// <summary>Other files saved by the same job (for example the sound track when it could not be joined to the picture).</summary>
    public IReadOnlyList<string> AdditionalFiles { get; internal set; } = Array.Empty<string>();
    /// <summary>Streaming downloads: what the job is doing now, one of <see cref="StreamCodes.PhasePieces"/>, <see cref="StreamCodes.PhaseJoining"/> (writing the pieces into one file), <see cref="StreamCodes.PhaseMerging"/> (the helper tool joins picture and sound). Null for files and when not running. Not saved.</summary>
    public string? StreamPhase { get; internal set; }
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
    /// <summary>Which User-Agent to present (Browser, NeoDM or Custom). A request from the extension carries the browser's own identity, used in Browser mode.</summary>
    public IdentityMode Identity { get; init; } = IdentityMode.Browser;
    /// <summary>The User-Agent text of <see cref="IdentityMode.Custom"/>.</summary>
    public string? CustomUserAgent { get; init; }
    /// <summary>Accept-Language for requests that carry none; null = derived from the Windows display language.</summary>
    public string? AcceptLanguage { get; init; }
    /// <summary>Pause between compatibility attempts and the cap on a server's Retry-After; null = 250 ms and 10 s. A seam for tests.</summary>
    public TimeSpan? LadderPause { get; init; }
    public TimeSpan? MaxRetryAfter { get; init; }
    /// <summary>Write the Zone.Identifier (Mark of the Web) stream on every finished download. Best effort.</summary>
    public bool MarkOfTheWeb { get; init; } = true;
    /// <summary>Free bytes of the drive that holds a path; null = unknown (skip the check). Default: DriveInfo. A seam for tests.</summary>
    public Func<string, long?>? FreeSpace { get; init; }
    /// <summary>For a download without a declared length: how many bytes are written between two looks at the free space.</summary>
    public long SpaceCheckIntervalBytes { get; init; } = 16L * 1024 * 1024;
    /// <summary>Streaming downloads: parallel piece downloads per job (1 to 16).</summary>
    public int StreamConnections { get; init; } = 8;
    /// <summary>Streaming downloads: tries per piece and the first pause between tries (doubles each time).</summary>
    public int StreamSegmentTries { get; init; } = 3;
    public TimeSpan StreamRetryDelay { get; init; } = TimeSpan.FromMilliseconds(300);
    /// <summary>Longest time the helper tool may run for one join.</summary>
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>The helper tool (ffmpeg) manager; null = the default one in the state folder, PATH not used.</summary>
    public ToolManager? Tools { get; init; }
    internal void Validate()
    {
        if (MaxConcurrentDownloads is < 1 or > 16 || MaxSegments is < 1 or > 32 ||
            MinimumSegmentSize < 65536 || RetryCount is < 0 or > 8 ||
            SpaceCheckIntervalBytes < 4096 || StreamConnections is < 1 or > 16 || StreamSegmentTries is < 1 or > 8 || StreamRetryDelay < TimeSpan.Zero || StreamRetryDelay > TimeSpan.FromMinutes(1) || ToolTimeout < TimeSpan.FromSeconds(1) ||
            RequestTimeout < TimeSpan.FromMilliseconds(100) || RequestTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(DownloadOptions));
        if (Identity == IdentityMode.Custom && !RequestIdentity.IsHeaderValue(CustomUserAgent, RequestIdentity.MaxUserAgent)) throw new ArgumentOutOfRangeException(nameof(CustomUserAgent));
        if (AcceptLanguage != null && !RequestIdentity.IsHeaderValue(AcceptLanguage, RequestIdentity.MaxAcceptLanguage)) throw new ArgumentOutOfRangeException(nameof(AcceptLanguage));
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
