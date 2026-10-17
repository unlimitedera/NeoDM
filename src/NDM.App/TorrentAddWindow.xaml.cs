using System.IO;
using System.Windows;
using Microsoft.Win32;
using NDM.Torrent;

namespace NDM.App;

public delegate Task<TorrentMetadata> MetadataFetcher(string magnetUri, TimeSpan timeout, CancellationToken ct);

/// <summary>File-selection step shown before any torrent is added. Magnets fetch metadata first (progress, timeout, cancel).</summary>
public partial class TorrentAddWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string? _magnet;
    private readonly byte[]? _data;
    private readonly MetadataFetcher? _fetch;
    private readonly TimeSpan _timeout;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<TorrentFileNode> _roots = Array.Empty<TorrentFileNode>();
    private string _name = "";
    private int _fileCount;
    private bool _folderEdited;
    public TorrentAddSelection? Selection { get; private set; }
    /// <summary>Completes when metadata is loaded or failed; lets tests await the fetch.</summary>
    public Task MetadataTask { get; private set; } = Task.CompletedTask;
    public bool MetadataReady { get; private set; }
    public string? MetadataError { get; private set; }

    public TorrentAddWindow(AppSettings settings, string? magnet, byte[]? torrentData, MetadataFetcher? fetch, TimeSpan? metadataTimeout = null)
    {
        _settings = settings; _magnet = magnet; _data = torrentData; _fetch = fetch; _timeout = metadataTimeout ?? TorrentService.DefaultMetadataTimeout;
        InitializeComponent(); WindowPlacements.Track(this, "TorrentAdd");
        SaveFolder.Text = settings.TorrentDownloadDirectory.Length > 0 ? settings.TorrentDownloadDirectory : settings.DownloadDirectory;
        SaveFolder.TextChanged += (_, _) => { _folderEdited |= SaveFolder.IsKeyboardFocusWithin; UpdateSpace(); };
        SourceText.Text = magnet ?? "Torrent file";
        Closed += (_, _) => { _cts?.Cancel(); };
        Loaded += (_, _) => Begin();
    }

    private void Begin()
    {
        if (_data is not null) { FetchPanel.Visibility = Visibility.Collapsed; LoadFromTorrentData(_data); return; }
        MetadataTask = FetchAsync();
    }

    /// <summary>Starts (or restarts) the metadata fetch for a magnet.</summary>
    public Task StartMetadataFetch() => MetadataTask = FetchAsync();

    private async Task FetchAsync()
    {
        if (_magnet is null || _fetch is null) { ShowMetadataError("No torrent engine is available."); return; }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        RetryButton.Visibility = Visibility.Collapsed; FetchBar.Visibility = Visibility.Visible; FetchPanel.Visibility = Visibility.Visible; ErrorText.Text = "";
        var started = DateTime.UtcNow;
        var ticker = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => FetchText.Text = $"Fetching torrent metadata… {(int)(DateTime.UtcNow - started).TotalSeconds}s of {(int)_timeout.TotalSeconds}s. Cancel to stop.", Dispatcher);
        FetchText.Text = $"Fetching torrent metadata… 0s of {(int)_timeout.TotalSeconds}s. Cancel to stop.";
        try
        {
            var meta = await _fetch(_magnet, _timeout, cts.Token);
            if (cts.IsCancellationRequested) return;
            ShowFiles(meta.Name, meta.Files.Select(f => (f.Path, f.Length)).ToList());
        }
        catch (OperationCanceledException) { if (!cts.IsCancellationRequested) ShowMetadataError("Metadata fetch was interrupted."); }
        catch (TimeoutException) { ShowMetadataError($"No metadata arrived within {(int)_timeout.TotalSeconds} seconds. The magnet may have no reachable peers. Try again or cancel."); }
        catch (TorrentRejectedException ex) { ShowMetadataError(ex.Message); }
        catch (Exception ex) { ShowMetadataError("Could not fetch metadata: " + ex.Message); }
        finally { ticker.Stop(); }
    }

    public void LoadFromTorrentData(byte[] data)
    {
        try
        {
            var torrent = TorrentValidator.LoadTorrent(data);
            ShowFiles(torrent.Name, torrent.Files.Select(f => (f.Path, f.Length)).ToList());
        }
        catch (TorrentRejectedException ex) { ShowMetadataError(ex.Message); }
    }

    private void ShowFiles(string name, IReadOnlyList<(string Path, long Length)> files)
    {
        _name = name; _fileCount = files.Count;
        TitleText.Text = name; FetchPanel.Visibility = Visibility.Collapsed;
        _roots = TorrentFileTree.Build(files, UpdateSelection);
        FileTree.ItemsSource = _roots;
        if (!_folderEdited) SaveFolder.Text = TorrentSavePlanner.DefaultFolder(_settings, files);
        MetadataReady = true; MetadataError = null;
        PausedButton.IsEnabled = StartButton.IsEnabled = true;
        UpdateSelection();
    }

    private void ShowMetadataError(string message)
    {
        MetadataError = message; MetadataReady = false;
        FetchBar.Visibility = Visibility.Collapsed; FetchText.Text = ""; ErrorText.Text = message;
        RetryButton.Visibility = _magnet is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private long TotalBytes => _roots.SelectMany(r => r.Leaves()).Sum(l => l.Length);
    private void UpdateSelection()
    {
        int count = TorrentFileTree.SelectedIndexes(_roots).Count;
        SelectionText.Text = $"{count} of {_fileCount} files · {DownloadRow.FormatBytes(TorrentFileTree.SelectedBytes(_roots))} of {DownloadRow.FormatBytes(TotalBytes)}";
        UpdateSpace();
    }
    private void UpdateSpace()
    {
        if (SpaceText is null) return;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(SaveFolder.Text.Trim()));
            if (string.IsNullOrEmpty(root)) { SpaceText.Text = ""; return; }
            long free = new DriveInfo(root).AvailableFreeSpace, need = TorrentFileTree.SelectedBytes(_roots);
            SpaceText.Text = $"Free on {root}: {DownloadRow.FormatBytes(free)}" + (need > free ? ". Not enough space for the selected files." : "");
        }
        catch (Exception) { SpaceText.Text = ""; }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var r in _roots) r.IsChecked = true; }
    private void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var r in _roots) r.IsChecked = false; }
    private void Retry_Click(object sender, RoutedEventArgs e) => StartMetadataFetch();
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose torrent save folder", Multiselect = false };
        if (Directory.Exists(SaveFolder.Text)) picker.InitialDirectory = SaveFolder.Text;
        if (picker.ShowDialog(this) == true) { _folderEdited = true; SaveFolder.Text = picker.FolderName; }
    }
    private void StartNow_Click(object sender, RoutedEventArgs e) => Accept(true);
    private void AddPaused_Click(object sender, RoutedEventArgs e) => Accept(false);

    private void Accept(bool startNow)
    {
        ErrorText.Text = "";
        if (!MetadataReady) { ErrorText.Text = MetadataError ?? "Wait for the torrent's file list first."; return; }
        var selected = TorrentFileTree.SelectedIndexes(_roots);
        if (selected.Count == 0) { ErrorText.Text = "Select at least one file to download."; return; }
        var folder = SaveFolder.Text.Trim();
        if (!Path.IsPathFullyQualified(folder)) { ErrorText.Text = "Choose a complete folder path to save to."; return; }
        try
        {
            folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".ndm-write-test-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { ErrorText.Text = "The save folder is not writable. Choose another folder."; return; }
        Selection = new TorrentAddSelection(_magnet, _data, _name, folder, selected, _fileCount, startNow);
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }
}
