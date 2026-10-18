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
    public static bool ShouldAutoCheck(AppSettings settings, DateTimeOffset now) =>
        settings.CheckForUpdates && (settings.LastUpdateCheckUtc is not { } last || now - last >= MinimumGap || last > now + TimeSpan.FromMinutes(5)); // a clock set backwards never blocks checks
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
    public Action<ReleaseInfo> ShowAvailable { get; set; } = _ => { };
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
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = firstDelay ?? UpdateSchedule.StartDelay };
        timer.Tick += async (_, _) => { timer.Interval = UpdateSchedule.Period; await RunAutomaticAsync(); };
        timer.Start();
    }
    public void Stop() => timer?.Stop();

    /// <summary>Background check: silent unless a new, not-skipped version exists.</summary>
    public async Task<UpdateCheckResult?> RunAutomaticAsync()
    {
        if (checking || !UpdateSchedule.ShouldAutoCheck(settings, clock())) return null;
        var result = await CheckAsync();
        if (result is { Outcome: UpdateOutcome.Available, Release: { } release } && UpdateSchedule.ShouldNotify(settings, release)) ShowAvailable(release);
        return result;
    }

    /// <summary>Help &gt; Check for updates / About button: always answers, even when the version was skipped.</summary>
    public async Task<UpdateCheckResult?> CheckNowAsync(Action<string>? report = null)
    {
        if (checking) return null;
        var result = await CheckAsync();
        switch (result.Outcome)
        {
            case UpdateOutcome.Available when result.Release is { } release: ShowAvailable(release); break;
            case UpdateOutcome.UpToDate: (report ?? ShowMessage)(Loc.T("update.uptodate", CurrentVersion)); break;
            default: (report ?? ShowMessage)(Loc.T("update.check_failed", ErrorMessages.OneLine(result.Reason) is { Length: > 0 } reason ? reason : Loc.T("update.check_failed_hint"))); break;
        }
        return result;
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
        try { save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
