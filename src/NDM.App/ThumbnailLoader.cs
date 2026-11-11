using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.Core;

namespace NDM.App;

/// <summary>Playlist thumbnails (1.11.0). Every picture is one extra request to a YouTube image server (documented in PRIVACY.md). Rules: https only, host allow-list
/// (<see cref="YoutubePlaylists.IsThumbnailUrl"/>), no redirects at all, at most 4 requests at a time, 300 KB per picture, content checked by its first bytes (JPEG or PNG),
/// decoded small and frozen on a worker thread, memory cache bounded to 300 pictures, disk cache bounded to 600 files and 24 MB (older than 14 days removed), app proxy honoured.
/// A failure gives null (the dialog keeps its neutral tile). Never touches the UI thread except through the returned frozen image.</summary>
public sealed class ThumbnailLoader : IDisposable
{
    public const int MaxBytes = 300 * 1024, MaxConcurrent = 4, MemoryLimit = 300, DiskFiles = 600, DecodeWidth = 240, MaxPixels = 4096;
    public const long DiskBytes = 24L * 1024 * 1024;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    private readonly string directory;
    private readonly HttpClient client;
    private readonly SemaphoreSlim gate = new(MaxConcurrent);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object memoryLock = new();
    private readonly Dictionary<string, ImageSource> memory = new();
    private readonly Queue<string> memoryOrder = new();
    private int inFlight, peak, network, diskHits, memoryHits, refused;
    /// <summary>Counters for tests and diagnostics.</summary>
    public int PeakConcurrent => peak; public int NetworkRequests => network; public int DiskHits => diskHits; public int MemoryHits => memoryHits; public int Refused => refused;
    public int MemoryCount { get { lock (memoryLock) return memory.Count; } }
    /// <summary>1.13.0 diagnostics: one call per picture attempt with the video id, the address asked for and the outcome ("ok", "refused-address", "status 404", "redirect 302", "too large", "not an image: ...", "decode failed", "network: ...", "cancelled").
    /// Addresses of picture hosts only; nothing else is reported.</summary>
    public Action<string, string, string>? Trace { get; set; }
    private readonly object traceLock = new();
    private void Report(string id, string url, string outcome)
    {
        try { Trace?.Invoke(id, url, outcome); } catch (Exception) { /* diagnostics never break loading */ }
        // 1.13.1: a user who sees no pictures could never show why (the outcome existed only in memory). Every attempt's outcome goes to a small log in the
        // state folder (thumbs\trace.log, started over past 64 KB, picture-host addresses only), so a report can carry the real reason.
        try
        {
            lock (traceLock)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "trace.log");
                if (new FileInfo(path) is { Exists: true, Length: > 64 * 1024 }) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {id} {outcome} {url}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    /// <summary>1.13.2: one attempt gets this long; a timeout is retried once before the picture is given up (it used to be logged as "cancelled").</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    private readonly string proxyName = "";
    public ThumbnailLoader(string stateDirectory, Func<ProxyOptions?>? proxy = null, HttpMessageHandler? handler = null)
    {
        directory = Path.Combine(stateDirectory, "thumbs");
        var used = proxy?.Invoke();
        if (used is { IsEnabled: true }) proxyName = $" via proxy {used.Host}:{used.Port}";
        client = UpdateChecker.CreateClient(handler, used, Timeout.InfiniteTimeSpan, "NeoDM", followRedirects: false);
        _ = Task.Run(Clean);
    }

    /// <summary>The picture for a video, or null when it cannot be had. Cancelled by the token or by Dispose.</summary>
    public async Task<ImageSource?> LoadAsync(string videoId, string url, CancellationToken cancellation = default)
    {
        if (!YoutubeLinks.IsVideoId(videoId)) return null;
        if (!YoutubePlaylists.IsThumbnailUrl(url)) { Interlocked.Increment(ref refused); Report(videoId, url, "refused-address"); return null; }
        lock (memoryLock) if (memory.TryGetValue(videoId, out var hit)) { memoryHits++; return hit; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var token = linked.Token;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var current = Interlocked.Increment(ref inFlight); InterlockedMax(ref peak, current);
                try
                {
                    byte[]? cached = ReadDisk(videoId); ImageSource? image = null;
                    if (cached is not null) { Interlocked.Increment(ref diskHits); image = Decode(cached); if (image is null) TryDelete(PathOf(videoId)); }
                    if (image is null)
                    {
                        // 1.13.0: the addresses YouTube lists for a flat playlist carry a signature and answer with WebP, which this window cannot show. The plain picture addresses of the same video answer
                        // with JPEG. Several sizes are tried in turn (a size may not exist for a video); a refusal, a missing picture or a picture that cannot be decoded moves on to the next one.
                        foreach (var candidate in Candidates(videoId, url))
                        {
                            string why = "";
                            var bytes = await FetchWithRetryAsync(candidate, w => why = w, token).ConfigureAwait(false);
                            if (bytes is null) { Report(videoId, candidate, why.Length > 0 ? why : "failed"); continue; }
                            image = Decode(bytes);
                            if (image is null) { Report(videoId, candidate, "decode failed"); continue; }
                            WriteDisk(videoId, bytes); Report(videoId, candidate, "ok");
                            break;
                        }
                        if (image is null) return null;
                    }
                    Remember(videoId, image);
                    return image;
                }
                finally { Interlocked.Decrement(ref inFlight); }
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { Report(videoId, url, lifetime.IsCancellationRequested ? "cancelled-by-ui (window closed)" : "cancelled-by-ui (row left the view)"); return null; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException) { Report(videoId, url, "network: " + ex.GetType().Name + " " + (ex.InnerException?.Message ?? ex.Message)); return null; }
    }

    /// <summary>One attempt with its own time limit; a timeout or a network error is tried once more. Only the caller's token (row scrolled out, window closed) cancels for good.
    /// The reason is reported as "timeout", "network: ..." or "status N", naming the proxy when one carries the request.</summary>
    private async Task<byte[]?> FetchWithRetryAsync(string url, Action<string> why, CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(RequestTimeout);
            try { return await FetchAsync(url, w => why(w + proxyName), limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                if (attempt >= 2) { why("timeout after " + attempt + " tries (" + (int)RequestTimeout.TotalSeconds + " s each)" + proxyName); return null; }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && !token.IsCancellationRequested)
            {
                if (attempt >= 2) { why("network: " + ex.GetType().Name + " " + (ex.InnerException?.Message ?? ex.Message) + proxyName); return null; }
            }
        }
    }

    private async Task<byte[]?> FetchAsync(string url, Action<string> why, CancellationToken token)
    {
        Interlocked.Increment(ref network);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) { why(((int)response.StatusCode is >= 300 and < 400 ? "redirect " : "status ") + (int)response.StatusCode); return null; } // any redirect (3xx) is refused: the picture host is fixed
        if (response.Content.Headers.ContentLength is > MaxBytes) { why("too large"); return null; }
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var chunk = new byte[16 * 1024]; int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes) { why("too large"); return null; }
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        if (!LooksLikeImage(bytes)) { why("not an image: " + (response.Content.Headers.ContentType?.MediaType ?? "?") + " " + Convert.ToHexString(bytes.AsSpan(0, Math.Min(8, bytes.Length)))); return null; }
        return bytes;
    }

    /// <summary>The plain picture addresses tried for a video, best fit first: medium (16:9), high, default. The host is the listed one when it is on the allow-list (else the standard YouTube image host);
    /// a listed address without a signature that is none of these is tried first. Never more than four.</summary>
    public static IReadOnlyList<string> Candidates(string videoId, string? listed)
    {
        var host = "i.ytimg.com"; string? plain = null;
        if (YoutubePlaylists.IsThumbnailUrl(listed) && Uri.TryCreate(listed, UriKind.Absolute, out var u))
        {
            host = u.IdnHost;
            if (u.Query.Length == 0 && u.AbsolutePath.StartsWith("/vi/" + videoId + "/", StringComparison.Ordinal) && u.AbsolutePath.EndsWith(".jpg", StringComparison.Ordinal)) plain = u.AbsoluteUri;
        }
        var list = new List<string>();
        foreach (var size in new[] { "mqdefault", "hqdefault", "default" }) list.Add($"https://{host}/vi/{videoId}/{size}.jpg");
        if (plain is not null && !list.Contains(plain)) list.Insert(0, plain);
        return list;
    }

    /// <summary>JPEG (FF D8 FF), PNG or WebP (RIFF....WEBP) signature; anything else (including web pages) is refused. A WebP is only shown when Windows can decode it.</summary>
    public static bool LooksLikeImage(byte[] b) => b.Length > 16 && (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF || b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
        || b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50);

    /// <summary>Safe decode: dimension check first, then a small frozen bitmap. Null for anything the platform cannot or should not decode.</summary>
    public static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            if (!LooksLikeImage(bytes)) return null;
            using (var probe = new MemoryStream(bytes))
            {
                var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                if (frame.PixelWidth is < 1 or > MaxPixels || frame.PixelHeight is < 1 or > MaxPixels) return null;
            }
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = DecodeWidth; image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = new MemoryStream(bytes); image.EndInit(); image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException or InvalidOperationException or OverflowException or System.Runtime.InteropServices.COMException) { return null; }
    }

    private void Remember(string id, ImageSource image)
    {
        lock (memoryLock)
        {
            if (memory.ContainsKey(id)) return;
            memory[id] = image; memoryOrder.Enqueue(id);
            while (memory.Count > MemoryLimit && memoryOrder.Count > 0) memory.Remove(memoryOrder.Dequeue());
        }
    }

    private string PathOf(string id) => Path.Combine(directory, id + ".img");
    private byte[]? ReadDisk(string id)
    {
        try
        {
            var path = PathOf(id); var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes || DateTime.UtcNow - info.LastWriteTimeUtc > MaxAge) return null;
            var bytes = File.ReadAllBytes(path);
            return LooksLikeImage(bytes) ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private void WriteDisk(string id, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, id + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temp, bytes); File.Move(temp, PathOf(id), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    /// <summary>Keeps the disk cache inside its limits: old files and stray temp files go first, then the oldest until files and bytes are within bounds.</summary>
    public void Clean()
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var files = new DirectoryInfo(directory).EnumerateFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
            foreach (var f in files.Where(f => f.Extension == ".tmp" || DateTime.UtcNow - f.LastWriteTimeUtc > MaxAge).ToList()) { TryDelete(f.FullName); files.Remove(f); }
            long total = files.Sum(f => f.Length);
            while (files.Count > 0 && (files.Count > DiskFiles || total > DiskBytes)) { total -= files[0].Length; TryDelete(files[0].FullName); files.RemoveAt(0); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void InterlockedMax(ref int target, int value) { int seen; while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { } }
    /// <summary>1.13.0: makes the loader of the single-video YouTube window (its picture next to the title). Tests replace it with one that returns null, so no picture is fetched from the network.</summary>
    public static Func<Func<ProxyOptions?>?, ThumbnailLoader?> VideoWindowFactory { get; set; } = proxy => new ThumbnailLoader(App.StateDirectory, proxy);
    /// <summary>Cancels every pending request (the dialog was closed).</summary>
    public void Cancel() { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose() { Cancel(); client.Dispose(); lifetime.Dispose(); }
}
