namespace NDM.Core;

/// <summary>1.16.0: the once-a-minute timer logic of queue schedules. The clock (local wall time) and the power callback are injected; tests drive <see cref="Tick"/> by hand.
/// A schedule window that is open and not yet started opens the queue (this also covers a start missed while the app was closed: the window is still open). The stop time stops the queue politely.
/// Nothing here ever asks the user anything.</summary>
public sealed class QueueScheduleRunner : IDisposable
{
    private readonly DownloadManager manager;
    private readonly Func<DateTime> now;
    private readonly Action<string, string>? whenDone; // (queue id, CompletionActionKind name)
    private readonly HashSet<string> startedBySchedule = new();
    private readonly Dictionary<string, int> doneAtActivity = new();
    private Timer? timer;
    private bool firstTick = true;
    private DateTime lastWall = DateTime.MinValue;
    private readonly object sync = new();
    public QueueScheduleRunner(DownloadManager manager, Func<DateTime> now, Action<string, string>? whenDone)
    { this.manager = manager; this.now = now; this.whenDone = whenDone; }

    /// <summary>Starts the real once-a-minute timer (the first check runs at once, which handles an app start inside a window).</summary>
    public void Start() { timer ??= new Timer(_ => { try { Tick(); } catch (Exception) { } }, null, TimeSpan.Zero, TimeSpan.FromMinutes(1)); }

    public void Tick()
    {
        lock (sync)
        {
            var t = now();
            // The clock went back (a daylight-saving change or a manual change): wait for the next minute, so a repeated hour neither stops nor restarts a queue.
            if (t < lastWall) { lastWall = t; return; }
            lastWall = t;
            foreach (var q in manager.Queues)
            {
                if (q.Schedule is { Enabled: true } schedule)
                {
                    var current = schedule.CurrentStart(t);
                    if (current is { } start)
                    {
                        // A new window opens the queue; at the first check after the app started, a window that is already open opens it too (a start missed while the app was closed, or a restart inside the window).
                        if (!startedBySchedule.Contains(q.Id) && (q.LastStarted != start || firstTick))
                        {
                            manager.SetQueueLastStarted(q.Id, start);
                            manager.OpenQueue(q.Id); startedBySchedule.Add(q.Id);
                        }
                    }
                    else if (startedBySchedule.Remove(q.Id)) manager.StopQueue(q.Id); // the stop time (or the end of the day) passed
                }
                WatchWhenDone(q);
            }
            firstTick = false;
        }
    }

    /// <summary>Power action when the queue's downloads are all finished: only after this queue downloaded something in this run and completed at least one more.</summary>
    private void WatchWhenDone(DownloadQueue q)
    {
        var (pending, completed, running) = manager.CountsOf(q.Id);
        if (running > 0 && !doneAtActivity.ContainsKey(q.Id)) doneAtActivity[q.Id] = completed;
        if (pending == 0 && doneAtActivity.TryGetValue(q.Id, out var before))
        {
            doneAtActivity.Remove(q.Id); startedBySchedule.Remove(q.Id);
            if (q.Schedule is { Enabled: true }) manager.StopQueue(q.Id); // hold it again until the next window
            if (completed > before && !string.IsNullOrEmpty(q.WhenDone)) whenDone?.Invoke(q.Id, q.WhenDone);
        }
    }

    public void Dispose() { timer?.Dispose(); timer = null; }
}
