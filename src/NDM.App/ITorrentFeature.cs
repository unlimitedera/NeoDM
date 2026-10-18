using System.Collections.ObjectModel;

namespace NDM.App;

/// <summary>Everything the main window needs from torrent support. The product runs with <c>TorrentFeature</c>;
/// <see cref="NoTorrentFeature"/> is a null object for windows and tests that have no torrent engine.</summary>
public interface ITorrentFeature : IDisposable
{
    bool IsAvailable { get; }
    /// <summary>Shown when a magnet link or .torrent file reaches a build without torrent support.</summary>
    string UnavailableMessage { get; }
    void Attach(MainWindow window);
    /// <summary>Row ids (<c>bt:&lt;hash&gt;</c>) that currently exist.</summary>
    void CollectRowIds(ISet<string> ids);
    /// <summary>Creates/updates torrent rows. Sets the flags when the visible list or its sort order may have changed.</summary>
    void SyncRows(IList<DownloadRow> rows, IDictionary<string, DownloadRow> lookup, ref bool filterChanged, ref bool sortChanged);
    /// <summary>Torrent rows for the 'when downloads finish' observer; the flag marks entries restored from the previous session (history).</summary>
    IReadOnlyList<(QueueJob Job, bool RestoredHistory)> QueueSnapshot();
    Task<int> EnforceSeedLimitAsync();
    Task PauseAsync(DownloadRow row);
    Task ResumeAsync(DownloadRow row);
    /// <summary>Removes the torrent; with deleteFiles also its own files (inside the save folder only). Returns how many files were deleted / could not be.</summary>
    Task<(int Deleted, int Failed)> RemoveAsync(string torrentId, bool deleteFiles);
    IReadOnlyList<string> CompletedIds();
    Task<(int Changed, int Skipped, int Failed)> ChangeAllAsync(bool resume);
    Task AddMagnetFromAddDialogAsync(string magnet);
    Task AddMagnetAsync(string magnet);
    Task AddTorrentPathAsync(string path);
    Task PickTorrentFileAsync();
    void ShowDetails(DownloadRow row);
    Task ApplySettingsAsync(AppSettings settings);
    /// <summary>Bulk import of one magnet line; returns a short result text and success flag.</summary>
    Task<(bool Ok, string Message)> ImportMagnetAsync(string magnet, TimeSpan timeout);
    /// <summary>Null when the magnet is acceptable, otherwise the reason it is not (also null-identity info hash via <paramref name="id"/>).</summary>
    string? ValidateMagnet(string magnet, out string id);
    bool KnowsTorrent(string id);
}

/// <summary>Null object: no torrent engine attached.</summary>
public sealed class NoTorrentFeature : ITorrentFeature
{
    public bool IsAvailable => false;
    public string UnavailableMessage => Localization.Loc.T("torrent.unavailable");
    public void Attach(MainWindow window) { }
    public void CollectRowIds(ISet<string> ids) { }
    public void SyncRows(IList<DownloadRow> rows, IDictionary<string, DownloadRow> lookup, ref bool filterChanged, ref bool sortChanged) { }
    public IReadOnlyList<(QueueJob Job, bool RestoredHistory)> QueueSnapshot() => Array.Empty<(QueueJob, bool)>();
    public Task<int> EnforceSeedLimitAsync() => Task.FromResult(0);
    public Task PauseAsync(DownloadRow row) => Task.CompletedTask;
    public Task ResumeAsync(DownloadRow row) => Task.CompletedTask;
    public Task<(int Deleted, int Failed)> RemoveAsync(string torrentId, bool deleteFiles) => Task.FromResult((0, 0));
    public IReadOnlyList<string> CompletedIds() => Array.Empty<string>();
    public Task<(int Changed, int Skipped, int Failed)> ChangeAllAsync(bool resume) => Task.FromResult((0, 0, 0));
    public Task AddMagnetFromAddDialogAsync(string magnet) => Task.CompletedTask;
    public Task AddMagnetAsync(string magnet) => Task.CompletedTask;
    public Task AddTorrentPathAsync(string path) => Task.CompletedTask;
    public Task PickTorrentFileAsync() => Task.CompletedTask;
    public void ShowDetails(DownloadRow row) { }
    public Task ApplySettingsAsync(AppSettings settings) => Task.CompletedTask;
    public Task<(bool Ok, string Message)> ImportMagnetAsync(string magnet, TimeSpan timeout) => Task.FromResult((false, UnavailableMessage));
    public string? ValidateMagnet(string magnet, out string id) { id = ""; return UnavailableMessage; }
    public bool KnowsTorrent(string id) => false;
    public void Dispose() { }
}
