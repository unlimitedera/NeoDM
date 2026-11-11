global using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal sealed class V1132App : Application
{
    public V1132App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}

/// <summary>1.13.2 tests: proxy scope per purpose (settings, fixture proxy proof per purpose, Options page), thumbnails under a slow connection, Open on installers.
/// Each item lives in its own partial file and is listed in Main.</summary>
internal static partial class V1132Tests
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
                if (info.Tag != "en") Check(v != english[k] || k == "options.proxy.name_torrents", $"{label} {info.Tag}: {k} is translated");
            }
        }
        Loc.SetLanguage("en");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-V1132Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var app = new V1132App();
        string only = args.Length > 1 && args[0] == "--only" ? args[1] : "";
        try
        {
            Loc.SetLanguage("en");
            void Run(string name, Action a) { if (only.Length > 0 && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) return; Console.WriteLine("== " + name); a(); }
            Run("ProxySettings", () => ProxySettings(scratch));
            Run("ProxyPurposes", () => ProxyPurposes(scratch));
            Run("ProxyOptionsPage", () => ProxyOptionsPage(scratch));
            Run("ProxyLocales", ProxyLocales);
            Run("Thumbnails", () => SlowThumbnails(scratch));
            Run("OpenInstaller", () => OpenInstaller(scratch));
            Run("DriveMessage", DriveMessage);
            Run("MagnetHint", () => MagnetHint(scratch));
            Console.WriteLine($"V1132 TESTS PASSED: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(scratch, true); } catch (Exception) { } }
    }
}
