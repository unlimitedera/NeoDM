using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;

internal static partial class V1110Tests
{
    private const string Dev = "hohmmojlhbkelkobobpkochpdempcihd", Store = "ojbaibjfbdjodgmfbiajkigpanbdkiid";

    // ---------------------------------------------------------------- item 1: the installer has no extension policy option
    private static void InstallerNoPolicy()
    {
        string root = RepoRoot;
        string wxs = File.ReadAllText(Path.Combine(root, "installer", "NeoDM.wxs"));
        string cfg = File.ReadAllText(Path.Combine(root, "installer", "configure.ps1"));
        string build = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));
        string instReadme = File.ReadAllText(Path.Combine(root, "installer", "README.md"));
        string readme = File.ReadAllText(Path.Combine(root, "README.md"));
        foreach (var token in new[] { "INSTALL_POLICY", "POLICY_CHECK", "PolicyBox", "PolicyNote", "PolicyDefault", "browser policy" })
            Check(!wxs.Contains(token, StringComparison.OrdinalIgnoreCase) && !build.Contains(token, StringComparison.OrdinalIgnoreCase), "installer sources do not contain " + token);
        Check(!cfg.Contains("[int]$Policy") && !cfg.Contains("-Policy") && !cfg.Contains("$Policy -eq"), "configure.ps1 has no Policy parameter and no code that writes a policy");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(cfg, @"Set-ItemProperty[^\n]*\$policyName") && !cfg.Contains("New-Item -Path $list"), "configure.ps1 never creates a policy key or value");
        Check(!readme.Contains("INSTALL_POLICY") && !instReadme.Contains("INSTALL_POLICY=1` (off") && !readme.Contains("Also install through browser policy"), "README and installer README no longer offer the option");
        Check(!wxs.Contains("-Policy"), "the Configure custom action passes no policy argument");
        // the native messaging host registration and both trusted IDs stay
        foreach (var vendor in new[] { @"SOFTWARE\Google\Chrome\NativeMessagingHosts\com.ndm.bridge", @"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.ndm.bridge", @"SOFTWARE\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.ndm.bridge" })
            Check(wxs.Contains(vendor), "host registration stays: " + vendor);
        Check(build.Contains("$extId = \"$devExtId,$storeExtId\""), "both trusted extension IDs are still passed to configure.ps1");
        Check(File.ReadAllText(Path.Combine(root, "tools", "extension-id.txt")).Trim() == Dev && File.ReadAllText(Path.Combine(root, "tools", "extension-store-id.txt")).Trim() == Store, "the development and store IDs are unchanged");
        Check(cfg.Contains("allowed_origins") && cfg.Contains("extensionIds"), "configure.ps1 still writes the host manifest origins and bridge config IDs");
        // clean upgrade: an older version's policy value is removed, on install and on uninstall, and only when it is ours
        Check(cfg.Contains("function Remove-OldPolicy") && cfg.Contains("$policyName = '7421'"), "configure.ps1 can remove the value an older version wrote");
        int removeCalls = System.Text.RegularExpressions.Regex.Matches(cfg, @"^\s*Remove-OldPolicy\s*$", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        Check(removeCalls == 2, "the cleanup runs on install (upgrade) and on uninstall");
        Check(cfg.Contains("StartsWith(\"$_;\")") && cfg.Contains("continue }   # not written by NeoDM"), "only a value whose data starts with a NeoDM extension ID is removed; other policies are left alone");
    }

    // ---------------------------------------------------------------- item 2: version comparison
    private static void VersionTable()
    {
        var table = new (string? ext, string app, ExtensionStatus status)[]
        {
            ("1.11.0", "1.11.0", ExtensionStatus.Match), ("1.11", "1.11.0", ExtensionStatus.Match), ("1.10.0", "1.11.0", ExtensionStatus.Older), ("1.9.9", "1.10.0", ExtensionStatus.Older),
            ("1.12.0", "1.11.0", ExtensionStatus.Newer), ("2.0", "1.11.0", ExtensionStatus.Newer), ("1.11.0.1", "1.11.0", ExtensionStatus.Newer),
            (null, "1.11.0", ExtensionStatus.Missing), ("", "1.11.0", ExtensionStatus.Missing), ("banana", "1.11.0", ExtensionStatus.Missing), ("1.x.0", "1.11.0", ExtensionStatus.Missing),
            ("1.11.0; drop table", "1.11.0", ExtensionStatus.Missing), ("1.2.3.4.5", "1.11.0", ExtensionStatus.Missing), (new string('1', 30), "1.11.0", ExtensionStatus.Missing), ("1.11.0", "1.11.0+abc123", ExtensionStatus.Match),
        };
        foreach (var (ext, app, status) in table)
            Check(ExtensionVersionCheck.Compare(ext, app, ExtensionChannel.Unknown, "Brave").Status == status, $"compare '{ext}' with app {app} = {status}");
        var older = ExtensionVersionCheck.Compare("1.10.0", "1.11.0", ExtensionChannel.Development, "Microsoft Edge");
        Check(older.Message.Contains("The NeoDM extension in Microsoft Edge is outdated (version 1.10.0, app version 1.11.0) and might cause problems."), "older message names the browser and both versions: " + older.Message);
        Check(older.Message.Contains("reload the unpacked extension"), "a manual (development) copy is told to reload the unpacked extension");
        var store = ExtensionVersionCheck.Compare("1.10.0", "1.11.0", ExtensionChannel.Store, "Google Chrome");
        Check(store.Message.Contains("Chrome Web Store") && !store.Message.Contains("unpacked"), "a store copy is told to update from the store page");
        var unknownChannel = ExtensionVersionCheck.Compare("1.10.0", "1.11.0", ExtensionChannel.Unknown, "Brave");
        Check(unknownChannel.Message.Contains("store") && unknownChannel.Message.Contains("unpacked"), "an unknown channel gets both hints");
        var newer = ExtensionVersionCheck.Compare("1.12.0", "1.11.0", ExtensionChannel.Development, "Brave");
        Check(newer.Message.Contains("newer than the app") && newer.Message.Contains("Brave") && newer.Message.Contains("Update NeoDM"), "newer message: " + newer.Message);
        var missing = ExtensionVersionCheck.Compare(null, "1.11.0", ExtensionChannel.Unknown, "Brave");
        Check(missing.Message.Contains("outdated") && missing.Message.Contains("reports no version"), "a missing version counts as outdated: " + missing.Message);
        Check(ExtensionVersionCheck.Compare("1.11.0", "1.11.0", ExtensionChannel.Unknown, "Brave").Message == "", "a matching version says nothing");
        Check(ExtensionVersionCheck.Compare("1.10.0", "1.11.0").Message.Contains("Your browser extension"), "without a browser name the general wording is used (old hosts)");
    }

    // ---------------------------------------------------------------- item 2: per-browser records
    private static void Book(string scratch)
    {
        string dir = Path.Combine(scratch, "book", "NDM"); Directory.CreateDirectory(dir);
        var settings = new AppSettings(); var now = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero); int saves = 0;
        var book = new BrowserExtensionBook(settings, () => { saves++; settings.Save(dir); }, "1.11.0", () => now);
        var chrome = book.Seen("Google Chrome", "1.11.0", Store);
        Check(!chrome.NoticeDue && chrome.Check.Status == ExtensionStatus.Match && saves == 1, "a matching browser is recorded and gets no notice");
        now = now.AddMinutes(5);
        var edge = book.Seen("Microsoft Edge", "1.10.0", Dev);
        Check(edge.NoticeDue && edge.Check.Status == ExtensionStatus.Older && edge.Record.Channel == ExtensionChannel.Development, "an outdated Edge gets a notice");
        Check(book.Records.Count == 2 && book.Records[0].Browser == "Microsoft Edge", "records are listed newest first");
        // one notice per browser per run and per version pair
        book.NoticeShown(edge.Record);
        Check(!book.Seen("Microsoft Edge", "1.10.0", Dev).NoticeDue, "no second notice for the same browser and version in this run");
        Check(!book.Seen("microsoft edge", "1.10.0", Dev).NoticeDue && book.Records.Count == 2, "browser names compare without case (one record)");
        var again = new BrowserExtensionBook(AppSettings.Load(dir), () => { }, "1.11.0", () => now);
        Check(!again.Seen("Microsoft Edge", "1.10.0", Dev).NoticeDue, "after a restart the same browser, version and app version pair is not announced again (remembered)");
        Check(again.Seen("Brave", null, null).NoticeDue, "a browser that sends no version is outdated and announced once");
        var bumped = new BrowserExtensionBook(AppSettings.Load(dir), () => { }, "1.12.0", () => now);
        Check(bumped.Seen("Microsoft Edge", "1.10.0", Dev).NoticeDue, "a new app version makes a new pair, so one new notice");
        // store build ahead of the app is not a problem
        var ahead = new BrowserExtensionBook(new AppSettings(), () => { }, "1.11.0", () => now).Seen("Google Chrome", "1.12.0", Store);
        Check(ahead.Check.Status == ExtensionStatus.Newer && !ahead.NoticeDue, "the store copy ahead of the app gets no notice");
        var devAhead = new BrowserExtensionBook(new AppSettings(), () => { }, "1.11.0", () => now).Seen("Google Chrome", "1.12.0", Dev);
        Check(devAhead.NoticeDue, "an unpacked copy ahead of the app does get one");
        // bad data
        var bad = new BrowserExtensionBook(new AppSettings(), () => { }, "1.11.0", () => now);
        var weird = bad.Seen("<script>", "javascript:1", "zzz");
        Check(weird.Record.Browser == BrowserExtensionBook.UnknownBrowser && weird.Record.Version is null && weird.Record.Channel == ExtensionChannel.Unknown, "invalid name, version and ID are treated as not reported");
        var longName = bad.Seen(new string('a', 33), "1.11.0"); Check(longName.Record.Browser == BrowserExtensionBook.UnknownBrowser, "a 33 character name is refused");
        // the cap
        var many = new BrowserExtensionBook(new AppSettings(), () => { }, "1.11.0", () => now);
        for (int i = 0; i < 12; i++) { now = now.AddMinutes(1); many.Seen("Browser" + i, "1.11.0"); }
        Check(many.Records.Count == BrowserExtensionBook.MaxBrowsers && many.Records.All(r => int.Parse(r.Browser[7..]) >= 4), "at most 8 browsers are kept; the oldest sightings go first");
        // persistence and hand-edited files
        many.Seen("Vivaldi", "1.9.0", Dev);
        var raw = new AppSettings(); raw.BrowserExtensions = Enumerable.Range(0, 30).Select(i => new BrowserExtensionRecord { Browser = i % 5 == 0 ? "bad<name>" : "B" + i, Version = i % 3 == 0 ? "evil" : "1.2.3", LastSeen = now.AddMinutes(i), NoticedKey = new string('k', 100) }).ToList();
        raw.Save(dir); var loaded = AppSettings.Load(dir);
        Check(loaded.BrowserExtensions.Count <= 8 && loaded.BrowserExtensions.All(r => BrowserNames.IsValid(r.Browser) && (r.Version is null || ExtensionVersionCheck.IsValid(r.Version)) && r.NoticedKey is null), "loading a hand-edited settings file bounds and cleans the records");
        var copy = loaded.Copy(); Check(copy.BrowserExtensions.Count == loaded.BrowserExtensions.Count && !ReferenceEquals(copy.BrowserExtensions, loaded.BrowserExtensions), "Options copies keep the records without sharing the list");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"BrowserExtensions\":null}"); Check(AppSettings.Load(dir).BrowserExtensions.Count == 0, "a null list loads as empty");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"CheckForUpdates\":false}"); Check(AppSettings.Load(dir).BrowserExtensions.Count == 0, "an older settings file without the list loads fine");
        // muted versions (the existing 'do not remind me' choice) stay respected
        var mutedSettings = new AppSettings(); mutedSettings.MutedExtensionVersions.Add("1.10.0");
        Check(!new BrowserExtensionBook(mutedSettings, () => { }, "1.11.0", () => now).Seen("Brave", "1.10.0").NoticeDue, "a muted extension version gets no notice");
    }

    // ---------------------------------------------------------------- item 2: notice rate limiting through the dialog/window path
    private static void NoticeLimits()
    {
        var settings = new AppSettings(); var now = DateTimeOffset.UtcNow;
        var notice = new ExtensionNotice(settings, () => { }, "1.11.0", () => now);
        Check(notice.Evaluate("1.10.0", Dev, "Brave") is { } first && first.Browser == "Brave", "first mismatch for Brave: notice with the browser name");
        Check(notice.Evaluate("1.10.0", Dev, "Brave") is null, "second capture in the same run: no notice");
        Check(notice.Evaluate("1.10.0", Dev, "Microsoft Edge") is { } edge && edge.Message.Contains("Microsoft Edge"), "a different browser with the same version gets its own notice");
        Check(notice.Evaluate("1.10.0", Dev, "Microsoft Edge") is null, "and only one");
        Check(notice.Evaluate("1.11.0", Dev, "Google Chrome") is null, "a matching browser: nothing");
        Check(notice.Evaluate("1.10.0", Store, "Google Chrome") is { } s && s.Message.Contains("Chrome Web Store"), "store copy outdated: store wording");
        Check(notice.Evaluate("1.10.0", Store, "Google Chrome") is null, "store copy: not again in the same run");
        Check(notice.Evaluate("1.12.0", Store, "Google Chrome") is null, "store copy ahead of the app: nothing");
        var later = new ExtensionNotice(settings, () => { }, "1.11.0", () => now.AddHours(1));
        Check(later.Evaluate("1.10.0", Store, "Google Chrome") is null, "the store notice is limited to once a day across restarts (existing rule, now per browser)");
        notice.Mute("1.9.0"); Check(notice.Evaluate("1.9.0", Dev, "Vivaldi") is null, "'do not remind me about this version' still works");
        Check(notice.Evaluate("1.10.0", Dev, null) is { Browser: null } generic && generic.Message.Contains("Your browser extension"), "no browser given (old host): the general message, unchanged");
    }

    // ---------------------------------------------------------------- item 2: wire compatibility in both directions (mocked transport: component evidence)
    private static string Ask(string pipe, string json)
    {
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        client.Connect(3000);
        var bytes = Encoding.UTF8.GetBytes(json + "\n"); client.Write(bytes); client.Flush();
        var reply = new StringBuilder(); int b; var one = new byte[1];
        while (client.Read(one, 0, 1) == 1) { b = one[0]; if (b == 10) break; reply.Append((char)b); }
        return reply.ToString();
    }

    private static void WireCompat()
    {
        string pipe = "NDM.Downloads.v1110." + Guid.NewGuid().ToString("N");
        var captured = new List<NativeBridge.BrowserDownloadRequest>(); var seen = new List<NativeBridge.ExtensionSeen>();
        using var bridge = new NativeBridge(r => { lock (captured) captured.Add(r); r.Respond(new NativeBridge.BrowserDownloadResponse(true, "job-1")); }, pipe, null, s => { lock (seen) seen.Add(s); });
        bridge.Start();
        // new extension, new host, new app
        Check(Ask(pipe, "{\"url\":\"https://example.org/a.zip\",\"extVersion\":\"1.11.0\",\"browser\":\"Microsoft Edge\",\"extId\":\"" + Dev + "\"}").Contains("\"ok\":true"), "a capture with extVersion and browser is accepted");
        Check(captured.Count == 1 && captured[0].Browser == "Microsoft Edge" && captured[0].ExtVersion == "1.11.0" && captured[0].ExtId == Dev, "the app receives the browser name next to the version");
        // old extension (no new fields) against the new app
        Check(Ask(pipe, "{\"url\":\"https://example.org/b.zip\",\"extVersion\":\"1.4.3\"}").Contains("\"ok\":true") && captured[1].Browser is null, "an old extension without browser works: Browser stays null");
        Check(Ask(pipe, "{\"url\":\"https://example.org/c.zip\"}").Contains("\"ok\":true") && captured[2].Browser is null && captured[2].ExtVersion is null, "an extension without any version works");
        // validation in the app
        foreach (var bad in new[] { "\\\"<b>\\\"", "\\\"" + new string('x', 33) + "\\\"", "\\\" Edge\\\"", "7", "true", "[]", "{}" })
        {
            int before; lock (captured) before = captured.Count;
            string reply = Ask(pipe, "{\"url\":\"https://example.org/d.zip\",\"browser\":" + bad.Replace("\\\"", "\"") + "}");
            lock (captured) Check(captured.Count == before && !reply.Contains("\"ok\":true"), "a bad browser value is refused and starts nothing: " + bad);
        }
        Check(Ask(pipe, "{\"url\":\"https://example.org/e.zip\",\"browser\":\"\"}").Contains("\"ok\":true") && captured.Last().Browser is null or "", "an empty browser value is the same as none");
        captured.RemoveAt(captured.Count - 1);
        // YouTube handoff: browser allowed, other extra fields still refused (existing rule)
        Check(Ask(pipe, "{\"url\":\"https://www.youtube.com/watch?v=aqz-KE-bpKQ\",\"youtube\":true,\"extVersion\":\"1.11.0\",\"browser\":\"Brave\"}").Contains("\"ok\":true") && captured.Last().Browser == "Brave" && captured.Last().Youtube, "the YouTube handoff accepts the browser name");
        Check(!Ask(pipe, "{\"url\":\"https://www.youtube.com/watch?v=aqz-KE-bpKQ\",\"youtube\":true,\"cookie\":\"a=b\"}").Contains("\"ok\":true"), "the YouTube handoff still refuses other extra fields");
        // the app says it understands the field, so a new host can forward it (an old app does not say so)
        Check(Ask(pipe, "{\"action\":\"selfcheck\"}").Contains("\"browser\":true"), "the self check reports that the browser field is understood");
        // extension-seen: a ping forwarded by the host
        Check(Ask(pipe, "{\"action\":\"extension-seen\",\"browser\":\"Brave\",\"extVersion\":\"1.10.0\",\"extId\":\"" + Store + "\"}").Contains("\"ok\":true"), "extension-seen is answered");
        Check(seen.Count == 1 && seen[0].Browser == "Brave" && seen[0].ExtVersion == "1.10.0" && seen[0].ExtId == Store, "the app learns the browser and version from a ping");
        Ask(pipe, "{\"action\":\"extension-seen\",\"browser\":\"<x>\",\"extVersion\":\"bad\",\"extId\":\"nope\"}");
        Check(seen.Count == 2 && seen[1].Browser is null && seen[1].ExtVersion is null && seen[1].ExtId is null, "bad fields in a ping are dropped, not trusted");
        Ask(pipe, "{\"action\":\"extension-seen\"}"); Check(seen.Count == 3 && seen[2] == new NativeBridge.ExtensionSeen(null, null, null), "a ping with no fields (old extension) is harmless");
        Check(captured.Count == 4, "a ping never creates a capture or a dialog");
        // a bridge without the callback (an app built before the field) still answers
        string bare = pipe + ".bare";
        using var plain = new NativeBridge(_ => { }, bare); plain.Start();
        Check(Ask(bare, "{\"action\":\"extension-seen\",\"browser\":\"Brave\"}").Contains("\"ok\":true"), "without a handler the ping is still answered");
        Check(NativeBridge.ValidBrowserName("Google Chrome") && !NativeBridge.ValidBrowserName("") && !NativeBridge.ValidBrowserName("A\tB") && !NativeBridge.ValidBrowserName("Ünï"), "name rule: printable ASCII letters, digits, space . - _");
    }

    // ---------------------------------------------------------------- item 2: the hint in the capture dialog
    private static void CaptureHint(string scratch)
    {
        string files = Path.Combine(scratch, "dlg"); Directory.CreateDirectory(files);
        var dialog = new DownloadOptionsDialog("https://example.org/files/a.zip", "a.zip", null, null, files) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
        dialog.Show(); Pump(120);
        Check(dialog.ExtensionHintText == "", "no hint while the extension matches");
        var check = ExtensionVersionCheck.Compare("1.10.0", "1.11.0", ExtensionChannel.Development, "Brave");
        dialog.ShowExtensionHint(check); Pump(80);
        var hint = (TextBlock)dialog.FindName("ExtensionHint");
        Check(hint.Visibility == Visibility.Visible && hint.Text.Contains("Brave") && hint.Text.Contains("outdated"), "the footer hint names the outdated browser: " + hint.Text);
        var start = (Button)dialog.FindName("StartButton");
        Check(start.IsEnabled && hint.TranslatePoint(new Point(0, 0), dialog).Y < start.TranslatePoint(new Point(0, 0), dialog).Y, "the hint sits above the buttons and does not disable them");
        Check(hint.ActualWidth <= dialog.ActualWidth && hint.ActualHeight > 0, "the hint fits the dialog width");
        dialog.Width = 520; Pump(120); Check(hint.ActualWidth <= dialog.ActualWidth, "the hint wraps in a narrow dialog");
        dialog.FlowDirection = FlowDirection.RightToLeft; Pump(80); Check(hint.ActualWidth > 0, "the hint is laid out in a right-to-left dialog");
        dialog.Close();
        // the notice window never takes the focus
        var window = ExtensionNoticeWindow.Show(check, _ => { }); Pump(150);
        Check(!window.IsActive || !window.ShowActivated, "the standalone notice does not activate itself (ShowActivated is off)");
        Check(window.NoticeBanner.Message.Contains("Brave"), "the standalone notice names the browser");
        window.Close();
    }

    // ---------------------------------------------------------------- item 2: Options > General rows
    private static AppSettings SeededSettings(DateTimeOffset now) => new()
    {
        BrowserExtensions = new()
        {
            new() { Browser = "Google Chrome", Version = AboutInfo.Version, LastSeen = now.AddMinutes(-2), Channel = ExtensionChannel.Store },
            new() { Browser = "Microsoft Edge", Version = "1.11.0", LastSeen = now.AddHours(-3), Channel = ExtensionChannel.Development },
            new() { Browser = "Brave", Version = null, LastSeen = now.AddDays(-2), Channel = ExtensionChannel.Unknown },
            new() { Browser = "Vivaldi", Version = "9.0.0", LastSeen = now.AddSeconds(-5), Channel = ExtensionChannel.Development },
        },
    };

    private static SettingsWindow NewOptions(AppSettings s, string dir, DateTimeOffset now, double width = 1000, double height = 760)
    {
        var w = new SettingsWindow(s, dir) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Width = width, Height = height };
        w.Show(); Pump(150); return w;
    }

    private static void OptionsRows(string scratch)
    {
        string dir = Path.Combine(scratch, "opt", "NDM"); Directory.CreateDirectory(dir);
        var now = DateTimeOffset.UtcNow;
        var empty = new SettingsWindow(new AppSettings(), dir) { ShowInTaskbar = false }; empty.Show(); Pump(100);
        Check(((TextBlock)empty.FindName("BrowserExtensionEmpty")).Visibility == Visibility.Visible && ((StackPanel)empty.FindName("BrowserExtensionList")).Children.Count == 0, "no browser seen: an explanation, no rows");
        empty.Close();
        var w = NewOptions(SeededSettings(now), dir, now);
        var rows = w.BrowserExtensionRows;
        Check(rows.Count == 4 && ((TextBlock)w.FindName("BrowserExtensionEmpty")).Visibility == Visibility.Collapsed, "four browsers, four rows");
        Check(rows[0].Browser == "Vivaldi" && rows[1].Browser == "Google Chrome", "newest sighting first");
        var chrome = rows.Single(r => r.Browser == "Google Chrome"); var edge = rows.Single(r => r.Browser == "Microsoft Edge"); var brave = rows.Single(r => r.Browser == "Brave"); var viv = rows.Single(r => r.Browser == "Vivaldi");
        Check(chrome.Ok && chrome.StatusText == "Up to date." && chrome.FixText == "" && chrome.LastSeenText == "Last seen 2 min ago.", "Chrome: up to date, last seen 2 min ago");
        Check(edge.Status == ExtensionStatus.Older && edge.StatusText.Contains("Outdated (app version") && edge.FixText.Contains("reload the unpacked extension") && edge.LastSeenText.Contains("3 h ago"), "Edge: outdated, manual fix hint");
        Check(brave.Status == ExtensionStatus.Missing && brave.VersionText == "version unknown" && brave.LastSeenText.Contains("2 d ago") && brave.StatusText.Contains("no version reported"), "Brave: no version counts as outdated");
        Check(viv.Status == ExtensionStatus.Newer && viv.FixText.Contains("Update NeoDM") && viv.LastSeenText.Contains("just now"), "Vivaldi: newer than the app says to update NeoDM");
        var list = (StackPanel)w.FindName("BrowserExtensionList");
        Check(list.Children.Count == 4, "the page shows one line per row");
        var tabs = (TabControl)w.FindName("OptionsTabs"); tabs.SelectedIndex = 0; Pump(60);
        var boxes = Visual<TextBox>(list).ToList();
        Check(boxes.Count >= 4 && boxes.All(b => b.IsReadOnly), "the values are selectable, read-only text");
        Check(list.Children.OfType<Grid>().All(g => System.Windows.Automation.AutomationProperties.GetName(g).Contains(":")), "each line has a spoken summary");
        // sizes and RTL
        foreach (var (width, height) in new[] { (560.0, 520.0), (760.0, 600.0), (1400.0, 900.0) })
        {
            w.Width = width; w.Height = height; Pump(120);
            double right = list.TranslatePoint(new Point(list.ActualWidth, 0), w).X;
            Check(list.ActualWidth > 100 && right <= w.ActualWidth + 0.5 && boxes.All(b => b.ActualWidth <= list.ActualWidth + 0.5), $"rows stay inside the window at {width}x{height}");
        }
        w.FlowDirection = FlowDirection.RightToLeft; Pump(120);
        var first = list.Children.OfType<Grid>().First();
        var dot = (System.Windows.Shapes.Ellipse)first.Children[0]; var text = (FrameworkElement)first.Children[1];
        Check(dot.TranslatePoint(new Point(0, 0), w).X > text.TranslatePoint(new Point(0, 0), w).X, "right-to-left: the status dot is on the right of the text");
        w.Close();
    }

    private static double Lum(Color c) { double f(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); } return 0.2126 * f(c.R) + 0.7152 * f(c.G) + 0.0722 * f(c.B); }
    private static double Ratio(Color a, Color b) { double x = Lum(a), y = Lum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

    private static void OptionsThemes(string scratch)
    {
        string dir = Path.Combine(scratch, "optthemes", "NDM"); Directory.CreateDirectory(dir);
        var now = DateTimeOffset.UtcNow;
        var w = NewOptions(SeededSettings(now), dir, now, 900, 700);
        var list = (StackPanel)w.FindName("BrowserExtensionList");
        foreach (var theme in ThemeCatalog.All.Where(t => t.Id != ThemeCatalog.SystemId))
        {
            ThemeManager.Apply(Application.Current, theme.Id, null); Pump(120);
            var surface = ((SolidColorBrush)w.FindResource("SurfaceBrush")).Color;
            var texts = Visual<TextBox>(list).ToList();
            Check(texts.Count >= 4, theme.Id + ": rows exist");
            foreach (var t in texts)
            {
                var fg = ((SolidColorBrush)t.Foreground).Color;
                Check(Ratio(fg, surface) >= 4.5, $"{theme.Id}: text '{t.Text.Substring(0, Math.Min(18, t.Text.Length))}' has 4.5:1 contrast on the page ({Ratio(fg, surface):0.0})");
            }
            foreach (var dot in Visual<System.Windows.Shapes.Ellipse>(list))
                Check(Ratio(((SolidColorBrush)dot.Fill).Color, surface) >= 3.0, $"{theme.Id}: status dot has 3:1 contrast on the page");
            var render = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32); render.Render(w);
            Check(render.PixelWidth > 100, theme.Id + ": the options page renders");
        }
        ThemeManager.Apply(Application.Current, "Light", null);
        w.Close();
    }

    // ---------------------------------------------------------------- item 2: localisation
    private static void LocaleKeys()
    {
        string dir = Path.Combine(RepoRoot, "src", "NDM.App", "Locales");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "en.json")))!;
        var keys = en.Keys.Where(k => k.StartsWith("extver.browser_") || k.StartsWith("extver.fix_") || k.StartsWith("extver.row.") || k.StartsWith("options.text.extension_in_your_browsers") || k == "options.text.extension_none_seen").ToList();
        Check(keys.Count >= 20, "the new keys exist in English (" + keys.Count + ")");
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
            Check(keys.All(k => data.TryGetValue(k, out var v) && v.Length > 0), Path.GetFileName(file) + " has every new key");
            if (!file.EndsWith("en.json")) Check(keys.Where(k => k.StartsWith("extver.browser_")).All(k => data[k] != en[k]), Path.GetFileName(file) + " is translated, not copied");
        }
        Check(Directory.GetFiles(dir, "*.json").Length == 10, "the ten shipped locales");
    }
}
