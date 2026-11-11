using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.App.Localization;
using NDM.Torrent;

internal static partial class V1132Tests
{
    // ------------------------------------------------------------------ item 4 (app side): the drive message is localized and names the drive
    private static void DriveMessage()
    {
        Check(TorrentController.LocalizeError(null) is null && TorrentController.LocalizeError("Some disk error") == "Some disk error", "other errors are shown as they are");
        Loc.SetLanguage("en");
        var text = TorrentController.LocalizeError(TorrentService.DriveMissingPrefix + @"E:\");
        Check(text == @"Drive not available: E:\ Reconnect it and press Resume.", "the English sentence names the drive and says what to do: " + text);
        foreach (var info in LanguageCatalog.All)
        {
            Loc.SetLanguage(info.Tag); var local = TorrentController.LocalizeError(TorrentService.DriveMissingPrefix + @"E:\");
            Check(local is not null && local.Contains(@"E:\") && (info.Tag == "en" || local != text), info.Tag + ": the drive sentence is translated and keeps the drive name");
        }
        Loc.SetLanguage("en");
    }

    // ------------------------------------------------------------------ item 5: what a magnet that finds nobody tells the user
    private static void MagnetHint(string root)
    {
        MetadataStatus S(TimeSpan elapsed, int dht, int connections, int trackers, int tested, int answered, string? proxy = null) =>
            new(1, 5, elapsed, TimeSpan.FromSeconds(60), dht, connections, true, trackers, MetadataPhase.Searching, tested, answered, 6881, true, proxy);
        MetadataStatus? current = null;
        var settings = new AppSettings { DownloadDirectory = root };
        var w = new TorrentAddWindow(settings, "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", null, (_, _, _) => new TaskCompletionSource<TorrentMetadata>().Task, null, null, () => current) { ShowInTaskbar = false, Left = 20, Top = 20 };
        w.Show(); Pump(150);
        Check(w.FetchHint_() is null, "no status yet: no hint");
        current = S(TimeSpan.FromSeconds(8), 40, 0, 3, 0, 0); Check(w.FetchHint_() is null, "too early (8 s): no hint");
        current = S(TimeSpan.FromSeconds(30), 40, 2, 3, 3, 3); Check(w.FetchHint_() is null, "a peer is connected: no hint");
        current = S(TimeSpan.FromSeconds(30), 40, 0, 3, 3, 0);
        var silent = w.FetchHint_()!;
        Check(silent.Contains("0 of 3 UDP trackers answered (3 listed)") && silent.Contains("port 6881") && silent.Contains("port mapping on") && silent.Contains("Proxy for torrents: none") && silent.Contains("no UDP tracker answered"), "DHT nodes but no connection and no tracker answering: the numbers and the likely reason: " + silent);
        current = S(TimeSpan.FromSeconds(30), 40, 0, 3, 3, 2);
        var some = w.FetchHint_()!; Check(some.Contains("2 of 3 UDP trackers answered") && some.Contains("nobody is sharing"), "trackers answer but nobody connects: the reason is no peers: " + some);
        current = S(TimeSpan.FromSeconds(30), 40, 0, 0, 0, 0); Check(w.FetchHint_()!.Contains("none in the link"), "a magnet without trackers says so");
        current = S(TimeSpan.FromSeconds(30), 40, 0, 2, 0, 0); Check(w.FetchHint_()!.Contains("2 listed (answers not tested)"), "trackers not probed: listed count only");
        current = S(TimeSpan.FromSeconds(30), 40, 0, 2, 0, 0, "127.0.0.1:1080"); var viaProxy = w.FetchHint_()!;
        Check(viaProxy.Contains("Proxy for torrents: 127.0.0.1:1080") && viaProxy.Contains("DHT and UDP trackers cannot go through a proxy"), "proxy in use for torrents is shown and named as the likely reason: " + viaProxy);
        // the hint block exists in the window and is a plain text line (no new window)
        var hint = (TextBlock)w.FindName("FetchHint");
        Check(hint is not null && hint.Visibility == Visibility.Collapsed, "the hint line is part of the fetch panel and hidden until it has something to say");
        hint!.Text = viaProxy; hint.Visibility = Visibility.Visible; Pump(100); SaveShot(w, "v1132-magnet-hint.png");
        w.Close();
        CheckLocales(new[] { "torrent.drive_missing", "torrentadd.hint.trackers_none", "torrentadd.hint.trackers_answered", "torrentadd.hint.trackers_untested", "torrentadd.hint.facts", "torrentadd.hint.mapping_on", "torrentadd.hint.mapping_off",
            "torrentadd.hint.proxy_none", "torrentadd.hint.reason_proxy", "torrentadd.hint.reason_trackers_silent", "torrentadd.hint.reason_nobody" }, "torrent diagnostics");
    }
}
