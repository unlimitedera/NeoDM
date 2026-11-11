using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;
using NDM.Tests;

internal static partial class V1132Tests
{
    private static T Field<T>(Window w, string name) where T : class => (T)(w.FindName(name) ?? throw new Exception("missing control " + name));

    // ------------------------------------------------------------------ item 1: settings and migration
    private static void ProxySettings(string root)
    {
        var d = new AppSettings();
        Check(d.ProxyForDownloads && !d.ProxyForTorrents && !d.ProxyForYoutube && !d.ProxyForApp, "new defaults: downloads on, torrents/YouTube/app off");
        // a 1.13.1 file: proxy configured, none of the new fields present -> downloads only
        var state = Path.Combine(root, "p-old"); Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "settings.json"), "{\"SettingsSchema\":11,\"ProxyType\":\"Socks5\",\"ProxyHost\":\"127.0.0.1\",\"ProxyPort\":10808,\"ProxyForPeers\":true,\"ProxyForTrackers\":true,\"ProxyForDownloads\":true}");
        var old = AppSettings.Load(state);
        Check(old.ProxyFor(ProxyPurpose.Download) is { Kind: ProxyKind.Socks5, Port: 10808 }, "migration: an existing proxy keeps working for file downloads");
        Check(old.ProxyFor(ProxyPurpose.Torrent) is null && old.ProxyFor(ProxyPurpose.YouTube) is null && old.ProxyFor(ProxyPurpose.App) is null, "migration: torrents, YouTube and the app's own requests stay direct (even with the old peer/tracker flags on)");
        old.ProxyForTorrents = true; old.ProxyForYoutube = true; old.ProxyForApp = true; old.Save(state);
        var again = AppSettings.Load(state);
        Check(again.ProxyForTorrents && again.ProxyForYoutube && again.ProxyForApp && again.ProxyFor(ProxyPurpose.App) is not null, "the new switches survive a save and load");
        var copy = new AppSettings(); copy.CopyFrom(again);
        Check(copy.ProxyForTorrents && copy.ProxyForYoutube && copy.ProxyForApp, "CopyFrom carries the switches");
        var offDownloads = new AppSettings { ProxyType = ProxyKind.Http, ProxyHost = "h", ProxyPort = 3128, ProxyForDownloads = false };
        Check(offDownloads.ProxyFor(ProxyPurpose.Download) is null && OptionsHelper.CreateDownloadOptions(offDownloads).Proxy is null, "downloads switch off: direct");
        var opt = OptionsHelper.CreateDownloadOptions(again);
        Check(opt.Proxy is not null && opt.YoutubeProxy is not null && opt.AppProxy is not null, "engine options carry each purpose's proxy from its own switch");
        // torrents: the engine gets a proxy (and loses the UDP features) only when the torrents switch is on
        var t = new AppSettings { ProxyType = ProxyKind.Socks5, ProxyHost = "127.0.0.1", ProxyPort = 1080, TorrentEnableDht = true, TorrentEnableLsd = true, TorrentEnablePortForwarding = true };
        var direct = TorrentController.ToOptions(t);
        Check(direct.PeerProxy is null && direct.TrackerProxy is null && direct.EnableDht && direct.EnableLsd && direct.EnablePortForwarding, "torrents switch off: no peer/tracker proxy and DHT, local discovery and port mapping stay as the user set them");
        t.ProxyForTorrents = true;
        var via = TorrentController.ToOptions(t);
        Check(via.PeerProxy is not null && via.TrackerProxy is not null && !via.EnableDht && !via.EnableLsd && !via.EnablePortForwarding, "torrents switch on: peers and trackers use the proxy, the UDP features are off (stated in Options)");
        t.ProxyForPeers = false;
        Check(TorrentController.ToOptions(t).PeerProxy is null && TorrentController.ToOptions(t).EnableDht, "peers not proxied: DHT is available again");
    }

    // ------------------------------------------------------------------ item 1 proof: what passes through a recording proxy, per purpose
    private static void ProxyPurposes(string root)
    {
        using var server = new FixtureServer(); using var proxy = new FixtureProxy();
        var s = new AppSettings { ProxyType = ProxyKind.Http, ProxyHost = "127.0.0.1", ProxyPort = proxy.Port, ProxyForDownloads = true };
        int Down(string tag)
        {
            int before = proxy.Total;
            using var m = new DownloadManager(Path.Combine(root, tag, "f"), Path.Combine(root, tag, "s"), OptionsHelper.CreateDownloadOptions(s));
            var added = m.AddAsync(new(server.BaseUrl + "/range", tag + ".bin")).GetAwaiter().GetResult();
            var end = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < end && m.Jobs.Single(j => j.Id == added.Id).Status is not ("Completed" or "Failed")) Thread.Sleep(30);
            Check(m.Jobs.Single(j => j.Id == added.Id).Status == "Completed", tag + ": the download completes");
            return proxy.Total - before;
        }
        Check(Down("dl-on") >= 2, "DOWNLOAD purpose, switch on: the transfer goes through the proxy");
        s.ProxyForDownloads = false;
        Check(Down("dl-off") == 0, "DOWNLOAD purpose, switch off: nothing reaches the proxy");
        s.ProxyForDownloads = true;

        int UpdateCheck()
        {
            int before = proxy.Total;
            var checker = new UpdateChecker("1.13.2", () => s.ProxyFor(ProxyPurpose.App), apiBase: server.BaseUrl);
            var r = Sync(checker.CheckAsync());
            return proxy.Total - before;
        }
        Check(UpdateCheck() == 0, "APP purpose (update check), default: direct, nothing reaches the proxy although downloads use it");
        s.ProxyForApp = true;
        Check(UpdateCheck() >= 1, "APP purpose (update check), \"also use the proxy\" on: the request goes through the proxy");

        // pictures: the loader asks the policy; with the proxy on the CONNECT to the picture host is seen by the proxy
        s.ProxyForApp = false;
        using (var l = new ThumbnailLoader(Path.Combine(root, "thumb-direct"), () => s.ProxyFor(ProxyPurpose.App)) { RequestTimeout = TimeSpan.FromMilliseconds(500) }) { Check(NameOfProxyUsed(l) == "", "APP purpose (pictures), default: the loader has no proxy"); }
        s.ProxyForApp = true; int connectsBefore = proxy.Connects;
        using (var l = new ThumbnailLoader(Path.Combine(root, "thumb-proxy"), () => s.ProxyFor(ProxyPurpose.App)) { RequestTimeout = TimeSpan.FromSeconds(3) })
        {
            Sync(l.LoadAsync("dQw4w9WgXcQ", "https://i.ytimg.com/vi/dQw4w9WgXcQ/mqdefault.jpg"));
        }
        bool seen; lock (proxy.Targets) seen = proxy.Targets.Any(t => t.StartsWith("connect:i.ytimg.com:443"));
        Check(proxy.Connects > connectsBefore && seen, "APP purpose (pictures), switch on: the picture host is requested through the proxy");
        // torrents: engine options per switch (see ProxySettings); YouTube: command line proven in the core suite
        var y = new AppSettings { ProxyType = ProxyKind.Socks5, ProxyHost = "127.0.0.1", ProxyPort = 1080, ProxyForYoutube = true };
        Check(YoutubeService.ProxyArguments(y.ProxyFor(ProxyPurpose.YouTube)).SequenceEqual(new[] { "--proxy", "socks5h://127.0.0.1:1080" }), "YOUTUBE purpose, switch on: the helper gets --proxy with the right scheme");
        y.ProxyForYoutube = false;
        Check(YoutubeService.ProxyArguments(y.ProxyFor(ProxyPurpose.YouTube)).Length == 0, "YOUTUBE purpose, switch off: no --proxy");
    }
    private static string NameOfProxyUsed(ThumbnailLoader l) => (string)(typeof(ThumbnailLoader).GetField("proxyName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(l) ?? "");

    // ------------------------------------------------------------------ item 1 UI: the Options > Proxy page
    private static void ProxyOptionsPage(string root)
    {
        var state = Path.Combine(root, "o-state"); Directory.CreateDirectory(state);
        foreach (var (theme, rtl, tag) in new[] { ("Light", false, "light"), ("Dark", false, "dark"), ("Light", true, "rtl") })
        {
            Application.Current.Resources.MergedDictionaries[0] = ThemeManager.LoadFile(theme);
            var settings = new AppSettings { ProxyType = ProxyKind.Http, ProxyHost = "127.0.0.1", ProxyPort = 3128 };
            var w = new SettingsWindow(settings, state) { ShowInTaskbar = false, Left = 20, Top = 20, Width = 1000, Height = 760 };
            w.Show(); w.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight; Pump(200);
            var tabs = Field<TabControl>(w, "OptionsTabs");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => (string)t.Header == Loc.T("options.tab.proxy")); Pump(150);
            Check(Field<CheckBox>(w, "ProxyForDownloads").IsChecked == true && Field<CheckBox>(w, "ProxyForTorrents").IsChecked == false && Field<CheckBox>(w, "ProxyForYoutube").IsChecked == false && Field<CheckBox>(w, "ProxyForApp").IsChecked == false, tag + ": an old proxy shows downloads on and the rest off");
            Check(!Field<CheckBox>(w, "ProxyForPeers").IsEnabled && !Field<CheckBox>(w, "ProxyForTrackers").IsEnabled, tag + ": peer/tracker options wait for the torrents switch");
            Check(Field<TextBlock>(w, "ProxyScopeSummary").Text.Contains(Loc.T("options.proxy.name_app")) && Field<TextBlock>(w, "ProxyDetected").Text.Contains("HTTP"), tag + ": summary and detected type are shown");
            Field<CheckBox>(w, "ProxyForTorrents").IsChecked = true; Field<CheckBox>(w, "ProxyForTorrents").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(100);
            Check(Field<CheckBox>(w, "ProxyForPeers").IsEnabled && Field<TextBlock>(w, "ProxyTorrentWarning").Visibility == Visibility.Visible, tag + ": torrents on with an HTTP proxy shows the warning and the sub-options");
            Check(Field<TextBlock>(w, "ProxyScopeSummary").Text.Contains(Loc.T("options.proxy.torrent_udp_off")), tag + ": the summary says DHT and UDP trackers are off while torrents use the proxy");
            SaveShot(w, $"v1132-proxy-http-torrents-{tag}.png");
            // a pasted address fills the type, host, port and login
            Field<TextBox>(w, "ProxyHost").Text = "socks5://me:pw@10.1.2.3:10808"; Pump(100);
            Check(Field<TextBox>(w, "ProxyHost").Text == "10.1.2.3" && Field<TextBox>(w, "ProxyPort").Text == "10808" && Field<TextBox>(w, "ProxyUser").Text == "me" && ((ComboBoxItem)Field<ComboBox>(w, "ProxyTypeBox").SelectedItem).Tag is ProxyKind.Socks5, tag + ": a pasted socks5:// address is split into type, host, port and login");
            Check(Field<TextBlock>(w, "ProxyDetected").Text.Contains("SOCKS5") && Field<TextBlock>(w, "ProxyTorrentWarning").Visibility == Visibility.Collapsed, tag + ": detected SOCKS5, the HTTP warning is gone");
            Field<CheckBox>(w, "ProxyForApp").IsChecked = true; Field<CheckBox>(w, "ProxyForYoutube").IsChecked = true;
            SaveShot(w, $"v1132-proxy-socks-{tag}.png");
            if (tag == "light")
            {
                ((Button)w.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(500);
                var saved = AppSettings.Load(state);
                Check(saved is { ProxyType: ProxyKind.Socks5, ProxyHost: "10.1.2.3", ProxyPort: 10808, ProxyForTorrents: true, ProxyForYoutube: true, ProxyForApp: true }, "saving stores the type, address and all four switches");
            }
            else w.Close();
            if (w.IsLoaded) w.Close();
        }
        Application.Current.Resources.MergedDictionaries[0] = ThemeManager.LoadFile("Light");
    }

    private static readonly string[] ProxyKeys = { "options.proxy.for_torrents", "options.proxy.for_youtube", "options.proxy.for_app", "options.proxy.accepted", "options.proxy.detected", "options.proxy.torrent_http_warning",
        "options.proxy.torrent_udp_off", "options.proxy.summary", "options.proxy.summary_none", "options.proxy.name_downloads", "options.proxy.name_torrents", "options.proxy.name_youtube", "options.proxy.name_app", "options.proxy.nothing", "update.reason.proxy" };
    private static void ProxyLocales() => CheckLocales(ProxyKeys, "proxy scope");
}
