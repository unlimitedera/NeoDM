using System.Globalization;

namespace NDM.Core;

/// <summary>How a video is delivered in pieces. None = an ordinary file.</summary>
public enum StreamKind { None = 0, Hls = 1, Dash = 2 }

/// <summary>Why a manifest or playlist is refused or unusable. The Core reports these codes; the app turns them into localized text.</summary>
public enum StreamError { None = 0, Live, Drm, Unsupported, Malformed, Downgrade }

/// <summary>Error codes stored on a job (<see cref="DownloadJob.ErrorCode"/>) and note codes (<see cref="DownloadJob.StreamNote"/>) for streaming downloads.</summary>
public static class StreamCodes
{
    public const string Live = "stream-live", Drm = "stream-drm", Unsupported = "stream-unsupported", Malformed = "stream-malformed", Downgrade = "stream-downgrade";
    /// <summary>A segment answered 404 or 410 (<see cref="DownloadJob.ErrorSegment"/> holds its number, counted from 1).</summary>
    public const string SegmentMissing = "stream-segment-missing";
    /// <summary>The tool step failed with no usable fallback (reserved; a failed join normally falls back to separate files with the note <see cref="MuxFailed"/>).</summary>
    public const string ToolFailed = "stream-tool-failed";
    /// <summary>Note: picture and sound (or a transport stream wanted as MP4) need the helper tool that is not installed; the pieces were saved without joining.</summary>
    public const string NeedsMuxer = "needs-muxer";
    /// <summary>Note: the helper tool ran but its result was refused (failed, timed out, empty or the wrong length); the pieces were saved without joining.</summary>
    public const string MuxFailed = "mux-failed";
    /// <summary>Phases a running stream job reports in <see cref="DownloadJob.StreamPhase"/>.</summary>
    public const string PhasePieces = "pieces", PhaseJoining = "joining", PhaseMerging = "merging";

    public static string CodeOf(StreamError error) => error switch
    {
        StreamError.Live => Live, StreamError.Drm => Drm, StreamError.Unsupported => Unsupported, StreamError.Malformed => Malformed, StreamError.Downgrade => Downgrade, _ => ""
    };
}

/// <summary>A refusal with a typed reason. Never retried.</summary>
public sealed class StreamException : Exception
{
    public StreamError Error { get; }
    public string Code => StreamCodes.CodeOf(Error);
    /// <summary>Number of the segment the problem belongs to (from 1), when there is one.</summary>
    public int? Segment { get; }
    public StreamException(StreamError error, string message, int? segment = null) : base(message) { Error = error; Segment = segment; }
}

/// <summary>A segment is gone from the server (404 or 410). Never retried.</summary>
public sealed class StreamSegmentMissingException : Exception
{
    public int Segment { get; }
    public int Status { get; }
    public StreamSegmentMissingException(int segment, int status) : base($"Segment {segment} is missing on the server (HTTP {status}).") { Segment = segment; Status = status; }
}

/// <summary>Result of a parser: exactly one of Value and Error is set. Parsers never throw for bad input.</summary>
public sealed record StreamParseResult<T>(T? Value, StreamException? Error) where T : class
{
    public bool Ok => Value is not null;
    public T Require() => Value ?? throw Error ?? new StreamException(StreamError.Malformed, "Empty result.");
}

public enum StreamContainer { Ts, Fmp4, WebM, Mp4Single, Aac, Mp3 }

public readonly record struct ByteRange(long Offset, long Length)
{
    public long Last => Offset + Length - 1;
}

/// <summary>AES-128-CBC delivery encryption of one segment: where the 16 byte key is and the 16 byte IV (explicit or derived from the segment number).</summary>
public sealed record StreamEncryption(Uri KeyUri, byte[] Iv);

/// <summary>One piece. Index counts from 0 in the track; the initialization section is kept apart in <see cref="TrackPlan.Init"/>.</summary>
public sealed record StreamSegment(int Index, Uri Uri, ByteRange? Range, StreamEncryption? Encryption, double Duration);

/// <summary>An ordered list of pieces that make one elementary track (the picture, or the sound, or both muxed together).</summary>
public sealed class TrackPlan
{
    public IReadOnlyList<StreamSegment> Segments { get; init; } = Array.Empty<StreamSegment>();
    public StreamSegment? Init { get; init; }
    public StreamContainer Container { get; init; }
    public string? Codecs { get; init; }
    /// <summary>Bits per second the manifest announces (0 = unknown).</summary>
    public long Bandwidth { get; init; }
    public double Duration { get; init; }
    public bool IsAudio { get; init; }
    /// <summary>Set for a track that is one file (DASH SegmentBase or a bare BaseURL): a plain ranged download of this address.</summary>
    public Uri? SingleFile { get; init; }
    /// <summary>Pieces including the initialization section.</summary>
    public int ItemCount => Segments.Count + (Init is null ? 0 : 1);
}

public sealed class StreamPlan
{
    public StreamKind Kind { get; init; }
    public double Duration { get; init; }
    public TrackPlan? Video { get; init; }
    public TrackPlan? Audio { get; init; }
    public IEnumerable<TrackPlan> Tracks { get { if (Video is not null) yield return Video; if (Audio is not null) yield return Audio; } }
    public int TotalItems => Tracks.Sum(t => t.ItemCount);
    /// <summary>Bandwidth x duration; 0 when the manifest announces no bandwidth.</summary>
    public long EstimatedBytes
    {
        get
        {
            double bits = 0;
            foreach (var t in Tracks) bits += (double)t.Bandwidth * (t.Duration > 0 ? t.Duration : Duration);
            var bytes = bits / 8;
            return bytes <= 0 || double.IsNaN(bytes) ? 0 : bytes >= long.MaxValue / 2 ? long.MaxValue / 2 : (long)bytes;
        }
    }
}

public sealed record VideoVariant(string Id, long Bandwidth, int Width, int Height, double FrameRate, string? Codecs, string? AudioGroupId)
{
    /// <summary>"1280x720" or "" when the manifest names no size.</summary>
    public string Resolution => Width > 0 && Height > 0 ? Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture) : "";
    /// <summary>True when the picture variant already carries its sound (no separate audio track is needed).</summary>
    public bool HasOwnAudio { get; init; }
}

public sealed record AudioTrack(string Id, string? Language, string? Name, long Bandwidth, string? Codecs, string? GroupId = null, bool IsDefault = false, int Channels = 0);

/// <summary>What a manifest offers, in neutral terms, for a quality picker.</summary>
public sealed class StreamManifest
{
    public StreamKind Kind { get; init; }
    /// <summary>The address the manifest was read from (after redirects).</summary>
    public string Url { get; init; } = "";
    public string? Title { get; init; }
    /// <summary>Zero when unknown.</summary>
    public TimeSpan Duration { get; init; }
    /// <summary>Container of the pieces when known (HLS: read from the best quality's playlist); null when not known. A transport stream can be rewrapped as MP4 by the helper tool.</summary>
    public StreamContainer? PieceContainer { get; init; }
    public IReadOnlyList<VideoVariant> Variants { get; init; } = Array.Empty<VideoVariant>();
    public IReadOnlyList<AudioTrack> AudioTracks { get; init; } = Array.Empty<AudioTrack>();

    /// <summary>The variant picked when none is named: the highest bandwidth.</summary>
    public VideoVariant? BestVariant => Variants.OrderByDescending(v => v.Bandwidth).ThenByDescending(v => v.Height).FirstOrDefault();

    /// <summary>Audio tracks that go with a variant (its audio group for HLS; every track for DASH).</summary>
    public IEnumerable<AudioTrack> AudioFor(VideoVariant? variant) =>
        variant?.AudioGroupId is { } group ? AudioTracks.Where(a => a.GroupId == group) : variant?.HasOwnAudio == true ? Enumerable.Empty<AudioTrack>() : AudioTracks;

    /// <summary>The audio picked when none is named: the default one of the group, else the first; null when the variant carries its own sound.</summary>
    public AudioTrack? DefaultAudio(VideoVariant? variant)
    {
        if (variant?.HasOwnAudio == true) return null;
        var list = AudioFor(variant).ToList();
        return list.FirstOrDefault(a => a.IsDefault) ?? list.FirstOrDefault();
    }

    /// <summary>Rough size from bandwidth x duration for a chosen pair; 0 when unknown.</summary>
    public long EstimateBytes(VideoVariant? variant, AudioTrack? audio)
    {
        double bits = (variant?.Bandwidth ?? 0) + (audio?.Bandwidth ?? 0);
        var bytes = bits * Duration.TotalSeconds / 8;
        return bytes <= 0 || double.IsNaN(bytes) ? 0 : (long)Math.Min(bytes, long.MaxValue / 2);
    }
}

/// <summary>Picks what to download from a manifest. "none" as audio id means: no separate sound track.</summary>
public static class StreamSelection
{
    public const string NoAudio = "none";

    public static (VideoVariant? Variant, AudioTrack? Audio) Choose(StreamManifest manifest, string? variantId, string? audioId)
    {
        VideoVariant? variant = null;
        if (!string.IsNullOrEmpty(variantId))
        {
            variant = manifest.Variants.FirstOrDefault(v => v.Id == variantId) ?? throw new StreamException(StreamError.Unsupported, "The chosen quality is not in this stream.");
        }
        else variant = manifest.BestVariant;
        AudioTrack? audio = null;
        if (audioId == NoAudio) audio = null;
        else if (!string.IsNullOrEmpty(audioId)) audio = manifest.AudioTracks.FirstOrDefault(a => a.Id == audioId) ?? throw new StreamException(StreamError.Unsupported, "The chosen sound track is not in this stream.");
        else audio = manifest.DefaultAudio(variant);
        if (variant is null && audio is null) throw new StreamException(StreamError.Unsupported, "The stream has no picture or sound track to download.");
        return (variant, audio);
    }
}

/// <summary>Recognises a streaming manifest from its first bytes (a link ending in .m3u8 or .mpd is not trusted; the content decides).</summary>
public static class StreamSniffer
{
    /// <summary>The message of the <see cref="StreamError.Unsupported"/> refusal for an address whose content is not a playlist or manifest (the app shows its own text for it).</summary>
    public const string NotAStreamMessage = "The address does not give a streaming playlist or manifest.";

    public static StreamKind Detect(ReadOnlySpan<byte> head)
    {
        int i = 0;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) i = 3;
        while (i < head.Length && head[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        var text = System.Text.Encoding.Latin1.GetString(head[i..Math.Min(head.Length, i + 8192)]);
        if (text.StartsWith("#EXTM3U", StringComparison.Ordinal))
            return text.Contains("#EXT-X-", StringComparison.Ordinal) ? StreamKind.Hls : StreamKind.None; // a bare #EXTM3U/#EXTINF list is a music playlist, not a stream
        if (text.StartsWith('<'))
        {
            var lower = text.ToLowerInvariant();
            int mpd = lower.IndexOf("<mpd", StringComparison.Ordinal);
            if (mpd >= 0 && lower.Contains("urn:mpeg:dash", StringComparison.Ordinal)) return StreamKind.Dash;
        }
        return StreamKind.None;
    }
}
