using System.IO;
using Microsoft.Win32;
namespace NDM.App;

/// <summary>One selectable theme. <see cref="Id"/> is what settings.json stores; <see cref="IsDark"/> drives the window chrome.</summary>
public sealed record ThemeInfo(string Id, string Name, bool IsDark, string Description, bool Branded);

/// <summary>Registry of known themes, id validation and the System (follow Windows) resolution.</summary>
public static class ThemeCatalog
{
    public const string SystemId = "System";
    public const string Default = "Light";
    public static readonly IReadOnlyList<ThemeInfo> All = new[]
    {
        new ThemeInfo("Light", "Light", false, "Clean light theme", false),
        new ThemeInfo("Dark", "Dark", true, "Original dark theme", false),
        new ThemeInfo(SystemId, "System", false, "Follows the Windows light/dark setting", false),
        new ThemeInfo("Midnight", "Midnight", true, "Flat charcoal with a soft violet accent", true),
        new ThemeInfo("Graphite", "Graphite", true, "Neutral grey with a teal accent", true),
        new ThemeInfo("Ocean", "Ocean", true, "Deep blue with a cyan accent", true),
        new ThemeInfo("Forest", "Forest", true, "Dark green with a mint accent", true),
        new ThemeInfo("Rose", "Rose", true, "Dark plum with a pink accent", true),
        new ThemeInfo("Paper", "Paper", false, "Warm sepia light with a brown accent", true),
        new ThemeInfo("DeepNavy", "Deep Navy", true, "Deep navy with a blue accent and mint highlights", true),
    };
    /// <summary>Themes that have their own dictionary file (everything except System).</summary>
    public static IEnumerable<ThemeInfo> Concrete => All.Where(t => t.Id != SystemId);

    /// <summary>Reads whether Windows apps use the dark theme: true = dark, false = light, null = unknown.</summary>
    public static Func<bool?> OsDarkProvider { get; set; } = ReadOsDark;
    private static bool? ReadOsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light ? light == 0 : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public static bool IsKnown(string? id) => id is not null && All.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    /// <summary>Canonical id of a stored value; unknown, empty or legacy-damaged values fall back to Light.</summary>
    public static string Normalize(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id ?? Default;
    /// <summary>The concrete theme (a dictionary file exists) for an id; System follows <paramref name="osDark"/> (default: the registry).</summary>
    public static string Resolve(string? id, Func<bool?>? osDark = null)
    {
        string normal = Normalize(id);
        if (normal != SystemId) return normal;
        return (osDark ?? OsDarkProvider)() == true ? "Dark" : "Light";
    }
    public static bool IsDark(string? id, Func<bool?>? osDark = null) => All.First(t => t.Id == Resolve(id, osDark)).IsDark;

    /// <summary>Accepts #RRGGBB (the # is optional when typing); returns the canonical upper-case form.</summary>
    public static bool TryParseAccent(string? text, out string normalized)
    {
        normalized = "";
        string s = (text ?? "").Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6 || !s.All(Uri.IsHexDigit)) return false;
        normalized = "#" + s.ToUpperInvariant();
        return true;
    }
    public static string? NormalizeAccent(string? text) => TryParseAccent(text, out var n) ? n : null;
}
