using System.Net.Http;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>The words for streaming downloads: plain-language text for every refusal and error code, and the labels of the quality and sound pickers.
/// The Core reports codes and English messages; everything the user reads comes from here, in the active language.</summary>
public static class StreamMessages
{
    /// <summary>The sentence for a typed refusal of a playlist or manifest.</summary>
    public static string ForError(StreamError error) => Loc.T(error switch
    {
        StreamError.Live => "stream.error.live",
        StreamError.Drm => "stream.error.drm",
        StreamError.Unsupported => "stream.error.unsupported",
        StreamError.Malformed => "stream.error.malformed",
        StreamError.Downgrade => "stream.error.downgrade",
        _ => "error.generic"
    });

    /// <summary>The sentence for a failed request status while reading or fetching a stream. There is no browser fallback for streams, so these never say so.</summary>
    public static string ForStatus(int status) => status switch
    {
        401 or 403 => Loc.T("stream.error.http_refused", status),
        404 or 410 => Loc.T("stream.error.http_missing", status),
        _ => Loc.T("stream.error.http_status", status)
    };

    /// <summary>What went wrong while reading a playlist or manifest (the stream dialog's failure text).</summary>
    public static string Describe(Exception ex)
    {
        switch (ex)
        {
            case StreamException { Error: StreamError.Unsupported } s when s.Message == StreamSniffer.NotAStreamMessage: return Loc.T("stream.error.not_stream");
            case StreamException s: return ForError(s.Error);
            case HttpRequestException { StatusCode: { } status }: return ForStatus((int)status);
            case UserMessageException u: return u.Message;
            default: return ErrorMessages.Describe(ex);
        }
    }

    /// <summary>True for the error codes a stream job stores (<c>stream-live</c> and so on).</summary>
    public static bool IsStreamCode(string? code) => code is StreamCodes.Live or StreamCodes.Drm or StreamCodes.Unsupported or StreamCodes.Malformed or StreamCodes.Downgrade or StreamCodes.SegmentMissing or StreamCodes.ToolFailed;

    /// <summary>The sentence for a failed stream job by its stored code; null when the code is not a stream code.</summary>
    public static string? ForJob(string? code, int? segment, int? status) => code switch
    {
        StreamCodes.Live => ForError(StreamError.Live),
        StreamCodes.Drm => ForError(StreamError.Drm),
        StreamCodes.Unsupported => ForError(StreamError.Unsupported),
        StreamCodes.Malformed => segment is { } m && m > 0 ? Loc.T("stream.error.malformed_piece", m) : ForError(StreamError.Malformed),
        StreamCodes.Downgrade => ForError(StreamError.Downgrade),
        StreamCodes.SegmentMissing => Loc.T("stream.error.segment_missing", segment ?? 0, status ?? 404),
        StreamCodes.ToolFailed => Loc.T("stream.error.tool_failed"),
        _ => null
    };

    /// <summary>The line about how the finished files were saved; null when everything is as asked.</summary>
    public static string? NoteText(string? note) => note switch
    {
        StreamCodes.NeedsMuxer => Loc.T("stream.note.needs_tool"),
        StreamCodes.MuxFailed => Loc.T("stream.note.mux_failed"),
        _ => null
    };

    /// <summary>The status line of a running stream job in its phase; null when the ordinary status text applies.</summary>
    public static string? PhaseText(string? phase) => phase switch
    {
        StreamCodes.PhaseJoining => Loc.T("stream.phase.joining"),
        StreamCodes.PhaseMerging => Loc.T("stream.phase.merging"),
        _ => null
    };

    public static string KindName(StreamKind kind) => kind switch { StreamKind.Hls => "HLS", StreamKind.Dash => "DASH", _ => "" };

    /// <summary>"1.2 Mbit/s" or "640 kbit/s"; empty when unknown.</summary>
    public static string Bitrate(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "";
        return bitsPerSecond >= 1_000_000 ? Loc.T("stream.quality.mbit", Loc.Number(bitsPerSecond / 1_000_000d, 1)) : Loc.T("stream.quality.kbit", Loc.Number(Math.Max(1, bitsPerSecond / 1000d), 0));
    }

    /// <summary>"1080p", "1280x720", "Audio only" or "Single stream".</summary>
    public static string Resolution(VideoVariant variant)
    {
        if (variant.Height > 0)
        {
            var text = variant.Height + "p";
            if (variant.FrameRate >= 49) text += Loc.Number(Math.Round(variant.FrameRate), 0);
            return text;
        }
        if (variant.Resolution.Length > 0) return variant.Resolution;
        if (variant.Codecs is { Length: > 0 } codecs && !HasVideoCodec(codecs)) return Loc.T("stream.quality.audio_only");
        return Loc.T("stream.quality.single");
    }

    private static bool HasVideoCodec(string codecs) => new[] { "avc", "hvc", "hev", "vp0", "vp8", "vp9", "av01", "dvh", "mp4v" }.Any(c => codecs.Contains(c, StringComparison.OrdinalIgnoreCase));

    /// <summary>The picker line of one quality: "1080p, 5.0 Mbit/s, about 1.2 GB".</summary>
    public static string QualityLabel(StreamManifest manifest, VideoVariant variant, AudioTrack? audio)
    {
        var parts = new List<string> { Resolution(variant) };
        var rate = Bitrate(variant.Bandwidth); if (rate.Length > 0) parts.Add(Loc.Ltr(rate));
        var bytes = manifest.EstimateBytes(variant, audio);
        if (bytes > 0) parts.Add(Loc.T("stream.quality.about_size", Loc.Size(bytes)));
        return string.Join(", ", parts);
    }

    /// <summary>The picker line of a sound track: its name and language.</summary>
    public static string AudioLabel(AudioTrack track, int index)
    {
        var name = string.IsNullOrWhiteSpace(track.Name) ? null : track.Name.Trim();
        var language = string.IsNullOrWhiteSpace(track.Language) ? null : track.Language.Trim();
        if (name is not null && language is not null && !name.Contains(language, StringComparison.OrdinalIgnoreCase)) return name + " (" + language + ")";
        return name ?? language ?? Loc.T("stream.audio.track", index + 1);
    }

    /// <summary>The estimate line: "About 1.2 GB, 1 h 42 min"; empty when neither is known.</summary>
    public static string EstimateLine(long bytes, TimeSpan duration)
    {
        var parts = new List<string>();
        if (bytes > 0) parts.Add(Loc.T("stream.estimate.size", Loc.Size(bytes)));
        if (duration > TimeSpan.Zero) parts.Add(Loc.T("stream.estimate.duration", Loc.Duration(duration)));
        return string.Join("  \u00b7  ", parts);
    }

    /// <summary>The first address a stream download is asked for: a path that ends in .m3u8 or .mpd (the query and fragment are ignored, case does not matter). The content decides later.</summary>
    public static StreamKind KindFromAddress(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return StreamKind.None;
        var path = uri.AbsolutePath;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) return StreamKind.Hls;
        if (path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)) return StreamKind.Dash;
        return StreamKind.None;
    }
}

/// <summary>The helper tool as the windows see it. The real one wraps the engine's ToolManager; tests use a fake that never downloads anything.</summary>
public interface IVideoTool
{
    bool IsInstalled { get; }
    /// <summary>Installed, or allowed from PATH.</summary>
    bool IsAvailable { get; }
    string? Version { get; }
    /// <summary>Size of the installed program in bytes; 0 when not installed.</summary>
    long InstalledBytes { get; }
    Task InstallAsync(IProgress<(long Done, long Total)> progress, CancellationToken cancellation);
    void Uninstall();
    bool UseFromPath { get; set; }
}

public sealed class ToolManagerAdapter : IVideoTool
{
    private readonly ToolManager tools;
    public ToolManagerAdapter(ToolManager tools) { this.tools = tools; }
    public bool IsInstalled => tools.IsInstalled;
    public bool IsAvailable => tools.IsAvailable;
    public string? Version => tools.GetVersion();
    public long InstalledBytes { get { try { return tools.IsInstalled ? new System.IO.FileInfo(tools.FfmpegPath).Length : 0; } catch (System.IO.IOException) { return 0; } } }
    public Task InstallAsync(IProgress<(long Done, long Total)> progress, CancellationToken cancellation) => tools.InstallAsync(progress, cancellation);
    public void Uninstall() => tools.Uninstall();
    public bool UseFromPath { get => tools.UseFromPath; set => tools.UseFromPath = value; }
}

/// <summary>Where the video tool is described and its licence is read.</summary>
public static class VideoToolInfo
{
    public const string LicenceUrl = "https://ffmpeg.org/legal.html";
    /// <summary>Approximate download size shown to the user, in bytes (the pinned archive).</summary>
    public static long DownloadBytes => ToolManager.FfmpegPin.ArchiveSize;

    /// <summary>The plain-words text for a failed install or removal.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        ToolException { Code: ToolException.Host } => Loc.T("tool.error.host"),
        ToolException { Code: ToolException.HashMismatch } => Loc.T("tool.error.hash"),
        ToolException { Code: ToolException.Space } => Loc.T("tool.error.space"),
        ToolException { Code: ToolException.Archive } => Loc.T("tool.error.archive"),
        ToolException { Code: ToolException.Download } => Loc.T("tool.error.download"),
        ToolException => Loc.T("tool.error.other"),
        _ => ErrorMessages.Describe(ex)
    };
}

/// <summary>Runs one install of the video tool for a window: progress, cancel, and a result the window can show. At most one run at a time.</summary>
public sealed class ToolInstallRun
{
    private readonly IVideoTool tool;
    private CancellationTokenSource? cancel;
    public ToolInstallRun(IVideoTool tool) { this.tool = tool; }
    public bool Running { get; private set; }
    /// <summary>Raised on the UI thread with the fraction done (0 to 1), or null when the total is not known.</summary>
    public Action<double?>? Progress { get; set; }
    public enum Outcome { Installed, Canceled, Failed }
    public string? Message { get; private set; }
    public void Cancel() { try { cancel?.Cancel(); } catch (ObjectDisposedException) { } }

    public async Task<Outcome> RunAsync()
    {
        if (Running) return Outcome.Failed;
        Running = true; Message = null;
        cancel = new CancellationTokenSource();
        var context = SynchronizationContext.Current;
        var progress = new SyncProgress(p => Progress?.Invoke(p.Total > 0 ? Math.Clamp((double)p.Done / p.Total, 0, 1) : null), context);
        try
        {
            await tool.InstallAsync(progress, cancel.Token).ConfigureAwait(true);
            return tool.IsInstalled ? Outcome.Installed : Fail(Loc.T("tool.error.other"));
        }
        catch (OperationCanceledException) { return Outcome.Canceled; }
        catch (Exception ex) when (ex is ToolException or System.IO.IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or TimeoutException) { return Fail(VideoToolInfo.Describe(ex)); }
        finally { Running = false; cancel.Dispose(); cancel = null; }
    }

    private Outcome Fail(string message) { Message = message; return Outcome.Failed; }

    private sealed class SyncProgress : IProgress<(long Done, long Total)>
    {
        private readonly Action<(long Done, long Total)> report; private readonly SynchronizationContext? context;
        public SyncProgress(Action<(long Done, long Total)> report, SynchronizationContext? context) { this.report = report; this.context = context; }
        public void Report((long Done, long Total) value) { if (context is null) report(value); else context.Post(_ => report(value), null); }
    }
}
