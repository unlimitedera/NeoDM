using System.Globalization;
using System.Text;

namespace NDM.Core;

/// <summary>A master playlist entry (EXT-X-STREAM-INF).</summary>
public sealed record HlsVariantInfo(Uri Uri, long Bandwidth, long AverageBandwidth, int Width, int Height, double FrameRate, string? Codecs, string? AudioGroup);

/// <summary>An EXT-X-MEDIA entry of type AUDIO.</summary>
public sealed record HlsRendition(string GroupId, string? Name, string? Language, bool IsDefault, bool AutoSelect, Uri? Uri, int Channels);

/// <summary>A parsed playlist: either a master (variants and renditions) or a media playlist (a track).</summary>
public sealed class HlsDocument
{
    public bool IsMaster { get; init; }
    public IReadOnlyList<HlsVariantInfo> Variants { get; init; } = Array.Empty<HlsVariantInfo>();
    public IReadOnlyList<HlsRendition> Audio { get; init; } = Array.Empty<HlsRendition>();
    /// <summary>For a media playlist: its track (bandwidth unknown, zero).</summary>
    public TrackPlan? Media { get; init; }
    public bool IsLive { get; init; }
    public double TargetDuration { get; init; }
}

/// <summary>HTTP Live Streaming playlists (RFC 8216). Input is untrusted: bad input gives a typed error, never another exception.</summary>
public static class HlsParser
{
    public const int MaxBytes = 32 * 1024 * 1024, MaxLineChars = 65536, MaxSegments = 200000, MaxVariants = 2000;

    public static bool LooksLikeHls(string text) => StreamSniffer.Detect(Encoding.UTF8.GetBytes(text.Length > 9000 ? text[..9000] : text)) == StreamKind.Hls;

    /// <summary>Parses a master or media playlist. A media playlist that is not finished (no EXT-X-ENDLIST and not a VOD list) is a typed <see cref="StreamError.Live"/> unless allowLive is set.</summary>
    public static StreamParseResult<HlsDocument> Parse(string? text, Uri baseUri, bool allowLive = false)
    {
        try { return new(ParseCore(text, baseUri, allowLive), null); }
        catch (StreamException ex) { return new(null, ex); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or InvalidOperationException or UriFormatException or IndexOutOfRangeException or KeyNotFoundException or OutOfMemoryException)
        { return new(null, new StreamException(StreamError.Malformed, "The playlist could not be read.")); }
    }

    /// <summary>Neutral view of a parsed document for the picker. A media playlist is shown as a single variant.</summary>
    public static StreamManifest ToManifest(HlsDocument doc, string url, string? title = null, TimeSpan? duration = null, StreamContainer? container = null)
    {
        if (!doc.IsMaster)
        {
            var track = doc.Media!;
            return new StreamManifest
            {
                Kind = StreamKind.Hls, Url = url, Title = title, Duration = Seconds(track.Duration), PieceContainer = track.Container,
                Variants = new[] { new VideoVariant("v0", 0, 0, 0, 0, track.Codecs, null) { HasOwnAudio = true } },
                AudioTracks = Array.Empty<AudioTrack>()
            };
        }
        var audio = new List<AudioTrack>();
        foreach (var r in doc.Audio.Where(r => r.Uri is not null))
            audio.Add(new AudioTrack("a" + audio.Count.ToString(CultureInfo.InvariantCulture), r.Language, r.Name, 0, null, r.GroupId, r.IsDefault, r.Channels));
        var variants = new List<VideoVariant>();
        foreach (var v in doc.Variants)
        {
            var hasGroup = v.AudioGroup is not null && audio.Any(a => a.GroupId == v.AudioGroup);
            variants.Add(new VideoVariant("v" + variants.Count.ToString(CultureInfo.InvariantCulture), v.AverageBandwidth > 0 ? v.AverageBandwidth : v.Bandwidth, v.Width, v.Height, v.FrameRate, v.Codecs, hasGroup ? v.AudioGroup : null) { HasOwnAudio = !hasGroup });
        }
        return new StreamManifest { Kind = StreamKind.Hls, Url = url, Title = title, Duration = duration ?? TimeSpan.Zero, PieceContainer = container, Variants = variants, AudioTracks = audio };
    }

    private static TimeSpan Seconds(double s) => double.IsFinite(s) && s > 0 && s < 1e9 ? TimeSpan.FromSeconds(s) : TimeSpan.Zero;

    /// <summary>Address of the media playlist behind a variant id of <see cref="ToManifest"/>.</summary>
    public static Uri? VariantUri(HlsDocument doc, string id) => id.Length >= 2 && id[0] == 'v' && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n < doc.Variants.Count ? doc.Variants[n].Uri : null;

    public static Uri? AudioUri(HlsDocument doc, string id)
    {
        if (id.Length < 2 || id[0] != 'a' || !int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
        var list = doc.Audio.Where(r => r.Uri is not null).ToList();
        return n < list.Count ? list[n].Uri : null;
    }

    private static HlsDocument ParseCore(string? text, Uri baseUri, bool allowLive)
    {
        if (text is null || text.Length == 0) throw new StreamException(StreamError.Malformed, "The playlist is empty.");
        if (text.Length > MaxBytes) throw new StreamException(StreamError.Malformed, "The playlist is too large.");
        if (text[0] == '\uFEFF') text = text[1..];
        var lines = SplitLines(text);
        int first = 0;
        while (first < lines.Count && lines[first].Length == 0) first++;
        if (first >= lines.Count || lines[first] != "#EXTM3U") throw new StreamException(StreamError.Malformed, "This is not a playlist (#EXTM3U is missing).");
        bool master = false, media = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) master = true;
            else if (line.StartsWith("#EXTINF", StringComparison.Ordinal)) media = true;
        }
        // Protection markers anywhere in the file win over every other judgement.
        foreach (var line in lines)
            if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) || line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal)) CheckKeyForDrm(ParseAttributes(line[(line.IndexOf(':') + 1)..]));
        if (master) return ParseMaster(lines, baseUri);
        if (!media) throw new StreamException(StreamError.Malformed, "The playlist lists no pieces.");
        return ParseMedia(lines, baseUri, allowLive);
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                int end = i; if (end > start && text[end - 1] == '\r') end--;
                if (end - start > MaxLineChars) throw new StreamException(StreamError.Malformed, "A line of the playlist is too long.");
                lines.Add(text.Substring(start, end - start).Trim());
                start = i + 1;
                if (lines.Count > MaxSegments * 8) throw new StreamException(StreamError.Malformed, "The playlist has too many lines.");
            }
        }
        return lines;
    }

    private static void CheckKeyForDrm(Dictionary<string, string> a)
    {
        var method = a.GetValueOrDefault("METHOD") ?? "";
        if (method.StartsWith("SAMPLE-AES", StringComparison.Ordinal)) throw new StreamException(StreamError.Drm, "The stream is protected (DRM).");
        if (method.Length == 0) throw new StreamException(StreamError.Malformed, "An encryption tag has no method.");
        if (a.TryGetValue("KEYFORMAT", out var format) && !format.Equals("identity", StringComparison.OrdinalIgnoreCase)) throw new StreamException(StreamError.Drm, "The stream is protected (DRM).");
        if (a.TryGetValue("URI", out var uri))
        {
            var u = uri.TrimStart();
            if (u.StartsWith("skd:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("skd-", StringComparison.OrdinalIgnoreCase) || u.Contains("widevine", StringComparison.OrdinalIgnoreCase) || u.Contains("playready", StringComparison.OrdinalIgnoreCase) || u.Contains("fairplay", StringComparison.OrdinalIgnoreCase))
                throw new StreamException(StreamError.Drm, "The stream is protected (DRM).");
        }
    }

    /// <summary>Attribute list: NAME=value pairs separated by commas; quoted strings may contain commas. Names are upper-cased.</summary>
    public static Dictionary<string, string> ParseAttributes(string s)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (s[i] == ',' || s[i] == ' ' || s[i] == '\t')) i++;
            int nameStart = i;
            while (i < s.Length && s[i] != '=' && s[i] != ',') i++;
            if (i >= s.Length || s[i] != '=') { continue; }
            var name = s[nameStart..i].Trim().ToUpperInvariant();
            i++;
            string value;
            if (i < s.Length && s[i] == '"')
            {
                int close = s.IndexOf('"', i + 1);
                if (close < 0) { value = s[(i + 1)..]; i = s.Length; } else { value = s[(i + 1)..close]; i = close + 1; }
            }
            else
            {
                int vs = i;
                while (i < s.Length && s[i] != ',') i++;
                value = s[vs..i].Trim();
            }
            if (name.Length > 0 && !map.ContainsKey(name)) map[name] = value;
        }
        return map;
    }

    private static Uri Resolve(Uri baseUri, string reference)
    {
        if (reference.Length == 0 || !Uri.TryCreate(baseUri, reference, out var uri)) throw new StreamException(StreamError.Malformed, "A playlist address could not be read.");
        if (uri.Scheme is not ("http" or "https")) throw new StreamException(StreamError.Unsupported, "The playlist uses an address that is not http or https.");
        return uri;
    }

    private static HlsDocument ParseMaster(List<string> lines, Uri baseUri)
    {
        var variants = new List<HlsVariantInfo>(); var audio = new List<HlsRendition>();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                var a = ParseAttributes(line["#EXT-X-STREAM-INF:".Length..]);
                int j = i + 1;
                while (j < lines.Count && (lines[j].Length == 0 || (lines[j][0] == '#'))) j++;
                if (j >= lines.Count) throw new StreamException(StreamError.Malformed, "A variant has no address.");
                i = j;
                if (variants.Count >= MaxVariants) throw new StreamException(StreamError.Malformed, "The playlist has too many variants.");
                long bw = a.TryGetValue("BANDWIDTH", out var b) && long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bn) ? bn : 0;
                long avg = a.TryGetValue("AVERAGE-BANDWIDTH", out var ab) && long.TryParse(ab, NumberStyles.None, CultureInfo.InvariantCulture, out var an) ? an : 0;
                int w = 0, h = 0;
                if (a.TryGetValue("RESOLUTION", out var res))
                {
                    var parts = res.Split('x', 'X');
                    if (parts.Length == 2) { int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out w); int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out h); }
                }
                double fps = a.TryGetValue("FRAME-RATE", out var fr) && double.TryParse(fr, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && double.IsFinite(f) ? f : 0;
                variants.Add(new HlsVariantInfo(Resolve(baseUri, lines[j]), bw, avg, w, h, fps, a.GetValueOrDefault("CODECS"), a.GetValueOrDefault("AUDIO")));
            }
            else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var a = ParseAttributes(line["#EXT-X-MEDIA:".Length..]);
                if (!string.Equals(a.GetValueOrDefault("TYPE"), "AUDIO", StringComparison.Ordinal)) continue; // subtitles and closed captions are ignored
                Uri? uri = a.TryGetValue("URI", out var u) && u.Length > 0 ? Resolve(baseUri, u) : null;
                int channels = 0;
                if (a.TryGetValue("CHANNELS", out var ch)) int.TryParse(ch.Split('/')[0], NumberStyles.None, CultureInfo.InvariantCulture, out channels);
                audio.Add(new HlsRendition(a.GetValueOrDefault("GROUP-ID") ?? "", a.GetValueOrDefault("NAME"), a.GetValueOrDefault("LANGUAGE"),
                    string.Equals(a.GetValueOrDefault("DEFAULT"), "YES", StringComparison.Ordinal), string.Equals(a.GetValueOrDefault("AUTOSELECT"), "YES", StringComparison.Ordinal), uri, channels));
            }
        }
        if (variants.Count == 0) throw new StreamException(StreamError.Malformed, "The playlist offers no variants.");
        return new HlsDocument { IsMaster = true, Variants = variants, Audio = audio };
    }

    private sealed class KeyState { public string Method = "NONE"; public Uri? Uri; public byte[]? Iv; }

    private static HlsDocument ParseMedia(List<string> lines, Uri baseUri, bool allowLive)
    {
        var segments = new List<StreamSegment>();
        StreamSegment? init = null; (Uri, ByteRange?)? initKey = null;
        var key = new KeyState();
        long mediaSequence = 0; double target = 0, total = 0, pending = -1;
        bool ended = false, vod = false, sawSequence = false;
        ByteRange? pendingRange = null; long lastEnd = 0; string? lastRangeUri = null;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            if (line[0] == '#')
            {
                if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    var rest = line["#EXTINF:".Length..]; int comma = rest.IndexOf(',');
                    var num = (comma >= 0 ? rest[..comma] : rest).Trim();
                    if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out pending) || !double.IsFinite(pending) || pending < 0 || pending > 1e7) throw new StreamException(StreamError.Malformed, "A piece has an invalid length.");
                }
                else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal)) double.TryParse(line["#EXT-X-TARGETDURATION:".Length..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out target);
                else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                {
                    if (!long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out mediaSequence)) throw new StreamException(StreamError.Malformed, "The media sequence number is invalid.");
                    sawSequence = true;
                }
                else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal)) vod = line["#EXT-X-PLAYLIST-TYPE:".Length..].Trim() == "VOD";
                else if (line == "#EXT-X-ENDLIST") ended = true;
                else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
                {
                    pendingRange = ParseRange(line["#EXT-X-BYTERANGE:".Length..].Trim(), null, true);
                }
                else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
                {
                    var a = ParseAttributes(line["#EXT-X-KEY:".Length..]);
                    var method = a.GetValueOrDefault("METHOD");
                    if (method == "NONE") key = new KeyState();
                    else if (method == "AES-128")
                    {
                        if (!a.TryGetValue("URI", out var uri) || uri.Length == 0) throw new StreamException(StreamError.Malformed, "An encryption key has no address.");
                        byte[]? iv = null;
                        if (a.TryGetValue("IV", out var ivText)) iv = ParseIv(ivText);
                        key = new KeyState { Method = "AES-128", Uri = Resolve(baseUri, uri), Iv = iv };
                    }
                    else throw new StreamException(StreamError.Unsupported, "The encryption method is not supported.");
                }
                else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                {
                    var a = ParseAttributes(line["#EXT-X-MAP:".Length..]);
                    if (!a.TryGetValue("URI", out var uri) || uri.Length == 0) throw new StreamException(StreamError.Malformed, "An initialization section has no address.");
                    var mapUri = Resolve(baseUri, uri);
                    ByteRange? range = a.TryGetValue("BYTERANGE", out var br) ? ParseRange(br, null, false) : null;
                    if (init is not null)
                    {
                        if (initKey!.Value.Item1 != mapUri || initKey.Value.Item2 != range) throw new StreamException(StreamError.Unsupported, "The stream changes its initialization section; it cannot be joined.");
                        continue;
                    }
                    StreamEncryption? enc = key.Method == "AES-128" && key.Iv is not null ? new StreamEncryption(key.Uri!, key.Iv) : null;
                    init = new StreamSegment(-1, mapUri, range, enc, 0); initKey = (mapUri, range);
                }
                continue;
            }
            // A URI line is one piece.
            if (pending < 0) throw new StreamException(StreamError.Malformed, "A piece has no length line.");
            if (segments.Count >= MaxSegments) throw new StreamException(StreamError.Malformed, "The playlist has too many pieces.");
            var segmentUri = Resolve(baseUri, line);
            ByteRange? segRange = null;
            if (pendingRange is { } pr)
            {
                var offset = pr.Offset >= 0 ? pr.Offset : (lastRangeUri == segmentUri.AbsoluteUri ? lastEnd : throw new StreamException(StreamError.Malformed, "A byte range has no start."));
                segRange = new ByteRange(offset, pr.Length); lastEnd = offset + pr.Length; lastRangeUri = segmentUri.AbsoluteUri;
            }
            else lastRangeUri = null;
            pendingRange = null;
            StreamEncryption? encryption = null;
            if (key.Method == "AES-128") encryption = new StreamEncryption(key.Uri!, key.Iv ?? DeriveIv(mediaSequence + segments.Count));
            segments.Add(new StreamSegment(segments.Count, segmentUri, segRange, encryption, pending));
            total += pending; pending = -1;
        }
        _ = sawSequence;
        if (segments.Count == 0) throw new StreamException(StreamError.Malformed, "The playlist lists no pieces.");
        bool live = !ended && !vod;
        if (live && !allowLive) throw new StreamException(StreamError.Live, "This is a live stream. Live streams are not downloaded.");
        return new HlsDocument
        {
            IsMaster = false, IsLive = live, TargetDuration = target,
            Media = new TrackPlan { Segments = segments, Init = init, Container = ContainerOf(segments, init), Duration = total }
        };
    }

    private static StreamContainer ContainerOf(List<StreamSegment> segments, StreamSegment? init)
    {
        var path = segments[0].Uri.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".vtt", StringComparison.Ordinal) || path.EndsWith(".webvtt", StringComparison.Ordinal) || path.EndsWith(".srt", StringComparison.Ordinal))
            throw new StreamException(StreamError.Unsupported, "This playlist carries subtitles, not a video.");
        if (init is not null) return StreamContainer.Fmp4;
        if (path.EndsWith(".aac", StringComparison.Ordinal) || path.EndsWith(".adts", StringComparison.Ordinal)) return StreamContainer.Aac;
        if (path.EndsWith(".mp3", StringComparison.Ordinal)) return StreamContainer.Mp3;
        if (path.EndsWith(".m4s", StringComparison.Ordinal) || path.EndsWith(".mp4", StringComparison.Ordinal) || path.EndsWith(".m4a", StringComparison.Ordinal) || path.EndsWith(".m4v", StringComparison.Ordinal) || path.EndsWith(".cmfv", StringComparison.Ordinal) || path.EndsWith(".cmfa", StringComparison.Ordinal)) return StreamContainer.Fmp4;
        return StreamContainer.Ts;
    }

    /// <summary>"length[@offset]". An absent offset is reported as -1 (it continues after the previous range of the same file).</summary>
    private static ByteRange ParseRange(string text, object? unused, bool offsetOptional)
    {
        var parts = text.Split('@');
        if (parts.Length is < 1 or > 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length <= 0) throw new StreamException(StreamError.Malformed, "A byte range is invalid.");
        long offset = -1;
        if (parts.Length == 2 && !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out offset)) throw new StreamException(StreamError.Malformed, "A byte range is invalid.");
        if (parts.Length == 1 && !offsetOptional) throw new StreamException(StreamError.Malformed, "A byte range has no start.");
        if (offset > long.MaxValue - length) throw new StreamException(StreamError.Malformed, "A byte range is invalid.");
        return new ByteRange(offset, length);
    }

    private static byte[] ParseIv(string text)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (hex.Length == 0 || hex.Length > 32 || !hex.All(Uri.IsHexDigit)) throw new StreamException(StreamError.Malformed, "An encryption IV is invalid.");
        hex = hex.PadLeft(32, '0');
        return Convert.FromHexString(hex);
    }

    /// <summary>RFC 8216 5.2: without an IV attribute the IV is the media sequence number as a 128-bit big-endian value.</summary>
    public static byte[] DeriveIv(long sequence)
    {
        var iv = new byte[16];
        for (int i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
        return iv;
    }
}
