using System.IO;
using System.Text;
using System.Text.Json;

using NDM.App.Localization;
namespace NDM.App;

public enum RegistryScope { CurrentUser, LocalMachine }

/// <summary>Registry reads and writes the watchdog needs. Only the default value of a key; tests use a fake.</summary>
public interface IRegistryAccess
{
    /// <summary>Default value of the key, or null when the key or the value does not exist.</summary>
    string? GetDefaultValue(RegistryScope scope, string subKey);
    void SetDefaultValue(RegistryScope scope, string subKey, string value);
    /// <summary>Deletes the key and everything below it. Returns false when it did not exist.</summary>
    bool DeleteSubKeyTree(RegistryScope scope, string subKey);
}

public interface IFileSystemAccess
{
    bool FileExists(string path);
    /// <summary>File text, or null when the file does not exist or cannot be read.</summary>
    string? ReadAllText(string path);
    /// <summary>Writes the file (creating its folder) so a reader never sees half of it.</summary>
    void WriteAllText(string path, string text);
}

public enum HostProbeFailure { None, ExeMissing, StartFailed, ExitedEarly, NoReply, BadReply }

public sealed record HostProbeResult(bool Ok, HostProbeFailure Failure = HostProbeFailure.None, int? ExitCode = null, string? StderrLine = null, string? Detail = null, TimeSpan Elapsed = default, string? AppVersion = null, TimeSpan Cpu = default);

/// <summary>Cheap questions about the machine that decide whether a check is worth running now. Tests use a fake.</summary>
public interface IMachineState
{
    /// <summary>A full-screen app or game, presentation mode, or a locked session: do not disturb.</summary>
    bool IsBusy();
    /// <summary>Windows battery saver is on.</summary>
    bool IsPowerSaver();
    /// <summary>A native host process started by a browser is running right now (proves the host starts).</summary>
    bool BrowserHostRunning();
}

public interface IHostLauncher
{
    /// <summary>Starts the host exe with the origin argument, sends one ping frame, waits for the reply and ends only the process it started.</summary>
    Task<HostProbeResult> ProbeAsync(string hostExe, string origin, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IPipeProbe
{
    /// <summary>True when the app's own listener answers the self check within the timeout.</summary>
    Task<bool> PingAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken);
}

public enum CheckState { Ok, Repaired, NotInstalled, Problem }
public enum WatchdogOverall { Ok, Repaired, Problem }
/// <summary>Why a run happens. Check now and Repair bypass the host test cadence; Repair also bypasses the repair rate limit.</summary>
public enum WatchdogRunMode { Scheduled, CaptureFailure, CheckNow, Repair }

/// <summary>One line of the report. <see cref="Summary"/> is plain language, <see cref="Detail"/> is the technical second line.</summary>
public sealed record CheckItem(string Id, string Name, CheckState State, string Summary, string Detail = "");

public sealed record WatchdogReport(WatchdogOverall Overall, IReadOnlyList<CheckItem> Items, DateTime TimeUtc, IReadOnlyList<string> RepairsMade)
{
    public string OverallText => Overall switch
    {
        WatchdogOverall.Ok => Loc.T("bridge.overall.ok"),
        WatchdogOverall.Repaired => Loc.T("bridge.overall.repaired"),
        _ => Loc.T("bridge.overall.problem")
    };

    public static string StateText(CheckState state) => state switch
    {
        CheckState.Ok => Loc.T("bridge.state.ok"), CheckState.Repaired => Loc.T("bridge.state.repaired"), CheckState.NotInstalled => Loc.T("bridge.state.not_installed"), _ => Loc.T("bridge.state.problem")
    };

    /// <summary>Plain text for Copy report.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine(Loc.T("bridge.report.title"));
        text.AppendLine(Loc.T("bridge.report.checked", TimeUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)));
        text.AppendLine(Loc.T("bridge.report.result", OverallText));
        foreach (var item in Items)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("bridge.report.item", item.Name, StateText(item.State), item.Summary));
            if (item.Detail.Length > 0) text.AppendLine("  " + item.Detail);
        }
        if (RepairsMade.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("bridge.report.repairs"));
            foreach (var repair in RepairsMade) text.AppendLine("  " + repair);
        }
        return text.ToString();
    }
}

public interface IBridgeWatchdog
{
    WatchdogReport? Last { get; }
    bool IsRunning { get; }
    event Action<WatchdogReport>? ReportChanged;
    Task<WatchdogReport> RunAsync(WatchdogRunMode mode);
}

/// <summary>Extension ids the app expects in every browser manifest. Add the Web Store id to <see cref="Builtin"/> when the listing is approved.</summary>
public static class BridgeIds
{
    public const string HostName = "com.ndm.bridge";
    public static readonly string[] Builtin =
    {
        "hohmmojlhbkelkobobpkochpdempcihd", // development / unpacked id (matches the key in the extension manifest)
        // Web Store id: add it here once the listing exists.
    };

    /// <summary>Built-in ids plus the id from bridge-config.json, without duplicates.</summary>
    public static IReadOnlyList<string> Known(string? configuredId) =>
        Builtin.Concat(configuredId is { Length: > 0 } ? new[] { configuredId } : Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();

    public static string Origin(string id) => $"chrome-extension://{id}/";
}

public sealed record BrowserTarget(string Id, string Name, string HostKey, string ExeName, string[] InstallPaths, bool AlwaysListed)
{
    public static readonly BrowserTarget Chrome = new("chrome", "Chrome", @"SOFTWARE\Google\Chrome\NativeMessagingHosts\" + BridgeIds.HostName, "chrome.exe", new[] { @"Google\Chrome\Application\chrome.exe" }, true);
    public static readonly BrowserTarget Edge = new("edge", "Edge", @"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\" + BridgeIds.HostName, "msedge.exe", new[] { @"Microsoft\Edge\Application\msedge.exe" }, true);
    public static readonly BrowserTarget Brave = new("brave", "Brave", @"SOFTWARE\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + BridgeIds.HostName, "brave.exe", new[] { @"BraveSoftware\Brave-Browser\Application\brave.exe" }, true);
    public static readonly BrowserTarget Chromium = new("chromium", "Chromium", @"SOFTWARE\Chromium\NativeMessagingHosts\" + BridgeIds.HostName, "chromium.exe", new[] { @"Chromium\Application\chrome.exe" }, false);
    public static readonly BrowserTarget[] All = { Chrome, Edge, Brave, Chromium };
}

/// <summary>Everything the watchdog needs to know about this installation. Tests build their own.</summary>
public sealed class WatchdogEnvironment
{
    /// <summary>Candidate host exe paths in order of preference (installed layout first, then the dist layout).</summary>
    public IReadOnlyList<string> HostCandidates { get; init; } = Array.Empty<string>();
    public string UserManifestPath { get; init; } = "";
    public string PipeName { get; init; } = NativeBridge.PipeName;
    /// <summary>Folders searched for browser programs (Program Files, Program Files (x86), local app data).</summary>
    public IReadOnlyList<string> ProgramDirectories { get; init; } = Array.Empty<string>();
    /// <summary>False in isolated runs (NDM_STATE_DIR): the checks still run but nothing in the registry or the user manifest is touched.</summary>
    public bool AllowRepairs { get; init; } = true;
}

/// <summary>
/// Checks the browser side of NeoDM and repairs what it can: the app's own pipe listener, the native messaging registration in Chrome, Edge, Brave and
/// Chromium, and a real start of the browser host. One run at a time. All IO goes through injected interfaces and runs on a pool thread.
/// </summary>
public sealed class BridgeWatchdog : IBridgeWatchdog, IDisposable
{
    public static readonly TimeSpan RepairLimit = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan CaptureFailureGap = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HostTestEvery = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan HostTestTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PipeTimeout = TimeSpan.FromSeconds(3);
    public const string ListenerId = "listener", HostTestId = "host";

    private readonly WatchdogEnvironment _env;
    private readonly IRegistryAccess _registry;
    private readonly IFileSystemAccess _files;
    private readonly IHostLauncher _launcher;
    private readonly IPipeProbe _pipe;
    private readonly Func<Task<bool>>? _recreateListener;
    private readonly Func<DateTime> _now;
    private readonly Action<string> _log;
    private readonly IMachineState? _machine;
    private readonly Queue<string> _recent = new();
    private int _healthyStreak;
    private bool _anyInstalled;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _repairAttempts = new(StringComparer.Ordinal);
    private Task<WatchdogReport>? _current;
    private DateTime? _lastRunUtc, _lastHostTestUtc;
    private CheckItem? _lastHostItem;
    private Timer? _timer;
    private bool _disposed;

    /// <summary>Delay before the first check and the gap between checks while the timer runs. Tests shorten them.</summary>
    public TimeSpan FirstDelay { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Gap before the next scheduled check, given how many results in a row were Ok: 5 minutes for the first 3, then 15 minutes for the next 3, then 60 minutes.</summary>
    public Func<int, TimeSpan> IntervalFor { get; set; } = DefaultInterval;
    public static TimeSpan DefaultInterval(int healthyStreak) => healthyStreak <= 3 ? TimeSpan.FromMinutes(5) : healthyStreak <= 6 ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(60);
    public int HealthyStreak { get { lock (_gate) return _healthyStreak; } }
    /// <summary>Delay the timer was last armed with (for tests and the log).</summary>
    public TimeSpan LastArmedDelay { get; private set; }
    public int SkippedRuns { get; private set; }
    public string LastSkipReason { get; private set; } = "";
    /// <summary>The last 20 log lines. Nothing else grows with the run count.</summary>
    public IReadOnlyList<string> RecentLog { get { lock (_recent) return _recent.ToArray(); } }

    public WatchdogReport? Last { get; private set; }
    public bool IsRunning { get { lock (_gate) return _current is { IsCompleted: false }; } }
    public bool TimerRunning { get { lock (_gate) return _timer is not null; } }
    public int RunCount { get; private set; }
    public event Action<WatchdogReport>? ReportChanged;

    public BridgeWatchdog(WatchdogEnvironment env, IRegistryAccess registry, IFileSystemAccess files, IHostLauncher launcher, IPipeProbe pipe,
        Func<Task<bool>>? recreateListener = null, Func<DateTime>? now = null, Action<string>? log = null, IMachineState? machine = null)
    {
        _env = env; _registry = registry; _files = files; _launcher = launcher; _pipe = pipe; _recreateListener = recreateListener; _machine = machine;
        _now = now ?? (() => DateTime.UtcNow);
        _log = line =>
        {
            lock (_recent) { _recent.Enqueue(line); while (_recent.Count > 20) _recent.Dequeue(); }
            try { log?.Invoke(line); } catch (Exception) { }
        };
    }

    // ---- scheduling ----

    /// <summary>Starts or stops the timer to match the setting. Starting twice keeps one timer.</summary>
    public void ApplySetting(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!enabled) { _timer?.Dispose(); _timer = null; return; }
            if (_timer is not null) return;
            LastArmedDelay = FirstDelay;
            _timer = new Timer(_ => { _ = RunScheduledAsync(); }, null, FirstDelay, Timeout.InfiniteTimeSpan); // one-shot, re-armed after every run
        }
    }

    private void Rearm(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_timer is null || _disposed) return;
            LastArmedDelay = delay;
            try { _timer.Change(delay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }
    }

    private async Task RunScheduledAsync()
    {
        try { if (!IsRunning) await RunAsync(WatchdogRunMode.Scheduled); }
        catch (Exception ex) { _log("Scheduled check failed: " + ex.Message); Rearm(IntervalFor(HealthyStreak)); }
    }

    /// <summary>The app saw a capture fail because of the pipe or the host. Runs a check unless one ran in the last 60 seconds or is running now.</summary>
    public bool NotifyCaptureProblem()
    {
        lock (_gate)
        {
            if (_disposed || (_current is { IsCompleted: false })) return false;
            if (_lastRunUtc is { } last && _now() - last < CaptureFailureGap) return false;
        }
        _ = Task.Run(async () =>
        {
            try { await RunAsync(WatchdogRunMode.CaptureFailure); }
            catch (Exception ex) { _log("Check after a capture problem failed: " + ex.Message); }
        });
        return true;
    }

    public void Dispose() { lock (_gate) { _disposed = true; _timer?.Dispose(); _timer = null; } }

    /// <summary>Runs one check. A second call while a run is active returns that run's report instead of starting another.</summary>
    public Task<WatchdogReport> RunAsync(WatchdogRunMode mode)
    {
        lock (_gate)
        {
            if (_current is { IsCompleted: false }) return _current;
            _lastRunUtc = _now();
            // A dedicated low-priority thread starts the work; it never touches the UI thread.
            var done = new TaskCompletionSource<WatchdogReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { done.SetResult(ExecuteAsync(mode).GetAwaiter().GetResult()); }
                catch (Exception ex) { done.SetException(ex); }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "NeoDM browser connection check" };
            _current = done.Task;
            thread.Start();
            return _current;
        }
    }

    // ---- the run ----

    private async Task<WatchdogReport> ExecuteAsync(WatchdogRunMode mode)
    {
        bool unlimited = mode == WatchdogRunMode.Repair;
        bool manual = mode is WatchdogRunMode.CheckNow or WatchdogRunMode.Repair;
        var repairs = new List<string>();
        var items = new List<CheckItem>();
        if (!manual && SkipReason() is { } why)
        {
            SkippedRuns++; LastSkipReason = why; _log("Check skipped: " + why);
            Rearm(IntervalFor(HealthyStreak));
            return Last ?? new WatchdogReport(WatchdogOverall.Ok, Array.Empty<CheckItem>(), _now(), Array.Empty<string>());
        }
        RunCount++;
        _anyInstalled = false;
        try
        {
            string? host = _env.HostCandidates.FirstOrDefault(_files.FileExists);
            string? configuredId = ReadConfiguredId(host);
            var ids = BridgeIds.Known(configuredId);
            items.Add(await CheckListenerAsync(unlimited, repairs));
            foreach (var browser in BrowserTarget.All)
            {
                var item = CheckBrowser(browser, host, ids, unlimited, repairs);
                if (item is not null) items.Add(item);
            }
            items.Add(await CheckHostStartAsync(host, configuredId ?? ids[0], manual));
        }
        catch (Exception ex)
        {
            _log("Check failed: " + ex);
            items.Add(new CheckItem("watchdog", Loc.T("bridge.check.name"), CheckState.Problem, Loc.T("bridge.check.failed"), ex.Message));
        }
        var overall = items.Any(i => i.State == CheckState.Problem) ? WatchdogOverall.Problem : repairs.Count > 0 ? WatchdogOverall.Repaired : WatchdogOverall.Ok;
        var report = new WatchdogReport(overall, items, _now(), repairs);
        Last = report;
        lock (_gate) { _healthyStreak = mode == WatchdogRunMode.Scheduled && overall == WatchdogOverall.Ok ? _healthyStreak + 1 : 0; }
        Rearm(IntervalFor(HealthyStreak));
        _log("Result: " + overall + (repairs.Count > 0 ? ". Repairs: " + string.Join("; ", repairs) : "") + ". " + string.Join(" | ", items.Where(i => i.State == CheckState.Problem).Select(i => i.Name + ": " + i.Summary)));
        try { ReportChanged?.Invoke(report); } catch (Exception ex) { _log("Report handler failed: " + ex.Message); }
        return report;
    }

    private string? SkipReason()
    {
        if (_machine is null) return null;
        try
        {
            if (_machine.IsBusy()) return "a full-screen app, a presentation or a locked session is active";
            if (_machine.IsPowerSaver()) return "battery saver is on";
        }
        catch (Exception) { }
        return null;
    }

    private string? ReadConfiguredId(string? hostExe)
    {
        if (hostExe is null) return null;
        try
        {
            var text = _files.ReadAllText(Path.Combine(Path.GetDirectoryName(hostExe)!, "bridge-config.json"));
            if (text is null) return null;
            using var doc = JsonDocument.Parse(text);
            foreach (var property in doc.RootElement.EnumerateObject())
                if (property.NameEquals("extensionId") || string.Equals(property.Name, "extensionId", StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.String && IsExtensionId(property.Value.GetString()!) ? property.Value.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException) { }
        return null;
    }

    private static bool IsExtensionId(string value) => value.Length == 32 && value.All(c => c is >= 'a' and <= 'p');

    private bool RepairAllowed(string key, bool unlimited)
    {
        if (!_env.AllowRepairs) return false;
        var now = _now();
        if (!unlimited && _repairAttempts.TryGetValue(key, out var last) && now - last < RepairLimit) return false;
        _repairAttempts[key] = now;
        return true;
    }

    // ---- a) the app's listener ----

    private async Task<CheckItem> CheckListenerAsync(bool unlimited, List<string> repairs)
    {
        string name = Loc.T("bridge.listener.name");
        bool ok = await SafePing();
        if (ok) return new CheckItem(ListenerId, name, CheckState.Ok, Loc.T("bridge.listener.ok"), Loc.T("bridge.listener.ok_detail", _env.PipeName));
        if (_recreateListener is null || !RepairAllowed(ListenerId, unlimited))
            return new CheckItem(ListenerId, name, CheckState.Problem, Loc.T("bridge.listener.down"),
                Loc.T("bridge.listener.silent", _env.PipeName) + (!_env.AllowRepairs ? " " + Loc.T("bridge.repair_off") : _recreateListener is null ? "" : " " + Loc.T("bridge.listener.tried")));
        _log("The app listener did not answer; restarting it.");
        bool recreated;
        try { recreated = await _recreateListener(); } catch (Exception ex) { _log("Restarting the listener failed: " + ex.Message); recreated = false; }
        if (recreated && await SafePing())
        {
            repairs.Add(Loc.T("bridge.listener.restarted"));
            return new CheckItem(ListenerId, name, CheckState.Repaired, Loc.T("bridge.listener.repaired"), Loc.T("bridge.listener.repaired_detail"));
        }
        return new CheckItem(ListenerId, name, CheckState.Problem, Loc.T("bridge.listener.down"), Loc.T("bridge.listener.still_down"));
    }

    private async Task<bool> SafePing()
    {
        try { return await _pipe.PingAsync(_env.PipeName, PipeTimeout, CancellationToken.None); }
        catch (Exception ex) { _log("Pipe check failed: " + ex.Message); return false; }
    }

    // ---- b) and c) registration ----

    private enum RegKind { Ok, NotRegistered, ManifestMissing, ManifestInvalid, HostMissing, HostElsewhere, MissingOrigin }
    private sealed record RegState(RegKind Kind, string Where, string? ManifestPath, string Detail);

    private RegState Evaluate(BrowserTarget browser, string? host, IReadOnlyList<string> ids)
    {
        // Chromium reads the current user key first and uses it alone when it exists, then the machine key.
        string? value = _registry.GetDefaultValue(RegistryScope.CurrentUser, browser.HostKey), where = "current user";
        if (string.IsNullOrWhiteSpace(value)) { value = _registry.GetDefaultValue(RegistryScope.LocalMachine, browser.HostKey); where = "all users"; }
        if (string.IsNullOrWhiteSpace(value)) return new RegState(RegKind.NotRegistered, "", null, Loc.T("bridge.reg.no_key", BridgeIds.HostName, browser.HostKey.Replace(@"\" + BridgeIds.HostName, "")));
        string manifestPath = value.Trim();
        var text = _files.ReadAllText(manifestPath);
        if (text is null) return new RegState(RegKind.ManifestMissing, where, manifestPath, Loc.T("bridge.reg.manifest_missing", WhereText(where), manifestPath));
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return new RegState(RegKind.ManifestInvalid, where, manifestPath, Loc.T("bridge.reg.not_json", manifestPath)); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new RegState(RegKind.ManifestInvalid, where, manifestPath, Loc.T("bridge.reg.not_object", manifestPath));
            string? Str(string key) => root.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            if (Str("name") != BridgeIds.HostName) return new RegState(RegKind.ManifestInvalid, where, manifestPath, Loc.T("bridge.reg.wrong_name", manifestPath, BridgeIds.HostName));
            if (Str("type") != "stdio") return new RegState(RegKind.ManifestInvalid, where, manifestPath, Loc.T("bridge.reg.wrong_type", manifestPath));
            var path = Str("path");
            if (string.IsNullOrWhiteSpace(path)) return new RegState(RegKind.ManifestInvalid, where, manifestPath, Loc.T("bridge.reg.no_path", manifestPath));
            string exe = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath) ?? "", path));
            if (!_files.FileExists(exe)) return new RegState(RegKind.HostMissing, where, manifestPath, Loc.T("bridge.reg.helper_missing", exe, manifestPath));
            if (host is not null && !SamePath(exe, host)) return new RegState(RegKind.HostElsewhere, where, manifestPath, Loc.T("bridge.reg.helper_elsewhere", manifestPath, exe, host));
            var origins = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("allowed_origins", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var origin in list.EnumerateArray()) if (origin.ValueKind == JsonValueKind.String) origins.Add(origin.GetString()!);
            var missing = ids.Where(id => !origins.Contains(BridgeIds.Origin(id))).ToList();
            if (missing.Count > 0) return new RegState(RegKind.MissingOrigin, where, manifestPath, Loc.T("bridge.reg.missing_origin", manifestPath, string.Join(", ", missing)));
            return new RegState(RegKind.Ok, where, manifestPath, manifestPath);
        }
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private bool BrowserInstalled(BrowserTarget browser)
    {
        foreach (var scope in new[] { RegistryScope.LocalMachine, RegistryScope.CurrentUser })
        {
            var path = _registry.GetDefaultValue(scope, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + browser.ExeName);
            if (!string.IsNullOrWhiteSpace(path) && _files.FileExists(path.Trim('"'))) return true;
        }
        return _env.ProgramDirectories.Any(dir => browser.InstallPaths.Any(rel => _files.FileExists(Path.Combine(dir, rel))));
    }

    private CheckItem? CheckBrowser(BrowserTarget browser, string? host, IReadOnlyList<string> ids, bool unlimited, List<string> repairs)
    {
        string id = "reg-" + browser.Id, name = Loc.T("bridge.reg.name", browser.Name);
        bool installed = BrowserInstalled(browser);
        if (installed) _anyInstalled = true;
        var state = Evaluate(browser, host, ids);
        var done = new List<string>();

        // (i) A stale current-user key beats a good machine key in the browser, so it goes first. Only the com.ndm.bridge key of this browser is removed.
        if (state.Where == "current user" && state.Kind is RegKind.ManifestMissing or RegKind.HostMissing && RepairAllowed(id + ":stale", unlimited))
        {
            bool removed = false;
            try { removed = _registry.DeleteSubKeyTree(RegistryScope.CurrentUser, browser.HostKey); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { _log("Could not remove the stale " + browser.Name + " key: " + ex.Message); }
            if (removed)
            {
                string note = Loc.T("bridge.reg.removed", browser.Name, state.Detail.TrimEnd('.'));
                done.Add(note);
                state = Evaluate(browser, host, ids);
            }
        }

        if (!installed && !browser.AlwaysListed && done.Count == 0) return null;
        if (!installed && state.Kind != RegKind.Ok)
            return done.Count > 0 ? Finish(id, name, CheckState.Repaired, Loc.T("bridge.reg.not_installed_cleaned", browser.Name), string.Join(" ", done), done, repairs)
                : new CheckItem(id, name, CheckState.NotInstalled, Loc.T("bridge.reg.not_installed", browser.Name), state.Kind == RegKind.NotRegistered ? "" : state.Detail);
        if (!installed) return new CheckItem(id, name, CheckState.NotInstalled, Loc.T("bridge.reg.not_installed", browser.Name), "");

        if (state.Kind != RegKind.Ok)
        {
            if (host is null)
                return new CheckItem(id, name, CheckState.Problem, Loc.T("bridge.reg.helper_gone", browser.Name), state.Detail + " " + Loc.T("bridge.reinstall"));
            // (ii) and (iii) A correct manifest in the user's folder, registered for the current user (no administrator rights).
            if (RepairAllowed(id + ":" + state.Kind, unlimited))
            {
                try
                {
                    _files.WriteAllText(_env.UserManifestPath, BuildManifest(host, ids));
                    _registry.SetDefaultValue(RegistryScope.CurrentUser, browser.HostKey, _env.UserManifestPath);
                    done.Add(Loc.T("bridge.reg.registered", browser.Name, _env.UserManifestPath, state.Detail.TrimEnd('.')));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    _log("Registering " + browser.Name + " failed: " + ex.Message);
                }
                state = Evaluate(browser, host, ids);
            }
            else if (!_env.AllowRepairs) state = state with { Detail = state.Detail + " " + Loc.T("bridge.repair_off") };
            else state = state with { Detail = state.Detail + " " + Loc.T("bridge.repair_tried") };
        }

        if (state.Kind == RegKind.Ok)
            return done.Count > 0 ? Finish(id, name, CheckState.Repaired, Loc.T("bridge.reg.connected_again", browser.Name), string.Join(" ", done), done, repairs)
                : new CheckItem(id, name, CheckState.Ok, Loc.T("bridge.reg.connected", browser.Name), Loc.T("bridge.reg.registered_for", WhereText(state.Where), state.ManifestPath));
        done.ForEach(repairs.Add);
        return new CheckItem(id, name, CheckState.Problem, ProblemSummary(browser, state.Kind), state.Detail);
    }

    private static CheckItem Finish(string id, string name, CheckState state, string summary, string detail, List<string> done, List<string> repairs)
    {
        repairs.AddRange(done);
        return new CheckItem(id, name, state, summary, detail);
    }

    private static string ProblemSummary(BrowserTarget b, RegKind kind) => kind switch
    {
        RegKind.NotRegistered => Loc.T("bridge.kind.not_registered", b.Name),
        RegKind.ManifestMissing => Loc.T("bridge.kind.manifest_missing", b.Name),
        RegKind.ManifestInvalid => Loc.T("bridge.kind.manifest_invalid", b.Name),
        RegKind.HostMissing => Loc.T("bridge.kind.host_missing", b.Name),
        RegKind.HostElsewhere => Loc.T("bridge.kind.host_elsewhere", b.Name),
        RegKind.MissingOrigin => Loc.T("bridge.kind.missing_origin", b.Name),
        _ => Loc.T("bridge.kind.other", b.Name)
    };

    public static string BuildManifest(string hostExe, IReadOnlyList<string> ids)
    {
        var manifest = new Dictionary<string, object>
        {
            ["name"] = BridgeIds.HostName,
            ["description"] = "NeoDM native download bridge",
            ["path"] = hostExe,
            ["type"] = "stdio",
            ["allowed_origins"] = ids.Select(BridgeIds.Origin).ToArray()
        };
        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    // ---- d) host start test ----

    private async Task<CheckItem> CheckHostStartAsync(string? host, string extensionId, bool force)
    {
        string name = Loc.T("bridge.host.name");
        if (host is null)
            return new CheckItem(HostTestId, name, CheckState.Problem, Loc.T("bridge.host.file_missing"), Loc.T("bridge.host.not_found", string.Join(Loc.T("bridge.or"), _env.HostCandidates.Select(c => Path.GetDirectoryName(c)))));
        if (!_anyInstalled)
            return new CheckItem(HostTestId, name, CheckState.NotInstalled, Loc.T("bridge.host.no_browser"), "");
        if (!force && SafeHostRunning())
            return new CheckItem(HostTestId, name, CheckState.Ok, Loc.T("bridge.host.running"), Loc.T("bridge.host.running_detail"));
        var now = _now();
        if (!force && _lastHostItem is not null && _lastHostTestUtc is { } at && now - at < HostTestEvery - TimeSpan.FromSeconds(5)) return _lastHostItem;
        HostProbeResult result;
        try { result = await _launcher.ProbeAsync(host, BridgeIds.Origin(extensionId), HostTestTimeout, CancellationToken.None); }
        catch (Exception ex) { result = new HostProbeResult(false, HostProbeFailure.StartFailed, Detail: ex.Message); }
        var item = result.Ok
            ? new CheckItem(HostTestId, name, CheckState.Ok, Loc.T("bridge.host.ok"), result.AppVersion is { Length: > 0 } v ? Loc.T("bridge.host.answered_version", Math.Max(1, (int)result.Elapsed.TotalMilliseconds), v) : Loc.T("bridge.host.answered", Math.Max(1, (int)result.Elapsed.TotalMilliseconds)))
            : new CheckItem(HostTestId, name, CheckState.Problem, HostFailureSummary(result), HostFailureDetail(result, host));
        _lastHostItem = item; _lastHostTestUtc = now;
        return item;
    }

    /// <summary>The registration scope as words ("current user" / "all users" stay as ids in the logic).</summary>
    private static string WhereText(string where) => Loc.T(where == "all users" ? "bridge.where.all" : "bridge.where.user");

    private bool SafeHostRunning()
    {
        try { return _machine?.BrowserHostRunning() == true; } catch (Exception) { return false; }
    }

    public static string HostFailureSummary(HostProbeResult r) => r.Failure switch
    {
        HostProbeFailure.ExeMissing => Loc.T("bridge.host.file_missing"),
        HostProbeFailure.StartFailed => Loc.T("bridge.host.start_failed"),
        HostProbeFailure.ExitedEarly => Loc.T("bridge.host.exited"),
        HostProbeFailure.NoReply => Loc.T("bridge.host.no_reply", (int)HostTestTimeout.TotalSeconds),
        HostProbeFailure.BadReply => Loc.T("bridge.host.bad_reply"),
        _ => Loc.T("bridge.host.test_failed")
    };

    private static string HostFailureDetail(HostProbeResult r, string host)
    {
        var text = new StringBuilder(host + ". ");
        if (r.ExitCode is { } code) text.Append(Loc.T("bridge.host.exit_code", code) + " ");
        if (r.StderrLine is { Length: > 0 } line) text.Append(Loc.T("bridge.host.printed", line) + " ");
        if (r.ExitCode == 2) text.Append(Loc.T("bridge.host.code2") + " ");
        if (r.Detail is { Length: > 0 }) text.Append(r.Detail);
        return text.ToString().Trim();
    }
}
