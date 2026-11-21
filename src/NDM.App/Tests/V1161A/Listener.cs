// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using NDM.App;
using NDM.Core;

internal static partial class V1161ATests
{
    private static readonly CaptchaPageText PageText = new("T", "H", "B", "D", "F");
    private const string Key = "6LfFIXTUREFIXTUREFIXTUREFIXTUREFIXTUREab";
    private static HostCaptcha Re(string key = Key) => new(HostCaptchaKind.ReCaptcha, key);

    private static (int Status, string Body) Http(HttpClient c, HttpMethod m, string url, string? host = null, string? body = null, string? origin = null)
    {
        using var r = new HttpRequestMessage(m, url); if (host is not null) r.Headers.Host = host; if (origin is not null) r.Headers.TryAddWithoutValidation("Origin", origin);
        if (body is not null) r.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        try { using var resp = c.SendAsync(r).GetAwaiter().GetResult(); return ((int)resp.StatusCode, resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()); }
        catch (HttpRequestException) { return (0, ""); }
    }
    private static bool Refused(int port) { try { using var t = new TcpClient(); t.Connect(IPAddress.Loopback, port); return false; } catch (SocketException) { return true; } }

    private static void Listener()
    {
        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        string tok = "tok-ABCDEFGHIJKLMNOP_1234567890";
        using (var l = new CaptchaListener(Re(), PageText))
        {
            Check(l.Url.StartsWith("http://127.0.0.1:") && l.Url.Contains("/c/" + l.Nonce), "listener address: loopback IP, random port, random nonce path");
            using (var raw = new LoopbackHttpServer(_ => LoopbackResponse.Text(200, "text/plain", "x"))) Check(raw.LocalEndPoint.Address.Equals(IPAddress.Loopback) && !raw.LocalEndPoint.Address.Equals(IPAddress.Any), "the server binds 127.0.0.1 only (not all interfaces)");
            var (s, page) = Http(c, HttpMethod.Get, l.Url);
            Check(s == 200 && page.Contains("data-sitekey=\"" + Key + "\"") && page.Contains("https://www.google.com/recaptcha/api.js") && page.Contains("g-recaptcha"), "page: the widget with the site key, script from Google's own domain");
            Check(!page.Contains(".click(") && !page.Contains("dispatchEvent") && !page.Contains("grecaptcha.execute"), "page: no scripted click, no synthetic event, no programmatic execute");
            Check(Http(c, HttpMethod.Get, l.Url + "x").Status == 404 && Http(c, HttpMethod.Get, l.Url.Replace("/c/", "/d/")).Status == 404 && Http(c, HttpMethod.Get, "http://127.0.0.1:" + l.Port + "/").Status == 404, "wrong nonce or path: 404");
            Check(Http(c, HttpMethod.Get, l.Url, host: "evil.example").Status == 400 && Http(c, HttpMethod.Get, l.Url, host: "localhost:" + l.Port).Status == 400, "wrong Host header: refused");
            Check(Http(c, HttpMethod.Post, l.Url + "/token", body: "token=" + tok, origin: "http://evil.example").Status == 403, "foreign Origin: refused");
            Check(Http(c, HttpMethod.Post, l.Url + "x/token", body: "token=" + tok).Status == 404 && Http(c, HttpMethod.Post, l.Url, body: "token=" + tok).Status == 404, "token post to a wrong path: refused");
            Check(Http(c, HttpMethod.Post, l.Url + "/token", body: "x=" + tok).Status == 400 && Http(c, HttpMethod.Post, l.Url + "/token", body: "token=a b<script>").Status == 400, "malformed token body: refused");
            Check(!l.Token.IsCompleted, "still no token after every refused request");
            Check(Http(c, HttpMethod.Post, l.Url + "/token", body: "token=" + tok).Status == 200 && l.Token.Wait(2000) && l.Token.Result == tok && l.Used, "the one valid post: token delivered");
            Check(Http(c, HttpMethod.Post, l.Url + "/token", body: "token=" + tok + "2").Status == 410 && l.Token.Result == tok, "second post refused (single use)");
            Check(Http(c, HttpMethod.Get, l.Url).Status == 410, "page after use: gone");
            int port = l.Port; Thread.Sleep(2000); Check(Refused(port), "the listener closes itself after the token");
        }
        using (var l = new CaptchaListener(Re(), PageText, TimeSpan.FromMilliseconds(300)))
        {
            int port = l.Port; Check(l.Token.Wait(3000) && l.Token.Result is null, "expiry: no token, result is null"); Thread.Sleep(200); Check(Refused(port), "expiry: the listener is closed");
        }
        bool bad = false; try { new CaptchaListener(Re("short"), PageText).Dispose(); } catch (ArgumentException) { bad = true; }
        Check(bad, "a malformed site key is never put into a page");
        bool bad2 = false; try { new CaptchaListener(new HostCaptcha(HostCaptchaKind.Image, null), PageText).Dispose(); } catch (ArgumentException) { bad2 = true; }
        Check(bad2, "an image captcha cannot start the browser step");
        bool bad3 = false; try { BrowserCaptchaStep.OpenInDefaultBrowser("file:///c:/windows/notepad.exe"); } catch (ArgumentException) { bad3 = true; }
        Check(bad3, "the launcher opens only the local page");
        using (var l = new CaptchaListener(new HostCaptcha(HostCaptchaKind.Turnstile, "0x4AAAAAAAfixturefixture1"), PageText)) { var pg = Http(c, HttpMethod.Get, l.Url).Body; Check(pg.Contains("cf-turnstile") && pg.Contains("challenges.cloudflare.com/turnstile"), "Turnstile page uses Cloudflare's own script"); }
        using (var l = new CaptchaListener(new HostCaptcha(HostCaptchaKind.HCaptcha, "10000000-ffff-ffff-ffff-000000000001"), PageText)) { var pg = Http(c, HttpMethod.Get, l.Url).Body; Check(pg.Contains("h-captcha") && pg.Contains("js.hcaptcha.com"), "hCaptcha page uses hCaptcha's own script"); }
    }

    private static void BrowserStep()
    {
        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // the test harness plays the person: the launcher "opens" the page, loads it and posts the token a click would give
        var opened = new List<string>(); string mode = "person";
        var step = new BrowserCaptchaStep { Expiry = TimeSpan.FromSeconds(20) };
        var waits = new List<bool>(); step.Waiting = w => waits.Add(w);
        step.Launcher = url =>
        {
            opened.Add(url); if (mode != "person") return;
            Task.Run(() => { Http(c, HttpMethod.Get, url); Http(c, HttpMethod.Post, url + "/token", body: "token=human-" + Guid.NewGuid().ToString("N")); });
        };
        var page = new Uri("https://host.example/file"); var tokens = new List<string>();
        for (int i = 0; i < 3; i++) { var o = Task.Run(() => step.RunAsync(Re(), page, CancellationToken.None, () => false)).GetAwaiter().GetResult(); Check(o.Token is { } t && t.StartsWith("human-"), $"browser step {i + 1}: token from the person's click"); tokens.Add(o.Token!); }
        Check(opened.Distinct().Count() == 3 && tokens.Distinct().Count() == 3, "each challenge: its own page address and its own token");
        Check(waits.SequenceEqual(new[] { true, false, true, false, true, false }), "NeoDM comes forward when the step waits and again when it ends");

        mode = "nobody"; opened.Clear();
        using var cts = new CancellationTokenSource();
        var pending = Task.Run(() => step.RunAsync(Re(), page, cts.Token, () => false)); Thread.Sleep(500);
        Check(!pending.IsCompleted && step.CurrentUrl is not null, "no click yet: the step keeps waiting");
        Check(step.OpenAgain() && opened.Count == 2 && opened[0] == opened[1], "'Open the page again' opens the same one-time page");
        cts.Cancel(); bool cancelled = false; try { pending.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && step.CurrentUrl is null && !step.OpenAgain(), "cancel ends the step and closes the page");
        bool skipNow = false; var p2 = Task.Run(() => step.RunAsync(Re(), page, CancellationToken.None, () => skipNow)); Thread.Sleep(400); skipNow = true;
        Check(p2.Wait(3000) && p2.Result.Skipped, "skip ends the step");
        var shortStep = new BrowserCaptchaStep { Expiry = TimeSpan.FromMilliseconds(400), Launcher = _ => { } };
        Check(Task.Run(() => shortStep.RunAsync(Re(), page, CancellationToken.None, () => false)).GetAwaiter().GetResult().TimedOut, "five-minute expiry (short here): timed out, nothing posted");
        var failing = new BrowserCaptchaStep { Expiry = TimeSpan.FromMilliseconds(400), Launcher = _ => throw new InvalidOperationException() };
        Check(Task.Run(() => failing.RunAsync(Re(), page, CancellationToken.None, () => false)).GetAwaiter().GetResult().TimedOut, "browser could not be launched: the step still waits and then times out cleanly");
    }

    private static void EndToEnd()
    {
        // the engine with the REAL browser step against the fixture host; the harness posts the token to the listener (the stand-in for a human click)
        using var host = new XfsHost { Countdown = 2 }; using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var step = new BrowserCaptchaStep { Expiry = TimeSpan.FromSeconds(30) }; var urls = new List<string>();
        step.Launcher = url => { urls.Add(url); Task.Run(() => { Http(c, HttpMethod.Get, url); Http(c, HttpMethod.Post, url + "/token", body: "token=" + host.IssueToken()); }); };
        var clock = new FakeClock(); var engine = Engine(clock, step);
        var results = new List<FormFlowResult>();
        foreach (var n in new[] { "1", "2", "3", "4" }) results.Add(Run(engine, Item(host.FileUrl("e2e" + n))));
        Check(results.All(r => r.Result.State == PageLinkState.Resolved), "4 links through the real browser step: all resolved");
        Check(urls.Count == 4 && urls.Distinct().Count() == 4, "4 links: 4 separate challenges (4 pages, never one token reused)");
        Check(clock.Seconds >= 8, "4 links: every site timer waited (>= 2 s each)");
    }
}
