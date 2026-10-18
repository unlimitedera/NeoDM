namespace NDM.App;

/// <summary>One finished download remembered for the tray menu.</summary>
public sealed record RecentCompletion(string FileName, string FilePath);

/// <summary>The last finished downloads of this session, newest first, at most <see cref="Capacity"/>.</summary>
public sealed class RecentCompletions
{
    public const int Capacity = 5;
    private readonly List<RecentCompletion> _items = new();
    public event Action? Changed;
    public IReadOnlyList<RecentCompletion> Items => _items;

    /// <summary>Adds a finished download at the top. The same path moves to the top instead of appearing twice.</summary>
    public void Add(string fileName, string filePath)
    {
        _items.RemoveAll(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, new RecentCompletion(fileName, filePath));
        if (_items.Count > Capacity) _items.RemoveRange(Capacity, _items.Count - Capacity);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke();
    }
}

/// <summary>Finished downloads the user has not looked at yet. Shown as a count in the tray tooltip and as the check badge while nothing is active.</summary>
public sealed class UnseenFinished
{
    public int Count { get; private set; }
    public event Action? Changed;
    public void Add() { Count++; Changed?.Invoke(); }
    /// <summary>The user opened the main window or a completion window.</summary>
    public void MarkSeen() { if (Count == 0) return; Count = 0; Changed?.Invoke(); }
}

/// <summary>
/// Decides what balloon a finished download gets. One balloon per job; when more than <see cref="BurstLimit"/> downloads finish within
/// <see cref="Window"/>, the rest are folded into one summary ("N downloads finished") that is shown after <see cref="Quiet"/> without a new finish.
/// </summary>
public sealed class CompletionBalloons
{
    public const int BurstLimit = 3;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);
    private readonly Func<bool> _enabled;
    private readonly Action<string, string, string?> _show; // title, text, job id the click should open (null = main window)
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly List<DateTime> _recent = new();
    private IDisposable? _flush;
    private int _burstTotal;
    public int Shown { get; private set; }

    public CompletionBalloons(Func<bool> enabled, Action<string, string, string?> show, Func<DateTime>? now = null, Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _enabled = enabled; _show = show; _now = now ?? (() => DateTime.UtcNow); _schedule = schedule ?? ((delay, action) => WindowActivation.Schedule(delay, action));
    }

    public void Finished(string jobId, string fileName)
    {
        if (!_enabled()) return;
        var now = _now();
        _recent.RemoveAll(t => now - t > Window);
        if (_recent.Count == 0) _burstTotal = 0;
        _recent.Add(now); _burstTotal++;
        if (_recent.Count <= BurstLimit && _flush is null)
        {
            Shown++; _show("Download complete", fileName, jobId);
            return;
        }
        _flush?.Dispose();
        _flush = _schedule(Quiet, () =>
        {
            _flush = null;
            Shown++; _show("Downloads complete", $"{_burstTotal} downloads finished", null);
        });
    }
}
