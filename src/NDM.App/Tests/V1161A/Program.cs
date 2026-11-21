// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
global using System.IO;
using System.Windows;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal sealed class V1161AApp : Application
{
    public V1161AApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}

/// <summary>1.16.1 worker A tests: the form-based host resolver (parse, timer, captcha kind, links), the fixture host flow, the loopback browser step and the page-links window.</summary>
internal static partial class V1161ATests
{
    private static int passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static string Fx(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static void Pump(int ms = 60)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-V1161ATests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch); Environment.SetEnvironmentVariable("NDM_STATE_DIR", Path.Combine(scratch, "NDM"));
        var app = new V1161AApp();
        try
        {
            Loc.SetLanguage("en");
            void Run(string name, Action a) { Console.WriteLine("== " + name); TestGuard.Run(name, a); }
            if (args.Contains("--widget")) { Run("WidgetRender", () => WidgetRender(scratch)); return 0; } // opt-in: needs the network and the WebView2 runtime
            if (args.Contains("--real")) { RealCheck(scratch); return 0; } // opt-in: real network, plain requests up to the captcha step only
            Run("Parse", Parse);
            Run("Flow", Flow);
            Run("Listener", Listener);
            Run("BrowserStep", BrowserStep);
            Run("EndToEnd", EndToEnd);
            Run("BoundKey", BoundKey);
            Run("Integration", Integration);
            Run("WindowButtons", WindowButtons);
            Console.WriteLine($"V1161A TESTS PASSED: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close(); try { Directory.Delete(scratch, true); } catch (IOException) { } app.Shutdown(); }
    }
}
