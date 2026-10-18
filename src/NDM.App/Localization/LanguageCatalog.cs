using System.Globalization;
using System.Reflection;

namespace NDM.App.Localization;

/// <summary>One language the app knows about. A language is only offered to the user when its JSON file is embedded (see <see cref="LanguageCatalog.Installed"/>).</summary>
/// <param name="Tag">File name and settings value: "en", "ar", "pt-BR", "zh-Hans".</param>
/// <param name="NativeName">Name in the language itself.</param>
/// <param name="EnglishName">Name in English, shown muted next to the native name.</param>
/// <param name="IsRtl">Right-to-left script: windows mirror.</param>
/// <param name="AiTranslated">The file was produced with AI help and has not been reviewed by a native speaker. The Options page shows a note.</param>
/// <param name="Fonts">Font fallback chain used for every window.</param>
/// <param name="Culture">Culture used for number and date formatting.</param>
/// <param name="Plural">Plural rule set, see <see cref="PluralRules"/>.</param>
public sealed record LanguageInfo(string Tag, string NativeName, string EnglishName, bool IsRtl, bool AiTranslated, string Fonts, string Culture, string Plural);

/// <summary>Registry of known languages, OS language mapping and the validated language setting.</summary>
public static class LanguageCatalog
{
    /// <summary>Prefix of the embedded locale resources (Locales.en.json).</summary>
    public const string ResourcePrefix = "Locales.";
    public const string System = "system";
    public const string English = "en";
    /// <summary>Layout-testing pseudo language. Only reachable through NEODM_PSEUDO=1, never listed.</summary>
    public const string Pseudo = "qps-ploc";
    private const string Latin = "Segoe UI";

    public static readonly IReadOnlyList<LanguageInfo> All = new LanguageInfo[]
    {
        new("en", "English", "English", false, false, Latin, "en-US", "en"),
        new("ar", "العربية", "Arabic", true, true, "Segoe UI, Tahoma, Arial", "ar-AE", "ar"),
        new("es", "Español", "Spanish", false, true, Latin, "es-ES", "es"),
        new("fr", "Français", "French", false, true, Latin, "fr-FR", "fr"),
        new("de", "Deutsch", "German", false, true, Latin, "de-DE", "de"),
        new("pt-BR", "Português (Brasil)", "Portuguese (Brazil)", false, true, Latin, "pt-BR", "pt"),
        new("ru", "Русский", "Russian", false, true, Latin, "ru-RU", "ru"),
        new("zh-Hans", "简体中文", "Chinese (Simplified)", false, true, "Microsoft YaHei UI, Segoe UI", "zh-CN", "zh"),
        new("ja", "日本語", "Japanese", false, true, "Yu Gothic UI, Segoe UI", "ja-JP", "ja"),
        new("tr", "Türkçe", "Turkish", false, true, Latin, "tr-TR", "tr"),
        new("hi", "हिन्दी", "Hindi", false, true, "Nirmala UI, Segoe UI", "hi-IN", "hi"),
        new("it", "Italiano", "Italian", false, true, Latin, "it-IT", "it"),
        new("ko", "한국어", "Korean", false, true, "Malgun Gothic, Segoe UI", "ko-KR", "ko"),
        new("id", "Bahasa Indonesia", "Indonesian", false, true, Latin, "id-ID", "id"),
        new("nl", "Nederlands", "Dutch", false, true, Latin, "nl-NL", "nl"),
        new("pl", "Polski", "Polish", false, true, Latin, "pl-PL", "pl"),
        new("fa", "فارسی", "Persian", true, true, "Segoe UI, Tahoma, Arial", "fa-IR", "fa"),
        new("he", "עברית", "Hebrew", true, true, Latin, "he-IL", "he"),
    };

    private static readonly LanguageInfo PseudoInfo = new(Pseudo, "Pseudo", "Pseudo-locale", false, false, Latin, "en-US", "en");

    public static LanguageInfo? Find(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        if (string.Equals(tag, Pseudo, StringComparison.OrdinalIgnoreCase)) return PseudoInfo;
        return All.FirstOrDefault(l => string.Equals(l.Tag, tag.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Tags whose JSON file is embedded in this build.</summary>
    public static Func<IReadOnlySet<string>> InstalledSource { get; set; } = EmbeddedTags;

    /// <summary>Languages offered in Options: catalog order, English first, only those with a locale file.</summary>
    public static IReadOnlyList<LanguageInfo> Installed()
    {
        var present = InstalledSource();
        return All.Where(l => present.Contains(l.Tag)).ToArray();
    }

    private static IReadOnlySet<string> EmbeddedTags()
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in typeof(LanguageCatalog).Assembly.GetManifestResourceNames())
            if (name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
                tags.Add(name[ResourcePrefix.Length..^5]);
        return tags;
    }

    /// <summary>The Windows display language, read once; tests replace it.</summary>
    public static Func<string> OsLanguageProvider { get; set; } = () => CultureInfo.CurrentUICulture.Name;

    /// <summary>Maps an OS culture name to a catalog tag: ar-* gives ar, pt-* gives pt-BR, simplified Chinese gives zh-Hans, others by primary subtag. Null when nothing matches.</summary>
    public static string? MapOsLanguage(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName)) return null;
        var parts = cultureName.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        string primary = parts[0].ToLowerInvariant();
        if (primary == "iw") primary = "he";
        if (primary == "in") primary = "id";
        if (primary == "pt") return "pt-BR";
        if (primary == "zh")
        {
            bool traditional = parts.Skip(1).Any(p => p.Equals("Hant", StringComparison.OrdinalIgnoreCase) || p.Equals("TW", StringComparison.OrdinalIgnoreCase)
                || p.Equals("HK", StringComparison.OrdinalIgnoreCase) || p.Equals("MO", StringComparison.OrdinalIgnoreCase));
            return traditional ? null : "zh-Hans";
        }
        return All.FirstOrDefault(l => l.Tag.Equals(primary, StringComparison.OrdinalIgnoreCase))?.Tag;
    }

    /// <summary>The installed language that matches the OS display language, English when there is none.</summary>
    public static LanguageInfo DetectOs()
    {
        string? mapped;
        try { mapped = MapOsLanguage(OsLanguageProvider()); } catch (Exception) { mapped = null; }
        var installed = Installed();
        return installed.FirstOrDefault(l => l.Tag == mapped) ?? installed.FirstOrDefault(l => l.Tag == English) ?? All[0];
    }

    /// <summary>Validates a stored setting: "system" or a catalog tag; anything else (unknown, empty, null) becomes "system".</summary>
    public static string NormalizeSetting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return System;
        if (value.Trim().Equals(System, StringComparison.OrdinalIgnoreCase)) return System;
        return All.FirstOrDefault(l => l.Tag.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase))?.Tag ?? System;
    }

    /// <summary>The language a setting resolves to right now. "system" and a tag without a locale file fall back to the OS match and then English.</summary>
    public static LanguageInfo Resolve(string? setting)
    {
        string normalized = NormalizeSetting(setting);
        if (normalized == System) return DetectOs();
        var installed = Installed();
        return installed.FirstOrDefault(l => l.Tag == normalized) ?? DetectOs();
    }
}
