using System.IO;
using NDM.Torrent;

namespace NDM.App;

/// <summary>What the file-selection dialog hands back; everything the controller needs to add a torrent.</summary>
public sealed record TorrentAddSelection(string? MagnetUri, byte[]? TorrentData, string Name, string SaveDirectory,
    IReadOnlyList<int>? SelectedFileIndexes, int TotalFileCount, bool StartNow);

/// <summary>Chooses the default save folder: the torrent-specific folder if set, else the same category rules as HTTP downloads.</summary>
public static class TorrentSavePlanner
{
    public static string DefaultFolder(AppSettings settings, IReadOnlyList<(string Path, long Length)> files)
    {
        if (!string.IsNullOrWhiteSpace(settings.TorrentDownloadDirectory)) return settings.TorrentDownloadDirectory;
        if (files.Count == 0) return settings.DownloadDirectory;
        var largest = files.OrderByDescending(f => f.Length).First();
        return CategoryHelper.GetDownloadPath(Path.GetFileName(largest.Path.Replace('/', Path.DirectorySeparatorChar)), settings);
    }
}

/// <summary>
/// Owns the lazily created <see cref="TorrentService"/>: no engine, no listening socket until the user has torrents
/// (saved ones at startup, or the first add). Disposing stops every torrent and writes fast-resume data.
/// </summary>
public sealed class TorrentController : IDisposable
{
    private readonly string _stateDirectory;
    private readonly Func<AppSettings, TorrentServiceOptions> _options;
    private readonly AppSettings _settings;
    private readonly object _lock = new();
    private Task<TorrentService>? _starting;
    private TorrentService? _service;
    private bool _disposed;
    private HashSet<string> _restored = new();
    public event Action? Changed;
    public string? StartError { get; private set; }
    /// <summary>Torrents that existed when the engine started: history for 'when downloads finish', not completions.</summary>
    public IReadOnlySet<string> RestoredIds => _restored;
    public TorrentService? Service => _service;
    public bool IsStarted => _service is not null;

    public TorrentController(string stateDirectory, AppSettings settings, Func<AppSettings, TorrentServiceOptions>? options = null)
    { _stateDirectory = stateDirectory; _settings = settings; _options = options ?? ToOptions; }

    public static bool HasSavedTorrents(string stateDirectory) => File.Exists(Path.Combine(stateDirectory, "torrents.json"));

    public static TorrentServiceOptions ToOptions(AppSettings s)
    {
        var proxy = s.BuildProxy();
        var peerProxy = proxy is not null && s.ProxyForPeers ? proxy : null;
        var trackerProxy = proxy is not null && s.ProxyForTrackers ? proxy : null;
        return new()
        {
            ListenPort = s.TorrentListenPort, EnablePex = s.TorrentEnablePex,
            // DHT and local discovery are UDP/multicast and cannot go through the proxy: off while peers are proxied, so the real address is not exposed.
            EnableDht = s.TorrentEnableDht && peerProxy is null, EnableLsd = s.TorrentEnableLsd && peerProxy is null,
            EnablePortForwarding = s.TorrentEnablePortForwarding && peerProxy is null,
            Encryption = s.TorrentEncryption switch { TorrentEncryptionMode.Required => TorrentEncryption.Required, TorrentEncryptionMode.Disabled => TorrentEncryption.Disabled, _ => TorrentEncryption.Enabled },
            MaxConnections = s.TorrentMaxConnections, MarkOfTheWeb = s.MarkDownloadedFiles,
            MaxDownloadRate = KiBToBytes(s.TorrentMaxDownloadKiB), MaxUploadRate = KiBToBytes(s.TorrentMaxUploadKiB),
            PeerProxy = peerProxy, TrackerProxy = trackerProxy
        };
    }
    private static int KiBToBytes(int kib) => (int)Math.Min(int.MaxValue, Math.Max(0L, kib) * 1024L);

    /// <summary>Single-flight start. A failure (e.g. port in use) is reported and the next call retries.</summary>
    public Task<TorrentService> EnsureAsync()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _starting ??= StartAsync();
        }
    }
    private async Task<TorrentService> StartAsync()
    {
        try
        {
            var service = await Task.Run(() => TorrentService.CreateAsync(_stateDirectory, _options(_settings)));
            _restored = service.List().Select(j => j.Id).ToHashSet();
            lock (_lock)
            {
                if (_disposed) { _ = service.DisposeAsync().AsTask(); throw new ObjectDisposedException(nameof(TorrentController)); }
                _service = service; StartError = null;
            }
            service.Changed += (_, _) => Changed?.Invoke();
            Changed?.Invoke();
            return service;
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            lock (_lock) { _starting = null; StartError = ex.Message; }
            throw;
        }
    }

    public IReadOnlyList<TorrentJob> List()
    {
        try { return _service?.List() ?? []; }
        catch (ObjectDisposedException) { return []; }
    }

    public async Task ApplySettingsAsync(AppSettings s)
    {
        if (_service is null) return; // The next start reads the saved settings.
        var o = _options(s);
        await _service.UpdateOptionsAsync(t =>
        {
            t.ListenPort = o.ListenPort; t.EnableDht = o.EnableDht; t.EnablePex = o.EnablePex; t.EnableLsd = o.EnableLsd;
            t.EnablePortForwarding = o.EnablePortForwarding; t.Encryption = o.Encryption; t.MaxConnections = o.MaxConnections;
            t.MaxDownloadRate = o.MaxDownloadRate; t.MaxUploadRate = o.MaxUploadRate;
            t.PeerProxy = o.PeerProxy; t.TrackerProxy = o.TrackerProxy;
        });
    }

    /// <summary>Adds without starting, applies the file selection (skipped files never download), then starts if requested.</summary>
    public async Task<TorrentJob> AddAsync(TorrentAddSelection selection, TimeSpan? metadataTimeout = null, CancellationToken ct = default)
    {
        var service = await EnsureAsync();
        TorrentJob job;
        if (selection.TorrentData is { } data) job = await service.AddTorrentFileAsync(data, selection.SaveDirectory, start: false, ct);
        else if (selection.MagnetUri is { } magnet) job = await service.AddMagnetAsync(magnet, selection.SaveDirectory, metadataTimeout, start: false, ct);
        else throw new ArgumentException("A magnet link or torrent data is required.", nameof(selection));
        try
        {
            if (selection.SelectedFileIndexes is { } indexes)
            {
                if (job.Files.Count != selection.TotalFileCount) throw new InvalidOperationException("The torrent's file list changed after it was previewed.");
                var keep = indexes.ToHashSet();
                foreach (var file in job.Files.Where(f => !keep.Contains(f.Index))) await service.SkipFileAsync(job.Id, file.Index);
            }
            if (selection.StartNow) await service.ResumeAsync(job.Id);
        }
        catch { try { await service.RemoveAsync(job.Id, deleteFiles: false); } catch { } throw; }
        return service.Get(job.Id) ?? job;
    }

    public async Task PauseAsync(string id) => await (await EnsureAsync()).PauseAsync(id);
    public async Task ResumeAsync(string id) => await (await EnsureAsync()).ResumeAsync(id);
    public async Task<(int Deleted, int Failed)> RemoveAsync(string id, bool deleteFiles) => await (await EnsureAsync()).RemoveDetailedAsync(id, deleteFiles);

    /// <summary>With 'stop seeding at ratio' enabled, pauses seeding torrents whose ratio reached the limit. Returns how many were stopped.</summary>
    public async Task<int> EnforceSeedLimitAsync()
    {
        if (_service is null || !_settings.TorrentStopSeedingAtRatio) return 0;
        int stopped = 0;
        foreach (var job in List().Where(j => j.State == "Seeding" && j.Ratio >= _settings.TorrentSeedRatioLimit))
        {
            try { await _service.PauseAsync(job.Id); stopped++; } catch (Exception ex) when (ex is KeyNotFoundException or ObjectDisposedException or InvalidOperationException) { }
        }
        return stopped;
    }

    /// <summary>Magnet from the stored identity (info hash + name). Tracker lists are not kept in the snapshot.</summary>
    public static string MagnetFor(TorrentJob job) => "magnet:?xt=urn:btih:" + job.Id + "&dn=" + Uri.EscapeDataString(job.Name);

    public void Dispose()
    {
        TorrentService? service;
        lock (_lock) { if (_disposed) return; _disposed = true; service = _service; }
        // Stops every torrent (fast-resume written) and saves the list; blocks the exit path on purpose so state is durable.
        try { service?.Dispose(); } catch { }
    }
}
