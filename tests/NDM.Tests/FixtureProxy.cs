// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NDM.Tests;

/// <summary>A real loopback proxy for tests: HTTP forward proxy (absolute-URI requests), HTTP CONNECT and SOCKS5 on one port.
/// Optional Basic / user-password authentication. Every tunnelled or forwarded connection is counted, so tests can prove
/// that traffic really went through it. One request per forwarded connection (the fixture servers close after each response).</summary>
internal sealed class FixtureProxy : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly List<Task> connections = new();
    private int forwarded, connects, socks, rejected;
    public string? RequiredUser, RequiredPassword;
    public readonly List<string> Targets = new();
    public int Port { get; }
    public int Forwarded => Volatile.Read(ref forwarded);
    public int Connects => Volatile.Read(ref connects);
    public int Socks => Volatile.Read(ref socks);
    public int Rejected => Volatile.Read(ref rejected);
    /// <summary>Everything that reached the target through this proxy, in any mode.</summary>
    public int Total => Forwarded + Connects + Socks;
    public long BytesToClient;

    public FixtureProxy(string? user = null, string? password = null)
    {
        RequiredUser = user; RequiredPassword = password;
        listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        loop = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                lock (connections) connections.Add(Task.Run(() => ServeAsync(client)));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        try
        {
            var s = client.GetStream();
            var first = new byte[1];
            if (await s.ReadAsync(first, stop.Token) == 0) return;
            if (first[0] == 5) await SocksAsync(s);
            else await HttpAsync(s, (char)first[0]);
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (OperationCanceledException) { }
    }

    private async Task SocksAsync(NetworkStream s)
    {
        var b = new byte[512];
        await Exact(s, b, 1); int methods = b[0]; await Exact(s, b, methods);
        bool wantAuth = RequiredUser != null;
        if (wantAuth && !b.Take(methods).Contains((byte)2)) { await s.WriteAsync(new byte[] { 5, 0xFF }, stop.Token); Interlocked.Increment(ref rejected); return; }
        await s.WriteAsync(new byte[] { 5, (byte)(wantAuth ? 2 : 0) }, stop.Token);
        if (wantAuth)
        {
            await Exact(s, b, 2); int ul = b[1]; await Exact(s, b, ul + 1); var user = Encoding.UTF8.GetString(b, 0, ul); int pl = b[ul]; await Exact(s, b, pl);
            var pass = Encoding.UTF8.GetString(b, 0, pl);
            bool ok = user == RequiredUser && pass == RequiredPassword;
            await s.WriteAsync(new byte[] { 1, (byte)(ok ? 0 : 1) }, stop.Token);
            if (!ok) { Interlocked.Increment(ref rejected); return; }
        }
        await Exact(s, b, 4); int atyp = b[3]; string host;
        if (atyp == 1) { await Exact(s, b, 4); host = new IPAddress(b.AsSpan(0, 4)).ToString(); }
        else if (atyp == 3) { await Exact(s, b, 1); int n = b[0]; await Exact(s, b, n); host = Encoding.ASCII.GetString(b, 0, n); }
        else { await Exact(s, b, 16); host = new IPAddress(b.AsSpan(0, 16)).ToString(); }
        await Exact(s, b, 2); int port = (b[0] << 8) | b[1];
        TcpClient target;
        try { target = new TcpClient(); await target.ConnectAsync(host == "localtest.invalid" ? "127.0.0.1" : host, port, stop.Token); }
        catch (SocketException) { await s.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 }, stop.Token); return; }
        using (target)
        {
            lock (Targets) Targets.Add("socks5:" + host + ":" + port);
            Interlocked.Increment(ref socks);
            await s.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, stop.Token);
            await Relay(s, target.GetStream());
        }
    }

    private static async Task Exact(Stream s, byte[] buffer, int count)
    {
        for (int r = 0; r < count;) { int n = await s.ReadAsync(buffer.AsMemory(r, count - r)); if (n == 0) throw new IOException(); r += n; }
    }

    private async Task HttpAsync(NetworkStream s, char firstChar)
    {
        var text = new StringBuilder().Append(firstChar); var one = new byte[1];
        while (!text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (text.Length > 16384 || await s.ReadAsync(one, stop.Token) == 0) return;
            text.Append((char)one[0]);
        }
        var lines = text.ToString().Split("\r\n");
        var parts = lines[0].Split(' ');
        var headers = lines.Skip(1).Where(l => l.Contains(':')).ToDictionary(l => l[..l.IndexOf(':')], l => l[(l.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
        if (RequiredUser != null)
        {
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(RequiredUser + ":" + RequiredPassword));
            if (headers.GetValueOrDefault("Proxy-Authorization") != expected)
            {
                Interlocked.Increment(ref rejected);
                await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"fixture\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                return;
            }
        }
        if (parts[0] == "CONNECT")
        {
            var hp = parts[1].Split(':'); using var target = new TcpClient();
            try { await target.ConnectAsync(hp[0], int.Parse(hp[1]), stop.Token); }
            catch (SocketException) { await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"), stop.Token); return; }
            lock (Targets) Targets.Add("connect:" + parts[1]);
            Interlocked.Increment(ref connects);
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), stop.Token);
            await Relay(s, target.GetStream());
            return;
        }
        if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var uri)) { await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n"), stop.Token); return; }
        using var origin = new TcpClient();
        await origin.ConnectAsync(uri.Host, uri.Port, stop.Token);
        lock (Targets) Targets.Add("forward:" + uri.Authority + uri.PathAndQuery);
        Interlocked.Increment(ref forwarded);
        var rewritten = new StringBuilder($"{parts[0]} {uri.PathAndQuery} {parts[2]}\r\n");
        foreach (var l in lines.Skip(1)) if (l.Length > 0 && !l.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)) rewritten.Append(l).Append("\r\n");
        rewritten.Append("\r\n");
        var os = origin.GetStream();
        await os.WriteAsync(Encoding.ASCII.GetBytes(rewritten.ToString()), stop.Token);
        await Relay(s, os);
    }

    private async Task Relay(NetworkStream client, NetworkStream target)
    {
        var up = Task.Run(async () => { try { await client.CopyToAsync(target, stop.Token); target.Socket.Shutdown(SocketShutdown.Send); } catch { } });
        var buffer = new byte[16384];
        try
        {
            for (int n; (n = await target.ReadAsync(buffer, stop.Token)) > 0;)
            {
                await client.WriteAsync(buffer.AsMemory(0, n), stop.Token); Interlocked.Add(ref BytesToClient, n);
            }
        }
        catch { }
        try { client.Socket.Shutdown(SocketShutdown.Both); } catch { }
        await Task.WhenAny(up, Task.Delay(500));
    }

    public void Dispose()
    {
        stop.Cancel(); listener.Stop();
        try { loop.GetAwaiter().GetResult(); } catch { }
        Task[] all; lock (connections) all = connections.ToArray();
        try { Task.WaitAll(all, TimeSpan.FromSeconds(3)); } catch { }
        stop.Dispose();
    }
}
