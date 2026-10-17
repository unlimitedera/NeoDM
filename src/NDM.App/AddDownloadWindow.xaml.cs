using System.IO;
using System.Windows;
using NDM.Core;
using NDM.Torrent;
namespace NDM.App;
public partial class AddDownloadWindow : Window
{
    public DownloadRequest? Request { get; private set; }
    public DateTimeOffset? ScheduledAt { get; private set; }
    public long? SpeedLimit { get; private set; }
    /// <summary>Set instead of <see cref="Request"/> when the user entered a magnet link; the torrent file-selection dialog takes over.</summary>
    public string? MagnetUri { get; private set; }
    // Shared by the real Add flow and focused tests; a requested limit is set before starting.
    public async Task<DownloadJob> AddToAsync(DownloadManager manager)
    {
        if (Request is null) throw new InvalidOperationException("Validate the download first.");
        var job = await manager.AddScheduledAsync(Request, SpeedLimit.HasValue ? DateTimeOffset.UtcNow.AddYears(1) : ScheduledAt);
        if (SpeedLimit.HasValue)
        {
            try { manager.SetSpeedLimit(job.Id, SpeedLimit); manager.Schedule(job.Id, ScheduledAt ?? DateTimeOffset.UtcNow); }
            catch { manager.Pause(job.Id); throw new InvalidOperationException("The job was saved but paused because its options could not be applied."); }
        }
        return manager.Jobs.First(j => j.Id == job.Id);
    }
    public AddDownloadWindow(string initialUrl)
    {
        InitializeComponent(); WindowPlacements.Track(this, "Add"); UrlInput.Text = initialUrl;
        Loaded += (_, _) => { UrlInput.Focus(); UrlInput.SelectAll(); };
    }
    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string url = UrlInput.Text.Trim();
            if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                TorrentValidator.ParseMagnet(url);
                MagnetUri = url; Request = null;
                try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
                return;
            }
            NativeBridge.ValidateUrl(url);
            string name = NameInput.Text.Trim();
            if (name.Length > 0 && (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')))
                throw new FormatException("Use a file name without folder paths or invalid characters.");
            ScheduledAt = ScheduleEnabled.IsChecked == true ? DownloadDialogValidation.ParseSchedule(ScheduleInput.Text) : null;
            SpeedLimit = DownloadDialogValidation.ParseSpeed(SpeedInput.Text);
            Request = new DownloadRequest(url, name.Length == 0 ? null : name);
            DialogResult = true;
        }
        catch (TorrentRejectedException ex) { ValidationError.Text = ex.Message; UpdateLayout(); ValidationError.BringIntoView(); }
        catch (FormatException ex) { ValidationError.Text = ex.Message; UpdateLayout(); ValidationError.BringIntoView(); }
    }
}
