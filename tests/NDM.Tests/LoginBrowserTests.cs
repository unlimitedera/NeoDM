using System.Diagnostics;
using NDM.Core;

namespace NDM.Tests;

/// <summary>1.12.0 login browser, the parts that need no browser window: where it opens, which job a capture belongs to, which cookies may travel, size bounds, and the same job continuing after a sign-in.</summary>
internal static class LoginBrowserTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    public static Task Rules()
    {
        // where it opens: the page the download came from, else the origin of the file address
        Check(LoginBrowserLogic.StartUrl("https://site.example/page?x=1", "https://cdn.example/f.zip") == "https://site.example/page?x=1", "start: the referer page");
        Check(LoginBrowserLogic.StartUrl(null, "https://cdn.example/a/b/f.zip?token=1") == "https://cdn.example/", "start: the origin of the file address, no path or token");
        Check(LoginBrowserLogic.StartUrl("javascript:alert(1)", "http://files.example:8080/x") == "http://files.example:8080/", "start: a non-web referer is ignored");
        Check(LoginBrowserLogic.StartUrl("", "ftp://x.example/f") is null && LoginBrowserLogic.StartUrl(null, null) is null, "start: nothing usable");
        Check(LoginBrowserLogic.StartUrl("https://user:pw@site.example/", null) is null, "start: addresses with user information are refused");
        Check(LoginBrowserLogic.DisplayHost("https://User.Example.com:8443/a/b?q=1#x") == "user.example.com" && LoginBrowserLogic.DisplayHost("file:///c:/x") == "", "address bar shows the host only");
        // sites and matching
        Check(LoginBrowserLogic.SiteOf("download1.files.example.com") == "example.com" && LoginBrowserLogic.SiteOf("a.b.example.co.uk") == "example.co.uk" && LoginBrowserLogic.SiteOf("127.0.0.1") == "127.0.0.1" && LoginBrowserLogic.SiteOf("localhost") == "localhost", "registrable site");
        var grab = new BrowserGrab("https://dl.example.com/get/42", "report.pdf", "https://www.example.com/page", null, null, null, null);
        Check(LoginBrowserLogic.Matches("https://www.example.com/old", null, "x.bin", grab), "matches: same site as the job address");
        Check(LoginBrowserLogic.Matches("https://other.test/a", "https://app.example.com/", "x.bin", grab), "matches: same site as the job's page");
        Check(LoginBrowserLogic.Matches("https://other.test/a", null, "REPORT.pdf", grab), "matches: same file name");
        Check(!LoginBrowserLogic.Matches("https://other.test/a", "https://third.test/", "x.bin", grab), "no match: different site and name (a new job is added)");
        Check(!LoginBrowserLogic.Matches("https://notexample.com/a", null, "x.bin", grab), "no match: lookalike site");
        // runtime
        Check(LoginBrowserLogic.RuntimeFrom(() => "157.0.4341.42") == LoginBrowserRuntime.Available && LoginBrowserLogic.RuntimeFrom(() => null) == LoginBrowserRuntime.Missing
            && LoginBrowserLogic.RuntimeFrom(() => " ") == LoginBrowserRuntime.Missing && LoginBrowserLogic.RuntimeFrom(() => throw new InvalidOperationException()) == LoginBrowserRuntime.Missing, "runtime state");
        return Task.CompletedTask;
    }

    public static Task Cookies()
    {
        BrowserCookie C(string n, string v, string d, string p = "/", bool secure = false, DateTimeOffset? exp = null) => new(n, v, d, p, secure, exp);
        var target = new Uri("https://dl.example.com/files/a/b.zip");
        string Header(params BrowserCookie[] c) => LoginBrowserLogic.CookieHeader(c, target, Now);
        Check(Header(C("sid", "1", ".example.com"), C("host", "2", "dl.example.com"), C("other", "3", "evil.test"), C("lookalike", "4", ".notexample.com"), C("sub", "5", "www.example.com")) == "host=2; sid=1", "domain scope: parent-domain and host-only cookies for this host only");
        Check(Header(C("a", "1", ".example.com", "/files/"), C("b", "2", ".example.com", "/files/a"), C("c", "3", ".example.com", "/other"), C("d", "4", ".example.com", "/fil")) == "b=2; a=1", "path scope, longest path first, a partial segment does not match");
        Check(Header(C("s", "1", ".example.com", "/", true)) == "s=1" && LoginBrowserLogic.CookieHeader(new[] { C("s", "1", ".example.com", "/", true) }, new Uri("http://dl.example.com/x"), Now) == "", "secure cookies go over https only");
        Check(Header(C("old", "1", ".example.com", "/", false, Now.AddMinutes(-1)), C("new", "2", ".example.com", "/", false, Now.AddDays(1)), C("session", "3", ".example.com")) == "new=2; session=3", "expired cookies are skipped");
        Check(Header(C("bad name", "1", ".example.com"), C("x", "a;b", ".example.com"), C("y", "line\r\nbreak", ".example.com"), C("", "v", ".example.com"), C("ok", "fine", ".example.com")) == "ok=fine", "unusable names and values are skipped");
        var many = Enumerable.Range(0, 400).Select(i => C("n" + i.ToString("D3"), new string('v', 60), ".example.com")).ToArray();
        var capped = Header(many);
        Check(capped.Length <= LoginBrowserLogic.MaxCookieHeader && capped.Split("; ").Length <= LoginBrowserLogic.MaxCookieCount && capped.StartsWith("n000="), "bounded: at most 100 cookies and 8192 characters");
        Check(LoginBrowserLogic.CookieHeader(new[] { C("a", "1", ".example.com") }, new Uri("ftp://dl.example.com/x"), Now) == "" && Header() == "", "no cookies for a non-web address or an empty list");
        return Task.CompletedTask;
    }

    public static Task Bounds()
    {
        var ok = LoginBrowserLogic.Clean(new BrowserGrab("https://h.example/f.bin?t=1", "a/b\\c:d.bin", "https://h.example/page", "sid=1", "Mozilla/5.0 (X) Chrome/9", "*/*", "en-US,en;q=0.9"));
        Check(ok is not null && ok.Url == "https://h.example/f.bin?t=1" && ok.Cookie == "sid=1" && ok.FileName is { } n && !n.Contains('/') && !n.Contains('\\') && !n.Contains(':'), "clean: a normal capture passes, the file name is made safe");
        Check(LoginBrowserLogic.Clean(new BrowserGrab("file:///c:/x", null, null, null, null, null, null)) is null && LoginBrowserLogic.Clean(new BrowserGrab("https://h.example/" + new string('a', 9000), null, null, null, null, null, null)) is null, "clean: only http(s) addresses within the length limit");
        var c = LoginBrowserLogic.Clean(new BrowserGrab("https://h.example/f", null, "ftp://x/", new string('c', 9000), "bad\r\nagent", new string('a', 600), "x\ny"))!;
        Check(c.Cookie is null && c.UserAgent is null && c.Accept is null && c.AcceptLanguage is null && c.Referer is null, "clean: oversized or control-character values are dropped, not repaired");
        var ctrl = LoginBrowserLogic.Clean(new BrowserGrab("https://h.example/f", null, null, "a=1\r\nX: y", null, null, null))!;
        Check(ctrl.Cookie is null, "clean: a cookie with a line break is dropped");
        return Task.CompletedTask;
    }

    /// <summary>The browser component is replaced by its output: a cookie list as the browser would report it. A server that needs the sign-in cookie refuses the job; after the capture the SAME job (same id,
    /// no second job) continues with the cookie and finishes with the exact bytes. The cookie never reaches another host.</summary>
    public static async Task SameJobContinues((string Download, string State) paths)
    {
        using var server = new CompatServer { RequireCookie = "sid=abc" };
        using var m = new DownloadManager(paths.Download, paths.State, new DownloadOptions { MaxSegments = 4, MinimumSegmentSize = 65536, RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(10), LadderPause = TimeSpan.FromMilliseconds(20) });
        async Task<DownloadJob> Finish(string id)
        {
            var clock = Stopwatch.StartNew();
            while (m.Jobs.Single(j => j.Id == id).Status is not ("Completed" or "Failed")) { if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(); await Task.Delay(20); }
            return m.Jobs.Single(j => j.Id == id);
        }
        var added = await m.AddAsync(new(server.BaseUrl + "/file", "signed.bin", Referer: server.BaseUrl + "/page"));
        var failed = await Finish(added.Id);
        Check(failed.Status == "Failed" && failed.ErrorStatus == 403, "the download fails with 403 without the sign-in: " + failed.Status);
        // what the browser component reports for the download address
        var uri = new Uri(server.BaseUrl + "/file");
        var header = LoginBrowserLogic.CookieHeader(new[] { new BrowserCookie("sid", "abc", "127.0.0.1", "/", false, null), new BrowserCookie("leak", "no", "other.test", "/", false, null) }, uri, DateTimeOffset.UtcNow);
        Check(header == "sid=abc", "only the host's cookie: " + header);
        var grab = new BrowserGrab(server.BaseUrl + "/file", "signed.bin", server.BaseUrl + "/page", header, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Edg/157.0", null, null);
        Check(LoginBrowserLogic.Matches(failed.Url, failed.Referer, failed.FileName, grab), "the capture belongs to the failed job");
        server.Cookies.Clear();
        m.RefreshFromBrowser(added.Id, grab); m.Resume(added.Id);
        var done = await Finish(added.Id);
        Check(done.Status == "Completed" && done.Id == added.Id && m.Jobs.Count == 1, "the same job finished, no second job: " + done.Status + " " + done.Error);
        Check(CompatServer.Hash(File.ReadAllBytes(done.FilePath)) == CompatServer.Hash(server.Payload), "exact bytes");
        Check(server.Cookies.Count > 0 && server.Cookies.All(c => c.Contains("sid=abc")), "every request carried the refreshed cookie");
        Check(server.Log.Any(s => s.UserAgent?.Contains("Edg/157") == true), "the browser's User-Agent was used");
        // a completed job cannot be refreshed; a bad capture is refused
        bool refused = false; try { m.RefreshFromBrowser(added.Id, grab); } catch (InvalidOperationException) { refused = true; }
        Check(refused, "a completed download is not refreshed");
        bool bad = false; try { m.RefreshFromBrowser(added.Id, new BrowserGrab("file:///x", null, null, null, null, null, null)); } catch (ArgumentException) { bad = true; }
        Check(bad, "a capture that is not a web address is refused");
    }
}
