using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>1.11.0 Part C (items 7, 9, 11, 12, 17): completion wording and power options, selection checkboxes with the bulk bar, taskbar progress, sidebar chevron, Add to queue. Fakes only: no power action, no real taskbar, no real clipboard.</summary>
internal static partial class V1110Tests
{
    private sealed class FakeCaps(bool hibernate) : IPowerCapabilities { public bool HibernateAvailable { get; } = hibernate; }
    private sealed class RecPower : IPowerActions { public readonly List<(CompletionActionKind Kind, bool Force)> Calls = new(); public void Execute(CompletionActionKind kind, bool force) => Calls.Add((kind, force)); }
    private sealed class FakeCount(CountdownOutcome outcome) : ICountdownPrompt { public int Shown; public CompletionActionKind Last; public CountdownOutcome Show(CompletionActionKind kind, int seconds) { Shown++; Last = kind; return outcome; } }
    private sealed class RecSink : ITaskbarSink { public readonly List<(TaskbarState State, double Value)> Calls = new(); public void Set(TaskbarState state, double fraction) => Calls.Add((state, fraction)); }

    internal static string[] AllLanguages = { "en", "ar", "es", "fr", "de", "pt-BR", "ru", "tr", "zh-Hans", "ja" };
    internal static Dictionary<string, string> AppLocale(string tag) => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", "Locales", tag + ".json")))!;
    private static void KeysInEveryLocale(IEnumerable<string> keys, string what)
    {
        foreach (var tag in AllLanguages) { var l = AppLocale(tag); var missing = keys.Where(k => !l.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToArray(); Check(missing.Length == 0, $"{what}: every key has text in {tag} ({string.Join(",", missing)})"); }
    }

    private static DownloadManager CManager(string scratch, string name, int concurrent = 2)
    {
        string state = Path.Combine(scratch, name, "NDM"), files = Path.Combine(scratch, name, "files"); Directory.CreateDirectory(state); Directory.CreateDirectory(files);
        return new DownloadManager(files, state, new DownloadOptions { MaxConcurrentDownloads = concurrent, MaxSegments = 2, RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(2) });
    }
    private static AppSettings CSettings(string dir) => new() { DownloadDirectory = dir, ShowCompletionDialog = false, ShowProgressOnCompletion = false, CheckForUpdates = false };
    private static MainWindow CMain(DownloadManager m, AppSettings s, Action? save = null, double width = 1180, double height = 720, IPowerActions? power = null)
        => new(m, s, save ?? (() => { }), enableTray: false, power ?? new RecPower()) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20, Width = width, Height = height };
    private static DownloadJob Snap(string name, string status, long total = 1000, long done = 0)
    {
        var job = new DownloadJob();
        void Set(string prop, object? value) => typeof(DownloadJob).GetProperty(prop)!.SetValue(job, value);
        Set("Id", Guid.NewGuid().ToString("N")); Set("FileName", name); Set("Url", "http://files.invalid/" + name); Set("Status", status); Set("TotalBytes", total); Set("DownloadedBytes", done);
        return job;
    }
    private static DownloadRow RowOf(string status, long total = 1000, long done = 0) => new(Snap("f.bin", status, total, done));
    private static DownloadRow TorrentRow(string state, double progress = 0.5) =>
        new(new NDM.Torrent.TorrentJob("t" + Guid.NewGuid().ToString("N")[..8], "t", "C:/x", 1000, 1000, (long)(1000 * progress), progress * 100, 0, 0, 0, 0, 0, 0, state, null, Array.Empty<NDM.Torrent.TorrentFileJob>()));
    private static void Click(FrameworkElement? target) => target!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    private static async Task<List<DownloadJob>> SeedPaused(DownloadManager m, params string[] names) { var list = new List<DownloadJob>(); foreach (var n in names) list.Add(await m.AddPausedAsync(new DownloadRequest("http://127.0.0.1:9/" + n, n))); return list; }
    private static void PumpUntil(Func<bool> done, string what, int ms = 6000) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (!done() && DateTime.UtcNow < end) Pump(30); Check(done(), what); }
    private static void SaveShot(Window w, string folder, string name)
    {
        Directory.CreateDirectory(folder); w.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32); bmp.Render(w);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var fs = File.Create(Path.Combine(folder, name)); enc.Save(fs);
    }
    private static readonly string? ShotsFolder = Environment.GetEnvironmentVariable("NEODM_SHOTS");
    private static (byte R, byte G, byte B) Px(byte[] px, int stride, int x, int y) { int i = y * stride + x * 4; byte a = px[i + 3]; if (a == 0) return (0, 0, 0); return ((byte)Math.Min(255, px[i + 2] * 255 / a), (byte)Math.Min(255, px[i + 1] * 255 / a), (byte)Math.Min(255, px[i] * 255 / a)); }
    private static bool Near((byte R, byte G, byte B) p, Color c, int tol = 14) => Math.Abs(p.R - c.R) <= tol && Math.Abs(p.G - c.G) <= tol && Math.Abs(p.B - c.B) <= tol;
    private static Color BrushColor(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

    // ======================================================================== item 7
    private static void C7Power(string scratch)
    {
        // what the dropdown offers
        var withHib = JobCompletionOptions.PowerChoicesFor(true); var withoutHib = JobCompletionOptions.PowerChoicesFor(false);
        Check(withHib.SequenceEqual(new[] { CompletionActionKind.Sleep, CompletionActionKind.Hibernate, CompletionActionKind.Shutdown, CompletionActionKind.Restart, CompletionActionKind.Lock }), "power choices: Sleep, Hibernate, Shut down, Restart, Lock");
        Check(!withoutHib.Contains(CompletionActionKind.Hibernate) && withoutHib.Contains(CompletionActionKind.Restart), "Hibernate is offered only when the system reports it; Restart is always there");
        Check(JobCompletionOptions.PowerChoicesFor(false, CompletionActionKind.Hibernate).Contains(CompletionActionKind.Hibernate), "a download that already has Hibernate keeps it in the list");

        var m = CManager(scratch, "c7"); var settings = CSettings(Path.Combine(scratch, "c7", "files"));
        var job = Wait(m.AddPausedAsync(new DownloadRequest("http://127.0.0.1:9/a.bin", "a.bin")));
        string storePath = Path.Combine(scratch, "c7", "completion.json");
        var oldCaps = PowerCapabilities.Current;
        try
        {
            // settings migration: an old 'Turn off computer when done' (box ticked, nothing else) shows as Shut down; nothing stored shows Do nothing
            File.WriteAllText(storePath, "{\"" + job.Id + "\":{\"ShowDialog\":false,\"TurnOff\":true,\"Force\":false}}");
            foreach (bool hib in new[] { true, false })
            {
                PowerCapabilities.Current = new FakeCaps(hib);
                var store = new JobCompletionStore(storePath);
                var w = new DownloadProgressWindow(m, job.Id, store, settings) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
                w.Show(); Pump(80);
                var kind = Part<ComboBox>(w, "TurnOffKind"); var labels = kind.Items.Cast<ComboBoxItem>().Select(i => (string)i.Content).ToList();
                Check(((ComboBoxItem)kind.SelectedItem).Content as string == "Shut down" && kind.IsEnabled, $"hibernate {hib}: an old turn-off setting shows as Shut down");
                Check(labels.Contains("Hibernate") == hib && labels[0] == "Do nothing" && labels.Contains("Sleep") && labels.Contains("Restart"), $"hibernate {hib}: dropdown {string.Join(", ", labels)}");
                if (hib) w.Close(); else { Part<CheckBox>(w, "ShowDialogBox"); w.Close(); }
            }
            PowerCapabilities.Current = new FakeCaps(true);
            var fresh = new DownloadProgressWindow(m, job.Id, new JobCompletionStore(Path.Combine(scratch, "c7", "none.json")), settings) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
            fresh.Show(); Pump(80);
            var box = Part<ComboBox>(fresh, "TurnOffKind");
            Check((string)((ComboBoxItem)box.SelectedItem).Content == "Do nothing", "nothing chosen shows Do nothing");
            // wording: the two checkboxes say what they do, with help text
            var dialogBox = Part<CheckBox>(fresh, "ShowDialogBox"); var windowBox = Part<CheckBox>(fresh, "ShowWindowBox");
            Check((string)dialogBox.Content == "Show a completion window when this download finishes" && (string)windowBox.Content == "Bring this progress window back when the download finishes", "the two completion options have distinct wording: " + dialogBox.Content + " / " + windowBox.Content);
            Check(Part<TextBlock>(fresh, "CompleteWindowHelp").Text.Contains("Open folder") && Part<TextBlock>(fresh, "ShowWindowHelp").Text.Contains("Complete state"), "each option has one line of help");
            Check(Part<TextBlock>(fresh, "PowerLabel").Text == "Power options when done:" && !Loc.T("progress.chk.turn_off_computer_when_done").Contains("never"), "the label reads Power options when done");
            // choosing writes the per-download option, Force follows Shut down / Restart only
            var store2 = new JobCompletionStore(Path.Combine(scratch, "c7", "none.json"));
            void Pick(string label) => box.SelectedItem = box.Items.Cast<ComboBoxItem>().First(i => (string)i.Content == label);
            Part<CheckBox>(fresh, "ShowDialogBox").IsChecked = false; Pump(30);
            Pick("Sleep"); Check(!Part<CheckBox>(fresh, "ForceBox").IsEnabled, "Sleep does not take Force");
            Pick("Restart"); Check(Part<CheckBox>(fresh, "ForceBox").IsEnabled, "Restart takes Force");
            Pick("Do nothing"); Check(!Part<CheckBox>(fresh, "ForceBox").IsEnabled, "Do nothing: Force off");
            fresh.Close();
        }
        finally { PowerCapabilities.Current = oldCaps; }

        // countdown and cancel through the fake power service; nothing real is ever powered off
        var power = new RecPower(); var cancel = new FakeCount(CountdownOutcome.Cancelled); var go = new FakeCount(CountdownOutcome.Elapsed); var now = new FakeCount(CountdownOutcome.DoItNow);
        var runner = new CompletionActionRunner(new AppSettings(), power, cancel, () => { }, () => { }, () => { }, () => { });
        Check(!runner.RunFor(CompletionActionKind.Sleep, false) && power.Calls.Count == 0 && cancel.Shown == 1 && cancel.Last == CompletionActionKind.Sleep, "countdown cancelled: Sleep never reaches the power service");
        runner.Countdown = go;
        Check(runner.RunFor(CompletionActionKind.Sleep, false) && power.Calls.SequenceEqual(new[] { (CompletionActionKind.Sleep, false) }) && go.Shown == 1, "countdown elapsed: Sleep goes to the fake power service once, after the countdown");
        runner.Countdown = now; runner.RunFor(CompletionActionKind.Restart, true); runner.RunFor(CompletionActionKind.Hibernate, true); runner.RunFor(CompletionActionKind.Shutdown, true);
        Check(power.Calls.Skip(1).SequenceEqual(new[] { (CompletionActionKind.Restart, true), (CompletionActionKind.Hibernate, false), (CompletionActionKind.Shutdown, true) }), "Restart and Shut down take Force, Hibernate never does");
        var real = new SystemPowerActions(); Check(PowerCommands.For(CompletionActionKind.Hibernate, false) is not null && PowerCommands.For(CompletionActionKind.Restart, false)!.Arguments.Contains("/r"), "the real service maps Restart and Hibernate to the shutdown tool (never called in tests)");
        KeysInEveryLocale(new[] { "progress.help.complete_window", "progress.help.show_window", "progress.text.power_options_when_done", "progress.chk.show_download_complete_dialog", "progress.chk.show_this_window_when_the_download", "options.chk.show_download_completion_dialog", "options.chk.open_the_download_window_when_a", "completion.label.none", "completion.label.restart", "completion.label.sleep", "completion.label.hibernate", "completion.power.shutdown" }, "item 7");
        var en = AppLocale("en"); Check(en["options.chk.show_download_completion_dialog"].Replace("_", "") == "Show a completion window when a download finishes" && en["options.chk.open_the_download_window_when_a"].Replace("_", "").StartsWith("Bring back"), "Options > Downloads uses the same two wordings");

        // themes and right-to-left
        PowerCapabilities.Current = new FakeCaps(true);
        try
        {
            foreach (var theme in ThemeCatalog.Concrete)
            {
                ThemeManager.Apply(Application.Current, theme.Id, null);
                var w = new DownloadProgressWindow(m, job.Id, new JobCompletionStore(), settings) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
                w.Show(); Pump(60); Part<TabControl>(w, "Tabs").SelectedItem = w.FindName("OptionsTab"); Pump(80); w.FlowDirection = FlowDirection.RightToLeft; Pump(80);
                var kind = Part<ComboBox>(w, "TurnOffKind"); var label = Part<TextBlock>(w, "PowerLabel");
                var lp = label.PointToScreen(new Point(0, 0)); var kp = kind.PointToScreen(new Point(0, 0));
                Check(kind.ActualWidth > 100 && lp.X > kp.X, $"{theme.Id}: right to left puts the label to the right of the dropdown");
                w.FlowDirection = FlowDirection.LeftToRight; Pump(40);
                if (ShotsFolder is { Length: > 0 } f && theme.Id is "DeepNavy" or "Light") { SaveShot(w, f, $"c7-completion-options-{theme.Id}.png"); }
                w.Close();
            }
        }
        finally { PowerCapabilities.Current = oldCaps; ThemeManager.Apply(Application.Current, "Light", null); }
    }

    // ======================================================================== item 9
    private static void C9Checkboxes(string scratch)
    {
        var m = CManager(scratch, "c9", 1); var files = Path.Combine(scratch, "c9", "files"); int saves = 0;
        var settings = CSettings(files);
        var w = CMain(m, settings, () => saves++);
        w.Show(); Pump(100);
        var seeded = Wait(SeedPaused(m, "a0.zip", "a1.zip", "a2.zip", "a3.zip", "a4.zip", "a5.zip", "movie.mp4"));
        PumpUntil(() => w.Rows.Count == 7, "rows appear");
        var header = Part<CheckBox>(w, "HeaderCheck"); var list = Part<ListView>(w, "DownloadList"); var search = Part<TextBox>(w, "Search");
        Check(header.IsChecked == false && !w.BulkBarVisible && settings.ShowSelectionCheckboxes && Col(w, "CheckColumn") is null == false, "starts with nothing checked, no bulk bar, checkboxes on by default");
        Check(((GridView)list.View).Columns[0].Header is CheckBox && ((GridView)list.View).Columns[0].Width == 34, "a leading checkbox column");
        w.Rows[0].IsChecked = true; w.Rows[1].IsChecked = true; Pump(60);
        Check(header.IsChecked == null && w.BulkBarVisible && Part<TextBlock>(w, "BulkCount").Text == "Selected: 2", "two checked: header shows the mixed state, the bar appears with the count");
        Click(header); Pump(60);
        Check(w.Rows.All(r => r.IsChecked) && header.IsChecked == true && Part<TextBlock>(w, "BulkCount").Text == "Selected: 7", "header click checks everything shown");
        Pump(1300);
        Check(w.Rows.All(r => r.IsChecked) && w.Rows.Count == 7, "the 500 ms refresh keeps the checks");
        // filter and search: select all applies to the visible rows only
        search.Text = "movie"; Pump(120);
        Check(w.VisibleRows.Count == 1 && header.IsChecked == true && Part<TextBlock>(w, "BulkCount").Text.Contains("(6 more hidden by the filter)") && w.CheckedRows.Count == 1, "a search keeps the other checks, says how many are hidden, and the bar only counts what is shown");
        Click(header); Pump(60);
        Check(w.Rows.Count(r => r.IsChecked) == 6 && !w.Rows.First(r => r.FileName == "movie.mp4").IsChecked && header.IsChecked == false, "header click while filtered clears the shown row only");
        Pump(400); Check(!w.BulkBarVisible, "nothing shown is checked: the bar closes");
        search.Text = ""; Pump(120);
        Check(header.IsChecked == null && w.CheckedRows.Count == 6 && w.BulkBarVisible, "clearing the search brings the six back");
        Check(Part<ListBox>(w, "Navigation") is { } nav && nav.Items.Count > 0, "sidebar present");
        // bulk bar state follows the engine states (same rules as the toolbar and the menu)
        var st = w.BulkState; Check(st.Resume && !st.Pause && st.AddToQueue && st.Move && st.CopyLinks && st.Remove && !st.RemoveWithFiles, "paused rows: Resume, Add to queue, Move, Copy links and Remove are on; Pause and Remove with files are off");
        // keyboard: Space toggles the highlighted rows, Ctrl+A checks everything shown
        foreach (var r in w.Rows) r.IsChecked = false; Pump(40);
        list.SelectedItem = w.Rows[2]; Pump(40);
        var space = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent };
        Check(w.HandleCheckKeys(space, requireFocus: false, modifiers: ModifierKeys.None) && w.Rows[2].IsChecked, "Space checks the highlighted row");
        Check(w.HandleCheckKeys(space, requireFocus: false, modifiers: ModifierKeys.None) && !w.Rows[2].IsChecked, "Space again clears it");
        var ctrlA = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, Key.A) { RoutedEvent = Keyboard.KeyDownEvent };
        Check(w.HandleCheckKeys(ctrlA, requireFocus: false, modifiers: ModifierKeys.Control) && w.Rows.All(r => r.IsChecked), "Ctrl+A checks every shown download");
        Check(!w.HandleCheckKeys(ctrlA, requireFocus: false, modifiers: ModifierKeys.None), "plain A is not handled");
        // row highlight and check are independent
        foreach (var r in w.Rows) r.IsChecked = false; list.SelectedItems.Clear(); list.SelectedItems.Add(w.Rows[0]); list.SelectedItems.Add(w.Rows[1]); Pump(50);
        Check(w.Rows.All(r => !r.IsChecked) && list.SelectedItems.Count == 2, "highlighting rows (Ctrl and Shift click) does not check them");
        w.Rows[3].IsChecked = true; Pump(50); Check(list.SelectedItems.Count == 2, "checking a row does not change the highlight");
        // bulk commands: Move to a folder (finished and unfinished jobs), Resume, Queue and Remove with the same confirmation
        foreach (var r in w.Rows) r.IsChecked = false; w.Rows[0].IsChecked = true; w.Rows[1].IsChecked = true; Pump(40);
        string target = Path.Combine(scratch, "c9", "moved"); w.MoveRows(w.CheckedRows, target);
        Check(m.Jobs.Where(j => w.CheckedRows.Any(r => r.Id == j.Id)).All(j => Path.GetDirectoryName(j.FilePath) == target), "Move to: the two checked downloads switch their folder");
        Check(w.QueueRows(w.CheckedRows) == 2, "bulk Add to queue queues both paused downloads");
        PumpUntil(() => m.Jobs.Where(j => w.CheckedRows.Any(r => r.Id == j.Id)).All(j => j.Status != "Paused"), "the queued jobs leave Paused");
        // Remove: the themed confirmation lists the checked rows; Cancel keeps them, OK removes them
        int before = w.Rows.Count; var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { var d = Application.Current.Windows.OfType<RemoveConfirmWindow>().First(); d.DialogResult = false; }));
        Click(Part<Button>(w, "BulkRemove")); Pump(200);
        Check(w.Rows.Count == before, "bulk Remove asks first; Cancel keeps every row");
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { var d = Application.Current.Windows.OfType<RemoveConfirmWindow>().First(); d.DialogResult = true; }));
        Click(Part<Button>(w, "BulkRemove")); PumpUntil(() => w.Rows.Count == before - 2, "OK removes the two checked rows");
        // Copy links: the text that would be copied (the real clipboard is never touched here)
        foreach (var r in w.Rows) r.IsChecked = false; w.Rows[0].IsChecked = true; w.Rows[1].IsChecked = true; Pump(40);
        var text = RowMenu.LinkText(w.CheckedRows); Check(text is not null && text.Split(Environment.NewLine).Length == 2 && text.Contains("http://127.0.0.1:9/"), "Copy links would copy one address per checked download");
        // the View menu switch
        var menu = Part<MenuItem>(w, "CheckboxesMenuItem"); menu.IsChecked = false; menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(100);
        Check(!settings.ShowSelectionCheckboxes && saves > 0 && Col(w, "CheckColumn").Width == 0 && !w.BulkBarVisible && w.Rows.All(r => !r.IsChecked), "View > Show selection checkboxes (off) hides the column and the bar and drops the checks");
        var json = JsonSerializer.Serialize(settings); Check(json.Contains("\"ShowSelectionCheckboxes\":false") && !json.Contains(m.Jobs[0].Id), "only the on/off switch is persisted, never which rows are checked");
        menu.IsChecked = true; menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(100); Check(settings.ShowSelectionCheckboxes && Col(w, "CheckColumn").Width == 34, "switching it on brings the column back");
        // right to left and a narrow window
        w.FlowDirection = FlowDirection.RightToLeft; Pump(150);
        var hp = header.PointToScreen(new Point(header.ActualWidth / 2, 0)); var lpos = list.PointToScreen(new Point(0, 0));
                Check(hp.X > lpos.X - list.ActualWidth / 2 && lpos.X - hp.X < 60, "right to left: the checkbox column sits on the right");
        w.FlowDirection = FlowDirection.LeftToRight; w.Width = 720; Pump(150);
        w.Rows[0].IsChecked = true; Pump(150);
        Check(Part<Border>(w, "BulkBar").ActualWidth <= list.ActualWidth + 1 && Part<Button>(w, "BulkRemove").ActualWidth > 20, "narrow window: the bar wraps inside the list width");
        // screenshot with a few rows checked
        foreach (var r in w.Rows) r.IsChecked = false; w.Width = 1180; w.Rows[0].IsChecked = true; w.Rows[2].IsChecked = true; Pump(250);
        if (ShotsFolder is { Length: > 0 } f) { foreach (var theme in new[] { "Light", "DeepNavy" }) { ThemeManager.Apply(Application.Current, theme, null); Pump(150); SaveShot(w, f, $"c9-main-checkboxes-{theme}.png"); } ThemeManager.Apply(Application.Current, "Light", null); }
        w.Close(); m.Dispose();
    }

    private static void C9Contrast()
    {
        var accents = new string?[] { null, "#FF0000", "#00FF00", "#FFFF00", "#0000FF", "#808080", "#FFFFFF", "#000000", "#FF00FF", "#00FFFF", "#7A5CFF" };
        int cases = 0;
        try
        {
            foreach (var theme in ThemeCatalog.Concrete)
                foreach (var accent in accents)
                {
                    ThemeManager.Apply(Application.Current, theme.Id, accent);
                    var surface = BrushColor("SurfaceBrush"); var onAccent = BrushColor("OnAccentBrush"); var muted = BrushColor("MutedBrush"); var hover = BrushColor("ItemHoverBrush"); var selected = BrushColor("ItemSelectedBrush");
                    var fill = (Brush)Application.Current.FindResource("AccentFillBrush");
                    var stops = fill is SolidColorBrush sb ? new[] { sb.Color } : ((LinearGradientBrush)fill).GradientStops.Select(s => s.Color).ToArray();
                    foreach (var c in stops)
                    {
                        Check(ThemeManager.Contrast(c, surface) >= 3, $"{theme.Id}/{accent ?? "default"}: the checked fill reads at 3:1 on the row ({ThemeManager.Contrast(c, surface):0.00})");
                        Check(ThemeManager.Contrast(c, hover) >= 3, $"{theme.Id}/{accent ?? "default"}: the checked fill reads at 3:1 on a hovered row ({ThemeManager.Contrast(c, hover):0.00})");
                        Check(ThemeManager.Contrast(onAccent, c) >= 4.5, $"{theme.Id}/{accent ?? "default"}: the check mark reads at 4.5:1 on the fill ({ThemeManager.Contrast(onAccent, c):0.00})");
                    }
                    Check(ThemeManager.Contrast(muted, surface) >= 3, $"{theme.Id}/{accent ?? "default"}: the empty box border reads at 3:1 ({ThemeManager.Contrast(muted, surface):0.00})");
                    cases++;
                }
        }
        finally { ThemeManager.Apply(Application.Current, "Light", null); }
        Check(cases == ThemeCatalog.Concrete.Count() * accents.Length, "every theme and custom accent was checked");
    }

    private static void C9Pixels()
    {
        try
        {
            foreach (var theme in new[] { "Light", "DeepNavy", "Dark", "Paper" })
            {
                ThemeManager.Apply(Application.Current, theme, null);
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };
                var style = (Style)Application.Current.FindResource("SelectCheckBox");
                var on = new CheckBox { Style = style, IsChecked = true }; var off = new CheckBox { Style = style, IsChecked = false }; var mixed = new CheckBox { Style = style, IsThreeState = true, IsChecked = null }; var disabled = new CheckBox { Style = style, IsChecked = true, IsEnabled = false };
                foreach (var c in new[] { on, off, mixed, disabled }) panel.Children.Add(c);
                var host = new Window { Width = 220, Height = 120, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Content = new Border { Background = (Brush)Application.Current.FindResource("SurfaceBrush"), Child = panel } };
                host.Show(); Pump(100); host.UpdateLayout();
                var bmp = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(host);
                var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4]; bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
                (int x, int y) Center(FrameworkElement e) { var p = e.TranslatePoint(new Point(e.ActualWidth / 2, e.ActualHeight / 2), host); return ((int)p.X, (int)p.Y); }
                var fillColor = (Application.Current.FindResource("AccentFillBrush") as SolidColorBrush)?.Color ?? ((LinearGradientBrush)Application.Current.FindResource("AccentFillBrush")).GradientStops[0].Color;
                var surface = BrushColor("SurfaceBrush"); var tick = BrushColor("OnAccentBrush");
                var (cx, cy) = Center(on);
                // a corner of the box (inside the rounded border, outside the tick) shows the accent fill; the middle shows the tick colour somewhere
                bool fillSeen = false, tickSeen = false;
                for (int dy = -6; dy <= 6; dy++) for (int dx = -6; dx <= 6; dx++) { var p = Px(px, bmp.PixelWidth * 4, cx + dx, cy + dy); if (Near(p, fillColor, 30)) fillSeen = true; if (Near(p, tick, 40)) tickSeen = true; }
                Check(fillSeen && tickSeen, $"{theme}: a checked box is filled with the accent and carries a check mark of the contrast colour");
                var (ox, oy) = Center(off); var inner = Px(px, bmp.PixelWidth * 4, ox, oy);
                Check(Near(inner, surface, 10), $"{theme}: an empty box is open (row colour inside)");
                bool border = false; for (int dx = -9; dx <= 9; dx++) { var p = Px(px, bmp.PixelWidth * 4, ox + dx, oy); if (Near(p, BrushColor("MutedBrush"), 40)) border = true; }
                Check(border, $"{theme}: an empty box has a muted border");
                var (mx, my) = Center(mixed); bool dash = false; for (int dx = -5; dx <= 5; dx++) if (Near(Px(px, bmp.PixelWidth * 4, mx + dx, my), tick, 40)) dash = true;
                Check(dash, $"{theme}: the indeterminate box shows a dash");
                var (dx2, dy2) = Center(disabled); var dis = Px(px, bmp.PixelWidth * 4, dx2 - 4, dy2 - 4); Check(!Near(dis, fillColor, 8), $"{theme}: a disabled box is dimmed");
                host.Close();
            }
        }
        finally { ThemeManager.Apply(Application.Current, "Light", null); }
    }

    // ======================================================================== item 17
    private static void C17Queue(string scratch)
    {
        var table = new (string Status, bool Queue, bool Resume)[] { ("Paused", true, true), ("Failed", true, true), ("Canceled", true, true), ("Completed", false, false), ("Downloading", false, false), ("Queued", false, false), ("Scheduled", false, false) };
        foreach (var (status, queue, resume) in table)
        {
            var state = RowMenu.For(new[] { RowOf(status) }, _ => true, _ => true);
            Check(state.AddToQueue == queue && state.Resume == resume, $"menu state for a {status} download: Add to queue {queue}, Resume {resume}");
        }
        var torrent = RowMenu.For(new[] { TorrentRow("Paused") }, _ => true, _ => true);
        Check(!torrent.AddToQueue && torrent.Resume, "a paused torrent keeps Resume but has no Add to queue (torrents have no queue state)");
        Check(RowMenu.For(new[] { TorrentRow("Paused"), RowOf("Paused") }, _ => true, _ => true).AddToQueue && !RowMenu.For(new[] { RowOf("Downloading"), RowOf("Completed") }, _ => true, _ => true).AddToQueue, "several rows: the entry is on when any of them can be queued");

        var m = CManager(scratch, "c17", 1); var settings = CSettings(Path.Combine(scratch, "c17", "files"));
        var w = CMain(m, settings); w.Show(); Pump(100);
        Wait(SeedPaused(m, "q1.bin", "q2.bin")); PumpUntil(() => w.Rows.Count == 2, "rows appear");
        var list = Part<ListView>(w, "DownloadList"); list.SelectedItem = w.Rows[0]; Pump(60);
        Check(w.UpdateRowMenu() && Part<MenuItem>(w, "CtxQueue").IsEnabled && Part<MenuItem>(w, "CtxResume").IsEnabled && Part<MenuItem>(w, "QueueMenuItem").IsEnabled, "right-click on a paused download: Add to queue and Resume now are both there and enabled; so is the Downloads menu entry");
        Check((string)Part<MenuItem>(w, "CtxResume").Header == Loc.T("main.menu.resume_now") && (string)Part<MenuItem>(w, "CtxQueue").Header == Loc.T("main.menu.add_to_queue") && Loc.T("main.menu.resume_now") != Loc.T("main.menu.add_to_queue"), "the two entries are worded differently");
        list.SelectedItems.Add(w.Rows[1]); Pump(40);
        Check(w.UpdateRowMenu() && Part<MenuItem>(w, "CtxQueue").IsEnabled, "multi-select keeps it enabled");
        Check(w.QueueRows(w.Rows.ToArray()) == 2, "Add to queue on two paused downloads queues both");
        PumpUntil(() => m.Jobs.All(j => j.Status != "Paused"), "both leave Paused");
        Check(Part<TextBlock>(w, "Notice").Text.Contains("Added to the queue: 2"), "the status line says what happened: " + Part<TextBlock>(w, "Notice").Text);
        Check(w.QueueRows(w.Rows.ToArray()) == 0 && Part<TextBlock>(w, "Notice").Text.Contains("Nothing to add"), "queued or failed-after-start rows are not queued twice by mistake");
        // the queued jobs may still be busy: skip the "quit while downloading?" question, which would wait for an answer forever
        typeof(MainWindow).GetField("_exitWithoutPrompt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w, true);
        w.Close(); m.Dispose();
        KeysInEveryLocale(new[] { "main.menu.add_to_queue", "main.menu.resume_now", "main.notice.queued", "main.notice.nothing_to_queue", "main.menu.show_checkboxes" }, "item 17");
        foreach (var tag in AllLanguages) { var l = AppLocale(tag); Check(l["main.menu.resume_now"] != l["main.menu.add_to_queue"], $"{tag}: 'Resume now' and 'Add to queue' read differently"); }
    }

    // ======================================================================== follow-up (c): one casing for Add to queue
    private static void QueueButtonCasing()
    {
        foreach (var tag in AllLanguages) { var l = AppLocale(tag); Check(l["capture.btn.add_to_queue"] == l["grab.btn.add_queue"], $"{tag}: the same Add to queue text in every dialog"); }
        Check(AppLocale("en")["capture.btn.add_to_queue"] == "Add to queue", "English casing is Add to queue");
        foreach (var xaml in new[] { "DownloadOptionsDialog", "StreamOptionsDialog", "YoutubeOptionsDialog", "PlaylistOptionsDialog" })
            Check(File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", xaml + ".xaml")).Contains("x:Name=\"QueueButton\" Content=\"{loc:T capture.btn.add_to_queue}\""), $"{xaml} uses the shared Add to queue text");
        Check(File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", "LinkGrabberWindow.xaml")).Contains("grab.btn.add_queue"), "the link grabber uses the shared text through its own key");
    }

    // ======================================================================== item 11
    private static void C11Taskbar(string scratch)
    {
        TaskbarProgressValue V(params DownloadRow[] rows) => TaskbarProgressModel.Compute(rows);
        Check(V().State == TaskbarState.None && V(RowOf("Completed")).State == TaskbarState.None, "nothing active or paused: no bar");
        Check(V(RowOf("Failed")).State == TaskbarState.None && V(RowOf("Failed"), RowOf("Completed")).State == TaskbarState.None, "failed jobs alone: no bar");
        Check(V(RowOf("Queued"), RowOf("Scheduled")).State == TaskbarState.None, "queued or scheduled jobs alone: no bar");
        var active = V(RowOf("Downloading", 1000, 250)); Check(active.State == TaskbarState.Normal && active.Percent == 25, "one active job: normal bar at its percent");
        var most = V(RowOf("Downloading", 1000, 250), RowOf("Downloading", 1000, 800), RowOf("Downloading", 1000, 100)); Check(most.State == TaskbarState.Normal && most.Percent == 80, "the most complete active job is shown");
        Check(V(RowOf("Downloading", 1000, 250), RowOf("Paused", 1000, 900)).Percent == 25, "active wins over paused");
        var paused = V(RowOf("Paused", 1000, 400), RowOf("Paused", 1000, 700), RowOf("Failed")); Check(paused.State == TaskbarState.Paused && paused.Percent == 70, "only paused jobs: the paused state at the most complete one");
        Check(V(RowOf("Downloading", 0, 5000)).State == TaskbarState.Indeterminate, "an active job without a known size: indeterminate");
        Check(V(RowOf("Downloading", 0, 5000), RowOf("Downloading", 1000, 500)).State == TaskbarState.Normal, "a known size among the active ones wins over an unknown one");
        var seedingRow = TorrentRow("Seeding", 1.0); Check(V(seedingRow).State == TaskbarState.None, "a seeding torrent is not progress");
        var torrentActive = V(TorrentRow("Downloading", 0.4), RowOf("Downloading", 1000, 100)); Check(torrentActive.State == TaskbarState.Normal && torrentActive.Percent == 40, "a torrent counts like any other download");
        Check(V(RowOf("Downloading", 1000, 1000)).Percent == 100 && V(RowOf("Downloading", 1000, 999)).Percent == 99, "percent never rounds up to 100 early");

        // throttle: at most once per second, unchanged values are skipped, a change waits for the next update, clear is immediate
        var sink = new RecSink(); var clock = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc); var up = new TaskbarProgressUpdater(sink, () => clock);
        var rows = new List<DownloadRow> { RowOf("Downloading", 1000, 100) };
        Check(up.Update(rows) && sink.Calls.Count == 1 && sink.Calls[0].State == TaskbarState.Normal && sink.Calls[0].Value == 0.10, "first update is applied");
        Check(!up.Update(rows) && sink.Calls.Count == 1, "an unchanged value is not applied again");
        rows[0] = RowOf("Downloading", 1000, 200); clock = clock.AddMilliseconds(400); Check(!up.Update(rows) && sink.Calls.Count == 1, "a change inside one second waits");
        clock = clock.AddMilliseconds(700); Check(up.Update(rows) && sink.Calls.Count == 2 && sink.Calls[1].Value == 0.20, "after a second the new value is applied");
        for (int i = 0; i < 30; i++) { rows[0] = RowOf("Downloading", 1000, 200 + i * 10); clock = clock.AddMilliseconds(100); up.Update(rows); }
        Check(sink.Calls.Count <= 2 + 3 + 1, $"30 refreshes in 3 seconds make at most 4 calls ({sink.Calls.Count - 2})");
        up.Clear(); Check(sink.Calls[^1] == (TaskbarState.None, 0), "clear removes the bar at once");
        int count = sink.Calls.Count; up.Reassert(); Check(sink.Calls.Count == count + 1 && sink.Calls[^1].State == TaskbarState.None, "reassert puts the last value on the button again");

        // the main window: its own updater, a fake sink, hide to tray and show again, clear on exit
        var m = CManager(scratch, "c11", 1); var settings = CSettings(Path.Combine(scratch, "c11", "files"));
        var w = CMain(m, settings); w.Show(); Pump(100);
        var fake = new RecSink(); w.Taskbar.Sink = fake; w.Taskbar.MinimumInterval = TimeSpan.Zero;
        Wait(SeedPaused(m, "p1.bin")); PumpUntil(() => w.Rows.Count == 1, "row appears"); Pump(700);
        Check(fake.Calls.Count > 0 && fake.Calls[^1].State == TaskbarState.Paused, "a paused download shows the paused state on the main window's button");
        int n = fake.Calls.Count; w.Hide(); Pump(60); w.Show(); Pump(100);
        Check(fake.Calls.Count > n && fake.Calls[^1].State == TaskbarState.Paused, "after hide to tray and show again the state is put back");
        w.Close(); Check(fake.Calls[^1] == (TaskbarState.None, 0), "closing the window clears the bar");
        m.Dispose();
        Check(new WindowTaskbarSink(new Window()) is ITaskbarSink && typeof(MainWindow).GetProperty("Taskbar") is not null, "the real sink is the WPF taskbar item of the main window only");
    }

    // ======================================================================== item 12
    private static void C12Chevron(string scratch)
    {
        try
        {
            foreach (var theme in new[] { "Light", "DeepNavy", "Midnight" })
                foreach (double scale in new[] { 1.0, 1.5, 2.0 })
                    foreach (bool rtl in new[] { false, true })
                    {
                        ThemeManager.Apply(Application.Current, theme, null);
                        var m = CManager(scratch, "c12" + theme + scale + rtl); var w = CMain(m, CSettings(Path.Combine(scratch, "c12")));
                        w.Show(); Pump(100); if (rtl) { w.FlowDirection = FlowDirection.RightToLeft; Pump(120); }
                        var nav = Part<NavListBox>(w, "Navigation"); var all = nav.Items.OfType<NavTreeItem>().First();
                        all.ApplyTemplate(); var chevron = (FrameworkElement)all.Template.FindName("Chevron", all); Check(chevron.Visibility == Visibility.Visible, "the All downloads entry has its chevron");
                        w.UpdateLayout();
                        var box = chevron.TransformToAncestor(all).TransformBounds(new Rect(0, 0, chevron.ActualWidth, chevron.ActualHeight));
                        string tag = $"{theme} x{scale}{(rtl ? " rtl" : "")}: ";
                        Check(Math.Abs((box.Top + box.Bottom) / 2 - all.ActualHeight / 2) <= 0.5, tag + $"the chevron box is centred on the entry vertically ({(box.Top + box.Bottom) / 2:0.0} of {all.ActualHeight:0.0})");
                        if (rtl) { var cs = chevron.PointToScreen(new Point(0, 0)); var es = all.PointToScreen(new Point(0, 0)); Check(cs.X < es.X, tag + "the chevron sits at the left end in right-to-left"); w.Close(); m.Dispose(); continue; }
                        // pixels: the stroke's own bounding box (left to right layout)
                        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth * scale), (int)Math.Ceiling(w.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bmp.Render(w);
                        var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4]; int stride = bmp.PixelWidth * 4; bmp.CopyPixels(px, stride, 0);
                        var origin = all.TransformToAncestor(w).Transform(new Point(0, 0));
                        var bg = Px(px, stride, (int)((origin.X + 3) * scale), (int)((origin.Y + all.ActualHeight / 2) * scale));
                        double left = all.ActualWidth, right = 0, top = all.ActualHeight, bottom = 0; bool any = false;
                        var cb = new Rect(box.Left, box.Top, box.Width, box.Height);
                        for (int y = (int)((origin.Y + all.ActualHeight / 2 - 9) * scale); y < (int)((origin.Y + all.ActualHeight / 2 + 9) * scale); y++) // the glyph is 14 high: the rows beyond it only hold the rounded corners of the entry
                            for (int x = (int)((origin.X + cb.Left) * scale); x < (int)((origin.X + cb.Right) * scale); x++)
                            {
                                var p = Px(px, stride, x, y); if (Math.Abs(p.R - bg.R) + Math.Abs(p.G - bg.G) + Math.Abs(p.B - bg.B) < 60) continue;
                                any = true; double lx = x / scale - origin.X, ly = y / scale - origin.Y; left = Math.Min(left, lx); right = Math.Max(right, lx); top = Math.Min(top, ly); bottom = Math.Max(bottom, ly);
                            }
                        Check(any, tag + "the chevron is drawn");
                        double cy = (top + bottom) / 2;
                        Check(Math.Abs(cy - all.ActualHeight / 2) <= 1.6, tag + $"the drawn chevron is vertically centred ({cy:0.0} vs {all.ActualHeight / 2:0.0})");
                        double inset = all.ActualWidth - right;
                        Check(inset >= 8 && inset <= 16, tag + $"the chevron's outer edge is {inset:0.0}px from the entry edge (bg {bg.R},{bg.G},{bg.B}; box {box.Left:0.0}..{box.Right:0.0}; ink {left:0.0}..{right:0.0} of {all.ActualWidth:0.0})");
                        Check(bottom - top >= 3 && right - left >= 5 && right - left <= 12, tag + $"the glyph keeps its size ({right - left:0.0} x {bottom - top:0.0})");
                        if (ShotsFolder is { Length: > 0 } f && scale == 1.0 && !rtl && theme is "Light" or "DeepNavy") SaveShot(w, f, $"c12-sidebar-{theme}.png");
                        // child rows keep their alignment: all category entries share one icon and text offset
                        var children = nav.Items.OfType<ListBoxItem>().Where(i => i is not NavTreeItem && i.Tag is string t && t.StartsWith(CategoryTree.FilterPrefix)).ToArray();
                        if (children.Length > 1)
                        {
                            double X(ListBoxItem i) { i.ApplyTemplate(); var host = (FrameworkElement)i.Template.FindName("IconHost", i); return rtl ? i.ActualWidth - host.TranslatePoint(new Point(host.ActualWidth, 0), i).X : host.TranslatePoint(new Point(0, 0), i).X; }
                            Check(children.Select(X).Distinct().Count() == 1 && children.All(c => c.ActualHeight > 20), tag + "the category rows still line up with each other");
                        }
                        w.Close(); m.Dispose();
                    }
        }
        finally { ThemeManager.Apply(Application.Current, "Light", null); }
    }
}
