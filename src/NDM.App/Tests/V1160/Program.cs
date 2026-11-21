// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
global using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;
using NDM.Tests;

internal sealed class V1160App : Application
{
    public V1160App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}

/// <summary>A download that never ends (huge length, trickling bytes), so a job stays Downloading as long as a test needs.</summary>
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

/// <summary>1.16.0 tests: named queues (model, migration, ordering), per-queue limits, run-together switch, queue schedules (weekday, one-off, stop, DST, missed start, app start inside a window), power action with a fake,
/// the chooser integration, the sidebar tree, the editor (themes, RTL, narrow) and localization.</summary>
internal static partial class V1160Tests
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
    private static readonly string? Shots = Environment.GetEnvironmentVariable("NEODM_SHOTS");
    private static void SaveShot(Window w, string name)
    {
        if (Shots is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder); w.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32); bmp.Render(w);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var fs = File.Create(Path.Combine(folder, name)); enc.Save(fs);
    }
    private static DownloadManager NewManager(string root, string label, int global = 1) =>
        new(Path.Combine(root, label, "files"), Path.Combine(root, label, "state"), new DownloadOptions { RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(6), MaxConcurrentDownloads = global, MaxSegments = 1 });
    private static string StateOf(string root, string label) => Path.Combine(root, label, "state");
    private static DownloadJob Job(DownloadManager m, string id) => m.Jobs.Single(j => j.Id == id);
    private static int Count(DownloadManager m, string queue, string status) => m.Jobs.Count(j => j.QueueId == queue && j.Status == status);
    private static DateTime D(int day, int h, int m) => new(2026, 11, day, h, m, 0, DateTimeKind.Unspecified); // Nov 16 2026 is a Monday
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
                if (info.Tag != "en") Check(v != english[k] || info.Tag == "de" && k == "queue.editor.name" || info.Tag == "pt-BR" && k == "queue.a11y.item" /* the same word in that language */ || k == "pagelink.col.status" && info.Tag is "de" or "pt-BR", $"{label} {info.Tag}: {k} is translated");
            }
        }
        Loc.SetLanguage("en");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-V1160Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("NDM_STATE_DIR", Path.Combine(scratch, "NDM"));
        var app = new V1160App();
        string only = args.Length > 1 && args[0] == "--only" ? args[1] : "";
        try
        {
            Loc.SetLanguage("en"); WindowPlacements.Configure(null);
            void Run(string name, Action a) { if (only.Length > 0 && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) return; Console.WriteLine("== " + name); TestGuard.Run(name, a); }
            Run("PrimaryContrast", PrimaryContrast);
            Run("ScheduleTable", ScheduleTable);
            Run("Migration", () => Migration(scratch));
            Run("QueueModel", () => QueueModel(scratch));
            Run("Limits", () => Limits(scratch));
            Run("RunTogether", () => RunTogether(scratch));
            Run("RunnerWindow", () => RunnerWindow(scratch));
            Run("RunnerRestart", () => RunnerRestart(scratch));
            Run("RunnerDst", () => RunnerDst(scratch));
            Run("WhenDone", () => WhenDone(scratch));
            Run("Chooser", () => Chooser(scratch));
            Run("Sidebar", () => Sidebar(scratch));
            Run("Editor", () => Editor(scratch));
            Run("PreTasks", () => PreTasks(scratch));
            Run("PageResolverLogic", PageResolverLogic);
            Run("NeedsYouPanel", () => NeedsYouPanel(scratch));
            Run("PackageFlow", () => PackageFlow(scratch));
            Run("Locales", () => CheckLocales(QueueKeys, "1.16.0"));
            if (args.Contains("--e2e-resolver")) Run("E2EResolver", () => EndToEndResolver(scratch));
            if (args.Contains("--real-datanodes")) Run("RealDatanodes", () => RealDatanodes(scratch));
            Console.WriteLine($"V1160 TESTS PASSED: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { WindowPlacements.Configure(null); BusyQueue.Current = null; foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close(); try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } app.Shutdown(); }
    }

    private static readonly string[] QueueKeys =
    {
        "queue.main", "queue.section", "queue.new", "queue.default_name", "queue.heading", "queue.a11y.item", "queue.waiting", "queue.today", "queue.notice.saved", "queue.notice.moved",
        "queue.menu.move_to", "queue.menu.start", "queue.menu.stop", "queue.menu.edit", "queue.menu.move_up", "queue.menu.move_down", "queue.menu.delete", "queue.delete.title", "queue.delete.message",
        "queue.delete.move", "queue.delete.remove", "queue.chooser.title", "queue.chooser.which", "queue.chooser.new", "queue.chooser.new_name", "queue.chooser.busy", "queue.chooser.add",
        "queue.editor.title", "queue.editor.name", "queue.editor.limit", "queue.editor.limit_hint", "queue.editor.schedule", "queue.editor.days", "queue.editor.one_off", "queue.editor.start", "queue.editor.stop",
        "queue.editor.parallel", "queue.editor.when_done", "queue.editor.save", "queue.editor.err_name", "queue.editor.err_dup", "queue.editor.err_limit", "queue.editor.err_days", "queue.editor.err_time",
        "queue.editor.err_stop", "queue.editor.err_save", "queue.editor.and_stops",
    };
}
