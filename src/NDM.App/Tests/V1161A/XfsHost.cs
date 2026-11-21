// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Collections.Concurrent;
using System.Text;
using NDM.App;
using NDM.Core;

/// <summary>A fixture host in the common form-based shape: page with a form (step 1), second page with the timer and an optional captcha (step 2), a JSON reply with the link, then the file.
/// The stand-in "person" gives the token through <see cref="IssueToken"/>; the host accepts each issued token once.</summary>
internal sealed class XfsHost : IDisposable
{
    private readonly LoopbackHttpServer _server;
    private readonly ConcurrentDictionary<string, bool> _tokens = new();
    public bool Captcha = true, RefuseUnlessOrigin = false, AlwaysRefuse = false, DirectFileAtPage = false, ImageCaptcha = false, NotAForm = false;
    public int Countdown = 5;
    public readonly ConcurrentQueue<string> Log = new();
    public readonly ConcurrentQueue<(string Path, string? Referer, bool HasCookie, string? UserAgent)> Seen = new();
    public string BaseUrl => _server.BaseUrl;
    public int Port => _server.Port;
    public XfsHost() { _server = new LoopbackHttpServer(Handle); }
    public string IssueToken() { var t = "OK-" + Guid.NewGuid().ToString("N"); _tokens[t] = false; return t; }
    public string FileUrl(string id, string name = "Part.zip") => $"{BaseUrl}/{id}/{name}";
    private static Dictionary<string, string> Form(byte[] body) => Encoding.UTF8.GetString(body).Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");

    private LoopbackResponse Handle(LoopbackRequest r)
    {
        bool cookie = (r.Header("Cookie") ?? "").Contains("file_code=");
        Seen.Enqueue((r.Path, r.Header("Referer"), cookie, r.Header("User-Agent")));
        if (r.Path.StartsWith("/files/"))
        {
            if (!cookie) return LoopbackResponse.Text(403, "text/plain", "no cookie");
            var body = new byte[1000]; var h = new[] { new KeyValuePair<string, string>("Content-Disposition", "attachment; filename=\"" + Path.GetFileName(r.Path) + "\"") };
            if (r.Header("Range") is not null) return new(206, "application/octet-stream", new byte[1], h.Append(new("Content-Range", "bytes 0-0/1000")).ToList());
            return new(200, "application/octet-stream", body, h);
        }
        var origin = BaseUrl + "/"; var referer = r.Header("Referer") ?? "";
        if (AlwaysRefuse || RefuseUnlessOrigin && !referer.StartsWith(origin)) return LoopbackResponse.Text(200, "text/html", "<html><body><div class=\"err\">Not allowed from domain you're coming from</div></body></html>");
        var id = r.Path.Trim('/').Split('/')[0];
        if (r.Method is "GET" or "HEAD")
        {
            if (DirectFileAtPage) return new(200, "application/octet-stream", new byte[10], new[] { new KeyValuePair<string, string>("Content-Disposition", "attachment; filename=\"direct.bin\"") });
            if (NotAForm) return LoopbackResponse.Text(200, "text/html", "<html><body><h1>Welcome</h1><a href=\"/x\">home</a></body></html>");
            return LoopbackResponse.Text(200, "text/html", $"<html><body><form method=\"POST\" action=\"\"><input type=\"hidden\" name=\"op\" value=\"download1\"><input type=\"hidden\" name=\"id\" value=\"{id}\"><input type=\"hidden\" name=\"referer\" value=\"\"><button type=\"submit\" name=\"method_free\" value=\"Free Download &gt;&gt;\" disabled>Continue to Download</button></form></body></html>",
                new[] { new KeyValuePair<string, string>("Set-Cookie", "file_code=" + id + "; Path=/") });
        }
        var f = Form(r.Body);
        if (!cookie) return LoopbackResponse.Text(200, "text/html", "<html><body><div class=\"err\">Session expired</div></body></html>");
        if (f.GetValueOrDefault("op") == "download1")
        {
            var captcha = ImageCaptcha ? "<img src=\"/captchas/1.jpg\"><input name=\"captcha_code\">" : Captcha ? "<div class=\"g-recaptcha\" data-sitekey=\"6LfFIXTUREFIXTUREFIXTUREFIXTUREFIXTUREab\"></div>" : "";
            return LoopbackResponse.Text(200, "text/html", $"<html><body><p>Please wait <span id=\"countdown\">{Countdown}</span> seconds</p><form method=\"POST\" action=\"\"><input type=\"hidden\" name=\"op\" value=\"download2\"><input type=\"hidden\" name=\"id\" value=\"{id}\"><input type=\"hidden\" name=\"rand\" value=\"abc123\"><input type=\"hidden\" name=\"referer\" value=\"\">{captcha}<button type=\"submit\" name=\"method_free\" value=\"Free Download\">Free Download</button></form></body></html>");
        }
        if (f.GetValueOrDefault("op") == "download2")
        {
            if (Captcha)
            {
                var t = f.GetValueOrDefault("g-recaptcha-response") ?? "";
                if (t.Length == 0 || !_tokens.TryGetValue(t, out var used) || used) return LoopbackResponse.Text(200, "text/html", "<html><body><div class=\"err\">Wrong captcha</div></body></html>");
                _tokens[t] = true;
            }
            return LoopbackResponse.Text(200, "application/json", "{\"status\":\"ok\",\"url\":\"" + BaseUrl.Replace("/", "\\/") + "\\/files\\/" + id + "\\/" + "Part.zip\"}");
        }
        return LoopbackResponse.Text(400, "text/plain", "bad op");
    }
    public void Dispose() => _server.Dispose();
}

/// <summary>Stand-in for the person: gives a token the host issued (the way a human click in the browser would), or waits, or skips.</summary>
internal sealed class StandInPerson : IHumanCaptchaStep
{
    public XfsHost Host = null!; public string Mode = "token"; public int Calls; public readonly List<string> Tokens = new(); public Func<Action>? OnWaiting; public TaskCompletionSource<string>? Gate; public string? Fixed;
    public async Task<CaptchaOutcome> RunAsync(HostCaptcha captcha, Uri page, CancellationToken token, Func<bool> skip)
    {
        Calls++;
        switch (Mode)
        {
            case "skip": return CaptchaOutcome.Skip;
            case "timeout": return CaptchaOutcome.Timeout;
            case "gate": { var t = await Gate!.Task.WaitAsync(token); Tokens.Add(t); return CaptchaOutcome.Got(t); }
            case "fixed": Tokens.Add(Fixed!); return CaptchaOutcome.Got(Fixed!);
            default: { var t = Host.IssueToken(); Tokens.Add(t); return CaptchaOutcome.Got(t); }
        }
    }
}
