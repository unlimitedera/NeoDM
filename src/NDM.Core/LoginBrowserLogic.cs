using System.Net;

namespace NDM.Core;

/// <summary>1.12.0: what the NeoDM browser hands back when the user starts a download on a page. Every field is bounded and checked by <see cref="LoginBrowserLogic.Clean"/>.</summary>
public sealed record BrowserGrab(string Url, string? FileName, string? Referer, string? Cookie, string? UserAgent, string? Accept, string? AcceptLanguage);

/// <summary>A cookie as the browser component reports it.</summary>
public readonly record struct BrowserCookie(string Name, string Value, string Domain, string Path, bool Secure, DateTimeOffset? Expires);

/// <summary>Whether the Edge WebView2 runtime that the NeoDM browser needs is installed.</summary>
public enum LoginBrowserRuntime { Available, Missing }

/// <summary>Pure rules of the NeoDM login browser (1.12.0): where it opens, which job a captured download belongs to, which cookies may travel with it. No browser component is needed to test any of this.
/// Cookies stay on this computer, go only to the download's own host, are bounded in size and are never logged.</summary>
public static class LoginBrowserLogic
{
    public const int MaxCookieHeader = 8192, MaxCookieCount = 100, MaxUrl = 8192;
    public const string RuntimeDownloadPage = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>The address the NeoDM browser opens for a failed download: the page it was started from when known (http or https), otherwise the origin of the file address; null when neither is usable.</summary>
    public static string? StartUrl(string? referer, string? fileUrl)
    {
        if (Web(referer) is { } page) return page.AbsoluteUri;
        if (Web(fileUrl) is { } file) return file.GetLeftPart(UriPartial.Authority) + "/";
        return null;
    }

    /// <summary>The host name shown in the address bar (no path, query or user information); empty when the address is not an http or https address.</summary>
    public static string DisplayHost(string? url) => Web(url)?.IdnHost ?? "";

    /// <summary>The registrable site of a host name: the last two labels, or three under a two-letter country code with a common second-level label (co.uk, com.au). Addresses of an IP are kept whole.</summary>
    public static string SiteOf(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || IPAddress.TryParse(host.Trim('[', ']'), out _)) return host;
        var labels = host.Split('.');
        if (labels.Length <= 2) return host;
        var second = labels[^2]; var take = labels[^1].Length == 2 && SecondLevel.Contains(second) ? 3 : 2;
        return string.Join('.', labels.Skip(labels.Length - Math.Min(take, labels.Length)));
    }
    private static readonly HashSet<string> SecondLevel = new(StringComparer.Ordinal) { "co", "com", "org", "net", "gov", "edu", "ac", "or", "ne", "go" };

    public static bool SameSite(string? a, string? b) => Web(a) is { } x && Web(b) is { } y && SiteOf(x.IdnHost) == SiteOf(y.IdnHost);

    /// <summary>True when a captured download belongs to the failed job: the same site as the job's address or page, or the same file name.</summary>
    public static bool Matches(string jobUrl, string? jobReferer, string jobFileName, BrowserGrab grab) =>
        SameSite(jobUrl, grab.Url) || SameSite(jobReferer, grab.Url)
        || (!string.IsNullOrWhiteSpace(grab.FileName) && !string.IsNullOrWhiteSpace(jobFileName) && string.Equals(grab.FileName.Trim(), jobFileName.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The Cookie header for a request to <paramref name="target"/>: only cookies whose domain, path and secure flag fit that address and that are not expired, longest path first, at most
    /// <see cref="MaxCookieCount"/> cookies and <see cref="MaxCookieHeader"/> characters. Cookies with unusable names or values are skipped. Empty when nothing fits.</summary>
    public static string CookieHeader(IEnumerable<BrowserCookie> cookies, Uri target, DateTimeOffset now)
    {
        if (target.Scheme is not ("http" or "https")) return "";
        var host = target.IdnHost.ToLowerInvariant(); var path = string.IsNullOrEmpty(target.AbsolutePath) ? "/" : target.AbsolutePath;
        var fit = new List<BrowserCookie>();
        foreach (var c in cookies)
        {
            if (!UsableName(c.Name) || !UsableValue(c.Value)) continue;
            if (c.Expires is { } expires && expires <= now) continue;
            if (c.Secure && target.Scheme != "https") continue;
            if (!DomainFits(c.Domain, host) || !PathFits(c.Path, path)) continue;
            fit.Add(c);
        }
        var text = new System.Text.StringBuilder(); var count = 0;
        foreach (var c in fit.OrderByDescending(c => c.Path?.Length ?? 0).ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            var piece = c.Name + "=" + c.Value; var add = piece.Length + (text.Length > 0 ? 2 : 0);
            if (count >= MaxCookieCount || text.Length + add > MaxCookieHeader) break;
            if (text.Length > 0) text.Append("; ");
            text.Append(piece); count++;
        }
        return text.ToString();
    }
    private static bool UsableName(string? name) => !string.IsNullOrEmpty(name) && name.Length <= 256 && name.All(ch => ch > ' ' && ch < 127 && ch != ';' && ch != '=' && ch != ',' && ch != '"');
    private static bool UsableValue(string? value) => value is not null && value.Length <= 4096 && value.All(ch => ch >= ' ' && ch < 127 && ch != ';');
    private static bool DomainFits(string? domain, string host)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        var d = domain.Trim().ToLowerInvariant();
        if (!d.StartsWith('.')) return d == host;                 // a host-only cookie
        d = d.TrimStart('.');
        return d.Length > 0 && (host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }
    private static bool PathFits(string? cookiePath, string requestPath)
    {
        var p = string.IsNullOrEmpty(cookiePath) ? "/" : cookiePath;
        if (p == "/") return true;
        if (!requestPath.StartsWith(p, StringComparison.Ordinal)) return false;
        return requestPath.Length == p.Length || p.EndsWith('/') || requestPath[p.Length] == '/';
    }

    /// <summary>Checks and bounds a captured download; null when it cannot be used (not an http or https address, too long). Header values with control characters or over their limits are dropped, not repaired.</summary>
    public static BrowserGrab? Clean(BrowserGrab grab)
    {
        if (Web(grab.Url) is not { } url || grab.Url.Length > MaxUrl) return null;
        string? referer = Web(grab.Referer) is { } r && grab.Referer!.Length <= MaxUrl ? r.AbsoluteUri : null;
        string? cookie = grab.Cookie is { Length: > 0 and <= MaxCookieHeader } c && c.All(ch => ch >= ' ' && ch != 127) ? c : null;
        string? agent = grab.UserAgent is { } a && RequestIdentity.IsHeaderValue(a, RequestIdentity.MaxUserAgent) ? a : null;
        string? accept = grab.Accept is { } ac && RequestIdentity.IsHeaderValue(ac, RequestIdentity.MaxAccept) ? ac : null;
        string? language = grab.AcceptLanguage is { } l && RequestIdentity.IsHeaderValue(l, RequestIdentity.MaxAcceptLanguage) ? l : null;
        string? name = grab.FileName is { Length: > 0 } f ? DownloadFileNames.Sanitize(f) : null;
        return new BrowserGrab(url.AbsoluteUri, name, referer, cookie, agent, accept, language);
    }

    /// <summary>The state of the browser component from the version text it reports (null or empty = not installed).</summary>
    public static LoginBrowserRuntime RuntimeFrom(Func<string?> version)
    {
        try { return string.IsNullOrWhiteSpace(version()) ? LoginBrowserRuntime.Missing : LoginBrowserRuntime.Available; }
        catch (Exception) { return LoginBrowserRuntime.Missing; }
    }

    private static Uri? Web(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.UserInfo.Length == 0 && u.Host.Length > 0 ? u : null;
}
