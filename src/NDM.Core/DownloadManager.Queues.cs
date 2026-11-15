using System.Text.Json;

namespace NDM.Core;

/// <summary>1.16.0: named queues. A download belongs to one queue; the queue decides how many of its downloads run at once, whether it waits behind other queues, and (with a schedule) when it may start.
/// Queue definitions live in queues.json next to queue.json; a state folder without that file gets one Main queue and every download belongs to it.</summary>
public sealed partial class DownloadManager
{
    private readonly List<DownloadQueue> queues = new();
    private readonly HashSet<string> openGates = new();
    private string defaultQueueId = QueueIds.Main;
    private string? pendingQueueId; private long pendingUntil;
    private static readonly JsonSerializerOptions QueueJson = new() { WriteIndented = true };

    public const string MainQueueName = "Main queue";

    private void LoadQueues()
    {
        SavedQueues? saved = null;
        if (File.Exists(queuesPath))
        {
            try { saved = JsonSerializer.Deserialize<SavedQueues>(FileLimits.ReadAllTextCapped(queuesPath, FileLimits.MaxSettingsBytes, "queues.json")); }
            catch (JsonException) { saved = null; } // a damaged queue list falls back to the Main queue; downloads keep their own ids and land in Main
        }
        foreach (var q in saved?.Queues ?? new())
        {
            if (string.IsNullOrEmpty(q.Id) || q.Id.Length > 40 || !QueueIds.ValidName(q.Name) || queues.Any(o => o.Id == q.Id) || queues.Count >= QueueIds.MaxQueues) continue;
            if (q.Limit is < 1 or > 16) q.Limit = null;
            if (q.Schedule?.Problem() is not null) q.Schedule.Enabled = false;
            queues.Add(q);
        }
        if (!queues.Any(q => q.Id == QueueIds.Main)) queues.Insert(0, new DownloadQueue { Id = QueueIds.Main, Name = MainQueueName });
        if (saved is not null && queues.Any(q => q.Id == saved.DefaultQueueId)) defaultQueueId = saved.DefaultQueueId;
    }

    private void SaveQueuesLocked()
    {
        var temporary = queuesPath + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, new SavedQueues { DefaultQueueId = defaultQueueId, Queues = queues }, QueueJson); file.Flush(true); }
        File.Move(temporary, queuesPath, true);
    }

    private string ResolveQueueLocked(string? requested)
    {
        if (requested is not null && queues.Any(q => q.Id == requested)) return requested;
        if (pendingQueueId is { } p && System.Diagnostics.Stopwatch.GetTimestamp() < pendingUntil && queues.Any(q => q.Id == p)) return p;
        return QueueIds.Main;
    }

    private DownloadQueue QueueLocked(string id) => queues.FirstOrDefault(q => q.Id == id) ?? queues[0];

    /// <summary>The queues in display order (copies).</summary>
    public IReadOnlyList<DownloadQueue> Queues { get { lock (gate) return queues.Select(q => q.Clone()).ToArray(); } }
    public string DefaultQueueId { get { lock (gate) return defaultQueueId; } set { lock (gate) { if (queues.Any(q => q.Id == value) && defaultQueueId != value) { defaultQueueId = value; SaveQueuesLocked(); } } } }

    /// <summary>The queue the next downloads are added to when a request names none (for ten seconds); set by the chooser before the add and cleared by <see cref="ClearPendingQueue"/>.</summary>
    public void SetPendingQueue(string queueId) { lock (gate) { pendingQueueId = queues.Any(q => q.Id == queueId) ? queueId : null; pendingUntil = System.Diagnostics.Stopwatch.GetTimestamp() + 10 * System.Diagnostics.Stopwatch.Frequency; } }
    public string? PendingQueueId { get { lock (gate) return pendingQueueId is not null && System.Diagnostics.Stopwatch.GetTimestamp() < pendingUntil ? pendingQueueId : null; } }
    public void ClearPendingQueue() { lock (gate) pendingQueueId = null; }

    public int LimitOf(string queueId) { lock (gate) return QueueLocked(queueId).Limit ?? MaxConcurrentDownloads; }
    public int RunningIn(string queueId) { lock (gate) return activeJobs.Count(a => a.Job.QueueId == queueId); }
    /// <summary>True while this queue's schedule has not started it (its downloads wait).</summary>
    public bool IsQueueHeld(string queueId) { lock (gate) return Held(QueueLocked(queueId)); }
    private bool Held(DownloadQueue q) => q.Schedule is { Enabled: true } && !openGates.Contains(q.Id);

    private static string CleanName(string name)
    {
        name = (name ?? "").Trim();
        if (!QueueIds.ValidName(name)) throw new ArgumentException("A queue name is 1 to 60 characters without control characters.");
        return name;
    }
    private void EnsureUniqueLocked(string name, string? exceptId)
    {
        if (queues.Any(q => q.Id != exceptId && string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("A queue with this name already exists.");
    }

    public DownloadQueue CreateQueue(string name)
    {
        lock (gate)
        {
            ThrowIfDisposed(); name = CleanName(name); EnsureUniqueLocked(name, null);
            if (queues.Count >= QueueIds.MaxQueues) throw new InvalidOperationException("Too many queues.");
            var q = new DownloadQueue { Id = Guid.NewGuid().ToString("N")[..12], Name = name };
            queues.Add(q); SaveQueuesLocked();
            Notify(); return q.Clone();
        }
    }

    public void RenameQueue(string id, string name) => UpdateQueue(id, q => q.Name = name);

    /// <summary>Edits a queue under the lock (name, limit, parallel, schedule, when done). Validates; a failure changes nothing.</summary>
    public void UpdateQueue(string id, Action<DownloadQueue> edit)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var q = queues.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException(id);
            var copy = q.Clone(); edit(copy);
            copy.Id = q.Id; copy.Name = CleanName(copy.Name); EnsureUniqueLocked(copy.Name, id);
            if (copy.Limit is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(edit), "Downloads at once is 1 to 16.");
            if (copy.Schedule?.Problem() is { } problem) throw new ArgumentException("Invalid schedule: " + problem);
            q.Name = copy.Name; q.Limit = copy.Limit; q.AllowParallel = copy.AllowParallel; q.Schedule = copy.Schedule; q.WhenDone = copy.WhenDone; q.LastStarted = copy.LastStarted;
            if (q.Schedule is not { Enabled: true }) openGates.Remove(id);
            SaveQueuesLocked();
        }
        Notify(); Tick(null);
    }

    /// <summary>Moves a queue to a new position in the list (0 = first).</summary>
    public void MoveQueue(string id, int newIndex)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var q = queues.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException(id);
            queues.Remove(q); queues.Insert(Math.Clamp(newIndex, 0, queues.Count), q); SaveQueuesLocked();
        }
        Notify();
    }

    /// <summary>Deletes a queue. Its downloads go to the Main queue, or are removed (the files already saved stay) when <paramref name="removeDownloads"/>. The Main queue cannot be deleted.</summary>
    public void DeleteQueue(string id, bool removeDownloads)
    {
        string[] ids;
        lock (gate)
        {
            ThrowIfDisposed();
            if (id == QueueIds.Main) throw new InvalidOperationException("The Main queue cannot be deleted.");
            var q = queues.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException(id);
            ids = jobs.Where(w => w.Job.QueueId == id).Select(w => w.Job.Id).ToArray();
            if (!removeDownloads) foreach (var w in jobs.Where(w => w.Job.QueueId == id)) w.Job.QueueId = QueueIds.Main;
            queues.Remove(q); openGates.Remove(id); if (defaultQueueId == id) defaultQueueId = QueueIds.Main;
            SaveQueuesLocked(); SaveLocked();
        }
        if (removeDownloads) foreach (var job in ids) { try { Remove(job, false); } catch (KeyNotFoundException) { } }
        Notify(); Tick(null);
    }

    /// <summary>Moves downloads to a queue (at the end, or before the download <paramref name="beforeId"/> of that queue). Running downloads keep running.</summary>
    public void MoveJobs(IReadOnlyList<string> jobIds, string queueId, string? beforeId = null)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!queues.Any(q => q.Id == queueId)) throw new KeyNotFoundException(queueId);
            var moving = jobIds.Distinct().Select(Find).ToList();
            foreach (var w in moving) jobs.Remove(w);
            int at = beforeId is null ? -1 : jobs.FindIndex(w => w.Job.Id == beforeId);
            if (at < 0)
            {
                // after the last download of the queue (or at the end of the list)
                int last = jobs.FindLastIndex(w => w.Job.QueueId == queueId);
                at = last < 0 ? jobs.Count : last + 1;
            }
            foreach (var w in moving) { w.Job.QueueId = queueId; jobs.Insert(at++, w); }
            SaveLocked();
        }
        Notify(); Tick(null);
    }

    /// <summary>The ids of a queue's downloads in run order.</summary>
    public IReadOnlyList<string> OrderOf(string queueId) { lock (gate) return jobs.Where(w => w.Job.QueueId == queueId).Select(w => w.Job.Id).ToArray(); }

    /// <summary>Lets a scheduled queue's downloads start (its schedule window began, or the user chose Start queue).</summary>
    public void OpenQueue(string id) { lock (gate) { if (queues.Any(q => q.Id == id)) openGates.Add(id); } Notify(); Tick(null); }

    /// <summary>Stops a queue politely: holds it again and puts its running downloads back in the queue (they keep their parts and stay queued).</summary>
    public void StopQueue(string id)
    {
        lock (gate)
        {
            openGates.Remove(id);
            foreach (var w in activeJobs.Where(a => a.Job.QueueId == id).ToArray())
            {
                if (w.Job.Status is "Downloading" or "Connecting") { w.Job.Status = "Queued"; w.Job.SpeedBytesPerSecond = 0; w.Cancellation?.Cancel(); }
            }
            SaveLocked();
        }
        Notify();
    }

    public void SetQueueLastStarted(string id, DateTime? start) { lock (gate) { var q = queues.FirstOrDefault(x => x.Id == id); if (q is null) return; q.LastStarted = start; SaveQueuesLocked(); } }

    /// <summary>How many downloads of the queue still have work to do (queued, scheduled or running), and how many are completed.</summary>
    public (int Pending, int Completed, int Running) CountsOf(string queueId)
    {
        lock (gate)
        {
            int pending = 0, done = 0, running = 0;
            foreach (var w in jobs) if (w.Job.QueueId == queueId)
                {
                    if (w.Job.Status == "Completed") done++;
                    else if (w.Job.Status is "Queued" or "Scheduled") pending++;
                    else if (w.Job.Status is "Downloading" or "Connecting" or "Checking") { pending++; running++; }
                }
            return (pending, done, running);
        }
    }

    /// <summary>Jobs in start order: queue by queue (list order), each queue's downloads in their order.</summary>
    private IEnumerable<Work> StartOrderLocked()
    {
        if (queues.Count <= 1) return jobs.ToArray();
        var order = new List<Work>(jobs.Count);
        foreach (var q in queues) order.AddRange(jobs.Where(w => w.Job.QueueId == q.Id));
        return order;
    }

    private bool QueueMayStartLocked(Work w)
    {
        var q = QueueLocked(w.Job.QueueId);
        if (Held(q)) return false;
        int mine = 0, others = 0;
        foreach (var a in activeJobs) { if (a.Job.QueueId == q.Id) mine++; else others++; }
        if (mine >= (q.Limit ?? MaxConcurrentDownloads)) return false;
        if (!q.AllowParallel && others > 0) return false;
        return true;
    }
}
