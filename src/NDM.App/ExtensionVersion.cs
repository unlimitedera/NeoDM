using System.Text.RegularExpressions;

using NDM.App.Localization;
namespace NDM.App;

public enum ExtensionStatus { Match, Older, Newer, Missing }

/// <summary>Which copy of the extension talks to the app, from the extension ID the native host forwards (1.5.6): Store = the Chrome Web Store build,
/// Development = the unpacked copy (the key in the manifest gives it the development ID), Unknown = an older host or an ID that is neither.</summary>
public enum ExtensionChannel { Unknown, Store, Development }

public static class ExtensionChannels
{
    public static ExtensionChannel Of(string? extensionId) =>
        extensionId == BridgeIds.StoreId ? ExtensionChannel.Store : extensionId == BridgeIds.DevelopmentId ? ExtensionChannel.Development : ExtensionChannel.Unknown;
}

/// <summary>What the capture notice says: the extension's version (null = an extension that does not report one), the app's, and how they compare.</summary>
public sealed record ExtensionCheck(ExtensionStatus Status, string? ExtensionVersion, string AppVersion, ExtensionChannel Channel = ExtensionChannel.Unknown)
{
    public string Message => ExtensionVersionCheck.Describe(this);
}

/// <summary>Compares the extension's manifest version (sent as the optional <c>extVersion</c> field) with the running app's version.</summary>
public static class ExtensionVersionCheck
{
    private static readonly Regex Format = new(@"^\d{1,4}(\.\d{1,4}){1,3}\z", RegexOptions.CultureInvariant);

    /// <summary>Wire format: 2 to 4 numeric parts of 1 to 4 digits, at most 24 characters. Same rule as the native host.</summary>
    public static bool IsValid(string? value) => value is { Length: <= 24 } && Format.IsMatch(value);

    private static Version? Parse(string? text)
    {
        var core = (text ?? "").Trim().Split('+')[0];
        if (!IsValid(core)) return null;
        var parts = core.Split('.').Select(int.Parse).ToList();
        while (parts.Count < 4) parts.Add(0); // "1.4" equals "1.4.0.0"
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    /// <param name="extensionVersion">Null, empty or malformed = the extension did not report a usable version (an older extension).</param>
    /// <param name="appVersion">The running app's informational version; build metadata after '+' is ignored.</param>
    public static ExtensionCheck Compare(string? extensionVersion, string appVersion, ExtensionChannel channel = ExtensionChannel.Unknown)
    {
        var app = Parse(appVersion) ?? Parse(SemVer.Running(appVersion).ToString(3)) ?? new Version(0, 0, 0, 0);
        string appText = appVersion.Split('+')[0];
        var ext = Parse(extensionVersion);
        if (ext is null) return new ExtensionCheck(ExtensionStatus.Missing, null, appText, channel);
        string extText = extensionVersion!.Trim().Split('+')[0];
        return new ExtensionCheck(ext < app ? ExtensionStatus.Older : ext > app ? ExtensionStatus.Newer : ExtensionStatus.Match, extText, appText, channel);
    }

    public static string Describe(ExtensionCheck check) => check.Status switch
    {
        ExtensionStatus.Missing => Loc.T("extver.missing", check.AppVersion),
        ExtensionStatus.Older when check.Channel == ExtensionChannel.Store => Loc.T("extver.store_older", check.ExtensionVersion, check.AppVersion),
        ExtensionStatus.Older => Loc.T("extver.older", check.ExtensionVersion, check.AppVersion),
        ExtensionStatus.Newer => Loc.T("extver.newer", check.ExtensionVersion, check.AppVersion),
        _ => "",
    };
}

/// <summary>
/// Decides whether a capture should show the version notice. At most once per app session for each (extension version, app version) pair,
/// and never for an extension version the user chose not to be reminded about (stored in <see cref="AppSettings.MutedExtensionVersions"/>).
/// The Chrome Web Store build is handled differently (1.5.6): it updates itself, so it is mentioned only when it is OLDER than the app, and at most once
/// a day for each (extension version, app version) pair; the last time is kept in <see cref="AppSettings.ExtensionNoticeTimes"/> across restarts.
/// </summary>
public sealed class ExtensionNotice
{
    public const string MissingKey = "none";
    private readonly AppSettings settings;
    private readonly Action save;
    private readonly string appVersion;
    private readonly HashSet<string> shown = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> clock;
    /// <summary>Least time between two store notices for the same extension version and app version.</summary>
    public static readonly TimeSpan StoreNoticeGap = TimeSpan.FromHours(24);

    public ExtensionNotice(AppSettings settings, Action save, string appVersion, Func<DateTimeOffset>? clock = null)
    { this.settings = settings; this.save = save; this.appVersion = appVersion; this.clock = clock ?? (() => DateTimeOffset.UtcNow); }

    public static string KeyOf(string? extensionVersion) => ExtensionVersionCheck.IsValid(extensionVersion) ? extensionVersion! : MissingKey;

    /// <summary>The notice to show for this capture, or null (versions match, already shown this session, or muted). Calling it counts as showing.</summary>
    public ExtensionCheck? Evaluate(string? extensionVersion, string? extensionId = null)
    {
        var channel = ExtensionChannels.Of(extensionId);
        var check = ExtensionVersionCheck.Compare(extensionVersion, appVersion, channel);
        if (check.Status == ExtensionStatus.Match) return null;
        bool store = channel == ExtensionChannel.Store && check.Status != ExtensionStatus.Missing;
        if (store && check.Status == ExtensionStatus.Newer) return null; // the store may be ahead of this app for a while: nothing to do for the user
        string key = KeyOf(extensionVersion);
        lock (gate)
        {
            if (settings.MutedExtensionVersions.Contains(key)) return null;
            if (!shown.Add((store ? "store|" : "") + key + "|" + check.AppVersion)) return null;
            if (!store) return check;
            string pair = "store|" + key + "|" + check.AppVersion;
            var now = clock();
            if (settings.ExtensionNoticeTimes.TryGetValue(pair, out var last) && now - last < StoreNoticeGap && last <= now + TimeSpan.FromMinutes(5)) return null;
            settings.ExtensionNoticeTimes[pair] = now;
            while (settings.ExtensionNoticeTimes.Count > 20) settings.ExtensionNoticeTimes.Remove(settings.ExtensionNoticeTimes.OrderBy(e => e.Value).First().Key);
        }
        try { save(); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        return check;
    }

    /// <summary>'Don't remind me about this version': remembered across restarts.</summary>
    public void Mute(string? extensionVersion)
    {
        string key = KeyOf(extensionVersion);
        lock (gate)
        {
            if (settings.MutedExtensionVersions.Contains(key)) return;
            settings.MutedExtensionVersions.Add(key);
            if (settings.MutedExtensionVersions.Count > 50) settings.MutedExtensionVersions.RemoveAt(0);
        }
        try { save(); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }
}
