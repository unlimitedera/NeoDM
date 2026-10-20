using System.Diagnostics;
using NDM.Core;
using NDM.Tests;

var root = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch"), "ndm-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var failures = new List<string>(); var passed = 0;
if (args.Contains("--benchmark"))
{
    try { await DynamicTests.Benchmark(root); return 0; }
    finally { Directory.Delete(root, true); }
}
var tests = new (string Name, Func<Task> Run)[]
{
    ("parallel validated ranges and exact SHA-256", Ranges),
    ("no-range streaming and request headers", Streaming),
    ("chunked unknown-length streaming and empty file", UnknownAndEmpty),
    ("pause/resume retains validated range offsets", PauseResume),
    ("persistent state restore and exclusive ownership", Restore),
    ("changed validator restarts without mixed bytes", ChangedValidator),
    ("invalid range / If-Range rejection falls back safely", BadRanges),
    ("missing or unsafe date validator streams; strong date segments", Validators),
    ("bounded retries succeed and failures remain unpublished", RetryFailure),
    ("truncated response never publishes and retries are bounded", Truncated),
    ("malicious filenames and collision protection", Names),
    ("remove keeps completed files and deletes partials", Remove),
    ("job concurrency bound and speed limit", Limits),
    ("scheduling persists and starts at due time", Scheduling),
    ("detached snapshots and bad input rejection", Input),
    ("corrupt queue fails closed without data loss", Corrupt),
    ("streaming pause restarts cleanly", StreamingPause),
    ("range retry uses validated offsets", RangeRetry),
    ("live removal of very low speed limit", LiveSpeedChange),
    ("redirects bound cookies and final validator scope", Redirects),
    ("observer can dispose manager without deadlock", ObserverDispose),
    ("dynamic splits preserve coverage, hash and live cap", () => DynamicTests.Split(Paths())),
    ("dynamic shards pause/restore at a smaller connection cap", () => DynamicTests.Restore(Paths(), false)),
    ("dynamic shard restore rejects changed validators", () => DynamicTests.Restore(Paths(), true)),
    ("legacy index part files migrate without lost progress", () => DynamicTests.Legacy(Paths())),
    ("smoothed speed and finite ETA telemetry", () => DynamicTests.Telemetry(Paths())),
    ("one connection disables splitting and respects telemetry", () => DynamicTests.OneConnection(Paths())),
    ("dynamic low rate stays bounded and pause stays responsive", () => DynamicTests.LowRate(Paths())),
    ("expanded settings validate exact bounds", () => DynamicTests.Options(Paths())),
    ("dynamic split rejection joins owners before streaming fallback", () => DynamicTests.Fallback(Paths())),
    ("dynamic shard truncation retries validated checkpoint offsets", () => DynamicTests.Retry(Paths())),
    ("streaming and unknown-length telemetry reports actual ownership", () => DynamicTests.StreamingTelemetry(Paths())),
    ("HTTP proxy carries downloads with auth; bad login fails without leaking", () => ManageTests.HttpProxy(Paths)),
    ("SOCKS5 proxy: remote and local name resolution, auth", () => ManageTests.Socks5Proxy(Paths)),
    ("CONNECT tunnel is byte-exact and reports 407", () => ManageTests.HttpsTunnelThroughConnect(Paths)),
    ("proxy test button: success, refused, missing credentials, validation", () => ManageTests.ProxyTestButton(Paths)),
    ("remove can delete only the job's own file; missing/locked reported", () => ManageTests.RemoveWithFile(Paths)),
    ("move/rename updates disk and job, persists, rejects unsafe targets", () => ManageTests.MoveRename(Paths)),
    ("refresh address keeps validated progress", () => ManageTests.RefreshAddress(Paths)),
    ("redownload restarts without touching the old file", () => ManageTests.Redownload(Paths)),
    ("change save location: partial, running, completed, collision, bad destinations, same folder", () => ManageTests.MoveTo(Paths)),
    ("file names: Content-Disposition / URL / query / MIME / magic-number parsing", NameTests.Parsing),
    ("file names: video requests from the extension (signed, extensionless, long, post page)", NameTests.MediaRequests),
    ("file names: priority, extension repair, GUID/empty fallback", NameTests.Decisions),
    ("file names: real downloads resolve the right name (disposition, redirects, type, magic, HEAD 405, collisions)", () => NameTests.Downloads(Paths())),
    ("file names: pre-download probe (HEAD, ranged-GET fallback, redirects, size/type)", () => NameTests.Probe(Paths())),
    ("1.5.3 cookie at rest: protected in queue.json, restored, never plain text", () => SecurityTests.CookieAtRest(Paths())),
    ("1.5.3 cookie: legacy plain text migrates; failed and removed jobs keep nothing", () => SecurityTests.CookieMigrationAndClearing(Paths())),
    ("1.5.3 Mark of the Web: exact Zone.Identifier stream, sanitized addresses", () => SecurityTests.MarkOfTheWebStream(Paths())),
    ("1.5.3 Mark of the Web: skipped without ADS support, off by setting", () => SecurityTests.MarkOfTheWebSkippedAndOff(Paths())),
    ("1.5.3 redirects: https to http refused, Referer and cookie dropped across origins", () => SecurityTests.RedirectHardening(Paths())),
    ("1.5.3 file names: bidi and zero-width removed, desktop.ini/autorun.inf/CLOCK$/superscript COM reserved", SecurityTests.FileNames),
    ("1.5.3 disk space: declared length, staging overhead, floor for unknown length, mid-stream check", () => SecurityTests.DiskSpace(Paths())),
    ("1.5.3 read caps: queue.json 64 MB and settings 1 MB, file preserved", () => SecurityTests.ReadCaps(Paths())),
    ("1.5.3 queue.json shape: request cookie is null in the file", () => SecurityTests.QueueJsonStillValid(Paths()))
};
foreach (var test in tests)
{
    var clock = Stopwatch.StartNew();
    try { await test.Run(); passed++; Console.WriteLine($"PASS {test.Name} ({clock.ElapsedMilliseconds} ms)"); }
    catch (Exception ex) { failures.Add(test.Name); Console.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"RESULT {passed}/{tests.Length} passed; {failures.Count} failed");
Console.WriteLine($"Artifacts: {root}");
if (failures.Count == 0) Directory.Delete(root, true);
return failures.Count == 0 ? 0 : 1;

(string Download, string State) Paths()
{
    var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
    return (Path.Combine(directory, "downloads"), Path.Combine(directory, "state"));
}
DownloadManager Manager((string Download, string State) paths, int concurrent = 2, int retry = 2) => new(paths.Download, paths.State, new DownloadOptions { MaxConcurrentDownloads = concurrent, MaxSegments = 4, MinimumSegmentSize = 65536, RetryCount = retry, RequestTimeout = TimeSpan.FromSeconds(3) });
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Wait(Func<bool> condition, string message, int seconds = 15)
{
    var clock = Stopwatch.StartNew();
    while (!condition())
    {
        if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message);
        await Task.Delay(20);
    }
}
DownloadJob Job(DownloadManager manager, string id) => manager.Jobs.Single(j => j.Id == id);
async Task<DownloadJob> Complete(DownloadManager manager, string id)
{
    await Wait(() => Job(manager, id).Status is "Completed" or "Failed", "Download did not finish");
    var job = Job(manager, id);
    Check(job.Status == "Completed", "Unexpected failure: " + job.Error);
    Check(job.Progress == 100 && job.TotalBytes == job.DownloadedBytes, "Invalid completed progress");
    return job;
}
void Exact(DownloadJob job, byte[] expected) => Check(FixtureServer.Hash(File.ReadAllBytes(job.FilePath)) == FixtureServer.Hash(expected), "Downloaded file SHA-256 differs");

async Task Ranges()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var added = await manager.AddAsync(new(server.BaseUrl + "/range", "ranges.bin"));
    var job = await Complete(manager, added.Id); Exact(job, server.Payload);
    Check(server.PeakRanges is > 1 and <= 4, "Expected bounded parallel ranges");
    Check(server.Ranges.Count(r => r.Path == "/range" && r.End > r.Start) == 4, "Expected four data ranges");
}
async Task Streaming()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var job = await manager.AddAsync(new(server.BaseUrl + "/no-range", Referer: server.BaseUrl + "/page", Cookie: "session=fixture"));
    Exact(await Complete(manager, job.Id), server.Payload);
    Check(server.Ranges.Count == 0 && server.Requests("/no-range") == 2, "Expected probe then full stream");
    Check(server.Headers.Where(h => h.Path == "/no-range").All(h => h.Cookie == "session=fixture" && h.Referer == server.BaseUrl + "/page"), "Header forwarding failed");
}
async Task UnknownAndEmpty()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var chunked = await manager.AddAsync(new(server.BaseUrl + "/chunked"));
    Exact(await Complete(manager, chunked.Id), server.Payload);
    var empty = await manager.AddAsync(new(server.BaseUrl + "/empty"));
    Exact(await Complete(manager, empty.Id), Array.Empty<byte>());
}
async Task PauseResume()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
    manager.SetSpeedLimit(j.Id, 400000); manager.Schedule(j.Id, DateTimeOffset.UtcNow);
    await Wait(() => Job(manager, j.Id).DownloadedBytes >= 65536, "No download progress");
    manager.Pause(j.Id); await Task.Delay(250);
    var paused = Job(manager, j.Id); var before = server.Ranges.Count;
    Check(paused.Status == "Paused" && paused.DownloadedBytes < paused.TotalBytes, "Pause failed");
    await Task.Delay(200); Check(Job(manager, j.Id).DownloadedBytes == paused.DownloadedBytes, "Paused transfer still advanced");
    manager.SetSpeedLimit(j.Id, null); manager.Resume(j.Id);
    Exact(await Complete(manager, j.Id), server.Payload);
    Check(server.Ranges.Skip(before).Any(r => r.End > 0 && r.Start % (server.Payload.LongLength / 4) != 0), "Resume did not use saved range offsets");
}
async Task Restore()
{
    using var server = new FixtureServer(); var paths = Paths(); string id;
    using (var manager = Manager(paths))
    {
        var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1)); id = j.Id;
        manager.SetSpeedLimit(id, 400000); manager.Schedule(id, DateTimeOffset.UtcNow);
        await Wait(() => Job(manager, id).DownloadedBytes >= 65536, "No progress before restore");
        bool refused = false; try { using var other = Manager(paths); } catch (IOException) { refused = true; }
        Check(refused, "Second owner was not refused");
    }
    Check(File.Exists(Path.Combine(paths.State, "queue.json")), "Queue not persisted");
    using (var manager = Manager(paths))
    {
        Check(Job(manager, id).Status == "Paused" && Job(manager, id).DownloadedBytes > 0, "Interrupted job did not restore safely");
        manager.SetSpeedLimit(id, null); manager.Resume(id); Exact(await Complete(manager, id), server.Payload);
    }
    using var restored = Manager(paths);
    Check(Job(restored, id).Status == "Completed", "Completed state did not restore"); Exact(Job(restored, id), server.Payload);
}
async Task ChangedValidator()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
    manager.SetSpeedLimit(j.Id, 400000); manager.Schedule(j.Id, DateTimeOffset.UtcNow);
    await Wait(() => Job(manager, j.Id).DownloadedBytes >= 65536, "No progress"); manager.Pause(j.Id); await Task.Delay(250);
    Volatile.Write(ref server.Version, 2); manager.SetSpeedLimit(j.Id, null); manager.Resume(j.Id);
    Exact(await Complete(manager, j.Id), server.Replacement);
}
async Task BadRanges()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var invalid = await manager.AddAsync(new(server.BaseUrl + "/invalid-range")); Exact(await Complete(manager, invalid.Id), server.Payload);
    var changed = await manager.AddAsync(new(server.BaseUrl + "/changed-segment")); Exact(await Complete(manager, changed.Id), server.Replacement);
}
async Task Validators()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    foreach (var path in new[] { "/no-validator", "/unsafe-date", "/weak", "/date-validator" })
    {
        var j = await manager.AddAsync(new(server.BaseUrl + path)); Exact(await Complete(manager, j.Id), server.Payload);
        var dataRanges = server.Ranges.Count(r => r.Path == path && r.End > 0);
        Check(path == "/date-validator" ? dataRanges == 4 : dataRanges == 0, "Unsafe validator handling for " + path);
    }
}
async Task RetryFailure()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths(), retry: 2);
    var retry = await manager.AddAsync(new(server.BaseUrl + "/retry")); Exact(await Complete(manager, retry.Id), server.Payload);
    Check(server.Requests("/retry") == 7, "Unexpected retry count");
    var failed = await manager.AddAsync(new(server.BaseUrl + "/fail"));
    await Wait(() => Job(manager, failed.Id).Status == "Failed", "Failure not reported");
    Check(server.Requests("/fail") == 3 && Job(manager, failed.Id).Error != null && !File.Exists(failed.FilePath), "Failure retries or publication unsafe");
}
async Task Truncated()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths(), retry: 1);
    var j = await manager.AddAsync(new(server.BaseUrl + "/truncate"));
    await Wait(() => Job(manager, j.Id).Status == "Failed", "Truncated response did not fail");
    Check(server.Requests("/truncate") == 4 && !File.Exists(j.FilePath), "Truncation retries/publication unsafe");
}
async Task Names()
{
    using var server = new FixtureServer(); var paths = Paths(); using var manager = Manager(paths);
    File.WriteAllText(Path.Combine(paths.Download, "same.bin"), "existing");
    var same = await manager.AddAsync(new(server.BaseUrl + "/range", "same.bin"));
    var duplicate = await manager.AddAsync(new(server.BaseUrl + "/range", "same.bin"));
    Check(same.FileName != duplicate.FileName && same.FileName != "same.bin", "Collision handling failed");
    Exact(await Complete(manager, same.Id), server.Payload); Exact(await Complete(manager, duplicate.Id), server.Payload);
    Check(File.ReadAllText(Path.Combine(paths.Download, "same.bin")) == "existing", "Existing destination overwritten");
    foreach (var malicious in new[] { "../../escape.exe", "..\\..\\escape.exe", "CON.txt", "LPT1", "name:stream", "trailing. ", "\r\n/*?\0", "...", new string('x', 400) + ".bin" })
    {
        var job = await manager.AddScheduledAsync(new(server.BaseUrl + "/range", malicious), DateTimeOffset.UtcNow.AddHours(1));
        Check(Path.GetDirectoryName(job.FilePath) == Path.GetFullPath(paths.Download) && job.FileName.Length <= 180 && !job.FileName.EndsWith('.') && !job.FileName.EndsWith(' ') && job.FileName.IndexOfAny("<>:\"/\\|?*".ToCharArray()) == -1, "Unsafe filename: " + job.FileName);
        Check(job.FileName is not ("CON.txt" or "LPT1"), "Reserved device name not escaped");
    }
    // A different process may create the reserved name during the transfer.
    var late = await manager.AddScheduledAsync(new(server.BaseUrl + "/range", "late.bin"), DateTimeOffset.UtcNow.AddHours(1));
    File.WriteAllText(late.FilePath, "late-existing");
    var unrelated = Path.Combine(paths.Download, ".ndm-" + late.Id + ".tmp");
    File.WriteAllText(unrelated, "unrelated"); manager.Schedule(late.Id, DateTimeOffset.UtcNow);
    var done = await Complete(manager, late.Id); Exact(done, server.Payload);
    Check(done.FilePath != late.FilePath && File.ReadAllText(late.FilePath) == "late-existing", "Late collision overwritten");
    manager.Remove(late.Id); Check(File.ReadAllText(unrelated) == "unrelated", "Staging cleanup touched an unrelated destination file");
}
async Task Remove()
{
    using var server = new FixtureServer(); var paths = Paths(); using var manager = Manager(paths);
    var completed = await manager.AddAsync(new(server.BaseUrl + "/range")); var done = await Complete(manager, completed.Id);
    manager.Remove(done.Id); Check(File.Exists(done.FilePath) && manager.Jobs.Count == 0, "Remove deleted completed file");
    var partial = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
    manager.SetSpeedLimit(partial.Id, 400000); manager.Schedule(partial.Id, DateTimeOffset.UtcNow);
    await Wait(() => Job(manager, partial.Id).DownloadedBytes > 0, "No partial progress"); manager.Remove(partial.Id);
    await Wait(() => !Directory.Exists(Path.Combine(paths.State, partial.Id + ".parts")), "Partial directory not removed");
    Check(!File.Exists(partial.FilePath) && manager.Jobs.Count == 0, "Removed partial published");
}
async Task Limits()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths(), concurrent: 1);
    var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
    manager.SetSpeedLimit(j.Id, 512000); manager.Schedule(j.Id, DateTimeOffset.UtcNow);
    var clock = Stopwatch.StartNew(); var second = await manager.AddAsync(new(server.BaseUrl + "/range"));
    await Task.Delay(300); Check(Job(manager, second.Id).Status == "Queued", "Concurrent job limit exceeded");
    Exact(await Complete(manager, j.Id), server.Payload); Check(clock.Elapsed >= TimeSpan.FromSeconds(3.5), "Aggregate speed limit not enforced");
    Exact(await Complete(manager, second.Id), server.Payload);
    Check(server.PeakRanges <= 4, "Concurrent connections exceeded segment/job bounds");
}
async Task Scheduling()
{
    using var server = new FixtureServer(); var paths = Paths(); string id;
    var due = DateTimeOffset.UtcNow.AddSeconds(1.3);
    using (var manager = Manager(paths))
    {
        var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), due); id = j.Id;
        await Task.Delay(250); Check(server.Requests("/range") == 0 && Job(manager, id).Status == "Scheduled", "Scheduled job started early");
    }
    using var restored = Manager(paths);
    Check(Job(restored, id).ScheduledAt == due, "Schedule not persisted"); Exact(await Complete(restored, id), server.Payload);
}
async Task Input()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    foreach (var request in new[] { new DownloadRequest("file:///C:/secret"), new DownloadRequest("not a URL"), new DownloadRequest(server.BaseUrl, Cookie: "a=b\r\nInjected: x"), new DownloadRequest(server.BaseUrl, Referer: "file:///test") })
    {
        bool rejected = false; try { await manager.AddAsync(request); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Bad input not rejected");
    }
    var added = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
    manager.Resume(added.Id); Check(added.Status == "Scheduled", "Snapshots should be detached");
    manager.Pause(added.Id); Check(added.Status == "Scheduled" && Job(manager, added.Id).Status == "Paused", "Snapshot mutated");
    manager.Changed += () => throw new Exception("observer error");
    manager.Schedule(added.Id, DateTimeOffset.UtcNow); Exact(await Complete(manager, added.Id), server.Payload);
}
Task Corrupt()
{
    var paths = Paths(); Directory.CreateDirectory(paths.State); var queue = Path.Combine(paths.State, "queue.json");
    File.WriteAllText(queue, "{broken"); bool rejected = false;
    try { using var manager = Manager(paths); } catch (InvalidDataException) { rejected = true; }
    Check(rejected && File.ReadAllText(queue) == "{broken", "Corrupt queue was silently discarded"); return Task.CompletedTask;
}
async Task StreamingPause()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/no-range"), DateTimeOffset.UtcNow.AddHours(1));
    manager.SetSpeedLimit(j.Id, 400000); manager.Schedule(j.Id, DateTimeOffset.UtcNow);
    await Wait(() => Job(manager, j.Id).DownloadedBytes >= 65536, "No streaming progress"); manager.Pause(j.Id); await Task.Delay(250);
    manager.SetSpeedLimit(j.Id, null); manager.Resume(j.Id); Exact(await Complete(manager, j.Id), server.Payload);
    Check(server.Requests("/no-range") == 4, "Streaming resume should re-probe and restart from zero");
}
async Task RangeRetry()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths(), retry: 1);
    var j = await manager.AddAsync(new(server.BaseUrl + "/range-retry")); Exact(await Complete(manager, j.Id), server.Payload);
    Check(server.Ranges.Any(r => r.Path == "/range-retry" && r.Start > 0 && r.Start % (server.Payload.LongLength / 4) != 0), "Retry did not preserve validated partial ranges");
    Check(server.Requests("/range-retry") <= 10, "Range retries unbounded");
}
async Task LiveSpeedChange()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
    manager.SetSpeedLimit(j.Id, 1); manager.Schedule(j.Id, DateTimeOffset.UtcNow);
    await Wait(() => Job(manager, j.Id).TotalBytes > 0, "Probe did not finish"); await Task.Delay(300);
    Check(Job(manager, j.Id).DownloadedBytes == 0, "One byte/second limit bypassed");
    manager.SetSpeedLimit(j.Id, null); Exact(await Complete(manager, j.Id), server.Payload);
}
async Task Redirects()
{
    using var origin = new FixtureServer(); using var target = new FixtureServer(); using var manager = Manager(Paths(), retry: 0);
    origin.RedirectTarget = target.BaseUrl;
    var j = await manager.AddAsync(new(origin.BaseUrl + "/redirect", Cookie: "secret=original")); Exact(await Complete(manager, j.Id), target.Payload);
    Check(origin.Headers.All(h => h.Cookie == "secret=original") && target.Headers.All(h => h.Cookie == null), "Cookie leaked across redirect origins");
    var switched = await manager.AddAsync(new(origin.BaseUrl + "/redirect-switch")); Exact(await Complete(manager, switched.Id), origin.Replacement);
    var loop = await manager.AddAsync(new(origin.BaseUrl + "/redirect-loop")); await Wait(() => Job(manager, loop.Id).Status == "Failed", "Redirect loop did not fail");
    Check(origin.Requests("/redirect-loop") == 10, "Redirect limit not enforced");
}
async Task ObserverDispose()
{
    using var server = new FixtureServer(); var manager = Manager(Paths());
    var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    manager.Changed += () => { manager.Dispose(); done.TrySetResult(); };
    manager.Schedule(j.Id, DateTimeOffset.UtcNow);
    await done.Task.WaitAsync(TimeSpan.FromSeconds(5)); manager.Dispose();
}
