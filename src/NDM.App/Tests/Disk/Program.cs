global using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;
using NDM.Tests;

internal sealed class DiskTestApp : Application
{
    public DiskTestApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}

/// <summary>1.9.0 items 2, 5 and 6: status kinds and retry click, the status-bar disk usage (cycle, keyboard, polling, handles, themes, RTL) and the disk rows of the progress window.</summary>
internal static class DiskTests
{
    private static int passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static void Pump(int ms = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Wait(Task task) { var end = DateTime.UtcNow.AddSeconds(10); while (!task.IsCompleted && DateTime.UtcNow < end) Pump(20); Pump(10); if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); }
    private static T Field<T>(Window w, string name) where T : class => (T)(w.FindName(name) ?? throw new Exception("missing control " + name));
    private static IEnumerable<T> Visual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Visual<T>(child)) yield return nested;
        }
    }
    /// <summary>Renders the window itself (not through a VisualBrush, which mirrors glyphs in right-to-left windows).</summary>
    private static RenderTargetBitmap Capture(Window window)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); return bitmap;
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private const long GB = 1024L * 1024 * 1024;
    private static DiskInfo Disk(string letter, long totalGb, long freeGb, DiskKind kind = DiskKind.Fixed, bool ready = true)
        => new(letter + ":\\", letter + ":", kind, ready, ready ? totalGb * GB : 0, ready ? freeGb * GB : 0);

    [STAThread]
    private static int Main()
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-DiskTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var app = new DiskTestApp();
        var realEnumerate = DiskUsage.Enumerate; var realResolve = DiskUsage.Resolve; var realBusy = PdhDiskBusy.Factory;
        try
        {
            StatusKinds();
            DiskLogic();
            RealDisks();
            StatusBar(scratch);
            Polling(scratch);
            ProgressView();
            ProgressWindow(scratch);
            Console.WriteLine($"Disk tests passed: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            DiskUsage.Enumerate = realEnumerate; DiskUsage.Resolve = realResolve; PdhDiskBusy.Factory = realBusy;
            foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close();
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            app.Shutdown();
        }
    }

    // ===== item 2: status kind per state and the retry action =====
    private static DownloadJob JobWith(string status, string? error = null, string name = "a.zip")
    {
        var job = new DownloadJob();
        void Set(string property, object? value) => typeof(DownloadJob).GetProperty(property)!.GetSetMethod(true)!.Invoke(job, new[] { value });
        Set("Id", Guid.NewGuid().ToString("N")); Set("Url", "https://example.org/" + name); Set("FileName", name); Set("Status", status); Set("Error", error); Set("FilePath", Path.Combine(Path.GetTempPath(), name));
        return job;
    }
    private static void StatusKinds()
    {
        var expected = new (string Status, string Kind)[] { ("Completed", "Completed"), ("Downloading", "Downloading"), ("Connecting", "Downloading"), ("Checking", "Downloading"), ("Paused", "Paused"), ("Canceled", "Paused"), ("Stopped", "Paused"), ("Failed", "Failed"), ("Queued", "Queued"), ("Scheduled", "Queued"), ("Skipped", "Queued") };
        foreach (var (status, kind) in expected) Check(new DownloadRow(JobWith(status)).StatusKind == kind, $"Status {status} uses the {kind} colour");
        var failed = new DownloadRow(JobWith("Failed", "The server closed the connection."));
        Check(failed.CanRetryFromStatus && failed.StatusDisplay == Loc.T("row.failed_retry") && failed.StatusTip == Loc.T("row.failed_retry_tip"), "A failed job that can be resumed shows 'Failed, click to retry' and a tooltip");
        var paused = new DownloadRow(JobWith("Paused"));
        Check(!paused.CanRetryFromStatus && paused.StatusDisplay == Loc.Status("Paused") && paused.StatusTip == "", "Other states show their plain status and no retry hint");
        Check(new[] { "ar", "es", "fr", "de", "pt-BR", "ru", "tr", "zh-Hans", "ja" }.All(tag => LanguageText(tag, "row.failed_retry") != LanguageText("en", "row.failed_retry") && LanguageText(tag, "row.failed_retry").Length > 3), "'Failed, click to retry' is translated in the nine other languages");
    }
    private static string LanguageText(string tag, string key)
    {
        string? before = Loc.Language;
        Loc.SetLanguage(tag); try { return Loc.T(key); } finally { Loc.SetLanguage(before ?? "en"); }
    }

    // ===== item 5: pure disk logic =====
    private static void DiskLogic()
    {
        Check(DiskUsage.LabelOf("C:\\") == "C:" && DiskUsage.LabelOf("\\\\nas\\media\\") == "\\\\nas\\media", "Label of C:\\ is C: and a share keeps its name");
        Check(DiskUsage.RootOf("D:\\Downloads\\Video\\a.mkv") == "D:\\" && DiskUsage.RootOf("") is null && DiskUsage.RootOf(null) is null && DiskUsage.RootOf("bad\0path") is null, "Root of a path is its drive; empty and invalid paths have none");
        var c = Disk("C", 476, 120); var d = Disk("D", 1000, 900); var e = Disk("E", 64, 60, DiskKind.Removable); var gone = Disk("G", 0, 0, ready: false);
        Check(Math.Abs(c.UsedFraction - (476 - 120) / 476.0) < 1e-9 && c.UsedPercent == 75 && gone.UsedFraction == 0 && gone.UsedBytes == 0, "Used fraction and percent come from total and free; an unreadable disk shows zero");
        var cycler = new DiskCycler(); cycler.Update(new[] { e, c, d }, d);
        Check(cycler.Current!.Label == "D:" && cycler.IsDefault && cycler.Disks.Select(x => x.Label).SequenceEqual(new[] { "D:", "C:", "E:" }), "Default disk is shown first; the cycle is default, then the others by letter");
        var seen = new List<string>(); for (int i = 0; i < 4; i++) { cycler.Next(); seen.Add(cycler.Current!.Label); }
        Check(seen.SequenceEqual(new[] { "C:", "E:", "D:", "C:" }), "Each click shows the next disk and wraps back to the default");
        cycler.Reset(); Check(cycler.Current!.Label == "D:" && cycler.IsDefault, "Escape (Reset) returns to the default disk");
        cycler.Next(); cycler.Next(); cycler.Update(new[] { c, d }, d); Check(cycler.Current!.Label == "D:", "A chosen removable disk that disappears falls back to the default");
        var lonely = new DiskCycler(); lonely.Update(Array.Empty<DiskInfo>(), null); lonely.Next(); Check(lonely.Current is null && lonely.Disks.Count == 0, "No disks at all: nothing to cycle, no crash");
        var offline = new DiskCycler(); offline.Update(new[] { c }, gone); Check(offline.Current!.Label == "G:" && !offline.Current.Ready && offline.Disks.Count == 2, "An unavailable default disk is still shown first (as not available)");
        string before = Loc.Language; Loc.SetLanguage("en");
        Check(DiskUsage.StatusText(c) == "C: 476 GB free of 476 GB".Replace("476 GB free", "120 GB free") && DiskUsage.StatusText(gone) == "G: not available" && DiskUsage.StatusText(null) == "No disk information", "Status text: 'C: 120 GB free of 476 GB', 'G: not available', 'No disk information'");
        Check(DiskUsage.TipLine(c) == "C: 120 GB free of 476 GB (75% used)" && DiskUsage.TipLine(gone) == "G: not available", "Tooltip line includes the used percent");
        Check(DiskUsage.LowSpaceWarning(c, 130 * GB) is { Length: > 0 } w && w.Contains("130 GB") && w.Contains("120 GB") && w.Contains("C:") && DiskUsage.LowSpaceWarning(c, 50 * GB) == "" && DiskUsage.LowSpaceWarning(c, 0) == "" && DiskUsage.LowSpaceWarning(gone, 10 * GB) == "" && DiskUsage.LowSpaceWarning(null, 10 * GB) == "", "Low-space warning names the numbers; no warning when it fits or when something is unknown");
        Loc.SetLanguage(before);
    }

    private static void RealDisks()
    {
        var all = DiskUsage.Enumerate();
        Check(all.Count >= 1 && all.All(x => x.Ready && x.TotalBytes > 0 && x.FreeBytes >= 0 && x.FreeBytes <= x.TotalBytes && x.Kind is DiskKind.Fixed or DiskKind.Removable), $"The real disk reader lists {all.Count} ready fixed or removable disk(s) with sane numbers");
        var here = DiskUsage.Resolve(Path.GetTempPath());
        Check(here is { Ready: true } && here.TotalBytes > 0 && all.Any(x => x.SameDisk(here)), "Resolve finds the disk of a real folder");
        var nowhere = DiskUsage.Resolve("Q:\\this\\drive\\does\\not\\exist.bin");
        Check(nowhere is null || !nowhere.Ready || all.Any(x => x.SameDisk(nowhere)), "A path on a missing drive resolves to 'not ready' (or a real disk), never throws");
        Check(DiskUsage.Resolve("relative-name-only.bin") is { } rel && rel.Ready, "A relative path resolves against the current folder's disk");
    }

    // ===== item 5: the status bar =====
    private static DownloadManager NewManager(string scratch, string name)
    {
        string state = Path.Combine(scratch, name, "NDM"); Directory.CreateDirectory(state); string files = Path.Combine(scratch, name, "files"); Directory.CreateDirectory(files);
        return new DownloadManager(files, state, new DownloadOptions { MaxConcurrentDownloads = 2, MaxSegments = 2, RetryCount = 0 });
    }
    private static MainWindow NewMain(DownloadManager m, string downloadDir)
        => new(m, new AppSettings { DownloadDirectory = downloadDir, ShowCompletionDialog = false, ShowProgressOnCompletion = false }, () => { }, enableTray: false) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Width = 1100, Height = 640 };
    private static void Press(UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
    }
    private static void Invoke(Button b) => ((IInvokeProvider)new ButtonAutomationPeer(b).GetPattern(PatternInterface.Invoke)!).Invoke();

    private static void StatusBar(string scratch)
    {
        var c = Disk("C", 476, 120); var d = Disk("D", 1000, 900); var e = Disk("E", 64, 60, DiskKind.Removable);
        DiskUsage.Enumerate = () => new[] { c, d, e };
        DiskUsage.Resolve = path => path?.StartsWith("D:", StringComparison.OrdinalIgnoreCase) == true ? d : path?.StartsWith("X:", StringComparison.OrdinalIgnoreCase) == true ? Disk("X", 0, 0, DiskKind.Network, false) : c;
        Loc.SetLanguage("en");
        using var m = NewManager(scratch, "bar");
        var main = NewMain(m, "D:\\Downloads"); main.Show(); Pump(100);
        var button = Field<Button>(main, "DiskButton"); var text = Field<TextBlock>(main, "DiskText"); var bar = Field<ProgressBar>(main, "DiskBar");
        Wait(main.ProbeDisksNow()); Pump(40);
        Check(text.Text == "D: 900 GB free of 1000 GB" && Math.Abs(bar.Value - 10) < 0.01 && main.ShownDisk!.Label == "D:", "Status bar starts on the disk of the default download folder: 'D: 900 GB free of 1000 GB' with a 10% bar [" + text.Text + "|" + bar.Value + "|" + main.ShownDisk?.Label + "]");
        Check(button.IsEnabled && button.IsTabStop && button.Focusable && (string?)System.Windows.Automation.AutomationProperties.GetName(button) == Loc.T("disk.a11y"), "The disk button is keyboard reachable and has an accessible name");
        string tip = (string)button.ToolTip;
        Check(tip.Contains("D: 900 GB free of 1000 GB (10% used)") && tip.Contains("C: 120 GB free of 476 GB (75% used)") && tip.Contains("E: 60 GB free of 64 GB (6% used)") && tip.Contains(Loc.T("disk.tip_hint")) && tip.Split(Environment.NewLine).Count(l => l.StartsWith("▸")) == 1, "Tooltip lists every disk, marks the shown one and explains the click and Escape");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
        Check(text.Text == "C: 120 GB free of 476 GB" && Math.Abs(bar.Value - 75.2) < 0.5, "Click shows the next disk (C:)");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
        Check(text.Text == "E: 60 GB free of 64 GB", "Another click shows the removable disk (E:)");
        Press(button, Key.Escape); Pump();
        Check(text.Text == "D: 900 GB free of 1000 GB", "Escape returns to the disk of the download folder");
        button.Focus(); Invoke(button); Pump();
        Check(text.Text == "C: 120 GB free of 476 GB", "Keyboard activation (Enter/Space invoke the button) cycles as well");
        DiskUsage.Enumerate = () => new[] { c, d }; Wait(main.ProbeDisksNow());
        Check(main.ShownDisk!.Label == "C:" && text.Text.StartsWith("C:"), "A refresh keeps the chosen disk while it is still there");
        DiskUsage.Enumerate = () => new[] { d }; Wait(main.ProbeDisksNow());
        Check(main.ShownDisk!.Label == "D:", "When the chosen disk disappears the bar falls back to the default disk");
        // unavailable default (network share offline)
        main.Close(); Pump();
        var offline = NewMain(m, "X:\\Share"); offline.Show(); Pump(100); Wait(offline.ProbeDisksNow());
        var offText = Field<TextBlock>(offline, "DiskText"); var offBar = Field<ProgressBar>(offline, "DiskBar");
        Check(offText.Text == "X: not available" && offBar.Visibility == Visibility.Collapsed, "An unavailable download disk shows 'X: not available' and hides the bar");
        // every ready disk too full: bar colour follows the fill level
        offline.Close(); Pump();
        var levels = new (DiskInfo Disk, string Brush)[] { (Disk("D", 100, 50), "AccentBrush"), (Disk("D", 100, 8), "WarningBrush"), (Disk("D", 100, 2), "ErrorBrush") };
        foreach (var (disk, brush) in levels)
        {
            DiskUsage.Resolve = _ => disk; DiskUsage.Enumerate = () => new[] { disk };
            var w = NewMain(m, "D:\\Downloads"); w.Show(); Pump(60); Wait(w.ProbeDisksNow());
            var fill = ((SolidColorBrush)Field<ProgressBar>(w, "DiskBar").Foreground).Color;
            Check(fill == ((SolidColorBrush)Application.Current.FindResource(brush)).Color, $"{disk.UsedPercent}% used: the bar uses {brush}");
            w.Close(); Pump();
        }
        DiskUsage.Resolve = path => path?.StartsWith("D:", StringComparison.OrdinalIgnoreCase) == true ? d : c; DiskUsage.Enumerate = () => new[] { c, d, e };
        // themes and RTL
        string? shots = Environment.GetEnvironmentVariable("NEODM_SHOTS");
        foreach (var theme in ThemeCatalog.Concrete)
        {
            ThemeManager.Apply(Application.Current, theme.Id, null);
            var w = NewMain(m, "D:\\Downloads"); w.Show(); Pump(80); Wait(w.ProbeDisksNow());
            var t = Field<TextBlock>(w, "DiskText"); var b = Field<Button>(w, "DiskButton");
            var fg = ((SolidColorBrush)t.Foreground).Color; var bg = ((SolidColorBrush)Application.Current.FindResource("CanvasBrush")).Color;
            Check(ThemeManager.Contrast(fg, bg) >= 4.5 && b.ActualWidth > 100 && b.ActualHeight > 12, $"{theme.Id}: disk text reads at {ThemeManager.Contrast(fg, bg):0.0}:1 and the button has size {b.ActualWidth:0}x{b.ActualHeight:0}");
            if (shots is { Length: > 0 } && theme.Id is "DeepNavy" or "Light" or "Midnight") { Save(Capture(w), Path.Combine(shots, $"statusbar-{theme.Id}.png")); Console.WriteLine("SHOT: statusbar-" + theme.Id); }
            w.Close(); Pump();
        }
        ThemeManager.Apply(Application.Current, "Light", null);
        Loc.SetLanguage("ar");
        var rtl = NewMain(m, "D:\\Downloads"); rtl.Show(); Pump(100); Wait(rtl.ProbeDisksNow());
        var rb = Field<Button>(rtl, "DiskButton"); var rt = Field<TextBlock>(rtl, "DiskText");
        var origin = rb.TransformToAncestor(rtl).Transform(new Point(0, 0));
        Check(rtl.FlowDirection == FlowDirection.RightToLeft && rt.Text.Length > 5 && origin.X >= 0 && origin.X + rb.ActualWidth <= rtl.ActualWidth + 1, "Arabic: the window is mirrored and the disk button stays inside it");
        Check(rt.Text.Contains("D:") && rt.Text != "D: 900 GB free of 1000 GB", "Arabic: the disk text is translated and keeps the drive letter");
        var navIcons = Visual<System.Windows.Shapes.Path>(rtl).Where(p => p.Name == "Icon").ToArray();
        Check(navIcons.Length >= 7 && navIcons.All(p => p.FlowDirection == FlowDirection.LeftToRight), $"Arabic: all {navIcons.Length} sidebar icons keep their own orientation (a check mark is not mirrored)");
        var resumeAll = Field<Button>(rtl, "ResumeAllButton");
        Check(Visual<TextBlock>(resumeAll).Any(t => t.TextWrapping == TextWrapping.Wrap && t.TextTrimming == TextTrimming.None) && resumeAll.ToolTip is string { Length: > 3 }, "Arabic: a long sidebar button label wraps (never cut off) and carries the full text as its tooltip");
        if (shots is { Length: > 0 }) { Save(Capture(rtl), Path.Combine(shots, "statusbar-rtl.png")); Console.WriteLine("SHOT: statusbar-rtl"); }
        rtl.Close(); Pump(); Loc.SetLanguage("en");
        // small window still shows the button
        var small = NewMain(m, "D:\\Downloads"); small.Show(); small.Width = 900; small.Height = 480; Pump(100); Wait(small.ProbeDisksNow());
        var sb = Field<Button>(small, "DiskButton");
        var sbPos = sb.TransformToAncestor(small).Transform(new Point(0, 0));
        Check(sb.ActualWidth > 100 && sbPos.X + sb.ActualWidth <= small.ActualWidth + 1, "At the smallest window size the disk button is fully visible");
        small.Close(); Pump();
    }

    // ===== item 5: polling is cheap, stops when hidden, and leaks nothing =====
    private static void Polling(string scratch)
    {
        int reads = 0;
        DiskUsage.Enumerate = () => { Interlocked.Increment(ref reads); return new[] { Disk("C", 100, 50) }; };
        DiskUsage.Resolve = _ => Disk("C", 100, 50);
        var before = MainWindow.DiskPollInterval; MainWindow.DiskPollInterval = TimeSpan.FromMilliseconds(80);
        try
        {
            using var m = NewManager(scratch, "poll");
            var main = NewMain(m, "C:\\Downloads");
            Check(!main.DiskPollingActive && main.DiskProbeCount == 0, "A window that was never shown does not poll");
            main.Show(); Pump(500);
            Check(main.DiskPollingActive && main.DiskProbeCount >= 3, $"A shown window polls on its interval ({main.DiskProbeCount} reads in 0.5 s at an 80 ms test interval)");
            main.WindowState = WindowState.Minimized; Pump(60);
            int atMinimize = main.DiskProbeCount; Pump(400);
            Check(!main.DiskPollingActive && main.DiskProbeCount <= atMinimize + 1, $"Minimised: polling stops ({atMinimize} -> {main.DiskProbeCount} reads over 0.4 s)");
            main.WindowState = WindowState.Normal; Pump(300);
            Check(main.DiskPollingActive && main.DiskProbeCount > atMinimize + 1, "Restored: polling resumes");
            main.Hide(); Pump(60); int atHide = main.DiskProbeCount; Pump(300);
            Check(!main.DiskPollingActive && main.DiskProbeCount <= atHide + 1, "Hidden (tray): polling stops");
            main.Show(); Pump(200);
            // no overlapping reads: a slow read never runs twice at once
            int concurrent = 0, maxConcurrent = 0;
            DiskUsage.Enumerate = () => { int now = Interlocked.Increment(ref concurrent); maxConcurrent = Math.Max(maxConcurrent, now); Thread.Sleep(150); Interlocked.Decrement(ref concurrent); return new[] { Disk("C", 100, 50) }; };
            Pump(700);
            Check(maxConcurrent == 1, "A slow disk read is never started twice at once");
            DiskUsage.Enumerate = () => new[] { Disk("C", 100, 50) };
            // handle and memory flatness over many reads
            MainWindow.DiskPollInterval = TimeSpan.FromMinutes(5);
            for (int i = 0; i < 20; i++) Wait(main.ProbeDisksNow());
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); int handles0 = Process.GetCurrentProcess().HandleCount; long mem0 = GC.GetTotalMemory(true);
            for (int i = 0; i < 400; i++) Wait(main.ProbeDisksNow());
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); int handles1 = Process.GetCurrentProcess().HandleCount; long mem1 = GC.GetTotalMemory(true);
            Check(handles1 - handles0 <= 15 && mem1 - mem0 < 4_000_000, $"400 disk reads: handles {handles0} -> {handles1}, managed memory {(mem1 - mem0) / 1024} KB growth");
            main.Close(); Pump(100);
            int afterClose = main.DiskProbeCount; Pump(300);
            Check(!main.DiskPollingActive && main.DiskProbeCount == afterClose, "A closed window stops polling for good");
        }
        finally { MainWindow.DiskPollInterval = before; }
        DiskUsage.Enumerate = () => new[] { Disk("C", 476, 120) };
    }

    // ===== item 6: the live view model =====
    private static void ProgressView()
    {
        Loc.SetLanguage("en");
        var view = new DiskActivityView(); var c = Disk("C", 476, 120);
        Check(view.DiskText(0) == "(unknown)" && view.ActivityText(0) == "No writes right now", "No disk data yet: the disk and activity rows stay unknown/idle, nothing is invented");
        view.Update(c, null, null, 0, false);
        Check(view.DiskText(0) == "C: · 120 GB free of 476 GB" && view.ActivityText(0) == "No writes right now" && !view.HasBusy && !view.Active, "Disk known, no rate and no busy counter: only the free space is shown");
        view.Update(c, 5.2 * 1024 * 1024, null, 1, false);
        Check(view.ActivityText(1) == "Writing 5.2 MB/s", "A known write rate is shown from the job's real transfer speed");
        view.Update(c, 5.2 * 1024 * 1024, 23, 2, false);
        Check(view.ActivityText(2) == "Writing 5.2 MB/s · Disk busy 23%" && view.HasBusy, "The busy percent joins the rate when the counter exists");
        view.Update(c, 10 * 1024 * 1024, 23, 3, true);
        double mid = 3.2; string midText = view.ActivityText(mid);
        Check(view.Active && midText != "Writing 5.2 MB/s · Disk busy 23%" && midText != "Writing 10 MB/s · Disk busy 23%" && view.ActivityText(3 + NumericTransition.Duration + 0.01) == "Writing 10 MB/s · Disk busy 23%" && !view.Active, $"A changed rate glides ('{midText}') and lands exactly on the new value");
        view.Update(Disk("C", 476, 100), 10 * 1024 * 1024, 23, 5, true);
        Check(view.Active && view.DiskText(5.2) != view.DiskText(6) && view.DiskText(6) == "C: · 100 GB free of 476 GB", "Free space glides to its new value");
        view.Update(c, null, null, 7, true);
        Check(view.ActivityText(7) == "No writes right now", "When the rate becomes unknown it is hidden, not shown as zero");
        view.Update(Disk("C", 0, 0, ready: false), null, null, 8, false);
        Check(view.DiskText(8) == "C: cannot be read right now", "An unreadable disk says so");
    }

    // ===== item 6: the real progress window =====
    private sealed class FakeBusy : IDiskBusySource { public int Reads; public bool Disposed; public double? Read() { Reads++; return 37; } public void Dispose() => Disposed = true; }
    private static void ProgressWindow(string scratch)
    {
        Loc.SetLanguage("en");
        using var server = new FixtureServer();
        using var m = NewManager(scratch, "progress");
        var settings = new AppSettings { DownloadDirectory = Path.Combine(scratch, "progress", "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = false };
        var c = Disk("C", 476, 120); DiskUsage.Resolve = _ => c;
        FakeBusy? busy = null; PdhDiskBusy.Factory = label => busy = new FakeBusy();
        var live = m.AddScheduledAsync(new DownloadRequest(server.BaseUrl + "/range?disk-live.bin", "disk-live.bin"), DateTimeOffset.UtcNow.AddHours(1)).GetAwaiter().GetResult();
        m.SetSpeedLimit(live.Id, 200 * 1024); m.Schedule(live.Id, DateTimeOffset.UtcNow.AddSeconds(-1));
        var end = DateTime.UtcNow.AddSeconds(20);
        while (!m.Jobs.Any(j => j.Id == live.Id && j.Status == "Downloading" && j.DownloadedBytes > 0 && j.SpeedBytesPerSecond > 0) && DateTime.UtcNow < end) Pump(100);
        var before = DownloadProgressWindow.DiskProbeInterval; DownloadProgressWindow.DiskProbeInterval = TimeSpan.FromMilliseconds(100);
        try
        {
            var w = new DownloadProgressWindow(m, live.Id, new JobCompletionStore(), settings) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
            w.Show(); Wait(w.ProbeDiskNow()); Pump(700);
            var disk = Field<TextBlock>(w, "DiskText"); var activity = Field<TextBlock>(w, "DiskActivityText"); var warning = Field<TextBlock>(w, "DiskWarning");
            Check(disk.Text == "C: · 120 GB free of 476 GB", "Progress window shows the drive, free space and total of the disk the file is saved to");
            Check(activity.Text.StartsWith("Writing ") && activity.Text.Contains("/s") && activity.Text.EndsWith("Disk busy 37%"), $"Live activity shows this job's write rate and the disk busy percent ('{activity.Text}')");
            Check(warning.Visibility == Visibility.Collapsed, "No low-space warning while the file fits");
            c = Disk("C", 476, 0); DiskUsage.Resolve = _ => c;
            long remaining = Math.Max(0, w.Row.TotalBytes - w.Row.DownloadedBytes);
            Wait(w.ProbeDiskNow()); Pump(120);
            Check(w.Row.TotalBytes <= 0 || remaining == 0 || warning.Visibility == Visibility.Visible, "When free space is below the remaining bytes the warning appears in plain words");
            Check(Field<TextBlock>(w, "DiskText").Text.Contains("C:") && w.Row.Status == "Downloading", "The window keeps updating while the job runs");
            var small = (w.Width, w.Height); w.Width = 420; w.Height = 560; Pump(150); w.UpdateLayout();
            Check(disk.ActualWidth > 40 && activity.ActualWidth > 40, "At a narrow width the disk rows are still visible (they trim instead of overflowing)");
            if (Environment.GetEnvironmentVariable("NEODM_SHOTS") is { Length: > 0 } shots) { Save(Capture(w), Path.Combine(shots, "progress-disk.png")); Console.WriteLine("SHOT: progress-disk"); }
            int reads = busy!.Reads; w.Close(); Pump(300);
            Check(busy.Disposed && busy.Reads == reads, "Closing the window releases the performance query and stops reading it");
            // no busy counter at all: the activity row shows only what is known
            PdhDiskBusy.Factory = _ => null; c = Disk("C", 476, 120); DiskUsage.Resolve = _ => c;
            var w2 = new DownloadProgressWindow(m, live.Id, new JobCompletionStore(), settings) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 }; w2.Show(); Wait(w2.ProbeDiskNow()); Pump(300);
            Check(!Field<TextBlock>(w2, "DiskActivityText").Text.Contains("busy") && Field<TextBlock>(w2, "DiskActivityText").Text.StartsWith("Writing "), "Without a performance counter the busy part is hidden gracefully");
            DiskUsage.Resolve = _ => null; Wait(w2.ProbeDiskNow()); Pump(100);
            Check(Field<TextBlock>(w2, "DiskText").Text == "(unknown)", "A path with no readable disk shows (unknown)");
            // the real counter source: either a number in range or null, never an exception, and it disposes
            var real = PdhDiskBusy.Create("C:");
            double? first = real?.Read(), second = null; Thread.Sleep(300); second = real?.Read();
            Check(real is null || (first is null && second is null or >= 0 and <= 100) || second is >= 0 and <= 100, $"The real Windows performance counter returns a percent or nothing ({(real is null ? "unavailable here" : second?.ToString("0.0") ?? "null")})");
            real?.Dispose(); real?.Dispose();
            Check(PdhDiskBusy.Create("\\\\nas\\share") is null && PdhDiskBusy.Create("") is null, "Network shares and empty names have no busy counter");
            w2.Close();
        }
        finally { DownloadProgressWindow.DiskProbeInterval = before; }
        m.Pause(live.Id);
    }
}
