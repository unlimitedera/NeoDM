using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace NDM.App;

/// <summary>Registry access for the watchdog. Reads both registry views of the machine hive; writes only the current-user com.ndm.bridge key of a browser.</summary>
public sealed class SystemRegistryAccess : IRegistryAccess
{
    private const string HostKeyTail = @"\NativeMessagingHosts\" + BridgeIds.HostName;

    public string? GetDefaultValue(RegistryScope scope, string subKey)
    {
        foreach (var view in scope == RegistryScope.LocalMachine ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry64 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(scope == RegistryScope.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view);
                using var key = root.OpenSubKey(subKey, false);
                if (key?.GetValue("") is string value && value.Length > 0) return value;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return null;
    }

    public void SetDefaultValue(RegistryScope scope, string subKey, string value)
    {
        RequireOwnKey(scope, subKey);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.CreateSubKey(subKey, true);
        key.SetValue("", value, RegistryValueKind.String);
    }

    public bool DeleteSubKeyTree(RegistryScope scope, string subKey)
    {
        RequireOwnKey(scope, subKey);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using (var existing = root.OpenSubKey(subKey, false)) { if (existing is null) return false; }
        root.DeleteSubKeyTree(subKey, false);
        return true;
    }

    // Writes are limited to the current user's com.ndm.bridge key of a browser; nothing else can be changed through this class.
    private static void RequireOwnKey(RegistryScope scope, string subKey)
    {
        if (scope != RegistryScope.CurrentUser || !subKey.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase) || !subKey.EndsWith(HostKeyTail, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only the current user's " + BridgeIds.HostName + " key can be changed.");
    }
}

public sealed class SystemFileAccess : IFileSystemAccess
{
    public const int MaximumReadBytes = 64 * 1024;
    public bool FileExists(string path)
    {
        try { return Path.IsPathFullyQualified(path) && File.Exists(path); } catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    public string? ReadAllText(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(stream.Length, MaximumReadBytes)];
            int read = 0;
            while (read < buffer.Length) { int n = stream.Read(buffer, read, buffer.Length - read); if (n == 0) break; read += n; }
            return new UTF8Encoding(false).GetString(buffer, 0, read).TrimStart('\uFEFF');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}

public sealed class SystemPipeProbe : IPipeProbe
{
    public async Task<bool> PingAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
        => (await NativeBridge.SendControlAsync(pipeName, NativeBridge.ActionSelfCheck, null, timeout)).Ok;
}

/// <summary>The started host as the launcher sees it. The real one wraps a Process; tests use a fake.</summary>
public interface IHostProcess : IDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    TimeSpan CpuTime { get; }
    Task<string?> ReadFirstStderrLineAsync();
    bool WaitForExit(int milliseconds);
    void Kill();
}

public sealed class SystemHostProcess : IHostProcess
{
    private readonly Process _process;
    private SystemHostProcess(Process process) => _process = process;

    public static IHostProcess Start(string exe, string origin)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        info.ArgumentList.Add(origin);
        var process = Process.Start(info) ?? throw new InvalidOperationException("The process did not start.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
        return new SystemHostProcess(process);
    }

    /// <summary>Priority class of the child (BelowNormal after start).</summary>
    public ProcessPriorityClass? Priority { get { try { return _process.PriorityClass; } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { return null; } } }

    public Stream StandardInput => _process.StandardInput.BaseStream;
    public Stream StandardOutput => _process.StandardOutput.BaseStream;
    public bool HasExited { get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } } }
    public int ExitCode => _process.ExitCode;
    public TimeSpan CpuTime { get { try { return _process.TotalProcessorTime; } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { return default; } } }
    public async Task<string?> ReadFirstStderrLineAsync() { try { return await _process.StandardError.ReadLineAsync(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { return null; } }
    public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);
    public void Kill() { try { _process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { } }
    public void Dispose() => _process.Dispose();
}

/// <summary>
/// Starts the host exe the way a browser does (origin as the first argument), sends one ping frame and checks the reply. At most one child exists at a time,
/// it lives at most three seconds, and only the process this launcher started is ended.
/// </summary>
public sealed class HostLauncher : IHostLauncher
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static readonly byte[] PingFrame = BuildFrame("{\"action\":\"ping\"}");
    private readonly Func<string, string, IHostProcess> _start;
    private readonly Func<string, bool> _exists;

    public HostLauncher(Func<string, string, IHostProcess>? start = null, Func<string, bool>? exists = null)
    {
        _start = start ?? SystemHostProcess.Start; _exists = exists ?? File.Exists;
    }

    private static byte[] BuildFrame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + body.Length];
        BitConverter.GetBytes((uint)body.Length).CopyTo(frame, 0);
        body.CopyTo(frame, 4);
        return frame;
    }

    public async Task<HostProbeResult> ProbeAsync(string hostExe, string origin, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_exists(hostExe)) return new HostProbeResult(false, HostProbeFailure.ExeMissing);
        if (!await OneAtATime.WaitAsync(timeout, cancellationToken)) return new HostProbeResult(false, HostProbeFailure.NoReply, Detail: "Another test of the helper was still running.");
        try
        {
            var clock = Stopwatch.StartNew();
            IHostProcess process;
            try { process = _start(hostExe, origin); }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
            { return new HostProbeResult(false, HostProbeFailure.StartFailed, Detail: ex.Message); }
            using (process)
            {
                TimeSpan cpu = default;
                bool killed = false;
                try
                {
                    var exchange = ExchangeAsync(process);
                    var first = await Task.WhenAny(exchange, Task.Delay(timeout, cancellationToken));
                    if (first != exchange)
                    {
                        cpu = process.CpuTime; process.Kill(); killed = true;
                        _ = exchange.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        return new HostProbeResult(false, HostProbeFailure.NoReply, Elapsed: clock.Elapsed, Cpu: cpu);
                    }
                    var outcome = await exchange;
                    if (outcome.Ok) { cpu = process.CpuTime; return new HostProbeResult(true, Elapsed: clock.Elapsed, AppVersion: outcome.AppVersion, Cpu: cpu); }
                    if (outcome.Closed)
                    {
                        process.WaitForExit(1000);
                        int? code = process.HasExited ? process.ExitCode : null;
                        string? line = null;
                        var stderr = process.ReadFirstStderrLineAsync();
                        if (await Task.WhenAny(stderr, Task.Delay(300)) == stderr) line = (await stderr)?.Trim();
                        cpu = process.CpuTime;
                        return new HostProbeResult(false, HostProbeFailure.ExitedEarly, code, line is { Length: > 0 } ? Shorten(line) : null, Elapsed: clock.Elapsed, Cpu: cpu);
                    }
                    cpu = process.CpuTime;
                    return new HostProbeResult(false, HostProbeFailure.BadReply, Detail: outcome.Detail, Elapsed: clock.Elapsed, Cpu: cpu);
                }
                finally
                {
                    // Only the process started above. A host that already exited is left alone.
                    if (!killed && !process.HasExited) process.Kill();
                }
            }
        }
        finally { OneAtATime.Release(); }
    }

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "...";

    private sealed record Exchange(bool Ok, bool Closed, string? Detail = null, string? AppVersion = null);

    private static async Task<Exchange> ExchangeAsync(IHostProcess process)
    {
        try
        {
            await process.StandardInput.WriteAsync(PingFrame);
            await process.StandardInput.FlushAsync();
            var header = new byte[4];
            await process.StandardOutput.ReadExactlyAsync(header);
            uint length = BitConverter.ToUInt32(header, 0);
            if (length == 0 || length > 65536) return new Exchange(false, false, "The reply had an invalid length.");
            var body = new byte[length];
            await process.StandardOutput.ReadExactlyAsync(body);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                bool ok = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True
                    && root.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String && h.GetString() == BridgeIds.HostName;
                if (!ok) return new Exchange(false, false, "The reply was not the expected answer to a ping.");
                string? version = root.TryGetProperty("appVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                return new Exchange(true, false, AppVersion: version);
            }
            catch (JsonException) { return new Exchange(false, false, "The reply was not valid JSON."); }
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            return new Exchange(false, true);
        }
    }
}

/// <summary>Cheap machine checks: one shell call, one power call and a process name lookup.</summary>
public sealed class SystemMachineState : IMachineState
{
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);

    public bool IsBusy()
    {
        // 2 busy (full-screen app), 3 Direct3D full screen, 4 presentation mode.
        if (SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4) return true;
        // The input desktop cannot be opened while the workstation is locked.
        var desktop = OpenInputDesktop(0, false, 0x0100);
        if (desktop == IntPtr.Zero) return true;
        CloseDesktop(desktop);
        return false;
    }

    public bool IsPowerSaver() => GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1;

    public bool BrowserHostRunning()
    {
        var processes = Process.GetProcessesByName("NDM.NativeHost");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

/// <summary>Appends watchdog lines to bridge-watchdog.log in the state folder. The file stays under 100 KB: one older copy is kept.</summary>
public sealed class WatchdogLogFile
{
    private const long MaximumBytes = 100 * 1024;
    private readonly string _path;
    private readonly object _gate = new();
    public WatchdogLogFile(string stateDirectory) => _path = Path.Combine(stateDirectory, "bridge-watchdog.log");

    public void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && new FileInfo(_path).Length > MaximumBytes) File.Move(_path, _path + ".old", true);
                File.AppendAllText(_path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " " + line.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Decides when a report earns a notice. At most one notice per distinct problem per session, and one "repaired" notice per session.</summary>
public sealed class WatchdogNotifier
{
    private readonly Func<string, bool> _show;
    private readonly HashSet<string> _problemsShown = new(StringComparer.Ordinal);
    private bool _repairedShown;
    public WatchdogNotifier(Func<string, bool> show) => _show = show;

    public void Handle(WatchdogReport report)
    {
        if (report.Overall == WatchdogOverall.Problem)
        {
            var fresh = report.Items.Where(i => i.State == CheckState.Problem).Where(i => _problemsShown.Add(i.Id + "|" + i.Summary)).ToList();
            if (fresh.Count > 0) _show("Browser connection problem: " + fresh[0].Summary.TrimEnd('.') + ". Open Help > Browser connection.");
        }
        else if (report.Overall == WatchdogOverall.Repaired && !_repairedShown)
        {
            _repairedShown = true;
            _show("Repaired the browser connection.");
        }
    }
}

public static class WatchdogFactory
{
    /// <summary>The environment of the running app: installed layout first (host folder next to the app), then the dist layout (sibling host folder).</summary>
    public static WatchdogEnvironment CreateEnvironment(string pipeName, string stateDirectory, bool allowRepairs)
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string parent = Path.GetDirectoryName(baseDir) ?? baseDir;
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        return new WatchdogEnvironment
        {
            HostCandidates = new[] { Path.Combine(baseDir, "host", "NDM.NativeHost.exe"), Path.Combine(parent, "host", "NDM.NativeHost.exe") },
            UserManifestPath = Path.Combine(stateDirectory, "host", BridgeIds.HostName + ".json"),
            PipeName = pipeName,
            ProgramDirectories = roots.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            AllowRepairs = allowRepairs
        };
    }
}
