using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NDM.Core;

/// <summary>The words on the small page the person sees in the browser (already translated).</summary>
public sealed record CaptchaPageText(string Title, string Heading, string Body, string Done, string Failed);

/// <summary>1.16.1: the one-shot loopback page for the human step. It binds to 127.0.0.1 on a random port, serves one page with Google's, hCaptcha's or Cloudflare's own widget script loaded from their own domain,
/// and accepts exactly one POST of the token the PERSON got by clicking in the browser. Path = a random nonce; the Host header must name this exact address; a second post, a wrong nonce or path,
/// or a wrong Host is refused; everything closes after the token or after the expiry (5 minutes). NeoDM does not click, solve or script anything on the widget.</summary>
public sealed class CaptchaListener : IDisposable
{
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromMinutes(5);
    private static readonly Regex KeyRx = new(@"^[A-Za-z0-9_\-]{20,80}$", RegexOptions.Compiled), TokenRx = new(@"^[A-Za-z0-9_\-\.~=+/]{10,4096}$", RegexOptions.Compiled);
    private readonly LoopbackHttpServer _server; private readonly HostCaptcha _captcha; private readonly CaptchaPageText _text; private readonly string _nonce;
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _expiry = new(); private int _used;
    public string Url { get; }
    public int Port => _server.Port;
    public string Nonce => _nonce;
    public bool Used => Volatile.Read(ref _used) != 0;
    /// <summary>The token the person produced (null when the listener expired or was closed first).</summary>
    public Task<string?> Token => _result.Task;
    public int PageServed { get; private set; }

    public CaptchaListener(HostCaptcha captcha, CaptchaPageText text, TimeSpan? expiry = null)
    {
        if (!captcha.BrowserStepCan || captcha.SiteKey is null || !KeyRx.IsMatch(captcha.SiteKey)) throw new ArgumentException("The browser step cannot carry this check.", nameof(captcha));
        _captcha = captcha; _text = text; _nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _server = new LoopbackHttpServer(Handle); Url = _server.BaseUrl + "/c/" + _nonce;
        var delay = expiry ?? DefaultExpiry;
        _ = Task.Run(async () => { try { await Task.Delay(delay, _expiry.Token).ConfigureAwait(false); Close(null); } catch (OperationCanceledException) { } });
    }

    private bool HostOk(LoopbackRequest r) => string.Equals(r.Header("Host"), "127.0.0.1:" + _server.Port, StringComparison.Ordinal);
    private static LoopbackResponse Plain(int status, string text) => LoopbackResponse.Text(status, "text/plain; charset=utf-8", text, new[] { new KeyValuePair<string, string>("X-Content-Type-Options", "nosniff") });

    private LoopbackResponse Handle(LoopbackRequest r)
    {
        if (!HostOk(r)) return Plain(400, "Bad host");
        if (r.Path == "/c/" + _nonce && r.Method == "GET")
        {
            if (Used) return Plain(410, "Used");
            PageServed++; return LoopbackResponse.Text(200, "text/html; charset=utf-8", Page(), new[]
            {
                new KeyValuePair<string, string>("Content-Security-Policy", "default-src 'self' https:; script-src 'unsafe-inline' https:; style-src 'unsafe-inline' https:; img-src https: data:; frame-src https:; connect-src 'self'; base-uri 'none'; form-action 'none'"),
                new KeyValuePair<string, string>("X-Content-Type-Options", "nosniff"), new KeyValuePair<string, string>("Referrer-Policy", "no-referrer"),
            });
        }
        if (r.Path == "/c/" + _nonce + "/token" && r.Method == "POST")
        {
            if (r.Header("Origin") is { } origin && origin != "http://127.0.0.1:" + _server.Port) return Plain(403, "Bad origin");
            var text = Encoding.UTF8.GetString(r.Body);
            if (!text.StartsWith("token=", StringComparison.Ordinal)) return Plain(400, "Bad body");
            string token; try { token = Uri.UnescapeDataString(text[6..].Replace("+", "%2B")); } catch (UriFormatException) { return Plain(400, "Bad body"); }
            if (!TokenRx.IsMatch(token)) return Plain(400, "Bad token");
            if (Interlocked.Exchange(ref _used, 1) != 0) return Plain(410, "Used");
            _result.TrySetResult(token); _ = Task.Run(async () => { await Task.Delay(1500).ConfigureAwait(false); Dispose(); }); // the reply goes out first, then everything closes
            return Plain(200, "ok");
        }
        return Plain(404, "Not found");
    }

    private string Page()
    {
        var (script, css) = _captcha.Kind switch
        {
            HostCaptchaKind.HCaptcha => ("https://js.hcaptcha.com/1/api.js", "h-captcha"),
            HostCaptchaKind.Turnstile => ("https://challenges.cloudflare.com/turnstile/v0/api.js", "cf-turnstile"),
            _ => ("https://www.google.com/recaptcha/api.js", "g-recaptcha"),
        };
        string E(string s) => WebUtility.HtmlEncode(s);
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>" + E(_text.Title) + "</title><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<style>body{font-family:Segoe UI,Arial,sans-serif;margin:0;background:#f4f6fb;color:#1c2333}main{max-width:520px;margin:12vh auto;background:#fff;padding:28px 32px;border-radius:12px;box-shadow:0 2px 14px #0002}h1{font-size:20px;margin:0 0 8px}p{line-height:1.5}#done{display:none;font-weight:600;color:#0a7a3d}#fail{display:none;font-weight:600;color:#b3261e}</style></head><body><main>"
            + "<h1>" + E(_text.Heading) + "</h1><p>" + E(_text.Body) + "</p>"
            + "<div class=\"" + css + "\" data-sitekey=\"" + _captcha.SiteKey + "\" data-callback=\"neodmDone\"></div>"
            + "<p id=\"done\">" + E(_text.Done) + "</p><p id=\"fail\">" + E(_text.Failed) + "</p>"
            + "<script src=\"" + script + "\" async defer></script>"
            + "<script>function neodmDone(t){fetch('/c/" + _nonce + "/token',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:'token='+encodeURIComponent(t)}).then(function(r){document.getElementById(r.ok?'done':'fail').style.display='block'}).catch(function(){document.getElementById('fail').style.display='block'})}</script>"
            + "</main></body></html>";
    }

    private void Close(string? token) { _result.TrySetResult(token); Dispose(); }
    public void Dispose() { try { _expiry.Cancel(); } catch (ObjectDisposedException) { } _server.Dispose(); _result.TrySetResult(null); }
}
