global using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;
using NDM.Tests;

internal sealed class V1150App : Application
{
    public V1150App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}

/// <summary>A download that never ends: answers with a huge length and trickles bytes, so a job stays Downloading for as long as a test needs.</summary>
internal sealed class SlowServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    public SlowServer() { listener.Start(); _ = Task.Run(Accept); }
    private async Task Accept()
    {
        try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); _ = Task.Run(() => Serve(client)); } }
        catch (Exception) { }
    }
    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream(); var buffer = new byte[8192]; int total = 0;
            while (!stop.IsCancellationRequested) { int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), stop.Token); if (n <= 0) return; total += n; if (System.Text.Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n")) break; }
            var head = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: 2000000000\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, stop.Token);
            var chunk = new byte[512];
            while (!stop.IsCancellationRequested) { await stream.WriteAsync(chunk, stop.Token); await Task.Delay(40, stop.Token); }
        }
        catch (Exception) { }
    }
    public void Dispose() { stop.Cancel(); try { listener.Stop(); } catch (Exception) { } }
}

/// <summary>1.15.0 tests: tabbed downloads window (model, close semantics, shortcuts, 25 tabs, themes, RTL, locales) and the busy-queue prompt (defaults, migration, trigger table, choices).</summary>
internal static partial class V1150Tests
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
    private static void Sync(Task task) { PumpUntil(() => task.IsCompleted, "task finished", 30000); task.GetAwaiter().GetResult(); }
    private static readonly string? Shots = Environment.GetEnvironmentVariable("NEODM_SHOTS");
    private static void SaveShot(Window w, string name)
    {
        if (Shots is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder); w.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32); bmp.Render(w);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var fs = File.Create(Path.Combine(folder, name)); enc.Save(fs);
    }
    private static DownloadManager NewManager(string root, string label, int concurrent = 3) =>
        new(Path.Combine(root, label, "files"), Path.Combine(root, label, "state"), new DownloadOptions { RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(6), MaxConcurrentDownloads = concurrent });
    private static DownloadJob Job(DownloadManager m, string id) => m.Jobs.Single(j => j.Id == id);
    private static void CheckLocales(string[] keys, string label)
    {
        Loc.SetLanguage("en"); var english = keys.ToDictionary(k => k, Loc.T);
        foreach (var info in LanguageCatalog.All)
        {
            Loc.SetLanguage(info.Tag);
            foreach (var k in keys)
            {
                var v = Loc.T(k);
                Check(v.Length > 0 && v != k, $"{label} {info.Tag}: {k} exists");
                if (info.Tag != "en" && k is not "tabs.state_percent" && !(info.Tag is "de" or "pt-BR" && k is "tabs.window_title" or "tabs.summary")) Check(v != english[k], $"{label} {info.Tag}: {k} is translated");
            }
        }
        Loc.SetLanguage("en");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-V1150Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("NDM_STATE_DIR", Path.Combine(scratch, "NDM"));
        var app = new V1150App();
        string only = args.Length > 1 && args[0] == "--only" ? args[1] : "";
        try
        {
            Loc.SetLanguage("en"); WindowPlacements.Configure(null);
            void Run(string name, Action a) { if (only.Length > 0 && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) return; Console.WriteLine("== " + name); a(); }
            Run("Settings", () => SettingsTests(scratch));
            Run("TabModel", TabModel);
            Run("ClosePolicy", ClosePolicy);
            Run("TabsWindow", () => TabsWindow(scratch));
            Run("CloseSemantics", () => CloseSemantics(scratch));
            Run("ManyTabs", () => ManyTabs(scratch));
            Run("Themes", () => ThemesAndRtl(scratch));
            Run("MainWindowRouting", () => MainWindowRouting(scratch));
            Run("BusyTrigger", BusyTrigger);
            Run("BusyChoices", () => BusyChoices(scratch));
            Run("BusyPromptWindow", () => BusyPromptWindow());
            Run("Locales", () => CheckLocales(new[] { "busy.title", "busy.message", "busy.message_batch", "busy.wait", "busy.now", "busy.count", "busy.count_invalid", "busy.remember",
                "options.chk.ask_busy_queue", "options.text.ask_busy_queue_help", "options.chk.downloads_as_tabs", "options.text.downloads_as_tabs_help", "options.btn.reset_tab_choice",
                "options.text.tab_choice_cancel", "options.text.tab_choice_hide", "options.text.tab_choice_ask", "tabs.window_title", "tabs.a11y.strip", "tabs.scroll_start", "tabs.scroll_end",
                "tabs.add", "tabs.close_tab", "tabs.state_percent", "tabs.title_more", "tabs.summary", "tabs.close_title", "tabs.close_message", "tabs.close_message_all", "tabs.close_hide",
                "tabs.close_hide_all", "tabs.close_cancel", "tabs.close_cancel_all", "tabs.close_keep", "tabs.close_remember" }, "1.15.0"));
            Console.WriteLine($"V1150 TESTS PASSED: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { WindowPlacements.Configure(null); BusyQueue.Current = null; foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close(); try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } app.Shutdown(); }
    }
}
