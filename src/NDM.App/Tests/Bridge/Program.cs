global using System.IO;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NDM.App;
using NDM.Core;

internal sealed class BridgeTestApp : Application
{
    public BridgeTestApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}


/// <summary>Records activation calls so the tests never steal the real foreground.</summary>
internal sealed class FakeNative : IWindowNative
{
    public int Activations;
    private IntPtr foreground;
    public IntPtr Foreground { set => foreground = value; }
    public IntPtr ForegroundWindow() => foreground;
    public uint ThreadOf(IntPtr window) => 1;
    public uint CurrentThread() => 2;
    public bool IsMinimized(IntPtr window) => false;
    public void Restore(IntPtr window) { }
    public bool AttachThreadInput(uint from, uint to, bool attach) => true;
    public bool SetForeground(IntPtr window) { foreground = window; Activations++; return true; }
    public void BringToTop(IntPtr window) { }
    public void TapAlt() { }
    public void Flash(IntPtr window) { }
}

// ---------- fakes: nothing here touches the real registry, the real disk or a real browser ----------

internal sealed class FakeRegistry : IRegistryAccess
{
    public Dictionary<(RegistryScope, string), string> Values { get; } = new();
    public List<string> Writes { get; } = new();
    public List<string> Deletes { get; } = new();
    public int Reads;
    public string? GetDefaultValue(RegistryScope scope, string subKey) { Reads++; return Values.TryGetValue((scope, subKey.ToLowerInvariant()), out var v) ? v : null; }
    public void SetDefaultValue(RegistryScope scope, string subKey, string value)
    {
        if (scope != RegistryScope.CurrentUser) throw new InvalidOperationException("the watchdog must only write the current user hive");
        Values[(scope, subKey.ToLowerInvariant())] = value; Writes.Add(subKey);
    }
    public bool DeleteSubKeyTree(RegistryScope scope, string subKey)
    {
        if (scope != RegistryScope.CurrentUser) throw new InvalidOperationException("the watchdog must only delete in the current user hive");
        Deletes.Add(subKey);
        return Values.Remove((scope, subKey.ToLowerInvariant()));
    }
    public void Set(RegistryScope scope, string subKey, string value) => Values[(scope, subKey.ToLowerInvariant())] = value;
}

internal sealed class FakeFiles : IFileSystemAccess
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Writes { get; } = new();
    public int Reads;
    public bool FileExists(string path) => Files.ContainsKey(path);
    public string? ReadAllText(string path) { Reads++; return Files.TryGetValue(path, out var t) ? t : null; }
    public void WriteAllText(string path, string text) { Files[path] = text; Writes.Add(path); }
}

internal sealed class FakeLauncher : IHostLauncher
{
    public HostProbeResult Result = new(true, Elapsed: TimeSpan.FromMilliseconds(40), AppVersion: "1.4.7");
    public int Calls, Active, MaxActive;
    public int DelayMs;
    public string? LastExe, LastOrigin;
    public async Task<HostProbeResult> ProbeAsync(string hostExe, string origin, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls++; LastExe = hostExe; LastOrigin = origin;
        int now = Interlocked.Increment(ref Active); MaxActive = Math.Max(MaxActive, now);
        try { if (DelayMs > 0) await Task.Delay(DelayMs); return Result; }
        finally { Interlocked.Decrement(ref Active); }
    }
}

internal sealed class FakePipe : IPipeProbe
{
    public bool Alive = true;
    public int Calls;
    public Task<bool> PingAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken) { Interlocked.Increment(ref Calls); return Task.FromResult(Alive); }
}

internal sealed class FakeMachine : IMachineState
{
    public bool Busy, Saver, HostRunning;
    public int Asked;
    public bool IsBusy() { Asked++; return Busy; }
    public bool IsPowerSaver() => Saver;
    public bool BrowserHostRunning() => HostRunning;
}

internal sealed class Rig
{
    public const string Host = @"C:\NeoDM\host\NDM.NativeHost.exe";
    public const string Config = @"C:\NeoDM\host\bridge-config.json";
    public const string Machine = @"C:\NeoDM\host\com.ndm.bridge.json";
    public const string User = @"C:\Users\test\AppData\Local\NDM\host\com.ndm.bridge.json";
    public const string DevId = "hohmmojlhbkelkobobpkochpdempcihd";
    public const string OtherId = "abcdefghijklmnopabcdefghijklmnop";
    public FakeRegistry Reg = new(); public FakeFiles Files = new(); public FakeLauncher Launcher = new(); public FakePipe Pipe = new(); public FakeMachine Mach = new();
    public DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    public int Recreated; public bool RecreateFixes = true;
    public BridgeWatchdog Dog = null!;
    public List<string> Log = new();
    public List<string> BrowsersInstalled = new();

    public Rig(bool chrome = true, bool edge = true, bool brave = true, bool machineRegistration = true, bool hostExists = true, bool allowRepairs = true, bool withMachine = false)
    {
        if (hostExists) { Files.Files[Host] = "exe"; Files.Files[Config] = "{\"extensionId\":\"" + Rig.DevId + "\",\"appPath\":\"C:\\\\NeoDM\\\\NeoDM.exe\"}"; }
        Files.Files[Machine] = BridgeWatchdog.BuildManifest(Host, new[] { DevId });
        foreach (var (b, on) in new[] { (BrowserTarget.Chrome, chrome), (BrowserTarget.Edge, edge), (BrowserTarget.Brave, brave) })
        {
            if (!on) continue;
            Install(b);
            if (machineRegistration) Reg.Set(RegistryScope.LocalMachine, b.HostKey, Machine);
        }
        Reg.Set(RegistryScope.LocalMachine, "x", "x");
        var env = new WatchdogEnvironment { HostCandidates = new[] { Host, @"C:\other\host\NDM.NativeHost.exe" }, UserManifestPath = User, PipeName = "NDM.Downloads.test", ProgramDirectories = new[] { @"C:\Program Files" }, AllowRepairs = allowRepairs };
        Dog = new BridgeWatchdog(env, Reg, Files, Launcher, Pipe, () => { Recreated++; if (RecreateFixes) Pipe.Alive = true; return Task.FromResult(true); }, () => Now, Log.Add, withMachine ? Mach : null);
    }

    public void Install(BrowserTarget b)
    {
        string exe = @"C:\Browsers\" + b.ExeName;
        Files.Files[exe] = "exe";
        Reg.Set(RegistryScope.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + b.ExeName, exe);
        BrowsersInstalled.Add(b.Id);
    }

    public Task<WatchdogReport> Run(WatchdogRunMode mode = WatchdogRunMode.CheckNow) => Dog.RunAsync(mode);
    public static CheckItem Item(WatchdogReport r, string id) => r.Items.Single(i => i.Id == id);
}

internal sealed class FakeDog : IBridgeWatchdog
{
    public WatchdogReport? Last { get; set; }
    public bool IsRunning => false;
    public List<WatchdogRunMode> Modes { get; } = new();
    public WatchdogReport Next = BridgeTests.Sample(WatchdogOverall.Ok);
    public event Action<WatchdogReport>? ReportChanged;
    public Task<WatchdogReport> RunAsync(WatchdogRunMode mode) { Modes.Add(mode); Last = Next; ReportChanged?.Invoke(Next); return Task.FromResult(Next); }
}

internal sealed class FakeHostProcess : IHostProcess
{
    public static int Alive, MaxAlive;
    public enum Script { Reply, BadReply, EarlyExit, Silent }
    private readonly TaskCompletionSource _killed = new();
    private readonly Script _script;
    public int Kills; public bool Exited; public int Code;
    public MemoryStream Input { get; } = new();
    public FakeHostProcess(Script script, int delayMs = 0)
    {
        _script = script;
        int now = Interlocked.Increment(ref Alive); lock (typeof(FakeHostProcess)) MaxAlive = Math.Max(MaxAlive, now);
        StandardOutput = script switch
        {
            Script.Reply => new DelayedStream(Frame("{\"ok\":true,\"host\":\"com.ndm.bridge\",\"keepAlive\":true,\"appVersion\":\"1.4.7\"}"), delayMs, _killed.Task),
            Script.BadReply => new DelayedStream(Frame("{\"ok\":false}"), 0, _killed.Task),
            Script.EarlyExit => new MemoryStream(),
            _ => new DelayedStream(Array.Empty<byte>(), -1, _killed.Task)
        };
        if (script == Script.EarlyExit) { Exited = true; Code = 7; }
    }
    public static byte[] Frame(string json) { var body = Encoding.UTF8.GetBytes(json); var f = new byte[4 + body.Length]; BitConverter.GetBytes((uint)body.Length).CopyTo(f, 0); body.CopyTo(f, 4); return f; }
    public Stream StandardInput => Input;
    public Stream StandardOutput { get; }
    public bool HasExited => Exited;
    public int ExitCode => Code;
    public TimeSpan CpuTime => TimeSpan.FromMilliseconds(5);
    public Task<string?> ReadFirstStderrLineAsync() => Task.FromResult<string?>(_script == Script.EarlyExit ? "The framework was not found" : null);
    public bool WaitForExit(int milliseconds) => Exited;
    public void Kill() { Kills++; Exited = true; _killed.TrySetResult(); }
    public void Dispose() { Interlocked.Decrement(ref Alive); }

    /// <summary>Hands out the bytes after a delay; a negative delay never answers until the process is killed.</summary>
    private sealed class DelayedStream(byte[] data, int delayMs, Task killed) : Stream
    {
        private int _position;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (delayMs < 0) { await killed; return 0; }
            if (delayMs > 0 && _position == 0) await Task.Delay(delayMs, cancellationToken);
            int n = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, n).CopyTo(buffer); _position += n; return n;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}

internal static class BridgeTests
{
    private static int passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static void Pump(int ms = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Click(System.Windows.Controls.Primitives.ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    private static IEnumerable<T> Visual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Visual<T>(child)) yield return nested;
        }
    }
    private static T Wait<T>(Task<T> task) { if (!task.Wait(10000)) throw new Exception("timeout"); return task.Result; }
    private static bool Until(Func<bool> condition, int ms = 5000) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { if (condition()) return true; Thread.Sleep(10); } return condition(); }

    public static WatchdogReport Sample(WatchdogOverall overall) => new(overall, new[]
    {
        new CheckItem("listener", "App listener", CheckState.Ok, "NeoDM is listening for the browser.", "The pipe NDM.Downloads answered the self check."),
        new CheckItem("reg-chrome", "Chrome registration", overall == WatchdogOverall.Problem ? CheckState.Problem : CheckState.Ok, overall == WatchdogOverall.Problem ? "Chrome points to a NeoDM file that is missing." : "Chrome is connected to NeoDM.", "C:\\NeoDM\\host\\com.ndm.bridge.json"),
        new CheckItem("reg-edge", "Edge registration", CheckState.NotInstalled, "Edge is not installed."),
        new CheckItem("reg-brave", "Brave registration", overall == WatchdogOverall.Repaired ? CheckState.Repaired : CheckState.Ok, "Brave is connected to NeoDM.", "detail"),
        new CheckItem("host", "Host start test", CheckState.Ok, "The browser helper starts and answers.", "Answered in 40 ms.")
    }, new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc), overall == WatchdogOverall.Repaired ? new[] { "Registered NeoDM for Brave for the current user." } : Array.Empty<string>());

    [STAThread]
    private static int Main()
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-BridgeTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var app = new BridgeTestApp();
        try
        {
            Healthy(); Listener(); StaleUserKey(); MissingOrigins(); NoBrowsers(); HostFailures(); LauncherKill(); RateLimits(); Overlap(); Timer(); Backoff(); Skips(); ReportText(); Notices();
            SelfCheck(); SettingDefault(scratch); StatusWindow(); TrayAlwaysVisible(scratch); Budget(); RealHost(scratch);
            Console.WriteLine($"Bridge tests passed: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close();
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            app.Shutdown();
        }
    }

    // ===== healthy =====
    private static void Healthy()
    {
        var r = new Rig();
        var report = Wait(r.Run());
        Check(report.Overall == WatchdogOverall.Ok && report.RepairsMade.Count == 0, "healthy: overall Ok, no repairs");
        Check(report.Items.Select(i => i.Id).SequenceEqual(new[] { "listener", "reg-chrome", "reg-edge", "reg-brave", "host" }), "healthy: items are listener, Chrome, Edge, Brave, host test (Chromium only when installed)");
        Check(report.Items.All(i => i.State == CheckState.Ok), "healthy: every item Ok");
        Check(r.Reg.Writes.Count == 0 && r.Reg.Deletes.Count == 0 && r.Files.Writes.Count == 0, "healthy: nothing written or deleted");
        Check(r.Launcher.Calls == 1 && r.Launcher.LastExe == Rig.Host && r.Launcher.LastOrigin == "chrome-extension://" + Rig.DevId + "/", "healthy: host test starts the installed host with the origin argument");
        Check(r.Dog.Last == report && Rig.Item(report, "reg-chrome").Detail.Contains(Rig.Machine), "healthy: last report kept, detail names the manifest");
        Check(Rig.Item(report, "host").Detail.Contains("1.4.7"), "healthy: host test detail shows the app version from the reply");
        var chromium = new Rig(); chromium.Install(BrowserTarget.Chromium);
        Check(Wait(chromium.Run()).Items.Any(i => i.Id == "reg-chromium" && i.State == CheckState.Repaired), "Chromium is listed when installed (unregistered, it gets a user registration)");
    }

    // ===== listener =====
    private static void Listener()
    {
        var r = new Rig(); r.Pipe.Alive = false;
        var report = Wait(r.Run());
        Check(r.Recreated == 1 && report.Overall == WatchdogOverall.Repaired && Rig.Item(report, "listener").State == CheckState.Repaired, "listener dead then recreated: Repaired after one restart");
        Check(report.RepairsMade.Any(x => x.Contains("listener")), "listener repair is listed");
        var stuck = new Rig(); stuck.Pipe.Alive = false; stuck.RecreateFixes = false;
        var bad = Wait(stuck.Run());
        Check(stuck.Recreated == 1 && bad.Overall == WatchdogOverall.Problem && Rig.Item(bad, "listener").State == CheckState.Problem, "listener that stays dead: one restart only, then a Problem (no loop)");
        Check(Rig.Item(bad, "listener").Detail.Contains("even after"), "listener problem detail says the restart did not help");
        var off = new Rig(allowRepairs: false); off.Pipe.Alive = false;
        Check(Wait(off.Run()).Overall == WatchdogOverall.Problem && off.Recreated == 0, "listener: repairs off in isolated runs, only reported");
    }

    // ===== registration repairs =====
    private static void StaleUserKey()
    {
        string chromeKey = BrowserTarget.Chrome.HostKey;
        var r = new Rig();
        r.Reg.Set(RegistryScope.CurrentUser, chromeKey, @"C:\Old\gone.json");
        var report = Wait(r.Run());
        Check(r.Reg.Deletes.SequenceEqual(new[] { chromeKey }), "stale user key: only the Chrome com.ndm.bridge key is deleted");
        Check(r.Reg.Writes.Count == 0 && r.Files.Writes.Count == 0, "stale user key with a good machine key: nothing is written");
        Check(Rig.Item(report, "reg-chrome").State == CheckState.Repaired && report.Overall == WatchdogOverall.Repaired && Rig.Item(report, "reg-edge").State == CheckState.Ok, "stale user key: Chrome Repaired, Edge untouched");

        var noMachine = new Rig(machineRegistration: false);
        noMachine.Reg.Set(RegistryScope.CurrentUser, chromeKey, @"C:\Old\gone.json");
        var fixedReport = Wait(noMachine.Run());
        Check(noMachine.Files.Writes.Contains(Rig.User), "stale user key and no machine key: user manifest written");
        Check(noMachine.Reg.Values[(RegistryScope.CurrentUser, chromeKey.ToLowerInvariant())] == Rig.User, "stale user key and no machine key: Chrome registered for the current user with the user manifest");
        using var doc = JsonDocument.Parse(noMachine.Files.Files[Rig.User]); var root = doc.RootElement;
        var origins = root.GetProperty("allowed_origins").EnumerateArray().Select(x => x.GetString()).ToList();
        Check(root.GetProperty("name").GetString() == "com.ndm.bridge" && root.GetProperty("type").GetString() == "stdio" && root.GetProperty("path").GetString() == Rig.Host && origins.Contains("chrome-extension://" + Rig.DevId + "/"), "user manifest content: name, type stdio, installed host path, development origin");
        Check(Rig.Item(fixedReport, "reg-chrome").State == CheckState.Repaired && Rig.Item(fixedReport, "reg-edge").State == CheckState.Repaired && Rig.Item(fixedReport, "reg-brave").State == CheckState.Repaired, "no registration at all: every installed browser gets a user registration");
        Check(noMachine.Reg.Writes.All(w => w.EndsWith(@"NativeMessagingHosts\com.ndm.bridge")) && noMachine.Reg.Writes.Count == 3, "registry writes are only the three com.ndm.bridge keys");
        var again = Wait(noMachine.Run(WatchdogRunMode.Scheduled));
        Check(again.Overall == WatchdogOverall.Ok && again.RepairsMade.Count == 0, "after the repair the next check is Ok");

        var host = new Rig(hostExists: false, machineRegistration: false);
        var missing = Wait(host.Run());
        Check(host.Reg.Writes.Count == 0 && host.Files.Writes.Count == 0 && missing.Overall == WatchdogOverall.Problem && Rig.Item(missing, "reg-chrome").Summary.Contains("helper is missing"), "host exe missing: nothing is written, Problem says the helper is missing");
        Check(Rig.Item(missing, "host").State == CheckState.Problem && Rig.Item(missing, "host").Summary.Contains("file is missing"), "host exe missing: host test reports the missing file");
    }

    private static void MissingOrigins()
    {
        var r = new Rig();
        r.Files.Files[Rig.Machine] = BridgeWatchdog.BuildManifest(Rig.Host, new[] { Rig.OtherId });
        var report = Wait(r.Run());
        Check(r.Files.Writes.Contains(Rig.User) && Rig.Item(report, "reg-chrome").State == CheckState.Repaired, "manifest without the known id: repaired through the user manifest");
        Check(r.Files.Files[Rig.User].Contains(Rig.DevId), "the user manifest allows the known id");
        var extra = new Rig();
        extra.Files.Files[Rig.Machine] = BridgeWatchdog.BuildManifest(Rig.Host, new[] { Rig.DevId, Rig.OtherId });
        Check(Wait(extra.Run()).Overall == WatchdogOverall.Ok && extra.Files.Writes.Count == 0, "manifest that lists more ids than the app knows is fine");
        var wrongPath = new Rig();
        wrongPath.Files.Files[Rig.Machine] = BridgeWatchdog.BuildManifest(@"C:\Gone\NDM.NativeHost.exe", new[] { Rig.DevId });
        var wp = Wait(wrongPath.Run());
        Check(wrongPath.Files.Writes.Contains(Rig.User) && Rig.Item(wp, "reg-edge").State == CheckState.Repaired, "manifest whose host path is missing: repaired through the user manifest");
        var elsewhere = new Rig(); elsewhere.Files.Files[@"C:\Old\NDM.NativeHost.exe"] = "exe";
        elsewhere.Files.Files[Rig.Machine] = BridgeWatchdog.BuildManifest(@"C:\Old\NDM.NativeHost.exe", new[] { Rig.DevId });
        Check(Rig.Item(Wait(elsewhere.Run()), "reg-chrome").State == CheckState.Repaired, "manifest that starts another helper than the installed one: repaired");
        var broken = new Rig(); broken.Files.Files[Rig.Machine] = "{ not json";
        Check(Rig.Item(Wait(broken.Run()), "reg-chrome").State == CheckState.Repaired, "damaged manifest: repaired");
        var typeBad = new Rig(); typeBad.Files.Files[Rig.Machine] = BridgeWatchdog.BuildManifest(Rig.Host, new[] { Rig.DevId }).Replace("stdio", "other");
        Check(Rig.Item(Wait(typeBad.Run()), "reg-chrome").State == CheckState.Repaired, "manifest with the wrong type: repaired");
        var configured = new Rig(); configured.Files.Files[Rig.Config] = "{\"extensionId\":\"" + Rig.OtherId + "\"}";
        var cfg = Wait(configured.Run());
        Check(configured.Launcher.LastOrigin == "chrome-extension://" + Rig.OtherId + "/" && Rig.Item(cfg, "reg-chrome").State == CheckState.Repaired && configured.Files.Files[Rig.User].Contains(Rig.OtherId) && configured.Files.Files[Rig.User].Contains(Rig.DevId), "id from bridge-config.json joins the built-in list and is used for the host test");
    }

    private static void NoBrowsers()
    {
        var r = new Rig(chrome: false, edge: false, brave: false, machineRegistration: false);
        var report = Wait(r.Run());
        Check(report.Overall == WatchdogOverall.Ok && r.Reg.Writes.Count == 0 && r.Reg.Deletes.Count == 0 && r.Files.Writes.Count == 0, "no browsers installed: no repair writes and not a Problem");
        Check(report.Items.Where(i => i.Id.StartsWith("reg-")).All(i => i.State == CheckState.NotInstalled), "no browsers installed: registrations show Not installed");
        Check(r.Launcher.Calls == 0 && Rig.Item(report, "host").State == CheckState.NotInstalled, "no browsers installed: the host is not started");
        var stale = new Rig(chrome: false, edge: false, brave: false, machineRegistration: false);
        stale.Reg.Set(RegistryScope.CurrentUser, BrowserTarget.Edge.HostKey, @"C:\Old\gone.json");
        Wait(stale.Run());
        Check(stale.Reg.Deletes.SequenceEqual(new[] { BrowserTarget.Edge.HostKey }) && stale.Files.Writes.Count == 0, "browser not installed but a stale user key exists: only the key is removed");
    }

    // ===== host start test mapping =====
    private static void HostFailures()
    {
        var cases = new (HostProbeResult Result, string Summary, string Detail)[]
        {
            (new(false, HostProbeFailure.ExeMissing), "file is missing", ""),
            (new(false, HostProbeFailure.ExitedEarly, 2, "Cannot find runtime"), "stopped right after", "Exit code 2"),
            (new(false, HostProbeFailure.NoReply), "did not answer within 3 seconds", ""),
            (new(false, HostProbeFailure.BadReply, Detail: "The reply was not valid JSON."), "unexpected answer", "not valid JSON"),
            (new(false, HostProbeFailure.StartFailed, Detail: "The specified executable is not a valid application"), "could not start", "not a valid application"),
        };
        foreach (var (result, summary, detail) in cases)
        {
            var r = new Rig(); r.Launcher.Result = result;
            var report = Wait(r.Run());
            var item = Rig.Item(report, "host");
            Check(report.Overall == WatchdogOverall.Problem && item.State == CheckState.Problem && item.Summary.Contains(summary) && item.Detail.Contains(detail), "host test " + result.Failure + " is a Problem with the reason: " + item.Summary);
        }
        var early = new Rig(); early.Launcher.Result = new(false, HostProbeFailure.ExitedEarly, 2, "Cannot find runtime");
        Check(Rig.Item(Wait(early.Run()), "host").Detail.Contains("Cannot find runtime") && Rig.Item(Wait(early.Run()), "host").Detail.Contains("does not accept the extension id"), "early exit detail has the first stderr line and the meaning of code 2");
    }

    private static void LauncherKill()
    {
        FakeHostProcess? last = null;
        HostLauncher Make(FakeHostProcess.Script script, int delay = 0) => new((_, _) => last = new FakeHostProcess(script, delay), _ => true);
        var ok = Wait(Make(FakeHostProcess.Script.Reply).ProbeAsync("h.exe", "o", TimeSpan.FromSeconds(2), default));
        Check(ok.Ok && ok.AppVersion == "1.4.7" && last!.Kills == 1 && last.Input.ToArray().Length > 4 && Encoding.UTF8.GetString(last.Input.ToArray(), 4, last.Input.ToArray().Length - 4) == "{\"action\":\"ping\"}", "launcher: sends one framed ping, ok reply, kills the child it started exactly once");
        var early = Wait(Make(FakeHostProcess.Script.EarlyExit).ProbeAsync("h.exe", "o", TimeSpan.FromSeconds(2), default));
        Check(!early.Ok && early.Failure == HostProbeFailure.ExitedEarly && early.ExitCode == 7 && early.StderrLine == "The framework was not found" && last!.Kills == 0, "launcher: early exit reports code and first stderr line and kills nothing");
        var sw = Stopwatch.StartNew();
        var silent = Wait(Make(FakeHostProcess.Script.Silent).ProbeAsync("h.exe", "o", TimeSpan.FromMilliseconds(400), default));
        Check(!silent.Ok && silent.Failure == HostProbeFailure.NoReply && last!.Kills == 1 && sw.ElapsedMilliseconds < 2500, "launcher: no reply ends at the timeout and kills the child once");
        var bad = Wait(Make(FakeHostProcess.Script.BadReply).ProbeAsync("h.exe", "o", TimeSpan.FromSeconds(2), default));
        Check(!bad.Ok && bad.Failure == HostProbeFailure.BadReply && last!.Kills == 1, "launcher: unexpected reply is BadReply, child killed");
        var missing = Wait(new HostLauncher((_, _) => throw new Exception("must not start"), _ => false).ProbeAsync("h.exe", "o", TimeSpan.FromSeconds(1), default));
        Check(missing.Failure == HostProbeFailure.ExeMissing, "launcher: missing exe is reported without starting anything");
        var denied = Wait(new HostLauncher((_, _) => throw new System.ComponentModel.Win32Exception("blocked by policy"), _ => true).ProbeAsync("h.exe", "o", TimeSpan.FromSeconds(1), default));
        Check(denied.Failure == HostProbeFailure.StartFailed && denied.Detail!.Contains("blocked by policy"), "launcher: a start error is StartFailed with the message");
        FakeHostProcess.MaxAlive = 0;
        var slow = new HostLauncher((_, _) => new FakeHostProcess(FakeHostProcess.Script.Reply, 150), _ => true);
        var both = Task.WhenAll(slow.ProbeAsync("h", "o", TimeSpan.FromSeconds(3), default), slow.ProbeAsync("h", "o", TimeSpan.FromSeconds(3), default), slow.ProbeAsync("h", "o", TimeSpan.FromSeconds(3), default));
        both.Wait(10000);
        Check(FakeHostProcess.MaxAlive == 1 && both.Result.All(x => x.Ok), "launcher: three simultaneous probes never have more than one child alive");
    }

    // ===== limits =====
    private static void RateLimits()
    {
        var r = new Rig(machineRegistration: false);
        Wait(r.Run(WatchdogRunMode.Scheduled));
        int writes = r.Reg.Writes.Count;
        Check(writes == 3, "rate limit: first run repairs three browsers");
        // break Chrome again right away
        r.Reg.Values.Remove((RegistryScope.CurrentUser, BrowserTarget.Chrome.HostKey.ToLowerInvariant()));
        r.Now = r.Now.AddMinutes(2);
        var limited = Wait(r.Run(WatchdogRunMode.Scheduled));
        Check(r.Reg.Writes.Count == writes && Rig.Item(limited, "reg-chrome").State == CheckState.Problem && Rig.Item(limited, "reg-chrome").Detail.Contains("moment ago"), "rate limit: the same problem is not repaired again within 10 minutes");
        var forced = Wait(r.Run(WatchdogRunMode.Repair));
        Check(r.Reg.Writes.Count == writes + 1 && Rig.Item(forced, "reg-chrome").State == CheckState.Repaired, "Repair bypasses the rate limit");
        r.Reg.Values.Remove((RegistryScope.CurrentUser, BrowserTarget.Chrome.HostKey.ToLowerInvariant()));
        r.Now = r.Now.AddMinutes(11);
        Wait(r.Run(WatchdogRunMode.Scheduled));
        Check(r.Reg.Writes.Count == writes + 2, "rate limit: allowed again after 10 minutes");
        // host test cadence
        var c = new Rig();
        Wait(c.Run(WatchdogRunMode.Scheduled)); c.Now = c.Now.AddMinutes(1); Wait(c.Run(WatchdogRunMode.CaptureFailure));
        Check(c.Launcher.Calls == 1, "host test cadence: a check one minute later reuses the last host result");
        c.Now = c.Now.AddMinutes(5); Wait(c.Run(WatchdogRunMode.Scheduled));
        Check(c.Launcher.Calls == 2, "host test cadence: tested again after 5 minutes");
        Wait(c.Run(WatchdogRunMode.CheckNow));
        Check(c.Launcher.Calls == 3, "Check now always starts the host");
        // capture failure gap
        var g = new Rig(); Wait(g.Run(WatchdogRunMode.Scheduled));
        Check(!g.Dog.NotifyCaptureProblem(), "capture failure within 60 seconds of the last check is ignored");
        g.Now = g.Now.AddSeconds(61); int before = g.Dog.RunCount;
        Check(g.Dog.NotifyCaptureProblem() && Until(() => g.Dog.RunCount == before + 1 && !g.Dog.IsRunning), "capture failure after 60 seconds starts a check");
        Check(!g.Dog.NotifyCaptureProblem(), "and the next one inside the gap is ignored again");
    }

    private static void Overlap()
    {
        var r = new Rig(); r.Launcher.DelayMs = 300;
        var first = r.Run(WatchdogRunMode.CheckNow); var second = r.Run(WatchdogRunMode.CheckNow); var third = r.Run(WatchdogRunMode.Scheduled);
        Check(ReferenceEquals(first, second) && ReferenceEquals(first, third), "no overlap: a run requested while one is active gets the active run");
        Wait(first);
        Check(r.Launcher.Calls == 1 && r.Launcher.MaxActive == 1 && r.Dog.RunCount == 1, "no overlap: one host start, one run");
    }

    private static void Timer()
    {
        var r = new Rig(); r.Dog.FirstDelay = TimeSpan.FromMilliseconds(80); r.Dog.IntervalFor = _ => TimeSpan.FromMilliseconds(120);
        Check(!r.Dog.TimerRunning, "timer: not running before the setting is applied");
        r.Dog.ApplySetting(false);
        Check(!r.Dog.TimerRunning, "timer: setting off keeps it stopped");
        r.Dog.ApplySetting(true); r.Dog.ApplySetting(true);
        Check(r.Dog.TimerRunning, "timer: setting on starts it");
        Check(Until(() => r.Dog.RunCount >= 2 && !r.Dog.IsRunning), "timer: first check after the delay, then re-armed");
        r.Dog.ApplySetting(false);
        Check(!r.Dog.TimerRunning, "timer: setting off stops it");
        Thread.Sleep(150); int count = r.Dog.RunCount; Thread.Sleep(400);
        Check(r.Dog.RunCount == count, "timer: no check runs after it was stopped");
        r.Dog.ApplySetting(true); Check(Until(() => r.Dog.RunCount > count), "timer: can be started again");
        r.Dog.Dispose(); Check(!r.Dog.TimerRunning, "timer: dispose stops it");
        var late = new Rig(); late.Dog.ApplySetting(true);
        Check(late.Dog.LastArmedDelay == TimeSpan.FromSeconds(10) && late.Dog.RunCount == 0, "timer: the first check is armed for 10 seconds after start");
        late.Dog.Dispose();
    }

    private static void Backoff()
    {
        var expected = new[] { (0, 5), (1, 5), (2, 5), (3, 5), (4, 15), (5, 15), (6, 15), (7, 60), (20, 60) };
        Check(expected.All(e => BridgeWatchdog.DefaultInterval(e.Item1) == TimeSpan.FromMinutes(e.Item2)), "back-off table: 5 min for the first 3 healthy results, 15 min for the next 3, then 60 min");
        var r = new Rig(); r.Dog.FirstDelay = TimeSpan.FromHours(1); r.Dog.ApplySetting(true);
        var seen = new List<int>();
        for (int i = 0; i < 8; i++) { Wait(r.Run(WatchdogRunMode.Scheduled)); r.Now = r.Now.AddMinutes(6); seen.Add((int)r.Dog.LastArmedDelay.TotalMinutes); }
        Check(seen.SequenceEqual(new[] { 5, 5, 5, 15, 15, 15, 60, 60 }), "back-off: armed delays over eight healthy scheduled results are " + string.Join(",", seen));
        r.Pipe.Alive = false; r.RecreateFixes = false;
        Wait(r.Run(WatchdogRunMode.Scheduled));
        Check(r.Dog.HealthyStreak == 0 && r.Dog.LastArmedDelay == TimeSpan.FromMinutes(5), "back-off: a Problem result resets to 5 minutes");
        r.Pipe.Alive = true; for (int i = 0; i < 5; i++) { r.Now = r.Now.AddMinutes(6); Wait(r.Run(WatchdogRunMode.Scheduled)); }
        Check(r.Dog.LastArmedDelay == TimeSpan.FromMinutes(15), "back-off: climbs again after healthy results");
        Wait(r.Run(WatchdogRunMode.CheckNow));
        Check(r.Dog.HealthyStreak == 0 && r.Dog.LastArmedDelay == TimeSpan.FromMinutes(5), "back-off: Check now resets to 5 minutes");
        for (int i = 0; i < 5; i++) { r.Now = r.Now.AddMinutes(6); Wait(r.Run(WatchdogRunMode.Scheduled)); }
        r.Now = r.Now.AddMinutes(2); r.Dog.NotifyCaptureProblem(); Until(() => r.Dog.HealthyStreak == 0 && !r.Dog.IsRunning);
        Check(r.Dog.HealthyStreak == 0 && r.Dog.LastArmedDelay == TimeSpan.FromMinutes(5), "back-off: a capture failure resets to 5 minutes");
        var repaired = new Rig(machineRegistration: false); repaired.Dog.FirstDelay = TimeSpan.FromHours(1); repaired.Dog.ApplySetting(true);
        Wait(repaired.Run(WatchdogRunMode.Scheduled));
        Check(repaired.Dog.HealthyStreak == 0, "back-off: a Repaired result is not counted as healthy");
        r.Dog.Dispose(); repaired.Dog.Dispose();
    }

    private static void Skips()
    {
        var r = new Rig(withMachine: true); r.Mach.Busy = true; r.Dog.FirstDelay = TimeSpan.FromHours(1); r.Dog.ApplySetting(true);
        var skipped = Wait(r.Run(WatchdogRunMode.Scheduled));
        Check(r.Dog.SkippedRuns == 1 && r.Pipe.Calls == 0 && r.Reg.Reads == 0 && r.Launcher.Calls == 0 && r.Dog.RunCount == 0 && r.Dog.LastArmedDelay == TimeSpan.FromMinutes(5), "full-screen or locked: scheduled check does nothing and re-arms");
        r.Mach.Busy = false; r.Mach.Saver = true;
        Wait(r.Run(WatchdogRunMode.Scheduled));
        Check(r.Dog.SkippedRuns == 2 && r.Pipe.Calls == 0 && r.Dog.LastSkipReason.Contains("battery saver"), "battery saver: scheduled check is skipped");
        Wait(r.Run(WatchdogRunMode.CheckNow));
        Check(r.Dog.RunCount == 1 && r.Pipe.Calls == 1, "manual Check now still runs while busy or saving power");
        r.Mach.Saver = false; r.Mach.HostRunning = true; r.Now = r.Now.AddMinutes(10);
        int calls = r.Launcher.Calls;
        var live = Wait(r.Run(WatchdogRunMode.Scheduled));
        Check(r.Launcher.Calls == calls && Rig.Item(live, "host").State == CheckState.Ok && Rig.Item(live, "host").Summary.Contains("running the helper"), "a host already started by a browser: no probe, counted as proof");
        r.Dog.Dispose();
    }

    private static void ReportText()
    {
        var report = Sample(WatchdogOverall.Repaired);
        string text = report.ToText();
        Check(text.Contains("NeoDM browser connection report") && text.Contains("Result: The browser connection was repaired.") && text.Contains("Chrome registration: OK. Chrome is connected to NeoDM.") && text.Contains("Edge registration: Not installed.") && text.Contains("Repairs made:") && text.Contains("Registered NeoDM for Brave"), "report text lists result, each check with state and the repairs");
        Check(!text.Contains('\u2014') && !text.Contains('\u2013'), "report text has no dash characters");
        Check(Sample(WatchdogOverall.Problem).OverallText == "The browser connection has a problem." && Sample(WatchdogOverall.Ok).OverallText == "The browser connection works.", "overall wording");
    }

    private static void Notices()
    {
        var shown = new List<string>(); var n = new WatchdogNotifier(t => { shown.Add(t); return true; });
        var problem = Sample(WatchdogOverall.Problem);
        n.Handle(problem); n.Handle(problem); n.Handle(problem);
        Check(shown.Count == 1 && shown[0] == "Browser connection problem: Chrome points to a NeoDM file that is missing. Open Help > Browser connection.", "balloon: one notice for the same problem, with the short reason");
        var other = new WatchdogReport(WatchdogOverall.Problem, new[] { new CheckItem("host", "Host start test", CheckState.Problem, "The NeoDM browser helper did not answer within 3 seconds.") }, DateTime.UtcNow, Array.Empty<string>());
        n.Handle(other); n.Handle(other);
        Check(shown.Count == 2 && shown[1].StartsWith("Browser connection problem: The NeoDM browser helper did not answer within 3 seconds. Open"), "balloon: a different problem gets its own single notice");
        n.Handle(Sample(WatchdogOverall.Ok)); n.Handle(problem);
        Check(shown.Count == 2, "balloon: the same problem coming back in the session is not announced again");
        n.Handle(Sample(WatchdogOverall.Repaired)); n.Handle(Sample(WatchdogOverall.Repaired));
        Check(shown.Count == 3 && shown[2] == "Repaired the browser connection.", "balloon: Repaired shows once per session");
    }

    // ===== selfcheck on the real listener =====
    private static void SelfCheck()
    {
        string pipe = "NDM.Downloads.bridgetest." + Guid.NewGuid().ToString("N");
        int requests = 0, controls = 0;
        using var bridge = new NativeBridge(request => { Interlocked.Increment(ref requests); request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, "no")); }, pipe, _ => { Interlocked.Increment(ref controls); return null; });
        bridge.Start();
        var probe = new SystemPipeProbe();
        Check(Wait(probe.PingAsync(pipe, TimeSpan.FromSeconds(3), default)), "selfcheck: the real listener answers the ping");
        Check(requests == 0 && controls == 0, "selfcheck: no capture request, no control call, so no dialog and no job");
        var reply = Wait(NativeBridge.SendControlAsync(pipe, NativeBridge.ActionSelfCheck, null, TimeSpan.FromSeconds(2)));
        Check(reply.Ok, "selfcheck: SendControl gets ok");
        var unknown = Wait(NativeBridge.SendControlAsync(pipe, "nonsense", null, TimeSpan.FromSeconds(2)));
        Check(!unknown.Ok && controls == 0, "unknown actions are still refused");
        using var bare = new NativeBridge(_ => { Interlocked.Increment(ref requests); }, pipe + ".bare"); bare.Start();
        Check(Wait(probe.PingAsync(pipe + ".bare", TimeSpan.FromSeconds(3), default)) && requests == 0, "selfcheck: answered by a listener that has no control handler too");
        var sw = Stopwatch.StartNew();
        Check(!Wait(probe.PingAsync(pipe + ".nobody", TimeSpan.FromMilliseconds(600), default)) && sw.ElapsedMilliseconds < 3000, "selfcheck: a pipe nobody listens on fails within the timeout");
        bridge.Dispose();
        Thread.Sleep(300);
        Check(!Wait(probe.PingAsync(pipe, TimeSpan.FromMilliseconds(600), default)), "selfcheck: a disposed listener no longer answers");
    }

    private static void SettingDefault(string scratch)
    {
        Check(new AppSettings().BridgeWatchdogEnabled, "setting: new settings have the watchdog on");
        string dir = Path.Combine(scratch, "settings"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Theme\":\"Dark\",\"SettingsSchema\":4}");
        Check(AppSettings.Load(dir).BridgeWatchdogEnabled, "setting: an old settings file without the key loads as on");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"BridgeWatchdogEnabled\":false,\"SettingsSchema\":4}");
        var off = AppSettings.Load(dir);
        Check(!off.BridgeWatchdogEnabled, "setting: an explicit off is kept");
        var copy = new AppSettings(); copy.CopyFrom(off);
        Check(!copy.BridgeWatchdogEnabled && !off.Copy().BridgeWatchdogEnabled, "setting: copy and copy-from keep it");
        off.BridgeWatchdogEnabled = true; off.Save(dir);
        Check(AppSettings.Load(dir).BridgeWatchdogEnabled, "setting: saved and loaded");
        var w = new SettingsWindow(new AppSettings { BridgeWatchdogEnabled = false }) { ShowInTaskbar = false }; w.Show(); Pump();
        var box = (CheckBox)w.FindName("BridgeWatchdogBox");
        Check(box.IsChecked == false && ((string)box.Content).Replace("_", "") == "Check the browser connection automatically", "Options > General has the checkbox with the right text and state");
        w.Close();
        var def = new SettingsWindow(new AppSettings()) { ShowInTaskbar = false }; def.Show(); Pump();
        Check(((CheckBox)def.FindName("BridgeWatchdogBox")).IsChecked == true, "Options checkbox is on by default");
        Check(((TextBlock)def.FindName("TrayHint")).Text == "If you cannot see the icon, drag it from the Windows hidden-icons arrow to the taskbar corner, or turn it on in Windows Settings > Personalization > Taskbar > Other system tray icons.", "Options > General has the hint line under the tray checkbox");
        def.Close();
    }

    // ===== status window in every theme =====
    private static void StatusWindow()
    {
        var clip = new List<string>(); var oldClip = BridgeStatusWindow.SetClipboard; BridgeStatusWindow.SetClipboard = t => { clip.Add(t); return true; };
        int guides = 0; var oldGuide = BridgeStatusWindow.OpenGuide; BridgeStatusWindow.OpenGuide = () => guides++;
        try
        {
            foreach (var theme in ThemeCatalog.Concrete)
            {
                ThemeManager.Apply(Application.Current, theme.Id, null); string t = theme.Id + ": ";
                var dog = new FakeDog { Last = Sample(WatchdogOverall.Problem) };
                var window = new BridgeStatusWindow(dog) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20, ShowInTaskbar = false };
                window.Show(); Pump(100); window.UpdateLayout();
                Check(window.IsVisible && window.ActualWidth > 300 && window.Rows.Children.Count == 5, t + "status window renders one row per check");
                Check(window.OverallLine == "The browser connection has a problem." && ((SolidColorBrush)window.Badge.Background).Color == ((SolidColorBrush)Application.Current.FindResource("ErrorBrush")).Color, t + "problem state shows its text and the theme error colour");
                Check(window.ResizeMode == ResizeMode.CanResize && window.Owner is null, t + "standalone and resizable");
                var scroll = Visual<ScrollViewer>(window).First(s => s.Name == "ItemsScroll");
                Check(window.Rows.Margin.Right >= 16 && scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, t + "content keeps room for the scroll bar");
                var texts = Visual<TextBlock>(window.Rows).ToList();
                var muted = texts.Where(x => x.Text == "C:\\NeoDM\\host\\com.ndm.bridge.json").ToList();
                Check(muted.Count > 0 && muted.All(x => x.FontSize < 13 && ((SolidColorBrush)x.Foreground).Color == ((SolidColorBrush)Application.Current.FindResource("MutedBrush")).Color), t + "detail line is the smaller grey second line");
                window.Width = 470; window.Height = 400; Pump(80); window.UpdateLayout();
                Check(scroll.ViewportWidth > 0 && window.Rows.ActualWidth <= scroll.ViewportWidth + 0.5, t + "after resizing the rows still fit the viewport (no overlap with the scroll bar)");
                dog.Next = Sample(WatchdogOverall.Ok);
                Click(window.Check); Pump(150);
                Check(dog.Modes.SequenceEqual(new[] { WatchdogRunMode.CheckNow }) && window.OverallLine == "The browser connection works." && window.Shown!.Overall == WatchdogOverall.Ok, t + "Check now runs the watchdog and refreshes");
                dog.Next = Sample(WatchdogOverall.Repaired);
                Click(window.Repair); Pump(150);
                Check(dog.Modes.Last() == WatchdogRunMode.Repair && window.OverallLine == "The browser connection was repaired." && window.Rows.Children.Count == 6, t + "Repair runs with the limit bypassed and lists the repairs");
                Click(window.Copy);
                Check(clip.Count > 0 && clip[^1] == dog.Next.ToText() && window.StatusMessage == "Report copied.", t + "Copy report copies the report text");
                Click(window.Guide); Check(guides > 0, t + "Install guide button opens the guide");
                dog.Next = Sample(WatchdogOverall.Problem); dog.RunAsync(WatchdogRunMode.Scheduled); Pump(100);
                Check(window.OverallLine == "The browser connection has a problem.", t + "a report from the timer refreshes the open window");
                var close = (Button)window.FindName("CloseButton"); Click(close); Pump(50);
                Check(!window.IsLoaded, t + "Close closes the window");
                guides = 0; clip.Clear();
            }
            var shared = new FakeDog { Last = Sample(WatchdogOverall.Ok) };
            var one = BridgeStatusWindow.ShowOrActivate(shared); var two = BridgeStatusWindow.ShowOrActivate(shared);
            Check(ReferenceEquals(one, two), "the menu item raises the open window instead of opening a second");
            one.Close();
            var fresh = new FakeDog(); var first = new BridgeStatusWindow(fresh) { ShowInTaskbar = false }; first.Show(); Pump(150);
            Check(fresh.Modes.Count == 1 && fresh.Modes[0] == WatchdogRunMode.CheckNow, "without a report the window runs a check when it opens");
            first.Close();
        }
        finally { BridgeStatusWindow.SetClipboard = oldClip; BridgeStatusWindow.OpenGuide = oldGuide; ThemeManager.Apply(Application.Current, "Light", null); }
    }

    // ===== tray icon stays visible =====
    private static void TrayAlwaysVisible(string scratch)
    {
        string files = Path.Combine(scratch, "tray-files"), state = Path.Combine(scratch, "tray-state"); Directory.CreateDirectory(files); Directory.CreateDirectory(state);
        var native = new FakeNative(); var oldNative = WindowActivation.Native; WindowActivation.Native = native;
        using var manager = new DownloadManager(files, state, new DownloadOptions { MaxConcurrentDownloads = 1, MaxSegments = 1, RetryCount = 0 });
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var off = new MainWindow(manager, new AppSettings { DownloadDirectory = files }, () => { }, enableTray: false) { ShowInTaskbar = false };
        off.Show(); Pump(); Check(!off.TrayIconVisible, "tray: no icon when the tray is switched off"); off.Close();
        var main = new MainWindow(manager, new AppSettings { DownloadDirectory = files }, () => { }, enableTray: true) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
        var icon = (Hardcodet.Wpf.TaskbarNotification.TaskbarIcon)typeof(MainWindow).GetField("_trayIcon", flags)!.GetValue(main)!;
        var controller = (TrayIconController)typeof(MainWindow).GetField("_trayController", flags)!.GetValue(main)!;
        try
        {
            Check(main.TrayIconVisible && icon.Icon is not null && controller.IconRenders >= 1 && icon.ToolTipText == "NeoDM", "tray: the icon exists with the right icon and tooltip before the window is even shown");
            main.Show(); Pump(150);
            Check(main.IsVisible && main.TrayIconVisible, "tray: visible at startup with the main window shown");
            for (int round = 0; round < 3; round++)
            {
                main.WindowState = WindowState.Minimized; Pump(120);
                Check(!main.IsVisible && main.TrayIconVisible, "tray: visible after minimize (round " + round + ")");
                main.BringToFront(); Pump(120);
                Check(main.IsVisible && main.WindowState == WindowState.Normal && main.TrayIconVisible, "tray: visible after restore (round " + round + ")");
                main.WindowState = WindowState.Maximized; Pump(100);
                Check(main.TrayIconVisible, "tray: visible while maximized (round " + round + ")");
                main.WindowState = WindowState.Normal; Pump(60);
                main.Hide(); Pump(60);
                Check(main.TrayIconVisible, "tray: visible while hidden (round " + round + ")");
                main.Show(); Pump(60);
                Check(main.IsVisible && main.TrayIconVisible, "tray: visible after Show (round " + round + ")");
            }
            // capture dialogs
            var dialogs = new List<DownloadOptionsDialog>();
            for (int i = 0; i < 2; i++)
            {
                var d = new DownloadOptionsDialog("https://example.org/files/a" + i + ".zip", "a" + i + ".zip", null, null, files) { ShowInTaskbar = false };
                dialogs.Add(d); _ = CaptureFlow.ShowAsync(d, main.TrayWindows, bringToFront: false); Pump(100);
                Check(main.TrayIconVisible, "tray: visible while capture dialog " + (i + 1) + " is open");
            }
            Check(main.TrayWindows.Count == 2, "tray: the capture dialogs are listed for the tray menu");
            // left click: pending dialog first, main window untouched
            var activated = new List<Window>(); main.TrayWindows.Activate = w => activated.Add(w);
            main.Hide(); Pump(60);
            icon.RaiseEvent(new RoutedEventArgs(Hardcodet.Wpf.TaskbarNotification.TaskbarIcon.TrayLeftMouseUpEvent)); Pump(60);
            Check(activated.Count == 1 && ReferenceEquals(activated[0], dialogs[0]) && !main.IsVisible && main.TrayIconVisible, "tray: left click restores the oldest pending dialog first and leaves the hidden main window alone");
            foreach (var d in dialogs) d.Close();
            Pump(100);
            Check(main.TrayWindows.Count == 0 && main.TrayIconVisible, "tray: visible after the capture dialogs closed");
            // left click without dialogs: shows or raises the window, never hides it
            icon.RaiseEvent(new RoutedEventArgs(Hardcodet.Wpf.TaskbarNotification.TaskbarIcon.TrayLeftMouseUpEvent)); Pump(100);
            Check(main.IsVisible && main.WindowState == WindowState.Normal && main.TrayIconVisible, "tray: left click on a hidden window shows it");
            native.Foreground = (IntPtr)12345; int before = native.Activations;
            icon.RaiseEvent(new RoutedEventArgs(Hardcodet.Wpf.TaskbarNotification.TaskbarIcon.TrayLeftMouseUpEvent)); Pump(100);
            Check(main.IsVisible && native.Activations > before && main.TrayIconVisible, "tray: left click on a visible window brings it to the front and does not hide it");
            // right-click menu
            icon.RaiseEvent(new RoutedEventArgs(Hardcodet.Wpf.TaskbarNotification.TaskbarIcon.TrayContextMenuOpenEvent)); Pump(40);
            var headers = icon.ContextMenu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
            Check(headers.SequenceEqual(new[] { "Show", "When downloads finish", "Exit" }), "tray: right-click menu has Show, When downloads finish and Exit");
            // exit
            typeof(MainWindow).GetField("_exitWithoutPrompt", flags)!.SetValue(main, true);
            typeof(MainWindow).GetField("_exitRequested", flags)!.SetValue(main, true);
            main.Close(); Pump(100);
            Check(!main.TrayIconVisible && icon.IsDisposed, "tray: the icon is removed when the app exits");
            main.DisposeTray();
            Check(true, "tray: disposing twice is harmless");
        }
        finally { WindowActivation.Native = oldNative; foreach (Window w in Application.Current.Windows.Cast<Window>().ToArray()) w.Close(); }
    }

    // ===== resource budget =====
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
    private static long Managed() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }

    private static void Budget()
    {
        var r = new Rig(withMachine: true);
        string priority = "";
        r.Reg.Reads = 0;
        // priority of the thread that does the work: the registry fake sees it on the first read
        var probe = new Rig(); var seen = new List<ThreadPriority>(); var mainThread = Environment.CurrentManagedThreadId; int thread = -1; bool pool = true;
        var spy = new SpyRegistry(probe.Reg, () => { seen.Add(Thread.CurrentThread.Priority); thread = Environment.CurrentManagedThreadId; pool = Thread.CurrentThread.IsThreadPoolThread; });
        var spyDog = new BridgeWatchdog(new WatchdogEnvironment { HostCandidates = new[] { Rig.Host }, UserManifestPath = Rig.User, ProgramDirectories = new[] { @"C:\Program Files" } }, spy, probe.Files, probe.Launcher, probe.Pipe, null, null, null, null);
        Wait(spyDog.RunAsync(WatchdogRunMode.CheckNow));
        Check(seen.Count > 0 && seen[0] == ThreadPriority.BelowNormal && thread != mainThread && !pool, "the work starts on a dedicated BelowNormal background thread, not the UI thread");

        for (int i = 0; i < 20; i++) Wait(r.Run());
        long memBefore = Managed(); var proc = Process.GetCurrentProcess(); proc.Refresh();
        int handles = proc.HandleCount; uint gdi = GetGuiResources(proc.Handle, 0), user = GetGuiResources(proc.Handle, 1); int threads = proc.Threads.Count;
        for (int i = 0; i < 500; i++)
        {
            r.Now = r.Now.AddMinutes(6);
            if (i % 50 == 0) r.Pipe.Alive = false; // some cycles repair, to cover those paths too
            Wait(r.Run(i % 10 == 0 ? WatchdogRunMode.CheckNow : WatchdogRunMode.Scheduled));
        }
        long memAfter = Managed(); proc.Refresh();
        Thread.Sleep(200); proc.Refresh();
        long growth = memAfter - memBefore; int handleDelta = proc.HandleCount - handles; int gdiDelta = (int)GetGuiResources(proc.Handle, 0) - (int)gdi;
        Console.WriteLine($"BUDGET: 500 cycles, managed growth {growth} bytes, handle delta {handleDelta}, GDI delta {gdiDelta}, USER delta {(int)GetGuiResources(proc.Handle, 1) - (int)user}, threads {threads} -> {proc.Threads.Count}");
        Check(growth < 1024 * 1024, $"500 cycles: managed memory grows by {growth} bytes (< 1 MB)");
        Check(Math.Abs(handleDelta) <= 8, $"500 cycles: handle count stays flat (delta {handleDelta})");
        Check(gdiDelta == 0, "500 cycles: GDI object count stays flat");
        Check(r.Dog.RecentLog.Count <= 20 && r.Log.Count > 20, $"log is bounded to 20 lines in memory ({r.Dog.RecentLog.Count}) while the sink got {r.Log.Count}");
        Check(proc.Threads.Count <= threads + 6, $"no thread build-up ({threads} -> {proc.Threads.Count})");

        // CPU of the fake cycle: average over 50 (process CPU time ticks are coarse)
        for (int i = 0; i < 5; i++) Wait(r.Run());
        var cpuStart = proc.TotalProcessorTime; var wall = Stopwatch.StartNew();
        const int cycles = 50;
        for (int i = 0; i < cycles; i++) Wait(r.Run());
        proc.Refresh(); var cpu = proc.TotalProcessorTime - cpuStart;
        double perCycle = cpu.TotalMilliseconds / cycles;
        Console.WriteLine($"BUDGET: fake cycle CPU {perCycle:F2} ms average over {cycles} cycles (wall {wall.Elapsed.TotalMilliseconds / cycles:F2} ms)");
        Check(perCycle < 20, $"one watchdog cycle with fakes uses {perCycle:F2} ms CPU on average (< 20 ms)");
        r.Dog.Dispose();
    }

    private sealed class SpyRegistry(FakeRegistry inner, Action onRead) : IRegistryAccess
    {
        private bool _first = true;
        public string? GetDefaultValue(RegistryScope s, string k) { if (_first) { _first = false; onRead(); } return inner.GetDefaultValue(s, k); }
        public void SetDefaultValue(RegistryScope s, string k, string v) => inner.SetDefaultValue(s, k, v);
        public bool DeleteSubKeyTree(RegistryScope s, string k) => inner.DeleteSubKeyTree(s, k);
    }

    // ===== real host from dist/host =====
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Directory.Build.props"))) dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
        return dir ?? throw new Exception("repository root not found");
    }

    private static void RealHost(string scratch)
    {
        string source = Path.Combine(RepoRoot(), "dist", "host");
        if (!File.Exists(Path.Combine(source, "NDM.NativeHost.exe")))
        {
            Console.WriteLine("SKIP: real host test, dist/host/NDM.NativeHost.exe is missing. Run build.ps1 first.");
            return;
        }
        // Copy the published host so the repository output stays untouched; the copy gets its own bridge-config.json.
        string copy = Path.Combine(scratch, "host"); Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)), true);
        File.WriteAllText(Path.Combine(copy, "bridge-config.json"), "{\"extensionId\":\"" + Rig.DevId + "\"}");
        string exe = Path.Combine(copy, "NDM.NativeHost.exe");
        var launcher = new HostLauncher();
        var wall = Stopwatch.StartNew(); var self = Process.GetCurrentProcess(); var cpu0 = self.TotalProcessorTime;
        var result = Wait(launcher.ProbeAsync(exe, "chrome-extension://" + Rig.DevId + "/", TimeSpan.FromSeconds(3), default));
        self.Refresh(); var own = self.TotalProcessorTime - cpu0;
        Check(result.Ok, "real host: the published host starts, answers the ping and is ended" + (result.Ok ? "" : " (" + result.Failure + " " + result.Detail + ")"));
        double total = (own + result.Cpu).TotalMilliseconds;
        Console.WriteLine($"BUDGET: real host cycle wall {wall.ElapsedMilliseconds} ms, CPU child {result.Cpu.TotalMilliseconds:F0} ms + this process {own.TotalMilliseconds:F0} ms = {total:F0} ms");
        Check(total < 1500, $"real host cycle total CPU {total:F0} ms (< 1.5 s)");
        Thread.Sleep(300);
        Check(Process.GetProcessesByName("NDM.NativeHost").All(p => { try { return !p.MainModule!.FileName.StartsWith(copy, StringComparison.OrdinalIgnoreCase); } catch { return true; } }), "real host: no child left running");
        var wrong = Wait(launcher.ProbeAsync(exe, "chrome-extension://" + Rig.OtherId + "/", TimeSpan.FromSeconds(3), default));
        Check(!wrong.Ok && wrong.Failure == HostProbeFailure.ExitedEarly && wrong.ExitCode == 2, "real host: an origin the host does not accept ends early with exit code 2");
        // child priority
        var started = (SystemHostProcess)SystemHostProcess.Start(exe, "chrome-extension://" + Rig.DevId + "/");
        try { Check(started.Priority == ProcessPriorityClass.BelowNormal, "real host: the child runs at BelowNormal priority"); }
        finally { started.Kill(); started.Dispose(); }
        // the whole watchdog cycle with the real launcher and the real pipe, fakes for registry and files
        string pipe = "NDM.Downloads.bridgetest." + Guid.NewGuid().ToString("N");
        using var bridge = new NativeBridge(_ => { }, pipe); bridge.Start();
        var r = new Rig();
        var env = new WatchdogEnvironment { HostCandidates = new[] { exe }, UserManifestPath = Rig.User, PipeName = pipe, ProgramDirectories = new[] { @"C:\Program Files" } };
        r.Files.Files[exe] = "exe"; r.Files.Files[Path.Combine(copy, "bridge-config.json")] = File.ReadAllText(Path.Combine(copy, "bridge-config.json"));
        r.Files.Files[Rig.Machine] = BridgeWatchdog.BuildManifest(exe, new[] { Rig.DevId });
        var real = new BridgeWatchdog(env, r.Reg, r.Files, launcher, new SystemPipeProbe(), null);
        var cpu1 = self.TotalProcessorTime;
        var report = Wait(real.RunAsync(WatchdogRunMode.CheckNow));
        self.Refresh(); var cycle = self.TotalProcessorTime - cpu1;
        Console.WriteLine($"BUDGET: full watchdog cycle with real pipe and real host: this process CPU {cycle.TotalMilliseconds:F0} ms plus child");
        Check(report.Overall == WatchdogOverall.Ok && Rig.Item(report, "host").State == CheckState.Ok && Rig.Item(report, "listener").State == CheckState.Ok, "real host: full cycle with the real listener and host is Ok");
    }
}
