using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>The title shown on one tab of the strip.</summary>
public sealed class TabHeader : INotifyPropertyChanged
{
    public int Id { get; init; }
    private string _title = "";
    public string Title { get => _title; set { if (_title == value) return; _title = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>The NeoDM browser: an Edge WebView2 window with tabs and its own private profile folder under the NeoDM state folder (never the user's browser profile; sign-ins stay in it between runs until
/// Options > Clear NeoDM browser data). A download started in any tab is cancelled here and handed to the normal NeoDM capture dialog with the address, name, size, referrer, user agent and the cookies
/// the browser holds for that address only; the window stays open. A video stream or media file a page asks for gets a small bar with "Download this video". When the window was opened for a failed
/// download (1.12.0) the first download goes back to that same job and the window closes. Cookies stay on this computer and are never logged.</summary>
public partial class NeoBrowserWindow : Window
{
    /// <summary>The private profile folder; removed by Options > Clear NeoDM browser data.</summary>
    public static string DefaultDataFolder => Path.Combine(App.StateDirectory, "browser-data");

    private readonly string? _startUrl;
    private readonly string _dataFolder;
    private readonly Func<BrowserGrab, Task>? _captured;
    private readonly Func<string?>? _runtimeVersion;
    private readonly Dictionary<int, WebView2> _views = new();
    private readonly ObservableCollection<TabHeader> _headers = new();
    private readonly MediaPickup _pickup = new();
    private CoreWebView2Environment? _environment;
    private bool _handled, _syncing;

    public BrowserTabSet Tabs { get; } = new();
    /// <summary>Where a captured download or video goes: the normal capture flow. Set by the main window; null = nothing can be captured.</summary>
    public Action<NativeBridge.BrowserDownloadRequest>? CaptureSink { get; set; }
    /// <summary>True once a download was handed over in the failed-download mode (the window closes right after).</summary>
    public bool Captured => _handled;
    public LoginBrowserRuntime Runtime { get; private set; } = LoginBrowserRuntime.Available;
    public string StatusMessage => StatusText.Text;
    public string AddressText => AddressBox.Text;
    public bool MediaBarVisible => MediaBar.Visibility == Visibility.Visible;
    public IReadOnlyList<string> TabTitles => _headers.Select(h => h.Title).ToList();
    /// <summary>The page the active tab is showing.</summary>
    public string? CurrentAddress => ActiveCore()?.Source;

    public NeoBrowserWindow(string? startUrl, Func<BrowserGrab, Task>? captured = null, string? dataFolder = null, Func<string?>? runtimeVersion = null, Action<NativeBridge.BrowserDownloadRequest>? captureSink = null)
    {
        _startUrl = startUrl; _captured = captured; _dataFolder = dataFolder ?? DefaultDataFolder; _runtimeVersion = runtimeVersion; CaptureSink = captureSink;
        InitializeComponent();
        TabStrip.ItemsSource = _headers;
        StatusText.Text = Loc.T("neobrowser.status.loading");
        Tabs.Changed += SyncStrip;
        SizeChanged += (_, _) => LayoutToolbar();
        PreviewKeyDown += Window_PreviewKeyDown;
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => { foreach (var view in _views.Values) { try { view.Dispose(); } catch (Exception) { /* the component may already be gone */ } } _views.Clear(); };
    }

    /// <summary>Wide window: buttons and address share a row. Narrow window: the address box gets its own row under the buttons so nothing is pushed out.</summary>
    private void LayoutToolbar()
    {
        bool narrow = ActualWidth < 640;
        Grid.SetRow(AddressBox, narrow ? 1 : 0); Grid.SetColumn(AddressBox, narrow ? 0 : 1); Grid.SetColumnSpan(AddressBox, narrow ? 2 : 1);
        AddressBox.Margin = narrow ? new Thickness(0, 6, 0, 0) : new Thickness(0);
    }

    // ---------------------------------------------------------------- start
    private async Task StartAsync()
    {
        Runtime = LoginBrowserLogic.RuntimeFrom(_runtimeVersion ?? (() => { try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch (Exception) { return null; } }));
        if (Runtime == LoginBrowserRuntime.Missing) { ShowMissing(); return; }
        try
        {
            Directory.CreateDirectory(_dataFolder);
            _environment = await CoreWebView2Environment.CreateAsync(null, _dataFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or WebView2RuntimeNotFoundException or ArgumentException or InvalidOperationException)
        {
            Runtime = LoginBrowserRuntime.Missing; ShowMissing(); return;
        }
        StatusText.Text = _captured is not null ? Loc.T("neobrowser.status.signin") : "";
        OpenTab(_startUrl);
    }

    private void ShowMissing()
    {
        foreach (var view in _views.Values) view.Visibility = Visibility.Collapsed;
        MissingPanel.Visibility = Visibility.Visible;
        MissingText.Text = Loc.T("neobrowser.missing"); StatusText.Text = Loc.T("neobrowser.status.unavailable");
        BackButton.IsEnabled = ForwardButton.IsEnabled = ReloadButton.IsEnabled = StopButton.IsEnabled = NewTabButton.IsEnabled = false;
    }

    // ---------------------------------------------------------------- tabs
    /// <summary>Opens a tab (the Home page when no address is given). Null when the limit of <see cref="BrowserTabSet.Limit"/> tabs is reached or the browser is not running.</summary>
    public BrowserTab? OpenTab(string? address = null, bool activate = true)
    {
        if (_environment is null) return null;
        if (Tabs.IsFull) { StatusText.Text = Loc.T("neobrowser.status.tab_limit", BrowserTabSet.Limit); return null; }
        var tab = Tabs.Open(address, activate)!;
        tab.Title = tab.IsHome ? Loc.T("neobrowser.tab.new") : Loc.T("neobrowser.tab.loading");
        var web = new WebView2 { Visibility = Visibility.Collapsed };
        _views[tab.Id] = web; WebHost.Children.Insert(0, web);
        SyncStrip(); ShowActive();
        _ = InitTabAsync(tab, web, address);
        return tab;
    }

    private async Task InitTabAsync(BrowserTab tab, WebView2 web, string? address)
    {
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background); // the control starts from inside the running event loop
        if (!_views.ContainsKey(tab.Id)) return;
        try { await web.EnsureCoreWebView2Async(_environment); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or ObjectDisposedException) { if (!_views.ContainsKey(tab.Id)) return; Runtime = LoginBrowserRuntime.Missing; ShowMissing(); return; }
        if (!_views.ContainsKey(tab.Id)) return; // closed while starting
        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false; core.Settings.IsStatusBarEnabled = false; core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
        core.DownloadStarting += (_, e) => OnDownloadStarting(tab, core, e);
        core.NavigationStarting += (_, e) => { if (!tab.IsHome || LoginBrowserLogic.DisplayHost(e.Uri).Length > 0) { if (LoginBrowserLogic.DisplayHost(e.Uri).Length > 0) tab.Address = e.Uri; } tab.Loading = true; Refresh(tab); };
        core.ContentLoading += (_, _) => { _pickup.Clear(tab.Id); if (ReferenceEquals(Tabs.Active, tab)) UpdateMediaBar(); }; // a new page forgets what the old page played (a download is not a new page)
        core.NavigationCompleted += (_, _) => { tab.Loading = false; Refresh(tab); };
        core.SourceChanged += (_, _) => { if (LoginBrowserLogic.DisplayHost(core.Source).Length > 0) tab.Address = core.Source; Refresh(tab); };
        core.HistoryChanged += (_, _) => Refresh(tab);
        core.DocumentTitleChanged += (_, _) => { tab.Title = TitleFor(tab, core.DocumentTitle); Refresh(tab); };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true; // a link that wants a new window (target=_blank, middle click) opens a tab here
            if (LoginBrowserLogic.DisplayHost(e.Uri).Length == 0) return;
            if (Tabs.IsFull) { StatusText.Text = Loc.T("neobrowser.status.tab_limit", BrowserTabSet.Limit); core.Navigate(e.Uri); return; } // at the limit a link opens in place
            OpenTab(e.Uri);
        };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => { if (_pickup.Observe(tab.Id, e.Request.Uri) is not null && ReferenceEquals(Tabs.Active, tab)) UpdateMediaBar(); };
        core.WebMessageReceived += (_, e) => OnHomeMessage(tab, core, e);
        if (tab.IsHome) core.NavigateToString(HomeHtml());
        else core.Navigate(address);
        Refresh(tab);
    }

    private string TitleFor(BrowserTab tab, string? documentTitle)
    {
        if (tab.IsHome) return Loc.T("neobrowser.tab.new");
        if (!string.IsNullOrWhiteSpace(documentTitle) && !documentTitle.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return documentTitle.Trim();
        var host = LoginBrowserLogic.DisplayHost(tab.Address);
        return host.Length > 0 ? host : Loc.T("neobrowser.tab.loading");
    }

    public void CloseTab(int id)
    {
        if (Tabs.Find(id) is null) return;
        if (_views.Remove(id, out var view)) { WebHost.Children.Remove(view); try { view.Dispose(); } catch (Exception) { } }
        _pickup.Clear(id);
        var next = Tabs.Close(id);
        if (next is null) { Close(); return; }
        ShowActive();
    }
    public void CloseActiveTab() { if (Tabs.Active is { } t) CloseTab(t.Id); }

    private CoreWebView2? ActiveCore() => Tabs.Active is { } t && _views.TryGetValue(t.Id, out var v) ? v.CoreWebView2 : null;

    private void ShowActive()
    {
        foreach (var (id, view) in _views) view.Visibility = Runtime == LoginBrowserRuntime.Available && Tabs.Active?.Id == id ? Visibility.Visible : Visibility.Collapsed;
        UpdateToolbar();
        if (Tabs.Active is not null && _views.TryGetValue(Tabs.Active.Id, out var active) && !AddressBox.IsKeyboardFocusWithin && !TabStrip.IsKeyboardFocusWithin) active.Focus();
    }

    private void SyncStrip()
    {
        _syncing = true;
        try
        {
            var wanted = Tabs.Tabs.ToList();
            for (int i = _headers.Count - 1; i >= 0; i--) if (wanted.All(t => t.Id != _headers[i].Id)) _headers.RemoveAt(i);
            foreach (var tab in wanted)
            {
                var header = _headers.FirstOrDefault(h => h.Id == tab.Id);
                if (header is null) _headers.Insert(wanted.IndexOf(tab), header = new TabHeader { Id = tab.Id });
                header.Title = tab.Title.Length > 0 ? tab.Title : Loc.T("neobrowser.tab.loading");
            }
            TabStrip.SelectedItem = _headers.FirstOrDefault(h => h.Id == Tabs.Active?.Id);
        }
        finally { _syncing = false; }
        NewTabButton.IsEnabled = Runtime == LoginBrowserRuntime.Available && !Tabs.IsFull;
    }

    private void Refresh(BrowserTab tab)
    {
        SyncStrip();
        if (ReferenceEquals(Tabs.Active, tab)) UpdateToolbar();
    }

    private void UpdateToolbar()
    {
        var tab = Tabs.Active; var core = ActiveCore();
        BackButton.IsEnabled = core?.CanGoBack == true; ForwardButton.IsEnabled = core?.CanGoForward == true;
        ReloadButton.IsEnabled = core is not null && tab is { IsHome: false }; StopButton.IsEnabled = tab is { Loading: true };
        if (!AddressBox.IsKeyboardFocusWithin) AddressBox.Text = tab is null || tab.IsHome ? "" : tab.Address;
        UpdateMediaBar();
    }

    // ---------------------------------------------------------------- home page and the address box
    private string HomeHtml()
    {
        string C(string key) { var c = ((SolidColorBrush)FindResource(key)).Color; return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
        return NeoBrowserHome.Html(Loc.T("neobrowser.title"), Loc.T("neobrowser.home.hint"), Loc.T("neobrowser.home.go"), Loc.IsRtl, Loc.Language, C("CanvasBrush"), C("InkBrush"), C("AccentFillBrush"), C("OnAccentBrush"), C("MutedBrush"));
    }

    private void OnHomeMessage(BrowserTab tab, CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!tab.IsHome) return; // only the Home page of a tab may type an address
        string text; try { text = e.TryGetWebMessageAsString() ?? ""; } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        if (text.Length > BrowserAddress.MaxLength) return;
        Go(text, tab, core);
    }

    /// <summary>Opens what was typed in the active tab: a web address, or a search. Returns what was understood (a refused entry shows its hint in the status line).</summary>
    public AddressResult SubmitAddress(string text)
    {
        var tab = Tabs.Active; var core = ActiveCore();
        var result = BrowserAddress.Normalize(text);
        if (tab is null || core is null) return result;
        return Go(text, tab, core);
    }
    private AddressResult Go(string text, BrowserTab tab, CoreWebView2 core)
    {
        var result = BrowserAddress.Normalize(text);
        switch (result.Kind)
        {
            case AddressKind.Refused: StatusText.Text = Loc.T(result.HintKey ?? "neobrowser.hint.not_web"); break;
            case AddressKind.Url or AddressKind.Search: StatusText.Text = ""; tab.Address = result.Url!; core.Navigate(result.Url); break;
        }
        return result;
    }

    private void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; var r = SubmitAddress(AddressBox.Text); if (r.Kind is AddressKind.Url or AddressKind.Search) { AddressBox.Text = r.Url!; if (Tabs.Active is { } t && _views.TryGetValue(t.Id, out var v)) v.Focus(); } }
        else if (e.Key == Key.Escape) { e.Handled = true; AddressBox.Text = Tabs.Active is { IsHome: false } t ? t.Address : ""; if (Tabs.Active is { } a && _views.TryGetValue(a.Id, out var view)) view.Focus(); }
    }
    private void AddressBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => AddressBox.SelectAll();

    // ---------------------------------------------------------------- downloads and videos
    private void OnDownloadStarting(BrowserTab tab, CoreWebView2 core, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true; e.Handled = true; // the browser never saves the file itself: NeoDM downloads it
        var uri = e.DownloadOperation.Uri;
        if (_captured is not null && _handled) return;
        if (LoginBrowserLogic.DisplayHost(uri).Length == 0) { StatusText.Text = Loc.T("neobrowser.status.not_capturable"); return; } // blob: and data: addresses cannot be fetched again
        var name = Path.GetFileName(e.ResultFilePath); var agent = core.Settings.UserAgent; var page = LoginBrowserLogic.DisplayHost(core.Source).Length > 0 ? core.Source : tab.Address; // the page the person is on (a download starts as a navigation to the file)
        if (LoginBrowserLogic.DisplayHost(core.Source).Length > 0) { tab.Address = core.Source; tab.Loading = false; Refresh(tab); }
        long? size = e.DownloadOperation.TotalBytesToReceive is { } total and > 0 and <= long.MaxValue ? (long)total : null; var mime = e.DownloadOperation.MimeType;
        if (_captured is not null) { _handled = true; StatusText.Text = Loc.T("neobrowser.status.captured"); _ = CaptureForJobAsync(core, uri, name, agent, page); return; }
        _ = CaptureToSinkAsync(core, uri, name, size, mime, page, agent);
    }

    private static async Task<string> CookieHeaderAsync(CoreWebView2 core, string uri)
    {
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(uri); // only the cookies the browser would send to this address
            return LoginBrowserLogic.CookieHeader(cookies.Select(c => new BrowserCookie(c.Name, c.Value, c.Domain, c.Path, c.IsSecure, c.IsSession ? null : new DateTimeOffset(c.Expires.ToUniversalTime()))), new Uri(uri), DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UriFormatException or ObjectDisposedException) { return ""; }
    }

    private async Task CaptureForJobAsync(CoreWebView2 core, string uri, string? name, string? agent, string? page)
    {
        var header = await CookieHeaderAsync(core, uri);
        var grab = LoginBrowserLogic.Clean(new BrowserGrab(uri, name, page, header.Length > 0 ? header : null, agent, null, null));
        if (grab is null) { _handled = false; StatusText.Text = Loc.T("neobrowser.status.not_capturable"); return; }
        try { await _captured!(grab); }
        finally { Close(); }
    }

    private async Task CaptureToSinkAsync(CoreWebView2 core, string uri, string? name, long? size, string? mime, string? page, string? agent)
    {
        var header = await CookieHeaderAsync(core, uri);
        var request = NeoBrowserCapture.ForDownload(uri, name, size, mime, page, agent, header);
        if (request is null || CaptureSink is null) { StatusText.Text = Loc.T("neobrowser.status.not_capturable"); return; }
        StatusText.Text = Loc.T("neobrowser.status.sent");
        CaptureSink(request);
    }

    /// <summary>What the pages of each tab asked for (video streams and media files).</summary>
    public MediaPickup Pickup => _pickup;
    public void RefreshMediaBar() => UpdateMediaBar();
    private void UpdateMediaBar()
    {
        var show = CaptureSink is not null && _captured is null && Tabs.Active is { } t && _pickup.Current(t.Id) is not null;
        MediaBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void MediaDownload_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.Active is not { } tab || _pickup.Current(tab.Id) is not { } media || ActiveCore() is not { } core) return;
        var header = await CookieHeaderAsync(core, media.Url);
        var request = NeoBrowserCapture.ForMedia(media, LoginBrowserLogic.DisplayHost(core.Source).Length > 0 ? core.Source : tab.Address, core.Settings.UserAgent, header);
        if (request is null || CaptureSink is null) { StatusText.Text = Loc.T("neobrowser.status.not_capturable"); return; }
        StatusText.Text = Loc.T("neobrowser.status.sent");
        CaptureSink(request);
    }
    private void MediaHide_Click(object sender, RoutedEventArgs e) { if (Tabs.Active is { } t) _pickup.Dismiss(t.Id); UpdateMediaBar(); }

    // ---------------------------------------------------------------- buttons and keys
    private void NewTab_Click(object sender, RoutedEventArgs e) { OpenTab(); if (Tabs.Active is { IsHome: true }) AddressBox.Focus(); }
    private void CloseTab_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: int id }) CloseTab(id); }
    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || TabStrip.SelectedItem is not TabHeader h) return;
        Tabs.Activate(h.Id); ShowActive();
    }
    private void TabStrip_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        var item = e.OriginalSource is DependencyObject source ? FindAncestor<ListBoxItem>(source) : null;
        if (item?.DataContext is TabHeader h) { e.Handled = true; CloseTab(h.Id); }
    }
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null) { if (node is T hit) return hit; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node); }
        return null;
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0; var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0; var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ctrl && key == Key.T) { e.Handled = true; NewTab_Click(this, e); }
        else if (ctrl && key == Key.W) { e.Handled = true; CloseActiveTab(); }
        else if (ctrl && key == Key.Tab) { e.Handled = true; if (shift) Tabs.Previous(); else Tabs.Next(); ShowActive(); }
        else if ((ctrl && key == Key.L) || key == Key.F6) { e.Handled = true; AddressBox.Focus(); }
        else if (key == Key.F5 || (ctrl && key == Key.R)) { e.Handled = true; Reload_Click(this, e); }
        else if (alt && key == Key.Left) { e.Handled = true; Back_Click(this, e); }
        else if (alt && key == Key.Right) { e.Handled = true; Forward_Click(this, e); }
        else if (key == Key.Escape && !AddressBox.IsKeyboardFocusWithin && StopButton.IsEnabled) { e.Handled = true; Stop_Click(this, e); }
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (ActiveCore() is { CanGoBack: true } core) core.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (ActiveCore() is { CanGoForward: true } core) core.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) { if (ActiveCore() is { } core && Tabs.Active is { IsHome: false }) core.Reload(); }
    private void Stop_Click(object sender, RoutedEventArgs e) { if (ActiveCore() is { } core) core.Stop(); }
    private void MissingLink_Click(object sender, RoutedEventArgs e) => UrlOpener.Open(LoginBrowserLogic.RuntimeDownloadPage);
}

/// <summary>The private profile of the NeoDM browser: only its folder, so Options can show and clear it.</summary>
public static class NeoBrowserData
{
    public static bool Exists(string? folder = null) => Directory.Exists(folder ?? NeoBrowserWindow.DefaultDataFolder);
    /// <summary>Deletes the profile folder (cookies, sign-ins, cache). False with a reason when something is still using it, for example an open NeoDM browser window.</summary>
    public static bool Clear(out string? problem, string? folder = null)
    {
        problem = null; folder ??= NeoBrowserWindow.DefaultDataFolder;
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problem = ex.Message; return false; }
    }
}
