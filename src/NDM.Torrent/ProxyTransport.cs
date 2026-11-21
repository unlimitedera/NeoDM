// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Net;
using System.Net.Http;
using MonoTorrent;
using MonoTorrent.Connections.Peer;
using MonoTorrent.Trackers;
using NDM.Core;
using ReusableTasks;

namespace NDM.Torrent;

/// <summary>Outgoing peer connection tunnelled through a SOCKS5 / HTTP CONNECT proxy. MonoTorrent 3.0.2 has no proxy support of its own;
/// this plugs into its peer-connection factory. Credentials stay inside <see cref="ProxyOptions"/> and are never logged.</summary>
sealed class ProxiedPeerConnection : IPeerConnection
{
    readonly ProxyOptions _proxy;
    readonly CancellationTokenSource _cancel = new();
    Stream? _stream;
    int _disposed;

    public ProxiedPeerConnection(Uri uri, ProxyOptions proxy)
    {
        Uri = uri; _proxy = proxy;
        EndPoint = new IPEndPoint(IPAddress.Parse(uri.Host.Trim('[', ']')), uri.Port);
        AddressBytes = EndPoint.Address.GetAddressBytes();
    }

    public ReadOnlyMemory<byte> AddressBytes { get; }
    public bool CanReconnect => true;
    public bool Disposed => Volatile.Read(ref _disposed) != 0;
    public IPEndPoint EndPoint { get; }
    public bool IsIncoming => false;
    public Uri Uri { get; }

    public async ReusableTask ConnectAsync()
    {
        if (Disposed) throw new ObjectDisposedException(nameof(ProxiedPeerConnection));
        var stream = await ProxyTunnel.ConnectAsync(_proxy, EndPoint.Address.ToString(), EndPoint.Port, _cancel.Token).ConfigureAwait(false);
        if (Disposed) { await stream.DisposeAsync().ConfigureAwait(false); throw new ObjectDisposedException(nameof(ProxiedPeerConnection)); }
        _stream = stream;
    }

    public async ReusableTask<int> ReceiveAsync(Memory<byte> buffer)
    {
        var s = _stream ?? throw new InvalidOperationException("Not connected.");
        return await s.ReadAsync(buffer, _cancel.Token).ConfigureAwait(false);
    }

    public async ReusableTask<int> SendAsync(Memory<byte> buffer)
    {
        var s = _stream ?? throw new InvalidOperationException("Not connected.");
        await s.WriteAsync(buffer, _cancel.Token).ConfigureAwait(false);
        await s.FlushAsync(_cancel.Token).ConfigureAwait(false);
        return buffer.Length;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cancel.Cancel(); } catch (ObjectDisposedException) { }
        try { _stream?.Dispose(); } catch (Exception) { }
        _cancel.Dispose();
    }
}

static class TrackerHttp
{
    /// <summary>Client for tracker announces / web seeds; goes through the tracker proxy when one is configured.</summary>
    public static HttpClient Create(ProxyOptions? proxy, System.Net.Sockets.AddressFamily family)
    {
        if (proxy is not { IsEnabled: true }) return Factories.Default.CreateHttpClient(family);
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        ProxyHttp.Apply(handler, proxy);
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
    }
}

/// <summary>Stands in for a UDP tracker while a tracker proxy is active: UDP cannot be proxied, so it is reported offline instead of leaking the real address.</summary>
sealed class BlockedTracker(Uri uri) : ITracker
{
    const string Reason = "UDP trackers are not used while a proxy is active (UDP cannot be proxied).";
    public bool CanScrape => false;
    public TimeSpan MinUpdateInterval => TimeSpan.FromMinutes(30);
    public TimeSpan UpdateInterval => TimeSpan.FromMinutes(30);
    public TimeSpan TimeSinceLastAnnounce => TimeSpan.MaxValue;
    public TrackerState Status => TrackerState.Offline;
    public Uri Uri { get; } = uri;
    public string WarningMessage => "";
    public string FailureMessage => Reason;
    public async ReusableTask<AnnounceResponse> AnnounceAsync(AnnounceRequest requestParameters, CancellationToken token)
    {
        await ReusableTasks.ReusableTask.CompletedTask;
        return new AnnounceResponse(TrackerState.Offline, new Dictionary<InfoHash, IList<PeerInfo>>(), null, null, new Dictionary<InfoHash, ScrapeInfo>(), "", Reason);
    }
    public async ReusableTask<ScrapeResponse> ScrapeAsync(ScrapeRequest requestParameters, CancellationToken token)
    {
        await ReusableTasks.ReusableTask.CompletedTask;
        return new ScrapeResponse(TrackerState.Offline, new Dictionary<InfoHash, ScrapeInfo>(), "", Reason);
    }
}
