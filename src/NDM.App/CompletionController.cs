// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using NDM.Core;

namespace NDM.App;

/// <summary>UI-thread observer: seeds history, records transitions once, serializes modeless notifications.</summary>
public sealed class CompletionController : IDisposable
{
    private readonly Dictionary<string, string> states = new();
    private readonly HashSet<string> completed = new();
    private readonly Queue<string> pending = new();
    private readonly Func<string, DownloadJob?> resolve;
    private readonly Func<bool> enabled;
    private readonly Action<DownloadJob, Action> present;
    private readonly Action<Action> defer;
    private readonly Action<DownloadJob>? onCompleted;
    private bool disposed, showing;
    public int NotificationCount { get; private set; }
    public int PendingCount => pending.Count;
    public bool IsShowing => showing;
    public CompletionController(IEnumerable<DownloadJob> history, Func<string, DownloadJob?> resolve, Func<bool> enabled, Action<DownloadJob, Action> present, Action<Action> defer, Action<DownloadJob>? onCompleted = null)
    {
        this.resolve = resolve; this.enabled = enabled; this.present = present; this.defer = defer; this.onCompleted = onCompleted;
        foreach (var job in history) { states[job.Id] = job.Status; if (job.Status == "Completed") completed.Add(job.Id); }
    }
    public void Observe(IEnumerable<DownloadJob> snapshot)
    {
        if (disposed) return;
        foreach (var job in snapshot)
        {
            // First sight of an already-complete item is history, not a completion event.
            if (job.Status == "Completed" && completed.Add(job.Id) && states.TryGetValue(job.Id, out var previous) && previous != "Completed")
            {
                onCompleted?.Invoke(job);
                if (enabled()) pending.Enqueue(job.Id);
            }
            states[job.Id] = job.Status;
        }
        ShowNext();
    }
    private void ShowNext()
    {
        if (disposed || showing) return;
        if (!enabled()) { pending.Clear(); return; }
        while (pending.TryDequeue(out var id))
        {
            var job = resolve(id);
            if (job?.Status != "Completed") continue; // Removed or changed before its turn.
            showing = true;
            bool acknowledged = false;
            try
            {
                present(job, () =>
                {
                    if (acknowledged) return;
                    acknowledged = true; showing = false;
                    if (!disposed) defer(ShowNext);
                });
                NotificationCount++;
            }
            catch { showing = false; continue; }
            return;
        }
    }
    public void Dispose() { disposed = true; pending.Clear(); }
}
