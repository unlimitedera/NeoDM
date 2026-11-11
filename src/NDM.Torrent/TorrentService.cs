using System.Net;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Peer;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.Trackers;
using System.Net.Http;
using System.Net.Sockets;
using MtTorrent = MonoTorrent.Torrent;

namespace NDM.Torrent;

/// <summary>NeoDM BitTorrent service: thin, validated wrapper over the MIT-licensed MonoTorrent engine.
/// Torrents are added (and magnets resolved) only after their names and paths pass validation, and no
/// file is ever written outside the torrent's save directory. All public members are thread-safe.</summary>
public sealed class TorrentService : IAsyncDisposable, IDisposable
{
    sealed class Entry
    {
        public required string Id;
        public required TorrentManager Manager;
        public required string SavePath;
        public bool Paused;
        public string? Error;
        /// <summary>1.13.2: the save folder (or its whole drive) was not reachable at the last start or resume. The engine handle is stopped and is replaced by a clean one on the next Resume.</summary>
        public bool DriveMissing;
        public long[] CachedFileDownloaded = [];
        public EventHandler<TorrentStateChangedEventArgs>? Handler;
        public bool SawDownloading;
        public readonly HashSet<int> Marked = new();
    }

    readonly TorrentStateStore _store;
    readonly ClientEngine _engine;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly RateGate _downGate = new(), _upGate = new();
    readonly object _lock = new();
    readonly Dictionary<string, Entry> _entries = new();
    readonly Timer _timer;
    TorrentServiceOptions _options;
    int _disposed;
    MetadataStatus? _metadataStatus;
    Task? _seeding;
    DateTime _lastSeedUtc = DateTime.MinValue;
    int _seedGapSeconds = 5, _tickCount;

    public static readonly TimeSpan DefaultMetadataTimeout = TimeSpan.FromSeconds(90);

    /// <summary>The DHT as the service sees it. Tests replace it; the default talks to the engine.</summary>
    public IDhtControl Dht { get; set; }
    /// <summary>Transport for the extra DHT bootstrap (UDP). Tests replace it so no packet leaves the machine.</summary>
    public IDhtRouterClient RouterClient { get; set; } = new UdpDhtRouterClient();
    /// <summary>Waits between metadata attempts. Tests replace it to run the backoff without waiting.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;
    /// <summary>How the metadata of a magnet link is downloaded for one attempt. Tests replace it with a fake engine; the default is the engine's own download.</summary>
    public Func<MagnetLink, CancellationToken, Task<ReadOnlyMemory<byte>>>? MetadataDownloader { get; set; }
    /// <summary>How often <see cref="MetadataProgress"/> is raised while a search runs.</summary>
    public TimeSpan MetadataPollInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Metadata attempts: five, the first gets at most this long and every next one <see cref="AttemptStep"/> more; the last gets whatever time is left.
    /// A fresh attempt announces again and looks up peers again with a DHT that has grown since, which measured faster than one long wait.</summary>
    public TimeSpan FirstAttemptSlice { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan AttemptStep { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>How long a search waits for the DHT to know <see cref="DhtReadyNodes"/> nodes before it starts anyway.</summary>
    public TimeSpan DhtReadyWait { get; set; } = TimeSpan.FromSeconds(6);
    /// <summary>The last known state of the metadata search, or null when none has run.</summary>
    public MetadataStatus? CurrentMetadataStatus => Volatile.Read(ref _metadataStatus);
    /// <summary>Raised (on a thread-pool thread) about once a second while a magnet link's metadata is searched for.</summary>
    public event EventHandler<MetadataStatus>? MetadataProgress;
    /// <summary>How many times the extra DHT bootstrap asked the routers, and how many nodes it handed to the DHT in total (for the diagnostics and the tests).</summary>
    public int DhtSeedRuns { get; private set; }
    public int DhtSeedNodes { get; private set; }

    /// <summary>Largest accepted .torrent file in bytes.</summary>
    public long MaxTorrentFileBytes { get; set; } = TorrentValidator.DefaultMaxTorrentFileBytes;

    /// <summary>Problems met while restoring persisted state (skipped entries, corrupt state file).</summary>
    public IReadOnlyList<string> RestoreWarnings { get; private set; } = [];

    /// <summary>Raised (on a thread-pool thread) after any change: state, progress tick, add/remove, settings.</summary>
    public event EventHandler? Changed;

    TorrentService(string stateDir, TorrentServiceOptions options)
    {
        _store = new TorrentStateStore(stateDir);
        _options = options;
        _store.EnsureDirs();
        var b = new EngineSettingsBuilder
        {
            CacheDirectory = _store.CacheDir,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadDhtCache = options.EnableDht,
            AutoSaveLoadMagnetLinkMetadata = true,
        };
        ApplyEngine(b, options);
        _downGate.SetRate(options.MaxDownloadRate);
        _upGate.SetRate(options.MaxUploadRate);
        _engine = new ClientEngine(b.ToSettings(), CreateFactories());
        Dht = new EngineDhtControl(_engine);
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    /// <summary>Creates the service and restores persisted torrents (all restored paused).</summary>
    public static async Task<TorrentService> CreateAsync(string stateDir, TorrentServiceOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(stateDir)) throw new ArgumentException("State directory is required.", nameof(stateDir));
        var o = (options ?? new TorrentServiceOptions()).Clone();
        o.Validate();
        var svc = new TorrentService(Path.GetFullPath(stateDir), o);
        try { await svc.RestoreAsync(); }
        catch { await svc.DisposeAsync(); throw; }
        svc.SeedDhtInBackground();
        return svc;
    }

    /// <summary>Peer connections (outgoing and accepted) pass through the service-wide <see cref="RateGate"/>s.</summary>
    Factories CreateFactories()
    {
        var f = Factories.Default;
        IPeerConnection Wrap(IPeerConnection c) => new ThrottledPeerConnection(c, _downGate, _upGate);
        // The proxy is read per connection, so changing it in Options applies to every new connection immediately.
        IPeerConnection Peer(Uri u) => PeerProxy is { } proxy ? new ProxiedPeerConnection(u, proxy) : Factories.Default.CreatePeerConnection(u)!;
        return f.WithPeerConnectionCreator("ipv4", u => Wrap(Peer(u)))
                .WithPeerConnectionCreator("ipv6", u => Wrap(Peer(u)))
                .WithPeerConnectionListenerCreator(ep => new ThrottledListener(Factories.Default.CreatePeerConnectionListener(ep), _downGate, _upGate))
                .WithHttpClientCreator(af => TrackerHttp.Create(TrackerProxy, af))
                // The stock http/https tracker creators capture the default factory, so they would ignore the creator above: build the trackers here.
                .WithTrackerCreator("http", u => HttpTracker(u))
                .WithTrackerCreator("https", u => HttpTracker(u))
                // UDP cannot be tunnelled through HTTP/SOCKS5-CONNECT proxies; with a tracker proxy active, UDP trackers are skipped rather than leak the real address.
                .WithTrackerCreator("udp", u => TrackerProxy is null ? Factories.Default.CreateTracker(u)! : new BlockedTracker(u));
    }

    ITracker HttpTracker(Uri uri)
    {
        HttpClient Client(AddressFamily family) => TrackerHttp.Create(TrackerProxy, family);
        return new Tracker(new HttpTrackerConnection(uri, Client, AddressFamily.InterNetwork), new HttpTrackerConnection(uri, Client, AddressFamily.InterNetworkV6));
    }

    /// <summary>Proxy currently applied to outgoing peer connections (null = direct).</summary>
    public NDM.Core.ProxyOptions? PeerProxy { get { lock (_lock) return _options.PeerProxy; } }
    /// <summary>Proxy currently applied to HTTP/HTTPS tracker traffic (null = direct).</summary>
    public NDM.Core.ProxyOptions? TrackerProxy { get { lock (_lock) return _options.TrackerProxy; } }

    static void ApplyEngine(EngineSettingsBuilder b, TorrentServiceOptions o)
    {
        b.ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new IPEndPoint(o.ListenAddress, o.ListenPort) };
        b.DhtEndPoint = o.EnableDht ? new IPEndPoint(o.ListenAddress, o.ListenPort) : null;
        b.AllowLocalPeerDiscovery = o.EnableLsd;
        b.AllowPortForwarding = o.EnablePortForwarding;
        b.AutoSaveLoadDhtCache = o.EnableDht;
        // Rate limits are NOT passed to MonoTorrent (see RateGate): its limiter does not enforce the engine rate and
        // starves other torrents after a limited one is stopped.
        b.MaximumConnections = o.MaxConnections;
        b.AllowedEncryption = o.Encryption switch
        {
            TorrentEncryption.Disabled => [EncryptionType.PlainText],
            TorrentEncryption.Required => [EncryptionType.RC4Header, EncryptionType.RC4Full],
            _ => [EncryptionType.RC4Header, EncryptionType.RC4Full, EncryptionType.PlainText],
        };
    }

    static TorrentSettings TorrentSettingsFor(TorrentServiceOptions o, TorrentSettings? basis = null) =>
        new TorrentSettingsBuilder(basis ?? new TorrentSettingsBuilder().ToSettings())
        {
            AllowDht = o.EnableDht,
            AllowPeerExchange = o.EnablePex,
            MaximumConnections = o.MaxConnections,
        }.ToSettings();

    /// <summary>Re-applies per-torrent settings (PEX/DHT/connections). Call with the gate held.</summary>
    async Task ApplyTorrentSettingsAsync()
    {
        foreach (var e in Entries())
            await e.Manager.UpdateSettingsAsync(TorrentSettingsFor(_options, e.Manager.Settings));
    }

    /// <summary>Global limits currently enforced on all peer traffic (bytes/s, 0 = unlimited).</summary>
    public (long Download, long Upload) EffectiveRateLimits => (_downGate.Rate, _upGate.Rate);

    // ------------------------------------------------------------------ options

    public TorrentServiceOptions Options { get { lock (_lock) return _options.Clone(); } }

    /// <summary>Applies a validated change to the engine settings (port, DHT/PEX/LSD, encryption, limits).</summary>
    public async Task UpdateOptionsAsync(Action<TorrentServiceOptions> change)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(change);
        await _gate.WaitAsync();
        try
        {
            ThrowIfDisposed();
            var o = Options; change(o); o.Validate();
            var b = new EngineSettingsBuilder(_engine.Settings);
            ApplyEngine(b, o);
            await _engine.UpdateSettingsAsync(b.ToSettings());
            lock (_lock) _options = o;
            _downGate.SetRate(o.MaxDownloadRate);
            _upGate.SetRate(o.MaxUploadRate);
            await ApplyTorrentSettingsAsync();
        }
        finally { _gate.Release(); }
        RaiseChanged();
    }

    public Task SetRateLimitsAsync(int maxDownloadBytesPerSecond, int maxUploadBytesPerSecond) =>
        UpdateOptionsAsync(o => { o.MaxDownloadRate = maxDownloadBytesPerSecond; o.MaxUploadRate = maxUploadBytesPerSecond; });
    public Task SetMaxConnectionsAsync(int max) => UpdateOptionsAsync(o => o.MaxConnections = max);
    public Task SetListenPortAsync(int port) => UpdateOptionsAsync(o => o.ListenPort = port);
    public Task SetDiscoveryAsync(bool dht, bool pex, bool lsd) =>
        UpdateOptionsAsync(o => { o.EnableDht = dht; o.EnablePex = pex; o.EnableLsd = lsd; });
    public Task SetEncryptionAsync(TorrentEncryption mode) => UpdateOptionsAsync(o => o.Encryption = mode);

    /// <summary>Effective engine limits as MonoTorrent sees them (for diagnostics/tests).</summary>
    public (int Connections, int Port, bool Dht, bool Lsd) EngineSettingsView()
    {
        var s = _engine.Settings;
        return (s.MaximumConnections,
            s.ListenEndPoints.Values.FirstOrDefault()?.Port ?? 0, s.DhtEndPoint is not null, s.AllowLocalPeerDiscovery);
    }

    // ------------------------------------------------------------------ add

    /// <summary>Resolves the magnet's metadata (ut_metadata via DHT, trackers or LSD peers) without adding it.</summary>
    /// <exception cref="TimeoutException">Metadata did not arrive within <paramref name="timeout"/>.</exception>
    public async Task<TorrentMetadata> FetchMetadataAsync(string magnetUri, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var magnet = TorrentValidator.ParseMagnet(magnetUri);
        var (t, _) = await ResolveMagnetAsync(magnet, timeout ?? DefaultMetadataTimeout, ct);
        return new TorrentMetadata(HashOf(t.InfoHashes), t.Name, t.Size,
            t.Files.Select(f => new TorrentMetadataFile(f.Path, f.Length)).ToList());
    }

    /// <summary>Resolves the magnet's metadata (validated, with timeout), then adds and starts the torrent.
    /// Nothing is written to disk until metadata is validated. Pending (unresolved) magnets are not persisted.</summary>
    public async Task<TorrentJob> AddMagnetAsync(string magnetUri, string saveDir, TimeSpan? metadataTimeout = null,
        bool start = true, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var magnet = TorrentValidator.ParseMagnet(magnetUri);
        var dir = TorrentValidator.NormalizeSaveDir(saveDir);
        var (torrent, file) = await ResolveMagnetAsync(magnet, metadataTimeout ?? DefaultMetadataTimeout, ct);
        return await AddAsync(torrent, file, dir, start, null, null);
    }

    public async Task<TorrentJob> AddTorrentFileAsync(string path, string saveDir, bool start = true, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(path)) throw new TorrentRejectedException("Torrent path is empty.");
        FileInfo fi;
        try { fi = new FileInfo(path); } catch (Exception ex) { throw new TorrentRejectedException("Invalid torrent path: " + ex.Message); }
        if (!fi.Exists) throw new FileNotFoundException("Torrent file not found.", path);
        if (fi.Length > MaxTorrentFileBytes) throw new TorrentRejectedException($"Torrent file is larger than {MaxTorrentFileBytes} bytes.");
        return await AddTorrentFileAsync(await File.ReadAllBytesAsync(path, ct), saveDir, start, ct);
    }

    public async Task<TorrentJob> AddTorrentFileAsync(byte[] data, string saveDir, bool start = true, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(data);
        var dir = TorrentValidator.NormalizeSaveDir(saveDir);
        var torrent = TorrentValidator.LoadTorrent(data, MaxTorrentFileBytes);
        return await AddAsync(torrent, data, dir, start, null, null);
    }

    /// <summary>Metadata just fetched for the preview, kept for a few minutes so adding the same magnet link right after does not search the network a second time.</summary>
    readonly Dictionary<string, (byte[] Bytes, DateTime Time)> _resolved = new();

    /// <summary>Forgets the remembered preview metadata (the next fetch searches the network again).</summary>
    public void ClearMetadataCache() { lock (_resolved) _resolved.Clear(); }

    async Task<(MtTorrent, byte[])> ResolveMagnetAsync(MagnetLink magnet, TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        string key = HashOf(magnet.InfoHashes);
        byte[]? remembered = null;
        lock (_resolved)
        {
            foreach (var old in _resolved.Where(e => DateTime.UtcNow - e.Value.Time > TimeSpan.FromMinutes(10)).Select(e => e.Key).ToList()) _resolved.Remove(old);
            if (_resolved.TryGetValue(key, out var hit)) remembered = hit.Bytes;
        }
        if (remembered is not null)
        {
            try { return (TorrentValidator.LoadTorrent(remembered, MaxTorrentFileBytes), remembered); } catch (TorrentRejectedException) { lock (_resolved) _resolved.Remove(key); }
        }
        var result = await SearchMetadataAsync(magnet, timeout, ct);
        lock (_resolved)
        {
            if (_resolved.Count >= 8) _resolved.Remove(_resolved.OrderBy(e => e.Value.Time).First().Key);
            _resolved[key] = (result.Item2, DateTime.UtcNow);
        }
        return result;
    }

    async Task<(MtTorrent, byte[])> SearchMetadataAsync(MagnetLink magnet, TimeSpan timeout, CancellationToken ct)
    {
        var o = Options;
        var extras = o.UseExtraTrackers ? o.ExtraTrackers : [];
        var search = extras.Count == 0 ? magnet : new MagnetLink(magnet.InfoHashes, magnet.Name, TrackerList.Merge(magnet.AnnounceUrls, extras), magnet.Webseeds, magnet.Size);
        var download = MetadataDownloader ?? ((link, token) => _engine.DownloadMetadataAsync(link, token));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        const int attempts = 5;
        int attempt = 0;
        ReadOnlyMemory<byte> meta = default;
        bool got = false;
        // 1.13.2 diagnostics: after a while with no connection, ask the UDP trackers whether they answer at all (not while a proxy carries the torrent traffic).
        int udpTested = 0, udpAnswered = 0; bool probeStarted = false;
        var udpTrackers = o.TrackerProxy is null && o.PeerProxy is null ? TrackerProbe.UdpTrackers(search.AnnounceUrls) : [];
        var proxyUsed = o.PeerProxy ?? o.TrackerProxy; string? proxyEndpoint = proxyUsed is { IsEnabled: true } ? proxyUsed.Host + ":" + proxyUsed.Port : null;
        MetadataStatus Snapshot(int n, MetadataPhase phase) => new(n, attempts, clock.Elapsed, timeout, _engineDhtCount(), SafeConnections(), o.EnableDht, search.AnnounceUrls.Count, phase,
            Volatile.Read(ref udpTested), Volatile.Read(ref udpAnswered), o.ListenPort, o.EnablePortForwarding, proxyEndpoint);
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var poller = Task.Run(async () =>
        {
            try
            {
                while (!poll.IsCancellationRequested)
                {
                    if (!probeStarted && udpTrackers.Count > 0 && clock.Elapsed >= TrackerProbeAfter && SafeConnections() == 0)
                    {
                        probeStarted = true;
                        _ = Task.Run(async () => { try { int answered = await TrackerProbe.CountAnsweringAsync(udpTrackers, TimeSpan.FromSeconds(4), poll.Token); Volatile.Write(ref udpAnswered, answered); Volatile.Write(ref udpTested, udpTrackers.Count); } catch (Exception) { } });
                    }
                    Publish(Snapshot(Math.Max(1, attempt), attempt > 1 ? MetadataPhase.Retrying : MetadataPhase.Searching));
                    await Task.Delay(MetadataPollInterval, poll.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            while (!got && attempt < attempts && clock.Elapsed < timeout)
            {
                attempt++;
                var remaining = timeout - clock.Elapsed;
                var slice = attempt == attempts ? remaining : TimeSpan.FromTicks(Math.Min(remaining.Ticks, (FirstAttemptSlice + AttemptStep * (attempt - 1)).Ticks));
                Publish(Snapshot(attempt, attempt > 1 ? MetadataPhase.Retrying : MetadataPhase.Starting));
                // Make sure the DHT has nodes before searching: the engine's own bootstrap relies on a single router that some networks never answer.
                if (o.EnableDht) { await SeedDhtAsync(ct, force: attempt > 1); if (search.AnnounceUrls.Count == 0) await WaitForDhtAsync(timeout - clock.Elapsed, ct); } // with trackers the search starts at once; without any, the DHT is the only way to peers
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(slice);
                try
                {
                    var running = download(search, cts.Token);
                    // A cancelled search takes a while to wind down (tracker goodbyes, up to ~15 s measured). The next attempt has to wait for that,
                    // or the engine refuses it ("already registered") and the attempt is wasted; 1.13.1 waits out the wind-down within the overall
                    // time limit and keeps metadata that arrives while the search winds down.
                    var drain = timeout - clock.Elapsed + TimeSpan.FromSeconds(3);
                    var finished = await Task.WhenAny(running, Task.Delay(drain > TimeSpan.Zero ? drain : TimeSpan.Zero, ct));
                    if (finished != running) { _ = running.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default); throw new OperationCanceledException(cts.Token); }
                    meta = await running; got = true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // This attempt ran out of time: wait a moment, then announce again with a fresh search (backoff 1 s, then 2 s) as long as time is left.
                    var rest = timeout - clock.Elapsed;
                    if (attempt < attempts && rest > TimeSpan.Zero) await Delay(TimeSpan.FromTicks(Math.Min(rest.Ticks, TimeSpan.FromSeconds(attempt).Ticks)), ct);
                }
                catch (Exception ex) when (ex is InvalidOperationException or MonoTorrent.TorrentException)
                {
                    // "Already registered": the previous attempt is still winding down. Wait briefly and try again without using up an attempt
                    // (before 1.13.1 this burnt the remaining attempts and even escaped as a crash on the last one).
                    attempt = Math.Max(0, attempt - 1);
                    var rest = timeout - clock.Elapsed;
                    if (rest > TimeSpan.Zero) await Delay(TimeSpan.FromTicks(Math.Min(rest.Ticks, TimeSpan.FromSeconds(attempt + 1).Ticks)), ct);
                }
            }
        }
        finally { poll.Cancel(); try { await poller; } catch (Exception) { } }
        if (!got)
        {
            var last = Snapshot(Math.Max(1, attempt), MetadataPhase.Searching);
            Publish(last);
            throw new MetadataTimeoutException($"Magnet metadata not received within {timeout.TotalSeconds:0} s.", last);
        }

        BEncodedDictionary root;
        try
        {
            var decoded = BEncodedValue.Decode<BEncodedDictionary>(meta.Span);
            if (decoded.ContainsKey("info")) root = decoded;
            else root = new BEncodedDictionary { ["info"] = decoded };
        }
        catch (Exception ex) { throw new TorrentRejectedException("Magnet metadata is malformed: " + ex.Message); }
        // The stored torrent keeps the magnet's own trackers, plus the extra ones unless the torrent is private (a private torrent must only talk to its own tracker).
        bool isPrivate = root["info"] is BEncodedDictionary info && info.TryGetValue("private", out var flag) && flag is BEncodedNumber { Number: 1 };
        var announce = isPrivate ? magnet.AnnounceUrls.ToList() : TrackerList.Merge(magnet.AnnounceUrls, extras);
        if (announce.Count > 0 && !root.ContainsKey("announce-list"))
        {
            var tier = new BEncodedList();
            foreach (var u in announce) tier.Add((BEncodedString)u);
            root["announce-list"] = new BEncodedList { tier };
        }
        var bytes = root.Encode();
        var torrent = TorrentValidator.LoadTorrent(bytes, MaxTorrentFileBytes);
        if (torrent.InfoHashes.V1OrV2 != magnet.InfoHashes.V1OrV2)
            throw new TorrentRejectedException("Received metadata does not match the magnet's info hash.");
        return (torrent, bytes);
    }

    /// <summary>Waits (at most <see cref="DhtReadyWait"/> and never longer than <paramref name="limit"/>) until the DHT knows a few nodes, so the first peer lookup is not made blind.</summary>
    async Task WaitForDhtAsync(TimeSpan limit, CancellationToken ct)
    {
        var wait = limit < DhtReadyWait ? limit : DhtReadyWait;
        for (int steps = (int)Math.Ceiling(wait.TotalMilliseconds / 250); steps > 0 && _engineDhtCount() < DhtReadyNodes; steps--)
            await Delay(TimeSpan.FromMilliseconds(250), ct);
    }

    /// <summary>How long a metadata search may run with no connection before the UDP trackers are asked whether they answer (diagnostics only).</summary>
    public TimeSpan TrackerProbeAfter { get; set; } = TimeSpan.FromSeconds(15);
    int _engineDhtCount() { try { return Dht.NodeCount; } catch (Exception) { return 0; } }
    int SafeConnections() { try { return _engine.ConnectionManager.OpenConnections; } catch (Exception) { return 0; } }
    void Publish(MetadataStatus status)
    {
        Volatile.Write(ref _metadataStatus, status);
        var handler = MetadataProgress;
        if (handler is null || Volatile.Read(ref _disposed) != 0) return;
        try { handler(this, status); } catch (Exception) { /* a subscriber never breaks the search */ }
    }

    // ------------------------------------------------------------------ extra DHT bootstrap

    /// <summary>Few nodes (fewer than this) count as "the DHT has not started".</summary>
    public const int DhtReadyNodes = 4;

    void SeedDhtInBackground() { if (Options.EnableDht) _ = SeedDhtAsync(CancellationToken.None, force: false).ContinueWith(_ => { }, TaskScheduler.Default); }

    /// <summary>When the DHT knows fewer than <see cref="DhtReadyNodes"/> nodes, asks the routers for some and hands them over. One run at a time; a run less than
    /// 5 s after the previous one is skipped unless <paramref name="force"/> (the gap doubles up to 10 minutes while nothing is found). Never throws.</summary>
    public Task SeedDhtAsync(CancellationToken ct, bool force)
    {
        lock (_lock)
        {
            if (_seeding is { IsCompleted: false }) return _seeding;
            if (Dht.NodeCount >= DhtReadyNodes) return Task.CompletedTask;
            if (!force && (DateTime.UtcNow - _lastSeedUtc).TotalSeconds < _seedGapSeconds) return Task.CompletedTask;
            _lastSeedUtc = DateTime.UtcNow;
            return _seeding = RunSeedAsync(ct);
        }
    }

    async Task RunSeedAsync(CancellationToken ct)
    {
        try
        {
            var routers = _options.DhtRouters.Count > 0 ? _options.DhtRouters : DhtBootstrap.DefaultRouters;
            var nodes = await DhtBootstrap.FindNodesAsync(routers, RouterClient, TimeSpan.FromSeconds(3), ct);
            DhtSeedRuns++;
            if (nodes.Count > 0 && Dht.TryAdd(nodes)) { DhtSeedNodes += nodes.Count; _seedGapSeconds = 5; }
            else _seedGapSeconds = Math.Min(600, _seedGapSeconds * 2);
        }
        catch (Exception) { _seedGapSeconds = Math.Min(600, _seedGapSeconds * 2); }
    }

    string DhtCachePath => Path.Combine(_store.CacheDir, "dht_nodes.cache");

    /// <summary>Writes the DHT node table next to the engine's own cache file, so a crash or a forced stop still leaves the nodes for the next start.
    /// Only a table with at least <see cref="DhtReadyNodes"/> nodes is written; the file is replaced in one step.</summary>
    public async Task<bool> SaveDhtNodesAsync()
    {
        if (!_options.EnableDht || Dht.NodeCount < DhtReadyNodes) return false;
        var data = await Dht.TrySaveAsync();
        if (data is null || data.Length < 2 + DhtBootstrap.CompactNodeLength) return false;
        try
        {
            string temp = DhtCachePath + ".tmp";
            await File.WriteAllBytesAsync(temp, data);
            File.Move(temp, DhtCachePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    async Task<TorrentJob> AddAsync(MtTorrent torrent, byte[] fileBytes, string saveDir, bool start, int[]? priorities, long[]? cached)
    {
        await _gate.WaitAsync();
        try
        {
            ThrowIfDisposed();
            var id = HashOf(torrent.InfoHashes);
            lock (_lock) if (_entries.ContainsKey(id)) throw new InvalidOperationException("Torrent is already added: " + id);
            var job = await AddLockedAsync(torrent, fileBytes, saveDir, start, priorities, cached, persistTorrent: true);
            SaveState();
            RaiseChanged();
            return job;
        }
        finally { _gate.Release(); }
    }

    async Task<TorrentJob> AddLockedAsync(MtTorrent torrent, byte[] fileBytes, string saveDir, bool start,
        int[]? priorities, long[]? cached, bool persistTorrent)
    {
        var id = HashOf(torrent.InfoHashes);
        bool driveMissing = false;
        try { Directory.CreateDirectory(saveDir); }
        catch (Exception ex) when (!persistTorrent && ex is IOException or UnauthorizedAccessException)
        {
            // 1.13.2: a torrent restored at startup whose drive is not connected used to be skipped (and then dropped from the saved list). It is kept, stopped, with the drive message.
            driveMissing = true;
        }
        var manager = await _engine.AddAsync(torrent, saveDir, TorrentSettingsFor(_options));
        try { TorrentValidator.EnsureInside(saveDir, manager); TorrentValidator.EnsureNoReparsePoints(saveDir, manager); }
        catch { await _engine.RemoveAsync(manager, RemoveMode.KeepAllData); throw; }

        if (persistTorrent) _store.WriteTorrent(id, fileBytes);
        var entry = new Entry { Id = id, Manager = manager, SavePath = saveDir, Paused = !start };
        if (cached is not null && cached.Length == manager.Files.Count) entry.CachedFileDownloaded = cached;
        if (priorities is not null && priorities.Length == manager.Files.Count)
            for (int i = 0; i < priorities.Length; i++)
                if (priorities[i] != (int)FilePriority.Normal)
                    await manager.SetFilePriorityAsync(manager.Files[i], ToMono((FilePriority)priorities[i]));
        if (driveMissing) { entry.DriveMissing = true; entry.Paused = true; entry.Error = DriveMissingPrefix + (Path.GetPathRoot(Path.GetFullPath(saveDir)) ?? saveDir); }
        entry.Handler = (_, args) =>
        {
            MarkFinishedFiles(entry, args.NewState);
            if (args.NewState == TorrentState.Error && !entry.DriveMissing) _ = Task.Run(async () => await NoteDriveLossAsync(entry));
            RaiseChanged();
        };
        manager.TorrentStateChanged += entry.Handler;
        lock (_lock) _entries[id] = entry;
        if (start) await StartEntryAsync(entry);
        return Snapshot(entry);
    }

    /// <summary>Mark of the Web on every file that finished while this session watched the torrent download (never on files that were already there). Best effort.</summary>
    void MarkFinishedFiles(Entry e, TorrentState state)
    {
        try
        {
            if (state == TorrentState.Downloading) e.SawDownloading = true;
            bool enabled; lock (_lock) enabled = _options.MarkOfTheWeb;
            if (!enabled || !e.SawDownloading) return;
            var files = e.Manager.Files;
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                if (e.Marked.Contains(i) || f.Length == 0 || !f.BitField.AllTrue) continue;
                var path = f.DownloadCompleteFullPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                e.Marked.Add(i);
                NDM.Core.MarkOfTheWeb.TryApply(path, null, null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    // ------------------------------------------------------------------ queries

    public IReadOnlyList<TorrentJob> List()
    {
        ThrowIfDisposed();
        return Entries().Select(Snapshot).OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public TorrentJob? Get(string id)
    {
        ThrowIfDisposed();
        Entry? e; lock (_lock) _entries.TryGetValue(id ?? "", out e);
        return e is null ? null : Snapshot(e);
    }

    // ------------------------------------------------------------------ control

    public Task PauseAsync(string id) => Control(id, async e =>
    {
        e.Paused = true;
        await e.Manager.StopAsync(TimeSpan.FromSeconds(10)); // also writes fast-resume data
    });

    public Task ResumeAsync(string id) => Control(id, async e =>
    {
        e.Paused = false; e.Error = null;
        // 1.13.2: an unplugged drive used to leave the engine's torrent object in its error state for good: it kept the old error, reported "Failed" even after the drive came back
        // and a Resume only restarted that same broken handle. Resume now checks the save folder first and, when the handle is faulted or the folder was missing, replaces it with a clean one.
        var problem = CheckSaveLocation(e.SavePath);
        if (problem is not null)
        {
            e.Paused = true; e.DriveMissing = true; e.Error = DriveMissingPrefix + problem;
            try { await e.Manager.StopAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { /* already stopped or errored: the next Resume replaces the handle */ }
            return;
        }
        if (e.DriveMissing || e.Manager.State == TorrentState.Error || e.Manager.Error is not null) await RecycleManagerAsync(e);
        e.DriveMissing = false;
        await StartEntryAsync(e);
    });

    /// <summary>Start of the error text for an unreachable save location; followed by the drive or folder (for example <c>Drive not available: E:\</c>). The app shows its own localized sentence for it.</summary>
    public const string DriveMissingPrefix = "Drive not available: ";

    /// <summary>The engine reported an error state: when the cause is a save folder or drive that is gone, say so (and stop the handle) instead of leaving a raw disk error.</summary>
    async Task NoteDriveLossAsync(Entry e)
    {
        try
        {
            var problem = CheckSaveLocation(e.SavePath);
            if (problem is null || e.DriveMissing) return;
            e.DriveMissing = true; e.Paused = true; e.Error = DriveMissingPrefix + problem;
            try { await e.Manager.StopAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            RaiseChanged();
        }
        catch (Exception) { /* diagnostics only */ }
    }

    /// <summary>Null when the save folder exists (or can be created) and a file can be written in it; otherwise the drive root or folder that is not reachable.
    /// A missing drive is reported by its root so the message can name it. Creating the folder is what a start does anyway.</summary>
    public static string? CheckSaveLocation(string savePath)
    {
        try
        {
            var full = Path.GetFullPath(savePath); var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root)) return root;
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, ".neodm-write-" + Guid.NewGuid().ToString("N")[..8]);
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var root = Path.GetPathRoot(Path.GetFullPath(savePath));
            return string.IsNullOrEmpty(root) ? savePath : root;
        }
    }

    /// <summary>Replaces the engine's torrent object by a new one built from the stored .torrent file: same save folder, same file priorities, no leftover error state. Caller holds the gate.</summary>
    async Task RecycleManagerAsync(Entry e)
    {
        var old = e.Manager;
        var priorities = old.Files.Select(f => FromMono(f.Priority)).ToArray();
        old.TorrentStateChanged -= e.Handler;
        try { await old.StopAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { /* a stopped or errored handle: it is being thrown away */ }
        try { await _engine.RemoveAsync(old, RemoveMode.KeepAllData); } catch (Exception) { /* the engine may already have dropped it */ }
        var bytes = await File.ReadAllBytesAsync(_store.TorrentPath(e.Id));
        var torrent = TorrentValidator.LoadTorrent(bytes, MaxTorrentFileBytes);
        var manager = await _engine.AddAsync(torrent, e.SavePath, TorrentSettingsFor(_options));
        for (int i = 0; i < priorities.Length && i < manager.Files.Count; i++)
            if (priorities[i] != FilePriority.Normal) await manager.SetFilePriorityAsync(manager.Files[i], ToMono(priorities[i]));
        e.Manager = manager;
        manager.TorrentStateChanged += e.Handler;
    }

    /// <summary>Removes the torrent. With <paramref name="deleteFiles"/>, deletes its downloaded files
    /// (only paths verified to be inside the save directory) and now-empty folders.</summary>
    public async Task RemoveAsync(string id, bool deleteFiles) => await RemoveDetailedAsync(id, deleteFiles);

    /// <summary>Same as <see cref="RemoveAsync"/> but reports how many of the torrent's files were deleted and how many could not be (locked/read-only).</summary>
    public async Task<(int Deleted, int Failed)> RemoveDetailedAsync(string id, bool deleteFiles)
    {
        (int Deleted, int Failed) report = (0, 0);
        ThrowIfDisposed();
        await _gate.WaitAsync();
        try
        {
            ThrowIfDisposed();
            var e = Require(id);
            var m = e.Manager;
            m.TorrentStateChanged -= e.Handler;
            try { await m.StopAsync(TimeSpan.FromSeconds(10)); } catch { /* removing anyway */ }
            var paths = m.Files.SelectMany(f => new[] { f.FullPath, f.DownloadCompleteFullPath, f.DownloadIncompleteFullPath })
                .Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            await _engine.RemoveAsync(m, RemoveMode.CacheDataOnly);
            lock (_lock) _entries.Remove(id);
            try { File.Delete(_store.TorrentPath(id)); } catch { /* best effort */ }
            if (deleteFiles) report = await DeleteFilesAsync(e.SavePath, paths);
            SaveState();
        }
        finally { _gate.Release(); }
        RaiseChanged();
        return report;
    }

    public Task SetFilePriorityAsync(string id, int fileIndex, FilePriority priority) => Control(id, async e =>
    {
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        var files = e.Manager.Files;
        if (fileIndex < 0 || fileIndex >= files.Count) throw new ArgumentOutOfRangeException(nameof(fileIndex));
        await e.Manager.SetFilePriorityAsync(files[fileIndex], ToMono(priority));
    });

    public Task SkipFileAsync(string id, int fileIndex) => SetFilePriorityAsync(id, fileIndex, FilePriority.Skip);

    /// <summary>Adds a manual peer (e.g. a known LAN/loopback peer) to an existing torrent.</summary>
    public async Task<bool> AddPeerAsync(string id, IPEndPoint peer)
    {
        ThrowIfDisposed();
        var e = Require(id);
        return await e.Manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://{peer}")));
    }

    async Task Control(string id, Func<Entry, Task> action)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync();
        try
        {
            ThrowIfDisposed();
            await action(Require(id));
            SaveState();
        }
        finally { _gate.Release(); }
        RaiseChanged();
    }

    async Task StartEntryAsync(Entry e)
    {
        try { await e.Manager.StartAsync(); }
        catch (Exception ex) { e.Error = ex.Message; }
    }

    static async Task<(int Deleted, int Failed)> DeleteFilesAsync(string saveDir, IEnumerable<string> paths)
    {
        int deleted = 0, failed = 0;
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (!TorrentValidator.IsInside(saveDir, p)) continue; // never touch anything outside the save dir
            // A handle the engine or a scanner still holds can release a moment after the manager stops: retry briefly before reporting a locked file.
            for (var attempt = 0; ; attempt++)
            {
                try { if (File.Exists(p)) { File.Delete(p); deleted++; } break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 7) { failed++; break; } // locked/readonly: leave it
                    await Task.Delay(150);
                }
                catch { failed++; break; }
            }
            for (var d = Path.GetDirectoryName(Path.GetFullPath(p)); d is not null && TorrentValidator.IsInside(saveDir, d); d = Path.GetDirectoryName(d))
                dirs.Add(d);
        }
        foreach (var d in dirs.OrderByDescending(x => x.Length))
            try { Directory.Delete(d, false); } catch { /* not empty or in use */ }
        return (deleted, failed);
    }

    // ------------------------------------------------------------------ snapshots

    Entry Require(string id)
    {
        lock (_lock) if (_entries.TryGetValue(id ?? "", out var e)) return e;
        throw new KeyNotFoundException("Unknown torrent: " + id);
    }

    List<Entry> Entries() { lock (_lock) return _entries.Values.ToList(); }

    TorrentJob Snapshot(Entry e)
    {
        var m = e.Manager;
        var files = m.Files;
        bool live = m.HashChecked;
        var list = new List<TorrentFileJob>(files.Count);
        long total = 0, selected = 0, downloaded = 0, selectedDone = 0;
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            long done = live
                ? (f.BitField.AllTrue ? f.Length : (long)(f.Length * f.BitField.PercentComplete / 100.0))
                : (i < e.CachedFileDownloaded.Length ? e.CachedFileDownloaded[i] : 0);
            var pr = FromMono(f.Priority);
            list.Add(new TorrentFileJob(i, f.Path, f.Length, done, f.Length == 0 ? 100 : done * 100.0 / f.Length, pr, f.FullPath));
            total += f.Length; downloaded += done;
            if (pr != FilePriority.Skip) { selected += f.Length; selectedDone += done; }
        }
        bool complete = selected > 0 && selectedDone >= selected;
        string? error = e.Error ?? m.Error?.Exception?.Message;
        var state = (error != null || m.State == TorrentState.Error) ? "Failed" : m.State switch
        {
            TorrentState.Hashing or TorrentState.HashingPaused or TorrentState.FetchingHashes => "Checking",
            TorrentState.Downloading => complete ? "Seeding" : "Downloading",
            TorrentState.Seeding => "Seeding",
            TorrentState.Starting or TorrentState.Metadata => "Queued",
            _ => complete ? "Completed" : "Paused",
        };
        bool active = state is "Downloading" or "Seeding" or "Checking" or "Queued";
        return new TorrentJob(e.Id, m.Name, e.SavePath, total, selected, downloaded,
            selected == 0 ? 0 : Math.Min(100, selectedDone * 100.0 / selected),
            active ? m.Monitor.DownloadRate : 0, active ? m.Monitor.UploadRate : 0,
            m.Peers.Seeds, m.Peers.Leechs, m.OpenConnections,
            (double)m.Monitor.DataBytesSent / Math.Max(1, downloaded), state, error, list);
    }

    static string HashOf(InfoHashes h) => Convert.ToHexString(h.V1OrV2.Span).ToLowerInvariant();

    static Priority ToMono(FilePriority p) => p switch
    {
        FilePriority.Skip => Priority.DoNotDownload,
        FilePriority.Low => Priority.Low,
        FilePriority.High => Priority.High,
        _ => Priority.Normal,
    };

    static FilePriority FromMono(Priority p) => p switch
    {
        Priority.DoNotDownload => FilePriority.Skip,
        Priority.Lowest or Priority.Low => FilePriority.Low,
        Priority.High or Priority.Highest or Priority.Immediate => FilePriority.High,
        _ => FilePriority.Normal,
    };

    // ------------------------------------------------------------------ persistence

    void SaveState()
    {
        var st = new SavedState();
        foreach (var e in Entries())
        {
            var s = Snapshot(e);
            if (e.Manager.HashChecked) e.CachedFileDownloaded = s.Files.Select(f => f.DownloadedBytes).ToArray();
            st.Torrents.Add(new SavedTorrent
            {
                Id = e.Id, SavePath = e.SavePath, TorrentFile = e.Id + ".torrent",
                Priorities = s.Files.Select(f => (int)f.Priority).ToArray(),
                FileDownloaded = e.CachedFileDownloaded.Length == s.Files.Count ? e.CachedFileDownloaded : s.Files.Select(f => f.DownloadedBytes).ToArray(),
            });
        }
        _store.Save(st);
    }

    async Task RestoreAsync()
    {
        var warnings = new List<string>();
        var st = _store.Load(out var w);
        if (w is not null) warnings.Add(w);
        await _gate.WaitAsync();
        try
        {
            foreach (var s in st.Torrents)
            {
                try
                {
                    var file = _store.TorrentPath(s.Id);
                    var bytes = await File.ReadAllBytesAsync(file);
                    var torrent = TorrentValidator.LoadTorrent(bytes, MaxTorrentFileBytes);
                    if (HashOf(torrent.InfoHashes) != s.Id) throw new TorrentRejectedException("stored torrent does not match its id");
                    var dir = TorrentValidator.NormalizeSaveDir(s.SavePath);
                    await AddLockedAsync(torrent, bytes, dir, start: false, s.Priorities, s.FileDownloaded, persistTorrent: false);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Skipped torrent {s.Id}: {ex.Message}");
                }
            }
        }
        finally { _gate.Release(); }
        RestoreWarnings = warnings;
    }

    // ------------------------------------------------------------------ events / lifetime

    void Tick()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            foreach (var e in Entries()) if (e.SawDownloading && e.Marked.Count < e.Manager.Files.Count) MarkFinishedFiles(e, e.Manager.State); // files that finish between two state changes
            if (Entries().Any(e => e.Manager.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Hashing or TorrentState.Starting))
                RaiseChanged();
            // DHT upkeep: a DHT that is still empty is seeded again (with growing gaps); a filled one is saved every five minutes.
            _tickCount++;
            if (Options.EnableDht)
            {
                if (_tickCount % 30 == 0 && Dht.NodeCount < DhtReadyNodes) _ = SeedDhtAsync(CancellationToken.None, force: false);
                else if (_tickCount % 300 == 0) _ = SaveDhtNodesAsync();
            }
        }
        catch { /* timer must never throw */ }
    }

    void RaiseChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var h = Changed;
        if (h is null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { h(this, EventArgs.Empty); } catch { /* subscriber errors never break the engine */ }
        });
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>Stops all torrents (writing fast-resume data), saves the torrent list and releases the engine. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _timer.DisposeAsync();
        await _gate.WaitAsync();
        try
        {
            foreach (var e in Entries())
            {
                if (e.Handler is not null) e.Manager.TorrentStateChanged -= e.Handler;
                try { await e.Manager.StopAsync(TimeSpan.FromSeconds(10)); } catch { /* shutting down */ }
            }
            try { SaveState(); } catch { /* shutting down */ }
            try { _engine.Dispose(); } catch { /* shutting down */ }
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => Task.Run(() => DisposeAsync().AsTask()).GetAwaiter().GetResult();
}
