using System.Net;

namespace NDM.Core.Captcha;

// 1.16.2: the loopback page has to show the site's own page, but the widget key may only accept the site's own address,
// so the page is fetched again with the job's cookies and user agent and laid out again under the loopback address.
// Every address the page names is pointed back through the loopback server, so those requests leave with the same cookies.

/// <summary>One fetched resource: the body, its type, and the address it really came from (after redirects).</summary>
public sealed record ProxyFetch(byte[] Body, string ContentType, string FinalUrl);

/// <summary>Fetches one address with the job's identity. Tests give a fake that answers from saved files.</summary>
public interface IPageSource
{
    Task<ProxyFetch?> GetAsync(Uri url, CancellationToken token);
}

/// <summary>The real one: one plain GET, no redirect surprise, a size cap, and the job's cookies and user agent. It goes through the policy's proxy like every other fetch.</summary>
public sealed class HttpPageSource : IPageSource, IDisposable
{
    public const int MaxBytes = 8 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly string? _cookie;

    public HttpPageSource(string? cookieHeader, string? userAgent, ProxyOptions? proxy = null, HttpClient? client = null)
    {
        _cookie = string.IsNullOrWhiteSpace(cookieHeader) ? null : cookieHeader.Trim();
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, UseCookies = false };
        ProxyHttp.Apply(handler, proxy);
        _client = client ?? new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        if (!string.IsNullOrWhiteSpace(userAgent)) _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
    }

    public async Task<ProxyFetch?> GetAsync(Uri url, CancellationToken token)
    {
        if (url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", _cookie);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            if (body.Length > MaxBytes) return null;
            var type = response.Content.Headers.ContentType is { } ct ? ct.MediaType + (ct.CharSet is { Length: > 0 } cs ? "; charset=" + cs : "") : "application/octet-stream";
            return new ProxyFetch(body, type, response.RequestMessage?.RequestUri?.AbsoluteUri ?? url.AbsoluteUri);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException or InvalidOperationException) { return null; }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Lays the fetched page out again for the loopback address: absolute addresses back through the loopback server, a base tag for the rest, the site's own scripts kept.</summary>
public static class PageRewriter
{
    private static readonly System.Text.RegularExpressions.Regex MetaCsp = new(@"<meta\b(?=[^>]*http-equiv\s*=\s*[""']?content-security-policy)[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex Integrity = new(@"\s+integrity\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex TagRx = new(@"<(?<n>[a-zA-Z][^\s/>]*)(?<a>(?:[^>""']|""[^""]*""|'[^']*')*)>", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex LinkAttr = new(@"(?<n>\b(?:src|href|action|poster)\s*=\s*)(?<q>[""'])(?<v>[^""']*)(?<q2>[""'])", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex HeadOpen = new(@"<head\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The page with every address it names pointed at the loopback server. <paramref name="link"/> turns a web address into the loopback address that fetches it.</summary>
    public static string Rewrite(string html, Uri pageUrl, Func<Uri, string> link)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = MetaCsp.Replace(Integrity.Replace(html, ""), "");
        var builder = new System.Text.StringBuilder(text.Length + 512);
        int at = 0;
        foreach (System.Text.RegularExpressions.Match tag in TagRx.Matches(text))
        {
            builder.Append(text, at, tag.Index - at);
            var attrs = LinkAttr.Replace(tag.Groups["a"].Value, m =>
            {
                var raw = WebUtility.HtmlDecode(m.Groups["v"].Value).Trim();
                if (raw.Length == 0 || raw.StartsWith('#') || Skip(raw)) return m.Value;
                if (!Uri.TryCreate(pageUrl, raw, out var absolute) || absolute.Scheme is not ("http" or "https") || absolute.UserInfo.Length > 0) return m.Value;
                return m.Groups["n"].Value + "\"" + WebUtility.HtmlEncode(link(absolute)) + "\"";
            });
            builder.Append('<').Append(tag.Groups["n"].Value).Append(attrs).Append('>');
            at = tag.Index + tag.Length;
        }
        builder.Append(text, at, text.Length - at);
        var withBase = builder.ToString();
        var baseTag = "<base href=\"" + WebUtility.HtmlEncode(pageUrl.AbsoluteUri) + "\">";
        var head = HeadOpen.Match(withBase);
        return head.Success ? withBase.Insert(head.Index + head.Length, baseTag) : baseTag + withBase;
    }

    private static bool Skip(string raw) => raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
        || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
        || raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The page service for one challenge: the fetched page, and every asset the page names.</summary>
public sealed class LoopbackPageProxy(Uri pageUrl, IPageSource source)
{
    public Uri PageUrl { get; } = pageUrl;
    private IPageSource Source { get; } = source;

    /// <summary>The page itself, laid out again for the loopback address. Null when the site would not answer.</summary>
    public async Task<string?> PageHtmlAsync(Func<Uri, string> link, CancellationToken token)
    {
        var page = await Source.GetAsync(PageUrl, token).ConfigureAwait(false);
        if (page is null || page.Body.Length == 0) return null;
        var html = System.Text.Encoding.UTF8.GetString(page.Body);
        if (html.Length > 0 && html[0] == '\uFEFF') html = html[1..];
        var finalUrl = Uri.TryCreate(page.FinalUrl, UriKind.Absolute, out var f) && f.Scheme is "http" or "https" ? f : PageUrl;
        return PageRewriter.Rewrite(html, finalUrl, link);
    }

    /// <summary>One asset (script, style, image or font).</summary>
    public Task<ProxyFetch?> AssetAsync(Uri url, CancellationToken token) => Source.GetAsync(url, token);
}
