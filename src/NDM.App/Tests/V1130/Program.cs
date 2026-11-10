global using System.IO;
using System.Windows;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;

internal sealed class V1130App : Application
{
    public V1130App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}

/// <summary>1.13.0 tests (worker 1: completion window, start with Windows). Each item lives in its own partial file and is listed in Main.</summary>
internal static partial class V1130Tests
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
                if (info.Tag != "en") Check(v != english[k] || k.EndsWith(".maximized") || k == "neobrowser.a11y.tabs", $"{label} {info.Tag}: {k} is translated");
            }
        }
        Loc.SetLanguage("en");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-V1130Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var app = new V1130App();
        string only = args.Length > 1 && args[0] == "--only" ? args[1] : args.Contains("--real-playlist") ? "none-selected" : "";
        try
        {
            Loc.SetLanguage("en");
            void Run(string name, Action a) { if (only.Length > 0 && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) return; Console.WriteLine("== " + name); a(); }
            Run("CompletionWindow", () => CompletionWindow(scratch));
            Run("StartupLogic", () => StartupLogic(scratch));
            Run("StartupOptions", () => StartupOptions(scratch));
            Run("Thumbnails", () => ThumbnailCases(scratch));
            Run("BrowserAddress", BrowserAddressTable);
            Run("BrowserTabs", BrowserTabModel);
            Run("BrowserCapture", BrowserCaptureRouting);
            Run("BrowserMedia", BrowserMediaPickup);
            Run("BrowserHome", BrowserHomePage);
            Run("BrowserLocales", BrowserLocales);
            Run("BrowserRender", () => BrowserRender(scratch));
            Run("BrowserMenu", () => BrowserMenu(scratch));
            Run("BrowserEndToEnd", () => BrowserEndToEnd(scratch));
            Run("PickerWindow", () => PickerWindow(scratch));
            Run("PickerInteractions", () => PickerInteractions(scratch));
            Run("PlaylistVocabulary", () => PlaylistVocabulary(scratch));
            Run("StreamSubtitleRow", () => StreamDialogRow(scratch));
            Run("PickerLocales", PickerLocales);
            if (args.Contains("--real-playlist")) RealPlaylist(scratch, args);
            Console.WriteLine($"V1130 TESTS PASSED: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(scratch, true); } catch (Exception) { } }
    }
}
