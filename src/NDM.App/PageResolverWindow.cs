using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>1.16.0: the small NeoDM browser window the page resolver works in (one at a time). It shows quietly in a corner without taking focus; for a human check it comes to the front with a plain message.
/// It uses the NeoDM browser's own private profile folder (Options > Clear NeoDM browser data removes it), never a real browser profile. Downloads are cancelled here and handed over (address, name, user agent, referrer,
/// and only the cookies the browser holds for that address); pop-ups are blocked; navigation stays on the pasted link's site. Nothing about the page or its cookies is logged.</summary>
public sealed class PageResolverWindow : Window, IPageSession
{
    private readonly WebView2 _web = new() { Visibility = Visibility.Visible };
    private readonly Border _banner = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(12, 8, 12, 8) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly string _dataFolder; private readonly Func<string?>? _runtimeVersion;
    private CoreWebView2Environment? _environment; private string _startUrl = "";
    private BrowserGrab? _capture; private string? _refusal; private DateTime _clickedAt = DateTime.MinValue; private bool _capturing;
    private TaskCompletionSource<bool>? _loaded;
    private bool _disposed;
    /// <summary>The message the banner shows for a human check or a missing control (tests read it).</summary>
    public string BannerText => _message.Text;
    public bool BannerVisible => _banner.Visibility == Visibility.Visible;
    public bool InFront { get; private set; }
    /// <summary>What a human check message says: the check kind decides the wording.</summary>
    public string HumanMessageKey { get; set; } = "pagelink.window.needs_you";

    public PageResolverWindow(string? dataFolder = null, Func<string?>? runtimeVersion = null)
    {
        _dataFolder = dataFolder ?? NeoBrowserWindow.DefaultDataFolder; _runtimeVersion = runtimeVersion;
        Title = Loc.T("pagelink.window.title"); Width = 520; Height = 440; MinWidth = 360; MinHeight = 300;
        ShowActivated = false; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.Manual;
        if (TryFindResource(typeof(Window)) is Style style) Style = style;
        var area = SystemParameters.WorkArea; Left = Math.Max(0, area.Right - Width - 16); Top = Math.Max(0, area.Bottom - Height - 16);
        _banner.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush"); _banner.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); _banner.BorderThickness = new Thickness(0, 1, 0, 0);
        _message.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush"); _message.FontWeight = FontWeights.SemiBold; _banner.Child = _message;
        var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(_web); Grid.SetRow(_banner, 1); grid.Children.Add(_banner); Content = grid;
        System.Windows.Automation.AutomationProperties.SetName(_web, Loc.T("pagelink.window.title"));
        Closed += (_, _) => { _disposed = true; try { _web.Dispose(); } catch (Exception) { /* the component may already be gone */ } };
    }

    private async Task EnsureAsync(CancellationToken token)
    {
        if (_web.CoreWebView2 is not null) return;
        var runtime = LoginBrowserLogic.RuntimeFrom(_runtimeVersion ?? (() => { try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch (Exception) { return null; } }));
        if (runtime == LoginBrowserRuntime.Missing) throw new InvalidOperationException("The Edge WebView2 runtime is missing.");
        if (!IsVisible) Show();
        Directory.CreateDirectory(_dataFolder);
        _environment ??= await CoreWebView2Environment.CreateAsync(null, _dataFolder);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        token.ThrowIfCancellationRequested();
        await _web.EnsureCoreWebView2Async(_environment);
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false; core.Settings.IsStatusBarEnabled = false; core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.NewWindowRequested += (_, e) => e.Handled = true; // pop-ups and ads never open
        core.NavigationStarting += (_, e) =>
        {
            if (ResolverSafety.AllowNavigation(_startUrl, e.Uri, DateTime.UtcNow - _clickedAt < TimeSpan.FromSeconds(30))) return;
            e.Cancel = true; // not this site, and not right after our own click
            if (e.IsRedirected) _refusal = LoginBrowserLogic.DisplayHost(e.Uri); // the first address itself sent us to another site: the link is refused, not followed
        };
        core.NavigationCompleted += (_, _) =>
        {
            // after the click the host may send us to its download address on another site; if that ended on a page (not a download) we are somewhere we do not belong
            if (!_capturing && _capture is null && ResolverSafety.IsWeb(core.Source) && !LoginBrowserLogic.SameSite(_startUrl, core.Source)) { _refusal = LoginBrowserLogic.DisplayHost(core.Source); try { core.Navigate("about:blank"); } catch (Exception) { } }
            _loaded?.TrySetResult(true);
        };
        core.DownloadStarting += (_, e) => OnDownload(core, e);
    }

    private void OnDownload(CoreWebView2 core, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true; e.Handled = true; // the page never saves the file itself: NeoDM downloads it
        if (_capture is not null || _capturing) return;
        var uri = e.DownloadOperation.Uri;
        if (!ResolverSafety.CapturableAddress(uri)) return;
        _capturing = true;
        string? name = null; try { name = Path.GetFileName(e.ResultFilePath); } catch (ArgumentException) { }
        var agent = core.Settings.UserAgent; var page = ResolverSafety.IsWeb(core.Source) ? core.Source : _startUrl;
        _ = CaptureAsync(core, uri, name, page, agent);
    }

    private async Task CaptureAsync(CoreWebView2 core, string uri, string? name, string? page, string? agent)
    {
        string header = "";
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(uri); // only the cookies the browser would send to this address
            header = LoginBrowserLogic.CookieHeader(cookies.Select(c => new BrowserCookie(c.Name, c.Value, c.Domain, c.Path, c.IsSecure, c.IsSession ? null : new DateTimeOffset(c.Expires.ToUniversalTime()))), new Uri(uri), DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UriFormatException or ObjectDisposedException) { }
        var grab = LoginBrowserLogic.Clean(new BrowserGrab(uri, name, page, header.Length > 0 ? header : null, agent, null, null));
        if (grab is not null) _capture = grab;
        _capturing = false;
    }

    public async Task LoadAsync(string url, CancellationToken token)
    {
        if (_disposed) throw new InvalidOperationException("The window is closed.");
        _startUrl = url; _capture = null; _refusal = null; _clickedAt = DateTime.MinValue;
        await EnsureAsync(token);
        _loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _web.CoreWebView2.Navigate(url);
        var done = await Task.WhenAny(_loaded.Task, Task.Delay(TimeSpan.FromSeconds(60), token));
        token.ThrowIfCancellationRequested();
        if (done != _loaded.Task && _capture is null) throw new TimeoutException("The page did not load.");
    }

    public async Task<PageSnapshot> ScanAsync(CancellationToken token)
    {
        if (_disposed || _web.CoreWebView2 is null) return PageSnapshot.Empty(_startUrl);
        try { return PageScripts.Parse(await _web.CoreWebView2.ExecuteScriptAsync(PageScripts.Scan), _startUrl); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException) { return PageSnapshot.Empty(_startUrl); }
    }

    public async Task<bool> ClickAsync(string controlId, CancellationToken token)
    {
        if (_disposed || _web.CoreWebView2 is null) return false;
        try { return await _web.CoreWebView2.ExecuteScriptAsync(PageScripts.Click(controlId)) == "true"; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException) { return false; }
    }

    public BrowserGrab? TakeCapture() { var c = _capture; _capture = null; return c; }
    public string? TakeRefusal() { var r = _refusal; _refusal = null; return r; }
    public void NoteClick() => _clickedAt = DateTime.UtcNow;

    public void Present(bool front)
    {
        if (_disposed) return;
        InFront = front;
        _banner.Visibility = front ? Visibility.Visible : Visibility.Collapsed;
        if (front) _message.Text = Loc.T(HumanMessageKey);
        if (!IsVisible) { ShowActivated = front; Show(); }
        if (front) { WindowState = WindowState.Normal; WindowActivation.BringToFront(this); }
    }
    /// <summary>Test seam: runs a script in the page (the end-to-end checks use it to play the person who completes a fixture check).</summary>
    public async Task<string> RunScriptForTestAsync(string script) => _web.CoreWebView2 is null ? "" : await _web.CoreWebView2.ExecuteScriptAsync(script);
    public void SetHumanMessage(string key) { HumanMessageKey = key; if (InFront) _message.Text = Loc.T(key); }
    void IDisposable.Dispose() { if (!_disposed) { _disposed = true; try { Close(); } catch (InvalidOperationException) { } } }
}
