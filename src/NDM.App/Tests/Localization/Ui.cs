using System.ComponentModel;
using System.Text.RegularExpressions;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static partial class LocalizationTests
{
    internal static IEnumerable<T> Visual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Visual<T>(child)) yield return nested;
        }
    }
    private static T Named<T>(Window w, string name) where T : class => (T)(w.FindName(name) ?? throw new Exception("missing control " + name));
    private static T Sync<T>(Task<T> task) { var end = DateTime.UtcNow.AddSeconds(20); while (!task.IsCompleted) { if (DateTime.UtcNow > end) throw new TimeoutException(); Pump(40); } return task.GetAwaiter().GetResult(); }
    private static DownloadManager NewManager(string root, string label) =>
        new(Path.Combine(root, label, "files"), Path.Combine(root, label, "state"), new DownloadOptions { RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(4) });
    private static ReleaseInfo ReleaseInfoFor(string version) => UpdateChecker.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "release-latest.json")).Replace("1.3.1", version))!;

    private sealed class FakeDog : IBridgeWatchdog
    {
        public WatchdogReport? Last { get; set; } = new(WatchdogOverall.Problem, new[]
        {
            new CheckItem("listener", "App listener", CheckState.Ok, "NeoDM is listening for the browser.", "The pipe NDM.Downloads answered the self check."),
            new CheckItem("reg-chrome", "Chrome registration", CheckState.Problem, "Chrome points to a NeoDM file that is missing.", "C:\\NeoDM\\host\\com.ndm.bridge.json"),
            new CheckItem("reg-edge", "Edge registration", CheckState.NotInstalled, "Edge is not installed."),
            new CheckItem("host", "Host start test", CheckState.Ok, "The browser helper starts and answers.", "Answered in 40 ms."),
        }, new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc), new[] { "Registered NeoDM for Brave for the current user." });
        public bool IsRunning => false;
#pragma warning disable CS0067
        public event Action<WatchdogReport>? ReportChanged;
#pragma warning restore CS0067
        public Task<WatchdogReport> RunAsync(WatchdogRunMode mode) => Task.FromResult(Last!);
    }

    private sealed record Case(string Name, Func<Window> Make);

    private static List<Case> Cases(string root, DownloadManager manager, DownloadJob job)
    {
        var meta = new NDM.Torrent.TorrentMetadata("id", "Pack", 600, new[] { new NDM.Torrent.TorrentMetadataFile("Pack/a/x.bin", 100), new NDM.Torrent.TorrentMetadataFile("Pack/z.bin", 500) });
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "layout", "files") };
        var torrents = new TorrentController(Path.Combine(root, "layout", "torrent"), settings);
        return new List<Case>
        {
            new("Progress", () => new DownloadProgressWindow(manager, job.Id, new JobCompletionStore(), new AppSettings())),
            new("Main", () => new MainWindow(manager, settings, () => { }, enableTray: false)),
            new("Options", () => new SettingsWindow(new AppSettings { DownloadDirectory = Path.Combine(root, "layout") }, Path.Combine(root, "layout-state"))),
            new("Add download", () => new AddDownloadWindow("https://example.org/file.zip")),
            new("Capture dialog", () => new DownloadOptionsDialog("https://example.org/dir/file.zip", "file.zip", "", "", root)),
            new("Bulk import", () => new BulkImportWindow(manager)),
            new("Job options", () => new JobOptionsWindow(manager, job.Id, false)),
            new("Properties", () => new PropertiesWindow(new DownloadRow(job))),
            new("Download complete", () => new DownloadCompleteWindow(job, new AppSettings(), () => { })),
            new("Add torrent", () => new TorrentAddWindow(new AppSettings { DownloadDirectory = root }, "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", null, (_, _, _) => Task.FromResult(meta))),
            new("Torrent details", () => new TorrentDetailsWindow(torrents, "missing")),
            new("Remove confirm", () => new RemoveConfirmWindow(new[] { new RemoveItem("file-a.zip", "C:\\Downloads\\file-a.zip", 1, false), new RemoveItem("file-b.iso", "C:\\Downloads\\file-b.iso", 1, false), new RemoveItem("file-c.mp4", "C:\\Downloads\\file-c.mp4", 1, false) })),
            new("Open confirm", () => new OpenFileConfirmWindow("a-very-long-holiday-movie-file-name-with-many-words-and-numbers-2026-final-cut-director-version-part-1-of-3.mp4", OpenKind.Video)),
            new("Open confirm (program)", () => new OpenFileConfirmWindow("setup.exe", null)),
            new("Update", () => new UpdateWindow(ReleaseInfoFor("1.3.1"), new UpdateController(new AppSettings(), () => { }, "1.3.0", () => throw new InvalidOperationException(), new UpdateInstaller(Path.Combine(root, "u"), "1.3.0")))),
            new("About", () => new AboutWindow()),
            new("Extension guide", () => new ExtensionGuideWindow("1.4.4", new ExtensionFolder(ExtensionFolderKind.Development, @"F:\proj\extension"))),
            new("Extension notice", () => new ExtensionNoticeWindow(new ExtensionCheck(ExtensionStatus.Older, "1.0.0", "1.4.4"), _ => { })),
            new("Text prompt", () => new TextPromptWindow("Rename", "New name", "file.zip", _ => null)),
            new("Countdown", () => new CountdownWindow(CompletionActionKind.Shutdown, 30)),
            new("Bridge status", () => new BridgeStatusWindow(new FakeDog())),
        };
    }

    private static Window Open(Case c, Rect work)
    {
        var w = c.Make(); w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = 0; w.Top = 0; w.Owner = null;
        WindowSizing.Fit(w, work); w.Show(); Pump(150); w.UpdateLayout(); return w;
    }
    private static void CloseWindow(Window w)
    {
        if (w is MainWindow) typeof(MainWindow).GetField("_exitWithoutPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(w, true);
        w.Close();
    }

    // ===== live switching =====
    private static void LiveSwitching(string root)
    {
        using var manager = NewManager(root, "live");
        var job = Sync(manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/some/file.iso"), DateTimeOffset.UtcNow.AddDays(1)));
        Loc.SetLanguage("en");
        var about = new AboutWindow(); about.ShowInTaskbar = false; about.Show(); Pump(100);
        var main = new MainWindow(manager, new AppSettings { DownloadDirectory = Path.Combine(root, "live", "files") }, () => { }, enableTray: false); main.ShowInTaskbar = false; main.Show(); Pump(150);
        try
        {
            Check(about.Title == English("about.title.about_neodm") && about.FlowDirection == FlowDirection.LeftToRight, "About window starts in English, left to right");
            var changes = new List<string?>(); PropertyChangedEventHandler h = (_, e) => changes.Add(e.PropertyName);
            ((INotifyPropertyChanged)Loc.Source).PropertyChanged += h;
            int languageEvents = 0; Action onLang = () => languageEvents++; Loc.LanguageChanged += onLang;
            Check(Loc.SetLanguage("ar"), "switching to Arabic succeeds");
            Pump(150);
            Loc.LanguageChanged -= onLang; ((INotifyPropertyChanged)Loc.Source).PropertyChanged -= h;
            var ar = ReadLocale(Path.Combine(LocalesDir, "ar.json"));
            Check(changes.SequenceEqual(new string?[] { "Item[]" }) && languageEvents == 1, $"one switch raises PropertyChanged(Item[]) once and LanguageChanged once ({string.Join(",", changes)}; {languageEvents})");
            Check(about.Title == ar["about.title.about_neodm"], "open About window title changed live");
            Check(about.FlowDirection == FlowDirection.RightToLeft && main.FlowDirection == FlowDirection.RightToLeft, "open windows flipped to right-to-left live");
            var headers = Visual<MenuItem>(main).Select(m => m.Header as string).Where(s => s is not null).ToList();
            Check(headers.Contains(ar["main.menu.tasks"]) && headers.Contains(ar["main.menu.help"]) && !headers.Contains(English("main.menu.tasks")), "main menu headers changed live");
            var buttons = Visual<ButtonBase>(main).Select(b => b.Content as string).ToList();
            Check(buttons.Contains(ar["main.btn.options"]) || buttons.Contains(ar["main.btn.remove"]) || buttons.Contains(ar["main.btn.resume"]), "toolbar button text changed live");
            var columns = Visual<GridViewColumnHeader>(main).Select(c => c.Content as string).ToList();
            Check(columns.Contains(ar["main.btn.filename"]) || columns.Contains(ar["main.btn.size"]), $"column headers changed live ({string.Join(",", columns.Where(c => c is not null))})");
            Check(main.Dispatcher.Invoke(() => (string?)typeof(MainWindow).GetProperty("Title")!.GetValue(main)) is { Length: > 0 }, "main window still has a title");
            Check(Visual<TextBlock>(main).Any(t => t.Text == ar["main.status.ready"] || t.Text.Contains(ar["main.summary.empty"]) || t.Text.Contains("تنزيل")), "status and summary text changed live");
            var row = new DownloadRow(job); Check(row.StatusText == ar["status.scheduled"], "row status text is localised");
            Check(Loc.SetLanguage("en"), "switching back to English succeeds"); Pump(150);
            Check(about.Title == English("about.title.about_neodm") && about.FlowDirection == FlowDirection.LeftToRight && main.FlowDirection == FlowDirection.LeftToRight, "switching back restores text and left-to-right");
            Check(!Loc.SetLanguage("xx") && !Loc.SetLanguage("it") && Loc.Language == "en", "an unknown language or one without a file is refused and changes nothing");
        }
        finally { about.Close(); CloseWindow(main); Loc.SetLanguage("en"); }

        // New windows pick up the active language at creation.
        Loc.SetLanguage("ar");
        try
        {
            var w = new AboutWindow(); w.ShowInTaskbar = false; w.Show(); Pump(100);
            Check(w.FlowDirection == FlowDirection.RightToLeft && w.Language.IetfLanguageTag.StartsWith("en"), "a window created in Arabic is right-to-left with the English XML language tag (an Arabic tag breaks number direction)");
            Check(w.FontFamily.Source.Contains("Segoe UI") && w.FontFamily.Source.Contains("Tahoma"), $"Arabic font chain applied to windows ({w.FontFamily.Source})");
            w.Close();
        }
        finally { Loc.SetLanguage("en"); }
    }

    // ===== popups: context menus, tooltips and combo lists follow the window =====
    private static void PopupsFollowWindow()
    {
        Loc.SetLanguage("ar");
        try
        {
            var button = new Button { Content = "x", Width = 80, Height = 30, ToolTip = new ToolTip { Content = "tip" } };
            var menu = new ContextMenu { Items = { new MenuItem { Header = "one" } } }; button.ContextMenu = menu;
            var combo = new ComboBox { Items = { "a", "b" }, Width = 80 };
            var panel = new StackPanel { Children = { button, combo } };
            var w = new Window { Content = panel, Width = 300, Height = 200, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            w.Show(); Pump(150);
            try
            {
                Check(w.FlowDirection == FlowDirection.RightToLeft, "an ad-hoc window created in Arabic is mirrored by the class handler");
                menu.PlacementTarget = button; menu.IsOpen = true; Pump(150);
                Check(menu.FlowDirection == FlowDirection.RightToLeft, "context menus open right-to-left in Arabic"); menu.IsOpen = false;
                var tip = (ToolTip)button.ToolTip; tip.PlacementTarget = button; tip.IsOpen = true; Pump(150);
                Check(tip.FlowDirection == FlowDirection.RightToLeft, "tooltips open right-to-left in Arabic"); tip.IsOpen = false;
                combo.IsDropDownOpen = true; Pump(150);
                Check(((Visual?)combo.Template.FindName("PART_Popup", combo) is System.Windows.Controls.Primitives.Popup popup && ((FrameworkElement)popup.Child).FlowDirection == FlowDirection.RightToLeft), "combo box lists open right-to-left in Arabic"); combo.IsDropDownOpen = false;
            }
            finally { w.Close(); }
        }
        finally { Loc.SetLanguage("en"); }
    }

    // ===== RTL flags: what mirrors and what stays left-to-right =====
    private static void RtlFlip(string root)
    {
        using var manager = NewManager(root, "rtl");
        var job = Sync(manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/some/file.iso"), DateTimeOffset.UtcNow.AddDays(1)));
        Loc.SetLanguage("ar");
        try
        {
            var work = new Rect(0, 0, 1920, 1040);
            foreach (var c in Cases(root, manager, job))
            {
                var w = Open(c, work);
                try
                {
                    Check(w.FlowDirection == FlowDirection.RightToLeft, $"{c.Name}: window root is right-to-left in Arabic");
                    var bad = new List<string>();
                    foreach (var tb in Visual<TextBox>(w).Where(t => t.IsVisible && !t.IsReadOnly || t is { IsVisible: true, Name: "UrlBox" or "PathBox" }))
                    {
                        string name = tb.Name ?? "";
                        bool ltrField = Regex.IsMatch(name, "(?i)url|path|folder|hash|hex|accent|port|host|limit|segment|timeout|retry|date|time|address|connections|program|argument|magnet|log|ratio") || tb.Tag as string == "ltr";
                        if (ltrField && tb.FlowDirection != FlowDirection.LeftToRight) bad.Add(name + " should be LTR");
                    }
                    Check(bad.Count == 0, $"{c.Name}: URL, path, number and hex boxes stay left-to-right ({string.Join("; ", bad)})");
                    var barBad = Visual<ProgressBar>(w).Where(p => p.IsVisible && p.FlowDirection != FlowDirection.RightToLeft).Select(p => p.Name).ToList();
                    Check(barBad.Count == 0, $"{c.Name}: progress bars fill from the right (mirrored) ({string.Join(",", barBad)})");
                }
                finally { CloseWindow(w); }
            }
        }
        finally { Loc.SetLanguage("en"); }
    }
}
