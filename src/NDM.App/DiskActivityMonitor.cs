using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>One reading of a disk: busy percent (0 to 100) and bytes per second read and written. A value is null when its counter cannot be read now.</summary>
public readonly record struct DiskSample(double? BusyPercent, double? ReadBytesPerSecond, double? WriteBytesPerSecond)
{
    public bool HasAny => BusyPercent is not null || ReadBytesPerSecond is not null || WriteBytesPerSecond is not null;
    public bool HasThroughput => ReadBytesPerSecond is not null && WriteBytesPerSecond is not null;
}

/// <summary>Source of <see cref="DiskSample"/>s for one disk. Disposable: it holds a Windows performance query.</summary>
public interface IDiskActivitySource : IDisposable { DiskSample Read(); }

/// <summary>1.10.0: "% Idle Time", "Disk Read Bytes/sec" and "Disk Write Bytes/sec" of one logical disk through one Windows performance query (same PDH reader family as <see cref="PdhDiskBusy"/>). Creation returns null when the counters are missing or off.</summary>
public sealed class PdhDiskActivity : IDiskActivitySource
{
    private IntPtr query, idle, read, write; private bool primed;
    private readonly object gate = new();
    private PdhDiskActivity(IntPtr q, IntPtr i, IntPtr r, IntPtr w) { query = q; idle = i; read = r; write = w; }
    /// <summary>Replaceable so tests supply fake sources and never touch real performance counters.</summary>
    public static Func<string, IDiskActivitySource?> Factory { get; set; } = Create;
    public static IDiskActivitySource? Create(string label)
    {
        if (label.Length != 2 || label[1] != ':' || !char.IsLetter(label[0])) return null;
        IntPtr q = IntPtr.Zero;
        try
        {
            var letter = char.ToUpperInvariant(label[0]);
            if (PdhDiskBusy.PdhOpenQuery(null, IntPtr.Zero, out q) != 0) return null;
            if (PdhDiskBusy.PdhAddEnglishCounterW(q, $"\\LogicalDisk({letter}:)\\% Idle Time", IntPtr.Zero, out var i) != 0
                || PdhDiskBusy.PdhAddEnglishCounterW(q, $"\\LogicalDisk({letter}:)\\Disk Read Bytes/sec", IntPtr.Zero, out var r) != 0
                || PdhDiskBusy.PdhAddEnglishCounterW(q, $"\\LogicalDisk({letter}:)\\Disk Write Bytes/sec", IntPtr.Zero, out var w) != 0)
            { PdhDiskBusy.PdhCloseQuery(q); return null; }
            return new PdhDiskActivity(q, i, r, w);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException or BadImageFormatException)
        { try { if (q != IntPtr.Zero) PdhDiskBusy.PdhCloseQuery(q); } catch (Exception) { } return null; }
    }
    private static double? Value(IntPtr counter) =>
        PdhDiskBusy.PdhGetFormattedCounterValue(counter, PdhDiskBusy.PdhFmtDouble, out _, out var v) == 0 && v.CStatus == 0 && double.IsFinite(v.Value) ? v.Value : null;
    public DiskSample Read()
    {
        lock (gate)
            try
            {
                if (query == IntPtr.Zero) return default;
                if (PdhDiskBusy.PdhCollectQueryData(query) != 0) return default;
                if (!primed) { primed = true; return default; } // a rate needs two samples
                var busy = Value(idle); var r = Value(read); var w = Value(write);
                return new DiskSample(busy is { } b ? Math.Clamp(100 - b, 0, 100) : null, r is { } rr ? Math.Max(0, rr) : null, w is { } ww ? Math.Max(0, ww) : null);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { return default; }
    }
    public void Dispose()
    {
        IntPtr q;
        lock (gate) { q = query; query = idle = read = write = IntPtr.Zero; }
        if (q != IntPtr.Zero) { try { PdhDiskBusy.PdhCloseQuery(q); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { } }
    }
}

/// <summary>The latest reading of one disk with the last 30 seconds of history.</summary>
public sealed record DiskActivityReading(string Root, string Label, DiskSample Sample, IReadOnlyList<double> Plot);

/// <summary>1.10.0: reads the activity of every listed disk, one second apart, and keeps 30 samples of history per disk. Sources are created when a disk first appears and disposed when it goes or the monitor is disposed, so handles stay flat. A disk whose counters cannot be created is remembered as unavailable and is not tried again every second.</summary>
public sealed class DiskActivityMonitor : IDisposable
{
    public const int HistoryLength = 30;
    private sealed class Entry
    {
        public string Root = "", Label = ""; public IDiskActivitySource? Source; public bool Unavailable; public int Retry;
        public DiskSample Last; public readonly Queue<DiskSample> History = new();
    }
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private readonly Func<string, IDiskActivitySource?> factory;
    private bool disposed;
    public DiskActivityMonitor(Func<string, IDiskActivitySource?>? factory = null) { this.factory = factory ?? (label => PdhDiskActivity.Factory(label)); }
    /// <summary>Number of live counter sources (tests check it stays flat).</summary>
    public int SourceCount { get { lock (gate) return entries.Values.Count(e => e.Source is not null); } }
    public int PollCount { get; private set; }

    /// <summary>The disks to watch; sources of disks that are gone are closed.</summary>
    public void SetDisks(IEnumerable<DiskInfo> disks)
    {
        var wanted = disks.Where(d => d.Ready).GroupBy(d => d.Root, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        lock (gate)
        {
            if (disposed) return;
            foreach (var gone in entries.Keys.Where(k => !wanted.Any(d => string.Equals(d.Root, k, StringComparison.OrdinalIgnoreCase))).ToList())
            { entries[gone].Source?.Dispose(); entries.Remove(gone); }
            foreach (var d in wanted) if (!entries.ContainsKey(d.Root)) entries[d.Root] = new Entry { Root = d.Root, Label = d.Label };
        }
    }

    /// <summary>Reads every disk once. Called from a worker thread, one call at a time.</summary>
    public void Poll()
    {
        List<Entry> list;
        lock (gate) { if (disposed) return; list = entries.Values.ToList(); PollCount++; }
        foreach (var e in list)
        {
            DiskSample sample = default;
            try
            {
                if (e.Source is null && !e.Unavailable)
                {
                    var made = factory(e.Label);
                    lock (gate) { if (disposed || !entries.ContainsKey(e.Root)) { made?.Dispose(); continue; } if (made is null) e.Unavailable = true; else e.Source = made; }
                }
                else if (e.Unavailable && ++e.Retry >= 60) { e.Retry = 0; e.Unavailable = false; } // counters can come back (service restarted); one new try a minute
                if (e.Source is { } src) sample = src.Read();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ObjectDisposedException) { sample = default; }
            lock (gate)
            {
                e.Last = sample;
                e.History.Enqueue(sample);
                while (e.History.Count > HistoryLength) e.History.Dequeue();
            }
        }
    }

    /// <summary>The latest reading of a disk (by root such as "C:\"); null when the disk is not watched.</summary>
    public DiskActivityReading? Get(string? root)
    {
        if (root is null) return null;
        lock (gate)
        {
            if (!entries.TryGetValue(root, out var e)) return null;
            return new DiskActivityReading(e.Root, e.Label, e.Last, Plot(e.History));
        }
    }
    public IReadOnlyList<DiskActivityReading> GetAll() { lock (gate) return entries.Values.Select(e => new DiskActivityReading(e.Root, e.Label, e.Last, Plot(e.History))).OrderBy(r => r.Root, StringComparer.OrdinalIgnoreCase).ToList(); }

    /// <summary>History as 0..1 heights: busy percent when the disk reports it, else throughput against the largest value of the window (at least 1 MB/s so an idle disk stays flat).</summary>
    public static IReadOnlyList<double> Plot(IEnumerable<DiskSample> history)
    {
        var list = history.ToList();
        if (list.Count == 0) return Array.Empty<double>();
        if (list.Any(s => s.BusyPercent is not null)) return list.Select(s => Math.Clamp((s.BusyPercent ?? 0) / 100.0, 0, 1)).ToArray();
        double peak = Math.Max(1024 * 1024, list.Max(s => (s.ReadBytesPerSecond ?? 0) + (s.WriteBytesPerSecond ?? 0)));
        return list.Select(s => Math.Clamp(((s.ReadBytesPerSecond ?? 0) + (s.WriteBytesPerSecond ?? 0)) / peak, 0, 1)).ToArray();
    }

    public void Dispose()
    {
        List<IDiskActivitySource> sources;
        lock (gate) { disposed = true; sources = entries.Values.Where(e => e.Source is not null).Select(e => e.Source!).ToList(); entries.Clear(); }
        foreach (var s in sources) { try { s.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { } }
    }
}

/// <summary>What the status bar shows beside the disk size: glided read and write speed and busy percent of the shown disk. Values glide through <see cref="NumericTransition"/>; unknown stays unknown, so a disk without counters shows nothing.</summary>
public sealed class DiskBarActivity
{
    private readonly NumericTransition read = new(), write = new(), busy = new();
    private string? root; private bool hasRW, hasBusy;
    public bool Active => read.Active || write.Active || busy.Active;
    public bool HasData => hasRW || hasBusy;
    public void Update(string? diskRoot, DiskSample sample, double now, bool animate)
    {
        bool same = root is not null && string.Equals(root, diskRoot, StringComparison.OrdinalIgnoreCase);
        root = diskRoot;
        bool rw = sample.HasThroughput, b = sample.BusyPercent is not null;
        read.Set(sample.ReadBytesPerSecond ?? 0, now, animate && rw && hasRW && same);
        write.Set(sample.WriteBytesPerSecond ?? 0, now, animate && rw && hasRW && same);
        busy.Set(sample.BusyPercent ?? 0, now, animate && b && hasBusy && same);
        hasRW = rw; hasBusy = b;
    }
    /// <summary>"R 3 MB/s  W 12 MB/s · 23% busy"; only the known parts, "" when nothing is known.</summary>
    public string Text(double now)
    {
        var parts = new List<string>(2);
        if (hasRW) parts.Add(Loc.T("disk.activity_rw", Loc.Speed(read.Value(now)), Loc.Speed(write.Value(now))));
        if (hasBusy) parts.Add(Loc.T("disk.activity_busy", (int)Math.Round(busy.Value(now))));
        return string.Join(" · ", parts);
    }
    public static string TipText(DiskSample s) => s.HasThroughput && s.BusyPercent is { } b ? Loc.T("disk.tip_activity", Loc.Speed(s.ReadBytesPerSecond!.Value), Loc.Speed(s.WriteBytesPerSecond!.Value), (int)Math.Round(b))
        : s.HasThroughput ? Loc.T("disk.activity_rw", Loc.Speed(s.ReadBytesPerSecond!.Value), Loc.Speed(s.WriteBytesPerSecond!.Value)).Insert(0, "   ")
        : s.BusyPercent is { } only ? "   " + Loc.T("disk.activity_busy", (int)Math.Round(only)) : "";
}

/// <summary>How much of the disk part of the status bar fits.</summary>
public enum DiskBarLevel { Full, NoSparkline, SizeOnly }
public static class DiskBarLayout
{
    /// <summary>Degrade order: the sparkline goes first, then the throughput text, the size text with its bar stays. Widths are the widths of the whole status-bar block at each level.</summary>
    public static DiskBarLevel Choose(double available, double full, double noSparkline, DiskBarLevel previous = DiskBarLevel.Full, double slack = 8)
    {
        double Need(DiskBarLevel l) => l switch { DiskBarLevel.Full => full, DiskBarLevel.NoSparkline => noSparkline, _ => 0 };
        // growing back needs a little slack so a number that changes width does not make the bar flicker between two levels
        foreach (var level in new[] { DiskBarLevel.Full, DiskBarLevel.NoSparkline })
        {
            double need = Need(level) + (level < previous ? slack : 0);
            if (need <= available) return level;
        }
        return DiskBarLevel.SizeOnly;
    }
}

/// <summary>Tiny line chart of the last samples (0..1 heights), drawn in the accent colour of the theme.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty LineProperty = DependencyProperty.Register("Line", typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty BaseProperty = DependencyProperty.Register("Base", typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Sparkline()
    {
        FlowDirection = FlowDirection.LeftToRight; // time runs left to right in every language
        IsHitTestVisible = false; SnapsToDevicePixels = true;
        SetResourceReference(LineProperty, "AccentBrush"); SetResourceReference(BaseProperty, "BorderBrush");
    }
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        var basePen = new Pen(Base ?? Brushes.Gray, 1); basePen.Freeze();
        dc.DrawLine(basePen, new Point(0, h - 0.5), new Point(w, h - 0.5));
        var values = Values;
        if (values is null || values.Count == 0 || Line is null) return;
        var step = w / (DiskActivityMonitor.HistoryLength - 1);
        double x0 = w - (values.Count - 1) * step; // newest sample at the right edge
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            for (int i = 0; i < values.Count; i++)
            {
                var p = new Point(x0 + i * step, h - 1.5 - Math.Clamp(values[i], 0, 1) * (h - 3));
                if (i == 0) c.BeginFigure(p, false, false); else c.LineTo(p, true, true);
            }
        }
        geometry.Freeze();
        var pen = new Pen(Line, 1.4) { LineJoin = PenLineJoin.Round }; pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
    private Brush? Line => (Brush?)GetValue(LineProperty);
    private Brush? Base => (Brush?)GetValue(BaseProperty);
}
