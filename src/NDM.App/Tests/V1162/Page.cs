// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using NDM.Core;
using NDM.Core.Captcha;

internal static partial class V1162Tests
{
    private static readonly CaptchaPageText PageText = new("T", "H", "B", "D", "F");
    private const string Key = "0x4AAAAAAAfixtureturnstilekey01";

    private static (int Status, string Body) Http(HttpClient c, HttpMethod m, string url, string? host = null, string? body = null, string? origin = null)
    {
        using var r = new HttpRequestMessage(m, url); if (host is not null) r.Headers.Host = host; if (origin is not null) r.Headers.TryAddWithoutValidation("Origin", origin);
        if (body is not null) r.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        try { using var resp = c.SendAsync(r).GetAwaiter().GetResult(); return ((int)resp.StatusCode, resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()); }
        catch (HttpRequestException) { return (0, ""); }
    }
    private static bool Refused(int port) { try { using var t = new TcpClient(); t.Connect(IPAddress.Loopback, port); return false; } catch (SocketException) { return true; } }
    private static string ReportBody(int x = 100, int y = 200, int w = 300, int h = 65, int vw = 1024, int vh = 768, string dpr = "1", int ew = 1040, int eh = 868)
        => $"do=loaded&x={x}&y={y}&w={w}&h={h}&vw={vw}&vh={vh}&dpr={dpr}&eleft=0&etop=0&ew={ew}&eh={eh}";

    /// <summary>The page, the report it posts, the guards that stay on, and the token it finally sends.</summary>
    private static void Page()
    {
        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var l = new CaptchaListener(new HostCaptcha(HostCaptchaKind.Turnstile, Key), PageText);
        var (status, page) = Http(c, HttpMethod.Get, l.Url);
        Check(status == 200 && page.Contains("data-sitekey=\"" + Key + "\"") && page.Contains("cf-turnstile"), "the page draws the widget with the key");
        Check(page.Contains("do=loaded&") && page.Contains("'/r'") && page.Contains("devicePixelRatio"), "the page carries the measuring script");
        Check(!page.Contains(".click("), "the page never presses the widget itself");
        Check(Http(c, HttpMethod.Get, l.Url, host: "evil.example").Status == 400, "a wrong Host is still refused");
        Check(Http(c, HttpMethod.Post, l.Url + "/r", body: ReportBody(), origin: "http://evil.example").Status == 403, "a foreign Origin on the report is refused");
        Check(Http(c, HttpMethod.Post, l.Url + "/r", body: "do=loaded&x=1").Status == 400, "a report with too few numbers is refused");
        Check(Http(c, HttpMethod.Post, l.Url + "/r", body: "do=other&x=1").Status == 400, "an unknown do is refused");
        Check(!l.Placement.IsCompleted, "every refused report leaves the page without a measurement");
        var origin = "http://127.0.0.1:" + l.Port;
        Check(Http(c, HttpMethod.Post, l.Url + "/r", body: ReportBody(), origin: origin).Status == 200, "a full report from the page is accepted");
        Check(l.Placement.Wait(2000) && l.Placement.Result is { } first && first.Widget.X == 100 && l.Reports == 1, "the first measurement is handed over");
        var seen = new List<int>();
        l.Placed += r => seen.Add((int)(r.Widget.X + r.Widget.W / 2));
        Check(Http(c, HttpMethod.Post, l.Url + "/r", body: ReportBody(x: 140), origin: origin).Status == 200, "a later report (the window moved) is accepted");
        Check(l.Reports == 2 && l.Latest!.Widget.X == 140 && l.Placement.Result!.Widget.X == 100 && seen.SequenceEqual(new[] { 290 }), "the newest measurement replaces the first, the first stays for the waiter");
        Check(Http(c, HttpMethod.Post, l.Url + "/r", body: "do=token&token=abc-123_XYZ-abcdef", origin: origin).Status == 200 && l.Token.Wait(2000) && l.Token.Result == "abc-123_XYZ-abcdef" && l.Used, "the token post through the report path delivers");
        Check(Http(c, HttpMethod.Post, l.Url + "/r", body: "do=token&token=abc-123_XYZ-abcdef").Status == 410, "a second token post is refused");
        int port = l.Port; Thread.Sleep(2000); Check(Refused(port), "the listener closes itself after the token");
        using var old = new CaptchaListener(new HostCaptcha(HostCaptchaKind.Turnstile, Key), PageText);
        Check(Http(c, HttpMethod.Post, old.Url + "/token", body: "token=abcdefghij-1234").Status == 200 && old.Token.Wait(2000), "the older token path still works");
        using var expiring = new CaptchaListener(new HostCaptcha(HostCaptchaKind.Turnstile, Key), PageText, TimeSpan.FromMilliseconds(300));
        Check(expiring.Placement.Wait(3000) && expiring.Placement.Result is null, "expiry: the measurement waiter is released with null");
        using var withBase = new CaptchaListener(new CaptchaPageSpec(HostCaptchaKind.Turnstile, Key, "https://host.example/d/abc"), PageText);
        Check(Http(c, HttpMethod.Get, withBase.Url).Body.Contains("<base href=\"https://host.example/d/abc\">"), "an optional base address is written into the page");
        Throws<ArgumentException>(() => new CaptchaListener(new CaptchaPageSpec(HostCaptchaKind.Turnstile, null), PageText).Dispose(), "loop mode needs a page source when there is no key");
        Throws<ArgumentException>(() => new CaptchaListener(new CaptchaPageSpec(HostCaptchaKind.Turnstile, "short"), PageText).Dispose(), "a malformed key never reaches a page");
    }

    private sealed class Source : IPageSource
    {
        public readonly List<string> Asked = new();
        public byte[] Page = "x"u8.ToArray();
        public string? Fail;
        public Task<ProxyFetch?> GetAsync(Uri url, CancellationToken token)
        {
            Asked.Add(url.AbsoluteUri);
            if (Fail is not null && url.AbsoluteUri.Contains(Fail, StringComparison.Ordinal)) return Task.FromResult<ProxyFetch?>(null);
            return Task.FromResult<ProxyFetch?>(new ProxyFetch(Page, "text/html; charset=utf-8", url.AbsoluteUri));
        }
    }

    /// <summary>The browser loop: the site's page comes back through the server, its assets too, and a refused address is reported.</summary>
    private static void Assets()
    {
        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var source = new Source();
        source.Page = ("<!DOCTYPE html><html><head><script src=\"/app.js\" integrity=\"x\"></script></head><body><div class=\"cf-turnstile\" data-sitekey=\"" + Key + "\"></div></body></html>").Select(ch => (byte)ch).ToArray();
        using var l = new CaptchaListener(new CaptchaPageSpec(HostCaptchaKind.Turnstile, Key, null, new LoopbackPageProxy(new Uri("https://host.example/d/abc"), source)), PageText);
        var (status, page) = Http(c, HttpMethod.Get, l.Url);
        Check(status == 200 && l.Loop, "the site's page is served under the loopback address");
        Check(page.Contains("<base href=\"https://host.example/d/abc\">") && page.Contains("/c/" + l.Nonce + "/p?u="), "its addresses point back here and a base tag is added");
        Check(page.Contains("/r'") && page.Contains("id=\"neodm\""), "the measuring bar and script are added to the site's page");
        Check(source.Asked.SequenceEqual(new[] { "https://host.example/d/abc" }), "the site's page is fetched exactly once, with the job identity");
        var asset = Http(c, HttpMethod.Get, l.Url + "/p?u=" + Uri.EscapeDataString("https://host.example/app.js"));
        Check(asset.Status == 200 && asset.Body.Length > 0 && source.Asked.Contains("https://host.example/app.js"), "an asset comes back through the server");
        Check(Http(c, HttpMethod.Get, l.Url + "/p?u=" + Uri.EscapeDataString("ftp://host.example/a")).Status == 400, "a non web asset address is refused");
        Check(Http(c, HttpMethod.Get, l.Url + "/p").Status == 400, "an asset request with no address is refused");
        source.Fail = "missing.js";
        Check(Http(c, HttpMethod.Get, l.Url + "/p?u=" + Uri.EscapeDataString("https://host.example/missing.js")).Status == 502, "an asset the site refused is reported as unavailable");
        var dead = new Source { Fail = "host.example" };
        using var fallback = new CaptchaListener(new CaptchaPageSpec(HostCaptchaKind.Turnstile, Key, null, new LoopbackPageProxy(new Uri("https://host.example/d/abc"), dead)), PageText);
        var (deadStatus, deadPage) = Http(c, HttpMethod.Get, fallback.Url);
        Check(deadStatus == 502 && deadPage.Contains("data-sitekey=\"" + Key + "\""), "when the site will not answer, the bare widget page is served instead");
        Check(Http(c, HttpMethod.Get, fallback.Url + "/p?u=" + Uri.EscapeDataString("https://host.example/a.js")).Status == 502, "the fallback page still forwards assets to the site");
    }
}
