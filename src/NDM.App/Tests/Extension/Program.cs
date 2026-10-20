global using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.Core;

internal sealed class ExtensionTestApp : Application
{
    public ExtensionTestApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}

/// <summary>Records activation calls so the tests never steal the real foreground.</summary>
internal sealed class FakeNative : IWindowNative
{
    public int Activations;
    private IntPtr foreground;
    public IntPtr ForegroundWindow() => foreground;
    public uint ThreadOf(IntPtr window) => 1;
    public uint CurrentThread() => 2;
    public bool IsMinimized(IntPtr window) => false;
    public void Restore(IntPtr window) { }
    public bool AttachThreadInput(uint from, uint to, bool attach) => true;
    public bool SetForeground(IntPtr window) { foreground = window; Activations++; return true; }
    public void BringToTop(IntPtr window) { }
    public void TapAlt() { }
    public void Flash(IntPtr window) { }
}

/// <summary>1.4.3: extension version compare, capture notice (banner and window), nag control, install guide window.</summary>
internal static class ExtensionTests
{
    private static int passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static void Pump(int ms = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static T Field<T>(FrameworkElement w, string name) where T : class => (T)(w.FindName(name) ?? throw new Exception("missing control " + name));
    private static IEnumerable<T> Visual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Visual<T>(child)) yield return nested;
        }
    }
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Directory.Build.props"))) dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        return dir ?? throw new Exception("repository root not found");
    }
    private static DownloadOptionsDialog NewDialog(string folder) => new("https://example.org/files/a.zip", "a.zip", null, null, folder) { ShowInTaskbar = false };
    private static string Mark(string text) => text.Replace("_", "");

    [STAThread]
    private static int Main()
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-ExtensionTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var app = new ExtensionTestApp();
        var native = new FakeNative(); var oldNative = WindowActivation.Native; WindowActivation.Native = native;
        try
        {
            CompareTable(); NoticeRules(scratch); WireFormat(); LongAddresses(); BannerInDialog(scratch, native); StandaloneNotice(scratch); Folders(scratch); Browsers(); GuideInEveryTheme(scratch, native); HelpMenu(scratch); Docs();
            Console.WriteLine($"Extension tests passed: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            WindowActivation.Native = oldNative;
            foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close();
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            app.Shutdown();
        }
    }

    // ===== 1. version compare =====
    private static void CompareTable()
    {
        var rows = new (string? Ext, string App, ExtensionStatus Expected)[]
        {
            ("1.4.3", "1.4.3", ExtensionStatus.Match), ("1.4.3.0", "1.4.3", ExtensionStatus.Match), ("1.4", "1.4.0", ExtensionStatus.Match), ("1.4.3", "1.4.3+abc123", ExtensionStatus.Match), ("1.4.3", "1.4.3-rc.1", ExtensionStatus.Match),
            ("1.4.2", "1.4.3", ExtensionStatus.Older), ("1.3.9", "1.4.0", ExtensionStatus.Older), ("1.9.0", "1.10.0", ExtensionStatus.Older), ("0.9", "1.0.0", ExtensionStatus.Older), ("1.4.3", "1.4.3.1", ExtensionStatus.Older),
            ("1.10.0", "1.9.0", ExtensionStatus.Newer), ("2.0", "1.9.9", ExtensionStatus.Newer), ("1.4.3.1", "1.4.3", ExtensionStatus.Newer), ("1.4.4", "1.4.3", ExtensionStatus.Newer),
            (null, "1.4.3", ExtensionStatus.Missing), ("", "1.4.3", ExtensionStatus.Missing), ("abc", "1.4.3", ExtensionStatus.Missing), ("1.4.3-beta", "1.4.3", ExtensionStatus.Missing), ("v1.4.3", "1.4.3", ExtensionStatus.Missing), ("1", "1.4.3", ExtensionStatus.Missing),
        };
        foreach (var (ext, app, expected) in rows)
            Check(ExtensionVersionCheck.Compare(ext, app).Status == expected, $"compare ext '{ext ?? "(none)"}' with app '{app}' is {expected}");
        foreach (var good in new[] { "1.4", "1.4.3", "0.0.1", "1234.5678.9012.3456" }) Check(ExtensionVersionCheck.IsValid(good), "valid wire version " + good);
        foreach (var bad in new[] { null, "", "1", "1.4.3.2.1", "1.4.x", "v1.4.3", "1.4.3-beta", "12345.1", "1..2", " 1.4.3", "1.4.3\n", "1.4.3+abc", new string('1', 25) }) Check(!ExtensionVersionCheck.IsValid(bad), "invalid wire version " + (bad?.Replace("\n", "\\n") ?? "(null)"));
        Check(ExtensionVersionCheck.Compare("1.3.3", "1.4.3").Message == "Your browser extension is version 1.3.3, NeoDM is 1.4.3. Update the extension so captured downloads and the media list keep working.", "older message is the specified sentence");
        Check(ExtensionVersionCheck.Compare(null, "1.4.3").Message.Contains("older than this app") && ExtensionVersionCheck.Compare(null, "1.4.3").Message.Contains("1.4.3"), "missing field is reported as older than this app");
        var newer = ExtensionVersionCheck.Compare("1.5.0", "1.4.3").Message;
        Check(newer.Contains("1.5.0") && newer.Contains("Update NeoDM"), "newer extension tells the user to update NeoDM");
        Check(ExtensionVersionCheck.Compare("1.4.3", "1.4.3").Message == "", "equal versions say nothing");
    }

    // ===== 2. nag control =====
    private static void NoticeRules(string scratch)
    {
        string state = Path.Combine(scratch, "state"); Directory.CreateDirectory(state);
        var settings = new AppSettings(); int saves = 0;
        var notice = new ExtensionNotice(settings, () => { saves++; settings.Save(state); }, "1.4.3");
        Check(notice.Evaluate("1.4.3") is null, "equal versions: no notice");
        Check(notice.Evaluate("1.3.3") is { Status: ExtensionStatus.Older }, "older extension: notice on the first capture");
        Check(notice.Evaluate("1.3.3") is null && notice.Evaluate("1.3.3") is null, "same extension version again in this session: no second notice");
        Check(notice.Evaluate("1.3.2") is { } , "a different extension version is a different pair and gets its own notice");
        Check(notice.Evaluate(null) is { Status: ExtensionStatus.Missing } && notice.Evaluate(null) is null, "a missing field is its own pair and is shown once");
        Check(new ExtensionNotice(settings, () => { }, "1.4.3").Evaluate("1.3.3") is not null, "a new app session shows the notice again");
        Check(new ExtensionNotice(settings, () => { }, "1.4.4").Evaluate("1.3.3") is not null && notice.Evaluate("1.3.3") is null, "the pair includes the app version");
        notice.Mute("1.3.3");
        Check(saves == 1 && settings.MutedExtensionVersions.SequenceEqual(new[] { "1.3.3" }), "muting stores the extension version and saves once");
        notice.Mute("1.3.3"); Check(saves == 1 && settings.MutedExtensionVersions.Count == 1, "muting twice stores it once");
        var reloaded = AppSettings.Load(state);
        Check(reloaded.MutedExtensionVersions.SequenceEqual(new[] { "1.3.3" }), "the mute is in settings.json after a restart");
        var fresh = new ExtensionNotice(reloaded, () => { }, "1.4.3");
        Check(fresh.Evaluate("1.3.3") is null, "muted version stays quiet in a new session");
        Check(fresh.Evaluate("1.3.4") is { }, "another version is not muted");
        fresh.Mute(null);
        Check(reloaded.MutedExtensionVersions.Contains("none") && new ExtensionNotice(reloaded, () => { }, "1.4.3").Evaluate(null) is null, "an extension without a version can be muted too (key 'none')");
        var copy = reloaded.Copy(); var target = new AppSettings(); target.CopyFrom(copy);
        Check(target.MutedExtensionVersions.SequenceEqual(reloaded.MutedExtensionVersions), "Copy and CopyFrom keep the list");
        File.WriteAllText(Path.Combine(state, "settings.json"), "{\"SettingsSchema\":4}");
        Check(AppSettings.Load(state).MutedExtensionVersions.Count == 0, "a settings file without the list loads as empty (no schema bump)");
        Check(AppSettings.CurrentSchema == 5, "settings schema is 5");
    }

    // ===== 3. wire format reaching the app =====
    private static void WireFormat()
    {
        string pipe = "NDM.ExtensionTest." + Guid.NewGuid().ToString("N");
        var seen = new List<NativeBridge.BrowserDownloadRequest>();
        using var bridge = new NativeBridge(request => { lock (seen) seen.Add(request); request.Respond(new NativeBridge.BrowserDownloadResponse(true, "id-1")); }, pipe);
        bridge.Start();
        string Send(string json)
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.ConnectAsync(5000).GetAwaiter().GetResult();
            var bytes = Encoding.UTF8.GetBytes(json + "\n"); client.Write(bytes); client.Flush();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return Encoding.UTF8.GetString(NativeBridge.ReadRequestAsync(client, cts.Token).GetAwaiter().GetResult());
        }
        var ok = Send("{\"url\":\"https://example.org/a.zip\",\"extVersion\":\"1.4.2\"}");
        Check(ok.Contains("\"ok\":true") && seen.Last().ExtVersion == "1.4.2", "app parses extVersion from the request");
        ok = Send("{\"url\":\"https://example.org/a.zip\"}");
        Check(ok.Contains("\"ok\":true") && seen.Last().ExtVersion is null, "a request without the field (older extension) still works and has no version");
        ok = Send("{\"url\":\"https://example.org/a.zip\",\"extVersion\":null}");
        Check(ok.Contains("\"ok\":true") && seen.Last().ExtVersion is null, "null extVersion is treated as absent");
        int before = seen.Count;
        foreach (var bad in new[] { "\"extVersion\":\"1.4.x\"", "\"extVersion\":\"1.4.3-beta\"", "\"extVersion\":14", "\"extVersion\":\"" + new string('1', 30) + "\"" })
            Check(Send("{\"url\":\"https://example.org/a.zip\"," + bad + "}").Contains("\"ok\":false") && seen.Count == before, "malformed " + bad[..Math.Min(24, bad.Length)] + " is rejected before it reaches the dialog");
    }

    // ===== 3b. long redirect addresses and the probe switch =====
    private static void LongAddresses()
    {
        string pipe = "NDM.ExtensionLong." + Guid.NewGuid().ToString("N");
        var seen = new List<NativeBridge.BrowserDownloadRequest>();
        using var bridge = new NativeBridge(request => { lock (seen) seen.Add(request); request.Respond(new NativeBridge.BrowserDownloadResponse(true, "id-1")); }, pipe);
        bridge.Start();
        string Send(string json)
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.ConnectAsync(5000).GetAwaiter().GetResult();
            var bytes = Encoding.UTF8.GetBytes(json + "\n"); client.Write(bytes); client.Flush();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return Encoding.UTF8.GetString(NativeBridge.ReadRequestAsync(client, cts.Token).GetAwaiter().GetResult());
        }
        string Of(int length) { const string head = "https://tunnel1.example/download/"; return head + new string('t', length - head.Length); }
        foreach (var length in new[] { 400, 8192 })
        {
            var ok = Send("{\"url\":\"" + Of(length) + "\",\"noProbe\":true}");
            Check(ok.Contains("\"ok\":true") && seen.Last().Url.Length == length && seen.Last().NoProbe, length + " character address is accepted with noProbe");
        }
        int before = seen.Count;
        Check(Send("{\"url\":\"" + Of(8193) + "\"}").Contains("\"ok\":false") && seen.Count == before, "8193 character address is rejected before the dialog");
        Check(Send("{\"url\":\"https://example.org/a.zip\",\"noProbe\":\"yes\"}").Contains("\"ok\":false") && seen.Count == before, "noProbe must be a boolean");
        Send("{\"url\":\"https://example.org/a.zip\"}");
        Check(!seen.Last().NoProbe, "noProbe defaults to false");
        Check(NativeBridge.LooksLikeTokenUrl("https://tunnel1.example/download/" + new string('x', 190)), "long token path skips the probe");
        Check(!NativeBridge.LooksLikeTokenUrl("https://example.org/files/setup.exe"), "short address keeps the probe");
        Check(!NativeBridge.LooksLikeTokenUrl("https://example.org/" + new string('a', 150) + ".zip?x=" + new string('b', 100)), "long query with a plain file name keeps the probe");
    }

    // ===== 4. banner inside the capture dialog =====
    private static void BannerInDialog(string scratch, FakeNative native)
    {
        string folder = Path.Combine(scratch, "dl"); Directory.CreateDirectory(folder);
        var plain = NewDialog(folder); plain.Show(); Pump();
        Check(plain.NoticeBanner.Visibility == Visibility.Collapsed, "no notice: the banner is hidden");
        plain.Close();

        foreach (var (ext, expectText) in new (string?, string)[] { ("1.3.3", "Your browser extension is version 1.3.3, NeoDM is 1.4.3. Update the extension so captured downloads and the media list keep working."), (null, "older than this app"), ("1.9.0", "Update NeoDM") })
        {
            var dialog = NewDialog(folder); dialog.Show();
            dialog.ShowExtensionNotice(ExtensionVersionCheck.Compare(ext, "1.4.3"), _ => { }); Pump();
            Check(dialog.NoticeBanner.Visibility == Visibility.Visible && dialog.NoticeBanner.Message.Contains(expectText), $"banner shows for extension '{ext ?? "(none)"}': {dialog.NoticeBanner.Message[..Math.Min(60, dialog.NoticeBanner.Message.Length)]}...");
            var start = Field<Button>(dialog, "StartButton");
            Check(start.IsEnabled && start.IsDefault && Field<Button>(dialog, "QueueButton").IsEnabled && Field<Button>(dialog, "ScheduleButton").IsEnabled, "the download buttons stay enabled and Start Download stays the default");
            dialog.Close();
        }

        var muted = new List<string?>(); var d2 = NewDialog(folder); d2.Show();
        d2.ShowExtensionNotice(ExtensionVersionCheck.Compare("1.3.3", "1.4.3"), muted.Add);
        int guides = 0; d2.NoticeBanner.OpenGuide = () => guides++;
        var banner = d2.NoticeBanner;
        Check(Mark(((Button)banner.FindName("GuideButton")).Content.ToString()!) == "Show install guide" && ((Button)banner.FindName("DismissButton")).Content.ToString() == "Dismiss" && Mark(((CheckBox)banner.FindName("MuteBox")).Content.ToString()!).Contains("Don't remind me about this version"), "banner has Show install guide, Dismiss and the mute check box with the specified labels");
        Click((Button)banner.FindName("GuideButton"));
        Check(guides == 1 && banner.Visibility == Visibility.Collapsed && muted.Count == 0, "Show install guide runs the guide, hides the banner and mutes nothing when the box is unticked");
        d2.Close();

        var d3 = NewDialog(folder); d3.Show(); muted.Clear();
        d3.ShowExtensionNotice(ExtensionVersionCheck.Compare("1.3.3", "1.4.3"), muted.Add);
        ((CheckBox)d3.NoticeBanner.FindName("MuteBox")).IsChecked = true;
        Click((Button)d3.NoticeBanner.FindName("DismissButton"));
        Check(d3.NoticeBanner.Visibility == Visibility.Collapsed && muted.SequenceEqual(new string?[] { "1.3.3" }), "Dismiss with the box ticked hides the banner and mutes the extension version");
        d3.Close(); Check(muted.Count == 1, "closing the dialog does not mute twice");

        var d4 = NewDialog(folder); d4.Show(); muted.Clear();
        d4.ShowExtensionNotice(ExtensionVersionCheck.Compare(null, "1.4.3"), muted.Add);
        ((CheckBox)d4.NoticeBanner.FindName("MuteBox")).IsChecked = true; d4.Close();
        Check(muted.SequenceEqual(new string?[] { null }), "ticking the box and closing the dialog still saves the choice");

        // the real guide opens from the button and is raised, not duplicated, the second time
        int before = native.Activations;
        var d5 = NewDialog(folder); d5.Show(); d5.ShowExtensionNotice(ExtensionVersionCheck.Compare("1.3.3", "1.4.3"), _ => { });
        Click((Button)d5.NoticeBanner.FindName("GuideButton"));
        var guide = Application.Current.Windows.OfType<ExtensionGuideWindow>().ToList();
        Check(guide.Count == 1 && guide[0].IsVisible && native.Activations > before, "Show install guide opens the guide window and brings it to the front");
        var second = ExtensionGuideWindow.ShowOrActivate();
        Check(ReferenceEquals(second, guide[0]) && Application.Current.Windows.OfType<ExtensionGuideWindow>().Count() == 1, "asking again raises the open guide instead of opening another");
        Check(guide[0].ShowInTaskbar && guide[0].Owner is null, "the guide has its own taskbar button and no owner");
        guide[0].Close(); d5.Close();
        Check(!Application.Current.Windows.OfType<ExtensionGuideWindow>().Any(), "closing the guide releases it");

        // the flow used by the app on a capture
        string state = Path.Combine(scratch, "flow"); Directory.CreateDirectory(state);
        var settings = new AppSettings(); var notice = new ExtensionNotice(settings, () => settings.Save(state), "1.4.3");
        var d6 = NewDialog(folder); d6.Show();
        Check(ExtensionNoticeFlow.Present(notice, "1.4.3", d6) is null && d6.NoticeBanner.Visibility == Visibility.Collapsed, "capture with an equal version: no banner");
        Check(ExtensionNoticeFlow.Present(notice, "1.4.2", d6) is { } && d6.NoticeBanner.Visibility == Visibility.Visible, "capture with an older extension: banner");
        var d7 = NewDialog(folder); d7.Show();
        Check(ExtensionNoticeFlow.Present(notice, "1.4.2", d7) is null && d7.NoticeBanner.Visibility == Visibility.Collapsed, "second capture in the same session: no banner");
        d6.Close(); d7.Close();
    }

    // ===== 5. standalone notice window =====
    private static void StandaloneNotice(string scratch)
    {
        var settings = new AppSettings(); var notice = new ExtensionNotice(settings, () => { }, "1.4.3");
        int before = Application.Current.Windows.OfType<ExtensionNoticeWindow>().Count();
        Check(ExtensionNoticeFlow.Present(notice, null, null) is { Status: ExtensionStatus.Missing }, "without a capture dialog the notice goes to its own window");
        var window = Application.Current.Windows.OfType<ExtensionNoticeWindow>().Single(); Pump();
        Check(window.IsVisible && window.ShowInTaskbar && window.Owner is null && window.NoticeBanner.Visibility == Visibility.Visible && window.NoticeBanner.Message.Contains("older than this app"), "the notice window is standalone and shows the message");
        ((CheckBox)window.NoticeBanner.FindName("MuteBox")).IsChecked = true;
        Click((Button)window.NoticeBanner.FindName("DismissButton")); Pump();
        Check(!window.IsVisible && settings.MutedExtensionVersions.Contains("none"), "Dismiss closes the notice window and saves the choice");
        Check(ExtensionNoticeFlow.Present(notice, "1.4.2", null) is { }, "a second window for a different extension version");
        foreach (var w in Application.Current.Windows.OfType<ExtensionNoticeWindow>().ToArray()) w.Close();
        Check(before == 0, "no notice window was left over");
    }

    // ===== 6. extension folder resolution =====
    private static void Folders(string scratch)
    {
        var installed = ExtensionGuide.ResolveFolder(@"C:\Program Files\NeoDM", p => p == @"C:\Program Files\NeoDM\extension\unpacked");
        Check(installed.Kind == ExtensionFolderKind.Installed && installed.Path == @"C:\Program Files\NeoDM\extension\unpacked", "installed layout is found first");
        var both = ExtensionGuide.ResolveFolder(@"C:\Program Files\NeoDM\", p => p.EndsWith(@"extension\unpacked") || p == @"C:\Program Files\extension");
        Check(both.Kind == ExtensionFolderKind.Installed, "the installed folder wins over a development folder; a trailing slash is fine");
        var dev = ExtensionGuide.ResolveFolder(@"F:\proj\dist\app", p => p == @"F:\proj\extension");
        Check(dev.Kind == ExtensionFolderKind.Development && dev.Path == @"F:\proj\extension", "development tree is found by walking up from the app folder");
        var none = ExtensionGuide.ResolveFolder(@"F:\proj\dist\app", _ => false);
        Check(none.Kind == ExtensionFolderKind.Missing && none.Path is null, "no folder: Missing");
        Check(ExtensionGuide.ResolveFolder("::not a path::", _ => false).Kind == ExtensionFolderKind.Missing, "a bad base directory does not throw");
        Check(ExtensionGuide.IsExtensionFolder(Path.Combine(RepoRoot(), "extension")), "the real extension folder has the NeoDM Integration manifest");
        string impostor = Path.Combine(scratch, "other"); Directory.CreateDirectory(impostor); File.WriteAllText(Path.Combine(impostor, "manifest.json"), "{\"name\":\"Something else\"}");
        Check(!ExtensionGuide.IsExtensionFolder(impostor) && !ExtensionGuide.IsExtensionFolder(Path.Combine(scratch, "nothing")), "another extension's folder and a missing folder do not count");
        File.WriteAllText(Path.Combine(impostor, "manifest.json"), "not json"); Check(!ExtensionGuide.IsExtensionFolder(impostor), "a broken manifest does not count");
        var real = ExtensionGuide.ResolveFolder();
        Check(real.Kind == ExtensionFolderKind.Development && real.Path == Path.Combine(RepoRoot(), "extension"), "from the test output folder the development tree is resolved: " + real.Path);
        string manifestVersion = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(real.Path!, "manifest.json")), "\"version\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
        Check(manifestVersion == AboutInfo.Version, "the extension folder holds version " + manifestVersion + ", the app version");
    }

    // ===== 7. browsers: finding and opening =====
    private static void Browsers()
    {
        Check(ExtensionGuide.Browsers.Select(b => b.Url).SequenceEqual(new[] { "chrome://extensions", "edge://extensions", "brave://extensions" }), "browser addresses");
        var chrome = ExtensionGuide.Chrome;
        Check(ExtensionGuide.FindBrowser(chrome, _ => "\"C:\\Reg\\chrome.exe\"", p => p == @"C:\Reg\chrome.exe") == @"C:\Reg\chrome.exe", "App Paths registry entry is used first (quotes removed)");
        string? viaDefault = ExtensionGuide.FindBrowser(ExtensionGuide.Edge, _ => null, p => p.EndsWith(@"Microsoft\Edge\Application\msedge.exe"));
        Check(viaDefault is not null && viaDefault.EndsWith("msedge.exe") && !viaDefault.Contains('%'), "default install folders are the fallback, with variables expanded");
        Check(ExtensionGuide.FindBrowser(ExtensionGuide.Brave, _ => @"C:\gone\brave.exe", _ => false) is null, "missing browser: null");
        var calls = new List<(string File, string Args)>(); var oldStart = ExtensionGuide.StartProgram;
        ExtensionGuide.StartProgram = (f, a) => calls.Add((f, a));
        try
        {
            foreach (var b in ExtensionGuide.Browsers) Check(ExtensionGuide.OpenExtensionsPage(b, _ => @"C:\x\b.exe") == "" && calls.Last() == (@"C:\x\b.exe", b.Url), $"{b.Name}: the browser program is started with {b.Url} and nothing else (no close, no kill, no new profile)");
            int count = calls.Count; var message = ExtensionGuide.OpenExtensionsPage(ExtensionGuide.Brave, _ => null);
            Check(calls.Count == count && message.Contains("Brave was not found"), "missing browser is reported and nothing is started: " + message);
            ExtensionGuide.StartProgram = (_, _) => throw new System.ComponentModel.Win32Exception(5);
            Check(ExtensionGuide.OpenExtensionsPage(chrome, _ => @"C:\x\chrome.exe").Contains("could not be started"), "a start failure is reported");
        }
        finally { ExtensionGuide.StartProgram = oldStart; }
        Check(!File.ReadAllText(Path.Combine(RepoRoot(), "src", "NDM.App", "ExtensionGuide.cs")).Contains(".Kill("), "the guide never kills a process");
    }

    // ===== 8. the guide window in every theme =====
    private static RenderTargetBitmap Capture(Window window)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        int w = (int)Math.Ceiling(content.ActualWidth), h = (int)Math.Ceiling(content.ActualHeight);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen()) { ctx.DrawRectangle(window.Background, null, new Rect(0, 0, w, h)); ctx.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight)); }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); return bitmap;
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static int CountColor(BitmapSource bitmap, Color color, int tol = 3)
    {
        var data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0); int n = 0;
        for (int i = 0; i < data.Length; i += 4) if (Math.Abs(data[i + 2] - color.R) <= tol && Math.Abs(data[i + 1] - color.G) <= tol && Math.Abs(data[i] - color.B) <= tol) n++;
        return n;
    }

    private static void GuideInEveryTheme(string scratch, FakeNative native)
    {
        string? shots = Environment.GetEnvironmentVariable("NEODM_SHOTS");
        var devFolder = new ExtensionFolder(ExtensionFolderKind.Installed, @"C:\Program Files\NeoDM\extension\unpacked");
        var clipboard = new List<string>(); var oldClip = ExtensionGuideWindow.SetClipboard; ExtensionGuideWindow.SetClipboard = t => { clipboard.Add(t); return true; };
        var started = new List<(string, string)>(); var oldStart = ExtensionGuide.StartProgram; ExtensionGuide.StartProgram = (f, a) => started.Add((f, a));
        try
        {
            foreach (var theme in ThemeCatalog.Concrete)
            {
                ThemeManager.Apply(Application.Current, theme.Id, null); string t = theme.Id + ": ";
                var window = new ExtensionGuideWindow("1.4.3", devFolder) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20, ShowInTaskbar = false };
                window.Show(); Pump(120);
                Check(window.PendingNotice == "The Chrome Web Store listing is waiting for approval. Until then, install the extension manually with these steps." && Visual<TextBlock>(window).Any(x => x.Text == window.PendingNotice && x.IsVisible), t + "the pending-approval line is shown at the top");
                Check(window.GuideImages.Count == 5 && window.GuideImages.All(i => i.Source is BitmapSource { PixelWidth: > 200 }), t + "five picture slots (four distinct screenshots) are loaded");
                Check(window.GuideImages.Select(i => ((BitmapImage)i.Source).UriSource.AbsolutePath).Distinct().Count() == 4, t + "four distinct screenshot files");
                Check(window.GuideImages.All(i => i.StretchDirection == StretchDirection.DownOnly && i.Stretch == Stretch.Uniform), t + "images shrink to fit and are never enlarged");
                var steps = Visual<TextBlock>(window).Select(x => x.Text).ToList();
                foreach (var step in ExtensionGuide.Steps("1.4.3", ExtensionGuide.Chrome)) Check(steps.Contains(step.Title), t + "step " + step.Number + " title is shown");
                Check(steps.Contains("Check that NeoDM Integration shows version 1.4.3 and is switched on."), t + "step 4 names the app version");
                var scroll = Field<ScrollViewer>(window, "GuideScroll");
                Check(scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && scroll.ScrollableHeight > 0 && Field<StackPanel>(window, "StepsPanel").Margin.Right >= 12, t + "steps scroll and keep a gap before the scroll bar");
                Check(window.BrowserButtons.Count == 3 && window.BrowserButtons["Chrome"].IsChecked == true && Field<TextBox>(window, "AddressBox").Text == "chrome://extensions", t + "selector starts on Chrome");
                foreach (var (id, url, where) in new[] { ("Edge", "edge://extensions", "left side"), ("Brave", "brave://extensions", "top right"), ("Chrome", "chrome://extensions", "top right") })
                {
                    window.BrowserButtons[id].IsChecked = true; Pump(30);
                    Check(Field<TextBox>(window, "AddressBox").Text == url && window.Browser.Id == id && window.DeveloperModeText.Contains(where), t + $"{id} shows {url} and '{where}' for Developer mode");
                }
                window.BrowserButtons["Edge"].IsChecked = true; Pump(30);
                Click(Field<Button>(window, "CopyAddressButton"));
                Check(clipboard.Last() == "edge://extensions" && window.StatusMessage.Contains("copied"), t + "Copy puts the shown address on the clipboard");
                window.BrowserFinder = b => b.Id == "Edge" ? @"C:\fake\msedge.exe" : null;
                Click(Field<Button>(window, "OpenPageButton"));
                Check(started.Last() == (@"C:\fake\msedge.exe", "edge://extensions"), t + "Open this page starts the chosen browser with its address");
                window.BrowserButtons["Brave"].IsChecked = true; Pump(30); int n = started.Count;
                Click(Field<Button>(window, "OpenPageButton"));
                Check(started.Count == n && window.StatusMessage.Contains("not found"), t + "a missing browser is announced and nothing starts");
                window.BrowserButtons["Chrome"].IsChecked = true; Pump(30);
                Check(window.PathBox.Text == devFolder.Path && window.CopyPathButton!.IsEnabled && window.OpenFolderButton!.IsEnabled, t + "step 3 shows the resolved folder with both buttons enabled");
                Click(window.CopyPathButton!); Check(clipboard.Last() == devFolder.Path, t + "Copy path copies the real folder path");
                var canvas = ((SolidColorBrush)Application.Current.FindResource("CanvasBrush")).Color;
                var shot = Capture(window);
                Check(shot.PixelWidth > 500 && CountColor(shot, canvas) > 20000, t + "window paints with the theme's canvas colour");
                var fill = Application.Current.FindResource("AccentFillBrush") is LinearGradientBrush g ? g.GradientStops[0].Color : ((SolidColorBrush)Application.Current.FindResource("AccentFillBrush")).Color;
                Check(CountColor(shot, fill, 6) > 100, t + "selected browser button and step badges use the accent");
                if (shots is { Length: > 0 } && theme.Id is "Midnight" or "Light")
                {
                    window.SelectBrowser(ExtensionGuide.Chrome); scroll.ScrollToTop(); window.UpdateLayout();
                    string file = Path.Combine(shots, "extension-guide-" + theme.Id.ToLowerInvariant() + ".png"); Save(Capture(window), file); Console.WriteLine("SHOT: " + file);
                }
                window.Close();
            }
        }
        finally { ExtensionGuideWindow.SetClipboard = oldClip; ExtensionGuide.StartProgram = oldStart; ThemeManager.Apply(Application.Current, "Light", null); }

        // folder cases in the window
        var dev = new ExtensionGuideWindow("1.4.3", new ExtensionFolder(ExtensionFolderKind.Development, @"F:\proj\extension")) { ShowInTaskbar = false }; dev.Show(); Pump();
        Check(dev.PathBox.Text == @"F:\proj\extension" && Visual<TextBlock>(dev).Any(x => x.Text.Contains("development copy")), "development tree: the path and a note that it is the development copy");
        dev.Close();
        var missing = new ExtensionGuideWindow("1.4.3", new ExtensionFolder(ExtensionFolderKind.Missing, null)) { ShowInTaskbar = false }; missing.Show(); Pump();
        Check(missing.PathBox.Text == "The extension folder was not found. Reinstall NeoDM to restore it." && !missing.CopyPathButton!.IsEnabled && !missing.OpenFolderButton!.IsEnabled, "missing folder: reinstall message, folder buttons disabled");
        missing.Close();
        Check(ExtensionGuide.OpenFolder(null) == ExtensionGuide.NotFoundMessage && ExtensionGuide.OpenFolder(@"Z:\does\not\exist") == ExtensionGuide.NotFoundMessage, "Open folder on a missing folder says so instead of starting anything");
        Check(ExtensionGuide.Steps("1.4.3").Select(s => s.Number).SequenceEqual(new[] { 0, 1, 2, 3, 4, 5 }) && ExtensionGuide.Steps("1.4.3")[0].Title == "If you already installed NeoDM Integration, remove the old one first." && ExtensionGuide.Steps("1.4.3")[0].Lines.Contains(ExtensionGuide.RemoveWhy), "step 0 is the remove-the-old-one step with its reason, in order before Developer mode");
        Check(ExtensionGuide.Steps("1.4.3").Count(s => s.Image is not null) == 5 && ExtensionGuide.Steps("1.4.3").Where(s => s.Image is not null).Select(s => s.Image).Distinct().Count() == 4, "four screenshots, the extension card used for steps 0 and 4");
    }

    // ===== 9. Help menu =====
    private static void HelpMenu(string scratch)
    {
        string files = Path.Combine(scratch, "main-files"), state = Path.Combine(scratch, "main-state"); Directory.CreateDirectory(files); Directory.CreateDirectory(state);
        using var manager = new DownloadManager(files, state, new DownloadOptions { MaxConcurrentDownloads = 1, MaxSegments = 1, RetryCount = 0 });
        var main = new MainWindow(manager, new AppSettings { DownloadDirectory = files }, () => { }, enableTray: false) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
        main.Show(); Pump(200);
        var help = Visual<MenuItem>(main).Select(m => m).FirstOrDefault(m => Mark(m.Header?.ToString() ?? "") == "Help");
        Check(help is not null, "Help menu exists");
        var item = help!.Items.OfType<MenuItem>().FirstOrDefault(m => Mark(m.Header?.ToString() ?? "") == "Browser extension install guide…");
        Check(item is not null && Field<MenuItem>(main, "ExtensionGuideMenuItem") == item, "Help > Browser extension install guide… exists");
        int before = Application.Current.Windows.OfType<ExtensionGuideWindow>().Count();
        item!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(150);
        var guide = Application.Current.Windows.OfType<ExtensionGuideWindow>().ToList();
        Check(before == 0 && guide.Count == 1 && guide[0].IsVisible, "the menu item opens the guide");
        Check(guide[0].Owner is null && guide[0].ShowInTaskbar, "the guide opened from the menu is modeless and standalone");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(100);
        Check(Application.Current.Windows.OfType<ExtensionGuideWindow>().Count() == 1, "the menu item raises the open guide instead of opening a second");
        guide[0].Close();
        var about = new AboutWindow { ShowInTaskbar = false }; about.Show(); Pump();
        Check(about.FindName("InstallGuideButton") is Button { IsVisible: true }, "About has the install guide button");
        Click(Field<Button>(about, "InstallGuideButton")); Pump(150);
        Check(Application.Current.Windows.OfType<ExtensionGuideWindow>().Count() == 1 && !about.IsVisible, "the About button closes About and opens the guide");
        foreach (var w in Application.Current.Windows.OfType<ExtensionGuideWindow>().ToArray()) w.Close();
        typeof(MainWindow).GetField("_exitWithoutPrompt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(main, true);
        main.Close();
    }

    // ===== 10. docs and images =====
    private static void Docs()
    {
        string root = RepoRoot();
        string guideDoc = File.ReadAllText(Path.Combine(root, "docs", "EXTENSION-INSTALL.md")), version = AboutInfo.Version;
        foreach (var step in ExtensionGuide.Steps(version))
        {
            Check(guideDoc.Contains(step.Title), "docs: step " + step.Number + " title matches the window");
            foreach (var line in step.Lines) Check(guideDoc.Contains(line), "docs: step " + step.Number + " line matches: " + line[..Math.Min(40, line.Length)]);
        }
        foreach (var (problem, answer) in ExtensionGuide.Troubleshooting) Check(guideDoc.Contains(problem) && guideDoc.Contains(answer), "docs: troubleshooting matches: " + problem[..30]);
        Check(guideDoc.StartsWith("# Install the NeoDM browser extension") && guideDoc.Contains("> " + ExtensionGuide.PendingApprovalDoc) && guideDoc.Contains("it will install from the store and update by itself, and a manual install can then be removed."), "docs: begins with the pending-approval blockquote and says what changes once approved");
        string readme = File.ReadAllText(Path.Combine(root, "README.md")), chrome = File.ReadAllText(Path.Combine(root, "docs", "CHROME.md"));
        Check(readme.Contains("### Browser extension") && readme.Contains(ExtensionGuide.PendingApprovalDoc) && readme.Contains("[Extension install guide](docs/EXTENSION-INSTALL.md)") && ExtensionGuide.Images.All(i => readme.Contains("docs/images/extension-guide/" + i)), "README: Browser extension section with the notice, nav link and the four screenshot links");
        Check(chrome.Contains(ExtensionGuide.PendingApprovalDoc) && chrome.Contains("EXTENSION-INSTALL.md"), "docs/CHROME.md: notice and guide link");
        Check(!(readme + guideDoc + chrome).Contains("is published") && !(readme + guideDoc + chrome).Contains("has been approved"), "no claim that the listing is published or approved");
        Check(ExtensionGuide.SameForAllBrowsers is { } && guideDoc.Contains(ExtensionGuide.SameForAllBrowsers) && guideDoc.Contains(ExtensionGuide.NotFoundMessage), "docs: same-for-all-browsers sentence and the not-found message");
        foreach (var browser in ExtensionGuide.Browsers) Check(guideDoc.Contains(browser.Url) && guideDoc.Contains(ExtensionGuide.DeveloperModeLine(browser)), "docs: " + browser.Name + " address and Developer mode wording");
        foreach (var image in ExtensionGuide.Images)
        {
            string asset = Path.Combine(root, "src", "NDM.App", "Assets", "Guide", image), doc = Path.Combine(root, "docs", "images", "extension-guide", image);
            Check(File.Exists(asset) && File.Exists(doc) && File.ReadAllBytes(asset).SequenceEqual(File.ReadAllBytes(doc)), "image " + image + " is the same in Assets/Guide and docs/images/extension-guide");
            Check(guideDoc.Contains("images/extension-guide/" + image), "docs link " + image);
            var bitmap = new BitmapImage(new Uri(asset)); Check(bitmap.PixelWidth is >= 500 and <= 800 && new FileInfo(asset).Length < 150_000, $"{image} is small ({bitmap.PixelWidth}px wide, {new FileInfo(asset).Length / 1024} KB)");
        }
        Check(Directory.GetFiles(Path.Combine(root, "src", "NDM.App", "Assets", "Guide")).Length == 4, "exactly four guide images");
        Check(File.ReadAllText(Path.Combine(root, "README.md")).Contains("docs/EXTENSION-INSTALL.md") && File.ReadAllText(Path.Combine(root, "docs", "CHROME.md")).Contains("EXTENSION-INSTALL.md"), "README and docs/CHROME.md link the guide");
        Check(new[] { "README.md", "docs/EXTENSION-INSTALL.md", "docs/CHROME.md", "CHANGELOG.md" }.All(f => !File.ReadAllText(Path.Combine(root, f)).Contains('\u2014')), "no em dashes in the new text");
    }
}
