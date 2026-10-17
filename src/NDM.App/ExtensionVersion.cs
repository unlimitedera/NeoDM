using System.Text.RegularExpressions;

namespace NDM.App;

public enum ExtensionStatus { Match, Older, Newer, Missing }

/// <summary>What the capture notice says: the extension's version (null = an extension that does not report one), the app's, and how they compare.</summary>
public sealed record ExtensionCheck(ExtensionStatus Status, string? ExtensionVersion, string AppVersion)
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
    public static ExtensionCheck Compare(string? extensionVersion, string appVersion)
    {
        var app = Parse(appVersion) ?? Parse(SemVer.Running(appVersion).ToString(3)) ?? new Version(0, 0, 0, 0);
        string appText = appVersion.Split('+')[0];
        var ext = Parse(extensionVersion);
        if (ext is null) return new ExtensionCheck(ExtensionStatus.Missing, null, appText);
        string extText = extensionVersion!.Trim().Split('+')[0];
        return new ExtensionCheck(ext < app ? ExtensionStatus.Older : ext > app ? ExtensionStatus.Newer : ExtensionStatus.Match, extText, appText);
    }

    public static string Describe(ExtensionCheck check) => check.Status switch
    {
        ExtensionStatus.Missing => $"Your browser extension is older than this app (NeoDM is {check.AppVersion}). Update the extension so captured downloads and the media list keep working.",
        ExtensionStatus.Older => $"Your browser extension is version {check.ExtensionVersion}, NeoDM is {check.AppVersion}. Update the extension so captured downloads and the media list keep working.",
        ExtensionStatus.Newer => $"Your browser extension is version {check.ExtensionVersion}, which is newer than NeoDM {check.AppVersion}. Update NeoDM so captured downloads and the media list keep working.",
        _ => "",
    };
}

/// <summary>
/// Decides whether a capture should show the version notice. At most once per app session for each (extension version, app version) pair,
/// and never for an extension version the user chose not to be reminded about (stored in <see cref="AppSettings.MutedExtensionVersions"/>).
/// </summary>
public sealed class ExtensionNotice
{
    public const string MissingKey = "none";
    private readonly AppSettings settings;
    private readonly Action save;
    private readonly string appVersion;
    private readonly HashSet<string> shown = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public ExtensionNotice(AppSettings settings, Action save, string appVersion) { this.settings = settings; this.save = save; this.appVersion = appVersion; }

    public static string KeyOf(string? extensionVersion) => ExtensionVersionCheck.IsValid(extensionVersion) ? extensionVersion! : MissingKey;

    /// <summary>The notice to show for this capture, or null (versions match, already shown this session, or muted). Calling it counts as showing.</summary>
    public ExtensionCheck? Evaluate(string? extensionVersion)
    {
        var check = ExtensionVersionCheck.Compare(extensionVersion, appVersion);
        if (check.Status == ExtensionStatus.Match) return null;
        string key = KeyOf(extensionVersion);
        lock (gate)
        {
            if (settings.MutedExtensionVersions.Contains(key)) return null;
            return shown.Add(key + "|" + check.AppVersion) ? check : null;
        }
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
