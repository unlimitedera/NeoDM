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

    public static readonly TimeSpan DefaultMetadataTimeout = TimeSpan.FromSeconds(90);

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

    async Task<(MtTorrent, byte[])> ResolveMagnetAsync(MagnetLink magnet, TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var fetch = _engine.DownloadMetadataAsync(magnet, cts.Token);
        ReadOnlyMemory<byte> meta;
        try { meta = await fetch; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Magnet metadata not received within {timeout.TotalSeconds:0} s.");
        }
        finally { cts.Cancel(); }

        BEncodedDictionary root;
        try
        {
            var decoded = BEncodedValue.Decode<BEncodedDictionary>(meta.Span);
            if (decoded.ContainsKey("info")) root = decoded;
            else root = new BEncodedDictionary { ["info"] = decoded };
        }
        catch (Exception ex) { throw new TorrentRejectedException("Magnet metadata is malformed: " + ex.Message); }
        if (magnet.AnnounceUrls.Count > 0 && !root.ContainsKey("announce-list"))
        {
            var tier = new BEncodedList();
            foreach (var u in magnet.AnnounceUrls) tier.Add((BEncodedString)u);
            root["announce-list"] = new BEncodedList { tier };
        }
        var bytes = root.Encode();
        var torrent = TorrentValidator.LoadTorrent(bytes, MaxTorrentFileBytes);
        if (torrent.InfoHashes.V1OrV2 != magnet.InfoHashes.V1OrV2)
            throw new TorrentRejectedException("Received metadata does not match the magnet's info hash.");
        return (torrent, bytes);
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
        Directory.CreateDirectory(saveDir);
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
        entry.Handler = (_, args) => { MarkFinishedFiles(entry, args.NewState); RaiseChanged(); };
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
        await StartEntryAsync(e);
    });

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
            if (deleteFiles) report = DeleteFiles(e.SavePath, paths);
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

    static (int Deleted, int Failed) DeleteFiles(string saveDir, IEnumerable<string> paths)
    {
        int deleted = 0, failed = 0;
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (!TorrentValidator.IsInside(saveDir, p)) continue; // never touch anything outside the save dir
            try { if (File.Exists(p)) { File.Delete(p); deleted++; } } catch { failed++; /* locked/readonly: leave it */ }
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
