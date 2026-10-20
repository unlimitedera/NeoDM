using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NDM.Core;

namespace NDM.Tests;

/// <summary>1.5.3 security hardening in the download engine: cookie at rest, Mark of the Web, redirects, file names, disk space, read caps.</summary>
internal static class SecurityTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Wait(Func<bool> condition, string message, int seconds = 20)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message); await Task.Delay(20); }
    }
    private static DownloadManager Open((string Download, string State) paths, DownloadOptions? options = null) =>
        new(paths.Download, paths.State, options ?? new DownloadOptions { MaxConcurrentDownloads = 2, MaxSegments = 4, MinimumSegmentSize = 65536, RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(10) });
    private static DownloadJob Job(DownloadManager m, string id) => m.Jobs.Single(j => j.Id == id);
    private static async Task<DownloadJob> Finish(DownloadManager m, string id)
    {
        await Wait(() => Job(m, id).Status is "Completed" or "Failed", "download did not finish");
        return Job(m, id);
    }
    private static string QueueText((string Download, string State) paths) => File.ReadAllText(Path.Combine(paths.State, "queue.json"));

    // ---------------------------------------------------------------- 6: cookie at rest
    public static async Task CookieAtRest((string Download, string State) paths)
    {
        const string secret = "session=TOP-SECRET-COOKIE-VALUE-9f3a";
        using var server = new FixtureServer(); string id;
        using (var manager = Open(paths))
        {
            var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range", "cookie.bin", server.BaseUrl + "/page", secret), DateTimeOffset.UtcNow.AddHours(1)); id = j.Id;
        }
        var saved = QueueText(paths);
        Check(!saved.Contains("TOP-SECRET-COOKIE", StringComparison.Ordinal) && !saved.Contains(secret, StringComparison.Ordinal), "queue.json contains the cookie in plain text");
        Check(saved.Contains("dpapi1:", StringComparison.Ordinal), "no protected cookie blob saved");
        Check(saved.Contains(server.BaseUrl + "/page", StringComparison.Ordinal), "the Referer is still saved");
        // Round trip: the restored job sends the cookie again.
        using (var manager = Open(paths))
        {
            Check(Job(manager, id).Status == "Scheduled", "scheduled job did not restore");
            manager.Schedule(id, DateTimeOffset.UtcNow);
            var done = await Finish(manager, id); Check(done.Status == "Completed", "restored job failed: " + done.Error);
            Check(server.Headers.Where(h => h.Path == "/range").All(h => h.Cookie == secret), "the restored cookie was not sent");
        }
        var after = QueueText(paths);
        Check(!after.Contains("TOP-SECRET-COOKIE", StringComparison.Ordinal) && !after.Contains("dpapi1:", StringComparison.Ordinal), "the cookie is still in queue.json after the download completed");
    }

    public static async Task CookieMigrationAndClearing((string Download, string State) paths)
    {
        const string secret = "legacy=PLAINTEXT-LEGACY-COOKIE";
        using var server = new FixtureServer(); string id;
        using (var manager = Open(paths)) { id = (await manager.AddScheduledAsync(new(server.BaseUrl + "/range", "m.bin", null, "tmp=x"), DateTimeOffset.UtcNow.AddHours(1))).Id; }
        // Rewrite the file the way 1.5.2 wrote it: the cookie in the request, no protected blob.
        var queuePath = Path.Combine(paths.State, "queue.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(queuePath))!.AsArray();
        var entry = root[0]!.AsObject(); entry.Remove("CookieProtected"); entry["Request"]!.AsObject()["Cookie"] = secret;
        File.WriteAllText(queuePath, root.ToJsonString());
        Check(QueueText(paths).Contains(secret, StringComparison.Ordinal), "test setup: legacy file should hold a plain cookie");
        using (var manager = Open(paths))
        {
            manager.Schedule(id, DateTimeOffset.UtcNow.AddHours(2)); // forces a save while the job is not finished
            await Wait(() => !QueueText(paths).Contains(secret, StringComparison.Ordinal), "legacy plain-text cookie was not rewritten protected", 10);
            Check(QueueText(paths).Contains("dpapi1:", StringComparison.Ordinal), "the migrated cookie is not stored protected");
            manager.Schedule(id, DateTimeOffset.UtcNow);
            var done = await Finish(manager, id); Check(done.Status == "Completed", "migrated job failed: " + done.Error);
            Check(server.Headers.Where(h => h.Path == "/range").Any(h => h.Cookie == secret), "the migrated cookie was not sent");
        }
        // Failed jobs and removed jobs do not keep the cookie either.
        using (var manager = Open(paths))
        {
            var failed = await manager.AddAsync(new(server.BaseUrl + "/fail", "f.bin", null, "fail=FAILCOOKIE"));
            await Wait(() => Job(manager, failed.Id).Status == "Failed", "failure not reported");
            await Wait(() => !QueueText(paths).Contains("FAILCOOKIE", StringComparison.Ordinal), "failed job kept its cookie", 10);
            var pending = await manager.AddScheduledAsync(new(server.BaseUrl + "/range", "p.bin", null, "rm=REMOVEDCOOKIE"), DateTimeOffset.UtcNow.AddHours(1));
            Check(QueueText(paths).Contains("dpapi1:", StringComparison.Ordinal), "pending job's cookie is not stored protected");
            manager.Remove(pending.Id);
            await Wait(() => !QueueText(paths).Contains(pending.Id, StringComparison.Ordinal), "removed job still in queue.json", 10);
            Check(!QueueText(paths).Contains("REMOVEDCOOKIE", StringComparison.Ordinal), "removed job left its cookie");
        }
        Check(CookieVault.TryUnprotect("dpapi1:AAAA") is null && CookieVault.TryUnprotect("not ours") is null && CookieVault.TryUnprotect(null) is null, "bad blobs must be ignored");
        Check(CookieVault.TryUnprotect(CookieVault.Protect("a=b; c=d")) == "a=b; c=d", "DPAPI round trip");
    }

    // ---------------------------------------------------------------- 7: Mark of the Web
    public static async Task MarkOfTheWebStream((string Download, string State) paths)
    {
        using var server = new FixtureServer();
        using (var manager = Open(paths))
        {
            var page = server.BaseUrl + "/some/page?x=1#frag";
            var j = await manager.AddAsync(new(server.BaseUrl + "/range", "motw.bin", page));
            var done = await Finish(manager, j.Id); Check(done.Status == "Completed", "download failed: " + done.Error);
            var stream = File.ReadAllText(done.FilePath + ":Zone.Identifier");
            Check(stream == "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=" + server.BaseUrl + "/some/page?x=1\r\nHostUrl=" + server.BaseUrl + "/range\r\n", "Zone.Identifier content is not exact: " + stream.Replace("\r\n", "|"));
            var streaming = await manager.AddAsync(new(server.BaseUrl + "/no-range", "motw2.bin"));
            var done2 = await Finish(manager, streaming.Id);
            Check(File.ReadAllText(done2.FilePath + ":Zone.Identifier") == "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=" + server.BaseUrl + "/no-range\r\n", "streamed download without referer: wrong stream");
        }
        // sanitizing
        var built = MarkOfTheWeb.BuildContent("https://user:pass@host.example/f.zip#x", "https://page.example/a\r\nZoneId=0");
        Check(!built.Contains("pass") && !built.Contains("user") && !built.Contains("#x") && built.Split("\r\n").Count(l => l.StartsWith("ZoneId=")) == 1, "credentials, fragment or injected lines survived: " + built.Replace("\r\n", "|"));
        Check(MarkOfTheWeb.SanitizeUrl("ftp://x.example/a") is null && MarkOfTheWeb.SanitizeUrl("file:///c:/a") is null && MarkOfTheWeb.SanitizeUrl("javascript:alert(1)") is null && MarkOfTheWeb.SanitizeUrl("") is null && MarkOfTheWeb.SanitizeUrl(null) is null, "only http(s) addresses may be written");
        Check(MarkOfTheWeb.SanitizeUrl("http://x.example/" + new string('a', 3000)) is null, "over-long address");
        Check(MarkOfTheWeb.BuildContent(null, null) == "[ZoneTransfer]\r\nZoneId=3\r\n", "no addresses: ZoneId only");
    }

    public static async Task MarkOfTheWebSkippedAndOff((string Download, string State) paths)
    {
        using var server = new FixtureServer();
        // File system without streams: the writer throws; the download still completes and nothing leaks out.
        var original = MarkOfTheWeb.WriteStream;
        try
        {
            foreach (Exception failure in new Exception[] { new NotSupportedException("no streams"), new IOException("The volume does not support named streams"), new UnauthorizedAccessException("denied"), new ArgumentException("bad path") })
            {
                MarkOfTheWeb.WriteStream = (_, _) => throw failure;
                Check(!MarkOfTheWeb.TryApply(Path.Combine(paths.Download, "x.bin"), "https://a.example/x", null), "TryApply must report false for " + failure.GetType().Name);
                var p = (Path.Combine(paths.Download, failure.GetType().Name), Path.Combine(paths.State, failure.GetType().Name));
                using var manager = Open(p);
                var j = await manager.AddAsync(new(server.BaseUrl + "/range", "skip.bin"));
                var done = await Finish(manager, j.Id);
                Check(done.Status == "Completed" && File.Exists(done.FilePath), "a file system without ADS must not fail the download (" + failure.GetType().Name + "): " + done.Error);
            }
        }
        finally { MarkOfTheWeb.WriteStream = original; }
        // Setting off: no stream at all.
        var offPaths = (Path.Combine(paths.Download, "off"), Path.Combine(paths.State, "off"));
        using (var manager = Open(offPaths, new DownloadOptions { MarkOfTheWeb = false, RetryCount = 0 }))
        {
            var j = await manager.AddAsync(new(server.BaseUrl + "/range", "off.bin", server.BaseUrl + "/p"));
            var done = await Finish(manager, j.Id); Check(done.Status == "Completed", "download failed");
            Check(!File.Exists(done.FilePath + ":Zone.Identifier"), "the stream was written although the option is off");
        }
        Check(!MarkOfTheWeb.TryApply(Path.Combine(paths.Download, "no-such-dir", "x.bin"), "https://a.example/x", null), "missing file: best effort, no exception");
    }

    // ---------------------------------------------------------------- 8: redirects
    public static async Task RedirectHardening((string Download, string State) paths)
    {
        var https = new Uri("https://a.example/x"); var http = new Uri("http://a.example/x");
        bool Refused(Uri from, Uri to) { try { RedirectRules.EnsureNotDowngrade(from, to); return false; } catch (HttpRequestException ex) { return ex.Message.StartsWith("Redirect from https to http refused", StringComparison.Ordinal); } }
        Check(Refused(https, http) && Refused(https, new Uri("http://evil.example/")), "https to http must be refused");
        Check(!Refused(http, https) && !Refused(http, http) && !Refused(https, https) && !Refused(https, new Uri("https://b.example/")), "other combinations are allowed");
        Check(RedirectRules.SameOrigin(new Uri("https://A.example/x"), new Uri("https://a.example/y")) && !RedirectRules.SameOrigin(new Uri("https://a.example/x"), new Uri("https://a.example:8443/x")) && !RedirectRules.SameOrigin(new Uri("http://a.example/"), new Uri("https://a.example/")), "origin comparison");
        // cross-origin hop: no Referer, no Cookie. same-origin hop keeps both.
        using var origin = new FixtureServer(); using var target = new FixtureServer(); using var manager = Open(paths);
        origin.RedirectTarget = target.BaseUrl;
        var j = await manager.AddAsync(new(origin.BaseUrl + "/redirect", null, origin.BaseUrl + "/page", "secret=original"));
        var done = await Finish(manager, j.Id); Check(done.Status == "Completed", "redirected download failed: " + done.Error);
        Check(origin.Headers.Where(h => h.Path == "/redirect").All(h => h.Referer == origin.BaseUrl + "/page" && h.Cookie == "secret=original"), "the first hop must carry Referer and cookie");
        Check(target.Headers.Count > 0 && target.Headers.All(h => h.Referer is null && h.Cookie is null), "Referer or cookie leaked to the other origin: " + string.Join(",", target.Headers.Select(h => h.Referer + "/" + h.Cookie)));
        origin.RedirectTarget = null; // same origin: /redirect -> /range on the same server
        var same = await manager.AddAsync(new(origin.BaseUrl + "/redirect", null, origin.BaseUrl + "/page2", "k=v"));
        var done2 = await Finish(manager, same.Id); Check(done2.Status == "Completed", "same-origin redirect failed: " + done2.Error);
        Check(origin.Headers.Where(h => h.Path == "/range").Any(h => h.Referer == origin.BaseUrl + "/page2" && h.Cookie == "k=v"), "a same-origin redirect must keep Referer and cookie");
    }

    // ---------------------------------------------------------------- 9: file names
    public static Task FileNames()
    {
        string S(string v) => DownloadFileNames.Sanitize(v);
        Check(S("pay\u202Egnp.exe") == "paygnp.exe", "right-to-left override must go: " + S("pay\u202Egnp.exe"));
        Check(S("a\u200Bb\u200Fc\u2066d\u2069e\uFEFF.txt") == "abcde.txt", "zero-width and isolate characters must go: " + S("a\u200Bb\u200Fc\u2066d\u2069e\uFEFF.txt"));
        Check(S("\u202A\u202B\u202C\u202D\u202E\u200D\u200E.pdf") == "pdf", "all direction controls must go: " + S("\u202A\u202B\u202C\u202D\u202E\u200D\u200E.pdf"));
        Check(S("\u200B\u200B") == "download.bin" && S("\uFEFF") == "download.bin", "a name made only of invisible characters falls back");
        Check(S("desktop.ini") == "_desktop.ini" && S("DESKTOP.INI") == "_DESKTOP.INI" && S("autorun.inf") == "_autorun.inf" && S("AutoRun.Inf") == "_AutoRun.Inf", "shell files are reserved");
        Check(S("my desktop.ini") == "my desktop.ini" && S("desktop.ini.txt") == "desktop.ini.txt" && S("xautorun.inf") == "xautorun.inf", "only the exact names are reserved");
        foreach (var name in new[] { "CON", "con.txt", "PRN", "AUX", "NUL.x", "COM1", "COM9.bin", "LPT1", "LPT9.txt", "CONIN$", "CONOUT$", "CLOCK$", "clock$.txt", "COM\u00B9", "COM\u00B2.txt", "LPT\u00B3", "CON .txt" })
            Check(S(name).StartsWith('_'), "reserved name not escaped: " + name + " -> " + S(name));
        foreach (var name in new[] { "COM0", "COM10", "LPT0", "console", "clocks", "COM", "readme.txt" })
            Check(!S(name).StartsWith('_'), "ordinary name must be kept: " + name);
        Check(ReservedNames.IsReserved("desktop.ini") && ReservedNames.IsReserved("Desktop.INI") && ReservedNames.IsReserved("CLOCK$") && ReservedNames.IsReserved("LPT\u00B2") && !ReservedNames.IsReserved("movie.mkv") && !ReservedNames.IsReserved(""), "ReservedNames.IsReserved");
        Check(ReservedNames.ContainsInvisible("a\u202Eb") && ReservedNames.ContainsInvisible("a\u2067b") && !ReservedNames.ContainsInvisible("plain \u00E9 name") && ReservedNames.StripInvisible("\u200Ba\u200E") == "a", "invisible character helpers");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- 10: disk space
    public static async Task DiskSpace((string Download, string State) paths)
    {
        using var server = new FixtureServer();
        // declared length (ranged) does not fit: fails before downloading, with a clear message, and is not retried
        using (var manager = Open((Path.Combine(paths.Download, "a"), Path.Combine(paths.State, "a")), new DownloadOptions { RetryCount = 3, FreeSpace = _ => 1024 * 1024 }))
        {
            var j = await manager.AddAsync(new(server.BaseUrl + "/range", "big.bin"));
            await Wait(() => Job(manager, j.Id).Status == "Failed", "a download that does not fit must fail");
            var error = Job(manager, j.Id).Error ?? "";
            Check(error.StartsWith("Not enough free disk space on", StringComparison.Ordinal) && error.Contains("MB"), "unclear message: " + error);
            Check(server.Ranges.Count(r => r.Path == "/range" && r.End > r.Start) == 0, "data was requested although it cannot fit");
            Check(!File.Exists(j.FilePath), "nothing may be published");
        }
        // declared length, plenty of room, and exactly the staging overhead matters: parts + staging copy
        var payload = server.Payload.LongLength;
        using (var manager = Open((Path.Combine(paths.Download, "b"), Path.Combine(paths.State, "b")), new DownloadOptions { RetryCount = 0, FreeSpace = _ => payload * 2 - 1 }))
        {
            var j = await manager.AddAsync(new(server.BaseUrl + "/range", "tight.bin"));
            await Wait(() => Job(manager, j.Id).Status == "Failed", "parts plus the staging copy need 2x the size when both are on one drive");
        }
        using (var manager = Open((Path.Combine(paths.Download, "c"), Path.Combine(paths.State, "c")), new DownloadOptions { RetryCount = 0, FreeSpace = _ => payload * 2 + 4096 }))
        {
            var j = await manager.AddAsync(new(server.BaseUrl + "/range", "fits.bin"));
            Check((await Finish(manager, j.Id)).Status == "Completed", "a download with enough room must work");
        }
        // streaming with a declared length
        using (var manager = Open((Path.Combine(paths.Download, "d"), Path.Combine(paths.State, "d")), new DownloadOptions { RetryCount = 0, FreeSpace = _ => 100 * 1024 }))
        {
            var j = await manager.AddAsync(new(server.BaseUrl + "/no-range", "stream.bin"));
            await Wait(() => Job(manager, j.Id).Status == "Failed", "streaming download with a declared length that does not fit");
            Check((Job(manager, j.Id).Error ?? "").StartsWith("Not enough free disk space", StringComparison.Ordinal), "message");
        }
        // unknown length: the floor of 100 MB applies at the start
        using (var manager = Open((Path.Combine(paths.Download, "e"), Path.Combine(paths.State, "e")), new DownloadOptions { RetryCount = 0, FreeSpace = _ => DownloadManager.UnknownLengthFloor - 1 }))
        {
            var j = await manager.AddAsync(new(server.BaseUrl + "/chunked", "chunk.bin"));
            await Wait(() => Job(manager, j.Id).Status == "Failed", "unknown length below the floor must fail");
        }
        // unknown length: checked again while it is written (free space shrinks while the stream runs)
        int calls = 0;
        using (var manager = Open((Path.Combine(paths.Download, "f"), Path.Combine(paths.State, "f")), new DownloadOptions { RetryCount = 0, SpaceCheckIntervalBytes = 256 * 1024, FreeSpace = _ => Interlocked.Increment(ref calls) <= 3 ? 10L << 30 : 1L << 20 }))
        {
            var j = await manager.AddAsync(new(server.BaseUrl + "/chunked", "shrinking.bin"));
            await Wait(() => Job(manager, j.Id).Status == "Failed", "free space falling below the floor mid-stream must stop the download");
            Check(!File.Exists(j.FilePath), "nothing may be published");
        }
        using (var manager = Open((Path.Combine(paths.Download, "g"), Path.Combine(paths.State, "g")), new DownloadOptions { RetryCount = 0, FreeSpace = _ => 10L << 30 }))
        {
            var j = await manager.AddAsync(new(server.BaseUrl + "/chunked", "ok.bin"));
            Check((await Finish(manager, j.Id)).Status == "Completed", "unknown length with plenty of space");
        }
        // DriveInfo path (no seam): a normal download on the real disk still works
        using (var manager = Open((Path.Combine(paths.Download, "h"), Path.Combine(paths.State, "h"))))
        {
            var j = await manager.AddAsync(new(server.BaseUrl + "/range", "real.bin"));
            Check((await Finish(manager, j.Id)).Status == "Completed", "real free-space lookup must not block normal downloads");
        }
    }

    // ---------------------------------------------------------------- 11: read caps
    public static Task ReadCaps((string Download, string State) paths)
    {
        Directory.CreateDirectory(paths.State);
        var queue = Path.Combine(paths.State, "queue.json");
        using (var f = new FileStream(queue, FileMode.Create)) { f.SetLength(FileLimits.MaxQueueBytes + 1); }
        var before = new FileInfo(queue).Length;
        try { using var m = Open(paths); throw new Exception("an oversized queue.json must refuse to open"); }
        catch (InvalidDataException ex) { Check(ex.Message.StartsWith("queue.json is larger than", StringComparison.Ordinal), "message: " + ex.Message); }
        Check(File.Exists(queue) && new FileInfo(queue).Length == before, "the oversized queue.json must be left untouched");
        File.Delete(queue);
        // exactly at the cap is readable text (here: an empty list padded with spaces)
        File.WriteAllText(queue, "[" + new string(' ', 1024) + "]");
        using (var m = Open(paths)) Check(m.Jobs.Count == 0, "a small queue loads");
        var settings = Path.Combine(paths.State, "s.json"); File.WriteAllText(settings, new string('x', (int)FileLimits.MaxSettingsBytes + 1));
        try { FileLimits.ReadAllTextCapped(settings, FileLimits.MaxSettingsBytes, "settings.json"); throw new Exception("oversized settings must be refused"); }
        catch (InvalidDataException ex) { Check(ex.Message.Contains("settings.json is larger than", StringComparison.Ordinal), ex.Message); }
        File.WriteAllText(settings, new string('x', (int)FileLimits.MaxSettingsBytes));
        Check(FileLimits.ReadAllTextCapped(settings, FileLimits.MaxSettingsBytes, "settings.json").Length == FileLimits.MaxSettingsBytes, "a file of exactly the cap is read");
        return Task.CompletedTask;
    }

    public static Task QueueJsonStillValid((string Download, string State) paths)
    {
        // the JSON shape other code (and older versions reading a newer file) relies on
        using var m = Open(paths);
        var j = m.AddScheduledAsync(new("http://127.0.0.1:9/x", "n.bin", null, "c=1"), DateTimeOffset.UtcNow.AddHours(1)).GetAwaiter().GetResult();
        m.Dispose();
        using var doc = JsonDocument.Parse(QueueText(paths));
        var entry = doc.RootElement[0];
        Check(entry.GetProperty("Id").GetString() == j.Id && entry.GetProperty("Request").GetProperty("Cookie").ValueKind == JsonValueKind.Null, "request cookie is null in the file");
        return Task.CompletedTask;
    }
}
