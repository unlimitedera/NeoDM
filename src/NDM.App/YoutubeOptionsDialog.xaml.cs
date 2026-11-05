using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

public partial class YoutubeOptionsDialog : Window
{
    private readonly YoutubeService service;
    private readonly ToolManager muxer;
    private readonly DownloadRequest seed;
    private readonly CancellationTokenSource cancel = new();
    private readonly Func<string, CancellationToken, Action<string>, Task<YoutubeMetadata>> inspector;
    private YoutubeMetadata? metadata;
    private CancellationTokenSource? loadCancel;
    private readonly DispatcherTimer loadTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    public DownloadRequest? Request { get; private set; }
    /// <summary>What the user chose: Download now (StartNow), Add to queue (Queue) or Schedule (Schedule).</summary>
    public DownloadOptionsDialog.DialogAction UserAction { get; private set; } = DownloadOptionsDialog.DialogAction.Cancel;
    public DateTimeOffset? ScheduledAt { get; private set; }
    public bool Accepted { get; private set; }
    internal bool Modeless { get; set; }
    /// <summary>The estimate behind the progress panel (stage, percentage, elapsed time). Tests drive it with a fake clock through <see cref="LoadProgressFactory"/>.</summary>
    public YoutubeLoadProgress LoadProgress { get; private set; }
    internal Func<YoutubeLoadProgress> LoadProgressFactory { get; set; } = () => new YoutubeLoadProgress();
    private bool expired;
    private bool reading, accepting, automaticFolder, loadCancelled;
    private readonly AppSettings? settings;
    private bool folderEdited;
    public void MarkExpired()
    {
        expired = true; cancel.Cancel(); Error.Text = Loc.T("stream.expired");
        Choices.IsEnabled = StartButton.IsEnabled = QueueButton.IsEnabled = ScheduleButton.IsEnabled = ReadButton.IsEnabled = false;
    }
    public Task ReadTask { get; private set; } = Task.CompletedTask;
    public YoutubeOptionsDialog(DownloadRequest seed, string folder, YoutubeService service, ToolManager muxer, Func<string, CancellationToken, Task<YoutubeMetadata>>? inspector = null, AppSettings? settings = null,
        DateTimeOffset? initialSchedule = null, Func<string, CancellationToken, Action<string>, Task<YoutubeMetadata>>? lineInspector = null)
    {
        this.seed = seed with { Url = YoutubeUrls.Canonical(seed.Url) }; this.service = service; this.muxer = muxer;
        this.inspector = inspector is not null ? (url, token, _) => inspector(url, token) : lineInspector ?? ((url, token, line) => Task.Run(() => service.InspectAsync(url, token, line), token));
        this.settings = settings; LoadProgress = LoadProgressFactory();
        InitializeComponent(); WindowPlacements.Track(this, "Youtube");
        UrlDisplay.Text = this.seed.Url; Folder.Text = seed.TargetDirectory ?? folder;
        folderEdited = !string.IsNullOrWhiteSpace(seed.TargetDirectory) || settings is not null && !string.Equals(Path.GetFullPath(folder), Path.GetFullPath(settings.DownloadDirectory), StringComparison.OrdinalIgnoreCase); UpdateFolder();
        if (initialSchedule is { } at) { ScheduleInput.Text = at.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture); presetSchedule = true; }
        loadTimer.Tick += (_, _) => RefreshLoading();
        UpdateAcceptance();
        Closed += (_, _) => { loadTimer.Stop(); cancel.Cancel(); };
    }
    private bool presetSchedule;
    private void Read_Click(object sender, RoutedEventArgs e) => ReadTask = ReadAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void CancelLoad_Click(object sender, RoutedEventArgs e) => CancelLoad();
    /// <summary>Stops a running "Load video info" (the helper is ended); the dialog stays open and can load again.</summary>
    public void CancelLoad() { if (reading) { loadCancelled = true; loadCancel?.Cancel(); } }
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
    /// <summary>Called for each stderr line of the helper while loading (also by tests with a fake process).</summary>
    public void HelperLine(string line) { LoadProgress.Line(line); }
    /// <summary>Redraws the progress panel from the estimate. Runs on a 200 ms timer while loading; tests call it directly.</summary>
    public void RefreshLoading()
    {
        var percent = LoadProgress.Percent();
        LoadingProgress.Value = percent; LoadingPercent.Text = Loc.T("youtube.percent", (int)Math.Round(percent));
        LoadingStatus.Text = Loc.T(LoadProgress.StatusKey);
        LoadingElapsed.Text = Loc.T(LoadProgress.UsesHelperLines ? "youtube.elapsed" : "youtube.elapsed_timed", (int)LoadProgress.Elapsed.TotalSeconds);
        System.Windows.Automation.AutomationProperties.SetHelpText(LoadingProgress, LoadingStatus.Text);
    }
    private async Task ReadOnUiAsync()
    {
        if (expired || reading || accepting) return;
        reading = true; loadCancelled = false; ReadButton.IsEnabled = false; Error.Text = "";
        metadata = null; Choices.IsEnabled = false; UpdateAcceptance();
        LoadProgress = LoadProgressFactory(); LoadProgress.Start();
        loadCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        LoadPanel.Visibility = Visibility.Visible; CancelLoadButton.IsEnabled = true; RefreshLoading(); loadTimer.Start();
        var token = loadCancel.Token;
        try
        {
            var result = await inspector(seed.Url, token, line => OnUi(() => HelperLine(line))).ConfigureAwait(false);
            await OnUi(() =>
            {
                if (expired || cancel.IsCancellationRequested) return;
                LoadProgress.Building();
                metadata = result;
                Quality.ItemsSource = metadata.Formats.Where(f => f.Video).OrderByDescending(f => f.Height).ToArray();
                Audio.ItemsSource = metadata.Formats.Where(f => f.Audio && !f.Video).OrderByDescending(f => f.Bitrate).ToArray();
                Quality.SelectedIndex = Audio.SelectedIndex = 0;
                ApplyMode(); FileName.Text = seed.FileName ?? metadata.Title;
                Choices.IsEnabled = true; LoadProgress.Finish(); RefreshLoading();
                ReadButton.Style = (Style)FindResource(typeof(Button)); UpdateAcceptance();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { await OnUi(() => { LoadProgress.Stop(); if (loadCancelled && !cancel.IsCancellationRequested) Error.Text = Loc.T("youtube.load_cancelled"); else if (!cancel.IsCancellationRequested) Error.Text = Loc.T("youtube.failed"); }).ConfigureAwait(false); }
        catch (Exception ex) { await OnUi(() => { LoadProgress.Stop(); Error.Text = Describe(ex); }).ConfigureAwait(false); }
        finally
        {
            await OnUi(() =>
            {
                reading = false; loadTimer.Stop(); ReadButton.IsEnabled = !expired && !cancel.IsCancellationRequested;
                if (metadata is null) LoadPanel.Visibility = Visibility.Collapsed; else LoadingProgress.Value = 100;
                loadCancel?.Dispose(); loadCancel = null; CancelLoadButton.IsEnabled = false;
            }).ConfigureAwait(false);
        }
    }
    private void Error_TextChanged(object sender, TextChangedEventArgs e) => ErrorRow.Visibility = Error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    private void CopyError_Click(object sender, RoutedEventArgs e) => SafeClipboard.TrySetText(Error.Text);
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
        if (StartButton is null || QueueButton is null || ScheduleButton is null) return;
        var ready = !expired && !accepting && metadata is not null && SelectedFormat is not null && (AudioOnly.IsChecked == true || !Audio.IsEnabled || Audio.SelectedItem is YoutubeFormat);
        StartButton.IsEnabled = QueueButton.IsEnabled = ScheduleButton.IsEnabled = ready;
        // A disabled button says why (step 1 first); an enabled one needs no tip.
        object? tip = ready ? null : Loc.T("youtube.need_info");
        StartButton.ToolTip = QueueButton.ToolTip = ScheduleButton.ToolTip = tip;
        Step2Label.Opacity = metadata is null ? 0.6 : 1;
    }
    private async void Queue_Click(object sender, RoutedEventArgs e) => await AcceptAsync(DownloadOptionsDialog.DialogAction.Queue);
    private async void Add_Click(object sender, RoutedEventArgs e) => await AcceptAsync(DownloadOptionsDialog.DialogAction.StartNow);
    private async void Schedule_Click(object sender, RoutedEventArgs e)
    {
        // The first press shows the date field (already filled when the date came from Add URL, which then schedules at once); the next press schedules.
        if (SchedulePanel.Visibility != Visibility.Visible)
        {
            SchedulePanel.Visibility = Visibility.Visible;
            if (!presetSchedule) { ScheduleInput.Focus(); SchedulePanel.BringIntoView(); return; }
        }
        await AcceptAsync(DownloadOptionsDialog.DialogAction.Schedule);
    }
    /// <summary>Builds the request and finishes the dialog with the chosen action (Download now, Add to queue, Schedule).</summary>
    public async Task AcceptAsync(DownloadOptionsDialog.DialogAction action)
    {
        await Task.CompletedTask;
        if (expired || reading || accepting || metadata is null || SelectedFormat is not YoutubeFormat f) return;
        accepting = true; StartButton.IsEnabled = QueueButton.IsEnabled = ScheduleButton.IsEnabled = ReadButton.IsEnabled = false;
        try
        {
            DateTimeOffset? when = null;
            if (action == DownloadOptionsDialog.DialogAction.Schedule) when = ScheduleInput.Require();
            var selection = new YoutubeSelection(f.Id, f.Video && !f.Audio ? (Audio.SelectedItem as YoutubeFormat)?.Id : null, Mp3.IsChecked == true);
            var extension = YoutubeService.OutputExtension(selection, metadata);
            var folder = DownloadManager.EnsureWritableFolder(Folder.Text);
            var name = DownloadFileNames.Sanitize(FileName.Text.Trim().Length == 0 ? metadata.Title : FileName.Text.Trim());
            if (new[] { ".mp4", ".mkv", ".webm", ".m4a", ".mp3", ".opus", ".ogg" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name);
            Request = new DownloadRequest(seed.Url, name + "." + extension, TargetDirectory: folder, Youtube: selection);
            if (expired || cancel.IsCancellationRequested) { Request = null; return; }
            UserAction = action; ScheduledAt = when; Accepted = true;
            if (Modeless) Close(); else DialogResult = true;
        }
        catch (OperationCanceledException) { }
        catch (FormatException ex) { Error.Text = ex.Message; SchedulePanel.Visibility = Visibility.Visible; ScheduleInput.Focus(); }
        catch (Exception ex) { Error.Text = Describe(ex); }
        finally { accepting = false; ReadButton.IsEnabled = !expired; UpdateAcceptance(); }
    }
    public static string Describe(Exception ex) => ex is YoutubeException y ? (y.Code == "youtube.failed" && !string.IsNullOrWhiteSpace(y.Detail) ? Loc.T("youtube.failed") + "\r\n" + Loc.T("youtube.detail", y.Detail) : Loc.T(y.Code)) : ex is ToolException { Code: ToolException.NotInstalled } ? Loc.T("tool.bundle_missing") : ex is ToolException ? VideoToolInfo.Describe(ex) : ErrorMessages.Describe(ex);
}

/// <summary>Turns an accepted YouTube dialog into a job: Download now starts at once, Add to queue waits for a free slot, Schedule starts at the chosen time. An optional speed limit (Add URL) is applied before the job can start.</summary>
public static class YoutubeCommit
{
    public static async Task<DownloadJob?> AddAsync(DownloadManager manager, DownloadRequest request, DownloadOptionsDialog.DialogAction action, DateTimeOffset? scheduledAt = null, long? speedLimit = null)
    {
        DownloadJob job;
        switch (action)
        {
            case DownloadOptionsDialog.DialogAction.StartNow:
            case DownloadOptionsDialog.DialogAction.Queue:
                job = await manager.AddScheduledAsync(request, speedLimit.HasValue ? DateTimeOffset.UtcNow.AddYears(1) : null);
                if (speedLimit.HasValue) { manager.SetSpeedLimit(job.Id, speedLimit); manager.Schedule(job.Id, DateTimeOffset.UtcNow); }
                break;
            case DownloadOptionsDialog.DialogAction.Schedule:
                job = await manager.AddScheduledAsync(request, speedLimit.HasValue ? DateTimeOffset.UtcNow.AddYears(1) : scheduledAt);
                if (speedLimit.HasValue) { manager.SetSpeedLimit(job.Id, speedLimit); manager.Schedule(job.Id, scheduledAt ?? DateTimeOffset.UtcNow); }
                break;
            default: return null;
        }
        return job;
    }
    /// <summary>Only Download now opens the progress window.</summary>
    public static bool StartsNow(DownloadOptionsDialog.DialogAction action) => action == DownloadOptionsDialog.DialogAction.StartNow;
}
