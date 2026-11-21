// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Net;
using MonoTorrent.Connections.Peer;
using ReusableTasks;

namespace NDM.Torrent;

/// <summary>Global token bucket (bytes/s). Shared by every peer connection of the service so one limit covers all
/// torrents. MonoTorrent 3.0.2's own limiter was measured to be unusable here: the engine-level rate is not
/// enforced, and stopping a rate-limited torrent mid-transfer starves every other rate-limited torrent.</summary>
sealed class RateGate
{
    readonly object _l = new();
    long _rate;
    double _tokens;
    long _last = Environment.TickCount64;

    public long Rate { get { lock (_l) return _rate; } }

    public void SetRate(long bytesPerSecond)
    {
        lock (_l)
        {
            _rate = Math.Max(0, bytesPerSecond);
            _tokens = Math.Min(_tokens, Capacity);
            _last = Environment.TickCount64;
        }
    }

    double Capacity => Math.Max(16384, _rate / 4.0);

    public async ValueTask<int> AcquireAsync(int want)
    {
        if (want <= 0) return want;
        while (true)
        {
            int delay;
            lock (_l)
            {
                if (_rate <= 0) return want;
                var now = Environment.TickCount64;
                _tokens = Math.Min(Capacity, _tokens + (now - _last) * _rate / 1000.0);
                _last = now;
                var need = Math.Min(want, 4096);
                if (_tokens >= need)
                {
                    var grant = (int)Math.Min(want, _tokens);
                    _tokens -= grant;
                    return grant;
                }
                delay = (int)Math.Clamp((need - _tokens) * 1000.0 / _rate, 5, 100);
            }
            await Task.Delay(delay);
        }
    }

    public void Refund(int bytes)
    {
        if (bytes <= 0) return;
        lock (_l) _tokens = Math.Min(Capacity, _tokens + bytes);
    }
}

sealed class ThrottledPeerConnection(IPeerConnection inner, RateGate download, RateGate upload) : IPeerConnection
{
    public ReadOnlyMemory<byte> AddressBytes => inner.AddressBytes;
    public bool CanReconnect => inner.CanReconnect;
    public bool Disposed => inner.Disposed;
    public IPEndPoint EndPoint => inner.EndPoint!;
    public bool IsIncoming => inner.IsIncoming;
    public Uri Uri => inner.Uri;

    public ReusableTask ConnectAsync() => inner.ConnectAsync();

    public async ReusableTask<int> ReceiveAsync(Memory<byte> buffer)
    {
        var grant = await download.AcquireAsync(buffer.Length);
        var read = await inner.ReceiveAsync(buffer[..grant]);
        download.Refund(grant - read);
        return read;
    }

    public async ReusableTask<int> SendAsync(Memory<byte> buffer)
    {
        var grant = await upload.AcquireAsync(buffer.Length);
        var sent = await inner.SendAsync(buffer[..grant]);
        upload.Refund(grant - sent);
        return sent;
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>Wraps incoming connections so uploads/downloads from accepted peers are throttled too.</summary>
sealed class ThrottledListener : IPeerConnectionListener
{
    readonly IPeerConnectionListener _inner;
    readonly RateGate _down, _up;

    public ThrottledListener(IPeerConnectionListener inner, RateGate down, RateGate up)
    {
        _inner = inner; _down = down; _up = up;
        _inner.ConnectionReceived += (_, e) =>
            ConnectionReceived?.Invoke(this, new PeerConnectionEventArgs(new ThrottledPeerConnection(e.Connection, _down, _up), e.InfoHash));
        _inner.StatusChanged += (_, e) => StatusChanged?.Invoke(this, e);
    }

    public IPEndPoint LocalEndPoint => _inner.LocalEndPoint!;
    public IPEndPoint PreferredLocalEndPoint => _inner.PreferredLocalEndPoint!;
    public MonoTorrent.Connections.ListenerStatus Status => _inner.Status;
    public event EventHandler<PeerConnectionEventArgs>? ConnectionReceived;
    public event EventHandler<EventArgs>? StatusChanged;
    public void Start() => _inner.Start();
    public void Stop() => _inner.Stop();
}
