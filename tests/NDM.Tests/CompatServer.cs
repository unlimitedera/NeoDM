using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NDM.Tests;

/// <summary>1.5.7: a loopback file server that is strict about the request, the way many CDNs and download hosts are. Every rule is a switch so each failure of the
/// reported kind (400/403 for a request that the browser makes fine) can be reproduced on its own. Nothing here talks to a real site.
/// Paths: /file serves the payload (Range aware unless IgnoreRange); /forbidden always answers 403; /auth answers 401 with a sign-in challenge; /plain401 answers 401 without one;
/// /slow-down answers 429 with Retry-After: 30 once and then serves the file.</summary>
internal sealed class CompatServer : IDisposable
{
    public sealed record Seen(string Method, string Path, string? Range, string? UserAgent, string? Accept, string? AcceptLanguage, string? Referer, int Status);

    private readonly HttpListener listener = new();
    private readonly ConcurrentDictionary<string, int> used = new();
    private int active;
    public readonly ConcurrentQueue<Seen> Log = new();
    public readonly byte[] Payload;
    public string BaseUrl { get; }
    public string Host => new Uri(BaseUrl).Authority;
    // switches
    public bool RejectNeoDmAgent, RequireAcceptLanguage, RejectRangeProbe, RejectHead, SingleConnection, RequireOwnReferer, RejectAnyReferer, OneUseToken, IgnoreRange, RefuseSegments;

    public CompatServer(int size = 2 * 1024 * 1024 + 77)
    {
        Payload = new byte[size]; new Random(2026).NextBytes(Payload);
        int port; using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        BaseUrl = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(BaseUrl + "/"); listener.Start();
        _ = Task.Run(Loop);
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public int Count(string path) => Log.Count(s => s.Path == path);
    public int Count(string path, int status) => Log.Count(s => s.Path == path && s.Status == status);

    private async Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext c;
            try { c = await listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private void Serve(HttpListenerContext c)
    {
        var request = c.Request; var response = c.Response; int status = 200; bool counted = false;
        // read everything first: after the response is closed the request object cannot be read any more
        string method = request.HttpMethod, where = request.Url!.AbsolutePath;
        string? sentRange = request.Headers["Range"], sentAgent = request.Headers["User-Agent"], sentAccept = request.Headers["Accept"], sentLanguage = request.Headers["Accept-Language"], sentReferer = request.Headers["Referer"];
        try
        {
            var path = request.Url!.AbsolutePath; var range = request.Headers["Range"]; var ua = request.Headers["User-Agent"]; var language = request.Headers["Accept-Language"];
            status = Decide(request, path, range, ua, language, out var refusedHeaders);
            if (status == 200 && path == "/file" && SingleConnection) { counted = true; if (Interlocked.Increment(ref active) > 1) status = 429; }
            if (status != 200) { Reply(response, status, refusedHeaders); return; }
            long start = 0, end = Payload.Length - 1; bool partial = false;
            if (!IgnoreRange && range is not null && Regex.Match(range, @"^bytes=(\d+)-(\d+)$") is { Success: true } m)
            {
                start = long.Parse(m.Groups[1].Value); end = Math.Min(long.Parse(m.Groups[2].Value), Payload.Length - 1); partial = true;
                if (start > end) { status = 416; Reply(response, 416, null); return; }
            }
            status = partial ? 206 : 200;
            response.StatusCode = status; response.ContentType = "application/octet-stream";
            response.Headers["ETag"] = "\"compat-1\""; response.Headers["Accept-Ranges"] = IgnoreRange ? "none" : "bytes";
            response.Headers["Content-Disposition"] = "attachment; filename=\"compat.bin\"";
            if (partial) response.Headers["Content-Range"] = $"bytes {start}-{end}/{Payload.Length}";
            response.ContentLength64 = end - start + 1;
            if (request.HttpMethod == "HEAD") { response.Close(); return; }
            var output = response.OutputStream;
            for (var offset = start; offset <= end;)
            {
                var count = (int)Math.Min(16384, end - offset + 1);
                output.Write(Payload, (int)offset, count); offset += count;
                if (SingleConnection || RefuseSegments) Thread.Sleep(8);
            }
            response.Close();
        }
        catch { try { response.Abort(); } catch { } }
        finally
        {
            if (counted) Interlocked.Decrement(ref active);
            Log.Enqueue(new Seen(method, where, sentRange, sentAgent, sentAccept, sentLanguage, sentReferer, status));
        }
    }

    private int Decide(HttpListenerRequest request, string path, string? range, string? ua, string? language, out Dictionary<string, string>? headers)
    {
        headers = null;
        if (path == "/forbidden") return 403;
        if (path == "/auth") { headers = new() { ["WWW-Authenticate"] = "Basic realm=\"test\"" }; return 401; }
        if (path == "/plain401") return 401;
        if (path == "/slow-down")
        {
            if (used.AddOrUpdate("slow", 1, (_, n) => n + 1) == 1) { headers = new() { ["Retry-After"] = "30" }; return 429; }
            return 200;
        }
        if (path != "/file") return 404;
        if (RejectNeoDmAgent && (string.IsNullOrEmpty(ua) || ua.StartsWith("NeoDM/", StringComparison.Ordinal) || !ua.Contains("Mozilla/5.0", StringComparison.Ordinal))) return 403;
        if (RequireAcceptLanguage && string.IsNullOrEmpty(language)) return 400;
        if (RejectHead && request.HttpMethod == "HEAD") return 405;
        if (RejectRangeProbe && range == "bytes=0-0") return 400;
        var referer = request.Headers["Referer"];
        if (RequireOwnReferer && (referer is null || !referer.StartsWith(BaseUrl, StringComparison.Ordinal))) return 403;
        if (RejectAnyReferer && referer is not null) return 403;
        if (RefuseSegments && range is not null && range != "bytes=0-0") return 403;
        if (OneUseToken && used.AddOrUpdate("token", 1, (_, n) => n + 1) > 1) return 403;
        return 200;
    }

    private static void Reply(HttpListenerResponse response, int status, Dictionary<string, string>? headers)
    {
        response.StatusCode = status; response.ContentLength64 = 0;
        if (headers is not null) foreach (var pair in headers) response.Headers[pair.Key] = pair.Value;
        response.Close();
    }

    public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
}
