// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Diagnostics;
using NDM.Core;

namespace NDM.Tests;

/// <summary>2.0.0 stage S1 engine tests: cancel cleanup, the Processing step, the temporary files folder, free space, duplicates, 64 segments.</summary>
internal static class V2000Tests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Wait(Func<bool> condition, string message, int seconds = 30)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message); await Task.Delay(20); }
    }
    private sealed class Tweaks { public int Segments = 4; public Func<string, long?>? Free; public string? Temp; public long Interval = 16L * 1024 * 1024; }
    private static DownloadManager Manager(string download, string state, Action<Tweaks>? tweak = null)
    {
        var t = new Tweaks(); tweak?.Invoke(t);
        return new DownloadManager(download, state, new DownloadOptions { MaxSegments = t.Segments, MinimumSegmentSize = 65536, RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(5), FreeSpace = t.Free, TemporaryFolder = t.Temp, SpaceCheckIntervalBytes = t.Interval });
    }
    private static DownloadJob Job(DownloadManager m, string id) => m.Jobs.Single(j => j.Id == id);
    private static string WorkOf(string folder, string id) => Path.Combine(folder, "." + id + ".ndmpart");

    /// <summary>Item 1: cancelling (Remove) a running and a paused download removes every byte of partial data; the partial size is known first.</summary>
    public static async Task CancelCleansUp((string Download, string State) p)
    {
        using var server = new FixtureServer();
        using var m = Manager(p.Download, p.State);
        var a = await m.AddScheduledAsync(new(server.BaseUrl + "/range", "cancel-run.bin"), DateTimeOffset.UtcNow.AddHours(1));
        m.SetSpeedLimit(a.Id, 600000); m.Schedule(a.Id, DateTimeOffset.UtcNow);
        await Wait(() => Job(m, a.Id).DownloadedBytes >= 131072, "No progress");
        var work = WorkOf(p.Download, a.Id);
        Check(Directory.Exists(work), "The hidden work folder exists while downloading");
        Check(m.PartialBytes(a.Id) > 0, "The partial size is known while downloading");
        m.Remove(a.Id);
        await Wait(() => !Directory.Exists(work), "Cancel left the hidden work folder behind");
        Check(Directory.GetDirectories(p.Download, "*.ndmpart").Length == 0, "No hidden folder is left in the destination");

        var b = await m.AddScheduledAsync(new(server.BaseUrl + "/range", "cancel-paused.bin"), DateTimeOffset.UtcNow.AddHours(1));
        m.SetSpeedLimit(b.Id, 600000); m.Schedule(b.Id, DateTimeOffset.UtcNow);
        await Wait(() => Job(m, b.Id).DownloadedBytes >= 131072, "No progress (paused case)");
        m.Pause(b.Id); await Wait(() => Job(m, b.Id).ActiveConnectionCount == 0, "Pause"); await Task.Delay(300);
        var sizeBefore = m.PartialBytes(b.Id);
        Check(sizeBefore >= 131072, "A paused download reports its partial size: " + sizeBefore);
        m.Remove(b.Id);
        await Wait(() => !Directory.Exists(WorkOf(p.Download, b.Id)), "Removing a paused download deletes its hidden folder");
    }

    /// <summary>Item 1: a file that is still open when the download is cancelled no longer leaves the folder behind: the delete is retried; a folder that stays locked is remembered and cleaned later.</summary>
    public static async Task LockedPartsAreRetried((string Download, string State) p)
    {
        using var server = new FixtureServer();
        using var m = Manager(p.Download, p.State);
        var a = await m.AddScheduledAsync(new(server.BaseUrl + "/range", "locked.bin"), DateTimeOffset.UtcNow.AddHours(1));
        m.SetSpeedLimit(a.Id, 600000); m.Schedule(a.Id, DateTimeOffset.UtcNow);
        await Wait(() => Job(m, a.Id).DownloadedBytes >= 131072, "No progress");
        var work = WorkOf(p.Download, a.Id);
        m.Pause(a.Id); await Wait(() => Job(m, a.Id).ActiveConnectionCount == 0, "Pause"); await Task.Delay(300);
        var held = Path.Combine(work, "parts", "held.lock"); File.WriteAllText(held, "x");
        var hold = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None); // another program (a scanner) holds a file inside
        try
        {
            m.Remove(a.Id);
            Check(Directory.Exists(work), "The delete cannot succeed while a file inside is held (the old engine stopped here, silently)");
            Check(m.FindOrphanedPartials().Any(o => o.Path == work), "A folder the queue no longer owns is listed as partial data to clean up");
        }
        finally { hold.Dispose(); }
        var swept = m.CleanOrphanedPartials();
        Check(swept.Folders >= 1 && swept.Bytes > 0 && !Directory.Exists(work), "The clean-up button removes it once the file is free: " + swept);

        // the handle is released a moment after Cancel: the engine's own retry removes the folder
        var b = await m.AddScheduledAsync(new(server.BaseUrl + "/range", "late.bin"), DateTimeOffset.UtcNow.AddHours(1));
        m.SetSpeedLimit(b.Id, 600000); m.Schedule(b.Id, DateTimeOffset.UtcNow);
        await Wait(() => Job(m, b.Id).DownloadedBytes >= 131072, "No progress (late)");
        var workB = WorkOf(p.Download, b.Id);
        var extra = Path.Combine(workB, "parts", "extra.lock"); File.WriteAllText(extra, "x");
        var heldB = new FileStream(extra, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(500); heldB.Dispose(); });
        m.Remove(b.Id);
        await Wait(() => !Directory.Exists(workB), "The retry should remove the folder after the handle closes", 20);
        await release;
    }

    /// <summary>Item 1: folders named like ours that no download owns are found and removed; owned folders and anything else are never touched.</summary>
    public static async Task StartupSweep((string Download, string State) p)
    {
        using var server = new FixtureServer();
        var temp = Path.Combine(Path.GetDirectoryName(p.Download)!, "tmp-sweep");
        using var m = Manager(p.Download, p.State, i => i.Temp = temp);
        var owned = await m.AddScheduledAsync(new(server.BaseUrl + "/range", "owned.bin"), DateTimeOffset.UtcNow.AddHours(1));
        var ownedWork = WorkOf(p.Download, owned.Id); Directory.CreateDirectory(Path.Combine(ownedWork, "parts")); File.WriteAllBytes(Path.Combine(ownedWork, "parts", "0.part"), new byte[1000]);
        var orphan1 = WorkOf(p.Download, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(orphan1, "parts")); File.WriteAllBytes(Path.Combine(orphan1, "parts", "0.part"), new byte[5000]);
        var orphan2 = WorkOf(temp, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(orphan2); File.WriteAllBytes(Path.Combine(orphan2, "assemble.tmp"), new byte[7000]);
        var notOurs = Path.Combine(p.Download, ".backup.ndmpart"); Directory.CreateDirectory(notOurs); File.WriteAllText(Path.Combine(notOurs, "keep.txt"), "mine");
        var userFolder = Path.Combine(p.Download, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(userFolder); File.WriteAllText(Path.Combine(userFolder, "keep.txt"), "mine");
        var found = m.FindOrphanedPartials();
        Check(found.Count == 2 && found.Sum(f => f.Bytes) == 12000, "Exactly the two unowned folders are found: " + string.Join(",", found.Select(f => f.Path + ":" + f.Bytes)));
        var result = m.CleanOrphanedPartials();
        Check(result.Folders == 2 && result.Bytes == 12000, "Both are removed: " + result);
        Check(!Directory.Exists(orphan1) && !Directory.Exists(orphan2), "The orphan folders are gone");
        var finished = await m.AddAsync(new(server.BaseUrl + "/range", "finished.bin"));
        await Wait(() => Job(m, finished.Id).Status is "Completed" or "Failed", "A download to finish");
        await Wait(() => !Directory.Exists(WorkOf(p.Download, finished.Id)), "The engine removes its own work folder");
        var leftOver = WorkOf(p.Download, finished.Id); Directory.CreateDirectory(leftOver); File.WriteAllBytes(Path.Combine(leftOver, "assemble.tmp"), new byte[300]);
        Check(m.FindOrphanedPartials().Any(f => f.Path == leftOver) && m.CleanOrphanedPartials().Folders == 1 && !Directory.Exists(leftOver), "A work folder left behind by a finished download is swept too");
        Check(Directory.Exists(ownedWork) && Directory.Exists(notOurs) && File.Exists(Path.Combine(userFolder, "keep.txt")), "An owned work folder and anything not named like ours stay");
    }

    /// <summary>Item 2: the last step reports Processing with percent, speed and time left, deletes each part as it is copied, ignores Pause, and Remove cleans everything.</summary>
    public static async Task ProcessingStep((string Download, string State) p)
    {
        using var server = new FixtureServer();
        using var m = Manager(p.Download, p.State);
        m.ProcessingPace = async (bytes, token) => await Task.Delay(600, token);
        var j = await m.AddAsync(new(server.BaseUrl + "/range", "joined.bin"));
        await Wait(() => Job(m, j.Id).Status is "Processing" or "Completed" or "Failed", "The job never reached Processing");
        var first = Job(m, j.Id);
        Check(first.Status == "Processing", "The status word is Processing, got " + first.Status + " " + first.Error);
        Check(first.TotalBytes == server.Payload.Length, "Processing counts the whole file: " + first.TotalBytes);
        var work = WorkOf(p.Download, j.Id);
        var partsBefore = Directory.GetFiles(Path.Combine(work, "parts"), "*.part").Length;
        long lastDone = 0; var sawSpeed = false; var sawEta = false; var sawFewerParts = false;
        var clock = Stopwatch.StartNew();
        while (Job(m, j.Id).Status == "Processing" && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            var s = Job(m, j.Id);
            Check(s.ProcessedBytes >= lastDone, "Processing progress never goes back"); lastDone = s.ProcessedBytes; Check(s.DownloadedBytes == server.Payload.Length, "The downloaded size stays complete while Processing");
            if (s.SpeedBytesPerSecond > 0) sawSpeed = true;
            if (s.RemainingTime is { } r && r > TimeSpan.Zero) sawEta = true;
            if (Directory.Exists(Path.Combine(work, "parts")) && Directory.GetFiles(Path.Combine(work, "parts"), "*.part").Length < partsBefore) sawFewerParts = true;
            m.Pause(j.Id); // refused while Processing
            await Task.Delay(40);
        }
        var done = Job(m, j.Id);
        Check(done.Status == "Completed", "Pause during Processing must not stop it: " + done.Status + " " + done.Error);
        Check(sawSpeed && sawEta, "Speed and time left are shown while Processing: speed=" + sawSpeed + " eta=" + sawEta);
        Check(lastDone > 0 && Job(m, j.Id).ProcessedBytes == 0, "Processed bytes reset after completion");
        Check(sawFewerParts, "Each part is deleted as soon as it is copied (" + partsBefore + " parts at the start)");
        Check(FixtureServer.Hash(File.ReadAllBytes(done.FilePath)) == FixtureServer.Hash(server.Payload), "The joined file is byte-identical");
        await Wait(() => !Directory.Exists(work), "Work folder removed after publishing");

        var k = await m.AddAsync(new(server.BaseUrl + "/range", "cancelled-join.bin"));
        await Wait(() => Job(m, k.Id).Status is "Processing" or "Completed" or "Failed", "The second job never reached Processing");
        Check(Job(m, k.Id).Status == "Processing", "second job status " + Job(m, k.Id).Status);
        var workK = WorkOf(p.Download, k.Id);
        await Wait(() => File.Exists(Path.Combine(workK, "assemble.tmp")), "The staging file exists during the join");
        m.Remove(k.Id);
        await Wait(() => !Directory.Exists(workK), "Cancel during Processing leaves the staging file or parts behind", 20);
        Check(!File.Exists(Path.Combine(p.Download, "cancelled-join.bin")), "A cancelled join publishes nothing");
        Check(Directory.GetDirectories(p.Download, "*.ndmpart").Length == 0, "No hidden folder is left after the cancelled join");
    }

    /// <summary>Item 2: when the join is interrupted (the app closes) the parts already copied are fetched again on the next start; the job does not claim 100%.</summary>
    public static async Task InterruptedJoinResumes((string Download, string State) p)
    {
        using var server = new FixtureServer();
        string id;
        {
            var m = Manager(p.Download, p.State);
            m.ProcessingPace = async (bytes, token) => await Task.Delay(150, token);
            var j = await m.AddAsync(new(server.BaseUrl + "/range", "restart-join.bin")); id = j.Id;
            await Wait(() => Job(m, id).Status == "Processing" && Job(m, id).ProcessedBytes > 1024 * 1024 / 2, "Processing did not progress");
            m.Dispose();
        }
        using var again = Manager(p.Download, p.State);
        var r = Job(again, id);
        Check(r.Status == "Paused", "An interrupted join comes back paused: " + r.Status);
        Check(r.DownloadedBytes < server.Payload.Length && r.ProcessedBytes == 0, "Parts that were already joined count as missing: " + r.DownloadedBytes);
        Check(!File.Exists(Path.Combine(p.Download, "restart-join.bin")), "Nothing was published");
        again.Resume(id);
        await Wait(() => Job(again, id).Status is "Completed" or "Failed", "Did not finish after the restart");
        var done = Job(again, id);
        Check(done.Status == "Completed" && FixtureServer.Hash(File.ReadAllBytes(done.FilePath)) == FixtureServer.Hash(server.Payload), "The file is complete and identical after the restart: " + done.Status + " " + done.Error);
    }

    /// <summary>Item 3: with a temporary files folder the parts live there (hidden), the joined file grows next to the destination, both folders are cleaned, and the choice survives a restart.</summary>
    public static async Task TemporaryFolder((string Download, string State) p)
    {
        using var server = new FixtureServer();
        var root = Path.GetDirectoryName(p.Download)!;
        var temp = Path.Combine(root, "cache-drive"); var dest = Path.Combine(root, "dest-drive");
        var m = Manager(p.Download, p.State, i => i.Temp = temp);
        var j = await m.AddScheduledAsync(new(server.BaseUrl + "/range", "cached.bin", TargetDirectory: dest), DateTimeOffset.UtcNow.AddHours(1));
        m.SetSpeedLimit(j.Id, 500000); m.Schedule(j.Id, DateTimeOffset.UtcNow);
        await Wait(() => Job(m, j.Id).DownloadedBytes >= 131072, "No progress");
        var inTemp = WorkOf(temp, j.Id);
        Check(Directory.Exists(Path.Combine(inTemp, "parts")) && Directory.GetFiles(Path.Combine(inTemp, "parts"), "*.part").Length > 0, "The part files are in the temporary folder");
        Check((new DirectoryInfo(inTemp).Attributes & FileAttributes.Hidden) != 0, "The temporary work folder is hidden");
        Check(!Directory.Exists(Path.Combine(dest, "." + j.Id + ".ndmpart", "parts")), "No part files next to the destination");
        m.Pause(j.Id); await Task.Delay(300); m.Dispose();
        m = Manager(p.Download, p.State); // no temporary folder set now: the job keeps its parts where they are
        Check(Job(m, j.Id).Status == "Paused" && Job(m, j.Id).DownloadedBytes > 0, "The partial download is found in the temporary folder after a restart");
        m.SetSpeedLimit(j.Id, null); m.Resume(j.Id);
        await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Did not finish after restart");
        var done = Job(m, j.Id);
        Check(done.Status == "Completed" && done.FilePath == Path.Combine(dest, "cached.bin") && FixtureServer.Hash(File.ReadAllBytes(done.FilePath)) == FixtureServer.Hash(server.Payload), "Identical file in the destination: " + done.Status + " " + done.Error);
        await Wait(() => !Directory.Exists(inTemp) && !Directory.Exists(Path.Combine(dest, "." + j.Id + ".ndmpart")), "Both work folders are removed after publishing");

        m.TemporaryFolder = temp;
        var c = await m.AddScheduledAsync(new(server.BaseUrl + "/range", "cached-cancel.bin", TargetDirectory: dest), DateTimeOffset.UtcNow.AddHours(1));
        m.SetSpeedLimit(c.Id, 500000); m.Schedule(c.Id, DateTimeOffset.UtcNow);
        await Wait(() => Job(m, c.Id).DownloadedBytes >= 131072, "No progress (cancel)");
        var cTemp = WorkOf(temp, c.Id);
        Check(Directory.Exists(cTemp), "Temporary parts exist before the cancel");
        Check(m.PartialBytes(c.Id) > 0, "Partial size counts the temporary folder");
        m.Remove(c.Id);
        await Wait(() => !Directory.Exists(cTemp), "Cancel removes the temporary parts");
        m.Dispose();
    }

    /// <summary>Items 3 and 11: what each drive needs. One drive: the file plus the largest part (parts are deleted as they are copied). Parts on another drive: that drive needs what is still to come, the file's drive needs the file.</summary>
    public static Task SpaceRules((string Download, string State) p)
    {
        const long mb = 1024 * 1024;
        long size = 800 * mb;
        var dest = @"D:\Downloads"; var cache = @"E:\Cache"; // only the drive letters are read, never the disk
        using var m = Manager(p.Download, p.State, i => i.Free = path => path.StartsWith("E", StringComparison.OrdinalIgnoreCase) ? 300 * mb : path.StartsWith("D", StringComparison.OrdinalIgnoreCase) ? 900 * mb : 5000 * mb);
        Check(m.CheckSpace(size, dest, null, 8).Count == 0, "One drive with 900 MB free holds an 800 MB file plus a 100 MB part");
        var one = m.CheckSpace(size, dest, null, 4);
        Check(one.Count == 1 && one[0].Drive == "D:" && one[0].NeededBytes == 1000 * mb && one[0].FreeBytes == 900 * mb, "Four parts of 200 MB need 1000 MB on one drive: " + string.Join(",", one));
        var two = m.CheckSpace(size, dest, cache, 8);
        Check(two.Count == 1 && two[0].Drive == "E:" && two[0].IsTemporaryFolder && two[0].NeededBytes == 800 * mb && two[0].FreeBytes == 300 * mb, "The temporary drive is named: " + string.Join(",", two));
        Check(m.CheckSpace(0, dest).Count == 0 && m.CheckSpace(-1, dest).Count == 0, "An unknown size is never a shortage");
        using var roomy = Manager(p.Download, p.State + "-b", i => i.Free = _ => 10000 * mb);
        Check(roomy.CheckSpace(size, dest, cache, 8).Count == 0, "Enough room on both drives");
        using var unknown = Manager(p.Download, p.State + "-c", i => i.Free = _ => null);
        Check(unknown.CheckSpace(size, dest, cache, 8).Count == 0, "Unknown free space (a network share) is never a shortage");
        return Task.CompletedTask;
    }

    /// <summary>Item 11: a drive that fills while the download runs stops it with the drive and the numbers, and keeps the parts so the user can free space and resume.</summary>
    public static async Task SpaceDuringTransfer((string Download, string State) p)
    {
        using var server = new FixtureServer();
        long calls = 0;
        using var m = Manager(p.Download, p.State, i => { i.Free = _ => Interlocked.Increment(ref calls) <= 6 ? 100L << 30 : 1L << 20; i.Interval = 65536; });
        var j = await m.AddScheduledAsync(new(server.BaseUrl + "/range", "fills.bin"), DateTimeOffset.UtcNow.AddHours(1));
        m.SetSpeedLimit(j.Id, 700000); m.Schedule(j.Id, DateTimeOffset.UtcNow);
        await Wait(() => Job(m, j.Id).Status == "Failed", "A drive that fills up must stop the download");
        var f = Job(m, j.Id);
        Check(f.ErrorCode == DownloadManager.DiskSpaceCode && f.ErrorExpectedBytes > f.ErrorReceivedBytes && (f.Error ?? "").StartsWith("Not enough free disk space on", StringComparison.Ordinal), "The failure carries the code, the need and the free space: " + f.ErrorCode + " " + f.Error);
        Check(Directory.Exists(WorkOf(p.Download, j.Id)), "The parts are kept so the download can resume after space is freed");
        Check(!File.Exists(f.FilePath), "Nothing is published");
    }

    /// <summary>Item 10: the same address or the same name and size is found; a finished download whose file is gone is not a duplicate.</summary>
    public static async Task Duplicates((string Download, string State) p)
    {
        using var server = new FixtureServer();
        using var m = Manager(p.Download, p.State);
        var folder = p.Download;
        Check(m.FindDuplicate(server.BaseUrl + "/range", "a.bin", server.Payload.Length, folder) is null, "Nothing is a duplicate in an empty list");
        var j = await m.AddScheduledAsync(new(server.BaseUrl + "/range", "a.bin"), DateTimeOffset.UtcNow.AddHours(1));
        var inList = m.FindDuplicate(server.BaseUrl + "/range#frag", null, null, folder);
        Check(inList is { Kind: DuplicateKind.InList } && inList.JobId == j.Id, "The same address (with a fragment) is found in the list: " + inList);
        Check(m.FindDuplicate(server.BaseUrl + "/other", "x.bin", null, folder) is null, "A different address is not a duplicate");
        m.Remove(j.Id);
        var done = await m.AddAsync(new(server.BaseUrl + "/range", "b.bin"));
        await Wait(() => Job(m, done.Id).Status is "Completed" or "Failed", "Download for the duplicate test");
        var finished = Job(m, done.Id); Check(finished.Status == "Completed", "setup download: " + finished.Error);
        var dl = m.FindDuplicate(server.BaseUrl + "/range", "new-name.bin", null, folder);
        Check(dl is { Kind: DuplicateKind.Downloaded }, "A finished download with its file in place is a duplicate: " + dl);
        var byName = m.FindDuplicate(server.BaseUrl + "/different-address", "b.bin", server.Payload.Length, folder);
        Check(byName is { Kind: DuplicateKind.SameNameAndSizeInList or DuplicateKind.InFolder }, "Same name and size is found: " + byName);
        Check(m.FindDuplicate(server.BaseUrl + "/different-address", "b.bin", server.Payload.Length + 1, folder) is null, "Same name with another size is not a duplicate");
        Check(m.FindDuplicate(server.BaseUrl + "/different-address", "b.bin", null, folder) is null, "A name alone (size unknown) is not a duplicate");
        var onDisk = Path.Combine(folder, "only-on-disk.bin"); File.WriteAllBytes(onDisk, new byte[4096]);
        Check(m.FindDuplicate(server.BaseUrl + "/z", "only-on-disk.bin", 4096, folder) is { Kind: DuplicateKind.InFolder }, "A file with the same name and size in the save folder is found");
        Check(m.FindDuplicate(server.BaseUrl + "/z", "only-on-disk.bin", 4097, folder) is null, "Same name, other size: not a duplicate");
        File.Delete(finished.FilePath);
        Check(m.FindDuplicate(server.BaseUrl + "/range", "b.bin", null, folder) is null, "A finished download whose file was deleted is not a duplicate");
        Check(m.FreeFileName("only-on-disk.bin", folder) == "only-on-disk (1).bin", "The rename suggestion skips names in use");
    }

    /// <summary>Item 23: up to 64 connections are accepted and used; 65 is refused.</summary>
    public static async Task SixtyFourSegments((string Download, string State) p)
    {
        using (new DownloadManager(p.Download, p.State + "-v", new DownloadOptions { MaxSegments = 64 })) { }
        var refused = false;
        try { using var _ = new DownloadManager(p.Download, p.State + "-w", new DownloadOptions { MaxSegments = 65 }); } catch (ArgumentOutOfRangeException) { refused = true; }
        Check(refused, "65 connections are refused");
        using var server = new FixtureServer();
        using var m = Manager(p.Download, p.State, i => i.Segments = 64);
        var j = await m.AddScheduledAsync(new(server.BaseUrl + "/range", "many.bin"), DateTimeOffset.UtcNow.AddHours(1));
        m.SetSpeedLimit(j.Id, 800000); m.Schedule(j.Id, DateTimeOffset.UtcNow);
        await Wait(() => Job(m, j.Id).Segments.Count > 16, "A 64-connection download splits into more than 16 ranges");
        m.SetSpeedLimit(j.Id, null);
        await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Finish");
        var done = Job(m, j.Id);
        Check(done.Status == "Completed" && FixtureServer.Hash(File.ReadAllBytes(done.FilePath)) == FixtureServer.Hash(server.Payload), "Identical file with many ranges: " + done.Error);
    }

    // ---------------- stage S2: queues ----------------
    private static async Task<DownloadJob> Slow(DownloadManager m, FixtureServer server, string name, long rate = 20000)
    {
        var j = await m.AddScheduledAsync(new(server.BaseUrl + "/range", name), DateTimeOffset.UtcNow.AddHours(1));
        m.SetSpeedLimit(j.Id, rate); m.Schedule(j.Id, DateTimeOffset.UtcNow); return j;
    }
    private static async Task<DownloadJob> Waiting(DownloadManager m, FixtureServer server, string name) => await m.AddScheduledAsync(new(server.BaseUrl + "/range", name), null);
    private static DownloadQueue QueueOf(DownloadManager m, string id = QueueIds.Main) => m.Queues.Single(q => q.Id == id);
    private static async Task Settle() => await Task.Delay(700);

    /// <summary>Item 19: Start now runs a waiting download past the limit, leaves the limit alone, overrides a hold and a schedule, refuses a download that is not waiting.</summary>
    public static async Task StartNowPastLimit((string Download, string State) p)
    {
        using var server = new FixtureServer();
        using var m = Manager(p.Download, p.State); m.MaxConcurrentDownloads = 1;
        var a = await Slow(m, server, "sn-a.bin");
        var b = await Waiting(m, server, "sn-b.bin");
        await Wait(() => Job(m, a.Id).Status == "Downloading", "A runs");
        await Settle();
        Check(Job(m, b.Id).Status == "Queued", "B waits behind the limit: " + Job(m, b.Id).Status);
        Check(!m.StartNow(a.Id), "A download that is already running has nothing to start");
        Check(m.StartNow(b.Id), "Start now accepts a waiting download");
        await Wait(() => Job(m, b.Id).Status is "Downloading" or "Processing" or "Completed", "B starts beside A");
        Check(Job(m, a.Id).Status == "Downloading", "A keeps running");
        Check(m.MaxConcurrentDownloads == 1 && m.LimitOf(QueueIds.Main) == 1, "The saved limit is unchanged");
        await Wait(() => Job(m, b.Id).Status == "Completed", "B completes");
        // a held queue and a scheduled download do not stop Start now
        var c = await Waiting(m, server, "sn-c.bin");
        m.PauseQueue(QueueIds.Main, false);
        await Settle();
        Check(Job(m, c.Id).Status == "Queued", "A held queue starts nothing");
        Check(m.StartNow(c.Id), "Start now overrides the hold");
        await Wait(() => Job(m, c.Id).Status == "Completed", "C runs although the queue is held");
        var d = await m.AddScheduledAsync(new(server.BaseUrl + "/range", "sn-d.bin"), DateTimeOffset.UtcNow.AddHours(5));
        Check(Job(m, d.Id).Status == "Scheduled", "D is scheduled for later");
        Check(m.StartNow(d.Id), "Start now overrides a schedule");
        await Wait(() => Job(m, d.Id).Status == "Completed", "D runs now");
        Check(Job(m, d.Id).ScheduledAt is null, "The schedule is cleared for that one download");
        foreach (var j in m.Jobs.ToArray()) { try { m.Remove(j.Id); } catch (Exception) { } }
    }

    /// <summary>Item 20 with Downloads at once = 1: pausing the running download holds the queue; resuming it releases the hold.</summary>
    public static async Task PauseHoldsQueueAtOne((string Download, string State) p)
    {
        using var server = new FixtureServer();
        using var m = Manager(p.Download, p.State); m.MaxConcurrentDownloads = 1;
        var a = await Slow(m, server, "h1-a.bin");
        var b = await Waiting(m, server, "h1-b.bin");
        await Wait(() => Job(m, a.Id).Status == "Downloading", "A runs");
        m.Pause(a.Id);
        await Wait(() => m.RunningCount == 0, "A stopped");
        await Settle();
        Check(Job(m, b.Id).Status == "Queued" && m.IsQueuePaused(QueueIds.Main), "Nothing new starts from the held queue: " + Job(m, b.Id).Status);
        m.SetSpeedLimit(a.Id, null); m.Resume(a.Id);
        Check(!m.IsQueuePaused(QueueIds.Main), "Resuming the paused download releases the hold");
        await Wait(() => Job(m, a.Id).Status == "Completed" && Job(m, b.Id).Status is "Downloading" or "Processing" or "Completed", "A finishes and B follows");
        // the hold is saved with the queue
        var c = await Slow(m, server, "h1-c.bin"); var d = await Waiting(m, server, "h1-d.bin");
        await Wait(() => Job(m, c.Id).Status == "Downloading" || Job(m, b.Id).Status != "Completed", "next running");
        foreach (var j in m.Jobs.Where(j => j.Status == "Downloading").ToArray()) m.Pause(j.Id);
        await Wait(() => m.RunningCount == 0, "all stopped");
        Check(QueueOf(m).Hold, "The queue is held");
        var json = File.ReadAllText(Path.Combine(p.State, "queues.json"));
        Check(json.Contains("\"Hold\": true"), "queues.json carries the hold");
    }

    /// <summary>Item 20b with Downloads at once = 2: the others keep running, the paused download restarts first when one finishes, a second pause leaves it paused, and the option turns it all off.</summary>
    public static async Task PauseFirstInLineAtTwo((string Download, string State) p)
    {
        using var server = new FixtureServer();
        using var m = Manager(p.Download, p.State); m.MaxConcurrentDownloads = 2;
        var a = await Slow(m, server, "t2-a.bin"); var b = await Slow(m, server, "t2-b.bin"); var c = await Slow(m, server, "t2-c.bin");
        var d = await Waiting(m, server, "t2-d.bin");
        await Wait(() => Job(m, a.Id).Status == "Downloading" && Job(m, b.Id).Status == "Downloading", "A and B run");
        Check(Job(m, c.Id).Status == "Queued" || Job(m, c.Id).Status == "Downloading", "C is next in the queue");
        m.Pause(a.Id);
        await Wait(() => Job(m, a.Id).Status == "Paused", "A paused");
        Check(!QueueOf(m).Hold && QueueOf(m).FirstInLine == a.Id, "With two at once the queue is not held; A is first in line");
        await Wait(() => Job(m, b.Id).Status == "Downloading" && Job(m, c.Id).Status == "Downloading", "B keeps running and C takes the free slot");
        Check(Job(m, d.Id).Status == "Queued", "D keeps waiting");
        m.SetSpeedLimit(b.Id, null);
        await Wait(() => Job(m, b.Id).Status == "Completed", "B finishes");
        await Wait(() => Job(m, a.Id).Status is "Downloading" or "Queued", "A restarts first");
        await Settle();
        Check(Job(m, d.Id).Status == "Queued", "D still waits behind A: " + Job(m, d.Id).Status);
        Check(QueueOf(m).FirstInLine is null, "First in line is used up");
        m.Pause(a.Id); // second pause: stays paused, the queue carries on
        await Wait(() => Job(m, a.Id).Status == "Paused", "A paused again");
        Check(!QueueOf(m).Hold && QueueOf(m).FirstInLine is null, "A second pause neither holds the queue nor claims first place");
        await Wait(() => Job(m, d.Id).Status is "Downloading" or "Processing" or "Completed", "D starts in the free slot");
        Check(Job(m, a.Id).Status == "Paused", "A stays paused");
        // option off: the old behaviour
        m.PausePausesQueue = false; m.MaxConcurrentDownloads = 1;
        foreach (var j in m.Jobs.ToArray()) { try { m.Remove(j.Id); } catch (Exception) { } }
        var e = await Slow(m, server, "t2-e.bin"); var f = await Waiting(m, server, "t2-f.bin");
        await Wait(() => Job(m, e.Id).Status == "Downloading", "E runs");
        m.Pause(e.Id);
        await Wait(() => Job(m, f.Id).Status is "Downloading" or "Processing" or "Completed", "With the option off the next download starts at once");
        Check(!QueueOf(m).Hold, "No hold with the option off");
        foreach (var j in m.Jobs.ToArray()) { try { m.Remove(j.Id); } catch (Exception) { } }
    }

    /// <summary>Items 24 and 24b: Pause queue stops the running downloads and holds; Hold queue lets them finish; Resume queue puts the stopped ones back first; the state survives a restart; a schedule releases a hand hold only when asked.</summary>
    public static async Task QueuePauseHoldResume((string Download, string State) p)
    {
        using var server = new FixtureServer();
        string a, b, c;
        using (var m = Manager(p.Download, p.State)) 
        {
            m.MaxConcurrentDownloads = 2;
            var ja = await Slow(m, server, "q-a.bin"); var jb = await Slow(m, server, "q-b.bin"); var jc = await Waiting(m, server, "q-c.bin"); a = ja.Id; b = jb.Id; c = jc.Id;
            await Wait(() => Job(m, a).Status == "Downloading" && Job(m, b).Status == "Downloading", "A and B run");
            m.PauseQueue(QueueIds.Main, true);
            await Wait(() => m.RunningCount == 0, "Pause queue stops the running downloads");
            Check(Job(m, a).Status == "Paused" && Job(m, b).Status == "Paused" && Job(m, c).Status == "Queued", "Both paused, C waits");
            Check(QueueOf(m).Hold && QueueOf(m).PausedByQueue.Count == 2, "The queue is held and remembers what it stopped");
            await Settle();
            Check(Job(m, c).Status == "Queued", "Nothing new starts while held");
        }
        using (var m = Manager(p.Download, p.State)) // restart
        {
            m.MaxConcurrentDownloads = 2;
            Check(QueueOf(m).Hold && QueueOf(m).PausedByQueue.Count == 2, "The pause survives a restart (queues.json)");
            await Settle();
            Check(m.RunningCount == 0 && Job(m, c).Status is "Queued" or "Paused", "Still nothing running after the restart");
            m.Enqueue(c); // a restart leaves waiting downloads paused (as in 1.16); put C back in the line
            Check(Job(m, c).Status == "Queued", "C is waiting again");
            m.ResumeQueue(QueueIds.Main);
            Check(!QueueOf(m).Hold, "Resume queue releases the hold");
            await Wait(() => Job(m, a).Status == "Downloading" && Job(m, b).Status == "Downloading", "What the pause stopped runs first");
            Check(Job(m, c).Status is "Queued" or "Paused", "C waits behind them (limit 2)");
            // Hold: running ones finish, nothing new starts
            m.PauseQueue(QueueIds.Main, false);
            await Settle();
            Check(Job(m, a).Status == "Downloading" && Job(m, b).Status == "Downloading" && QueueOf(m).Hold && QueueOf(m).PausedByQueue.Count == 0, "Hold queue leaves the running downloads alone");
            m.SetSpeedLimit(a, null); m.SetSpeedLimit(b, null);
            await Wait(() => Job(m, a).Status == "Completed" && Job(m, b).Status == "Completed", "Running downloads finish");
            await Settle();
            Check(Job(m, c).Status == "Queued", "C still waits: the queue is held");
            // a schedule opening does not release a hand hold unless the queue says so
            m.OpenQueue(QueueIds.Main);
            Check(QueueOf(m).Hold, "A schedule window does not release a hold set by hand");
            m.UpdateQueue(QueueIds.Main, q => q.ResumeOnSchedule = true);
            m.OpenQueue(QueueIds.Main);
            Check(!QueueOf(m).Hold, "With resume-on-schedule ticked it does");
            await Wait(() => Job(m, c).Status is "Downloading" or "Processing" or "Completed", "C starts after the release");
            foreach (var j in m.Jobs.ToArray()) { try { m.Remove(j.Id); } catch (Exception) { } }
        }
    }
}
