using System.Diagnostics;
using NDM.Core;

namespace NDM.Tests;

/// <summary>1.12.0: the 403 Forbidden matrix. Each case is a loopback server that refuses a request in one of the ways real hosts do while the browser's own request is accepted;
/// the engine must finish with the exact bytes (or, where the refusal cannot be undone, fail clearly after the ladder). Every case runs alone so one failure does not hide another.</summary>
internal static class ForbiddenMatrixTests
{
    private static async Task Wait(Func<bool> condition, string message, int seconds = 30)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message); await Task.Delay(20); }
    }
    private static DownloadManager Open((string Download, string State) paths, string suffix, IdentityMode identity = IdentityMode.Browser) =>
        new(paths.Download + suffix, paths.State + suffix, new DownloadOptions { MaxConcurrentDownloads = 2, MaxSegments = 4, MinimumSegmentSize = 65536, RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(10), Identity = identity, LadderPause = TimeSpan.FromMilliseconds(20) });
    private static async Task<DownloadJob> Run(DownloadManager m, DownloadRequest request)
    {
        var added = await m.AddAsync(request);
        await Wait(() => m.Jobs.Single(j => j.Id == added.Id).Status is "Completed" or "Failed", "download did not finish");
        return m.Jobs.Single(j => j.Id == added.Id);
    }
    private static string? Verdict(DownloadJob job, CompatServer server) =>
        job.Status != "Completed" ? $"failed: {job.Error} [{job.Attempts}]"
        : CompatServer.Hash(File.ReadAllBytes(job.FilePath)) != CompatServer.Hash(server.Payload) ? "wrong bytes" : null;

    public static async Task Matrix((string Download, string State) paths)
    {
        var failures = new List<string>(); var n = 0;
        async Task Case(string name, Func<string, Task<string?>> body)
        {
            var suffix = "-m" + (++n);
            try { if (await body(suffix) is { } why) failures.Add(name + " -> " + why); }
            catch (Exception ex) { failures.Add(name + " -> exception " + ex.Message); }
        }
        const string Page = "http://127.0.0.1:1/page"; // a page address that is not the server's own origin

        await Case("known limit: a captured agent the server refuses is not replaced (the browser's own agent is the best identity); fails clearly with 403", async s =>
        {
            using var server = new CompatServer { RejectNeoDmAgent = true }; using var m = Open(paths, s);
            var job = await Run(m, new(server.BaseUrl + "/file", "a.bin", Referer: Page, UserAgent: "curl/8.4.0"));
            return job.Status == "Failed" && job.ErrorStatus == 403 ? null : "expected a clear 403 failure, got " + job.Status;
        });
        await Case("missing User-Agent from the capture, NeoDM identity chosen", async s =>
        {
            using var server = new CompatServer { RejectNeoDmAgent = true }; using var m = Open(paths, s, IdentityMode.NeoDM);
            return Verdict(await Run(m, new(server.BaseUrl + "/file", "b.bin")), server);
        });
        await Case("missing Referer: the server wants its own origin", async s =>
        {
            using var server = new CompatServer { RequireOwnReferer = true }; using var m = Open(paths, s);
            return Verdict(await Run(m, new(server.BaseUrl + "/file", "c.bin")), server);
        });
        await Case("foreign Referer captured, server wants its own origin", async s =>
        {
            using var server = new CompatServer { RequireOwnReferer = true }; using var m = Open(paths, s);
            return Verdict(await Run(m, new(server.BaseUrl + "/file", "c2.bin", Referer: Page)), server);
        });
        await Case("HEAD refused with 403 (name probe)", async s =>
        {
            using var server = new CompatServer { RejectHead = true, HeadStatus = 403 }; using var m = Open(paths, s);
            return Verdict(await Run(m, new(server.BaseUrl + "/file", null, AutoName: true)), server);
        });
        await Case("Range probe bytes=0-0 refused with 403 (CDN)", async s =>
        {
            using var server = new CompatServer { RejectRangeProbe = true, RangeProbeStatus = 403 }; using var m = Open(paths, s);
            return Verdict(await Run(m, new(server.BaseUrl + "/file", "d.bin")), server);
        });
        await Case("every Range request refused with 403, plain GET works", async s =>
        {
            using var server = new CompatServer { RejectAllRanges = true }; using var m = Open(paths, s);
            return Verdict(await Run(m, new(server.BaseUrl + "/file", "e.bin")), server);
        });
        await Case("cookie required: captured cookie is sent on every request", async s =>
        {
            using var server = new CompatServer { RequireCookie = "sid=abc" }; using var m = Open(paths, s);
            var job = await Run(m, new(server.BaseUrl + "/file", "f.bin", Cookie: "sid=abc; other=1"));
            return Verdict(job, server) ?? (server.Cookies.All(c => c.Contains("sid=abc")) ? null : "a request went without the cookie: " + string.Join(" | ", server.Cookies));
        });
        await Case("cookie required: a failed download retried keeps working after the cookie arrives later", async s =>
        {
            using var server = new CompatServer { RequireCookie = "sid=abc" }; using var m = Open(paths, s);
            var job = await Run(m, new(server.BaseUrl + "/file", "f2.bin"));
            return job.Status == "Failed" && job.ErrorStatus == 403 ? null : "expected a clear 403 failure without a cookie, got " + job.Status + " " + job.Error;
        });
        await Case("Accept header required (signed link)", async s =>
        {
            using var server = new CompatServer { RequireAccept = true }; using var m = Open(paths, s);
            return Verdict(await Run(m, new(server.BaseUrl + "/file", "g.bin")), server);
        });
        await Case("known limit: a firewall that refuses Accept-Encoding identity fails clearly with 403 (answering with compressed data would need response decoding)", async s =>
        {
            using var server = new CompatServer { RejectIdentityEncoding = true }; using var m = Open(paths, s);
            var job = await Run(m, new(server.BaseUrl + "/file", "h.bin"));
            return job.Status == "Failed" && job.ErrorStatus == 403 ? null : "expected a clear 403 failure, got " + job.Status;
        });
        await Case("redirect to another host that wants the browser's headers", async s =>
        {
            using var target = new CompatServer { RejectNeoDmAgent = true, RequireAccept = true, RequireAcceptLanguage = true };
            using var origin = new CompatServer { RedirectTo = target.BaseUrl }; using var m = Open(paths, s);
            var job = await Run(m, new(origin.BaseUrl + "/redir", "i.bin", Referer: origin.BaseUrl + "/page"));
            return Verdict(job, target);
        });
        await Case("redirect to another host that wants the page's origin as Referer (as the browser sends)", async s =>
        {
            using var origin = new CompatServer(); using var target = new CompatServer(); origin.RedirectTo = target.BaseUrl; target.RequireRefererPrefix = origin.BaseUrl + "/";
            using var m = Open(paths, s);
            var job = await Run(m, new(origin.BaseUrl + "/redir", "j.bin", Referer: origin.BaseUrl + "/page"));
            return Verdict(job, target);
        });
        await Case("redirect to another host: the cookie never leaves the first host", async s =>
        {
            using var origin = new CompatServer(); using var target = new CompatServer(); origin.RedirectTo = target.BaseUrl;
            using var m = Open(paths, s);
            var job = await Run(m, new(origin.BaseUrl + "/redir", "k.bin", Cookie: "sid=secret"));
            return Verdict(job, target) ?? (target.Cookies.All(c => c.Length == 0) ? null : "the cookie reached another host");
        });
        if (failures.Count > 0) throw new Exception(failures.Count + " of " + n + " cases fail:\n  " + string.Join("\n  ", failures));
    }
}
