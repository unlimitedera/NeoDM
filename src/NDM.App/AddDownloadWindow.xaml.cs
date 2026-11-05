using System.IO;
using System.Windows;
using NDM.App.Localization;
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
    /// <summary>Set instead of <see cref="Request"/> when the address is a YouTube playlist (1.11.0): the Playlist dialog takes over.</summary>
    public string? PlaylistUrl { get; private set; }
    /// <summary>Asks "this video or the whole playlist" for a watch address with a list; tests replace it. 0 = video, 1 = playlist, -1 = cancelled.</summary>
    public Func<int>? VideoOrPlaylistAnswer { get; set; }
    private void UrlInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var note = YoutubeRouting.Note(UrlInput.Text);
        YoutubeNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        if (note is { } n) { YoutubeNote.Text = n.Text; YoutubeNote.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, n.IsProblem ? "ErrorBrush" : "AccentBrush"); }
    }
    // Shared by the real Add flow and focused tests; a requested limit is set before starting.
    public async Task<DownloadJob> AddToAsync(DownloadManager manager)
    {
        if (Request is null) throw new InvalidOperationException(Loc.T("add.error.validate_first"));
        var job = await manager.AddScheduledAsync(Request, SpeedLimit.HasValue ? DateTimeOffset.UtcNow.AddYears(1) : ScheduledAt);
        if (SpeedLimit.HasValue)
        {
            try { manager.SetSpeedLimit(job.Id, SpeedLimit); manager.Schedule(job.Id, ScheduledAt ?? DateTimeOffset.UtcNow); }
            catch { manager.Pause(job.Id); throw new InvalidOperationException(Loc.T("add.error.paused_options")); }
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
            if (YoutubeUrls.IsHost(url))
            {
                var plan = YoutubeRouting.Plan(url);
                if (plan.Action == YoutubeAction.Refused) throw new UserMessageException(plan.Message ?? Loc.T("youtube.url_invalid"));
                var action = plan.Action;
                if (action == YoutubeAction.AskVideoOrPlaylist)
                {
                    var answer = (VideoOrPlaylistAnswer ?? (() => MainWindow.AskVideoOrPlaylist(this)))();
                    if (answer < 0) return;
                    action = answer == 0 ? YoutubeAction.OpenVideo : YoutubeAction.OpenPlaylist;
                }
                if (action == YoutubeAction.OpenPlaylist)
                {
                    PlaylistUrl = plan.PlaylistUrl; Request = null;
                    ScheduledAt = ScheduleEnabled.IsChecked == true ? ScheduleInput.Require() : null;
                    DialogResult = true; return;
                }
                try { url = YoutubeUrls.Canonical(url); }
                catch (ArgumentException) { throw new UserMessageException(Loc.T("youtube.url_invalid")); }
            }
            string name = NameInput.Text.Trim();
            if (name.Length > 0 && (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')))
                throw new UserMessageException(Loc.T("error.file_name_invalid"));
            ScheduledAt = ScheduleEnabled.IsChecked == true ? ScheduleInput.Require() : null;
            SpeedLimit = DownloadDialogValidation.ParseSpeed(SpeedInput.Text);
            Request = new DownloadRequest(url, name.Length == 0 ? null : name);
            DialogResult = true;
        }
        catch (TorrentRejectedException ex) { ValidationError.Text = ErrorMessages.Describe(ex.Message); UpdateLayout(); ValidationError.BringIntoView(); }
        catch (FormatException ex) { ValidationError.Text = ErrorMessages.Describe(ex); UpdateLayout(); ValidationError.BringIntoView(); }
    }
}
