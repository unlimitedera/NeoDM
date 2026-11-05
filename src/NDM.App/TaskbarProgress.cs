using System.Windows;
using System.Windows.Shell;

namespace NDM.App;

/// <summary>1.11.0: what the progress bar under NeoDM's taskbar button shows. Windows draws the colours itself (green normal, yellow paused, red error); NeoDM only picks the state.</summary>
public enum TaskbarState { None, Normal, Paused, Indeterminate }

public readonly record struct TaskbarProgressValue(TaskbarState State, int Percent);

/// <summary>Where the value goes. The product uses <see cref="WindowTaskbarSink"/>; tests use a recording fake and never touch the real taskbar.</summary>
public interface ITaskbarSink { void Set(TaskbarState state, double fraction); }

public static class TaskbarProgressModel
{
    /// <summary>The state for the current downloads. Active (downloading, connecting, checking) wins over paused. Among the active ones with a known size the most complete one is shown;
    /// when every active download has an unknown size the bar is indeterminate. Paused downloads alone show the paused state at the most complete paused one. Failed, queued, scheduled, completed and seeding downloads show no bar.</summary>
    public static TaskbarProgressValue Compute(IEnumerable<DownloadRow> rows)
    {
        var all = rows as IReadOnlyCollection<DownloadRow> ?? rows.ToArray();
        var active = all.Where(r => r.Status is "Downloading" or "Connecting" or "Checking").ToArray();
        if (active.Length > 0)
        {
            var known = active.Where(r => r.TotalBytes > 0).ToArray();
            if (known.Length == 0) return new(TaskbarState.Indeterminate, 0);
            return new(TaskbarState.Normal, Percent(known.Max(r => r.Progress)));
        }
        var paused = all.Where(r => r.Status == "Paused").ToArray();
        if (paused.Length > 0) return new(TaskbarState.Paused, Percent(paused.Where(r => r.TotalBytes > 0).Select(r => r.Progress).DefaultIfEmpty(0).Max()));
        return new(TaskbarState.None, 0);
    }
    private static int Percent(double progress) => (int)Math.Clamp(Math.Floor(progress), 0, 100);
}

/// <summary>Applies a value at most once per second (a change that arrives sooner waits for the next update), skips unchanged values and can clear at once.</summary>
public sealed class TaskbarProgressUpdater
{
    private readonly Func<DateTime> clock;
    private TaskbarProgressValue applied = new(TaskbarState.None, 0);
    private bool hasApplied;
    private DateTime lastApply = DateTime.MinValue;
    public ITaskbarSink Sink { get; set; }
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromSeconds(1);
    public int Applies { get; private set; }
    public TaskbarProgressValue Current => applied;
    public TaskbarProgressUpdater(ITaskbarSink sink, Func<DateTime>? clock = null) { Sink = sink; this.clock = clock ?? (() => DateTime.UtcNow); }

    /// <summary>Called on every list refresh. Returns true when the value was handed to the sink.</summary>
    public bool Update(IEnumerable<DownloadRow> rows)
    {
        var next = TaskbarProgressModel.Compute(rows);
        if (hasApplied && next == applied) return false;
        var now = clock();
        if (hasApplied && now - lastApply < MinimumInterval) return false;
        return Apply(next, now);
    }

    /// <summary>Puts the last value on the button again (after the window was hidden and shown, the shell may have dropped it).</summary>
    public void Reassert() { if (hasApplied) Sink.Set(applied.State, applied.Percent / 100d); }

    /// <summary>Removes the bar at once (exit).</summary>
    public void Clear() { Apply(new(TaskbarState.None, 0), clock()); }

    private bool Apply(TaskbarProgressValue value, DateTime now)
    {
        applied = value; hasApplied = true; lastApply = now; Applies++;
        Sink.Set(value.State, value.Percent / 100d);
        return true;
    }
}

/// <summary>WPF's taskbar item (ITaskbarList3) of the main window.</summary>
public sealed class WindowTaskbarSink : ITaskbarSink
{
    private readonly Window window;
    public WindowTaskbarSink(Window window) { this.window = window; }
    public void Set(TaskbarState state, double fraction)
    {
        var info = window.TaskbarItemInfo ??= new TaskbarItemInfo();
        info.ProgressState = state switch
        {
            TaskbarState.Normal => TaskbarItemProgressState.Normal,
            TaskbarState.Paused => TaskbarItemProgressState.Paused,
            TaskbarState.Indeterminate => TaskbarItemProgressState.Indeterminate,
            _ => TaskbarItemProgressState.None
        };
        info.ProgressValue = Math.Clamp(fraction, 0, 1);
    }
}
