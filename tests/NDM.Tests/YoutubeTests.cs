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
    public const string Metadata = """
    {"title":"../Sample\u202efile", "availability":"public", "formats":[
      {"format_id":"18","ext":"mp4","height":360,"vcodec":"avc1","acodec":"aac"},
      {"format_id":"137","ext":"mp4","height":1080,"vcodec":"avc1","acodec":"none"},
      {"format_id":"140","ext":"m4a","abr":128,"vcodec":"none","acodec":"aac"},
      {"format_id":"251","ext":"webm","abr":160,"vcodec":"none","acodec":"opus"},
      {"format_id":"18;touch BAD","ext":"mp4","vcodec":"avc1","acodec":"aac"},
      {"format_id":"999","ext":"../bad.exe","vcodec":"avc1","acodec":"aac"},
      {"format_id":"998","ext":"mp4","has_drm":true,"vcodec":"avc1","acodec":"aac"}]}
    """;
    public static int Run(string[] args)
    {
        if (args.Contains("--yt-fake-child")) { Thread.Sleep(60000); return 0; }
        if (Environment.GetEnvironmentVariable("NDM_YT_TEST_LOG") is { } log) File.AppendAllText(log, JsonSerializer.Serialize(args) + "\n");
        var mode = Environment.GetEnvironmentVariable("NDM_YT_TEST_MODE");
        if (mode == "oversize") { Console.Write(new string('X', 5 * 1024 * 1024)); return 0; }
        if (mode == "restricted") { Console.Error.WriteLine("Sign in is required. secret-cookie=must-not-persist"); return 1; }
        if (args.Contains("--dump-single-json")) { Console.WriteLine(Metadata); return 0; }
        var folder = args[Array.IndexOf(args, "--paths") + 1]; var format = args[Array.IndexOf(args, "--format") + 1];
        var ext = args.Contains("--extract-audio") ? "mp3" : format.Contains('+') ? "mkv" : format == "140" ? "m4a" : format == "251" ? "webm" : "mp4";
        if (mode is "slow" or "unknown")
        {
            using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--yt-fake-child" } })!;
            File.WriteAllText(Path.Combine(folder, "child.pid"), child.Id.ToString());
            for (int i = 0; i < 200; i++) { Console.WriteLine(mode == "unknown" ? $"NDM:{i * 32}:NA" : $"NDM:{i * 32}:6400"); Thread.Sleep(50); }
        }
        Console.WriteLine("NDM:4096:4096");
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
        Ensure(m.Formats.Count == 4 && !m.Title.Contains('/') && !m.Title.Contains('\u202e'), "bounded sanitized metadata");
        Ensure(YoutubeService.OutputExtension(new("137", "140"), m) == "mkv", "video plus audio");
        Ensure(YoutubeService.OutputExtension(new("251"), m) == "webm", "original audio no transcode");
        Ensure(YoutubeService.OutputExtension(new("140", Mp3: true), m) == "mp3", "audio conversion distinct");
        foreach (var s in new[] { new YoutubeSelection("137"), new YoutubeSelection("18", "140"), new YoutubeSelection("18", Mp3: true), new YoutubeSelection("137", "18"), new YoutubeSelection("missing") })
        { try { YoutubeService.OutputExtension(s, m); throw new Exception("accepted invalid selection"); } catch (YoutubeException) { } }
        foreach (var json in new[] { "{}", "not json", FakeYoutube.Metadata.Replace("\"availability\":\"public\"", "\"availability\":\"private\""), FakeYoutube.Metadata.Replace("\"availability\":\"public\"", "\"is_live\":true"), "{\"entries\":[]}" })
        { try { YoutubeService.ParseMetadata(json); throw new Exception("accepted unsafe metadata"); } catch (YoutubeException) { } }
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
                var pidFile = Directory.GetFiles(paths.State, "child.pid", SearchOption.AllDirectories).Single(); var childPid = int.Parse(File.ReadAllText(pidFile));
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
            try { await service.InspectAsync(Url).WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("oversized metadata accepted"); } catch (Exception e) when (e is YoutubeException or OperationCanceledException) { }
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
