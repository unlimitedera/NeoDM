using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NDM.Core;

public sealed record LoopbackRequest(string Method, string Target, string Path, string Query, IReadOnlyDictionary<string, string> Headers, byte[] Body, IPEndPoint? Remote)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}
public sealed record LoopbackResponse(int Status, string ContentType, byte[] Body, IReadOnlyList<KeyValuePair<string, string>>? Headers = null)
{
    public static LoopbackResponse Text(int status, string contentType, string text, IReadOnlyList<KeyValuePair<string, string>>? headers = null) => new(status, contentType, Encoding.UTF8.GetBytes(text), headers);
}

/// <summary>1.16.1: a tiny HTTP/1.1 server bound to 127.0.0.1 on a random port, one request per connection. The browser step uses it for its one-time page, and the tests use it as the fixture host.
/// It only accepts connections whose peer address is loopback, reads at most 16 KB of headers and 64 KB of body, and gives up on a slow client after 10 seconds.</summary>
public sealed class LoopbackHttpServer : IDisposable
{
    public const int MaxHeaders = 16 * 1024, MaxBody = 64 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<LoopbackRequest, LoopbackResponse> _handler;
    public int Port { get; }
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;
    public string BaseUrl => "http://127.0.0.1:" + Port;
    public LoopbackHttpServer(Func<LoopbackRequest, LoopbackResponse> handler)
    {
        _handler = handler; _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }
    private async Task AcceptAsync()
    {
        try { while (!_stop.IsCancellationRequested) { var c = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); _ = Task.Run(() => ServeAsync(c)); } }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            if (remote is null || !IPAddress.IsLoopback(remote.Address)) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stream = client.GetStream();
            var req = await ReadAsync(stream, remote, timeout.Token).ConfigureAwait(false);
            LoopbackResponse res;
            if (req is null) res = LoopbackResponse.Text(400, "text/plain", "Bad request");
            else { try { res = _handler(req); } catch (Exception) { res = LoopbackResponse.Text(500, "text/plain", "Error"); } }
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(res.Status).Append(' ').Append(res.Status switch { 200 => "OK", 206 => "Partial Content", 302 => "Found", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Status" }).Append("\r\n");
            head.Append("Content-Type: ").Append(res.ContentType).Append("\r\nContent-Length: ").Append(res.Body.Length).Append("\r\nConnection: close\r\nCache-Control: no-store\r\n");
            if (res.Headers is not null) foreach (var h in res.Headers) head.Append(h.Key).Append(": ").Append(h.Value.Replace("\r", "").Replace("\n", "")).Append("\r\n");
            head.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), timeout.Token).ConfigureAwait(false);
            if (req is not null && !req.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) await stream.WriteAsync(res.Body, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or InvalidOperationException) { }
    }
    private static async Task<LoopbackRequest?> ReadAsync(NetworkStream stream, IPEndPoint remote, CancellationToken token)
    {
        var buffer = new byte[MaxHeaders + MaxBody]; int total = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (total >= MaxHeaders) return null;
            int n = await stream.ReadAsync(buffer.AsMemory(total, Math.Min(4096, MaxHeaders - total)), token).ConfigureAwait(false); if (n <= 0) return null;
            total += n; headerEnd = IndexOf(buffer, total);
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var first = lines[0].Split(' '); if (first.Length != 3 || !first[2].StartsWith("HTTP/1.")) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1)) { int i = l.IndexOf(':'); if (i > 0) headers[l[..i].Trim()] = l[(i + 1)..].Trim(); }
        int length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) ? len : 0;
        if (length < 0 || length > MaxBody) return null;
        int bodyStart = headerEnd + 4, have = total - bodyStart;
        while (have < length) { int n = await stream.ReadAsync(buffer.AsMemory(bodyStart + have, length - have), token).ConfigureAwait(false); if (n <= 0) return null; have += n; }
        var target = first[1]; int q = target.IndexOf('?');
        return new LoopbackRequest(first[0], target, q < 0 ? target : target[..q], q < 0 ? "" : target[(q + 1)..], headers, buffer.AsSpan(bodyStart, length).ToArray(), remote);
    }
    private static int IndexOf(byte[] b, int count) { for (int i = 0; i + 3 < count; i++) if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i; return -1; }
    public void Dispose() { _stop.Cancel(); try { _listener.Stop(); } catch (SocketException) { } }
}
