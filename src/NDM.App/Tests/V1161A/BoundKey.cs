// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Net;
using System.Net.Http;
using NDM.App;
using NDM.Core;

internal static partial class V1161ATests
{
    private sealed class FixtureHandler : HttpMessageHandler
    {
        public readonly List<string> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            var html = request.Method == HttpMethod.Get ? Fx("datanodes-download1.html") : Fx("datanodes-download2.html");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") });
        }
    }

    private static void BoundKey()
    {
        // the pilot host's two real pages (saved copies): step 1 waits the host's own time and is posted, step 2 shows Turnstile whose key only works on the host's own address
        var clock = new FakeClock(); var rec = new Recorder(); var handler = new FixtureHandler();
        var engine = new HostFormEngine { Delay = clock.Delay, Captcha = rec, HandlerFactory = _ => handler };
        var item = Item("https://datanodes.to/li77lxy835bq/Part_1.zip", "Part_1.zip");
        var r = Run(engine, item);
        Check(r.Unrecognised && rec.Calls == 0, "host with a site-bound captcha key: the browser step is not used, the NeoDM browser page takes over");
        Check(handler.Seen.SequenceEqual(new[] { "GET /li77lxy835bq/Part_1.zip", "POST /li77lxy835bq/Part_1.zip" }), "the step-1 form was posted once, nothing else was requested");
        Check(clock.Seconds == 13, "the site's own timers were both waited in full (8 s for step 1, 5 s stated on step 2)");
    }
}
