using System.IO;
using System.Windows;
using Microsoft.Win32;
using NDM.Torrent;

using NDM.App.Localization;
namespace NDM.App;

/// <summary>Torrent support: wraps <see cref="TorrentController"/>
/// and owns the torrent dialogs, so <see cref="MainWindow"/> only talks to <see cref="ITorrentFeature"/>.</summary>
public sealed class TorrentFeature : ITorrentFeature
{
    private readonly TorrentController _controller;
    private readonly AppSettings _settings;
    private readonly HashSet<string> _known = new();
    private readonly Dictionary<string, TorrentDetailsWindow> _windows = new();
    private MainWindow? _window;

    public TorrentFeature(TorrentController controller, AppSettings settings) { _controller = controller; _settings = settings; }
    public TorrentController Controller => _controller;
    public bool IsAvailable => true;
    public string UnavailableMessage => "";
    public void Attach(MainWindow window) => _window = window;
    private MainWindow Window => _window ?? throw new InvalidOperationException("Torrent support is not attached to a window.");

    public void CollectRowIds(ISet<string> ids) { foreach (var t in _controller.List()) ids.Add(DownloadRow.TorrentRowId(t.Id)); }

    public void SyncRows(IList<DownloadRow> rows, IDictionary<string, DownloadRow> lookup, ref bool filterChanged, ref bool sortChanged)
    {
        foreach (var torrent in _controller.List())
        {
            if (lookup.TryGetValue(DownloadRow.TorrentRowId(torrent.Id), out var row))
            {
                filterChanged |= row.Status != torrent.State || row.FileName != torrent.Name;
                sortChanged |= row.Update(torrent);
            }
            else rows.Insert(0, new DownloadRow(torrent));
        }
    }

    /// <summary>Downloading/checking torrents are active; seeding and completed ones count as completed, never active.
    /// Torrents restored from the previous session are history, so reopening the app never triggers the finish action.</summary>
    public IReadOnlyList<(QueueJob Job, bool RestoredHistory)> QueueSnapshot()
    {
        var result = new List<(QueueJob, bool)>();
        foreach (var t in _controller.List())
        {
            var status = t.State switch { "Downloading" or "Checking" or "Queued" => "Downloading", "Seeding" or "Completed" => "Completed", var other => other };
            var job = new QueueJob(DownloadRow.TorrentRowId(t.Id), status, null);
            result.Add((job, _known.Add(t.Id) && _controller.RestoredIds.Contains(t.Id)));
        }
        return result;
    }

    public Task<int> EnforceSeedLimitAsync() => _settings.TorrentStopSeedingAtRatio ? _controller.EnforceSeedLimitAsync() : Task.FromResult(0);
    public Task PauseAsync(DownloadRow row) => _controller.PauseAsync(row.TorrentId);
    public Task ResumeAsync(DownloadRow row) => _controller.ResumeAsync(row.TorrentId);
    public Task<(int Deleted, int Failed)> RemoveAsync(string torrentId, bool deleteFiles) => _controller.RemoveAsync(torrentId, deleteFiles);
    public IReadOnlyList<string> CompletedIds() => _controller.List().Where(t => t.State == "Completed").Select(t => t.Id).ToArray();
    public bool KnowsTorrent(string id) => _controller.List().Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    private static bool Eligible(string state, bool resume) => resume ? state == "Paused" : state is "Downloading" or "Queued" or "Checking" or "Seeding";
    public async Task<(int Changed, int Skipped, int Failed)> ChangeAllAsync(bool resume)
    {
        int changed = 0, skipped = 0, failed = 0;
        foreach (var id in _controller.List().Where(t => Eligible(t.State, resume)).Select(t => t.Id).ToArray())
        {
            try
            {
                var current = _controller.List().FirstOrDefault(t => t.Id == id);
                if (current is null || !Eligible(current.State, resume)) { skipped++; continue; }
                if (resume) await _controller.ResumeAsync(id); else await _controller.PauseAsync(id);
                changed++;
            }
            catch (Exception) { failed++; }
        }
        return (changed, skipped, failed);
    }

    public Task AddMagnetFromAddDialogAsync(string magnet) => ShowMagnetDialogAsync(magnet);

    private async Task ShowMagnetDialogAsync(string magnet)
    {
        var dialog = new TorrentAddWindow(_settings, magnet, null, async (uri, timeout, ct) => await (await _controller.EnsureAsync()).FetchMetadataAsync(uri, timeout, ct)) { Owner = Window };
        await FinishAddAsync(dialog);
    }

    public async Task AddMagnetAsync(string magnet)
    {
        var window = Window;
        try { TorrentValidator.ParseMagnet(magnet); }
        catch (TorrentRejectedException ex) { Dialogs.Show(window, ErrorMessages.Describe(ex.Message), Loc.T("torrent.dialog.magnet_rejected"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        await ShowMagnetDialogAsync(magnet.Trim());
    }

    public async Task AddTorrentPathAsync(string path)
    {
        var window = Window;
        byte[] data;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException();
            if (info.Length > TorrentValidator.DefaultMaxTorrentFileBytes) throw new TorrentRejectedException("Torrent file is larger than 8 MiB.");
            data = await File.ReadAllBytesAsync(path);
        }
        catch (TorrentRejectedException ex) { Dialogs.Show(window, ErrorMessages.Describe(ex.Message), Loc.T("torrent.dialog.file_rejected"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Dialogs.Show(window, Loc.T("torrent.dialog.file_unreadable"), Loc.T("torrent.dialog.file_rejected"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        await FinishAddAsync(new TorrentAddWindow(_settings, null, data, null, null, path) { Owner = window });
    }

    public async Task PickTorrentFileAsync()
    {
        var picker = new OpenFileDialog { Title = Loc.T("torrent.picker.title"), Filter = Loc.T("torrent.picker.filter"), CheckFileExists = true };
        if (picker.ShowDialog(Window) == true) await Window.AddTorrentPathAsync(picker.FileName);
    }

    private async Task FinishAddAsync(TorrentAddWindow dialog)
    {
        var window = Window;
        if (dialog.ShowDialog() != true || dialog.Selection is not { } selection) return;
        window.SetNotice(Loc.T("torrent.notice.adding"));
        try
        {
            var job = await _controller.AddAsync(selection, TimeSpan.FromSeconds(90));
            window.ShowAndSelect(DownloadRow.TorrentRowId(job.Id));
            window.SetNotice(Loc.T(selection.StartNow ? "torrent.notice.added_started" : "torrent.notice.added_paused"));
        }
        catch (InvalidOperationException) { window.RefreshNow(); window.SetNotice(Loc.T("torrent.notice.duplicate")); Dialogs.Show(window, Loc.T("torrent.notice.duplicate"), Loc.T("torrent.dialog.not_added"), MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (TorrentRejectedException ex) { window.RefreshNow(); window.SetNotice(Loc.T("torrent.notice.not_added")); Dialogs.Show(window, ErrorMessages.Describe(ex.Message), Loc.T("torrent.dialog.not_added"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (TimeoutException) { window.RefreshNow(); window.SetNotice(Loc.T("torrent.notice.metadata_timeout")); Dialogs.Show(window, Loc.T("torrent.dialog.metadata_timeout"), Loc.T("torrent.dialog.not_added"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (Exception) { window.RefreshNow(); window.SetNotice(Loc.T("torrent.notice.not_added")); Dialogs.Show(window, Loc.T("torrent.dialog.add_failed"), Loc.T("torrent.dialog.not_added"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    public void ShowDetails(DownloadRow row)
    {
        if (_windows.TryGetValue(row.TorrentId, out var existing)) { existing.Activate(); return; }
        var window = new TorrentDetailsWindow(_controller, row.TorrentId) { Owner = Window };
        _windows[row.TorrentId] = window;
        window.Closed += (_, _) => _windows.Remove(row.TorrentId);
        window.Show();
    }

    public async Task ApplySettingsAsync(AppSettings settings)
    {
        try { await _controller.ApplySettingsAsync(settings); }
        catch (Exception) { Window.SetNotice(Loc.T("torrent.notice.settings_not_applied")); }
    }

    public string? ValidateMagnet(string magnet, out string id)
    {
        id = "";
        try { id = Convert.ToHexString(TorrentValidator.ParseMagnet(magnet).InfoHashes.V1OrV2.Span); return null; }
        catch (TorrentRejectedException ex) { return ex.Message; }
    }

    public async Task<(bool Ok, string Message)> ImportMagnetAsync(string magnet, TimeSpan timeout)
    {
        string folder = _settings.TorrentDownloadDirectory.Length > 0 ? _settings.TorrentDownloadDirectory : _settings.DownloadDirectory;
        try { await _controller.AddAsync(new TorrentAddSelection(magnet, null, "", folder, null, 0, true), timeout); return (true, Loc.T("bulk.torrent_added")); }
        catch (TimeoutException) { return (false, Loc.T("bulk.torrent_timeout")); }
        catch (InvalidOperationException) { return (false, Loc.T("bulk.torrent_duplicate")); }
        catch (Exception) { return (false, Loc.T("bulk.result.not_saved_destination")); }
    }

    public void Dispose() => _controller.Dispose();
}
