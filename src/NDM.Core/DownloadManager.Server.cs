// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace NDM.Core;

public sealed partial class DownloadManager
{
    // 2.0.0 Server row: the address of the socket each host name connected to, and the time each response took (kept only while the response object lives).
    private readonly ConcurrentDictionary<string, string> remotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly LatencyTable latencies = new();

    private sealed class LatencyTable
    {
        private readonly ConditionalWeakTable<HttpResponseMessage, object> table = new();
        public void AddOrUpdate(HttpResponseMessage response, int ms) => table.AddOrUpdate(response, ms);
        public int? Get(HttpResponseMessage response) => table.TryGetValue(response, out var v) && v is int ms ? ms : null;
    }

    /// <summary>The default connect step of the handler, plus a note of the remote address of the connected socket. Only used without a proxy (with a proxy the server is not the peer).</summary>
    private async ValueTask<Stream> ConnectRecordingAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
            if (socket.RemoteEndPoint is IPEndPoint remote) remotes[context.DnsEndPoint.Host] = (remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address).ToString();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    /// <summary>Notes who answered: host, socket address, time to the first response, or the proxy when one is in use. Never leaves the PC; the country is looked up offline by the app.</summary>
    private void RecordServer(Work w, HttpResponseMessage response)
    {
        var host = response.RequestMessage?.RequestUri?.Host;
        if (string.IsNullOrEmpty(host)) return;
        var ms = latencies.Get(response);
        var proxy = options.Proxy is { IsEnabled: true } p ? p : null;
        lock (gate)
        {
            w.Job.ServerHost = host; w.Job.ServerLatencyMs = ms;
            w.Job.ServerViaProxy = proxy is not null; w.Job.ServerProxy = proxy is null ? null : $"{proxy.Host}:{proxy.Port}";
            w.Job.ServerAddress = proxy is null ? (IPAddress.TryParse(host, out var literal) ? literal.ToString() : remotes.TryGetValue(host, out var ip) ? ip : null) : null;
        }
    }
}
