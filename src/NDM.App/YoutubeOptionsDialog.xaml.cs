using System.IO;
using System.Windows;
using System.Windows.Controls;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

public partial class YoutubeOptionsDialog : Window
{
    private readonly YoutubeService service;
    private readonly ToolManager muxer;
    private readonly DownloadRequest seed;
    private readonly CancellationTokenSource cancel = new();
    private readonly Func<string, CancellationToken, Task<YoutubeMetadata>> inspector;
    private YoutubeMetadata? metadata;
    public DownloadRequest? Request { get; private set; }
    public bool QueueOnly { get; private set; }
    public bool Accepted { get; private set; }
    internal bool Modeless { get; set; }
    private bool expired;
    private bool reading, accepting, automaticFolder;
    private readonly AppSettings? settings;
    private bool folderEdited;
    public void MarkExpired()
    {
        expired = true; cancel.Cancel(); Error.Text = Loc.T("stream.expired");
        Choices.IsEnabled = StartButton.IsEnabled = QueueButton.IsEnabled = ReadButton.IsEnabled = false;
    }
    public Task ReadTask { get; private set; } = Task.CompletedTask;
    public YoutubeOptionsDialog(DownloadRequest seed, string folder, YoutubeService service, ToolManager muxer, Func<string, CancellationToken, Task<YoutubeMetadata>>? inspector = null, AppSettings? settings = null)
    {
        this.seed = seed with { Url = YoutubeUrls.Canonical(seed.Url) }; this.service = service; this.muxer = muxer;
        this.inspector = inspector ?? ((url, token) => Task.Run(() => service.InspectAsync(url, token), token)); this.settings = settings;
        InitializeComponent(); WindowPlacements.Track(this, "Youtube");
        UrlDisplay.Text = this.seed.Url; Folder.Text = seed.TargetDirectory ?? folder;
        folderEdited = !string.IsNullOrWhiteSpace(seed.TargetDirectory) || settings is not null && !string.Equals(Path.GetFullPath(folder), Path.GetFullPath(settings.DownloadDirectory), StringComparison.OrdinalIgnoreCase); UpdateFolder();
        Closed += (_, _) => cancel.Cancel();
    }
    private void Read_Click(object sender, RoutedEventArgs e) => ReadTask = ReadAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    public Task ReadAsync()
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.InvokeAsync(() => ReadAsync()).Task.Unwrap();
        return ReadOnUiAsync();
    }
    private Task OnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
        return Dispatcher.InvokeAsync(action).Task;
    }
    private async Task ReadOnUiAsync()
    {
        if (expired || reading || accepting) return;
        reading = true; ReadButton.IsEnabled = false; Error.Text = "";
        metadata = null; Choices.IsEnabled = StartButton.IsEnabled = QueueButton.IsEnabled = false;
        LoadingProgress.Visibility = LoadingStatus.Visibility = Visibility.Visible;
        try
        {
            var result = await inspector(seed.Url, cancel.Token).ConfigureAwait(false);
            await OnUi(() =>
            {
                if (expired || cancel.IsCancellationRequested) return;
                metadata = result;
                Quality.ItemsSource = metadata.Formats.Where(f => f.Video).OrderByDescending(f => f.Height).ToArray();
                Audio.ItemsSource = metadata.Formats.Where(f => f.Audio && !f.Video).OrderByDescending(f => f.Bitrate).ToArray();
                Quality.SelectedIndex = Audio.SelectedIndex = 0;
                ApplyMode(); FileName.Text = seed.FileName ?? metadata.Title;
                Choices.IsEnabled = true; UpdateAcceptance();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { await OnUi(() => { if (!cancel.IsCancellationRequested) Error.Text = Loc.T("youtube.failed"); }).ConfigureAwait(false); }
        catch (Exception ex) { await OnUi(() => Error.Text = Describe(ex)).ConfigureAwait(false); }
        finally
        {
            await OnUi(() =>
            {
                reading = false; ReadButton.IsEnabled = !expired && !cancel.IsCancellationRequested;
                LoadingProgress.Visibility = LoadingStatus.Visibility = Visibility.Collapsed;
            }).ConfigureAwait(false);
        }
    }
    private void Folder_Changed(object sender, TextChangedEventArgs e) { if (!automaticFolder) folderEdited = true; }
    private void UpdateFolder()
    {
        if (settings is null || folderEdited) return;
        automaticFolder = true;
        try { Folder.Text = CategoryHelper.GetDownloadPath(AudioOnly.IsChecked == true ? "audio.mp3" : "video.mp4", settings, seed.Url, seed.Referer); }
        finally { automaticFolder = false; }
    }
    private void Mode_Changed(object sender, RoutedEventArgs e) => ApplyMode();
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => ApplyMode();
    private void ApplyMode()
    {
        if (AudioOnly is null || Quality is null || Audio is null || Mp3 is null) return;
        var audioOnly = AudioOnly.IsChecked == true;
        Quality.IsEnabled = !audioOnly;
        Audio.IsEnabled = audioOnly || Quality.SelectedItem is YoutubeFormat { Audio: false };
        Mp3.IsEnabled = audioOnly; if (!audioOnly) Mp3.IsChecked = false;
        UpdateFolder(); UpdateAcceptance();
    }
    private YoutubeFormat? SelectedFormat => (AudioOnly.IsChecked == true ? Audio : Quality).SelectedItem as YoutubeFormat;
    private void UpdateAcceptance()
    {
        if (StartButton is null || QueueButton is null) return;
        StartButton.IsEnabled = QueueButton.IsEnabled = !expired && !accepting && metadata is not null && SelectedFormat is not null && (AudioOnly.IsChecked == true || !Audio.IsEnabled || Audio.SelectedItem is YoutubeFormat);
    }
    private async void Queue_Click(object sender, RoutedEventArgs e) => await AcceptAsync(true);
    private async void Add_Click(object sender, RoutedEventArgs e) => await AcceptAsync(false);
    public async Task AcceptAsync(bool queueOnly)
    {
        await Task.CompletedTask;
        if (expired || reading || accepting || metadata is null || SelectedFormat is not YoutubeFormat f) return;
        accepting = true; StartButton.IsEnabled = QueueButton.IsEnabled = ReadButton.IsEnabled = false;
        try
        {
            var selection = new YoutubeSelection(f.Id, f.Video && !f.Audio ? (Audio.SelectedItem as YoutubeFormat)?.Id : null, Mp3.IsChecked == true);
            var extension = YoutubeService.OutputExtension(selection, metadata);
            var folder = DownloadManager.EnsureWritableFolder(Folder.Text);
            var name = DownloadFileNames.Sanitize(FileName.Text.Trim().Length == 0 ? metadata.Title : FileName.Text.Trim());
            if (new[] { ".mp4", ".mkv", ".webm", ".m4a", ".mp3", ".opus", ".ogg" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name);
            Request = new DownloadRequest(seed.Url, name + "." + extension, TargetDirectory: folder, Youtube: selection);
            if (expired || cancel.IsCancellationRequested) { Request = null; return; }
            QueueOnly = queueOnly; Accepted = true;
            if (Modeless) Close(); else DialogResult = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error.Text = Describe(ex); }
        finally { accepting = false; ReadButton.IsEnabled = !expired; UpdateAcceptance(); }
    }
    public static string Describe(Exception ex) => ex is YoutubeException y ? Loc.T(y.Code) : ex is ToolException { Code: ToolException.NotInstalled } ? Loc.T("tool.bundle_missing") : ex is ToolException ? VideoToolInfo.Describe(ex) : ErrorMessages.Describe(ex);
}
