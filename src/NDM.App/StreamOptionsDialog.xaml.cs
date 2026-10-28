using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>The window for a video stream (HLS or DASH): reads the playlist, then offers quality, sound track, file type, folder and when to start.
/// It is its own top-level window like the capture dialog. The caller reads <see cref="Request"/>, <see cref="UserAction"/> and <see cref="ScheduledAt"/> after it closes
/// and adds the download with <see cref="StreamCommit.AddAsync"/>.</summary>
public partial class StreamOptionsDialog : Window
{
    public enum DialogState { Reading, Failed, Ready }
    public enum DialogAction { Cancel, Queue, Schedule, StartNow }

    /// <summary>Reads the playlist; the real one is <c>DownloadManager.InspectStreamAsync</c>.</summary>
    public delegate Task<StreamManifest> Inspector(DownloadRequest request, CancellationToken cancellation);

    public DialogState State { get; private set; } = DialogState.Reading;
    public StreamManifest? Manifest { get; private set; }
    public DownloadRequest? Request { get; private set; }
    public DateTimeOffset? ScheduledAt { get; private set; }
    public DialogAction UserAction { get; private set; } = DialogAction.Cancel;
    public bool Accepted { get; private set; }
    /// <summary>The connection to the browser is gone (the browser or the page went away): nothing may be added any more.</summary>
    public bool Expired { get; private set; }
    public string SavePath { get; private set; } = "";
    public string Category { get; private set; } = "Video";
    /// <summary>The read of the playlist; completes when the dialog shows the result or the failure.</summary>
    public Task ReadTask { get; private set; } = Task.CompletedTask;
    public Task? InstallTask { get; private set; }
    public string FileNameText => NameInput.Text;
    /// <summary>The failure text shown (empty unless <see cref="State"/> is Failed).</summary>
    public string FailureMessage => FailureText.Text;
    public ToolInstallRun Installer { get; }
    public bool ToolPanelVisible => ToolPanel.Visibility == Visibility.Visible;

    private readonly string url, defaultDirectory;
    private readonly DownloadRequest seed;
    private readonly Inspector inspect;
    private readonly IVideoTool tool;
    private readonly Func<string, string>? autoFolder;
    private readonly CancellationTokenSource readCancel = new();
    private bool programmatic, nameEdited, pathEdited, categoryEdited, toolDeclined, installing;
    private string? toolResult; private bool toolResultIsError;

    /// <param name="seed">What the browser or the user gave: address, name hint, referer, cookie and the request identity.</param>
    public StreamOptionsDialog(DownloadRequest seed, string defaultDownloadDirectory, Inspector inspect, IVideoTool tool, Func<string, string>? autoFolder = null)
    {
        this.seed = seed; url = seed.Url; defaultDirectory = defaultDownloadDirectory; this.inspect = inspect; this.tool = tool; this.autoFolder = autoFolder;
        Installer = new ToolInstallRun(tool);
        InitializeComponent(); WindowPlacements.Track(this, "Stream");
        UrlDisplay.Text = url;
        var uri = new Uri(url);
        SetName(StreamNaming.BaseName(seed.FileName, uri));
        NameInput.TextChanged += (_, _) => { if (!programmatic) nameEdited = true; if (NameInput.Text.Trim().Length > 0) Title = NameInput.Text.Trim(); };
        PathInput.TextChanged += (_, _) => { if (!programmatic) pathEdited = true; };
        CategoryDropdown.SelectionChanged += (_, _) => OnCategoryChanged();
        Installer.Progress = fraction => { ToolBar.IsIndeterminate = fraction is null; if (fraction is { } f) ToolBar.Value = f * 100; };
        Closed += (_, _) => { readCancel.Cancel(); Installer.Cancel(); };
        Loaded += (_, _) => { if (State == DialogState.Ready) { NameInput.Focus(); NameInput.SelectAll(); } else CancelButton.Focus(); };
        ApplyState();
        ReadTask = ReadAsync();
    }

    /// <summary>The address is on this computer or the local network: say so next to it. Never blocks.</summary>
    public void ShowPrivateNetworkWarning() => PrivateNetworkNotice.Visibility = Visibility.Visible;
    public bool PrivateNetworkWarningVisible => PrivateNetworkNotice.Visibility == Visibility.Visible;

    /// <summary>The browser asked for this and has gone away: the dialog is locked and says so; the user can only close it.</summary>
    public void MarkExpired(string message)
    {
        if (Expired || Accepted) return;
        Expired = true;
        try { readCancel.Cancel(); } catch (ObjectDisposedException) { }
        StartButton.IsEnabled = QueueButton.IsEnabled = ScheduleButton.IsEnabled = false;
        if (State == DialogState.Reading) { FailureText.Text = message; State = DialogState.Failed; ApplyState(); return; }
        ValidationError.Text = message;
    }

    // ---- reading the playlist ----

    private async Task ReadAsync()
    {
        try
        {
            var manifest = await inspect(seed with { StreamKind = StreamKind.None, StreamVariantId = null, StreamAudioId = null }, readCancel.Token);
            if (readCancel.IsCancellationRequested) return;
            Manifest = manifest;
            FillChoices(manifest);
            State = DialogState.Ready;
        }
        catch (OperationCanceledException) when (readCancel.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailureText.Text = StreamMessages.Describe(ex);
            State = DialogState.Failed;
        }
        ApplyState();
        if (State == DialogState.Ready && IsLoaded) { NameInput.Focus(); NameInput.SelectAll(); }
    }

    private void ApplyState()
    {
        ReadingPanel.Visibility = State == DialogState.Reading ? Visibility.Visible : Visibility.Collapsed;
        FailedPanel.Visibility = State == DialogState.Failed ? Visibility.Visible : Visibility.Collapsed;
        ReadyPanel.Visibility = State == DialogState.Ready ? Visibility.Visible : Visibility.Collapsed;
        bool ready = State == DialogState.Ready;
        QueueButton.Visibility = ScheduleButton.Visibility = StartButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = Loc.T(State == DialogState.Failed ? "common.btn.close" : "common.btn.cancel");
        CancelButton.IsDefault = State == DialogState.Failed;
    }

    // ---- the choices ----

    private void FillChoices(StreamManifest m)
    {
        programmatic = true;
        try
        {
            QualityBox.Items.Clear();
            var variants = m.Variants.OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth).ToList();
            var best = m.BestVariant;
            foreach (var v in variants) QualityBox.Items.Add(new ComboBoxItem { Content = StreamMessages.QualityLabel(m, v, m.DefaultAudio(v)), Tag = v });
            QualityBox.SelectedIndex = Math.Max(0, variants.FindIndex(v => v.Id == best?.Id));
            QualityBox.IsEnabled = variants.Count > 1;
            FormatBox.Items.Clear();
            FormatBox.Items.Add(new ComboBoxItem { Content = Loc.T("stream.format.mp4"), Tag = "mp4" });
            FormatBox.Items.Add(new ComboBoxItem { Content = Loc.T("stream.format.ts"), Tag = "ts" });
            FormatBox.SelectedIndex = 0;
        }
        finally { programmatic = false; }
        FillAudio();
        Refresh();
    }

    public VideoVariant? SelectedVariant => (QualityBox.SelectedItem as ComboBoxItem)?.Tag as VideoVariant;
    /// <summary>The chosen separate sound track; null when none is chosen or the picture already carries its sound.</summary>
    public AudioTrack? SelectedAudio => AudioPanel.Visibility == Visibility.Visible ? (AudioBox.SelectedItem as ComboBoxItem)?.Tag as AudioTrack : null;
    private string? SelectedAudioId => AudioPanel.Visibility != Visibility.Visible ? null : (AudioBox.SelectedItem as ComboBoxItem)?.Tag is AudioTrack a ? a.Id : StreamSelection.NoAudio;

    private void FillAudio()
    {
        var m = Manifest; if (m is null) return;
        programmatic = true;
        try
        {
            AudioBox.Items.Clear();
            var list = m.AudioFor(SelectedVariant).ToList();
            AudioPanel.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (list.Count == 0) return;
            for (int i = 0; i < list.Count; i++) AudioBox.Items.Add(new ComboBoxItem { Content = StreamMessages.AudioLabel(list[i], i), Tag = list[i] });
            AudioBox.Items.Add(new ComboBoxItem { Content = Loc.T("stream.audio.none"), Tag = StreamSelection.NoAudio });
            var def = m.DefaultAudio(SelectedVariant);
            AudioBox.SelectedIndex = Math.Max(0, def is null ? 0 : list.FindIndex(a => a.Id == def.Id));
        }
        finally { programmatic = false; }
    }

    /// <summary>The file type choice matters for one track of transport stream pieces (HLS): MP4 needs the tool, TS does not. Fragmented MP4 and DASH come out as MP4 anyway.</summary>
    public bool FormatChoiceVisible => FormatPanel.Visibility == Visibility.Visible;
    public bool PreferMp4 => FormatChoiceVisible && (FormatBox.SelectedItem as ComboBoxItem)?.Tag as string == "mp4";
    /// <summary>Picture and sound come as separate tracks and both are wanted.</summary>
    public bool JoinsTracks => SelectedAudio is not null && SelectedVariant is { HasOwnAudio: false };
    /// <summary>The choice needs the video tool and it is not available.</summary>
    public bool NeedsTool => Manifest is not null && !tool.IsAvailable && (JoinsTracks || PreferMp4);

    private void Quality_Changed(object sender, SelectionChangedEventArgs e) { if (programmatic || Manifest is null) return; FillAudio(); Refresh(); }
    private void Choice_Changed(object sender, SelectionChangedEventArgs e) { if (programmatic || Manifest is null) return; Refresh(); }

    private void Refresh()
    {
        var m = Manifest; if (m is null) return;
        bool tsPossible = m.Kind == StreamKind.Hls && m.PieceContainer is null or StreamContainer.Ts;
        FormatPanel.Visibility = tsPossible && !JoinsTracks ? Visibility.Visible : Visibility.Collapsed;
        EstimateInfo.Text = StreamMessages.EstimateLine(m.EstimateBytes(SelectedVariant, SelectedAudio), m.Duration);
        EstimateInfo.Visibility = EstimateInfo.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshToolPanel();
    }

    // ---- the video tool ----

    private void RefreshToolPanel()
    {
        bool needs = NeedsTool;
        bool shown;
        string text = ""; bool buttons = false, bar = false, cancel = false, copy = false;
        if (installing) { shown = true; text = Loc.T("tool.installing"); bar = true; cancel = true; }
        else if (toolResult is not null) { shown = true; text = toolResult; copy = toolResultIsError; buttons = toolResultIsError && needs; }
        else if (needs && toolDeclined) { shown = true; text = Loc.T(JoinsTracks ? "tool.declined_join" : "tool.declined_ts"); }
        else if (needs) { shown = true; text = Loc.T(JoinsTracks ? "tool.notice_join" : "tool.notice_ts"); buttons = true; }
        else shown = false;
        ToolPanel.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        ToolText.Text = text;
        ToolBar.Visibility = bar ? Visibility.Visible : Visibility.Collapsed;
        InstallToolButton.Visibility = ContinueToolButton.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
        CancelToolButton.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed;
        CopyToolButton.Visibility = copy ? Visibility.Visible : Visibility.Collapsed;
        LicenceLine.Visibility = shown && (needs || installing) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InstallTool_Click(object sender, RoutedEventArgs e) => InstallTask = InstallToolAsync();
    private void ContinueTool_Click(object sender, RoutedEventArgs e) { toolDeclined = true; toolResult = null; RefreshToolPanel(); }
    private void CancelTool_Click(object sender, RoutedEventArgs e) => Installer.Cancel();
    private void CopyTool_Click(object sender, RoutedEventArgs e) { if (toolResult is { } t) SafeClipboard.TrySetText(t); }
    private void CopyFailure_Click(object sender, RoutedEventArgs e) => SafeClipboard.TrySetText(FailureText.Text);
    private void Licence_Click(object sender, RoutedEventArgs e) => UrlOpener.Open(VideoToolInfo.LicenceUrl);

    /// <summary>Downloads and installs the video tool on the user's click; the dialog stays usable and shows progress, a result and Cancel.</summary>
    public async Task InstallToolAsync()
    {
        if (installing) return;
        installing = true; toolResult = null; toolDeclined = false;
        ToolBar.IsIndeterminate = true; ToolBar.Value = 0;
        RefreshToolPanel();
        Installer.Progress = fraction => { ToolBar.IsIndeterminate = fraction is null; if (fraction is { } f) ToolBar.Value = f * 100; };
        var outcome = await Installer.RunAsync();
        installing = false;
        switch (outcome)
        {
            case ToolInstallRun.Outcome.Installed: toolResult = Loc.T("tool.installed"); toolResultIsError = false; break;
            case ToolInstallRun.Outcome.Canceled: toolResult = null; break;
            default: toolResult = Installer.Message ?? Loc.T("tool.error.other"); toolResultIsError = true; break;
        }
        RefreshToolPanel();
    }

    // ---- name, folder ----

    private void SetName(string name)
    {
        programmatic = true;
        try
        {
            if (!nameEdited) { NameInput.Text = name; Title = name; }
            if (!categoryEdited) { Category = "Video"; SelectCategory("Video"); }
            if (!pathEdited)
            {
                var path = !categoryEdited && autoFolder is not null ? autoFolder((nameEdited ? NameInput.Text.Trim() : name) + ".mp4") : GetCategoryPath(Category);
                PathInput.Text = path; SavePath = path;
            }
        }
        finally { programmatic = false; }
    }

    private void OnCategoryChanged()
    {
        if (programmatic) return;
        categoryEdited = true;
        if (pathEdited) return;
        programmatic = true;
        try { var path = GetCategoryPath(Category = (CategoryDropdown.SelectedItem as ComboBoxItem)?.Tag as string ?? Category); PathInput.Text = path; SavePath = path; }
        finally { programmatic = false; }
    }

    /// <summary>True while neither the folder nor the category was chosen by the user: the engine's own category folder applies.</summary>
    public bool PathIsAuto => !pathEdited && !categoryEdited;

    private void SelectCategory(string category)
    {
        foreach (ComboBoxItem item in CategoryDropdown.Items)
            if (item.Tag as string == category) { CategoryDropdown.SelectedItem = item; break; }
    }

    private string GetCategoryPath(string category) => category == "General" ? defaultDirectory : Path.Combine(defaultDirectory, category);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("capture.picker.title"), InitialDirectory = PathInput.Text };
        if (dialog.ShowDialog() == true) { PathInput.Text = dialog.FolderName; pathEdited = true; }
    }

    // ---- the decision ----

    private void Finish(bool accepted)
    {
        Accepted = accepted;
        try { DialogResult = accepted; } // only valid for ShowDialog
        catch (InvalidOperationException) { Close(); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { UserAction = DialogAction.Cancel; Finish(false); }
    private void Queue_Click(object sender, RoutedEventArgs e) { if (Prepare()) { UserAction = DialogAction.Queue; Finish(true); } }
    private void Start_Click(object sender, RoutedEventArgs e) { if (Prepare()) { UserAction = DialogAction.StartNow; Finish(true); } }

    private void Schedule_Click(object sender, RoutedEventArgs e)
    {
        SchedulePanel.Visibility = Visibility.Visible;
        if (!Prepare()) return;
        try
        {
            ScheduledAt = DownloadDialogValidation.ParseSchedule(ScheduleInput.Text);
            UserAction = DialogAction.Schedule;
            Finish(true);
        }
        catch (FormatException ex)
        {
            ValidationError.Text = ErrorMessages.Describe(ex);
            UpdateLayout(); ValidationError.BringIntoView();
        }
    }

    private bool Prepare()
    {
        if (Expired || State != DialogState.Ready || Manifest is null) return false;
        try
        {
            ValidationError.Text = "";
            NativeBridge.ValidateUrl(url);
            string name = NameInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) throw new UserMessageException(Loc.T("capture.error.name_empty"));
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')) throw new UserMessageException(Loc.T("error.file_name_invalid"));
            string savePath = PathInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(savePath)) throw new UserMessageException(Loc.T("capture.error.path_empty"));
            if (!Path.IsPathFullyQualified(savePath)) throw new UserMessageException(Loc.T("capture.error.path_full"));
            try { savePath = DownloadManager.EnsureWritableFolder(savePath); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                throw new UserMessageException(ex is ArgumentException ? ErrorMessages.Describe(ex.Message) : Loc.T("capture.error.folder", ErrorMessages.OneLine(ex.Message)));
            }
            var m = Manifest;
            var variant = SelectedVariant;
            Request = new DownloadRequest(url, name, seed.Referer, seed.Cookie, false, savePath, null, null, seed.UserAgent, seed.Accept, seed.AcceptLanguage,
                m.Kind, m.Variants.Count > 1 ? variant?.Id : null, SelectedAudioId, PreferMp4);
            SavePath = savePath;
            if ((CategoryDropdown.SelectedItem as ComboBoxItem)?.Tag is string category) Category = category;
            return true;
        }
        catch (FormatException ex)
        {
            ValidationError.Text = ErrorMessages.Describe(ex);
            UpdateLayout(); ValidationError.BringIntoView();
            return false;
        }
    }
}

/// <summary>Turns an accepted stream dialog into a job.</summary>
public static class StreamCommit
{
    /// <summary>Null when the dialog was not accepted with a valid action.</summary>
    public static async Task<DownloadJob?> AddAsync(DownloadManager manager, StreamOptionsDialog dialog)
    {
        if (dialog.Request is null || !dialog.Accepted) return null;
        return dialog.UserAction switch
        {
            StreamOptionsDialog.DialogAction.StartNow or StreamOptionsDialog.DialogAction.Queue => await manager.AddStreamAsync(dialog.Request),
            StreamOptionsDialog.DialogAction.Schedule => await manager.AddStreamAsync(dialog.Request, dialog.ScheduledAt),
            _ => null
        };
    }

    /// <summary>Start now and Add to queue open the progress window; a scheduled download stays quiet.</summary>
    public static bool StartsNow(StreamOptionsDialog dialog) => dialog.UserAction is StreamOptionsDialog.DialogAction.StartNow or StreamOptionsDialog.DialogAction.Queue;
}

/// <summary>A capture request that carries <c>stream</c>: opens the stream dialog (never the file dialog), adds the download and answers the browser.</summary>
public static class StreamCapture
{
    /// <summary>Runs on the UI thread. <paramref name="onDialog"/> is for tests that drive the dialog; <paramref name="showProgress"/> opens the progress window of a download that starts now.</summary>
    public static async Task HandleAsync(NativeBridge.BrowserDownloadRequest request, DownloadManager manager, AppSettings settings, TrayWindowList windows, IVideoTool tool,
        Action<StreamOptionsDialog>? onDialog = null, Action<string>? showProgress = null, Action<string>? captured = null)
    {
        var seed = new DownloadRequest(request.Url, request.FileName, request.Referer, request.Cookie, false, null, null, null, request.UserAgent, request.Accept, request.AcceptLanguage);
        var dialog = new StreamOptionsDialog(seed, settings.DownloadDirectory, (r, ct) => manager.InspectStreamAsync(r, ct), tool, fileName => CategoryHelper.GetDownloadPath(fileName, settings));
        try
        {
            _ = PrivateNetwork.IsPrivateUrlAsync(request.Url).ContinueWith(t => { if (t.IsCompletedSuccessfully && t.Result) dialog.Dispatcher.BeginInvoke(new Action(dialog.ShowPrivateNetworkWarning)); }, TaskScheduler.Default);
            // The wait for the user is unbounded; if the browser side goes away meanwhile the dialog says so and can no longer add a download.
            request.Gone.Register(() => dialog.Dispatcher.BeginInvoke(new Action(() => dialog.MarkExpired(Loc.T("stream.expired")))));
            onDialog?.Invoke(dialog);
            bool accepted = await StreamFlow.ShowAsync(dialog, windows);
            if (!accepted || dialog.Request is null) { request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, "Download cancelled in NeoDM.", Cancelled: true)); return; }
            if (request.Gone.IsCancellationRequested) { request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, "The browser connection was lost before you decided.")); return; }
            var job = await StreamCommit.AddAsync(manager, dialog);
            if (job is null) { request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, "Download cancelled in NeoDM.", Cancelled: true)); return; }
            captured?.Invoke(job.Id);
            if (StreamCommit.StartsNow(dialog)) showProgress?.Invoke(job.Id);
            request.Respond(new NativeBridge.BrowserDownloadResponse(true, job.Id));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { dialog.Close(); } catch (InvalidOperationException) { }
            request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, ex.Message));
        }
    }
}

public static class StreamFlow
{
    /// <summary>True = the user accepted (Start now / Add to queue / Schedule); false = cancelled or closed. Independent window, listed in the tray menu like the capture dialog.</summary>
    public static Task<bool> ShowAsync(StreamOptionsDialog dialog, TrayWindowList windows, bool bringToFront = true)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Owner = null;
        dialog.ShowInTaskbar = true;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        windows.Add(dialog, TrayWindowKind.Options, () => dialog.FileNameText);
        dialog.Closed += (_, _) => done.TrySetResult(dialog.Accepted);
        if (bringToFront) dialog.ContentRendered += (_, _) => { if (dialog.IsVisible) WindowActivation.BringToFront(dialog); }; // the window may already be closed when its first frame arrives
        dialog.Show();
        return done.Task;
    }
}
