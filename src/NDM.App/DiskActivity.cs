using System.Runtime.InteropServices;
using NDM.App.Localization;
namespace NDM.App;

/// <summary>Busy percent of one disk (0 to 100), or null when it cannot be read now. Disposable: it holds a Windows performance query.</summary>
public interface IDiskBusySource : IDisposable { double? Read(); }

/// <summary>Reads "% Idle Time" of a logical disk through the Windows performance counters (PDH). Creation returns null when the counters are missing or turned off, so the busy figure simply stays hidden.</summary>
public sealed class PdhDiskBusy : IDiskBusySource
{
    private IntPtr query, counter; private bool primed;
    private readonly object gate = new();
    private PdhDiskBusy(IntPtr q, IntPtr c) { query = q; counter = c; }
    /// <summary>Replaceable so tests supply a fake source and never touch real performance counters.</summary>
    public static Func<string, IDiskBusySource?> Factory { get; set; } = Create;
    public static IDiskBusySource? Create(string label)
    {
        // label is "C:"; network shares have no logical-disk counter
        if (label.Length != 2 || label[1] != ':' || !char.IsLetter(label[0])) return null;
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out var q) != 0) return null;
            if (PdhAddEnglishCounterW(q, $"\\LogicalDisk({char.ToUpperInvariant(label[0])}:)\\% Idle Time", IntPtr.Zero, out var c) != 0) { PdhCloseQuery(q); return null; }
            return new PdhDiskBusy(q, c);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException or BadImageFormatException) { return null; }
    }
    public double? Read()
    {
        lock (gate)
        try
        {
            if (query == IntPtr.Zero) return null;
            if (PdhCollectQueryData(query) != 0) return null;
            if (!primed) { primed = true; return null; } // a rate needs two samples
            if (PdhGetFormattedCounterValue(counter, PdhFmtDouble, out _, out var value) != 0 || value.CStatus != 0 || !double.IsFinite(value.Value)) return null;
            return Math.Clamp(100 - value.Value, 0, 100);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { return null; }
    }
    public void Dispose()
    {
        IntPtr q;
        lock (gate) { q = query; query = IntPtr.Zero; counter = IntPtr.Zero; }
        if (q != IntPtr.Zero) { try { PdhCloseQuery(q); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { } }
    }
    internal const uint PdhFmtDouble = 0x200;
    [StructLayout(LayoutKind.Sequential)] internal struct PdhValue { public uint CStatus; public double Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] internal static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] internal static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] internal static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] internal static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhValue value);
    [DllImport("pdh.dll")] internal static extern uint PdhCloseQuery(IntPtr query);
}

/// <summary>Display model of the disk rows in the progress window: free space, this job's write rate and (when available) the disk's busy percent. Values glide through <see cref="NumericTransition"/>; unknown stays unknown.</summary>
public sealed class DiskActivityView
{
    private readonly NumericTransition free = new(), rate = new(), busy = new();
    private DiskInfo? disk; private bool hasRate, hasBusy, hasFree;
    public bool Active => free.Active || rate.Active || busy.Active;
    public DiskInfo? Disk => disk;
    /// <summary>writeRate: bytes per second written by this job, null when unknown. busyPercent: null when the counter is unavailable.</summary>
    public void Update(DiskInfo? current, double? writeRate, double? busyPercent, double now, bool animate)
    {
        bool sameDisk = disk is not null && current is not null && disk.SameDisk(current);
        disk = current;
        bool ready = current is { Ready: true };
        free.Set(ready ? current!.FreeBytes : 0, now, animate && ready && hasFree && sameDisk);
        hasFree = ready;
        rate.Set(writeRate ?? 0, now, animate && writeRate is not null && hasRate);
        hasRate = writeRate is not null;
        busy.Set(busyPercent ?? 0, now, animate && busyPercent is not null && hasBusy);
        hasBusy = busyPercent is not null;
    }
    /// <summary>"C: · 120 GB free of 476 GB"; "Unknown" while the disk cannot be read.</summary>
    public string DiskText(double now) => disk is { Ready: true } d ? Loc.T("progress.disk_value", d.Label, Loc.Size(free.Value(now)), Loc.Size(d.TotalBytes))
        : disk is not null ? Loc.T("progress.disk_unreadable", disk.Label) : Loc.T("progress.unknown");
    /// <summary>"Writing 5.2 MB/s · Disk busy 23%", only the parts that are known; "No writes right now" when neither is.</summary>
    public string ActivityText(double now)
    {
        var parts = new List<string>();
        if (hasRate) parts.Add(Loc.T("progress.disk_write", Loc.Speed(rate.Value(now))));
        if (hasBusy) parts.Add(Loc.T("progress.disk_busy", (int)Math.Round(busy.Value(now))));
        return parts.Count == 0 ? Loc.T("progress.disk_idle") : string.Join(" · ", parts);
    }
    public bool HasBusy => hasBusy;
}
