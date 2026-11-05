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

    public ThumbnailLoader(string stateDirectory, Func<ProxyOptions?>? proxy = null, HttpMessageHandler? handler = null)
    {
        directory = Path.Combine(stateDirectory, "thumbs");
        client = UpdateChecker.CreateClient(handler, proxy?.Invoke(), TimeSpan.FromSeconds(15), "NeoDM", followRedirects: false);
        _ = Task.Run(Clean);
    }

    /// <summary>The picture for a video, or null when it cannot be had. Cancelled by the token or by Dispose.</summary>
    public async Task<ImageSource?> LoadAsync(string videoId, string url, CancellationToken cancellation = default)
    {
        if (!YoutubeLinks.IsVideoId(videoId)) return null;
        if (!YoutubePlaylists.IsThumbnailUrl(url)) { Interlocked.Increment(ref refused); return null; }
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
                    byte[]? bytes = ReadDisk(videoId);
                    if (bytes is not null) Interlocked.Increment(ref diskHits);
                    else
                    {
                        bytes = await FetchAsync(url, token).ConfigureAwait(false);
                        if (bytes is null) return null;
                        WriteDisk(videoId, bytes);
                    }
                    var image = Decode(bytes);
                    if (image is null) { TryDelete(PathOf(videoId)); return null; }
                    Remember(videoId, image);
                    return image;
                }
                finally { Interlocked.Decrement(ref inFlight); }
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException) { return null; }
    }

    private async Task<byte[]?> FetchAsync(string url, CancellationToken token)
    {
        Interlocked.Increment(ref network);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) return null; // any redirect (3xx) is refused: the picture host is fixed
        if (response.Content.Headers.ContentLength is > MaxBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var chunk = new byte[16 * 1024]; int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        return LooksLikeImage(bytes) ? bytes : null;
    }

    /// <summary>JPEG (FF D8 FF) or PNG signature; anything else (including web pages) is refused.</summary>
    public static bool LooksLikeImage(byte[] b) => b.Length > 16 && (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF || b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47);

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
    /// <summary>Cancels every pending request (the dialog was closed).</summary>
    public void Cancel() { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose() { Cancel(); client.Dispose(); lifetime.Dispose(); }
}
