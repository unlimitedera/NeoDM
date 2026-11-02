using System.ComponentModel;
using NDM.Torrent;

namespace NDM.App;

public sealed partial class DownloadRow
{
    public IReadOnlyList<TorrentFileJob> TorrentFiles { get; private set; } = Array.Empty<TorrentFileJob>();
    public DownloadRow(TorrentJob job) { Id = TorrentRowId(job.Id); IsTorrent = true; TorrentId = job.Id; Update(job); }
    /// <summary>Torrent counterpart of <see cref="Update(DownloadJob)"/>; same change-detection contract.</summary>
    public bool Update(TorrentJob job)
    {
        if (!IsTorrent || job.Id != TorrentId) throw new ArgumentException("Snapshot belongs to another download.", nameof(job));
        var speed = Math.Max(0, (double)job.DownloadSpeed); var upload = Math.Max(0, (double)job.UploadSpeed);
        var progress = double.IsFinite(job.Progress) ? Math.Clamp(job.Progress, 0, 100) : 0;
        long total = job.SelectedBytes > 0 ? job.SelectedBytes : job.TotalBytes;
        string path = job.Files.Count == 1 ? job.Files[0].FullPath : System.IO.Path.Combine(job.SavePath, job.Name);
        TimeSpan? remaining = job.State == "Downloading" && speed > 0 && total > job.DownloadedBytes ? TimeSpan.FromSeconds(Math.Min((total - job.DownloadedBytes) / speed, TimeSpan.MaxValue.TotalSeconds - 1)) : null;
        bool changed = FileName != job.Name || FilePath != path || SavePath != job.SavePath || Status != job.State || Error != job.Error || TotalBytes != total || DownloadedBytes != job.DownloadedBytes
            || Speed != speed || UploadSpeed != upload || Progress != progress || RemainingTime != remaining || Seeds != job.Seeds || Peers != job.Peers || Ratio != job.Ratio
            || ActiveConnectionCount != job.Connections || !TorrentFiles.SequenceEqual(job.Files);
        if (!changed) return false;
        Url = TorrentController.MagnetFor(job); FileName = job.Name; FilePath = path; SavePath = job.SavePath; Status = job.State; Error = job.Error;
        TotalBytes = total; DownloadedBytes = job.DownloadedBytes; Speed = speed; UploadSpeed = upload; Progress = progress; RemainingTime = remaining;
        Seeds = job.Seeds; Peers = job.Peers; Ratio = job.Ratio; ActiveConnectionCount = job.Connections; ResumeSupported = true; TorrentFiles = job.Files.ToArray(); TorrentFileCount = job.Files.Count(f => f.Selected && f.DownloadedBytes > 0);
        RetargetDisplay();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        return true;
    }
}
