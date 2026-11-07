using System.Collections.Concurrent;
using System.Diagnostics;
using NDM.Core;

namespace NDM.Tests;

/// <summary>1.12.0: a download writes its parts, joined copy and temporary files on the destination volume only. The state folder keeps the small queue record.
/// A second physical volume is not available to these tests (a substituted drive letter is the same volume, so it would prove nothing), so the proof is by path: a different folder tree for the
/// base folder, the state folder and the temp folder, a file watcher on each, and the rule that every created file path is under the destination.</summary>
internal static class DestinationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Wait(Func<bool> condition, string message, int seconds = 20)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message); await Task.Delay(20); }
    }
    private static DownloadManager Manager(string download, string state) =>
        new(download, state, new DownloadOptions { MaxSegments = 4, MinimumSegmentSize = 65536, RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(4) });
    private static DownloadJob Job(DownloadManager m, string id) => m.Jobs.Single(j => j.Id == id);

    /// <summary>Records every file created or written below the watched folders, with the largest size seen.</summary>
    private sealed class Trace : IDisposable
    {
        private readonly List<FileSystemWatcher> watchers = new();
        public readonly ConcurrentDictionary<string, long> Files = new(StringComparer.OrdinalIgnoreCase);
        public Trace(params string[] folders)
        {
            foreach (var folder in folders)
            {
                Directory.CreateDirectory(folder);
                var w = new FileSystemWatcher(folder) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime };
                FileSystemEventHandler seen = (_, e) => Note(e.FullPath);
                w.Created += seen; w.Changed += seen; w.Renamed += (_, e) => Note(e.FullPath);
                w.EnableRaisingEvents = true; watchers.Add(w);
            }
        }
        private void Note(string path)
        {
            long size = 0; try { var info = new FileInfo(path); if (info.Exists) size = info.Length; else if (Directory.Exists(path)) return; } catch (IOException) { }
            Files.AddOrUpdate(path, size, (_, old) => Math.Max(old, size));
        }
        public void Dispose() { foreach (var w in watchers) w.Dispose(); }
    }

    public static async Task NothingOutsideTheDestination((string Download, string State) paths)
    {
        using var server = new FixtureServer();
        var root = Path.GetDirectoryName(paths.Download)!;
        var destination = Path.Combine(root, "dest-volume", "Videos"); var temp = Path.Combine(root, "temp-probe");
        var saved = (Environment.GetEnvironmentVariable("TEMP"), Environment.GetEnvironmentVariable("TMP"));
        Environment.SetEnvironmentVariable("TEMP", temp); Environment.SetEnvironmentVariable("TMP", temp); Directory.CreateDirectory(temp);
        try
        {
            using var trace = new Trace(paths.State, paths.Download, temp, destination);
            using var m = Manager(paths.Download, paths.State);
            var added = await m.AddScheduledAsync(new(server.BaseUrl + "/range", "movie.bin", TargetDirectory: destination), DateTimeOffset.UtcNow.AddHours(1));
            m.SetSpeedLimit(added.Id, 700000); m.Schedule(added.Id, DateTimeOffset.UtcNow);
            await Wait(() => Job(m, added.Id).DownloadedBytes >= 131072, "No progress");
            var work = Path.Combine(destination, "." + added.Id + DownloadManagerNames.WorkSuffix);
            Check(Directory.Exists(Path.Combine(work, "parts")) && Directory.GetFiles(Path.Combine(work, "parts"), "*.part").Length > 0, "The part files are in the hidden work folder next to the destination");
            Check((new DirectoryInfo(work).Attributes & FileAttributes.Hidden) != 0, "The work folder is hidden");
            Check(!Directory.Exists(Path.Combine(paths.State, added.Id + ".parts")), "No part folder in the state folder");
            m.Pause(added.Id); await Task.Delay(300);
            // restart: a new manager over the same state finds the parts next to the destination and finishes
            m.Dispose();
            using var again = Manager(paths.Download, paths.State);
            var restored = Job(again, added.Id); Check(restored.Status == "Paused" && restored.DownloadedBytes > 0, "The partial download is restored after a restart: " + restored.Status + " " + restored.DownloadedBytes);
            again.SetSpeedLimit(added.Id, null); again.Resume(added.Id);
            await Wait(() => Job(again, added.Id).Status is "Completed" or "Failed", "Did not finish after the restart");
            var done = Job(again, added.Id); Check(done.Status == "Completed", "Failed after restart: " + done.Error);
            Check(done.FilePath == Path.Combine(destination, "movie.bin") && FixtureServer.Hash(File.ReadAllBytes(done.FilePath)) == FixtureServer.Hash(server.Payload), "Byte-identical file in the destination after resume");
            await Wait(() => !Directory.Exists(work), "The work folder is removed after publishing");
            await Task.Delay(300);
            var created = trace.Files.ToArray();
            var outside = created.Where(f => !f.Key.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var (path, size) in outside)
            {
                var name = Path.GetFileName(path);
                Check(size <= 1024 * 1024, "A file above 1 MB was written outside the destination: " + path + " " + size);
                Check(!name.Contains(added.Id) || name.StartsWith("queue", StringComparison.OrdinalIgnoreCase), "A per-job file was written outside the destination: " + path);
                Check(!name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !name.Equals("assemble.tmp", StringComparison.OrdinalIgnoreCase), "A part or joined file outside the destination: " + path);
            }
            Check(!Directory.Exists(Path.Combine(paths.Download, ".ndm-staging")), "The old staging folder in the base folder is no longer created");
            Check(Directory.GetFileSystemEntries(paths.Download).Length == 0, "Nothing was written to the base folder when the destination is elsewhere");
            Check(created.Any(f => f.Key.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && f.Key.StartsWith(destination, StringComparison.OrdinalIgnoreCase)), "The trace did see the part files under the destination");
        }
        finally { Environment.SetEnvironmentVariable("TEMP", saved.Item1); Environment.SetEnvironmentVariable("TMP", saved.Item2); }
    }

    public static async Task CancelAndRemoveCleanTheDestination((string Download, string State) paths)
    {
        using var server = new FixtureServer();
        var destination = Path.Combine(Path.GetDirectoryName(paths.Download)!, "dest-clean");
        using var m = Manager(paths.Download, paths.State);
        var added = await m.AddScheduledAsync(new(server.BaseUrl + "/range", "gone.bin", TargetDirectory: destination), DateTimeOffset.UtcNow.AddHours(1));
        m.SetSpeedLimit(added.Id, 400000); m.Schedule(added.Id, DateTimeOffset.UtcNow);
        await Wait(() => Job(m, added.Id).DownloadedBytes >= 65536, "No progress");
        var work = Path.Combine(destination, "." + added.Id + DownloadManagerNames.WorkSuffix);
        Check(Directory.Exists(work), "Work folder exists while downloading");
        m.Remove(added.Id);
        await Wait(() => !Directory.Exists(work), "Removing a partial download deletes its work folder");
        Check(!File.Exists(Path.Combine(destination, "gone.bin")), "Nothing was published");
        Check(Directory.GetFileSystemEntries(destination).Length == 0, "The destination folder holds nothing of the removed download: " + string.Join(", ", Directory.GetFileSystemEntries(destination)));
    }

    /// <summary>The engine sources never ask for the system temp folder or the old staging folder, so a later edit cannot quietly move writes back to the system drive.</summary>
    public static Task SourceGuard()
    {
        var dir = AppContext.BaseDirectory; string? core = null;
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent) if (Directory.Exists(Path.Combine(d.FullName, "src", "NDM.Core"))) { core = Path.Combine(d.FullName, "src", "NDM.Core"); break; }
        Check(core != null, "Repository sources not found from " + dir);
        foreach (var file in Directory.GetFiles(core!, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Check(!text.Contains("GetTempPath") && !text.Contains("GetTempFileName"), Path.GetFileName(file) + " uses the system temp folder");
            Check(!text.Contains("\".ndm-staging\""), Path.GetFileName(file) + " uses the old staging folder in the base folder");
            Check(!text.Contains("+ \".parts\")") || Path.GetFileName(file) == "DownloadManager.cs", Path.GetFileName(file) + " builds a part folder in the state folder");
        }
        return Task.CompletedTask;
    }
}

internal static class DownloadManagerNames { public const string WorkSuffix = ".ndmpart"; }
