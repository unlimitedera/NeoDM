global using System.IO;
using System.Net.Http;
using System.Windows;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal sealed class V1162App : Application
{
    public V1162App() => ShutdownMode = ShutdownMode.OnExplicitShutdown;
}

/// <summary>1.16.2 tests: the real press (geometry, pixel ratios, screens, focus retry, event order), the loopback page that reports its widget,
/// the browser loop (rewrite, cookies, assets) and the step that presses by itself. No real mouse and no real browser: every system call is faked.</summary>
internal static partial class V1162Tests
{
    private static int passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static void Throws<T>(Action a, string name) where T : Exception
    {
        try { a(); } catch (T) { passed++; Console.WriteLine("PASS: " + name); return; }
        throw new Exception("FAIL: " + name);
    }
    private static string Fx(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [STAThread]
    private static int Main(string[] args)
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-V1162Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch); Environment.SetEnvironmentVariable("NDM_STATE_DIR", Path.Combine(scratch, "NDM"));
        var app = new V1162App();
        try
        {
            Loc.SetLanguage("en");
            if (args.Contains("--dump")) { Dump(); return 0; }
            void Run(string name, Action a) { Console.WriteLine("== " + name); TestGuard.Run(name, a); }
            Run("Dpi", Dpi);
            Run("Screens", Screens);
            Run("Focus", Focus);
            Run("Sequence", Sequence);
            Run("Report", Report);
            Run("Discovery", Discovery);
            Run("Rewrite", Rewrite);
            Run("Cookies", Cookies);
            Run("Page", Page);
            Run("Assets", Assets);
            Run("Press", Press);
            Run("KeyRetry", KeyRetry);
            Run("AutoPress", AutoPress);
            Run("AutoGiveUp", AutoGiveUp);
            Run("AutoOff", AutoOff);
            Run("AutoMatching", AutoMatching);
            Run("AutoEngine", AutoEngine);
            Run("AutoWire", AutoWire);
            Console.WriteLine($"V1162 TESTS PASSED: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(scratch, true); } catch (IOException) { } app.Shutdown(); }
    }
}

internal static partial class V1162Tests
{
    /// <summary>Writes the two page shapes out so the script text can be checked by a JS parser.</summary>
    private static void Dump()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "dump"); Directory.CreateDirectory(dir);
        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using (var l = new NDM.Core.CaptchaListener(new HostCaptcha(HostCaptchaKind.Turnstile, "0x4AAAAAAAfixtureturnstilekey01"), new CaptchaPageText("T", "H", "B", "D", "F")))
            File.WriteAllText(Path.Combine(dir, "plain.html"), c.GetStringAsync(l.Url).GetAwaiter().GetResult());
        var source = new Source(); source.Page = "<!DOCTYPE html><html><head></head><body><div class=\"cf-turnstile\" data-sitekey=\"0x4AAAAAAAfixtureturnstilekey01\"></div></body></html>"u8.ToArray();
        using (var l = new NDM.Core.CaptchaListener(new NDM.Core.CaptchaPageSpec(HostCaptchaKind.Turnstile, "0x4AAAAAAAfixtureturnstilekey01", null, new NDM.Core.Captcha.LoopbackPageProxy(new Uri("https://host.example/d/abc"), source)), new CaptchaPageText("T", "H", "B", "D", "F")))
            File.WriteAllText(Path.Combine(dir, "loop.html"), c.GetStringAsync(l.Url).GetAwaiter().GetResult());
        Console.WriteLine(dir);
    }
}
