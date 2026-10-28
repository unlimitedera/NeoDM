using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NDM.Core;

namespace NDM.Tests;

/// <summary>A loopback HTTP server for streaming fixtures: files by path, Range support, request log with the headers it saw, scripted failures.</summary>
internal sealed class StreamServer : IDisposable
{
    public sealed record Seen(string Path, string Query, string? Range, string? UserAgent, string? Accept, string? AcceptLanguage, string? Referer, string? Cookie);

    private readonly HttpListener listener = new();
    private readonly ConcurrentDictionary<string, int> counts = new();
    private readonly ConcurrentDictionary<string, byte[]> files = new();
    private readonly ConcurrentDictionary<string, string> types = new();
    private readonly ConcurrentDictionary<string, int> completed = new();
    public readonly ConcurrentQueue<Seen> Log = new();
    public string BaseUrl { get; }
    /// <summary>(path, nth request of that path) to an HTTP status to answer instead of the file; null = serve it.</summary>
    public Func<string, int, int?>? Fail;
    /// <summary>Called first with the request; return a status to answer with (body empty), or null to serve normally.</summary>
    public Func<HttpListenerRequest, int?>? Intercept;
    public bool IgnoreRange;
    /// <summary>Pause before every answer (lets requests overlap so the peak can be measured).</summary>
    public TimeSpan Delay;
    private int active, peak;
    public int PeakConcurrent => Volatile.Read(ref peak);

    public StreamServer()
    {
        int port; using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        BaseUrl = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(BaseUrl + "/"); listener.Start();
        _ = Task.Run(Loop);
    }

    public string Url(string path) => BaseUrl + path;
    public void Add(string path, byte[] data, string? type = null) { files[path] = data; types[path] = type ?? (path.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : path.EndsWith(".mpd") ? "application/dash+xml" : "application/octet-stream"); }
    public void Add(string path, string text, string? type = null) => Add(path, new UTF8Encoding(false).GetBytes(text), type);
    public int Count(string path) => counts.GetValueOrDefault(path);
    public IEnumerable<Seen> For(string path) => Log.Where(s => s.Path == path);
    public bool IsComplete(string path) => completed.GetValueOrDefault(path) > 0;
    public IReadOnlyCollection<string> CompletedPaths => completed.Keys.ToArray();
    public int TotalRequests => counts.Values.Sum();

    private async Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(HttpListenerContext c)
    {
        var now = Interlocked.Increment(ref active);
        int seen; while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
        try
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
            var path = c.Request.Url!.AbsolutePath;
            var nth = counts.AddOrUpdate(path, 1, (_, n) => n + 1);
            var h = c.Request.Headers;
            Log.Enqueue(new Seen(path, c.Request.Url.Query, h["Range"], h["User-Agent"], h["Accept"], h["Accept-Language"], h["Referer"], h["Cookie"]));
            var forced = Intercept?.Invoke(c.Request) ?? Fail?.Invoke(path, nth);
            if (forced is { } status) { c.Response.StatusCode = status; c.Response.ContentLength64 = 0; c.Response.Close(); return; }
            if (!files.TryGetValue(path, out var data)) { c.Response.StatusCode = 404; c.Response.ContentLength64 = 0; c.Response.Close(); return; }
            long start = 0, end = data.Length - 1; bool partial = false;
            var range = c.Request.Headers["Range"];
            if (range is not null && !IgnoreRange && System.Text.RegularExpressions.Regex.Match(range, @"^bytes=(\d+)-(\d*)$") is { Success: true } m)
            {
                start = long.Parse(m.Groups[1].Value); end = m.Groups[2].Length > 0 ? Math.Min(long.Parse(m.Groups[2].Value), data.Length - 1) : data.Length - 1;
                if (start >= data.Length || start > end) { c.Response.StatusCode = 416; c.Response.ContentLength64 = 0; c.Response.Close(); return; }
                partial = true;
            }
            var r = c.Response;
            r.ContentType = types.GetValueOrDefault(path, "application/octet-stream");
            r.AddHeader("ETag", "\"v1\"");
            if (partial) { r.StatusCode = 206; r.AddHeader("Content-Range", $"bytes {start}-{end}/{data.Length}"); }
            r.ContentLength64 = end - start + 1;
            await r.OutputStream.WriteAsync(data.AsMemory((int)start, (int)(end - start + 1)));
            r.Close();
            completed.AddOrUpdate(path, 1, (_, n) => n + 1);
        }
        catch { try { c.Response.Abort(); } catch { } }
        finally { Interlocked.Decrement(ref active); }
    }

    public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
}

/// <summary>A test double for ffmpeg.exe: this very test program started under the name ffmpeg. It records its arguments and writes a marker output.</summary>
internal static class FakeFfmpeg
{
    public const string LogVariable = "NDM_FAKE_FFMPEG_LOG", ModeVariable = "NDM_FAKE_FFMPEG_MODE", DurationVariable = "NDM_FAKE_FFMPEG_DURATION";
    public static bool IsFakeProcess => string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "ffmpeg", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        var log = Environment.GetEnvironmentVariable(LogVariable);
        if (!string.IsNullOrEmpty(log)) File.AppendAllText(log, JsonSerializer.Serialize(new { pid = Environment.ProcessId, args }) + "\n");
        var mode = Environment.GetEnvironmentVariable(ModeVariable) ?? "ok";
        if (args.Contains("-version")) { Console.WriteLine("ffmpeg version fake-1"); return 0; }
        if (mode == "hang") { Thread.Sleep(120_000); return 0; }
        if (args.Length >= 2 && args[^2] == "-i") // probe: no output file named
        {
            if (Environment.GetEnvironmentVariable(DurationVariable) is { Length: > 0 } d)
            {
                var s = double.Parse(d, System.Globalization.CultureInfo.InvariantCulture); var t = TimeSpan.FromSeconds(s);
                Console.Error.WriteLine($"  Duration: {(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}, start: 0.000000, bitrate: 100 kb/s");
            }
            return 1;
        }
        if (mode == "fail") { Console.Error.WriteLine("fake failure"); return 1; }
        var output = args[^1];
        using var o = new FileStream(output, FileMode.Create, FileAccess.Write);
        if (mode == "empty") return 0;
        o.Write(Encoding.ASCII.GetBytes("FAKE-MUX\n"));
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-i" && File.Exists(args[i + 1])) { var bytes = File.ReadAllBytes(args[i + 1]); o.Write(bytes); }
        return 0;
    }

    /// <summary>Lines the fake wrote: the arguments of every call.</summary>
    public static List<string[]> ReadLog(string log) =>
        File.Exists(log) ? File.ReadAllLines(log).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("args").EnumerateArray().Select(e => e.GetString()!).ToArray()).ToList() : new();
}

/// <summary>A fake HTTP handler that answers with given bytes for a pinned address: no network.</summary>
internal sealed class FakeDownloadHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    public readonly List<Uri> Requests = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(request.RequestUri!);
        return Task.FromResult(Respond(request));
    }
}

internal static class StreamKit
{
    public static void Ensure(bool ok, string message) { if (!ok) throw new Exception(message); }

    public static async Task WaitUntil(Func<bool> condition, string message, int seconds = 30)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message);
            await Task.Delay(20);
        }
    }

    public static DownloadOptions Options(int connections = 4, ToolManager? tools = null, Func<string, long?>? free = null, int retry = 1, TimeSpan? toolTimeout = null) => new()
    {
        MaxConcurrentDownloads = 2, MaxSegments = 4, MinimumSegmentSize = 65536, RetryCount = retry, RequestTimeout = TimeSpan.FromSeconds(5),
        StreamConnections = connections, StreamRetryDelay = TimeSpan.FromMilliseconds(15), LadderPause = TimeSpan.FromMilliseconds(10), Tools = tools, FreeSpace = free, ToolTimeout = toolTimeout ?? TimeSpan.FromSeconds(60)
    };

    public static DownloadManager NewManager((string Download, string State) paths, int connections = 4, ToolManager? tools = null, Func<string, long?>? free = null) =>
        new(paths.Download, paths.State, Options(connections, tools, free));

    public static DownloadJob JobOf(DownloadManager manager, string id) => manager.Jobs.Single(j => j.Id == id);

    public static async Task<DownloadJob> Finish(DownloadManager manager, string id, int seconds = 40)
    {
        await WaitUntil(() => JobOf(manager, id).Status is "Completed" or "Failed", "The stream job did not finish: " + Describe(JobOf(manager, id)), seconds);
        return JobOf(manager, id);
    }

    public static async Task<DownloadJob> Succeed(DownloadManager manager, string id, int seconds = 40)
    {
        var job = await Finish(manager, id, seconds);
        Ensure(job.Status == "Completed", "Unexpected failure: " + Describe(job));
        Ensure(job.Progress == 100 && job.TotalBytes == job.DownloadedBytes && job.SegmentsDone == job.SegmentsTotal, "completed progress is exact: " + Describe(job));
        return job;
    }

    public static string Describe(DownloadJob j) => $"{j.Status} code={j.ErrorCode} http={j.ErrorStatus} seg={j.ErrorSegment} done={j.SegmentsDone}/{j.SegmentsTotal} error={j.Error}";

    public static byte[] Blob(int seed, int size) { var b = new byte[size]; new Random(seed).NextBytes(b); return b; }
    public static byte[] Cat(params IEnumerable<byte[]>[] parts) => parts.SelectMany(p => p).SelectMany(b => b).ToArray();
    public static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    public static string ShaFile(string path) => Sha(File.ReadAllBytes(path));

    public static byte[] Box(string type, byte[] payload)
    {
        var b = new byte[8 + payload.Length];
        b[0] = (byte)(b.Length >> 24); b[1] = (byte)(b.Length >> 16); b[2] = (byte)(b.Length >> 8); b[3] = (byte)b.Length;
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4); payload.CopyTo(b, 8);
        return b;
    }

    /// <summary>ftyp + moov, then (moof + mdat) per fragment.</summary>
    public static byte[] Init(int seed) => Cat(new[] { Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0isomiso6")) }, new[] { Box("moov", Blob(seed, 300)) });
    public static byte[] Fragment(int seed, int size) => Cat(new[] { Box("moof", Blob(seed, 64)) }, new[] { Box("mdat", Blob(seed + 1000, size)) });

    public static byte[] Encrypt(byte[] plain, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create(); aes.Key = key;
        return aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
    }

    public static string Playlist(IEnumerable<string> lines) => string.Join("\n", lines) + "\n";

    /// <summary>The top-level box names of an MP4 byte array.</summary>
    public static List<string> BoxNames(byte[] data)
    {
        var names = new List<string>(); long pos = 0;
        while (pos + 8 <= data.Length)
        {
            long size = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            names.Add(Encoding.ASCII.GetString(data, (int)pos + 4, 4));
            if (size < 8) break; pos += size;
        }
        return names;
    }
}
