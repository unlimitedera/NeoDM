// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
global using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>2.0.0 stage S1 app tests: the mouse wheel on the Options pages, the Processing status in the rows, the remove dialog's partial size, the add guards,
/// the Options temporary files folder and clean-up, and the 64 connection limit. No real mouse movement: the build's wheel turns are the routed events WPF raises for a wheel message;
/// the hand-made real-input check is <c>--reproduce --real-wheel</c> (moves the pointer, run once by a person).</summary>
internal static partial class V2000Tests
{
    private static int passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T hit) return hit;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Find<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    /// <summary>The visual that is really under the middle (or the top-left part of a tall control) of the element, as the pointer would find it.</summary>
    private static UIElement Under(Window window, FrameworkElement at)
    {
        var p = at.TranslatePoint(new Point(Math.Min(at.ActualWidth / 2, 60), Math.Min(at.ActualHeight / 2, 12)), window);
        var hit = VisualTreeHelper.HitTest(window, p)?.VisualHit;
        while (hit is not null and not UIElement) hit = VisualTreeHelper.GetParent(hit);
        return (UIElement?)hit ?? at;
    }

    private static MouseWheelEventArgs Turn(UIElement target, int delta, RoutedEvent routed) => new(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = routed, Source = target };

    /// <summary>One wheel turn over what is under the element: the tunnelling Preview event, then the bubbling one when nobody took it. Returns how far the page moved.</summary>
    private static double Wheel(Window window, ScrollViewer page, FrameworkElement at, int notches = -3)
    {
        page.ScrollToVerticalOffset(0); Pump(20); at.BringIntoView(); Pump(40); var before = page.VerticalOffset;
        if (before >= page.ScrollableHeight - 1) notches = Math.Abs(notches); // at the bottom: turn the wheel up instead
        var target = Under(window, at);
        var down = Turn(target, 120 * notches, UIElement.PreviewMouseWheelEvent);
        target.RaiseEvent(down);
        if (!down.Handled) target.RaiseEvent(Turn(target, 120 * notches, UIElement.MouseWheelEvent));
        Pump(40);
        return Math.Abs(page.VerticalOffset - before);
    }

    /// <summary>Real input: puts the pointer over the element and turns the wheel with mouse_event, then puts the pointer back. For a person at the desk; never part of the build.</summary>
    private static double RealWheel(Window window, ScrollViewer page, FrameworkElement at, int notches = -3)
    {
        page.ScrollToVerticalOffset(0); Pump(60); at.BringIntoView(); Pump(120); var before = page.VerticalOffset;
        var screen = at.PointToScreen(new Point(Math.Min(at.ActualWidth / 2, 60), Math.Min(at.ActualHeight / 2, 12)));
        GetCursorPos(out var old);
        SetCursorPos((int)screen.X, (int)screen.Y); Pump(120);
        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, 120 * notches, UIntPtr.Zero); Pump(250);
        var offset = Math.Abs(page.VerticalOffset - before);
        SetCursorPos(old.X, old.Y);
        return offset;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--crash-child") return CrashChild(args);
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-V2000Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch); Environment.SetEnvironmentVariable("NDM_STATE_DIR", Path.Combine(scratch, "NDM"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
        try
        {
            Loc.SetLanguage("en"); TestGuard.Enable();
            var only = Environment.GetEnvironmentVariable("NEODM_ONLY");
            void Run(string name, Action a) { if (only is { Length: > 0 } && !name.StartsWith(only)) return; Console.WriteLine("== " + name); TestGuard.Run(name, a); }
            if (args.Contains("--reproduce")) { Reproduce(scratch, args.Contains("--real-wheel")); return 0; }
            Run("FirefoxRecognised", FirefoxRecognised);
            Run("WheelOptionsPages", () => WheelOptionsPages(scratch));
            Run("WheelNestedList", WheelNestedList);
            Run("ProcessingRow", ProcessingRow);
            Run("DiskSpaceRow", DiskSpaceRow);
            Run("RemovePartial", RemovePartial);
            Run("GuardDuplicate", () => GuardDuplicate(scratch));
            Run("GuardSpace", () => GuardSpace(scratch));
            Run("GuardBatch", () => GuardBatch(scratch));
            Run("OptionsTemp", () => OptionsTemp(scratch));
            Run("PromptSurvives", PromptSurvives);
            Run("QueueUi", () => QueueUi(scratch));
            Run("Localized", Localized);
            Run("ServerData", ServerData);
            Run("ServerRender", () => ServerRender(scratch));
            Run("ServerColumn", () => ServerColumn(scratch));
            Run("MirrorGate", MirrorGate);
            Run("MirrorChooser", () => MirrorChooser(scratch));
            Run("MirrorRender", () => MirrorRender(scratch));
            Run("MirrorErrorAndProxy", MirrorErrorAndProxy);
            Run("RefreshLocalized", RefreshLocalized);
            Run("RefreshBridge", RefreshBridge);
            Run("RefreshFlow", () => RefreshFlow(scratch));
            Run("RefreshUi", () => RefreshUi(scratch));
            Run("TabRulesPure", TabRulesPure);
            Run("TabsWindowTweaks", () => TabsWindowTweaks(scratch));
            Run("TabsOverflow", () => TabsOverflow(scratch));
            Run("TabsOffer", () => TabsOffer(scratch));
            Run("TabsOptions", () => TabsOptions(scratch));
            Run("BackupFiles", () => BackupFiles(scratch));
            Run("BackupUi", () => BackupUi(scratch));
            Run("ReportZip", () => ReportZip(scratch));
            Run("ReportWindowUi", () => ReportWindowUi(scratch));
            Run("CrashNotes", () => CrashNotes(scratch));
            Run("CrashReal", () => CrashReal(scratch));
            Run("SearchCoverage", () => SearchCoverage(scratch));
            Run("SearchControls", () => SearchControls(scratch));
            Run("SearchBehaviour", () => SearchBehaviour(scratch));
            Run("SearchShots", () => SearchShots(scratch));
            Run("FirstRun", () => FirstRun(scratch));
            Run("FirstRunShots", () => FirstRunShots(scratch));
            Run("ReportShots", () => ReportShots(scratch));
            Console.WriteLine($"V2000 APP TESTS PASSED: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(scratch, true); } catch (IOException) { } app.Shutdown(); }
    }

    private static SettingsWindow OpenOptions(string scratch, string tab, double height = 760)
    {
        var settings = AppSettings.Load(Path.Combine(scratch, "opt-" + Guid.NewGuid().ToString("N")));
        var w = new SettingsWindow(settings, Path.Combine(scratch, "NDM")) { Height = height, Width = 720, Left = 40, Top = 40, WindowStartupLocation = WindowStartupLocation.Manual, Topmost = true };
        w.Show(); w.Activate(); Pump(150);
        var tabs = (TabControl)w.FindName("OptionsTabs");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => t.Name == tab || tab == "*"); Pump(150);
        return w;
    }

    private static void Reproduce(string scratch, bool real)
    {
        if (Environment.GetEnvironmentVariable("V2000_DEBUG") == "2")
        {
            var d = OpenOptions(scratch, "*"); var tabs = (TabControl)d.FindName("OptionsTabs");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => t.Header?.ToString() == "Downloads" || t.Header?.ToString() == "_Downloads"); Pump(200);
            var pg = (ScrollViewer)((TabItem)tabs.SelectedItem).Content; var seg = (FrameworkElement)d.FindName("Segments");
            seg.BringIntoView(); Pump(100); var tgt = Under(d, seg);
            var ev = Turn(tgt, -360, UIElement.PreviewMouseWheelEvent); tgt.RaiseEvent(ev); Pump(300);
            Console.WriteLine($"header={((TabItem)tabs.SelectedItem).Header} handled={ev.Handled} page offset={pg.VerticalOffset} of {pg.ScrollableHeight}");
            return;
        }
        var w = OpenOptions(scratch, "AppearanceTab");
        var page = (ScrollViewer)((TabItem)w.FindName("AppearanceTab")).Content;
        Console.WriteLine($"page scrollable height {page.ScrollableHeight:0}");
        foreach (var name in new[] { "ThemeGallery", "AccentSwatches", "AccentHex", "AnimateBars", "TabsBox" })
        {
            var el = (FrameworkElement)w.FindName(name);
            Console.WriteLine($"{name,-16} page moved by {(real ? RealWheel(w, page, el) : Wheel(w, page, el)):0.#}");
        }
        w.Close();
    }

    /// <summary>Item 25: on every Options page, a wheel turn over each kind of control moves the page (Light and Deep Navy, left-to-right and right-to-left).</summary>
    private static void WheelOptionsPages(string scratch)
    {
        foreach (var theme in new[] { "Light", "DeepNavy" })
            foreach (var rtl in new[] { false, true })
            {
                ThemeManager.Apply(Application.Current, theme, null);
                var w = OpenOptions(scratch, "*");
                w.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight; Pump(100);
                var tabs = (TabControl)w.FindName("OptionsTabs");
                int pages = 0, probes = 0;
                foreach (var tab in tabs.Items.OfType<TabItem>())
                {
                    tabs.SelectedItem = tab; Pump(120);
                    var page = (ScrollViewer)tab.Content;
                    if (page.ScrollableHeight < 20) continue;
                    pages++;
                    var seen = new HashSet<Type>();
                    var candidates = Descendants(page).OfType<FrameworkElement>().Where(e => e.IsVisible && e.ActualHeight > 8 && e.ActualWidth > 8 && (e is ListBox or ItemsControl or TextBox or ComboBox or CheckBox or Button or RadioButton or GroupBox or TextBlock or Border)).ToList();
                    foreach (var el in candidates.Where(c => c is ListBox or ItemsControl or ComboBox or TextBox).Concat(candidates.Where(c => seen.Add(c.GetType()))).Distinct().Take(16))
                    {
                        var moved = Wheel(w, page, el);
                        probes++;
                        if (moved <= 0.5 && Environment.GetEnvironmentVariable("V2000_DEBUG") == "1") Console.WriteLine($"  debug: page offset {page.VerticalOffset} of {page.ScrollableHeight}, under = {Under(w, el).GetType().Name}, route handled-by = ?"); if (moved <= 0.5 && Environment.GetEnvironmentVariable("V2000_DEBUG") == "1") { DependencyObject? d = Under(w, el); while (d is not null) { if (d is ScrollViewer sv) Console.WriteLine($"   ancestor ScrollViewer {sv.Name} vis={sv.VerticalScrollBarVisibility} off={sv.VerticalOffset} h={sv.ScrollableHeight} can={WheelForwarding.CanScroll(sv, -120)}"); d = VisualTreeHelper.GetParent(d); } }
                        if (moved <= 0.5) throw new Exception($"FAIL: {theme}{(rtl ? " RTL" : "")} / {tab.Name}: the wheel over a {el.GetType().Name} '{el.Name}' did not move the page");
                    }
                }
                Check(pages >= 3 && probes >= 12, $"{theme}{(rtl ? " RTL" : "")}: the wheel moved the page over {probes} controls on {pages} Options pages");
                w.Close();
            }
        ThemeManager.Apply(Application.Current, "Light", null);
    }

    /// <summary>A list that scrolls keeps the wheel until its end, then the page takes over; a list with nothing to scroll hands the wheel to the page at once.</summary>
    private static void WheelNestedList()
    {
        var list = new ListBox { Height = 90, ItemsSource = Enumerable.Range(0, 40).Select(i => "item " + i).ToList() };
        var shortList = new ListBox { Height = 60, ItemsSource = new[] { "a", "b" } };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Height = 30, Text = "top" }); panel.Children.Add(list); panel.Children.Add(shortList); panel.Children.Add(new Border { Height = 900 });
        var page = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        var w = new Window { Width = 400, Height = 400, Content = page, Left = 30, Top = 30, WindowStartupLocation = WindowStartupLocation.Manual }; w.Show(); Pump(150);
        var inner = Find<ScrollViewer>(list)!;
        page.ScrollToVerticalOffset(0);
        var t1 = Under(w, list); t1.RaiseEvent(Turn(t1, -120, UIElement.PreviewMouseWheelEvent)); Pump(30);
        Check(page.VerticalOffset == 0, "a list that can scroll keeps the wheel (the page stays)");
        inner.ScrollToVerticalOffset(inner.ScrollableHeight); Pump(30);
        var before = page.VerticalOffset;
        var e1 = Turn(t1, -120, UIElement.PreviewMouseWheelEvent); t1.RaiseEvent(e1); Pump(30);
        Check(page.VerticalOffset > before && e1.Handled, "a list at its end hands the wheel to the page");
        page.ScrollToVerticalOffset(0); Pump(30);
        var t2 = Under(w, shortList); var e2 = Turn(t2, -120, UIElement.PreviewMouseWheelEvent); t2.RaiseEvent(e2); Pump(30);
        Check(page.VerticalOffset > 0, "a list with nothing to scroll hands the wheel to the page");
        page.ScrollToVerticalOffset(25); inner.ScrollToVerticalOffset(0); Pump(30);
        var t3 = Under(w, list); var top = page.VerticalOffset; t3.RaiseEvent(Turn(t3, 120, UIElement.PreviewMouseWheelEvent)); Pump(30);
        Check(page.VerticalOffset < top, "turning up from the top of a list moves the page up");
        w.Close();
    }

    private static DownloadJob JobWith(Action<DownloadJob> set)
    {
        var job = new DownloadJob();
        SetJob(job, "Id", Guid.NewGuid().ToString("N")); SetJob(job, "Url", "https://example.test/file.bin"); SetJob(job, "FileName", "file.bin"); SetJob(job, "FilePath", @"C:\Temp\file.bin");
        set(job);
        return job;
    }
    private static void SetJob(DownloadJob job, string name, object? value) => typeof(DownloadJob).GetProperty(name)!.GetSetMethod(true)!.Invoke(job, new[] { value });

    /// <summary>Item 2: the word Processing in the list row, the tab, the taskbar and the counts; percent, speed and time left show; Pause is not offered.</summary>
    private static void ProcessingRow()
    {
        var job = JobWith(j =>
        {
            SetJob(j, "Status", "Processing"); SetJob(j, "TotalBytes", 1000L * 1024 * 1024); SetJob(j, "DownloadedBytes", 1000L * 1024 * 1024); SetJob(j, "ProcessedBytes", 250L * 1024 * 1024);
            SetJob(j, "SpeedBytesPerSecond", 50.0 * 1024 * 1024); SetJob(j, "RemainingTime", TimeSpan.FromSeconds(15));
        });
        var row = new DownloadRow(job);
        Check(row.StatusText == "Processing", "the status reads Processing, not Joining parts: " + row.StatusText);
        Check(row.Percent == "25%", "the percent is the processed share: " + row.Percent);
        Check(row.SpeedText.Contains("MB") && row.SpeedText != "—", "the speed shows: " + row.SpeedText);
        Check(row.Remaining != "—" && row.Remaining.Length > 0, "the time left shows: " + row.Remaining);
        Check(!row.CanPause && row.IsBusy && row.StatusKind == "Downloading", "no Pause, counted as busy, drawn in the downloading colour");
        Check(row.IsTransferring, "the bar sweeps while Processing");
        Check(TaskbarProgressModel.Compute(new[] { row }) == new TaskbarProgressValue(TaskbarState.Normal, 25), "the taskbar shows the processing percent");
        Check(TabClosePolicy.IsRunning("Processing"), "a processing download is a running tab");
        Check(CompletionActionsActive("Processing"), "'when downloads finish' waits for a processing download");
        foreach (var l in new[] { "ar", "de", "en", "es", "fr", "ja", "pt-BR", "ru", "tr", "zh-Hans" })
        {
            Loc.SetLanguage(l); var t = Loc.Status("Processing");
            if (t.Length == 0 || t == "Processing" && l != "en" || t.Contains("Joining")) throw new Exception("FAIL: status word in " + l + ": " + t);
        }
        Loc.SetLanguage("en");
        Check(true, "every language names the status");
    }
    private static bool CompletionActionsActive(string status) => (bool)typeof(QueueFinishTrigger).GetMethod("IsActive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { status })!;

    /// <summary>Item 11: a stopped download names the drive and the numbers in plain words.</summary>
    private static void DiskSpaceRow()
    {
        var job = JobWith(j => { SetJob(j, "Status", "Failed"); SetJob(j, "ErrorCode", "disk-space"); SetJob(j, "Error", "Not enough free disk space on D: this download needs about 1.5 GB and 200 MB is free."); SetJob(j, "ErrorExpectedBytes", 1500L * 1024 * 1024); SetJob(j, "ErrorReceivedBytes", 200L * 1024 * 1024); });
        var row = new DownloadRow(job);
        Check(row.IsDiskSpace && row.ErrorIsLocalized && row.Error!.Contains("D:") && row.Error.Contains("1.5 GB") && row.Error.Contains("200 MB") && row.Error.Contains("Resume"), "the failure names the drive, what is needed and what is free: " + row.Error);
    }

    /// <summary>Item 1: the remove window tells how much unfinished data goes with a cancelled download.</summary>
    private static void RemovePartial()
    {
        var job = JobWith(j => { SetJob(j, "Status", "Paused"); SetJob(j, "TotalBytes", 100L << 30); SetJob(j, "DownloadedBytes", 12L << 30); });
        var item = RemoveActions.Describe(new DownloadRow(job), null, _ => 12L * 1024 * 1024 * 1024 + 400L * 1024 * 1024);
        Check(item.PartialBytes > 12L << 30, "the describe step carries the partial size");
        var window = new RemoveConfirmWindow(new[] { item });
        var explain = (TextBlock)window.FindName("Explain");
        Check(explain.Text.Contains("12.4 GB of unfinished data will be deleted"), "the dialog says how much unfinished data is deleted: " + explain.Text);
        var done = RemoveActions.Describe(new DownloadRow(JobWith(j => SetJob(j, "Status", "Completed"))), null, _ => 999L);
        Check(done.PartialBytes == 0, "a finished download has no partial data");
        var none = new RemoveConfirmWindow(new[] { RemoveActions.Describe(new DownloadRow(job)) });
        Check(!((TextBlock)none.FindName("Explain")).Text.Contains("will be deleted"), "no partial size, no sentence");
    }

    private static DownloadManager Manager(string scratch, Func<string, long?>? free = null)
    {
        var dir = Path.Combine(scratch, "m-" + Guid.NewGuid().ToString("N"));
        return new DownloadManager(Path.Combine(dir, "downloads"), Path.Combine(dir, "state"), new DownloadOptions { FreeSpace = free, RetryCount = 0 });
    }

    private static (string Title, string Text, string[] Labels) lastAsk;
    private static void Answer(int index) => ChoiceWindow.Override = (_, title, text, labels) => { lastAsk = (title, text, labels); return index; };

    /// <summary>Item 10: Skip, Download again and Rename; nothing is asked for a new file; streams are never asked.</summary>
    private static void GuardDuplicate(string scratch)
    {
        using var m = Manager(scratch);
        var first = new DownloadRequest("https://example.test/archive.zip", "archive.zip");
        m.AddScheduledAsync(first, DateTimeOffset.UtcNow.AddHours(1)).GetAwaiter().GetResult();
        try
        {
            var asked = 0; ChoiceWindow.Override = (_, t, x, l) => { asked++; lastAsk = (t, x, l); return -1; };
            Check(AddGuard.Check(m, null, new DownloadRequest("https://example.test/other.zip", "other.zip"), null).Proceed && asked == 0, "a new file is not asked about");
            var skip = AddGuard.Check(m, null, first, null);
            Check(!skip.Proceed && asked == 1 && lastAsk.Labels.SequenceEqual(new[] { "Download again", "Rename", "Skip" }) && lastAsk.Text.Contains("archive.zip"), "Skip stops the add: " + lastAsk.Text);
            Answer(0);
            var again = AddGuard.Check(m, null, first, null);
            Check(again.Proceed && again.Request == first, "Download again goes on unchanged");
            Answer(1); AddGuard.AskName = (_, _, _, suggestion, validate) =>
            {
                if (suggestion != "archive (1).zip" || validate("a/b") is null || validate("fine.zip") is not null) throw new Exception("FAIL: rename suggestion or validation: " + suggestion);
                return "archive-v2.zip";
            };
            var renamed = AddGuard.Check(m, null, first, null);
            Check(renamed.Proceed && renamed.Request.FileName == "archive-v2.zip" && !renamed.Request.AutoName, "Rename carries the new name");
            AddGuard.AskName = (_, _, _, _, _) => null;
            Check(!AddGuard.Check(m, null, first, null).Proceed, "a cancelled rename stops the add");
            asked = 0; ChoiceWindow.Override = (_, t, x, l) => { asked++; return -1; };
            Check(AddGuard.Check(m, null, new DownloadRequest("https://example.test/live.m3u8", StreamKind: StreamKind.Hls), null).Proceed && asked == 0, "streams are not asked");
        }
        finally { ChoiceWindow.Override = null; AddGuard.AskName = null; }
    }

    /// <summary>Item 11: the low-space question names the drive and the numbers; each answer does what it says.</summary>
    private static void GuardSpace(string scratch)
    {
        const long gb = 1L << 30;
        using var m = Manager(scratch, path => 2 * gb);
        var settings = AppSettings.Load(Path.Combine(scratch, "guard-settings"));
        var big = new DownloadRequest("https://example.test/big.iso", "big.iso", ExpectedSize: 10 * gb);
        try
        {
            Answer(3); var cancelled = AddGuard.CheckSpace(m, settings, big, null);
            Check(!cancelled.Proceed && lastAsk.Text.Contains("12.5 GB") && lastAsk.Text.Contains("2 GB") && lastAsk.Labels[0] == "Choose another folder" && lastAsk.Labels[1].StartsWith("Use a temporary folder on another drive"), "the question names the numbers: " + lastAsk.Text);
            Answer(2); Check(AddGuard.CheckSpace(m, settings, big, null).Proceed, "Download anyway goes on");
            var temp = Path.Combine(scratch, "other-drive-temp"); var saved = 0;
            var rounds = 0; ChoiceWindow.Override = (_, t, x, l) => { lastAsk = (t, x, l); return ++rounds == 1 ? 1 : 2; }; AddGuard.PickFolder = (_, _) => temp;
            AddGuard.CheckSpace(m, settings, big, null, () => saved++);
            Check(m.TemporaryFolder == Path.GetFullPath(temp) && settings.TemporaryFolder == temp && saved == 1, "the temporary folder is set and saved");
            var folder = Path.Combine(scratch, "bigger-folder"); AddGuard.PickFolder = (_, _) => folder; m.TemporaryFolder = null; settings.TemporaryFolder = null;
            var asked = 0; ChoiceWindow.Override = (_, t, x, l) => { asked++; lastAsk = (t, x, l); return asked == 1 ? 0 : 2; };
            var moved = AddGuard.CheckSpace(m, settings, big, null);
            Check(moved.Proceed && moved.Request.TargetDirectory == folder && asked == 2, "another folder is picked and the space is checked again");
            AddGuard.PickFolder = (_, _) => null; Answer(0);
            Check(!AddGuard.CheckSpace(m, settings, big, null).Proceed, "a cancelled folder choice stops the add");
            asked = 0; ChoiceWindow.Override = (_, t, x, l) => { asked++; return 3; };
            Check(AddGuard.CheckSpace(m, settings, new DownloadRequest("https://example.test/small.bin", "small.bin", ExpectedSize: gb), null).Proceed && asked == 0, "a file that fits is not asked about");
            Check(AddGuard.CheckSpace(m, settings, new DownloadRequest("https://example.test/unknown.bin", "unknown.bin"), null).Proceed && asked == 0, "an unknown size is not asked about");
        }
        finally { ChoiceWindow.Override = null; AddGuard.PickFolder = null; }
    }

    private static void GuardBatch(string scratch)
    {
        using var m = Manager(scratch);
        m.AddScheduledAsync(new DownloadRequest("https://example.test/a.zip", "a.zip"), DateTimeOffset.UtcNow.AddHours(1)).GetAwaiter().GetResult();
        var batch = new List<DownloadRequest> { new("https://example.test/a.zip", "a.zip"), new("https://example.test/b.zip", "b.zip"), new("https://example.test/c.zip", "c.zip") };
        try
        {
            Answer(0); var skipped = AddGuard.FilterBatch(m, batch, null);
            Check(skipped is { Count: 2 } && skipped.All(r => r.Url != "https://example.test/a.zip") && lastAsk.Text.Contains("1") && lastAsk.Text.Contains("3"), "Skip those leaves the other two: " + lastAsk.Text);
            Answer(1); Check(AddGuard.FilterBatch(m, batch, null)!.Count == 3, "Download all keeps everything");
            Answer(-1); Check(AddGuard.FilterBatch(m, batch, null) is null, "Cancel adds nothing");
            var asked = 0; ChoiceWindow.Override = (_, _, _, _) => { asked++; return 0; };
            Check(AddGuard.FilterBatch(m, batch.Skip(1).ToList(), null)!.Count == 2 && asked == 0, "no duplicates, no question");
        }
        finally { ChoiceWindow.Override = null; }
    }

    private static T Field<T>(Window w, string name) where T : class => (T)w.FindName(name);
    private static void Click(Window w, string name) { Field<Button>(w, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(250); }

    /// <summary>Items 3, 1, 23: the temporary files folder and the clean-up button in Options, and 64 connections.</summary>
    private static void OptionsTemp(string scratch)
    {
        var state = Path.Combine(scratch, "opt-state-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(state);
        var settings = AppSettings.Load(state);
        Check(settings.TemporaryFolder is null, "the default is the same drive as the download");
        var w = new SettingsWindow(settings, state) { PartialCleanup = new PartialCleanup(() => 5L * 1024 * 1024 * 1024, () => (2, 5L * 1024 * 1024 * 1024)) };
        w.Show(); Pump(250);
        Check(Field<TextBox>(w, "TempFolder").Text == "", "the folder box is empty by default");
        Check(Field<TextBlock>(w, "CleanupState").Text.Contains("5 GB"), "the clean-up line shows what can be removed: " + Field<TextBlock>(w, "CleanupState").Text);
        Click(w, "CleanupButton"); Pump(500);
        Check(Field<TextBlock>(w, "CleanupState").Text.Contains("5 GB") && Field<TextBlock>(w, "CleanupState").Text.StartsWith("Removed"), "the clean-up reports what it removed: " + Field<TextBlock>(w, "CleanupState").Text);
        var temp = Path.Combine(scratch, "temp-options");
        Field<TextBox>(w, "TempFolder").Text = @"relative\folder"; Field<TextBox>(w, "Segments").Text = "64";
        Click(w, "SaveButton");
        Check(w.IsVisible && Field<TextBlock>(w, "ErrorText").Text.Length > 0 && settings.TemporaryFolder is null, "a relative temporary folder is refused: " + Field<TextBlock>(w, "ErrorText").Text);
        Field<TextBox>(w, "TempFolder").Text = temp;
        Click(w, "SaveButton");
        Check(!w.IsVisible && settings.TemporaryFolder == Path.GetFullPath(temp) && settings.MaxSegments == 64 && Directory.Exists(temp), "a complete folder is created and saved; 64 connections are accepted");
        Check(AppSettings.Load(state).TemporaryFolder == Path.GetFullPath(temp) && AppSettings.Load(state).MaxSegments == 64, "both survive a restart");
        var again = new SettingsWindow(settings, state); again.Show(); Pump(250);
        Field<TextBox>(again, "Segments").Text = "65"; Click(again, "SaveButton");
        Check(again.IsVisible && settings.MaxSegments == 64 && Field<TextBlock>(again, "ErrorText").Text.Contains("64"), "65 connections are refused and the message names the limit: " + Field<TextBlock>(again, "ErrorText").Text);
        Field<TextBox>(again, "TempFolder").Text = ""; Field<TextBox>(again, "Segments").Text = "8"; Click(again, "SaveButton");
        Check(settings.TemporaryFolder is null, "emptying the box goes back to the same drive");
        var o = OptionsHelper.CreateDownloadOptions(new AppSettings { TemporaryFolder = temp, MaxSegments = 64 });
        Check(o.TemporaryFolder == temp && o.MaxSegments == 64, "the engine receives the folder and 64 connections");
    }

    /// <summary>Every new text exists in all ten languages.</summary>
    // ---------------- stage S2: queues ----------------
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    private static IntPtr Handle(Window w) => new System.Windows.Interop.WindowInteropHelper(w).Handle;
    /// <summary>True when window <paramref name="a"/> is above <paramref name="b"/> in the real z-order (walks up from b).</summary>
    private static bool IsAbove(Window a, Window b)
    {
        var target = Handle(a);
        for (var h = GetWindow(Handle(b), 3); h != IntPtr.Zero; h = GetWindow(h, 3)) if (h == target) return true; // GW_HWNDPREV
        return false;
    }
    private static TextBlock? ClockOf(ListBoxItem node) => (node.Content as DockPanel)?.Children.OfType<StackPanel>().SelectMany(x => x.Children.OfType<TextBlock>()).FirstOrDefault(t => t.Tag as string == "clock");
    private static T Find<T>(DependencyObject root, Func<T, bool> test) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t && test(t)) return t;
            var r = Find(c, test); if (r is not null) return r;
        }
        return null!;
    }

    /// <summary>Item 22. The real failure: the old chooser (no taskbar button, not topmost, owner hidden) sank under the next window the user clicked. Shown for real, the old shape is buried and the new one is not.</summary>
    private static void PromptSurvives()
    {
        var queues = new[] { new DownloadQueue { Id = "main", Name = "Main queue" }, new DownloadQueue { Id = "q2", Name = "Night" } };
        Func<string, (int, int)> load = _ => (0, 3);
        var browser = new Window { Title = "Browser stand-in", Width = 500, Height = 400, Left = 80, Top = 80, WindowStartupLocation = WindowStartupLocation.Manual };
        browser.Show(); Pump(150);

        var old = new QueueChooserWindow(queues, "main", load, false, BusyChoice.Wait, 1) { Owner = null, ShowInTaskbar = false, Topmost = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 100, Top = 100 };
        old.Show(); Pump(150);
        browser.Activate(); browser.Topmost = true; browser.Topmost = false; Pump(150);
        Check(!old.ShowInTaskbar && IsAbove(browser, old), "reproduced: a click on the browser buries the old chooser (no taskbar button, below the browser)");
        old.Close();

        var shown = new QueueChooserWindow(queues, "main", load, false, BusyChoice.Wait, 1);
        PromptWindows.Prepare(shown, null);
        shown.WindowStartupLocation = WindowStartupLocation.Manual; shown.Left = 100; shown.Top = 100;
        shown.Show(); Pump(250);
        Check(shown.ShowInTaskbar && shown.Topmost && shown.Owner is null, "the prompt has a taskbar button, stays on top and stands alone when there is no visible owner");
        Check((GetWindowLongPtr(Handle(shown), -20).ToInt64() & 0x40000) != 0, "the prompt is listed in Alt+Tab (app-window style)");
        browser.Activate(); Pump(150);
        var other = new Window { Title = "Another window", Width = 400, Height = 300, Left = 120, Top = 120, WindowStartupLocation = WindowStartupLocation.Manual };
        other.Show(); other.Activate(); Pump(200);
        Check(IsAbove(shown, browser) && IsAbove(shown, other), "the prompt stays above the browser and another window after a click elsewhere");
        Check(shown.IsVisible, "the prompt is still open");
        shown.Close(); other.Close();

        var main = new Window { Title = "Main", Width = 500, Height = 400, Left = 60, Top = 60, WindowStartupLocation = WindowStartupLocation.Manual }; main.Show(); Pump(100);
        var owned = new BusyQueuePrompt(1, 1, 1); PromptWindows.Prepare(owned, main);
        Check(owned.Owner == main && owned.ShowInTaskbar && owned.Topmost, "with a visible owner the prompt is owned, in the taskbar and on top");
        var text = new TextPromptWindow("t", "p", "x", null); PromptWindows.Prepare(text, main);
        Check(text.ShowInTaskbar && text.Topmost && text.Owner == main, "the rename prompt of the duplicate check is a proper window too");
        var choice = (Window)Activator.CreateInstance(typeof(ChoiceWindow), "t", "m", new[] { "a", "b" })!; PromptWindows.Prepare(choice, main);
        Check(choice.ShowInTaskbar && choice.Topmost, "the choice windows of the duplicate and space questions are proper windows too");
        main.WindowState = WindowState.Minimized; Pump(100);
        var alone = new BusyQueuePrompt(1, 1, 1); PromptWindows.Prepare(alone, main);
        Check(alone.Owner is null && alone.WindowStartupLocation == WindowStartupLocation.CenterScreen, "a minimised owner is not used: the prompt stands alone, centred on the screen");
        main.Close();
        Dialogs.TestMode = true;
        Check(ChoiceWindow.Ask(null, "t", "m", "a", "b") == -1, "test mode still answers choice windows without showing them");
        Dialogs.TestMode = false;
    }

    /// <summary>Items 19, 20, 24: Start now in every place, the queue menu, the paused mark, the Downloads menu entries, the option.</summary>
    private static void QueueUi(string scratch)
    {
        var root = Path.Combine(scratch, "qui-" + Guid.NewGuid().ToString("N"));
        using var manager = new DownloadManager(Path.Combine(root, "files"), Path.Combine(root, "state"), new DownloadOptions { MaxConcurrentDownloads = 1, MaxSegments = 1, RetryCount = 0 });
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "files"), ShowSelectionCheckboxes = true };
        var night = manager.CreateQueue("Night");
        manager.PauseQueue(QueueIds.Main, false); // a held queue keeps the waiting download Queued, so nothing touches the network
        var job = manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/never.bin", "never.bin"), null).GetAwaiter().GetResult();
        var window = new MainWindow(manager, settings, () => { }, enableTray: false) { ShowInTaskbar = false, Width = 1100, Height = 700 };
        window.Show(); Pump(400);
        var row = window.Rows.Single();
        Check(row.Status == "Queued" && row.CanStartNow, "a waiting download offers Start now");
        Check(!new DownloadRow(JobWith(j => SetJob(j, "Status", "Paused"))).CanStartNow && !new DownloadRow(JobWith(j => SetJob(j, "Status", "Downloading"))).CanStartNow, "a paused or running download does not");
        Check(RowMenu.For(new[] { row }, _ => false, _ => false).StartNow && !RowMenu.For(new[] { new DownloadRow(JobWith(j => SetJob(j, "Status", "Paused"))) }, _ => false, _ => false).StartNow, "the row menu state offers Start now only for waiting downloads");
        var list = (ListView)window.FindName("DownloadList"); list.SelectedItem = row; Pump(200);
        window.UpdateRowMenu();
        Check(((MenuItem)window.FindName("CtxStartNow")).Visibility == Visibility.Visible, "the right-click menu shows Start now");
        Check(((MenuItem)window.FindName("StartNowMenuItem")).IsEnabled, "the Downloads menu enables Start now");
        row.IsChecked = true; Pump(200);
        Check(((Button)window.FindName("BulkStartNow")).IsEnabled, "the selection bar enables Start now");
        var view = new DownloadProgressView(manager, job.Id, null, settings); Pump(100);
        Check(((Button)view.FindName("StartNowButton")).Visibility == Visibility.Visible, "the progress window and tab show a Start now button for a waiting download");
        ((MenuItem)window.FindName("CtxStartNow")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(300);
        Check(manager.Jobs.Single().Status != "Queued", "clicking Start now starts the download although its queue is held: " + manager.Jobs.Single().Status);
        Check(manager.LimitOf(QueueIds.Main) == 1, "the queue limit is unchanged");

        // queue menu, paused mark, Downloads menu
        var build = typeof(MainWindow).GetMethod("BuildQueueMenu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ContextMenu Menu(string id) => (ContextMenu)build.Invoke(window, new object[] { id })!;
        MenuItem Entry(ContextMenu m, string header) => m.Items.OfType<MenuItem>().Single(i => (string)i.Header == header);
        var menu = Menu(night.Id);
        Check(Entry(menu, "Pause queue").IsEnabled && Entry(menu, "Hold queue, let running ones finish").IsEnabled && !Entry(menu, "Resume queue").IsEnabled, "a running queue offers Pause and Hold, not Resume");
        Entry(menu, "Hold queue, let running ones finish").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(300);
        Check(manager.IsQueuePaused(night.Id), "Hold queue holds the queue"); Pump(1500);
        menu = Menu(night.Id);
        Check(!Entry(menu, "Hold queue, let running ones finish").IsEnabled && Entry(menu, "Resume queue").IsEnabled, "a held queue offers Resume");
        var nav = (ListBox)window.FindName("Navigation");
        var node = nav.Items.OfType<ListBoxItem>().First(i => i.Tag as string == "Q:" + night.Id);
        var marks = ClockOf(node);
        Check(marks is not null && marks.Text.Contains("Paused") && marks.Visibility == Visibility.Visible, "the sidebar shows the pause mark and the word Paused: " + (marks is null ? "no clock block" : "[" + marks.Text + "]"));
        nav.SelectedItem = node; Pump(300);
        Check(((MenuItem)window.FindName("QueueResumeMenuItem")).Visibility == Visibility.Visible && ((MenuItem)window.FindName("QueueResumeMenuItem")).IsEnabled && !((MenuItem)window.FindName("QueuePauseMenuItem")).IsEnabled, "the Downloads menu shows Resume queue for the selected held queue");
        ((MenuItem)window.FindName("QueueResumeMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(300);
        Check(!manager.IsQueuePaused(night.Id), "Resume queue from the Downloads menu releases the hold");
        Check(!(ClockOf(node)?.Text ?? "").Contains("Paused"), "the mark is gone after Resume");
        nav.SelectedIndex = 0; Pump(300);
        Check(((MenuItem)window.FindName("QueuePauseMenuItem")).Visibility == Visibility.Collapsed, "the queue entries of the Downloads menu hide when no queue is selected");
        window.Close(); Pump(100);

        // option
        var s = new AppSettings(); Check(s.PausePausesQueue, "the option defaults to on");
        s.PausePausesQueue = false; var dir = Path.Combine(root, "opt"); Directory.CreateDirectory(dir); s.Save(dir);
        Check(!AppSettings.Load(dir).PausePausesQueue, "the option is saved");
        var opt = new SettingsWindow(AppSettings.Load(dir), dir) { Width = 720, Height = 760 }; opt.Show(); Pump(200);
        var box = (CheckBox)opt.FindName("PausePausesQueueBox");
        Check(box.IsChecked == false && !string.IsNullOrWhiteSpace(box.Content as string), "Options > Downloads shows the option with its saved value");
        opt.Close();
    }

    private static void Localized()
    {
        string[] keys = { "status.processing", "error.disk_space", "remove.partial", "options.temp.title", "options.temp.help", "options.temp.label", "options.temp.choose", "options.temp.same", "main.menu.start_now", "queue.menu.pause", "queue.menu.hold", "queue.menu.resume", "queue.paused", "queue.notice.paused", "queue.notice.resumed", "queue.notice.started_now", "options.chk.pause_pauses_queue", "queue.editor.resume_on_schedule", "options.temp.cleanup", "options.temp.found", "options.temp.none", "options.temp.done", "options.temp.left", "options.temp.picker", "options.temp.incomplete", "options.temp.unusable",
            "dup.title", "dup.in_list", "dup.downloaded", "dup.same_file", "dup.btn.again", "dup.btn.rename", "dup.btn.skip", "dup.btn.skip_those", "dup.btn.all", "dup.batch", "dup.rename.title", "dup.rename.text",
            "space.title", "space.short", "space.short_temp", "space.other_drives", "space.drive_free", "space.btn.folder", "space.btn.temp", "space.btn.anyway", "space.temp_note", "space.picker.folder", "space.picker.temp" };
        foreach (var lang in new[] { "ar", "de", "en", "es", "fr", "ja", "pt-BR", "ru", "tr", "zh-Hans" })
        {
            Loc.SetLanguage(lang);
            foreach (var key in keys) if (Loc.T(key) == key) throw new Exception($"FAIL: {lang} is missing {key}");
        }
        Loc.SetLanguage("en");
        Check(true, "all " + keys.Length + " new texts exist in ten languages");
        Check(Loc.T("options.label.maximum_segments_per_file_1_to").Contains("64") && !Loc.T("options.label.maximum_segments_per_file_1_to").Contains("32"), "the connection label says 64");
    }
}
