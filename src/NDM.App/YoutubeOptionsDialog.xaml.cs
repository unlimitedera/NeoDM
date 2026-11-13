using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
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
        DateTimeOffset? initialSchedule = null, Func<string, CancellationToken, Action<string>, Task<YoutubeMetadata>>? lineInspector = null, ThumbnailLoader? thumbnails = null, Func<string, CancellationToken, Task<SubtitleListing>>? subtitleLister = null)
    {
        this.thumbnails = thumbnails;
        this.subtitleLister = subtitleLister ?? (inspector is not null || lineInspector is not null ? (_, _) => Task.FromResult(SubtitleListing.Empty) : (url, token) => service.ListSubtitlesAsync(url, token));
        this.seed = seed with { Url = YoutubeUrls.Canonical(seed.Url) }; this.service = service; this.muxer = muxer;
        this.inspector = inspector is not null ? (url, token, _) => inspector(url, token) : lineInspector ?? ((url, token, line) => Task.Run(() => service.InspectAsync(url, token, line), token));
        this.settings = settings; LoadProgress = LoadProgressFactory();
        InitializeComponent(); WindowPlacements.Track(this, "Youtube");
        UrlDisplay.Text = this.seed.Url; Folder.Text = seed.TargetDirectory ?? folder;
        folderEdited = !string.IsNullOrWhiteSpace(seed.TargetDirectory) || settings is not null && !string.Equals(Path.GetFullPath(folder), Path.GetFullPath(settings.DownloadDirectory), StringComparison.OrdinalIgnoreCase); UpdateFolder();
        if (initialSchedule is { } at) { ScheduleInput.Text = at.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture); presetSchedule = true; }
        loadTimer.Tick += (_, _) => RefreshLoading();
        UpdateAcceptance();
        Closed += (_, _) => { loadTimer.Stop(); cancel.Cancel(); try { this.thumbnails?.Dispose(); } catch (ObjectDisposedException) { } };
    }
    private bool presetSchedule;
    private readonly ThumbnailLoader? thumbnails; // 1.13.0: owned by the dialog; the picture of the video next to its title
    /// <summary>The picture that was loaded for the video (null until it arrives or when none can be had). For tests.</summary>
    public System.Windows.Media.ImageSource? PreviewPicture => PreviewImage.Source;
    private async Task LoadPreviewAsync(string title)
    {
        PreviewTitle.Text = title; PreviewRow.Visibility = Visibility.Visible;
        var id = YoutubeLinks.Parse(seed.Url).VideoId;
        if (thumbnails is null || id is null) return;
        System.Windows.Media.ImageSource? image = null;
        try { image = await thumbnails.LoadAsync(id, YoutubePlaylists.StandardThumbnail(id), cancel.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await OnUi(() => { if (image is not null && !cancel.IsCancellationRequested) PreviewImage.Source = image; }).ConfigureAwait(false);
    }
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
                BuildPicker();
                FileName.Text = seed.FileName ?? metadata.Title; _ = LoadPreviewAsync(metadata.Title); SubtitleTask = LoadSubtitlesAsync();
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
        try { Folder.Text = CategoryHelper.GetDownloadPath(AudioOnlySelected ? "audio.mp3" : "video.mp4", settings, seed.Url, seed.Referer); }
        finally { automaticFolder = false; }
    }
    private bool AudioOnlySelected => picker?.Type == PickerType.AudioOnly;
    private void UpdateAcceptance()
    {
        if (StartButton is null || QueueButton is null || ScheduleButton is null) return;
        var ready = !expired && !accepting && metadata is not null && picker?.Result is not null;
        StartButton.IsEnabled = QueueButton.IsEnabled = ScheduleButton.IsEnabled = ready;
        // A disabled button says why (step 1 first); an enabled one needs no tip.
        object? tip = ready ? null : Loc.T("youtube.need_info");
        StartButton.ToolTip = QueueButton.ToolTip = ScheduleButton.ToolTip = tip;
        Step2Label.Opacity = metadata is null ? 0.6 : 1;
    }

    // ---- 1.13.0 format picker ----
    private YoutubeFormatPicker? picker;
    private bool syncing;
    /// <summary>For tests: the model behind the steps.</summary>
    public YoutubeFormatPicker? Picker => picker;
    public sealed record PickItem(string Title, string Detail, object Tag)
    {
        public Visibility DetailVisibility => Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    public sealed record RawItem(YoutubeFormat Format, string Group, int GroupOrder, string Text, string SizeText);
    public sealed class SubtitleItem : INotifyPropertyChanged
    {
        private bool ticked;
        public SubtitleItem(SubtitleLanguage language, bool yours) { Language = language; Yours = yours; }
        public SubtitleLanguage Language { get; }
        public bool Yours { get; }
        public bool IsTicked { get => ticked; set { if (ticked == value) return; ticked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTicked))); } }
        public string Text => EndFix(Language.Name + " (" + Language.Code + ")" + (Yours ? "  -  " + Loc.T("youtube.subs.yours") : ""));
        public FontWeight Weight => Yours ? FontWeights.SemiBold : FontWeights.Normal;
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly Func<string, CancellationToken, Task<SubtitleListing>> subtitleLister;
    private readonly ObservableCollection<SubtitleItem> manualItems = new(), autoItems = new();
    private ListCollectionView? rawView;
    /// <summary>The language tag used to highlight a subtitle language (the Windows display language). Tests set it.</summary>
    public string WindowsLanguage { get; set; } = CultureInfo.CurrentUICulture.Name;
    public Task SubtitleTask { get; private set; } = Task.CompletedTask;
    public IReadOnlyList<SubtitleItem> ManualSubtitles => manualItems;
    public IReadOnlyList<SubtitleItem> AutoSubtitles => autoItems;

    /// <summary>A text that ends with a closing bracket gets an invisible direction mark so the bracket does not flip when the window is mirrored (the mark follows the last letter of the text).</summary>
    public static string EndFix(string text)
    {
        if (text.Length == 0 || text[^1] != ')') return text;
        for (int i = text.Length - 1; i >= 0; i--)
            if (char.IsLetter(text[i])) return text + (text[i] < 0x590 ? "‎" : "‏");
        return text;
    }
    internal static string HeightText(int h) => h.ToString(CultureInfo.InvariantCulture) + "p";
    private static string ContainerLabel(string c) => c switch { "mp4" => "MP4", "webm" => "WebM", _ => "MKV" };
    private void BuildPicker()
    {
        picker = new YoutubeFormatPicker(metadata!.Formats);
        rawView = null; RawList.ItemsSource = null; RawExpander.IsExpanded = false;
        RefreshPicker();
    }
    private void RefreshPicker()
    {
        if (picker is null) return;
        syncing = true;
        try
        {
            var types = picker.AvailableTypes;
            TypeVideoAudio.IsEnabled = types.Contains(PickerType.VideoAudio); TypeVideoOnly.IsEnabled = types.Contains(PickerType.VideoOnly); TypeAudioOnly.IsEnabled = types.Contains(PickerType.AudioOnly);
            TypeVideoAudio.IsChecked = picker.Type == PickerType.VideoAudio; TypeVideoOnly.IsChecked = picker.Type == PickerType.VideoOnly; TypeAudioOnly.IsChecked = picker.Type == PickerType.AudioOnly;
            var containers = picker.AvailableContainers;
            ContainerRow.Visibility = containers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ContainerBox.ItemsSource = containers.Select(c => new PickItem(ContainerLabel(c), "", c)).ToArray();
            ContainerBox.SelectedItem = ((IEnumerable<PickItem>)ContainerBox.ItemsSource).FirstOrDefault(i => (string)i.Tag == picker.Container);
            var resolutions = picker.Resolutions;
            ResolutionRow.Visibility = picker.Type == PickerType.AudioOnly ? Visibility.Collapsed : Visibility.Visible;
            var items = resolutions.Select(r => new PickItem(r.IsBest ? Loc.T("youtube.pick.best") : HeightText(r.Height), ResolutionDetail(r, resolutions), r)).ToArray();
            Quality.ItemsSource = items; Quality.SelectedItem = items.FirstOrDefault(i => ((PickerResolution)i.Tag).Height == picker.Height) ?? items.FirstOrDefault();
            var audio = picker.AudioOptions;
            AudioRow.Visibility = picker.Type == PickerType.VideoOnly ? Visibility.Collapsed : Visibility.Visible;
            var audioItems = audio.Select(a => new PickItem(a.IsBest ? Loc.T("youtube.pick.best") + (a.Bitrate > 0 ? " (" + Loc.T("playlist.kbps", a.Bitrate) + ")" : "") : Loc.T("playlist.kbps", a.Bitrate), AudioDetail(a), a)).ToArray();
            Audio.ItemsSource = audioItems; Audio.SelectedItem = audioItems.FirstOrDefault(i => ((PickerAudio)i.Tag).Id == picker.AudioId) ?? audioItems.FirstOrDefault();
            Audio.Visibility = audioItems.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            AudioIncluded.Visibility = picker.Type == PickerType.VideoAudio && audioItems.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Mp3.Visibility = picker.Type == PickerType.AudioOnly ? Visibility.Visible : Visibility.Collapsed;
            if (picker.Type != PickerType.AudioOnly) Mp3.IsChecked = false; else picker.Mp3 = Mp3.IsChecked == true;
            if (picker.RawId is null) RawList.SelectedItem = null;
        }
        finally { syncing = false; }
        RefreshSummary();
    }
    private static string ResolutionDetail(PickerResolution r, IReadOnlyList<PickerResolution> all)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(r.Codec)) parts.Add(r.Codec!);
        if (YoutubeFormatPicker.FpsText(r.Fps) is { Length: > 0 } fps) parts.Add(fps);
        if (r.IsBest && all.Count > 1) parts.Insert(0, HeightText(all[1].Height));
        if (r.Size > 0) parts.Add(Loc.T("youtube.pick.about", YoutubeFormatPicker.Megabytes(r.Size)));
        return string.Join(" \u00B7 ", parts);
    }
    private static string AudioDetail(PickerAudio a)
    {
        var parts = new List<string> { a.Extension.ToUpperInvariant() };
        if (a.Size > 0) parts.Add(Loc.T("youtube.pick.about", YoutubeFormatPicker.Megabytes(a.Size)));
        return string.Join(" \u00B7 ", parts);
    }
    private void Type_Checked(object sender, RoutedEventArgs e)
    {
        if (syncing || picker is null) return;
        picker.Type = TypeAudioOnly.IsChecked == true ? PickerType.AudioOnly : TypeVideoOnly.IsChecked == true ? PickerType.VideoOnly : PickerType.VideoAudio;
        UpdateFolder(); RefreshPicker(); UpdateAcceptance();
    }
    private void Container_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || picker is null || ContainerBox.SelectedItem is not PickItem { Tag: string c }) return;
        picker.Container = c; RefreshPicker(); UpdateAcceptance();
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || picker is null || Quality.SelectedItem is not PickItem { Tag: PickerResolution r }) return;
        picker.Height = r.Height; RefreshPicker(); UpdateAcceptance();
    }
    private void Audio_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || picker is null || Audio.SelectedItem is not PickItem { Tag: PickerAudio a }) return;
        picker.AudioId = a.Id; RefreshSummary(); UpdateAcceptance();
    }
    private void Mp3_Changed(object sender, RoutedEventArgs e)
    {
        if (syncing || picker is null) return;
        picker.Mp3 = Mp3.IsChecked == true; RefreshSummary();
    }

    /// <summary>The sentence that says what will be downloaded, e.g. "1080p MP4 plus audio 128 kb/s, about 220 MB".</summary>
    public string SummaryText => Summary.Text;
    private void RefreshSummary()
    {
        if (Summary is null || RemuxNote is null || SubtitleWarning is null || SubtitlesPanel is null || SubEmbed is null || SubSeparate is null) return; // still loading the window
        if (picker is null || picker.Result is not { } r) { Summary.Text = ""; RemuxNote.Visibility = Visibility.Collapsed; return; }
        string core;
        var container = r.Extension.ToUpperInvariant();
        if (r.Selection.Mp3) core = Loc.T("youtube.summary.mp3");
        else if (!r.Primary.Video) core = Loc.T("youtube.summary.audio_only", r.Primary.Bitrate, r.Primary.Extension.ToUpperInvariant());
        else if (r.Selection.VideoOnly) core = Loc.T("youtube.summary.video_only", HeightText(r.Height), container);
        else if (r.Audio is null) core = Loc.T("youtube.summary.video_included", HeightText(r.Height), container);
        else core = Loc.T("youtube.summary.video_audio", HeightText(r.Height), container, r.AudioBitrate);
        if (r.EstimatedSize > 0) core += Loc.T("youtube.summary.size", YoutubeFormatPicker.Megabytes(r.EstimatedSize));
        var chosen = TickedSubtitles();
        if (chosen.Count > 0 && r.Primary.Video) core += Loc.T(SubSeparate.IsChecked == true ? "youtube.summary.subs_separate" : "youtube.summary.subs_embedded", chosen.Count);
        Summary.Text = EndFix(core);
        RemuxNote.Text = r.NeedsRemux ? Loc.T("youtube.pick.remux", ContainerLabel(r.Selection.Container ?? r.Extension)) : "";
        RemuxNote.Visibility = r.NeedsRemux ? Visibility.Visible : Visibility.Collapsed;
        var embedWebm = chosen.Count > 0 && SubEmbed.IsChecked == true && SubtitleRules.EmbedChangesContainer(PlainContainer(r));
        SubtitleWarning.Text = embedWebm ? Loc.T("youtube.subs.webm_warning") : "";
        SubtitleWarning.Visibility = embedWebm ? Visibility.Visible : Visibility.Collapsed;
        SubtitlesPanel.Visibility = manualItems.Count + autoItems.Count > 0 && r.Primary.Video ? Visibility.Visible : Visibility.Collapsed;
    }
    private static string PlainContainer(PickerResult r) => r.Selection.Container ?? (r.Selection.AudioId is not null ? "mkv" : r.Primary.Extension);

    // ---- all formats (advanced) ----
    private void Raw_Expanded(object sender, RoutedEventArgs e)
    {
        if (metadata is null || rawView is not null) return;
        string Group(YoutubeFormat f) => f.Video ? (f.Audio ? Loc.T("youtube.pick.type_video_audio") : Loc.T("youtube.pick.type_video_only")) : Loc.T("youtube.pick.type_audio_only");
        int Order(YoutubeFormat f) => f.Video ? (f.Audio ? 0 : 1) : 2;
        var items = metadata.Formats.Select(f => new RawItem(f, Group(f), Order(f),
            f.Video ? $"{f.Id}  {f.Extension}  {HeightText(f.Height)}  {f.VideoCodec ?? ""}  {YoutubeFormatPicker.FpsText(f.Fps)}".TrimEnd() : $"{f.Id}  {f.Extension}  {Loc.T("playlist.kbps", f.Bitrate)}  {f.AudioCodec ?? ""}".TrimEnd(),
            f.Size > 0 ? YoutubeFormatPicker.Megabytes(f.Size) : "")).ToList();
        rawView = new ListCollectionView(items);
        rawView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RawItem.Group)));
        syncing = true;
        try { RawSort.ItemsSource = new[] { Loc.T("youtube.pick.sort_resolution"), Loc.T("youtube.pick.sort_size"), Loc.T("youtube.pick.sort_id") }; RawSort.SelectedIndex = 0; }
        finally { syncing = false; }
        ApplyRawSort(); RawList.ItemsSource = rawView;
    }
    private void ApplyRawSort()
    {
        if (rawView is null) return;
        rawView.SortDescriptions.Clear();
        rawView.SortDescriptions.Add(new SortDescription(nameof(RawItem.GroupOrder), ListSortDirection.Ascending));
        switch (RawSort.SelectedIndex)
        {
            case 1: rawView.SortDescriptions.Add(new SortDescription("Format.Size", ListSortDirection.Descending)); break;
            case 2: rawView.SortDescriptions.Add(new SortDescription("Format.Id", ListSortDirection.Ascending)); break;
            default: rawView.SortDescriptions.Add(new SortDescription("Format.Height", ListSortDirection.Descending)); rawView.SortDescriptions.Add(new SortDescription("Format.Bitrate", ListSortDirection.Descending)); break;
        }
    }
    private void Raw_Sort(object sender, SelectionChangedEventArgs e) { if (!syncing) ApplyRawSort(); }
    private void Raw_Filter(object sender, TextChangedEventArgs e)
    {
        if (rawView is null) return;
        var q = RawSearch.Text.Trim();
        rawView.Filter = q.Length == 0 ? null : o => o is RawItem r && (r.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Group.Contains(q, StringComparison.OrdinalIgnoreCase));
    }
    private void Raw_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || picker is null || RawList.SelectedItem is not RawItem item) return;
        UseRawFormat(item.Format.Id);
    }
    /// <summary>Takes one exact format from the full list (the steps above show what it is closest to).</summary>
    public void UseRawFormat(string id)
    {
        if (picker is null || !picker.UseRaw(id)) return;
        RefreshSummary(); UpdateAcceptance();
    }

    // ---- subtitles ----
    private async Task LoadSubtitlesAsync()
    {
        SubtitleListing listing;
        try { listing = await subtitleLister(seed.Url, cancel.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { listing = SubtitleListing.Empty; }
        await OnUi(() =>
        {
            if (cancel.IsCancellationRequested || expired || metadata is null) return;
            var yours = SubtitleRules.MatchWindowsLanguage(listing.Manual.Select(l => l.Code), WindowsLanguage) ?? (listing.Manual.Count == 0 ? SubtitleRules.MatchWindowsLanguage(listing.Automatic.Select(l => l.Code), WindowsLanguage) : null);
            manualItems.Clear(); autoItems.Clear();
            foreach (var l in listing.Manual) manualItems.Add(NewSubtitle(l, l.Code == yours));
            foreach (var l in listing.Automatic) autoItems.Add(NewSubtitle(l, listing.Manual.Count == 0 && l.Code == yours));
            ManualList.ItemsSource = manualItems; AutoList.ItemsSource = autoItems;
            ManualHeader.Visibility = ManualList.Visibility = manualItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AutoHeader.Visibility = AutoScroll.Visibility = autoItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SubtitleStatus.Text = Loc.T("youtube.subs.none_ticked");
            RefreshSummary();
        }).ConfigureAwait(false);
    }
    private SubtitleItem NewSubtitle(SubtitleLanguage language, bool yours) => new(language, yours);
    private void Subtitle_Toggled(object sender, RoutedEventArgs e) { if (!syncing) RefreshSummary(); }
    private List<SubtitleChoice> TickedSubtitles() => manualItems.Where(i => i.IsTicked).Select(i => new SubtitleChoice(i.Language.Code, false)).Concat(autoItems.Where(i => i.IsTicked).Select(i => new SubtitleChoice(i.Language.Code, true))).ToList();
    /// <summary>For tests: ticks a subtitle language and refreshes the summary.</summary>
    public void TickSubtitle(string code, bool auto, bool value) { (auto ? autoItems : manualItems).First(i => i.Language.Code == code).IsTicked = value; RefreshSummary(); }

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
        if (expired || reading || accepting || metadata is null || picker?.Result is not { } picked) return;
        accepting = true; StartButton.IsEnabled = QueueButton.IsEnabled = ScheduleButton.IsEnabled = ReadButton.IsEnabled = false;
        try
        {
            DateTimeOffset? when = null;
            if (action == DownloadOptionsDialog.DialogAction.Schedule) when = ScheduleInput.Require();
            var selection = picked.Selection;
            var ticked = TickedSubtitles();
            if (ticked.Count > 0 && picked.Primary.Video) selection = selection with { Subtitles = new SubtitleRequest(SubSeparate.IsChecked == true ? SubtitleMode.Separate : SubtitleMode.Embed, ticked) };
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
        DownloadJob job; var busy = action == DownloadOptionsDialog.DialogAction.Queue ? BusyQueue.Current?.BeforeAddAsyncSync(1) ?? BusyChoice.Wait : BusyChoice.Wait; // 1.15.0
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
        BusyQueue.Current?.AfterAdd(busy, new[] { job });
        return job;
    }
    /// <summary>Only Download now opens the progress window.</summary>
    public static bool StartsNow(DownloadOptionsDialog.DialogAction action) => action == DownloadOptionsDialog.DialogAction.StartNow;
}
