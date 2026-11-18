using System.Text.RegularExpressions;

namespace NDM.Core.Captcha;

// 1.16.2: finding the site key of the widget on a page, and remembering the ones that only work on the site's own address.
// The keys are public values taken from the markup the browser already has. Only the shapes the three widgets use are read:
// a site-key attribute on the widget element, the widget's own class, and the render key in the loader address.

/// <summary>One key found in a page, with the widget kind it belongs to and the shape that named it.</summary>
public sealed record SiteKeyCandidate(HostCaptchaKind Kind, string SiteKey, string Source)
{
    public HostCaptcha AsCaptcha() => new(Kind, SiteKey);
}

public static class SiteKeyDiscovery
{
    public const int MaxKeys = 8;
    private static readonly RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly Regex TagRx = new(@"<(?:div|span|script|iframe|section|form|p|a)\b(?<a>(?:[^>""']|""[^""]*""|'[^']*')*)>", O);
    private static readonly Regex AttrRx = new(@"(?<n>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*(?:=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""'>]+)))?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex KeyRx = new(@"^[A-Za-z0-9_\-]{20,80}$", RegexOptions.Compiled);

    /// <summary>Every usable key in the page, in page order, without duplicates, and without the keys already known to fail.</summary>
    public static IReadOnlyList<SiteKeyCandidate> Find(string? html, IReadOnlyCollection<string>? failed = null)
    {
        var list = new List<SiteKeyCandidate>(); if (string.IsNullOrEmpty(html)) return list;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var documentKind = DocumentKind(html);
        foreach (Match tag in TagRx.Matches(html))
        {
            if (list.Count >= MaxKeys) break;
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in AttrRx.Matches(tag.Groups["a"].Value)) attrs.TryAdd(a.Groups["n"].Value, a.Groups["v"].Success ? a.Groups["v"].Value : "");
            attrs.TryGetValue("class", out var cls);
            var kind = KindOfClass(cls) ?? KindOfSource(attrs.GetValueOrDefault("src")) ?? documentKind;
            if (kind is null or HostCaptchaKind.None or HostCaptchaKind.Unsupported or HostCaptchaKind.Image) continue;
            if (!attrs.TryGetValue("data-sitekey", out var key) || !KeyRx.IsMatch(key)) continue;
            Add(list, seen, failed, kind.Value, key, KindOfClass(cls) is not null ? "widget-element" : "site-key-attribute");
        }
        // a loader address that names its key outright (the widget is then drawn by that script)
        var render = KindOfSource(html);
        if (render is { } renderKind)
            foreach (Match m in Regex.Matches(html, @"[?&]render=([A-Za-z0-9_\-]{20,80})", O))
                Add(list, seen, failed, renderKind, m.Groups[1].Value, "loader-address");
        return list;
    }

    private static void Add(List<SiteKeyCandidate> list, HashSet<string> seen, IReadOnlyCollection<string>? failed, HostCaptchaKind kind, string key, string source)
    {
        if (list.Count >= MaxKeys || failed is not null && failed.Contains(key) || !seen.Add(key)) return;
        list.Add(new SiteKeyCandidate(kind, key, source));
    }

    private static HostCaptchaKind? KindOfClass(string? cls) => cls switch
    {
        null => null,
        _ when Regex.IsMatch(cls, @"\bcf-turnstile\b", O) => HostCaptchaKind.Turnstile,
        _ when Regex.IsMatch(cls, @"\bg-recaptcha\b", O) => HostCaptchaKind.ReCaptcha,
        _ when Regex.IsMatch(cls, @"\bh-captcha\b", O) => HostCaptchaKind.HCaptcha,
        _ => null,
    };

    private static HostCaptchaKind? KindOfSource(string? src) => src switch
    {
        null => null,
        _ when src.Contains("challenges.cloudflare.com/turnstile", StringComparison.OrdinalIgnoreCase) => HostCaptchaKind.Turnstile,
        _ when src.Contains("recaptcha", StringComparison.OrdinalIgnoreCase) => HostCaptchaKind.ReCaptcha,
        _ when src.Contains("hcaptcha.com", StringComparison.OrdinalIgnoreCase) => HostCaptchaKind.HCaptcha,
        _ => null,
    };

    private static HostCaptchaKind? DocumentKind(string html)
    {
        if (html.Contains("challenges.cloudflare.com/turnstile", StringComparison.OrdinalIgnoreCase)) return HostCaptchaKind.Turnstile;
        if (html.Contains("js.hcaptcha.com", StringComparison.OrdinalIgnoreCase)) return HostCaptchaKind.HCaptcha;
        if (html.Contains("google.com/recaptcha", StringComparison.OrdinalIgnoreCase)) return HostCaptchaKind.ReCaptcha;
        return null;
    }
}

/// <summary>The keys that already failed on the loopback origin, kept for the session only. Small, bounded, never logged.</summary>
public sealed class FailedSiteKeys
{
    public const int Max = 40;
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public int Count { get { lock (_keys) return _keys.Count; } }
    public bool Contains(string? key) { lock (_keys) return key is not null && _keys.Contains(key); }

    /// <summary>Remembers a key; the oldest one is dropped once the set is full.</summary>
    public void Add(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_keys)
        {
            if (!_keys.Add(key)) return;
            _order.Enqueue(key);
            while (_order.Count > Max) _keys.Remove(_order.Dequeue());
        }
    }

    public IReadOnlyList<string> Snapshot() { lock (_keys) return _keys.ToArray(); }
    public void Clear() { lock (_keys) { _keys.Clear(); _order.Clear(); } }
}
