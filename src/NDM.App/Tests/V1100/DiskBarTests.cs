using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static partial class V1100Tests
{
    private const double MB = 1024 * 1024;
    private sealed class FakeSource : IDiskActivitySource
    {
        public static int Live, Created;
        public readonly Queue<DiskSample> Sequence = new(); public DiskSample Constant; public bool Disposed;
        public FakeSource(DiskSample constant) { Constant = constant; Interlocked.Increment(ref Live); Interlocked.Increment(ref Created); }
        public DiskSample Read() => Sequence.Count > 0 ? Sequence.Dequeue() : Constant;
        public void Dispose() { if (!Disposed) { Disposed = true; Interlocked.Decrement(ref Live); } }
    }
    private static DiskInfo D(string letter, long gb = 500, long freeGb = 100) => new(letter + ":\\", letter + ":", DiskKind.Fixed, true, gb * 1024 * 1024 * 1024, freeGb * 1024 * 1024 * 1024);

    // ===== item 4: the monitor, formats, layout rules =====
    private static void DiskMonitor()
    {
        Motion.SystemAnimations = () => true; Loc.SetLanguage("en");
        // fake counter sequences
        var sources = new Dictionary<string, FakeSource>(); int factoryCalls = 0;
        IDiskActivitySource? Factory(string label) { factoryCalls++; if (label == "E:") return null; var s = new FakeSource(new DiskSample(20, MB, 2 * MB)); sources[label] = s; return s; }
        using (var monitor = new DiskActivityMonitor(Factory))
        {
            monitor.SetDisks(new[] { D("C"), D("D"), D("E") }); monitor.Poll();
            Check(factoryCalls == 3 && monitor.SourceCount == 2 && FakeSource.Live == 2, "One counter source per disk that has counters; a disk without counters is skipped");
            Check(monitor.Get("C:\\")!.Sample.BusyPercent == 20 && !monitor.Get("E:\\")!.Sample.HasAny, "A disk without counters reads as unknown, not zero");
            for (int i = 0; i < 12; i++) monitor.Poll();
            Check(factoryCalls == 3, "A disk whose counters are missing is not tried again every second");
            for (int i = 0; i < 60; i++) monitor.Poll();
            Check(factoryCalls == 4, "It is tried again once a minute (counters can come back)");
            sources["C:"].Sequence.Enqueue(new DiskSample(10, 0, 0)); sources["C:"].Sequence.Enqueue(new DiskSample(90, 8 * MB, 40 * MB));
            monitor.Poll(); monitor.Poll();
            var last = monitor.Get("C:\\")!;
            Check(last.Sample.BusyPercent == 90 && last.Sample.WriteBytesPerSecond == 40 * MB, "The latest reading is the last fake counter value");
            Check(last.Plot.Count == DiskActivityMonitor.HistoryLength && Math.Abs(last.Plot[^1] - 0.9) < 1e-9 && Math.Abs(last.Plot[^2] - 0.1) < 1e-9, "Thirty seconds of history are kept (busy percent as height)");
            monitor.SetDisks(new[] { D("C") });
            Check(monitor.SourceCount == 1 && FakeSource.Live == 1 && monitor.Get("D:\\") is null, "A disk that goes away has its counter closed");
        }
        Check(FakeSource.Live == 0, "Disposing the monitor closes every counter");
        var plain = DiskActivityMonitor.Plot(new[] { new DiskSample(null, MB, MB), new DiskSample(null, 0, 0), new DiskSample(null, 4 * MB, 4 * MB) });
        Check(plain.Count == 3 && Math.Abs(plain[2] - 1) < 1e-9 && Math.Abs(plain[0] - 0.25) < 1e-9, "Without a busy counter the line follows throughput against the largest value");
        Check(DiskActivityMonitor.Plot(new[] { new DiskSample(null, 0, 0) })[0] == 0, "An idle disk stays flat");
        // formatting
        var view = new DiskBarActivity(); double t = 100;
        Check(view.Text(t) == "" && !view.HasData, "Before any reading nothing is shown");
        view.Update("C:\\", new DiskSample(23, 3 * MB, 12 * MB), t, false);
        var text = view.Text(t);
        Check(text.StartsWith("R ") && text.Contains("W ") && text.Contains("3 MB") && text.Contains("12 MB") && text.Contains("/s") && text.EndsWith("23% busy"), "Text is 'R 3 MB/s  W 12 MB/s · 23% busy': " + text);
        view.Update("C:\\", new DiskSample(23, null, null), t, false);
        Check(view.Text(t) == "23% busy", "Only what is known is shown");
        view.Update("C:\\", new DiskSample(null, null, null), t, false);
        Check(view.Text(t) == "" && !view.HasData, "A disk without counters shows no text and no placeholder");
        // numbers glide
        view.Update("C:\\", new DiskSample(10, 0, 0), t, false); view.Update("C:\\", new DiskSample(90, 8 * MB, 40 * MB), t, true);
        string early = view.Text(t + 0.15), done = view.Text(t + 1.0);
        Check(!view.Active && done.Contains("90% busy") && early != done && !early.Contains("90% busy") && !early.Contains("10% busy"), $"Values glide between readings ({early} -> {done})");
        view.Update("C:\\", new DiskSample(50, MB, MB), t + 2, true); view.Update("D:\\", new DiskSample(5, MB, MB), t + 2.01, true);
        Check(!view.Active, "Switching to another disk shows its numbers at once (no glide from the other disk)");
        // degrade order
        Check(DiskBarLayout.Choose(500, 300, 200) == DiskBarLevel.Full && DiskBarLayout.Choose(250, 300, 200) == DiskBarLevel.NoSparkline && DiskBarLayout.Choose(150, 300, 200) == DiskBarLevel.SizeOnly, "Degrade order: the sparkline goes first, then the throughput text");
        Check(DiskBarLayout.Choose(303, 300, 200, DiskBarLevel.NoSparkline) == DiskBarLevel.NoSparkline && DiskBarLayout.Choose(310, 300, 200, DiskBarLevel.NoSparkline) == DiskBarLevel.Full, "Growing back needs a little slack (no flicker at the edge)");
        Check(DiskBarActivity.TipText(new DiskSample(null, null, null)) == "" && DiskBarActivity.TipText(new DiskSample(30, MB, MB)).Contains("busy 30%"), "The tooltip line is empty when nothing is known");
    }

    private static void SetFakeDisks()
    {
        var c = D("C", 476, 120); var d = D("D", 1000, 900); var e = D("E", 64, 60);
        DiskUsage.Enumerate = () => new[] { c, d, e };
        DiskUsage.Resolve = path => path?.StartsWith("D:", StringComparison.OrdinalIgnoreCase) == true ? d : c;
    }

    // ===== item 4: the status bar =====
    private static void DiskBar(string scratch)
    {
        Motion.SystemAnimations = () => true; Loc.SetLanguage("en"); SetFakeDisks();
        PdhDiskActivity.Factory = label => label switch
        {
            "C:" => new FakeSource(new DiskSample(10, MB, 2 * MB)), "D:" => new FakeSource(new DiskSample(50, 5 * MB, 20 * MB)), _ => null,
        };
        using var m = NewManager(scratch, "dbar");
        var s = Settings("D:\\Downloads"); var main = NewMain(m, s); main.Show(); Pump(200);
        Wait(main.ProbeDisksNow()); Wait(main.PollActivityNow()); Pump(60);
        var text = Field<TextBlock>(main, "DiskActivityText"); var spark = Field<Sparkline>(main, "DiskSpark"); var diskText = Field<TextBlock>(main, "DiskText"); var button = Field<Button>(main, "DiskButton");
        Check(diskText.Text == "D: 900 GB free of 1000 GB" && text.Text.Contains("R 5 MB/s") && text.Text.Contains("W 20 MB/s") && text.Text.EndsWith("50% busy") && text.Visibility == Visibility.Visible, "Beside the size the bar shows throughput and busy percent of the shown disk: " + text.Text);
        Check(spark.Visibility == Visibility.Visible && spark.Values is { Count: > 0 }, "A sparkline of the last seconds is shown");
        string tip = (string)button.ToolTip;
        Check(tip.Contains("D: 900 GB free of 1000 GB") && tip.Contains("busy 50%") && tip.Contains("C: 120 GB free of 476 GB") && tip.Contains("busy 10%") && tip.Contains("E: 60 GB free of 64 GB"), "The tooltip lists every disk with its numbers");
        Check(!tip.Split('\n').SkipWhile(l => !l.Contains("E: 60 GB")).Skip(1).Take(1).Any(l => l.Contains("busy")), "A disk without counters has no activity line");
        // cycling follows the disk
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(100);
        Check(main.ShownDisk!.Label == "C:" && text.Text.Contains("R 1 MB/s") && text.Text.EndsWith("10% busy"), "Clicking shows the next disk and its activity: " + text.Text);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(100);
        Check(main.ShownDisk!.Label == "E:" && text.Text == "" && text.Visibility == Visibility.Collapsed && spark.Visibility == Visibility.Collapsed && diskText.Text.StartsWith("E:"), "A disk without counters shows only its size, no placeholder");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(100);
        Check(main.ShownDisk!.Label == "D:" && text.Text.EndsWith("50% busy"), "The cycle returns to the first disk");
        Check(System.Windows.Automation.AutomationProperties.GetHelpText(button).Contains("50% busy"), "The screen reader text of the button includes the activity");
        // polling cadence and stop
        Check(main.ActivityPollingActive && MainWindow.DiskActivityInterval == TimeSpan.FromSeconds(1), "Activity is read while the window is shown, once a second");
        int p0 = main.ActivityPollCount; Pump(3200); int polls = main.ActivityPollCount - p0;
        Check(polls is >= 2 and <= 4, $"At most once a second ({polls} reads in 3.2 s)");
        main.Hide(); Pump(150); int hidden0 = main.ActivityPollCount; Pump(2300);
        Check(!main.ActivityPollingActive && main.ActivityPollCount == hidden0, "Polling stops while the window is hidden");
        main.Show(); Pump(250);
        Check(main.ActivityPollingActive, "Polling resumes when the window is shown");
        main.WindowState = WindowState.Minimized; Pump(250); int min0 = main.ActivityPollCount; Pump(2300);
        Check(!main.ActivityPollingActive && main.ActivityPollCount == min0 && !main.ActivityGlideActive, "Polling and the glide timer stop while the window is minimised");
        main.WindowState = WindowState.Normal; Pump(250);
        // handles
        var proc = Process.GetCurrentProcess(); proc.Refresh(); int h0 = proc.HandleCount; int liveBefore = FakeSource.Live;
        for (int i = 0; i < 120; i++) { Wait(main.PollActivityNow()); if (i % 10 == 0) { button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Wait(main.ProbeDisksNow()); } }
        Pump(100); proc.Refresh();
        Console.WriteLine($"MEASURE: handles {h0} -> {proc.HandleCount}, counter sources {liveBefore} -> {FakeSource.Live} after 120 polls and disk cycling");
        Check(proc.HandleCount - h0 <= 25 && FakeSource.Live == liveBefore && main.ActivityMonitor.SourceCount <= 2, "Handles and counter sources stay flat over 120 polls with disk cycling");
        if (Shots is { Length: > 0 } dir) { Save(Capture(main), Path.Combine(dir, "statusbar-activity.png")); Console.WriteLine("SHOT: statusbar-activity"); }
        main.Close(); Pump(100);
        Check(FakeSource.Live == 0, "Closing the window closes the counters");
        // counters unavailable everywhere
        PdhDiskActivity.Factory = _ => null;
        var main2 = NewMain(m, s); main2.Show(); Pump(200); Wait(main2.ProbeDisksNow()); Wait(main2.PollActivityNow()); Pump(60); Wait(main2.PollActivityNow());
        Check(Field<TextBlock>(main2, "DiskActivityText").Visibility == Visibility.Collapsed && Field<Sparkline>(main2, "DiskSpark").Visibility == Visibility.Collapsed && Field<TextBlock>(main2, "DiskText").Text == "D: 900 GB free of 1000 GB" && main2.DiskLevel == DiskBarLevel.SizeOnly, "Without counters the bar shows only the size (no placeholder)");
        main2.Close(); Pump(60);
        // themes and right to left
        PdhDiskActivity.Factory = label => new FakeSource(new DiskSample(60, 5 * MB, 20 * MB));
        foreach (var theme in ThemeCatalog.Concrete) foreach (var rtl in new[] { false, true })
        {
            ThemeManager.Apply(Application.Current, theme.Id, null);
            var w = NewMain(m, s); w.Show(); Pump(150); w.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight; Pump(60); Wait(w.ProbeDisksNow()); for (int k = 0; k < 14; k++) Wait(w.PollActivityNow()); Pump(60);
            var sp = Field<Sparkline>(w, "DiskSpark"); var tx = Field<TextBlock>(w, "DiskActivityText");
            Check(sp.FlowDirection == FlowDirection.LeftToRight && sp.ActualWidth > 20 && tx.ActualWidth > 100, $"{theme.Id} {(rtl ? "RTL" : "LTR")}: the sparkline and the numbers are laid out");
            var bmp = Capture(sp); var px = Pixels(bmp); var accent = ((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color; int hits = 0;
            for (int i = 0; i < px.Length; i += 4) { int al = px[i + 3]; if (al > 90 && Math.Abs(px[i] * 255 / al - accent.B) + Math.Abs(px[i + 1] * 255 / al - accent.G) + Math.Abs(px[i + 2] * 255 / al - accent.R) < 60) hits++; }
            Check(hits >= 8, $"{theme.Id} {(rtl ? "RTL" : "LTR")}: the line is drawn in the accent colour ({hits} pixels)");
            var muted = ((SolidColorBrush)Application.Current.FindResource("MutedBrush")).Color; var surface = ((SolidColorBrush)Application.Current.FindResource("CanvasBrush")).Color;
            if (ThemeManager.Contrast(muted, surface) < 4.5) throw new Exception($"FAIL: {theme.Id} status text contrast {ThemeManager.Contrast(muted, surface):0.0}");
            if (Shots is { Length: > 0 } dir2 && (theme.Id is "DeepNavy" or "Light") && (!rtl || theme.Id == "DeepNavy")) { Save(Capture(w), Path.Combine(dir2, $"statusbar-{theme.Id}{(rtl ? "-rtl" : "")}.png")); Console.WriteLine($"SHOT: statusbar-{theme.Id}{(rtl ? "-rtl" : "")}"); }
            w.Close(); Pump(50);
        }
        ThemeManager.Apply(Application.Current, "Light", null);
    }

    // ===== item 4: narrow windows =====
    private static void DiskNarrow(string scratch)
    {
        Motion.SystemAnimations = () => true; Loc.SetLanguage("en"); SetFakeDisks();
        PdhDiskActivity.Factory = label => new FakeSource(new DiskSample(50, 5 * MB, 20 * MB));
        using var m = NewManager(scratch, "narrow");
        var s = Settings("D:\\Downloads"); var main = NewMain(m, s, null, 1400, 600); main.MinWidth = 200; main.Show(); Pump(200); Wait(main.ProbeDisksNow()); Wait(main.PollActivityNow()); Pump(100);
        var seen = new List<DiskBarLevel>();
        var host = Field<Border>(main, "StatusHost"); var spark = Field<Sparkline>(main, "DiskSpark"); var text = Field<TextBlock>(main, "DiskActivityText"); var button = Field<Button>(main, "DiskButton");
        var summary = Field<TextBlock>(main, "Summary"); var rate = Field<TextBlock>(main, "RateLabel"); var notice = Field<TextBlock>(main, "Notice");
        bool noOverlap = true; int steps = 0;
        for (double width = 1400; width >= 560; width -= 20)
        {
            main.Width = width; Pump(40); steps++;
            seen.Add(main.DiskLevel);
            double left(FrameworkElement e) => e.TranslatePoint(new Point(0, 0), host).X; double right(FrameworkElement e) => left(e) + e.ActualWidth;
            bool rtl = false;
            noOverlap &= right(button) <= left(summary) + 0.5 && right(summary) <= left(rate) + 0.5 && right(rate) <= host.ActualWidth + 0.5 && (width < 700 || right(notice) <= left(button) + 0.5);
            if (!noOverlap) throw new Exception($"FAIL: overlap at width {width}: notice {right(notice):0} button {left(button):0}-{right(button):0} summary {left(summary):0}-{right(summary):0} rate {left(rate):0}-{right(rate):0} host {host.ActualWidth:0}");
        }
        Check(noOverlap && steps > 40, $"No item overlaps another at any width ({steps} widths checked)");
        int firstNoSpark = seen.IndexOf(DiskBarLevel.NoSparkline), firstSizeOnly = seen.IndexOf(DiskBarLevel.SizeOnly);
        Check(seen[0] == DiskBarLevel.Full && firstNoSpark > 0, "A wide window shows everything; narrowing drops the sparkline first");
        Check(firstSizeOnly == -1 || firstSizeOnly > firstNoSpark, "The throughput text goes only after the sparkline");
        for (int i = 1; i < seen.Count; i++) if (seen[i] < seen[i - 1]) throw new Exception("FAIL: the level came back while the window kept narrowing");
        Check(true, "The level only degrades while the window narrows (" + string.Join(">", seen.Distinct()) + ")");
        Console.WriteLine("MEASURE: levels from 1400 down to 560 px: " + string.Join(" ", seen.Select((l, i) => (1400 - i * 20) + "=" + l).Where((_, i) => i % 6 == 0)));
        main.Width = 1400; Pump(80);
        Check(main.DiskLevel == DiskBarLevel.Full && spark.Visibility == Visibility.Visible, "Widening the window brings everything back");
        main.Width = 380; Pump(100); Check(main.DiskLevel == DiskBarLevel.SizeOnly && text.Visibility == Visibility.Collapsed && spark.Visibility == Visibility.Collapsed && Field<TextBlock>(main, "DiskText").ActualWidth > 40, "At a very small width only the size and its bar stay");
        if (Shots is { Length: > 0 } dir) { main.Width = 760; Pump(120); Save(Capture(main), Path.Combine(dir, "statusbar-narrow.png")); Console.WriteLine("SHOT: statusbar-narrow"); }
        main.Close(); Pump(60);
    }
}
