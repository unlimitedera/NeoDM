using System.IO;
using NDM.Core;

namespace NDM.App;

/// <summary>1.9.0: a category the user made. Name, target folder (relative to the download folder, or a full path), file extensions, optional address rules and an on/off switch.</summary>
public sealed class CustomCategory
{
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    /// <summary>Extensions without a dot, lower case ("zip", "tar.gz").</summary>
    public List<string> Extensions { get; set; } = new();
    /// <summary>Host names and simple wildcard patterns; see <see cref="UrlRuleMatcher"/>.</summary>
    public List<string> UrlRules { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public CustomCategory Clone() => new() { Name = Name, Folder = Folder, Extensions = Extensions.ToList(), UrlRules = UrlRules.ToList(), Enabled = Enabled };
}

/// <summary>1.9.0: what the user changed on one of the five built-in categories besides its folder (which stays in the old per-category settings). Null extensions = the built-in list.</summary>
public sealed class BuiltInCategorySetting
{
    public List<string>? Extensions { get; set; }
    public List<string> UrlRules { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public BuiltInCategorySetting Clone() => new() { Extensions = Extensions?.ToList(), UrlRules = UrlRules.ToList(), Enabled = Enabled };
}

/// <summary>Host and wildcard matching for category address rules. No regular expressions (a pattern cannot cause runaway matching).
/// A pattern without '/' is a host pattern: a plain host such as "example.com" matches that host and its subdomains; a pattern with '*' or '?' must match the whole host, and "*.example.com" also matches "example.com". Host patterns are tried against the address host and the referrer host.
/// A pattern with '/' matches "host/path?query" of the address (the scheme in the pattern is ignored). '*' is any text, '?' is one character. Case is ignored.</summary>
public static class UrlRuleMatcher
{
    public const int MaxPatternLength = 200;
    public static bool IsPathPattern(string pattern) => pattern.Contains('/');

    public static bool Matches(string pattern, string? url, string? referer)
    {
        pattern = Normalize(pattern);
        if (pattern.Length == 0 || pattern.Length > MaxPatternLength) return false;
        Uri.TryCreate(url, UriKind.Absolute, out var target);
        Uri.TryCreate(referer, UriKind.Absolute, out var from);
        if (IsPathPattern(pattern))
            return target is not null && Glob(pattern, target.IdnHost + target.PathAndQuery);
        return HostMatches(pattern, target?.IdnHost) || HostMatches(pattern, from?.IdnHost);
    }

    public static string Normalize(string pattern)
    {
        var p = (pattern ?? "").Trim();
        foreach (var scheme in new[] { "https://", "http://" }) if (p.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) { p = p[scheme.Length..]; break; }
        return p;
    }

    private static bool HostMatches(string pattern, string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        host = host.TrimEnd('.');
        pattern = pattern.TrimEnd('.');
        if (pattern.IndexOfAny(new[] { '*', '?' }) < 0)
            return host.Equals(pattern, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase);
        if (pattern.StartsWith("*.", StringComparison.Ordinal) && pattern.IndexOfAny(new[] { '*', '?' }, 2) < 0 && host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase)) return true;
        return Glob(pattern, host);
    }

    /// <summary>Linear-time wildcard match ('*' any run, '?' one character), case-insensitive.</summary>
    public static bool Glob(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>Null when the pattern is acceptable, else a locale key explaining why not.</summary>
    public static string? Validate(string pattern)
    {
        var p = Normalize(pattern);
        if (p.Length == 0) return "cat.err.rule_empty";
        if (p.Length > MaxPatternLength) return "cat.err.rule_long";
        if (p.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '<' or '>' or '"' or '\\' or '|')) return "cat.err.rule_chars";
        return null;
    }
}

/// <summary>Safe folder handling for category folders: no path traversal, no device names, no reparse points, no odd prefixes.</summary>
public static class CategoryPaths
{
    /// <summary>Resolves a category folder against the download folder. Returns the full path, or null with a locale key in <paramref name="error"/>.
    /// An empty folder is allowed only when <paramref name="allowEmpty"/> is set and means the download folder itself.</summary>
    public static string? Resolve(string downloadDirectory, string? folder, bool allowEmpty, out string? error)
    {
        error = null;
        folder = (folder ?? "").Trim();
        if (folder.Length == 0)
        {
            if (allowEmpty) return downloadDirectory;
            error = "cat.err.folder_empty"; return null;
        }
        if (folder.Length > 240) { error = "cat.err.folder_long"; return null; }
        if (folder.StartsWith(@"\\?\") || folder.StartsWith(@"\\.\") || folder.StartsWith("//?/") || folder.StartsWith("//./")) { error = "cat.err.folder_device"; return null; }
        if (folder.Any(c => c < 32 || c is '<' or '>' or '"' or '|' or '?' or '*') || ReservedNames.ContainsInvisible(folder)) { error = "cat.err.folder_chars"; return null; }
        bool absolute = Path.IsPathFullyQualified(folder);
        if (!absolute && (Path.IsPathRooted(folder) || folder.Contains(':'))) { error = "cat.err.folder_rooted"; return null; }
        var rest = folder;
        string full;
        if (absolute)
        {
            var root = Path.GetPathRoot(folder) ?? "";
            rest = folder[root.Length..];
            if (rest.Trim('\\', '/').Length == 0) { error = "cat.err.folder_root"; return null; }
            if (folder.IndexOf(':', root.Length) >= 0) { error = "cat.err.folder_chars"; return null; }
        }
        foreach (var segment in rest.Split('\\', '/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") { error = "cat.err.folder_traversal"; return null; }
            if (segment != segment.TrimEnd(' ', '.') ) { error = "cat.err.folder_chars"; return null; }
            if (ReservedNames.IsReserved(segment)) { error = "cat.err.folder_reserved"; return null; }
        }
        try
        {
            full = Path.GetFullPath(absolute ? folder : Path.Combine(downloadDirectory, folder));
            if (full.Length > Path.GetPathRoot(full)!.Length) full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { error = "cat.err.folder_chars"; return null; }
        if (!absolute)
        {
            var baseFull = Path.GetFullPath(downloadDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { error = "cat.err.folder_traversal"; return null; }
        }
        if (HasReparsePoint(full, absolute ? null : downloadDirectory)) { error = "cat.err.folder_link"; return null; }
        return full;
    }

    /// <summary>True when an existing folder on the way to <paramref name="full"/> is a link (symbolic link or junction). With a base, only folders below it are looked at.</summary>
    public static bool HasReparsePoint(string full, string? below)
    {
        try
        {
            var stop = below is null ? Path.GetPathRoot(full)!.TrimEnd(Path.DirectorySeparatorChar) : Path.GetFullPath(below).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (var current = full; !string.IsNullOrEmpty(current) && !current.TrimEnd(Path.DirectorySeparatorChar).Equals(stop, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
            {
                var info = new DirectoryInfo(current);
                if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        return false;
    }
}

/// <summary>What the rules decided: the folder, and which category (or null for the download folder) made the decision.</summary>
public sealed record CategoryDecision(string Folder, string? Category, bool IsCustom, string Reason);

/// <summary>1.9.0 folder rules. Order: custom categories in list order, first by address rule and then by extension; then the built-in categories (address rule, then extension); then the download folder.
/// An explicit folder chosen by the user never comes here: it is passed as TargetDirectory and wins.</summary>
public static class CategoryRules
{
    public const int MaxCustom = 100, MaxExtensions = 200, MaxRules = 50, MaxName = 60;
    public static readonly string[] BuiltInIds = { "Programs", "Compressed", "Music", "Video", "Documents" };

    public static IReadOnlyList<string> DefaultExtensions(string id) => id switch
    {
        "Programs" => new[] { "exe", "msi", "dmg", "pkg", "deb", "rpm" },
        "Compressed" => new[] { "zip", "rar", "7z", "tar", "gz", "bz2", "xz" },
        "Music" => new[] { "mp3", "flac", "wav", "aac", "m4a", "ogg", "wma" },
        "Video" => new[] { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v" },
        "Documents" => new[] { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt" },
        _ => Array.Empty<string>()
    };

    public static string BuiltInFolder(AppSettings s, string id) => id switch
    {
        "Programs" => s.ProgramsFolder, "Compressed" => s.CompressedFolder, "Music" => s.MusicFolder, "Video" => s.VideoFolder, "Documents" => s.DocumentsFolder, _ => ""
    };
    public static void SetBuiltInFolder(AppSettings s, string id, string folder)
    {
        switch (id) { case "Programs": s.ProgramsFolder = folder; break; case "Compressed": s.CompressedFolder = folder; break; case "Music": s.MusicFolder = folder; break; case "Video": s.VideoFolder = folder; break; case "Documents": s.DocumentsFolder = folder; break; }
    }

    public static IReadOnlyList<string> BuiltInExtensions(AppSettings s, string id) => s.BuiltInCategories.TryGetValue(id, out var o) && o.Extensions is { } list ? list : DefaultExtensions(id);
    public static bool BuiltInEnabled(AppSettings s, string id) => !s.BuiltInCategories.TryGetValue(id, out var o) || o.Enabled;
    public static IReadOnlyList<string> BuiltInRules(AppSettings s, string id) => s.BuiltInCategories.TryGetValue(id, out var o) ? o.UrlRules : Array.Empty<string>();

    /// <summary>Normalises a typed extension list: split on comma, semicolon, space; strip dots and '*'; lower case; valid characters only; no duplicates.</summary>
    public static List<string> ParseExtensions(string? text)
    {
        var result = new List<string>();
        foreach (var raw in (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().TrimStart('*').TrimStart('.').TrimEnd('.').ToLowerInvariant();
            if (IsValidExtension(token) && !result.Contains(token)) result.Add(token);
            if (result.Count >= MaxExtensions) break;
        }
        return result;
    }
    public static bool IsValidExtension(string token) => token.Length is > 0 and <= 24 && token.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '+' or '-' or '.') && !token.StartsWith('.') && !token.EndsWith('.') && !token.Contains("..");
    public static string FormatExtensions(IEnumerable<string> list) => string.Join(", ", list);
    public static List<string> ParseRules(string? text) =>
        (text ?? "").Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRules).ToList();

    /// <summary>Cleans loaded or edited categories: bounded counts and lengths, valid extensions, no duplicate names. Folder safety is checked when a rule is used and when options are saved.</summary>
    public static void Normalize(AppSettings s)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<CustomCategory>();
        foreach (var c in s.CustomCategories ?? new())
        {
            if (c is null) continue;
            var name = (c.Name ?? "").Trim();
            if (name.Length == 0 || name.Length > MaxName || name.Any(char.IsControl) || BuiltInIds.Contains(name, StringComparer.OrdinalIgnoreCase) || !seen.Add(name)) continue;
            list.Add(new CustomCategory
            {
                Name = name, Folder = (c.Folder ?? "").Trim(), Enabled = c.Enabled,
                Extensions = (c.Extensions ?? new()).Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Where(IsValidExtension).Distinct().Take(MaxExtensions).ToList(),
                UrlRules = (c.UrlRules ?? new()).Select(r => r?.Trim() ?? "").Where(r => r.Length > 0 && UrlRuleMatcher.Validate(r) is null).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRules).ToList()
            });
            if (list.Count >= MaxCustom) break;
        }
        s.CustomCategories = list;
        var builtIn = new Dictionary<string, BuiltInCategorySetting>(StringComparer.Ordinal);
        foreach (var id in BuiltInIds)
        {
            if (s.BuiltInCategories is null || !s.BuiltInCategories.TryGetValue(id, out var o) || o is null) continue;
            var clean = new BuiltInCategorySetting
            {
                Enabled = o.Enabled,
                Extensions = o.Extensions?.Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Where(IsValidExtension).Distinct().Take(MaxExtensions).ToList(),
                UrlRules = (o.UrlRules ?? new()).Select(r => r?.Trim() ?? "").Where(r => r.Length > 0 && UrlRuleMatcher.Validate(r) is null).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRules).ToList()
            };
            if (clean.Enabled && clean.Extensions is null && clean.UrlRules.Count == 0) continue; // all defaults: nothing to store
            builtIn[id] = clean;
        }
        s.BuiltInCategories = builtIn;
    }

    private static bool ExtensionMatches(IEnumerable<string> extensions, string fileName)
    {
        foreach (var e in extensions)
            if (fileName.EndsWith("." + e, StringComparison.OrdinalIgnoreCase) && fileName.Length > e.Length + 1) return true;
        return false;
    }
    private static bool RuleMatches(IEnumerable<string> rules, string? url, string? referer) => rules.Any(r => UrlRuleMatcher.Matches(r, url, referer));

    /// <summary>Folder for a file. Unsafe folders (hand-edited settings) are skipped, never used.</summary>
    public static CategoryDecision Resolve(AppSettings s, string fileName, string? url = null, string? referer = null)
    {
        var baseDir = s.DownloadDirectory;
        if (!s.EnableCategoryFolders) return new(baseDir, null, false, "off");
        fileName ??= "";
        bool Usable(string folder, bool allowEmpty, out string path) { path = CategoryPaths.Resolve(baseDir, folder, allowEmpty, out _) ?? ""; return path.Length > 0; }
        // 1. custom categories: address rules first, then extensions, each in list order
        foreach (var c in s.CustomCategories)
            if (c.Enabled && c.UrlRules.Count > 0 && RuleMatches(c.UrlRules, url, referer) && Usable(c.Folder, false, out var p)) return new(p, c.Name, true, "url");
        foreach (var c in s.CustomCategories)
            if (c.Enabled && c.Extensions.Count > 0 && ExtensionMatches(c.Extensions, fileName) && Usable(c.Folder, false, out var p)) return new(p, c.Name, true, "extension");
        // 2. built-in categories
        foreach (var id in BuiltInIds)
            if (BuiltInEnabled(s, id) && BuiltInRules(s, id).Count > 0 && RuleMatches(BuiltInRules(s, id), url, referer) && Usable(BuiltInFolder(s, id), true, out var p)) return new(p, id, false, "url");
        foreach (var id in BuiltInIds)
            if (BuiltInEnabled(s, id) && ExtensionMatches(BuiltInExtensions(s, id), fileName) && Usable(BuiltInFolder(s, id), true, out var p)) return new(p, id, false, "extension");
        return new(baseDir, null, false, "none");
    }

    /// <summary>Checks the whole category setup for the options window. Returns (locale key, argument) of the first problem, or null.</summary>
    public static (string Key, string? Arg)? Validate(AppSettings s)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in BuiltInIds)
        {
            if (CategoryPaths.Resolve(s.DownloadDirectory, BuiltInFolder(s, id), true, out var err) is null) return (err!, id);
            foreach (var r in BuiltInRules(s, id)) if (UrlRuleMatcher.Validate(r) is { } rule) return (rule, id);
        }
        foreach (var c in s.CustomCategories)
        {
            var name = (c.Name ?? "").Trim();
            if (name.Length == 0) return ("cat.err.name_empty", null);
            if (name.Length > MaxName || name.Any(char.IsControl)) return ("cat.err.name_long", name);
            if (BuiltInIds.Contains(name, StringComparer.OrdinalIgnoreCase) || !names.Add(name)) return ("cat.err.name_taken", name);
            if (CategoryPaths.Resolve(s.DownloadDirectory, c.Folder, false, out var err) is null) return (err!, name);
            foreach (var r in c.UrlRules) if (UrlRuleMatcher.Validate(r) is { } rule) return (rule, name);
            if (c.Extensions.Count == 0 && c.UrlRules.Count == 0) return ("cat.err.no_rule", name);
        }
        return null;
    }

    /// <summary>Extension presets offered in the editor: (locale key of the name, extensions).</summary>
    public static readonly (string Key, string[] Extensions)[] Presets =
    {
        ("cat.preset.documents", new[] { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "odt", "ods", "csv" }),
        ("cat.preset.archives", new[] { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "tar.gz", "tgz" }),
        ("cat.preset.audio", new[] { "mp3", "flac", "wav", "aac", "m4a", "ogg", "opus", "wma" }),
        ("cat.preset.video", new[] { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v" }),
        ("cat.preset.images", new[] { "jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "tif", "tiff", "heic" }),
        ("cat.preset.programs", new[] { "exe", "msi", "msix", "appx", "dmg", "pkg", "deb", "rpm", "apk" }),
        ("cat.preset.books", new[] { "epub", "mobi", "azw3", "djvu", "cbz", "cbr" }),
    };
}
