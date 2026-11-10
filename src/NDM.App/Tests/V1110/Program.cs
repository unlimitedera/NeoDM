global using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;

internal sealed class V1110App : Application
{
    public V1110App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}

/// <summary>1.11.0 (Part A): installer without the extension policy option, per-browser extension version check, magnet metadata. Mocked transports are component evidence.</summary>
internal static partial class V1110Tests
{
    private static int passed;
    internal static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    internal static void Pump(int ms = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    internal static T Wait<T>(Task<T> task) { var end = DateTime.UtcNow.AddSeconds(15); while (!task.IsCompleted && DateTime.UtcNow < end) Pump(20); Pump(10); if (!task.IsCompleted) throw new TimeoutException(); return task.GetAwaiter().GetResult(); }
    internal static void Wait(Task task) { var end = DateTime.UtcNow.AddSeconds(15); while (!task.IsCompleted && DateTime.UtcNow < end) Pump(20); Pump(10); if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); }
    internal static IEnumerable<T> Visual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Visual<T>(child)) yield return nested;
        }
    }
    internal static string RepoRoot
    {
        get { var dir = new DirectoryInfo(AppContext.BaseDirectory); while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent; return dir?.FullName ?? throw new Exception("repo root not found"); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-V1110Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        NDM.App.ThumbnailLoader.VideoWindowFactory = _ => null; // 1.13.0: no picture is fetched from the network in tests
        var app = new V1110App();
        string only = args.Length > 1 && args[0] == "--only" ? args[1] : "";
        try
        {
            Loc.SetLanguage("en");
            void Run(string name, Action a) { if (only.Length > 0 && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) return; Console.WriteLine("== " + name); a(); }
            Run("Item1.Installer", InstallerNoPolicy);
            Run("Item2.Compare", VersionTable);
            Run("Item2.Book", () => Book(scratch));
            Run("Item2.Notice", NoticeLimits);
            Run("Item2.Wire", WireCompat);
            Run("Item2.Dialog", () => CaptureHint(scratch));
            Run("Item2.Options", () => OptionsRows(scratch));
            Run("Item2.OptionsThemes", () => OptionsThemes(scratch));
            Run("Item2.Locales", LocaleKeys);
            Run("Item3.Setting", () => TrackerSetting(scratch));
            Run("Item3.Options", () => TrackerOptionsPage(scratch));
            Run("Item3.Dialog", () => AddDialogStates(scratch));
            Run("Item3.Locales", TorrentLocaleKeys);
            Run("ItemB.Routing", Routing);
            Run("ItemB.VideoDialog", () => VideoDialogStates(scratch));
            Run("ItemB.Model", PlaylistModelRules);
            Run("ItemB.Thumbnails", () => Thumbnails(scratch));
            Run("ItemB.PlaylistDialog", () => PlaylistDialogFlow(scratch));
            Run("ItemB.Virtualised", () => PlaylistVirtualised(scratch));
            Run("ItemB.ThumbRetry", () => PlaylistThumbnailRetry(scratch));
            Run("ItemB.Render", () => PlaylistRender(scratch));
            Run("ItemB.Locales", PartBLocales);
            Run("ItemC7.Power", () => C7Power(scratch));
            Run("ItemC9.Checkboxes", () => C9Checkboxes(scratch));
            Run("ItemC9.Contrast", C9Contrast);
            Run("ItemC9.Pixels", C9Pixels);
            Run("ItemC17.Queue", () => C17Queue(scratch));
            Run("ItemC.QueueCasing", QueueButtonCasing);
            Run("ItemC11.Taskbar", () => C11Taskbar(scratch));
            Run("ItemC12.Chevron", () => C12Chevron(scratch));
            Run("ItemC.YoutubeEntryPoints", () => YoutubeEntryPoints(scratch));
            Run("ItemC10.Pill", () => C10Pill(scratch));
            Run("ItemC18.Audit", () => C18Audit(scratch));
            Run("ItemC18.TitleBars", () => C18TitleBars(scratch));
            Run("ItemC16.Text", C16Text);
            Run("ItemC16.Control", C16Control);
            Run("ItemC16.Themes", () => C16Themes(scratch));
            Run("ItemC16.Usage", () => C16Usage(scratch));
            Run("Shots", () => Shots(scratch));
            Console.WriteLine($"V1110 TESTS PASSED: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(scratch, true); } catch (Exception) { } }
    }
}
