global using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal sealed class V1100App : Application
{
    public V1100App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}

/// <summary>1.10.0: sidebar category list, file type icons, status coloured bars, downloading sweep, disk activity in the status bar.</summary>
internal static partial class V1100Tests
{
    private static int passed;
    internal static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    internal static void Pump(int ms = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    internal static void Wait(Task task) { var end = DateTime.UtcNow.AddSeconds(15); while (!task.IsCompleted && DateTime.UtcNow < end) Pump(20); Pump(10); if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); }
    internal static T Field<T>(Window w, string name) where T : class => (T)(w.FindName(name) ?? throw new Exception("missing control " + name));
    internal static IEnumerable<T> Visual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Visual<T>(child)) yield return nested;
        }
    }
    internal static RenderTargetBitmap Capture(FrameworkElement element, double dpi = 96)
    {
        element.UpdateLayout();
        int w = (int)Math.Ceiling(element.ActualWidth * dpi / 96), h = (int)Math.Ceiling(element.ActualHeight * dpi / 96);
        var bitmap = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
        if (element is Window || element.Parent is null && VisualTreeHelper.GetParent(element) is null) { bitmap.Render(element); return bitmap; }
        // an element inside a tree: draw it through a brush so its offset in the parent does not shift the picture
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(visual); return bitmap;
    }
    internal static byte[] Pixels(BitmapSource bmp) { var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4]; bmp.CopyPixels(px, bmp.PixelWidth * 4, 0); return px; }
    internal static void Save(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    internal static string? Shots => Environment.GetEnvironmentVariable("NEODM_SHOTS");
    internal static DownloadManager NewManager(string scratch, string name)
    {
        string state = Path.Combine(scratch, name, "NDM"); Directory.CreateDirectory(state); string files = Path.Combine(scratch, name, "files"); Directory.CreateDirectory(files);
        return new DownloadManager(files, state, new DownloadOptions { MaxConcurrentDownloads = 2, MaxSegments = 2, RetryCount = 0 });
    }
    internal static MainWindow NewMain(DownloadManager m, AppSettings s, Action? save = null, double width = 1180, double height = 720)
        => new(m, s, save ?? (() => { }), enableTray: false) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20, Width = width, Height = height };
    internal static void Press(UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
    }
    /// <summary>A detached job snapshot with chosen values (the setters are internal to the engine, so reflection fills them).</summary>
    internal static DownloadJob Job(string name, string status, long total = 1000, long done = 0, double speed = 0, string? url = null, bool torrent = false)
    {
        var job = new DownloadJob();
        void Set(string prop, object? value) => typeof(DownloadJob).GetProperty(prop)!.SetValue(job, value);
        Set("Id", Guid.NewGuid().ToString("N")); Set("FileName", name); Set("Url", url ?? "http://files.invalid/" + name); Set("Status", status);
        Set("TotalBytes", total); Set("DownloadedBytes", done); Set("SpeedBytesPerSecond", speed);
        return job;
    }
    internal static AppSettings Settings(string dir) => new() { DownloadDirectory = dir, ShowCompletionDialog = false, ShowProgressOnCompletion = false, CheckForUpdates = false };

    [STAThread]
    private static int Main(string[] args)
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-V1100Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var app = new V1100App();
        var realEnumerate = DiskUsage.Enumerate; var realResolve = DiskUsage.Resolve; var realFactory = PdhDiskActivity.Factory; var realMotion = Motion.SystemAnimations;
        string only = args.Length > 1 && args[0] == "--only" ? args[1] : "";
        try
        {
            Loc.SetLanguage("en");
            void Run(string name, Action a) { if (only.Length > 0 && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) return; Console.WriteLine("== " + name); a(); }
            Run("Item1.Model", () => CategoryModel(scratch));
            Run("Item1.Sidebar", () => Sidebar(scratch));
            Run("Item1.Keyboard", () => TreeKeyboard(scratch));
            Run("Item1.Themes", () => TreeThemes(scratch));
            Run("Item2.Mapping", IconMapping);
            Run("Item2.Contrast", IconContrast);
            Run("Item2.Render", IconRender);
            Run("Item2.Rows", () => IconRows(scratch));
            Run("Item3.Colours", BarColours);
            Run("Item3.Render", BarRender);
            Run("Item3.Transitions", BarTransitions);
            Run("Item5.Clock", () => WaveClock(scratch));
            Run("Item5.Look", WaveLook);
            Run("Item5.Settings", () => WaveSettings(scratch));
            Run("Item4.Monitor", DiskMonitor);
            Run("Item4.Bar", () => DiskBar(scratch));
            Run("Item4.Narrow", () => DiskNarrow(scratch));
            Console.WriteLine($"V1100 TESTS PASSED: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally
        {
            DiskUsage.Enumerate = realEnumerate; DiskUsage.Resolve = realResolve; PdhDiskActivity.Factory = realFactory; Motion.SystemAnimations = realMotion;
            try { Directory.Delete(scratch, true); } catch (Exception) { }
        }
    }
}
