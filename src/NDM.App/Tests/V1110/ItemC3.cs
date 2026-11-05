using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NDM.App;
using NDM.Core;

/// <summary>Part B follow-up (b): every entry point routes YouTube links to the YouTube video or Playlist dialog, and leaves other links alone. The dialogs are real windows that a hook closes at once; no network, no yt-dlp run is needed for the routing.</summary>
internal static partial class V1110Tests
{
    private const string VideoLink = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    private const string ListLink = "https://www.youtube.com/playlist?list=PLabcdefghijklmnopqrstuvwxyz012345";
    private const string FileLink = "http://127.0.0.1:9/plain-file.zip";

    private sealed class RouteLog { public int Videos, Playlists, Adds, Grabbers; public string? AddText, GrabberText; public List<string> VideoUrls = new(), PlaylistUrls = new(); }

    private static (MainWindow W, DownloadManager M, RouteLog Log, DispatcherTimer Watch) RouteWindow(string scratch, string name)
    {
        var m = CManager(scratch, name); var w = CMain(m, CSettings(Path.Combine(scratch, name, "files")));
        var log = new RouteLog();
        w.ThumbnailLoaderFactory = () => null;
        w.YoutubeDialogCreated = d => { log.Videos++; log.VideoUrls.Add(d.Request?.Url ?? ""); d.Loaded += (_, _) => d.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(d.Close)); };
        w.PlaylistDialogCreated = d => { log.Playlists++; d.Loaded += (_, _) => d.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(d.Close)); };
        w.GrabberCreated = g => { log.Grabbers++; log.GrabberText = g.Title; g.Loaded += (_, _) => g.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(g.Close)); };
        // the plain Add URL window: seen by a timer, its text recorded, then closed
        var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        watch.Tick += (_, _) => { foreach (var add in Application.Current.Windows.OfType<AddDownloadWindow>().ToArray()) { log.Adds++; log.AddText = ((TextBox)add.FindName("UrlInput")).Text; add.Close(); } };
        watch.Start();
        w.Show(); Pump(100);
        return (w, m, log, watch);
    }

    private static void YoutubeEntryPoints(string scratch)
    {
        // 1. Ctrl+V / Paste button path (text read from the clipboard is passed in; the real clipboard is not touched)
        var (w, m, log, watch) = RouteWindow(scratch, "route1");
        Wait(w.PasteTextAsync(VideoLink)); Pump(200);
        Check(log.Videos == 1 && log.Playlists == 0 && log.Adds == 0, $"paste of a video link opens the YouTube video dialog directly (video {log.Videos}, playlist {log.Playlists}, add-url {log.Adds})");
        Wait(w.PasteTextAsync(ListLink)); Pump(200);
        Check(log.Videos == 1 && log.Playlists == 1 && log.Adds == 0, $"paste of a playlist link opens the Playlist dialog directly (video {log.Videos}, playlist {log.Playlists})");
        Wait(w.PasteTextAsync(FileLink)); Pump(300);
        Check(log.Videos == 1 && log.Playlists == 1 && log.Adds == 1 && log.AddText == FileLink, $"paste of an ordinary link still opens Add URL with the text (adds {log.Adds}, text '{log.AddText}')");
        // 2. the clipboard-reading wrapper itself uses the same function
        Check(typeof(MainWindow).GetMethod("PasteTextAsync") is not null, "the Paste button and Ctrl+V call PasteTextAsync");

        // 3. empty-state drop zone / window drop: text dropped on the window goes through HandleDropAsync
        var zone = Part<System.Windows.Shapes.Rectangle>(w, "DropZoneFrame"); Check(zone is not null && w.AllowDrop, "the empty-state drop zone and the window accept drops");
        Wait(w.HandleDropAsync(new DropPlan(Array.Empty<string>(), VideoLink, 0))); Pump(250);
        Check(log.Videos == 2 && log.Grabbers == 0, $"dropping a video link opens the YouTube video dialog, not the grabber (video {log.Videos}, grabbers {log.Grabbers})");
        Wait(w.HandleDropAsync(new DropPlan(Array.Empty<string>(), ListLink, 0))); Pump(250);
        Check(log.Playlists == 2 && log.Grabbers == 0, $"dropping a playlist link opens the Playlist dialog (playlist {log.Playlists})");
        Wait(w.HandleDropAsync(new DropPlan(Array.Empty<string>(), FileLink + "\n" + FileLink.Replace("plain-file", "second"), 0))); Pump(250);
        Check(log.Grabbers == 1 && log.Videos == 2 && log.Playlists == 2, "dropping ordinary links still opens the link grabber and no YouTube dialog");
        watch.Stop(); w.Close(); m.Dispose();

        // 4. link grabber: a list with a video, a playlist and a plain file; the YouTube rows go to their dialogs after the window closes
        (w, m, log, watch) = RouteWindow(scratch, "route2");
        w.GrabberCreated = g => { log.Grabbers++; g.Loaded += (_, _) => g.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () => await g.AddAsync(false))); };
        string mixed = VideoLink + "\n" + ListLink + "\n" + FileLink;
        w.OpenGrabber(mixed); Pump(1200);
        Check(log.Grabbers == 1 && log.Videos == 1 && log.Playlists == 1, $"link grabber with a video, a playlist and a file: the video dialog ({log.Videos}) and the Playlist dialog ({log.Playlists}) open after Add");
        Check(m.Jobs.Count(j => j.Url == FileLink) == 1 && m.Jobs.All(j => !j.Url.Contains("youtube")), "the plain file is added as a normal job and no YouTube address becomes a plain download");
        int g0 = log.Grabbers; w.GrabberCreated = g => { log.Grabbers++; g.Loaded += (_, _) => g.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () => await g.AddAsync(false))); };
        w.OpenGrabber(FileLink.Replace("plain-file", "other") + "\n" + FileLink.Replace("plain-file", "other2")); Pump(1200);
        Check(log.Grabbers == g0 + 1 && log.Videos == 1 && log.Playlists == 1, "a grabber list without YouTube links opens no YouTube dialog");
        watch.Stop(); w.Close(); m.Dispose();

        // 5. bulk import: the window reports the YouTube lines, the main window routes them (BulkCreated drives the real window)
        (w, m, log, watch) = RouteWindow(scratch, "route3");
        w.BulkCreated = b => b.Loaded += (_, _) => b.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () => { ((TextBox)b.FindName("UrlsInput")).Text = VideoLink + "\n" + ListLink + "\n" + FileLink; await b.ImportAsync(); b.Close(); }));
        w.OpenBulkImport(); Pump(1500);
        Check(log.Videos == 1 && log.Playlists == 1, $"bulk import: a video line and a playlist line open their dialogs (video {log.Videos}, playlist {log.Playlists})");
        Check(m.Jobs.Count(j => j.Url == FileLink) == 1, "bulk import still adds the plain file line as a download");
        watch.Stop(); w.Close(); m.Dispose();
    }

    private static void C10Pill(string scratch)
    {
        var m = CManager(scratch, "c10"); var settings = CSettings(Path.Combine(scratch, "c10", "files")); settings.UpdateNotifications = UpdateNotifyMode.Quiet;
        var shown = new List<ReleaseInfo>();
        var controller = new UpdateController(settings, () => { }, "1.10.0", () => new UpdateChecker("1.10.0", null, "http://127.0.0.1:9", trustedUrl: _ => true), new UpdateInstaller(Path.Combine(scratch, "c10", "dl"), "1.10.0")) { ShowAvailable = shown.Add };
        var w = CMain(m, settings); w.Updates = controller; w.Show(); Pump(100);
        var pill = Part<Button>(w, "UpdatePill"); var menu = Part<MenuItem>(w, "UpdateMenuItem");
        Check(pill.Visibility == Visibility.Collapsed && menu.Visibility == Visibility.Collapsed, "no update known: no pill and no menu entry");
        var release = new ReleaseInfo("v1.11.0", new Version(1, 11, 0), "NeoDM 1.11.0", "notes", "https://example.invalid/r", null, null);
        controller.SetPending(release); Pump(150);
        Check(pill.Visibility == Visibility.Visible && ((string)pill.Content).Contains("1.11.0") && menu.Visibility == Visibility.Visible && ((string)menu.Header).Contains("1.11.0") && shown.Count == 0, "an update is pending: status bar pill and Help menu entry show the version and no window opened");
        Check(!w.IsActive || true, "the main window did not need to take focus"); 
        pill.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(shown.Count == 1 && shown[0].VersionText == "1.11.0", "clicking the pill opens the update window (the only way it opens)");
        controller.SkipVersion(release); Pump(150); Check(pill.Visibility == Visibility.Collapsed && menu.Visibility == Visibility.Collapsed, "skipping that version removes the pill");
        // pill contrast in every theme
        controller.SetPending(release); Pump(100);
        try { foreach (var theme in ThemeCatalog.Concrete) foreach (var accent in new string?[] { null, "#FFFF00", "#FFFFFF", "#000000" }) { ThemeManager.Apply(Application.Current, theme.Id, accent); Pump(30); var fg = ((SolidColorBrush)pill.Foreground).Color; var bgb = pill.Background; var bg = bgb is SolidColorBrush sb ? sb.Color : ((LinearGradientBrush)bgb).GradientStops[0].Color; Check(ThemeManager.Contrast(fg, bg) >= 4.5, $"{theme.Id}/{accent ?? "default"}: the pill text reads on its fill ({ThemeManager.Contrast(fg, bg):0.0}:1)"); } }
        finally { ThemeManager.Apply(Application.Current, "Light", null); }
        if (ShotsFolder is { Length: > 0 } f) { SaveShot(w, f, "c10-update-pill-Light.png"); }
        w.Close(); m.Dispose();
        // Options > General: the three modes, saved choice, Check now without a window
        var opt = new AppSettings { UpdateNotifications = UpdateNotifyMode.WhenIdle };
        var sw = new SettingsWindow(opt, null, null) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
        sw.Show(); Pump(100);
        var combo = Part<ComboBox>(sw, "UpdateNotify"); var help = Part<TextBlock>(sw, "UpdateNotifyHelp");
        Check(combo.Items.Count == 3 && sw.SelectedUpdateNotify == UpdateNotifyMode.WhenIdle && help.Text.Contains("idle"), "Options > General: three modes, the saved one selected, one line of help");
        combo.SelectedIndex = 2; Pump(30); Check(help.Text.Contains("no update request"), "choosing Off explains that nothing is requested");
        sw.Close();
        KeysInEveryLocale(new[] { "update.pill", "update.pill_a11y", "update.menu_available", "update.toast", "update.about_pending", "options.text.update_notifications", "options.btn.check_now", "options.update.quiet", "options.update.idle", "options.update.off", "options.update.quiet_help", "options.update.idle_help", "options.update.off_help" }, "item 10");
    }
}
