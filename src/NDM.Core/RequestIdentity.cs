using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace NDM.Core;

/// <summary>Which User-Agent NeoDM presents. Browser = the browser's own (captured by the extension) or a browser-style default; NeoDM = the product name; Custom = the user's text.</summary>
public enum IdentityMode { Browser, NeoDM, Custom }

/// <summary>The request identity of a download: User-Agent, Accept and Accept-Language. Pure functions, so every rule is testable.</summary>
public static class RequestIdentity
{
    /// <summary>The Chrome major version named by the default browser-style User-Agent. Bump this one number when Chrome moves on.</summary>
    public const int ChromeMajor = 156;
    public const string DefaultAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8";
    public const string FallbackAcceptLanguage = "en-US,en;q=0.9";
    public const int MaxUserAgent = 512, MaxAccept = 512, MaxAcceptLanguage = 128;

    /// <summary>Major.Minor.Build of this build, for example 1.5.7.</summary>
    public static string Version { get; } = FormatVersion(typeof(RequestIdentity).Assembly.GetName().Version);
    public static string FormatVersion(Version? version) => version is null ? "1.0.0" : $"{version.Major}.{version.Minor}{(version.Build >= 0 ? "." + version.Build : ".0")}";
    /// <summary>NeoDM/1.5.7</summary>
    public static string ProductToken => "NeoDM/" + Version;
    /// <summary>A browser-style User-Agent that still names the product at the end.</summary>
    public static string BrowserUserAgent(string? version = null) =>
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromeMajor}.0.0.0 Safari/537.36 NeoDM/{version ?? Version}";

    /// <summary>Printable ASCII (space to tilde), not empty, at most <paramref name="max"/> characters. Anything else is refused: no control characters can reach a header.</summary>
    public static bool IsHeaderValue(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length > max) return false;
        foreach (var c in value) if (c < 0x20 || c > 0x7e) return false;
        return value.Trim().Length > 0;
    }

    /// <summary>Accept-Language from the Windows display language: "de-DE,de;q=0.9,en;q=0.8"; English and unknown cultures give "en-US,en;q=0.9".</summary>
    public static string DefaultAcceptLanguage(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        var name = culture.Name;
        if (name.Length == 0 || name.Length > 20 || !name.All(c => c is '-' or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))) return FallbackAcceptLanguage;
        var language = culture.TwoLetterISOLanguageName;
        if (language.Length != 2 || language == "iv") return FallbackAcceptLanguage;
        if (language == "en") return name.Equals("en-US", StringComparison.OrdinalIgnoreCase) || !name.Contains('-') ? FallbackAcceptLanguage : $"{name},en;q=0.9";
        return name.Contains('-') ? $"{name},{language};q=0.9,en;q=0.8" : $"{language},en;q=0.8";
    }

    /// <summary>The User-Agent to send. Custom text wins when set; NeoDM mode names the product (unless the compatibility ladder asks for the browser identity); Browser uses the captured one or the default.</summary>
    public static string UserAgentFor(IdentityMode mode, string? custom, string? captured, bool forceBrowser = false)
    {
        if (mode == IdentityMode.Custom && IsHeaderValue(custom, MaxUserAgent)) return custom!.Trim();
        if (mode == IdentityMode.NeoDM && !forceBrowser) return ProductToken;
        return IsHeaderValue(captured, MaxUserAgent) ? captured! : BrowserUserAgent();
    }
}

public enum RefererMode { Original, SiteRoot, None }

/// <summary>One shape of the first request of a job. The default is a ranged probe with the job's own identity and referer.</summary>
public readonly record struct RequestVariant(bool NoRange = false, bool BrowserIdentity = false, RefererMode Referer = RefererMode.Original);

/// <summary>The bounded sequence of variants tried when the first answer is a refusal that a different request shape often cures, and the rules for the
/// per-link connection limit. Nothing here changes the address of the connection, hides the user or answers challenges.</summary>
public static class CompatibilityLadder
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(10);
    /// <summary>ErrorCode of a job that failed with an HTTP status.</summary>
    public const string HttpStatusError = "http-status";

    /// <summary>400, 403, 405, 406, 409, 416, 429, 501, 503, and 401 when the server sent no sign-in challenge.</summary>
    public static bool IsLadderStatus(HttpStatusCode status, bool hasAuthenticationChallenge = false) => status switch
    {
        HttpStatusCode.Unauthorized => !hasAuthenticationChallenge,
        HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotAcceptable or HttpStatusCode.Conflict
            or HttpStatusCode.RequestedRangeNotSatisfiable or HttpStatusCode.TooManyRequests or HttpStatusCode.NotImplemented or HttpStatusCode.ServiceUnavailable => true,
        _ => false
    };

    /// <summary>Statuses where a segment of a transfer that already started works is read as a limit on connections.</summary>
    public static bool IsConnectionLimitStatus(HttpStatusCode status) => status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    /// <summary>The next variant after a refusal, or null when none is left. (a) without Range, (b) browser identity when the job used the NeoDM identity,
    /// (c) the site's own root as referer when it was missing or from another site, (d) no referer when one was sent.</summary>
    public static RequestVariant? Next(RequestVariant current, IdentityMode mode, string? referer, Uri url)
    {
        if (!current.NoRange) return current with { NoRange = true };
        if (mode == IdentityMode.NeoDM && !current.BrowserIdentity) return current with { BrowserIdentity = true };
        var sameSite = referer is not null && Uri.TryCreate(referer, UriKind.Absolute, out var r) && r.Scheme == url.Scheme && r.Authority.Equals(url.Authority, StringComparison.OrdinalIgnoreCase);
        if (current.Referer == RefererMode.Original && !sameSite) return current with { Referer = RefererMode.SiteRoot };
        if (referer is not null && current.Referer != RefererMode.None) return current with { Referer = RefererMode.None };
        return null;
    }

    /// <summary>What changed between two variants, in words for the attempt trail.</summary>
    public static string Describe(RequestVariant before, RequestVariant after)
    {
        var parts = new List<string>();
        if (after.NoRange && !before.NoRange) parts.Add("without Range");
        if (after.BrowserIdentity && !before.BrowserIdentity) parts.Add("browser identity");
        if (after.Referer != before.Referer) parts.Add(after.Referer == RefererMode.SiteRoot ? "referer: site root" : after.Referer == RefererMode.None ? "without referer" : "original referer");
        return string.Join(", ", parts);
    }

    /// <summary>Pause before the next attempt: Retry-After for 429 and 503 (at most ten seconds), otherwise a quarter of a second.</summary>
    public static TimeSpan Delay(HttpStatusCode status, RetryConditionHeaderValue? retryAfter, DateTimeOffset now, TimeSpan? pause = null, TimeSpan? maximum = null)
    {
        var floor = pause ?? Pause; var cap = maximum ?? MaxRetryAfter;
        if (status is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) || retryAfter is null) return floor;
        TimeSpan? wait = retryAfter.Delta ?? (retryAfter.Date is { } date ? date - now : null);
        if (wait is null) return floor;
        return wait.Value < floor ? floor : wait.Value > cap ? cap : wait.Value;
    }

    /// <summary>Halves the number of connections, never below one.</summary>
    public static int ReduceConnections(int current) => Math.Max(1, current / 2);

    /// <summary>"Attempt 1: 403, Attempt 2 (without Range): 200"</summary>
    public static string Label(int number, string change, string result) => $"Attempt {number}{(change.Length > 0 ? " (" + change + ")" : "")}: {result}";
}
