using System.Diagnostics;
using System.Text;
using NDM.Core;

namespace NDM.Tests;

/// <summary>Playlist and manifest parsers: hand-written inputs, refusals, limits and random input.</summary>
internal static class StreamParserTests
{
    private static readonly Uri Base = new("https://cdn.example.com/video/main/index.m3u8?token=abc");
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static string Join(params string[] lines) => string.Join("\n", lines) + "\n";

    private static HlsDocument Hls(string text, Uri? baseUri = null, bool allowLive = false)
    {
        var r = HlsParser.Parse(text, baseUri ?? Base, allowLive);
        Check(r.Ok, "HLS parse failed: " + r.Error?.Message);
        return r.Value!;
    }
    private static StreamError HlsError(string? text, bool allowLive = false)
    {
        var r = HlsParser.Parse(text, Base, allowLive);
        Check(!r.Ok && r.Error is not null, "HLS input was accepted but should be refused: " + (text is null ? "null" : text[..Math.Min(60, text.Length)].Replace("\n", "|")));
        return r.Error!.Error;
    }
    private static DashDocument Dash(string xml, string? baseUrl = null)
    {
        var r = DashParser.Parse(xml, new Uri(baseUrl ?? "https://cdn.example.com/dash/manifest.mpd"));
        Check(r.Ok, "DASH parse failed: " + r.Error?.Message);
        return r.Value!;
    }
    private static StreamError DashError(string? xml)
    {
        var r = DashParser.Parse(xml, new Uri("https://cdn.example.com/dash/manifest.mpd"));
        if (r.Ok)
        {
            // the pieces of a representation are worked out when it is chosen, so some faults show only there
            try { r.Value!.BuildPlan(null, null); }
            catch (StreamException e) { return e.Error; }
        }
        Check(!r.Ok && r.Error is not null, "DASH input was accepted but should be refused: " + (xml is null ? "null" : xml[..Math.Min(60, xml.Length)]));
        return r.Error!.Error;
    }

    public static Task HlsMaster()
    {
        var text = "\uFEFF" + string.Join("\r\n", new[]
        {
            "#EXTM3U", "#EXT-X-VERSION:6", "#EXT-X-INDEPENDENT-SEGMENTS",
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English, main\",LANGUAGE=\"en\",DEFAULT=YES,AUTOSELECT=YES,CHANNELS=\"2\",URI=\"audio/en/index.m3u8\"",
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"Deutsch\",LANGUAGE=\"de\",DEFAULT=NO,URI=\"audio/de/index.m3u8?lang=de\"",
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"muxed\",NAME=\"Inline\",DEFAULT=YES",
            "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"en\",URI=\"subs/en.m3u8\"",
            "#EXT-X-STREAM-INF:BANDWIDTH=800000,AVERAGE-BANDWIDTH=700000,RESOLUTION=640x360,CODECS=\"avc1.4d401e,mp4a.40.2\",FRAME-RATE=29.970,AUDIO=\"aud\",SUBTITLES=\"subs\"",
            "low/index.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720,CODECS=\"avc1.64001f,mp4a.40.2\",FRAME-RATE=30,AUDIO=\"aud\"",
            "../hi/index.m3u8?sig=1&x=2",
            "# a comment line",
            "#EXT-X-STREAM-INF:BANDWIDTH=150000,CODECS=\"mp4a.40.2\"",
            "https://other.example.net/radio/index.m3u8",
            "#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=90000,RESOLUTION=640x360,URI=\"iframe/low.m3u8\"", ""
        });
        var doc = Hls(text);
        Check(doc.IsMaster && doc.Variants.Count == 3, "three variants expected, I-frame list ignored: " + doc.Variants.Count);
        var low = doc.Variants[0];
        Check(low.Bandwidth == 800000 && low.AverageBandwidth == 700000 && low.Width == 640 && low.Height == 360 && Math.Abs(low.FrameRate - 29.97) < 0.001 && low.Codecs == "avc1.4d401e,mp4a.40.2" && low.AudioGroup == "aud", "variant attributes");
        Check(low.Uri.AbsoluteUri == "https://cdn.example.com/video/main/low/index.m3u8", "relative variant address: " + low.Uri);
        Check(doc.Variants[1].Uri.AbsoluteUri == "https://cdn.example.com/video/hi/index.m3u8?sig=1&x=2", "../ and query string kept: " + doc.Variants[1].Uri);
        Check(doc.Variants[2].Uri.Host == "other.example.net", "absolute address kept");
        Check(doc.Audio.Count == 3 && doc.Audio[0].Name == "English, main" && doc.Audio[0].IsDefault && doc.Audio[0].Channels == 2 && doc.Audio[1].Language == "de" && doc.Audio[2].Uri is null, "audio renditions (quoted comma kept, subtitles ignored)");
        var manifest = HlsParser.ToManifest(doc, Base.AbsoluteUri);
        Check(manifest.Variants.Count == 3 && manifest.AudioTracks.Count == 2, "manifest: two audio tracks with an address");
        Check(manifest.Variants[0].Bandwidth == 700000, "average bandwidth preferred");
        Check(manifest.Variants[0].AudioGroupId == "aud" && manifest.Variants[2].HasOwnAudio, "audio group of the variant; the audio-only one is self contained");
        Check(manifest.BestVariant!.Id == "v1", "best = highest bandwidth");
        Check(manifest.DefaultAudio(manifest.Variants[0])!.Language == "en", "default audio of the group");
        Check(HlsParser.VariantUri(doc, "v1") == doc.Variants[1].Uri && HlsParser.VariantUri(doc, "v9") is null && HlsParser.VariantUri(doc, "") is null && HlsParser.VariantUri(doc, "x1") is null, "variant lookup");
        Check(HlsParser.AudioUri(doc, "a1")!.AbsoluteUri.EndsWith("audio/de/index.m3u8?lang=de", StringComparison.Ordinal), "audio lookup keeps the query");
        return Task.CompletedTask;
    }

    public static Task HlsMedia()
    {
        var text = Join("#EXTM3U", "#EXT-X-VERSION:7", "#EXT-X-TARGETDURATION:6", "#EXT-X-MEDIA-SEQUENCE:7", "#EXT-X-PLAYLIST-TYPE:VOD",
            "#EXT-X-KEY:METHOD=AES-128,URI=\"keys/k1.bin?id=1\"",
            "#EXTINF:5.005,title", "seg0.ts?t=1",
            "#EXT-X-KEY:METHOD=AES-128,URI=\"https://keys.example.org/k2\",IV=0x000102030405060708090A0B0C0D0E0F",
            "#EXTINF:4.5,", "../seg1.ts",
            "#EXT-X-DISCONTINUITY",
            "#EXTINF:3,", "https://cdn2.example.com/seg2.ts",
            "#EXT-X-KEY:METHOD=NONE",
            "#EXTINF:2.25,", "seg3.ts", "#EXT-X-ENDLIST");
        var track = Hls(text).Media!;
        Check(track.Segments.Count == 4 && Math.Abs(track.Duration - 14.755) < 1e-9 && track.Container == StreamContainer.Ts && track.Init is null, "segment count/duration/container");
        Check(track.Segments[0].Uri.AbsoluteUri == "https://cdn.example.com/video/main/seg0.ts?t=1", "query string kept: " + track.Segments[0].Uri);
        Check(track.Segments[1].Uri.AbsoluteUri == "https://cdn.example.com/video/seg1.ts", "../ resolved: " + track.Segments[1].Uri);
        Check(track.Segments[2].Uri.Host == "cdn2.example.com", "absolute segment");
        var e0 = track.Segments[0].Encryption!;
        Check(e0.KeyUri.AbsoluteUri == "https://cdn.example.com/video/main/keys/k1.bin?id=1" && e0.Iv.Length == 16 && e0.Iv[15] == 7 && e0.Iv.Take(15).All(b => b == 0), "derived IV is the media sequence number (7)");
        var e1 = track.Segments[1].Encryption!;
        Check(e1.KeyUri.Host == "keys.example.org" && Convert.ToHexString(e1.Iv) == "000102030405060708090A0B0C0D0E0F", "explicit IV and rotated key");
        Check(track.Segments[2].Encryption!.KeyUri == e1.KeyUri, "key stays until changed");
        Check(track.Segments[3].Encryption is null, "METHOD=NONE ends encryption");
        Check(Convert.ToHexString(HlsParser.DeriveIv(0x0102030405060708)) == "00000000000000000102030405060708", "IV derivation is big-endian");
        // short explicit IV is left padded
        var padded = Hls(Join("#EXTM3U", "#EXT-X-KEY:METHOD=AES-128,URI=\"k\",IV=0x1F", "#EXTINF:1,", "a.ts", "#EXT-X-ENDLIST")).Media!.Segments[0].Encryption!;
        Check(Convert.ToHexString(padded.Iv) == "0000000000000000000000000000001F", "short IV padded");

        // byte ranges and an initialization section with a range
        var br = Hls(Join("#EXTM3U", "#EXT-X-VERSION:7", "#EXT-X-MAP:URI=\"init.mp4\",BYTERANGE=\"720@0\"",
            "#EXTINF:4,", "#EXT-X-BYTERANGE:1000@720", "media.mp4", "#EXTINF:4,", "#EXT-X-BYTERANGE:500", "media.mp4", "#EXTINF:4,", "#EXT-X-BYTERANGE:200@9000", "media.mp4", "#EXT-X-ENDLIST")).Media!;
        Check(br.Container == StreamContainer.Fmp4 && br.Init!.Range == new ByteRange(0, 720) && br.Init.Uri.AbsolutePath.EndsWith("init.mp4"), "init with range");
        Check(br.Segments[0].Range == new ByteRange(720, 1000) && br.Segments[1].Range == new ByteRange(1720, 500) && br.Segments[2].Range == new ByteRange(9000, 200), "implicit offsets continue after the previous range");

        // live and VOD rules
        Check(HlsError(Join("#EXTM3U", "#EXT-X-TARGETDURATION:4", "#EXTINF:4,", "a.ts", "#EXTINF:4,", "b.ts")) == StreamError.Live, "no ENDLIST = live");
        Check(HlsError(Join("#EXTM3U", "#EXT-X-PLAYLIST-TYPE:EVENT", "#EXTINF:4,", "a.ts")) == StreamError.Live, "EVENT without ENDLIST = live");
        Check(Hls(Join("#EXTM3U", "#EXT-X-PLAYLIST-TYPE:VOD", "#EXTINF:4,", "a.ts")).Media!.Segments.Count == 1, "VOD type without ENDLIST is accepted");
        Check(Hls(Join("#EXTM3U", "#EXTINF:4,", "a.ts"), allowLive: true).IsLive, "allowLive parses it");
        // container hints
        Check(Hls(Join("#EXTM3U", "#EXTINF:4,", "a.aac", "#EXT-X-ENDLIST")).Media!.Container == StreamContainer.Aac, "aac");
        Check(Hls(Join("#EXTM3U", "#EXTINF:4,", "a.m4s", "#EXT-X-ENDLIST")).Media!.Container == StreamContainer.Fmp4, "m4s");
        // CRLF, blank lines, unknown tags, comments, trailing text
        var messy = "#EXTM3U\r\n\r\n#EXT-X-FUTURE-TAG:1\r\n# note\r\n#EXTINF:1.5,\r\n\r\nx.ts\r\n#EXT-X-ENDLIST";
        Check(Hls(messy).Media!.Segments.Count == 1, "CRLF and blank lines");
        return Task.CompletedTask;
    }

    public static Task HlsRefusals()
    {
        string K(string attrs) => Join("#EXTM3U", "#EXT-X-KEY:" + attrs, "#EXTINF:4,", "a.ts", "#EXT-X-ENDLIST");
        var drm = new[]
        {
            K("METHOD=SAMPLE-AES,URI=\"k\""), K("METHOD=SAMPLE-AES-CTR,URI=\"k\""),
            K("METHOD=AES-128,URI=\"k\",KEYFORMAT=\"com.apple.streamingkeydelivery\",KEYFORMATVERSIONS=\"1\""),
            K("METHOD=AES-128,URI=\"k\",KEYFORMAT=\"com.microsoft.playready\""),
            K("METHOD=SAMPLE-AES,URI=\"data:text/plain;base64,AAAA\",KEYFORMAT=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\""),
            K("METHOD=AES-128,URI=\"skd://license.example.com/key\""),
            K("METHOD=AES-128,URI=\"https://example.com/widevine/key\""),
            K("METHOD=AES-128,URI=\"https://example.com/fairplay/key\""),
            Join("#EXTM3U", "#EXT-X-SESSION-KEY:METHOD=SAMPLE-AES,URI=\"skd://x\"", "#EXT-X-STREAM-INF:BANDWIDTH=1000", "v.m3u8"),
            Join("#EXTM3U", "#EXT-X-SESSION-KEY:METHOD=AES-128,URI=\"skd://x\",KEYFORMAT=\"com.apple.streamingkeydelivery\"", "#EXT-X-STREAM-INF:BANDWIDTH=1000", "v.m3u8"),
        };
        foreach (var t in drm) Check(HlsError(t) == StreamError.Drm, "DRM marker not refused: " + t.Split('\n')[1]);
        Check(HlsParser.Parse(K("METHOD=AES-128,URI=\"k\",KEYFORMAT=\"identity\""), Base).Ok, "identity key format is plain AES-128");
        Check(HlsError(K("METHOD=MAGIC,URI=\"k\"")) == StreamError.Unsupported, "unknown method");
        Check(HlsError(K("URI=\"k\"")) == StreamError.Malformed, "key tag without method");
        Check(HlsError(K("METHOD=AES-128")) == StreamError.Malformed, "AES-128 without key address");
        Check(HlsError(Join("#EXTM3U", "#EXTINF:4,", "data:video/mp2t;base64,AAAA", "#EXT-X-ENDLIST")) == StreamError.Unsupported, "data: segment");
        Check(HlsError(Join("#EXTM3U", "#EXTINF:4,", "file:///C:/secret.ts", "#EXT-X-ENDLIST")) == StreamError.Unsupported, "file: segment");
        Check(HlsError(Join("#EXTM3U", "#EXTINF:4,", "ftp://example.com/a.ts", "#EXT-X-ENDLIST")) == StreamError.Unsupported, "ftp: segment");
        Check(HlsError(Join("#EXTM3U", "#EXTINF:4,", "seg.vtt", "#EXT-X-ENDLIST")) == StreamError.Unsupported, "subtitle playlist");
        foreach (var bad in new[] { null, "", "   \n", "hello", "<html></html>", "#EXTM3U", "#EXTM3U\n#EXT-X-VERSION:3\n", "#EXTM3U\n#EXTINF:abc,\na.ts\n#EXT-X-ENDLIST", "#EXTM3U\n#EXTINF:-3,\na.ts\n#EXT-X-ENDLIST", "#EXTM3U\n#EXT-X-ENDLIST\na.ts",
            "#EXTM3U\n#EXTINF:4,\n#EXT-X-BYTERANGE:x@y\na.ts\n#EXT-X-ENDLIST", "#EXTM3U\n#EXTINF:4,\n#EXT-X-BYTERANGE:0@0\na.ts\n#EXT-X-ENDLIST", "#EXTM3U\n#EXT-X-MAP:BYTERANGE=\"1@0\"\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST",
            "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:-4\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n", "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\",IV=0xZZ\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST" })
            HlsError(bad);
        // an initialization section that changes cannot be joined
        Check(HlsError(Join("#EXTM3U", "#EXT-X-MAP:URI=\"i1.mp4\"", "#EXTINF:4,", "a.m4s", "#EXT-X-MAP:URI=\"i2.mp4\"", "#EXTINF:4,", "b.m4s", "#EXT-X-ENDLIST")) == StreamError.Unsupported, "init change");
        // ByteRange without an offset after a different file
        Check(HlsError(Join("#EXTM3U", "#EXTINF:4,", "#EXT-X-BYTERANGE:100", "a.mp4", "#EXT-X-ENDLIST")) == StreamError.Malformed, "range without start for a first piece");
        return Task.CompletedTask;
    }

    public static Task HlsLimits()
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:2\n");
        for (int i = 0; i < 150_000; i++) sb.Append("#EXTINF:2.0,\ns").Append(i).Append(".ts\n");
        sb.Append("#EXT-X-ENDLIST\n");
        var clock = Stopwatch.StartNew();
        var doc = Hls(sb.ToString());
        Check(doc.Media!.Segments.Count == 150_000 && doc.Media.Segments[149_999].Index == 149_999, "150k segments");
        Check(clock.Elapsed < TimeSpan.FromSeconds(10), "150k segments took " + clock.Elapsed);
        sb.Clear(); sb.Append("#EXTM3U\n");
        for (int i = 0; i < HlsParser.MaxSegments + 1; i++) sb.Append("#EXTINF:1,\nx").Append(i).Append(".ts\n");
        sb.Append("#EXT-X-ENDLIST\n");
        Check(HlsError(sb.ToString()) == StreamError.Malformed, "too many segments");
        Check(HlsError("#EXTM3U\n#EXTINF:4,\n" + new string('a', 70_000) + ".ts\n#EXT-X-ENDLIST\n") == StreamError.Malformed, "very long line is a typed error");
        Check(HlsError("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"" + new string('k', 200_000) + "\"\n") == StreamError.Malformed, "very long attribute line");
        Check(HlsError(new string('#', 40_000_000)) == StreamError.Malformed, "oversized input");
        var varied = new StringBuilder("#EXTM3U\n");
        for (int i = 0; i < HlsParser.MaxVariants + 1; i++) varied.Append("#EXT-X-STREAM-INF:BANDWIDTH=").Append(i + 1).Append("\nv").Append(i).Append(".m3u8\n");
        Check(HlsError(varied.ToString()) == StreamError.Malformed, "too many variants");
        return Task.CompletedTask;
    }

    private const string Ns = "xmlns=\"urn:mpeg:dash:schema:mpd:2011\"";

    public static Task DashTemplates()
    {
        var xml = $@"<?xml version=""1.0""?>
<MPD {Ns} type=""static"" mediaPresentationDuration=""PT1M0.0S"" minBufferTime=""PT2S"">
 <ProgramInformation><Title>My clip</Title></ProgramInformation>
 <BaseURL>https://cdn.example.com/dash/</BaseURL>
 <Period id=""p0"">
  <BaseURL>content/</BaseURL>
  <AdaptationSet mimeType=""video/mp4"" segmentAlignment=""true"" startWithSAP=""1"">
   <BaseURL>video/</BaseURL>
   <SegmentTemplate timescale=""1000"" duration=""10000"" startNumber=""3"" initialization=""$RepresentationID$/init.mp4"" media=""$RepresentationID$/seg-$Number%05d$.m4s?bw=$Bandwidth$""/>
   <Representation id=""v720"" codecs=""avc1.64001f"" width=""1280"" height=""720"" frameRate=""30000/1001"" bandwidth=""2000000""/>
   <Representation id=""v360"" codecs=""avc1.4d401e"" width=""640"" height=""360"" bandwidth=""600000""/>
  </AdaptationSet>
  <AdaptationSet mimeType=""audio/mp4"" lang=""en"" contentType=""audio"">
   <Representation id=""a1"" codecs=""mp4a.40.2"" bandwidth=""128000"" audioSamplingRate=""48000"">
    <AudioChannelConfiguration schemeIdUri=""urn:mpeg:dash:23003:3:audio_channel_configuration:2011"" value=""2""/>
    <SegmentTemplate timescale=""48000"" duration=""480000"" initialization=""a/init.m4a"" media=""a/$Number$.m4s""/>
   </Representation>
  </AdaptationSet>
  <AdaptationSet contentType=""text"" mimeType=""application/mp4"" lang=""en""><Representation id=""t1"" codecs=""stpp"" bandwidth=""1000""><BaseURL>t.mp4</BaseURL></Representation></AdaptationSet>
 </Period>
</MPD>";
        var doc = Dash(xml);
        var m = doc.Manifest;
        Check(m.Duration == TimeSpan.FromSeconds(60) && m.Title == "My clip" && m.Kind == StreamKind.Dash, "manifest basics");
        Check(m.Variants.Count == 2 && m.AudioTracks.Count == 1, "subtitle representation ignored");
        Check(m.Variants[0].Id == "v720" && m.Variants[0].Width == 1280 && Math.Abs(m.Variants[0].FrameRate - 29.97) < 0.01 && m.Variants[0].Bandwidth == 2000000, "video variant");
        Check(m.AudioTracks[0].Language == "en" && m.AudioTracks[0].Channels == 2 && m.AudioTracks[0].IsDefault, "audio track");
        var plan = doc.BuildPlan("v720", null);
        Check(plan.Video is not null && plan.Audio is not null, "default audio chosen");
        var v = plan.Video!; var a = plan.Audio!;
        Check(v.Segments.Count == 6 && v.Container == StreamContainer.Fmp4, "60 s / 10 s = 6 segments");
        Check(v.Init!.Uri.AbsoluteUri == "https://cdn.example.com/dash/content/video/v720/init.mp4", "BaseURL at three levels: " + v.Init.Uri);
        Check(v.Segments[0].Uri.AbsoluteUri == "https://cdn.example.com/dash/content/video/v720/seg-00003.m4s?bw=2000000" && v.Segments[5].Uri.AbsoluteUri.Contains("seg-00008.m4s"), "$Number%05d$ from startNumber 3 and $Bandwidth$: " + v.Segments[0].Uri);
        Check(Math.Abs(v.Segments[0].Duration - 10) < 1e-9 && Math.Abs(v.Duration - 60) < 1e-9, "durations");
        Check(a.Segments.Count == 6 && a.Segments[0].Uri.AbsoluteUri == "https://cdn.example.com/dash/content/a/1.m4s" && a.IsAudio, "audio template at representation level, timescale 48000");
        Check(plan.EstimatedBytes == (2_000_000L + 128_000L) * 60 / 8, "estimate = bandwidth x duration: " + plan.EstimatedBytes);
        var plan2 = doc.BuildPlan("v360", StreamSelection.NoAudio);
        Check(plan2.Audio is null && plan2.Video!.Segments[0].Uri.AbsoluteUri.Contains("/v360/") , "choose variant, no audio");
        bool threw = false; try { doc.BuildPlan("nope", null); } catch (StreamException) { threw = true; }
        Check(threw, "unknown variant id is refused");
        return Task.CompletedTask;
    }

    public static Task DashTimelineAndOthers()
    {
        var xml = $@"<MPD {Ns} type=""static"" mediaPresentationDuration=""PT30S"">
 <Period>
  <AdaptationSet mimeType=""video/mp4"" codecs=""avc1.42c01e"">
   <SegmentTemplate timescale=""90000"" media=""v/$RepresentationID$/t$Time$.m4s"" initialization=""v/$RepresentationID$/init.mp4"">
    <SegmentTimeline><S t=""0"" d=""180000"" r=""2""/><S d=""90000""/><S d=""270000"" r=""-1""/></SegmentTimeline>
   </SegmentTemplate>
   <Representation id=""A"" bandwidth=""500000"" width=""320"" height=""180""/>
  </AdaptationSet>
 </Period>
</MPD>";
        var doc = Dash(xml);
        var t = doc.BuildPlan(null, null).Video!;
        // 3 x 2 s, 1 s, then 3 s repeats to 30 s: (30 - 7) / 3 = 7.67 -> 8
        Check(t.Segments.Count == 3 + 1 + 8, "timeline entries: " + t.Segments.Count);
        Check(t.Segments[0].Uri.AbsolutePath.EndsWith("/v/A/t0.m4s") && t.Segments[1].Uri.AbsolutePath.EndsWith("/t180000.m4s") && t.Segments[3].Uri.AbsolutePath.EndsWith("/t540000.m4s") && t.Segments[4].Uri.AbsolutePath.EndsWith("/t630000.m4s"), "$Time$ values: " + string.Join(",", t.Segments.Take(5).Select(s => s.Uri.AbsolutePath)));
        Check(Math.Abs(t.Segments[0].Duration - 2) < 1e-9 && Math.Abs(t.Segments[3].Duration - 1) < 1e-9, "timeline durations");

        // multi period: concatenated in order, the same init is used once
        var multi = $@"<MPD {Ns} type=""static"" mediaPresentationDuration=""PT20S"">
 <Period id=""1"" duration=""PT8S""><AdaptationSet mimeType=""video/mp4"" codecs=""avc1""><SegmentTemplate duration=""4"" media=""p1-$Number$.m4s"" initialization=""init.mp4""/><Representation id=""r"" bandwidth=""100""/></AdaptationSet></Period>
 <Period id=""2"" start=""PT8S""><AdaptationSet mimeType=""video/mp4"" codecs=""avc1""><SegmentTemplate duration=""4"" media=""p2-$Number$.m4s"" initialization=""init.mp4""/><Representation id=""r"" bandwidth=""100""/></AdaptationSet></Period>
</MPD>";
        var mp = Dash(multi).BuildPlan(null, null).Video!;
        Check(mp.Segments.Count == 2 + 3 && mp.Segments.Select((s, i) => s.Index == i).All(x => x), "periods concatenated (2 + 3) with running indexes");
        Check(mp.Segments[1].Uri.AbsolutePath.EndsWith("p1-2.m4s") && mp.Segments[2].Uri.AbsolutePath.EndsWith("p2-1.m4s"), "period order kept");
        var mismatched = multi.Replace("p2-$Number$.m4s\" initialization=\"init.mp4\"", "p2-$Number$.m4s\" initialization=\"other-init.mp4\"");
        var r = DashParser.Parse(mismatched, new Uri("https://x.example/m.mpd"));
        Check(r.Ok, "parse is fine"); bool refused = false; try { r.Value!.BuildPlan(null, null); } catch (StreamException e) { refused = e.Error == StreamError.Unsupported; }
        Check(refused, "different init sections across periods are refused");

        // SegmentList with ranges
        var list = $@"<MPD {Ns} type=""static"" mediaPresentationDuration=""PT10S""><Period><AdaptationSet mimeType=""audio/mp4""><Representation id=""x"" bandwidth=""64000"" codecs=""mp4a.40.2"">
 <BaseURL>https://a.example/file/</BaseURL>
 <SegmentList duration=""5""><Initialization sourceURL=""i.mp4"" range=""0-599""/><SegmentURL media=""s1.m4s""/><SegmentURL media=""s2.m4s"" mediaRange=""100-199""/></SegmentList>
</Representation></AdaptationSet></Period></MPD>";
        var sl = Dash(list).BuildPlan(null, null).Audio!;
        Check(sl.Segments.Count == 2 && sl.Init!.Range == new ByteRange(0, 600) && sl.Segments[1].Range == new ByteRange(100, 100) && Math.Abs(sl.Segments[0].Duration - 5) < 1e-9, "SegmentList with ranges");

        // SegmentBase / BaseURL only = one file
        var single = $@"<MPD {Ns} type=""static"" mediaPresentationDuration=""PT10S""><Period><AdaptationSet mimeType=""video/mp4""><Representation id=""x"" bandwidth=""64000"" codecs=""avc1""><BaseURL>movie.mp4</BaseURL><SegmentBase indexRange=""800-1200""><Initialization range=""0-799""/></SegmentBase></Representation></AdaptationSet></Period></MPD>";
        var sp = Dash(single).BuildPlan(null, null);
        Check(sp.Video!.SingleFile!.AbsoluteUri == "https://cdn.example.com/dash/movie.mp4" && sp.Video.Container == StreamContainer.Mp4Single && sp.Video.Segments.Count == 0, "single-file representation");
        var plainBase = single.Replace("<SegmentBase indexRange=\"800-1200\"><Initialization range=\"0-799\"/></SegmentBase>", "");
        Check(Dash(plainBase).BuildPlan(null, null).Video!.SingleFile is not null, "BaseURL only");
        // webm and ts hints
        var web = single.Replace("video/mp4", "video/webm").Replace("avc1", "vp9");
        Check(Dash(web).BuildPlan(null, null).Video!.Container == StreamContainer.WebM, "webm container");
        // audio only manifest
        var audioOnly = $@"<MPD {Ns} type=""static"" mediaPresentationDuration=""PT10S""><Period><AdaptationSet mimeType=""audio/mp4"" lang=""fr""><Representation id=""x"" bandwidth=""64000""><SegmentTemplate duration=""5"" media=""a$Number$.m4s"" initialization=""i.mp4""/></Representation></AdaptationSet></Period></MPD>";
        var ao = Dash(audioOnly);
        Check(ao.Manifest.Variants.Count == 0 && ao.Manifest.AudioTracks.Count == 1, "audio-only manifest");
        var ap = ao.BuildPlan(null, null);
        Check(ap.Video is null && ap.Audio!.Segments.Count == 2, "audio-only plan");
        // muxed representation needs no separate sound
        var muxed = $@"<MPD {Ns} type=""static"" mediaPresentationDuration=""PT10S""><Period><AdaptationSet mimeType=""video/mp4""><Representation id=""m"" bandwidth=""64000"" codecs=""avc1.4d401e,mp4a.40.2""><SegmentTemplate duration=""5"" media=""m$Number$.m4s""/></Representation></AdaptationSet></Period></MPD>";
        Check(Dash(muxed).Manifest.Variants[0].HasOwnAudio, "muxed codecs recognised");
        return Task.CompletedTask;
    }

    public static Task DashRefusals()
    {
        string Wrap(string inner, string attrs = "type=\"static\"") => $"<MPD {Ns} {attrs} mediaPresentationDuration=\"PT10S\"><Period><AdaptationSet mimeType=\"video/mp4\">{inner}</AdaptationSet></Period></MPD>";
        const string Rep = "<Representation id=\"r\" bandwidth=\"1\"><SegmentTemplate duration=\"5\" media=\"$Number$.m4s\"/></Representation>";
        Check(DashError(Wrap(Rep, "type=\"dynamic\"")) == StreamError.Live, "dynamic = live");
        Check(DashError(Wrap(Rep.Replace("<Representation id=\"r\" bandwidth=\"1\">", "<Representation id=\"r\" bandwidth=\"1\"><ContentProtection schemeIdUri=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"/>"))) == StreamError.Drm, "Widevine at representation level");
        Check(DashError(Wrap("<ContentProtection schemeIdUri=\"urn:mpeg:dash:mp4protection:2011\" value=\"cenc\"/>" + Rep)) == StreamError.Drm, "mp4protection at set level");
        Check(DashError(Wrap("<ContentProtection schemeIdUri=\"urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95\"/>" + Rep)) == StreamError.Drm, "PlayReady");
        Check(DashError($"<MPD {Ns} type=\"static\" mediaPresentationDuration=\"PT10S\"><ContentProtection schemeIdUri=\"urn:uuid:94ce86fb-07ff-4f43-adb8-93d2fa968ca2\"/><Period/></MPD>") == StreamError.Drm, "FairPlay at MPD level");
        Check(DashError($"<MPD {Ns} xmlns:xlink=\"http://www.w3.org/1999/xlink\" type=\"static\" mediaPresentationDuration=\"PT10S\"><Period xlink:href=\"http://ads.example/p.xml\" xlink:actuate=\"onLoad\"/></MPD>") == StreamError.Unsupported, "xlink");
        Check(DashError(Wrap(Rep.Replace("$Number$", "$Bogus$"))) == StreamError.Malformed, "unknown template identifier");
        Check(DashError(Wrap(Rep.Replace("$Number$", "$Number%xx$"))) == StreamError.Malformed, "bad format tag");
        Check(DashError(Wrap(Rep.Replace("$Number$.m4s", "$Number.m4s"))) == StreamError.Malformed, "unclosed template");
        Check(DashError(Wrap("<Representation id=\"r\" bandwidth=\"1\"/>")) == StreamError.Malformed, "no way to fetch media");
        Check(DashError(Wrap("<Representation id=\"r\" bandwidth=\"1\"><SegmentTemplate media=\"$Number$.m4s\"/></Representation>")) == StreamError.Malformed, "template without duration or timeline");
        Check(DashError(Wrap("<Representation id=\"r\" bandwidth=\"1\"><SegmentTemplate duration=\"1\" media=\"$Number$.m4s\"/></Representation>").Replace("PT10S", "PT900000S")) == StreamError.Malformed, "too many pieces");
        Check(DashError(Wrap("<Representation id=\"r\" bandwidth=\"1\"><SegmentTemplate timescale=\"1\" media=\"$Time$.m4s\"><SegmentTimeline><S t=\"0\" d=\"1\" r=\"999999999\"/></SegmentTimeline></SegmentTemplate></Representation>")) == StreamError.Malformed, "huge timeline repeat");
        Check(DashError(Wrap("<Representation id=\"r\" bandwidth=\"1\"><SegmentTemplate timescale=\"1\" media=\"$Time$.m4s\"><SegmentTimeline><S d=\"0\"/></SegmentTimeline></SegmentTemplate></Representation>")) == StreamError.Malformed, "zero-length timeline entry");
        Check(DashError(Wrap("<Representation id=\"r\" bandwidth=\"1\"><BaseURL>ftp://x/y.mp4</BaseURL></Representation>")) == StreamError.Unsupported, "ftp base");
        // no tracks at all
        Check(DashError($"<MPD {Ns} type=\"static\" mediaPresentationDuration=\"PT10S\"><Period/></MPD>") == StreamError.Unsupported, "no tracks");
        Check(DashError($"<MPD {Ns} type=\"static\"/>") == StreamError.Malformed, "no period");
        foreach (var bad in new[] { null, "", "   ", "not xml", "<html><body>hi</body></html>", "<MPD", "<MPD></MPD", "{\"json\":true}", "\u0000\u0001\u0002" }) DashError(bad);
        // XML hazards: a DTD / entity expansion is never processed
        var xxe = "<?xml version=\"1.0\"?><!DOCTYPE MPD [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]>" + $"<MPD {Ns} type=\"static\"><Period><BaseURL>&c;</BaseURL></Period></MPD>";
        Check(DashError(xxe) == StreamError.Malformed, "DTD prohibited");
        var file = "<?xml version=\"1.0\"?><!DOCTYPE MPD [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]>" + $"<MPD {Ns}><Period><BaseURL>&x;</BaseURL></Period></MPD>";
        Check(DashError(file) == StreamError.Malformed, "external entity prohibited");
        // deep nesting
        var deep = new StringBuilder($"<MPD {Ns} type=\"static\">");
        for (int i = 0; i < 5000; i++) deep.Append("<X>"); for (int i = 0; i < 5000; i++) deep.Append("</X>"); deep.Append("</MPD>");
        Check(DashError(deep.ToString()) == StreamError.Malformed, "deep nesting");
        return Task.CompletedTask;
    }

    public static Task DashDurationsAndTemplates()
    {
        Check(DashParser.ParseDuration("PT1H2M3.5S") is { } a && Math.Abs(a - 3723.5) < 1e-9, "PT1H2M3.5S");
        Check(DashParser.ParseDuration("PT0.5S") == 0.5 && DashParser.ParseDuration("P1DT1H") == 90000 && DashParser.ParseDuration("PT90M") == 5400, "other forms");
        foreach (var bad in new[] { null, "", "P", "PT", "1H", "PT-5S", "PTxS", "PT1H2", new string('P', 500) }) Check(DashParser.ParseDuration(bad) is null, "bad duration: " + bad);
        Check(DashParser.Expand("a/$RepresentationID$/$Number%05d$-$Time$-$Bandwidth%08d$.m4s", "v1", 1234, 7, 99) == "a/v1/00007-99-00001234.m4s", "expansion");
        Check(DashParser.Expand("cost$$5/$Number%03d$", "r", 1, 5, 0) == "cost$5/005", "$$ escape");
        Check(DashParser.Expand("n$Number%1d$", "r", 1, 123, 0) == "n123", "short width does not truncate");
        Check(DashParser.Expand("plain.m4s", "r", 1, 1, 1) == "plain.m4s", "no tokens");
        return Task.CompletedTask;
    }

    public static Task Selection()
    {
        var manifest = new StreamManifest
        {
            Kind = StreamKind.Hls, Duration = TimeSpan.FromSeconds(100),
            Variants = new[] { new VideoVariant("v0", 1000, 640, 360, 30, null, "g") { HasOwnAudio = false }, new VideoVariant("v1", 5000, 1280, 720, 30, null, "g") { HasOwnAudio = false }, new VideoVariant("v2", 100, 0, 0, 0, null, null) { HasOwnAudio = true } },
            AudioTracks = new[] { new AudioTrack("a0", "en", "English", 0, null, "g", false), new AudioTrack("a1", "de", "Deutsch", 0, null, "g", true), new AudioTrack("a2", "fr", "Francais", 0, null, "other", true) }
        };
        var (v, a) = StreamSelection.Choose(manifest, null, null);
        Check(v!.Id == "v1" && a!.Id == "a1", "best quality and the default audio of its group");
        (v, a) = StreamSelection.Choose(manifest, "v0", "a0");
        Check(v!.Id == "v0" && a!.Id == "a0", "explicit choice");
        (v, a) = StreamSelection.Choose(manifest, "v2", null);
        Check(a is null, "self contained variant has no separate audio");
        (v, a) = StreamSelection.Choose(manifest, "v1", StreamSelection.NoAudio);
        Check(a is null, "none = no audio");
        foreach (var (vid, aid) in new[] { ("zzz", (string?)null), ("v0", "zzz") })
        {
            bool threw = false; try { StreamSelection.Choose(manifest, vid, aid); } catch (StreamException e) { threw = e.Error == StreamError.Unsupported; }
            Check(threw, "unknown id refused");
        }
        Check(manifest.EstimateBytes(manifest.Variants[1], null) == 5000L * 100 / 8, "estimate");
        Check(manifest.AudioFor(manifest.Variants[1]).Count() == 2 && !manifest.AudioFor(manifest.Variants[2]).Any(), "audio per variant");
        return Task.CompletedTask;
    }

    public static Task Sniffer()
    {
        byte[] B(string s) => Encoding.UTF8.GetBytes(s);
        Check(StreamSniffer.Detect(B("#EXTM3U\n#EXT-X-VERSION:3\n")) == StreamKind.Hls, "hls");
        Check(StreamSniffer.Detect(B("\uFEFF \r\n#EXTM3U\r\n#EXT-X-STREAM-INF:BANDWIDTH=1\r\n")) == StreamKind.Hls, "hls with BOM and blank lines");
        Check(StreamSniffer.Detect(B("#EXTM3U\n#EXTINF:123,Artist - Title\nsong.mp3\n")) == StreamKind.None, "a music playlist is not a stream");
        Check(StreamSniffer.Detect(B("<?xml version=\"1.0\"?><!-- c --><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\">")) == StreamKind.Dash, "dash");
        Check(StreamSniffer.Detect(B("<html><body>#EXTM3U</body></html>")) == StreamKind.None && StreamSniffer.Detect(B("<MPD>not dash</MPD>")) == StreamKind.None, "pages and look-alikes");
        Check(StreamSniffer.Detect(Array.Empty<byte>()) == StreamKind.None && StreamSniffer.Detect(new byte[] { 0x47, 0x40, 0x00 }) == StreamKind.None, "empty and binary");
        return Task.CompletedTask;
    }

    public static Task Fuzz()
    {
        var rnd = new Random(1606);
        var seeds = new List<string>
        {
            Join("#EXTM3U", "#EXT-X-VERSION:7", "#EXT-X-TARGETDURATION:4", "#EXT-X-MEDIA-SEQUENCE:3", "#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\",IV=0x01", "#EXT-X-MAP:URI=\"i.mp4\",BYTERANGE=\"10@0\"", "#EXTINF:4.0,", "#EXT-X-BYTERANGE:100@10", "a.m4s", "#EXT-X-ENDLIST"),
            Join("#EXTM3U", "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"n\",URI=\"x.m3u8\"", "#EXT-X-STREAM-INF:BANDWIDTH=100,RESOLUTION=1x1,CODECS=\"a,b\",AUDIO=\"a\"", "v.m3u8"),
            $@"<MPD {Ns} type=""static"" mediaPresentationDuration=""PT1M""><Period><AdaptationSet mimeType=""video/mp4""><Representation id=""r"" bandwidth=""5""><SegmentTemplate timescale=""1000"" media=""$Number$.m4s"" initialization=""i.mp4""><SegmentTimeline><S t=""0"" d=""4000"" r=""3""/></SegmentTimeline></SegmentTemplate></Representation></AdaptationSet></Period></MPD>",
            $@"<MPD {Ns} type=""static"" mediaPresentationDuration=""PT1M""><Period><AdaptationSet mimeType=""audio/mp4""><Representation id=""r"" bandwidth=""5""><BaseURL>a.mp4</BaseURL><SegmentList duration=""2""><SegmentURL media=""a""/></SegmentList></Representation></AdaptationSet></Period></MPD>"
        };
        var clock = Stopwatch.StartNew(); var before = GC.GetTotalMemory(true); int cases = 0, accepted = 0;
        var baseUri = new Uri("https://f.example/p/index.m3u8");
        void Feed(string s)
        {
            cases++;
            var h = HlsParser.Parse(s, baseUri, true); var d = DashParser.Parse(s, baseUri);
            if (h.Ok == (h.Error is not null) || d.Ok == (d.Error is not null)) throw new Exception("a parser result must carry exactly one of value and error");
            if (h.Ok || d.Ok) accepted++;
            if (d.Ok) { try { d.Value!.BuildPlan(null, null); } catch (StreamException) { } }
        }
        foreach (var seed in seeds)
        {
            for (int cut = 0; cut <= seed.Length; cut += Math.Max(1, seed.Length / 60)) Feed(seed[..cut]);       // truncations
            for (int i = 0; i < 400; i++)                                                                            // byte flips, insertions, deletions
            {
                var chars = seed.ToCharArray(); var sb = new StringBuilder(seed);
                int edits = rnd.Next(1, 6);
                for (int e = 0; e < edits && sb.Length > 0; e++)
                {
                    int pos = rnd.Next(sb.Length);
                    switch (rnd.Next(3))
                    {
                        case 0: sb[pos] = (char)rnd.Next(1, 256); break;
                        case 1: sb.Insert(pos, (char)rnd.Next(32, 127)); break;
                        default: sb.Remove(pos, 1); break;
                    }
                }
                Feed(sb.ToString());
            }
        }
        for (int i = 0; i < 300; i++)                                                                                // random bytes, with a believable start
        {
            var bytes = new byte[rnd.Next(0, 4000)]; rnd.NextBytes(bytes);
            var prefix = i % 3 == 0 ? "#EXTM3U\n" : i % 3 == 1 ? "<MPD " + Ns + ">" : "";
            Feed(prefix + Encoding.Latin1.GetString(bytes));
        }
        Check(clock.Elapsed < TimeSpan.FromSeconds(60), $"fuzz took {clock.Elapsed}");
        var grown = GC.GetTotalMemory(true) - before;
        Check(grown < 200L * 1024 * 1024, "memory grew by " + grown);
        Console.WriteLine($"    fuzz: {cases} inputs, {accepted} still parsed, {clock.ElapsedMilliseconds} ms");
        return Task.CompletedTask;
    }
}
