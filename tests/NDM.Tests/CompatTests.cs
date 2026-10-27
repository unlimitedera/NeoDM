using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NDM.Core;

namespace NDM.Tests;

/// <summary>1.5.7: captured downloads that failed with 400/403 on strict servers. For each strict-server mode: the 1.5.6 request (User-Agent NeoDM/1.0, no Accept-Language,
/// a throwaway Range: bytes=0-0 first, HEAD for names) is refused by the server, and the 1.5.7 engine completes the transfer with the exact SHA-256 and records the attempt trail.</summary>
internal static class CompatTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Wait(Func<bool> condition, string message, int seconds = 30)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message); await Task.Delay(20); }
    }
    private static DownloadOptions Options(Action<DownloadOptionsBuilder>? tweak = null)
    {
        var b = new DownloadOptionsBuilder(); tweak?.Invoke(b);
        return new DownloadOptions
        {
            MaxConcurrentDownloads = 2, MaxSegments = b.Segments, MinimumSegmentSize = 65536, RetryCount = b.Retry, RequestTimeout = TimeSpan.FromSeconds(10),
            Identity = b.Identity, CustomUserAgent = b.Custom, LadderPause = TimeSpan.FromMilliseconds(20), MaxRetryAfter = b.MaxRetryAfter
        };
    }
    private sealed class DownloadOptionsBuilder { public int Segments = 4, Retry = 0; public IdentityMode Identity = IdentityMode.Browser; public string? Custom; public TimeSpan MaxRetryAfter = TimeSpan.FromMilliseconds(400); }
    private static DownloadManager Open((string Download, string State) paths, Action<DownloadOptionsBuilder>? tweak = null) => new(paths.Download, paths.State, Options(tweak));
    private static DownloadJob Job(DownloadManager m, string id) => m.Jobs.Single(j => j.Id == id);
    private static async Task<DownloadJob> Finish(DownloadManager m, string id)
    {
        await Wait(() => Job(m, id).Status is "Completed" or "Failed", "download did not finish");
        return Job(m, id);
    }
    private static async Task<DownloadJob> Done(DownloadManager m, string id, CompatServer server)
    {
        var job = await Finish(m, id);
        Check(job.Status == "Completed", "expected the job to complete, got: " + job.Error);
        Check(CompatServer.Hash(File.ReadAllBytes(job.FilePath)) == CompatServer.Hash(server.Payload), "SHA-256 of the saved file differs");
        return job;
    }

    /// <summary>The exact request set of 1.5.6: User-Agent NeoDM/1.0, Accept-Encoding identity, no Accept, no Accept-Language, Range 0-0 first.</summary>
    private static async Task<HttpStatusCode> Legacy(string url, bool range = true, string? referer = null, HttpMethod? method = null)
    {
        using var client = new HttpClient();
        using var message = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        message.Headers.AcceptEncoding.ParseAdd("identity"); message.Headers.UserAgent.ParseAdd("NeoDM/1.0");
        if (range) message.Headers.Range = new RangeHeaderValue(0, 0);
        if (referer is not null) message.Headers.Referrer = new Uri(referer);
        using var response = await client.SendAsync(message);
        return response.StatusCode;
    }

    // ---------------------------------------------------------------- pure rules
    public static Task Rules()
    {
        // identity strings
        Check(RequestIdentity.BrowserUserAgent("1.5.7") == $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{RequestIdentity.ChromeMajor}.0.0.0 Safari/537.36 NeoDM/1.5.7", "default User-Agent format");
        Check(RequestIdentity.BrowserUserAgent().EndsWith("NeoDM/" + RequestIdentity.Version, StringComparison.Ordinal) && RequestIdentity.Version.Split('.').Length == 3, "the default User-Agent ends with the product and its version");
        Check(RequestIdentity.DefaultAccept == "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8", "default Accept");
        Check(RequestIdentity.DefaultAcceptLanguage(new CultureInfo("en-US")) == "en-US,en;q=0.9" && RequestIdentity.DefaultAcceptLanguage(CultureInfo.InvariantCulture) == "en-US,en;q=0.9", "english / invariant Accept-Language");
        Check(RequestIdentity.DefaultAcceptLanguage(new CultureInfo("de-DE")) == "de-DE,de;q=0.9,en;q=0.8" && RequestIdentity.DefaultAcceptLanguage(new CultureInfo("fr")) == "fr,en;q=0.8" && RequestIdentity.DefaultAcceptLanguage(new CultureInfo("en-GB")) == "en-GB,en;q=0.9", "Accept-Language from the display language");
        Check(RequestIdentity.UserAgentFor(IdentityMode.Browser, null, "Mozilla/5.0 (X) Chrome/9") == "Mozilla/5.0 (X) Chrome/9", "browser mode: the captured identity");
        Check(RequestIdentity.UserAgentFor(IdentityMode.Browser, null, null) == RequestIdentity.BrowserUserAgent(), "browser mode: the default");
        Check(RequestIdentity.UserAgentFor(IdentityMode.Browser, null, "bad\r\nvalue") == RequestIdentity.BrowserUserAgent(), "a captured value with control characters is never used");
        Check(RequestIdentity.UserAgentFor(IdentityMode.NeoDM, null, "Mozilla/5.0 (X)") == RequestIdentity.ProductToken && RequestIdentity.UserAgentFor(IdentityMode.NeoDM, null, "Mozilla/5.0 (X)", true) == "Mozilla/5.0 (X)", "NeoDM mode names the product; the ladder may ask for the browser identity");
        Check(RequestIdentity.UserAgentFor(IdentityMode.Custom, " MyAgent/2 ", "Mozilla/5.0 (X)", true) == "MyAgent/2", "custom text wins");
        Check(RequestIdentity.UserAgentFor(IdentityMode.Custom, "", null) == RequestIdentity.BrowserUserAgent(), "empty custom text falls back to the browser identity");
        Check(RequestIdentity.IsHeaderValue(new string('a', 512), 512) && !RequestIdentity.IsHeaderValue(new string('a', 513), 512) && !RequestIdentity.IsHeaderValue("a\tb", 512) && !RequestIdentity.IsHeaderValue("caf\u00e9", 512) && !RequestIdentity.IsHeaderValue("a\u007f", 512) && !RequestIdentity.IsHeaderValue("", 512) && !RequestIdentity.IsHeaderValue("   ", 512), "header value caps and characters");
        // status set
        foreach (var code in new[] { 400, 403, 405, 406, 409, 416, 429, 501, 503 }) Check(CompatibilityLadder.IsLadderStatus((HttpStatusCode)code), "ladder status " + code);
        foreach (var code in new[] { 200, 206, 301, 404, 410, 500, 502, 504 }) Check(!CompatibilityLadder.IsLadderStatus((HttpStatusCode)code), "not a ladder status " + code);
        Check(CompatibilityLadder.IsLadderStatus(HttpStatusCode.Unauthorized, false) && !CompatibilityLadder.IsLadderStatus(HttpStatusCode.Unauthorized, true), "401 only without a sign-in challenge");
        // variant order and cap
        var url = new Uri("https://files.example/a/b.zip");
        var v = default(RequestVariant); var steps = new List<string>();
        for (var next = CompatibilityLadder.Next(v, IdentityMode.NeoDM, "https://other.example/page", url); next is { } n; next = CompatibilityLadder.Next(v, IdentityMode.NeoDM, "https://other.example/page", url))
        { steps.Add(CompatibilityLadder.Describe(v, n)); v = n; }
        Check(string.Join("|", steps) == "without Range|browser identity|referer: site root|without referer", "ladder order (NeoDM identity, foreign referer): " + string.Join("|", steps));
        steps.Clear(); v = default;
        for (var next = CompatibilityLadder.Next(v, IdentityMode.Browser, "https://files.example/page", url); next is { } n; next = CompatibilityLadder.Next(v, IdentityMode.Browser, "https://files.example/page", url))
        { steps.Add(CompatibilityLadder.Describe(v, n)); v = n; }
        Check(string.Join("|", steps) == "without Range|without referer", "browser identity and a same-site referer skip two steps: " + string.Join("|", steps));
        steps.Clear(); v = default;
        for (var next = CompatibilityLadder.Next(v, IdentityMode.Custom, null, url); next is { } n; next = CompatibilityLadder.Next(v, IdentityMode.Custom, null, url))
        { steps.Add(CompatibilityLadder.Describe(v, n)); v = n; }
        Check(string.Join("|", steps) == "without Range|referer: site root", "no referer sent: only the site root is tried: " + string.Join("|", steps));
        Check(CompatibilityLadder.MaxAttempts == 5, "at most five attempts");
        // Retry-After
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Check(CompatibilityLadder.Delay(HttpStatusCode.Forbidden, new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)), now) == TimeSpan.FromMilliseconds(250), "Retry-After is only read for 429 and 503");
        Check(CompatibilityLadder.Delay(HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(TimeSpan.FromSeconds(3)), now) == TimeSpan.FromSeconds(3), "Retry-After seconds honoured");
        Check(CompatibilityLadder.Delay(HttpStatusCode.ServiceUnavailable, new RetryConditionHeaderValue(TimeSpan.FromSeconds(60)), now) == TimeSpan.FromSeconds(10), "Retry-After is capped at ten seconds");
        Check(CompatibilityLadder.Delay(HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(now.AddSeconds(4)), now) == TimeSpan.FromSeconds(4), "Retry-After as a date");
        Check(CompatibilityLadder.Delay(HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(now.AddSeconds(-30)), now) == TimeSpan.FromMilliseconds(250) && CompatibilityLadder.Delay(HttpStatusCode.TooManyRequests, null, now) == TimeSpan.FromMilliseconds(250) && CompatibilityLadder.Delay(HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(TimeSpan.Zero), now) == TimeSpan.FromMilliseconds(250), "small or missing values give the 250 ms pause");
        // connection reduction
        Check(CompatibilityLadder.ReduceConnections(8) == 4 && CompatibilityLadder.ReduceConnections(5) == 2 && CompatibilityLadder.ReduceConnections(2) == 1 && CompatibilityLadder.ReduceConnections(1) == 1 && CompatibilityLadder.ReduceConnections(0) == 1, "halve, never below one");
        Check(CompatibilityLadder.IsConnectionLimitStatus(HttpStatusCode.Forbidden) && CompatibilityLadder.IsConnectionLimitStatus(HttpStatusCode.TooManyRequests) && CompatibilityLadder.IsConnectionLimitStatus(HttpStatusCode.BadRequest) && CompatibilityLadder.IsConnectionLimitStatus(HttpStatusCode.ServiceUnavailable) && !CompatibilityLadder.IsConnectionLimitStatus(HttpStatusCode.NotFound), "segment statuses read as a connection limit");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- the request the engine sends
    public static async Task Headers((string Download, string State) paths)
    {
        using var server = new CompatServer();
        using (var manager = Open(paths))
        {
            // no browser identity: the browser-style default; Accept, Accept-Language, Accept-Encoding identity
            var plain = await manager.AddAsync(new(server.BaseUrl + "/file", "plain.bin")); await Done(manager, plain.Id, server);
            var first = server.Log.First();
            Check(first.UserAgent == RequestIdentity.BrowserUserAgent() && first.Accept == RequestIdentity.DefaultAccept && !string.IsNullOrEmpty(first.AcceptLanguage), $"default headers: UA={first.UserAgent} Accept={first.Accept} Lang={first.AcceptLanguage}");
            Check(first.Range == "bytes=0-0", "the probe is still a ranged request when the server accepts it");
            // the browser's own identity wins in Browser mode
            while (server.Log.TryDequeue(out _)) { }
            var captured = await manager.AddAsync(new(server.BaseUrl + "/file", "captured.bin", null, null, false, null, null, null, "Mozilla/5.0 (X11; Linux x86_64) Test/1.0", "text/plain,*/*;q=0.5", "de-DE,de;q=0.9"));
            await Done(manager, captured.Id, server);
            Check(server.Log.All(s => s.UserAgent == "Mozilla/5.0 (X11; Linux x86_64) Test/1.0" && s.Accept == "text/plain,*/*;q=0.5" && s.AcceptLanguage == "de-DE,de;q=0.9"), "every request of the job carries the browser's identity: " + string.Join(" | ", server.Log.Select(s => s.UserAgent)));
            Check(server.Log.Count > 2, "the segments carry it too");
        }
        // 'NeoDM' and 'Custom' are the user's choice: the captured identity is not used for the User-Agent
        using (var manager = Open(paths, o => o.Identity = IdentityMode.NeoDM))
        {
            while (server.Log.TryDequeue(out _)) { }
            var job = await manager.AddAsync(new(server.BaseUrl + "/file", "neodm.bin", null, null, false, null, null, null, "Mozilla/5.0 Captured", null, null)); await Done(manager, job.Id, server);
            Check(server.Log.All(s => s.UserAgent == RequestIdentity.ProductToken && s.AcceptLanguage != null), "NeoDM identity: " + server.Log.First().UserAgent);
        }
        using (var manager = Open(paths, o => { o.Identity = IdentityMode.Custom; o.Custom = "MyAgent/9 (test)"; }))
        {
            while (server.Log.TryDequeue(out _)) { }
            var job = await manager.AddAsync(new(server.BaseUrl + "/file", "custom.bin", null, null, false, null, null, null, "Mozilla/5.0 Captured", null, null)); await Done(manager, job.Id, server);
            Check(server.Log.All(s => s.UserAgent == "MyAgent/9 (test)"), "custom identity");
        }
        bool badCustom = false; try { using var _ = Open(paths, o => { o.Identity = IdentityMode.Custom; o.Custom = "bad\r\nvalue"; }); } catch (ArgumentOutOfRangeException) { badCustom = true; }
        Check(badCustom, "a custom User-Agent with control characters is refused");
        using (var manager = Open(paths))
        {
            foreach (var bad in new DownloadRequest[] { new(server.BaseUrl + "/file", UserAgent: "a\r\nb: c"), new(server.BaseUrl + "/file", UserAgent: new string('a', 513)), new(server.BaseUrl + "/file", Accept: "x\u0001"), new(server.BaseUrl + "/file", Accept: new string('a', 513)), new(server.BaseUrl + "/file", AcceptLanguage: "de\n"), new(server.BaseUrl + "/file", AcceptLanguage: new string('a', 129)) })
            {
                bool rejected = false; try { await manager.AddAsync(bad); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "bad identity value not rejected");
            }
            var edge = await manager.AddAsync(new(server.BaseUrl + "/file", "edge.bin", UserAgent: new string('u', 512), Accept: new string('a', 512), AcceptLanguage: new string('l', 128)));
            await Done(manager, edge.Id, server);
        }
    }

    // ---------------------------------------------------------------- persistence
    public static async Task Persistence((string Download, string State) paths)
    {
        using var server = new CompatServer();
        string id;
        using (var manager = Open(paths))
        {
            var job = await manager.AddScheduledAsync(new(server.BaseUrl + "/file", "p.bin", null, null, false, null, null, null, "Mozilla/5.0 Saved/1", "image/*", "fr-FR,fr;q=0.9"), DateTimeOffset.UtcNow.AddHours(1)); id = job.Id;
        }
        var text = File.ReadAllText(Path.Combine(paths.State, "queue.json"));
        Check(text.Contains("Mozilla/5.0 Saved/1") && text.Contains("fr-FR,fr;q=0.9"), "the identity is saved in queue.json");
        using (var manager = Open(paths))
        {
            manager.Schedule(id, DateTimeOffset.UtcNow);
            await Done(manager, id, server);
            Check(server.Log.All(s => s.UserAgent == "Mozilla/5.0 Saved/1" && s.Accept == "image/*"), "a restored job still sends the saved identity");
        }
        // a queue.json written by 1.5.6 (no identity fields, no attempts) restores and downloads
        var old = Path.Combine(Path.GetDirectoryName(paths.State)!, "old"); var oldState = Path.Combine(old, "state"); var oldDownloads = Path.Combine(old, "downloads"); Directory.CreateDirectory(oldState);
        var oldId = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(oldState, "queue.json"), "[{\"Id\":\"" + oldId + "\",\"Request\":{\"Url\":\"" + server.BaseUrl + "/file\",\"FileName\":\"old.bin\",\"Referer\":null,\"Cookie\":null,\"AutoName\":false,\"TargetDirectory\":null,\"ExpectedSize\":null,\"ExpectedMime\":null},\"FileName\":\"old.bin\",\"Status\":\"Paused\",\"Total\":0,\"Segments\":[]}]");
        using (var manager = new DownloadManager(oldDownloads, oldState, Options()))
        {
            var job = Job(manager, oldId); Check(job.Status == "Paused" && job.Attempts is null, "an old queue entry restores");
            manager.Resume(oldId); await Done(manager, oldId, server);
        }
        // the attempt trail survives a restart
        var failPaths = (Path.Combine(Path.GetDirectoryName(paths.State)!, "f-downloads"), Path.Combine(Path.GetDirectoryName(paths.State)!, "f-state"));
        string failId; string trail;
        using (var manager = Open(failPaths))
        {
            var failed = await manager.AddAsync(new(server.BaseUrl + "/forbidden", "x.bin")); failId = failed.Id;
            var done = await Finish(manager, failId); trail = done.Attempts ?? "";
            Check(done.Status == "Failed" && trail.Length > 0, "the job failed with a trail");
        }
        using (var manager = Open(failPaths))
        {
            var job = Job(manager, failId); Check(job.Attempts == trail && job.ErrorCode == CompatibilityLadder.HttpStatusError && job.ErrorStatus == 403, "trail, code and status survive a restart");
        }
    }

    // ---------------------------------------------------------------- the seven strict-server modes
    public static async Task BrowserAgentRequired((string Download, string State) paths)
    {
        using var server = new CompatServer { RejectNeoDmAgent = true };
        Check(await Legacy(server.BaseUrl + "/file") == HttpStatusCode.Forbidden && await Legacy(server.BaseUrl + "/file", range: false) == HttpStatusCode.Forbidden, "before: the 1.5.6 request is refused with 403");
        server.Log.Clear();
        using var manager = Open(paths);
        var job = await manager.AddAsync(new(server.BaseUrl + "/file", "ua.bin")); var done = await Done(manager, job.Id, server);
        Check(server.Count("/file", 403) == 0 && done.Attempts == "Attempt 1: 206", "after: the browser-style identity is accepted at once: " + done.Attempts);
        // a user who chose the NeoDM identity still gets the file: the ladder offers the browser identity
        server.Log.Clear();
        using var neodm = Open((paths.Download + "-b", paths.State + "-b"), o => o.Identity = IdentityMode.NeoDM);
        var second = await neodm.AddAsync(new(server.BaseUrl + "/file", "ua2.bin")); var after = await Done(neodm, second.Id, server);
        Check(after.Attempts == "Attempt 1: 403, Attempt 2 (without Range): 403, Attempt 3 (browser identity): 200", "NeoDM identity trail: " + after.Attempts);
        Check(server.Log.Count == 3, "three requests, no throwaway: " + server.Log.Count);
    }

    public static async Task AcceptLanguageRequired((string Download, string State) paths)
    {
        using var server = new CompatServer { RequireAcceptLanguage = true };
        Check(await Legacy(server.BaseUrl + "/file") == HttpStatusCode.BadRequest, "before: no Accept-Language, 400");
        server.Log.Clear();
        using var manager = Open(paths);
        var job = await manager.AddAsync(new(server.BaseUrl + "/file", "lang.bin")); var done = await Done(manager, job.Id, server);
        Check(server.Count("/file", 400) == 0 && server.Log.All(s => !string.IsNullOrEmpty(s.AcceptLanguage)), "after: every request carries Accept-Language, no 400");
        Check(done.Attempts == "Attempt 1: 206", "trail: " + done.Attempts);
        // a captured value is forwarded as it is
        server.Log.Clear();
        var captured = await manager.AddAsync(new(server.BaseUrl + "/file", "lang2.bin", AcceptLanguage: "ja,en;q=0.5")); await Done(manager, captured.Id, server);
        Check(server.Log.All(s => s.AcceptLanguage == "ja,en;q=0.5"), "the captured Accept-Language is sent");
    }

    public static async Task RangeProbeRefused((string Download, string State) paths)
    {
        using var server = new CompatServer { RejectRangeProbe = true };
        Check(await Legacy(server.BaseUrl + "/file") == HttpStatusCode.BadRequest && await Legacy(server.BaseUrl + "/file", range: false) == HttpStatusCode.OK, "before: Range 0-0 is refused with 400, a plain GET is served");
        server.Log.Clear();
        using var manager = Open(paths);
        var job = await manager.AddAsync(new(server.BaseUrl + "/file", "range.bin")); var done = await Done(manager, job.Id, server);
        Check(done.Attempts == "Attempt 1: 400, Attempt 2 (without Range): 200", "trail: " + done.Attempts);
        Check(server.Log.Count == 2 && server.Log.Last().Range is null && server.Log.Last().Status == 200, "exactly two requests; the plain GET is the transfer");
        // the site is remembered for the rest of the session: the next job starts with the plain GET
        server.Log.Clear();
        var next = await manager.AddAsync(new(server.BaseUrl + "/file", "range2.bin")); var again = await Done(manager, next.Id, server);
        Check(server.Log.Count == 1 && server.Log.Single().Range is null && again.Attempts == "Attempt 1 (without Range): 200", "remembered for the site: " + again.Attempts + " / " + server.Log.Count);
    }

    public static async Task HeadRefused((string Download, string State) paths)
    {
        using var server = new CompatServer { RejectHead = true };
        Check(await Legacy(server.BaseUrl + "/file", range: false, method: HttpMethod.Head) == HttpStatusCode.MethodNotAllowed, "before: HEAD is refused with 405");
        server.Log.Clear();
        using var manager = Open(paths);
        var first = await manager.ProbeNameAsync(new(server.BaseUrl + "/file"));
        Check(first is not null, "the name probe falls back to a ranged GET");
        Check(server.Log.Count(s => s.Method == "HEAD") == 1 && server.Log.Count(s => s.Method == "GET") == 1, "one HEAD (405), then one GET");
        var second = await manager.ProbeNameAsync(new(server.BaseUrl + "/file"));
        Check(second is not null && server.Log.Count(s => s.Method == "HEAD") == 1, "HEAD is not tried again for this site in this session");
        server.Log.Clear();
        var job = await manager.AddAsync(new(server.BaseUrl + "/file", "head.bin")); await Done(manager, job.Id, server);
        Check(server.Log.All(s => s.Method == "GET"), "the transfer never uses HEAD");
    }

    public static async Task OneConnectionOnly((string Download, string State) paths)
    {
        using var server = new CompatServer { SingleConnection = true };
        using var manager = Open(paths);
        var job = await manager.AddAsync(new(server.BaseUrl + "/file", "one.bin")); var done = await Done(manager, job.Id, server);
        Check(server.Log.Any(s => s.Status == 429), "the server did refuse parallel connections");
        Check(done.ConnectionLimit == 1 && done.Attempts is not null && done.Attempts.Contains("connections 4 to 2") && done.Attempts.Contains("connections 2 to 1"), "the job reduced to one connection: limit=" + done.ConnectionLimit + " trail=" + done.Attempts);
        Check(done.ActiveConnectionCount == 0, "nothing keeps running");
        // the next job for this site starts at one connection and is never refused
        var refusals = server.Log.Count(s => s.Status == 429);
        var next = await manager.AddAsync(new(server.BaseUrl + "/file", "one2.bin")); var again = await Done(manager, next.Id, server);
        Check(again.ConnectionLimit == 1 && !again.Attempts!.Contains("connections 4 to 2") && server.Log.Count(s => s.Status == 429) - refusals <= 1, "remembered for the site: limit=" + again.ConnectionLimit + " new refusals=" + (server.Log.Count(s => s.Status == 429) - refusals) + " " + again.Attempts);
        // a fresh manager does not remember (memory only)
        using var fresh = Open((paths.Download + "-f", paths.State + "-f"));
        var third = await fresh.AddAsync(new(server.BaseUrl + "/file", "one3.bin")); await Done(fresh, third.Id, server);
        Check(server.Log.Count(s => s.Status == 429) > refusals, "the memory is per session, not saved");
    }

    public static async Task SegmentsRefusedAlways((string Download, string State) paths)
    {
        using var server = new CompatServer { RefuseSegments = true };
        using var manager = Open(paths);
        var job = await manager.AddAsync(new(server.BaseUrl + "/file", "refused.bin"));
        var done = await Finish(manager, job.Id);
        Check(done.Status == "Failed" && done.ErrorCode == CompatibilityLadder.HttpStatusError && done.ErrorStatus == 403, "a single connection that is refused too fails the job: " + done.Status + " " + done.ErrorCode);
        Check(done.ConnectionLimit == 1 && !File.Exists(done.FilePath), "reduced to one and nothing is published");
        Check(server.Log.Count(s => s.Range is not null && s.Range != "bytes=0-0") <= 16, "refusals are bounded: " + server.Log.Count);
        Check(done.Error!.Contains("403") && done.Attempts!.Contains("connections 4 to 2") && done.Attempts.Contains("connections 2 to 1"), "the message and the trail say what happened: " + done.Error);
    }

    public static async Task RefererRules((string Download, string State) paths)
    {
        using var server = new CompatServer { RequireOwnReferer = true };
        Check(await Legacy(server.BaseUrl + "/file", referer: "https://other.example/page") == HttpStatusCode.Forbidden && await Legacy(server.BaseUrl + "/file", referer: server.BaseUrl + "/page") == HttpStatusCode.PartialContent, "before: a referer from another site is refused");
        server.Log.Clear();
        using var manager = Open(paths);
        var job = await manager.AddAsync(new(server.BaseUrl + "/file", "ref.bin", "https://other.example/page")); var done = await Done(manager, job.Id, server);
        Check(done.Attempts == "Attempt 1: 403, Attempt 2 (without Range): 403, Attempt 3 (referer: site root): 200", "foreign referer trail: " + done.Attempts);
        Check(server.Log.Last().Referer == server.BaseUrl + "/", "the site's own root was sent as referer");
        // no referer at all
        server.Log.Clear();
        var none = await manager.AddAsync(new(server.BaseUrl + "/file", "ref2.bin")); await Done(manager, none.Id, server);
        // a server that rejects every referer: the last step is no referer
        using var strict = new CompatServer { RejectAnyReferer = true };
        using var other = Open((paths.Download + "-s", paths.State + "-s"));
        var third = await other.AddAsync(new(strict.BaseUrl + "/file", "ref3.bin", "https://other.example/page")); var finished = await Done(other, third.Id, strict);
        Check(finished.Attempts == "Attempt 1: 403, Attempt 2 (without Range): 403, Attempt 3 (referer: site root): 403, Attempt 4 (without referer): 200", "no-referer trail: " + finished.Attempts);
        Check(strict.Log.Last().Referer is null, "the last attempt carries no referer");
    }

    public static async Task OneUseLink((string Download, string State) paths)
    {
        using var server = new CompatServer { OneUseToken = true, IgnoreRange = true };
        Check(await Legacy(server.BaseUrl + "/file") == HttpStatusCode.OK && await Legacy(server.BaseUrl + "/file", range: false) == HttpStatusCode.Forbidden, "before: the throwaway request uses the link up, the real one gets 403");
        // a new server: the same rule, a fresh token
        using var fresh = new CompatServer { OneUseToken = true, IgnoreRange = true };
        using var manager = Open(paths);
        var job = await manager.AddAsync(new(fresh.BaseUrl + "/file", "once.bin")); var done = await Done(manager, job.Id, fresh);
        Check(fresh.Log.Count == 1 && fresh.Log.Single().Status == 200 && done.Attempts == "Attempt 1: 200", "after: one request only, its answer is the transfer: " + fresh.Log.Count + " " + done.Attempts);
        Check(done.ResumeSupported == false, "streamed as a single connection");
    }

    // ---------------------------------------------------------------- the bounds
    public static async Task AttemptCap((string Download, string State) paths)
    {
        using var server = new CompatServer();
        using var manager = Open(paths, o => { o.Identity = IdentityMode.NeoDM; o.Retry = 0; });
        var job = await manager.AddAsync(new(server.BaseUrl + "/forbidden", "cap.bin", "https://other.example/page"));
        var done = await Finish(manager, job.Id);
        Check(done.Status == "Failed" && server.Count("/forbidden") == 5, "never more than five attempts: " + server.Count("/forbidden") + " " + done.Status + " " + done.Error + " " + done.Attempts);
        Check(done.Attempts == "Attempt 1: 403, Attempt 2 (without Range): 403, Attempt 3 (browser identity): 403, Attempt 4 (referer: site root): 403, Attempt 5 (without referer): 403", "trail: " + done.Attempts);
        Check(done.Error!.Contains("403") && done.Error.Contains("Attempt 5") && done.ErrorStatus == 403 && done.ErrorCode == CompatibilityLadder.HttpStatusError, "the final error carries the trail: " + done.Error);
        // a refusal in the 400s is not repeated by the transfer retry loop either, so retries being on changes nothing
        using var again = Open((paths.Download + "-r", paths.State + "-r"), o => { o.Identity = IdentityMode.NeoDM; o.Retry = 3; });
        var second = await again.AddAsync(new(server.BaseUrl + "/forbidden", "cap2.bin", "https://other.example/page"));
        var ended = await Finish(again, second.Id);
        Check(ended.Status == "Failed" && ended.Attempts!.Split(", ").Length == 5, "the trail holds five attempts with retries on: " + ended.Attempts);
        // a 401 with a sign-in challenge is only reported
        server.Log.Clear();
        var auth = await manager.AddAsync(new(server.BaseUrl + "/auth", "auth.bin")); var login = await Finish(manager, auth.Id);
        Check(login.Status == "Failed" && server.Count("/auth") == 1 && login.ErrorStatus == 401, "401 with a challenge: one request");
        var plain = await manager.AddAsync(new(server.BaseUrl + "/plain401", "plain401.bin")); await Finish(manager, plain.Id);
        Check(server.Count("/plain401") > 1, "401 without a challenge goes through the ladder");
        // a missing file is not retried with other shapes
        var missing = await manager.AddAsync(new(server.BaseUrl + "/nothing-here", "m.bin")); var gone = await Finish(manager, missing.Id);
        Check(gone.Status == "Failed" && server.Count("/nothing-here") == 1 && gone.ErrorStatus == 404, "404: one request");
    }

    public static async Task RetryAfterIsHonoured((string Download, string State) paths)
    {
        using var server = new CompatServer();
        using var manager = Open(paths, o => o.MaxRetryAfter = TimeSpan.FromMilliseconds(600));
        var clock = Stopwatch.StartNew();
        var job = await manager.AddAsync(new(server.BaseUrl + "/slow-down", "slow.bin")); var done = await Finish(manager, job.Id);
        Check(done.Status == "Completed", "the second attempt is served: " + done.Error);
        Check(clock.Elapsed >= TimeSpan.FromMilliseconds(550) && clock.Elapsed < TimeSpan.FromSeconds(8), "Retry-After: 30 is waited for, but only up to the cap: " + clock.Elapsed);
        Check(done.Attempts == "Attempt 1: 429, Attempt 2 (without Range): 200", "trail: " + done.Attempts);
    }
}
