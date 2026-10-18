using System.ComponentModel;
using System.Windows;
using NDM.App.Localization;
using NDM.Core;
namespace NDM.App;
public partial class BulkImportWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly ITorrentFeature? _torrents;
    private readonly AppSettings? _settings;
    private bool _importing;
    /// <summary>How long each magnet may take to resolve its metadata before the line is reported as not saved.</summary>
    public TimeSpan MagnetTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public BulkImportWindow(DownloadManager manager, ITorrentFeature? torrents = null, AppSettings? settings = null)
    {
        _manager = manager; _torrents = torrents; _settings = settings; InitializeComponent(); Loaded += (_, _) => UrlsInput.Focus();
    }
    private async void Import_Click(object sender, RoutedEventArgs e) => await ImportAsync();
    public async Task ImportAsync()
    {
        if (_importing) return;
        _importing = true; ImportButton.IsEnabled = CloseButton.IsEnabled = UrlsInput.IsEnabled = false;
        var results = new List<(int Line, string Text)>();
        var pendingMagnets = new List<(int Line, string Magnet)>();
        var magnetSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int added = 0, invalid = 0, duplicates = 0, failed = 0, blanks = 0;
        try
        {
            var seen = _manager.Jobs.Select(j => new Uri(j.Url).AbsoluteUri).ToHashSet(StringComparer.Ordinal);
            var lines = UrlsInput.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string url = lines[index].Trim(); int line = index + 1;
                if (url.Length == 0) { blanks++; continue; }
                if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    if (_torrents is null || !_torrents.IsAvailable || _settings is null) { failed++; results.Add((line, Loc.T("bulk.result.not_saved", _torrents?.UnavailableMessage ?? Loc.T("bulk.torrents_unavailable")))); continue; }
                    if (_torrents.ValidateMagnet(url, out var hash) is { } problem) { invalid++; results.Add((line, Loc.T("bulk.result.invalid", ErrorMessages.Describe(problem)))); continue; }
                    if (_torrents.KnowsTorrent(hash) || !magnetSeen.Add(hash)) { duplicates++; results.Add((line, Loc.T("bulk.result.duplicate"))); continue; }
                    pendingMagnets.Add((index + 1, url));
                    continue;
                }
                try { NativeBridge.ValidateUrl(url); }
                catch (FormatException) { invalid++; results.Add((line, Loc.T("bulk.result.invalid_url"))); continue; }
                string key = new Uri(url).AbsoluteUri;
                if (seen.Contains(key)) { duplicates++; results.Add((line, Loc.T("bulk.result.duplicate"))); continue; }
                try { await _manager.AddAsync(new DownloadRequest(url)); seen.Add(key); added++; results.Add((line, Loc.T("bulk.result.added"))); }
                catch (Exception) { failed++; results.Add((line, Loc.T("bulk.result.not_saved_destination"))); }
                // Keep the dialog responsive even when the core completes synchronously.
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
            if (pendingMagnets.Count > 0)
            {
                Summary.Text = Loc.T("bulk.summary.resolving", pendingMagnets.Count, (int)MagnetTimeout.TotalSeconds);
                var outcomes = await Task.WhenAll(pendingMagnets.Select(async (m) =>
                {
                    var (ok, message) = await _torrents!.ImportMagnetAsync(m.Magnet, MagnetTimeout);
                    return (Line: m.Line, Ok: ok, Message: message);
                }));
                foreach (var o in outcomes)
                {
                    if (o.Ok) added++; else if (o.Message == Loc.T("bulk.torrent_duplicate")) duplicates++; else failed++;
                    results.Add((o.Line, o.Message));
                }
                results.Sort((a, b) => a.Line.CompareTo(b.Line));
            }
            Summary.Text = Loc.T("bulk.summary.done", added, invalid, duplicates, failed, blanks);
            Results.ItemsSource = results.Select(r => Loc.T("bulk.result.line", r.Line, r.Text)).ToList();
        }
        catch (Exception) { Summary.Text = Loc.T("bulk.summary.interrupted", added, invalid, duplicates, failed); Results.ItemsSource = results.Select(r => Loc.T("bulk.result.line", r.Line, r.Text)).ToList(); }
        finally { _importing = false; ImportButton.IsEnabled = CloseButton.IsEnabled = UrlsInput.IsEnabled = true; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (_importing) e.Cancel = true; }
}
