using System.Diagnostics;
using System.Text;
using NDM.Core;
using static NDM.Tests.StreamKit;

namespace NDM.Tests;

using P = (string Download, string State);

/// <summary>1.14.0: subtitle files a player loads on its own. WebVTT to SubRip table, captured-file rules, the engine fetching them with the stream's headers (size cap, scheme, de-duplication),
/// and a real embed with the bundled ffmpeg verified by ffprobe. The HLS fixture is generated from a test pattern; nothing leaves this computer.</summary>
internal static class V1140Tests
{
    private const string Vtt = "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\nNOTE made for the NeoDM test\n\n1\n00:00:00.500 --> 00:00:01.500 align:start position:10%\nHello <b>there</b>, fixture\n\n2\n00:00:02.000 --> 00:00:03.000\nSecond cue\nwith two lines\n";
    private const string ExpectedSrt = "1\n00:00:00,500 --> 00:00:01,500\nHello there, fixture\n\n2\n00:00:02,000 --> 00:00:03,000\nSecond cue\nwith two lines\n\n";

    public static Task Conversion()
    {
        var table = new (string Name, string Input, string Expected)[]
        {
            ("full file: header map, NOTE, ids, cue settings, tags, two lines", Vtt, ExpectedSrt),
            ("byte order mark and CRLF", "\uFEFFWEBVTT\r\n\r\n00:01.000 --> 00:02.000\r\nHi\r\n", "1\n00:01.000".Replace("00:01.000", "00:00:01,000") + " --> 00:00:02,000\nHi\n\n"),
            ("STYLE and REGION blocks are not cues", "WEBVTT\n\nSTYLE\n::cue { color: red }\n\nREGION\nid:r1\n\n00:00:01.000 --> 00:00:02.000\nOnly this\n", "1\n00:00:01,000 --> 00:00:02,000\nOnly this\n\n"),
            ("voice, class and timestamp tags and entities", "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v Bob>Tom &amp; <c.yellow>Jerry</c> <00:00:01.500>&lt;ok&gt; &quot;q&quot; it&#39;s\n", "1\n00:00:01,000 --> 00:00:02,000\nTom & Jerry <ok> \"q\" it's\n\n"),
            ("whitespace-only separator lines still separate cues", "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nA\n   \n00:00:03.000 --> 00:00:04.000\nB\n", "1\n00:00:01,000 --> 00:00:02,000\nA\n\n2\n00:00:03,000 --> 00:00:04,000\nB\n\n"),
            ("an empty cue is dropped", "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<i></i>\n\n00:00:03.000 --> 00:00:04.000\nKept\n", "1\n00:00:03,000 --> 00:00:04,000\nKept\n\n"),
            ("hours over 9 and short milliseconds", "WEBVTT\n\n10:00:01.5 --> 10:00:02.25\nLate\n", "1\n10:00:01,500 --> 10:00:02,250\nLate\n\n"),
            ("a SubRip file keeps its cues and gets clean numbering", "5\n00:00:01,000 --> 00:00:02,000\nOne\n\n9\n00:00:03,000 --> 00:00:04,000\n<i>Two</i>\n", "1\n00:00:01,000 --> 00:00:02,000\nOne\n\n2\n00:00:03,000 --> 00:00:04,000\nTwo\n\n"),
            ("a broken time line is skipped, the next cue survives", "WEBVTT\n\nbad --> worse\nx\n\n00:00:01.000 --> 00:00:02.000\nGood\n", "1\n00:00:01,000 --> 00:00:02,000\nGood\n\n"),
        };
        foreach (var (name, input, expected) in table) { var actual = SubtitleRules.ToSrt(input); Ensure(actual == expected, name + ": got " + actual.Replace("\n", "\\n")); }
        Ensure(SubtitleRules.ToSrt("WEBVTT\n\n") == "" && SubtitleRules.ToSrt("") == "" && SubtitleRules.ToSrt("<html>not subtitles</html>") == "", "no cue gives empty text");
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nمرحبا\n")).ToArray();
        Ensure(SubtitleRules.ToSrt(SubtitleRules.DecodeText(utf16)).Contains("مرحبا"), "UTF-16 with a byte order mark");
        Ensure(SubtitleRules.DecodeText(new byte[] { 0xEF, 0xBB, 0xBF, 0x41 }) == "A", "UTF-8 byte order mark dropped");
        Ensure(SubtitleRules.DecodeText(new byte[] { 0x63, 0x61, 0x66, 0xE9 }) == "caf\u00e9", "not UTF-8: Latin-1 (Windows-1252 for these letters)");
        Ensure(SubtitleRules.DecodeText(Encoding.UTF8.GetBytes("caf\u00e9")) == "caf\u00e9", "UTF-8 text unchanged");
        return Task.CompletedTask;
    }

    public static Task CapturedRules()
    {
        Ensure(CapturedSubtitle.TryCreate("https://cdn.example/s/en.vtt", "en", "English", "vtt") is { Supported: true, Language: "en", Label: "English" } c && c.DisplayName == "English", "valid entry, name from the code");
        Ensure(CapturedSubtitle.TryCreate("https://cdn.example/s/x.vtt", "", "", "vtt") is { Language: "", Label: "Subtitles" } u && u.DisplayName == "Subtitles", "unknown language is called Subtitles");
        Ensure(CapturedSubtitle.TryCreate("https://cdn.example/s/x.ass", "en", "x", "ass") is { Supported: false }, "ass is kept but not offered");
        foreach (var bad in new[] { "ftp://h/a.vtt", "file:///c:/a.vtt", "javascript:alert(1)", "https://u:p@h/a.vtt", "https://h/a\u0000.vtt", "not a url", "", "https://h/" + new string('a', 2100) + ".vtt" })
            Ensure(CapturedSubtitle.TryCreate(bad, "en", "x", "vtt") is null, "refused: " + (bad.Length > 30 ? bad[..30] : bad));
        Ensure(CapturedSubtitle.TryCreate("https://h/a.vtt", "en", "x", "exe") is null && CapturedSubtitle.TryCreate("https://h/a.vtt", "en", "x", null) is null, "unknown format refused");
        Ensure(CapturedSubtitle.TryCreate("https://h/a.vtt", "e n!", "x", "vtt")!.Language == "", "a language that is not a plain code is dropped");
        Ensure(new SubtitleRequest(SubtitleMode.Embed, new[] { new SubtitleChoice("x0", false, "https://h/a.vtt", "en", "English") }).IsValid, "a choice with a file address is a valid request");
        Ensure(!new SubtitleRequest(SubtitleMode.Embed, new[] { new SubtitleChoice("x0", false, "file:///c:/a.vtt") }).IsValid, "a file:// address is an invalid request");
        Ensure(!new SubtitleRequest(SubtitleMode.Embed, new[] { new SubtitleChoice("x0", false, "https://h/" + new string('a', 3000)) }).IsValid, "a very long address is an invalid request");
        var round = System.Text.Json.JsonSerializer.Deserialize<SubtitleRequest>(System.Text.Json.JsonSerializer.Serialize(new SubtitleRequest(SubtitleMode.Separate, new[] { new SubtitleChoice("x0", false, "https://h/a.vtt", "en", "English") })))!;
        Ensure(round.Choices[0].Url == "https://h/a.vtt" && round.Choices[0].Language == "en" && round.Mode == SubtitleMode.Separate, "the request survives saving and reading (restart)");
        var old = System.Text.Json.JsonSerializer.Deserialize<SubtitleRequest>("{\"Mode\":0,\"Choices\":[{\"Code\":\"s0\",\"Auto\":false}]}")!;
        Ensure(old.Choices[0].Code == "s0" && old.Choices[0].Url is null && old.IsValid, "a request saved by 1.13 still reads");
        return Task.CompletedTask;
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Directory.Build.props"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("repository root not found");
    }
    internal static string ToolsDir => Environment.GetEnvironmentVariable("NEODM_TOOLS_DIR") is { Length: > 0 } e ? e : Path.Combine(RepoRoot(), "dist", "app", "tools");
    private static string Ff(string name) => Path.Combine(ToolsDir, "ffmpeg", name);

    private static (int Exit, string Out) Run(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!; var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60000)) { p.Kill(true); throw new TimeoutException(exe); }
        return (p.ExitCode, o.Result + e.Result);
    }

    /// <summary>A 4 second HLS stream (two segments) made by the bundled ffmpeg from a generated test pattern, plus a separate WebVTT file the playlist does not list.</summary>
    private static StreamServer RealStream(string scratch, string vtt = Vtt)
    {
        var dir = Path.Combine(scratch, "hls"); Directory.CreateDirectory(dir);
        var r = Run(Ff("ffmpeg.exe"), "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=22050", "-t", "4",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "10", "-c:a", "aac", "-b:a", "32k", "-f", "hls", "-hls_time", "2", "-hls_playlist_type", "vod", "-hls_segment_filename", Path.Combine(dir, "seg%d.ts"), Path.Combine(dir, "index.m3u8"));
        Ensure(r.Exit == 0, "ffmpeg made the fixture stream: " + r.Out);
        var s = new StreamServer();
        // ffmpeg writes 2.000 for each segment but the encoded audio runs a few frames longer (4.09 s in all); the playlist is made to say what the files hold, as a real packager would
        foreach (var f in Directory.GetFiles(dir)) s.Add("/real/" + Path.GetFileName(f), f.EndsWith(".m3u8") ? Encoding.UTF8.GetBytes(File.ReadAllText(f).Replace("#EXTINF:2.000000,", "#EXTINF:2.045000,")) : File.ReadAllBytes(f), f.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t");
        s.Add("/real/subs/en.vtt", vtt, "text/vtt");
        return s;
    }

    private static ToolManager RealTools(string state) => new(state, bundleDirectory: ToolsDir);
    private static bool HaveRealTools => File.Exists(Ff("ffmpeg.exe")) && File.Exists(Ff("ffprobe.exe"));

    private static DownloadRequest Req(StreamServer s, string name, SubtitleMode mode, bool mp4, params SubtitleChoice[] choices) =>
        new(s.Url("/real/index.m3u8"), name, "https://page.example/watch?v=1", "sid=abc123", UserAgent: "NeoTest/1.0 (fixture)", AcceptLanguage: "en-GB", StreamKind: StreamKind.Hls, PreferMp4: mp4, Subtitles: new SubtitleRequest(mode, choices));
    private static SubtitleChoice Choice(StreamServer s, string id = "x0", string path = "/real/subs/en.vtt", string lang = "en", string label = "English") => new(id, false, s.Url(path), lang, label);

    public static async Task SeparateFileJobs(P paths)
    {
        using var server = new StreamServer(); using var manager = NewManager(paths);
        var pieces = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n");
        for (int i = 0; i < 2; i++) { var data = Blob(900 + i, 3000); data[0] = 0x47; server.Add($"/real/s{i}.ts", data, "video/mp2t"); pieces.Append("#EXTINF:2.0,\ns").Append(i).Append(".ts\n"); }
        pieces.Append("#EXT-X-ENDLIST\n"); server.Add("/real/index.m3u8", pieces.ToString());
        server.Add("/real/subs/en.vtt", Vtt, "text/vtt");
        server.Add("/real/subs/big.vtt", "WEBVTT\n\n" + string.Concat(Enumerable.Range(0, 60000).Select(i => $"{i / 3600:00}:{i / 60 % 60:00}:{i % 60:00}.000 --> {i / 3600:00}:{i / 60 % 60:00}:{i % 60:00}.900\nCue number {i}\n\n")), "text/vtt");
        server.Add("/real/subs/ok.vtt", "WEBVTT\n\n" + string.Concat(Enumerable.Range(0, 20000).Select(i => $"{i / 3600:00}:{i / 60 % 60:00}:{i % 60:00}.000 --> {i / 3600:00}:{i / 60 % 60:00}:{i % 60:00}.900\nCue number {i}\n\n")), "text/vtt");
        Ensure(server.Url("/real/subs/big.vtt").Length > 0, "fixture ready");

        // 1. a separate file the playlist never lists: fetched with the stream's own headers, converted, saved next to the video
        var job = await manager.AddStreamAsync(Req(server, "movie", SubtitleMode.Separate, false, Choice(server)));
        var done = await Succeed(manager, job.Id);
        var srt = Path.Combine(paths.Download, "movie.en.srt");
        Ensure(File.Exists(done.FilePath) && File.Exists(srt) && done.AdditionalFiles.Count == 1 && done.StreamNote is null, "separate mode: the video and movie.en.srt");
        Ensure(File.ReadAllText(srt) == ExpectedSrt, "the .srt is the converted file: " + File.ReadAllText(srt).Replace("\n", "\\n"));
        var seen = server.For("/real/subs/en.vtt").ToList();
        Ensure(seen.Count == 1 && seen[0].Referer == "https://page.example/watch?v=1" && seen[0].UserAgent == "NeoTest/1.0 (fixture)" && seen[0].AcceptLanguage == "en-GB" && seen[0].Cookie == "sid=abc123", "the subtitle request carries the stream's referer, user agent, language and cookie: " + string.Join("|", seen.Select(s => s.Referer + ";" + s.UserAgent + ";" + s.Cookie)));

        // 2. size cap: about 2 MiB is read, more is refused and only that file is lost
        var bigBytes = server.Url("/real/subs/big.vtt"); var okBytes = server.Url("/real/subs/ok.vtt");
        using (var http = new HttpClient()) { Ensure((await http.GetByteArrayAsync(bigBytes)).Length > SubtitleRules.MaxSeparateBytes && (await http.GetByteArrayAsync(okBytes)).Length < SubtitleRules.MaxSeparateBytes, "the fixture files sit on both sides of the cap"); }
        var capped = await Succeed(manager, (await manager.AddStreamAsync(Req(server, "capped", SubtitleMode.Separate, false, new SubtitleChoice("x0", false, bigBytes, "en", "English"), new SubtitleChoice("x1", false, okBytes, "fr", "French")))).Id);
        Ensure(!File.Exists(Path.Combine(paths.Download, "capped.en.srt")) && File.Exists(Path.Combine(paths.Download, "capped.fr.srt")) && capped.StreamNote == StreamCodes.SubtitlesFailed && File.Exists(capped.FilePath), "over the cap: that file is dropped with a note, the other file and the video stay");

        // 3. a failing file is a note, not a failed video
        server.Fail = (path, _) => path == "/real/subs/en.vtt" ? 404 : null;
        var missing = await Succeed(manager, (await manager.AddStreamAsync(Req(server, "missing", SubtitleMode.Separate, false, Choice(server)))).Id);
        server.Fail = null;
        Ensure(missing.StreamNote == StreamCodes.SubtitlesFailed && File.Exists(missing.FilePath) && !File.Exists(Path.Combine(paths.Download, "missing.en.srt")), "404 on the subtitle file: a note, the video is kept");

        // 4. a file:// address cannot even start a request
        bool refused = false;
        try { await manager.AddStreamAsync(Req(server, "local", SubtitleMode.Separate, false, new SubtitleChoice("x0", false, "file:///c:/windows/win.ini", "en", "English"))); } catch (ArgumentException) { refused = true; }
        Ensure(refused, "a request with a file:// subtitle address is refused");

        // 5. the playlist already lists the same address: one file, fetched once
        var master = new StreamServer();
        try
        {
            foreach (var p in new[] { "/real/s0.ts", "/real/s1.ts" }) master.Add(p, Blob(5, 3000).Select((b, i) => i == 0 ? (byte)0x47 : b).ToArray(), "video/mp2t");
            master.Add("/real/v.m3u8", "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:2.0,\ns0.ts\n#EXTINF:2.0,\ns1.ts\n#EXT-X-ENDLIST\n");
            master.Add("/real/index.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"en\",URI=\"subs/en.vtt\"\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=160x120,SUBTITLES=\"subs\"\nv.m3u8\n");
            master.Add("/real/subs/en.vtt", Vtt, "text/vtt");
            var listed = await Succeed(manager, (await manager.AddStreamAsync(Req(master, "dup", SubtitleMode.Separate, false, new SubtitleChoice("s0"), Choice(master)))).Id);
            Ensure(master.Count("/real/subs/en.vtt") == 1 && listed.AdditionalFiles.Count == 1, "the same address listed twice is fetched once and saved once: " + master.Count("/real/subs/en.vtt"));
        }
        finally { master.Dispose(); }
    }

    public static async Task RealEmbed(P paths)
    {
        if (!HaveRealTools) { Console.WriteLine("SKIP real embed: the bundled ffmpeg/ffprobe are not in " + ToolsDir + " (run build.ps1 first)"); return; }
        using var server = RealStream(paths.State);
        using var manager = NewManager(paths, tools: RealTools(paths.State));
        // MP4 through the engine: embedded as mov_text, then verified with ffprobe, cue text read back out of the finished file
        var done = await Succeed(manager, (await manager.AddStreamAsync(Req(server, "film", SubtitleMode.Embed, true, Choice(server)))).Id, 90);
        Ensure(done.FileName == "film.mp4" && done.AdditionalFiles.Count == 0 && done.StreamNote is null, "embed into MP4: one file, no note: " + Describe(done) + " note=" + done.StreamNote + " extra=" + done.AdditionalFiles.Count);
        var (code, probe) = Run(Ff("ffprobe.exe"), "-v", "error", "-select_streams", "s", "-show_entries", "stream=codec_name:stream_tags=language,title", "-of", "default=nw=1", done.FilePath);
        Ensure(code == 0 && probe.Contains("codec_name=mov_text") && probe.Contains("TAG:language=eng"), "ffprobe: a mov_text subtitle stream with language eng: " + probe.Replace("\r\n", "|"));
        var (c2, cues) = Run(Ff("ffmpeg.exe"), "-v", "error", "-i", done.FilePath, "-map", "0:s:0", "-f", "srt", "-");
        Ensure(c2 == 0 && cues.Contains("Hello there, fixture") && cues.Contains("Second cue") && cues.Contains("00:00:00,500 --> 00:00:01,500"), "cue text and timing read back from the MP4: " + cues.Replace("\n", "|"));
        var (c3, video) = Run(Ff("ffprobe.exe"), "-v", "error", "-select_streams", "v", "-show_entries", "stream=codec_name", "-of", "csv=p=0", done.FilePath);
        Ensure(c3 == 0 && video.Trim() == "h264", "the picture is still there");

        // the finished file has no text track: the engine falls back to separate files with a message
        manager.SubtitleTrackCounter = (_, _, _) => Task.FromResult<int?>(0);
        var fell = await Succeed(manager, (await manager.AddStreamAsync(Req(server, "fallback", SubtitleMode.Embed, true, Choice(server)))).Id, 90);
        Ensure(fell.StreamNote == StreamCodes.SubtitlesEmbedFailed && File.Exists(Path.Combine(paths.Download, "fallback.en.srt")) && File.Exists(fell.FilePath), "no text track written: separate .srt kept with the embed-failed note");
        manager.SubtitleTrackCounter = YoutubeService.CountSubtitleTracksAsync;

        // MKV and WebM: the same arguments the engine uses, run on a real file (an HLS stream is never written as MKV by the engine, so this is rule-level proof)
        var mp4 = done.FilePath; var srtPath = Path.Combine(paths.State, "en.srt"); File.WriteAllText(srtPath, ExpectedSrt, new UTF8Encoding(false));
        var files = new List<SubtitleFile> { new(srtPath, "en") };
        var mkv = Path.Combine(paths.State, "out.mkv");
        var plain = Path.Combine(paths.State, "plain.mp4"); Run(Ff("ffmpeg.exe"), "-y", "-v", "error", "-i", mp4, "-map", "0:v", "-map", "0:a", "-c", "copy", plain);
        var r1 = Run(Ff("ffmpeg.exe"), SubtitleRules.EmbedArguments(plain, files, mkv, "mkv").ToArray());
        var (_, mkvProbe) = Run(Ff("ffprobe.exe"), "-v", "error", "-select_streams", "s", "-show_entries", "stream=codec_name:stream_tags=title", "-of", "csv=p=0", mkv);
        var (_, mkvCues) = Run(Ff("ffmpeg.exe"), "-v", "error", "-i", mkv, "-map", "0:s:0", "-f", "srt", "-");
        Ensure(r1.Exit == 0 && mkvProbe.Trim().StartsWith("subrip") && mkvProbe.Contains("English") && mkvCues.Contains("Second cue"), "MKV embed keeps SRT text: " + mkvProbe.Trim() + " / " + r1.Out);
        Ensure(SubtitleRules.OutputExtension("webm", SubtitleMode.Embed) == "mkv" && SubtitleRules.OutputExtension("webm", SubtitleMode.Separate) == "webm" && SubtitleRules.EmbedChangesContainer("webm") && !SubtitleRules.EmbedChangesContainer("mp4"), "WebM becomes MKV when embedding; separate files keep WebM");
        Ensure(SubtitleRules.EmbedArguments(plain, files, mkv, "mp4").Contains("mov_text") && SubtitleRules.EmbedArguments(plain, files, mkv, "mkv").Contains("srt"), "container rule: MP4 mov_text, MKV srt");
    }

    public static async Task NoToolFallsBack(P paths)
    {
        using var server = new StreamServer(); using var manager = NewManager(paths); // no ffmpeg available
        var pieces = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n");
        for (int i = 0; i < 2; i++) { var data = Blob(70 + i, 3000); data[0] = 0x47; server.Add($"/real/s{i}.ts", data, "video/mp2t"); pieces.Append("#EXTINF:2.0,\ns").Append(i).Append(".ts\n"); }
        pieces.Append("#EXT-X-ENDLIST\n"); server.Add("/real/index.m3u8", pieces.ToString()); server.Add("/real/subs/en.vtt", Vtt, "text/vtt");
        var done = await Succeed(manager, (await manager.AddStreamAsync(Req(server, "ts", SubtitleMode.Embed, false, Choice(server)))).Id);
        Ensure(done.StreamNote == StreamCodes.SubtitlesContainer && File.Exists(Path.Combine(paths.Download, "ts.en.srt")), "a transport stream cannot hold subtitles: separate .srt with the container note");
    }
}
