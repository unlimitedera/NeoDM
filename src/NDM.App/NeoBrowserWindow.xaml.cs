using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>1.12.0: the NeoDM browser, an Edge WebView2 window with its own private profile folder under the NeoDM state folder (never the user's browser profile). The person signs in, starts the
/// download on the page, and the first download is cancelled here and handed back with the sign-in details the browser holds for the download's own host. Cookies are read for that host only,
/// stay on this computer and are never logged.</summary>
public partial class NeoBrowserWindow : Window
{
    /// <summary>The private profile folder; removed by Options > Clear NeoDM browser data.</summary>
    public static string DefaultDataFolder => Path.Combine(App.StateDirectory, "browser-data");

    private readonly string _startUrl, _dataFolder;
    private readonly Func<BrowserGrab, Task> _captured;
    private readonly Func<string?>? _runtimeVersion;
    private bool _handled;

    /// <summary>Raised after the page asked for a download and the details were handed over (the window closes right after).</summary>
    public bool Captured => _handled;
    public LoginBrowserRuntime Runtime { get; private set; } = LoginBrowserRuntime.Available;
    public string StatusMessage => StatusText.Text;
    /// <summary>The page the window is showing (host only is displayed in the address box).</summary>
    public string? CurrentAddress => Web.CoreWebView2?.Source;

    public NeoBrowserWindow(string startUrl, Func<BrowserGrab, Task> captured, string? dataFolder = null, Func<string?>? runtimeVersion = null)
    {
        _startUrl = startUrl; _captured = captured; _dataFolder = dataFolder ?? DefaultDataFolder; _runtimeVersion = runtimeVersion;
        InitializeComponent();
        AddressBox.Text = LoginBrowserLogic.DisplayHost(startUrl);
        StatusText.Text = Loc.T("neobrowser.status.loading");
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => { try { Web.Dispose(); } catch (Exception) { /* the component may already be gone */ } };
    }

    private async Task StartAsync()
    {
        Runtime = LoginBrowserLogic.RuntimeFrom(_runtimeVersion ?? (() => { try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch (Exception) { return null; } }));
        if (Runtime == LoginBrowserRuntime.Missing) { ShowMissing(); return; }
        try
        {
            Directory.CreateDirectory(_dataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, _dataFolder);
            Web.Visibility = Visibility.Visible;
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or WebView2RuntimeNotFoundException or ArgumentException or InvalidOperationException)
        {
            Runtime = LoginBrowserRuntime.Missing; ShowMissing(); return;
        }
        var core = Web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false; core.Settings.IsStatusBarEnabled = false; core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
        core.DownloadStarting += OnDownloadStarting;
        core.NavigationStarting += (_, e) => { AddressBox.Text = LoginBrowserLogic.DisplayHost(e.Uri); };
        core.NavigationCompleted += (_, _) => UpdateButtons();
        core.HistoryChanged += (_, _) => UpdateButtons();
        core.NewWindowRequested += (_, e) => { e.Handled = true; if (LoginBrowserLogic.DisplayHost(e.Uri).Length > 0) core.Navigate(e.Uri); }; // one window: pop-ups open in place
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        ReloadButton.IsEnabled = true;
        StatusText.Text = Loc.T("neobrowser.status.signin");
        core.Navigate(_startUrl);
    }

    private void UpdateButtons()
    {
        var core = Web.CoreWebView2; if (core is null) return;
        BackButton.IsEnabled = core.CanGoBack; ForwardButton.IsEnabled = core.CanGoForward;
        AddressBox.Text = LoginBrowserLogic.DisplayHost(core.Source);
    }

    private void ShowMissing()
    {
        Web.Visibility = Visibility.Collapsed; MissingPanel.Visibility = Visibility.Visible;
        MissingText.Text = Loc.T("neobrowser.missing"); StatusText.Text = Loc.T("neobrowser.status.unavailable");
        BackButton.IsEnabled = ForwardButton.IsEnabled = ReloadButton.IsEnabled = false;
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true; e.Handled = true; // the browser never saves the file itself: NeoDM downloads it
        if (_handled) return;
        var uri = e.DownloadOperation.Uri;
        if (LoginBrowserLogic.DisplayHost(uri).Length == 0) { StatusText.Text = Loc.T("neobrowser.status.not_capturable"); return; } // blob: and data: addresses cannot be fetched again
        _handled = true;
        var name = Path.GetFileName(e.ResultFilePath); var agent = Web.CoreWebView2.Settings.UserAgent; var page = Web.CoreWebView2.Source;
        StatusText.Text = Loc.T("neobrowser.status.captured");
        _ = CaptureAsync(uri, name, agent, page);
    }

    private async Task CaptureAsync(string uri, string? name, string? agent, string? page)
    {
        string header = "";
        try
        {
            var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync(uri); // only the cookies the browser would send to this address
            header = LoginBrowserLogic.CookieHeader(cookies.Select(c => new BrowserCookie(c.Name, c.Value, c.Domain, c.Path, c.IsSecure, c.IsSession ? null : new DateTimeOffset(c.Expires.ToUniversalTime()))), new Uri(uri), DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UriFormatException) { header = ""; }
        var grab = LoginBrowserLogic.Clean(new BrowserGrab(uri, name, page, header.Length > 0 ? header : null, agent, null, null));
        if (grab is null) { _handled = false; StatusText.Text = Loc.T("neobrowser.status.not_capturable"); return; }
        try { await _captured(grab); }
        finally { Close(); }
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (Web.CoreWebView2?.CanGoBack == true) Web.CoreWebView2.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (Web.CoreWebView2?.CanGoForward == true) Web.CoreWebView2.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) { if (Web.CoreWebView2 is { } core) core.Reload(); }
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
