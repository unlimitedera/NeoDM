using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

using NDM.App.Localization;
namespace NDM.App;

/// <summary>When automatic checks run. Pure functions so the throttle and the skip memory can be tested.</summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20), Period = TimeSpan.FromHours(24), MinimumGap = TimeSpan.FromHours(12);
    /// <summary>1.11.0: a check never starts on a fixed beat. The first one waits 20 to 80 seconds, later ones 22 to 26 hours; <paramref name="random"/> returns 0..1.</summary>
    public static TimeSpan Jittered(TimeSpan baseValue, TimeSpan spread, double random) => baseValue + TimeSpan.FromTicks((long)(spread.Ticks * Math.Clamp(random, 0, 1)));
    public static TimeSpan FirstDelay(double random) => Jittered(StartDelay, TimeSpan.FromSeconds(60), random);
    public static TimeSpan NextPeriod(double random) => Jittered(Period - TimeSpan.FromHours(2), TimeSpan.FromHours(4), random);
    public static bool ShouldAutoCheck(AppSettings settings, DateTimeOffset now) =>
        settings.CheckForUpdates && settings.UpdateNotifications != UpdateNotifyMode.Off && (settings.LastUpdateCheckUtc is not { } last || now - last >= MinimumGap || last > now + TimeSpan.FromMinutes(5)); // a clock set backwards never blocks checks
    public static bool ShouldNotify(AppSettings settings, ReleaseInfo release) =>
        !string.Equals(settings.SkippedUpdateVersion, release.VersionText, StringComparison.Ordinal);
}

/// <summary>Opens a link in the default browser. A seam so tests can capture clicks instead of launching a browser.</summary>
public static class UrlOpener
{
    public static Action<string> Open { get; set; } = url =>
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    };
}

/// <summary>Owns checking (automatic and manual), the throttle, 'skip this version' and showing the result.</summary>
public sealed class UpdateController
{
    private readonly AppSettings settings;
    private readonly Action save;
    private readonly Func<UpdateChecker> checkerFactory;
    private readonly Func<DateTimeOffset> clock;
    private DispatcherTimer? timer;
    private bool checking;
    /// <summary>Opens the update window. Called only when the user asks (pill, Help menu, About button, Check now), never by an automatic check.</summary>
    public Action<ReleaseInfo> ShowAvailable { get; set; } = _ => { };
    /// <summary>The newer release an automatic or manual check found and the user has not skipped; shown as the status bar pill. Null when none.</summary>
    public ReleaseInfo? Pending { get; private set; }
    public event Action? PendingChanged;
    /// <summary>The single notice of 'Notify me when idle'. The app shows a tray balloon; tests count calls.</summary>
    public Action<ReleaseInfo> ShowToast { get; set; } = _ => { };
    public IShellNotificationState Shell { get; set; } = new SystemNotificationState();
    public Func<double> Random { get; set; } = () => System.Random.Shared.NextDouble();
    public UpdateToastPolicy_Result LastToast { get; private set; }
    public record struct UpdateToastPolicy_Result(ToastDecision Decision);
    public Action<string> ShowMessage { get; set; } = _ => { };
    public UpdateInstaller Installer { get; }
    public string CurrentVersion { get; }

    public UpdateController(AppSettings settings, Action save, string currentVersion, Func<UpdateChecker> checkerFactory, UpdateInstaller installer, Func<DateTimeOffset>? clock = null)
    {
        this.settings = settings; this.save = save; CurrentVersion = currentVersion; this.checkerFactory = checkerFactory; Installer = installer; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>~20 s after start, then every 24 h while running. Each tick still honours the 12 h gap stored in settings.</summary>
    public void Start(TimeSpan? firstDelay = null)
    {
        Installer.CleanUp(); // leftovers of an earlier update
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = firstDelay ?? UpdateSchedule.FirstDelay(Random()) };
        timer.Tick += async (_, _) => { timer.Interval = UpdateSchedule.NextPeriod(Random()); await RunAutomaticAsync(); };
        timer.Start();
        toastTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(5) };
        toastTimer.Tick += (_, _) => TryToast();
        toastTimer.Start();
    }
    private DispatcherTimer? toastTimer;
    public void Stop() { timer?.Stop(); toastTimer?.Stop(); }

    /// <summary>Background check: silent unless a new, not-skipped version exists.</summary>
    public async Task<UpdateCheckResult?> RunAutomaticAsync()
    {
        if (checking || !UpdateSchedule.ShouldAutoCheck(settings, clock())) return null;
        var result = await CheckAsync();
        // Quiet by design: an automatic check only records the release. The pill, the menu entries and (when allowed and safe) one notice tell the user; no window opens and nothing takes focus.
        if (result is { Outcome: UpdateOutcome.Available, Release: { } release } && UpdateSchedule.ShouldNotify(settings, release)) { SetPending(release); TryToast(); }
        else if (result is { Outcome: UpdateOutcome.UpToDate }) SetPending(null);
        return result;
    }

    /// <summary>Help &gt; Check for updates / About button: always answers, even when the version was skipped.</summary>
    public async Task<UpdateCheckResult?> CheckNowAsync(Action<string>? report = null)
    {
        if (checking) return null;
        var result = await CheckAsync();
        switch (result.Outcome)
        {
            case UpdateOutcome.Available when result.Release is { } release: SetPending(release); ShowAvailable(release); break;
            case UpdateOutcome.UpToDate: (report ?? ShowMessage)(Loc.T("update.uptodate", CurrentVersion)); break;
            default: (report ?? ShowMessage)(Loc.T("update.check_failed", ErrorMessages.OneLine(result.Reason) is { Length: > 0 } reason ? reason : Loc.T("update.check_failed_hint"))); break;
        }
        return result;
    }

    /// <summary>Records (or clears) the release the status bar pill announces.</summary>
    public void SetPending(ReleaseInfo? release) { if (Equals(Pending?.VersionText, release?.VersionText)) { Pending = release; return; } Pending = release; PendingChanged?.Invoke(); }

    /// <summary>Shows the single notice for the pending release when the setting allows it and the machine is quiet (not busy, not presenting, no full screen window, user idle). Returns the decision.</summary>
    public ToastDecision TryToast()
    {
        if (Pending is not { } release) return ToastDecision.Disabled;
        var decision = UpdateToastPolicy.Decide(settings.UpdateNotifications, Shell, release.VersionText, settings.UpdateToastVersion);
        LastToast = new(decision);
        if (decision != ToastDecision.Show) return decision;
        settings.UpdateToastVersion = release.VersionText;
        try { save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        ShowToast(release);
        return decision;
    }

    private async Task<UpdateCheckResult> CheckAsync()
    {
        checking = true;
        try
        {
            var result = await checkerFactory().CheckAsync();
            settings.LastUpdateCheckUtc = clock(); // recorded for every attempt: keeps the once-per-12-hours promise (and GitHub's rate limit) even when offline
            try { save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return result;
        }
        finally { checking = false; }
    }

    public void SkipVersion(ReleaseInfo release)
    {
        settings.SkippedUpdateVersion = release.VersionText;
        if (Pending?.VersionText == release.VersionText) SetPending(null); // skipped: the status bar pill goes away
        try { save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
