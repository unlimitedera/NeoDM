using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static partial class V1130Tests
{
    private static readonly string? Shots = Environment.GetEnvironmentVariable("NEODM_SHOTS");
    private static void SaveShot(Window w, string name)
    {
        if (Shots is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder); w.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32); bmp.Render(w);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var fs = File.Create(Path.Combine(folder, name)); enc.Save(fs);
    }

    // ------------------------------------------------------------------ item 4: address box
    private static void BrowserAddressTable()
    {
        var table = new (string Input, AddressKind Kind, string? Url, string? Hint)[]
        {
            ("", AddressKind.Empty, null, null), ("   ", AddressKind.Empty, null, null),
            ("example.com", AddressKind.Url, "https://example.com/", null),
            ("example.com/a/b?x=1#top", AddressKind.Url, "https://example.com/a/b?x=1#top", null),
            ("  www.example.org  ", AddressKind.Url, "https://www.example.org/", null),
            ("http://example.com/x", AddressKind.Url, "http://example.com/x", null),
            ("HTTPS://Example.com/Path", AddressKind.Url, "https://example.com/Path", null),
            ("localhost:8080/app", AddressKind.Url, "https://localhost:8080/app", null),
            ("localhost", AddressKind.Url, "https://localhost/", null),
            ("192.168.0.1", AddressKind.Url, "https://192.168.0.1/", null),
            ("192.168.0.1:8000/x", AddressKind.Url, "https://192.168.0.1:8000/x", null),
            ("[::1]:3000", AddressKind.Url, "https://[::1]:3000/", null),
            ("cats", AddressKind.Search, BrowserAddress.SearchBase + "cats", null),
            ("funny cats video", AddressKind.Search, BrowserAddress.SearchBase + "funny%20cats%20video", null),
            ("what is 2+2?", AddressKind.Search, BrowserAddress.SearchBase + "what%20is%202%2B2%3F", null),
            ("999.1.1.1", AddressKind.Search, BrowserAddress.SearchBase + "999.1.1.1", null),
            ("a..b", AddressKind.Search, BrowserAddress.SearchBase + "a..b", null),
            ("-bad-.com", AddressKind.Search, BrowserAddress.SearchBase + "-bad-.com", null),
            ("https://exa mple.com", AddressKind.Search, BrowserAddress.SearchBase + "https%3A%2F%2Fexa%20mple.com", null),
            ("javascript:alert(1)", AddressKind.Refused, null, "neobrowser.hint.not_web"),
            ("data:text/html,<b>x</b>", AddressKind.Refused, null, "neobrowser.hint.not_web"),
            ("file:///C:/Windows/win.ini", AddressKind.Refused, null, "neobrowser.hint.not_web"),
            ("ftp://example.com/x", AddressKind.Refused, null, "neobrowser.hint.not_web"),
            ("about:blank", AddressKind.Refused, null, "neobrowser.hint.not_web"),
            ("view-source:https://example.com", AddressKind.Refused, null, "neobrowser.hint.not_web"),
            ("mailto:a@b.c", AddressKind.Refused, null, "neobrowser.hint.not_web"),
            ("C:\\Windows\\system32", AddressKind.Refused, null, "neobrowser.hint.not_web"),
            ("https://user:pw@example.com/", AddressKind.Refused, null, "neobrowser.hint.not_web"),
            ("user@example.com", AddressKind.Search, BrowserAddress.SearchBase + "user%40example.com", null),
            ("a\u0007b.com", AddressKind.Refused, null, "neobrowser.hint.not_web"),
            (new string('a', BrowserAddress.MaxLength + 1), AddressKind.Refused, null, "neobrowser.hint.too_long"),
        };
        foreach (var (input, kind, url, hint) in table)
        {
            var r = BrowserAddress.Normalize(input);
            Check(r.Kind == kind && r.Url == url && r.HintKey == hint, $"address '{(input.Length > 30 ? input[..30] + "..." : input).Replace("\a", "<bell>")}' -> {kind} {url}");
        }
        var idn = BrowserAddress.Normalize("bücher.example"); Check(idn.Kind == AddressKind.Url && new Uri(idn.Url!).IdnHost == "xn--bcher-kva.example" && idn.Url!.StartsWith("https://"), "an address with non-English letters is opened as https");
        Check(BrowserAddress.SearchBase.StartsWith("https://"), "the search page is https");
    }

    // ------------------------------------------------------------------ item 4: tab model
    private static void BrowserTabModel()
    {
        var set = new BrowserTabSet(); int changes = 0; set.Changed += () => changes++;
        Check(set.Tabs.Count == 0 && set.Active is null, "starts with no tab");
        var a = set.Open("https://a.example/")!; var b = set.Open()!; var c = set.Open("https://c.example/")!;
        Check(set.Tabs.Count == 3 && set.Active == c && a.Id != b.Id && b.IsHome && !a.IsHome, "opening activates the new tab; an empty address is the Home page");
        var bg = set.Open("https://bg.example/", activate: false)!; Check(set.Active == c && set.Tabs[^1] == bg, "a background tab does not take over");
        set.Activate(a.Id); Check(set.Active == a, "activate by id"); Check(!set.Activate(9999), "an unknown id changes nothing");
        Check(set.Next() == b && set.Next() == c && set.Next() == bg && set.Next() == a, "Ctrl+Tab cycles and wraps");
        Check(set.Previous() == bg && set.Previous() == c, "Ctrl+Shift+Tab goes back and wraps");
        // active selection after close
        set.Activate(b.Id); Check(set.Close(b.Id) == c, "closing the active tab selects its right neighbour");
        set.Activate(bg.Id); Check(set.Close(bg.Id) == c, "closing the last tab selects the one before it");
        set.Activate(c.Id); Check(set.Close(a.Id) == c && set.Active == c, "closing another tab keeps the active tab");
        Check(set.Close(12345) == c, "closing an unknown id changes nothing");
        Check(set.Close(c.Id) is null && set.Tabs.Count == 0 && set.Active is null, "closing the last tab leaves none (the window closes)");
        Check(set.Next() is null && set.Previous() is null, "no tab: Ctrl+Tab does nothing");
        // limit
        var many = new BrowserTabSet(); for (int i = 0; i < BrowserTabSet.Limit; i++) Check(many.Open("https://t" + i + ".example/") is not null, "tab " + (i + 1) + " opens");
        Check(BrowserTabSet.Limit == 12 && many.IsFull && many.Open("https://one-more.example/") is null && many.Tabs.Count == 12, "the 13th tab is refused");
        many.Close(many.Tabs[3].Id); Check(!many.IsFull && many.Open() is not null, "after closing one a new tab fits again");
        Check(changes > 5, "changes are announced");
    }

    // ------------------------------------------------------------------ item 4: capture routing from any tab
    private static void BrowserCaptureRouting()
    {
        var sent = new List<NativeBridge.BrowserDownloadRequest>(); Action<NativeBridge.BrowserDownloadRequest> sink = sent.Add;
        // downloads from three different tabs all reach the same sink with their own referrer
        foreach (var (tab, page) in new[] { (1, "https://one.example/page"), (2, "https://two.example/other"), (7, "https://seven.example/") })
        {
            var r = NeoBrowserCapture.ForDownload("https://files.example/a/b/file.zip?t=1", "file.zip", 123456, "application/zip", page, "UA/1.0", "sid=abc; theme=dark");
            Check(r is not null, "tab " + tab + ": a request is built"); sink(r!);
        }
        Check(sent.Count == 3 && sent.Select(r => r.Referer).SequenceEqual(new[] { "https://one.example/page", "https://two.example/other", "https://seven.example/" }), "each tab's own page is the referrer");
        var one = sent[0];
        Check(one.Url == "https://files.example/a/b/file.zip?t=1" && one.FileName == "file.zip" && one.Size == 123456 && one.Mime == "application/zip" && one.UserAgent == "UA/1.0" && one.Cookie == "sid=abc; theme=dark", "address, name, size, type, user agent and cookies are carried");
        Check(one.FromNeoBrowser && one.Stream is null && !one.Youtube && !one.Playlist, "the request is a plain download from the NeoDM browser (no hand-back, no stream)");
        var unknownSize = NeoBrowserCapture.ForDownload("https://files.example/x.bin", "x.bin", 0, "", null, null, "");
        Check(unknownSize is { Size: null, Cookie: null, Mime: null }, "unknown size, no cookies and no type stay empty");
        Check(NeoBrowserCapture.ForDownload("blob:https://x.example/1234", "a.bin", null, null, null, null, null) is null && NeoBrowserCapture.ForDownload("data:text/plain,hi", null, null, null, null, null, null) is null && NeoBrowserCapture.ForDownload("file:///C:/x.bin", null, null, null, null, null, null) is null, "blob:, data: and file: downloads cannot be captured");
        // media
        var hls = NeoBrowserCapture.ForMedia(new MediaCandidate("https://cdn.example/v/master.m3u8?sig=1", PickupKind.Hls), "https://site.example/watch", "UA/1.0", "sid=abc");
        Check(hls is { Stream: "hls", FromNeoBrowser: true, Referer: "https://site.example/watch", Cookie: "sid=abc", FileName: null }, "an HLS video goes to the stream flow");
        var dash = NeoBrowserCapture.ForMedia(new MediaCandidate("https://cdn.example/v/manifest.mpd", PickupKind.Dash), null, null, null);
        Check(dash is { Stream: "dash", Cookie: null }, "a DASH video goes to the stream flow");
        var file = NeoBrowserCapture.ForMedia(new MediaCandidate("https://cdn.example/clips/holiday.mp4?x=1", PickupKind.File), "https://site.example/", null, null);
        Check(file is { Stream: null, FileName: "holiday.mp4" }, "a media file goes to the normal download dialog with its name");
        Check(NeoBrowserCapture.ForMedia(new MediaCandidate("blob:https://x/1", PickupKind.File), null, null, null) is null, "a blob video is not offered");
        Check(NativeBridge.ValidStream(hls!.Stream!) && NativeBridge.ValidStream(dash!.Stream!), "the stream field is one the capture flow accepts");
    }

    // ------------------------------------------------------------------ item 4: media pickups
    private static void BrowserMediaPickup()
    {
        var table = new (string Url, PickupKind? Kind)[]
        {
            ("https://cdn.example/a/master.m3u8", PickupKind.Hls), ("https://cdn.example/a/master.M3U8?token=1", PickupKind.Hls), ("https://cdn.example/a/manifest.mpd", PickupKind.Dash),
            ("https://cdn.example/a/video.mp4", PickupKind.File), ("https://cdn.example/a/clip.webm?range=1", PickupKind.File), ("https://cdn.example/a/song.mp3", PickupKind.File),
            ("https://cdn.example/a/seg-001.ts", null), ("https://cdn.example/a/seg-001.m4s", null), ("https://cdn.example/a/sub.vtt", null), ("https://cdn.example/a/poster.jpg", null),
            ("https://cdn.example/a/app.js", null), ("https://cdn.example/watch", null), ("blob:https://x.example/abc", null), ("ftp://x.example/v.mp4", null), ("", null),
        };
        foreach (var (url, kind) in table) Check(MediaPickup.Classify(url) == kind, $"classify {url} -> {kind?.ToString() ?? "nothing"}");
        var p = new MediaPickup();
        Check(p.Current(1) is null, "nothing seen: no bar");
        p.Observe(1, "https://cdn.example/a/seg-1.ts"); Check(p.Current(1) is null, "pieces of a stream are ignored");
        p.Observe(1, "https://cdn.example/a/video.mp4"); Check(p.Current(1)?.Kind == PickupKind.File, "a media file is offered");
        p.Observe(1, "https://cdn.example/a/master.m3u8"); Check(p.Current(1)?.Kind == PickupKind.Hls, "a streaming manifest comes before a plain file");
        p.Observe(1, "https://cdn.example/a/variant-720.m3u8"); Check(p.Current(1)?.Url == "https://cdn.example/a/master.m3u8", "of several manifests the first one asked for is offered");
        Check(p.Observe(1, "https://cdn.example/a/master.m3u8") is { } same && same.Url.EndsWith("master.m3u8"), "the same address is not added twice");
        Check(p.Current(2) is null, "tabs are separate");
        p.Dismiss(1); Check(p.Current(1)?.Url == "https://cdn.example/a/variant-720.m3u8", "Hide drops the shown one for that tab");
        p.Dismiss(1); p.Dismiss(1); Check(p.Current(1) is null, "hiding everything leaves no bar");
        p.Observe(1, "https://cdn.example/a/master.m3u8"); Check(p.Current(1) is null, "a hidden address stays hidden");
        p.Clear(1); p.Observe(1, "https://cdn.example/a/master.m3u8"); Check(p.Current(1) is not null, "a new page in the tab forgets the old page");
        var cap = new MediaPickup(); for (int i = 0; i < 40; i++) cap.Observe(3, $"https://cdn.example/clip{i}.mp4");
        int kept = 0; while (cap.Current(3) is not null) { cap.Dismiss(3); kept++; } Check(kept == MediaPickup.PerTab, "at most " + MediaPickup.PerTab + " are remembered per tab");
    }

    // ------------------------------------------------------------------ item 4: home page
    private static void BrowserHomePage()
    {
        var html = NeoBrowserHome.Html("NeoDM browser", "Type <b>here</b> & \"go\"", "Go", false, "en", "#101010", "#f0f0f0", "#00aa99", "#001010", "#889999");
        Check(!html.Contains("http://") && !html.Contains("https://") && !html.Contains("src=") && !html.Contains("<link"), "the Home page loads nothing from the internet");
        Check(html.Contains("Type &lt;b&gt;here&lt;/b&gt; &amp; &quot;go&quot;") && !html.Contains("<b>here</b>"), "texts are escaped");
        Check(html.Contains("dir=\"ltr\"") && html.Contains("lang=\"en\"") && html.Contains("Content-Security-Policy") && html.Contains("default-src 'none'"), "language, direction and a strict content policy");
        Check(NeoBrowserHome.Html("t", "h", "g", true, "ar", "#000", "#fff", "#0a0", "#000", "#999").Contains("dir=\"rtl\""), "right to left for Arabic");
    }

    private static readonly string[] BrowserKeys =
    {
        "main.menu.neodm_browser", "neobrowser.btn.stop", "neobrowser.btn.new_tab", "neobrowser.btn.close_tab", "neobrowser.a11y.tabs", "neobrowser.tab.new", "neobrowser.tab.loading",
        "neobrowser.status.tab_limit", "neobrowser.status.sent", "neobrowser.hint.not_web", "neobrowser.hint.too_long", "neobrowser.home.hint", "neobrowser.home.go", "neobrowser.media.found", "neobrowser.media.download", "neobrowser.media.hide",
    };
    private static void BrowserLocales()
    {
        CheckLocales(BrowserKeys.Where(k => k != "neobrowser.media.hide").ToArray(), "browser");
        foreach (var info in LanguageCatalog.All)
        {
            Loc.SetLanguage(info.Tag);
            Check(Loc.T("neobrowser.status.tab_limit", 12).Contains("12") && !Loc.T("neobrowser.status.tab_limit", 12).Contains("{0}"), $"{info.Tag}: the tab limit message carries the number");
            Check(!BrowserKeys.Any(k => Loc.T(k).Contains('\u2014')), $"{info.Tag}: no em dashes in the browser texts");
        }
        Loc.SetLanguage("en");
    }

    // ------------------------------------------------------------------ item 4: window in two themes, mirrored, small
    private static Rect Box(FrameworkElement e, Visual root) => e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
    private static void BrowserRender(string scratch)
    {
        foreach (var theme in new[] { "Light", "DeepNavy" })
        {
            ThemeManager.Apply(Application.Current, theme, null);
            foreach (var rtl in new[] { false, true })
            {
                var sink = new List<NativeBridge.BrowserDownloadRequest>();
                var w = new NeoBrowserWindow(null, null, Path.Combine(scratch, "render-data"), () => null, sink.Add) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20, Width = 900, Height = 520 };
                w.Show(); w.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight; Pump(250);
                // the window is in the "runtime missing" state; tabs and the media bar are still driven through the public model
                var one = w.Tabs.Open("https://one.example/")!; one.Title = "A page with a rather long title that must be trimmed"; var two = w.Tabs.Open()!; two.Title = "New tab"; w.Tabs.Open("https://three.example/")!.Title = "Third";
                w.Tabs.Touch(); w.Pickup.Observe(w.Tabs.Active!.Id, "https://cdn.example/v/master.m3u8"); w.RefreshMediaBar(); Pump(150);
                var strip = (ListBox)w.FindName("TabStrip"); var bar = (Border)w.FindName("MediaBar"); var tag = $"{theme}/{(rtl ? "RTL" : "LTR")}";
                Check(strip.Items.Count == 3 && strip.SelectedIndex == 2, $"{tag}: three tabs, the new one selected");
                Check(bar.Visibility == Visibility.Visible && bar.IsVisible && ((Button)w.FindName("MediaDownload")).IsVisible, $"{tag}: the media bar shows 'Download this video'");
                var newTab = (Button)w.FindName("NewTabButton"); var back = (Button)w.FindName("BackButton");
                Check(rtl ? Box(newTab, w).Left < Box(strip, w).Left : Box(newTab, w).Left > Box(strip, w).Left, $"{tag}: the new-tab button sits at the end of the strip");
                Check(rtl ? Box(back, w).Left > Box((TextBox)w.FindName("AddressBox"), w).Left : Box(back, w).Left < Box((TextBox)w.FindName("AddressBox"), w).Left, $"{tag}: the toolbar is mirrored");
                Check(!((TextBox)w.FindName("AddressBox")).IsReadOnly && ((TextBox)w.FindName("AddressBox")).FlowDirection == FlowDirection.LeftToRight, $"{tag}: the address box is editable and left to right");
                var dl = (Button)w.FindName("MediaDownload"); double Lum(Color c) { double F(byte v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); } return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B); }
                var fg = ((SolidColorBrush)dl.Foreground).Color; var fill = ((SolidColorBrush)dl.Background).Color; double hi = Math.Max(Lum(fg), Lum(fill)), lo = Math.Min(Lum(fg), Lum(fill));
                Check((hi + 0.05) / (lo + 0.05) >= 4.5, $"{tag}: 'Download this video' text has enough contrast on its button ({(hi + 0.05) / (lo + 0.05):0.0}:1)");
                ((Button)w.FindName("MediaHide")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(60);
                Check(!w.MediaBarVisible, $"{tag}: Hide closes the bar");
                w.Pickup.Observe(w.Tabs.Active!.Id, "https://cdn.example/v/other.mpd"); w.RefreshMediaBar();
                var bg = (w.Background as SolidColorBrush)?.Color ?? Colors.Magenta; double lum = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255;
                Check(theme == "Light" ? lum > 0.7 : lum < 0.3, $"{tag}: the window follows the theme");
                SaveShot(w, $"d-neobrowser-tabs-{theme}-{(rtl ? "rtl" : "ltr")}.png");
                w.Width = 340; w.Height = 300; Pump(250);
                foreach (var c in new FrameworkElement[] { back, (Button)w.FindName("ForwardButton"), (Button)w.FindName("ReloadButton"), (Button)w.FindName("StopButton"), (TextBox)w.FindName("AddressBox"), newTab, bar, (TextBlock)w.FindName("StatusText") })
                {
                    if (!c.IsVisible) continue;
                    var box = Box(c, w); Check(box.Left >= -1 && box.Right <= w.ActualWidth + 1, $"{tag} 340px: {c.GetType().Name} {c.Name} stays inside the window");
                }
                Check(((TextBox)w.FindName("AddressBox")).ActualWidth >= 200, $"{tag} 340px: the address box has its own row and a usable width");
                SaveShot(w, $"d-neobrowser-tabs-{theme}-{(rtl ? "rtl" : "ltr")}-small.png");
                w.Close();
            }
        }
        ThemeManager.Apply(Application.Current, "Light", null);
    }

    // ------------------------------------------------------------------ item 4: Tasks menu entry
    private static void BrowserMenu(string root)
    {
        var manager = new DownloadManager(Path.Combine(root, "m-files"), Path.Combine(root, "m-state"), new DownloadOptions());
        var main = new MainWindow(manager, new AppSettings { DownloadDirectory = Path.Combine(root, "m-files") }, () => { }, enableTray: false) { ShowInTaskbar = false };
        try
        {
            main.Show(); Pump(150);
            var menu = (Menu)main.FindName("MainMenu"); var tasks = (MenuItem)menu.Items[0];
            var entry = tasks.Items.OfType<MenuItem>().FirstOrDefault(i => (string)i.Header == Loc.T("main.menu.neodm_browser"));
            Check(entry is not null, "the Tasks menu has 'NeoDM browser'");
            Loc.SetLanguage("en");
        }
        finally { main.Close(); }
    }

    // ------------------------------------------------------------------ item 4: real WebView2 (skipped without the runtime): two tabs, a download from each, the bar, the window stays open
    private sealed class BrowserSite : IDisposable
    {
        private readonly HttpListener listener = new(); public readonly string Base;
        public BrowserSite()
        {
            int port; using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
            Base = $"http://127.0.0.1:{port}"; listener.Prefixes.Add(Base + "/"); listener.Start(); _ = Task.Run(Loop);
        }
        private async Task Loop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext c; try { c = await listener.GetContextAsync(); } catch { return; }
                try
                {
                    var path = c.Request.Url!.AbsolutePath; var r = c.Response;
                    void Html(string body) { var b = Encoding.UTF8.GetBytes(body); r.ContentType = "text/html; charset=utf-8"; r.ContentLength64 = b.Length; r.OutputStream.Write(b); r.Close(); }
                    if (path == "/one") { r.Headers.Add("Set-Cookie", "sid=tab1; Path=/"); Html("<!doctype html><title>First page</title><p>one</p><script>setTimeout(function(){ location.href = '/get/one.bin'; }, 400);</script>"); continue; }
                    if (path == "/two") { r.Headers.Add("Set-Cookie", "sid=tab2; Path=/"); Html("<!doctype html><title>Second page</title><p>two</p><script>fetch('/clip/master.m3u8').catch(function(){}); setTimeout(function(){ location.href = '/get/two.bin'; }, 900);</script>"); continue; }
                    if (path.StartsWith("/get/")) { var data = new byte[2048]; r.ContentType = "application/octet-stream"; r.Headers["Content-Disposition"] = $"attachment; filename=\"{path[5..]}\""; r.ContentLength64 = data.Length; r.OutputStream.Write(data); r.Close(); continue; }
                    r.StatusCode = 404; r.Close();
                }
                catch (Exception) { try { c.Response.Abort(); } catch (Exception) { } }
            }
        }
        public void Dispose() { try { listener.Stop(); listener.Close(); } catch (Exception) { } }
    }

    private static void BrowserEndToEnd(string scratch)
    {
        string? version = null; try { version = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch (Exception) { }
        if (string.IsNullOrEmpty(version)) { Console.WriteLine("SKIP: no WebView2 runtime on this machine, the two-tab end-to-end check was not run"); return; }
        using var site = new BrowserSite(); var data = Path.Combine(scratch, "e2e-browser");
        var sent = new List<NativeBridge.BrowserDownloadRequest>();
        var window = new NeoBrowserWindow(site.Base + "/one", null, data, null, r => { lock (sent) sent.Add(r); }) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20, Width = 900, Height = 600 };
        window.Show();
        PumpUntil(() => { lock (sent) return sent.Count >= 1; }, "the download from the first tab reached the capture flow", 40000);
        NativeBridge.BrowserDownloadRequest first; lock (sent) first = sent[0];
        Check(first.Url == site.Base + "/get/one.bin" && first.FileName == "one.bin" && first.Cookie == "sid=tab1" && first.FromNeoBrowser && first.Size == 2048, "tab 1: address, name, cookie and size were captured");
        Check(first.UserAgent is { } ua && ua.Contains("Edg/") && first.Referer == site.Base + "/one", "tab 1: user agent and the page as referrer");
        Check(window.IsLoaded && window.Tabs.Tabs.Count == 1, "the browser stays open after the capture");
        var second = window.OpenTab(site.Base + "/two"); Check(second is not null && window.Tabs.Tabs.Count == 2 && window.Tabs.Active == second, "a second tab opens and becomes active");
        PumpUntil(() => window.MediaBarVisible, "the video request on tab 2 shows the 'Download this video' bar", 40000);
        Check(window.Pickup.Current(second!.Id)?.Kind == PickupKind.Hls, "the bar offers the HLS manifest the page asked for");
        PumpUntil(() => { lock (sent) return sent.Count >= 2; }, "the download from the second tab reached the capture flow", 40000);
        NativeBridge.BrowserDownloadRequest secondRequest; lock (sent) secondRequest = sent[1];
        Check(secondRequest.Url == site.Base + "/get/two.bin" && secondRequest.Cookie == "sid=tab2" && secondRequest.Referer == site.Base + "/two", "tab 2: its own cookie and page");
        Check(window.IsLoaded && window.TabTitles.Count == 2, "still open with two tabs");
        PumpUntil(() => window.TabTitles.Contains("Second page"), "the tab title comes from the page", 20000);
        var typed = window.SubmitAddress("javascript:alert(1)"); Check(typed.Kind == AddressKind.Refused && window.StatusMessage == Loc.T("neobrowser.hint.not_web"), "a script address is refused with the hint");
        window.CloseTab(second.Id); Pump(200); Check(window.Tabs.Tabs.Count == 1 && window.IsLoaded, "closing a tab keeps the window");
        window.Close(); Pump(500);
        string? problem = null; var cleared = false; for (int i = 0; i < 20 && !cleared; i++) { cleared = NeoBrowserData.Clear(out problem, data); if (!cleared) Pump(300); }
        Check(cleared, "the private profile can be cleared after the window is closed " + problem);
    }
}
