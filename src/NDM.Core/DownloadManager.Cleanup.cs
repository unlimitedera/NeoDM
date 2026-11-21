// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Text.RegularExpressions;

namespace NDM.Core;

// 2.0.0: partial-data cleanup (cancel, remove, start-up sweep) and the temporary files folder.
public sealed partial class DownloadManager
{
    /// <summary>The hidden work folder a job would use next to its file, whether or not the transfer ever created it (so a folder the queue lost track of is still found).</summary>
    private string DefaultWorkRoot(Work w)
    {
        var dir = Path.GetDirectoryName(w.Job.FilePath);
        return Path.Combine(string.IsNullOrEmpty(dir) ? downloads : dir, "." + w.Job.Id + WorkFolderSuffix);
    }

    private List<string> CleanupTargets(Work w)
    {
        string? root, parts; lock (gate) { root = w.WorkRoot; parts = w.PartsRoot; }
        var list = new List<string> { LegacyFolder(w.Job.Id), root ?? DefaultWorkRoot(w) };
        if (parts != null) list.Add(parts);
        if (PlannedPartsRoot(w) is { } planned) list.Add(planned);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One try, no waiting (safe under the lock). Returns the folders that could not be removed.</summary>
    private List<string> DeletePartial(Work w)
    {
        var left = new List<string>();
        foreach (var path in CleanupTargets(w)) if (!TryDeleteTree(path)) left.Add(path);
        return left;
    }

    private static bool TryDeleteTree(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); return !Directory.Exists(path); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Removes a job's partial data after its transfer has stopped. Open handles can outlive the cancel for a moment (a part file still closing, a scanner looking at it),
    /// so it tries again a few times; what still cannot be removed is remembered and swept at the next start. Never called under the lock.</summary>
    private async Task DeletePartialRetryAsync(Work w)
    {
        List<string> left = new();
        for (var attempt = 0; attempt < CleanupAttempts; attempt++)
        {
            left = DeletePartial(w);
            if (left.Count == 0) return;
            await Task.Delay(CleanupDelay * (attempt + 1)).ConfigureAwait(false);
        }
        RememberPending(left);
    }

    /// <summary>Tries and pause between tries of the cleanup retry; a seam for tests.</summary>
    internal static int CleanupAttempts = 8;
    internal static TimeSpan CleanupDelay = TimeSpan.FromMilliseconds(150);

    private string PendingFile => Path.Combine(state, "cleanup-pending.txt");
    private readonly object pendingGate = new();
    private void RememberPending(IEnumerable<string> folders)
    {
        try
        {
            lock (pendingGate)
            {
                var known = File.Exists(PendingFile) ? File.ReadAllLines(PendingFile).ToList() : new List<string>();
                foreach (var f in folders) if (!known.Contains(f, StringComparer.OrdinalIgnoreCase)) known.Add(f);
                File.WriteAllLines(PendingFile, known.Take(200));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static readonly Regex WorkFolderName = new(@"^\.[0-9a-f]{32}\.ndmpart$", RegexOptions.CultureInvariant);
    private static bool IsWorkFolderName(string name) => WorkFolderName.IsMatch(name);

    /// <summary>A hidden work folder of ours that no download in the queue owns.</summary>
    public sealed record PartialFolder(string Path, long Bytes);

    /// <summary>Finds work folders (named <c>.&lt;32 hex&gt;.ndmpart</c>) that belong to no download in the queue: in the download folder, each job's folder, the temporary folder,
    /// the extra folders given (one level deep) and the list of folders an earlier cleanup could not remove. Left by a cancel that could not delete, a crash, or a removed job.</summary>
    public IReadOnlyList<PartialFolder> FindOrphanedPartials(IEnumerable<string>? folders = null)
    {
        HashSet<string> owned; var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (gate)
        {
            owned = new HashSet<string>(jobs.Where(w => w.Job.Status != "Completed").Select(w => w.Job.Id), StringComparer.OrdinalIgnoreCase); // a finished download has no work folder: one that is still there is left over
            roots.Add(downloads);
            foreach (var w in jobs)
            {
                if (Path.GetDirectoryName(w.Job.FilePath) is { Length: > 0 } d) roots.Add(d);
                if (w.WorkRoot is { } wr && Path.GetDirectoryName(wr) is { Length: > 0 } wd) roots.Add(wd);
                if (w.PartsRoot is { } pr && Path.GetDirectoryName(pr) is { Length: > 0 } pd) roots.Add(pd);
            }
        }
        if (TemporaryFolder is { } temp) roots.Add(temp);
        if (folders != null) foreach (var f in folders) if (!string.IsNullOrWhiteSpace(f)) { try { roots.Add(Path.GetFullPath(f)); } catch (ArgumentException) { } }
        var found = new Dictionary<string, PartialFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (!IsWorkFolderName(name) || owned.Contains(name.Substring(1, 32))) continue;
                    found[dir] = new PartialFolder(dir, TreeBytes(dir));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try
        {
            if (File.Exists(PendingFile))
                foreach (var line in File.ReadAllLines(PendingFile))
                    if (Path.IsPathFullyQualified(line) && IsWorkFolderName(Path.GetFileName(line)) && !owned.Contains(Path.GetFileName(line).Substring(1, 32)) && Directory.Exists(line))
                        found[line] = new PartialFolder(line, TreeBytes(line));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return found.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Deletes the orphaned work folders found by <see cref="FindOrphanedPartials"/>. Returns how many folders and bytes were removed; folders that stay locked go on the pending list.</summary>
    public (int Folders, long Bytes) CleanOrphanedPartials(IEnumerable<string>? folders = null)
    {
        int count = 0; long bytes = 0; var left = new List<string>();
        foreach (var f in FindOrphanedPartials(folders))
        {
            var removed = TryDeleteTree(f.Path);
            if (!removed) { Thread.Sleep(100); removed = TryDeleteTree(f.Path); }
            if (removed) { count++; bytes += f.Bytes; } else left.Add(f.Path);
        }
        if (left.Count > 0) RememberPending(left);
        else { try { lock (pendingGate) if (File.Exists(PendingFile)) File.Delete(PendingFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        return (count, bytes);
    }

    /// <summary>Bytes of partial data (part files, a half-joined copy) that removing or cancelling this download would delete. 0 for a finished download.</summary>
    public long PartialBytes(string id)
    {
        Work w; lock (gate) { w = Find(id); }
        if (w.Job.Status == "Completed") return 0;
        long total = 0;
        foreach (var path in CleanupTargets(w)) total += TreeBytes(path);
        return total;
    }

    private static long TreeBytes(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                // The directory entry of a file that is being written lags behind; the open handle knows the real length.
                try { using var handle = File.OpenHandle(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); total += RandomAccess.GetLength(handle); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { try { total += file.Length; } catch (IOException) { } }
            }
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>After an interrupted join some part files are gone (each is deleted as it is copied): those segments count as not downloaded,
    /// so the next start fetches them again instead of claiming 100%. Call under the lock.</summary>
    private void ReconcileParts(Work w)
    {
        if (w.Segments.Count == 0 || w.Request.StreamKind != StreamKind.None || w.Request.Youtube is not null || w.Job.Status == "Completed") return;
        try
        {
            foreach (var s in w.Segments) if (s.Done > 0 && s.PartId is { } id && !File.Exists(PartFile(w, id))) s.Done = 0;
            UpdateProgressLocked(w);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The folder for part files of new downloads (Options &gt; Downloads &gt; Temporary files folder). Null = next to the file (same drive as the download).
    /// Downloads that already have parts keep them where they are.</summary>
    public string? TemporaryFolder
    {
        get => Volatile.Read(ref temporaryFolder);
        set => Volatile.Write(ref temporaryFolder, string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim()));
    }
    private string? temporaryFolder;

    /// <summary>The hidden folder for the part files inside the temporary folder; null when none is set or the job still keeps old parts in the state folder.</summary>
    private string? PlannedPartsRoot(Work w) =>
        TemporaryFolder is { } temp && !Directory.Exists(LegacyFolder(w.Job.Id)) ? Path.Combine(temp, "." + w.Job.Id + WorkFolderSuffix) : null;

    /// <summary>Creates the folder the part files go in (a hidden folder inside the temporary folder when one is set, else the work folder next to the file). Call under the lock.</summary>
    private void PreparePartsRoot(Work w)
    {
        if (w.PartsRoot == null && w.Segments.Sum(x => x.Done) == 0) w.PartsRoot = PlannedPartsRoot(w);
        string root;
        if (w.PartsRoot != null)
        {
            root = w.PartsRoot;
            try { Directory.CreateDirectory(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { throw new IOException($"The temporary files folder cannot be used: {ex.Message}", ex); }
            try { var a = File.GetAttributes(root); if ((a & FileAttributes.Hidden) == 0) File.SetAttributes(root, a | FileAttributes.Hidden); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        else root = EnsureWorkRoot(w);
        Directory.CreateDirectory(PartDirectory(w));
    }
}
