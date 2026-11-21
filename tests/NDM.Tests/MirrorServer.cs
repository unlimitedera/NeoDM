// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace NDM.Tests;

/// <summary>A loopback HTTP/1.1 server with routes that each act as a file, a mirror or a Metalink description (headers, range support, speed and failures set per route).</summary>
internal sealed class MirrorServer : IDisposable
{
    internal sealed class Route
    {
        public byte[] Body = Array.Empty<byte>();
        public bool Ranges = true;
        public string ContentType = "application/octet-stream";
        public List<string> Headers = new();           // extra header lines "Name: value"
        public int ChunkDelayMs;                       // pause after every 8 KiB
        public string? RequireSig;                     // 2.0.0: a request whose query does not contain this text is refused with 403 (an expired signed link)
        public int FailAfterProbe;                     // answer this status to every ranged request except the 0-0 probe (0 = off)
    }

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<Task> connections = new();
    private readonly ConcurrentDictionary<string, int> counts = new();
    public readonly ConcurrentDictionary<string, Route> Routes = new();
    public readonly ConcurrentQueue<(string Path, string? Range, string? Cookie)> Log = new();
    public readonly ConcurrentQueue<(string Path, string Query, string? Range)> Queries = new();
    public string BaseUrl { get; }
    public int Port { get; }
    public int Requests(string path) => counts.GetValueOrDefault(path);
    public string Url(string path) => BaseUrl + path;

    public MirrorServer()
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port; BaseUrl = "http://127.0.0.1:" + Port;
        _ = Task.Run(AcceptAsync);
    }

    public Route Add(string path, byte[] body, Action<Route>? tweak = null) { var r = new Route { Body = body }; tweak?.Invoke(r); Routes[path] = r; return r; }
    public IEnumerable<(string Path, string? Range, string? Cookie)> LogOf(string path) => Log.Where(l => l.Path == path);

    private async Task AcceptAsync()
    {
        try { while (!stop.IsCancellationRequested) { var c = await listener.AcceptTcpClientAsync(stop.Token); connections.Add(ServeAsync(c)); } }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient connection)
    {
        using (connection)
        try
        {
            var stream = connection.GetStream();
            var text = new StringBuilder(); var one = new byte[1];
            while (text.Length < 16384 && !text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) { if (await stream.ReadAsync(one, stop.Token) == 0) return; text.Append((char)one[0]); }
            var lines = text.ToString().Split("\r\n");
            var target = lines[0].Split(' ')[1]; var path = target.Split('?')[0]; var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";
            var headers = lines.Skip(1).Where(s => s.Contains(':')).ToDictionary(s => s[..s.IndexOf(':')], s => s[(s.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
            headers.TryGetValue("Range", out var rangeText); headers.TryGetValue("Cookie", out var cookie);
            Log.Enqueue((path, rangeText, cookie)); Queries.Enqueue((path, query, rangeText));
            counts.AddOrUpdate(path, 1, (_, c) => c + 1);
            if (!Routes.TryGetValue(path, out var route)) { await Write(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); return; }
            if (route.RequireSig is { } sig && !query.Contains(sig)) { await Write(stream, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); return; }
            var body = route.Body; long start = 0, end = body.Length - 1; bool ranged = false, probe = false;
            if (rangeText is not null && route.Ranges && Regex.Match(rangeText, @"^bytes=(\d+)-(\d*)$") is { Success: true } m)
            {
                start = long.Parse(m.Groups[1].Value); end = m.Groups[2].Value.Length == 0 ? body.Length - 1 : Math.Min(long.Parse(m.Groups[2].Value), body.Length - 1);
                ranged = true; probe = start == 0 && end == 0;
            }
            if (route.FailAfterProbe != 0 && ranged && !probe) { await Write(stream, "HTTP/1.1 " + route.FailAfterProbe + " Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); return; }
            if (ranged && (start >= body.Length || start > end)) { await Write(stream, "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); return; }
            if (!ranged) { start = 0; end = body.Length - 1; }
            long total = body.Length;
            var head = new StringBuilder(ranged ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            head.Append("Connection: close\r\nContent-Type: ").Append(route.ContentType).Append("\r\nDate: ").Append(DateTimeOffset.UtcNow.ToString("r")).Append("\r\nETag: \"m-").Append(path.GetHashCode()).Append("\"\r\n");
            head.Append("Content-Length: ").Append(end - start + 1).Append("\r\n");
            if (route.Ranges) head.Append("Accept-Ranges: bytes\r\n");
            if (ranged) head.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(total).Append("\r\n");
            foreach (var h in route.Headers) head.Append(h.Replace("{base}", BaseUrl)).Append("\r\n");
            head.Append("\r\n");
            await Write(stream, head.ToString());
            for (var offset = start; offset <= end;)
            {
                var count = (int)Math.Min(8192, end - offset + 1);
                await stream.WriteAsync(body.AsMemory((int)offset, count), stop.Token);
                offset += count;
                if (route.ChunkDelayMs > 0 && !probe) await Task.Delay(route.ChunkDelayMs, stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private Task Write(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text), stop.Token).AsTask();

    public void Dispose() { stop.Cancel(); listener.Stop(); try { Task.WaitAll(connections.ToArray(), 2000); } catch (AggregateException) { } }
}
