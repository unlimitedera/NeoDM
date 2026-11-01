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
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "ndm-youtube-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("NDM_STATE_DIR", root); Environment.SetEnvironmentVariable("NEODM_LANG", "en");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
        try
        {
            var invalid = new AddDownloadWindow("https://www.youtube.com/playlist?list=invalid"); invalid.Show(); Pump();
            typeof(AddDownloadWindow).GetMethod("Accept_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(invalid, new object[] { invalid, new RoutedEventArgs() });
            Check(C<TextBlock>(invalid, "ValidationError").Text == Loc.T("youtube.url_invalid") && invalid.Request is null, "Add URL rejects playlists before any helper call"); invalid.Close();
            var missing = new YoutubeService(Path.Combine(root, "missing")); var mux = new ToolManager(root);
            var seed = new DownloadRequest("https://www.youtube.com/watch?v=aqz-KE-bpKQ");
            var notInstalled = new YoutubeOptionsDialog(seed, root, missing, mux); notInstalled.Show(); Pump(); notInstalled.ReadAsync().GetAwaiter().GetResult();
            Check(C<TextBox>(notInstalled, "Error").Text == Loc.T("youtube.consent_required") && Handler.Requests == 0, "no installation without explicit consent"); notInstalled.Close();
            var service = new YoutubeService(root, Tool(root, "yt-dlp"), Tool(root, "deno"));
            Func<string, CancellationToken, Task<YoutubeMetadata>> inspect = (_, _) => Task.FromResult(Metadata);
            var dialog = new YoutubeOptionsDialog(seed, root, service, mux, inspect);
            dialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                dialog.ReadAsync().GetAwaiter().GetResult();
                Check(C<ComboBox>(dialog, "Quality").Items.Count == 3, "real WPF quality list");
                Check(C<ComboBox>(dialog, "Audio").Items.Count == 1 && C<ComboBox>(dialog, "Audio").IsEnabled, "separate audio quality offered");
                C<ComboBox>(dialog, "Quality").SelectedIndex = 2;
                Check(!C<ComboBox>(dialog, "Audio").IsEnabled && C<CheckBox>(dialog, "Mp3").IsEnabled, "original audio choice independent of conversion");
                dialog.AcceptAsync(true).GetAwaiter().GetResult();
            }));
            Check(dialog.ShowDialog() == true && dialog.Request?.Youtube == new YoutubeSelection("140") && dialog.QueueOnly, "dialog returns audio-only paused queue request");
            using (var manager = new DownloadManager(Path.Combine(root, "downloads"), Path.Combine(root, "state")))
            {
                var job = manager.AddPausedAsync(dialog.Request!).GetAwaiter().GetResult();
                Check(manager.Jobs.Single().Status == "Paused" && manager.RequestOf(job.Id)?.Youtube?.VideoId == "140", "real manager persists dialog selection without starting helper");
            }
            var shots = "F:/NDM/artifacts/youtube-screenshots"; Directory.CreateDirectory(shots);
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
            var failure = new YoutubeOptionsDialog(seed, root, service, mux, (_, _) => Task.FromException<YoutubeMetadata>(new YoutubeException("youtube.restricted"))); failure.Show(); failure.ReadAsync().GetAwaiter().GetResult(); Pump();
            Check(C<TextBox>(failure, "Error").IsReadOnly && C<TextBox>(failure, "Error").Text == Loc.T("youtube.restricted") && !C<Button>(failure, "StartButton").IsEnabled, "copyable localized failure blocks submission"); failure.Close();
            Check(Handler.Requests == 2, "ordinary WPF suite uses only deterministic fixture installs");
            Console.WriteLine("YouTube WPF tests passed: " + checks); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close(); app.Shutdown(); }
    }
}
