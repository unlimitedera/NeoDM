// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
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
            q.PausedByQueue ??= new(); if (q.PausedByQueue.Count > 5000) q.PausedByQueue.Clear();
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
            q.Name = copy.Name; q.Limit = copy.Limit; q.AllowParallel = copy.AllowParallel; q.Schedule = copy.Schedule; q.WhenDone = copy.WhenDone; q.LastStarted = copy.LastStarted; q.ResumeOnSchedule = copy.ResumeOnSchedule;
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
    public void OpenQueue(string id)
    {
        lock (gate)
        {
            if (!queues.Any(q => q.Id == id)) return;
            openGates.Add(id);
            var q = QueueLocked(id);
            if (q.Hold && q.ResumeOnSchedule) { ReleaseHoldLocked(q); SaveQueuesLocked(); } // a hold set by hand stays unless the user ticked 'resume on schedule'
        }
        Notify(); Tick(null);
    }

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
                    else if (w.Job.Status is "Downloading" or "Processing" or "Connecting" or "Checking") { pending++; running++; }
                }
            return (pending, done, running);
        }
    }

    /// <summary>Jobs in start order: queue by queue (list order), each queue's downloads in their order.</summary>
    private IEnumerable<Work> StartOrderLocked()
    {
        if (queues.Count <= 1 && queues[0].FirstInLine is null && queues[0].PausedByQueue.Count == 0) return jobs.ToArray();
        var order = new List<Work>(jobs.Count);
        foreach (var q in queues)
        {
            // 2.0.0: downloads the queue pause stopped, and the paused download that is first in line, start before the waiting ones
            var mine = jobs.Where(w => w.Job.QueueId == q.Id).ToList();
            if (q.FirstInLine is not null || q.PausedByQueue.Count > 0)
                mine = mine.OrderBy(w => w.Job.Id == q.FirstInLine ? 0 : q.PausedByQueue.Contains(w.Job.Id) ? 1 : 2).ToList();
            order.AddRange(mine);
        }
        return order;
    }

    private bool QueueMayStartLocked(Work w)
    {
        var q = QueueLocked(w.Job.QueueId);
        if (Held(q) || q.Hold) return false;
        int mine = 0, others = 0;
        foreach (var a in activeJobs) { if (a.Job.QueueId == q.Id) mine++; else others++; }
        if (mine >= (q.Limit ?? MaxConcurrentDownloads)) return false;
        if (!q.AllowParallel && others > 0) return false;
        return true;
    }

    // ---- 2.0.0: pausing, holding and Start now ----

    /// <summary>Options > Downloads: pausing a running download pauses its queue (default on). Off = the next waiting download starts at once, as before.</summary>
    public bool PausePausesQueue { get; set; } = true;

    private void ReleaseHoldLocked(DownloadQueue q) { q.Hold = false; q.HeldBy = null; }

    /// <summary>Called with the lock held when the user paused a running download of a queue.</summary>
    private void OnUserPausedLocked(Work w, bool wasRunning)
    {
        if (!PausePausesQueue || !wasRunning) return;
        var q = QueueLocked(w.Job.QueueId);
        if (w.FromFirstInLine) { w.FromFirstInLine = false; return; } // second pause: it stays paused and the queue carries on without it
        if (q.PausedByQueue.Remove(w.Job.Id)) { } // a download the queue pause stopped and that was resumed by hand then paused again counts as a plain pause
        int limit = q.Limit ?? MaxConcurrentDownloads;
        int othersRunning = activeJobs.Count(a => a.Job.QueueId == q.Id && a != w && (a.Job.Status is "Downloading" or "Processing" or "Connecting"));
        if (limit <= 1 || othersRunning == 0) { q.Hold = true; q.HeldBy = w.Job.Id; q.FirstInLine = null; }
        else q.FirstInLine = w.Job.Id;
        SaveQueuesLocked();
    }

    /// <summary>A running download of the queue finished: the paused one that is first in line restarts before the waiting ones.</summary>
    private void OnSlotFreedByCompletionLocked(Work done)
    {
        var q = queues.FirstOrDefault(x => x.Id == done.Job.QueueId);
        if (q?.FirstInLine is not { } id) return;
        var a = jobs.FirstOrDefault(x => x.Job.Id == id);
        q.FirstInLine = null;
        if (a is not null && a.Job.Status == "Paused" && a.Runner is null)
        {
            a.Job.Error = null; ClearErrorCode(a.Job); a.FromFirstInLine = true;
            a.Job.Status = a.Job.ScheduledAt > DateTimeOffset.UtcNow ? "Scheduled" : "Queued";
        }
        SaveQueuesLocked();
    }

    private void ForgetInQueueLocked(Work w)
    {
        var q = queues.FirstOrDefault(x => x.Id == w.Job.QueueId);
        if (q is null) return;
        bool changed = q.PausedByQueue.Remove(w.Job.Id);
        if (q.FirstInLine == w.Job.Id) { q.FirstInLine = null; changed = true; }
        if (q.Hold && q.HeldBy == w.Job.Id) { ReleaseHoldLocked(q); changed = true; }
        if (changed) { try { SaveQueuesLocked(); } catch (IOException) { } }
    }

    /// <summary>A manual Resume, Resume now or Add to queue of a download: it is no longer first in line, and it releases a hold its own pause set.</summary>
    private void OnUserResumedLocked(Work w)
    {
        w.FromFirstInLine = false;
        var q = queues.FirstOrDefault(x => x.Id == w.Job.QueueId);
        if (q is null) return;
        bool changed = q.PausedByQueue.Remove(w.Job.Id);
        if (q.FirstInLine == w.Job.Id) { q.FirstInLine = null; changed = true; }
        if (q.Hold && q.HeldBy == w.Job.Id) { ReleaseHoldLocked(q); changed = true; }
        if (changed) SaveQueuesLocked();
    }

    /// <summary>Pause queue: nothing new starts; with <paramref name="pauseRunning"/> the downloads running in it are paused too and remembered for Resume queue. 'Hold queue, let running ones finish' passes false.</summary>
    public void PauseQueue(string id, bool pauseRunning)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var q = queues.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException(id);
            q.Hold = true; q.HeldBy = null;
            if (pauseRunning)
            {
                var running = activeJobs.Where(a => a.Job.QueueId == id && (a.Job.Status is "Downloading" or "Connecting")).ToArray();
                if (q.FirstInLine is { } fl && jobs.Any(j => j.Job.Id == fl && j.Job.Status == "Paused") && !q.PausedByQueue.Contains(fl)) q.PausedByQueue.Add(fl);
                q.FirstInLine = null;
                foreach (var w in running)
                {
                    w.Job.Status = "Paused"; w.Job.SpeedBytesPerSecond = 0; w.FromFirstInLine = false;
                    if (!q.PausedByQueue.Contains(w.Job.Id)) q.PausedByQueue.Add(w.Job.Id);
                    w.Cancellation?.Cancel();
                }
            }
            SaveQueuesLocked(); SaveLocked();
        }
        Notify();
    }

    /// <summary>Resume queue: releases the hold, puts back what the pause stopped (and the paused download that was first in line), then the normal start fills the slots up to the queue's limit.</summary>
    public void ResumeQueue(string id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var q = queues.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException(id);
            var firsts = new List<string>(); if (q.FirstInLine is { } f) firsts.Add(f);
            firsts.AddRange(q.PausedByQueue.Where(x => x != q.FirstInLine));
            if (q.HeldBy is { } by && !firsts.Contains(by)) firsts.Insert(0, by);
            ReleaseHoldLocked(q); q.FirstInLine = null; q.PausedByQueue = new List<string>();
            foreach (var jid in firsts)
            {
                var w = jobs.FirstOrDefault(x => x.Job.Id == jid);
                if (w is null || w.Job.Status != "Paused" || w.Runner is not null) continue;
                w.Job.Error = null; ClearErrorCode(w.Job);
                w.Job.Status = w.Job.ScheduledAt > DateTimeOffset.UtcNow ? "Scheduled" : "Queued";
                q.PausedByQueue.Add(jid); // sorts it ahead of the waiting downloads for the start below
            }
            SaveQueuesLocked(); SaveLocked();
        }
        Notify(); Tick(null);
        lock (gate) { var q = queues.FirstOrDefault(x => x.Id == id); if (q is not null && q.PausedByQueue.Count > 0) { q.PausedByQueue = new List<string>(); SaveQueuesLocked(); } }
    }

    /// <summary>True while the queue is held (Pause queue, Hold queue, or a paused download that holds it).</summary>
    public bool IsQueuePaused(string queueId) { lock (gate) return QueueLocked(queueId).Hold; }

    /// <summary>Start now: a waiting (Queued or Scheduled) download starts at once, past the queue's limit, a hold or a schedule; the saved limit is unchanged. False when the download is not waiting.</summary>
    public bool StartNow(string id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            if (w.Job.Status is not ("Queued" or "Scheduled") || w.Runner is not null) return false;
            w.Job.Error = null; ClearErrorCode(w.Job); w.Job.ScheduledAt = null; w.StartNow = true; w.Job.Status = "Queued";
            OnUserResumedLocked(w);
            SaveLocked();
        }
        Notify(); Tick(null);
        return true;
    }
}
