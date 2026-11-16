using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NDM.Core;

// 1.16.1: the generic form-based host page logic (pure, no network, no UI). Many file hosts share one page flow: a form with hidden fields
// (op, id, rand, referer, dl_token), a button such as "Free Download", a timer the site shows, sometimes a captcha, and a reply that holds the direct link.
// The field names are only signals; there is no per-site code here. What a human check needs is never solved or skipped: see HostCaptcha.

public enum HostCaptchaKind { None, ReCaptcha, HCaptcha, Turnstile, Image, Unsupported }

/// <summary>A captcha the page asks for. Only ReCaptcha, HCaptcha and Turnstile with a site key can be done by the person in the real browser step.</summary>
public sealed record HostCaptcha(HostCaptchaKind Kind, string? SiteKey)
{
    public static readonly HostCaptcha None = new(HostCaptchaKind.None, null);
    public bool Present => Kind != HostCaptchaKind.None;
    /// <summary>The browser step can carry this check: the widget of the three known services with a site key.</summary>
    public bool BrowserStepCan => Kind is HostCaptchaKind.ReCaptcha or HostCaptchaKind.HCaptcha or HostCaptchaKind.Turnstile && !string.IsNullOrEmpty(SiteKey);
    /// <summary>The form field the site reads the token from.</summary>
    public string? ResponseField => Kind switch { HostCaptchaKind.ReCaptcha => "g-recaptcha-response", HostCaptchaKind.HCaptcha => "h-captcha-response", HostCaptchaKind.Turnstile => "cf-turnstile-response", _ => null };
}

public sealed record HostFormField(string Name, string Value, string Type);

/// <summary>One form of the page: where it posts, its fields in page order, and the submit button that starts the free download.</summary>
public sealed record HostForm(string Action, string Method, IReadOnlyList<HostFormField> Fields, string? SubmitName, string? SubmitValue, string? SubmitLabel, bool SubmitDisabled, bool FromComponent = false)
{
    public string? Get(string name) => Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
    public bool Has(string name) => Fields.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public string Op => (Get("op") ?? "").ToLowerInvariant();
}

/// <summary>What one fetched page holds, as far as the generic logic can tell.</summary>
public sealed record HostPage(HostForm? Form, int? CountdownSeconds, HostCaptcha Captcha, string? ErrorText, IReadOnlyList<string> Links, bool ReferrerRefused)
{
    public bool Recognised => Form is not null;
}

/// <summary>Reads the form, the timer, the captcha kind and the direct links out of a page's HTML.</summary>
public static class HostFormParser
{
    public const int MaxHtml = 2_000_000, MaxCountdown = 120;
    private static readonly RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline;
    private static readonly Regex Comments = new(@"<!--.*?-->", O), Scripts = new(@"<script\b.*?</script>", O), Styles = new(@"<style\b.*?</style>", O);
    private static readonly Regex FormRx = new(@"<form\b(?<a>(?:[^>""']|""[^""]*""|'[^']*')*)>(?<b>.*?)</form>", O);
    private static readonly Regex InputRx = new(@"<input\b(?<a>(?:[^>""']|""[^""]*""|'[^']*')*?)/?>", O), ButtonRx = new(@"<button\b(?<a>(?:[^>""']|""[^""]*""|'[^']*')*)>(?<t>.*?)</button>", O);
    private static readonly Regex AttrRx = new(@"(?<n>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*(?:=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""'>]+)))?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);

    private static Dictionary<string, string> Attrs(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRx.Matches(text)) d.TryAdd(m.Groups["n"].Value, WebUtility.HtmlDecode(m.Groups["v"].Success ? m.Groups["v"].Value : ""));
        return d;
    }
    private static string Clean(string html) => Styles.Replace(Scripts.Replace(Comments.Replace(html, " "), " "), " ");

    public static HostPage Parse(string? html, string pageUrl)
    {
        if (string.IsNullOrEmpty(html)) return new(null, null, HostCaptcha.None, null, Array.Empty<string>(), false);
        if (html.Length > MaxHtml) html = html[..MaxHtml];
        var clean = Clean(html);
        var form = FindForm(clean) ?? FindComponentForm(clean);
        var captcha = DetectCaptcha(html, clean);
        var error = FindError(clean);
        return new(form, ParseCountdown(html, clean), captcha, error, ExtractLinks(html, clean, pageUrl), error is not null && NotAllowedRx.IsMatch(error));
    }

    // ---- form ----
    private static HostForm? FindForm(string clean)
    {
        HostForm? best = null; int bestScore = 0;
        foreach (Match fm in FormRx.Matches(clean))
        {
            var fa = Attrs(fm.Groups["a"].Value); var body = fm.Groups["b"].Value;
            var fields = new List<HostFormField>(); string? sName = null, sValue = null, sLabel = null; bool sDisabled = false; int sScore = 0;
            foreach (Match im in InputRx.Matches(body))
            {
                var a = Attrs(im.Groups["a"].Value); a.TryGetValue("name", out var name); a.TryGetValue("type", out var type); type = (type ?? "text").ToLowerInvariant();
                a.TryGetValue("value", out var value);
                if (type is "submit" or "button" or "image")
                {
                    int s = PageControlMatcher.Score(value); if (s > sScore) { sScore = s; sName = name; sValue = value; sLabel = value; sDisabled = a.ContainsKey("disabled"); }
                    continue;
                }
                if (string.IsNullOrEmpty(name) || type is "file" or "password" or "checkbox" or "radio" && !a.ContainsKey("checked")) continue;
                fields.Add(new HostFormField(name, value ?? "", type));
            }
            foreach (Match bm in ButtonRx.Matches(body))
            {
                var a = Attrs(bm.Groups["a"].Value); a.TryGetValue("type", out var type); if ((type ?? "submit").Equals("button", StringComparison.OrdinalIgnoreCase)) continue;
                var label = WebUtility.HtmlDecode(Regex.Replace(Tags.Replace(bm.Groups["t"].Value, " "), @"\s+", " ").Trim());
                a.TryGetValue("name", out var name); a.TryGetValue("value", out var value);
                int s = Math.Max(PageControlMatcher.Score(label), PageControlMatcher.Score(value));
                if (name is not null && name.StartsWith("method_free", StringComparison.OrdinalIgnoreCase)) s = Math.Max(s, 95);
                if (s > sScore) { sScore = s; sName = name; sValue = value; sLabel = label; sDisabled = a.ContainsKey("disabled"); }
            }
            var op = fields.FirstOrDefault(f => f.Name.Equals("op", StringComparison.OrdinalIgnoreCase))?.Value?.ToLowerInvariant() ?? "";
            int score = (op is "download1" or "download2" or "download" or "download3" ? 100 : 0) + sScore + (fields.Any(f => f.Name is "id" or "rand" or "dl_token") ? 10 : 0);
            if (op.Length == 0 && sScore == 0) continue; // a search box or a log-in form: not ours
            if (op is "login" or "register" or "search" or "premium" or "payments" or "upgrade") continue;
            fa.TryGetValue("action", out var action); fa.TryGetValue("method", out var method);
            if (score > bestScore) { bestScore = score; best = new HostForm(action ?? "", (method ?? "get").ToUpperInvariant(), fields, sName, sValue, sLabel, sDisabled); }
        }
        return best;
    }


    // ---- script-driven pages: a custom element carries what a form would (1.16.1, seen on the pilot host's second step) ----
    private static readonly Regex ComponentRx = new(@"<(?<t>[a-z][a-z0-9]*(?:-[a-z0-9]+)+)\b(?<a>(?:[^>""']|""[^""]*""|'[^']*')*)>", O);
    /// <summary>Some hosts draw the second step with a script component (for example a "download-countdown" element) instead of a form: the element holds the code, rand, referer, free-method and token as attributes.
    /// The standard XFileSharing-style fields are rebuilt from those names (generic: any custom element that carries rand together with a token or code). The caller marks it so a failed post falls back to the NeoDM browser page.</summary>
    private static HostForm? FindComponentForm(string clean)
    {
        foreach (Match m in ComponentRx.Matches(clean))
        {
            var a = Attrs(m.Groups["a"].Value);
            string? Pick(params string[] names) { foreach (var n in names) foreach (var k in new[] { n, ":" + n }) if (a.TryGetValue(k, out var v) && v.Length > 0) return v; return null; }
            var rand = Pick("rand"); var token = Pick("dl-token", "dl_token", "token"); var code = Pick("code", "file-code", "id", "file_id");
            if (rand is null || code is null || token is null) continue;
            var fields = new List<HostFormField> { new("op", "download2", "hidden"), new("id", code, "hidden"), new("rand", rand, "hidden"), new("referer", Pick("referer") ?? "", "hidden"), new("method_free", Pick("free-method", "method-free") ?? "Free Download", "hidden"), new("dl_token", token, "hidden") };
            return new HostForm("", "POST", fields, null, null, null, false, FromComponent: true);
        }
        return null;
    }

    // ---- timer ----
    private static readonly Regex[] CountdownRx =
    {
        new(@"\bcountdown\s*=\s*[""'](\d{1,4})[""']", O), new(@"\bdata-(?:countdown|seconds|wait|timer)\s*=\s*[""'](\d{1,4})[""']", O),
        new(@"\b(?:var|let|const)\s+(?:countdown|count_?down|timer|wait(?:_?time)?|seconds|secs|cnt|delay)\w*\s*=\s*(\d{1,4})\b", O),
        new(@"<(?:span|div|b|strong)\b[^>]*\bid\s*=\s*[""'](?:countdown|countdown_str|timer|seconds)[""'][^>]*>\s*(?:<[^>]+>\s*)*(\d{1,4})\b", O),
        new(@"(?:wait|please wait|in|after|within)\D{0,40}?<(?:span|b|strong)[^>]*>\s*(\d{1,4})\s*</(?:span|b|strong)>\s*(?:seconds?|secs?)", O),
        new(@"(?:please )?wait\s+(\d{1,4})\s*(?:seconds?|secs?)", O),
    };
    /// <summary>The seconds the site says to wait (capped at 120), or null when the page states none. The caller waits at least this long; never less.</summary>
    public static int? ParseCountdown(string html, string? clean = null)
    {
        clean ??= Clean(html);
        // the page's own markup first, then script text (a countdown set in a script), then readable text
        foreach (var text in new[] { clean, html })
            foreach (var rx in CountdownRx)
                foreach (Match m in rx.Matches(text))
                    if (int.TryParse(m.Groups[1].Value, out var s) && s > 0) return Math.Min(s, MaxCountdown);
        return null;
    }

    // ---- captcha ----
    private static readonly Regex SiteKeyRx = new(@"data-sitekey\s*=\s*[""']([A-Za-z0-9_\-]{20,80})[""']", O);
    private static readonly Regex EncodedKeyRx = new(@"data-sitekey\s*=\s*(?:&quot;|&#34;|&#x22;)([A-Za-z0-9_\-]{20,80})", O); // the widget markup sits inside a component attribute
    private static readonly Regex RenderKeyRx = new(@"sitekey['""]?\s*[:=]\s*['""]([A-Za-z0-9_\-]{20,80})['""]", O);
    private static readonly Regex V3Rx = new(@"recaptcha/(?:api|enterprise)\.js\?render=(?!explicit)([A-Za-z0-9_\-]{20,80})", O);
    public static HostCaptcha DetectCaptcha(string html, string? clean = null)
    {
        clean ??= Clean(html); html = Comments.Replace(html, " ");
        // a widget element in the markup (not a mention in a comment) names its kind; the script text only helps with the key
        var key = SiteKeyRx.Match(clean) is { Success: true } k ? k.Groups[1].Value : EncodedKeyRx.Match(clean) is { Success: true } ek ? ek.Groups[1].Value : RenderKeyRx.Match(html) is { Success: true } r ? r.Groups[1].Value : null;
        if (Regex.IsMatch(clean, @"class\s*=\s*[""'][^""']*\bcf-turnstile\b", O) || html.Contains("challenges.cloudflare.com/turnstile", StringComparison.OrdinalIgnoreCase)) return new(HostCaptchaKind.Turnstile, key);
        if (Regex.IsMatch(clean, @"class\s*=\s*[""'][^""']*\bh-captcha\b", O) || html.Contains("hcaptcha.com/1/api.js", StringComparison.OrdinalIgnoreCase)) return new(HostCaptchaKind.HCaptcha, key);
        if (Regex.IsMatch(clean, @"class\s*=\s*[""'][^""']*\bg-recaptcha\b", O) || (key is not null && html.Contains("recaptcha", StringComparison.OrdinalIgnoreCase))) return new(HostCaptchaKind.ReCaptcha, key);
        if (V3Rx.IsMatch(html) && !Regex.IsMatch(clean, @"id\s*=\s*[""']recaptcha-token[""']", O)) return new(HostCaptchaKind.Unsupported, null); // an invisible score check: nothing for a person to click
        if (Regex.IsMatch(clean, @"<img\b[^>]*(?:captcha)[^>]*>|name\s*=\s*[""']captcha_?code[""']", O)) return new(HostCaptchaKind.Image, null);
        return HostCaptcha.None;
    }

    // ---- errors ----
    private static readonly Regex ErrorRx = new(@"<(?:div|p|span|b|center)\b[^>]*\bclass\s*=\s*[""'][^""']*\b(?:err|error|alert-danger|alert-error|text-red-\d+)\w*[""'][^>]*>(.*?)</(?:div|p|span|b|center)>", O);
    public static readonly Regex NotAllowedRx = new(@"not allowed from (?:this )?domain|you(?:'|&#39;|’)re coming from|invalid referer|direct (?:link|access) (?:is )?not allowed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static string? FindError(string clean)
    {
        foreach (Match m in ErrorRx.Matches(clean))
        {
            var t = WebUtility.HtmlDecode(Regex.Replace(Tags.Replace(m.Groups[1].Value, " "), @"\s+", " ").Trim());
            if (t.Length is > 2 and < 300) return t;
        }
        var plain = WebUtility.HtmlDecode(Regex.Replace(Tags.Replace(clean, " "), @"\s+", " "));
        var n = NotAllowedRx.Match(plain);
        return n.Success ? plain.Substring(Math.Max(0, n.Index - 20), Math.Min(120, plain.Length - Math.Max(0, n.Index - 20))).Trim() : null;
    }

    // ---- links ----
    private static readonly Regex JsonLinkRx = new(@"""(?:url|link|direct_?link|download_?(?:url|link)|file|redirect)""\s*:\s*""(?<u>(?:\\.|[^""\\])+)""", O);
    private static readonly Regex EncodedRx = new(@"https?%3A%2F%2F[A-Za-z0-9%._~\-+]+", O);
    private static readonly Regex HrefRx = new(@"<a\b(?<a>(?:[^>""']|""[^""]*""|'[^']*')*)>(?<t>.*?)</a>", O);
    private static readonly Regex FileExt = new(@"\.(?:zip|rar|7z|exe|msi|iso|mp4|mkv|avi|mp3|flac|pdf|apk|bin|dmg|tar|gz|img|epub|cbz|cbr)(?:$|[?#])", O);
    /// <summary>Direct-link candidates in order of trust: values in a JSON reply, url-encoded addresses, then anchors that read as the download (or end in a file extension). Same-page anchors and non-web schemes are dropped.</summary>
    public static IReadOnlyList<string> ExtractLinks(string html, string? clean, string pageUrl)
    {
        clean ??= Clean(html); var list = new List<string>();
        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var s = WebUtility.HtmlDecode(raw.Trim()).Replace("\\/", "/");
            if (!Uri.TryCreate(new Uri(pageUrl), s, out var u) || u.Scheme is not ("http" or "https") || u.UserInfo.Length > 0) return;
            if (u.AbsoluteUri.TrimEnd('/') == new Uri(pageUrl).AbsoluteUri.TrimEnd('/')) return;
            var abs = u.AbsoluteUri; if (!list.Contains(abs)) list.Add(abs);
        }
        var trimmed = html.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try { using var doc = JsonDocument.Parse(trimmed); CollectJson(doc.RootElement, Add, 0); } catch (JsonException) { foreach (Match m in JsonLinkRx.Matches(html)) Add(Regex.Unescape(m.Groups["u"].Value)); }
        }
        foreach (Match m in EncodedRx.Matches(html)) { try { Add(Uri.UnescapeDataString(m.Value)); } catch (UriFormatException) { } }
        foreach (Match m in HrefRx.Matches(clean))
        {
            var a = Attrs(m.Groups["a"].Value); if (!a.TryGetValue("href", out var href) || href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
            var text = Regex.Replace(Tags.Replace(m.Groups["t"].Value, " "), @"\s+", " ").Trim();
            bool reads = Regex.IsMatch(text, @"direct (?:download )?link|click here to download|download (?:now|file|link)|^download$", RegexOptions.IgnoreCase) && PageControlMatcher.Score(text) > 0;
            a.TryGetValue("class", out var cls); bool classed = cls is not null && Regex.IsMatch(cls, @"\b(?:direct|download-?link|dl-?link)\b", RegexOptions.IgnoreCase);
            if (reads || classed || FileExt.IsMatch(href) && !Regex.IsMatch(href, @"/(?:premium|upgrade|app|extension)\b", RegexOptions.IgnoreCase)) Add(href);
        }
        return list;
    }
    private static void CollectJson(JsonElement e, Action<string?> add, int depth)
    {
        if (depth > 6) return;
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && Regex.IsMatch(p.Name, @"^(url|link|direct_?link|download_?(url|link)|file|redirect)$", RegexOptions.IgnoreCase)) add(p.Value.GetString());
                else CollectJson(p.Value, add, depth + 1);
            }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray().Take(20)) CollectJson(x, add, depth + 1);
    }
}

/// <summary>Builds the POST body of a form: the page's own fields, the submit button, and (when the person did the check in the browser) the one token in the field the site reads. A token is never reused.</summary>
public static class HostPostBuilder
{
    public static IReadOnlyList<KeyValuePair<string, string>> Build(HostForm form, HostCaptcha captcha, string? token, string? submitValueOverride = null)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var f in form.Fields)
        {
            if (f.Name.Equals("g-recaptcha-response", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("h-captcha-response", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("cf-turnstile-response", StringComparison.OrdinalIgnoreCase)) continue;
            // the site's own token box (for example "recaptcha-token") carries the person's token too
            string value = token is not null && Regex.IsMatch(f.Name, @"(?:recaptcha|captcha|turnstile)[-_]?token", RegexOptions.IgnoreCase) ? token : f.Value;
            list.Add(new(f.Name, value));
        }
        if (token is not null && captcha.ResponseField is { } field) list.Add(new(field, token));
        if (form.SubmitName is { Length: > 0 } sn) list.Add(new(sn, submitValueOverride ?? form.SubmitValue ?? form.SubmitLabel ?? ""));
        return list;
    }
    /// <summary>Where the form posts: its action resolved against the page address (an empty action posts to the page itself). Only web addresses.</summary>
    public static Uri? Target(HostForm form, Uri page)
    {
        var action = string.IsNullOrWhiteSpace(form.Action) ? page.AbsoluteUri : form.Action;
        return Uri.TryCreate(page, action, out var u) && u.Scheme is "http" or "https" && u.UserInfo.Length == 0 ? u : null;
    }
}

/// <summary>Small data table of known host quirks: the Referer a host wants, and the least time its own page needs before the first step may be sent. Data only; hosts not listed use the generic defaults.</summary>
public static class HostQuirks
{
    public sealed record Quirk(string Host, string? Referer, int MinWaitSeconds, bool KeyBoundToSite = false);
    private static readonly Quirk[] Table =
    {
        // the page reveals its Free Download button after its own short check (about six seconds, with an eight second failsafe) and refuses a step that arrives earlier
        // KeyBoundToSite: the host's captcha key only works on its own address (checked on the real host: the widget refuses the local 127.0.0.1 page), so the check is done in the NeoDM browser page at the real address
        new("datanodes.to", "https://datanodes.to/users", 8, true),
    };
    public static Quirk? For(string? host) => host is null ? null : Table.FirstOrDefault(q => host.Equals(q.Host, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + q.Host, StringComparison.OrdinalIgnoreCase));
    /// <summary>The Referer for the first request and for a retry after a refusal ("Not allowed from domain"): the table, else the host's own origin.</summary>
    public static string RefererFor(Uri page, bool retry) => !retry && For(page.Host)?.Referer is { } r ? r : page.GetLeftPart(UriPartial.Authority) + "/";
}
