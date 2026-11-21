// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using NDM.Core;

namespace NDM.App;

// 1.16.1: the form-based host resolver. Plain web requests (no browser page), one link at a time: GET the page with browser-like headers and a cookie jar of its own,
// read the form, wait the site's own timer, let the PERSON do a captcha in the real browser (see BrowserCaptchaStep), POST the form, find the direct link, check it.
// Rules that never change: no captcha is solved or clicked by NeoDM, no timer is shortened, a token is used for one post only, only the pasted link's own site is followed
// (the final direct link may be elsewhere), and no cookie, token or form value is logged.

/// <summary>What the person did in the browser step.</summary>
public sealed record CaptchaOutcome(string? Token, bool Skipped, bool TimedOut)
{
    public static CaptchaOutcome Got(string token) => new(token, false, false);
    public static readonly CaptchaOutcome Skip = new(null, true, false), Timeout = new(null, false, true);
}

/// <summary>The human step of one challenge. The real one opens the user's default browser; tests use a stand-in that posts a token the way a person's click would.</summary>
public interface IHumanCaptchaStep
{
    Task<CaptchaOutcome> RunAsync(HostCaptcha captcha, Uri page, CancellationToken token, Func<bool> skip);
}

/// <summary>Result of the form flow: a finished result, or "not recognised" (the embedded browser takes over).</summary>
public sealed record FormFlowResult(ResolveResult Result, bool Unrecognised, string? Detail = null)
{
    public static FormFlowResult Fallback(string detail) => new(new(PageLinkState.Failed, null, null), true, detail);
}

public sealed class HostFormEngine
{
    public TimeSpan Budget { get; init; } = TimeSpan.FromMinutes(3);
    public int MaxSteps { get; init; } = 4;
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;
    /// <summary>Waits one tick of the site's timer. Tests replace it with a fake clock; the real one is a real second.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (t, c) => Task.Delay(t, c);
    public Func<ProxyOptions?> Proxy { get; init; } = () => null;
    public string UserAgent { get; init; } = RequestIdentity.BrowserUserAgent();
    /// <summary>Builds the HTTP handler for one link (own cookie jar). Tests may replace it.</summary>
    public Func<CookieContainer, HttpMessageHandler>? HandlerFactory { get; init; }
    public IHumanCaptchaStep? Captcha { get; init; }
    /// <summary>The Referer for the first request (retry = false) and for the second try after a refusal (retry = true). Data first, the host's own origin after.</summary>
    public Func<Uri, bool, string> RefererPolicy { get; init; } = HostQuirks.RefererFor;
    /// <summary>The seconds the engine waited for the site's timer in the last link (tests assert it is at least the stated value).</summary>
    public int LastWaitedSeconds { get; private set; }
    /// <summary>Diagnostics only (the real-host check): called with the step number and the parsed page before the timer wait. Never given the page text, cookies or tokens.</summary>
    public Action<int, HostPage>? Inspect { get; init; }
    /// <summary>Diagnostics only: the text of each POST answer (the real-host check writes it to a scratch file to learn a new page layout; never logged).</summary>
    public Action<int, int, string>? InspectReply { get; init; }
    /// <summary>1.16.2: true when the form resolver's own site check ran by itself (the browser step presses) and still brought no answer, or the key refuses the loopback address. Path A may then take over.</summary>
    public bool LastCaptchaFailed { get; set; }

    private HttpMessageHandler MakeHandler(CookieContainer jar)
    {
        if (HandlerFactory is not null) return HandlerFactory(jar);
        var h = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = jar, ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli };
        ProxyHttp.Apply(h, Proxy()); return h;
    }

    private sealed class Reply { public Uri Url = null!; public int Status; public string ContentType = ""; public string Text = ""; public HttpResponseHeaders? Headers; public string? Disposition; public bool IsFile; }

    public async Task<FormFlowResult> ResolveAsync(PageLinkItem item, Func<bool> skip, CancellationToken token)
    {
        item.State = PageLinkState.Loading; item.Countdown = null; item.DetailKey = null; item.Challenge = null; LastWaitedSeconds = 0; LastCaptchaFailed = false;
        if (!ResolverSafety.IsWeb(item.Url) || !Uri.TryCreate(item.Url, UriKind.Absolute, out var start)) return Done(item, PageLinkState.Failed, "pagelink.fail.address");
        var started = Now(); var deadline = started + Budget;
        var jar = new CookieContainer(); using var client = new HttpClient(MakeHandler(jar), true) { Timeout = TimeSpan.FromSeconds(30) };
        bool retried = false; string referer = RefererPolicy(start, false);
        try
        {
            restart:
            var page = start; Reply? reply;
            reply = await GetAsync(client, start, referer, start, token);
            if (reply is null) return Done(item, PageLinkState.Failed, "pagelink.fail.load");
            if (reply.IsFile) return await FinishAsync(client, jar, item, new[] { reply.Url.AbsoluteUri }, reply.Url, referer, start, token);
            var parsed = HostFormParser.Parse(reply.Text, reply.Url.AbsoluteUri); page = reply.Url;
            if (parsed.ReferrerRefused && !retried) { retried = true; referer = RefererPolicy(start, true); goto restart; }
            if (parsed.ReferrerRefused) return Done(item, PageLinkState.Failed, "pagelink.fail.refused");
            if (!parsed.Recognised) return FormFlowResult.Fallback("no form");
            for (int step = 1; step <= MaxSteps; step++)
            {
                token.ThrowIfCancellationRequested();
                if (skip()) { item.State = PageLinkState.Skipped; return new(new(PageLinkState.Skipped, null), false); }
                var form = parsed.Form!; Inspect?.Invoke(step, parsed);
                // the site's own timer: at least the stated seconds (and, for a first step, the host's own known minimum); never less
                int wait = parsed.CountdownSeconds ?? 0;
                if (step == 1 && form.SubmitDisabled && HostQuirks.For(page.Host) is { } q) wait = Math.Max(wait, q.MinWaitSeconds);
                if (wait > 0) { deadline += TimeSpan.FromSeconds(wait); if (!await WaitTimerAsync(item, wait, skip, token)) { item.State = PageLinkState.Skipped; return new(new(PageLinkState.Skipped, null), false); } }
                string? tokenText = null;
                if (parsed.Captcha.Present)
                {
                    if (!parsed.Captcha.BrowserStepCan || Captcha is null) return FormFlowResult.Fallback("captcha " + parsed.Captcha.Kind);
                    if (HostQuirks.For(page.Host) is { KeyBoundToSite: true })
                    {
                        // 1.16.2: with the press switched on, an origin-bound key goes to the real page (path A) instead of the loopback address the key refuses.
                        if (Captcha is BrowserCaptchaStep { Clicker: not null }) { LastCaptchaFailed = true; item.Challenge = parsed.Captcha.Kind.ToString(); return Done(item, PageLinkState.Failed, "pagelink.fail.nobody"); }
                        return FormFlowResult.Fallback("captcha key is bound to the site: use the NeoDM browser page"); // the person's browser step would show the widget on 127.0.0.1, which this host's key refuses
                    }
                    item.Challenge = parsed.Captcha.Kind.ToString(); item.InBrowser = true; item.State = PageLinkState.NeedsYou;
                    var outcome = await Captcha.RunAsync(parsed.Captcha, page, token, skip);
                    deadline = Now() + Budget; // the person's time is not the site's time
                    item.Challenge = null; item.InBrowser = false;
                    if (outcome.Skipped) { item.State = PageLinkState.Skipped; return new(new(PageLinkState.Skipped, null), false); }
                    if (outcome.Token is null) { LastCaptchaFailed = Captcha is BrowserCaptchaStep { Clicker: not null }; return Done(item, PageLinkState.Failed, outcome.TimedOut ? "pagelink.fail.nobody" : "pagelink.fail.captcha_wrong"); }
                    tokenText = outcome.Token;
                }
                if (Now() > deadline) return Done(item, PageLinkState.Failed, "pagelink.fail.timeout");
                var target = HostPostBuilder.Target(form, page);
                if (target is null || !LoginBrowserLogic.SameSite(start.AbsoluteUri, target.AbsoluteUri)) return Done(item, PageLinkState.Failed, "pagelink.fail.left_site");
                item.State = PageLinkState.Clicked;
                var body = HostPostBuilder.Build(form, parsed.Captcha, tokenText); tokenText = null; // the token leaves this method with the one post
                reply = await PostAsync(client, target, page, body, token);
                if (reply is null) return Done(item, PageLinkState.Failed, "pagelink.fail.load");
                InspectReply?.Invoke(step, reply.Status, reply.Text);
                if (reply.IsFile) return await FinishAsync(client, jar, item, new[] { reply.Url.AbsoluteUri }, reply.Url, page.AbsoluteUri, start, token);
                parsed = HostFormParser.Parse(reply.Text, reply.Url.AbsoluteUri);
                if (parsed.ReferrerRefused)
                {
                    if (!retried) { retried = true; referer = RefererPolicy(start, true); goto restart; }
                    return Done(item, PageLinkState.Failed, "pagelink.fail.refused");
                }
                if (parsed.Links.Count > 0)
                    return await FinishAsync(client, jar, item, parsed.Links, reply.Url, page.AbsoluteUri, start, token);
                page = reply.Url;
                if (form.FromComponent && !parsed.Recognised) return FormFlowResult.Fallback("script page: the post gave no link"); // the rebuilt form was not what the page wanted: the NeoDM browser page and "Open in your browser" take over
                if (!parsed.Recognised) return Done(item, PageLinkState.Failed, parsed.ErrorText is not null && parsed.ErrorText.Contains("captcha", StringComparison.OrdinalIgnoreCase) ? "pagelink.fail.captcha_wrong" : "pagelink.fail.no_link");
                if (parsed.ErrorText is not null && parsed.ErrorText.Contains("captcha", StringComparison.OrdinalIgnoreCase)) return Done(item, PageLinkState.Failed, "pagelink.fail.captcha_wrong");
                item.State = PageLinkState.Loading;
            }
            return Done(item, PageLinkState.Failed, "pagelink.fail.no_link");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { item.State = PageLinkState.Cancelled; throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException or InvalidOperationException or UriFormatException)
        { return Done(item, PageLinkState.Failed, "pagelink.fail.load"); }
    }

    private static FormFlowResult Done(PageLinkItem item, PageLinkState state, string detail) { item.State = state; item.DetailKey = detail; return new(new(state, null, detail), false); }

    private async Task<bool> WaitTimerAsync(PageLinkItem item, int seconds, Func<bool> skip, CancellationToken token)
    {
        item.State = PageLinkState.WaitingTimer;
        for (int left = seconds; left > 0; left--)
        {
            if (skip()) return false;
            item.Countdown = left; await Delay(TimeSpan.FromSeconds(1), token); LastWaitedSeconds++;
        }
        item.Countdown = null; return true;
    }

    private HttpRequestMessage Build(HttpMethod method, Uri url, string? referer, Uri origin)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        r.Headers.TryAddWithoutValidation("Accept", RequestIdentity.DefaultAccept);
        r.Headers.TryAddWithoutValidation("Accept-Language", RequestIdentity.DefaultAcceptLanguage());
        if (referer is not null && Uri.TryCreate(referer, UriKind.Absolute, out _)) r.Headers.TryAddWithoutValidation("Referer", referer);
        return r;
    }

    private async Task<Reply?> GetAsync(HttpClient client, Uri url, string referer, Uri start, CancellationToken token)
        => await SendFollowAsync(client, () => Build(HttpMethod.Get, url, referer, start), url, referer, start, token);

    private async Task<Reply?> PostAsync(HttpClient client, Uri target, Uri page, IReadOnlyList<KeyValuePair<string, string>> body, CancellationToken token)
    {
        Func<HttpRequestMessage> make = () =>
        {
            var r = Build(HttpMethod.Post, target, page.AbsoluteUri, page); r.Headers.TryAddWithoutValidation("Origin", page.GetLeftPart(UriPartial.Authority));
            r.Content = new FormUrlEncodedContent(body); return r;
        };
        return await SendFollowAsync(client, make, target, page.AbsoluteUri, page, token, firstIsPost: true);
    }

    /// <summary>One request plus up to five redirects. Redirects stay on the pasted link's site, except that the very last answer may be a file on another address (the host's own download redirect).</summary>
    private async Task<Reply?> SendFollowAsync(HttpClient client, Func<HttpRequestMessage> first, Uri url, string referer, Uri start, CancellationToken token, bool firstIsPost = false)
    {
        var current = url; Func<HttpRequestMessage> make = first;
        for (int hop = 0; hop < 6; hop++)
        {
            using var request = make();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            int status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } loc)
            {
                var next = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                if (!ResolverSafety.IsWeb(next.AbsoluteUri)) return null;
                if (!LoginBrowserLogic.SameSite(start.AbsoluteUri, next.AbsoluteUri))
                    return new Reply { Url = next, Status = status, ContentType = "application/octet-stream", IsFile = true }; // the host sent us to its download address elsewhere: a candidate, probed by the caller
                current = next; make = () => Build(HttpMethod.Get, next, referer, start); continue;
            }
            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            var disposition = response.Content.Headers.ContentDisposition?.ToString();
            bool textual = type.StartsWith("text/") || type is "application/json" or "application/xhtml+xml" or "application/xml" || type.EndsWith("+json");
            if (!textual && response.IsSuccessStatusCode && (type.Length > 0 || disposition is not null))
                return new Reply { Url = current, Status = status, ContentType = type, IsFile = true, Disposition = disposition };
            if (response.Content.Headers.ContentLength > 3_000_000 && textual) return null;
            var bytes = await ReadLimitedAsync(response, 2_000_000, token);
            return new Reply { Url = current, Status = status, ContentType = type, Text = Encoding.UTF8.GetString(bytes), Headers = response.Headers, Disposition = disposition };
        }
        return null;
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int max, CancellationToken token)
    {
        await using var s = await response.Content.ReadAsStreamAsync(token); using var ms = new MemoryStream(); var buf = new byte[15872]; int n;
        while ((n = await s.ReadAsync(buf, token)) > 0) { ms.Write(buf, 0, n); if (ms.Length >= max) break; }
        return ms.ToArray();
    }

    /// <summary>Checks each candidate (HEAD, then a one-byte range GET) and hands the first real file over with the headers the engine needs.</summary>
    private async Task<FormFlowResult> FinishAsync(HttpClient client, CookieContainer jar, PageLinkItem item, IReadOnlyList<string> candidates, Uri from, string referer, Uri start, CancellationToken token)
    {
        foreach (var c in candidates.Take(6))
        {
            if (!ResolverSafety.IsWeb(c) || !Uri.TryCreate(c, UriKind.Absolute, out var u)) continue;
            var name = await ProbeAsync(client, u, referer, start, token);
            if (name is null) continue;
            var cookie = jar.GetCookieHeader(u);
            var grab = new BrowserGrab(u.AbsoluteUri, name.Length > 0 ? name : null, referer, cookie.Length > 0 ? cookie : null, UserAgent, RequestIdentity.DefaultAccept, RequestIdentity.DefaultAcceptLanguage());
            item.State = PageLinkState.Resolved; return new(new(PageLinkState.Resolved, grab), false);
        }
        return Done(item, PageLinkState.Failed, "pagelink.fail.no_link");
    }

    /// <summary>Returns the file name when the address answers like a file (not a web page), "" when it answers but names none, null when it does not.</summary>
    private async Task<string?> ProbeAsync(HttpClient client, Uri url, string referer, Uri start, CancellationToken token)
    {
        foreach (var method in new[] { HttpMethod.Head, HttpMethod.Get })
        {
            using var request = Build(method, url, referer, start);
            if (method == HttpMethod.Get) request.Headers.Range = new RangeHeaderValue(0, 0);
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
            catch (HttpRequestException) { continue; }
            using (response)
            {
                int status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308) return null; // a redirect chain is not followed from here: the engine's own ladder handles it
                if (status is 405 or 403 or 501 && method == HttpMethod.Head) continue;
                if (!response.IsSuccessStatusCode) return null;
                var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                if (type is "text/html" or "application/xhtml+xml" && response.Content.Headers.ContentDisposition is null) return null; // a web page, not a file
                var name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
                name = name?.Trim('"');
                if (string.IsNullOrEmpty(name)) { try { name = Uri.UnescapeDataString(Path.GetFileName(url.AbsolutePath)); } catch (UriFormatException) { name = ""; } }
                return name ?? "";
            }
        }
        return null;
    }
}
