using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using NDM.Core;

namespace NDM.Tests;

/// <summary>A local server that imitates a file host with one-time links: the first request of a token gets the file, every later one a 32 KB web page with status 200.</summary>
internal sealed class MediaFireFixture : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly HashSet<string> used = new();
    public readonly byte[] File;
    public readonly byte[] Expired;
    public bool Reusable, NoRange, RequireCookie, HtmlAsBinary, RepairRedirect;
    public string CookieMark = "mfsession=ok"; // the cookie the file server wants
    public int RepairServed; // requests of the "repair your download" page
    public int FileServed, ExpiredServed;
    public string? LastCookie;
    public string BaseUrl { get; }
    public MediaFireFixture(int size = 6 * 1024 * 1024)
    {
        File = new byte[size]; new Random(7).NextBytes(File); File[0] = (byte)'P'; File[1] = (byte)'K';
        var page = new StringBuilder("<!DOCTYPE html><html><head><title>expired</title></head><body><h1>This link is not valid or has expired.</h1>");
        while (page.Length < 32768 - 14) page.Append("<!-- padding padding padding padding padding padding -->\n");
        page.Length = 32768 - 14; page.Append("</body></html>");
        Expired = Encoding.ASCII.GetBytes(page.ToString());
        int port; using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        BaseUrl = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(BaseUrl + "/"); listener.Start();
        _ = Task.Run(Loop);
    }
    private async Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }
    private void Serve(HttpListenerContext c)
    {
        try
        {
            var token = c.Request.Url!.AbsolutePath;
            LastCookie = c.Request.Headers["Cookie"];
            bool first; lock (used) { first = used.Add(token); }
            bool cookieOk = !RequireCookie || (LastCookie ?? "").Contains(CookieMark);
            if (token == "/download_repair.php") { Interlocked.Increment(ref RepairServed); c.Response.ContentType = "text/html; charset=utf-8"; c.Response.ContentLength64 = Expired.Length; c.Response.OutputStream.Write(Expired, 0, Expired.Length); c.Response.Close(); return; }
            if (RepairRedirect && !cookieOk) { Interlocked.Increment(ref ExpiredServed); c.Response.StatusCode = 302; c.Response.RedirectLocation = BaseUrl + "/download_repair.php?flag=2&dkey=abc"; c.Response.Close(); return; } // like the real host: no cookie, no file
            if ((first || Reusable) && cookieOk)
            {
                Interlocked.Increment(ref FileServed);
                var r = c.Response; r.AddHeader("Content-Disposition", "attachment; filename=\"archive.zip\""); r.AddHeader("ETag", "\"mf\""); r.AddHeader("Last-Modified", "Wed, 01 Jan 2020 00:00:00 GMT"); r.AddHeader("Date", "Thu, 01 Jan 2026 00:00:00 GMT");
                r.ContentType = "application/octet-stream";
                var body = HtmlAsBinary ? Expired : File;
                var range = c.Request.Headers["Range"];
                if (range is not null && !NoRange && System.Text.RegularExpressions.Regex.Match(range, @"^bytes=(\d+)-(\d*)$") is { Success: true } m)
                {
                    long start = long.Parse(m.Groups[1].Value), end = m.Groups[2].Value.Length == 0 ? body.Length - 1 : Math.Min(body.Length - 1, long.Parse(m.Groups[2].Value));
                    r.StatusCode = 206; r.AddHeader("Accept-Ranges", "bytes"); r.AddHeader("Content-Range", $"bytes {start}-{end}/{body.Length}");
                    r.ContentLength64 = end - start + 1; r.OutputStream.Write(body, (int)start, (int)(end - start + 1)); r.Close(); return;
                }
                if (!NoRange) r.AddHeader("Accept-Ranges", "bytes");
                r.ContentLength64 = body.Length; r.OutputStream.Write(body, 0, body.Length); r.Close(); return;
            }
            Interlocked.Increment(ref ExpiredServed);
            c.Response.ContentType = "text/html; charset=utf-8"; c.Response.ContentLength64 = Expired.Length;
            c.Response.OutputStream.Write(Expired, 0, Expired.Length); c.Response.Close();
        }
        catch { try { c.Response.Abort(); } catch { } }
    }
    public string Link(string name = "archive.zip") => $"{BaseUrl}/dl/{Guid.NewGuid():N}{Guid.NewGuid():N}/{name}";
    public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
}

/// <summary>1.5.4: a web page sent in place of the file is never saved as the file.</summary>
internal static class WrongContentTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Wait(Func<bool> condition, string message, int seconds = 20)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message); await Task.Delay(20); }
    }
    private static DownloadManager Open((string Download, string State) paths) =>
        new(paths.Download, paths.State, new DownloadOptions { MaxConcurrentDownloads = 2, MaxSegments = 4, MinimumSegmentSize = 65536, RetryCount = 2, RequestTimeout = TimeSpan.FromSeconds(10) });
    private static DownloadJob Job(DownloadManager m, string id) => m.Jobs.Single(j => j.Id == id);
    private static async Task<DownloadJob> Finish(DownloadManager m, string id)
    {
        await Wait(() => Job(m, id).Status is "Completed" or "Failed", "download did not finish");
        return Job(m, id);
    }
    private static IEnumerable<string> FilesIn((string Download, string State) paths) =>
        Directory.EnumerateFiles(paths.Download, "*", SearchOption.AllDirectories).Concat(Directory.EnumerateDirectories(paths.State, "*.parts"));
    private static void NothingKept((string Download, string State) paths, string why) => Check(!FilesIn(paths).Any(), why + ": " + string.Join(", ", FilesIn(paths)));
    private static readonly byte[] Html = Encoding.ASCII.GetBytes("<!DOCTYPE html><html><body>expired</body></html>");

    // ---------------------------------------------------------------- the rules (a) (b) (c) without a server
    public static Task Rules()
    {
        var zip = new ExpectedContent(108003328, "application/zip", "archive.zip");
        // (a) HTML content type while a file is expected
        Check(ContentGuard.CheckHeaders(zip, "text/html; charset=utf-8", 32768, null) is not null, "text/html must be refused");
        Check(ContentGuard.CheckHeaders(zip, "application/xhtml+xml", null, null) is not null, "application/xhtml+xml must be refused");
        Check(ContentGuard.CheckHeaders(zip, "TEXT/HTML", 108003328, null) is not null, "the content type is case insensitive");
        Check(ContentGuard.CheckHeaders(zip, "application/octet-stream", 108003328, null) is null, "the real file is accepted");
        foreach (var name in new[] { "page.html", "page.htm", "index.php", "x.asp", "x.aspx", "a.xhtml" })
            Check(ContentGuard.CheckHeaders(new ExpectedContent(null, null, name), "text/html", 5000, null) is null, "an expected web page type must be accepted: " + name);
        Check(ContentGuard.CheckHeaders(new ExpectedContent(null, "text/html", "x.bin"), "text/html", 5000, null) is null, "browser type html: accepted");
        Check(ContentGuard.CheckHeaders(zip, "text/html", 5000, "report.html") is null, "the server itself announces an html file name: accepted");
        // no hint at all: only the name extension / mime decides
        Check(ContentGuard.CheckHeaders(new ExpectedContent(null, null, "tool.zip"), "text/html", 32768, null) is not null, "name .zip and an html reply: refused without a size");
        Check(ContentGuard.CheckHeaders(new ExpectedContent(null, "application/zip", null), "text/html", 100, null) is not null, "browser type zip and an html reply: refused");
        Check(ContentGuard.CheckHeaders(new ExpectedContent(null, null, null), "text/html", 100, null) is null, "nothing known: no judgement");
        Check(ContentGuard.CheckHeaders(new ExpectedContent(null, null, "download"), "text/html", 100, null) is null, "a name without extension and no hint: no judgement");
        Check(ContentGuard.CheckHeaders(new ExpectedContent(1000, null, "download"), "text/html", 100, null) is not null, "a size hint alone means a file");
        // (b) declared length below 50 percent
        Check(ContentGuard.CheckHeaders(zip, "application/octet-stream", 32768, null) is { ReceivedBytes: 32768, ExpectedBytes: 108003328 }, "32 KB of 103 MB refused with both sizes");
        Check(ContentGuard.CheckHeaders(new ExpectedContent(1000, null, "a.bin"), "application/octet-stream", 499, null) is not null, "499 of 1000 refused");
        Check(ContentGuard.CheckHeaders(new ExpectedContent(1000, null, "a.bin"), "application/octet-stream", 500, null) is null, "exactly half is accepted");
        Check(ContentGuard.CheckHeaders(new ExpectedContent(1000, null, "a.bin"), "application/octet-stream", null, null) is null, "unknown length is accepted");
        Check(ContentGuard.CheckHeaders(new ExpectedContent(null, null, "a.bin"), "application/octet-stream", 3, null) is null, "no size hint: no size rule");
        // (c) first bytes
        foreach (var head in new[] { "<!DOCTYPE html>", "<!doctype HTML PUBLIC", "<html lang=en>", "  \r\n\t<HTML>", "\uFEFF<!DOCTYPE html>", "<head><title>", "<body>" })
            Check(ContentGuard.LooksLikeHtml(Encoding.UTF8.GetBytes(head)), "html start not recognised: " + head);
        foreach (var head in new[] { "PK\u0003\u0004", "MZ\u0090", "%PDF-1.7", "<?xml version=\"1.0\"?><svg>", "<rss version=\"2.0\">", "", "hello <html> inside" })
            Check(!ContentGuard.LooksLikeHtml(Encoding.Latin1.GetBytes(head)), "not html but recognised: " + head);
        Check(ContentGuard.CheckHead(zip, Html, null, null) is not null && ContentGuard.CheckHead(new ExpectedContent(null, null, "a.html"), Html, null, null) is null && ContentGuard.CheckHead(zip, Html, null, "x.html") is null, "head rule follows the expectation");
        // the message
        Check(ContentGuard.Message(32768, 108003328) == "The site sent a web page instead of the file (about 32 KB instead of 103 MB). The link may have expired or may only work in the browser.", "message: " + ContentGuard.Message(32768, 108003328));
        Check(!ContentGuard.Message(null, null).Contains("(") && ContentGuard.Message(2048, null).Contains("about 2 KB"), "message variants");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- engine: single-use link, the browser already used it
    public static async Task ExpiredPageIsRefused((string Download, string State) paths)
    {
        using var site = new MediaFireFixture(); using var manager = Open(paths);
        var link = site.Link();
        // the browser's own request used the token; NeoDM asks again and gets the 32 KB web page
        using (var browser = new HttpClient()) { var first = await browser.GetByteArrayAsync(link); Check(first.Length == site.File.Length, "the first request gets the file"); }
        var job = await manager.AddAsync(new(link, "archive.zip", site.BaseUrl + "/file/abc/archive.zip/file", null, false, null, site.File.Length, "application/zip"));
        var done = await Finish(manager, job.Id);
        Check(done.Status == "Failed" && done.ErrorCode == ContentGuard.WrongContent, $"must fail with wrong-content: {done.Status} {done.ErrorCode} {done.Error}");
        Check(done.ErrorReceivedBytes == 32768 && done.ErrorExpectedBytes == site.File.Length, $"both sizes recorded: {done.ErrorReceivedBytes} / {done.ErrorExpectedBytes}");
        Check(done.Error == "The site sent a web page instead of the file (about 32 KB instead of 6 MB). The link may have expired or may only work in the browser.", "message: " + done.Error);
        Check(site.ExpiredServed >= 1 && site.FileServed == 1, "the file was served only to the browser");
        NothingKept(paths, "no file may be published and no staged data may stay");
        Check(done.DownloadedBytes == 0 && done.TotalBytes == 0, $"nothing counted as downloaded: {done.DownloadedBytes}/{done.TotalBytes}");
        // the error code survives a restart; resuming clears it and the job fails the same way again (the link is still expired)
    }

    public static async Task WrongContentSurvivesRestart((string Download, string State) paths)
    {
        using var site = new MediaFireFixture(); string id;
        var link = site.Link();
        using (var browser = new HttpClient()) await browser.GetByteArrayAsync(link);
        using (var manager = Open(paths))
        {
            id = (await manager.AddAsync(new(link, "archive.zip", null, null, false, null, site.File.Length, "application/zip"))).Id;
            await Finish(manager, id);
        }
        var text = File.ReadAllText(Path.Combine(paths.State, "queue.json"));
        using var doc = JsonDocument.Parse(text);
        var saved = doc.RootElement[0];
        Check(saved.GetProperty("ErrorCode").GetString() == "wrong-content" && saved.GetProperty("Request").GetProperty("ExpectedSize").GetInt64() == site.File.Length && saved.GetProperty("Request").GetProperty("ExpectedMime").GetString() == "application/zip", "queue.json carries the code and the browser facts: " + text[..Math.Min(400, text.Length)]);
        using var again = Open(paths);
        var restored = Job(again, id);
        Check(restored.Status == "Failed" && restored.ErrorCode == "wrong-content" && restored.ErrorReceivedBytes == 32768, "restored with the code");
        again.Resume(id);
        var second = await Finish(again, id);
        Check(second.ErrorCode == "wrong-content", "still expired: fails the same way");
        NothingKept(paths, "still nothing published");
    }

    public static Task OldQueueWithoutNewFields((string Download, string State) paths)
    {
        Directory.CreateDirectory(paths.State); Directory.CreateDirectory(paths.Download);
        var id = Guid.NewGuid().ToString("N");
        // a queue.json written by 1.5.3: no ExpectedSize, no ExpectedMime, no ErrorCode
        File.WriteAllText(Path.Combine(paths.State, "queue.json"), "[{\"Id\":\"" + id + "\",\"Request\":{\"Url\":\"http://127.0.0.1:1/a.zip\",\"FileName\":\"a.zip\",\"Referer\":null,\"Cookie\":null,\"AutoName\":false,\"TargetDirectory\":null},\"CookieProtected\":null,\"FileName\":\"a.zip\",\"Status\":\"Failed\",\"Total\":0,\"ETag\":null,\"ResolvedUrl\":null,\"Modified\":null,\"Segments\":[],\"ScheduledAt\":null,\"Error\":\"old failure\",\"SpeedLimit\":null,\"FilePath\":null,\"AddedAt\":null}]");
        using var manager = Open(paths);
        var job = Job(manager, id);
        Check(job.Status == "Failed" && job.Error == "old failure" && job.ErrorCode is null && job.ErrorReceivedBytes is null, "an older queue.json restores unchanged");
        manager.Remove(id);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- engine: variants
    public static async Task WithoutSizeHintTheNameDecides((string Download, string State) paths)
    {
        using var site = new MediaFireFixture(); using var manager = Open(paths);
        var link = site.Link();
        using (var browser = new HttpClient()) await browser.GetByteArrayAsync(link);
        var job = await manager.AddAsync(new(link, "archive.zip", null, null, false, null)); // no size, no mime: only the name says it is a file
        var done = await Finish(manager, job.Id);
        Check(done.ErrorCode == ContentGuard.WrongContent && done.ErrorReceivedBytes == 32768 && done.ErrorExpectedBytes is null, $"name .zip and a web page: {done.Status} {done.ErrorCode} {done.Error}");
        Check(done.Error!.Contains("(about 32 KB)") && !done.Error.Contains("instead of 6"), "only the received size is named: " + done.Error);
        NothingKept(paths, "nothing published");
        // an open name (automatic) carries no information: a link that really is a page can still be saved
        var page = await manager.AddAsync(new(site.Link("download"), null, null, null, true, null));
        var saved = await Finish(manager, page.Id);
        Check(saved.Status == "Completed" && File.ReadAllBytes(saved.FilePath).Length == 32768, $"an automatic name accepts the page: {saved.Status} {saved.Error}");
    }

    public static async Task HtmlHiddenBehindBinaryHeaders((string Download, string State) paths)
    {
        using var site = new MediaFireFixture { HtmlAsBinary = true, Reusable = true }; using var manager = Open(paths);
        // content type octet-stream, length is the same as the page (32768 of 6 MB declared by the browser: rule b) and without a size: rule c
        var withSize = await manager.AddAsync(new(site.Link(), "archive.zip", null, null, false, null, site.File.Length, "application/zip"));
        var a = await Finish(manager, withSize.Id);
        Check(a.ErrorCode == "wrong-content", "size rule: " + a.Error);
        var noSize = await manager.AddAsync(new(site.Link(), "archive.zip", null, null, false, null, null, null));
        var b = await Finish(manager, noSize.Id);
        Check(b.ErrorCode == "wrong-content" && b.Error!.Contains("web page"), "first-bytes rule: " + b.Status + " " + b.Error);
        NothingKept(paths, "nothing published");
        // the same bytes are a perfectly good file when a web page is what was asked for
        var html = await manager.AddAsync(new(site.Link("page.html"), "page.html", null, null, false, null));
        var c = await Finish(manager, html.Id);
        Check(c.Status == "Completed", "a .html download may be a web page: " + c.Error);
    }

    public static async Task FileIsStillDownloaded((string Download, string State) paths)
    {
        using var site = new MediaFireFixture { Reusable = true }; using var manager = Open(paths);
        var job = await manager.AddAsync(new(site.Link(), "archive.zip", null, null, false, null, site.File.Length, "application/zip"));
        var done = await Finish(manager, job.Id);
        Check(done.Status == "Completed" && done.ErrorCode is null, "control: a real file with the browser's size completes: " + done.Error);
        Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(done.FilePath))) == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(site.File)), "the file is exact");
    }

    public static async Task NoRangeVariant((string Download, string State) paths)
    {
        using var site = new MediaFireFixture { NoRange = true, Reusable = true }; using var manager = Open(paths);
        var job = await manager.AddAsync(new(site.Link(), "archive.zip", null, null, false, null, site.File.Length, "application/zip"));
        var done = await Finish(manager, job.Id);
        Check(done.Status == "Completed" && done.ResumeSupported == false, "a server that ignores Range streams the whole file: " + done.Status + " " + done.Error);
        Check(File.ReadAllBytes(done.FilePath).SequenceEqual(site.File), "streamed file is exact");
        // and the same server with an expired link: the first reply is the page, never saved
        site.Reusable = false;
        var link = site.Link();
        using (var browser = new HttpClient()) await browser.GetByteArrayAsync(link);
        var bad = await manager.AddAsync(new(link, "archive.zip", null, null, false, null, site.File.Length, "application/zip"));
        var failed = await Finish(manager, bad.Id);
        Check(failed.ErrorCode == "wrong-content", "no-range + expired: " + failed.Error);
        Check(Directory.EnumerateFiles(paths.Download).Count() == 1, "only the good file exists");
    }

    public static async Task CookieVariant((string Download, string State) paths)
    {
        using var site = new MediaFireFixture { RequireCookie = true, Reusable = true }; using var manager = Open(paths);
        var link = site.Link();
        var without = await manager.AddAsync(new(link, "archive.zip", null, null, false, null, site.File.Length, "application/zip"));
        var a = await Finish(manager, without.Id);
        Check(a.ErrorCode == "wrong-content", "no cookie: the site answers with a page: " + a.Error);
        var with = await manager.AddAsync(new(link, "archive.zip", null, "mfsession=ok", false, null, site.File.Length, "application/zip"));
        var b = await Finish(manager, with.Id);
        Check(b.Status == "Completed" && File.ReadAllBytes(b.FilePath).SequenceEqual(site.File), "with the cookie the file arrives: " + b.Error);
    }

    // ---------------------------------------------------------------- 1.5.4 (amended): the cookie the browser sent, and a redirect to a page
    public static async Task BrowserCookieMakesTheFileArrive((string Download, string State) paths)
    {
        using var site = new MediaFireFixture { RequireCookie = true, Reusable = true, RepairRedirect = true, CookieMark = "g2cn=1" }; using var manager = Open(paths);
        var link = site.Link("test-payload.dat");
        var with = await manager.AddAsync(new(link, "test-payload.dat", null, "g2cn=1; ukey=abc", false, null, site.File.Length, "application/octet-stream"));
        var done = await Finish(manager, with.Id);
        Check(done.Status == "Completed" && done.TotalBytes == site.File.Length, "with the cookie the whole file arrives: " + done.Status + " " + done.Error);
        Check(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(done.FilePath)).SequenceEqual(System.Security.Cryptography.SHA256.HashData(site.File)), "6 MB, hash equal");
        Check(site.RepairServed == 0 && site.LastCookie!.Contains("g2cn=1"), "the page was never requested and the cookie reached the server");
        Check(!File.ReadAllText(Path.Combine(paths.State, "queue.json")).Contains("g2cn"), "the cookie is not stored in the clear after the job ends");
    }

    public static async Task WithoutCookieFailsEarlyOnTheRedirect((string Download, string State) paths)
    {
        using var site = new MediaFireFixture { RequireCookie = true, Reusable = true, RepairRedirect = true, CookieMark = "g2cn=1" }; string id;
        var link = site.Link("test-payload.dat");
        using (var manager = Open(paths))
        {
            id = (await manager.AddAsync(new(link, "test-payload.dat", null, null, false, null, site.File.Length, "application/octet-stream"))).Id;
            var failed = await Finish(manager, id);
            Check(failed.Status == "Failed" && failed.ErrorCode == "wrong-content" && failed.ErrorRedirected, $"redirect to a page: wrong-content with the redirect flag: {failed.Status} {failed.ErrorCode} {failed.Error}");
            Check(failed.Error == ContentGuard.RedirectMessage && failed.Error!.Contains("browser session") && failed.Error.Contains("Download in browser instead"), "specific message: " + failed.Error);
            Check(site.RepairServed == 0, "the repair page was not even requested (fail early, nothing followed silently)");
            NothingKept(paths, "nothing published and no parts kept");
        }
        using var again = Open(paths);
        var restored = Job(again, id);
        Check(restored.ErrorCode == "wrong-content" && restored.ErrorRedirected, "the redirect flag survives a restart");
        again.Resume(id); var second = await Finish(again, id);
        Check(second.ErrorRedirected && site.RepairServed == 0, "a retry without the cookie fails the same way");
    }

    public static async Task RedirectToPageWithoutExpectationIsFollowed((string Download, string State) paths)
    {
        // no size, no type, a name without extension: nothing says it must be a file, so the redirect is followed as before
        using var site = new MediaFireFixture { RequireCookie = true, Reusable = true, RepairRedirect = true, CookieMark = "g2cn=1" }; using var manager = Open(paths);
        var job = await manager.AddAsync(new(site.Link("download"), "download", null, null, true));
        var done = await Finish(manager, job.Id);
        Check(!done.ErrorRedirected && site.RepairServed >= 1, $"without any expectation the redirect is followed: {done.Status} {done.ErrorCode} served={site.RepairServed}");
    }

    public static Task RedirectRules()
    {
        var file = new ExpectedContent(6291456, "application/octet-stream", "test-payload.dat");
        Uri U(string s) => new(s);
        var dl = U("https://download2391.mediafire.com/tok/test-payload.dat");
        Check(ContentGuard.CheckRedirect(file, dl, dl, U("https://www.mediafire.com/download_repair.php?flag=2&dkey=x")) is { Redirected: true }, "same site .php page: refused");
        Check(ContentGuard.CheckRedirect(file, dl, dl, U("https://www.mediafire.com/error.html")) is { Redirected: true }, "same site .html page: refused");
        Check(ContentGuard.CheckRedirect(file, dl, dl, U("https://www.other-site.com/repair.php")) is null, "another site: not judged here");
        Check(ContentGuard.CheckRedirect(file, dl, dl, U("https://cdn.mediafire.com/tok2/test-payload.dat")) is null, "another file address: followed");
        Check(ContentGuard.CheckRedirect(file, dl, dl, U("https://www.mediafire.com/login")) is null, "a page without a page-like extension is left to the content check");
        Check(ContentGuard.CheckRedirect(new ExpectedContent(null, null, "page.html"), dl, dl, U("https://www.mediafire.com/x.php")) is null, "a web page was expected");
        Check(ContentGuard.CheckRedirect(new ExpectedContent(null, null, null), dl, dl, U("https://www.mediafire.com/x.php")) is null, "no expectation");
        Check(ContentGuard.CheckRedirect(file, U("https://www.mediafire.com/start.php"), U("https://www.mediafire.com/start.php"), U("https://www.mediafire.com/x.php")) is null, "the request itself was already a page address");
        Check(ContentGuard.SameSite(U("https://www.bbc.co.uk/"), U("https://news.bbc.co.uk/")) && !ContentGuard.SameSite(U("https://a.co.uk/"), U("https://b.co.uk/")), "registrable site rule");
        Check(!ContentGuard.SameSite(U("http://127.0.0.1:1/"), U("http://127.0.0.2:1/")) && ContentGuard.SameSite(U("http://127.0.0.1:1/"), U("http://127.0.0.1:2/")), "addresses must be equal");
        Check(!ContentGuard.SameSite(U("https://notmediafire.com/"), U("https://www.mediafire.com/")), "a longer name does not match");
        Check(ContentGuard.RedirectMessage == "The site redirected to a page instead of the file. It may need your browser session. Use Download in browser instead.", "message text");
        Check(!ContentGuard.RedirectMessage.Contains((char)0x2014), "no em dash");
        return Task.CompletedTask;
    }

    public static async Task RuleBStreaming((string Download, string State) paths)
    {
        // a reply that is far smaller than the browser's size, with an ordinary content type, is refused too
        using var site = new MediaFireFixture { Reusable = true, NoRange = true }; using var manager = Open(paths);
        var job = await manager.AddAsync(new(site.Link(), "archive.zip", null, null, false, null, site.File.Length * 4L, null));
        var done = await Finish(manager, job.Id);
        Check(done.ErrorCode == "wrong-content" && done.ErrorReceivedBytes == site.File.Length && done.ErrorExpectedBytes == site.File.Length * 4L, $"smaller than half: {done.Status} {done.Error}");
        NothingKept(paths, "nothing published");
        await Task.CompletedTask;
    }
}
