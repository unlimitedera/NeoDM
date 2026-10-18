using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using NDM.Torrent;

using NDM.App.Localization;
namespace NDM.App;

public sealed class TorrentFileRow : INotifyPropertyChanged
{
    public int Index { get; }
    public string Path { get; private set; } = "";
    public string Size { get; private set; } = "";
    public double Progress { get; private set; }
    public string Percent => $"{Progress:0}%";
    public string Priority { get; private set; } = "";
    public TorrentFileRow(TorrentFileJob file) { Index = file.Index; Update(file); }
    public void Update(TorrentFileJob file)
    {
        Path = file.Path; Size = DownloadRow.FormatBytes(file.Length); Progress = file.Selected ? Math.Clamp(file.Progress, 0, 100) : 0;
        Priority = Loc.T("priority." + file.Priority.ToString().ToLowerInvariant());
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Modeless live view of one torrent: peers, seeds, speeds, ratio and per-file progress.</summary>
public partial class TorrentDetailsWindow : Window
{
    private readonly TorrentController _torrents;
    private readonly string _id;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<TorrentFileRow> _files = new();
    public TorrentDetailsWindow(TorrentController torrents, string torrentId)
    {
        _torrents = torrents; _id = torrentId;
        InitializeComponent(); WindowPlacements.Track(this, "TorrentDetails");
        FileList.ItemsSource = _files;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        Closed += (_, _) => _timer.Stop();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        Refresh();
    }
    public void Refresh()
    {
        var job = _torrents.List().FirstOrDefault(j => j.Id == _id);
        if (job is null)
        {
            Notice.Text = Loc.T("torrentdetails.notice.removed");
            PauseButton.IsEnabled = false; _timer.Stop(); return;
        }
        var row = new DownloadRow(job);
        Title = Loc.T("torrentdetails.window_title", row.Percent, job.Name);
        NameText.Text = job.Name; PathText.Text = row.FilePath; StatusText.Text = Loc.Status(job.State);
        SizeText.Text = Loc.T("row.detail_sizes", row.Downloaded, row.Size);
        DownText.Text = Loc.Speed(job.DownloadSpeed); UpText.Text = Loc.Speed(job.UploadSpeed);
        SeedsText.Text = job.Seeds.ToString(); PeersText.Text = job.Peers.ToString(); ConnectionsText.Text = job.Connections.ToString();
        RatioText.Text = job.Ratio.ToString("0.00");
        Bar.Value = row.Progress; PercentText.Text = row.Percent; ErrorText.Text = job.Error ?? "";
        PauseButton.Content = Loc.T(row.CanResume ? (job.State == "Completed" ? "torrentdetails.btn.start_seeding" : "main.btn.resume") : "common.btn.pause");
        PauseButton.IsEnabled = row.CanPause || row.CanResume;
        if (_files.Count != job.Files.Count) { _files.Clear(); foreach (var f in job.Files) _files.Add(new TorrentFileRow(f)); }
        else for (int i = 0; i < _files.Count; i++) _files[i].Update(job.Files[i]);
    }
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var job = _torrents.List().FirstOrDefault(j => j.Id == _id);
            if (job is null) { Refresh(); return; }
            var row = new DownloadRow(job);
            if (row.CanResume) await _torrents.ResumeAsync(_id); else if (row.CanPause) await _torrents.PauseAsync(_id);
            Refresh();
        }
        catch (Exception) { Notice.Text = Loc.T("torrentdetails.notice.change_failed"); }
    }
    private void Hide_Click(object sender, RoutedEventArgs e) => Close();
}
