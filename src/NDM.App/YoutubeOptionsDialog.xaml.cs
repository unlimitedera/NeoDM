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
    public void MarkExpired()
    {
        expired = true; cancel.Cancel(); Error.Text = Loc.T("stream.expired");
        Choices.IsEnabled = StartButton.IsEnabled = QueueButton.IsEnabled = ReadButton.IsEnabled = false;
    }
    public Task ReadTask { get; private set; } = Task.CompletedTask;
    public YoutubeOptionsDialog(DownloadRequest seed, string folder, YoutubeService service, ToolManager muxer, Func<string, CancellationToken, Task<YoutubeMetadata>>? inspector = null)
    {
        this.seed = seed with { Url = YoutubeUrls.Canonical(seed.Url) }; this.service = service; this.muxer = muxer;
        this.inspector = inspector ?? service.InspectAsync;
        InitializeComponent(); WindowPlacements.Track(this, "Youtube");
        UrlDisplay.Text = this.seed.Url; Folder.Text = folder;
        ConsentPanel.Visibility = service.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
        Closed += (_, _) => cancel.Cancel();
    }
    private void Read_Click(object sender, RoutedEventArgs e) => ReadTask = ReadAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    public async Task ReadAsync()
    {
        if (expired || !ReadButton.IsEnabled) return;
        ReadButton.IsEnabled = false; Error.Text = "";
        try
        {
            if (!service.IsInstalled)
            {
                if (Consent.IsChecked != true) { Error.Text = Loc.T("youtube.consent_required"); return; }
                await service.InstallAsync(cancel.Token);
                ConsentPanel.Visibility = Visibility.Collapsed;
            }
            metadata = await inspector(seed.Url, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            Quality.ItemsSource = metadata.Formats.Where(f => f.Video).OrderByDescending(f => f.Height).Concat(metadata.Formats.Where(f => !f.Video)).ToArray();
            Quality.SelectedIndex = 0;
            FileName.Text = seed.FileName ?? metadata.Title;
            Choices.IsEnabled = StartButton.IsEnabled = QueueButton.IsEnabled = true;
        }
        catch (OperationCanceledException) { if (!cancel.IsCancellationRequested) Error.Text = Loc.T("youtube.failed"); }
        catch (Exception ex) { Error.Text = Describe(ex); }
        finally { ReadButton.IsEnabled = !expired; }
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (metadata is null || Quality.SelectedItem is not YoutubeFormat f) return;
        Audio.ItemsSource = f.Video && !f.Audio ? metadata.Formats.Where(a => a.Audio && !a.Video).OrderByDescending(a => a.Bitrate).ToArray() : Array.Empty<YoutubeFormat>();
        Audio.SelectedIndex = 0; Audio.IsEnabled = f.Video && !f.Audio;
        Mp3.IsEnabled = !f.Video; if (f.Video) Mp3.IsChecked = false;
    }
    private async void Queue_Click(object sender, RoutedEventArgs e) => await AcceptAsync(true);
    private async void Add_Click(object sender, RoutedEventArgs e) => await AcceptAsync(false);
    public async Task AcceptAsync(bool queueOnly)
    {
        if (expired || metadata is null || Quality.SelectedItem is not YoutubeFormat f) return;
        StartButton.IsEnabled = QueueButton.IsEnabled = false;
        try
        {
            var selection = new YoutubeSelection(f.Id, Audio.IsEnabled ? (Audio.SelectedItem as YoutubeFormat)?.Id : null, Mp3.IsChecked == true);
            var extension = YoutubeService.OutputExtension(selection, metadata);
            if ((selection.AudioId is not null || selection.Mp3) && !muxer.IsAvailable)
            {
                if (Dialogs.Show(this, Loc.T("youtube.mux_consent"), Loc.T("youtube.title"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                await muxer.InstallAsync(cancellation: cancel.Token);
            }
            var folder = DownloadManager.EnsureWritableFolder(Folder.Text);
            var name = DownloadFileNames.Sanitize(FileName.Text.Trim().Length == 0 ? metadata.Title : FileName.Text.Trim());
            Request = new DownloadRequest(seed.Url, name + "." + extension, TargetDirectory: folder, Youtube: selection);
            if (expired || cancel.IsCancellationRequested) { Request = null; return; }
            QueueOnly = queueOnly; Accepted = true;
            if (Modeless) Close(); else DialogResult = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error.Text = Describe(ex); }
        finally { StartButton.IsEnabled = QueueButton.IsEnabled = !expired; }
    }
    public static string Describe(Exception ex) => ex is YoutubeException y ? Loc.T(y.Code) : ex is ToolException ? StreamMessages.Describe(ex) : ErrorMessages.Describe(ex);
}
