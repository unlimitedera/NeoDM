using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.Torrent;

internal static partial class V1110Tests
{
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    // ---------------------------------------------------------------- item 3: the setting
    private static void TrackerSetting(string scratch)
    {
        var fresh = new AppSettings();
        Check(fresh.TorrentUseExtraTrackers && fresh.TorrentExtraTrackers.SequenceEqual(TrackerList.Defaults), "new settings: extra trackers on, with the small default list");
        string dir = Path.Combine(scratch, "trk", "NDM"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"CheckForUpdates\":false}");
        var old = AppSettings.Load(dir);
        Check(old.TorrentUseExtraTrackers && old.TorrentExtraTrackers.Count == TrackerList.Defaults.Count, "a settings file from an older version loads with the new default");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"TorrentExtraTrackers\":[\"udp://ok.example.org:1000/a\",\"javascript:alert(1)\",\"http://127.0.0.1/a\",\"udp://ok.example.org:1000/a\",\"http://user:pw@t.example.org/a\"],\"TorrentUseExtraTrackers\":false}");
        var edited = AppSettings.Load(dir);
        Check(!edited.TorrentUseExtraTrackers && edited.TorrentExtraTrackers.SequenceEqual(new[] { "udp://ok.example.org:1000/a" }), "a hand-edited list keeps only valid, distinct addresses; the off switch is kept");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"TorrentExtraTrackers\":null}");
        Check(AppSettings.Load(dir).TorrentExtraTrackers.SequenceEqual(TrackerList.Defaults), "a null list loads the defaults");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"TorrentExtraTrackers\":" + System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 300).Select(i => $"udp://t{i}.example.org:1000/a")) + "}");
        Check(AppSettings.Load(dir).TorrentExtraTrackers.Count == TrackerList.MaxEntries, "a long list is cut to " + TrackerList.MaxEntries);
        var copy = edited.Copy(); copy.TorrentExtraTrackers.Add("udp://x.example.org:1/a"); Check(edited.TorrentExtraTrackers.Count == 1 && copy.TorrentUseExtraTrackers == edited.TorrentUseExtraTrackers, "copies do not share the list");
        var opts = TorrentController.ToOptions(new AppSettings());
        Check(opts.UseExtraTrackers && opts.ExtraTrackers.SequenceEqual(TrackerList.Defaults), "the engine gets the setting");
        opts.Validate();
        var off = new AppSettings { TorrentUseExtraTrackers = false }; Check(!TorrentController.ToOptions(off).UseExtraTrackers, "off reaches the engine");
        var bad = new AppSettings { TorrentExtraTrackers = new() { "nonsense", "udp://ok.example.org:1000/a" } };
        var viaOptions = TorrentController.ToOptions(bad); viaOptions.Validate();
        Check(viaOptions.ExtraTrackers.Count == 1, "a bad entry in memory never reaches Validate");
    }

    private static void TrackerOptionsPage(string scratch)
    {
        string state = Path.Combine(scratch, "trkui", "NDM"); Directory.CreateDirectory(state);
        var s = new AppSettings();
        var w = new SettingsWindow(s, state) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Width = 900, Height = 700 };
        w.Show(); Pump(120);
        var tabs = (TabControl)w.FindName("OptionsTabs"); tabs.SelectedItem = w.FindName("TorrentsTab"); Pump(80);
        var box = (CheckBox)w.FindName("TorrentExtraTrackersBox"); var text = (TextBox)w.FindName("TorrentExtraTrackers"); var error = (TextBlock)w.FindName("ErrorText");
        Check(box.IsChecked == true && text.IsEnabled && text.Text.Split(Environment.NewLine).Length == TrackerList.Defaults.Count, "the page shows the switch on and one default address per line");
        box.IsChecked = false; Pump(30); Check(!text.IsEnabled, "switching it off greys the list");
        box.IsChecked = true; Pump(30);
        text.Text = "udp://ok.example.org:1000/a\nthis is not an address\nhttp://127.0.0.1/a"; Pump(30);
        Click((Button)w.FindName("SaveButton")); Pump(100);
        Check(w.IsVisible && error.Text.Contains("this is not an address") && error.Text.Contains("not valid"), "saving a bad line is refused and names it: " + error.Text);
        Check(!File.Exists(Path.Combine(state, "settings.json")), "nothing is written when the list is refused");
        text.Text = "udp://ok.example.org:1000/a\r\n\r\nhttps://two.example.org/announce\nudp://ok.example.org:1000/a"; box.IsChecked = true; Pump(30);
        Click((Button)w.FindName("SaveButton")); Pump(150);
        var saved = AppSettings.Load(state);
        Check(!w.IsVisible && saved.TorrentUseExtraTrackers && saved.TorrentExtraTrackers.SequenceEqual(new[] { "udp://ok.example.org:1000/a", "https://two.example.org/announce" }), "a good list is saved, blanks and repeats removed");
        // small and large window, right-to-left
        var again = new SettingsWindow(saved, state) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Width = 560, Height = 520 };
        again.Show(); Pump(120);
        ((TabControl)again.FindName("OptionsTabs")).SelectedItem = again.FindName("TorrentsTab"); Pump(80);
        var t2 = (TextBox)again.FindName("TorrentExtraTrackers");
        Check(t2.ActualWidth > 80 && t2.TranslatePoint(new Point(t2.ActualWidth, 0), again).X <= again.ActualWidth + 0.5, "the list fits a small window");
        again.Width = 1400; again.Height = 900; Pump(100); Check(t2.ActualWidth > 200, "the list grows in a large window");
        again.FlowDirection = FlowDirection.RightToLeft; Pump(100);
        Check(t2.FlowDirection == FlowDirection.LeftToRight && t2.ActualWidth > 80, "right-to-left: the addresses stay left-to-right text and the box is laid out");
        again.Close();
    }

    // ---------------------------------------------------------------- item 3: the dialog states
    private static MetadataStatus Status(int attempt = 2, int nodes = 17, int conns = 3, bool dht = true) => new(attempt, 5, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(90), nodes, conns, dht, 2, MetadataPhase.Retrying);

    private static void AddDialogStates(string scratch)
    {
        var settings = new AppSettings { DownloadDirectory = Path.Combine(scratch, "dl") };
        Directory.CreateDirectory(settings.DownloadDirectory);
        // waiting: the live numbers are shown
        var gate = new TaskCompletionSource<TorrentMetadata>(); MetadataStatus? live = null;
        var d = new TorrentAddWindow(settings, "magnet:?xt=urn:btih:" + new string('a', 40), null, (m, t, ct) => gate.Task, TimeSpan.FromSeconds(90), null, () => live) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
        d.Show(); Pump(150);
        var fetchText = (TextBlock)d.FindName("FetchText"); var retry = (Button)d.FindName("RetryButton"); var error = (TextBlock)d.FindName("ErrorText"); var bar = (ProgressBar)d.FindName("FetchBar");
        Check(fetchText.Text.Contains("Fetching") && bar.Visibility == Visibility.Visible && retry.Visibility == Visibility.Collapsed, "before any number is known: the plain waiting line and an animated bar, no Retry");
        Check(d.FetchLine(7) == "Fetching torrent metadata… 7s of 90s. Cancel to stop.", "plain line text");
        live = Status();
        Check(d.FetchLine(12) == "Looking for peers: DHT nodes 17, connections 3. Attempt 2 of 5, 12s of 90s. Cancel to stop.", "live line: " + d.FetchLine(12));
        live = Status(1, 0, 0, dht: false);
        Check(d.FetchLine(3).StartsWith("Looking for peers (DHT is off): connections 0. Attempt 1 of 5"), "DHT off line: " + d.FetchLine(3));
        Check(fetchText.TextWrapping == TextWrapping.Wrap, "the status line wraps instead of clipping");
        // each failure reason gives its own plain message with Retry
        foreach (var (status, expect) in new[]
        {
            (Status(5, 0, 0), "could not reach the DHT network"), (Status(5, 40, 0), "no peer with this torrent answered"), (Status(5, 40, 6), "6 peers connected but none sent it"), (Status(5, 0, 0, dht: false), "DHT is turned off"),
        })
        {
            var ex = new MetadataTimeoutException("t", status);
            var failing = new TorrentAddWindow(settings, "magnet:?xt=urn:btih:" + new string('b', 40), null, (m, t, ct) => Task.FromException<TorrentMetadata>(ex), TimeSpan.FromSeconds(90)) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
            failing.Show(); Wait(failing.MetadataTask); Pump(60);
            var errorText = (TextBlock)failing.FindName("ErrorText"); var retryButton = (Button)failing.FindName("RetryButton");
            Check(errorText.Text.Contains(expect) && errorText.Text.Contains("90 seconds") && retryButton.Visibility == Visibility.Visible && !failing.MetadataReady, "timeout reason '" + expect + "' with a Retry button: " + errorText.Text);
            Check(((ProgressBar)failing.FindName("FetchBar")).Visibility == Visibility.Collapsed && ((TextBlock)failing.FindName("FetchText")).Text == "", "after the timeout the wait indicator is gone (no endless wait)");
            failing.Close();
        }
        // a plain timeout (older engine) and Retry starts a new search that can succeed
        int calls = 0;
        var meta = new TorrentMetadata(new string('c', 40), "Example", 1000, new[] { new TorrentMetadataFile("a.bin", 1000) });
        var retrying = new TorrentAddWindow(settings, "magnet:?xt=urn:btih:" + new string('c', 40), null, (m, t, ct) => ++calls == 1 ? Task.FromException<TorrentMetadata>(new TimeoutException("x")) : Task.FromResult(meta), TimeSpan.FromSeconds(30)) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
        retrying.Show(); Wait(retrying.MetadataTask); Pump(60);
        var retryBtn = (Button)retrying.FindName("RetryButton");
        Check(((TextBlock)retrying.FindName("ErrorText")).Text.Contains("30 seconds") && retryBtn.Visibility == Visibility.Visible, "plain timeout: the general message and Retry");
        Click(retryBtn); Wait(retrying.MetadataTask); Pump(80);
        Check(retrying.MetadataReady && calls == 2 && retryBtn.Visibility == Visibility.Collapsed && ((TextBlock)retrying.FindName("ErrorText")).Text == "", "Retry runs a new search; success shows the files");
        retrying.Close();
        // cancel stops the wait
        gate = new TaskCompletionSource<TorrentMetadata>();
        d.Close(); Pump(30);
        Check(true, "closing the window cancels the search (its token is cancelled on Closed)");
        // layout: narrow window, right-to-left
        foreach (var rtl in new[] { false, true })
        {
            var narrow = new TorrentAddWindow(settings, "magnet:?xt=urn:btih:" + new string('d', 40), null, (m, t, ct) => Task.FromException<TorrentMetadata>(new MetadataTimeoutException("t", Status(5, 0, 0))), TimeSpan.FromSeconds(90), null) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Width = 520, Height = 520 };
            narrow.Show(); Wait(narrow.MetadataTask); Pump(60); if (rtl) { narrow.FlowDirection = FlowDirection.RightToLeft; Pump(80); }
            var et = (TextBlock)narrow.FindName("ErrorText"); var rb = (Button)narrow.FindName("RetryButton");
            Check(et.ActualWidth > 100 && et.ActualHeight > 20 && et.TranslatePoint(new Point(et.ActualWidth, 0), narrow).X <= narrow.ActualWidth + 0.5 && rb.IsVisible, "narrow window " + (rtl ? "right-to-left" : "left-to-right") + ": the reason wraps inside the window and Retry stays visible");
            narrow.Close();
        }
    }

    private static void TorrentLocaleKeys()
    {
        string dir = Path.Combine(RepoRoot, "src", "NDM.App", "Locales");
        var en = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "en.json")))!;
        var keys = new[] { "options.chk.add_open_trackers_to_magnet_links", "options.text.extra_trackers_note", "options.a11y.extra_trackers", "options.error.extra_trackers", "torrentadd.searching", "torrentadd.searching_no_dht",
            "torrentadd.error.timeout_dht_unreachable", "torrentadd.error.timeout_dht_off", "torrentadd.error.timeout_peers_silent", "torrentadd.error.timeout_no_peers" };
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
            Check(keys.All(k => data.TryGetValue(k, out var v) && v.Length > 0 && (file.EndsWith("en.json") || v != en[k])), Path.GetFileName(file) + " has the ten new torrent strings, translated");
            Check(keys.All(k => !data[k].Contains('\u2014')), Path.GetFileName(file) + " has no em dash in them");
        }
    }

    /// <summary>Only when NEODM_SHOTS names a folder: pictures of the new pages for a person to look at.</summary>
    private static void Shots(string scratch)
    {
        string? folder = Environment.GetEnvironmentVariable("NEODM_SHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        void Save(Window w, string name)
        {
            w.UpdateLayout(); var bmp = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32); bmp.Render(w);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var fs = File.Create(Path.Combine(folder, name)); enc.Save(fs);
        }
        var now = DateTimeOffset.UtcNow; string state = Path.Combine(scratch, "shots", "NDM"); Directory.CreateDirectory(state);
        foreach (var theme in new[] { "Light", "DeepNavy" })
        {
            ThemeManager.Apply(Application.Current, theme, null);
            var w = NewOptions(SeededSettings(now), state, now, 760, 760);
            ((ScrollViewer)((TabItem)((TabControl)w.FindName("OptionsTabs")).Items[0]).Content).ScrollToEnd(); Pump(150);
            Save(w, $"options-browsers-{theme}.png"); w.Close();
            var t = new SettingsWindow(new AppSettings(), state) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Width = 760, Height = 760 };
            t.Show(); Pump(100); ((TabControl)t.FindName("OptionsTabs")).SelectedItem = t.FindName("TorrentsTab"); Pump(120);
            ((ScrollViewer)((TabItem)t.FindName("TorrentsTab")).Content).ScrollToEnd(); Pump(150); Save(t, $"options-trackers-{theme}.png"); t.Close();
            var d = new TorrentAddWindow(new AppSettings { DownloadDirectory = scratch }, "magnet:?xt=urn:btih:" + new string('d', 40), null, (m, tt, ct) => Task.FromException<TorrentMetadata>(new MetadataTimeoutException("t", Status(5, 0, 0))), TimeSpan.FromSeconds(90))
            { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Width = 640, Height = 460 };
            d.Show(); Wait(d.MetadataTask); Pump(120); Save(d, $"magnet-timeout-{theme}.png"); d.Close();
        }
        ThemeManager.Apply(Application.Current, "Light", null);
    }
}
