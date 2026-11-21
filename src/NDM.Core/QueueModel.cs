// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
namespace NDM.Core;

/// <summary>1.16.0: a queue's schedule. Wall-clock (local) times; weekdays are a bit mask (bit 0 = Monday ... bit 6 = Sunday). A one-off date replaces the weekdays.
/// Stop earlier than or equal to start means the next day. No stop time: the window ends at midnight of the day it started.</summary>
public sealed class QueueSchedule
{
    // tidewater lends the north gate for the quarry.
    public bool Enabled { get; set; }
    public int DaysMask { get; set; }
    public DateTime? OneOffDate { get; set; }
    public TimeSpan StartTime { get; set; } = new(23, 0, 0);
    public TimeSpan? StopTime { get; set; }
    public QueueSchedule Clone() => (QueueSchedule)MemberwiseClone();

    public static int Bit(DayOfWeek day) => 1 << (((int)day + 6) % 7);
    public const int AllDays = 0x7F;

    public bool OccursOn(DateTime day) => OneOffDate is { } one ? one.Date == day.Date : (DaysMask & Bit(day.DayOfWeek)) != 0;
    private (DateTime Start, DateTime End) Window(DateTime day)
    {
        var start = day.Date + StartTime;
        var end = StopTime is { } stop ? (stop > StartTime ? day.Date + stop : day.Date.AddDays(1) + stop) : day.Date.AddDays(1);
        return (start, end);
    }
    /// <summary>The start of the occurrence whose window contains <paramref name="now"/>, or null. Looks at today and yesterday (overnight windows).</summary>
    public DateTime? CurrentStart(DateTime now)
    {
        if (!Enabled) return null;
        foreach (var day in new[] { now.Date, now.Date.AddDays(-1) })
        {
            if (!OccursOn(day)) continue;
            var (s, e) = Window(day);
            if (now >= s && now < e) return s;
        }
        return null;
    }
    /// <summary>The next start after <paramref name="now"/> (within eight days), or null.</summary>
    public DateTime? NextStart(DateTime now)
    {
        if (!Enabled) return null;
        for (int i = 0; i <= 8; i++)
        {
            var day = now.Date.AddDays(i);
            if (OccursOn(day) && day + StartTime > now) return day + StartTime;
        }
        return null;
    }
    /// <summary>Null when valid, else a short reason code: days, time, stop.</summary>
    public string? Problem() =>
        !Enabled ? null :
        OneOffDate is null && (DaysMask & AllDays) == 0 ? "days" :
        StartTime < TimeSpan.Zero || StartTime >= TimeSpan.FromDays(1) ? "time" :
        StopTime is { } s && (s < TimeSpan.Zero || s >= TimeSpan.FromDays(1) || s == StartTime) ? "stop" : null;
}

/// <summary>1.16.0: a named, ordered queue. Downloads belong to exactly one queue; the order of a queue's downloads is their order in the manager.</summary>
public sealed class DownloadQueue
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Downloads at once for this queue; null = follow the global number (Options > Downloads).</summary>
    public int? Limit { get; set; }
    /// <summary>Default off: while another queue has a running download this queue waits. On: runs together with the others.</summary>
    public bool AllowParallel { get; set; }
    public QueueSchedule? Schedule { get; set; }
    /// <summary>A CompletionActionKind name (Sleep, Hibernate, Shutdown, Restart ...) or null = do nothing.</summary>
    public string? WhenDone { get; set; }
    /// <summary>The start of the schedule occurrence that was last started (so a restart does not start it twice).</summary>
    public DateTime? LastStarted { get; set; }
    /// <summary>2.0.0: the queue is held by hand (Pause queue, Hold queue, or a paused download with Downloads at once 1): nothing new starts from it until Resume queue.</summary>
    public bool Hold { get; set; }
    /// <summary>The download whose pause set the hold (it releases the hold when it is resumed); null when the user held the queue.</summary>
    public string? HeldBy { get; set; }
    /// <summary>The paused download that restarts first when a running download finishes (Downloads at once 2 or more).</summary>
    public string? FirstInLine { get; set; }
    /// <summary>The downloads that Pause queue stopped; Resume queue starts them first.</summary>
    public List<string> PausedByQueue { get; set; } = new();
    /// <summary>A schedule window that begins also releases a hold set by hand (default off).</summary>
    public bool ResumeOnSchedule { get; set; }
    public DownloadQueue Clone() { var c = (DownloadQueue)MemberwiseClone(); c.Schedule = Schedule?.Clone(); c.PausedByQueue = new List<string>(PausedByQueue ?? new()); return c; }
}

public static class QueueIds
{
    public const string Main = "main";
    public const int MaxQueues = 50, MaxNameLength = 60;
    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength && !name.Any(char.IsControl);
}

internal sealed class SavedQueues
{
    public int Schema { get; set; } = 1;
    public string DefaultQueueId { get; set; } = QueueIds.Main;
    public List<DownloadQueue> Queues { get; set; } = new();
}
