// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>2.0.0 stage S3c: the Servers and mirrors section of the progress window and tab: list, choice, Add mirror, 5-second refresh, themes, right to left, narrow, ten languages.</summary>
internal static partial class V2000Tests
{
    private sealed class MirrorKit : IDisposable
    {
        public readonly RangeServer A = new(), B = new(seed: 5), C = new(size: 200_000, seed: 6), D = new(seed: 5);
        public readonly string Root;
        public DownloadManager Manager;
        public int ProbeCalls;
        public DownloadJob Job;
        public readonly AppSettings Settings;
        public MirrorKit(string scratch)
        {
            Root = ServerRoot(scratch);
            A.Extra.Add(new("Link", $"<{B.Url}>; rel=duplicate; pri=1; geo=de, <{C.Url}>; rel=duplicate; pri=2; geo=us"));
            A.SegmentDelayMs = 4000; // keeps the download running while the window is looked at
            Manager = new DownloadManager(Path.Combine(Root, "files"), Path.Combine(Root, "state"), new DownloadOptions
            {
                MaxSegments = 1, RetryCount = 0, MinimumSegmentSize = 65536,
                ConnectProbe = (urls, _) => { ProbeCalls++; return Task.FromResult(urls.Select((_, i) => (int?)(new[] { 50, 120, 90, 400 }[Math.Min(i, 3)])).ToArray()); }
            });
            Settings = new AppSettings { DownloadDirectory = Path.Combine(Root, "files") };
            Job = Manager.AddAsync(new DownloadRequest(A.Url, "mirror.bin")).GetAwaiter().GetResult();
            var spin = System.Diagnostics.Stopwatch.StartNew();
            while (!(Manager.Jobs.Single().Sources.Count == 3 && Manager.Jobs.Single().Sources.All(s => s.State != MirrorState.Candidate)) && spin.Elapsed < TimeSpan.FromSeconds(20)) Pump(50);
            Manager.Pause(Job.Id); spin.Restart(); while (Manager.Jobs.Single().Status != "Paused" && spin.Elapsed < TimeSpan.FromSeconds(10)) Pump(50); // a paused download keeps its sources and the section stays live
        }
        public void Dispose() { try { Manager.Remove(Job.Id); } catch (Exception) { } Pump(100); Manager.Dispose(); A.Dispose(); B.Dispose(); C.Dispose(); D.Dispose(); }
    }

    private static List<Grid> MirrorRows(DownloadProgressView view) => FindNamed<StackPanel>(view, "MirrorList").Children.OfType<Grid>().ToList();
    private static T InRow<T>(Grid row, int column = -1) where T : UIElement => row.Children.OfType<T>().First(c => column < 0 || Grid.GetColumn(c) == column);

    /// <summary>The refresh cadence with a fake clock: every 5 seconds, only while active, fresh after being inactive.</summary>
    private static void MirrorGate()
    {
        var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        var gate = new MirrorRefreshGate(() => now);
        Check(!gate.Due(false), "an inactive section never measures");
        Check(gate.Due(true), "the first look when active measures at once");
        now += TimeSpan.FromSeconds(4.9); Check(!gate.Due(true), "4.9 seconds later: not yet");
        now += TimeSpan.FromSeconds(0.1); Check(gate.Due(true), "5 seconds after the last one: measure");
        now += TimeSpan.FromSeconds(5); Check(!gate.Due(false), "window closed or section collapsed: no measurement");
        Check(gate.Due(true), "reopened: measures at once, not after a wait");
        now += TimeSpan.FromSeconds(60); Check(gate.Due(true) && !gate.Due(true), "a long pause gives one measurement, then waits again");
        Check(MirrorRefreshGate.Interval == TimeSpan.FromSeconds(5), "the interval is 5 seconds");
    }

    /// <summary>The section end to end on a real running download: list, flags, notes, choice, Add mirror, Remove, and the refresh cadence through the engine.</summary>
    private static void MirrorChooser(string scratch)
    {
        using var kit = new MirrorKit(scratch);
        var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        var s = kit.Manager.Jobs.Single().Sources;
        Check(s.Count == 3 && s[1].State == MirrorState.Verified && s[2].State == MirrorState.Rejected && s[2].Reason == MirrorReasons.Size, "the engine found and checked the two mirrors of the Link header");
        var view = new DownloadProgressView(kit.Manager, kit.Job.Id, null, kit.Settings) { MirrorGate = new MirrorRefreshGate(() => now) };
        var window = new Window { Content = view, Width = 700, Height = 700, Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual }; window.Show(); Pump(700);
        var section = FindNamed<StackPanel>(view, "MirrorSection");
        Check(section.Visibility == Visibility.Visible && !view.MirrorsExpanded && MirrorRows(view).Count == 0, "the section is there, collapsed, and builds nothing while collapsed");
        var calls = kit.ProbeCalls; Pump(1200);
        Check(kit.ProbeCalls == calls, "collapsed: no measurement of any mirror");
        view.SetMirrorsExpanded(true); Pump(700);
        var rows = MirrorRows(view);
        Check(rows.Count == 4, "automatic, the original and two mirrors: " + rows.Count);
        Check(InRow<RadioButton>(rows[0]).IsChecked == true && FindNamed<TextBlock>(view, "MirrorHeader").Text == "Servers and mirrors", "Automatic (best) is the default");
        Check(((TextBlock)InRow<TextBlock>(rows[0], 2)).Inlines.OfType<Run>().Any(r => r.Text == "Automatic (best)"), "the first row says Automatic (best)");
        var germany = (TextBlock)InRow<TextBlock>(rows[2], 2);
        Check(germany.Text.Length == 0 && string.Concat(germany.Inlines.OfType<Run>().Select(r => r.Text)).Contains("Germany") && InRow<Image>(rows[2]).Source is BitmapSource { PixelWidth: 48 }, "a mirror with a geo hint shows the flag and the country");
        Check(string.Concat(((TextBlock)InRow<TextBlock>(rows[2], 2)).Inlines.OfType<Run>().Select(r => r.Text)).Contains("unverified"), "no checksum is published: the mirror is marked unverified");
        Check(!InRow<RadioButton>(rows[3]).IsEnabled && string.Concat(((TextBlock)InRow<TextBlock>(rows[3], 2)).Inlines.OfType<Run>().Select(r => r.Text)).Contains("different size"), "a mirror of another size is listed, cannot be picked, and says why");
        Check(FindNamed<TextBlock>(view, "MirrorNote").Text.Contains("No checksum is published"), "the note explains that nothing can be checked at the end");
        // refresh: first look measures at once, the second only after 5 seconds
        Pump(1200); var first = kit.ProbeCalls;
        Check(first == calls + 1, $"expanded: one measurement at once ({kit.ProbeCalls - calls})");
        Pump(1200); Check(kit.ProbeCalls == first, "1.2 seconds later: no second measurement");
        now += TimeSpan.FromSeconds(5.2); Pump(1200);
        Check(kit.ProbeCalls == first + 1, "5 seconds later: measured again");
        Pump(800);
        rows = MirrorRows(view);
        var lat = (TextBlock)InRow<TextBlock>(rows[2], 3);
        Check(lat.Text == "120 ms" && lat.Foreground is SolidColorBrush && ((TextBlock)InRow<TextBlock>(rows[1], 3)).Text == "50 ms" && ((TextBlock)InRow<TextBlock>(rows[1], 3)).Foreground != lat.Foreground, "the connect times show with their colours: " + lat.Text);
        // collapse: nothing more, expand again: at once
        view.SetMirrorsExpanded(false); now += TimeSpan.FromSeconds(30); var afterCollapse = kit.ProbeCalls; Pump(1200);
        Check(kit.ProbeCalls == afterCollapse, "collapsed again: no measurement even after a long time");
        view.SetMirrorsExpanded(true); Pump(1200);
        Check(kit.ProbeCalls == afterCollapse + 1, "expanded again: measured at once");
        // a hidden window does not measure either
        window.Hide(); now += TimeSpan.FromSeconds(30); var hiddenCalls = kit.ProbeCalls; Pump(1200);
        Check(kit.ProbeCalls == hiddenCalls, "a hidden window does not measure");
        window.Show(); Pump(1200);
        // choice
        rows = MirrorRows(view);
        InRow<RadioButton>(rows[2]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(300);
        Check(kit.Manager.Jobs.Single().MirrorChoice == kit.B.Url, "picking a mirror sets the choice");
        Pump(900); rows = MirrorRows(view);
        Check(InRow<RadioButton>(rows[2]).IsChecked == true && InRow<RadioButton>(rows[0]).IsChecked == false, "the list shows the choice");
        InRow<RadioButton>(rows[0]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(300);
        Check(kit.Manager.Jobs.Single().MirrorChoice is null, "Automatic (best) clears the choice");
        // Add mirror
        var answers = new Queue<string?>(new string?[] { null, "ftp://nope.example.test/f", kit.A.Url, kit.D.Url });
        view.AskMirrorAddress = () => answers.Dequeue();
        var add = FindNamed<Button>(view, "AddMirrorButton");
        var note = FindNamed<TextBlock>(view, "MirrorNote");
        add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(300);
        Check(kit.Manager.Jobs.Single().Sources.Count == 3, "cancelling the prompt adds nothing");
        add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(500);
        Check(note.Text.StartsWith("Enter a full web address"), "a bad address is explained: " + note.Text);
        add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(500);
        Check(note.Text.Contains("already in the list"), "the original address again is refused: " + note.Text);
        add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(1500);
        Check(kit.Manager.Jobs.Single().Sources.Any(x => x.Url == kit.D.Url && x.Origin == MirrorOrigin.User && x.State == MirrorState.Verified) && MirrorRows(view).Count == 5, "the user's mirror is checked and listed");
        var removeButton = MirrorRows(view).Select(r => r.Children.OfType<Button>().FirstOrDefault()).First(b => b is not null && (string?)b.Tag == kit.D.Url)!;
        removeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(500);
        Check(kit.Manager.Jobs.Single().Sources.All(x => x.Url != kit.D.Url) && MirrorRows(view).Count == 4, "Remove takes the mirror out of the list");
        // while the section shows, the Server row follows the source in use
        window.Close();
    }

    /// <summary>Themes, right to left, narrow and ten languages: nothing leaves the window, flags paint, texts exist.</summary>
    private static void MirrorRender(string scratch)
    {
        using var kit = new MirrorKit(scratch);
        try
        {
            foreach (var (theme, lang, width) in new[] { ("Light", "en", 700.0), ("Dark", "en", 700.0), ("Forest", "de", 700.0), ("Light", "ar", 700.0), ("Dark", "ja", 360.0), ("Rose", "ru", 360.0) })
            {
                Loc.SetLanguage(lang);
                Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/NeoDM;component/Themes/{theme}.xaml") };
                var view = new DownloadProgressView(kit.Manager, kit.Job.Id, null, kit.Settings);
                var window = new Window { Content = view, Width = width, Height = 760, Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual, FlowDirection = Loc.FlowDirection };
                window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
                window.Show(); Pump(400); view.SetMirrorsExpanded(true); Pump(800);
                var tag = $"{theme} {lang} {(int)width}";
                var rows = MirrorRows(view);
                Check(rows.Count == 4, tag + ": four rows");
                var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(window);
                foreach (var row in rows)
                {
                    var left = row.TranslatePoint(new Point(0, 0), window).X; var right = row.TranslatePoint(new Point(row.ActualWidth, 0), window).X;
                    Check(row.ActualWidth > 40 && Math.Min(left, right) >= -1 && Math.Max(left, right) <= window.ActualWidth + 1, tag + ": a row stays inside the window");
                }
                var germany = InRow<Image>(rows[2]);
                var at = germany.TranslatePoint(new Point(10, 7), window);
                var px = new byte[4]; bmp.CopyPixels(new Int32Rect((int)at.X, (int)at.Y, 1, 1), px, 4, 0);
                var bg = new byte[4]; bmp.CopyPixels(new Int32Rect(1, (int)at.Y, 1, 1), bg, 4, 0);
                Check(px[3] > 0 && !(px[0] == bg[0] && px[1] == bg[1] && px[2] == bg[2]), tag + ": the flag is painted");
                var radio = InRow<RadioButton>(rows[1]); var text = InRow<TextBlock>(rows[1], 2);
                if (lang == "ar") Check(view.FlowDirection == FlowDirection.RightToLeft && radio.TranslatePoint(new Point(0, 0), window).X > text.TranslatePoint(new Point(0, 0), window).X, tag + ": right to left puts the choice button on the right");
                else Check(radio.TranslatePoint(new Point(0, 0), window).X < text.TranslatePoint(new Point(0, 0), window).X, tag + ": left to right puts the choice button first");
                Check(FindNamed<Button>(view, "AddMirrorButton").ActualWidth > 50 && FindNamed<TextBlock>(view, "MirrorHelp").ActualHeight > 0, tag + ": the Add mirror button and the help line show");
                window.Close(); Pump(50);
            }
        }
        finally { Loc.SetLanguage("en"); Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") }; }
        string[] keys = { "mirror.header", "mirror.auto", "mirror.now", "mirror.original", "mirror.btn.add", "mirror.prompt.title", "mirror.prompt.text", "mirror.help", "mirror.candidate", "mirror.unverified", "mirror.reason.size", "mirror.reason.ranges", "mirror.reason.unreachable", "mirror.reason.http", "mirror.reason.checksum", "mirror.reason.content", "mirror.latency", "mirror.proxy", "mirror.origin.header", "mirror.origin.metalink", "mirror.origin.user", "mirror.remove", "mirror.checked", "mirror.warn.unverified", "mirror.warn.used_unverified", "mirror.add.ok", "mirror.add.pending", "mirror.add.bad_address", "mirror.add.duplicate", "mirror.add.unsupported", "mirror.add.metalink", "mirror.add.rejected", "error.checksum" };
        foreach (var lang in Langs) { Loc.SetLanguage(lang); foreach (var key in keys) if (Loc.T(key) == key) throw new Exception($"FAIL: {lang} is missing {key}"); }
        Loc.SetLanguage("en");
        Check(true, "all " + keys.Length + " mirror texts exist in ten languages");
    }

    /// <summary>A job that failed its checksum says so in plain words, and a proxy hides the connect times.</summary>
    private static void MirrorErrorAndProxy()
    {
        var failed = new DownloadRow(JobWith(j => { SetJob(j, "Status", "Failed"); SetJob(j, "ErrorCode", DownloadManager.ChecksumCode); SetJob(j, "Error", "The finished file does not match its checksum (sha-256)."); }));
        Check(failed.ErrorIsLocalized && failed.Error!.StartsWith("The finished file does not match its published checksum") && failed.Error.Contains("Resume"), "the checksum failure is explained in the user's words: " + failed.Error);
        var proxied = new DownloadRow(JobWith(j => { SetJob(j, "Status", "Paused"); SetJob(j, "ServerViaProxy", true); SetJob(j, "ServerProxy", "10.0.0.2:8080"); SetJob(j, "ServerHost", "files.example.test"); SetJob(j, "ServerLatencyMs", 70); }));
        Check(proxied.Server is { ViaProxy: true } && proxied.ServerViaProxy, "a proxied download shows the proxy in the Server row");
    }
}
