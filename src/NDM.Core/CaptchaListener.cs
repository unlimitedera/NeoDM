// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NDM.Core.Captcha;

namespace NDM.Core;

/// <summary>The words on the small page the person sees in the browser (already translated).</summary>
public sealed record CaptchaPageText(string Title, string Heading, string Body, string Done, string Failed);

/// <summary>What the loopback page should show (1.16.2): the widget kind and key to draw, an optional base address for the page, and, for a key that only
/// works on the site's own address, the site's real page fetched again and laid out under the loopback address ("browser loop").</summary>
public sealed record CaptchaPageSpec(HostCaptchaKind Kind, string? SiteKey, string? Base = null, LoopbackPageProxy? Proxy = null)
{
    public static CaptchaPageSpec For(HostCaptcha captcha) => new(captcha.Kind, captcha.SiteKey);
}

/// <summary>1.16.1: the one-shot loopback page for the human step. It binds to 127.0.0.1 on a random port, serves one page with Google's, hCaptcha's or Cloudflare's own widget script loaded from their own domain,
/// and accepts exactly one POST of the token the PERSON got by clicking in the browser. Path = a random nonce; the Host header must name this exact address; a second post, a wrong nonce or path,
/// or a wrong Host is refused; everything closes after the token or after the expiry (5 minutes).
/// 1.16.2 adds two things to the same page: the page measures the widget and posts the numbers back (so a real mouse can be pointed at it), and the page
/// can be the site's own page served again under this address, with every address it names sent back through here, so an origin-bound key still draws.</summary>
public sealed class CaptchaListener : IDisposable
{
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromMinutes(5);
    public const int MaxAssets = 400;
    private static readonly Regex KeyRx = new(@"^[A-Za-z0-9_\-]{20,80}$", RegexOptions.Compiled), TokenRx = new(@"^[A-Za-z0-9_\-\.~=+/]{10,8192}$", RegexOptions.Compiled);
    private readonly LoopbackHttpServer _server; private readonly CaptchaPageSpec _spec; private readonly CaptchaPageText _text; private readonly string _nonce;
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CaptchaReport?> _placement = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _expiry = new(); private int _used, _assets, _reports;
    public string Url { get; }
    public int Port => _server.Port;
    public string Nonce => _nonce;
    public bool Used => Volatile.Read(ref _used) != 0;
    /// <summary>The token the person produced (null when the listener expired or was closed first).</summary>
    public Task<string?> Token => _result.Task;
    /// <summary>The first widget measurement the page posted (null when it never did).</summary>
    public Task<CaptchaReport?> Placement => _placement.Task;
    /// <summary>The newest widget measurement, refreshed on resize and scroll.</summary>
    public CaptchaReport? Latest { get; private set; }
    /// <summary>Raised for every accepted measurement.</summary>
    public event Action<CaptchaReport>? Placed;
    public int PageServed { get; private set; }
    public int Reports => Volatile.Read(ref _reports);
    /// <summary>True when this listener shows the site's own page instead of a bare widget.</summary>
    public bool Loop => _spec.Proxy is not null;

    public CaptchaListener(HostCaptcha captcha, CaptchaPageText text, TimeSpan? expiry = null)
        : this(CaptchaPageSpec.For(captcha), text, expiry) { }

    public CaptchaListener(CaptchaPageSpec spec, CaptchaPageText text, TimeSpan? expiry = null)
    {
        if (spec.Kind is HostCaptchaKind.None or HostCaptchaKind.Image or HostCaptchaKind.Unsupported) throw new ArgumentException("The browser step cannot carry this check.", nameof(spec));
        if (spec.SiteKey is { } key && !KeyRx.IsMatch(key)) throw new ArgumentException("The browser step cannot carry this key.", nameof(spec));
        if (spec.SiteKey is null && spec.Proxy is null) throw new ArgumentException("The browser step cannot carry this check.", nameof(spec));
        _spec = spec; _text = text; _nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _server = new LoopbackHttpServer(Handle); Url = _server.BaseUrl + "/c/" + _nonce;
        var delay = expiry ?? DefaultExpiry;
        _ = Task.Run(async () => { try { await Task.Delay(delay, _expiry.Token).ConfigureAwait(false); Close(null); } catch (OperationCanceledException) { } });
    }

    private string Prefix => "/c/" + _nonce;
    private bool HostOk(LoopbackRequest r) => string.Equals(r.Header("Host"), "127.0.0.1:" + _server.Port, StringComparison.Ordinal);
    private static LoopbackResponse Plain(int status, string text) => LoopbackResponse.Text(status, "text/plain; charset=utf-8", text, NoSniff);

    private static readonly KeyValuePair<string, string>[] NoSniff = { new("X-Content-Type-Options", "nosniff") };
    private static readonly KeyValuePair<string, string>[] PageHeaders =
    {
        new("Content-Security-Policy", "default-src 'self' https:; script-src 'unsafe-inline' https:; style-src 'unsafe-inline' https:; img-src https: data:; frame-src https:; connect-src 'self'; base-uri 'self' https:; form-action 'none'"),
        new("X-Content-Type-Options", "nosniff"), new("Referrer-Policy", "no-referrer"),
    };

    private LoopbackResponse Handle(LoopbackRequest r)
    {
        if (!HostOk(r)) return Plain(400, "Bad host");
        if (r.Path == Prefix && r.Method == "GET") return Page();
        if (r.Path == Prefix + "/p" && r.Method == "GET") return Asset(r);
        if (r.Path == Prefix + "/r" && r.Method == "POST") return Report(r);
        if (r.Path == Prefix + "/token" && r.Method == "POST") return TokenPost(r, Encoding.UTF8.GetString(r.Body));
        return Plain(404, "Not found");
    }

    // ---- the page ----
    private LoopbackResponse Page()
    {
        if (Used) return Plain(410, "Used");
        PageServed++;
        if (!Loop) return LoopbackResponse.Text(200, "text/html; charset=utf-8", WidgetPage(), PageHeaders);
        string? html;
        try { html = _spec.Proxy!.PageHtmlAsync(AssetPath, _expiry.Token).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or InvalidOperationException) { html = null; }
        if (html is null) return LoopbackResponse.Text(502, "text/html; charset=utf-8", WidgetPage(), PageHeaders); // the site refused: the bare widget is still worth a try
        return LoopbackResponse.Text(200, "text/html; charset=utf-8", Inject(html), NoSniff.Concat(new[] { new KeyValuePair<string, string>("Referrer-Policy", "no-referrer") }).ToArray());
    }

    /// <summary>One address the page named, fetched with the job's own identity and handed back unchanged.</summary>
    private LoopbackResponse Asset(LoopbackRequest r)
    {
        if (_spec.Proxy is null || Interlocked.Increment(ref _assets) > MaxAssets) return Plain(404, "Not found");
        var raw = r.QueryValue("u");
        if (raw is null || !Uri.TryCreate(raw, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0) return Plain(400, "Bad address");
        ProxyFetch? got;
        try { got = _spec.Proxy.AssetAsync(url, _expiry.Token).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or InvalidOperationException) { got = null; }
        return got is null ? Plain(502, "Unavailable") : new LoopbackResponse(200, got.ContentType, got.Body, NoSniff);
    }

    private string AssetPath(Uri url) => _server.BaseUrl + Prefix + "/p?u=" + Uri.EscapeDataString(url.AbsoluteUri);

    // ---- what the page sends back ----
    private LoopbackResponse Report(LoopbackRequest r)
    {
        if (r.Header("Origin") is { } origin && origin != _server.BaseUrl) return Plain(403, "Bad origin");
        var form = r.FormValues();
        if (form.TryGetValue("do", out var kind) && kind.Equals("token", StringComparison.OrdinalIgnoreCase)) return TokenPost(r, "token=" + Uri.EscapeDataString(form.GetValueOrDefault("token") ?? ""));
        if (!"loaded".Equals(form.GetValueOrDefault("do"), StringComparison.OrdinalIgnoreCase)) return Plain(400, "Bad body");
        if (!CaptchaReport.TryParse(form, out var report, out _)) return Plain(400, "Bad numbers");
        Interlocked.Increment(ref _reports);
        Latest = report;
        _placement.TrySetResult(report);
        try { Placed?.Invoke(report); } catch (InvalidOperationException) { }
        return Plain(200, "ok");
    }

    private LoopbackResponse TokenPost(LoopbackRequest r, string body)
    {
        if (r.Header("Origin") is { } origin && origin != _server.BaseUrl) return Plain(403, "Bad origin");
        if (!body.StartsWith("token=", StringComparison.Ordinal)) return Plain(400, "Bad body");
        string token; try { token = Uri.UnescapeDataString(body[6..].Replace("+", "%2B")); } catch (UriFormatException) { return Plain(400, "Bad body"); }
        if (!TokenRx.IsMatch(token)) return Plain(400, "Bad token");
        if (Interlocked.Exchange(ref _used, 1) != 0) return Plain(410, "Used");
        _result.TrySetResult(token); _ = Task.Run(async () => { await Task.Delay(1500).ConfigureAwait(false); Dispose(); }); // the reply goes out first, then everything closes
        return Plain(200, "ok");
    }

    // ---- markup ----
    private (string Script, string Class) Widget() => _spec.Kind switch
    {
        HostCaptchaKind.HCaptcha => ("https://js.hcaptcha.com/1/api.js", "h-captcha"),
        HostCaptchaKind.Turnstile => ("https://challenges.cloudflare.com/turnstile/v0/api.js", "cf-turnstile"),
        _ => ("https://www.google.com/recaptcha/api.js", "g-recaptcha"),
    };

    /// <summary>The bare page: one widget, the message, and the reporting script.</summary>
    private string WidgetPage()
    {
        string E(string s) => WebUtility.HtmlEncode(s);
        var (script, css) = Widget();
        var baseTag = string.IsNullOrWhiteSpace(_spec.Base) ? "" : "<base href=\"" + E(_spec.Base) + "\">";
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" + baseTag + "<title>" + E(_text.Title) + "</title><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<style>body{font-family:Segoe UI,Arial,sans-serif;margin:0;background:#f4f6fb;color:#1c2333}main{max-width:520px;margin:12vh auto;background:#fff;padding:28px 32px;border-radius:12px;box-shadow:0 2px 14px #0002}h1{font-size:20px;margin:0 0 8px}p{line-height:1.5}#done{display:none;font-weight:600;color:#0a7a3d}#fail{display:none;font-weight:600;color:#b3261e}</style></head><body><main>"
            + "<h1>" + E(_text.Heading) + "</h1><p>" + E(_text.Body) + "</p>"
            + "<div class=\"" + css + "\" data-sitekey=\"" + E(_spec.SiteKey ?? "") + "\" data-callback=\"neodmDone\"></div>"
            + "<p id=\"done\">" + E(_text.Done) + "</p><p id=\"fail\">" + E(_text.Failed) + "</p>"
            + "<script src=\"" + script + "\" async defer></script>"
            + Script()
            + "</main></body></html>";
    }

    /// <summary>The site's own page with the reporting bar and script added at the end of the body.</summary>
    private string Inject(string html)
    {
        string E(string s) => WebUtility.HtmlEncode(s);
        var bar = "<div id=\"neodm\" style=\"position:fixed;left:0;right:0;bottom:0;z-index:2147483647;background:#fffffff2;border-top:1px solid #ccd;font:13px Segoe UI,Arial,sans-serif;padding:6px 10px;color:#1c2333\">"
            + "<span id=\"neodmhead\">" + E(_text.Heading) + "</span> <span id=\"done\" style=\"display:none;color:#0a7a3d;font-weight:600\">" + E(_text.Done) + "</span>"
            + "<span id=\"fail\" style=\"display:none;color:#b3261e;font-weight:600\">" + E(_text.Failed) + "</span></div>";
        var extra = bar + Script();
        int close = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return close >= 0 ? html.Insert(close, extra) : html + extra;
    }

    /// <summary>The script: it only measures its own page and reads the token, then posts both back here. It never presses the widget and never runs the site's own submit.</summary>
    private string Script() => "<script>(function(){var R=" + Js(Prefix) + ",sent=false;"
        + "function box(){var e=document.querySelector('.cf-turnstile,.g-recaptcha,.h-captcha,[data-sitekey]');if(e){var r=e.getBoundingClientRect();if(r.width>2&&r.height>2)return r}"
        + "var f=document.querySelector('iframe[src*=\"challenges.cloudflare.com\"],iframe[src*=\"recaptcha/api2\"],iframe[src*=\"hcaptcha.com\"]');if(f)return f.getBoundingClientRect();return null}"
        + "function post(b){return fetch(R+'/r',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:b})}"
        + "function n(v){return Math.round(v)}"
        + "function show(ok){var e=document.getElementById(ok?'done':'fail');if(e)e.style.display='inline'}"
        + "function report(){var b=box();if(!b)return false;"
        + "post('do=loaded&x='+n(b.left)+'&y='+n(b.top)+'&w='+n(b.width)+'&h='+n(b.height)+'&vw='+n(window.innerWidth)+'&vh='+n(window.innerHeight)+'&dpr='+(window.devicePixelRatio||1)"
        + "+'&eleft='+n(window.screenX)+'&etop='+n(window.screenY)+'&ew='+n(window.outerWidth)+'&eh='+n(window.outerHeight)).catch(function(){});return true}"
        + "var tries=0,t=setInterval(function(){tries++;if(report()||tries>80)clearInterval(t)},250);"
        + "window.addEventListener('resize',report);window.addEventListener('scroll',report,true);"
        + "function field(){return document.querySelector('[name=\"cf-turnstile-response\"],[name=\"h-captcha-response\"],textarea[name=\"g-recaptcha-response\"],#g-recaptcha-response')}"
        + "function take(){if(sent)return true;var f=field();var v=f&&(f.value!==undefined?f.value:f.textContent);if(!v||v.length<10)return false;sent=true;"
        + "post('do=token&token='+encodeURIComponent(v)).then(function(r){show(r.ok)},function(){show(false)});return true}"
        + "var p=setInterval(function(){if(take())clearInterval(p)},200);"
        + "window.neodmDone=function(){setTimeout(take,50)};"
        + "})()</script>";

    private static string Js(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private void Close(string? token) { _result.TrySetResult(token); Dispose(); }
    public void Dispose() { try { _expiry.Cancel(); } catch (ObjectDisposedException) { } _server.Dispose(); _result.TrySetResult(null); _placement.TrySetResult(null); }
}
