// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>2.0.0 stage S4b-2: Report a problem (diagnostics zip, GitHub page, copy summary) and the crash note with its one-time offer.</summary>
internal static partial class V2000Tests
{
    private static readonly string[] SecretBits = { "hunter2pass", "bobproxyuser", "sid=ABCDEF123", "SECRETFILE-NAME", "evil.example", "10.20.30.40", "tok_9f8e7d6c5b4a39281716151413121110abcdef", "alice@mail.test", "magnetsecret" };

    private static string SeedState(string root)
    {
        var state = Path.Combine(root, "NDM"); Directory.CreateDirectory(Path.Combine(state, "thumbs")); Directory.CreateDirectory(Path.Combine(state, "torrent"));
        var user = Environment.UserName; var machine = Environment.MachineName;
        var lines = new List<string>();
        for (int i = 1; i <= 5000; i++) lines.Add(i == 4999 ? "watch 4999 GET https://evil.example/dl?sig=zzz Cookie: sid=ABCDEF123; other=1 proxy http://bobproxyuser:hunter2pass@10.20.30.40:8080 file C:\\Users\\" + user + "\\Downloads\\SECRETFILE-NAME.zip mail alice@mail.test " + machine : "watch line " + i);
        File.WriteAllLines(Path.Combine(state, "bridge-watchdog.log"), lines);
        File.WriteAllLines(Path.Combine(state, "thumbs", "trace.log"), new[] { "thumb ok https://evil.example/pic.jpg 200", "thumb fail tok_9f8e7d6c5b4a39281716151413121110abcdef" });
        File.WriteAllLines(Path.Combine(state, "torrent", "engine.log"), new[] { "added magnet:?xt=urn:btih:magnetsecret&dn=SECRETFILE-NAME", "dht ok" });
        File.WriteAllText(Path.Combine(state, "queue.json"), "[{\"Id\":\"a\",\"Status\":\"Downloading\",\"Request\":{\"Url\":\"https://evil.example/secret.bin?sig=1\"},\"CookieProtected\":\"sid=ABCDEF123\"},{\"Id\":\"b\",\"Status\":\"Completed\",\"Request\":{\"Url\":\"https://evil.example/x\"}},{\"Id\":\"c\",\"Status\":\"Failed\"}]");
        File.WriteAllText(Path.Combine(state, "queues.json"), "{\"Queues\":[{\"Id\":\"main\",\"Name\":\"Main\"},{\"Id\":\"n\",\"Name\":\"Night\",\"Schedule\":{\"Enabled\":true},\"Hold\":true}]}");
        return state;
    }

    private static Dictionary<string, string> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); });
    }

    private static void ReportZip(string scratch)
    {
        var root = Path.Combine(scratch, "rep-" + Guid.NewGuid().ToString("N")); var state = SeedState(root); var desktop = Path.Combine(root, "Desktop");
        var settings = new AppSettings { DownloadDirectory = @"C:\Users\" + Environment.UserName + @"\Downloads", ProxyType = ProxyKind.Http, ProxyHost = "10.20.30.40", ProxyUsername = "bobproxyuser", RunProgramPath = @"C:\Users\" + Environment.UserName + @"\evil.exe", FinishChecksAutomatically = false };
        settings.SetProxyPassword("hunter2pass"); settings.RecentPackageNames.Add("SECRETFILE-NAME"); settings.TorrentExtraTrackers = new() { "udp://evil.example:80" };
        settings.BrowserExtensions.Add(new BrowserExtensionRecord { Browser = "Chrome", Version = "2.0.0" });
        // an old crash note with secrets in it goes through the same cleaner
        CrashLog.Clock = () => new DateTime(2026, 11, 18, 12, 0, 0);
        CrashLog.Record(state, new InvalidOperationException("failed https://evil.example/a?sig=1 for C:\\Users\\" + Environment.UserName + "\\f.zip Cookie: sid=ABCDEF123"), "test");

        var result = DiagnosticsReport.Build(new ReportInput(settings, state, desktop, "2.0.0", new DateTime(2026, 11, 18, 12, 30, 0), null, true));
        Check(File.Exists(result.ZipPath) && Path.GetDirectoryName(result.ZipPath) == desktop && Path.GetFileName(result.ZipPath).StartsWith("NeoDM-report-"), "the zip is on the Desktop");
        var files = ReadZip(result.ZipPath);
        Check(files.ContainsKey("summary.txt") && files.ContainsKey("settings.json") && files.ContainsKey("queues.txt") && files.ContainsKey("README.txt") && files.Keys.Any(k => k.StartsWith("logs/bridge")) && files.Keys.Any(k => k.StartsWith("logs/thumb")) && files.Keys.Any(k => k.StartsWith("logs/torrent")) && files.Keys.Any(k => k.StartsWith("crash/crash-")), "summary, settings, queue counts, watchdog, thumbnail and torrent traces and the crash note are in it");
        var all = string.Join("\n", files.Select(f => f.Key + "\n" + f.Value));
        foreach (var bit in SecretBits) Check(!all.Contains(bit, StringComparison.OrdinalIgnoreCase), "the zip does not contain: " + (bit.Length > 14 ? bit[..14] + "…" : bit));
        Check(!all.Contains("://") && !all.Contains("magnet:") && !all.Contains("Cookie: sid", StringComparison.OrdinalIgnoreCase) && !all.Contains("sig=1"), "no link, cookie or signature in the zip");
        Check(!all.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase) && !all.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase), "no user name and no machine name in the zip");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(all, @"[A-Za-z]:\\[A-Za-z]"), "no folder path in the zip");
        Check(files["settings.json"].Contains("\"FinishChecksAutomatically\": false") && files["settings.json"].Contains("\"ProxyType\": \"Http\"") && files["settings.json"].Contains("\"ProxyPasswordProtected\": \"<removed>\""), "settings are there, secrets replaced");
        Check(files["summary.txt"].Contains("NeoDM 2.0.0") && files["summary.txt"].Contains("build " + Environment.OSVersion.Version.Build) && files["summary.txt"].Contains("Chrome 2.0.0"), "version, Windows build and the extension version are in the summary");
        Check(files["queues.txt"].Contains("total: 3") && files["queues.txt"].Contains("Downloading: 1") && files["queues.txt"].Contains("Queues: 2, with a schedule: 1, held: 1"), "the queue summary counts downloads and queues");
        var wd = files.First(f => f.Key.StartsWith("logs/bridge")).Value;
        Check(wd.Contains("watch line 5000") && !wd.Contains("watch line 1\r\n") && wd.Split("\r\n").Length <= DiagnosticsReport.TailLines + 1 && wd.Contains("<link>"), "only the last log lines are kept, cleaned");
        // the link
        Check(result.IssueUrl.StartsWith("https://github.com/unlimitedera/NeoDM/issues/new?template=bug.yml&title=") && result.IssueUrl.Contains("&environment=") && result.IssueUrl.Length < 6000 && Uri.IsWellFormedUriString(result.IssueUrl, UriKind.Absolute), "the GitHub link carries the template, a title and the environment");
        var env = Uri.UnescapeDataString(result.IssueUrl[(result.IssueUrl.IndexOf("&environment=") + 13)..]);
        Check(env.StartsWith("NeoDM 2.0.0") && !env.Contains("://") && result.IssueUrl.Contains("closed%20unexpectedly"), "the environment block in the link is the clean summary; the title tells about the unexpected close");
        var yml = File.ReadAllText(Path.Combine(FindRepo(), ".github", "ISSUE_TEMPLATE", "bug.yml"));
        Check(yml.Contains("id: environment") && yml.Contains("type: textarea") && yml.Contains("name:"), "the issue form has an environment box for the link to fill");
        // a second report in the same second gets its own name
        var second = DiagnosticsReport.Build(new ReportInput(settings, state, desktop, "2.0.0", new DateTime(2026, 11, 18, 12, 30, 0)));
        Check(second.ZipPath != result.ZipPath && File.Exists(result.ZipPath), "a second report never overwrites the first");
        // the cleaner on its own
        var sample = Scrubber.Scrub("GET https://a.example.com/x?y=1 Set-Cookie: a=b; Path=/\npassword=abc token: xyz C:\\Program Files\\Foo\\bar.dll\nat NDM.App.MainWindow.Foo() in C:\\src\\x.cs:line 12\n\\\\srv\\share\\f.txt user:pw@host.example:81 www.site.org/p", new[] { "zzz" });
        Check(!sample.Contains("example.com") && !sample.Contains("a=b") && !sample.Contains("abc") && !sample.Contains("xyz") && !sample.Contains("Program Files") && !sample.Contains("srv") && !sample.Contains("pw@") && !sample.Contains("site.org") && sample.Contains("NDM.App.MainWindow.Foo()") && sample.Contains(":line 12"), "the cleaner removes links, cookies, secrets and paths and keeps stack frames: " + sample);
    }

    private static string FindRepo() { var d = AppContext.BaseDirectory; while (d is not null && !File.Exists(Path.Combine(d, "Directory.Build.props"))) d = Path.GetDirectoryName(d); return d ?? throw new Exception("repo not found"); }

    private static void ReportWindowUi(string scratch)
    {
        var root = Path.Combine(scratch, "repw-" + Guid.NewGuid().ToString("N")); var state = SeedState(root); var desktop = Path.Combine(root, "Desktop");
        var settings = new AppSettings(); var opened = new List<string>(); var oldOpen = UrlOpener.Open; var oldClip = SafeClipboard.Write; string? clip = null;
        UrlOpener.Open = u => opened.Add(u); SafeClipboard.Write = t => clip = t;
        try
        {
            var w = new ReportWindow(settings, state) { Desktop = desktop, ShowInTaskbar = false, Left = 40, Top = 40 }; w.Show(); Pump(200);
            Check(opened.Count == 0 && !Directory.Exists(desktop), "opening the window sends and writes nothing");
            ((Button)w.FindName("CopyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100);
            Check(clip is not null && clip.StartsWith("NeoDM ") && !clip.Contains("://") && !Directory.Exists(desktop) && opened.Count == 0, "Copy summary copies clean text, makes no file and opens nothing");
            ((Button)w.FindName("CreateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(300);
            Check(opened.Count == 1 && opened[0].StartsWith("https://github.com/unlimitedera/NeoDM/issues/new?template=bug.yml") && Directory.GetFiles(desktop, "NeoDM-report-*.zip").Length == 1, "Create report makes the zip and opens the GitHub page once");
            Check(((TextBlock)w.FindName("State")).Text.Contains("NeoDM-report-") && ((Button)w.FindName("ShowFileButton")).Visibility == Visibility.Visible, "the window says where the file is");
            w.Close();
            // a Desktop that cannot be written
            var blocker = Path.Combine(root, "blocker"); File.WriteAllText(blocker, "x");
            var w2 = new ReportWindow(settings, state) { Desktop = Path.Combine(blocker, "sub"), ShowInTaskbar = false }; w2.Show(); Pump(100); opened.Clear();
            w2.Create(); Pump(100);
            Check(((TextBlock)w2.FindName("State")).Text == Loc.T("report.failed") && opened.Count == 0, "a Desktop that cannot be written gives a plain message and opens nothing");
            w2.Close();
            // the window in the crash case, and the entry points
            var w3 = new ReportWindow(settings, state, true) { ShowInTaskbar = false }; w3.Show(); Pump(100);
            Check(((TextBlock)w3.FindName("CrashNote")).Visibility == Visibility.Visible, "after a crash the window says the note is included"); w3.Close();
            var main = new MainWindow(new DownloadManager(Path.Combine(root, "f"), Path.Combine(root, "m"), new DownloadOptions()), settings, () => { }, enableTray: false) { ShowInTaskbar = false }; main.Show(); Pump(200);
            var item = (MenuItem)main.FindName("ReportMenuItem");
            Check(item.Header is string h && h == Loc.T("main.menu.report_problem"), "Help has Report a problem");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(100); // test mode: no window, no error
            main.Close();
            var about = new AboutWindow { ShowInTaskbar = false }; about.Show(); Pump(150);
            Check(((Button)about.FindName("ReportButton")).Content is string, "About has a Report a problem button"); about.Close();
        }
        finally { UrlOpener.Open = oldOpen; SafeClipboard.Write = oldClip; }
    }

    private static void CrashNotes(string scratch)
    {
        var root = Path.Combine(scratch, "crash-" + Guid.NewGuid().ToString("N")); var state = Path.Combine(root, "NDM"); Directory.CreateDirectory(state);
        var now = new DateTime(2026, 11, 18, 9, 0, 0); CrashLog.Clock = () => now;
        Exception Deep(int n) => n == 0 ? new InvalidOperationException(new string('m', 50_000) + " https://evil.example/a?sig=1 C:\\Users\\" + Environment.UserName + "\\x.zip") : new Exception("level " + n, Deep(n - 1));
        var file = CrashLog.Record(state, Deep(10), "test");
        Check(file is not null && new FileInfo(file).Length <= 16 * 1024 + 4, "the crash note stays small even for a huge message and a deep chain");
        var text = File.ReadAllText(file!);
        Check(!text.Contains("evil.example") && !text.Contains("sig=1") && !text.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase) && text.Contains("NeoDM 2.0.0 closed unexpectedly") && text.Contains("Windows:"), "it names the version and Windows, with links and the user name removed");
        Check(CrashLog.Record(state, Deep(10), "again") is null && CrashLog.Files(state).Count() == 1, "the same crash reported by two handlers is written once");
        var first = CrashLog.TakePending(state);
        Check(first is not null && first.File == Path.GetFileName(file) && CrashLog.TakePending(state) is null, "the offer is taken once");
        now = now.AddMinutes(3);
        Check(CrashLog.Record(state, new Exception("loop 1"), "loop") is not null && CrashLog.TakePending(state) is null, "a crash soon after the offer is noted but not offered again (crash loop)");
        now = now.AddMinutes(20);
        CrashLog.Record(state, new Exception("later"), "later");
        Check(CrashLog.TakePending(state) is not null, "a later crash is offered again");
        for (int i = 0; i < 12; i++) { now = now.AddMinutes(1); CrashLog.Record(state, new Exception("n" + i), "many"); }
        Check(CrashLog.Files(state).Count() == CrashLog.MaxFiles, "old notes are removed: at most " + CrashLog.MaxFiles);
        File.WriteAllText(Path.Combine(state, "crash", "pending.json"), "{not json"); Check(CrashLog.TakePending(state) is null && !File.Exists(Path.Combine(state, "crash", "pending.json")), "a damaged mark is ignored and removed");

        // the offer at the next start
        var state2 = Path.Combine(root, "NDM2"); Directory.CreateDirectory(state2); var asked = new List<(string Message, string[] Labels)>();
        ChoiceWindow.Override = (_, _, message, labels) => { asked.Add((message, labels)); return 1; };
        try
        {
            Check(!CrashOffer.Run(null, new AppSettings(), state2) && asked.Count == 0, "no crash, no question");
            CrashLog.Record(state2, new Exception("boom"), "t");
            Check(CrashOffer.Run(null, new AppSettings(), state2) && asked.Count == 1 && asked[0].Message == "NeoDM closed unexpectedly. Report a problem?" && asked[0].Labels.Length == 2, "after a crash NeoDM asks 'NeoDM closed unexpectedly. Report a problem?'");
            Check(!CrashOffer.Run(null, new AppSettings(), state2) && asked.Count == 1, "and asks only once");
        }
        finally { ChoiceWindow.Override = null; }
        Check(new[] { "ar", "de", "en", "es", "fr", "ja", "pt-BR", "ru", "tr", "zh-Hans" }.All(l => { Loc.SetLanguage(l); return new[] { "report.title", "report.intro", "report.contents", "report.btn.create", "report.btn.copy", "report.btn.show", "report.done", "report.failed", "report.copied", "report.crash_note", "crash.message", "crash.report", "crash.later", "main.menu.report_problem", "about.btn.report" }.All(k => Loc.T(k) != k); }), "the report and crash texts exist in ten languages");
        Loc.SetLanguage("en"); CrashLog.Clock = () => DateTime.Now;
    }

    /// <summary>A real process that really dies: the child installs the handlers and then fails on a worker thread, or on the window thread; the parent reads what it left behind.</summary>
    private static void CrashReal(string scratch)
    {
        foreach (var mode in new[] { "thread", "window" })
        {
            var state = Path.Combine(scratch, "real-" + mode + "-" + Guid.NewGuid().ToString("N"), "NDM"); Directory.CreateDirectory(state);
            var psi = new ProcessStartInfo(Environment.ProcessPath!, "--crash-child " + mode + " \"" + state + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.Environment["NDM_STATE_DIR"] = state;
            using var p = Process.Start(psi)!; var err = p.StandardError.ReadToEndAsync(); p.StandardOutput.ReadToEnd();
            Check(p.WaitForExit(30000), "the child process ends by itself (" + mode + ")");
            var files = CrashLog.Files(state).ToList();
            Check(p.ExitCode != 0 && files.Count == 1, "the child died and left one crash note (" + mode + ") exit " + p.ExitCode + " notes " + files.Count);
            var text = File.ReadAllText(files[0]);
            Check(text.Contains("boom from " + mode) && !text.Contains("evil.example") && text.Contains("InvalidOperationException"), "the note holds the error type and message, cleaned (" + mode + ")");
            Check(CrashLog.TakePending(state) is not null, "the next start would offer to report it (" + mode + ")");
        }
    }

    private static int CrashChild(string[] args)
    {
        var mode = args[1]; var state = args[2];
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        CrashLog.Install(state, app);
        var error = new InvalidOperationException("boom from " + mode + " https://evil.example/x?sig=2");
        if (mode == "thread") { var t = new Thread(() => throw error); t.Start(); t.Join(); Thread.Sleep(3000); return 0; }
        app.Dispatcher.BeginInvoke(new Action(() => throw error)); app.Run(); return 0;
    }
}
