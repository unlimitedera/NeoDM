using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static class YoutubeUiTests
{
    private static int checks;
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static T C<T>(Window w, string name) where T : FrameworkElement => (T)w.FindName(name);
    private static void Pump() { var f = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) }; timer.Tick += (_, _) => { timer.Stop(); f.Continue = false; }; timer.Start(); Dispatcher.PushFrame(f); }
    private sealed class Handler : HttpMessageHandler
    {
        public static int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) }); }
    }
    private static ToolManager Tool(string state, string name)
    {
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant();
        var pin = new ToolPin("https://tools.example.test/tool.exe", "fixture", 3, hash, name + ".exe", 3, hash);
        var tool = new ToolManager(state, pin, handlerFactory: () => new Handler(), toolName: name, rawExecutable: true);
        tool.InstallAsync().GetAwaiter().GetResult(); return tool;
    }
    private static readonly YoutubeMetadata Metadata = new("Authorized sample", new[] { new YoutubeFormat("18", "mp4", 360, 128, true, true), new YoutubeFormat("137", "mp4", 1080, 0, true, false), new YoutubeFormat("140", "m4a", 0, 128, false, true) });
    private static void Capture(Window w, string path)
    {
        w.UpdateLayout(); var c = (FrameworkElement)w.Content;
        var bmp = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (var d = visual.RenderOpen()) { d.DrawRectangle(w.Background, null, new Rect(0, 0, c.ActualWidth, c.ActualHeight)); d.DrawRectangle(new VisualBrush(c), null, new Rect(0, 0, c.ActualWidth, c.ActualHeight)); }
        bmp.Render(w); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using var file = File.Create(path); encoder.Save(file); Console.WriteLine("SCREENSHOT=" + path);
    }
    private const string HelperMode = "NDM_UI_YT_MODE";
    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
    private static ToolManager BytesTool(string state, string name, byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var pin = new ToolPin("https://tools.example.test/" + name + ".exe", "fixture", bytes.Length, hash, name + ".exe", bytes.Length, hash);
        var tool = new ToolManager(state, pin, handlerFactory: () => new BytesHandler(bytes), toolName: name, rawExecutable: true);
        tool.InstallAsync().GetAwaiter().GetResult(); return tool;
    }
    /// <summary>A real child process: this test program copied next to its support files and installed as the pinned yt-dlp.exe.</summary>
    private static YoutubeService HelperService(string state)
    {
        var exe = Environment.ProcessPath!; var extractor = BytesTool(state, "yt-dlp", File.ReadAllBytes(exe));
        foreach (var file in new[] { "NDM.App.YoutubeTests.dll", "NDM.App.YoutubeTests.runtimeconfig.json", "NDM.App.YoutubeTests.deps.json", "NDM.Core.dll" })
        {
            var from = Path.Combine(AppContext.BaseDirectory, file); if (File.Exists(from)) File.Copy(from, Path.Combine(extractor.ToolDirectory, file), true);
        }
        return new YoutubeService(state, extractor, BytesTool(state, "deno", new byte[] { 4, 5, 6 }));
    }
    private static int FakeHelper()
    {
        var mode = Environment.GetEnvironmentVariable(HelperMode);
        switch (mode)
        {
            case "oversize": Console.Write(new string('X', 5 * 1024 * 1024)); return 0;
            case "malformed": Console.WriteLine("not json at all"); Console.WriteLine("[]"); return 0;
            case "filtered": return 0;
            case "hang": Thread.Sleep(60000); return 0;
            case "unknownfail": Console.Error.WriteLine("WARNING: note"); Console.Error.WriteLine("ERROR: [youtube] abc: odd failure"); return 1;
            default:
                Console.WriteLine("{\"title\":\"Many formats\",\"availability\":\"public\",\"is_live\":false}");
                var items = Enumerable.Range(0, 200).Select(i => (i % 4) switch
                {
                    0 => $"{{\"format_id\":\"sb{i}\",\"ext\":\"mhtml\",\"height\":27,\"vcodec\":\"none\",\"acodec\":\"none\"}}",
                    1 => $"{{\"format_id\":\"v{i}\",\"ext\":\"mp4\",\"height\":{144 + i},\"vcodec\":\"avc1\",\"acodec\":\"none\"}}",
                    2 => $"{{\"format_id\":\"a{i}\",\"ext\":\"m4a\",\"abr\":{48 + i},\"vcodec\":\"none\",\"acodec\":\"aac\"}}",
                    _ => $"{{\"format_id\":\"p{i}\",\"ext\":\"mp4\",\"height\":360,\"vcodec\":\"avc1\",\"acodec\":\"aac\"}}",
                });
                Console.WriteLine("[" + string.Join(",", items) + "]"); return 0;
        }
    }
    private static string Helper(Window host, string state, string mode, DownloadRequest seed, string root, ToolManager mux, Action<YoutubeOptionsDialog>? during = null)
    {
        Environment.SetEnvironmentVariable(HelperMode, mode);
        try
        {
            var dialog = new YoutubeOptionsDialog(seed, root, HelperService(Path.Combine(state, mode)), mux); dialog.Show(); Pump();
            var read = dialog.ReadAsync(); during?.Invoke(dialog);
            var until = DateTime.UtcNow.AddSeconds(60); while (!read.IsCompleted && DateTime.UtcNow < until) Pump();
            Check(read.IsCompleted, "real helper read " + mode + " finishes");
            var text = C<TextBox>(dialog, "Error").Text; lastDialogQuality = C<ComboBox>(dialog, "Quality").Items.Count; lastDialogAudio = C<ComboBox>(dialog, "Audio").Items.Count;
            lastLoadingHidden = C<Border>(dialog, "LoadPanel").Visibility == Visibility.Collapsed || C<ProgressBar>(dialog, "LoadingProgress").Value >= 100; dialog.Close(); return text;
        }
        finally { Environment.SetEnvironmentVariable(HelperMode, null); }
    }
    private static int lastDialogQuality, lastDialogAudio; private static bool lastLoadingHidden;
    [STAThread]
    private static int Main()
    {
        if (string.Equals(Path.GetFileName(Environment.ProcessPath), "yt-dlp.exe", StringComparison.OrdinalIgnoreCase)) return FakeHelper();
        return RunUi();
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int RunUi()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "ndm-youtube-ui-" + Guid.NewGuid().ToString("N"), "NDM"); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("NDM_STATE_DIR", root); Environment.SetEnvironmentVariable("NEODM_LANG", "en");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
        try
        {
            var invalid = new AddDownloadWindow("https://www.youtube.com/playlist?list=invalid"); invalid.Show(); Pump();
            typeof(AddDownloadWindow).GetMethod("Accept_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(invalid, new object[] { invalid, new RoutedEventArgs() });
            Check(C<TextBlock>(invalid, "ValidationError").Text == Loc.T("youtube.playlist_invalid") && invalid.Request is null && invalid.PlaylistUrl is null, "Add URL refuses an invalid playlist id before any helper call"); invalid.Close();
            var goodList = new AddDownloadWindow("https://www.youtube.com/playlist?list=PLav47HAVZMjnTFVZL-aImCQIC0uLZtNCz");
            Check(C<TextBlock>(goodList, "YoutubeNote").Visibility == Visibility.Visible && C<TextBlock>(goodList, "YoutubeNote").Text == Loc.T("add.youtube_note_playlist"), "Add URL shows the playlist note as soon as the text looks like a playlist");
            goodList.Dispatcher.BeginInvoke(new Action(() => typeof(AddDownloadWindow).GetMethod("Accept_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(goodList, new object[] { goodList, new RoutedEventArgs() })));
            Check(goodList.ShowDialog() == true && goodList.PlaylistUrl == "https://www.youtube.com/playlist?list=PLav47HAVZMjnTFVZL-aImCQIC0uLZtNCz" && goodList.Request is null, "Add URL routes a valid playlist to the playlist dialog instead of treating it as a file");
            var missing = new YoutubeService(Path.Combine(root, "missing")); var mux = new ToolManager(root);
            var seed = new DownloadRequest("https://www.youtube.com/watch?v=aqz-KE-bpKQ");
            var notInstalled = new YoutubeOptionsDialog(seed, root, missing, mux); notInstalled.Show(); Pump(); var missingRead = notInstalled.ReadAsync(); while (!missingRead.IsCompleted) Pump(); missingRead.GetAwaiter().GetResult();
            Check(C<TextBox>(notInstalled, "Error").Text == Loc.T("tool.bundle_missing") && Handler.Requests == 0, "missing bundle fails without runtime installation"); notInstalled.Close();
            var service = new YoutubeService(root, Tool(root, "yt-dlp"), Tool(root, "deno"));
            Func<string, CancellationToken, Task<YoutubeMetadata>> inspect = (_, _) => Task.FromResult(Metadata);
            var transition = new NumericTransition(); transition.Set(0, 0, false); transition.Set(100, 0, true);
            Check(Math.Abs(transition.Value(0.225) - 50) < 0.001, "numeric midpoint interpolates display only");
            transition.Set(200, 0.225, true);
            Check(Math.Abs(transition.Value(0.225) - 50) < 0.001 && transition.Value(0.45) > 100, "rapid retarget starts from current display without a queue");
            Check(transition.Value(1) == 200 && !transition.Active, "numeric final value snaps exactly and stops");
            transition.Set(20, 1, false); Check(transition.Value(1) == 20 && !transition.Active, "reduced motion and phase resets snap without animation");
            var telemetryJob = new DownloadJob();
            void SetJob(string name, object? value) => typeof(DownloadJob).GetProperty(name)!.SetValue(telemetryJob, value);
            SetJob("Id", "telemetry-fixture"); SetJob("Status", "Downloading"); SetJob("TotalBytes", 100L * 1024 * 1024); SetJob("DownloadedBytes", 10L * 1024 * 1024); SetJob("SpeedBytesPerSecond", 1024d * 1024); SetJob("RemainingTime", TimeSpan.FromSeconds(90));
            var telemetryRow = new DownloadRow(telemetryJob);
            var renderPanel = new StackPanel { Margin = new Thickness(24) };
            foreach (var property in new[] { "DisplayDownloadedSummary", "DisplaySize", "DisplayRate", "DisplayTimeLeft" })
            {
                var text = new TextBlock { FontSize = 18, Margin = new Thickness(0, 4, 0, 4) }; text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(property)); renderPanel.Children.Add(text);
            }
            var renderWindow = new Window { Title = "Authorized telemetry fixture", Width = 580, Height = 260, Content = renderPanel, DataContext = telemetryRow };
            renderWindow.Show(); Pump();
            var sequence = "F:/NDM/artifacts/telemetry-animation"; Directory.CreateDirectory(sequence);
            Capture(renderWindow, Path.Combine(sequence, "frame-00.png"));
            SetJob("DownloadedBytes", 20L * 1024 * 1024); SetJob("SpeedBytesPerSecond", 3d * 1024 * 1024); SetJob("RemainingTime", TimeSpan.FromSeconds(30)); telemetryRow.Update(telemetryJob);
            var intermediate = new List<double>();
            for (int frame = 1; frame <= 10; frame++) { Pump(); intermediate.Add(telemetryRow.DisplayProgress); Capture(renderWindow, Path.Combine(sequence, $"frame-{frame:00}.png")); }
            Check(telemetryJob.DownloadedBytes == 20L * 1024 * 1024 && intermediate.Any(v => v > 10 && v < 20) && Math.Abs(telemetryRow.DisplayProgress - 20) < 0.001, "real WPF render sequence interpolates numbers without changing engine counters");
            SetJob("IsYoutube", true); SetJob("YoutubePhase", "audio"); telemetryRow.Update(telemetryJob);
            Check(telemetryRow.ConnectionRows.Count == 0 && telemetryRow.StatusText == Loc.T("youtube.phase.audio"), "YouTube audio transfer never fabricates a Connecting row and shows its actual phase");
            SetJob("TotalBytesEstimated", true); telemetryRow.Update(telemetryJob);
            Check(telemetryRow.Size == Loc.T("stream.size_estimate", DownloadRow.FormatBytes(telemetryJob.TotalBytes)), "helper estimated totals retain the visible estimate label");
            SetJob("YoutubePhase", "merging"); SetJob("SpeedBytesPerSecond", 0d); SetJob("RemainingTime", null); telemetryRow.Update(telemetryJob);
            Check(telemetryRow.StatusText == Loc.T("youtube.phase.merging") && telemetryRow.DisplayTimeLeft == Loc.T("progress.unknown") && telemetryRow.ConnectionRows.Count == 0, "merge phase retains truthful unknown ETA without a connection table");
            SetJob("TotalBytes", 0L); SetJob("RemainingTime", null); telemetryRow.Update(telemetryJob);
            Check(telemetryRow.Size == Loc.T("row.unknown") && telemetryRow.DisplayTimeLeft == Loc.T("progress.unknown"), "unknown total and ETA stay unknown during display transitions");
            telemetryRow.MotionEnabled = false; SetJob("DownloadedBytes", 30L * 1024 * 1024); telemetryRow.Update(telemetryJob);
            Check(telemetryRow.Downloaded == DownloadRow.FormatBytes(30L * 1024 * 1024), "owner reduced-motion switch snaps display exactly");
            for (int i = 0; i < 100; i++) { SetJob("DownloadedBytes", (long)i); telemetryRow.Update(telemetryJob); }
            Pump();
            var activeRows = (System.Collections.ICollection)typeof(DownloadRow).GetField("animated", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            Check(activeRows.Count == 0, "display scheduler stops and retains no active rows after snap");
            renderWindow.Close();
            int loadingCalls = 0;
            var pendingInfo = new TaskCompletionSource<YoutubeMetadata>();
            var loading = new YoutubeOptionsDialog(seed, root, service, mux, (_, _) => { loadingCalls++; return pendingInfo.Task; }); loading.Show(); Pump();
            var load = loading.ReadAsync(); loading.ReadAsync().GetAwaiter().GetResult(); Pump();
            Check(loadingCalls == 1 && !C<ProgressBar>(loading, "LoadingProgress").IsIndeterminate && C<ProgressBar>(loading, "LoadingProgress").IsVisible && C<ProgressBar>(loading, "LoadingProgress").Maximum == 100 && !C<Button>(loading, "StartButton").IsEnabled, "metadata loading shows progress and refuses duplicate requests or stale choices");
            pendingInfo.SetResult(Metadata); while (!load.IsCompleted) Pump();
            Check(C<ProgressBar>(loading, "LoadingProgress").Value == 100 && C<Button>(loading, "ReadButton").IsEnabled, "successful metadata read completes the progress bar at 100 and re-enables the button"); loading.Close();
            var categorySettings = new AppSettings { DownloadDirectory = Path.Combine(root, "categories"), VideoFolder = "Movies", MusicFolder = "Sound" };
            var categories = new YoutubeOptionsDialog(seed, categorySettings.DownloadDirectory, service, mux, inspect, categorySettings); categories.Show(); categories.ReadAsync().GetAwaiter().GetResult(); Pump();
            Check(C<TextBox>(categories, "Folder").Text == Path.Combine(categorySettings.DownloadDirectory, "Movies"), "video category preview uses existing configured folder");
            C<CheckBox>(categories, "AudioOnly").IsChecked = true;
            Check(C<TextBox>(categories, "Folder").Text == Path.Combine(categorySettings.DownloadDirectory, "Sound"), "audio-only mode switches preview to Music category");
            C<TextBox>(categories, "Folder").Text = Path.Combine(root, "chosen"); C<CheckBox>(categories, "AudioOnly").IsChecked = false;
            Check(C<TextBox>(categories, "Folder").Text == Path.Combine(root, "chosen"), "explicit destination survives mode change");
            categories.Close();
            var dialog = new YoutubeOptionsDialog(seed, root, service, mux, inspect);
            dialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                dialog.ReadAsync().GetAwaiter().GetResult();
                Check(C<ComboBox>(dialog, "Quality").Items.Count == 2, "real WPF quality list");
                Check(C<ComboBox>(dialog, "Audio").Items.Count == 1 && C<ComboBox>(dialog, "Audio").IsEnabled, "separate audio quality offered");
                C<CheckBox>(dialog, "AudioOnly").IsChecked = true;
                Check(C<ComboBox>(dialog, "Audio").IsEnabled && C<CheckBox>(dialog, "Mp3").IsEnabled, "original audio choice independent of conversion");
                dialog.AcceptAsync(DownloadOptionsDialog.DialogAction.Queue).GetAwaiter().GetResult();
            }));
            Check(dialog.ShowDialog() == true && dialog.Request?.Youtube == new YoutubeSelection("140") && dialog.UserAction == DownloadOptionsDialog.DialogAction.Queue, "dialog returns audio-only queue request");
            var convert = new YoutubeOptionsDialog(seed, categorySettings.DownloadDirectory, service, mux, inspect, categorySettings);
            convert.Dispatcher.BeginInvoke(new Action(() =>
            {
                convert.ReadAsync().GetAwaiter().GetResult(); C<CheckBox>(convert, "AudioOnly").IsChecked = true; C<CheckBox>(convert, "Mp3").IsChecked = true;
                C<TextBox>(convert, "FileName").Text = "Authorized sample.mp3"; convert.AcceptAsync(DownloadOptionsDialog.DialogAction.Queue).GetAwaiter().GetResult();
            }));
            Check(convert.ShowDialog() == true && convert.Request?.Youtube == new YoutubeSelection("140", Mp3: true) && convert.Request.FileName == "Authorized sample.mp3" && convert.Request.TargetDirectory == Path.Combine(categorySettings.DownloadDirectory, "Sound"), "audio-only MP3 maps to correct metadata format, one extension and Music destination");
            categorySettings.EnableCategoryFolders = false;
            var uncategorized = new YoutubeOptionsDialog(seed, categorySettings.DownloadDirectory, service, mux, inspect, categorySettings); uncategorized.Show(); uncategorized.ReadAsync().GetAwaiter().GetResult();
            C<CheckBox>(uncategorized, "AudioOnly").IsChecked = true;
            Check(C<TextBox>(uncategorized, "Folder").Text == categorySettings.DownloadDirectory, "disabled categorization keeps base folder in either mode"); uncategorized.Close();
            var cancelledInfo = new TaskCompletionSource<YoutubeMetadata>();
            var cancelledRead = new YoutubeOptionsDialog(seed, root, service, mux, (_, token) => { token.Register(() => cancelledInfo.TrySetCanceled(token)); return cancelledInfo.Task; }); cancelledRead.Show();
            var readingCancelled = cancelledRead.ReadAsync(); Pump(); cancelledRead.Close(); while (!readingCancelled.IsCompleted) Pump();
            Check(cancelledRead.Request is null && C<TextBox>(cancelledRead, "Error").Text == "" && C<Border>(cancelledRead, "LoadPanel").Visibility == Visibility.Collapsed, $"metadata cancellation closes responsively and clears loading without a request, request={cancelledRead.Request is not null}, loading={C<Border>(cancelledRead, "LoadPanel").Visibility}, task={readingCancelled.Status}, error={readingCancelled.Exception}");
            using (var manager = new DownloadManager(Path.Combine(root, "downloads"), Path.Combine(root, "state")))
            {
                var job = manager.AddPausedAsync(dialog.Request!).GetAwaiter().GetResult();
                Check(manager.Jobs.Single().Status == "Paused" && manager.RequestOf(job.Id)?.Youtube?.VideoId == "140", "real manager persists dialog selection without starting helper");
            }
            var shots = "F:/NDM/artifacts/youtube-screenshots"; Directory.CreateDirectory(shots);
            using (var manager = new DownloadManager(Path.Combine(root, "handoff-files"), Path.Combine(root, "handoff-state")))
            {
                var windows = new TrayWindowList();
                var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "handoff-files") };
                foreach (var mode in new[] { "queue", "start", "cancel", "gone" })
                {
                    using var gone = new CancellationTokenSource();
                    NativeBridge.BrowserDownloadResponse? reply = null;
                    int inspections = 0, progress = 0, before = manager.Jobs.Count;
                    var request = new NativeBridge.BrowserDownloadRequest(seed.Url, null, null, null, r => reply = r, Gone: gone.Token, Youtube: true);
                    var task = YoutubeCapture.HandleAsync(request, manager, settings, windows, onDialog: d =>
                    {
                        d.Dispatcher.BeginInvoke(new Action(async () =>
                        {
                            Check(d.Owner is null && d.ShowInTaskbar && windows.HasPendingDialogs && inspections == 0, mode + " uses independent quality dialog without automatic inspection");
                            if (mode == "cancel")
                            {
                                var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(C<Button>(d, "CancelButton"));
                                ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke(); return;
                            }
                            await d.ReadAsync();
                            if (mode == "gone")
                            {
                                gone.Cancel(); Pump(); await d.AcceptAsync(DownloadOptionsDialog.DialogAction.Queue);
                                Check(!C<Button>(d, "StartButton").IsEnabled && d.Request is null, "lost browser connection blocks acceptance"); d.Close(); return;
                            }
                            C<CheckBox>(d, "AudioOnly").IsChecked = true; await d.AcceptAsync(mode == "start" ? DownloadOptionsDialog.DialogAction.StartNow : DownloadOptionsDialog.DialogAction.Queue);
                        }));
                    }, showProgress: _ => progress++, dialogFactory: s => new YoutubeOptionsDialog(s, settings.DownloadDirectory, service, mux, (_, _) => { inspections++; return Task.FromResult(Metadata); }));
                    var until = DateTime.UtcNow.AddSeconds(10); while (!task.IsCompleted && DateTime.UtcNow < until) Pump();
                    Check(task.IsCompletedSuccessfully, mode + " handoff completes");
                    if (mode == "queue") Check(reply?.Ok == true && manager.Jobs.Count == before + 1 && progress == 0 && manager.RequestOf(reply.Id!)?.Youtube?.VideoId == "140", "desktop Add to queue choice durably queued before acknowledgement, no progress window"); if (mode == "queue") manager.Pause(reply!.Id!);
                    else if (mode == "start") { Check(reply?.Ok == true && manager.Jobs.Count == before + 1 && progress == 1 && manager.RequestOf(reply.Id!)?.Youtube?.VideoId == "140", "desktop Add choice commits selection and requests progress"); manager.Pause(reply!.Id!); }
                    else Check(reply?.Ok == false && manager.Jobs.Count == before, mode + " starts no download");
                    Check(windows.Count == 0, mode + " tray entry removed");
                }
                using var expired = new CancellationTokenSource(); expired.Cancel();
                NativeBridge.BrowserDownloadResponse? expiredReply = null;
                var preGone = YoutubeCapture.HandleAsync(new(seed.Url, null, null, null, r => expiredReply = r, Gone: expired.Token, Youtube: true), manager, settings, windows, onDialog: _ => throw new Exception("expired request opened a dialog"));
                Check(preGone.IsCompletedSuccessfully && expiredReply?.Ok == false && windows.Count == 0, "already disconnected request opens no dialog or tray entry");
            }
            foreach (var theme in new[] { "Light", "Dark", "Midnight", "Graphite", "Ocean", "Forest", "Rose", "Paper" })
            {
                app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/NeoDM;component/Themes/{theme}.xaml") };
                foreach (var rtl in new[] { false, true })
                {
                    Loc.SetLanguage(rtl ? "ar" : "en");
                    var w = new YoutubeOptionsDialog(seed, "F:/Downloads", service, mux, inspect); w.Show(); w.ReadAsync().GetAwaiter().GetResult();
                    w.Width = 520; w.Height = 500; Pump(); Check(C<Button>(w, "StartButton").IsVisible && C<Button>(w, "ReadButton").ActualWidth > 0, theme + (rtl ? " RTL" : " LTR") + " small layout");
                    w.Width = 900; w.Height = 950; Pump(); Check(C<ComboBox>(w, "Quality").ActualWidth > 300, theme + (rtl ? " RTL" : " LTR") + " large layout");
                    if (theme is "Light" or "Dark") Capture(w, Path.Combine(shots, theme + (rtl ? "-ar.png" : "-en.png")));
                    w.Close();
                }
            }
            Loc.SetLanguage("en");
            var helperMux = new ToolManager(root); var helperState = Path.Combine(root, "helpers");
            var oversize = Helper(null!, helperState, "oversize", seed, root, helperMux);
            Check(oversize == Loc.T("youtube.metadata") && oversize != Loc.T("youtube.failed") && lastLoadingHidden, "oversized helper output shows the metadata message, never the generic tool failure");
            var malformed = Helper(null!, helperState, "malformed", seed, root, helperMux);
            Check(malformed == Loc.T("youtube.metadata"), "malformed helper output shows the metadata message");
            var filtered = Helper(null!, helperState, "filtered", seed, root, helperMux);
            Check(filtered == Loc.T("youtube.unsupported"), "a video rejected by the live or DRM filter keeps the unsupported message");
            var unknown = Helper(null!, helperState, "unknownfail", seed, root, helperMux);
            Check(unknown == Loc.T("youtube.failed") + "\r\n" + Loc.T("youtube.detail", "ERROR: [youtube] abc: odd failure") && C<TextBox>(new YoutubeOptionsDialog(seed, root, service, mux), "Error").IsReadOnly, "unknown helper failure keeps the generic message and adds a copyable detail line");
            var many = Helper(null!, helperState, "many", seed, root, helperMux);
            Check(many == "" && lastDialogQuality == 100 && lastDialogAudio == 50, $"200 helper formats with storyboards list only usable ones in the real dialog (video {lastDialogQuality}, audio {lastDialogAudio})");
            var silent = Helper(null!, helperState, "hang", seed, root, helperMux, d => { Pump(); Pump(); Pump(); d.Close(); });
            Check(silent == "" && lastLoadingHidden, "true user cancellation during a real helper read shows no error");
            var failure = new YoutubeOptionsDialog(seed, root, service, mux, (_, _) => Task.FromException<YoutubeMetadata>(new YoutubeException("youtube.restricted"))); failure.Show(); failure.ReadAsync().GetAwaiter().GetResult(); Pump();
            Check(C<TextBox>(failure, "Error").IsReadOnly && C<TextBox>(failure, "Error").Text == Loc.T("youtube.restricted") && !C<Button>(failure, "StartButton").IsEnabled, "copyable localized failure blocks submission"); failure.Close();
            Check(Handler.Requests == 2, "ordinary WPF suite uses only deterministic fixture installs");
            Console.WriteLine("YouTube WPF tests passed: " + checks); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close(); app.Shutdown(); }
    }
}
