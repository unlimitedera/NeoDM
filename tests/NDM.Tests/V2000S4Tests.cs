// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using NDM.Core;

namespace NDM.Tests;

/// <summary>2.0.0 stage S4: refreshing the link of an expired download keeps the same job and its parts.</summary>
internal static class V2000S4Tests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Wait(Func<bool> condition, string message, int seconds = 30)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message); await Task.Delay(20); }
    }
    private static DownloadJob Job(DownloadManager m, string id) => m.Jobs.Single(j => j.Id == id);
    private static DownloadManager Manager((string Download, string State) p, string suffix = "") =>
        new(p.Download, p.State + suffix, new DownloadOptions { MaxSegments = 1, MinimumSegmentSize = 65536, RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(5) });

    public static Task PageRules()
    {
        Check(RefreshLink.CleanPage("https://site.example/files/a?id=3#top") == "https://site.example/files/a?id=3", "The fragment is dropped");
        foreach (var bad in new[] { "", "ftp://x/y", "https://user:pw@x.example/", "javascript:1", "https://x.example/" + new string('a', 2100), "https://x.example/a\nb" })
            Check(RefreshLink.CleanPage(bad) is null, "Refused page: " + bad[..Math.Min(bad.Length, 30)]);
        Check(RefreshLink.OpenAddress("https://site.example/p#old", "0123456789abcdef0123456789abcdef") == "https://site.example/p#neodm-refresh=0123456789abcdef0123456789abcdef", "The open address carries the code");
        Check(RefreshLink.OpenAddress("https://site.example/p", "nope") is null, "A bad code gives no address");
        const string old = "https://cdn.example/dl/model-00002-of-00005.gguf?sig=old&exp=1";
        Check(RefreshLink.Acceptable(old, "https://site.example/p", "https://cdn.example/dl/model-00002-of-00005.gguf?sig=new&exp=2"), "Same file, new signature");
        Check(RefreshLink.Acceptable(old, "https://site.example/p", "https://cdn2.other.example/x/model-00002-of-00005.gguf?t=1"), "Same file name on another host");
        Check(!RefreshLink.Acceptable(old, "https://site.example/p", old), "The same address is not fresh");
        Check(!RefreshLink.Acceptable(old, "https://site.example/p", "https://evil.test/other.bin"), "A different file on a different site is refused");
        Check(!RefreshLink.Acceptable(old, "https://site.example/p", "ftp://cdn.example/dl/model-00002-of-00005.gguf"), "Not http");
        Check(!RefreshLink.Acceptable(old, "https://site.example/p", "https://u:p@cdn.example/dl/model-00002-of-00005.gguf"), "No user name");
        Check(RefreshLink.ExpiryStatus(403) && RefreshLink.ExpiryStatus(404) && !RefreshLink.ExpiryStatus(500) && !RefreshLink.ExpiryStatus(null), "Expiry statuses");
        return Task.CompletedTask;
    }

    /// <summary>A signed link that went bad half way: the job fails with 403, the fresh address goes into the SAME job and it finishes from the saved bytes.</summary>
    public static async Task RefreshKeepsParts((string Download, string State) p)
    {
        using var server = new MirrorServer();
        var data = new byte[1_500_000]; new Random(7).NextBytes(data);
        var route = server.Add("/f.bin", data, r => { r.ChunkDelayMs = 20; r.RequireSig = "sig=old"; });
        var manager = Manager(p);
        string id;
        try
        {
            var job = await manager.AddAsync(new(server.Url("/f.bin?sig=old"), "f.bin", PageUrl: "https://site.example/page?id=9#frag"));
            id = job.Id;
            Check(Job(manager, id).PageUrl == "https://site.example/page?id=9#frag", "The page address is on the job");
            await Wait(() => Job(manager, id).DownloadedBytes >= 300_000, "Progress before the link expires");
            manager.Pause(id);
            await Wait(() => Job(manager, id).Status == "Paused", "Paused");
            var before = Job(manager, id).DownloadedBytes;
            route.RequireSig = "sig=new"; // the signature in the address stops working
            manager.Resume(id);
            await Wait(() => Job(manager, id).Status == "Failed", "The expired link fails");
            var failed = Job(manager, id);
            Check(failed.ErrorStatus == 403 && failed.ErrorCode == CompatibilityLadder.HttpStatusError, "The failure is the 403 of an expired link");
            Check(failed.DownloadedBytes > 0, "The saved bytes are still counted: " + failed.DownloadedBytes);
            Check(Job(manager, id).Id == id && manager.Jobs.Count == 1, "Still one job");
            var started = server.Queries.Count;
            manager.RefreshFromBrowser(id, new BrowserGrab(server.Url("/f.bin?sig=new"), null, "https://site.example/page?id=9", null, null, null, null));
            manager.Resume(id);
            await Wait(() => Job(manager, id).Status is "Completed" or "Failed", "Finish after the refresh");
            var done = Job(manager, id);
            Check(done.Status == "Completed" && File.ReadAllBytes(done.FilePath).SequenceEqual(data), "The file is complete and identical: " + done.Error);
            Check(manager.Jobs.Count == 1 && done.Id == id, "No second job was made");
            var firstNew = server.Queries.Skip(started).Where(q => q.Query.Contains("sig=new") && q.Range is not null && q.Range != "bytes=0-0")
                .Select(q => long.Parse(q.Range!["bytes=".Length..q.Range!.IndexOf('-')])).OrderBy(x => x).FirstOrDefault(-1);
            Check(firstNew >= before, $"The fresh link continued from the saved bytes (first range starts at {firstNew}, {before} were saved)");
        }
        finally { manager.Dispose(); }
    }

    /// <summary>The page address survives a restart; an address that is not a web page is refused when a job is added.</summary>
    public static async Task PageSurvivesRestart((string Download, string State) p)
    {
        using var server = new MirrorServer();
        server.Add("/g.bin", new byte[300_000]);
        string id;
        using (var manager = Manager(p))
        {
            var job = await manager.AddScheduledAsync(new(server.Url("/g.bin"), "g.bin", PageUrl: "https://site.example/p"), DateTimeOffset.UtcNow.AddYears(1));
            id = job.Id;
            try { await manager.AddAsync(new(server.Url("/g.bin"), "h.bin", PageUrl: "javascript:alert(1)")); throw new Exception("A bad page address was accepted"); }
            catch (ArgumentException) { }
        }
        using var again = Manager(p);
        Check(Job(again, id).PageUrl == "https://site.example/p", "The page address came back after a restart");
    }
}
