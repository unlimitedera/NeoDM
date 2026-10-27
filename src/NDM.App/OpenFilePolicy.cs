using System.Globalization;
using System.IO;
using System.Text;

namespace NDM.App;

/// <summary>The kinds of finished downloads the user may allow to open without the confirmation window. The names are what settings.json stores (OpenWithoutAsking).</summary>
public enum OpenKind { Video, Music, Pictures, Documents, Archives }

/// <summary>
/// Decides which confirmation a finished download needs before it is opened (1.5.9). Only the kinds below can ever be remembered, and only by their LAST file extension;
/// everything else (programs, scripts, shortcuts, Office files that can hold macros, web pages, unknown or missing extensions, odd names) always asks and has no checkbox.
/// The rule lives here, in code: settings.json can list kinds, but a name that is not in the table below can never match one.
/// </summary>
public static class OpenFilePolicy
{
    private static readonly Dictionary<string, OpenKind> Table = Build();

    private static Dictionary<string, OpenKind> Build()
    {
        var map = new Dictionary<string, OpenKind>(StringComparer.OrdinalIgnoreCase);
        void Add(OpenKind kind, string list) { foreach (var e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map[e] = kind; }
        Add(OpenKind.Video, "mp4 mkv webm avi mov m4v wmv flv mpg mpeg 3gp ts");
        Add(OpenKind.Music, "mp3 m4a aac flac wav ogg opus wma");
        Add(OpenKind.Pictures, "jpg jpeg png gif webp bmp tif tiff avif heic svg");
        Add(OpenKind.Documents, "pdf txt rtf csv epub md");
        Add(OpenKind.Archives, "zip rar 7z tar gz tgz bz2 xz iso");
        return map;
    }

    /// <summary>Every kind, in the order the Options group lists them.</summary>
    public static IReadOnlyList<OpenKind> All { get; } = new[] { OpenKind.Video, OpenKind.Music, OpenKind.Pictures, OpenKind.Documents, OpenKind.Archives };

    /// <summary>The extensions that map to a kind (lower case, no dot); used by the tests and the documents.</summary>
    public static IReadOnlyCollection<string> Extensions(OpenKind kind) => Table.Where(p => p.Value == kind).Select(p => p.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The kind of a file name, or null when the file is risky or unknown (always ask, no checkbox). The last extension decides, so video.mp4.exe is a program.
    /// Names with control or invisible formatting characters (zero-width characters, bidirectional overrides), path or stream separators (: \ /), no extension,
    /// or an extension that is not plain ASCII letters and digits are never classified. Trailing dots and spaces are dropped first, as Windows does.
    /// </summary>
    public static OpenKind? Classify(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255) return null;
        foreach (var c in name)
        {
            if (c is ':' or '\\' or '/' or '*' or '?' or '"' or '<' or '>' or '|') return null;
            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse) return null;
            if (c is '\u2028' or '\u2029' or '\u00A0') return null;
        }
        var trimmed = name.TrimEnd('.', ' ');
        int dot = trimmed.LastIndexOf('.');
        if (dot <= 0 || dot == trimmed.Length - 1) return null; // no extension (a leading dot only, like ".mp4", is a hidden file without one)
        var extension = trimmed[(dot + 1)..];
        if (extension.Length > 8 || !extension.All(c => c < 128 && char.IsAsciiLetterOrDigit(c))) return null;
        return Table.TryGetValue(extension, out var kind) ? kind : null;
    }

    /// <summary>The kind for a download: both the recorded file name and the name of the file that will really be opened must agree, otherwise null.</summary>
    public static OpenKind? KindFor(string? fileName, string? filePath = null)
    {
        var kind = Classify(fileName);
        if (kind is null || filePath is null) return kind;
        string onDisk;
        try { onDisk = Path.GetFileName(filePath); } catch (ArgumentException) { return null; }
        return Classify(onDisk) == kind ? kind : null;
    }

    /// <summary>The kinds listed in the settings; unknown names (and any name for a risky kind) are ignored.</summary>
    public static IReadOnlySet<OpenKind> Remembered(AppSettings settings)
    {
        var set = new HashSet<OpenKind>();
        foreach (var text in settings.OpenWithoutAsking ?? new())
            if (TryParse(text, out var kind)) set.Add(kind);
        return set;
    }

    public static bool TryParse(string? text, out OpenKind kind)
    {
        kind = default;
        if (text is null) return false;
        foreach (var candidate in All)
            if (string.Equals(text.Trim(), candidate.ToString(), StringComparison.OrdinalIgnoreCase)) { kind = candidate; return true; }
        return false;
    }

    /// <summary>True when the confirmation can be skipped for this download: a safe kind that the user allowed.</summary>
    public static bool CanSkipPrompt(AppSettings settings, string? fileName, string? filePath = null) =>
        KindFor(fileName, filePath) is { } kind && Remembered(settings).Contains(kind);

    public static bool IsRemembered(AppSettings settings, OpenKind kind) => Remembered(settings).Contains(kind);

    /// <summary>Adds or removes a kind in the settings list (canonical names, no duplicates, no unknown entries).</summary>
    public static void SetRemembered(AppSettings settings, OpenKind kind, bool remembered)
    {
        var set = new HashSet<OpenKind>(Remembered(settings));
        if (remembered) set.Add(kind); else set.Remove(kind);
        settings.OpenWithoutAsking = All.Where(set.Contains).Select(k => k.ToString()).ToList();
    }

    /// <summary>Canonical, de-duplicated list for a loaded settings file.</summary>
    public static List<string> Normalize(IEnumerable<string>? names)
    {
        var set = new HashSet<OpenKind>();
        foreach (var text in names ?? Array.Empty<string>()) if (TryParse(text, out var kind)) set.Add(kind);
        return All.Where(set.Contains).Select(k => k.ToString()).ToList();
    }
}
