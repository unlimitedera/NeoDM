using System.Net;
using System.Net.Http;
using NDM.Core.Captcha;

internal static partial class V1162Tests
{
    private static readonly Uri PageUrl = new("https://host.example/d/abc123");

    private sealed class Link : Dictionary<string, string>
    {
        public string Map(Uri url) { var key = "/c/N/p?u=" + Uri.EscapeDataString(url.AbsoluteUri); this[url.AbsoluteUri] = key; return key; }
    }

    /// <summary>The page is laid out again for the loopback address: every address goes back through the server, and what the site's own scripts need is kept.</summary>
    private static void Rewrite()
    {
        var html = "<!DOCTYPE html><html><head><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'self'\"><link rel=\"stylesheet\" href=\"/theme/app.css\">"
            + "<script src=\"https://cdn.example/x.js\" integrity=\"sha384-abc\" crossorigin=\"anonymous\"></script></head>"
            + "<body><form action=\"/dl/2\" method=\"post\"><input type=\"hidden\" name=\"op\" value=\"download2\">"
            + "<a href=\"#top\">top</a><a href=\"javascript:void(0)\">js</a><img src=\"data:image/png;base64,AA\">"
            + "<a href=\"//other.example/x\">other</a><a href=\"next\">next</a><iframe src=\"https://challenges.cloudflare.com/turnstile/v0/api.html?k=1\"></iframe>"
            + "<script>var countdown=5;</script></form></body></html>";
        var link = new Link();
        var result = PageRewriter.Rewrite(html, PageUrl, link.Map);
        Check(link.ContainsKey("https://host.example/theme/app.css") && result.Contains("href=\"" + WebUtility.HtmlEncode(link["https://host.example/theme/app.css"]) + "\""), "a root relative style is pointed at the loopback server");
        Check(link.ContainsKey("https://cdn.example/x.js") && result.Contains(link["https://cdn.example/x.js"]), "an absolute script is pointed at the loopback server");
        Check(!result.Contains("integrity="), "the integrity value is dropped (the bytes now come from the server)");
        Check(result.Contains("crossorigin"), "crossorigin is left alone");
        Check(!result.Contains("Content-Security-Policy") && !result.Contains("http-equiv"), "the site's own policy tag is removed so the loopback page may run");
        Check(result.Contains("<base href=\"" + PageUrl.AbsoluteUri + "\">"), "a base tag names the real page");
        Check(result.IndexOf("<base ", StringComparison.Ordinal) < result.IndexOf("stylesheet", StringComparison.Ordinal), "the base tag comes before the first address is used");
        Check(result.Contains("<script>var countdown=5;</script>") && result.Contains("</script>"), "the site's own inline scripts are kept");
        Check(link.ContainsKey("https://host.example/dl/2") && result.Contains("action=\"" + WebUtility.HtmlEncode(link["https://host.example/dl/2"]) + "\""), "a form action is pointed at the loopback server");
        Check(result.Contains("href=\"#top\""), "a same page fragment is left alone");
        Check(result.Contains("href=\"javascript:void(0)\""), "a script address is left alone");
        Check(result.Contains("src=\"data:image/png;base64,AA\""), "an inline image is left alone");
        Check(link.ContainsKey("https://other.example/x"), "a scheme relative address is read as https");
        Check(link.ContainsKey("https://host.example/d/next"), "a plain relative address is resolved against the page");
        Check(link.ContainsKey("https://challenges.cloudflare.com/turnstile/v0/api.html?k=1"), "the widget frame is pointed at the loopback server too");
        Check(result.Contains("name=\"op\" value=\"download2\""), "hidden fields are untouched");
        Check(result.StartsWith("<!DOCTYPE html><html><head>", StringComparison.Ordinal), "the page shape is kept");
        Check(!result.Contains("src=\"next\""), "the rewritten value replaces the old one");
        var noHead = PageRewriter.Rewrite("<body><img src=\"a.png\"></body>", PageUrl, link.Map);
        Check(noHead.StartsWith("<base href=", StringComparison.Ordinal), "a page with no head gets the base tag at the front");
        Check(PageRewriter.Rewrite("", PageUrl, link.Map) == "", "an empty page stays empty");
        Check(!PageRewriter.Rewrite("<a href=\"mailto:a@b.c\">m</a>", PageUrl, link.Map).Contains("u="), "a mail address is left alone");
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Seen = new();
        public HttpStatusCode Status = HttpStatusCode.OK;
        public byte[] Body = "hello"u8.ToArray();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Seen.Add(request);
            var response = new HttpResponseMessage(Status) { Content = new ByteArrayContent(Body), RequestMessage = request };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/css");
            return Task.FromResult(response);
        }
    }

    /// <summary>The fetcher sends the job's cookies and user agent on every address, so a rewritten request leaves with the same identity the page had.</summary>
    private static void Cookies()
    {
        var recorder = new Recorder();
        using var source = new HttpPageSource("sid=abc123; other=1", "TestAgent/1.0", null, new HttpClient(recorder));
        var page = source.GetAsync(PageUrl, CancellationToken.None).GetAwaiter().GetResult();
        Check(page is not null && page.Body.Length == 5 && page.ContentType.StartsWith("text/css"), "a good answer is passed through with its type");
        Check(recorder.Seen.Count == 1 && recorder.Seen[0].Headers.GetValues("Cookie").Single() == "sid=abc123; other=1", "the job's cookies go with the request");
        Check(recorder.Seen[0].Headers.GetValues("User-Agent").Single() == "TestAgent/1.0", "the job's user agent goes with the request");
        Check(source.GetAsync(new Uri("https://cdn.example/x.js"), CancellationToken.None).GetAwaiter().GetResult() is not null && recorder.Seen[1].Headers.Contains("Cookie"), "an asset request carries the cookies too");
        recorder.Status = HttpStatusCode.NotFound;
        Check(source.GetAsync(PageUrl, CancellationToken.None).GetAwaiter().GetResult() is null, "a refused address gives null");
        recorder.Status = HttpStatusCode.OK;
        Check(source.GetAsync(new Uri("ftp://host.example/x"), CancellationToken.None).GetAwaiter().GetResult() is null, "a non web address is never fetched");
        Check(source.GetAsync(new Uri("https://user:pw@host.example/x"), CancellationToken.None).GetAwaiter().GetResult() is null, "an address with a password is skipped");
        using var bare = new HttpPageSource(null, null, null, new HttpClient(new Recorder()));
        Check(bare.GetAsync(PageUrl, CancellationToken.None).GetAwaiter().GetResult() is not null, "no cookies and no user agent still fetches");
    }
}
