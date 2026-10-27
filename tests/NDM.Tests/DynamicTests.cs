using System.Diagnostics;
using System.Text.Json.Nodes;
using NDM.Core;

namespace NDM.Tests;

internal static class DynamicTests
{
    private static DownloadManager Manager((string Download, string State) p, bool dynamic = true, int cap = 4) =>
        new(p.Download, p.State, new DownloadOptions
        {
            MaxSegments = cap, MinimumSegmentSize = 65536, EnableDynamicSegmentation = dynamic,
            RetryCount = 1, RequestTimeout = TimeSpan.FromSeconds(3)
        });
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    private static DownloadJob Job(DownloadManager manager, string id) => manager.Jobs.Single(j => j.Id == id);
    private static async Task Wait(Func<bool> predicate, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(message);
            await Task.Delay(10);
        }
    }
    private static void Coverage(DownloadJob j)
    {
        Check(j.ActiveConnectionCount >= 0 && j.ActiveConnectionCount <= j.ConnectionLimit, "Live connection cap exceeded");
        if (j.Segments.Count == 0) return;
        long offset = 0, done = 0;
        foreach (var s in j.Segments)
        {
            Check(s.Start == offset && s.Length > 0 && s.DownloadedBytes >= 0 && s.DownloadedBytes <= s.Length, "Invalid shard coverage/progress");
            offset += s.Length; done += s.DownloadedBytes;
        }
        Check(offset == j.TotalBytes && done == j.DownloadedBytes, "Coverage or progress sum differs");
        Check(j.Segments.Count <= 256, "Unbounded shard count");
        var activeShards = j.Segments.Count(s => s.IsActive);
        Check(activeShards == j.ActiveConnectionCount || (j.ResumeSupported == null && activeShards == 0 && j.ActiveConnectionCount <= 1), "Fabricated connection telemetry");
    }
    private static async Task<DownloadJob> Complete(DownloadManager manager, string id, byte[] bytes, int cap)
    {
        await Wait(() =>
        {
            var j = Job(manager, id); Coverage(j);
            Check(j.ConnectionLimit == cap, "Incorrect connection limit telemetry");
            Check(j.Status != "Failed", "Download failed: " + j.Error);
            return j.Status == "Completed";
        }, "Download did not finish");
        var completed = Job(manager, id);
        Check(FixtureServer.Hash(File.ReadAllBytes(completed.FilePath)) == FixtureServer.Hash(bytes), "SHA-256 differs");
        Check(completed.RemainingTime == TimeSpan.Zero && completed.ActiveConnectionCount == 0, "Invalid completion telemetry");
        return completed;
    }

    public static async Task Split((string Download, string State) p)
    {
        using var server = new FixtureServer(); using var manager = Manager(p);
        var added = await manager.AddAsync(new(server.BaseUrl + "/asymmetric"));
        Check(added.ResumeSupported == null && added.ConnectionLimit == 4 && added.Segments.Count == 0, "Probe-pending contract differs");
        DownloadJob? detached = null;
        await Wait(() =>
        {
            var j = Job(manager, added.Id); Coverage(j);
            if (j.Segments.Count > 4) { detached = j; return true; }
            return false;
        }, "No dynamic split occurred");
        Check(detached!.ResumeSupported == true, "Validated ranges not marked resumable");
        var frozen = detached.Segments.ToArray();
        var done = await Complete(manager, added.Id, server.Payload, 4);
        Check(done.Segments.Count > 4 && detached.Segments.SequenceEqual(frozen), "Snapshots were not detached");
        var list = detached.Segments as IList<DownloadSegmentSnapshot>;
        bool immutable = false;
        try { list![0] = new(0, 1, 0, false); } catch (NotSupportedException) { immutable = true; }
        Check(immutable, "Snapshot collection is mutable");
        Check(server.Ranges.Count(r => r.Path == "/asymmetric" && r.End > r.Start) > 4, "Split did not issue new HTTP ranges");
        Console.WriteLine($"DYNAMIC shards={done.Segments.Count} requests={server.Requests("/asymmetric")} client_cap=4");
    }

    public static async Task Restore((string Download, string State) p, bool change)
    {
        using var server = new FixtureServer(); string id; long savedBytes; int savedShards;
        using (var manager = Manager(p))
        {
            id = (await manager.AddAsync(new(server.BaseUrl + "/asymmetric"))).Id;
            await Wait(() => Job(manager, id).Segments.Count > 4, "No dynamic shards before pause");
            manager.Pause(id);
            await Wait(() => Job(manager, id).ActiveConnectionCount == 0, "Cancellation did not join owners");
            var paused = Job(manager, id); Coverage(paused);
            Check(paused.Status == "Paused" && paused.RemainingTime == null && paused.SpeedBytesPerSecond == 0, "Invalid paused telemetry");
            savedBytes = paused.DownloadedBytes; savedShards = paused.Segments.Count;
            await Task.Delay(100); Check(Job(manager, id).DownloadedBytes == savedBytes, "Paused bytes changed");
        }
        var records = JsonNode.Parse(File.ReadAllText(Path.Combine(p.State, "queue.json")))!.AsArray();
        var shards = records[0]!["Segments"]!.AsArray();
        Check(shards.Count == savedShards && shards.All(s => s!["PartId"] != null), "Dynamic ids not checkpointed");
        Check(shards.Select(s => s!["PartId"]!.GetValue<int>()).Distinct().Count() == shards.Count, "Duplicate part ownership");
        if (change) Volatile.Write(ref server.Version, 2);
        using var restored = Manager(p, cap: 2);
        var old = Job(restored, id); Coverage(old);
        Check(old.DownloadedBytes == savedBytes && old.Segments.Count == savedShards && old.ActiveConnectionCount == 0, "Dynamic topology/progress not restored");
        Check(old.ResumeSupported == null, "Restored unprobed job claims validated capability");
        restored.Resume(id);
        await Complete(restored, id, change ? server.Replacement : server.Payload, 2);
    }

    public static async Task Legacy((string Download, string State) p)
    {
        using var server = new FixtureServer(); string id; long saved;
        using (var manager = Manager(p, dynamic: false))
        {
            id = (await manager.AddAsync(new(server.BaseUrl + "/asymmetric"))).Id;
            await Wait(() => Job(manager, id).DownloadedBytes >= 65536, "No legacy progress");
            manager.Pause(id); await Wait(() => Job(manager, id).ActiveConnectionCount == 0, "Legacy pause did not stop");
            saved = Job(manager, id).DownloadedBytes;
        }
        var queue = Path.Combine(p.State, "queue.json");
        var records = JsonNode.Parse(File.ReadAllText(queue))!.AsArray();
        foreach (var s in records[0]!["Segments"]!.AsArray()) s!.AsObject().Remove("PartId");
        File.WriteAllText(queue, records.ToJsonString());
        var before = server.Ranges.Count;
        using var restored = Manager(p);
        Check(Job(restored, id).DownloadedBytes == saved, "Legacy progress lost");
        restored.Resume(id); await Complete(restored, id, server.Payload, 4);
        Check(server.Ranges.Skip(before).Any(r => r.End > 0 && r.Start % (server.Payload.LongLength / 4) != 0), "Legacy offsets were not reused");
    }

    public static async Task Telemetry((string Download, string State) p)
    {
        using var server = new FixtureServer(); using var manager = Manager(p, dynamic: false);
        var id = (await manager.AddAsync(new(server.BaseUrl + "/asymmetric"))).Id;
        await Wait(() => Job(manager, id).SpeedBytesPerSecond > 0, "No speed samples");
        var first = Job(manager, id);
        Check(double.IsFinite(first.SpeedBytesPerSecond) && first.RemainingTime > TimeSpan.Zero, "Invalid running speed/ETA");
        // After the three fast ranges finish, instantaneous throughput drops sharply.
        // The EWMA must retain history rather than report just the slow range sample.
        await Wait(() => Job(manager, id).Segments.Count(s => s.DownloadedBytes == s.Length) >= 3, "Fast ranges did not complete");
        var before = Job(manager, id); var clock = Stopwatch.StartNew();
        await Task.Delay(650);
        var after = Job(manager, id);
        var raw = (after.DownloadedBytes - before.DownloadedBytes) / clock.Elapsed.TotalSeconds;
        Check(after.Status == "Downloading" && after.SpeedBytesPerSecond > raw * 1.2, "Speed is a jittery instantaneous sample rather than smoothed history");
        Check(after.RemainingTime.HasValue && double.IsFinite(after.RemainingTime.Value.TotalSeconds), "ETA is not finite");
        var expected = (after.TotalBytes - after.DownloadedBytes) / after.SpeedBytesPerSecond;
        Check(Math.Abs(after.RemainingTime!.Value.TotalSeconds - expected) < .01, "ETA does not use smoothed speed");
        manager.Pause(id); await Wait(() => Job(manager, id).ActiveConnectionCount == 0, "Telemetry pause did not stop");
        Check(Job(manager, id).RemainingTime == null && Job(manager, id).SpeedBytesPerSecond == 0, "Pause did not clear ETA/speed");
        manager.Resume(id); await Complete(manager, id, server.Payload, 4);
    }

    public static async Task OneConnection((string Download, string State) p)
    {
        using var server = new FixtureServer(); using var manager = Manager(p, cap: 1);
        var id = (await manager.AddAsync(new(server.BaseUrl + "/range"))).Id;
        var done = await Complete(manager, id, server.Payload, 1);
        Check(done.Segments.Count == 1 && server.PeakRanges == 1 && done.ResumeSupported == true, "One connection setting ignored");
    }

    public static async Task LowRate((string Download, string State) p)
    {
        using var server = new FixtureServer(); using var manager = Manager(p);
        var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/asymmetric"), DateTimeOffset.UtcNow.AddHours(1));
        manager.SetSpeedLimit(j.Id, 1); manager.Schedule(j.Id, DateTimeOffset.UtcNow);
        await Wait(() => Job(manager, j.Id).Segments.Count == 4, "No bounded initial ranges");
        await Task.Delay(700);
        var slow = Job(manager, j.Id); Coverage(slow);
        Check(slow.DownloadedBytes == 0 && slow.RemainingTime == null && slow.SpeedBytesPerSecond == 0, "Very low throttle was bypassed or ETA invented");
        var clock = Stopwatch.StartNew(); manager.Pause(j.Id);
        await Wait(() => Job(manager, j.Id).ActiveConnectionCount == 0, "Low-rate pause deadlocked");
        Check(clock.Elapsed < TimeSpan.FromSeconds(1), "Low-rate cancellation unresponsive");
        manager.SetSpeedLimit(j.Id, 300000); clock.Restart(); manager.Resume(j.Id);
        await Complete(manager, j.Id, server.Payload, 4);
        Check(clock.Elapsed.TotalSeconds >= server.Payload.Length / 300000.0 - .2, "Aggregate rate exceeded during dynamic-enabled download");
    }

    public static async Task Fallback((string Download, string State) p)
    {
        using var server = new FixtureServer(); using var manager = Manager(p);
        var id = (await manager.AddAsync(new(server.BaseUrl + "/asymmetric-invalid"))).Id;
        await Wait(() => Job(manager, id).ResumeSupported == false, "Split rejection did not cause fallback");
        var fallback = Job(manager, id);
        Check(fallback.ActiveConnectionCount <= 1, "Range owners remained active during fallback");
        var completed = await Complete(manager, id, server.Payload, 4);
        Check(completed.Segments.Count == 1 && completed.ResumeSupported == false, "Fallback capability/topology invalid");
        Check(server.Ranges.Count(r => r.End > r.Start) > 4, "Fixture did not reject a dynamic range");
    }

    public static async Task Retry((string Download, string State) p)
    {
        using var server = new FixtureServer(); using var manager = Manager(p);
        var id = (await manager.AddAsync(new(server.BaseUrl + "/asymmetric-retry"))).Id;
        var completed = await Complete(manager, id, server.Payload, 4);
        Check(completed.Segments.Count > 4 && completed.ResumeSupported == true, "Dynamic retry discarded safe topology");
        Check(server.Ranges.Count(r => r.Start == 0 && r.End == 0) == 2, "Expected exactly one bounded retry/reprobe");
        Check(server.Requests("/asymmetric-retry") < 24, "Dynamic retry requests unbounded");
    }

    public static async Task StreamingTelemetry((string Download, string State) p)
    {
        using var server = new FixtureServer(); using var manager = Manager(p);
        foreach (var path in new[] { "/no-range", "/chunked" })
        {
            var id = (await manager.AddAsync(new(server.BaseUrl + path))).Id;
            await Wait(() => Job(manager, id).DownloadedBytes > 0, "No streaming progress");
            var running = Job(manager, id);
            Check(running.ResumeSupported == false && running.ActiveConnectionCount == 1 && running.Segments.Count == 1 && running.Segments[0].IsActive, "Streaming ownership/capability fabricated: " + path + " resume=" + running.ResumeSupported + " active=" + running.ActiveConnectionCount + " segs=" + running.Segments.Count + " status=" + running.Status + " dl=" + running.DownloadedBytes);
            if (path == "/chunked")
                Check(running.TotalBytes == 0 && running.Segments[0].Length == 0 && running.RemainingTime == null, "Unknown-length stream invented an ETA/length");
            await Wait(() => Job(manager, id).Status is "Completed" or "Failed", "Streaming not complete");
            var completed = Job(manager, id);
            Check(completed.Status == "Completed" && completed.ActiveConnectionCount == 0 && completed.RemainingTime == TimeSpan.Zero && completed.ResumeSupported == false, "Streaming completion telemetry invalid");
            Check(FixtureServer.Hash(File.ReadAllBytes(completed.FilePath)) == FixtureServer.Hash(server.Payload), "Streaming SHA-256 differs");
        }
    }

    public static Task Options((string Download, string State) p)
    {
        using (var allowed = new DownloadManager(p.Download, p.State, new DownloadOptions { MaxSegments = 32, MaxConcurrentDownloads = 16, RetryCount = 8 })) { }
        var invalid = new[]
        {
            new DownloadOptions { MaxSegments = 0 }, new DownloadOptions { MaxSegments = 33 },
            new DownloadOptions { MaxConcurrentDownloads = 0 }, new DownloadOptions { MaxConcurrentDownloads = 17 },
            new DownloadOptions { RetryCount = -1 }, new DownloadOptions { RetryCount = 9 },
            new DownloadOptions { MinimumSegmentSize = 65535 },
            new DownloadOptions { RequestTimeout = TimeSpan.FromMilliseconds(99) },
            new DownloadOptions { RequestTimeout = TimeSpan.FromMinutes(6) }
        };
        foreach (var options in invalid)
        {
            bool refused = false;
            try { using var manager = new DownloadManager(p.Download, p.State, options); }
            catch (ArgumentOutOfRangeException) { refused = true; }
            Check(refused, "Invalid option accepted");
        }
        Check(new DownloadOptions().EnableDynamicSegmentation, "Dynamic segmentation not enabled by default");
        return Task.CompletedTask;
    }

    public static async Task Benchmark(string root)
    {
        // Same payload, same range-start-dependent bandwidth and same cap. Alternate
        // order to reduce warm-up/order bias. No Internet or machine-wide tuning.
        Console.WriteLine("BENCH fixture=loopback asymmetric bytes=2097265 cap=4 min_shard=65536 chunk=8192 slow_delay_ms=40 fast_delay_ms=3");
        var results = new List<(bool Dynamic, double Seconds)>();
        for (var round = 1; round <= 3; round++)
        foreach (var dynamic in round % 2 == 1 ? new[] { false, true } : new[] { true, false })
        {
            using var server = new FixtureServer();
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            using var manager = Manager((Path.Combine(directory, "downloads"), Path.Combine(directory, "state")), dynamic);
            var clock = Stopwatch.StartNew();
            var id = (await manager.AddAsync(new(server.BaseUrl + "/asymmetric"))).Id;
            var done = await Complete(manager, id, server.Payload, 4);
            var seconds = clock.Elapsed.TotalSeconds;
            results.Add((dynamic, seconds));
            Console.WriteLine($"BENCH round={round} mode={(dynamic ? "dynamic" : "static")} duration_s={seconds:F6} throughput_Bps={server.Payload.Length / seconds:F2} shards={done.Segments.Count} data_requests={server.Ranges.Count(r => r.End > r.Start)} sha256={FixtureServer.Hash(server.Payload)}");
        }
        var staticMean = results.Where(r => !r.Dynamic).Average(r => r.Seconds);
        var dynamicMean = results.Where(r => r.Dynamic).Average(r => r.Seconds);
        Console.WriteLine($"BENCH mean_static_s={staticMean:F6} mean_dynamic_s={dynamicMean:F6} speedup={staticMean / dynamicMean:F3}x duration_reduction_pct={(1 - dynamicMean / staticMean) * 100:F2}");
    }
}
