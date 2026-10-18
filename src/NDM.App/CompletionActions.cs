using System.Diagnostics;
using System.IO;

using NDM.App.Localization;
namespace NDM.App;

public static class CompletionActionInfo
{
    public static readonly IReadOnlyList<CompletionActionKind> All = Enum.GetValues<CompletionActionKind>();
    public static string Label(CompletionActionKind kind) => kind switch
    {
        CompletionActionKind.None => Loc.T("completion.label.none"),
        CompletionActionKind.PlaySound => Loc.T("completion.label.playsound"),
        CompletionActionKind.OpenFolder => Loc.T("completion.label.openfolder"),
        CompletionActionKind.ExitApp => Loc.T("completion.label.exitapp"),
        CompletionActionKind.Lock => Loc.T("completion.label.lock"),
        CompletionActionKind.Sleep => Loc.T("completion.label.sleep"),
        CompletionActionKind.Hibernate => Loc.T("completion.label.hibernate"),
        CompletionActionKind.LogOff => Loc.T("completion.label.logoff"),
        CompletionActionKind.Restart => Loc.T("completion.label.restart"),
        CompletionActionKind.Shutdown => Loc.T("completion.label.shutdown"),
        _ => kind.ToString()
    };
    public static string Description(CompletionActionKind kind) => kind switch
    {
        CompletionActionKind.None => Loc.T("completion.desc.none"),
        CompletionActionKind.PlaySound => Loc.T("completion.desc.playsound"),
        CompletionActionKind.OpenFolder => Loc.T("completion.desc.openfolder"),
        CompletionActionKind.ExitApp => Loc.T("completion.desc.exitapp"),
        CompletionActionKind.Lock => Loc.T("completion.desc.lock"),
        CompletionActionKind.Sleep => Loc.T("completion.desc.sleep"),
        CompletionActionKind.Hibernate => Loc.T("completion.desc.hibernate"),
        CompletionActionKind.LogOff => Loc.T("completion.desc.logoff"),
        CompletionActionKind.Restart => Loc.T("completion.desc.restart"),
        CompletionActionKind.Shutdown => Loc.T("completion.desc.shutdown"),
        _ => ""
    };
    /// <summary>Actions that end or suspend the user's session and therefore get the cancellable countdown.</summary>
    public static bool IsPower(CompletionActionKind kind) => kind is CompletionActionKind.Sleep or CompletionActionKind.Hibernate or CompletionActionKind.LogOff or CompletionActionKind.Restart or CompletionActionKind.Shutdown;
    public static bool UsesForce(CompletionActionKind kind) => kind is CompletionActionKind.Restart or CompletionActionKind.Shutdown;
    /// <summary>Verb phrase for "NeoDM will ... in N seconds".</summary>
    public static string Verb(CompletionActionKind kind) => kind switch
    {
        CompletionActionKind.Sleep => Loc.T("completion.verb.sleep"),
        CompletionActionKind.Hibernate => Loc.T("completion.verb.hibernate"),
        CompletionActionKind.LogOff => Loc.T("completion.verb.logoff"),
        CompletionActionKind.Restart => Loc.T("completion.verb.restart"),
        CompletionActionKind.Shutdown => Loc.T("completion.verb.shutdown"),
        _ => Label(kind).ToLowerInvariant()
    };
    /// <summary>"NeoDM will put the computer to sleep in 5 seconds", as one sentence per action so every language orders the words its own way.</summary>
    public static string CountdownMessage(CompletionActionKind kind, int seconds) =>
        Loc.T("completion.countdown." + kind.ToString().ToLowerInvariant(), Loc.Plural("time.seconds_long", seconds));
}

public sealed record SystemCommand(string FileName, string Arguments);

/// <summary>The exact command lines used for system actions. Fixed strings, system32 absolute paths, no user input.</summary>
public static class PowerCommands
{
    private static string System32(string file) => Path.Combine(Environment.SystemDirectory, file);
    public static SystemCommand? For(CompletionActionKind kind, bool force) => kind switch
    {
        CompletionActionKind.Lock => new(System32("rundll32.exe"), "user32.dll,LockWorkStation"),
        CompletionActionKind.Sleep => new(System32("rundll32.exe"), "powrprof.dll,SetSuspendState 0,1,0"),
        CompletionActionKind.Hibernate => new(System32("shutdown.exe"), "/h"),
        CompletionActionKind.LogOff => new(System32("shutdown.exe"), "/l"),
        CompletionActionKind.Restart => new(System32("shutdown.exe"), force ? "/r /f /t 0" : "/r /t 0"),
        CompletionActionKind.Shutdown => new(System32("shutdown.exe"), force ? "/s /f /t 0" : "/s /t 0"),
        _ => null
    };
}

/// <summary>Seam for everything that touches the real session. Tests substitute a recording fake.</summary>
public interface IPowerActions { void Execute(CompletionActionKind kind, bool force); }

public sealed class SystemPowerActions : IPowerActions
{
    public void Execute(CompletionActionKind kind, bool force)
    {
        var command = PowerCommands.For(kind, force) ?? throw new ArgumentOutOfRangeException(nameof(kind));
        Process.Start(new ProcessStartInfo(command.FileName, command.Arguments) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    }
}

/// <summary>Validation and launch description for the optional "run program" step. Never goes through cmd.exe /c concatenation.</summary>
public static class RunProgramValidator
{
    private static readonly string[] DirectExtensions = { ".exe", ".com" };
    private static readonly string[] BatchExtensions = { ".bat", ".cmd" };
    private const string BatchMetacharacters = "&|<>^%\"!";
    /// <summary>Returns a user-facing problem, or null when the program and arguments are acceptable.</summary>
    public static string? Validate(string? path, string? arguments)
    {
        path = path?.Trim() ?? ""; arguments ??= "";
        if (path.Length == 0) return Loc.T("runprogram.error.choose");
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('"')) return Loc.T("runprogram.error.invalid_chars");
        if (!Path.IsPathFullyQualified(path)) return Loc.T("runprogram.error.full_path");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (!DirectExtensions.Contains(ext) && !BatchExtensions.Contains(ext) && ext != ".lnk") return Loc.T("runprogram.error.extension");
        if (!File.Exists(path)) return Loc.T("runprogram.error.not_found");
        if (arguments.Length > 2000) return Loc.T("runprogram.error.args_long");
        if (arguments.Any(c => char.IsControl(c))) return Loc.T("runprogram.error.args_control");
        if (BatchExtensions.Contains(ext) && arguments.IndexOfAny(BatchMetacharacters.ToCharArray()) >= 0)
            return Loc.T("runprogram.error.args_batch");
        return null;
    }
    public static ProcessStartInfo BuildStartInfo(string path, string? arguments)
    {
        if (Validate(path, arguments) is { } problem) throw new FormatException(problem);
        path = Path.GetFullPath(path.Trim());
        string ext = Path.GetExtension(path).ToLowerInvariant();
        // .exe/.com/.bat/.cmd are started directly by CreateProcess; only a shortcut needs the shell to resolve it.
        return new ProcessStartInfo(path) { Arguments = arguments ?? "", UseShellExecute = ext == ".lnk", WorkingDirectory = Path.GetDirectoryName(path)! };
    }
}

public enum CountdownOutcome { Elapsed, DoItNow, Cancelled }
public interface ICountdownPrompt { CountdownOutcome Show(CompletionActionKind kind, int seconds); }

/// <summary>Owns the session-scoped choice (with optional remembering) and executes it when the queue finishes.</summary>
public sealed class CompletionActionRunner
{
    private readonly AppSettings settings;
    private readonly IPowerActions power;
    private readonly Action save;
    private readonly Action playSound, openFolder, exitApp;
    private readonly Action<ProcessStartInfo> launch;
    public ICountdownPrompt Countdown { get; set; }
    public int CountdownSeconds { get; set; } = 30;
    public string? LastError { get; private set; }
    public event Action? SessionActionChanged;
    public CompletionActionRunner(AppSettings settings, IPowerActions power, ICountdownPrompt countdown, Action save, Action playSound, Action openFolder, Action exitApp, Action<ProcessStartInfo>? launch = null)
    {
        this.settings = settings; this.power = power; Countdown = countdown; this.save = save;
        this.playSound = playSound; this.openFolder = openFolder; this.exitApp = exitApp;
        this.launch = launch ?? (psi => Process.Start(psi)?.Dispose());
    }
    public CompletionActionKind Current => settings.SessionCompletionAction;
    /// <summary>Applies a choice for this session; persists it too only when "Remember" is on.</summary>
    public void Choose(CompletionActionKind kind)
    {
        settings.SessionCompletionAction = kind;
        if (settings.CompletionRemember)
        {
            var previous = settings.CompletionAction;
            settings.CompletionAction = kind;
            try { save(); LastError = null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { settings.CompletionAction = previous; LastError = Loc.T("completion.error.not_saved"); }
        }
        SessionActionChanged?.Invoke();
    }
    /// <summary>Called once per queue-finished edge. Returns true when the configured action was carried out.</summary>
    public bool Run()
    {
        var kind = Current;
        return Perform(kind, settings.CompletionForceClose, resetSessionOnCancel: true, runProgram: true);
    }
    /// <summary>Carries out one download's own completion action with the same countdown/cancel safety. Force only reaches Shut down / Restart.</summary>
    public bool RunFor(CompletionActionKind kind, bool force) => Perform(kind, force, resetSessionOnCancel: false, runProgram: false);
    private bool Perform(CompletionActionKind kind, bool force, bool resetSessionOnCancel, bool runProgram)
    {
        LastError = null;
        if (CompletionActionInfo.IsPower(kind))
        {
            var outcome = Countdown.Show(kind, CountdownSeconds);
            if (outcome == CountdownOutcome.Cancelled)
            {
                if (resetSessionOnCancel) { settings.SessionCompletionAction = CompletionActionKind.None; SessionActionChanged?.Invoke(); } // Cancel resets this session only; a remembered preference is untouched.
                return false;
            }
        }
        if (runProgram) RunProgram();
        try
        {
            switch (kind)
            {
                case CompletionActionKind.None: return false;
                case CompletionActionKind.PlaySound: playSound(); break;
                case CompletionActionKind.OpenFolder: openFolder(); break;
                case CompletionActionKind.ExitApp: exitApp(); break;
                default: power.Execute(kind, force && CompletionActionInfo.UsesForce(kind)); break;
            }
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { LastError = Loc.T("completion.error.failed", CompletionActionInfo.Label(kind)); return false; }
    }
    private void RunProgram()
    {
        if (!settings.RunProgramEnabled) return;
        try { launch(RunProgramValidator.BuildStartInfo(settings.RunProgramPath, settings.RunProgramArguments)); }
        catch (Exception ex) when (ex is FormatException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { LastError = Loc.T("completion.error.program_failed"); }
    }
}

/// <summary>Detects the busy-to-idle edge of a queue snapshot stream.</summary>
public readonly record struct QueueJob(string Id, string Status, DateTimeOffset? ScheduledAt);

public sealed class QueueFinishTrigger
{
    private readonly HashSet<string> completed = new();
    private bool hadActive;
    private int completedThisRun;
    public bool IgnoreScheduled { get; set; }
    /// <param name="history">Jobs already in the queue at start; they are history, never completions.</param>
    public QueueFinishTrigger(IEnumerable<QueueJob> history)
    {
        foreach (var job in history) if (job.Status == "Completed") completed.Add(job.Id);
    }
    /// <summary>Records an item that was already complete when first seen (restored torrents): history, not a completion.</summary>
    public void MarkHistory(QueueJob job) { if (job.Status == "Completed") completed.Add(job.Id); }
    private static bool IsActive(string status) => status is "Downloading" or "Connecting" or "Queued";
    /// <summary>True exactly once per batch: when active jobs drop to zero and something completed in this run.</summary>
    public bool Observe(IEnumerable<QueueJob> snapshot, DateTimeOffset now)
    {
        int active = 0; bool futureScheduled = false;
        foreach (var job in snapshot)
        {
            if (job.Status == "Completed" && completed.Add(job.Id)) { completedThisRun++; hadActive = true; }
            if (IsActive(job.Status)) active++;
            else if (job.Status == "Scheduled" && job.ScheduledAt is { } at && at > now) futureScheduled = true;
        }
        if (active > 0) { hadActive = true; return false; }
        if (!hadActive) return false;
        if (futureScheduled && !IgnoreScheduled) return false; // Hold the armed state until the scheduled job has run.
        bool fire = completedThisRun > 0;
        hadActive = false; completedThisRun = 0;
        return fire;
    }
}
