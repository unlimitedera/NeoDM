using System.IO;
using System.Windows;
using Microsoft.Win32;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

public partial class DownloadOptionsDialog : Window
{
    public enum DialogAction { Cancel, Queue, Schedule, StartNow }
    
    public DownloadRequest? Request { get; private set; }
    public DateTimeOffset? ScheduledAt { get; private set; }
    public long? SpeedLimit { get; private set; }
    public DialogAction UserAction { get; private set; } = DialogAction.Cancel;
    public string SavePath { get; private set; } = "";
    public string Category { get; private set; } = "General";

    private readonly string _defaultDownloadDirectory;
    private readonly Func<string, string>? _autoFolder;
    private readonly Uri _uri;
    private readonly string? _browserName, _browserMime;
    private readonly Func<CancellationToken, Task<NameProbeResult?>>? _probe;
    private readonly CancellationTokenSource _probeCancel = new();
    private bool _programmatic, _nameEdited, _pathEdited, _categoryEdited;
    private NameProbeResult? _seen;

    /// <summary>True once the user pressed Start / Add to Queue / Schedule (a closed window or Cancel is false).</summary>
    public bool Accepted { get; private set; }
    /// <summary>The file name as currently shown (used as the tray menu label).</summary>
    public string FileNameText => NameInput.Text;
    /// <summary>What the quick probe learned (null until it finished, or when the server could not be reached).</summary>
    public NameProbeResult? Detected => _seen;
    public Task ProbeTask { get; private set; } = Task.CompletedTask;

    /// <param name="suggestedFileName">The name the browser itself decided (may be empty).</param>
    /// <param name="mime">The browser's content type for the download (may be empty).</param>
    /// <param name="probe">Quick HEAD / ranged-GET look at the link; fills the name, size and type when it answers.</param>
    public DownloadOptionsDialog(string url, string? suggestedFileName, string? referer, string? cookie, string defaultDownloadDirectory,
        string? mime = null, Func<CancellationToken, Task<NameProbeResult?>>? probe = null, Func<string, string>? autoFolder = null)
    {
        _autoFolder = autoFolder;
        _defaultDownloadDirectory = defaultDownloadDirectory;
        _uri = new Uri(url);
        _browserName = string.IsNullOrWhiteSpace(suggestedFileName) ? null : suggestedFileName;
        _browserMime = string.IsNullOrWhiteSpace(mime) ? null : mime;
        _probe = probe;
        InitializeComponent(); WindowPlacements.Track(this, "Capture");
        
        UrlDisplay.Text = url;
        
        // Best name known before the server answers: the browser's, else the address.
        SetName(FileNameResolver.Resolve(_uri, _browserName, _browserMime, null).Name);
        
        // Store referer and cookie for later use
        Tag = new { Referer = referer, Cookie = cookie };

        NameInput.TextChanged += (_, _) => { if (!_programmatic) _nameEdited = true; if (NameInput.Text.Trim().Length > 0) Title = NameInput.Text.Trim(); };
        PathInput.TextChanged += (_, _) => { if (!_programmatic) _pathEdited = true; };
        CategoryDropdown.SelectionChanged += (_, _) => OnCategoryChanged();
        Closed += (_, _) => { _probeCancel.Cancel(); ExtensionBanner.CommitMute(); };
        Loaded += (_, _) => { NameInput.Focus(); NameInput.SelectAll(); };
        if (_probe is null) DetectedInfo.Visibility = Visibility.Collapsed;
        else
        {
            DetectedInfo.Text = Loc.T("capture.detect.checking");
            ProbeTask = RunProbeAsync();
        }
    }

    /// <summary>The extension version notice at the top of the dialog. Informational only: the download controls below are unaffected.</summary>
    public ExtensionNoticeBanner NoticeBanner => ExtensionBanner;
    public void ShowExtensionNotice(ExtensionCheck check, Action<string?> mute) => ExtensionBanner.Bind(check, mute);

    /// <summary>Sets the file name (and the category / folder that follow its extension) unless the user already edited those fields.</summary>
    private void SetName(string name)
    {
        _programmatic = true;
        try
        {
            if (!_nameEdited) { NameInput.Text = name; Title = name; }
            if (!_categoryEdited || !_pathEdited)
            {
                var category = DetectCategory(_nameEdited ? NameInput.Text : name);
                if (!_categoryEdited) { Category = category; SelectCategory(category); }
                if (!_pathEdited)
                {
                    // Untouched path: the folder the engine itself will use (category of the final extension, honouring the category-folder setting).
                    var path = !_categoryEdited && _autoFolder is not null ? _autoFolder(_nameEdited ? NameInput.Text.Trim() : name) : GetCategoryPath(Category);
                    PathInput.Text = path; SavePath = path;
                }
            }
        }
        finally { _programmatic = false; }
    }

    private void OnCategoryChanged()
    {
        if (_programmatic) return;
        _categoryEdited = true;
        if (_pathEdited) return;
        _programmatic = true;
        try { var path = GetCategoryPath(Category = (CategoryDropdown.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? Category); PathInput.Text = path; SavePath = path; }
        finally { _programmatic = false; }
    }

    /// <summary>True while neither the folder nor the category was chosen by the user: the engine's own category folder applies. Otherwise the
    /// folder shown in the box is the job's exact save folder.</summary>
    public bool PathIsAuto => !_pathEdited && !_categoryEdited;

    /// <summary>The connection to the browser that asked for this download is gone (the browser already restarted it or the extension stopped waiting).
    /// Adding it now would duplicate the download, so the dialog is locked and says so; the user can only close it.</summary>
    public bool Expired { get; private set; }
    /// <summary>The address is on this computer or the local network: say so next to it (the page chose the address, not the user). Never blocks.</summary>
    public void ShowPrivateNetworkWarning() => PrivateNetworkNotice.Visibility = Visibility.Visible;
    public bool PrivateNetworkWarningVisible => PrivateNetworkNotice.Visibility == Visibility.Visible;
    public void MarkExpired(string message)
    {
        if (Expired || Accepted) return;
        Expired = true;
        StartButton.IsEnabled = QueueButton.IsEnabled = ScheduleButton.IsEnabled = false;
        ValidationError.Text = message;
    }

    private async Task RunProbeAsync()
    {
        NameProbeResult? seen = null;
        try { seen = await _probe!(_probeCancel.Token); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        if (_probeCancel.IsCancellationRequested) return;
        _seen = seen;
        if (seen is null) { DetectedInfo.Text = Loc.T("capture.detect.failed"); return; }
        SetName(FileNameResolver.Resolve(_uri, _browserName, _browserMime, seen).Name);
        var parts = new List<string>();
        if (seen.Length is > 0) parts.Add(Loc.T("capture.detect.size", DownloadRow.FormatBytes(seen.Length.Value)));
        var type = seen.ContentType?.Split(';')[0].Trim();
        if (!string.IsNullOrEmpty(type)) parts.Add(Loc.T("capture.detect.type", type));
        DetectedInfo.Text = parts.Count > 0 ? string.Join("  \u00b7  ", parts) : Loc.T("capture.detect.server");
    }

    private void Finish(bool accepted)
    {
        Accepted = accepted;
        try { DialogResult = accepted; } // only valid for ShowDialog
        catch (InvalidOperationException) { Close(); }
    }

    private static string DetectCategory(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        
        // Programs
        if (new[] { ".exe", ".msi", ".dmg", ".pkg", ".deb", ".rpm", ".appimage", ".app" }.Contains(ext))
            return "Programs";
        
        // Compressed
        if (new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".tar.gz", ".tar.bz2" }.Contains(ext))
            return "Compressed";
        
        // Documents
        if (new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".odt", ".ods", ".odp" }.Contains(ext))
            return "Documents";
        
        // Music
        if (new[] { ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".wma", ".opus" }.Contains(ext))
            return "Music";
        
        // Video
        if (new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg" }.Contains(ext))
            return "Video";
        
        return "General";
    }

    private void SelectCategory(string category)
    {
        foreach (System.Windows.Controls.ComboBoxItem item in CategoryDropdown.Items)
        {
            if (item.Tag as string == category)
            {
                CategoryDropdown.SelectedItem = item;
                break;
            }
        }
    }

    private string GetCategoryPath(string category)
    {
        if (category == "General")
            return _defaultDownloadDirectory;
        
        return Path.Combine(_defaultDownloadDirectory, category);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.T("capture.picker.title"),
            InitialDirectory = PathInput.Text
        };
        
        if (dialog.ShowDialog() == true)
        {
            PathInput.Text = dialog.FolderName;
            _pathEdited = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        UserAction = DialogAction.Cancel;
        Finish(false);
    }

    private void Queue_Click(object sender, RoutedEventArgs e)
    {
        if (ValidateAndPrepare())
        {
            UserAction = DialogAction.Queue;
            Finish(true);
        }
    }

    private void Schedule_Click(object sender, RoutedEventArgs e)
    {
        SchedulePanel.Visibility = Visibility.Visible;
        if (!ValidateAndPrepare())
            return;
        
        try
        {
            ScheduledAt = DownloadDialogValidation.ParseSchedule(ScheduleInput.Text);
            UserAction = DialogAction.Schedule;
            Finish(true);
        }
        catch (FormatException ex)
        {
            ValidationError.Text = ErrorMessages.Describe(ex);
            UpdateLayout();
            ValidationError.BringIntoView();
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ValidateAndPrepare())
        {
            UserAction = DialogAction.StartNow;
            Finish(true);
        }
    }

    private bool ValidateAndPrepare()
    {
        if (Expired) return false; // the expiry message stays; nothing may be added any more
        try
        {
            ValidationError.Text = "";
            
            string url = UrlDisplay.Text.Trim();
            NativeBridge.ValidateUrl(url);
            
            string name = NameInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new UserMessageException(Loc.T("capture.error.name_empty"));
            
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' '))
                throw new UserMessageException(Loc.T("error.file_name_invalid"));
            
            string savePath = PathInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(savePath))
                throw new UserMessageException(Loc.T("capture.error.path_empty"));
            
            if (!Path.IsPathFullyQualified(savePath))
                throw new UserMessageException(Loc.T("capture.error.path_full"));
            
            // Create the folder if it is missing and prove it is writable now, so a bad folder keeps this dialog open instead of failing later.
            try { savePath = DownloadManager.EnsureWritableFolder(savePath); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                throw new UserMessageException(ex is ArgumentException ? ErrorMessages.Describe(ex.Message) : Loc.T("capture.error.folder", ErrorMessages.OneLine(ex.Message)));
            }
            
            SpeedLimit = DownloadDialogValidation.ParseSpeed(SpeedInput.Text);
            
            var metadata = (dynamic?)Tag;
            // The user's own name, or the browser's, is kept as is. Only a name derived from the address alone that the server has not confirmed yet
            // stays open for the engine to refine from the server's answer when the transfer starts.
            bool open = !_nameEdited && _browserName is null && _seen is null;
            Request = new DownloadRequest(url, name, metadata?.Referer, metadata?.Cookie, open, PathIsAuto ? null : savePath);
            SavePath = savePath;
            
            var selectedCategory = (CategoryDropdown.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
            if (selectedCategory != null)
                Category = selectedCategory;
            
            return true;
        }
        catch (FormatException ex)
        {
            ValidationError.Text = ErrorMessages.Describe(ex);
            UpdateLayout();
            ValidationError.BringIntoView();
            return false;
        }
    }
}
