using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using NDM.Core;
using static NDM.Tests.StreamKit;

namespace NDM.Tests;

internal static class FakeYoutube
{
    public static bool IsFakeProcess => string.Equals(Path.GetFileName(Environment.ProcessPath), "yt-dlp.exe", StringComparison.OrdinalIgnoreCase);
    // Shape of the two `--print` lines the service asks for: an info object, then the format array. Fields not requested never appear.
    public const string Info = """
    {"title":"../Sample\u202efile", "availability":"public", "is_live":false, "was_live":false, "has_drm":false}
    """;
    public const string Formats = """
    [
      {"format_id":"sb3","ext":"mhtml","height":27,"vcodec":"none","acodec":"none"},
      {"format_id":"18","ext":"mp4","height":360,"vcodec":"avc1","acodec":"aac"},
      {"format_id":"137","ext":"mp4","height":1080,"vcodec":"avc1","acodec":"none"},
      {"format_id":"140","ext":"m4a","abr":128,"vcodec":"none","acodec":"aac"},
      {"format_id":"251","ext":"webm","abr":160,"vcodec":"none","acodec":"opus"},
      {"format_id":"18;touch BAD","ext":"mp4","vcodec":"avc1","acodec":"aac"},
      {"format_id":"999","ext":"../bad.exe","vcodec":"avc1","acodec":"aac"},
      {"format_id":"998","ext":"mp4","has_drm":true,"vcodec":"avc1","acodec":"aac"}]
    """;
    private static string OneLine(string json) => string.Concat(json.Split('\n').Select(l => l.Trim()));
    public static string Metadata => OneLine(Info) + "\n" + OneLine(Formats);
    /// <summary>200 formats: storyboards (mhtml, no codecs), progressive, video-only and audio-only, ids 1..N.</summary>
    public static string ManyFormats()
    {
        var items = new List<string>();
        for (int i = 0; i < 200; i++)
        {
            items.Add((i % 4) switch
            {
                0 => $"{{\"format_id\":\"sb{i}\",\"ext\":\"mhtml\",\"height\":{27 + i},\"vcodec\":\"none\",\"acodec\":\"none\"}}",
                1 => $"{{\"format_id\":\"v{i}\",\"ext\":\"mp4\",\"height\":{144 + i},\"vcodec\":\"avc1\",\"acodec\":\"none\",\"filesize\":{1000 + i}}}",
                2 => $"{{\"format_id\":\"a{i}\",\"ext\":\"m4a\",\"abr\":{48 + i},\"vcodec\":\"none\",\"acodec\":\"aac\",\"filesize_approx\":{500 + i}}}",
                _ => $"{{\"format_id\":\"p{i}\",\"ext\":\"mp4\",\"height\":360,\"vcodec\":\"avc1\",\"acodec\":\"aac\"}}",
            });
        }
        return OneLine(Info) + "\n[" + string.Join(",", items) + "]";
    }
    /// <summary>JSON lines like the real flat listing: id, title, duration, channel, availability, thumbnails and the playlist fields. "n" entries, or the word "mixed" for a fixed set with private, deleted and bad lines.</summary>
    public static IEnumerable<string> PlaylistLines(string? spec)
    {
        if (spec == "mixed")
        {
            yield return "{\"id\": \"aaaaaaaaaaa\", \"title\": \"First video\", \"duration\": 61, \"channel\": \"Chan\", \"thumbnails\": [{\"url\": \"https://i.ytimg.com/vi/aaaaaaaaaaa/hqdefault.jpg?sqp=x\", \"height\": 94, \"width\": 168}, {\"url\": \"https://evil.example/x.jpg\", \"height\": 900, \"width\": 600}], \"playlist_title\": \"Fixture / list?\", \"playlist_count\": 5}";
            yield return "{\"id\": \"bbbbbbbbbbb\", \"title\": \"[Private video]\", \"duration\": null, \"availability\": \"private\", \"playlist_title\": \"Fixture / list?\", \"playlist_count\": 5}";
            yield return "{\"id\": \"ccccccccccc\", \"title\": \"[Deleted video]\", \"playlist_count\": 5}";
            yield return "not json at all";
            yield return "{\"id\": \"bad id\", \"title\": \"x\"}";
            yield return "{\"id\": \"ddddddddddd\", \"title\": \"Members clip\", \"availability\": \"subscriber_only\", \"duration\": 30}";
            yield break;
        }
        int n = int.TryParse(spec, out var parsed) ? parsed : 3;
        for (int i = 0; i < n; i++)
            yield return $"{{\"id\": \"v{i:D10}\", \"title\": \"Video number {i}\", \"duration\": {60 + i}, \"channel\": \"Fixture channel\", \"playlist_title\": \"Fixture list\", \"playlist_count\": {n}}}";
    }
    public static int Run(string[] args)
    {
        if (args.Contains("--yt-fake-child")) { Thread.Sleep(60000); return 0; }
        if (Environment.GetEnvironmentVariable("NDM_YT_TEST_LOG") is { } log) File.AppendAllText(log, JsonSerializer.Serialize(args) + "\n");
        var mode = Environment.GetEnvironmentVariable("NDM_YT_TEST_MODE");
        if (mode == "oversize") { Console.Write(new string('X', 5 * 1024 * 1024)); return 0; }
        if (mode == "restricted") { Console.Error.WriteLine("Sign in is required. secret-cookie=must-not-persist"); return 1; }
        if (mode == "unknownfail") { Console.Error.WriteLine("WARNING: ffmpeg not found"); Console.Error.WriteLine("ERROR: [youtube] abc: \u202eodd\tfailure\u0007 " + new string('z', 400)); Console.Error.WriteLine("WARNING: trailing note"); return 1; }
        if (mode == "hang" && args.Contains("--print")) { Thread.Sleep(60000); return 0; }
        if (mode == "rate" && args.Contains("--print")) { Console.Error.WriteLine("ERROR: [youtube] abc: HTTP Error 429: Too Many Requests"); return 1; }
        if (args.Contains("--flat-playlist"))
        {
            // 1.11.0: the playlist listing. --quiet --verbose puts the helper's progress lines on stderr; the entries are JSON lines on stdout.
            Console.Error.WriteLine("[debug] Command-line config: ['--flat-playlist']");
            Console.Error.WriteLine("[youtube:tab] Extracting URL: https://www.youtube.com/playlist?list=fixture");
            Console.Error.WriteLine("[youtube:tab] fixture: Downloading webpage");
            Console.Error.WriteLine("[youtube:tab] Playlist Fixture: Downloading 3 items of 3");
            foreach (var line in PlaylistLines(Environment.GetEnvironmentVariable("NDM_YT_PLAYLIST_COUNT"))) Console.WriteLine(line);
            return 0;
        }
        if (args.Contains("--print"))
        {
            if (args.Contains("--verbose"))
            {
                Console.Error.WriteLine("[debug] Command-line config: ['--verbose']");
                Console.Error.WriteLine("[youtube] Extracting URL: https://www.youtube.com/watch?v=aqz-KE-bpKQ");
                Console.Error.WriteLine("[youtube] aqz-KE-bpKQ: Downloading webpage");
                Console.Error.WriteLine("[youtube] [jsc:deno] Solving JS challenges using deno");
                Console.Error.WriteLine("[info] aqz-KE-bpKQ: Downloading 1 format(s): 401+258");
            }
            if (args.Contains(YoutubeService.SubtitleTemplate)) { Console.WriteLine(Environment.GetEnvironmentVariable("NDM_YT_SUBS") ?? "{}"); return 0; }
            // Real behaviour (yt-dlp 2026.10.26): a video rejected by --match-filters prints nothing and exits 0.
            if (mode == "filtered") return 0;
            if (mode == "malformed") { Console.WriteLine("this is not json"); Console.WriteLine("[]"); return 0; }
            if (mode == "linebomb") { Console.WriteLine(OneLine(Info)); Console.Write("[" + new string('1', 3 * 1024 * 1024) + "]\nextra\n"); return 0; }
            Console.WriteLine(mode == "many" ? ManyFormats() : Metadata); return 0;
        }
        if (args.Contains("--write-subs") || args.Contains("--write-auto-subs"))
        {
            // 1.13.0: the caption fetch is its own call (no --format). It writes media.<lang>.srt like the real tool after --convert-subs srt.
            var subMode = Environment.GetEnvironmentVariable("NDM_YT_SUBMODE");
            if (subMode == "fail") { Console.Error.WriteLine("ERROR: [youtube] abc: Unable to download video subtitles"); return 1; }
            if (subMode == "none") return 0;
            var subFolder = args[Array.IndexOf(args, "--paths") + 1];
            foreach (var lang in args[Array.IndexOf(args, "--sub-langs") + 1].Split(',')) File.WriteAllText(Path.Combine(subFolder, "media." + lang + ".srt"), "1\n00:00:01,000 --> 00:00:02,000\nHello " + lang + "\n\n");
            return 0;
        }
        var folder = args[Array.IndexOf(args, "--paths") + 1]; var format = args[Array.IndexOf(args, "--format") + 1];
        var ext = args.Contains("--extract-audio") ? "mp3" : format.Contains('+') ? (args.Contains("--merge-output-format") ? args[Array.IndexOf(args, "--merge-output-format") + 1] : "mkv") : format == "140" ? "m4a" : format == "251" ? "webm" : "mp4";
        if (mode is "slow" or "unknown")
        {
            using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--yt-fake-child" } })!;
            File.WriteAllText(Path.Combine(folder, "child.pid"), child.Id.ToString());
            for (int i = 0; i < 200; i++) { Console.WriteLine(mode == "unknown" ? $"NDM:18:downloading:{i * 32}:NA:NA:NA:NA" : $"NDM:18:downloading:{i * 32}:NA:6400:NA:NA"); Thread.Sleep(50); }
        }
        Console.WriteLine($"NDM:{format.Split('+')[0]}:finished:4096:4096:NA:NA:NA");
        if (mode == "missing") return 0;
        File.WriteAllBytes(Path.Combine(folder, "media." + ext), Enumerable.Repeat((byte)42, 4096).ToArray());
        return 0;
    }
}

internal static class YoutubeTests
{
    private static Task Until(Func<bool> predicate) => WaitUntil(predicate, "YouTube fixture condition", 15000);
    public const string Url = "https://www.youtube.com/watch?v=aqz-KE-bpKQ";
    public static async Task<YoutubeService> Install(string state)
    {
        async Task<ToolManager> One(string name)
        {
            var bytes = File.ReadAllBytes(FakeTool.ExePath); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var pin = new ToolPin("https://tools.example.test/" + name + ".exe", "fixture", bytes.Length, hash, name + ".exe", bytes.Length, hash);
            var tool = new ToolManager(state, pin, handlerFactory: () => new FakeDownloadHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } }, toolName: name, rawExecutable: true);
            await tool.InstallAsync(); FakeTool.MakeRunnable(tool); return tool;
        }
        return new YoutubeService(state, await One("yt-dlp"), await One("deno"));
    }
    public static Task Rules()
    {
        foreach (var u in new[] { Url, "http://youtu.be/aqz-KE-bpKQ?t=1", "https://m.youtube.com/shorts/aqz-KE-bpKQ", "https://music.youtube.com/watch?v=aqz-KE-bpKQ&list=ignored", "https://www.youtube.com/embed/aqz-KE-bpKQ" }) Ensure(YoutubeUrls.Canonical(u) == Url, "canonical single page");
        foreach (var u in new[] { "https://youtube.com.evil.test/watch?v=aqz-KE-bpKQ", "https://youtube.com@evil.test/watch?v=aqz-KE-bpKQ", "file://youtube.com/watch?v=aqz-KE-bpKQ", "https://youtube.com:444/watch?v=aqz-KE-bpKQ", "https://youtube.com/playlist?list=x", "https://youtube.com/watch?v=aqz-KE-bpKQ&v=aqz-KE-bpKQ", "https://youtu.be/aqz-KE-bpKQ/a", "https://youtube.com/watch?v=aqz-KE-bpKQ%26x", "https://www.youtube.com/watch?v=aqz-KE-bpKQ\n--exec=bad" })
        { try { YoutubeUrls.Canonical(u); throw new Exception("accepted unsafe URL"); } catch (ArgumentException) { } }
        var args = YoutubeService.BaseArguments(Url + "&exec=touch", "C:/a b/deno.exe");
        Ensure(args[^1] == Url && args[^2] == "--" && args.Contains("--ignore-config") && args.Contains("--no-plugin-dirs") && args.Contains("--no-remote-components") && !args.Any(a => a.Contains("touch")), "fixed command args");
        foreach (var id in new[] { "1;cmd", "--exec", "../a", "1+2", "1/2", "$(evil)" })
        { try { new YoutubeSelection(id).Validate(); throw new Exception("accepted unsafe format"); } catch (ArgumentException) { } }
        return Task.CompletedTask;
    }
    public static Task Metadata()
    {
        var m = YoutubeService.ParseMetadata(FakeYoutube.Metadata);
        Ensure(m.Formats.Count == 4 && m.Formats.All(f => f.Id != "sb3") && !m.Title.Contains('/') && !m.Title.Contains('\u202e'), "bounded sanitized metadata");
        Ensure(YoutubeService.OutputExtension(new("137", "140"), m) == "mkv", "video plus audio");
        Ensure(YoutubeService.OutputExtension(new("251"), m) == "webm", "original audio no transcode");
        Ensure(YoutubeService.OutputExtension(new("140", Mp3: true), m) == "mp3", "audio conversion distinct");
        foreach (var s in new[] { new YoutubeSelection("137"), new YoutubeSelection("18", "140"), new YoutubeSelection("18", Mp3: true), new YoutubeSelection("137", "18"), new YoutubeSelection("missing") })
        { try { YoutubeService.OutputExtension(s, m); throw new Exception("accepted invalid selection"); } catch (YoutubeException) { } }
        foreach (var json in new[] { "{}", "not json", FakeYoutube.Metadata.Replace("\"availability\":\"public\"", "\"availability\":\"private\""), FakeYoutube.Metadata.Replace("\"is_live\":false", "\"is_live\":true"), "{\"entries\":[]}", "", FakeYoutube.Info, FakeYoutube.Info + "\n{}", FakeYoutube.Info + "\n[]\n[]", "[]\n" + FakeYoutube.Info }.Where(j => j.Length > 0))
        { try { YoutubeService.ParseMetadata(json); throw new Exception("accepted unsafe metadata"); } catch (YoutubeException e) { Ensure(e.Code is "youtube.metadata" or "youtube.unsupported" or "youtube.restricted", "unsafe metadata keeps a specific localized error: " + e.Code); } }
        string Code(string json) { try { YoutubeService.ParseMetadata(json); return "accepted"; } catch (YoutubeException e) { return e.Code; } }
        Ensure(Code(FakeYoutube.Metadata.Replace("\"is_live\":false", "\"is_live\":true")) == "youtube.unsupported", "live keeps the unsupported message");
        Ensure(Code(FakeYoutube.Metadata.Replace("\"was_live\":false", "\"was_live\":true")) == "youtube.unsupported", "finished live stream keeps the unsupported message");
        Ensure(Code(FakeYoutube.Metadata.Replace("\"has_drm\":false", "\"has_drm\":true")) == "youtube.unsupported", "DRM keeps the unsupported message");
        Ensure(Code(FakeYoutube.Metadata.Replace("\"availability\"", "\"is_upcoming\":true,\"availability\"")) == "youtube.unsupported", "upcoming keeps the unsupported message");
        Ensure(Code(FakeYoutube.Metadata.Replace("\"availability\"", "\"_type\":\"playlist\",\"availability\"")) == "youtube.unsupported" && Code(FakeYoutube.Metadata.Replace("\"availability\"", "\"_type\":\"video\",\"availability\"")) == "accepted", "playlist type is refused, video type accepted");
        Ensure(Code(FakeYoutube.Metadata.Replace("\"availability\":\"public\"", "\"availability\":\"needs_auth\"")) == "youtube.restricted" && Code(FakeYoutube.Metadata.Replace("\"public\"", "\"unlisted\"")) == "accepted", "restricted availability refused, unlisted accepted");
        Ensure(Code("") == "youtube.unsupported" && Code("\n \r\n") == "youtube.unsupported", "empty output (video rejected by the live/DRM match filter) maps to the unsupported message");
        Ensure(Code(new string('X', 4 * 1024 * 1024 + 1)) == "youtube.metadata" && Code(FakeYoutube.Info + "\n[" + new string('1', 70000) + "]\n").Length > 0, "oversize output is a metadata error");
        Ensure(Code(FakeYoutube.Info.Replace("\"title\":\"../Sample\\u202efile\"", "\"title\":\"" + new string('T', 70000) + "\"") + "\n[]") == "youtube.metadata", "oversize info line refused");
        var many = YoutubeService.ParseMetadata(FakeYoutube.ManyFormats());
        Ensure(many.Formats.Count == 150 && many.Formats.All(f => !f.Id.StartsWith("sb") && (f.Video || f.Audio) && f.Extension != "mhtml"), "200 formats with storyboards yield only the 150 usable formats");
        Ensure(many.Formats.Count(f => f.Video && f.Audio) == 50 && many.Formats.Count(f => f.Video && !f.Audio) == 50 && many.Formats.Count(f => f.Audio && !f.Video) == 50 && many.Formats.First(f => f.Id == "v1").Size == 1001 && many.Formats.First(f => f.Id == "a2").SizeEstimated, "mixed progressive, video-only and audio-only formats keep their kind and sizes");
        var formats = new YoutubeMetadata("fixture", new[] { new YoutubeFormat("137", "mp4", 1080, 0, true, false, 1000), new YoutubeFormat("140", "m4a", 0, 128, false, true, 200, true) });
        var tracker = new YoutubeProgressTracker(new("137", "140"), formats);
        Ensure(tracker.Snapshot().Total == 1200 && tracker.Snapshot().Estimated, "selected metadata sizes aggregate and retain estimate marking");
        var sample = tracker.Read("NDM:137:downloading:400:1000:NA:2:5")!;
        Ensure(sample.Downloaded == 400 && sample.Total == 1200 && sample.Phase == "video", "exact video bytes and second-track estimate");
        sample = tracker.Read("NDM:137:finished:1000:1000:NA:5:5")!;
        sample = tracker.Read("NDM:140:downloading:50:NA:200:1:4")!;
        Ensure(sample.Downloaded == 1050 && sample.Total == 1200 && sample.Estimated && sample.Phase == "audio", "second track does not reset first-track byte counter");
        sample = tracker.Read("NDM:140:finished:210:NA:200:4:4")!;
        Ensure(sample.Downloaded == 1210 && sample.Total == 1210 && !sample.Estimated && sample.Phase == "merging", "finished tracks become exact but merge remains a distinct phase");
        Ensure(tracker.Read("NDMPOST:Merger:started")!.Phase == "merging", "real postprocessing event maps to merge phase");
        var unknown = new YoutubeProgressTracker(new("18"), new("fixture", new[] { new YoutubeFormat("18", "mp4", 360, 128, true, true) }));
        Ensure(unknown.Read("NDM:18:downloading:100:NA:NA:NA:NA")!.Total == 0, "absent totals stay unknown");
        sample = unknown.Read("NDM:18:downloading:100:NA:NA:1:5")!;
        Ensure(sample.Total == 500 && sample.Estimated && sample.FragmentsDone == 1 && sample.FragmentsTotal == 5, "fragment-based byte estimate is explicitly estimated");
        Ensure(unknown.Read("untrusted output") is null && unknown.Read("NDM:evil:downloading:1:2:3:4:5") is null, "unselected format progress ignored");
        return Task.CompletedTask;
    }
    public static async Task UnknownProgress((string Download, string State) paths)
    {
        var service = await Install(paths.State); Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "unknown");
        try
        {
            using var manager = new DownloadManager(paths.Download, paths.State, new DownloadOptions { Youtube = service });
            var job = await manager.AddAsync(new(Url, "unknown", Youtube: new("18")));
            await Until(() => JobOf(manager, job.Id).DownloadedBytes > 0);
            var snapshot = JobOf(manager, job.Id);
            Ensure(snapshot.TotalBytes == 0 && snapshot.Progress == 0 && snapshot.Status == "Downloading", "unknown helper total is not reported as complete");
            manager.Pause(job.Id);
        }
        finally { Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", null); }
    }
    public static async Task ResumeIdentity((string Download, string State) paths)
    {
        var service = await Install(paths.State); var tools = new ToolManager(paths.State);
        var work = Path.Combine(paths.State, "identity");
        var first = await service.DownloadAsync(Url, new("18"), work, tools, null, (_, _) => { }, default);
        var same = await service.DownloadAsync(Url + "&list=ignored", new("18"), work, tools, null, (_, _) => { }, default);
        var changed = await service.DownloadAsync("https://www.youtube.com/watch?v=abcdefghijk", new("18"), work, tools, null, (_, _) => { }, default);
        var audio = await service.DownloadAsync(Url, new("140"), work, tools, null, (_, _) => { }, default);
        Ensure(first == same && first != changed && first != audio, "partial bytes bind to canonical page and exact selection");
        Ensure(Directory.GetDirectories(work).Length == 3, "different media never share the helper resume folder");
    }
    public static async Task Hashes((string Download, string State) paths)
    {
        var bundle = Path.Combine(paths.State, "bundle"); var directory = Path.Combine(bundle, "fixture"); Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "fixture.exe"); var fixtureBytes = new byte[] { 1, 2, 3 }; File.WriteAllBytes(executable, fixtureBytes);
        var fixtureHash = Convert.ToHexString(SHA256.HashData(fixtureBytes)).ToLowerInvariant();
        var fixturePin = new ToolPin("https://tools.example.test/fixture.exe", "fixture", 3, fixtureHash, "fixture.exe", 3, fixtureHash);
        bool networkCalled = false;
        var bundled = new ToolManager(paths.State, fixturePin, handlerFactory: () => { networkCalled = true; throw new Exception("bundle attempted network access"); }, toolName: "fixture", rawExecutable: true, bundleDirectory: bundle);
        Ensure(bundled.IsBundled && bundled.IsInstalled && bundled.GetVersion() == "fixture", "immutable bundle discovery needs no mutable installation manifest");
        using (var held = bundled.OpenVerified(requirePinned: true))
        {
            Ensure(held.Pinned && held.Path == executable, "installed-layout bundle verifies pinned executable through held handle");
            try { File.WriteAllBytes(executable, fixtureBytes); throw new Exception("bundle write succeeded during use"); } catch (IOException) { }
        }
        try { bundled.Uninstall(); throw new Exception("bundle removal succeeded"); } catch (ToolException) { Ensure(File.Exists(executable), "runtime Remove cannot delete bundled tools"); }
        File.WriteAllBytes(executable, new byte[] { 3, 2, 1 });
        try { bundled.OpenVerified(); throw new Exception("same-size modified bundle launched"); } catch (ToolException ex) { Ensure(ex.Code == ToolException.HashMismatch, "same-size bundle tampering is refused"); }
        File.Delete(executable);
        try { bundled.OpenVerified(); throw new Exception("missing bundle launched"); } catch (ToolException ex) { Ensure(ex.Code == ToolException.NotInstalled, "missing bundle fails clearly"); }
        try { await bundled.InstallAsync(); throw new Exception("runtime bundle provisioning allowed"); } catch (ToolException ex) { Ensure(ex.Code == ToolException.NotInstalled && !networkCalled, "missing bundle never downloads a runtime fallback"); }
        File.WriteAllBytes(executable, fixtureBytes);
        var library = Path.Combine(directory, "private.dll"); File.WriteAllBytes(library, fixtureBytes);
        var shared = new ToolManager(paths.State, fixturePin, toolName: "fixture", rawExecutable: true, bundleDirectory: bundle, dependencyPins: new[] { new ToolFilePin("private.dll", 3, fixtureHash) });
        using (shared.OpenVerified(requirePinned: true))
        {
            try { File.WriteAllBytes(library, fixtureBytes); throw new Exception("DLL write succeeded during use"); } catch (IOException) { }
            try { File.Delete(library); throw new Exception("DLL delete succeeded during use"); } catch (IOException) { }
        }
        File.WriteAllBytes(library, new byte[] { 3, 2, 1 });
        try { shared.OpenVerified(); throw new Exception("same-size changed DLL accepted"); } catch (ToolException ex) { Ensure(ex.Code == ToolException.HashMismatch, "same-size DLL tampering is refused before launch"); }
        File.Delete(library);
        try { shared.OpenVerified(); throw new Exception("missing DLL accepted"); } catch (ToolException ex) { Ensure(ex.Code == ToolException.NotInstalled, "missing DLL cannot fall back to PATH"); }
        File.WriteAllBytes(library, fixtureBytes); var unexpected = Path.Combine(directory, "unexpected.dll"); File.WriteAllBytes(unexpected, fixtureBytes);
        try { shared.OpenVerified(); throw new Exception("unlisted DLL accepted"); } catch (ToolException ex) { Ensure(ex.Code == ToolException.HashMismatch, "unlisted DLL is refused"); }
        File.Delete(unexpected);
        using (shared.OpenVerified()) { }
        var service = await Install(paths.State); Ensure(service.IsInstalled, "raw pins install");
        File.AppendAllText(service.Runtime.FfmpegPath, "changed");
        try { await service.InspectAsync(Url); throw new Exception("tampered runtime launched"); } catch (ToolException e) { Ensure(e.Code == ToolException.HashMismatch, "runtime hash checked before extractor"); }
        var bad = service.Extractor.Pin with { ExeSha256 = new string('0', 64) };
        var bytes = File.ReadAllBytes(FakeTool.ExePath);
        var invalid = new ToolManager(Path.Combine(paths.State, "bad"), bad, handlerFactory: () => new FakeDownloadHandler { Respond = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } }, toolName: "yt-dlp", rawExecutable: true);
        try { await invalid.InstallAsync(); throw new Exception("bad executable hash installed"); } catch (ToolException e) { Ensure(e.Code == ToolException.HashMismatch && !invalid.IsInstalled, "raw exe hash fails closed"); }
    }
    public static async Task Downloads((string Download, string State) paths)
    {
        var service = await Install(paths.State); var tools = await FakeTool.Install(paths.State);
        using var manager = new DownloadManager(paths.Download, paths.State, new DownloadOptions { Youtube = service, Tools = tools, MaxConcurrentDownloads = 1 });
        foreach (var select in new[] { new YoutubeSelection("18"), new YoutubeSelection("137", "140"), new YoutubeSelection("251"), new YoutubeSelection("140", Mp3: true) })
        {
            var job = await manager.AddAsync(new(Url, "../reserved.exe", TargetDirectory: paths.Download, Youtube: select));
            await Until(() => JobOf(manager, job.Id).Status is "Completed" or "Failed"); var result = JobOf(manager, job.Id);
            Ensure(result.Status == "Completed", "YouTube fixture completes: " + result.Error);
            Ensure(new FileInfo(result.FilePath).Length == 4096 && Path.GetDirectoryName(result.FilePath) == paths.Download && !result.FilePath.EndsWith(".exe"), "safe published output");
        }
        var paused = await manager.AddPausedAsync(new(Url + "&token=must-not-persist", "scheduled", Youtube: new("18")), DateTimeOffset.UtcNow.AddMinutes(30));
        Ensure(JobOf(manager, paused.Id).Status == "Paused", "atomic paused queue insertion");
        manager.UpdateUrl(paused.Id, Url + "&token=must-not-persist");
        Ensure(JobOf(manager, paused.Id).Url == Url && manager.RequestOf(paused.Id)?.Url == Url && !File.ReadAllText(Path.Combine(paths.State, "queue.json")).Contains("must-not-persist"), "engine canonicalizes both add and address updates before persistence");
        manager.Resume(paused.Id); Ensure(JobOf(manager, paused.Id).Status == "Scheduled", "schedule preserved");
        manager.Schedule(paused.Id, DateTimeOffset.UtcNow); await Until(() => JobOf(manager, paused.Id).Status == "Completed");
        Ensure(File.ReadAllText(Path.Combine(paths.State, "queue.json")).Contains("\"Youtube\""), "selection persists");
    }
    public static async Task CancelAndRestore((string Download, string State) paths)
    {
        var service = await Install(paths.State); Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "slow"); string id;
        try
        {
            using (var manager = new DownloadManager(paths.Download, paths.State, new DownloadOptions { Youtube = service, MaxConcurrentDownloads = 1 }))
            {
                var job = await manager.AddAsync(new(Url, "cancel", Youtube: new("18"))); id = job.Id;
                await Until(() => manager.Jobs.Single().DownloadedBytes > 0);
                var pidFile = Directory.GetFiles(paths.Download, "child.pid", SearchOption.AllDirectories).Single(); var childPid = int.Parse(File.ReadAllText(pidFile));
                var waiting = await manager.AddAsync(new(Url, "waiting", Youtube: new("18")));
                Ensure(JobOf(manager, waiting.Id).Status == "Queued" && JobOf(manager, waiting.Id).DownloadedBytes == 0, "helper obeys queue concurrency slot");
                manager.Pause(id); await Task.Delay(700);
                Ensure(JobOf(manager, id).Status == "Paused" && JobOf(manager, id).ResumeSupported is null, "honest helper pause semantics");
                bool alive; try { using var child = Process.GetProcessById(childPid); alive = !child.HasExited; } catch (ArgumentException) { alive = false; }
                Ensure(!alive, "owned helper child killed");
                await Until(() => JobOf(manager, waiting.Id).DownloadedBytes > 0);
                Ensure(JobOf(manager, waiting.Id).Status == "Downloading", "next queued helper starts when slot is free");
                manager.Remove(waiting.Id);
            }
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", null);
            using var restored = new DownloadManager(paths.Download, paths.State, new DownloadOptions { Youtube = service });
            Ensure(JobOf(restored, id).Status == "Paused" && restored.RequestOf(id)?.Youtube?.VideoId == "18", "restore retains choice paused");
            restored.Resume(id); await Until(() => JobOf(restored, id).Status == "Completed");
        }
        finally { Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", null); }
    }
    public static async Task Errors((string Download, string State) paths)
    {
        var service = await Install(paths.State);
        Ensure(YoutubeService.Classify("HTTP Error 429") == "youtube.rate" && YoutubeService.Classify("DRM protected") == "youtube.unsupported", "stable error classification");
        try
        {
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "restricted");
            using var manager = new DownloadManager(paths.Download, paths.State, new DownloadOptions { Youtube = service });
            var job = await manager.AddAsync(new(Url, "failure", Youtube: new("18"))); await Until(() => JobOf(manager, job.Id).Status == "Failed");
            Ensure(JobOf(manager, job.Id).ErrorCode == "youtube.restricted" && !File.Exists(JobOf(manager, job.Id).FilePath), "restricted error no publish");
            Ensure(!File.ReadAllText(Path.Combine(paths.State, "queue.json")).Contains("secret-cookie"), "helper diagnostics never persisted");
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "oversize");
            try { await service.InspectAsync(Url).WaitAsync(TimeSpan.FromSeconds(20)); throw new Exception("oversized metadata accepted"); } catch (YoutubeException e) { Ensure(e.Code == "youtube.metadata", "oversized output reports the metadata error, not a cancellation or generic failure"); }
            foreach (var bad in new[] { "malformed", "linebomb" })
            {
                Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", bad);
                try { await service.InspectAsync(Url).WaitAsync(TimeSpan.FromSeconds(20)); throw new Exception(bad + " metadata accepted"); } catch (YoutubeException e) { Ensure(e.Code == "youtube.metadata", bad + " output reports the metadata error: " + e.Code); }
            }
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "filtered");
            try { await service.InspectAsync(Url).WaitAsync(TimeSpan.FromSeconds(20)); throw new Exception("filtered video accepted"); } catch (YoutubeException e) { Ensure(e.Code == "youtube.unsupported", "match-filter rejection (no output, exit 0) keeps the unsupported message"); }
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "unknownfail");
            try { await service.InspectAsync(Url).WaitAsync(TimeSpan.FromSeconds(20)); throw new Exception("failing helper accepted"); }
            catch (YoutubeException e)
            {
                Ensure(e.Code == "youtube.failed" && e.Detail is { Length: > 0 and <= 300 } d && d.StartsWith("ERROR: [youtube] abc:") && !d.Any(char.IsControl) && !d.Contains('\u202e') && !d.Contains("trailing"), "unknown failure keeps the generic message and carries a bounded, sanitised last ERROR line: " + e.Detail);
            }
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "restricted");
            try { await service.InspectAsync(Url); throw new Exception("restricted accepted"); } catch (YoutubeException e) { Ensure(e.Code == "youtube.restricted" && e.Detail is null, "classified failures carry no helper text"); }
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "hang");
            using (var user = new CancellationTokenSource())
            {
                var pending = service.InspectAsync(Url, user.Token); await Task.Delay(1500); user.Cancel();
                try { await pending.WaitAsync(TimeSpan.FromSeconds(20)); throw new Exception("cancelled inspection returned"); } catch (OperationCanceledException) { }
            }
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "many");
            var log = Path.Combine(paths.State, "inspect-args.log"); Environment.SetEnvironmentVariable("NDM_YT_TEST_LOG", log);
            try
            {
                var wide = await service.InspectAsync(Url);
                Ensure(wide.Formats.Count == 150 && wide.Formats.All(f => !f.Id.StartsWith("sb")), "real service call with 200 helper formats lists only usable ones");
                var logged = File.ReadAllLines(log).Select(l => JsonSerializer.Deserialize<string[]>(l)!).Last();
                Ensure(!logged.Contains("--dump-single-json") && logged.Contains("--skip-download") && logged.Count(a => a == "--print") == 2 && logged.Contains(YoutubeService.InfoTemplate) && logged.Contains(YoutubeService.FormatsTemplate) && logged.Contains("--match-filters") && !logged.Any(a => a.Contains("automatic_captions") || a.Contains("subtitles")), "inspection asks only for the needed fields and keeps the live/DRM match filter");
            }
            finally { Environment.SetEnvironmentVariable("NDM_YT_TEST_LOG", null); }
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "restricted");
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "missing");
            try { await service.DownloadAsync(Url, new("18"), Path.Combine(paths.State, "missing"), manager.Tools, null, (_, _) => { }, default); throw new Exception("missing output accepted"); } catch (YoutubeException e) { Ensure(e.Code == "youtube.output", "success exit does not imply media output"); }
        }
        finally { Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", null); }
    }
    public static async Task MissingDependencies((string Download, string State) paths)
    {
        var service = await Install(paths.State);
        try { await service.DownloadAsync(Url, new("137", "140"), Path.Combine(paths.State, "work"), new ToolManager(paths.State), null, (_, _) => { }, default); throw new Exception("missing muxer accepted"); } catch (ToolException e) { Ensure(e.Code == ToolException.NotInstalled, "merge needs explicit muxer"); }
        File.AppendAllText(service.Extractor.FfmpegPath, "changed");
        try { await service.InspectAsync(Url); throw new Exception("tampered extractor launched"); } catch (ToolException e) { Ensure(e.Code == ToolException.HashMismatch, "extractor verified at launch"); }
    }
}
