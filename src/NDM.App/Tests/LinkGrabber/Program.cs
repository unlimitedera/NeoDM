global using System.IO;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal sealed class GrabberTestApp : Application
{
    public GrabberTestApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}

/// <summary>A loopback web server for the link grabber tests (HttpListener on 127.0.0.1). Routes: /ok/N.bin (5000 bytes, HEAD and ranges), /gone (404), /page.html (a web page), /nohead (HEAD refused, ranged GET works),
/// /head403 (HEAD refused with 403, GET works), /fail (503), /cd (Content-Disposition name), /hold/N.bin (slow, counts simultaneous requests), /slow/N (answers after a pause, counts simultaneous requests).</summary>
internal sealed class MiniServer : IDisposable
{
    private readonly HttpListener listener = new();
    public string BaseUrl { get; }
    public readonly ConcurrentQueue<(string Method, string Path)> Log = new();
    private int active, peak;
    public int Peak => Volatile.Read(ref peak);
    public int Requests(string path) => Log.Count(l => l.Path == path);
    public MiniServer()
    {
        int port; using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        BaseUrl = "http://127.0.0.1:" + port; listener.Prefixes.Add(BaseUrl + "/"); listener.Start();
        _ = Task.Run(async () => { while (listener.IsListening) { HttpListenerContext c; try { c = await listener.GetContextAsync(); } catch { return; } _ = Task.Run(() => Serve(c)); } });
    }
    private void Serve(HttpListenerContext c)
    {
        string method = c.Request.HttpMethod, path = c.Request.Url!.AbsolutePath; string? range = c.Request.Headers["Range"];
        Log.Enqueue((method, path));
        var r = c.Response;
        try
        {
            void Send(int status, byte[]? body, string type = "application/octet-stream", long? length = null, string? disposition = null, string? contentRange = null)
            {
                r.StatusCode = status; r.ContentType = type; if (disposition != null) r.AddHeader("Content-Disposition", disposition); if (contentRange != null) r.AddHeader("Content-Range", contentRange);
                r.AddHeader("Accept-Ranges", "bytes");
                long len = length ?? body?.Length ?? 0; r.ContentLength64 = len;
                if (method != "HEAD" && body != null) r.OutputStream.Write(body, 0, (int)Math.Min(body.Length, len));
            }
            if (path.StartsWith("/ok/")) { var body = new byte[5000]; new Random(path.GetHashCode()).NextBytes(body); if (range != null) Send(206, body[..1], length: 1, contentRange: "bytes 0-0/5000"); else Send(200, body); }
            else if (path == "/gone") Send(404, Encoding.ASCII.GetBytes("gone"), "text/plain");
            else if (path == "/page.html") Send(200, Encoding.ASCII.GetBytes("<html><body>hello</body></html>"), "text/html; charset=utf-8");
            else if (path == "/nohead") { if (method == "HEAD") Send(405, null); else Send(206, new byte[1], length: 1, contentRange: "bytes 0-0/7777"); }
            else if (path == "/head403") { if (method == "HEAD") Send(403, null); else Send(206, new byte[1], length: 1, contentRange: "bytes 0-0/4242"); }
            else if (path == "/fail") Send(503, null);
            else if (path == "/cd") Send(200, new byte[321], disposition: "attachment; filename=\"server name.zip\"");
            else if (path.StartsWith("/slow/") || path.StartsWith("/hold/"))
            {
                int now = Interlocked.Increment(ref active); int old; do { old = Volatile.Read(ref peak); if (now <= old) break; } while (Interlocked.CompareExchange(ref peak, now, old) != old);
                try
                {
                    if (path.StartsWith("/slow/")) { Thread.Sleep(250); Send(200, new byte[1000]); }
                    else
                    {
                        var body = new byte[20000]; r.StatusCode = 200; r.ContentType = "application/octet-stream"; r.ContentLength64 = body.Length;
                        if (method != "HEAD") { r.OutputStream.Write(body, 0, 10000); r.OutputStream.Flush(); Thread.Sleep(300); r.OutputStream.Write(body, 10000, 10000); }
                    }
                }
                finally { Interlocked.Decrement(ref active); }
            }
            else Send(404, null);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { try { r.Close(); } catch (Exception) { } }
    }
    public void Dispose() { try { listener.Stop(); listener.Close(); } catch (Exception) { } }
}

internal sealed class FakeTorrents : ITorrentFeature
{
    public readonly List<string> Added = new();
    public bool IsAvailable => true;
    public string UnavailableMessage => "";
    public void Attach(MainWindow window) { }
    public void CollectRowIds(ISet<string> ids) { }
    public void SyncRows(IList<DownloadRow> rows, IDictionary<string, DownloadRow> lookup, ref bool filterChanged, ref bool sortChanged) { }
    public IReadOnlyList<(QueueJob Job, bool RestoredHistory)> QueueSnapshot() => Array.Empty<(QueueJob, bool)>();
    public Task<int> EnforceSeedLimitAsync() => Task.FromResult(0);
    public Task PauseAsync(DownloadRow row) => Task.CompletedTask;
    public Task ResumeAsync(DownloadRow row) => Task.CompletedTask;
    public Task<(int Deleted, int Failed)> RemoveAsync(string torrentId, bool deleteFiles) => Task.FromResult((0, 0));
    public IReadOnlyList<string> CompletedIds() => Array.Empty<string>();
    public Task<(int Changed, int Skipped, int Failed)> ChangeAllAsync(bool resume) => Task.FromResult((0, 0, 0));
    public Task AddMagnetFromAddDialogAsync(string magnet) => Task.CompletedTask;
    public Task AddMagnetAsync(string magnet) => Task.CompletedTask;
    public Task AddTorrentPathAsync(string path) { Added.Add(path); return Task.CompletedTask; }
    public Task PickTorrentFileAsync() => Task.CompletedTask;
    public void ShowDetails(DownloadRow row) { }
    public Task ApplySettingsAsync(AppSettings settings) => Task.CompletedTask;
    public Task<(bool Ok, string Message)> ImportMagnetAsync(string magnet, TimeSpan timeout) => Task.FromResult((false, ""));
    public string? ValidateMagnet(string magnet, out string id) { id = ""; return null; }
    public bool KnowsTorrent(string id) => false;
    public void Dispose() { }
}

/// <summary>1.9.0 item 9 and the Part B follow-ups: link parsing, the staging table, link checks, packages and folders, queue order and limits, the clipboard watcher, the drop zone of the empty list and the text fit of the sidebar buttons.</summary>
internal static class GrabberTests
{
    private static int passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static void Pump(int ms = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Wait(Task task, int seconds = 30)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!task.IsCompleted && DateTime.UtcNow < end) Pump(20);
        Pump(10); if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult();
    }
    private static void WaitUntil(Func<bool> condition, string what, int seconds = 30)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < end) Pump(30);
        if (!condition()) throw new TimeoutException("timed out waiting for " + what);
    }
    private static T Field<T>(Window w, string name) where T : class => (T)(w.FindName(name) ?? throw new Exception("missing control " + name));
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Use(string theme) => ThemeManager.Apply(Application.Current, theme, null);
    private static IEnumerable<T> Visual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Visual<T>(child)) yield return nested;
        }
    }
    private static RenderTargetBitmap Capture(Window window, double scale = 1)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window); return bitmap;
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static Color Px(BitmapSource shot, int x, int y)
    {
        var px = new byte[4]; shot.CopyPixels(new Int32Rect(Math.Clamp(x, 0, shot.PixelWidth - 1), Math.Clamp(y, 0, shot.PixelHeight - 1), 1, 1), px, 4, 0); return Color.FromRgb(px[2], px[1], px[0]);
    }
    private static double Lum(Color c) { double F(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); } return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B); }
    private static double Contrast(Color a, Color b) { double x = Lum(a), y = Lum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

    private static DownloadManager NewManager(string root, string name, int concurrent = 3, AppSettings? settings = null, int segments = 1)
    {
        var dl = Path.Combine(root, name, "dl"); Directory.CreateDirectory(dl);
        return new DownloadManager(dl, Path.Combine(root, name, "state", "NDM"), new DownloadOptions { MaxConcurrentDownloads = concurrent, MaxSegments = segments, RetryCount = 0 },
            settings is null ? null : ctx => CategoryHelper.GetDownloadPath(ctx.FileName, settings, ctx.Url, ctx.Referer));
    }
    private static AppSettings Settings(string root, string name) => new() { DownloadDirectory = Path.Combine(root, name, "dl"), EnableCategoryFolders = true };

    [STAThread]
    private static int Main()
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-GrabberTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var app = new GrabberTestApp();
        try
        {
            Parsing();
            Logic(scratch);
            Checking(scratch);
            QueueBehaviour(scratch);
            Window(scratch);
            Watcher();
            Drops(scratch);
            DropZone(scratch);
            SidebarFit(scratch);
            Console.WriteLine($"Grabber tests passed: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close();
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            app.Shutdown();
        }
    }

    // ===== parsing =====
    private static string[] Urls(string text) => LinkParser.Parse(text).Links.Select(l => l.Url).ToArray();
    private static void Parsing()
    {
        var mixed = Urls("Here you go: https://a.example/one.zip, and http://b.example/two.pdf; also <a href=\"https://c.example/x.iso\">link</a> end.");
        Check(mixed.SequenceEqual(new[] { "https://a.example/one.zip", "http://b.example/two.pdf", "https://c.example/x.iso" }), "Links are found in mixed text, HTML and after punctuation");
        Check(Urls("see (https://en.example/wiki/Foo_(bar)) now").SequenceEqual(new[] { "https://en.example/wiki/Foo_(bar)" }), "A closing parenthesis that belongs to the link stays, the one that closes the sentence goes");
        Check(Urls("(http://a.example/x)").SequenceEqual(new[] { "http://a.example/x" }) && Urls("[http://a.example/x]").SequenceEqual(new[] { "http://a.example/x" }), "Brackets around a link are not part of it");
        Check(Urls("http://a.example/x. http://a.example/y! http://a.example/z? 'http://a.example/q' \"http://a.example/r\"").Length == 5, "Trailing full stops, exclamation marks, question marks and quotes are dropped");
        Check(Urls("http://[2001:db8::1]:8080/file.bin and http://[::1]/x").SequenceEqual(new[] { "http://[2001:db8::1]:8080/file.bin", "http://[::1]/x" }), "IPv6 hosts are kept whole");
        Check(Urls("http://a.example/x\nhttp://a.example/x\nHTTP://A.EXAMPLE/x\nhttp://a.example/x#frag").Length == 1 && LinkParser.Parse("http://a.example/x http://a.example/x").Duplicates == 1, "Repeats, upper-case spellings and #fragments count as one link");
        var res = LinkParser.Parse("ftp://a.example/f magnet:?xt=urn:btih:abc javascript:alert(1) file:///c:/x data:text/plain,hi mailto:a@b.c https://ok.example/f");
        Check(res.Links.Count == 1 && res.Links[0].Url == "https://ok.example/f" && res.Ignored >= 5, "Only http and https links are kept, other schemes are counted as ignored");
        Check(Urls("https://user:pass@host.example/f http://:80/x http:///x https://").Length == 0, "Links with credentials, no host or nothing after the scheme are ignored");
        Check(Urls("http://a.example/x,http://b.example/y;http://c.example/z").Length == 3, "Links glued together with commas or semicolons are separated");
        Check(Urls("http://a.example/q?u=http://b.example/y").Length == 1, "A link inside the query of another link is not split off");
        Check(Urls("[docs](https://a.example/d.pdf) and **https://b.example/e.zip**").SequenceEqual(new[] { "https://a.example/d.pdf", "https://b.example/e.zip" }), "Markdown links are found");
        Check(Urls("x https://a.example/a\tb https://a.example/c\u200Bd").SequenceEqual(new[] { "https://a.example/a", "https://a.example/c" }) || Urls("x https://a.example/a\tb https://a.example/c\u200Bd").Length == 2, "Tabs and invisible characters end a link");
        Check(Urls("https://a.example/caf\u00e9/\u65e5\u672c.zip").Length == 1 && Urls("https://b\u00fccher.example/x").Length == 1, "International characters in the path and host are accepted");
        var lines = LinkParser.Parse("one\nhttp://a.example/1\n\nthree http://a.example/3").Links;
        Check(lines.Count == 2 && lines[0].Line == 2 && lines[1].Line == 4, "Each link remembers its line number");
        var sb = new StringBuilder(); for (int i = 0; i < 6000; i++) sb.Append("https://cap.example/f").Append(i).Append(".bin\n");
        var capped = LinkParser.Parse(sb.ToString());
        Check(capped.Links.Count == LinkParser.MaxLinks && capped.Truncated, "The list is capped at 5000 links and says so");
        var tooLong = LinkParser.Parse("https://long.example/" + new string('a', 8200) + " https://ok.example/short");
        Check(tooLong.Links.Count == 1 && tooLong.TooLong == 1, "A link longer than 8192 characters is rejected and counted");
        Check(LinkParser.Parse("https://edge.example/" + new string('a', 8192 - 21)).Links.Count == 1, "A link of exactly 8192 characters is accepted");
        var big = new string('x', 9_000_000) + " https://late.example/after-the-cap";
        var sw = System.Diagnostics.Stopwatch.StartNew(); var bigResult = LinkParser.Parse(big); sw.Stop();
        Check(bigResult.Links.Count == 0 && sw.ElapsedMilliseconds < 3000, $"Only the first 8 million characters of a text are read ({sw.ElapsedMilliseconds} ms)");
        var many = new StringBuilder(); for (int i = 0; i < 200000; i++) many.Append("http://x.example/").Append(i % 7).Append(' ');
        sw.Restart(); LinkParser.Parse(many.ToString()); sw.Stop();
        Check(sw.ElapsedMilliseconds < 5000, $"A text with 200000 repeated links is handled quickly ({sw.ElapsedMilliseconds} ms)");
        Check(LinkParser.Parse(null).Links.Count == 0 && LinkParser.Parse("").Links.Count == 0 && LinkParser.Parse("no links at all").Links.Count == 0, "Empty text gives an empty result");
        Check(LinkParser.GuessName("https://a.example/dir/My%20File.zip?x=1") == "My File.zip" && LinkParser.GuessName("https://a.example/").Length > 0 && LinkParser.GuessName("https://a.example/a/b/").Length > 0, "A file name is guessed from the address and is never empty");
        Check(LinkParser.GuessName("https://a.example/..%2f..%2fevil.exe").IndexOfAny(new[] { '/', '\\' }) < 0, "A guessed name never contains path separators");
    }

    // ===== logic =====
    private static void Logic(string root)
    {
        using var server = new MiniServer(); var settings = Settings(root, "logic");
        using var manager = NewManager(root, "logic", settings: settings);
        // an existing job makes its link a duplicate
        Wait(manager.AddPausedAsync(new DownloadRequest(server.BaseUrl + "/ok/1.bin")));
        var logic = new LinkGrabberLogic(manager, settings);
        var summary = logic.Load($"{server.BaseUrl}/ok/1.bin\n{server.BaseUrl}/ok/2.bin\n{server.BaseUrl}/ok/2.bin\nftp://x.example/a\nhttps://www.youtube.com/watch?v=abc\n{server.BaseUrl}/cd");
        Check(logic.Entries.Count == 4 && summary.Added == 4 && summary.Duplicates == 1 && summary.Ignored >= 1 && summary.AlreadyQueued == 1, "Load adds rows and counts repeats, ignored text and links already in the queue");
        Check(logic.Entries[0].State == LinkState.Duplicate && !logic.Entries[0].Selected && logic.Entries[1].Selected && logic.Entries[2].State == LinkState.Unsupported && !logic.Entries[2].Selected, "A queued link starts unticked, a YouTube link is not offered");
        Check(logic.Entries[1].Name == "2.bin" && logic.Entries[1].Host == "127.0.0.1" && logic.Entries[1].Extension == "bin", "Rows show the guessed name, the site and the extension");
        logic.Load($"{server.BaseUrl}/ok/2.bin\n{server.BaseUrl}/ok/3.bin");
        Check(logic.Entries.Count == 5, "Loading the same text again only adds the new links");
        Check(LinkGrabberLogic.CleanPackage("  My pack  ") == "My pack" && LinkGrabberLogic.CleanPackage("   ") is null && LinkGrabberLogic.CleanPackage(new string('x', 200))!.Length == 120 && LinkGrabberLogic.CleanPackage("a\u200Bb\nc") == "ab c", "Package names are cleaned: trimmed, capped at 120 characters, no invisible or control characters");
        string dl = settings.DownloadDirectory;
        Check(logic.PlanFolder(new(null, null, false)) is null && logic.PlanFolder(new("Pack", null, false)) is null, "By default the category rules decide the folder (no explicit folder)");
        Check(logic.PlanFolder(new("Pack", null, true)) == Path.Combine(dl, "Pack") && logic.PlanFolder(new("Pack", Path.Combine(root, "elsewhere"), true)) == Path.Combine(root, "elsewhere", "Pack") && logic.PlanFolder(new(null, Path.Combine(root, "plain"), true)) == Path.Combine(root, "plain"), "A package folder goes under the download folder or the chosen folder; without a package name only the chosen folder counts");
        bool Refused(GrabberDestination d) { try { logic.PlanFolder(d); return false; } catch (UserMessageException) { return true; } }
        Check(logic.PlanFolder(new("CON", null, true)) == Path.Combine(dl, "_CON") && logic.PlanFolder(new("a..", null, true)) == Path.Combine(dl, "a") && Refused(new("pack", "relative\folder", false)), "A reserved package name becomes a safe folder name and an incomplete folder is refused");
        Check(logic.PlanFolder(new("../up", null, true)) is { } up && up.StartsWith(dl) && !up.Contains(".."), "A package name with path characters can never leave the download folder");
        var rows = logic.Entries.Where(e => e.State == LinkState.Ready).ToList();
        rows[0].Name = "renamed.dat";
        var requests = logic.BuildRequests(rows, new("Pack", null, true));
        Check(requests.Count == rows.Count && requests[0].FileName == "renamed.dat" && !requests[0].AutoName && requests[1].FileName is null && requests[1].AutoName && requests.All(r => r.Package == "Pack" && r.TargetDirectory == Path.Combine(dl, "Pack")), "Requests carry the renamed name, auto names for the rest, the package and its folder");
    }

    // ===== checking =====
    private static void Checking(string root)
    {
        using var server = new MiniServer(); var settings = Settings(root, "check");
        using var manager = NewManager(root, "check", settings: settings);
        LinkCheckResult R(string path) { var t = manager.CheckLinkAsync(new DownloadRequest(server.BaseUrl + path)); Wait(t); return t.Result; }
        var ok = R("/ok/1.bin");
        Check(ok.Availability == LinkAvailability.Online && ok.Length == 5000 && !ok.IsPage && ok.FileName == "1.bin", "An available file reports its size and name");
        var gone = R("/gone");
        Check(gone.Availability == LinkAvailability.Offline && gone.Status == 404, "A 404 answer means not found");
        var page = R("/page.html");
        Check(page.Availability == LinkAvailability.Online && page.IsPage, "A web page answer is flagged as a page, not a file");
        var nohead = R("/nohead");
        Check(nohead.Availability == LinkAvailability.Online && nohead.Length == 7777 && server.Log.Any(l => l.Path == "/nohead" && l.Method == "GET"), "When HEAD is refused a ranged GET of one byte finds the size");
        var head403 = R("/head403");
        Check(head403.Availability == LinkAvailability.Online && head403.Length == 4242, "A refusal of HEAD with 403 falls back to the ranged request");
        var fail = R("/fail");
        Check(fail.Availability == LinkAvailability.Unknown && fail.Status == 503, "A server error is unknown, never offline");
        var unreachable = manager.CheckLinkAsync(new DownloadRequest("http://127.0.0.1:1/x")); Wait(unreachable);
        Check(unreachable.Result.Availability == LinkAvailability.Unknown, "A server that cannot be reached is unknown");
        var cd = R("/cd");
        Check(cd.FileName == "server name.zip", "The name the server gives is reported");
        Check(server.Log.All(l => l.Method is "HEAD" or "GET"), "A check only sends HEAD and GET requests");
        // rows
        var logic = new LinkGrabberLogic(manager, settings);
        string token = new string('a', 40);
        logic.Load($"{server.BaseUrl}/ok/1.bin\n{server.BaseUrl}/gone\n{server.BaseUrl}/page.html\n{server.BaseUrl}/cd\n{server.BaseUrl}/x/{token}/file?" + new string('q', 200) + $"\n{server.BaseUrl}/live/index.m3u8");
        int before = server.Log.Count;
        var progress = new List<(int, int)>();
        Wait(logic.CheckAsync(logic.Entries.ToList(), CancellationToken.None, new Progress<(int Done, int Total)>(p => progress.Add(p))));
        var by = logic.Entries.ToDictionary(e => e.Url.Replace(server.BaseUrl, ""));
        Check(by["/ok/1.bin"].State == LinkState.Online && by["/ok/1.bin"].SizeText != "—" && by["/gone"].State == LinkState.Offline && !by["/gone"].Selected && by["/page.html"].State == LinkState.Page && !by["/page.html"].Selected, "Check fills size and status; offline links and pages are unticked");
        Check(by["/cd"].Name == "server name.zip" && !by["/cd"].Renamed, "A name given by the server replaces the guess");
        var tokenRow = logic.Entries.First(e => e.Url.Contains(token)); var streamRow = logic.Entries.First(e => e.Url.EndsWith("index.m3u8"));
        Check(tokenRow.State == LinkState.NotChecked && streamRow.State == LinkState.NotChecked && !server.Log.Any(l => l.Path.Contains(token) || l.Path.EndsWith("index.m3u8")), "Links that look single use and stream playlists are never requested");
        Check(progress.Count > 0 && progress.Last().Item1 == progress.Last().Item2, "Progress ends at the total");
        // renamed rows keep their name
        var one = logic.Entries.First(e => e.Url.EndsWith("/cd")); one.Name = "mine.zip"; Wait(logic.CheckAsync(new[] { one }, CancellationToken.None));
        Check(one.Name == "mine.zip" && one.Renamed, "A check never overwrites a name the user typed");
        // concurrency limit
        var slow = new LinkGrabberLogic(manager, settings);
        slow.Load(string.Join("\n", Enumerable.Range(0, 12).Select(i => $"{server.BaseUrl}/slow/{i}")));
        Wait(slow.CheckAsync(slow.Entries.ToList(), CancellationToken.None));
        Check(server.Peak is > 1 and <= LinkGrabberLogic.CheckConcurrency && slow.Entries.All(e => e.State == LinkState.Online), $"Checks run in parallel but never more than {LinkGrabberLogic.CheckConcurrency} at once (peak {server.Peak})");
        // cancel
        var cancelLogic = new LinkGrabberLogic(manager, settings);
        cancelLogic.Load(string.Join("\n", Enumerable.Range(100, 40).Select(i => $"{server.BaseUrl}/slow/{i}")));
        using var cts = new CancellationTokenSource(); var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = cancelLogic.CheckAsync(cancelLogic.Entries.ToList(), cts.Token); cts.CancelAfter(400); Wait(run); sw.Stop();
        Check(sw.ElapsedMilliseconds < 5000 && cancelLogic.Entries.Any(e => e.State == LinkState.Ready) && cancelLogic.Entries.All(e => e.State != LinkState.Checking), $"A check can be cancelled; unreached rows stay as they were ({sw.ElapsedMilliseconds} ms)");
    }

    // ===== queue =====
    private static void QueueBehaviour(string root)
    {
        using var server = new MiniServer(); var settings = Settings(root, "queue");
        using var manager = NewManager(root, "queue", concurrent: 1, settings: settings);
        var logic = new LinkGrabberLogic(manager, settings);
        logic.Load(string.Join("\n", Enumerable.Range(1, 5).Select(i => $"{server.BaseUrl}/ok/{i}.bin")));
        var outcome = Task.Run(() => logic.AddSelectedAsync(new("Order pack", null, true), start: true)).GetAwaiter().GetResult();
        Check(outcome.Added == 5 && outcome.Package == "Order pack" && outcome.Folder == Path.Combine(settings.DownloadDirectory, "Order pack") && outcome.Jobs.All(j => j.Package == "Order pack"), "Add to queue adds every ticked link with the package name");
        WaitUntil(() => manager.Jobs.All(j => j.Status is "Completed" or "Failed"), "the queue to finish");
        var jobs = manager.Jobs;
        Check(jobs.Count == 5 && jobs.All(j => j.Status == "Completed") && jobs.All(j => Path.GetDirectoryName(j.FilePath) == Path.Combine(settings.DownloadDirectory, "Order pack") && File.Exists(j.FilePath)), "Every file is saved into the package folder");
        var firstSeen = server.Log.Where(l => l.Method == "GET").Select(l => l.Path).Distinct().ToList();
        Check(firstSeen.SequenceEqual(Enumerable.Range(1, 5).Select(i => $"/ok/{i}.bin")), "With one connection the files start in table order");
        // order in the job list follows the table
        var logic2 = new LinkGrabberLogic(manager, settings); logic2.Load($"{server.BaseUrl}/ok/9.bin\n{server.BaseUrl}/ok/8.bin\n{server.BaseUrl}/ok/7.bin");
        logic2.Entries[1].Selected = false;
        var paused = Task.Run(() => logic2.AddSelectedAsync(new(null, null, false), start: false)).GetAwaiter().GetResult();
        Check(paused.Added == 2 && paused.Package is null && paused.Jobs.Select(j => j.FileName).SequenceEqual(new[] { "9.bin", "7.bin" }) && paused.Jobs.All(j => j.Status == "Paused" && j.Package is null), "Add paused adds only the ticked rows, in table order, paused and without a package");
        Check(manager.Jobs.Count(j => j.FilePath.StartsWith(settings.DownloadDirectory)) == 7, "Without a package or folder the files follow the category rules (here the download folder)");
        // concurrency limit with long downloads
        using var manager2 = NewManager(root, "queue2", concurrent: 2, settings: Settings(root, "queue2"));
        var logic3 = new LinkGrabberLogic(manager2, Settings(root, "queue2"));
        logic3.Load(string.Join("\n", Enumerable.Range(1, 6).Select(i => $"{server.BaseUrl}/hold/{i}.bin")));
        int simultaneous = 0; var sampler = Task.Run(() => { var end = DateTime.UtcNow.AddSeconds(20); while (DateTime.UtcNow < end && !manager2.Jobs.All(j => j.Status is "Completed" or "Failed") || manager2.Jobs.Count == 0) { simultaneous = Math.Max(simultaneous, manager2.Jobs.Count(j => j.Status is "Downloading" or "Connecting")); Thread.Sleep(15); } });
        var added = Task.Run(() => logic3.AddSelectedAsync(new("Hold", null, false), start: true)).GetAwaiter().GetResult();
        Wait(sampler, 40);
        Check(added.Added == 6 && manager2.Jobs.All(j => j.Status == "Completed") && simultaneous is >= 1 and <= 2 && server.Peak <= 2 + 1, $"The queue never runs more downloads than the connection limit (seen {simultaneous} at once)");
        // a page instead of a file fails with the existing message
        var logic4 = new LinkGrabberLogic(manager, settings); logic4.Load($"{server.BaseUrl}/page.html");
        logic4.Entries[0].Name = "page.zip";
        Task.Run(() => logic4.AddSelectedAsync(new(null, null, false), true)).GetAwaiter().GetResult();
        WaitUntil(() => manager.Jobs.First(j => j.FileName == "page.zip").Status is "Completed" or "Failed", "the page job");
        var pageJob = manager.Jobs.First(j => j.FileName == "page.zip");
        Check(pageJob.Status == "Failed" && pageJob.ErrorCode == ContentGuard.WrongContent, "A web page that is not a file fails with the existing wrong-content message");
        // retry works for failed grabber jobs
        manager.Resume(pageJob.Id); WaitUntil(() => manager.Jobs.First(j => j.Id == pageJob.Id).Status is "Failed", "the retry");
        Check(manager.Jobs.First(j => j.Id == pageJob.Id).Package is null, "Retry keeps the job and its settings");
        // package survives a restart
        manager.Dispose();
        using var reopened = new DownloadManager(Path.Combine(root, "queue", "dl"), Path.Combine(root, "queue", "state", "NDM"));
        Check(reopened.Jobs.Count(j => j.Package == "Order pack") == 5, "Package names survive a restart");
        // bad folder: nothing half added
        using var manager3 = NewManager(root, "queue3"); var logic5 = new LinkGrabberLogic(manager3, Settings(root, "queue3")); logic5.Load($"{server.BaseUrl}/ok/1.bin");
        string blocker = Path.Combine(root, "queue3", "blocker"); File.WriteAllText(blocker, "x");
        var failed = Task.Run(() => logic5.AddSelectedAsync(new("x", Path.Combine(blocker, "sub"), false), true)).GetAwaiter().GetResult();
        Check(failed.Added == 0 && failed.Failed == 1 && manager3.Jobs.Count == 0, "A folder that cannot be created adds nothing and says so");
    }

    // ===== window =====
    private static LinkGrabberWindow Open(DownloadManager m, AppSettings s, string? text, bool show = true, Action? save = null)
    {
        var w = new LinkGrabberWindow(m, s, text, save) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
        if (show) { w.Show(); Pump(100); }
        return w;
    }
    private static void Window(string root)
    {
        Loc.SetLanguage("en"); Use("Light");
        using var server = new MiniServer(); var settings = Settings(root, "win");
        using var manager = NewManager(root, "win", settings: settings);
        var w = Open(manager, settings, $"links:\n{server.BaseUrl}/ok/1.bin\n{server.BaseUrl}/ok/2.bin\n{server.BaseUrl}/ok/3.zip\n{server.BaseUrl}/ok/1.bin");
        var list = Field<ListView>(w, "LinksList");
        Check(list.Items.Count == 3 && Field<TextBlock>(w, "Summary").Text.Contains("Links found: 3") && Field<TextBlock>(w, "Summary").Text.Contains("Repeats skipped: 1") && Field<TextBlock>(w, "CountText").Text == "3 of 3 selected", "A prefilled text is parsed at once and summarised");
        Check(!Field<Button>(w, "AddButton").IsDefault && Field<Button>(w, "AddButton").IsEnabled && Field<Button>(w, "PausedButton").IsEnabled, "The add buttons are ready when links are ticked");
        var filter = Field<TextBox>(w, "FilterBox"); filter.Text = "zip"; Pump(40);
        Check(list.Items.Count == 1 && Field<TextBlock>(w, "CountText").Text.Contains("shown"), "The filter box narrows the table by name, site or extension");
        Click(Field<Button>(w, "SelectNoneButton")); Pump(20);
        Check(w.Logic.Entries.Count(e => e.Selected) == 2, "Select none only touches the rows that are shown");
        filter.Text = ""; Pump(40);
        Click(Field<Button>(w, "SelectNoneButton")); Pump(20);
        Check(w.Logic.Entries.All(e => !e.Selected) && !Field<Button>(w, "AddButton").IsEnabled, "Select none clears everything and disables the add buttons");
        Click(Field<Button>(w, "SelectAllButton")); Pump(20); Click(Field<Button>(w, "InvertButton")); Pump(20);
        Check(w.Logic.Entries.All(e => !e.Selected), "Invert flips every shown row");
        Click(Field<Button>(w, "SelectAllButton")); Pump(20);
        var host = Field<ComboBox>(w, "HostFilter");
        Check(host.Items.Count == 2, "The site filter lists All sites and each host");
        list.SelectedIndex = 0; Pump(30);
        var rename = Field<TextBox>(w, "RenameBox");
        Check(rename.IsEnabled && rename.Text == w.Logic.Entries[0].Name, "Selecting one row loads its name for renaming");
        rename.Text = "First file.bin"; Pump(30);
        Check(w.Logic.Entries[0].Name == "First file.bin" && w.Logic.Entries[0].Renamed, "Renaming changes the row");
        list.SelectedIndex = -1; list.SelectedItems.Clear(); list.SelectedItems.Add(w.Logic.Entries[0]); list.SelectedItems.Add(w.Logic.Entries[1]); Pump(30);
        Check(!rename.IsEnabled, "With two rows selected renaming is off");
        Click(Field<Button>(w, "RemoveButton")); Pump(30);
        Check(w.Logic.Entries.Count == 1 && Field<TextBlock>(w, "CountText").Text == "1 of 1 selected", "Remove rows takes the highlighted rows out of the table");
        // package and destination
        var package = Field<ComboBox>(w, "PackageBox"); var subfolder = Field<CheckBox>(w, "SubfolderBox");
        Check(!subfolder.IsEnabled, "The package folder box is off while there is no package name");
        package.Text = "My   pack"; Pump(30);
        Check(subfolder.IsEnabled, "Typing a package name enables the package folder box");
        subfolder.IsChecked = true; Pump(20);
        Check(w.PlannedFolder == Path.Combine(settings.DownloadDirectory, "My pack"), "The planned folder is named after the package");
        Field<RadioButton>(w, "FolderRadio").IsChecked = true; Field<TextBox>(w, "FolderBox").Text = Path.Combine(root, "win", "chosen"); Pump(30);
        Check(Field<TextBox>(w, "FolderBox").IsEnabled && w.PlannedFolder == Path.Combine(root, "win", "chosen", "My pack"), "A chosen folder holds the package folder");
        Field<TextBox>(w, "FolderBox").Text = "relative"; Pump(20);
        bool saved = false;
        Wait(w.AddAsync(true));
        Check(w.IsVisible && Field<TextBlock>(w, "ErrorText").Text.Length > 0 && manager.Jobs.Count == 0, "An incomplete folder keeps the window open with a message and adds nothing");
        Field<TextBox>(w, "FolderBox").Text = Path.Combine(root, "win", "chosen"); Pump(20);
        w.Closed += (_, _) => saved = true;
        var add = w.AddAsync(false); Wait(add);
        Check(add.Result && w.Outcome is { Added: 1, Package: "My pack" } && manager.Jobs.Count == 1 && manager.Jobs[0].Status == "Paused" && manager.Jobs[0].FilePath.StartsWith(Path.Combine(root, "win", "chosen", "My pack")) && saved, "Add paused closes the window and adds a paused job in the package folder");
        Check(settings.RecentPackageNames.SequenceEqual(new[] { "My pack" }), "The package name is remembered");
        // window with nothing ticked
        var w2 = Open(manager, settings, "nothing here");
        Check(Field<TextBlock>(w2, "Summary").Text.Contains("Links found: 0") && Field<TextBlock>(w2, "Summary").Text.Length > 0, "Text without links is reported");
        Pump(600); // let the 350 ms parse debounce fire first: it clears the error line
        Wait(w2.AddAsync(true));
        Check(Field<TextBlock>(w2, "ErrorText").Text.Contains("Tick at least one"), "Adding with nothing ticked asks to tick a link");
        // check button on the window
        var w3 = Open(manager, settings, $"{server.BaseUrl}/ok/5.bin\n{server.BaseUrl}/gone");
        Click(Field<Button>(w3, "CheckButton")); WaitUntil(() => w3.Logic.Entries.All(e => e.State is LinkState.Online or LinkState.Offline), "the check");
        Pump(100);
        Check(w3.Logic.Entries[1].State == LinkState.Offline && !w3.Logic.Entries[1].Selected && Field<TextBlock>(w3, "StatusText").Text.Contains("1 available, 1 not found"), "The Check links button fills the table and reports the result");
        Check(Field<Button>(w3, "CheckButton").Content as string == Loc.T("grab.btn.check") && !w3.Checking, "The check button returns to its first caption when the pass is done");
        w.Close(); w2.Close(); w3.Close(); Pump(30);

        // looks: themes x sizes x DPI x RTL
        string? shots = Environment.GetEnvironmentVariable("NEODM_SHOTS"); var problems = new List<string>(); int renders = 0;
        foreach (var rtl in new[] { false, true })
            foreach (var theme in ThemeCatalog.Concrete)
                foreach (var (scale, small) in new[] { (1.0, true), (1.0, false), (1.5, true), (2.0, false) })
                {
                    Loc.SetLanguage(rtl ? "ar" : "en"); Use(theme.Id); renders++;
                    string tag = $"{theme.Id}{(rtl ? " RTL" : "")} {scale * 100:0}% {(small ? "small" : "large")}";
                    var text = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"{server.BaseUrl}/ok/file-with-a-long-name-{i}.zip"));
                    var win = Open(manager, settings, text, show: false);
                    VisualTreeHelper.SetRootDpi(win, new DpiScale(scale, scale));
                    if (small) { win.Width = win.MinWidth; win.Height = win.MinHeight; } else { win.Width = 1280; win.Height = 900; }
                    win.Show(); Pump(80); win.UpdateLayout();
                    var content = (FrameworkElement)win.Content; var lv = Field<ListView>(win, "LinksList");
                    lv.SelectedIndex = 2; Pump(20);
                    if (lv.ActualHeight < 100) problems.Add($"{tag}: table only {lv.ActualHeight:0} high");
                    foreach (var name in new[] { "AddButton", "PausedButton", "CancelButton", "CheckButton", "SelectAllButton", "RemoveButton", "BrowseButton" })
                    {
                        var b = Field<Button>(win, name); bool footer = name is "AddButton" or "PausedButton" or "CancelButton" or "CheckButton";
                        if (!footer) { b.BringIntoView(); Pump(15); win.UpdateLayout(); }
                        var frame = footer ? (FrameworkElement)content : Field<ScrollViewer>(win, "Scroller");
                        var r = b.TransformToAncestor(frame).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight));
                        if (r.Right > frame.ActualWidth + 1 || r.Left < -1 || r.Bottom > frame.ActualHeight + 1 || r.Top < -1) problems.Add($"{tag}: {name} leaves the window ({r})");
                        if (b.ActualHeight > 0 && b.Content is string caption0)
                        {
                            var caption = caption0.Replace("_", "");
                            var ft = new FormattedText(caption, CultureInfo.CurrentCulture, rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, new Typeface(b.FontFamily, b.FontStyle, b.FontWeight, b.FontStretch), b.FontSize, Brushes.Black, 1.0);
                            if (ft.Width > b.ActualWidth - b.Padding.Left - b.Padding.Right + 0.5) problems.Add($"{tag}: caption of {name} is wider than the button");
                        }
                    }
                    var input = Field<TextBox>(win, "LinksInput"); if (input.ActualHeight < 40) problems.Add($"{tag}: paste box only {input.ActualHeight:0} high");
                    var fb = Field<TextBox>(win, "FilterBox"); var fh = (ScrollViewer)fb.Template.FindName("PART_ContentHost", fb); if (fh.ScrollableHeight > 0.5) problems.Add($"{tag}: filter box text is clipped");
                    if (rtl && win.FlowDirection != FlowDirection.RightToLeft) problems.Add($"{tag}: not right-to-left");
                    var surface = (Color)ColorConverter.ConvertFromString(((SolidColorBrush)Application.Current.FindResource("SurfaceBrush")).Color.ToString());
                    var shot = Capture(win, scale);
                    var lvRect = lv.TransformToAncestor(win).TransformBounds(new Rect(0, 0, lv.ActualWidth, lv.ActualHeight));
                    int ink = 0; for (int y = (int)(lvRect.Top * scale) + 30; y < (int)(lvRect.Bottom * scale) - 4 && y < shot.PixelHeight; y += 3) for (int x = (int)(lvRect.Left * scale) + 8; x < (int)(lvRect.Right * scale) - 8 && x < shot.PixelWidth; x += 3) if (Contrast(Px(shot, x, y), surface) > 3) ink++;
                    if (ink < 20) problems.Add($"{tag}: the table shows no readable text");
                    if (shots is { Length: > 0 } && scale == 1.0 && (theme.Id is "DeepNavy" or "Light" or "Midnight") && !rtl || shots is { Length: > 0 } && rtl && theme.Id == "DeepNavy" && scale == 1.0)
                    { Save(shot, Path.Combine(shots, $"grabber-{theme.Id}{(rtl ? "-rtl" : "")}-{(small ? "small" : "large")}.png")); Console.WriteLine($"SHOT: grabber-{theme.Id}{(rtl ? "-rtl" : "")}-{(small ? "small" : "large")}"); }
                    win.Close(); Pump(10);
                }
        Loc.SetLanguage("en"); Use("Light");
        Check(problems.Count == 0, $"The link grabber window is whole in {renders} renders (themes x sizes x DPI x LTR/RTL): " + string.Join(" | ", problems.Take(8)));
    }

    // ===== clipboard watcher =====
    private static void Watcher()
    {
        uint seq = 100; string? text = null; bool on = false; var found = new List<string>(); var clock = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var w = new ClipboardLinkWatcher(() => on, () => seq, () => text, found.Add, () => clock);
        var settings = new AppSettings();
        Check(!settings.WatchClipboardForLinks, "The clipboard option is off by default");
        text = "http://a.example/1 http://a.example/2"; seq++; w.Poll();
        Check(found.Count == 0 && w.ReadCount == 0, "While the option is off the clipboard text is never read");
        on = true; w.Poll(); w.Poll();
        Check(found.Count == 0 && w.ReadCount == 0 || found.Count == 0, "Switching it on ignores what was already on the clipboard");
        seq++; text = "http://a.example/1 http://a.example/2"; clock = clock.AddSeconds(10); w.Poll();
        Check(found.Count == 1 && found[0] == text && w.ReadCount >= 1, "A new copy with two links is offered");
        w.Poll();
        Check(found.Count == 1, "Without a change of the clipboard counter nothing is read again");
        seq++; clock = clock.AddSeconds(1); w.Poll(); clock = clock.AddSeconds(10); seq++; w.Poll();
        Check(found.Count == 1, "The same text is not offered again within a minute, and offers are spaced out");
        seq++; text = "just one link http://a.example/1"; clock = clock.AddSeconds(30); w.Poll();
        Check(found.Count == 1, "A text with a single link is not offered");
        seq++; text = "plain words only"; clock = clock.AddSeconds(30); w.Poll();
        Check(found.Count == 1, "A text without links is not offered");
        seq++; text = new string('x', ClipboardLinkWatcher.MaxTextLength + 1) + " http://a.example/1 http://a.example/2"; clock = clock.AddSeconds(30); w.Poll();
        Check(found.Count == 1, "A huge text is never parsed");
        seq++; text = "http://a.example/3 http://a.example/4"; clock = clock.AddSeconds(30); w.IgnoreOwn(text); w.Poll();
        Check(found.Count == 1, "Text NeoDM copied itself is ignored");
        seq++; text = "http://a.example/5 http://a.example/6"; clock = clock.AddSeconds(30); w.Poll();
        Check(found.Count == 2, "After the pause a new text is offered");
        seq++; text = "http://a.example/7 http://a.example/8"; clock = clock.AddSeconds(1); w.Poll();
        Check(found.Count == 2, "Offers are at least a few seconds apart");
        seq++; text = "http://a.example/9 http://a.example/10"; clock = clock.AddSeconds(30); w.Poll();
        Check(found.Count == 3, "Later texts are offered again");
        on = false; w.Poll(); seq++; text = "http://a.example/11 http://a.example/12"; clock = clock.AddSeconds(60); w.Poll(); on = true; w.Poll();
        Check(found.Count == 3, "Turning the option off and on again forgets what was copied while it was off");
        var throwing = new ClipboardLinkWatcher(() => true, () => seq++, () => throw new System.Runtime.InteropServices.COMException("locked"), _ => { }, () => clock);
        throwing.Poll(); throwing.Poll(); throwing.Poll();
        Check(true, "A locked clipboard is not an error");
        Check(ClipboardLinkWatcher.SystemSequence() > 0, "The real clipboard counter can be read");
    }

    // ===== drops =====
    private static void Drops(string root)
    {
        string dir = Path.Combine(root, "drops"); Directory.CreateDirectory(dir);
        string list = Path.Combine(dir, "links.txt"); File.WriteAllText(list, "https://a.example/1.zip\nhttps://a.example/2.zip\n");
        string torrent = Path.Combine(dir, "x.torrent"); File.WriteAllBytes(torrent, new byte[] { 1, 2, 3 });
        string other = Path.Combine(dir, "pic.png"); File.WriteAllBytes(other, new byte[] { 1 });
        var files = new DataObject(DataFormats.FileDrop, new[] { list, torrent, other });
        var plan = DropPlan.Read(files);
        Check(plan.Torrents.SequenceEqual(new[] { torrent }) && plan.Text!.Contains("a.example/2.zip") && plan.Skipped == 1 && plan.HasContent, "A dropped list file, a .torrent file and an unsupported file are told apart");
        var text = new DataObject(DataFormats.UnicodeText, "see https://a.example/1 and https://a.example/2");
        Check(DropPlan.Read(text).Text!.StartsWith("see") && DropPlan.Read(text).Torrents.Count == 0, "Dropped text is passed on");
        Check(!DropPlan.Read(new DataObject(DataFormats.UnicodeText, "   ")).HasContent && !DropPlan.Read(null).HasContent && !DropPlan.Read(new DataObject(DataFormats.FileDrop, new[] { other })).HasContent, "Blank text and unsupported files are not accepted");
        string big = Path.Combine(dir, "big.txt"); File.WriteAllText(big, new string('a', 5_000_000));
        Check(DropPlan.Read(new DataObject(DataFormats.FileDrop, new[] { big })).Text!.Length <= 4 * 1024 * 1024 + 4, "A big list file is read only up to 4 MB");
        Check(DropPlan.Read(new DataObject(DataFormats.FileDrop, new[] { Path.Combine(dir, "missing.txt") })).Skipped == 1, "A list file that cannot be read is skipped");
    }

    // ===== drop zone and entry points =====
    private static MainWindow Main(string root, string name, out DownloadManager manager, out FakeTorrents torrents, AppSettings? settings = null)
    {
        settings ??= Settings(root, name); manager = NewManager(root, name, settings: settings); torrents = new FakeTorrents();
        var w = new MainWindow(manager, settings, () => { }, enableTray: false, power: null, torrents: torrents) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
        return w;
    }
    private static void DropZone(string root)
    {
        Loc.SetLanguage("en"); Use("Light");
        string dir = Path.Combine(root, "dropzone"); Directory.CreateDirectory(dir);
        var w = Main(root, "dropzone", out var manager, out var torrents);
        w.Show(); Pump(150); w.UpdateLayout();
        var empty = Field<Grid>(w, "EmptyState"); var frame = Field<System.Windows.Shapes.Rectangle>(w, "DropZoneFrame");
        Check(empty.Visibility == Visibility.Visible && frame.Visibility == Visibility.Visible && frame.StrokeDashArray.Count >= 2 && frame.RadiusX >= 8, "An empty list shows a dashed rounded drop zone");
        Check(Field<TextBlock>(w, "EmptyHeading").Text == "No downloads" && Field<TextBlock>(w, "EmptyBody").Text == "Add a URL, drop links or a .torrent file here, or send one from your browser.", "The drop zone says in plain words what to do");
        Check(Field<Button>(w, "EmptyAddUrl").IsVisible && Field<Button>(w, "EmptyAddTorrent").IsVisible && Field<Button>(w, "EmptyGrabber").IsVisible && Field<Viewbox>(w, "EmptyIcon").IsVisible, "The drop zone has an icon and the buttons Add URL, Add torrent and Link grabber");
        Check(!Visual<Button>(empty).Any(b => b.Content is string s && (s.Contains("Sample") || s.Contains("Populate") || s.Contains("Preview"))), "There are no debug or sample buttons");
        // buttons
        LinkGrabberWindow? seen = null; w.GrabberCreated = g => { seen = g; Dispatcher.CurrentDispatcher.BeginInvoke(new Action(g.Close)); };
        Click(Field<Button>(w, "EmptyGrabber")); Pump(200);
        Check(seen is not null && seen.Logic.Entries.Count == 0, "The Link grabber button opens an empty link grabber");
        seen = null; w.OpenGrabber("https://a.example/1.zip\nhttps://a.example/2.zip"); Pump(100);
        Check(seen is not null && seen.Logic.Entries.Count == 2, "OpenGrabber prefills the grabber");
        // drops
        seen = null; Wait(w.HandleDropAsync(new DropPlan(Array.Empty<string>(), "https://b.example/1.zip https://b.example/2.zip https://b.example/3.zip", 0))); 
        Check(seen is not null && seen.Logic.Entries.Count == 3, "Dropping text opens the link grabber with the links");
        seen = null; string torrentPath = Path.Combine(dir, "t.torrent"); File.WriteAllBytes(torrentPath, new byte[] { 1 });
        Wait(w.HandleDropAsync(new DropPlan(new[] { torrentPath }, null, 0)));
        Check(torrents.Added.SequenceEqual(new[] { torrentPath }) && seen is null, "Dropping a .torrent file adds a torrent and does not open the grabber");
        string listPath = Path.Combine(dir, "list.txt"); File.WriteAllText(listPath, "https://c.example/a.bin\nhttps://c.example/b.bin");
        seen = null; Wait(w.HandleDropAsync(DropPlan.Read(new DataObject(DataFormats.FileDrop, new[] { listPath }))));
        Check(seen is not null && seen.Logic.Entries.Count == 2, "Dropping a .txt list opens the link grabber prefilled");
        Check(w.EffectsFor(new DataObject(DataFormats.UnicodeText, "https://x.example/1")) == DragDropEffects.Copy && w.EffectsFor(new DataObject(DataFormats.FileDrop, new[] { torrentPath })) == DragDropEffects.Copy && w.EffectsFor(new DataObject(DataFormats.Bitmap, new byte[1])) == DragDropEffects.None, "Dragging links or a .torrent over the window shows the copy cursor; other data does not");
        var before = frame.Stroke; w.SetDropHighlight(true); var active = frame.Stroke; w.SetDropHighlight(false);
        Check(!ReferenceEquals(before, active) && frame.Stroke is SolidColorBrush s0 && ((SolidColorBrush)Application.Current.FindResource("BorderBrush")).Color == s0.Color, "While something is dragged over, the frame uses the accent colour and goes back afterwards");
        // entry points: menu, shortcut, sidebar
        Check(Visual<MenuItem>(w).Any(m => m.InputGestureText == "Ctrl+Shift+V" && Equals(m.Header, Loc.T("main.menu.link_grabber"))) || w.FindName("GrabberButton") is Button, "The Tasks menu and the sidebar offer the link grabber");
        seen = null; Click(Field<Button>(w, "GrabberButton")); Pump(200);
        Check(seen is not null, "The sidebar Link grabber button opens the grabber");
        // package column
        Wait(manager.AddPausedAsync(new DownloadRequest("http://127.0.0.1:1/p.bin", Package: "Col pack"))); Pump(700);
        var rows = Field<ListView>(w, "DownloadList").ItemsSource.Cast<DownloadRow>().ToList();
        Check(rows.Count == 1 && rows[0].Package == "Col pack" && rows[0].PackageText == "Col pack" && Field<Grid>(w, "EmptyState").Visibility == Visibility.Collapsed, "A package name shows in the Package column and the drop zone goes away when downloads exist");
        Check(w.FindName("PackageColumn") is System.Windows.Controls.GridViewColumn pc && pc.Width > 50, "The Package column opens when a download has a package");
        Field<TextBox>(w, "Search").Text = "col pack"; Pump(300);
        Check(Field<ListView>(w, "DownloadList").Items.Count == 1, "Search also finds a package name");
        Field<TextBox>(w, "Search").Text = "nothing matches this"; Pump(300);
        Check(Field<Grid>(w, "EmptyState").Visibility == Visibility.Visible && Field<System.Windows.Shapes.Rectangle>(w, "DropZoneFrame").Visibility == Visibility.Collapsed && !Field<Button>(w, "EmptyAddUrl").IsVisible, "A search that finds nothing shows plain text, not the drop zone");
        w.Close(); manager.Dispose(); Pump(30);

        // looks of the drop zone
        string? shots = Environment.GetEnvironmentVariable("NEODM_SHOTS"); var problems = new List<string>(); int renders = 0;
        foreach (var rtl in new[] { false, true })
            foreach (var theme in ThemeCatalog.Concrete)
                foreach (var (width, height, scale) in new[] { (900.0, 480.0, 1.0), (1500.0, 900.0, 1.0), (1000.0, 600.0, 1.5), (1100.0, 700.0, 2.0) })
                {
                    Loc.SetLanguage(rtl ? "ar" : "en"); Use(theme.Id); renders++;
                    string tag = $"{theme.Id}{(rtl ? " RTL" : "")} {width:0}x{height:0} @{scale * 100:0}%";
                    var win = Main(root, "zone-" + renders, out var m2, out _);
                    VisualTreeHelper.SetRootDpi(win, new DpiScale(scale, scale));
                    win.Width = width; win.Height = height; win.Show(); Pump(200); win.UpdateLayout();
                    var grid = Field<Grid>(win, "EmptyState"); var fr = Field<System.Windows.Shapes.Rectangle>(win, "DropZoneFrame"); var wrap = Field<WrapPanel>(win, "EmptyButtons");
                    var bounds = fr.TransformToAncestor(win).TransformBounds(new Rect(0, 0, fr.ActualWidth, fr.ActualHeight));
                    foreach (var name in new[] { "EmptyAddUrl", "EmptyAddTorrent", "EmptyGrabber" })
                    {
                        var b = Field<Button>(win, name); var scroll = Visual<ScrollViewer>(grid).First();
                        var r = b.TransformToAncestor(win).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight));
                        if (!b.IsVisible || b.ActualWidth < 40) problems.Add($"{tag}: {name} not shown");
                        if (r.Left < bounds.Left - 1 || r.Right > bounds.Right + 1) problems.Add($"{tag}: {name} sticks out of the frame ({r.Left:0}..{r.Right:0} vs {bounds.Left:0}..{bounds.Right:0})");
                        if (scroll.ScrollableHeight > 0.5) { if (r.Bottom > bounds.Bottom + 1 && scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible) problems.Add($"{tag}: {name} cut off without a scroll bar"); }
                        else if (r.Bottom > bounds.Bottom + 1 || r.Top < bounds.Top - 1) problems.Add($"{tag}: {name} is outside the frame vertically");
                    }
                    var body = Field<TextBlock>(win, "EmptyBody"); var body2 = body.TransformToAncestor(win).TransformBounds(new Rect(0, 0, body.ActualWidth, body.ActualHeight));
                    var ft = new FormattedText(body.Text, CultureInfo.CurrentCulture, rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, new Typeface(body.FontFamily, body.FontStyle, body.FontWeight, body.FontStretch), body.FontSize, Brushes.Black, 1.0) { MaxTextWidth = Math.Max(10, body.ActualWidth) };
                    if (ft.Height > body.ActualHeight + 1) problems.Add($"{tag}: body text is cut ({ft.Height:0.0} > {body.ActualHeight:0.0})");
                    if (rtl && win.FlowDirection != FlowDirection.RightToLeft) problems.Add($"{tag}: not RTL");
                    // pixel check: the dashed frame is visible against the list background in this theme
                    var surface = ((SolidColorBrush)Application.Current.FindResource("SurfaceBrush")).Color; var shot = Capture(win, scale);
                    int hits = 0; int y0 = (int)((bounds.Top + bounds.Height / 2) * scale);
                    for (int x = (int)((bounds.Left - 2) * scale); x < (int)((bounds.Left + 4) * scale); x++) if (Contrast(Px(shot, x, y0), surface) > 1.12) hits++;
                    for (int x = (int)((bounds.Right - 4) * scale); x < (int)((bounds.Right + 2) * scale); x++) if (Contrast(Px(shot, x, y0 + 3), surface) > 1.12) hits++;
                    int topHits = 0; int cx = (int)((bounds.Left + bounds.Width / 2) * scale);
                    for (int x = cx - 40; x < cx + 40; x++) for (int y = (int)((bounds.Top - 2) * scale); y < (int)((bounds.Top + 3) * scale); y++) if (Contrast(Px(shot, x, y), surface) > 1.12) { topHits++; break; }
                    if (topHits < 8) problems.Add($"{tag}: the dashed frame is not visible against the list ({topHits} dash pixels)");
                    var textColor = ((SolidColorBrush)Application.Current.FindResource("MutedBrush")).Color;
                    if (Contrast(textColor, surface) < 4.5) problems.Add($"{tag}: body text contrast {Contrast(textColor, surface):0.0}");
                    if (shots is { Length: > 0 } && (theme.Id is "DeepNavy" or "Light" or "Midnight") && scale == 1.0 && (!rtl || theme.Id == "DeepNavy") && (width == 900.0 || width == 1500.0 && theme.Id == "DeepNavy"))
                    { Save(shot, Path.Combine(shots, $"dropzone-{theme.Id}{(rtl ? "-rtl" : "")}-{width:0}.png")); Console.WriteLine($"SHOT: dropzone-{theme.Id}{(rtl ? "-rtl" : "")}-{width:0}"); }
                    win.Close(); m2.Dispose(); Pump(10);
                }
        Loc.SetLanguage("en"); Use("Light");
        Check(problems.Count == 0, $"The drop zone is whole and themed in {renders} renders (themes x sizes x DPI x LTR/RTL): " + string.Join(" | ", problems.Take(8)));
    }

    // ===== sidebar text fit =====
    private static void SidebarFit(string root)
    {
        string? shots = Environment.GetEnvironmentVariable("NEODM_SHOTS"); var problems = new List<string>(); int renders = 0;
        string[] languages = { "en", "ar", "es", "fr", "de", "pt-BR", "ru", "tr", "zh-Hans", "ja" };
        foreach (var lang in languages)
            foreach (var theme in new[] { "Light", "Dark", "Midnight", "DeepNavy" })
                foreach (var scale in new[] { 1.0, 1.5, 2.0 })
                    foreach (var width in new[] { 900.0, 1400.0 })
                    {
                        Loc.SetLanguage(lang); Use(theme); renders++;
                        string tag = $"{lang} {theme} {scale * 100:0}% {width:0}";
                        var win = Main(root, "side-" + renders, out var m, out _);
                        VisualTreeHelper.SetRootDpi(win, new DpiScale(scale, scale));
                        win.Width = width; win.Height = 700; win.Show(); Pump(60); win.UpdateLayout();
                        var surface = ((SolidColorBrush)Application.Current.FindResource("SidebarBrush")).Color;
                        var shot = Capture(win, scale);
                        foreach (var name in new[] { "ResumeAllButton", "BulkButton", "GrabberButton" })
                        {
                            var b = Field<Button>(win, name);
                            var tb = Visual<TextBlock>(b).FirstOrDefault(t => t.Text == (b.Content as string));
                            if (tb is null) { problems.Add($"{tag}: {name} has no text block"); continue; }
                            var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
                            // 1. no word may be wider than the space the text has (a longer word would be cut)
                            foreach (var word in tb.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            {
                                var ww = new FormattedText(word, CultureInfo.CurrentCulture, win.FlowDirection, typeface, tb.FontSize, Brushes.Black, scale).WidthIncludingTrailingWhitespace;
                                if (ww > tb.ActualWidth + 0.5) problems.Add($"{tag}: the word '{word}' of {name} is wider than its text area ({ww:0.0} > {tb.ActualWidth:0.0})");
                            }
                            // 2. the wrapped text must fit the text block and the button
                            var layout = new FormattedText(tb.Text, CultureInfo.CurrentCulture, win.FlowDirection, typeface, tb.FontSize, Brushes.Black, scale) { MaxTextWidth = Math.Max(1, tb.ActualWidth) };
                            if (layout.Height > tb.ActualHeight + 1) problems.Add($"{tag}: {name} text needs {layout.Height:0.0} but the text block is {tb.ActualHeight:0.0} high");
                            if (tb.TextTrimming != TextTrimming.None) problems.Add($"{tag}: {name} still trims its text");
                            var rect = b.TransformToAncestor(win).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight));
                            var side = Field<ListBox>(win, "Navigation").TransformToAncestor(win).TransformBounds(new Rect(0, 0, 1, 1));
                            var tr = tb.TransformToAncestor(win).TransformBounds(new Rect(0, 0, tb.ActualWidth, tb.ActualHeight));
                            if (tr.Right > rect.Right + 0.5 || tr.Left < rect.Left - 0.5 || tr.Bottom > rect.Bottom + 0.5) problems.Add($"{tag}: {name} text block leaves its button");
                            // 3. pixels: some ink is inside the text area and none touches the button's outer right/left edge
                            var device = new Rect(rect.Left * scale, rect.Top * scale, rect.Width * scale, rect.Height * scale);
                            int inkRight = -1, inkLeft = int.MaxValue; bool any = false;
                            var bg = Px(shot, (int)(device.Left + device.Width / 2), (int)(device.Top + 1));
                            for (int y = (int)device.Top + 3; y < (int)device.Bottom - 3; y++)
                                for (int x = (int)device.Left + 2; x < (int)device.Right - 2; x++)
                                    if (Contrast(Px(shot, x, y), bg) >= 2.2) { any = true; inkRight = Math.Max(inkRight, x); inkLeft = Math.Min(inkLeft, x); }
                            if (!any) problems.Add($"{tag}: {name} shows no text pixels");
                            else if (inkRight >= device.Right - 4 || inkLeft <= device.Left + 3) problems.Add($"{tag}: ink of {name} touches the button edge (left {inkLeft - device.Left:0}, right {device.Right - inkRight:0} px)");
                        }
                        var nav = Field<ListBox>(win, "Navigation");
                        if (shots is { Length: > 0 } && theme is "DeepNavy" or "Light" && scale == 1.0 && (lang is "en" or "de" or "ru" or "ar") && width == 900.0) { Save(shot, Path.Combine(shots, $"sidebar-{theme}-{lang}.png")); Console.WriteLine($"SHOT: sidebar-{theme}-{lang}"); }
                        win.Close(); m.Dispose(); Pump(5);
                    }
        Loc.SetLanguage("en"); Use("Light");
        Check(problems.Count == 0, $"The sidebar buttons show their whole text in {renders} renders (10 languages x 4 themes x 3 DPI scales x 2 widths): " + string.Join(" | ", problems.Take(8)));
    }
}
