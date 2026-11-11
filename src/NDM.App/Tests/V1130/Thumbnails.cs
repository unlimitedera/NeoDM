using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.Core;

internal static partial class V1130Tests
{
    // ------------------------------------------------------------------ item 7: thumbnails (a stand-in image server answers per address; the real picture hosts are only reached by --real-playlist)
    private sealed class ImageHost : HttpMessageHandler
    {
        public readonly List<string> Urls = new(); public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => new(HttpStatusCode.NotFound);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { lock (Urls) Urls.Add(request.RequestUri!.ToString()); return Task.FromResult(Reply(request)); }
    }
    private static byte[] Jpeg()
    {
        var pixels = new byte[32 * 18 * 3]; for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
        var enc = new JpegBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 18, 96, 96, PixelFormats.Rgb24, null, pixels, 32 * 3))); using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
    }
    private static byte[] FakeWebp() => Enumerable.Range(0, 64).Select(i => (byte)0).ToArray().Select((b, i) => i switch { 0 => (byte)'R', 1 => (byte)'I', 2 => (byte)'F', 3 => (byte)'F', 8 => (byte)'W', 9 => (byte)'E', 10 => (byte)'B', 11 => (byte)'P', _ => b }).ToArray();
    private const string VideoOne = "dQw4w9WgXcQ";
    private const string Signed = "https://i.ytimg.com/vi/" + VideoOne + "/hqdefault.jpg?sqp=-oaymwEcCNACELwBSFXyq4qpAw4IARUAAIhCGAFwAcABBg==&rs=AOn4CLDIUMo6wmNwCk7_4gaFgstLo8XRVQ";

    private static void ThumbnailCases(string scratch)
    {
        var jpeg = Jpeg(); int n = 0; string State() { var p = Path.Combine(scratch, "t13-" + n++); Directory.CreateDirectory(p); return p; }
        ImageSource? Load(ThumbnailLoader l, string id, string url) { var t = l.LoadAsync(id, url); PumpUntil(() => t.IsCompleted, "thumbnail finished", 20000); return t.Result; }
        // candidate order
        var c1 = ThumbnailLoader.Candidates(VideoOne, Signed);
        Check(c1.SequenceEqual(new[] { "https://i.ytimg.com/vi/" + VideoOne + "/mqdefault.jpg", "https://i.ytimg.com/vi/" + VideoOne + "/hqdefault.jpg", "https://i.ytimg.com/vi/" + VideoOne + "/default.jpg" }), "a signed listed address gives the plain medium, high and default addresses (the signed one answers with WebP)");
        var c2 = ThumbnailLoader.Candidates(VideoOne, "https://i.ytimg.com/vi/" + VideoOne + "/maxresdefault.jpg");
        Check(c2.Count == 4 && c2[0].EndsWith("/maxresdefault.jpg") && c2[1].EndsWith("/mqdefault.jpg"), "a plain listed size is tried first, the others follow");
        Check(ThumbnailLoader.Candidates(VideoOne, "https://i9.ytimg.com/vi/" + VideoOne + "/mqdefault.jpg").All(u => u.StartsWith("https://i9.ytimg.com/vi/")), "the listed (allowed) host is kept");
        Check(ThumbnailLoader.Candidates(VideoOne, "").All(u => u.StartsWith("https://i.ytimg.com/vi/") && u.EndsWith(".jpg")), "no listed address: the standard host and plain addresses");
        Check(!YoutubePlaylists.IsThumbnailUrl("https://i.ytimg.com/vi/x/a.jpg\n") && YoutubePlaylists.IsThumbnailUrl("https://i9.ytimg.com/vi_webp/x/a.webp") && YoutubePlaylists.IsThumbnailUrl("https://img.youtube.com/vi/x/0.jpg") && !YoutubePlaylists.IsThumbnailUrl("https://yt3.ggpht.com/a") && !YoutubePlaylists.IsThumbnailUrl("http://i.ytimg.com/vi/x/a.jpg"), "the host list stays strict: https only, three YouTube picture hosts");
        // 1. the signed address is never requested; the plain medium picture is
        var h = new ImageHost { Reply = r => r.RequestUri!.AbsolutePath.EndsWith("mqdefault.jpg") ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) } : new(HttpStatusCode.NotFound) };
        var trace = new List<string>();
        using (var l = new ThumbnailLoader(State(), null, h) { Trace = (id, url, outcome) => trace.Add(outcome + " " + url) })
        {
            Check(Load(l, VideoOne, Signed) is { IsFrozen: true }, "a listed (signed) address still gives a picture");
            Check(h.Urls.Count == 1 && !h.Urls[0].Contains('?') && h.Urls[0].EndsWith("/mqdefault.jpg"), "one request, to the plain address: " + string.Join(",", h.Urls));
            Check(trace.SequenceEqual(new[] { "ok https://i.ytimg.com/vi/" + VideoOne + "/mqdefault.jpg" }), "the trace names the address and 'ok'");
        }
        // 2. the medium picture is missing: the next size is used
        h = new ImageHost { Reply = r => r.RequestUri!.AbsolutePath.EndsWith("hqdefault.jpg") ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) } : new(HttpStatusCode.NotFound) }; trace.Clear();
        using (var l = new ThumbnailLoader(State(), null, h) { Trace = (id, url, outcome) => trace.Add(outcome) })
        {
            Check(Load(l, VideoOne, Signed) is not null && h.Urls.Count == 2, "a missing size falls back to the next one");
            Check(trace.SequenceEqual(new[] { "status 404", "ok" }), "the trace says why: " + string.Join(",", trace));
        }
        // 3. a missing maximum size listed plainly falls back too
        h = new ImageHost { Reply = r => r.RequestUri!.AbsolutePath.EndsWith("/mqdefault.jpg") ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) } : new(HttpStatusCode.NotFound) };
        using (var l = new ThumbnailLoader(State(), null, h)) { Check(Load(l, VideoOne, "https://i.ytimg.com/vi/" + VideoOne + "/maxresdefault.jpg") is not null && h.Urls.Count == 2 && h.Urls[0].EndsWith("maxresdefault.jpg"), "maxresdefault missing: medium size is used"); }
        // 4. nothing exists: three tries, then a placeholder (null); the reasons are in the trace
        h = new ImageHost(); trace.Clear();
        using (var l = new ThumbnailLoader(State(), null, h) { Trace = (id, url, outcome) => trace.Add(outcome) }) { Check(Load(l, VideoOne, Signed) is null && h.Urls.Count == 3 && trace.All(t => t == "status 404"), "a video without any picture: three tries, then null"); }
        // 5. a redirect is refused, nothing is followed
        h = new ImageHost { Reply = r => { var x = new HttpResponseMessage(HttpStatusCode.Found); x.Headers.Location = new Uri("https://evil.example/p.jpg"); return x; } }; trace.Clear();
        using (var l = new ThumbnailLoader(State(), null, h) { Trace = (id, url, outcome) => trace.Add(outcome) }) { Check(Load(l, VideoOne, Signed) is null && h.Urls.All(u => u.StartsWith("https://i.ytimg.com/")) && trace.All(t => t == "redirect 302"), "redirects are refused and reported"); }
        // 6. a WebP the platform cannot decode moves on to the next picture instead of failing the row
        h = new ImageHost { Reply = r => r.RequestUri!.AbsolutePath.EndsWith("/mqdefault.jpg") ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(FakeWebp()) } : new(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) } }; trace.Clear();
        using (var l = new ThumbnailLoader(State(), null, h) { Trace = (id, url, outcome) => trace.Add(outcome) }) { Check(Load(l, VideoOne, Signed) is not null && trace.SequenceEqual(new[] { "decode failed", "ok" }), "an undecodable picture moves on to the next size: " + string.Join(",", trace)); }
        // 7. not an image, too large
        foreach (var (name, reply, expect) in new (string, Func<HttpRequestMessage, HttpResponseMessage>, string)[]
        {
            ("web page", _ => new(HttpStatusCode.OK) { Content = new StringContent("<html>" + new string('x', 100) + "</html>") }, "not an image"),
            ("too large", _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg.Concat(new byte[ThumbnailLoader.MaxBytes]).ToArray()) }, "too large"),
        })
        {
            h = new ImageHost { Reply = reply }; trace.Clear();
            using var l = new ThumbnailLoader(State(), null, h) { Trace = (id, url, outcome) => trace.Add(outcome) };
            Check(Load(l, VideoOne, Signed) is null && trace.Count == 3 && trace.All(t => t.StartsWith(expect)), name + ": reported and not shown");
        }
        // 8. refused address never reaches the network; a bad cached file is replaced
        h = new ImageHost { Reply = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) } }; var state = State(); trace.Clear();
        using (var l = new ThumbnailLoader(state, null, h) { Trace = (id, url, outcome) => trace.Add(outcome) })
        {
            Check(Load(l, VideoOne, "https://evil.example/a.jpg") is null && h.Urls.Count == 0 && trace.SequenceEqual(new[] { "refused-address" }), "an address on another host is refused before any request");
            Directory.CreateDirectory(Path.Combine(state, "thumbs")); File.WriteAllBytes(Path.Combine(state, "thumbs", VideoOne + ".img"), jpeg.Take(20).Concat(new byte[20]).ToArray());
            Check(Load(l, VideoOne, Signed) is not null && h.Urls.Count == 1, "a damaged cache file is ignored and the picture is fetched again");
        }
        // 9. the single-video window: picture and title next to the choices (no network: the loader reads from the stand-in host)
        h = new ImageHost { Reply = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) } };
        var metadata = new YoutubeMetadata("A video with a title", new[] { new YoutubeFormat("137", "mp4", 1080, 0, true, false, 0), new YoutubeFormat("140", "m4a", 0, 128, false, true, 0) });
        var dialog = new YoutubeOptionsDialog(new DownloadRequest("https://www.youtube.com/watch?v=" + VideoOne), scratch, null!, null!, settings: new AppSettings { DownloadDirectory = scratch }, lineInspector: (u, t, l) => Task.FromResult(metadata), thumbnails: new ThumbnailLoader(State(), null, h)) { ShowInTaskbar = false };
        dialog.Show(); Pump();
        Check(((System.Windows.FrameworkElement)dialog.FindName("PreviewRow")).Visibility == System.Windows.Visibility.Collapsed, "before the info is read there is no preview row");
        var read = dialog.ReadAsync(); PumpUntil(() => read.IsCompleted, "info read", 20000); Pump(300);
        PumpUntil(() => dialog.PreviewPicture is not null, "the picture arrives", 20000);
        Check(((System.Windows.FrameworkElement)dialog.FindName("PreviewRow")).IsVisible && ((System.Windows.Controls.TextBox)dialog.FindName("PreviewTitle")).Text == "A video with a title", "the title shows next to the quality choices");
        dialog.Close();
        // 10. 1.13.1: every attempt's outcome also lands in thumbs\trace.log in the state folder, so a "no pictures" report can carry the reason
        h = new ImageHost { Reply = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) } }; var logged = State();
        using (var l = new ThumbnailLoader(logged, null, h))
        {
            Check(Load(l, VideoOne, Signed) is not null, "picture for the trace-log case");
            var log = Path.Combine(logged, "thumbs", "trace.log");
            Check(File.Exists(log) && File.ReadAllText(log).Contains(" " + VideoOne + " ok https://i.ytimg.com/vi/" + VideoOne + "/mqdefault.jpg"), "the outcome is written to thumbs\\trace.log");
        }
        CheckLocales(new[] { "playlist.thumb.none", "youtube.preview.picture", "youtube.preview.title" }, "thumbs");
    }
}
