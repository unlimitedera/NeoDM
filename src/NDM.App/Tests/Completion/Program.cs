using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.Core;
using NDM.Tests;

// Same isolation as the other WPF suites: the real App would take the machine-wide mutex and open the user's live queue.
internal sealed class TestApp : NDM.App.App
{
    public TestApp()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
    protected override void OnStartup(StartupEventArgs e) { }
}

/// <summary>1.4.8: finished downloads in the tray (window entries, recent list, balloons, unseen indicator) and the front-most completion window.</summary>
internal static class CompletionTrayTests
{
    private static int passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static void Pump(int ms = 100)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Func<bool> condition, string what, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException(what); Pump(50); }
    }
    private static T Sync<T>(Task<T> task) { PumpUntil(() => task.IsCompleted, "async operation"); return task.GetAwaiter().GetResult(); }

    /// <summary>Windows fake: the foreground belongs to another app, SetForegroundWindow can be made to fail, the full-screen state is switchable.</summary>
    private sealed class FakeNative : IWindowNative
    {
        public IntPtr Foreground = new(1); public bool Minimized, AllowForegroundSwitch, Busy;
        public List<string> Calls = new();
        public IntPtr ForegroundWindow() => Foreground;
        public uint ThreadOf(IntPtr window) => 9; public uint CurrentThread() => 7;
        public bool IsMinimized(IntPtr window) => Minimized;
        public void Restore(IntPtr window) { Calls.Add("restore"); Minimized = false; }
        public bool AttachThreadInput(uint from, uint to, bool attach) { Calls.Add(attach ? "attach" : "detach"); return true; }
        public bool SetForeground(IntPtr window) { Calls.Add("setfg"); if (AllowForegroundSwitch) Foreground = window; return AllowForegroundSwitch; }
        public void BringToTop(IntPtr window) => Calls.Add("top");
        public void TapAlt() => Calls.Add("alt");
        public void Flash(IntPtr window) => Calls.Add("flash");
        public void AllowForeground() => Calls.Add("allow");
        public bool IsFullscreenBusy() => Busy;
    }

    /// <summary>Timers that only run when the test fires them.</summary>
    private sealed class FakeTimers
    {
        public sealed class Handle(FakeTimers owner, TimeSpan delay, Action action) : IDisposable
        {
            public TimeSpan Delay = delay; public Action Action = action; public bool Disposed;
            public void Dispose() { Disposed = true; owner.Pending.Remove(this); }
        }
        public List<Handle> Pending = new(); public List<TimeSpan> History = new();
        public IDisposable Schedule(TimeSpan delay, Action action) { var h = new Handle(this, delay, action); Pending.Add(h); History.Add(delay); return h; }
        public void FireAll() { foreach (var h in Pending.ToArray()) { Pending.Remove(h); h.Action(); } }
    }

    private static Window Plain() => new() { ShowInTaskbar = false, Width = 50, Height = 50, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = -3000 };

    [STAThread]
    public static int Main()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-CompletionTrayTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack; var app = new TestApp();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var oldNative = WindowActivation.Native; var oldSchedule = WindowActivation.Schedule; var oldTop = WindowActivation.TopmostFor;
        try
        {
            TrayListModel(); RecentList(); Balloons(); SettingsTests(root); Tooltips(); CheckBadge(); ControllerBehaviour(); UnseenCounter(root);
            FrontMostHelper();
            using var server = new FixtureServer();
            Integration(root, server);
            Console.WriteLine($"ALL COMPLETION TRAY TESTS PASSED ({passed})");
            return 0;
        }
        finally
        {
            WindowActivation.Native = oldNative; WindowActivation.Schedule = oldSchedule; WindowActivation.TopmostFor = oldTop;
            foreach (Window w in Application.Current.Windows.Cast<Window>().ToArray()) w.Close();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ------------------------------------------------------------------ tray list model
    private static void TrayListModel()
    {
        var list = new TrayWindowList(); int changes = 0; list.Changed += () => changes++;
        Check(list.Model().Count == 0, "Tray list: nothing open and nothing recent => empty model (block hidden)");
        Window opt = Plain(), prog = Plain(), done = Plain(), doneByState = Plain(); bool complete = false;
        list.Add(done, TrayWindowKind.Completed, () => "finished.zip");
        list.Add(prog, TrayWindowKind.Progress, () => "movie.mkv (42%)");
        list.Add(doneByState, TrayWindowKind.Progress, () => complete ? "state.iso" : "state.iso (99%)", () => complete);
        list.Add(opt, TrayWindowKind.Options, () => "setup.exe");
        var model = list.Model();
        Check(model.Select(e => e.Header).SequenceEqual(new[] { "\u2022 Download options: setup.exe", "\u2022 Downloading: movie.mkv (42%)", "\u2022 Downloading: state.iso (99%)", "\u2022 Finished: finished.zip", null }), "Tray list: capture dialogs, then progress windows, then finished ones, then a separator");
        complete = true;
        model = list.Model();
        Check(model.Select(e => e.Header).SequenceEqual(new[] { "\u2022 Download options: setup.exe", "\u2022 Downloading: movie.mkv (42%)", "\u2022 Finished: finished.zip", "\u2022 Finished: state.iso", null }) && model[3].Kind == TrayWindowKind.Completed, "Tray list: a progress window that reaches Complete moves to the finished entries (file name only)");
        var activated = new List<Window>(); list.Activate = w => activated.Add(w);
        model[2].Activate!(); model[3].Activate!();
        Check(activated.SequenceEqual(new[] { done, doneByState }), "Tray list: clicking a finished entry activates that window");
        Check(!list.HasPendingDialogs == false && list.RestorePendingDialog() && activated.Last() == opt, "Tray list: finished entries do not change the pending dialog rule");
        list.Remove(done); list.Remove(doneByState); list.Remove(prog); list.Remove(opt);
        Check(list.Count == 0 && list.Model().Count == 0 && changes >= 8, "Tray list: removing the windows empties the model again");
    }

    private static void RecentList()
    {
        var list = new TrayWindowList(); var opened = new List<string>(); var existing = new HashSet<string>();
        list.OpenFolder = opened.Add; list.FileExists = existing.Contains;
        int changes = 0; list.Changed += () => changes++;
        for (int i = 1; i <= 7; i++) { existing.Add($"C:\\d\\f{i}.bin"); list.Recent.Add($"f{i}.bin", $"C:\\d\\f{i}.bin"); }
        Check(list.Recent.Items.Count == 5 && list.Recent.Items[0].FileName == "f7.bin" && list.Recent.Items[4].FileName == "f3.bin", "Recent: at most five, newest first");
        list.Recent.Add("f4.bin", "C:\\d\\F4.bin"); // same path, different case
        Check(list.Recent.Items.Count == 5 && list.Recent.Items[0].FileName == "f4.bin" && list.Recent.Items.Count(i => i.FileName == "f4.bin") == 1, "Recent: the same path moves to the top instead of repeating");
        var model = list.Model();
        Check(model.Count == 8 && model[0].Header == "Recent downloads" && !model[0].Enabled && model[1].Kind == TrayWindowKind.Recent && model[6].Kind == TrayWindowKind.Clear && model[7].IsSeparator, "Recent: heading, five files, Clear, separator, under the (empty) window list");
        model[1].Activate!();
        Check(opened.SequenceEqual(new[] { "C:\\d\\F4.bin" }), "Recent: an item opens the folder of its file");
        existing.Remove("C:\\d\\f7.bin");
        Check(list.Model().Single(e => e.Header == "   f7.bin").Enabled == false && list.Model().Single(e => e.Header == "   f6.bin").Enabled, "Recent: an item whose file is gone is disabled");
        Window w = Plain(); list.Add(w, TrayWindowKind.Completed, () => "x.bin");
        var both = list.Model();
        Check(both[0].Header == "\u2022 Finished: x.bin" && both[1].IsSeparator && both[2].Header == "Recent downloads", "Recent: the block sits under the window list");
        list.Remove(w);
        int before = changes; model = list.Model(); model.Single(e => e.Kind == TrayWindowKind.Clear).Activate!();
        Check(list.Recent.Items.Count == 0 && list.Model().Count == 0 && changes > before, "Recent: Clear empties the list and hides the whole block");
        list.Recent.Clear();
        Check(list.Model().Count == 0, "Recent: clearing an empty list is harmless");
    }

    // ------------------------------------------------------------------ balloons
    private static void Balloons()
    {
        var shown = new List<(string Title, string Text, string? Job)>(); var timers = new FakeTimers(); var clock = new DateTime(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc); bool on = true;
        var b = new CompletionBalloons(() => on, (t, x, j) => shown.Add((t, x, j)), () => clock, timers.Schedule);
        b.Finished("a", "a.zip");
        Check(shown.Count == 1 && shown[0] == ("Download complete", "a.zip", "a"), "Balloon: one finished download shows 'Download complete' with the file name and the job to open");
        on = false; b.Finished("x", "off.zip"); on = true;
        Check(shown.Count == 1, "Balloon: nothing when the setting is off");
        clock += TimeSpan.FromSeconds(1); b.Finished("b", "b.zip"); clock += TimeSpan.FromSeconds(1); b.Finished("c", "c.zip");
        Check(shown.Count == 3 && timers.Pending.Count == 0, "Balloon: three finishes within ten seconds get one balloon each");
        clock += TimeSpan.FromSeconds(1); b.Finished("d", "d.zip"); clock += TimeSpan.FromSeconds(1); b.Finished("e", "e.zip");
        Check(shown.Count == 3 && timers.Pending.Count == 1, "Balloon: the fourth and fifth are held back for a summary");
        timers.FireAll();
        Check(shown.Count == 4 && shown[3] == ("Downloads complete", "5 downloads finished", null), "Balloon: one summary '5 downloads finished' replaces the individual ones");
        clock += TimeSpan.FromSeconds(30); b.Finished("f", "f.zip");
        Check(shown.Count == 5 && shown[4].Text == "f.zip", "Balloon: after a quiet spell a finish gets its own balloon again");
        Check(b.Shown == 5, "Balloon: the shown counter matches");
    }

    // ------------------------------------------------------------------ settings
    private static void SettingsTests(string root)
    {
        Check(new AppSettings().TrayNotifyOnComplete, "Settings: tray notification defaults to on");
        string dir = Path.Combine(root, "settings"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Theme\":\"Dark\",\"SettingsSchema\":4,\"TrayShowActiveCount\":false}");
        var old = AppSettings.Load(dir);
        Check(old.TrayNotifyOnComplete && old.Theme == "Dark" && !old.TrayShowActiveCount && old.SettingsSchema == AppSettings.CurrentSchema, "Settings: an old file without the key loads as on, other values kept, no schema bump");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"TrayNotifyOnComplete\":false}");
        Check(!AppSettings.Load(dir).TrayNotifyOnComplete, "Settings: an explicit off is kept");
        var s = new AppSettings { TrayNotifyOnComplete = false }; s.Save(dir);
        Check(!AppSettings.Load(dir).TrayNotifyOnComplete, "Settings: the value survives a save and load");
        var copy = new AppSettings(); copy.CopyFrom(s);
        Check(!copy.TrayNotifyOnComplete, "Settings: CopyFrom carries the value (Options dialog save)");
        Check(AppSettings.CurrentSchema == 4, "Settings: schema marker unchanged");
        var window = new SettingsWindow(new AppSettings { TrayNotifyOnComplete = false });
        var box = (System.Windows.Controls.CheckBox)window.FindName("TrayNotifyComplete");
        Check(box.IsChecked == false && ((string)box.Content).Replace("_", "") == "Show a tray notification when a download finishes", "Settings: Options > Downloads has 'Show a tray notification when a download finishes', reflecting the setting");
        window.Close();
    }

    // ------------------------------------------------------------------ tooltip
    private static void Tooltips()
    {
        var table = new (int Down, int Seed, double Speed, int Unseen, string Expected)[]
        {
            (0, 0, 0, 0, "NeoDM"), (2, 0, 0, 0, "NeoDM, 2 downloading"), (2, 0, 5.4 * 1048576, 0, "NeoDM, 2 downloading, 5.4 MB/s"),
            (2, 0, 0, 1, "NeoDM, 2 downloading, 1 finished"), (0, 0, 0, 3, "NeoDM, 3 finished"), (0, 1, 0, 2, "NeoDM, 1 seeding, 2 finished"),
            (2, 1, 5.4 * 1048576, 1, "NeoDM, 2 downloading, 1 seeding, 1 finished, 5.4 MB/s"), (0, 0, 900_000, 1, "NeoDM, 1 finished"),
        };
        foreach (var r in table) Check(TrayIconRenderer.Tooltip(r.Down, r.Seed, r.Speed, null, r.Unseen) == r.Expected, $"Tooltip {r.Down}/{r.Seed}/{r.Unseen} unseen is \"{r.Expected}\"");
        Check(TrayIconRenderer.Tooltip(1, 0, 0, null, 1_000_000_000).Length <= 127, "Tooltip with a finished count stays within 127 characters");
    }

    // ------------------------------------------------------------------ check badge
    private static readonly int[] Sizes = { 16, 20, 24, 32, 48 };
    private static IReadOnlyList<BitmapSource>? _frames;
    private static IReadOnlyList<BitmapSource> Frames => _frames ??= TrayIconRenderer.LoadAppIconFrames();
    private static Color BgraAt(byte[] data, int size, int x, int y) { int i = (y * size + x) * 4; return Color.FromArgb(data[i + 3], data[i + 2], data[i + 1], data[i]); }
    private static bool SameBytes(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
    private static bool Near(Color a, Color b, int tol) => Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;
    private static double Lum(Color c) { double F(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); } return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B); }
    private static double Contrast(Color a, Color b) { double x = Lum(a), y = Lum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

    private static void CheckBadge()
    {
        Check(Contrast(TrayIconRenderer.CheckFill, TrayIconRenderer.CheckMark) >= 4.5, "Check badge: white on the green fill reads at 4.5:1 or better");
        var accents = new[] { Color.FromRgb(0x2E, 0x9E, 0x4F), Colors.DodgerBlue, Colors.White, Colors.Black, Color.FromRgb(0xFF, 0x88, 0x00) };
        foreach (int size in Sizes)
        {
            var plain = TrayIconRenderer.ToBgra(TrayIconRenderer.RenderBase(Frames, size));
            var check = TrayIconRenderer.RenderCheck(Frames, size); var data = TrayIconRenderer.ToBgra(check);
            Check(check.PixelWidth == size && check.PixelHeight == size, $"Check badge {size} px: icon is {size}x{size}");
            Check(!SameBytes(plain, data) && SameBytes(data, TrayIconRenderer.ToBgra(TrayIconRenderer.RenderCheck(Frames, size))), $"Check badge {size} px: differs from the plain icon and is deterministic");
            foreach (var accent in accents)
                for (int count = 1; count <= 10; count += 3)
                    Check(!SameBytes(data, TrayIconRenderer.ToBgra(TrayIconRenderer.Render(Frames, count, new TrayBadgeColors(accent, Contrast(accent, Colors.White) >= Contrast(accent, Colors.Black) ? Colors.White : Colors.Black), size))), $"Check badge {size} px: differs from the count badge {count} on accent {accent}");
            int outer = TrayIconRenderer.BadgeDiameter(size), ring = TrayIconRenderer.RingWidth(size); double inner = outer - 2 * ring, bx = size - outer / 2.0, by = outer / 2.0;
            int fill = 0; double best = 0, ringBest = 0; long sx = 0, sy = 0;
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                var c = BgraAt(data, size, x, y); double d = Math.Sqrt((x + 0.5 - bx) * (x + 0.5 - bx) + (y + 0.5 - by) * (y + 0.5 - by));
                if (c.A == 255 && Near(Color.FromRgb(c.R, c.G, c.B), TrayIconRenderer.CheckFill, 2)) { fill++; sx += x; sy += y; }
                if (d <= inner / 2 - 0.5) best = Math.Max(best, Contrast(Color.FromRgb(c.R, c.G, c.B), TrayIconRenderer.CheckFill));
                if (d > inner / 2 - 0.4 && d < outer / 2 - 0.1 && c.A == 255) ringBest = Math.Max(ringBest, Contrast(Color.FromRgb(c.R, c.G, c.B), TrayIconRenderer.CheckFill));
            }
            Check(fill >= Math.PI * inner * inner / 4 * 0.2, $"Check badge {size} px: green fill pixels present ({fill})");
            Check((double)sx / fill >= size / 2.0 && (double)sy / fill < size / 2.0, $"Check badge {size} px: sits in the upper right quadrant like the count badge");
            Check(best >= 4.5, $"Check badge {size} px: the check mark reads against the green (best {best:0.0}:1)");
            Check(ringBest >= 3.0, $"Check badge {size} px: the ring reads against the green at 3:1 or better ({ringBest:0.0}:1, graphical object), so it separates from any accent");
            for (int y = size / 2 + 1; y < size; y++) for (int x = 0; x < size / 2 - 1; x++) if (BgraAt(data, size, x, y).A != BgraAt(plain, size, x, y).A) throw new Exception("FAIL: check badge touches the lower left at " + size);
            passed++; Console.WriteLine($"PASS: Check badge {size} px: lower left of the icon is untouched");
        }
    }

    private sealed class FakeTray : ITrayTarget
    {
        public System.Drawing.Icon? Icon; public string Text = ""; public int Icons, Tips;
        public void SetIcon(System.Drawing.Icon icon) { Icon = icon; Icons++; }
        public void SetTooltip(string text) { Text = text; Tips++; }
        public int Count(Color color, int tol = 3) { using var bmp = Icon!.ToBitmap(); int n = 0; for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++) { var c = bmp.GetPixel(x, y); if (c.A == 255 && Near(Color.FromRgb(c.R, c.G, c.B), color, tol)) n++; } return n; }
    }

    private static void ControllerBehaviour()
    {
        var target = new FakeTray(); var clock = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        using var controller = new TrayIconController(Frames, target, () => true, () => new TrayBadgeColors(Colors.DodgerBlue, Colors.White), () => 32, () => clock);
        controller.Update(0, 0, 0);
        Check(target.Icons == 1 && target.Count(TrayIconRenderer.CheckFill) == 0 && target.Text == "NeoDM", "Tray controller: idle shows the plain icon");
        clock += TimeSpan.FromSeconds(2); controller.Update(0, 0, 0, 1);
        Check(target.Icons == 2 && target.Count(TrayIconRenderer.CheckFill) > 20 && target.Text == "NeoDM, 1 finished", "Tray controller: nothing active and one unseen finished download => check badge and tooltip");
        clock += TimeSpan.FromSeconds(2); controller.Update(0, 0, 0, 2);
        Check(target.Icons == 2 && target.Text == "NeoDM, 2 finished", "Tray controller: a second unseen download changes the tooltip but not the icon");
        clock += TimeSpan.FromSeconds(2); controller.Update(2, 0, 0, 2);
        Check(target.Icons == 3 && target.Count(TrayIconRenderer.CheckFill) == 0 && target.Count(Colors.DodgerBlue, 6) > 20 && target.Text == "NeoDM, 2 downloading, 2 finished", "Tray controller: active downloads show the count badge (badge keeps its meaning), the finished count only goes into the tooltip");
        clock += TimeSpan.FromSeconds(2); controller.Update(0, 0, 0, 0);
        Check(target.Icons == 4 && target.Count(TrayIconRenderer.CheckFill) == 0 && target.Text == "NeoDM", "Tray controller: once seen, back to the plain icon and tooltip");
        clock += TimeSpan.FromSeconds(2); controller.Update(0, 0, 0, 1); controller.Refresh();
        Check(target.Count(TrayIconRenderer.CheckFill) > 20, "Tray controller: a theme refresh keeps the check badge");
    }

    // ------------------------------------------------------------------ unseen counter
    private static void UnseenCounter(string root)
    {
        var u = new UnseenFinished(); int changes = 0; u.Changed += () => changes++;
        u.Add(); u.Add(); Check(u.Count == 2, "Unseen: counts finished downloads");
        u.MarkSeen(); u.MarkSeen(); Check(u.Count == 0 && changes == 3, "Unseen: MarkSeen clears once (a second call raises nothing)");

        var m = new DownloadManager(Path.Combine(root, "unseen", "files"), Path.Combine(root, "unseen", "state"), new DownloadOptions());
        var main = new MainWindow(m, new AppSettings { DownloadDirectory = Path.Combine(root, "unseen", "files") }, () => { }, enableTray: false) { ShowInTaskbar = false };
        try
        {
            main.UnseenCompletions.Add(); main.UnseenCompletions.Add();
            main.Show(); Pump(200); RaiseActivated(main);
            Check(main.UnseenCompletions.Count == 0, "Unseen: showing and activating the main window clears the count");
            main.UnseenCompletions.Add(); main.BringToFront();
            Check(main.UnseenCompletions.Count == 0, "Unseen: opening the main window from the tray clears the count");
            main.UnseenCompletions.Add();
            var job = Sync(m.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/x.bin"), DateTimeOffset.UtcNow.AddHours(1)));
            var dialog = new DownloadCompleteWindow(job, new AppSettings(), () => { });
            main.UnseenCompletions.Add();
            dialog.Activated += (_, _) => main.UnseenCompletions.MarkSeen();
            RaiseActivated(dialog);
            Check(main.UnseenCompletions.Count == 0, "Unseen: activating a completion window clears the count");
            dialog.Close();
        }
        finally { main.Close(); }
    }
    private static void RaiseActivated(Window w) => typeof(Window).GetMethod("OnActivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, new object[] { EventArgs.Empty });

    // ------------------------------------------------------------------ front-most helper
    private static void FrontMostHelper()
    {
        var timers = new FakeTimers(); WindowActivation.Schedule = timers.Schedule; WindowActivation.TopmostFor = TimeSpan.FromSeconds(8);

        // refused foreground: the full sequence, the flash last, Topmost held
        var native = new FakeNative(); var w = Plain();
        var result = WindowActivation.Present(w, native);
        Check(result == ActivationResult.Flashed && w.IsVisible && w.Topmost && WindowActivation.HasTopmostLease(w), "Present: when Windows refuses the foreground the window is shown Topmost and the lease is held");
        Check(native.Calls.SequenceEqual(new[] { "allow", "attach", "top", "setfg", "alt", "setfg", "detach", "flash" }), "Present: sequence is allow, attach, top, setfg, alt tap, setfg again, detach, taskbar flash last: " + string.Join(",", native.Calls));
        Check(w.ShowActivated, "Present: ShowActivated is never turned off");
        Check(timers.Pending.Count == 1 && timers.Pending[0].Delay == TimeSpan.FromSeconds(8), "Present: Topmost is scheduled to end after 8 seconds");
        timers.FireAll();
        Check(!w.Topmost && !WindowActivation.HasTopmostLease(w), "Present: the timer clears Topmost, the window is an ordinary one afterwards");
        w.Close();

        // interaction ends the lease, each kind
        foreach (var kind in new[] { "mouse enter", "mouse down", "key", "activation" })
        {
            native = new FakeNative(); timers.Pending.Clear(); w = Plain(); WindowActivation.Present(w, native);
            Check(w.Topmost, $"Present ({kind}): Topmost is on before the interaction");
            switch (kind)
            {
                case "mouse enter": w.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent }); break;
                case "mouse down": w.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent }); break;
                case "key": w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, Key.A) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); break;
                default: RaiseActivated(w); break;
            }
            Check(!w.Topmost && !WindowActivation.HasTopmostLease(w) && timers.Pending.Count == 0, $"Present ({kind}): Topmost ends at once and the timer is cancelled");
            w.Close();
        }

        // foreground granted: nothing to hold, no flash
        native = new FakeNative { AllowForegroundSwitch = true }; timers.Pending.Clear(); w = Plain();
        result = WindowActivation.Present(w, native);
        Check(result == ActivationResult.Foreground && !w.Topmost && !native.Calls.Contains("flash") && !native.Calls.Contains("alt") && timers.Pending.Count == 0, "Present: when the foreground is granted Topmost is dropped at once and there is no flash");
        w.Close();

        // minimised window is restored
        native = new FakeNative(); w = Plain(); w.Show(); w.WindowState = WindowState.Minimized; native.Minimized = true;
        WindowActivation.Present(w, native);
        Check(w.WindowState == WindowState.Normal && native.Calls[0] == "restore", "Present: a minimised window is restored first (WPF state and Win32)");
        w.Close();

        // re-present restarts the lease instead of stacking
        native = new FakeNative(); timers.Pending.Clear(); w = Plain();
        WindowActivation.Present(w, native); WindowActivation.Present(w, native);
        Check(timers.Pending.Count == 1 && w.Topmost, "Present: presenting twice keeps one lease and one timer");
        w.Close(); Check(!w.Topmost && timers.Pending.Count == 0, "Present: closing the window ends the lease");

        // full-screen app: skipped, shown when the user leaves it
        native = new FakeNative { Busy = true }; timers.Pending.Clear(); w = Plain();
        result = WindowActivation.Present(w, native);
        Check(result == ActivationResult.Deferred && !w.IsVisible && !w.Topmost && native.Calls.Count == 0 && WindowActivation.DeferredCount == 1, "Fullscreen: the pop-up is skipped (not shown, no focus calls) and queued");
        timers.FireAll();
        Check(!w.IsVisible && WindowActivation.DeferredCount == 1 && timers.Pending.Count == 1 && timers.Pending[0].Delay == WindowActivation.BusyPoll, "Fullscreen: still in full screen => keeps waiting");
        native.Busy = false; timers.FireAll();
        Check(w.IsVisible && w.Topmost && WindowActivation.DeferredCount == 0 && native.Calls.Contains("setfg"), "Fullscreen: when the user leaves it the window is presented");
        w.Close();
        native = new FakeNative { Busy = true }; w = Plain(); WindowActivation.Present(w, native); w.Close(); native.Busy = false; timers.FireAll();
        Check(!w.IsVisible && native.Calls.Count == 0, "Fullscreen: a window closed while waiting is not resurrected");

        // WhenClear
        native = new FakeNative(); int ran = 0; WindowActivation.WhenClear(() => ran++, native);
        Check(ran == 1, "WhenClear: runs at once when no full-screen app is in front");

        // existing activation sequence untouched when the fake has no extras
        WindowActivation.Schedule = oldScheduleDefault;
    }
    private static readonly Func<TimeSpan, Action, IDisposable> oldScheduleDefault = WindowActivation.Schedule;
    private sealed class FakeSource : PresentationSource
    {
        protected override CompositionTarget GetCompositionTargetCore() => null!;
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
    }

    // ------------------------------------------------------------------ real manager + main window
    private static DownloadJob Held(DownloadManager m, string url, long kib)
    {
        var job = Sync(m.AddScheduledAsync(new DownloadRequest(url), DateTimeOffset.UtcNow.AddHours(1)));
        m.SetSpeedLimit(job.Id, kib * 1024); return job;
    }
    private static DownloadJob Snap(DownloadManager m, string id) => m.Jobs.Single(j => j.Id == id);
    private static void Go(DownloadManager m, string id) => m.Schedule(id, DateTimeOffset.UtcNow.AddSeconds(-1));
    private static void Finish(DownloadManager m, string id) { m.SetSpeedLimit(id, null); PumpUntil(() => Snap(m, id).Status == "Completed", "job completes", 40); }
    private static int Dialogs() => Application.Current.Windows.OfType<DownloadCompleteWindow>().Count(d => d.IsVisible);

    private static void Integration(string root, FixtureServer server)
    {
        var timers = new FakeTimers(); WindowActivation.Schedule = timers.Schedule;
        var native = new FakeNative(); WindowActivation.Native = native;

        // already complete at app start: no window, no balloon, nothing in the lists
        var m = new DownloadManager(Path.Combine(root, "int", "files"), Path.Combine(root, "int", "state"), new DownloadOptions { RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(4), MinimumSegmentSize = 65536 });
        var old = Held(m, server.BaseUrl + "/range?ct-old", 1024); Go(m, old.Id); Finish(m, old.Id);
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "int", "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = true, TrayNotifyOnComplete = true };
        var main = new MainWindow(m, settings, () => { }, enableTray: false, new NoPower()) { ShowInTaskbar = false };
        var balloons = new List<(string, string)>(); main.BalloonSink = (t, x) => balloons.Add((t, x));
        main.PlayCompletionSound = () => { }; main.JobCompletion = new JobCompletionStore(Path.Combine(root, "int", "job-completion.json"));
        main.Show(); Pump(1200);
        try
        {
            Check(main.ProgressWindowCount == 0 && Dialogs() == 0 && balloons.Count == 0 && main.TrayWindows.Recent.Items.Count == 0 && main.UnseenCompletions.Count == 0 && main.TrayWindows.Model().Count == 0, "Start: a download that was already complete creates no window, no balloon, no tray entry");

            // 1. a download finishes while NeoDM is in the background
            var a = Held(m, server.BaseUrl + "/range?ct-a", 1024); Go(m, a.Id);
            Finish(m, a.Id);
            PumpUntil(() => main.HasProgressWindow(a.Id), "completion window opens");
            Pump(600);
            string name = Snap(m, a.Id).FileName;
            var window = main.OpenProgressWindow(a.Id, false)!;
            Check(main.ProgressWindowCount == 1 && Dialogs() == 0, "Finish: the completion window is created once (one progress window, no extra dialog)");
            Check(window.IsVisible && native.Calls.Contains("setfg") && native.Calls.Last() == "flash" && timers.History.Contains(TimeSpan.FromSeconds(8)), "Finish: shown through the front-most helper, a Topmost lease was started because Windows refuses the foreground (WPF may end it at once when the window really activates), taskbar flash as the last step");
            Check(main.TrayWindows.Model().Any(e => e.Header == "\u2022 Finished: " + name && e.Kind == TrayWindowKind.Completed) && !main.TrayWindows.Model().Any(e => e.Header?.StartsWith("\u2022 Downloading") == true), "Finish: the tray window list has the '\u2022 Finished: <name>' entry");
            Check(balloons.Count == 1 && balloons[0] == ("Download complete", name), "Finish: the balloon is raised exactly once with the file name");
            Check(main.TrayWindows.Recent.Items.Count == 1 && main.TrayWindows.Recent.Items[0].FilePath == Snap(m, a.Id).FilePath, "Finish: the recent list has the file");
            Check(main.TrayWindows.Model().Any(e => e.Kind == TrayWindowKind.Recent && e.Enabled && e.Header?.Trim() == name), "Finish: the recent entry is enabled while the file exists");
            Pump(1500);
            Check(balloons.Count == 1 && main.ProgressWindowCount == 1, "Finish: no second balloon or window on later refreshes");
            // already open and covered: another presentation brings it to the front again
            native.Calls.Clear(); main.OpenFromBalloon();
            Check(native.Calls.Contains("setfg") && main.ProgressWindowCount == 1, "Balloon click: activates the same completion window through the helper");
            window.Close(); Pump(200);
            Check(!main.TrayWindows.Model().Any(e => e.Kind == TrayWindowKind.Completed) && main.TrayWindows.Recent.Items.Count == 1, "Finish: closing the window removes the entry, the recent list stays");
            File.Delete(Snap(m, a.Id).FilePath);
            Check(main.TrayWindows.Model().Any(e => e.Kind == TrayWindowKind.Recent && !e.Enabled && e.Header?.Trim() == name), "Finish: the recent entry is disabled once the file is deleted");

            // 2. setting off: no balloon, window still appears
            settings.TrayNotifyOnComplete = false; balloons.Clear();
            var b = Held(m, server.BaseUrl + "/range?ct-b", 1024); Go(m, b.Id); Finish(m, b.Id); PumpUntil(() => main.HasProgressWindow(b.Id), "window b"); Pump(300);
            Check(balloons.Count == 0 && main.TrayWindows.Recent.Items.Count == 2, "Setting off: no balloon, the recent list still records the file");
            main.OpenProgressWindow(b.Id, false)!.Close(); Pump(200); settings.TrayNotifyOnComplete = true;

            // 3. window option off, dialog on: the complete dialog is one window, topmost, listed in the tray
            settings.ShowProgressOnCompletion = false; settings.ShowCompletionDialog = true; balloons.Clear(); native.Calls.Clear();
            var c = Held(m, server.BaseUrl + "/range?ct-c", 1024); Go(m, c.Id); Finish(m, c.Id); PumpUntil(() => Dialogs() == 1, "dialog c"); Pump(300);
            var dialog = Application.Current.Windows.OfType<DownloadCompleteWindow>().Single(d => d.IsVisible);
            Check(Dialogs() == 1 && main.ProgressWindowCount == 0 && dialog.Owner is null && dialog.ShowInTaskbar && dialog.ShowActivated, "Dialog: one completion dialog, no owner, own taskbar button, shown activated");
            Check(timers.History.Count(t => t == TimeSpan.FromSeconds(8)) >= 2 && native.Calls.Contains("setfg") && native.Calls.Last() == "flash" && balloons.Count == 1, "Dialog: presented through the helper (a second Topmost lease, flash last) and one balloon");
            string cname = Snap(m, c.Id).FileName;
            Check(main.TrayWindows.Model().Any(e => e.Header == "\u2022 Finished: " + cname), "Dialog: listed as Finished in the tray");
            dialog.Close(); Pump(300);
            Check(!main.TrayWindows.Model().Any(e => e.Kind == TrayWindowKind.Completed), "Dialog: closing removes the tray entry");

            // 4. full-screen app in front: no pop-up, balloon still raised, window appears when the user leaves
            settings.ShowProgressOnCompletion = true; settings.ShowCompletionDialog = false; balloons.Clear(); native.Calls.Clear(); native.Busy = true;
            var d = Held(m, server.BaseUrl + "/range?ct-d", 1024); Go(m, d.Id); Finish(m, d.Id); Pump(1300);
            Check(!main.HasProgressWindow(d.Id) && Dialogs() == 0 && native.Calls.Count == 0 && balloons.Count == 1 && WindowActivation.DeferredCount >= 1, "Fullscreen: no window while a full-screen app is in front, the balloon is still raised");
            native.Busy = false; timers.FireAll(); Pump(300);
            Check(main.HasProgressWindow(d.Id) && main.ProgressWindowCount == 1 && native.Calls.Contains("setfg"), "Fullscreen: the completion window appears once the user has left it");
            main.OpenProgressWindow(d.Id, false)!.Close(); Pump(200);
            Check(main.TrayWindows.Recent.Items.Count == 4, "Recent list holds the session's finished downloads");
            main.TrayWindows.Recent.Clear();
            Check(main.TrayWindows.Model().Count == 0, "Recent list: Clear hides the block");
        }
        finally
        {
            WindowActivation.Native = new FakeNative(); main.Close();
            foreach (Window w in Application.Current.Windows.Cast<Window>().ToArray()) w.Close();
        }
    }
    private sealed class NoPower : IPowerActions { public void Execute(CompletionActionKind kind, bool force) { } }
}
