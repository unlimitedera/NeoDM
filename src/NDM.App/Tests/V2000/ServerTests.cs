// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>2.0.0 stage S3: the Server row (country names and flags in ten languages, hidden when unknown, proxy, themes, RTL, narrow) and the optional list column.</summary>
internal static partial class V2000Tests
{
    private static readonly string[] Langs = { "ar", "de", "en", "es", "fr", "ja", "pt-BR", "ru", "tr", "zh-Hans" };

    private static void ServerData()
    {
        var table = IpCountry.Default!;
        var noName = table.Codes.Where(c => ServerInfo.CountryName(c, "en") == c).ToArray();
        Check(noName.Length == 0, "every country has an English name: " + string.Join(",", noName));
        var noFlag = table.Codes.Where(c => FlagImages.Get(c) is null).ToArray();
        Check(noFlag.Length == 0, "every country has a flag picture: " + string.Join(",", noFlag));
        var flag = (BitmapSource)FlagImages.Get("DE")!;
        Check(flag.PixelWidth == 48 && flag.PixelHeight == 36 && flag.IsFrozen, "a flag is a small frozen 48 x 36 picture");
        Check(FlagImages.Get("zz") is null && FlagImages.Get(null) is null, "an unknown code has no flag");
        var names = Langs.ToDictionary(l => l, l => ServerInfo.CountryName("DE", l));
        Check(names["en"] == "Germany" && names["de"] == "Deutschland" && names["ja"] == "\u30C9\u30A4\u30C4" && names["zh-Hans"] == "\u5FB7\u56FD" && names["fr"] == "Allemagne" && names["es"] == "Alemania" && names["tr"] == "Almanya" && names["pt-BR"] == "Alemanha" && names["ar"].Length > 2 && names["ru"].Length > 2, "Germany in ten languages: " + string.Join(" | ", names.Values));
        Check(table.Codes.All(c => Langs.All(l => ServerInfo.CountryName(c, l).Length > 0 && ServerInfo.CountryName(c, l) != c)), "every country has a name in every language");
        Check(ServerInfo.CountryName("DE", "xx") == "Germany", "an unknown language falls back to English");

        try
        {
            Loc.SetLanguage("en");
            ServerInfo.LookupOverride = a => a == "8.8.8.8" ? "US" : null;
            var fast = ServerInfo.Build("dns.test", "8.8.8.8", 12, false, null)!;
            Check(fast.Code == "US" && fast.Speed == ServerSpeed.Fast && fast.Text == "United States of America, about 12 ms (fast)", "fast: " + fast.Text);
            Check(ServerInfo.Build("dns.test", "8.8.8.8", 120, false, null)!.Text.EndsWith("about 120 ms (medium)") && ServerInfo.Build("dns.test", "8.8.8.8", 800, false, null)!.Text.EndsWith("about 800 ms (slow)"), "medium and slow words");
            Check(fast.BrushKey == "StatusCompletedBrush" && ServerInfo.Build("h", "8.8.8.8", 150, false, null)!.BrushKey == "StatusPausedBrush" && ServerInfo.Build("h", "8.8.8.8", 250, false, null)!.BrushKey == "StatusFailedBrush", "green, amber, red theme tokens");
            Check(ServerInfo.Build("dns.test", "8.8.8.8", null, false, null)!.Text == "United States of America", "no measured time: the country only");
            Check(ServerInfo.Build("h", "10.0.0.5", 20, false, null) is null && ServerInfo.Build("h", null, 20, false, null) is null && ServerInfo.Build(null, "8.8.8.8", 20, false, null) is null, "hidden when the address is unknown, private or missing");
            var proxy = ServerInfo.Build("dns.test", null, 40, true, "127.0.0.1:8080")!;
            Check(proxy.ViaProxy && proxy.Code is null && proxy.Text.Contains("127.0.0.1:8080") && proxy.Text.Contains("about 40 ms"), "a proxied download shows the proxy, not a country: " + proxy.Text);
            Loc.SetLanguage("de");
            var german = ServerInfo.Build("h", "8.8.8.8", 12, false, null)!.Text;
            Check(german.StartsWith("Vereinigte Staaten") && german.Contains("schnell"), "German: " + german);
            Loc.SetLanguage("ar");
            Check(ServerInfo.Build("h", "8.8.8.8", 12, false, null)!.Latency!.Contains('\u202A'), "in Arabic the number part is kept left to right");
        }
        finally { ServerInfo.LookupOverride = null; Loc.SetLanguage("en"); }
        Check(ServerInfo.Build("h", "8.8.8.8", 30, false, null) is { Code: "US" }, "without the override the bundled table answers");
    }

    private sealed class RangeServer : IDisposable
    {
        public readonly byte[] Payload;
        private readonly LoopbackHttpServer server;
        public string Url => server.BaseUrl + "/file.bin";
        /// <summary>Extra header lines of every answer (a Link header naming mirrors).</summary>
        public List<KeyValuePair<string, string>> Extra = new();
        /// <summary>Wait this long before answering a request for the bytes (not the first-byte check), to keep a download running.</summary>
        public int SegmentDelayMs;
        public RangeServer(int size = 300_000, int seed = 5)
        {
            Payload = new byte[size];
            new Random(seed).NextBytes(Payload);
            server = new LoopbackHttpServer(r =>
            {
                var range = r.Header("Range");
                var headers = new List<KeyValuePair<string, string>> { new("ETag", "\"v1\""), new("Accept-Ranges", "bytes") };
                headers.AddRange(Extra);
                if (SegmentDelayMs > 0 && range is not null && range != "bytes=0-0") Thread.Sleep(SegmentDelayMs);
                if (range is not null && System.Text.RegularExpressions.Regex.Match(range, @"^bytes=(\d+)-(\d+)$") is { Success: true } m)
                {
                    long a = long.Parse(m.Groups[1].Value), b = Math.Min(long.Parse(m.Groups[2].Value), Payload.Length - 1);
                    headers.Add(new("Content-Range", $"bytes {a}-{b}/{Payload.Length}"));
                    return new LoopbackResponse(206, "application/octet-stream", Payload[(int)a..(int)(b + 1)], headers);
                }
                return new LoopbackResponse(200, "application/octet-stream", Payload, headers);
            });
        }
        public void Dispose() => server.Dispose();
    }

    private static string ServerRoot(string scratch) { var d = Path.Combine(scratch, "srv-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    private static T FindNamed<T>(FrameworkElement root, string name) where T : class => (T)root.FindName(name)!;
    private static void WaitDone(DownloadManager manager) { var spin = System.Diagnostics.Stopwatch.StartNew(); while (manager.Jobs.Single().Status is not ("Completed" or "Failed") && spin.Elapsed < TimeSpan.FromSeconds(20)) Pump(50); }

    /// <summary>The Server row in the progress view: shown with flag, name and coloured answer time after a real download from the loopback; hidden when the country is unknown; rendered in several themes, right to left and narrow.</summary>
    private static void ServerRender(string scratch)
    {
        var root = ServerRoot(scratch);
        using var files = new RangeServer();
        using var manager = new DownloadManager(Path.Combine(root, "files"), Path.Combine(root, "state"), new DownloadOptions { MaxSegments = 2, MinimumSegmentSize = 65536, RetryCount = 0 });
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "files") };
        var job = manager.AddAsync(new DownloadRequest(files.Url, "server.bin")).GetAwaiter().GetResult();
        WaitDone(manager);
        Check(manager.Jobs.Single().Status == "Completed" && manager.Jobs.Single().ServerAddress == "127.0.0.1", "the real download finished and the socket address was noted: " + manager.Jobs.Single().Status + " " + manager.Jobs.Single().Error);
        try
        {
            ServerInfo.LookupOverride = a => a == "127.0.0.1" ? "DE" : null;
            foreach (var (theme, lang, width) in new[] { ("Light", "en", 640.0), ("Dark", "en", 640.0), ("Ocean", "de", 640.0), ("Light", "ar", 640.0), ("Dark", "ja", 360.0) })
            {
                Loc.SetLanguage(lang);
                Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/NeoDM;component/Themes/{theme}.xaml") };
                var view = new DownloadProgressView(manager, job.Id, null, settings);
                var window = new Window { Content = view, Width = width, Height = 560, Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual, FlowDirection = Loc.FlowDirection };
                window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
                window.Show(); Pump(500);
                var label = FindNamed<TextBlock>(view, "ServerLabel"); var panel = FindNamed<StackPanel>(view, "ServerPanel");
                var image = FindNamed<Image>(view, "ServerFlag"); var text = FindNamed<TextBlock>(view, "ServerText");
                var tag = $"{theme} {lang} {width}";
                Check(label.Visibility == Visibility.Visible && panel.Visibility == Visibility.Visible && label.Text == Loc.T("progress.text.server"), tag + ": the Server row is shown");
                var resume = FindNamed<TextBlock>(view, "ResumeText");
                Check(Grid.GetRow(panel) == Grid.GetRow(resume) + 1 && Grid.GetRow(label) == Grid.GetRow(resume) + 1, tag + ": the row sits right under Resume capability");
                var runs = text.Inlines.OfType<Run>().ToArray();
                var inline = string.Concat(runs.Select(r => r.Text));
                Check(inline.Contains(ServerInfo.CountryName("DE", lang)) && runs.Length == 3, tag + ": country name and answer time: " + inline);
                Check(runs.Last().Foreground is SolidColorBrush, tag + ": the answer time takes a theme colour");
                Check(image.Source is BitmapSource { PixelWidth: 48 } && image.ActualWidth == 20 && image.ActualHeight == 15, tag + ": the flag picture is shown");
                var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(window);
                var at = image.TranslatePoint(new Point(10, 7), window);
                var px = new byte[4]; bmp.CopyPixels(new Int32Rect((int)at.X, (int)at.Y, 1, 1), px, 4, 0);
                var bg = new byte[4]; bmp.CopyPixels(new Int32Rect(2, (int)at.Y, 1, 1), bg, 4, 0);
                Check(px[3] > 0 && !(px[0] == bg[0] && px[1] == bg[1] && px[2] == bg[2]), tag + ": the flag is painted");
                var left = panel.TranslatePoint(new Point(0, 0), window).X; var right = panel.TranslatePoint(new Point(panel.ActualWidth, 0), window).X;
                Check(panel.ActualWidth > 0 && Math.Min(left, right) >= 0 && Math.Max(left, right) <= window.ActualWidth + 1, tag + ": the row stays inside the window");
                if (lang == "ar") Check(view.FlowDirection == FlowDirection.RightToLeft && label.TranslatePoint(new Point(0, 0), window).X > panel.TranslatePoint(new Point(0, 0), window).X, tag + ": right to left puts the label on the right");
                window.Close(); Pump(50);
            }
            ServerInfo.LookupOverride = _ => null; Loc.SetLanguage("en");
            var hidden = new DownloadProgressView(manager, job.Id, null, settings);
            var w2 = new Window { Content = hidden, Width = 640, Height = 560, Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual }; w2.Show(); Pump(500);
            Check(FindNamed<TextBlock>(hidden, "ServerLabel").Visibility == Visibility.Collapsed && FindNamed<StackPanel>(hidden, "ServerPanel").Visibility == Visibility.Collapsed, "the Server row is hidden when the country is unknown");
            w2.Close();
        }
        finally { ServerInfo.LookupOverride = null; Loc.SetLanguage("en"); Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") }; }
        foreach (var lang in Langs)
        {
            Loc.SetLanguage(lang);
            foreach (var key in new[] { "progress.text.server", "server.latency", "server.fast", "server.medium", "server.slow", "server.via_proxy", "server.tip", "main.col.server", "options.chk.show_server_column" })
                if (Loc.T(key) == key) throw new Exception($"FAIL: {lang} is missing {key}");
        }
        Loc.SetLanguage("en");
        Check(true, "the Server texts exist in ten languages");
    }

    /// <summary>The optional Server column: off by default, shown by the setting, saved, and the Options checkbox.</summary>
    private static void ServerColumn(string scratch)
    {
        var root = ServerRoot(scratch);
        using var files = new RangeServer();
        using var manager = new DownloadManager(Path.Combine(root, "files"), Path.Combine(root, "state"), new DownloadOptions { MaxSegments = 1, RetryCount = 0 });
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "files") };
        Check(!settings.ShowServerColumn, "the column is off by default");
        manager.AddAsync(new DownloadRequest(files.Url, "col.bin")).GetAwaiter().GetResult();
        WaitDone(manager);
        try
        {
            ServerInfo.LookupOverride = a => a == "127.0.0.1" ? "FR" : null;
            var window = new MainWindow(manager, settings, () => { }, enableTray: false) { ShowInTaskbar = false, Width = 1100, Height = 700 };
            window.Show(); Pump(500);
            var column = (GridViewColumn)window.FindName("ServerColumn")!;
            Check(column.Width == 0, "hidden while the setting is off");
            settings.ShowServerColumn = true;
            typeof(MainWindow).GetMethod("UpdatePackageColumn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null); Pump(200);
            Check(column.Width > 0, "shown when the setting is on");
            Check(window.Rows.Single().ServerText.StartsWith("France"), "the cell text is the country and answer time: " + window.Rows.Single().ServerText);
            window.Close();
        }
        finally { ServerInfo.LookupOverride = null; }
        var dir = Path.Combine(root, "opt"); Directory.CreateDirectory(dir);
        new AppSettings { ShowServerColumn = true }.Save(dir);
        Check(AppSettings.Load(dir).ShowServerColumn, "the choice is saved");
        var opt = new SettingsWindow(AppSettings.Load(dir), dir) { Width = 720, Height = 760 }; opt.Show(); Pump(200);
        var box = (CheckBox)opt.FindName("ShowServerColumnBox")!;
        Check(box.IsChecked == true && !string.IsNullOrWhiteSpace(box.Content as string), "Options shows the checkbox with the saved value");
        opt.Close();
    }
}
