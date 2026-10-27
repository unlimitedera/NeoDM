using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.Core;

internal sealed class TestApp : NDM.App.App
{
    public TestApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
    protected override void OnStartup(StartupEventArgs e) { }
    public void Theme(string theme) => Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/NeoDM;component/Themes/{theme}.xaml") };
}

/// <summary>1.3.0/1.3.1: themed tabs, menus/scroll bars/combo boxes, contrast, layout at 100/125/150 %, completion sound, 'Remember' placement, activation helper, capture reply.</summary>
internal static partial class ReleaseTests
{
    internal static int Passed;
    internal static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        Passed++; Console.WriteLine("PASS: " + name);
    }
    internal static void Pump(int milliseconds = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    internal static void PumpUntil(Func<bool> condition, string what, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException(what); Pump(40); }
    }
    internal static T Sync<T>(Task<T> task) { PumpUntil(() => task.IsCompleted, "async operation"); return task.GetAwaiter().GetResult(); }
    internal static IEnumerable<T> Visual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Visual<T>(child)) yield return nested;
        }
    }
    internal static IEnumerable<T> Logical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Logical<T>(child)) yield return nested;
        }
    }

    // ---- colour helpers ----
    internal static Color Brush(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;
    private static double Channel(byte c) { double s = c / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
    internal static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    internal static double Contrast(Color a, Color b) { double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
    private static bool Near(Color a, Color b, int tolerance = 10) => Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;
    private static bool NearWhite(Color c) => c.R >= 0xDC && c.G >= 0xDC && c.B >= 0xDC;
    private static Color Px(BitmapSource bitmap, int x, int y)
    {
        var px = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }
    internal static RenderTargetBitmap Render(FrameworkElement element, double width, double height, double scale = 1.0, bool layout = true)
    {
        if (layout) { element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout(); }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element); return bitmap;
    }

    /// <summary>Pixels of an element exactly as laid out (VisualBrush avoids the offset quirk of RenderTargetBitmap.Render on a child element).</summary>
    internal static RenderTargetBitmap Snapshot(FrameworkElement element)
    {
        int w = (int)Math.Ceiling(element.ActualWidth), h = (int)Math.Ceiling(element.ActualHeight);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen()) ctx.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); return bitmap;
    }

    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new TestApp();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); // like the running app: async continuations return to the UI thread
            string root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "neodm-release-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            try
            {
                SettingsAndSound(root);
                CaptureReply(root);
                ActivationTests();
                foreach (var theme in ThemeCatalog.Concrete.Select(x => x.Id)) { app.Theme(theme); ThemeTests(app, theme, root); TabTests(app, theme, root); }
                app.Theme("Light");
                LayoutTests(app, root);
                SettingsRememberTests(root);
                UpdateTests(app, root);
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            Console.WriteLine($"Release 1.3.1 tests passed: {Passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    // ===================== 6 + 7: completion sound, schema 3, 'Remember' placement =====================
    private static DownloadManager NewManager(string root, string label) =>
        new(Path.Combine(root, label, "files"), Path.Combine(root, label, "state"), new DownloadOptions { RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(4) });

    private static void SettingsAndSound(string root)
    {
        // new installs
        Check(new AppSettings().PlayCompletionSound && AppSettings.CurrentSchema == 7 && new AppSettings().SettingsSchema == 7, "Completion sound defaults to ON for new installs (schema 4)");
        var dir = Path.Combine(root, "settings"); Directory.CreateDirectory(dir);
        Check(AppSettings.Load(dir).PlayCompletionSound, "No settings file -> sound on");
        // existing users: schema 2 file with the old default 'false' is switched once
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"SettingsSchema\":2,\"PlayCompletionSound\":false,\"Theme\":\"Dark\"}");
        var migrated = AppSettings.Load(dir);
        Check(migrated.PlayCompletionSound && migrated.SettingsSchema == 7 && migrated.Theme == "Dark", "Existing schema-2 settings: sound switched on once, other values kept");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"PlayCompletionSound\":false}");
        Check(AppSettings.Load(dir).PlayCompletionSound, "Existing file without any marker: sound on");
        // afterwards the user's choice is respected
        migrated.PlayCompletionSound = false; migrated.Save(dir);
        var again = AppSettings.Load(dir);
        Check(!again.PlayCompletionSound && again.SettingsSchema == 7, "After the migration a saved 'off' is respected");
        again.PlayCompletionSound = true; again.Save(dir); Check(AppSettings.Load(dir).PlayCompletionSound, "A saved 'on' stays on");
        // 1.3.1: "open the download window when a download finishes" (schema 4)
        Check(new AppSettings().ShowProgressOnCompletion, "Open-window-on-completion defaults to ON for new installs");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"SettingsSchema\":3,\"ShowProgressOnCompletion\":false,\"Theme\":\"Dark\"}");
        var windowMigrated = AppSettings.Load(dir);
        Check(windowMigrated.ShowProgressOnCompletion && windowMigrated.SettingsSchema == 7 && windowMigrated.Theme == "Dark", "Existing schema-3 settings get 'open the download window' switched on once, other values kept");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Theme\":\"Light\"}");
        Check(AppSettings.Load(dir).ShowProgressOnCompletion, "An old settings file without the option gets it on");
        windowMigrated.ShowProgressOnCompletion = false; windowMigrated.Save(dir);
        Check(!AppSettings.Load(dir).ShowProgressOnCompletion, "After the migration a saved 'off' is respected");
        Check(!windowMigrated.Copy().ShowProgressOnCompletion, "Settings copy carries the option");
        Check(CompletionSound.ShouldShowWindow(new AppSettings { ShowProgressOnCompletion = true }, null) && !CompletionSound.ShouldShowWindow(new AppSettings { ShowProgressOnCompletion = false }, null)
            && !CompletionSound.ShouldShowWindow(new AppSettings { ShowProgressOnCompletion = true }, new JobCompletionOptions { ShowWindow = false }) && CompletionSound.ShouldShowWindow(new AppSettings { ShowProgressOnCompletion = false }, new JobCompletionOptions { ShowWindow = true }), "Per-download 'show this window' overrides the global setting both ways; unset follows it");

        // Options window checkbox is bound to the setting and saves
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "files") };
        var options = new SettingsWindow(settings, dir);
        var box = (CheckBox)options.FindName("CompletionSound");
        Check(box.IsChecked == true, "Options shows the sound checkbox ticked by default");
        box.IsChecked = false; ((Button)options.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!settings.PlayCompletionSound && !AppSettings.Load(dir).PlayCompletionSound, "Options can switch the sound off (persisted)");

        // per-job decision
        Check(CompletionSound.ShouldPlay(new AppSettings { PlayCompletionSound = true }, null) && !CompletionSound.ShouldPlay(new AppSettings { PlayCompletionSound = false }, null), "No per-job choice: the global setting decides");
        Check(!CompletionSound.ShouldPlay(new AppSettings { PlayCompletionSound = true }, new JobCompletionOptions { PlaySound = false }) && CompletionSound.ShouldPlay(new AppSettings { PlayCompletionSound = false }, new JobCompletionOptions { PlaySound = true }), "A per-job choice overrides the global one both ways");

        // job store: PlaySound persists; only remembered options are written to disk
        string storePath = Path.Combine(root, "store", "job-completion.json");
        var store = new JobCompletionStore(storePath);
        store.Set("a", new JobCompletionOptions { PlaySound = false, Remember = true });
        store.Set("b", new JobCompletionOptions { PlaySound = true, Remember = false });
        var reloaded = new JobCompletionStore(storePath);
        Check(reloaded.Get("a") is { PlaySound: false } && reloaded.Get("b") is null, "Per-job sound choice persists when remembered; unremembered choices stay in memory only");
        Check(store.Get("b") is { PlaySound: true }, "Unremembered choice is still live in this run");

        // progress window tab
        using var manager = NewManager(root, "tab");
        var job = Sync(manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/never"), DateTimeOffset.UtcNow.AddDays(1)));
        var live = new JobCompletionStore();
        var window = new DownloadProgressWindow(manager, job.Id, live, new AppSettings { PlayCompletionSound = true }) { ShowInTaskbar = false };
        window.Show(); Pump();
        var sound = (CheckBox)window.FindName("PlaySoundBox"); var remember = (CheckBox)window.FindName("RememberBox");
        Check(sound.IsChecked == true && sound.IsEnabled, "Options-on-completion tab has 'Play sound when done', defaulting to the global setting (on)");
        Check(live.Get(job.Id) is null, "Merely opening the tab stores nothing (the job keeps following the global setting)");
        Check(remember.Content.ToString() == "Remember this choice for future sessions", "The temporary per-download choices keep the 'Remember this choice for future sessions' checkbox");
        sound.IsChecked = false; Pump();
        Check(live.Get(job.Id) is { PlaySound: false, Remember: false }, "Unticking stores a per-job mute");
        remember.IsChecked = true; Pump(); Check(live.Get(job.Id) is { Remember: true }, "Remember flag is stored per job");
        window.Close();
        var window2 = new DownloadProgressWindow(manager, job.Id, live, new AppSettings { PlayCompletionSound = true }) { ShowInTaskbar = false }; window2.Show(); Pump();
        Check(((CheckBox)window2.FindName("PlaySoundBox")).IsChecked == false, "The tab shows the stored per-job choice when reopened");
        window2.Close();
        var muted = new DownloadProgressWindow(manager, job.Id, new JobCompletionStore(), new AppSettings { PlayCompletionSound = false }) { ShowInTaskbar = false }; muted.Show(); Pump();
        Check(((CheckBox)muted.FindName("PlaySoundBox")).IsChecked == false, "Default follows the global setting when it is off too");
        muted.Close();

        // real pipeline: a finished download plays the sound once; a muted job does not
        using var server = new LocalServer(ctx => { var body = new byte[200_000]; ctx.Response.ContentLength64 = body.Length; ctx.Response.OutputStream.Write(body); ctx.Response.Close(); });
        using var m2 = NewManager(root, "pipeline");
        var s2 = new AppSettings { DownloadDirectory = Path.Combine(root, "pipeline", "files"), ShowCompletionDialog = false, PlayCompletionSound = true };
        var main = new MainWindow(m2, s2, () => { }, enableTray: false) { ShowInTaskbar = false };
        int plays = 0; main.PlayCompletionSound = () => plays++;
        main.JobCompletion = new JobCompletionStore();
        main.Show(); Pump();
        var loud = Sync(m2.AddScheduledAsync(new DownloadRequest(server.Url + "loud.bin"), DateTimeOffset.UtcNow.AddHours(1)));
        var quiet = Sync(m2.AddScheduledAsync(new DownloadRequest(server.Url + "quiet.bin"), DateTimeOffset.UtcNow.AddHours(1)));
        main.JobCompletion.Set(quiet.Id, new JobCompletionOptions { ShowDialog = false, PlaySound = false });
        m2.Schedule(loud.Id, DateTimeOffset.UtcNow); PumpUntil(() => m2.Jobs.Single(j => j.Id == loud.Id).Status == "Completed", "loud download completes");
        PumpUntil(() => plays >= 1, "sound played for the finished download");
        Check(plays == 1, "A finished download plays the completion sound through the normal completion pipeline");
        m2.Schedule(quiet.Id, DateTimeOffset.UtcNow); PumpUntil(() => m2.Jobs.Single(j => j.Id == quiet.Id).Status == "Completed", "quiet download completes"); Pump(800);
        Check(plays == 1, "A job muted in its own options does not play the sound");
        main.Close();
    }

    private static void SettingsRememberTests(string root)
    {
        var dir = Path.Combine(root, "remember"); Directory.CreateDirectory(dir);
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "remember", "files") };
        var w = new SettingsWindow(settings, dir); w.Show(); Pump();
        Check(w.FindName("FinishRemember") is null, "Options no longer has the 'Remember this choice' checkbox");
        var texts = Visual<TextBlock>(w).Select(t => t.Text).Concat(Visual<ContentControl>(w).Select(c => c.Content as string ?? "")).ToList();
        Check(!texts.Any(t => t.Contains("Remember this choice")) && !texts.Any(t => t.Contains("applies to the current run of NeoDM only")), "No 'Remember' wording (checkbox or explanation) anywhere in Options, on any tab");
        var tabs = (TabControl)w.FindName("OptionsTabs");
        for (int i = 0; i < tabs.Items.Count; i++) { tabs.SelectedIndex = i; w.UpdateLayout(); }
        var combo = (ComboBox)w.FindName("FinishAction");
        combo.SelectedIndex = CompletionActionInfo.All.ToList().IndexOf(CompletionActionKind.Sleep);
        ((Button)w.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var disk = AppSettings.Load(dir);
        Check(settings.CompletionAction == CompletionActionKind.Sleep && settings.SessionCompletionAction == CompletionActionKind.Sleep && disk.CompletionAction == CompletionActionKind.Sleep && disk.SessionCompletionAction == CompletionActionKind.Sleep, "The 'When all downloads finish' action chosen in Options is saved permanently and is active after the next start");
        var w2 = new SettingsWindow(settings, dir);
        ((ComboBox)w2.FindName("FinishAction")).SelectedIndex = CompletionActionInfo.All.ToList().IndexOf(CompletionActionKind.None);
        ((Button)w2.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(AppSettings.Load(dir).CompletionAction == CompletionActionKind.None && AppSettings.Load(dir).SessionCompletionAction == CompletionActionKind.None, "Changing it back in Options is permanent too");
    }

    // ===================== 2: capture reply carries 'cancelled'; activation helper =====================
    private static void CaptureReply(string root)
    {
        foreach (var (response, expectCancelled) in new[] { (new NativeBridge.BrowserDownloadResponse(false, null, "Download cancelled in NeoDM.", Cancelled: true), true), (new NativeBridge.BrowserDownloadResponse(false, null, "disk full"), false) })
        {
            string pipe = "NDM.Test." + Guid.NewGuid().ToString("N");
            using var bridge = new NativeBridge(request => request.Respond(response), pipe); bridge.Start();
            var reply = Task.Run(async () =>
            {
                using var client = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                await client.ConnectAsync(5000);
                await client.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a.zip\"}\n"));
                var bytes = await NativeBridge.ReadRequestAsync(client, new CancellationTokenSource(5000).Token);
                return System.Text.Json.JsonDocument.Parse(bytes).RootElement.Clone();
            }).GetAwaiter().GetResult();
            Check(reply.GetProperty("ok").GetBoolean() == false && reply.GetProperty("cancelled").GetBoolean() == expectCancelled && reply.GetProperty("error").GetString()!.Length > 0, expectCancelled ? "App reply for the user's Cancel has cancelled:true (the extension discards)" : "App reply for a hard failure has cancelled:false (the extension restarts the browser download)");
        }
        var dialog = new DownloadOptionsDialog("https://example.org/a.zip", "a.zip", "", "", Path.Combine(root, "dl"));
        dialog.Show(); Pump();
        Check(dialog.FindName("CaptureNote") is TextBlock { Text: var note } && note.Contains("Cancel discards"), "Capture dialog explains that Cancel discards the download");
        dialog.Close();
    }

    private sealed class FakeNative : IWindowNative
    {
        public IntPtr Foreground = new(100); public bool ForegroundAcceptsSet = true, AltUnlocks, Minimized;
        public List<string> Calls = new();
        public IntPtr ForegroundWindow() => Foreground;
        public uint ThreadOf(IntPtr window) => 77; public uint CurrentThread() => 11;
        public bool IsMinimized(IntPtr window) => Minimized;
        public void Restore(IntPtr window) { Calls.Add("restore"); Minimized = false; }
        public bool AttachThreadInput(uint from, uint to, bool attach) { Calls.Add(attach ? "attach" : "detach"); return true; }
        public bool SetForeground(IntPtr window) { Calls.Add("setfg"); if (ForegroundAcceptsSet) { Foreground = window; return true; } return false; }
        public void BringToTop(IntPtr window) => Calls.Add("top");
        public void TapAlt() { Calls.Add("alt"); if (AltUnlocks) ForegroundAcceptsSet = true; }
        public void Flash(IntPtr window) => Calls.Add("flash");
    }
    private static void ActivationTests()
    {
        var h = new IntPtr(500);
        var n = new FakeNative { Foreground = h };
        Check(WindowActivation.Activate(h, n) == ActivationResult.Foreground && !n.Calls.Contains("attach") && !n.Calls.Contains("flash"), "Activation: already in front -> nothing to do");
        n = new FakeNative();
        Check(WindowActivation.Activate(h, n) == ActivationResult.Foreground && n.Calls.SequenceEqual(new[] { "attach", "top", "setfg", "detach" }), "Activation: attaches to the foreground window's input queue, brings to front, detaches");
        n = new FakeNative { Minimized = true };
        WindowActivation.Activate(h, n); Check(n.Calls[0] == "restore", "Activation: a minimised window is restored first");
        n = new FakeNative { ForegroundAcceptsSet = false, AltUnlocks = true };
        Check(WindowActivation.Activate(h, n) == ActivationResult.Foreground && n.Calls.Contains("alt") && !n.Calls.Contains("flash"), "Activation: when refused, the Alt-key tap unlocks the second attempt");
        n = new FakeNative { ForegroundAcceptsSet = false, AltUnlocks = false };
        Check(WindowActivation.Activate(h, n) == ActivationResult.Flashed && n.Calls.Contains("flash") && n.Calls.Last() == "flash" && n.Calls.Count(c => c == "attach") == n.Calls.Count(c => c == "detach"), "Activation: if Windows still refuses, the taskbar button flashes (and input queues are detached)");
        Check(WindowActivation.Activate(IntPtr.Zero, new FakeNative()) == ActivationResult.NoHandle, "Activation: no window handle is reported, not thrown");

        // WPF side: restores a minimised window, goes Topmost briefly, then back to normal
        var window = new Window { Width = 200, Height = 120, ShowInTaskbar = true, WindowState = WindowState.Minimized };
        window.Show(); Pump();
        var fake = new FakeNative(); WindowActivation.BringToFront(window, fake); 
        Check(window.WindowState == WindowState.Normal && window.Topmost && fake.Calls.Contains("setfg"), "BringToFront: window restored and topmost while it is being raised");
        PumpUntil(() => !window.Topmost, "topmost is dropped afterwards"); Check(!window.Topmost, "BringToFront: Topmost only lasts briefly, then it is an ordinary window");
        window.Close();
    }

    // ===================== 1.3.1: themed tabs (progress window + Options window) =====================
    private static double MaxContrastIn(BitmapSource bitmap, Color background, Int32Rect area)
    {
        double best = 1; var pixels = new byte[area.Width * area.Height * 4]; bitmap.CopyPixels(area, pixels, area.Width * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4) best = Math.Max(best, Contrast(Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]), background));
        return best;
    }
    private static void TabChecks(string t, bool dark, TabControl tabs, string label)
    {
        t += label + ": ";
        Color sidebar = Brush("SidebarBrush"), surface = Brush("SurfaceBrush"), ink = Brush("InkBrush"), muted = Brush("MutedBrush"), accent = Brush("AccentBrush");
        var items = tabs.Items.OfType<TabItem>().ToList();
        Check(items.Count >= 3, t + "tab control has its tabs");
        var selected = items.First(i => i.IsSelected);
        var frame = (Border)tabs.Template.FindName("ContentFrame", tabs);
        Check(((SolidColorBrush)frame.Background).Color == surface && ((SolidColorBrush)frame.BorderBrush).Color == Brush("BorderBrush"), t + "content border uses the theme surface and border colours");
        foreach (var item in items)
        {
            var shot = Snapshot(item); int w = shot.PixelWidth, h = shot.PixelHeight;
            bool isSelected = item.IsSelected; var bg = isSelected ? surface : sidebar; var fg = ((SolidColorBrush)item.Foreground).Color;
            var corner = Px(shot, w / 2, 3);
            Check(Near(corner, bg) && !(dark && NearWhite(corner)), t + $"'{item.Header}' header background is the theme {(isSelected ? "surface" : "sidebar")} colour" + (dark ? " (no near-white)" : ""));
            bool enabled = item.IsEnabled; var expectedFg = !enabled ? Brush("DisabledInkBrush") : isSelected ? ink : muted;
            Check(fg == expectedFg, t + $"'{item.Header}' text colour is the theme {(!enabled ? "dim disabled" : isSelected ? "ink (selected)" : "muted (unselected)")} colour");
            Check(Contrast(fg, bg) >= (enabled ? 4.5 : 3.0), t + $"'{item.Header}' text contrast {Contrast(fg, bg):0.0}:1 on its header background >= 4.5:1");
            var text = Visual<TextBlock>(item).First(); var textColor = ((SolidColorBrush)text.Foreground).Color;
            Check(textColor == fg, t + $"'{item.Header}' header text is painted with the tab foreground (not a default system colour)");
            Check(MaxContrastIn(shot, bg, new Int32Rect(8, 5, w - 16, h - 9)) >= (enabled ? 4.5 : 3.0), t + $"'{item.Header}' rendered header text reaches >= 4.5:1 against its background");
            if (isSelected) { var underline = Px(shot, w / 2, h - 2); Check(Near(underline, accent), t + "selected tab has the accent underline"); }
        }
        // hover / disabled / focus come from the template, themed (IsMouseOver cannot be forced in a test, so the template's triggers are verified)
        var style = (Style)Application.Current.FindResource(typeof(TabItem));
        var template = (ControlTemplate)style.Setters.OfType<Setter>().First(s => s.Property == Control.TemplateProperty).Value;
        string KeyOf(Trigger tr, DependencyProperty p) => ((DynamicResourceExtension)tr.Setters.OfType<Setter>().First(s => s.Property == p).Value).ResourceKey.ToString()!;
        var hover = template.Triggers.OfType<Trigger>().First(x => x.Property == UIElement.IsMouseOverProperty);
        Check(KeyOf(hover, Control.BackgroundProperty) == "SurfaceHoverBrush" && KeyOf(hover, Control.ForegroundProperty) == "InkBrush" && Contrast(ink, Brush("SurfaceHoverBrush")) >= 4.5, t + "hover: subtle themed highlight with full-contrast text (>= 4.5:1)");
        var off = template.Triggers.OfType<Trigger>().First(x => x.Property == UIElement.IsEnabledProperty);
        Check(KeyOf(off, Control.ForegroundProperty) == "DisabledInkBrush" && Contrast(Brush("DisabledInkBrush"), sidebar) >= 3.0, t + "disabled tab text is the dim themed colour and stays readable (>= 3:1)");
        Check(style.Setters.OfType<Setter>().Any(s => s.Property == FrameworkElement.FocusVisualStyleProperty && ReferenceEquals(s.Value, Application.Current.FindResource("TabFocusRing"))), t + "keyboard focus uses the themed focus ring");
    }

    private static void TabTests(TestApp app, string theme, string root)
    {
        bool dark = ThemeCatalog.IsDark(theme); string t = theme + ": ";
        // contrast matrix for the tab colour pairs, both themes
        foreach (var (fg, bg) in new[] { ("MutedBrush", "SidebarBrush"), ("InkBrush", "SurfaceBrush"), ("InkBrush", "SurfaceHoverBrush") })
            Check(Contrast(Brush(fg), Brush(bg)) >= 4.5, $"{t}tab contrast {fg} on {bg} = {Contrast(Brush(fg), Brush(bg)):0.0}:1 (>= 4.5)");
        Check(Contrast(Brush("AccentBrush"), Brush("SurfaceBrush")) >= 3.0, t + "tab accent underline is visible on the selected surface (>= 3:1)");

        var host = new Window { Width = 460, Height = 220, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
        var plain = new TabControl { Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Top };
        plain.Items.Add(new TabItem { Header = "Download status", Content = new TextBlock { Text = "a" } });
        plain.Items.Add(new TabItem { Header = "Speed Limiter", Content = new TextBlock { Text = "b" } });
        plain.Items.Add(new TabItem { Header = "Options on completion", Content = new TextBlock { Text = "c" } });
        plain.Items.Add(new TabItem { Header = "Unavailable", IsEnabled = false, Content = new TextBlock { Text = "d" } });
        host.Content = plain; host.Show(); Pump(150);
        TabChecks(t, dark, plain, "generic tab control");
        plain.SelectedIndex = 2; Pump(100); TabChecks(t, dark, plain, "generic tab control, third tab selected");
        var disabled = plain.Items.OfType<TabItem>().Last(); Check(((SolidColorBrush)disabled.Foreground).Color == Brush("DisabledInkBrush"), t + "disabled tab header shows the dim themed text colour");
        host.Close();

        // the real progress window (Download status / Speed Limiter / Options on completion)
        var m = NewManager(root, "tabs-" + theme);
        try
        {
            var job = Sync(m.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/never.bin"), DateTimeOffset.UtcNow.AddHours(2)));
            var progress = new DownloadProgressWindow(m, job.Id, new JobCompletionStore(), new AppSettings()) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
            progress.Show(); Pump(250);
            var progressTabs = (TabControl)progress.FindName("Tabs");
            Check(progressTabs.Items.Count == 3, t + "progress window keeps its three tabs");
            TabChecks(t, dark, progressTabs, "progress window");
            progressTabs.SelectedIndex = 2; Pump(100); TabChecks(t, dark, progressTabs, "progress window (Options on completion selected)");
            if (Environment.GetEnvironmentVariable("NEODM_SHOTS") is { Length: > 0 } shots) { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(Snapshot((FrameworkElement)progress.Content))); using var f = File.Create(Path.Combine(shots, "progress-" + theme + ".png")); enc.Save(f); }
            progress.Close();
        }
        finally { m.Dispose(); }

        // the Options window (General / Save To / Downloads / Proxy / Torrents)
        var state = Path.Combine(root, "tabs-settings-" + theme); Directory.CreateDirectory(state);
        var options = new SettingsWindow(new AppSettings { DownloadDirectory = Path.Combine(root, "tabs-files") }, state) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
        options.Show(); Pump(250);
        var optionTabs = (TabControl)options.FindName("OptionsTabs");
        Check(optionTabs.Items.OfType<TabItem>().Select(i => ((string)i.Header).Replace("_", "")).Take(4).SequenceEqual(new[] { "General", "Appearance", "Save To", "Downloads" }), t + "Options window tabs: General / Save To / Downloads / Proxy (/ Torrents)");
        TabChecks(t, dark, optionTabs, "Options window");
        optionTabs.SelectedIndex = 3; Pump(100); TabChecks(t, dark, optionTabs, "Options window (Downloads selected)");
        options.Close();
    }

    // ===================== 1, 3, 4: themed menus, scroll bars, combo boxes, tool tips, contrast =====================
    private static void ThemeTests(TestApp app, string theme, string root)
    {
        bool dark = ThemeCatalog.IsDark(theme); string t = theme + ": ";
        Color surface = Brush("SurfaceBrush"), border = Brush("BorderBrush");

        // --- context menu: border + inner edge pixels ---
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "_Open" });
        menu.Items.Add(new MenuItem { Header = "Open _folder", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "_Checked", IsCheckable = true, IsChecked = true, InputGestureText = "Ctrl+K" });
        menu.Items.Add(new MenuItem { Header = "_Remove…", InputGestureText = "Del" });
        var bitmap = Render(menu, 240, 170);
        int w = bitmap.PixelWidth, h = bitmap.PixelHeight; bool whiteFound = false, edgeOk = true, innerOk = true;
        for (int i = 0; i < w; i++) foreach (var y in new[] { 0, 1, 2, h - 3, h - 2, h - 1 }) { var c = Px(bitmap, i, y); if (NearWhite(c) && dark) whiteFound = true; }
        for (int j = 0; j < h; j++) foreach (var x in new[] { 0, 1, 2, w - 3, w - 2, w - 1 }) { var c = Px(bitmap, x, j); if (NearWhite(c) && dark) whiteFound = true; }
        for (int i = 2; i < w - 2; i += 7) { edgeOk &= Near(Px(bitmap, i, 0), border) && Near(Px(bitmap, i, h - 1), border); innerOk &= Near(Px(bitmap, i, 1), surface) && Near(Px(bitmap, i, h - 2), surface); }
        for (int j = 2; j < h - 2; j += 7) { edgeOk &= Near(Px(bitmap, 0, j), border) && Near(Px(bitmap, w - 1, j), border); innerOk &= Near(Px(bitmap, 1, j), surface) && Near(Px(bitmap, w - 2, j), surface); }
        Check(!whiteFound, t + "context menu has no near-white pixel on its border or inner edge");
        Check(edgeOk, t + "context menu border is exactly the 1px theme border colour");
        Check(innerOk, t + "context menu inner edge is the theme surface colour (no inner white line)");
        var item = menu.Items.OfType<MenuItem>().First();
        Check(Contrast(((SolidColorBrush)item.Foreground).Color, surface) >= 4.5, t + "context menu text contrast on the menu background >= 4.5:1");
        var disabled = menu.Items.OfType<MenuItem>().ElementAt(1);
        var disabledColor = ((SolidColorBrush)disabled.Foreground).Color;
        Check(disabledColor == Brush("DisabledInkBrush") && Contrast(disabledColor, surface) >= 3.0, t + "disabled menu items use the dim themed colour (>= 3:1) and stay readable");

        // --- top-level menu dropdown popup ---
        var host = new Window { Width = 360, Height = 260, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
        var bar = new Menu(); var file = new MenuItem { Header = "_File" }; file.Items.Add(new MenuItem { Header = "Add _torrent file…" }); file.Items.Add(new MenuItem { Header = "Open file…", IsEnabled = false }); file.Items.Add(new Separator()); file.Items.Add(new MenuItem { Header = "E_xit" });
        bar.Items.Add(file); bar.Items.Add(new MenuItem { Header = "_View" }); host.Content = new DockPanel { Children = { bar } }; DockPanel.SetDock(bar, Dock.Top);
        host.Show(); Pump(); file.IsSubmenuOpen = true; Pump(200);
        var popup = (Popup)file.Template.FindName("PART_Popup", file);
        Check(popup.IsOpen && popup.Child is Border, t + "top menu dropdown opens with a templated popup");
        var frame = (Border)popup.Child;
        var pb = Snapshot(frame);
        bool popupWhite = false, popupEdge = true, popupInner = true;
        for (int i = 2; i < pb.PixelWidth - 2; i += 3) { popupWhite |= dark && (NearWhite(Px(pb, i, 0)) || NearWhite(Px(pb, i, 1)) || NearWhite(Px(pb, i, pb.PixelHeight - 1)) || NearWhite(Px(pb, i, pb.PixelHeight - 2))); popupEdge &= Near(Px(pb, i, 0), border); popupInner &= Near(Px(pb, i, 1), surface); }
        for (int j = 2; j < pb.PixelHeight - 2; j += 3) { popupWhite |= dark && (NearWhite(Px(pb, 0, j)) || NearWhite(Px(pb, 1, j))); popupEdge &= Near(Px(pb, 0, j), border); popupInner &= Near(Px(pb, 1, j), surface); }
        Check(!popupWhite && popupEdge && popupInner, t + "top menu dropdown: border is the theme border, inner edge is the surface, no white lines");
        file.IsSubmenuOpen = false; host.Close();

        // --- combo box: toggle, popup, items ---
        var cbHost = new Window { Width = 320, Height = 240, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 60, Top = 60 };
        var combo = new ComboBox { Width = 220, Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var s in new[] { "Prefer encrypted", "Require encryption", "Disable encryption" }) combo.Items.Add(new ComboBoxItem { Content = s });
        combo.Items.Add(new ComboBoxItem { Content = "Unavailable choice", IsEnabled = false });
        combo.SelectedIndex = 1; cbHost.Content = combo; cbHost.Show(); Pump();
        var toggleShot = Snapshot(combo);
        Check(Near(Px(toggleShot, 172, 6), surface) && (dark ? !NearWhite(Px(toggleShot, 172, 3)) : true), t + "combo box (closed) is painted with the theme surface" + (dark ? ", not white" : ""));
        combo.IsDropDownOpen = true; Pump(250);
        var dropPopup = (Popup)combo.Template.FindName("PART_Popup", combo);
        var dropFrame = (Border)dropPopup.Child;
        var ds = Snapshot(dropFrame);
        Check(dropPopup.IsOpen && Near(Px(ds, ds.PixelWidth - 6, ds.PixelHeight - 6), surface) && Near(Px(ds, 0, ds.PixelHeight / 2), border), t + "combo box dropdown: surface background and theme border" + (dark ? " (no bright white popup)" : ""));
        var items = combo.Items.OfType<ComboBoxItem>().ToList();
        Check(items.All(i => i.IsEnabled ? Contrast(((SolidColorBrush)i.Foreground).Color, surface) >= 4.5 : Contrast(((SolidColorBrush)i.Foreground).Color, surface) >= 3.0), t + "combo box item text contrast: enabled >= 4.5:1, disabled >= 3:1");
        var selectedItem = items[1]; Pump();
        Check(Contrast(((SolidColorBrush)selectedItem.Foreground).Color, Brush("AccentSoftBrush")) >= 4.5, t + "selected combo item text on the selected background >= 4.5:1");
        combo.IsDropDownOpen = false; cbHost.Close();

        // --- tooltip ---
        var tip = new ToolTip { Content = "C:\\Downloads\\archive.zip" };
        var tipShot = Render(tip, 220, 40);
        Check(Near(Px(tipShot, 100, 3), surface) && Near(Px(tipShot, 0, 20), border) && !(dark && NearWhite(Px(tipShot, 100, 1))), t + "tooltip uses the surface background and theme border" + (dark ? " (not white)" : ""));
        Check(Contrast(((SolidColorBrush)tip.Foreground).Color, surface) >= 4.5, t + "tooltip text contrast >= 4.5:1");

        // --- contrast matrix of the resolved brushes (text pairs >= 4.5:1) ---
        var pairs = new (string Fg, string Bg)[] { ("InkBrush", "CanvasBrush"), ("InkBrush", "SidebarBrush"), ("InkBrush", "SurfaceBrush"), ("InkBrush", "SurfaceHoverBrush"), ("InkBrush", "AccentSoftBrush"), ("MutedBrush", "CanvasBrush"), ("MutedBrush", "SurfaceBrush"), ("MutedBrush", "SidebarBrush"), ("OnAccentBrush", "AccentBrush"), ("AccentBrush", "CanvasBrush"), ("AccentBrush", "SurfaceBrush"), ("ErrorBrush", "SurfaceBrush"), ("SuccessBrush", "SurfaceBrush") };
        foreach (var (fg, bg) in pairs) Check(Contrast(Brush(fg), Brush(bg)) >= 4.5, $"{t}contrast {fg} on {bg} = {Contrast(Brush(fg), Brush(bg)):0.0}:1 (>= 4.5)");
        Check(Contrast(Brush("DisabledInkBrush"), Brush("SurfaceBrush")) >= 3.0, t + "disabled text >= 3:1 on surface");

        // --- column header: hover/pressed states use themed brushes, text stays readable ---
        var header = (Style)app.FindResource(typeof(GridViewColumnHeader));
        var template = (ControlTemplate)header.Setters.OfType<Setter>().First(s => s.Property == Control.TemplateProperty).Value;
        string KeyOf(Trigger trigger) => ((DynamicResourceExtension)trigger.Setters.OfType<Setter>().First(s => s.Property == Border.BackgroundProperty).Value).ResourceKey.ToString()!;
        var hover = template.Triggers.OfType<Trigger>().First(x => x.Property == UIElement.IsMouseOverProperty); var press = template.Triggers.OfType<Trigger>().First(x => x.Property == System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty);
        Check(KeyOf(hover) == "SurfaceHoverBrush" && KeyOf(press) == "AccentSoftBrush" && Contrast(Brush("InkBrush"), Brush("SurfaceHoverBrush")) >= 4.5 && Contrast(Brush("InkBrush"), Brush("AccentSoftBrush")) >= 4.5, t + "column header hover/pressed: themed backgrounds, text contrast >= 4.5:1 (no bright system hover)");

        ScrollBarTests(app, t, dark, root);
    }

    private static void ScrollBarTests(TestApp app, string t, bool dark, string root)
    {
        Color track = Brush("SurfaceBrush"), thumb = Brush("ThumbBrush");
        var vTemplate = (ControlTemplate)app.FindResource("VerticalScrollBarTemplate"); var hTemplate = (ControlTemplate)app.FindResource("HorizontalScrollBarTemplate");
        var list = new ListBox { Width = 200, Height = 120, Background = new SolidColorBrush(track) };
        for (int i = 0; i < 40; i++) list.Items.Add(new ListBoxItem { Content = "Row " + i });
        var listView = new ListView { Width = 200, Height = 120 }; var grid = new GridView(); grid.Columns.Add(new GridViewColumn { Header = "A", Width = 400 }); listView.View = grid;
        for (int i = 0; i < 40; i++) listView.Items.Add("x" + i);
        var data = new DataGrid { Width = 200, Height = 120, ItemsSource = Enumerable.Range(0, 40).Select(i => new { Id = i, Name = "Item " + i }).ToList() };
        var scroll = new ScrollViewer { Width = 200, Height = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Border { Height = 600, Width = 150 } };
        var text = new TextBox { Width = 200, Height = 80, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = string.Join("\n", Enumerable.Range(0, 60).Select(i => "line " + i)) };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(track) };
        foreach (FrameworkElement e in new FrameworkElement[] { list, listView, data, scroll, text }) panel.Children.Add(e);
        var win = new Window { Width = 1100, Height = 300, ShowInTaskbar = false, Content = panel, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
        win.Show(); Pump(150);
        foreach (var (name, element) in new (string, DependencyObject)[] { ("ListBox", list), ("ListView", listView), ("DataGrid", data), ("ScrollViewer", scroll), ("TextBox", text) })
        {
            var bars = Visual<ScrollBar>(element).Where(b => b.Orientation == Orientation.Vertical && b.Visibility == Visibility.Visible).ToList();
            Check(bars.Count >= 1 && bars.All(b => ReferenceEquals(b.Template, vTemplate) && b.ActualWidth == 12), $"{t}{name}: vertical scroll bar uses the themed thin template (12px), not the system one");
        }
        var bar = Visual<ScrollBar>(list).First(b => b.Orientation == Orientation.Vertical);
        var shot = Snapshot(panel);
        var thumbPart = Visual<Thumb>(bar).First();
        var thumbPoint = thumbPart.TranslatePoint(new Point(thumbPart.ActualWidth / 2, thumbPart.ActualHeight / 2), panel);
        bool thumbSeen = false; var barTopLeft = bar.TranslatePoint(new Point(0, 0), panel);
        for (int yy = 0; yy < (int)bar.ActualHeight && !thumbSeen; yy++) for (int xx = 0; xx < (int)bar.ActualWidth; xx++) if (Near(Px(shot, (int)barTopLeft.X + xx, (int)barTopLeft.Y + yy), thumb, 6)) { thumbSeen = true; break; }
        Check(thumbSeen, t + "scroll bar thumb is painted in the themed colour");
        var trackPoint = thumbPart.TranslatePoint(new Point(6, 90), panel);
        Check(Near(Px(shot, (int)trackPoint.X, (int)trackPoint.Y), track), t + "scroll bar track is transparent: the list background shows through (no system track colour)");
        var thumbBorder = Visual<Border>(thumbPart).First(); Check(thumbBorder.CornerRadius.TopLeft >= 4, t + "scroll bar thumb is rounded");
        // sidebar of the real main window
        using var manager = NewManager(root, "sidebar" + t.Replace(":", "").Trim());
        var main = new MainWindow(manager, new AppSettings { DownloadDirectory = Path.Combine(root, "sidebar") }, () => { }, enableTray: false) { ShowInTaskbar = false, Height = 480 };
        main.Show(); Pump(150);
        var nav = (ListBox)main.FindName("Navigation");
        main.Height = 480; nav.MaxHeight = 90; main.UpdateLayout(); Pump(100);
        var navBars = Visual<ScrollBar>(nav).Where(b => b.Orientation == Orientation.Vertical && b.Visibility == Visibility.Visible).ToList();
        Check(navBars.Count == 1 && ReferenceEquals(navBars[0].Template, vTemplate), t + "main window sidebar shows the themed scroll bar");
        Check(main.FindName("MainMenu") is Menu m && Visual<Border>(m).Any(), t + "main menu bar renders with the themed template");
        main.Close(); win.Close();
        Check(hTemplate is not null, t + "horizontal scroll bar template exists");
    }

    // ===================== 5: layout at 100 / 125 / 150 % =====================
    private static void LayoutTests(TestApp app, string root)
    {
        using var manager = NewManager(root, "layout");
        var job = Sync(manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/some/long/path/file-name.iso"), DateTimeOffset.UtcNow.AddDays(1)));
        var meta = new NDM.Torrent.TorrentMetadata("id", "Pack", 600, new[] { new NDM.Torrent.TorrentMetadataFile("Pack/a/x.bin", 100), new NDM.Torrent.TorrentMetadataFile("Pack/z.bin", 500) });
        var cases = new List<(string Name, Func<Window> Make)>
        {
            ("Options", () => new SettingsWindow(new AppSettings { DownloadDirectory = Path.Combine(root, "layout") }, Path.Combine(root, "layout-state"))),
            ("Add download", () => new AddDownloadWindow("https://example.org/file.zip")),
            ("Add torrent", () => new TorrentAddWindow(new AppSettings { DownloadDirectory = root }, "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", null, (_, _, _) => Task.FromResult(meta))),
            ("Properties", () => new PropertiesWindow(new DownloadRow(job))),
            ("About", () => new AboutWindow()),
            ("Progress", () => new DownloadProgressWindow(manager, job.Id, new JobCompletionStore(), new AppSettings())),
            ("Completion", () => new DownloadCompleteWindow(job, new AppSettings(), () => { })),
            ("Open confirm", () => new OpenFileConfirmWindow("a-very-long-holiday-movie-file-name-with-many-words-and-numbers-2026-final-cut-director-version-part-1-of-3.mp4", OpenKind.Video)),
            ("Capture dialog", () => new DownloadOptionsDialog("https://example.org/dir/file.zip", "file.zip", "", "", root)),
            ("Update", () => new UpdateWindow(ReleaseInfoFor("1.3.1"), new UpdateController(new AppSettings(), () => { }, "1.3.0", () => throw new InvalidOperationException(), new UpdateInstaller(Path.Combine(root, "u"), "1.3.0")))),
        };
        foreach (double scale in new[] { 1.0, 1.25, 1.5 })
        {
            // 1920x1080 screen minus a 40px taskbar, expressed in device-independent units at this Windows scale
            var work = new Rect(0, 0, 1920 / scale, 1040 / scale);
            string tag = $"{scale * 100:0}%: ";
            foreach (var (name, make) in cases)
            {
                var window = make(); window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = 0; window.Top = 0;
                WindowSizing.Fit(window, work);
                window.Show(); Pump(80); window.UpdateLayout();
                int tabCount = window.FindName("OptionsTabs") is TabControl tc ? tc.Items.Count : 1;
                for (int tab = 0; tab < tabCount; tab++)
                {
                    if (window.FindName("OptionsTabs") is TabControl tabs) { tabs.SelectedIndex = tab; window.UpdateLayout(); Pump(30); }
                    string label = tabCount > 1 ? $"{tag}{name} tab {tab + 1}" : $"{tag}{name}";
                    Check(window.ActualWidth <= work.Width + 0.5 && window.ActualHeight <= work.Height + 0.5, $"{label}: window fits the screen ({window.ActualWidth:0}x{window.ActualHeight:0} in {work.Width:0}x{work.Height:0})");
                    var overlaps = new List<string>();
                    foreach (var sv in Logical<ScrollViewer>(window).Where(s => s.IsVisible && s.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled))
                    {
                        if (sv.ExtentWidth > sv.ViewportWidth + 0.5) overlaps.Add("horizontal clip in " + sv.Name);
                        foreach (var control in Logical<Control>(sv).Where(c => c.IsVisible && c is TextBox or ComboBox or Button or CheckBox or PasswordBox or RadioButton && c.ActualWidth > 0))
                        {
                            var bounds = control.TransformToAncestor(sv).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
                            if (bounds.Right > sv.ActualWidth - 12 - 4 + 0.5 && sv.ExtentHeight > 0) overlaps.Add($"{control.GetType().Name} {control.Name} right edge {bounds.Right:0} > {sv.ActualWidth - 16:0}");
                        }
                    }
                    Check(overlaps.Count == 0, $"{label}: no control touches or is covered by the vertical scroll bar / clipped ({string.Join("; ", overlaps)})");
                    var outside = Logical<Control>(window).Where(c => c.IsVisible && c.ActualWidth > 0 && c is TextBox or ComboBox or Button).Where(c => { var r = c.TransformToAncestor(window).TransformBounds(new Rect(0, 0, c.ActualWidth, c.ActualHeight)); return r.Right > window.ActualWidth + 0.5; }).ToList();
                    Check(outside.Count == 0, $"{label}: no control extends past the right edge of the window");
                    var image = Render((FrameworkElement)window.Content, Math.Min(window.ActualWidth, 400), Math.Min(window.ActualHeight, 300), scale, layout: false);
                    Check(image.PixelWidth == (int)Math.Ceiling(Math.Min(window.ActualWidth, 400) * scale) && image.DpiX == 96 * scale, $"{label}: renders at {scale * 96:0} dpi");
                }
                window.Close();
            }
        }
        // oversized windows are clamped, SizeToContent windows get a maximum height so their content scrolls instead of leaving the screen
        var big = new Window { Width = 1400, Height = 900, MinWidth = 1300, MinHeight = 850 };
        WindowSizing.Fit(big, new Rect(0, 0, 1280, 693));
        Check(big.Width <= 1280 - WindowSizing.Margin && big.Height <= 693 - WindowSizing.Margin && big.MinHeight <= 693 - WindowSizing.Margin && big.MaxHeight <= 693, "WindowSizing clamps size, minimum size and maximum size to the work area");
        var auto = new Window { SizeToContent = SizeToContent.Height, Width = 500 }; WindowSizing.Fit(auto, new Rect(0, 0, 1280, 693));
        Check(auto.MaxHeight <= 693 - WindowSizing.Margin && auto.SizeToContent == SizeToContent.Height, "SizeToContent windows keep sizing to content up to the screen height");
    }

    internal static ReleaseInfo ReleaseInfoFor(string version) => UpdateChecker.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "release-latest.json")).Replace("1.3.1", version))!;
}
