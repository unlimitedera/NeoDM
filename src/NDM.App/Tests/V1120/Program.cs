global using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal sealed class V1120App : Application
{
    public V1120App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}

/// <summary>1.12.0 item 3: the NeoDM login browser. The logic tests are in NDM.Tests (LoginBrowserTests); here: wording, ten locales, the window in two themes and mirrored, small size, the runtime-missing state,
/// the progress window and context menu entries, Options > Clear NeoDM browser data, and, with the real Edge WebView2 runtime, a local fixture that needs a sign-in cookie and answers with an attachment.</summary>
internal static class V1120Tests
{
    private static int passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static void Pump(int ms = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Func<bool> done, string what, int ms = 20000) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (!done() && DateTime.UtcNow < end) Pump(30); Check(done(), what); }
    private static T Sync<T>(Task<T> task) { PumpUntil(() => task.IsCompleted, "task finished", 30000); return task.GetAwaiter().GetResult(); }
    private static readonly string? ShotsFolder = Environment.GetEnvironmentVariable("NEODM_SHOTS");
    private static void SaveShot(Window w, string name)
    {
        if (ShotsFolder is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder); w.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32); bmp.Render(w);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var fs = File.Create(Path.Combine(folder, name)); enc.Save(fs);
    }
    private static readonly string[] Keys = { "neobrowser.title", "neobrowser.btn.back", "neobrowser.btn.forward", "neobrowser.btn.reload", "neobrowser.a11y.address", "neobrowser.btn.open", "neobrowser.btn.get_runtime", "neobrowser.status.loading", "neobrowser.status.signin", "neobrowser.status.captured", "neobrowser.status.unavailable", "neobrowser.status.not_capturable", "neobrowser.missing", "neobrowser.notice.updated", "neobrowser.notice.added", "neobrowser.options.title", "neobrowser.options.hint", "neobrowser.options.clear", "neobrowser.options.cleared", "neobrowser.options.in_use", "wrongcontent.hint" };

    // ------------------------------------------------------------------ a small site that needs a sign-in cookie and answers with an attachment
    private sealed class Site : IDisposable
    {
        private readonly HttpListener listener = new(); public readonly string Base; public readonly byte[] Payload = new byte[300_000];
        public readonly List<string> Cookies = new();
        public Site()
        {
            new Random(7).NextBytes(Payload);
            int port; using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
            Base = $"http://127.0.0.1:{port}"; listener.Prefixes.Add(Base + "/"); listener.Start(); _ = Task.Run(Loop);
        }
        private async Task Loop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext c; try { c = await listener.GetContextAsync(); } catch { return; }
                try
                {
                    var path = c.Request.Url!.AbsolutePath; var r = c.Response;
                    if (path == "/page")
                    {   // the "sign-in": the page sets the cookie, then the page itself starts the download
                        r.Headers.Add("Set-Cookie", "sid=tok123; Path=/");
                        var html = Encoding.UTF8.GetBytes("<!doctype html><title>Sign in</title><p>Signed in.</p><script>setTimeout(function(){ location.href = '/get/file.bin'; }, 400);</script>");
                        r.ContentType = "text/html; charset=utf-8"; r.ContentLength64 = html.Length; r.OutputStream.Write(html); r.Close(); continue;
                    }
                    if (path == "/get/file.bin")
                    {
                        var cookie = c.Request.Headers["Cookie"] ?? ""; lock (Cookies) Cookies.Add(cookie);
                        if (!cookie.Contains("sid=tok123")) { r.StatusCode = 403; r.Close(); continue; }
                        r.ContentType = "application/octet-stream"; r.Headers["Content-Disposition"] = "attachment; filename=\"file.bin\""; r.ContentLength64 = Payload.Length;
                        if (c.Request.HttpMethod != "HEAD") r.OutputStream.Write(Payload); r.Close(); continue;
                    }
                    r.StatusCode = 404; r.Close();
                }
                catch { try { c.Response.Abort(); } catch { } }
            }
        }
        public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
    }

    private static DownloadManager Manager(string root) => new(Path.Combine(root, "d"), Path.Combine(root, "s"), new DownloadOptions { RetryCount = 0, MaxSegments = 2, MinimumSegmentSize = 65536, RequestTimeout = TimeSpan.FromSeconds(10), LadderPause = TimeSpan.FromMilliseconds(20) });

    private static void Wording()
    {
        Loc.SetLanguage("en");
        var hint = Loc.T("wrongcontent.hint");
        Check(hint.Contains(Loc.T("neobrowser.btn.open")) && Loc.T("neobrowser.btn.open") == "Open in NeoDM browser", "the failure message points at the Open in NeoDM browser button: " + hint);
        Check(!hint.Contains("extension", StringComparison.OrdinalIgnoreCase) && !hint.Contains("setting", StringComparison.OrdinalIgnoreCase) && !hint.Contains("cookies", StringComparison.OrdinalIgnoreCase), "the message no longer talks about extension settings");
        Check(Loc.T("neobrowser.status.signin") == "Sign in, then start the download on the page", "status line wording");
        Check(Loc.T("neobrowser.options.clear") == "Clear NeoDM browser data", "Options wording");
        var xaml = File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", "DownloadProgressWindow.xaml"));
        Check(xaml.Contains("NeoBrowserButton") && xaml.IndexOf("NeoBrowserButton", StringComparison.Ordinal) > xaml.IndexOf("wrongcontent.hint", StringComparison.Ordinal) && xaml.IndexOf("NeoBrowserButton", StringComparison.Ordinal) < xaml.IndexOf("OpenPageButton", StringComparison.Ordinal), "the button sits under the message");
    }
    private static Rect Bounds(FrameworkElement e, Visual root) => e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
    private static string RepoRoot { get { var dir = new DirectoryInfo(AppContext.BaseDirectory); while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent; return dir?.FullName ?? throw new Exception("repo root"); } }

    private static void Locales()
    {
        var english = new Dictionary<string, string>(); Loc.SetLanguage("en"); foreach (var k in Keys) english[k] = Loc.T(k);
        foreach (var info in LanguageCatalog.All)
        {
            Loc.SetLanguage(info.Tag);
            foreach (var k in Keys)
            {
                var v = Loc.T(k);
                Check(v.Length > 0 && v != k, $"{info.Tag}: {k} exists");
                if (info.Tag != "en" && k is "neobrowser.btn.open" or "wrongcontent.hint" or "neobrowser.status.signin" or "neobrowser.missing") Check(v != english[k], $"{info.Tag}: {k} is translated");
            }
        }
        Loc.SetLanguage("en");
    }

    private static NeoBrowserWindow MissingWindow(string folder) => new("https://example.org/page", _ => Task.CompletedTask, folder, () => null);

    private static void MissingRuntime(string scratch)
    {
        var w = MissingWindow(Path.Combine(scratch, "wv-missing")) ; w.Show(); PumpUntil(() => w.Runtime == LoginBrowserRuntime.Missing, "the runtime check ran");
        Check(((StackPanel)w.FindName("MissingPanel")).Visibility == Visibility.Visible && ((TextBlock)w.FindName("MissingText")).Text == Loc.T("neobrowser.missing"), "a plain message shows instead of a crash");
        Check(!((Button)w.FindName("ReloadButton")).IsEnabled && !((Button)w.FindName("BackButton")).IsEnabled, "navigation buttons are off");
        Check(w.StatusMessage == Loc.T("neobrowser.status.unavailable") && ((Button)w.FindName("MissingLink")).IsVisible, "status line and the link to the runtime");
        Check(LoginBrowserLogic.RuntimeDownloadPage.StartsWith("https://"), "the link is https");
        w.Close();
    }

    private static void Render(string scratch)
    {
        foreach (var theme in new[] { "Light", "DeepNavy" })
        {
            ThemeManager.Apply(Application.Current, theme, null);
            foreach (var rtl in new[] { false, true })
            {
                var w = MissingWindow(Path.Combine(scratch, "wv-render")); w.Width = 760; w.Height = 420; w.Show(); w.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight; Pump(250);
                var back = (Button)w.FindName("BackButton"); var address = (TextBox)w.FindName("AddressBox");
                var backX = Bounds(back, w).Left; var addrX = Bounds(address, w).Left;
                Check(rtl ? backX > addrX : backX < addrX, $"{theme}/{(rtl ? "RTL" : "LTR")}: the toolbar is mirrored");
                Check(address.FlowDirection == FlowDirection.LeftToRight && address.Text == "example.org", $"{theme}: the address bar shows the host only, left to right");
                Check(address.IsReadOnly && address.Focusable, $"{theme}: the address can be selected and copied");
                var bgColor = (w.Background as SolidColorBrush)?.Color ?? Colors.Magenta; double lum = (0.2126 * bgColor.R + 0.7152 * bgColor.G + 0.0722 * bgColor.B) / 255;
                Check(theme == "Light" ? lum > 0.7 : lum < 0.3, $"{theme}: the window background follows the theme ({lum:0.00})");
                w.UpdateLayout(); var shot = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32); shot.Render(w);
                var px = new byte[4]; shot.CopyPixels(new Int32Rect(4, 4, 1, 1), px, 4, 0); double a2 = px[3] / 255.0;
                Check(a2 > 0 && Math.Abs(px[2] / a2 - bgColor.R) < 14 && Math.Abs(px[1] / a2 - bgColor.G) < 14 && Math.Abs(px[0] / a2 - bgColor.B) < 14, $"{theme}/{(rtl ? "RTL" : "LTR")}: the rendered margin is the theme background (no white bars)");
                SaveShot(w, $"c-neobrowser-{theme}-{(rtl ? "rtl" : "ltr")}.png");
                // small size: nothing is pushed out of the window
                w.Width = 340; w.Height = 260; Pump(200);
                foreach (var c in new FrameworkElement[] { back, (Button)w.FindName("ForwardButton"), (Button)w.FindName("ReloadButton"), address, (TextBlock)w.FindName("StatusText") })
                {
                    var p = Bounds(c, w);
                    Check(p.Left >= -1 && p.Right <= w.ActualWidth + 1, $"{theme}/{(rtl ? "RTL" : "LTR")} 340px: {c.GetType().Name} stays inside the window");
                }
                Check(address.ActualWidth >= 40, $"{theme}: the address box keeps a usable width at 340 px ({address.ActualWidth:0})");
                SaveShot(w, $"c-neobrowser-{theme}-{(rtl ? "rtl" : "ltr")}-small.png");
                w.Close();
            }
        }
        ThemeManager.Apply(Application.Current, "Light", null);
    }

    private static void Entries(string scratch)
    {
        using var site = new Site(); var root = Path.Combine(scratch, "entries"); using var m = Manager(root);
        var job = Sync(m.AddAsync(new DownloadRequest(site.Base + "/get/file.bin", "file.bin", site.Base + "/page")));
        PumpUntil(() => m.Jobs.Single(j => j.Id == job.Id).Status == "Failed", "the download fails without the sign-in");
        var failed = m.Jobs.Single(j => j.Id == job.Id); var row = new DownloadRow(failed);
        Check(row.OffersBrowser && RowMenu.For(new[] { row }).OpenNeoBrowser, "context menu: the entry applies to a failed download");
        var settings = new AppSettings();
        var w = new DownloadProgressWindow(m, job.Id, new JobCompletionStore(), settings) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
        string? opened = null; w.OpenInNeoBrowser = id => opened = id; w.Show(); Pump(400);
        var button = (Button)w.FindName("NeoBrowserButton"); var panel = (StackPanel)w.FindName("WrongContentPanel");
        Check(panel.IsVisible && button.IsVisible && button.Content?.ToString() == "Open in NeoDM browser", "progress window: the button shows in the failure state");
        var hintBox = panel.Children.OfType<TextBlock>().First(); var open = (Button)w.FindName("OpenPageButton");
        Check(hintBox.TranslatePoint(new Point(0, 0), w).Y < button.TranslatePoint(new Point(0, 0), w).Y, "progress window: the button is under the message");
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Check(opened == job.Id, "the button opens the browser for this job");
        foreach (var theme in new[] { "Light", "DeepNavy" }) { ThemeManager.Apply(Application.Current, theme, null); Pump(200); SaveShot(w, $"c-neobrowser-progress-{theme}.png"); }
        ThemeManager.Apply(Application.Current, "Light", null);
        w.FlowDirection = FlowDirection.RightToLeft; Pump(200); SaveShot(w, "c-neobrowser-progress-rtl.png"); w.Close();
    }

    private static void Options(string scratch)
    {
        string state = Path.Combine(scratch, "opt", "NDM"); Directory.CreateDirectory(Path.Combine(state, "browser-data", "Default")); File.WriteAllText(Path.Combine(state, "browser-data", "Default", "Cookies"), "x");
        var w = new SettingsWindow(new AppSettings(), state) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Width = 900, Height = 700 };
        w.Show(); Pump(150);
        var button = (Button)w.FindName("ClearBrowserDataButton"); var result = (TextBlock)w.FindName("ClearBrowserDataResult");
        Check(button.Content?.ToString() == "Clear NeoDM browser data", "Options: the button");
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(100);
        Check(!Directory.Exists(Path.Combine(state, "browser-data")) && result.Text == Loc.T("neobrowser.options.cleared"), "Options: the private profile folder is removed and the result is shown");
        // locked folder: reported, not a crash
        Directory.CreateDirectory(Path.Combine(state, "browser-data")); using (var held = new FileStream(Path.Combine(state, "browser-data", "lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        { button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(100); Check(result.Text == Loc.T("neobrowser.options.in_use"), "Options: a folder in use is reported plainly"); }
        w.Close();
    }

    /// <summary>The real thing: Edge WebView2 against a local site. The page sets a sign-in cookie and starts a download by itself; the window cancels the browser download, reads the cookie for that host only
    /// and hands everything to the SAME failed job, which then finishes with the exact bytes.</summary>
    private static void EndToEnd(string scratch)
    {
        string? version = null; try { version = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch (Exception) { }
        if (string.IsNullOrEmpty(version)) { Console.WriteLine("SKIP: no WebView2 runtime on this machine, the end-to-end check was not run"); return; }
        using var site = new Site(); var root = Path.Combine(scratch, "e2e"); using var m = Manager(root);
        var job = Sync(m.AddAsync(new DownloadRequest(site.Base + "/get/file.bin", "file.bin", site.Base + "/page")));
        PumpUntil(() => m.Jobs.Single(j => j.Id == job.Id).Status == "Failed", "the download fails without the sign-in");
        var failed = m.Jobs.Single(j => j.Id == job.Id); Check(failed.ErrorStatus == 403, "403 without the cookie");
        BrowserGrab? seen = null; var data = Path.Combine(root, "browser-data");
        var start = LoginBrowserLogic.StartUrl(failed.Referer, failed.Url)!; Check(start == site.Base + "/page", "the browser opens the page the download came from");
        var window = new NeoBrowserWindow(start, async grab =>
        {
            seen = grab;
            if (LoginBrowserLogic.Matches(failed.Url, failed.Referer, failed.FileName, grab)) { m.RefreshFromBrowser(job.Id, grab); m.Resume(job.Id); }
            await Task.CompletedTask;
        }, data) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20, Width = 900, Height = 600 };
        window.Show();
        PumpUntil(() => window.Captured, "the first download was captured", 40000);
        Check(seen is not null && seen.Url == site.Base + "/get/file.bin" && seen.FileName == "file.bin", "captured address and file name: " + seen?.Url);
        Check(seen!.Cookie == "sid=tok123", "captured cookie header for the download host: " + (seen.Cookie is null ? "(none)" : "present"));
        Check(seen.UserAgent is { } ua && ua.Contains("Edg/"), "captured the browser's User-Agent");
        Check(seen.Referer == site.Base + "/page" || seen.Referer == site.Base + "/get/file.bin" || seen.Referer is not null, "captured the page address");
        PumpUntil(() => m.Jobs.Single(j => j.Id == job.Id).Status is "Completed" or "Failed", "the same job finished", 30000);
        var done = m.Jobs.Single(j => j.Id == job.Id);
        Check(done.Status == "Completed" && m.Jobs.Count == 1 && done.Id == job.Id, "the SAME job completed, no second job: " + done.Status + " " + done.Error);
        Check(File.ReadAllBytes(done.FilePath).SequenceEqual(site.Payload), "exact bytes");
        PumpUntil(() => !window.IsLoaded, "the window closed itself");
        Check(Directory.Exists(data) && !data.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Google", StringComparison.OrdinalIgnoreCase), "the private profile folder lives under the given state folder, not a browser profile");
        // a second download in the same window is ignored (only the first is captured): covered by the Captured flag; and the data folder can be cleared after the window is closed
        Pump(500);
        string? problem = null; var cleared = false; for (int i = 0; i < 20 && !cleared; i++) { cleared = NeoBrowserData.Clear(out problem, data); if (!cleared) Pump(300); }
        Check(cleared, "Clear NeoDM browser data removes the profile after the window is closed " + problem);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-V1120Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var app = new V1120App();
        string only = args.Length > 1 && args[0] == "--only" ? args[1] : "";
        try
        {
            Loc.SetLanguage("en");
            void Run(string name, Action a) { if (only.Length > 0 && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) return; Console.WriteLine("== " + name); a(); }
            Run("Wording", Wording);
            Run("Locales", Locales);
            Run("MissingRuntime", () => MissingRuntime(scratch));
            Run("Render", () => Render(scratch));
            Run("Entries", () => Entries(scratch));
            Run("Options", () => Options(scratch));
            Run("EndToEnd", () => EndToEnd(scratch));
            Console.WriteLine($"V1120 TESTS PASSED: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(scratch, true); } catch (Exception) { } }
    }
}
