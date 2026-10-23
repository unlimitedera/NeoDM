using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NDM.App.Localization;
using NDM.Core;
namespace NDM.App;
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string _stateDirectory;
    public SettingsWindow(AppSettings settings, string? stateDirectory = null)
    {
        _settings = settings; _stateDirectory = stateDirectory ?? App.StateDirectory;
        InitializeComponent(); WindowPlacements.Track(this, "Options");
        Destination.Text = settings.DownloadDirectory;
        _savedTheme = ThemeCatalog.Normalize(settings.Theme); _savedAccent = ThemeCatalog.NormalizeAccent(settings.AccentOverride);
        _theme = _savedTheme; _accent = _savedAccent;
        _cards = ThemeCatalog.All.Select(t => new ThemeCardModel(t, _accent)).ToList();
        ThemeGallery.ItemsSource = _cards; ThemeGallery.SelectedItem = _cards.First(c => c.Id == _theme);
        AccentSwatches.ItemsSource = AccentPreset.All;
        AccentHex.Text = _accent ?? ""; ShowAccentPreview();
        Segments.Text = settings.MaxSegments.ToString(CultureInfo.InvariantCulture);
        Concurrent.Text = settings.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture);
        Retries.Text = settings.RetryCount.ToString(CultureInfo.InvariantCulture);
        Timeout.Text = settings.RequestTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        DynamicSegmentation.IsChecked = settings.EnableDynamicSegmentation;
        CompletionDialog.IsChecked = settings.ShowCompletionDialog;
        CapturedProgress.IsChecked = settings.ShowProgressForCapturedDownloads;
        ProgressOnCompletion.IsChecked = settings.ShowProgressOnCompletion;
        CompletionSound.IsChecked = settings.PlayCompletionSound;
        TrayActiveCount.IsChecked = settings.TrayShowActiveCount;
        TrayNotifyComplete.IsChecked = settings.TrayNotifyOnComplete;
        MarkFiles.IsChecked = settings.MarkDownloadedFiles;
        EnableCategoryFolders.IsChecked = settings.EnableCategoryFolders;
        ProgramsFolder.Text = settings.ProgramsFolder;
        CompressedFolder.Text = settings.CompressedFolder;
        MusicFolder.Text = settings.MusicFolder;
        VideoFolder.Text = settings.VideoFolder;
        DocumentsFolder.Text = settings.DocumentsFolder;
        CategoryGrid.IsEnabled = settings.EnableCategoryFolders;
        FinishAction.SelectedIndex = -1; // items come from FillLists below; the saved choice is selected there
        _initialFinish = settings.SessionCompletionAction;
        FinishForce.IsChecked = settings.CompletionForceClose;
        BridgeWatchdogBox.IsChecked = settings.BridgeWatchdogEnabled;
        CheckForUpdates.IsChecked = settings.CheckForUpdates; _skipped = settings.SkippedUpdateVersion;
        ShowSkipped();
        FinishIgnoreScheduled.IsChecked = settings.CompletionIgnoreScheduled;
        RunProgramEnabled.IsChecked = settings.RunProgramEnabled;
        RunProgramPath.Text = settings.RunProgramPath; RunProgramArguments.Text = settings.RunProgramArguments;
        RunProgramGrid.IsEnabled = settings.RunProgramEnabled;
        TorrentFolder.Text = settings.TorrentDownloadDirectory;
        TorrentPort.Text = settings.TorrentListenPort.ToString(CultureInfo.InvariantCulture);
        TorrentConnections.Text = settings.TorrentMaxConnections.ToString(CultureInfo.InvariantCulture);
        TorrentDown.Text = settings.TorrentMaxDownloadKiB.ToString(CultureInfo.InvariantCulture);
        TorrentUp.Text = settings.TorrentMaxUploadKiB.ToString(CultureInfo.InvariantCulture);
        TorrentDht.IsChecked = settings.TorrentEnableDht; TorrentPex.IsChecked = settings.TorrentEnablePex;
        TorrentLsd.IsChecked = settings.TorrentEnableLsd; TorrentUpnp.IsChecked = settings.TorrentEnablePortForwarding;
        _initialEncryption = settings.TorrentEncryption; _initialProxy = settings.ProxyType;
        ProxyHost.Text = settings.ProxyHost; ProxyPort.Text = settings.ProxyPort.ToString(CultureInfo.InvariantCulture);
        ProxyUser.Text = settings.ProxyUsername;
        ShowProxyPasswordNote();
        ProxyResolve.IsChecked = settings.ProxyResolveHostsThroughProxy; ProxyForDownloads.IsChecked = settings.ProxyForDownloads;
        ProxyForPeers.IsChecked = settings.ProxyForPeers; ProxyForTrackers.IsChecked = settings.ProxyForTrackers;
        ProxyType_Changed(this, null!);
        SeedKeep.IsChecked = !settings.TorrentStopSeedingAtRatio; SeedStop.IsChecked = settings.TorrentStopSeedingAtRatio;
        TorrentSeedRatio.Text = settings.TorrentSeedRatioLimit.ToString("0.##", CultureInfo.InvariantCulture);
        _savedLanguage = LanguageCatalog.NormalizeSetting(settings.Language); _languageSetting = _savedLanguage; _languageAtOpen = Loc.Language;
        FillLists();
        Loc.LanguageChanged += OnLanguageChanged;
    }
    // ---- Language: choosing one switches every open window at once; only Save keeps it, anything else puts the previous language back ----
    private readonly string _savedLanguage; private string _languageSetting; private readonly string _languageAtOpen;
    private CompletionActionKind _initialFinish; private TorrentEncryptionMode _initialEncryption; private ProxyKind _initialProxy;
    private bool _fillingLists;
    /// <summary>One row of the language list: a bold native name and a muted English name.</summary>
    public sealed record LanguageChoice(string Tag, string Primary, string Secondary);
    private void FillLists()
    {
        _fillingLists = true;
        try
        {
            var finish = SelectedFinishOr(_initialFinish);
            FinishAction.ItemsSource = CompletionActionInfo.All.Select(k => new ComboBoxItem { Content = CompletionActionInfo.Label(k), Tag = k }).ToList();
            FinishAction.SelectedIndex = CompletionActionInfo.All.ToList().IndexOf(finish);
            var enc = (TorrentEncryptionBox.SelectedItem as ComboBoxItem)?.Tag is TorrentEncryptionMode chosenEnc ? chosenEnc : _initialEncryption;
            TorrentEncryptionBox.ItemsSource = new[]
            {
                new ComboBoxItem { Content = Loc.T("options.encryption.prefer"), Tag = TorrentEncryptionMode.Enabled },
                new ComboBoxItem { Content = Loc.T("options.encryption.require"), Tag = TorrentEncryptionMode.Required },
                new ComboBoxItem { Content = Loc.T("options.encryption.disable"), Tag = TorrentEncryptionMode.Disabled }
            };
            TorrentEncryptionBox.SelectedIndex = enc switch { TorrentEncryptionMode.Required => 1, TorrentEncryptionMode.Disabled => 2, _ => 0 };
            var proxy = (ProxyTypeBox.SelectedItem as ComboBoxItem)?.Tag is ProxyKind chosenKind ? chosenKind : _initialProxy;
            ProxyTypeBox.ItemsSource = new[]
            {
                new ComboBoxItem { Content = Loc.T("options.proxy.none"), Tag = ProxyKind.None },
                new ComboBoxItem { Content = "HTTP", Tag = ProxyKind.Http },
                new ComboBoxItem { Content = Loc.T("options.proxy.https"), Tag = ProxyKind.Https },
                new ComboBoxItem { Content = "SOCKS5", Tag = ProxyKind.Socks5 }
            };
            ProxyTypeBox.SelectedIndex = proxy switch { ProxyKind.Http => 1, ProxyKind.Https => 2, ProxyKind.Socks5 => 3, _ => 0 };
            var system = LanguageCatalog.DetectOs();
            var choices = new List<LanguageChoice> { new(LanguageCatalog.System, Loc.T("options.language.system", system.NativeName), "") };
            choices.AddRange(LanguageCatalog.Installed().Select(l => new LanguageChoice(l.Tag, l.NativeName, l.NativeName == l.EnglishName ? "" : " - " + l.EnglishName)));
            LanguageBox.ItemsSource = choices;
            LanguageBox.SelectedValue = _languageSetting;
        }
        finally { _fillingLists = false; }
        FinishAction_Changed(this, null!);
        ShowLanguageNote();
    }
    private CompletionActionKind SelectedFinishOr(CompletionActionKind fallback) => (FinishAction.SelectedItem as ComboBoxItem)?.Tag is CompletionActionKind k ? k : fallback;
    private void ShowLanguageNote() => LanguageNote.Visibility = LanguageCatalog.Find(Loc.Language)?.AiTranslated == true ? Visibility.Visible : Visibility.Collapsed;
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingLists || LanguageBox.SelectedValue is not string setting || setting == _languageSetting) return;
        _languageSetting = setting;
        Loc.SetLanguage(LanguageCatalog.Resolve(setting).Tag);
    }
    private void LanguageHelp_Click(object sender, RoutedEventArgs e) => UrlOpener.Open(TranslatingGuideUrl);
    public const string TranslatingGuideUrl = "https://github.com/unlimitedera/NeoDM/blob/main/docs/TRANSLATING.md";
    /// <summary>The language setting currently chosen (before Save): "system" or a tag.</summary>
    public string PreviewLanguage => _languageSetting;
    private void OnLanguageChanged()
    {
        FillLists();
        foreach (var card in _cards) card.RaiseLanguage();
        AccentSwatches.ItemsSource = AccentPreset.All.ToList();
        ShowSkipped(); ShowProxyPasswordNote();
        if (ProxyTestResult.Text.Length > 0 && !ProxyTestButton.IsEnabled) { /* a test is running: its own message replaces this */ }
    }
    private void ShowProxyPasswordNote() => ProxyPasswordNote.Text = _settings.HasProxyPassword ? Loc.T("options.proxy.password_saved") : Loc.T("options.proxy.password_info");
    // ---- Appearance: every change previews app-wide at once; only Save keeps it, anything else puts the saved theme back ----
    private readonly string _savedTheme; private readonly string? _savedAccent;
    private string _theme; private string? _accent; private bool _committed;
    private readonly List<ThemeCardModel> _cards;
    /// <summary>Theme currently previewed (id as stored in settings).</summary>
    public string PreviewTheme => _theme;
    /// <summary>Accent override currently previewed (null = the theme's own).</summary>
    public string? PreviewAccent => _accent;
    private void PreviewApply() { if (Application.Current is { } app) ThemeManager.Apply(app, _theme, _accent); }
    private void ThemeGallery_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeGallery.SelectedItem is not ThemeCardModel card || card.Id == _theme) return;
        _theme = card.Id; PreviewApply();
    }
    private void Swatch_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string hex }) AccentHex.Text = hex; }
    private void ResetAccent_Click(object sender, RoutedEventArgs e) => AccentHex.Clear();
    private void AccentHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (AccentError is null || _cards is null) return;
        string text = AccentHex.Text.Trim();
        if (text.Length == 0) { AccentError.Text = ""; SetAccent(null); }
        else if (ThemeCatalog.TryParseAccent(text, out var hex)) { AccentError.Text = ""; SetAccent(hex); }
        else AccentError.Text = Loc.T("options.error.accent_short");
        ShowAccentPreview();
    }
    private void SetAccent(string? hex)
    {
        if (hex == _accent) return;
        _accent = hex; foreach (var card in _cards) card.SetAccent(hex);
        PreviewApply();
    }
    private void ShowAccentPreview()
    {
        AccentPreview.Background = _accent is null ? (Brush)FindResource("AccentFillBrush") : new SolidColorBrush((Color)ColorConverter.ConvertFromString(_accent));
    }
    private void OpenAppearance_Click(object sender, RoutedEventArgs e) => OptionsTabs.SelectedItem = AppearanceTab;
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Loc.LanguageChanged -= OnLanguageChanged;
        if (!_committed)
        {
            _theme = _savedTheme; _accent = _savedAccent; PreviewApply();
            if (Loc.Language != _languageAtOpen) Loc.SetLanguage(_languageAtOpen);
        }
    }
    private string _skipped = "";
    private void ShowSkipped() { SkippedPanel.Visibility = _skipped.Length > 0 ? Visibility.Visible : Visibility.Collapsed; SkippedText.Text = Loc.T("options.skipped_version", _skipped); }
    private void ForgetSkip_Click(object sender, RoutedEventArgs e) { _skipped = ""; ShowSkipped(); }
    private ProxyKind SelectedProxyKind => (ProxyTypeBox.SelectedItem as ComboBoxItem)?.Tag is ProxyKind k ? k : ProxyKind.None;
    private void ProxyType_Changed(object sender, SelectionChangedEventArgs? e)
    {
        if (ProxyFields is null) return;
        bool on = SelectedProxyKind != ProxyKind.None;
        ProxyFields.IsEnabled = on; ProxyResolve.IsEnabled = on && SelectedProxyKind == ProxyKind.Socks5; ProxyTestButton.IsEnabled = on;
        ProxyForDownloads.IsEnabled = ProxyForPeers.IsEnabled = ProxyForTrackers.IsEnabled = on;
    }
    /// <summary>Proxy as currently typed (password from the box, else the saved one). Null when no proxy is selected. Throws FormatException on bad input.</summary>
    private ProxyOptions? ReadProxy()
    {
        var kind = SelectedProxyKind;
        if (kind == ProxyKind.None) return null;
        string host = ProxyHost.Text.Trim();
        if (host.Length == 0) throw new UserMessageException(Loc.T("options.error.proxy_host_empty"));
        if (host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@')) throw new UserMessageException(Loc.T("options.error.proxy_host_invalid"));
        int port = ReadNumber(ProxyPort.Text, 1, 65535, "options.field.proxy_port");
        string user = ProxyUser.Text.Trim();
        string? password = user.Length == 0 ? null : ProxyPassword.Password.Length > 0 ? ProxyPassword.Password : ProxySecret.TryUnprotect(_settings.ProxyPasswordProtected);
        return new ProxyOptions { Kind = kind, Host = host, Port = port, Username = user.Length > 0 ? user : null, Password = password, ResolveHostsThroughProxy = ProxyResolve.IsChecked == true };
    }
    /// <summary>Target for the connection test; tests point it at a local server.</summary>
    public string ProxyTestTarget { get; set; } = ProxyTester.DefaultTarget;
    public async Task<ProxyTestResult> TestProxyAsync()
    {
        ProxyOptions? proxy;
        try { proxy = ReadProxy(); }
        catch (FormatException ex) { ProxyTestResult.Text = ErrorMessages.Describe(ex); ProxyTestResult.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush"); return new(false, ProxyTestResult.Text, TimeSpan.Zero); }
        if (proxy is null) { ProxyTestResult.Text = Loc.T("options.proxy.choose_type"); return new(false, ProxyTestResult.Text, TimeSpan.Zero); }
        ProxyTestButton.IsEnabled = false; ProxyTestResult.Text = Loc.T("options.proxy.testing"); ProxyTestResult.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        try
        {
            var result = await ProxyTester.TestAsync(proxy, ProxyTestTarget);
            ProxyTestResult.Text = ProxyTestMessage(result);
            ProxyTestResult.Foreground = (System.Windows.Media.Brush)FindResource(result.Ok ? "SuccessBrush" : "ErrorBrush");
            return result;
        }
        finally { ProxyTestButton.IsEnabled = SelectedProxyKind != ProxyKind.None; }
    }
    /// <summary>The proxy tester (Core) answers in English; stable categories are translated, anything else keeps the English text.</summary>
    private static string ProxyTestMessage(ProxyTestResult result)
    {
        if (Loc.Language == LanguageCatalog.English) return result.Message;
        var ok = System.Text.RegularExpressions.Regex.Match(result.Message, @"^Connected through (?<h>\S+): the test request returned HTTP (?<c>\d+) in (?<ms>\d+) ms");
        if (result.Ok && ok.Success) return Loc.T("options.proxy.test_ok", ok.Groups["h"].Value, int.Parse(ok.Groups["c"].Value), long.Parse(ok.Groups["ms"].Value));
        var refused = System.Text.RegularExpressions.Regex.Match(result.Message, @"^Could not connect to the proxy (?<h>\S+) \(connection refused\)");
        if (refused.Success) return Loc.T("options.proxy.refused", refused.Groups["h"].Value);
        var late = System.Text.RegularExpressions.Regex.Match(result.Message, @"^The proxy (?<h>\S+) did not answer in time");
        if (late.Success) return Loc.T("options.proxy.timeout", late.Groups["h"].Value);
        if (result.Message.StartsWith("No proxy is configured", StringComparison.Ordinal)) return Loc.T("error.proxy_none");
        return ErrorMessages.OneLine(result.Message);
    }
    private async void ProxyTest_Click(object sender, RoutedEventArgs e) => await TestProxyAsync();
    private void BrowseTorrent_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = Loc.T("torrentadd.picker.title"), Multiselect = false };
        if (Directory.Exists(TorrentFolder.Text)) picker.InitialDirectory = TorrentFolder.Text;
        if (picker.ShowDialog(this) == true) TorrentFolder.Text = picker.FolderName;
    }
    private CompletionActionKind SelectedFinish => (FinishAction.SelectedItem as ComboBoxItem)?.Tag is CompletionActionKind k ? k : CompletionActionKind.None;
    private void FinishAction_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FinishDescription is null || FinishForce is null) return;
        FinishDescription.Text = CompletionActionInfo.Description(SelectedFinish);
        FinishForce.IsEnabled = CompletionActionInfo.UsesForce(SelectedFinish);
    }
    private void RunProgram_Changed(object sender, RoutedEventArgs e) { if (RunProgramGrid is not null) RunProgramGrid.IsEnabled = RunProgramEnabled.IsChecked == true; }
    private void BrowseProgram_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = Loc.T("options.picker.program"), Filter = Loc.T("options.picker.program_filter") };
        if (picker.ShowDialog(this) == true) RunProgramPath.Text = picker.FileName;
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = Loc.T("options.picker.destination"), Multiselect = false };
        if (Directory.Exists(Destination.Text)) picker.InitialDirectory = Destination.Text;
        if (picker.ShowDialog(this) == true) Destination.Text = picker.FolderName;
    }
    private void EnableCategoryFolders_Changed(object sender, RoutedEventArgs e)
    {
        CategoryGrid.IsEnabled = EnableCategoryFolders.IsChecked == true;
    }
    private static int ReadNumber(string text, int min, int max, string name)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            throw new UserMessageException(Loc.T("options.error.number", Loc.T(name), min, max));
        return value;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        try
        {
            var updated = _settings.Copy();
            updated.MaxSegments = ReadNumber(Segments.Text, 1, 32, "options.field.segments");
            updated.MaxConcurrentDownloads = ReadNumber(Concurrent.Text, 1, 16, "options.field.concurrent");
            updated.RetryCount = ReadNumber(Retries.Text, 0, 8, "options.field.retries");
            updated.RequestTimeoutSeconds = ReadNumber(Timeout.Text, 1, 300, "options.field.timeout");
            string directory = Destination.Text.Trim();
            if (!Path.IsPathFullyQualified(directory)) throw new UserMessageException(Loc.T("error.folder_incomplete"));
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".ndm-write-test-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) stream.WriteByte(0);
            updated.DownloadDirectory = directory;
            updated.TorrentListenPort = ReadNumber(TorrentPort.Text, 1, 65535, "options.field.torrent_port");
            updated.TorrentMaxConnections = ReadNumber(TorrentConnections.Text, 1, 10000, "options.field.torrent_connections");
            updated.TorrentMaxDownloadKiB = ReadNumber(TorrentDown.Text, 0, 2_000_000, "options.field.torrent_down");
            updated.TorrentMaxUploadKiB = ReadNumber(TorrentUp.Text, 0, 2_000_000, "options.field.torrent_up");
            if (!double.TryParse(TorrentSeedRatio.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) || !double.IsFinite(ratio) || ratio < 0.1 || ratio > 1000)
                throw new UserMessageException(Loc.T("options.error.seed_ratio"));
            updated.TorrentSeedRatioLimit = ratio;
            updated.TorrentStopSeedingAtRatio = SeedStop.IsChecked == true;
            string torrentFolder = TorrentFolder.Text.Trim();
            if (torrentFolder.Length > 0)
            {
                if (!Path.IsPathFullyQualified(torrentFolder)) throw new UserMessageException(Loc.T("options.error.torrent_folder"));
                torrentFolder = Path.GetFullPath(torrentFolder);
                Directory.CreateDirectory(torrentFolder);
                string torrentProbe = Path.Combine(torrentFolder, ".ndm-write-test-" + Guid.NewGuid().ToString("N"));
                using (var probeStream = new FileStream(torrentProbe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) probeStream.WriteByte(0);
            }
            updated.TorrentDownloadDirectory = torrentFolder;
            updated.TorrentEnableDht = TorrentDht.IsChecked == true; updated.TorrentEnablePex = TorrentPex.IsChecked == true;
            updated.TorrentEnableLsd = TorrentLsd.IsChecked == true; updated.TorrentEnablePortForwarding = TorrentUpnp.IsChecked == true;
            updated.TorrentEncryption = (TorrentEncryptionBox.SelectedItem as ComboBoxItem)?.Tag is TorrentEncryptionMode enc ? enc : TorrentEncryptionMode.Enabled;
            var proxy = ReadProxy();
            updated.ProxyType = proxy?.Kind ?? ProxyKind.None;
            if (proxy is not null)
            {
                updated.ProxyHost = proxy.Host; updated.ProxyPort = proxy.Port; updated.ProxyUsername = proxy.Username ?? "";
                if (proxy.Username is null) updated.SetProxyPassword(null);
                else if (ProxyPassword.Password.Length > 0) updated.SetProxyPassword(ProxyPassword.Password);
            }
            else { updated.ProxyHost = ProxyHost.Text.Trim(); updated.ProxyUsername = ProxyUser.Text.Trim(); }
            updated.ProxyResolveHostsThroughProxy = ProxyResolve.IsChecked == true; updated.ProxyForDownloads = ProxyForDownloads.IsChecked == true;
            updated.ProxyForPeers = ProxyForPeers.IsChecked == true; updated.ProxyForTrackers = ProxyForTrackers.IsChecked == true;
            string accentText = AccentHex.Text.Trim();
            if (accentText.Length > 0 && !ThemeCatalog.TryParseAccent(accentText, out _)) { OptionsTabs.SelectedItem = AppearanceTab; throw new UserMessageException(Loc.T("options.error.accent")); }
            updated.Theme = _theme; updated.AccentOverride = _accent; updated.Language = _languageSetting;
            updated.EnableDynamicSegmentation = DynamicSegmentation.IsChecked == true;
            updated.ShowCompletionDialog = CompletionDialog.IsChecked == true;
            updated.ShowProgressForCapturedDownloads = CapturedProgress.IsChecked == true;
            updated.ShowProgressOnCompletion = ProgressOnCompletion.IsChecked == true;
            updated.PlayCompletionSound = CompletionSound.IsChecked == true;
            updated.TrayShowActiveCount = TrayActiveCount.IsChecked == true;
            updated.MarkDownloadedFiles = MarkFiles.IsChecked == true;
            updated.TrayNotifyOnComplete = TrayNotifyComplete.IsChecked == true;
            updated.EnableCategoryFolders = EnableCategoryFolders.IsChecked == true;
            updated.ProgramsFolder = ProgramsFolder.Text.Trim();
            updated.CompressedFolder = CompressedFolder.Text.Trim();
            updated.MusicFolder = MusicFolder.Text.Trim();
            updated.VideoFolder = VideoFolder.Text.Trim();
            updated.DocumentsFolder = DocumentsFolder.Text.Trim();
            bool runProgram = RunProgramEnabled.IsChecked == true;
            if (runProgram && RunProgramValidator.Validate(RunProgramPath.Text, RunProgramArguments.Text) is { } problem) throw new UserMessageException(problem);
            updated.RunProgramEnabled = runProgram;
            updated.RunProgramPath = RunProgramPath.Text.Trim(); updated.RunProgramArguments = RunProgramArguments.Text.Trim();
            updated.CompletionForceClose = FinishForce.IsChecked == true;
            updated.CompletionIgnoreScheduled = FinishIgnoreScheduled.IsChecked == true;
            // Options are permanent: the 'When all downloads finish' action chosen here is saved and used from the next start on.
            // (Temporary choices are made in the menu or in a download's own completion options, which keep their 'Remember' choice.)
            updated.CompletionRemember = true;
            updated.SessionCompletionAction = SelectedFinish;
            updated.CompletionAction = SelectedFinish;
            updated.CheckForUpdates = CheckForUpdates.IsChecked == true;
            updated.BridgeWatchdogEnabled = BridgeWatchdogBox.IsChecked == true;
            updated.SkippedUpdateVersion = _skipped;
            updated.Save(_stateDirectory);
            _settings.CopyFrom(updated);
        }
        catch (FormatException ex) { ErrorText.Text = ErrorMessages.Describe(ex); return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { ErrorText.Text = Loc.T("options.error.not_saved"); return; }
        _committed = true;
        if (Application.Current is { } app) ThemeManager.Apply(app, _settings.Theme, _settings.AccentOverride);
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void StoreInstall_Click(object sender, RoutedEventArgs e) => UrlOpener.Open(ExtensionGuide.StoreUrl);
    private void BrowserSetup_Click(object sender, RoutedEventArgs e)
    {
        // Explicit click only: no installers, registry writes, or fabricated status.
        try { Process.Start(new ProcessStartInfo("https://support.google.com/chrome/answer/187443") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { ErrorText.Text = Loc.T("options.error.chrome_help"); }
    }
}

/// <summary>One gallery card: the theme's own palette (read from its dictionary, never applied) for the live swatch.</summary>
public sealed class ThemeCardModel : System.ComponentModel.INotifyPropertyChanged
{
    private readonly ResourceDictionary _file; private readonly ThemeInfo _info;
    public ThemeCardModel(ThemeInfo info, string? accent)
    {
        _info = info; _file = ThemeManager.LoadFile(ThemeCatalog.Resolve(info.Id)); SetAccent(accent);
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string Id => _info.Id;
    public string Name => Loc.T("theme." + _info.Id.ToLowerInvariant() + ".name"); public string Description => Loc.T("theme." + _info.Id.ToLowerInvariant() + ".desc");
    public void RaiseLanguage() { PropertyChanged?.Invoke(this, new(nameof(Name))); PropertyChanged?.Invoke(this, new(nameof(Description))); }
    public Brush Canvas => B("CanvasBrush"); public Brush Sidebar => B("SidebarBrush"); public Brush Surface => B("SurfaceBrush"); public Brush Edge => B("BorderBrush");
    public Brush Ink => B("InkBrush"); public Brush Muted => B("MutedBrush");
    private Brush B(string key) => (Brush)_file[key];
    private ResourceDictionary? _accented;
    public Brush AccentSoft => (Brush)(_accented?["AccentSoftBrush"] ?? _file["AccentSoftBrush"]);
    public Brush AccentFill => (Brush)(_accented?["AccentFillBrush"] ?? _file["AccentFillBrush"]);
    public Brush OnAccent => (Brush)(_accented?["OnAccentBrush"] ?? _file["OnAccentBrush"]);
    public void SetAccent(string? hex)
    {
        _accented = null;
        if (ThemeCatalog.TryParseAccent(hex, out var h))
        {
            _accented = new ResourceDictionary();
            foreach (var (key, brush) in ThemeManager.AccentBrushes(_file, h)) _accented[key] = brush;
        }
        PropertyChanged?.Invoke(this, new(nameof(AccentSoft))); PropertyChanged?.Invoke(this, new(nameof(AccentFill))); PropertyChanged?.Invoke(this, new(nameof(OnAccent)));
    }
    public override string ToString() => Name;
}

public sealed record AccentPreset(string Name, string Hex)
{
    public string Label => Loc.T("accent." + Name.ToLowerInvariant());
    public Brush Brush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Hex));
    public static readonly IReadOnlyList<AccentPreset> All = new AccentPreset[]
    {
        new("Violet", "#8B7CFF"), new("Blue", "#4C9AFF"), new("Cyan", "#3CC8F0"), new("Teal", "#2DD4BF"), new("Green", "#4ADE80"),
        new("Amber", "#F5B83D"), new("Orange", "#FB8C3C"), new("Pink", "#F472B6"), new("Red", "#F2555F"),
    };
}
