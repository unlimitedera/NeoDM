using System.IO;
using System.Windows;
using System.Windows.Threading;
using NDM.App;
using NDM.Core;

internal sealed class SecurityApp : NDM.App.App
{
    public SecurityApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
    protected override void OnStartup(StartupEventArgs e) { }
}

/// <summary>1.5.3 security hardening in the app: update trust, installer folder rules, pipe, private-network warning, settings, clipboard.</summary>
internal static partial class SecurityTests
{
    internal static int Passed;
    internal static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        Passed++; Console.WriteLine("PASS: " + name);
    }
    internal static void Pump(int milliseconds = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    internal static void PumpUntil(Func<bool> condition, string what, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException(what); Pump(30); }
    }
    internal static T Sync<T>(Task<T> task) { PumpUntil(() => task.IsCompleted, "async operation", 120); return task.GetAwaiter().GetResult(); }
    internal static void Sync(Task task) { PumpUntil(() => task.IsCompleted, "async operation", 120); task.GetAwaiter().GetResult(); }
    internal static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }

    internal static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
        }
    }

    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new SecurityApp();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            string root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "neodm-security-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            try
            {
                SignatureTests(root);
                InstallerTrustTests(root);
                HostPinningTests(root);
                InstallerFolderTests(root);
                PipeTests();
                PrivateNetworkTests();
                DataAndSettingsTests(app, root);
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            Console.WriteLine($"RESULT {Passed} checks passed");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
