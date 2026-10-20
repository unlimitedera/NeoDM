using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>1.5.4: browser size on the wire and in the dialog, hand back to the browser, wrong-content in the list, remembered sites.</summary>
internal static partial class SecurityTests
{
    private static void Press(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();

    /// <summary>A failed job with the code wrong-content, made by the real engine against a server that answers every request with a web page.</summary>
    private static DownloadJob WrongContentJob(string root, string referer, out DownloadManager manager)
    {
        var listener = new HttpListener(); int port; using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var page = Encoding.ASCII.GetBytes("<!DOCTYPE html><html><body>" + new string('x', 32768 - 27 - 14) + "</body></html>");
        _ = Task.Run(async () => { while (listener.IsListening) { try { var c = await listener.GetContextAsync(); c.Response.ContentType = "text/html"; c.Response.ContentLength64 = page.Length; c.Response.OutputStream.Write(page, 0, page.Length); c.Response.Close(); } catch { return; } } });
        var dir = Path.Combine(root, "wc-" + Guid.NewGuid().ToString("N")[..6]);
        var local = manager = new DownloadManager(Path.Combine(dir, "d"), Path.Combine(dir, "s"), new DownloadOptions { RetryCount = 0 });
        var job = Sync(local.AddAsync(new DownloadRequest($"http://127.0.0.1:{port}/dl/token/archive.zip", "archive.zip", referer, null, false, null, 108003328, "application/zip")));
        PumpUntil(() => local.Jobs.Single(j => j.Id == job.Id).Status is "Failed" or "Completed", "the job did not finish");
        listener.Stop();
        return local.Jobs.Single(j => j.Id == job.Id);
    }

    private static void CaptureFactsTests(string root)
    {
        // ---- the size on the wire (optional, 0..2^50) ----
        string name = "NDM.Facts." + Guid.NewGuid().ToString("N"); var seen = new List<NativeBridge.BrowserDownloadRequest>();
        bool handBackNext = false;
        using (var bridge = new NativeBridge(r => { lock (seen) seen.Add(r); r.Respond(handBackNext ? new NativeBridge.BrowserDownloadResponse(false, null, "left", HandBack: true) : new NativeBridge.BrowserDownloadResponse(true, "j")); }, name, c => null))
        {
            bridge.Start();
            long? Size(string field) { lock (seen) seen.Clear(); var reply = Sync(Ask(name, "{\"url\":\"https://example.org/a.zip\"" + field + "}")); Check(reply.Contains("\"ok\":true"), "size field accepted: " + field + " -> " + reply); lock (seen) return seen.Single().Size; }
            Check(Size(",\"size\":108003328") == 108003328, "Size: a positive integer reaches the app");
            Check(Size("") is null && Size(",\"size\":0") is null && Size(",\"size\":null") is null, "Size: absent, 0 and null mean unknown (older extensions keep working)");
            Check(Size(",\"size\":" + (1L << 50)) == 1L << 50, "Size: 2^50 is the largest accepted value");
            foreach (var bad in new[] { "-1", (1L << 50) + 1 + "", "1.5", "\"5\"", "true", "9223372036854775808", "[1]" })
            {
                lock (seen) seen.Clear();
                var reply = Sync(Ask(name, "{\"url\":\"https://example.org/a.zip\",\"size\":" + bad + "}"));
                Check(reply.Contains("\"ok\":false") && seen.Count == 0, "Size: " + bad + " is refused before it reaches the UI: " + reply);
            }
            handBackNext = true;
            var hb = Sync(Ask(name, "{\"url\":\"https://example.org/a.zip\"}"));
            Check(hb.Contains("\"ok\":false") && hb.Contains("\"handBack\":true") && hb.Contains("\"cancelled\":false"), "Hand back: the reply carries handBack:true and is not a cancel: " + hb);
            handBackNext = false;
        }

        // ---- the dialog: the browser size at once, the hand-back button ----
        var shown = new DownloadOptionsDialog("https://download1.example.org/token/archive.zip", "archive.zip", null, null, Path.GetTempPath(), "application/zip", null, null, 108003328) { ShowInTaskbar = false };
        shown.Show(); Pump(80);
        var info = (TextBlock)shown.FindName("DetectedInfo");
        string expectedLine = Loc.T("capture.detect.browser_size", DownloadRow.FormatBytes(108003328));
        Check(info.Visibility == Visibility.Visible && info.Text == expectedLine && info.Text.Contains("MB") && info.Text.Contains("browser"), "Dialog: the browser-reported size shows at once and says where it comes from: " + info.Text);
        Check(!shown.HandBackOffered && ((Button)shown.FindName("HandBackButton")).Visibility == Visibility.Collapsed, "Dialog: no hand-back button unless the capture flow offers it");
        shown.OfferHandBack(); Pump(50);
        var hand = (Button)shown.FindName("HandBackButton");
        Check(hand.IsVisible && hand.Content as string == Loc.T("capture.btn.hand_back") && Loc.T("capture.btn.hand_back") == "Download in browser instead", "Dialog: 'Download in browser instead' is shown next to Cancel");
        Press((Button)shown.FindName("StartButton")); Pump(50);
        Check(shown.Request is { ExpectedSize: 108003328, ExpectedMime: "application/zip" } && !shown.HandBack, "Dialog: Start passes the browser size and type to the engine request");
        var handDialog = new DownloadOptionsDialog("https://download1.example.org/token/archive.zip", "archive.zip", null, null, Path.GetTempPath(), "application/zip", null, null, 108003328) { ShowInTaskbar = false };
        handDialog.OfferHandBack(); handDialog.Show(); Pump(60);
        Press((Button)handDialog.FindName("HandBackButton")); Pump(50);
        Check(handDialog.HandBack && handDialog.UserAction == DownloadOptionsDialog.DialogAction.HandBack && handDialog.Request is null && !handDialog.Accepted, "Dialog: hand back records the choice and creates no request");
        var expired = new DownloadOptionsDialog("https://download1.example.org/token/archive.zip", "archive.zip", null, null, Path.GetTempPath()) { ShowInTaskbar = false };
        expired.OfferHandBack(); expired.Show(); Pump(50); expired.MarkExpired("gone"); Pump(30);
        Check(!((Button)expired.FindName("HandBackButton")).IsEnabled, "Dialog: after the browser side hung up, hand back is disabled too (the browser restarted the download already)");
        expired.Close();
        // a probe that answers with a small web page: the browser size stays and a warning is added
        var probed = new DownloadOptionsDialog("https://download1.example.org/token/archive.zip", "archive.zip", null, null, Path.GetTempPath(), "application/zip",
            _ => Task.FromResult<NameProbeResult?>(new NameProbeResult("https://download1.example.org/token/archive.zip", null, "text/html", 32768, Array.Empty<byte>(), 200)), null, 108003328) { ShowInTaskbar = false };
        probed.Show(); PumpUntil(() => probed.ProbeTask.IsCompleted, "probe"); Pump(50);
        var probedText = ((TextBlock)probed.FindName("DetectedInfo")).Text;
        Check(probedText.Contains(expectedLine) && probedText.Contains(DownloadRow.FormatBytes(32768)) && probedText.Contains("expired"), "Dialog: a much smaller probe reply is shown as a warning next to the browser size: " + probedText);
        probed.Close(); shown.Close(); handDialog.Close();
        var plain = new DownloadOptionsDialog("https://example.org/a.zip", "a.zip", null, null, Path.GetTempPath()) { ShowInTaskbar = false };
        plain.Show(); Pump(50);
        Check(((TextBlock)plain.FindName("DetectedInfo")).Visibility == Visibility.Collapsed, "Dialog: without browser size and probe the info line stays hidden as before");
        plain.Close();

        // ---- the job list: a web page instead of the file ----
        var job = WrongContentJob(root, "https://www.example.org/file/abc/archive.zip", out var manager);
        using (manager)
        {
            Check(job.Status == "Failed" && job.ErrorCode == "wrong-content", "Engine job: failed with wrong-content (" + job.Status + " " + job.Error + ")");
            var row = new DownloadRow(job);
            string expectedText = Loc.T("error.wrong_content", DownloadRow.FormatBytes(32768), DownloadRow.FormatBytes(108003328));
            Check(row.IsWrongContent && row.Error == expectedText && expectedText.Contains("32 KB") && expectedText.Contains("103 MB") && expectedText.StartsWith("The site sent a web page instead of the file (about 32 KB instead of 103 MB)."), "Row: the localized message names both sizes: " + row.Error);
            Check(row.PageToOpen == "https://www.example.org/file/abc/archive.zip", "Row: the referer page can be opened");
            Check(RowMenu.For(new[] { row }).OpenPage, "Row menu: 'Open the page in the browser' applies");
            Check(DownloadRow.WrongContentText(null, null) == Loc.T("error.wrong_content_plain") && DownloadRow.WrongContentText(2048, null) == Loc.T("error.wrong_content_got", DownloadRow.FormatBytes(2048)), "Row: the variants without sizes");
            Check(Loc.T("wrongcontent.hint") == "Try the download in the browser. If the site needs your sign-in, turn on Include authentication cookies in the NeoDM extension.", "Hint text: " + Loc.T("wrongcontent.hint"));
            Check(Loc.T("error.wrong_content_redirect") == ContentGuard.RedirectMessage && ContentGuard.RedirectMessage.Contains("It may need your browser session. Use Download in browser instead."), "Redirect text: " + Loc.T("error.wrong_content_redirect"));
            // only https pages are offered
            var httpJob = WrongContentJob(root, "http://www.example.org/file", out var m2);
            using (m2) { var httpRow = new DownloadRow(httpJob); Check(httpRow.IsWrongContent && httpRow.PageToOpen is null && !RowMenu.For(new[] { httpRow }).OpenPage, "Row: an http page is not offered (https only)"); }
            // the opener only starts https addresses
            var opened = new List<string>(); var before = UrlOpener.Open;
            UrlOpener.Open = u => opened.Add(u);
            UrlOpener.Open(row.PageToOpen!); UrlOpener.Open = before;
            Check(opened.SequenceEqual(new[] { "https://www.example.org/file/abc/archive.zip" }), "Row: the page address goes to the opener");

            // ---- remembered sites ----
            var remembered = new List<string>(); var watcher = new WrongContentWatcher(remembered.Add);
            watcher.Observe(new[] { job }); Check(remembered.Count == 0, "Watcher: a download that was not captured never puts a site on the list");
            watcher.Captured(job.Id); watcher.Observe(new[] { job }); watcher.Observe(new[] { job });
            Check(remembered.Count == 1 && remembered[0] == job.Url, "Watcher: a captured download that fails with wrong-content is reported once");
        }
        string state = Path.Combine(root, "sites-" + Guid.NewGuid().ToString("N")[..6]);
        Check(BrowserSites.HostOf("https://download1336.mediafire.com/abc/def/x.zip") == "download1336.mediafire.com" && BrowserSites.HostOf("HTTP://WWW.Example.COM:8080/a") == "www.example.com" && BrowserSites.HostOf("ftp://x.example/a") is null && BrowserSites.HostOf("not a url") is null && BrowserSites.HostOf(null) is null, "Sites: host names from addresses");
        Check(BrowserSites.Add(state, "https://download1336.mediafire.com/a") && !BrowserSites.Add(state, "https://DOWNLOAD1336.mediafire.com/b") && BrowserSites.Add(state, "https://example.co.uk/x") && !BrowserSites.Add(state, "ftp://x"), "Sites: added once, duplicates and non-http ignored");
        Check(BrowserSites.Pending(state).SequenceEqual(new[] { "download1336.mediafire.com", "example.co.uk" }), "Sites: the file holds host names only, one per line");
        for (int i = 0; i < 130; i++) BrowserSites.Add(state, $"https://h{i}.example.org/");
        var all = BrowserSites.Pending(state);
        Check(all.Count == BrowserSites.Maximum && all[^1] == "h129.example.org" && !all.Contains("download1336.mediafire.com"), "Sites: at most 100 entries, the oldest go first");
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", "App.xaml.cs"));
        Check(app.Contains("dialog.HandBack") && app.Contains("new WrongContentWatcher(RememberBrowserSite)") && app.Contains("HandBack: true") && app.Contains("_wrongContent.Observe") && app.Contains("request.Size"), "App: hand back replies handBack (the extension remembers the site) and wrong-content puts the site on the app list; the browser size reaches the dialog");
        var progress = File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", "DownloadProgressWindow.xaml"));
        Check(progress.Contains("OpenPageButton") && progress.Contains("wrongcontent.hint") && progress.Contains("wrongcontent.open_page"), "Progress window: hint and 'Open the page in the browser' button");
    }
}
