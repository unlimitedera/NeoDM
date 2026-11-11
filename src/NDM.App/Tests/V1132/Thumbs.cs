using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;

internal static partial class V1132Tests
{
    /// <summary>Stands in for a slow path to the picture host (a proxy that takes seconds): each request waits <c>Delay(call number)</c> before answering, and honours cancellation like a real connection.</summary>
    private sealed class SlowHost : HttpMessageHandler
    {
        public int Calls; public Func<int, TimeSpan> Delay = _ => TimeSpan.Zero; public byte[] Image = Array.Empty<byte>();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref Calls);
            await Task.Delay(Delay(call), cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Image) };
        }
    }
    private static byte[] Jpeg()
    {
        var pixels = new byte[32 * 18 * 3]; for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
        var enc = new JpegBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 18, 96, 96, PixelFormats.Rgb24, null, pixels, 32 * 3))); using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
    }

    // ------------------------------------------------------------------ item 2: pictures that ended as "cancelled"
    // Cause: the loader's HttpClient had a 15 s Timeout, and a timeout surfaces as TaskCanceledException, which the loader's
    // `catch (OperationCanceledException)` logged as "cancelled" and gave up on at once (no retry). Through a slow proxy every picture ended that way.
    private static void SlowThumbnails(string root)
    {
        var jpeg = Jpeg(); const string id = "dQw4w9WgXcQ"; const string url = "https://i.ytimg.com/vi/" + id + "/mqdefault.jpg"; int n = 0;
        string State() { var p = Path.Combine(root, "t-" + n++); Directory.CreateDirectory(p); return p; }
        string Log(string state) { var f = Path.Combine(state, "thumbs", "trace.log"); return File.Exists(f) ? File.ReadAllText(f) : ""; }

        // 1. a fast direct connection: loads, one request
        var fast = new SlowHost { Image = jpeg }; var s1 = State();
        using (var l = new ThumbnailLoader(s1, null, fast) { RequestTimeout = TimeSpan.FromSeconds(2) })
            Check(Sync(l.LoadAsync(id, url)) is { IsFrozen: true } && fast.Calls == 1 && Log(s1).Contains(" ok "), "fast connection: the picture decodes after one request");

        // 2. the first attempt is slower than the limit (a slow proxy): the retry succeeds, nothing is logged as cancelled
        var slowFirst = new SlowHost { Image = jpeg, Delay = c => c == 1 ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(50) }; var s2 = State();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var l = new ThumbnailLoader(s2, null, slowFirst) { RequestTimeout = TimeSpan.FromMilliseconds(400) })
        {
            Check(Sync(l.LoadAsync(id, url)) is { IsFrozen: true }, "a slow first attempt is retried and the picture decodes");
            Check(slowFirst.Calls == 2 && sw.Elapsed < TimeSpan.FromSeconds(4), $"one retry, no waiting for the slow request ({sw.ElapsedMilliseconds} ms)");
            Check(!Log(s2).Contains("cancelled"), "a timeout is never logged as cancelled");
        }

        // 3. every attempt is too slow: gives up with a trace that says timeout (and how often), not cancelled
        var dead = new SlowHost { Image = jpeg, Delay = _ => TimeSpan.FromSeconds(5) }; var s3 = State();
        using (var l = new ThumbnailLoader(s3, null, dead) { RequestTimeout = TimeSpan.FromMilliseconds(300) })
        {
            Check(Sync(l.LoadAsync(id, url)) is null, "a connection that never answers gives the placeholder");
            var log = Log(s3);
            Check(log.Contains("timeout after 2 tries") && !log.Contains("cancelled") && dead.Calls == 6, "the trace says timeout after 2 tries for each of the 3 picture sizes (" + dead.Calls + " requests)");
        }

        // 4. cancelled by the UI: the row scrolled out, then the window closed -> the two reasons are told apart
        var wait = new SlowHost { Image = jpeg, Delay = _ => TimeSpan.FromSeconds(5) }; var s4 = State();
        using (var l = new ThumbnailLoader(s4, null, wait) { RequestTimeout = TimeSpan.FromSeconds(30) })
        {
            using var row = new CancellationTokenSource();
            var task = l.LoadAsync(id, url, row.Token); Pump(200); row.Cancel();
            Check(Sync(task) is null && Log(s4).Contains("cancelled-by-ui (row left the view)"), "scrolling the row out cancels it, logged as cancelled-by-ui (row left the view)");
            var task2 = l.LoadAsync("aaaaaaaaaaa", "https://i.ytimg.com/vi/aaaaaaaaaaa/mqdefault.jpg"); Pump(200); l.Cancel();
            Check(Sync(task2) is null && Log(s4).Contains("cancelled-by-ui (window closed)"), "closing the window cancels it, logged as cancelled-by-ui (window closed)");
        }

        // 5. the proxy is named in the trace when one carries the request
        var s5 = State();
        using (var l = new ThumbnailLoader(s5, () => new NDM.Core.ProxyOptions { Kind = NDM.Core.ProxyKind.Http, Host = "127.0.0.1", Port = 9 }, new SlowHost { Image = jpeg, Delay = _ => TimeSpan.FromSeconds(5) }) { RequestTimeout = TimeSpan.FromMilliseconds(200) })
        {
            Sync(l.LoadAsync(id, url));
            Check(Log(s5).Contains("via proxy 127.0.0.1:9"), "a timeout through a proxy names the proxy");
        }
    }
}
