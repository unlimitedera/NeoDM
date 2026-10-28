using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NDM.Core;

/// <summary>A parsed MPD. <see cref="Manifest"/> is for the picker; <see cref="BuildPlan"/> turns a choice into the list of pieces.</summary>
public sealed class DashDocument
{
    internal sealed class Rep
    {
        public string Id = ""; public string RawId = ""; public bool IsAudio; public long Bandwidth; public string? Lang, Mime, Codecs;
        public XElement Element = null!; public XElement Set = null!; public Uri Base = null!; public int SetIndex;
    }
    internal sealed class PeriodModel { public double Start; public double? Duration; public XElement Element = null!; public Uri Base = null!; public List<Rep> Reps = new(); }

    public StreamManifest Manifest { get; internal init; } = null!;
    internal List<PeriodModel> Periods { get; init; } = new();
    internal double? TotalDuration { get; init; }

    public StreamPlan BuildPlan(string? variantId, string? audioId)
    {
        var (variant, audio) = StreamSelection.Choose(Manifest, variantId, audioId);
        TrackPlan? v = variant is null ? null : DashParser.BuildTrack(this, variant.Id, false);
        TrackPlan? a = audio is null ? null : DashParser.BuildTrack(this, audio.Id, true);
        return new StreamPlan { Kind = StreamKind.Dash, Duration = Manifest.Duration.TotalSeconds, Video = v, Audio = a };
    }
}

/// <summary>MPEG-DASH manifests (ISO/IEC 23009-1): static presentations only, no content protection. Input is untrusted.</summary>
public static class DashParser
{
    public const int MaxChars = 32 * 1024 * 1024, MaxDepth = 128, MaxSegments = 200000, MaxRepresentations = 2000;
    private const string XlinkNs = "http://www.w3.org/1999/xlink";

    public static StreamParseResult<DashDocument> Parse(string? text, Uri baseUri)
    {
        try { return new(ParseCore(text, baseUri), null); }
        catch (StreamException ex) { return new(null, ex); }
        catch (Exception ex) when (ex is XmlException or FormatException or ArgumentException or OverflowException or InvalidOperationException or UriFormatException or KeyNotFoundException or InvalidCastException or NullReferenceException)
        { return new(null, new StreamException(StreamError.Malformed, "The manifest could not be read.")); }
    }

    private static string? A(XElement? e, string name) => e?.Attribute(name)?.Value;
    private static IEnumerable<XElement> Kids(XElement e, string name) => e.Elements().Where(x => x.Name.LocalName == name);
    private static XElement? Kid(XElement e, string name) => Kids(e, name).FirstOrDefault();

    private static readonly Regex DurationRegex = new(@"^P(?:(\d+(?:\.\d+)?)Y)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)D)?(?:T(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?)?$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>ISO 8601 duration ("PT1H2M3.5S") in seconds, or null.</summary>
    public static double? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64) return null;
        Match m;
        try { m = DurationRegex.Match(text.Trim()); } catch (RegexMatchTimeoutException) { return null; }
        if (!m.Success || text.Trim() == "P" || text.Trim().EndsWith('T')) return null;
        double Get(int g) => m.Groups[g].Success ? double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
        var s = Get(1) * 365 * 86400 + Get(2) * 30 * 86400 + Get(3) * 86400 + Get(4) * 3600 + Get(5) * 60 + Get(6);
        return double.IsFinite(s) && s >= 0 && s < 1e10 ? s : null;
    }

    private static Uri ResolveUri(Uri baseUri, string reference)
    {
        if (!Uri.TryCreate(baseUri, reference, out var uri)) throw new StreamException(StreamError.Malformed, "A manifest address could not be read.");
        if (uri.Scheme is not ("http" or "https")) throw new StreamException(StreamError.Unsupported, "The manifest uses an address that is not http or https.");
        return uri;
    }

    private static Uri WithBase(Uri parent, XElement e)
    {
        var b = Kid(e, "BaseURL")?.Value.Trim();
        return string.IsNullOrEmpty(b) ? parent : ResolveUri(parent, b);
    }

    private static bool IsVideoCodec(string c) => c.StartsWith("avc", StringComparison.OrdinalIgnoreCase) || c.StartsWith("hvc", StringComparison.OrdinalIgnoreCase) || c.StartsWith("hev", StringComparison.OrdinalIgnoreCase) || c.StartsWith("vp0", StringComparison.OrdinalIgnoreCase) || c.StartsWith("vp9", StringComparison.OrdinalIgnoreCase) || c.StartsWith("av01", StringComparison.OrdinalIgnoreCase) || c.StartsWith("dvh", StringComparison.OrdinalIgnoreCase) || c.StartsWith("mp4v", StringComparison.OrdinalIgnoreCase);
    private static bool IsAudioCodec(string c) => c.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) || c.StartsWith("opus", StringComparison.OrdinalIgnoreCase) || c.StartsWith("ac-3", StringComparison.OrdinalIgnoreCase) || c.StartsWith("ec-3", StringComparison.OrdinalIgnoreCase) || c.StartsWith("vorbis", StringComparison.OrdinalIgnoreCase) || c.StartsWith("flac", StringComparison.OrdinalIgnoreCase) || c.StartsWith("ac-4", StringComparison.OrdinalIgnoreCase);

    private static DashDocument ParseCore(string? text, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new StreamException(StreamError.Malformed, "The manifest is empty.");
        if (text.Length > MaxChars) throw new StreamException(StreamError.Malformed, "The manifest is too large.");
        if (text[0] == '\uFEFF') text = text[1..];
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxChars, MaxCharactersFromEntities = 0, IgnoreComments = true, IgnoreProcessingInstructions = true };
        using (var probe = XmlReader.Create(new StringReader(text), settings))
        {
            int depth = 0;
            while (probe.Read()) { if (probe.NodeType == XmlNodeType.Element) { depth = Math.Max(depth, probe.Depth); if (depth > MaxDepth) throw new StreamException(StreamError.Malformed, "The manifest is nested too deeply."); } }
        }
        XDocument doc;
        using (var reader = XmlReader.Create(new StringReader(text), settings)) doc = XDocument.Load(reader, LoadOptions.None);
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "MPD") throw new StreamException(StreamError.Malformed, "This is not a manifest (MPD).");
        if (root.DescendantsAndSelf().Any(e => e.Name.LocalName == "ContentProtection")) throw new StreamException(StreamError.Drm, "The stream is protected (DRM).");
        if (string.Equals(A(root, "type"), "dynamic", StringComparison.OrdinalIgnoreCase)) throw new StreamException(StreamError.Live, "This is a live stream. Live streams are not downloaded.");
        if (root.DescendantsAndSelf().Any(e => e.Attributes().Any(a => a.Name.NamespaceName == XlinkNs && a.Name.LocalName == "href"))) throw new StreamException(StreamError.Unsupported, "The manifest loads parts from other addresses (xlink), which is not supported.");

        double? total = ParseDuration(A(root, "mediaPresentationDuration"));
        var mpdBase = WithBase(baseUri, root);
        var periods = new List<DashDocument.PeriodModel>();
        var periodEls = Kids(root, "Period").ToList();
        if (periodEls.Count == 0) throw new StreamException(StreamError.Malformed, "The manifest has no periods.");
        double cursor = 0;
        for (int p = 0; p < periodEls.Count; p++)
        {
            var el = periodEls[p];
            double start = ParseDuration(A(el, "start")) ?? cursor;
            double? duration = ParseDuration(A(el, "duration"));
            if (duration is null)
            {
                double? next = p + 1 < periodEls.Count ? ParseDuration(A(periodEls[p + 1], "start")) : null;
                duration = next is not null ? next - start : total is not null ? total - start : null;
                if (duration is <= 0) duration = null;
            }
            cursor = start + (duration ?? 0);
            var model = new DashDocument.PeriodModel { Start = start, Duration = duration, Element = el, Base = WithBase(mpdBase, el) };
            int setIndex = 0;
            foreach (var set in Kids(el, "AdaptationSet"))
            {
                var setBase = WithBase(model.Base, set);
                foreach (var rep in Kids(set, "Representation"))
                {
                    if (periods.Sum(x => x.Reps.Count) + model.Reps.Count >= MaxRepresentations) throw new StreamException(StreamError.Malformed, "The manifest has too many representations.");
                    string? mime = A(rep, "mimeType") ?? A(set, "mimeType"), ct = A(set, "contentType") ?? A(rep, "contentType"), codecs = A(rep, "codecs") ?? A(set, "codecs");
                    bool? audio = null;
                    var kindText = (ct ?? "").ToLowerInvariant(); var mimeText = (mime ?? "").ToLowerInvariant();
                    if (kindText == "video" || mimeText.StartsWith("video/", StringComparison.Ordinal)) audio = false;
                    else if (kindText == "audio" || mimeText.StartsWith("audio/", StringComparison.Ordinal)) audio = true;
                    else if (kindText == "text" || mimeText.StartsWith("text/", StringComparison.Ordinal) || mimeText.Contains("ttml") || mimeText.Contains("vtt")) audio = null;
                    else if (codecs is not null)
                    {
                        var list = codecs.Split(',').Select(c => c.Trim()).ToList();
                        if (list.Any(IsVideoCodec)) audio = false; else if (list.Any(IsAudioCodec)) audio = true;
                    }
                    if (audio is null) continue; // subtitles and anything we cannot place are ignored
                    if (codecs is not null && (codecs.Contains("stpp", StringComparison.OrdinalIgnoreCase) || codecs.Contains("wvtt", StringComparison.OrdinalIgnoreCase))) continue;
                    long bw = long.TryParse(A(rep, "bandwidth"), NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : 0;
                    model.Reps.Add(new DashDocument.Rep
                    {
                        RawId = A(rep, "id") ?? "", IsAudio = audio.Value, Bandwidth = bw, Lang = A(set, "lang") ?? A(rep, "lang"), Mime = mime, Codecs = codecs,
                        Element = rep, Set = set, Base = WithBase(setBase, rep), SetIndex = setIndex
                    });
                }
                setIndex++;
            }
            periods.Add(model);
        }
        var first = periods[0];
        if (first.Reps.Count == 0) throw new StreamException(StreamError.Unsupported, "The manifest has no video or sound tracks.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < first.Reps.Count; i++)
        {
            var r = first.Reps[i];
            var id = r.RawId.Length > 0 ? r.RawId : "r" + i.ToString(CultureInfo.InvariantCulture);
            if (!seen.Add(id)) { id = r.SetIndex.ToString(CultureInfo.InvariantCulture) + "." + id; seen.Add(id); }
            r.Id = id;
        }
        var variants = new List<VideoVariant>(); var audios = new List<AudioTrack>();
        var defaultSet = first.Reps.Where(r => r.IsAudio).Select(r => (int?)r.SetIndex).FirstOrDefault();
        var defaultAudio = first.Reps.Where(r => r.IsAudio && r.SetIndex == defaultSet).OrderByDescending(r => r.Bandwidth).FirstOrDefault();
        bool anyAudio = first.Reps.Any(r => r.IsAudio);
        foreach (var r in first.Reps)
        {
            if (!r.IsAudio)
            {
                var codecList = (r.Codecs ?? "").Split(',').Select(c => c.Trim()).ToList();
                bool own = codecList.Any(IsAudioCodec) || !anyAudio;
                int w = int.TryParse(A(r.Element, "width") ?? A(r.Set, "width"), NumberStyles.None, CultureInfo.InvariantCulture, out var wi) ? wi : 0;
                int h = int.TryParse(A(r.Element, "height") ?? A(r.Set, "height"), NumberStyles.None, CultureInfo.InvariantCulture, out var hi) ? hi : 0;
                var fr = A(r.Element, "frameRate") ?? A(r.Set, "frameRate");
                variants.Add(new VideoVariant(r.Id, r.Bandwidth, w, h, ParseFrameRate(fr), r.Codecs, null) { HasOwnAudio = own });
            }
            else
            {
                int ch = 0;
                var acc = Kid(r.Element, "AudioChannelConfiguration") ?? Kid(r.Set, "AudioChannelConfiguration");
                if (acc is not null) int.TryParse(A(acc, "value"), NumberStyles.None, CultureInfo.InvariantCulture, out ch);
                audios.Add(new AudioTrack(r.Id, r.Lang, A(r.Set, "label") ?? Kid(r.Set, "Label")?.Value, r.Bandwidth, r.Codecs, null, r == defaultAudio, ch));
            }
        }
        double totalSeconds = total ?? periods.Sum(p => p.Duration ?? 0);
        var title = Kid(Kid(root, "ProgramInformation") ?? root, "Title")?.Value.Trim();
        var manifest = new StreamManifest
        {
            Kind = StreamKind.Dash, Url = baseUri.AbsoluteUri, Title = string.IsNullOrEmpty(title) ? null : title,
            Duration = totalSeconds > 0 && totalSeconds < 1e9 ? TimeSpan.FromSeconds(totalSeconds) : TimeSpan.Zero, Variants = variants, AudioTracks = audios
        };
        return new DashDocument { Manifest = manifest, Periods = periods, TotalDuration = total };
    }

    private static double ParseFrameRate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var parts = s.Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n)) return 0;
        if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0) return n / d;
        return n;
    }

    // ---- building the pieces ----

    internal static TrackPlan BuildTrack(DashDocument doc, string repId, bool audio)
    {
        var first = doc.Periods[0].Reps.First(r => r.Id == repId && r.IsAudio == audio);
        var segments = new List<StreamSegment>(); StreamSegment? init = null; Uri? single = null; double duration = 0;
        StreamContainer container = ContainerOf(first);
        for (int p = 0; p < doc.Periods.Count; p++)
        {
            var period = doc.Periods[p];
            var rep = p == 0 ? first : Match(period, first);
            if (rep is null) throw new StreamException(StreamError.Unsupported, "The stream changes its tracks between periods; it cannot be joined.");
            var part = BuildForRepresentation(rep, period, segments.Count);
            if (part.Single is not null)
            {
                if (doc.Periods.Count > 1) throw new StreamException(StreamError.Unsupported, "A single-file track in several periods cannot be joined.");
                single = part.Single; duration = period.Duration ?? doc.TotalDuration ?? 0;
                break;
            }
            if (p == 0) init = part.Init;
            else if ((part.Init is null) != (init is null) || (part.Init is not null && (part.Init.Uri != init!.Uri || part.Init.Range != init.Range)))
                throw new StreamException(StreamError.Unsupported, "The stream changes its initialization section between periods; it cannot be joined.");
            if (segments.Count + part.Segments.Count > MaxSegments) throw new StreamException(StreamError.Malformed, "The manifest has too many pieces.");
            segments.AddRange(part.Segments);
            duration += part.Segments.Sum(s => s.Duration);
        }
        if (single is null && segments.Count == 0) throw new StreamException(StreamError.Malformed, "The track lists no pieces.");
        if (duration <= 0) duration = doc.Manifest.Duration.TotalSeconds;
        return new TrackPlan
        {
            Segments = segments, Init = init, Container = single is not null && container == StreamContainer.Fmp4 ? StreamContainer.Mp4Single : container,
            Codecs = first.Codecs, Bandwidth = first.Bandwidth, Duration = duration, IsAudio = audio, SingleFile = single
        };
    }

    private static DashDocument.Rep? Match(DashDocument.PeriodModel period, DashDocument.Rep wanted)
    {
        var same = period.Reps.Where(r => r.IsAudio == wanted.IsAudio).ToList();
        return same.FirstOrDefault(r => r.RawId.Length > 0 && r.RawId == wanted.RawId) ??
            same.Where(r => string.Equals(r.Lang, wanted.Lang, StringComparison.OrdinalIgnoreCase)).OrderBy(r => Math.Abs(r.Bandwidth - wanted.Bandwidth)).FirstOrDefault() ??
            same.OrderBy(r => Math.Abs(r.Bandwidth - wanted.Bandwidth)).FirstOrDefault();
    }

    private static StreamContainer ContainerOf(DashDocument.Rep r)
    {
        var mime = (r.Mime ?? "").ToLowerInvariant();
        if (mime.Contains("webm")) return StreamContainer.WebM;
        if (mime.Contains("mp2t")) return StreamContainer.Ts;
        if (mime == "audio/mpeg") return StreamContainer.Mp3;
        if (mime == "audio/aac") return StreamContainer.Aac;
        return StreamContainer.Fmp4;
    }

    private static XElement? Chain(DashDocument.Rep rep, DashDocument.PeriodModel period, string name) =>
        Kid(rep.Element, name) ?? Kid(rep.Set, name) ?? Kid(period.Element, name);

    private static string? ChainAttr(IEnumerable<XElement?> chain, string name) => chain.Select(e => A(e, name)).FirstOrDefault(v => v is not null);

    private sealed record Part(List<StreamSegment> Segments, StreamSegment? Init, Uri? Single);

    private static Part BuildForRepresentation(DashDocument.Rep rep, DashDocument.PeriodModel period, int startIndex)
    {
        var templates = new[] { Kid(rep.Element, "SegmentTemplate"), Kid(rep.Set, "SegmentTemplate"), Kid(period.Element, "SegmentTemplate") }.Where(x => x is not null).ToList();
        var lists = new[] { Kid(rep.Element, "SegmentList"), Kid(rep.Set, "SegmentList"), Kid(period.Element, "SegmentList") }.Where(x => x is not null).ToList();
        double? periodSeconds = period.Duration;
        if (templates.Count > 0)
        {
            string? media = ChainAttr(templates, "media"), initT = ChainAttr(templates, "initialization");
            if (media is null) throw new StreamException(StreamError.Malformed, "A segment template has no media address.");
            long timescale = long.TryParse(ChainAttr(templates, "timescale"), NumberStyles.None, CultureInfo.InvariantCulture, out var ts) && ts > 0 ? ts : 1;
            long startNumber = long.TryParse(ChainAttr(templates, "startNumber"), NumberStyles.None, CultureInfo.InvariantCulture, out var sn) ? sn : 1;
            long? dur = long.TryParse(ChainAttr(templates, "duration"), NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
            var timeline = templates.Select(t => Kid(t!, "SegmentTimeline")).FirstOrDefault(t => t is not null);
            StreamSegment? init = null;
            if (initT is not null) init = new StreamSegment(-1, ResolveUri(rep.Base, Expand(initT, rep.RawId, rep.Bandwidth, 0, 0)), null, null, 0);
            var segs = new List<StreamSegment>();
            if (timeline is not null)
            {
                long time = 0; long number = startNumber; bool firstS = true; long t0 = 0;
                long limit = periodSeconds is { } ps ? (long)Math.Min(ps * timescale, long.MaxValue / 4) : -1;
                foreach (var s in Kids(timeline, "S"))
                {
                    if (A(s, "t") is { } tt) { if (!long.TryParse(tt, NumberStyles.None, CultureInfo.InvariantCulture, out time)) throw new StreamException(StreamError.Malformed, "A timeline entry is invalid."); }
                    if (firstS) t0 = time;
                    firstS = false;
                    if (!long.TryParse(A(s, "d"), NumberStyles.None, CultureInfo.InvariantCulture, out var sd) || sd <= 0) throw new StreamException(StreamError.Malformed, "A timeline entry has no length.");
                    long r = 0;
                    if (A(s, "r") is { } rr && !long.TryParse(rr, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out r)) throw new StreamException(StreamError.Malformed, "A timeline repeat is invalid.");
                    long count;
                    if (r >= 0) count = r + 1;
                    else
                    {
                        if (limit < 0) throw new StreamException(StreamError.Malformed, "A timeline repeats to the end of a period of unknown length.");
                        var end = t0 + limit;
                        count = end <= time ? 0 : (end - time + sd - 1) / sd;
                    }
                    if (count > MaxSegments || segs.Count + count > MaxSegments) throw new StreamException(StreamError.Malformed, "The manifest has too many pieces.");
                    for (long k = 0; k < count; k++)
                    {
                        segs.Add(new StreamSegment(startIndex + segs.Count, ResolveUri(rep.Base, Expand(media, rep.RawId, rep.Bandwidth, number, time)), null, null, (double)sd / timescale));
                        time += sd; number++;
                    }
                }
            }
            else
            {
                if (dur is null) throw new StreamException(StreamError.Malformed, "A segment template has neither a duration nor a timeline.");
                if (periodSeconds is not > 0) throw new StreamException(StreamError.Malformed, "The length of a period is unknown.");
                double each = (double)dur.Value / timescale;
                double countD = Math.Ceiling(periodSeconds.Value / each - 1e-9);
                if (!double.IsFinite(countD) || countD > MaxSegments) throw new StreamException(StreamError.Malformed, "The manifest has too many pieces.");
                long count = (long)countD;
                long time = 0;
                for (long k = 0; k < count; k++)
                {
                    segs.Add(new StreamSegment(startIndex + (int)k, ResolveUri(rep.Base, Expand(media, rep.RawId, rep.Bandwidth, startNumber + k, time)), null, null, Math.Min(each, periodSeconds.Value - k * each)));
                    time += dur.Value;
                }
            }
            return new Part(segs, init, null);
        }
        if (lists.Count > 0)
        {
            long timescale = long.TryParse(ChainAttr(lists, "timescale"), NumberStyles.None, CultureInfo.InvariantCulture, out var ts) && ts > 0 ? ts : 1;
            long? dur = long.TryParse(ChainAttr(lists, "duration"), NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
            StreamSegment? init = null;
            var initEl = lists.Select(l => Kid(l!, "Initialization")).FirstOrDefault(i => i is not null);
            if (initEl is not null)
                init = new StreamSegment(-1, A(initEl, "sourceURL") is { } su ? ResolveUri(rep.Base, su) : rep.Base, A(initEl, "range") is { } ir ? ParseRange(ir) : null, null, 0);
            var segs = new List<StreamSegment>();
            var urls = lists.Select(l => Kids(l!, "SegmentURL").ToList()).FirstOrDefault(u => u.Count > 0) ?? new List<XElement>();
            if (urls.Count > MaxSegments) throw new StreamException(StreamError.Malformed, "The manifest has too many pieces.");
            double each = dur is { } dd ? (double)dd / timescale : (periodSeconds is > 0 && urls.Count > 0 ? periodSeconds.Value / urls.Count : 0);
            foreach (var u in urls)
                segs.Add(new StreamSegment(startIndex + segs.Count, A(u, "media") is { } m ? ResolveUri(rep.Base, m) : rep.Base, A(u, "mediaRange") is { } mr ? ParseRange(mr) : null, null, each));
            if (segs.Count == 0) throw new StreamException(StreamError.Malformed, "A segment list is empty.");
            return new Part(segs, init, null);
        }
        // SegmentBase with an index range, or only a BaseURL: the representation is one file.
        bool hasBase = Chain(rep, period, "SegmentBase") is not null;
        bool hasBaseUrl = Kid(rep.Element, "BaseURL") is not null || Kid(rep.Set, "BaseURL") is not null || Kid(period.Element, "BaseURL") is not null || (rep.Base != period.Base);
        if (hasBase || hasBaseUrl) return new Part(new List<StreamSegment>(), null, rep.Base);
        throw new StreamException(StreamError.Malformed, "A representation has no way to fetch its media.");
    }

    private static ByteRange ParseRange(string text)
    {
        var parts = text.Split('-');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a) || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var b) || b < a) throw new StreamException(StreamError.Malformed, "A byte range is invalid.");
        return new ByteRange(a, b - a + 1);
    }

    /// <summary>Fills $RepresentationID$, $Number$, $Bandwidth$, $Time$ (with optional %0Nd) and $$ in a template.</summary>
    public static string Expand(string template, string repId, long bandwidth, long number, long time)
    {
        if (template.IndexOf('$') < 0) return template;
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c != '$') { sb.Append(c); i++; continue; }
            int close = template.IndexOf('$', i + 1);
            if (close < 0) throw new StreamException(StreamError.Malformed, "A template is not closed.");
            var token = template[(i + 1)..close];
            i = close + 1;
            if (token.Length == 0) { sb.Append('$'); continue; }
            string name = token, format = "";
            int pct = token.IndexOf('%');
            if (pct >= 0) { name = token[..pct]; format = token[pct..]; }
            long value;
            switch (name)
            {
                case "RepresentationID": if (format.Length > 0) throw new StreamException(StreamError.Malformed, "A template format is invalid."); sb.Append(repId); continue;
                case "Number": value = number; break;
                case "Bandwidth": value = bandwidth; break;
                case "Time": value = time; break;
                case "SubNumber": value = 0; break;
                default: throw new StreamException(StreamError.Malformed, "A template names an unknown value.");
            }
            int width = 1;
            if (format.Length > 0)
            {
                var m = Regex.Match(format, @"^%0?(\d{1,2})d$", RegexOptions.CultureInvariant);
                if (!m.Success) throw new StreamException(StreamError.Malformed, "A template format is invalid.");
                width = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            sb.Append(value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'));
        }
        return sb.ToString();
    }
}
