using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

public sealed record QualityChoice(string Value, string Label)
{
    /// <summary>The drop-down shows the label (the themed combo box template draws the item text itself).</summary>
    public override string ToString() => Label;
}

/// <summary>1.11.0: the bulk playlist dialog. Step 1 reads the playlist (flat listing, no formats), step 2 picks videos and options. Each chosen video becomes its own YouTube job in one package;
/// the job resolves its real formats from the preset when it starts. Formats and sizes are never loaded for the whole list.</summary>
public partial class PlaylistOptionsDialog : Window
{
    private readonly string playlistUrl;
    private readonly string baseFolder;
    private readonly AppSettings settings;
    private readonly ThumbnailLoader? thumbnails;
    private readonly Func<string, CancellationToken, Action<string>, Task<YoutubePlaylist>> lister;
    private readonly CancellationTokenSource cancel = new();
    private CancellationTokenSource? loadCancel;
    private readonly DispatcherTimer loadTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly ObservableCollection<PlaylistRow> shown = new();
    private bool reading, loadCancelled, folderEdited, packageEdited, automatic, expired;
    private readonly bool presetSchedule;
    public PlaylistModel? Model { get; private set; }
    public YoutubeLoadProgress LoadProgress { get; private set; } = new();
    internal Func<YoutubeLoadProgress> LoadProgressFactory { get; set; } = () => new YoutubeLoadProgress();
    public IReadOnlyList<QualityChoice> RowChoices { get; }
    public Task ReadTask { get; private set; } = Task.CompletedTask;
    /// <summary>Test and diagnostics counter: how many thumbnail requests rows have made so far (only rows that were shown ask).</summary>
    public int ThumbnailRequests { get; private set; }
    public bool Accepted { get; private set; }
    internal bool Modeless { get; set; }
    public DownloadOptionsDialog.DialogAction UserAction { get; private set; } = DownloadOptionsDialog.DialogAction.Cancel;
    public DateTimeOffset? ScheduledAt { get; private set; }
    public IReadOnlyList<DownloadRequest> Requests { get; private set; } = Array.Empty<DownloadRequest>();
    public string Package { get; private set; } = "";
    public int Slots { get; private set; } = 1;
    public string FinalFolderPath { get; private set; } = "";

    public PlaylistOptionsDialog(string playlistUrl, string baseFolder, YoutubeService? service, AppSettings settings, Func<string, CancellationToken, Action<string>, Task<YoutubePlaylist>>? lister = null,
        ThumbnailLoader? thumbnails = null, DateTimeOffset? initialSchedule = null)
    {
        var link = YoutubeLinks.Parse(playlistUrl);
        this.playlistUrl = link.PlaylistUrl ?? throw new ArgumentException("Not a playlist address.");
        this.baseFolder = baseFolder; this.settings = settings; this.thumbnails = thumbnails;
        this.lister = lister ?? ((url, token, line) => Task.Run(() => service!.ListPlaylistAsync(url, token, line), token));
        RowChoices = new[] { new QualityChoice("", Loc.T("playlist.row_default")) }.Concat(QualityChoices()).Append(new("audio", Loc.T("youtube.audio_only"))).ToArray();
        LoadProgress = LoadProgressFactory();
        InitializeComponent(); WindowPlacements.Track(this, "Playlist");
        UrlDisplay.Text = this.playlistUrl; Folder.Text = baseFolder; AtOnce.Text = settings.PlaylistVideosAtOnce.ToString(CultureInfo.InvariantCulture);
        QualityBox.ItemsSource = QualityChoices().ToArray(); QualityBox.DisplayMemberPath = "Label"; QualityBox.SelectedValuePath = "Value"; QualityBox.SelectedIndex = 0;
        AudioBox.ItemsSource = new[] { new QualityChoice("0", Loc.T("playlist.quality_best")) }.Concat(YoutubePreset.AudioRates.Where(r => r > 0).Select(r => new QualityChoice(r.ToString(CultureInfo.InvariantCulture), Loc.T("playlist.kbps", r)))).ToArray();
        AudioBox.DisplayMemberPath = "Label"; AudioBox.SelectedValuePath = "Value"; AudioBox.SelectedIndex = 0;
        ContainerBox.ItemsSource = new[] { new QualityChoice("", Loc.T("playlist.container_auto")), new("mp4", "MP4"), new("webm", "WebM"), new("mkv", "MKV") };
        ContainerBox.DisplayMemberPath = "Label"; ContainerBox.SelectedValuePath = "Value"; ContainerBox.SelectedIndex = 0;
        SubtitleKindBox.ItemsSource = new[] { new QualityChoice("none", Loc.T("playlist.subs.none")), new("windows", Loc.T("playlist.subs.windows")), new("list", Loc.T("playlist.subs.list")) };
        SubtitleKindBox.DisplayMemberPath = "Label"; SubtitleKindBox.SelectedValuePath = "Value"; SubtitleKindBox.SelectedIndex = 0;
        VideoList.ItemsSource = shown;
        if (initialSchedule is { } at) { ScheduleInput.Text = at.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); presetSchedule = true; }
        LayoutUpdated += (_, _) => FitList();
        loadTimer.Tick += (_, _) => RefreshLoading();
        UpdateAcceptance();
        VideoList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => RetryFailedThumbnails()));
        Closed += (_, _) => { loadTimer.Stop(); cancel.Cancel(); thumbnails?.Cancel(); };
    }
    private static IEnumerable<QualityChoice> QualityChoices() =>
        new[] { new QualityChoice("best", Loc.T("playlist.quality_best")) }.Concat(new[] { 2160, 1440, 1080, 720, 480, 360 }.Select(h => new QualityChoice(h.ToString(CultureInfo.InvariantCulture), h + "p")));

    /// <summary>The list takes the height the other parts leave (at least 240, so the dialog scrolls as a whole when the window is small). An explicit height keeps the list virtualised.</summary>
    private void FitList()
    {
        if (Body is null || BodyScroll.ViewportHeight <= 0) return;
        double others = 0; for (int i = 0; i < Body.RowDefinitions.Count; i++) if (i != 3) others += Body.RowDefinitions[i].ActualHeight;
        var wanted = Math.Max(240, BodyScroll.ViewportHeight - others);
        if (Math.Abs(VideoList.Height - wanted) > 0.5 || double.IsNaN(VideoList.Height)) VideoList.Height = Math.Floor(wanted);
    }

    public void MarkExpired() { expired = true; cancel.Cancel(); Error.Text = Loc.T("stream.expired"); OptionsPanel.IsEnabled = ToolsRow.IsEnabled = FooterOptions.IsEnabled = ReadButton.IsEnabled = false; UpdateAcceptance(); }

    private void Read_Click(object sender, RoutedEventArgs e) => ReadTask = ReadAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void CancelLoad_Click(object sender, RoutedEventArgs e) => CancelLoad();
    public void CancelLoad() { if (reading) { loadCancelled = true; loadCancel?.Cancel(); } }
    public Task ReadAsync() => Dispatcher.CheckAccess() ? ReadOnUiAsync() : Dispatcher.InvokeAsync(() => ReadOnUiAsync()).Task.Unwrap();
    private Task OnUi(Action action) { if (Dispatcher.CheckAccess()) { action(); return Task.CompletedTask; } return Dispatcher.InvokeAsync(action).Task; }
    public void HelperLine(string line) => LoadProgress.Line(line);
    public void RefreshLoading()
    {
        var percent = LoadProgress.Percent();
        LoadingProgress.Value = percent; LoadingPercent.Text = Loc.T("youtube.percent", (int)Math.Round(percent));
        LoadingStatus.Text = Loc.T(LoadProgress.StatusKey);
        LoadingElapsed.Text = Loc.T(LoadProgress.UsesHelperLines ? "youtube.elapsed" : "youtube.elapsed_timed", (int)LoadProgress.Elapsed.TotalSeconds);
    }
    private async Task ReadOnUiAsync()
    {
        if (expired || reading) return;
        reading = true; loadCancelled = false; ReadButton.IsEnabled = false; Error.Text = "";
        Model = null; shown.Clear(); OptionsPanel.IsEnabled = ToolsRow.IsEnabled = FooterOptions.IsEnabled = false; UpdateAcceptance();
        LoadProgress = LoadProgressFactory(); LoadProgress.Start();
        loadCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        LoadPanel.Visibility = Visibility.Visible; CancelLoadButton.IsEnabled = true; RefreshLoading(); loadTimer.Start();
        var token = loadCancel.Token;
        try
        {
            var list = await lister(playlistUrl, token, line => OnUi(() => HelperLine(line))).ConfigureAwait(false);
            await OnUi(() =>
            {
                if (expired || cancel.IsCancellationRequested) return;
                LoadProgress.Building();
                var model = new PlaylistModel(list); Model = model; model.Changed += ModelChanged;
                automatic = true; try { if (!packageEdited) PackageName.Text = list.Title; } finally { automatic = false; }
                RebuildShown(); OptionsPanel.IsEnabled = ToolsRow.IsEnabled = FooterOptions.IsEnabled = true;
                TruncatedNote.Visibility = model.Truncated ? Visibility.Visible : Visibility.Collapsed;
                TruncatedNote.Text = model.Truncated ? Loc.T("playlist.truncated", YoutubePlaylists.MaxEntries) : "";
                LoadProgress.Finish(); RefreshLoading(); ReadButton.Style = (Style)FindResource(typeof(Button));
                UpdateFolder(); UpdateSummary(); UpdateAcceptance();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { await OnUi(() => { LoadProgress.Stop(); if (loadCancelled && !cancel.IsCancellationRequested) Error.Text = Loc.T("youtube.load_cancelled"); else if (!cancel.IsCancellationRequested) Error.Text = Loc.T("youtube.failed"); }).ConfigureAwait(false); }
        catch (Exception ex) { await OnUi(() => { LoadProgress.Stop(); Error.Text = YoutubeOptionsDialog.Describe(ex); }).ConfigureAwait(false); }
        finally
        {
            await OnUi(() =>
            {
                reading = false; loadTimer.Stop(); ReadButton.IsEnabled = !expired && !cancel.IsCancellationRequested;
                LoadingProgress.Value = Model is null ? LoadingProgress.Value : 100; LoadPanel.Visibility = Visibility.Collapsed; ReadHint.Visibility = Model is null ? Visibility.Visible : Visibility.Collapsed;
                loadCancel?.Dispose(); loadCancel = null; CancelLoadButton.IsEnabled = false; UpdateAcceptance();
            }).ConfigureAwait(false);
        }
    }

    private void Error_TextChanged(object sender, TextChangedEventArgs e) => ErrorRow.Visibility = Error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    private void CopyError_Click(object sender, RoutedEventArgs e) => SafeClipboard.TrySetText(Error.Text);

    private void RebuildShown()
    {
        shown.Clear(); if (Model is null) return;
        foreach (var r in Model.Visible) shown.Add(r);
    }
    private void ModelChanged() { UpdateSummary(); UpdateAcceptance(); }
    internal void UpdateSummary()
    {
        if (Model is null) { Summary.Text = ""; return; }
        Summary.Text = Loc.T("playlist.summary", Model.Title, Model.Total, Model.SelectedCount, PlaylistModel.FormatDuration(Model.TotalSeconds));
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) => Model?.SelectAll();
    private void SelectNone_Click(object sender, RoutedEventArgs e) => Model?.SelectNone();
    private void Invert_Click(object sender, RoutedEventArgs e) => Model?.Invert();
    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (Model is null) return; Model.SetFilter(FilterBox.Text); RebuildShown(); }

    private void Global_Changed(object sender, SelectionChangedEventArgs e) => ApplyGlobal();
    private void Global_Toggled(object sender, RoutedEventArgs e) => ApplyGlobal();
    private void Global_Text(object sender, TextChangedEventArgs e) => ApplyGlobal();
    private void ApplyGlobal()
    {
        if (QualityBox is null || AudioBox is null || AudioOnlyBox is null || Mp3Box is null || ContainerBox is null || SubtitleKindBox is null || SubtitleCodes is null || SubSeparate is null) return;
        if (Model is { } m)
        {
            m.Options.Quality = QualityBox.SelectedValue as string ?? "best";
            m.Options.AudioKbps = int.TryParse(AudioBox.SelectedValue as string, out var k) ? k : 0;
            m.Options.AudioOnly = AudioOnlyBox.IsChecked == true; m.Options.Mp3 = Mp3Box.IsChecked == true;
            m.Options.Container = ContainerBox.SelectedValue as string ?? "";
            m.Options.SubtitleKind = SubtitleKindBox.SelectedValue as string switch { "windows" => PlaylistSubtitleKind.Windows, "list" => PlaylistSubtitleKind.List, _ => PlaylistSubtitleKind.None };
            m.Options.SubtitleCodes = SubtitleCodes.Text; m.Options.SubtitlesSeparate = SubSeparate.IsChecked == true;
        }
        ContainerBox.IsEnabled = AudioOnlyBox.IsChecked != true; SubtitleKindBox.IsEnabled = AudioOnlyBox.IsChecked != true;
        var kind = SubtitleKindBox.SelectedValue as string;
        SubtitleCodesPanel.Visibility = kind == "list" && AudioOnlyBox.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
        SubtitleModePanel.Visibility = kind is "windows" or "list" && AudioOnlyBox.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
        QualityBox.IsEnabled = AudioOnlyBox.IsChecked != true;
        UpdateFolder();
    }

    private void Folder_Changed(object sender, TextChangedEventArgs e) { if (!automatic) folderEdited = true; UpdateFinalFolder(); }
    private void Package_Changed(object sender, TextChangedEventArgs e) { if (!automatic) packageEdited = true; UpdateFinalFolder(); }
    private void UpdateFolder()
    {
        if (folderEdited || Folder is null) return;
        automatic = true;
        try { Folder.Text = CategoryHelper.GetDownloadPath(Model?.Options.AudioOnly == true || AudioOnlyBox?.IsChecked == true ? "audio.mp3" : "video.mp4", settings, playlistUrl, null); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { Folder.Text = baseFolder; }
        finally { automatic = false; }
        UpdateFinalFolder();
    }
    private void UpdateFinalFolder()
    {
        if (FinalFolder is null) return;
        try { FinalFolder.Text = Loc.T("playlist.final_folder", ComputeFolder()); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { FinalFolder.Text = ""; }
    }
    private string ComputeFolder()
    {
        var root = Folder.Text.Trim(); var sub = PackageName.Text.Trim();
        return sub.Length == 0 ? Path.GetFullPath(root) : YoutubePlaylists.ResolveFolder(root, sub);
    }

    internal void UpdateAcceptance()
    {
        if (StartButton is null) return;
        var ready = !expired && !reading && Model is { SelectedCount: > 0 };
        StartButton.IsEnabled = QueueButton.IsEnabled = ScheduleButton.IsEnabled = ready;
        object? tip = ready ? null : Loc.T(Model is null ? "playlist.need_list" : "playlist.none_selected");
        StartButton.ToolTip = QueueButton.ToolTip = ScheduleButton.ToolTip = tip;
        Step2Label.Opacity = Model is null ? 0.6 : 1;
    }

    // ---- thumbnails: only rows that are on screen ask (the list is virtualised) ----
    /// <summary>1.13.0: a row whose picture failed asks again when it is shown again (scrolled back into view), but not more often than this.</summary>
    public static TimeSpan ThumbnailRetryDelay { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Scrolling shows rows again: those whose picture failed (and have waited the retry delay) ask once more. A row that stays in view is not asked again until it is scrolled away and back.</summary>
    private void RetryFailedThumbnails()
    {
        if (thumbnails is null || Model is null) return;
        foreach (var row in Model.Rows)
        {
            if (!row.ThumbnailFailed || row.Thumbnail is not null || DateTime.UtcNow - row.ThumbnailFailedAtUtc < ThumbnailRetryDelay) continue;
            if (VideoList.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement container || !container.IsVisible) continue;
            var top = container.TranslatePoint(new Point(0, 0), VideoList).Y;
            if (top + container.ActualHeight < 0 || top > VideoList.ActualHeight) continue;
            row.ThumbnailRequested = false; row.ThumbnailFailed = false; RequestThumbnail(row);
        }
    }
    private void Row_Loaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PlaylistRow row) return;
        if (row.ThumbnailFailed && row.Thumbnail is null && DateTime.UtcNow - row.ThumbnailFailedAtUtc >= ThumbnailRetryDelay) { row.ThumbnailRequested = false; row.ThumbnailFailed = false; }
        RequestThumbnail(row);
    }
    private void Row_Unloaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PlaylistRow { Thumbnail: null } row) { row.ThumbnailCancel?.Cancel(); row.ThumbnailRequested = false; }
    }
    public void RequestThumbnail(PlaylistRow row)
    {
        if (thumbnails is null || row.ThumbnailRequested || row.Thumbnail is not null) return;
        row.ThumbnailRequested = true; ThumbnailRequests++;
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token); row.ThumbnailCancel = source;
        _ = LoadThumbnailAsync(row, source);
    }
    private async Task LoadThumbnailAsync(PlaylistRow row, CancellationTokenSource source)
    {
        System.Windows.Media.ImageSource? image = null;
        try { image = await thumbnails!.LoadAsync(row.Entry.Id, row.Entry.ThumbnailUrl, source.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await OnUi(() =>
        {
            if (source.IsCancellationRequested) return;
            if (image is not null) row.Thumbnail = image; else { row.ThumbnailFailed = true; row.ThumbnailFailedAtUtc = DateTime.UtcNow; }
        }).ConfigureAwait(false);
    }

    // ---- keyboard: arrows move, Space toggles the row, A toggles audio only on the focused row ----
    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox or ComboBox or ComboBoxItem) return;
        if (VideoList.SelectedItem is not PlaylistRow row) return;
        if (e.Key == Key.Space) { row.IsSelected = !row.IsSelected; e.Handled = true; }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.None && row.Available) { row.AudioOnly = !row.AudioOnly; e.Handled = true; }
    }

    // ---- accepting ----
    private void Start_Click(object sender, RoutedEventArgs e) => Accept(DownloadOptionsDialog.DialogAction.StartNow);
    private void Queue_Click(object sender, RoutedEventArgs e) => Accept(DownloadOptionsDialog.DialogAction.Queue);
    private void Schedule_Click(object sender, RoutedEventArgs e)
    {
        if (SchedulePanel.Visibility != Visibility.Visible)
        {
            SchedulePanel.Visibility = Visibility.Visible;
            if (!presetSchedule) { ScheduleInput.Focus(); SchedulePanel.BringIntoView(); return; }
        }
        Accept(DownloadOptionsDialog.DialogAction.Schedule);
    }
    /// <summary>Builds one request per selected video and finishes the dialog. Nothing is looked up on the network here.</summary>
    public void Accept(DownloadOptionsDialog.DialogAction action)
    {
        if (expired || reading || Model is null) return;
        try
        {
            if (Model.SelectedCount == 0) throw new UserMessageException(Loc.T("playlist.none_selected"));
            DateTimeOffset? when = action == DownloadOptionsDialog.DialogAction.Schedule ? ScheduleInput.Require() : null;
            if (!int.TryParse(AtOnce.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var slots) || slots is < 1 or > 4) { AtOnce.Focus(); throw new UserMessageException(Loc.T("playlist.at_once_invalid")); }
            var folder = DownloadManager.EnsureWritableFolder(ComputeFolder());
            var package = PackageName.Text.Trim().Length > 0 ? PackageName.Text.Trim() : Model.Title;
            package = YoutubePlaylists.Clean(package, 120); if (package.Length == 0) package = "YouTube playlist";
            Requests = Model.BuildRequests(folder, package, slots, NumberBox.IsChecked == true);
            Package = package; Slots = slots; FinalFolderPath = folder; UserAction = action; ScheduledAt = when; Accepted = true;
            settings.PlaylistVideosAtOnce = slots;
            if (Modeless) Close(); else DialogResult = true;
        }
        catch (FormatException ex) { Error.Text = ex.Message; if (action == DownloadOptionsDialog.DialogAction.Schedule) ScheduleInput.Focus(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { Error.Text = ErrorMessages.Describe(ex); }
    }
}

/// <summary>Turns an accepted Playlist dialog into a package of jobs.</summary>
public static class PlaylistCommit
{
    public static async Task<IReadOnlyList<DownloadJob>> AddAsync(DownloadManager manager, IReadOnlyList<DownloadRequest> requests, DownloadOptionsDialog.DialogAction action, DateTimeOffset? scheduledAt = null)
    {
        var busy = action == DownloadOptionsDialog.DialogAction.Queue ? BusyQueue.Current?.BeforeAddAsyncSync(requests.Count) ?? BusyChoice.Wait : BusyChoice.Wait; // 1.15.0: one question for the whole package
        var result = await manager.AddManyAsync(requests, startPaused: false).ConfigureAwait(false);
        BusyQueue.Current?.AfterAdd(busy, result.Added);
        if (action == DownloadOptionsDialog.DialogAction.Schedule && scheduledAt is { } at) foreach (var job in result.Added) manager.Schedule(job.Id, at);
        return result.Added;
    }
}
