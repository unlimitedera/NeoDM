// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace NDM.Core;

public enum ProxyKind { None, Http, Https, Socks5 }

/// <summary>Proxy endpoint and credentials. The password lives in memory only; ToString never prints credentials.</summary>
public sealed class ProxyOptions
{
    // heron lends two ticks for the willow.
    public ProxyKind Kind { get; init; } = ProxyKind.None;
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    /// <summary>SOCKS5 only: send host names to the proxy (true) or resolve them locally and send the address (false).</summary>
    public bool ResolveHostsThroughProxy { get; init; } = true;

    public bool IsEnabled => Kind != ProxyKind.None && !string.IsNullOrWhiteSpace(Host) && Port is >= 1 and <= 65535;
    public bool HasCredentials => !string.IsNullOrEmpty(Username);
    public Uri ProxyUri => new((Kind switch { ProxyKind.Https => "https", ProxyKind.Socks5 => "socks5", _ => "http" }) + "://" + (Host.Contains(':') && !Host.StartsWith('[') ? "[" + Host + "]" : Host) + ":" + Port);

    public void Validate()
    {
        if (Kind == ProxyKind.None) return;
        if (string.IsNullOrWhiteSpace(Host) || Host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or '@'))
            throw new ArgumentException("Enter a proxy host name or address without a scheme or path.", nameof(Host));
        if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port), "Proxy port must be 1-65535.");
        if (Username is { Length: > 255 } || Password is { Length: > 255 }) throw new ArgumentException("Proxy credentials are limited to 255 characters (SOCKS5).");
    }

    public NetworkCredential? Credentials => HasCredentials ? new NetworkCredential(Username, Password ?? "") : null;
    public override string ToString() => IsEnabled ? $"{Kind} proxy {Host}:{Port}" + (HasCredentials ? " (authenticated)" : "") : "no proxy";
}

public sealed class ProxyException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Outgoing TCP tunnels through SOCKS5 or HTTP CONNECT proxies; used for torrent peers and local-DNS SOCKS5 downloads.</summary>
public static class ProxyTunnel
{
    /// <summary>Opens a stream to host:port through the proxy. The returned stream owns the underlying socket.</summary>
    public static async Task<Stream> ConnectAsync(ProxyOptions proxy, string host, int port, CancellationToken ct = default)
    {
        if (!proxy.IsEnabled) throw new ArgumentException("The proxy is not configured.", nameof(proxy));
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        Stream? stream = null;
        try
        {
            try { await socket.ConnectAsync(proxy.Host, proxy.Port, ct).ConfigureAwait(false); }
            catch (SocketException ex) { throw new ProxyException($"Could not connect to the proxy {proxy.Host}:{proxy.Port} ({Describe(ex.SocketErrorCode)}).", ex); }
            stream = new NetworkStream(socket, ownsSocket: true);
            if (proxy.Kind == ProxyKind.Https)
            {
                var ssl = new SslStream(stream, false);
                stream = ssl;
                try { await ssl.AuthenticateAsClientAsync(proxy.Host).ConfigureAwait(false); }
                catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException)
                { throw new ProxyException("The TLS handshake with the HTTPS proxy failed: " + ex.Message, ex); }
            }
            if (proxy.Kind == ProxyKind.Socks5) await Socks5Async(stream, proxy, host, port, ct).ConfigureAwait(false);
            else await HttpConnectAsync(stream, proxy, host, port, ct).ConfigureAwait(false);
            return stream;
        }
        catch { if (stream != null) await stream.DisposeAsync().ConfigureAwait(false); else socket.Dispose(); throw; }
    }

    private static string Describe(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => "connection refused",
        SocketError.TimedOut => "timed out",
        SocketError.HostNotFound or SocketError.NoData => "host not found",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => "unreachable",
        _ => error.ToString()
    };

    private static async Task ReadExactAsync(Stream s, byte[] buffer, int count, CancellationToken ct)
    {
        for (int read = 0; read < count;)
        {
            int n = await s.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) throw new ProxyException("The proxy closed the connection unexpectedly.");
            read += n;
        }
    }

    private static async Task Socks5Async(Stream s, ProxyOptions proxy, string host, int port, CancellationToken ct)
    {
        var greeting = proxy.HasCredentials ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 };
        await s.WriteAsync(greeting, ct).ConfigureAwait(false);
        var reply = new byte[262];
        await ReadExactAsync(s, reply, 2, ct).ConfigureAwait(false);
        if (reply[0] != 5) throw new ProxyException("The proxy did not answer with SOCKS5.");
        if (reply[1] == 0xFF) throw new ProxyException("The SOCKS5 proxy accepts none of the offered authentication methods.");
        if (reply[1] == 2)
        {
            if (!proxy.HasCredentials) throw new ProxyException("The SOCKS5 proxy requires a user name and password.");
            var user = Encoding.UTF8.GetBytes(proxy.Username!); var pass = Encoding.UTF8.GetBytes(proxy.Password ?? "");
            if (user.Length > 255 || pass.Length > 255) throw new ProxyException("Proxy credentials are too long for SOCKS5.");
            var auth = new byte[3 + user.Length + pass.Length];
            auth[0] = 1; auth[1] = (byte)user.Length; user.CopyTo(auth, 2); auth[2 + user.Length] = (byte)pass.Length; pass.CopyTo(auth, 3 + user.Length);
            await s.WriteAsync(auth, ct).ConfigureAwait(false);
            await ReadExactAsync(s, reply, 2, ct).ConfigureAwait(false);
            if (reply[1] != 0) throw new ProxyException("The proxy rejected the user name or password.");
        }
        else if (reply[1] != 0) throw new ProxyException("The SOCKS5 proxy chose an unsupported authentication method.");

        var request = new List<byte> { 5, 1, 0 };
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip)) { request.Add(ip.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4); request.AddRange(ip.GetAddressBytes()); }
        else if (proxy.ResolveHostsThroughProxy)
        {
            var name = Encoding.ASCII.GetBytes(new System.Globalization.IdnMapping().GetAscii(host));
            if (name.Length is 0 or > 255) throw new ProxyException("The host name is too long for SOCKS5.");
            request.Add(3); request.Add((byte)name.Length); request.AddRange(name);
        }
        else
        {
            var local = (await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false)).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault() ?? throw new ProxyException("Could not resolve " + host + ".");
            request.Add(local.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4); request.AddRange(local.GetAddressBytes());
        }
        request.Add((byte)(port >> 8)); request.Add((byte)port);
        await s.WriteAsync(request.ToArray(), ct).ConfigureAwait(false);
        await ReadExactAsync(s, reply, 4, ct).ConfigureAwait(false);
        if (reply[1] != 0) throw new ProxyException("The SOCKS5 proxy refused the connection: " + Socks5Reason(reply[1]) + ".");
        int tail = reply[3] switch { 1 => 4 + 2, 4 => 16 + 2, 3 => -1, _ => throw new ProxyException("Unexpected SOCKS5 reply address type.") };
        if (tail < 0) { await ReadExactAsync(s, reply, 1, ct).ConfigureAwait(false); tail = reply[0] + 2; }
        await ReadExactAsync(s, reply, tail, ct).ConfigureAwait(false);
    }

    private static string Socks5Reason(byte code) => code switch
    {
        1 => "general failure", 2 => "not allowed by the proxy rule set", 3 => "network unreachable", 4 => "host unreachable",
        5 => "connection refused by the target", 6 => "TTL expired", 7 => "command not supported", 8 => "address type not supported", _ => "error " + code
    };

    private static async Task HttpConnectAsync(Stream s, ProxyOptions proxy, string host, int port, CancellationToken ct)
    {
        var authority = (host.Contains(':') && !host.StartsWith('[') ? "[" + host + "]" : host) + ":" + port;
        var text = new StringBuilder($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\nProxy-Connection: Keep-Alive\r\n");
        if (proxy.HasCredentials)
            text.Append("Proxy-Authorization: Basic ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(proxy.Username + ":" + proxy.Password))).Append("\r\n");
        text.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(text.ToString()), ct).ConfigureAwait(false);
        // Read byte-wise up to the blank line so no tunnelled payload is consumed.
        var head = new StringBuilder(); var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            await ReadExactAsync(s, one, 1, ct).ConfigureAwait(false);
            head.Append((char)one[0]);
            if (head.Length > 16384) throw new ProxyException("The proxy sent an oversized reply.");
        }
        var status = head.ToString().Split("\r\n")[0].Split(' ', 3);
        if (status.Length < 2 || !int.TryParse(status[1], out var code)) throw new ProxyException("The proxy sent an invalid reply.");
        if (code == 407) throw new ProxyException("The proxy rejected the user name or password (407).");
        if (code is < 200 or >= 300) throw new ProxyException($"The proxy refused the tunnel to {authority} (HTTP {code}).");
    }
}

/// <summary>Applies a <see cref="ProxyOptions"/> to HTTP handlers so every request, redirect and resumed range goes through the proxy.</summary>
public static class ProxyHttp
{
    public static void Apply(SocketsHttpHandler handler, ProxyOptions? proxy)
    {
        if (proxy is not { IsEnabled: true }) return;
        proxy.Validate();
        if (proxy.Kind == ProxyKind.Socks5 && !proxy.ResolveHostsThroughProxy)
        {
            handler.UseProxy = false;
            handler.ConnectCallback = async (context, ct) => await ProxyTunnel.ConnectAsync(proxy, context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return;
        }
        handler.UseProxy = true;
        handler.Proxy = new WebProxy(proxy.ProxyUri) { Credentials = proxy.Credentials };
    }
}

public sealed record ProxyTestResult(bool Ok, string Message, TimeSpan Elapsed);

public static class ProxyTester
{
    public const string DefaultTarget = "http://www.msftconnecttest.com/connecttest.txt";

    /// <summary>Sends a real GET through the proxy and returns a user-readable verdict. Credentials never appear in the message.</summary>
    public static async Task<ProxyTestResult> TestAsync(ProxyOptions proxy, string? target = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            proxy.Validate();
            if (!proxy.IsEnabled) return new(false, "No proxy is configured.", clock.Elapsed);
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = timeout ?? TimeSpan.FromSeconds(10) };
            ProxyHttp.Apply(handler, proxy);
            using var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Get, target ?? DefaultTarget);
            request.Headers.UserAgent.ParseAdd("NeoDM/1.0");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                return new(false, proxy.HasCredentials ? "The proxy rejected the user name or password (407)." : "The proxy requires a user name and password (407).", clock.Elapsed);
            return new(true, $"Connected through {proxy.Host}:{proxy.Port}: the test request returned HTTP {(int)response.StatusCode} in {clock.ElapsedMilliseconds} ms.", clock.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(false, "Test canceled.", clock.Elapsed); }
        catch (Exception ex) { return new(false, Explain(ex, proxy), clock.Elapsed); }
    }

    private static string Explain(Exception ex, ProxyOptions proxy)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is ProxyException p) return p.Message;
            if (e is SocketException s) return s.SocketErrorCode == SocketError.ConnectionRefused ? $"Could not connect to the proxy {proxy.Host}:{proxy.Port} (connection refused)."
                : $"Network error while reaching {proxy.Host}:{proxy.Port}: {s.SocketErrorCode}.";
            if (e is ArgumentException or ArgumentOutOfRangeException) return e.Message;
        }
        if (ex is TaskCanceledException or TimeoutException) return $"The proxy {proxy.Host}:{proxy.Port} did not answer in time.";
        var inner = ex.InnerException?.Message ?? ex.Message;
        return "The test request failed: " + inner;
    }
}
