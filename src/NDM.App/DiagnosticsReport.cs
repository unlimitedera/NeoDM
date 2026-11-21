// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NDM.Core;

namespace NDM.App;

/// <summary>2.0.0, Help > Report a problem: a diagnostics zip the person can read before sharing. Everything that could identify the person or a download is removed:
/// links, cookies, proxy login, the user name, folders, file names in paths, e-mail and address-like tokens. Nothing is sent by NeoDM.</summary>
public static class Scrubber
{
    private static readonly Regex Url = new(@"(?i)\b(?:https?|ftps?|wss?|file|blob|data|chrome-extension|moz-extension)://[^\s""'<>\]\)]*|\bmagnet:\?[^\s""'<>]*", RegexOptions.Compiled);
    private static readonly Regex Header = new(@"(?im)\b(set-cookie|cookie|authorization|proxy-authorization|x-api-key)\b\s*[:=]\s*[^\r\n]*", RegexOptions.Compiled);
    private static readonly Regex Secret = new(@"(?i)\b(password|passwd|pwd|token|secret|apikey|api_key|signature|sessionid|session|sid|sig|key)\b(\s*[:=]\s*)(""[^""]*""|[^\s,;&""]+)", RegexOptions.Compiled);
    private static readonly Regex Login = new(@"[^\s:@/\\""']+:[^\s@/\\""']*@[A-Za-z0-9_.\-\[\]:]+", RegexOptions.Compiled);
    private static readonly Regex DrivePath = new(@"[A-Za-z]:[\\/][^\r\n""'<>|*?:]*", RegexOptions.Compiled);
    private static readonly Regex UncPath = new(@"\\\\[^\s\\/:*?""<>|]+\\[^\r\n""'<>|*?:]*", RegexOptions.Compiled);
    private static readonly Regex HomePath = new(@"(?i)(?:/Users|/home)/[^\s/]+(?:/[^\r\n""'<>|*?:]*)?|%(?:USERPROFILE|APPDATA|LOCALAPPDATA|HOMEPATH|TEMP|TMP)%[^\r\n""'<>|*?:]*", RegexOptions.Compiled);
    private static readonly Regex Domain = new(@"\b(?:[a-z0-9\-]+\.)+(?:com|net|org|io|dev|app|co|me|info|xyz|to|tv|ru|de|fr|es|jp|cn|tr|br|sa|eg|ae|cc|ws|nz|uk|us|eu|ly|gg|su|cloud|download|link|site|online|store|top|pro|live)\b(?:[:/][^\s""'<>]*)?", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex LongToken = new(@"\b[A-Za-z0-9+/_\-=]{32,}\b", RegexOptions.Compiled);
    private static readonly Regex Ipv4 = new(@"\b(?!127\.0\.0\.1\b)\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.Compiled);
    private static readonly Regex Ipv6 = new(@"\b(?:[0-9A-Fa-f]{1,4}:){4,7}[0-9A-Fa-f]{1,4}\b", RegexOptions.Compiled);

    /// <summary>Names that identify this person or PC and are replaced wherever they appear (user name, machine name, profile folder name).</summary>
    public static IReadOnlyList<string> OwnNames(IEnumerable<string>? extra = null)
    {
        var names = new List<string>();
        void Add(string? n) { if (!string.IsNullOrWhiteSpace(n) && n.Trim().Length >= 3 && !names.Contains(n.Trim(), StringComparer.OrdinalIgnoreCase)) names.Add(n.Trim()); }
        try { Add(Environment.UserName); Add(Environment.MachineName); Add(Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/'))); Add(Environment.UserDomainName); } catch (Exception) { }
        foreach (var e in extra ?? Array.Empty<string>()) Add(e);
        return names.OrderByDescending(n => n.Length).ToList();
    }

    public static string Scrub(string? text, IEnumerable<string>? ownNames = null)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;
        s = Header.Replace(s, m => m.Groups[1].Value + ": <removed>");
        s = Url.Replace(s, "<link>");
        s = Login.Replace(s, "<login>");
        s = Secret.Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + "<removed>");
        s = UncPath.Replace(s, "<path>");
        s = DrivePath.Replace(s, "<path>");
        s = HomePath.Replace(s, "<path>");
        s = Email.Replace(s, "<email>");
        s = Domain.Replace(s, "<link>");
        s = Ipv6.Replace(s, "<ip>");
        s = Ipv4.Replace(s, "<ip>");
        s = LongToken.Replace(s, "<token>");
        foreach (var name in ownNames ?? OwnNames())
            s = Regex.Replace(s, Regex.Escape(name), "<name>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return s;
    }
}

public sealed record ReportInput(AppSettings Settings, string StateDirectory, string Desktop, string AppVersion, DateTime Now, IReadOnlyList<string>? ExtraOwnNames = null, bool AfterCrash = false);
public sealed record ReportResult(string ZipPath, string Summary, string IssueUrl);

public static class DiagnosticsReport
{
    public const string IssueBase = "https://github.com/unlimitedera/NeoDM/issues/new";
    public const int TailLines = 200, TailBytes = 48 * 1024, MaxEntryChars = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Converters = { new JsonStringEnumConverter() } };

    /// <summary>Settings that are never shown, with what stands in their place.</summary>
    private static readonly Dictionary<string, string> Replaced = new()
    {
        ["ProxyPasswordProtected"] = "<removed>", ["ProxyUsername"] = "<removed>", ["ProxyHost"] = "<removed>", ["RunProgramPath"] = "<removed>", ["RunProgramArguments"] = "<removed>",
        ["DownloadDirectory"] = "<folder>", ["TemporaryFolder"] = "<folder>", ["TorrentDownloadDirectory"] = "<folder>", ["CustomUserAgent"] = "<text>"
    };
    private static readonly HashSet<string> Left = new() { "AdditionalSettings", "RecentPackageNames", "ExtensionNoticeTimes", "CustomCategories", "TorrentExtraTrackers", "BuiltInCategories" };

    public static ReportResult Build(ReportInput input)
    {
        var own = Scrubber.OwnNames(input.ExtraOwnNames);
        string S(string? t) => Scrubber.Scrub(t, own);
        var summary = BuildSummary(input, S);
        var entries = new List<(string Name, string Text)>
        {
            ("README.txt", "This file was made by NeoDM on your PC. Nothing was sent anywhere.\r\nIt holds: the version and Windows build, your settings without secrets, links, cookies, folders or your name, the last lines of NeoDM's own logs, a count of downloads by state, and a crash note if NeoDM closed unexpectedly.\r\nIt holds no download links, no cookies, no proxy login and no file paths. Open the zip and read it before you share it.\r\n"),
            ("summary.txt", summary),
            ("settings.json", S(SettingsText(input.Settings))),
            ("queues.txt", S(QueueSummary(input.StateDirectory)))
        };
        foreach (var (name, path) in LogFiles(input.StateDirectory)) entries.Add(("logs/" + name, S(Tail(path))));
        foreach (var path in CrashLog.Files(input.StateDirectory).Take(3)) entries.Add(("crash/" + Path.GetFileName(path), S(Tail(path))));

        Directory.CreateDirectory(input.Desktop);
        var stamp = input.Now.ToString("yyyyMMdd-HHmmss");
        var zip = Path.Combine(input.Desktop, "NeoDM-report-" + stamp + ".zip");
        for (int i = 2; File.Exists(zip); i++) zip = Path.Combine(input.Desktop, "NeoDM-report-" + stamp + "-" + i + ".zip");
        var temp = zip + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
                foreach (var (name, text) in entries)
                {
                    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    writer.Write(text.Length > MaxEntryChars ? text[^MaxEntryChars..] : text);
                }
            File.Move(temp, zip);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new ReportResult(zip, summary, IssueUrl(input.AppVersion, summary, input.AfterCrash));
    }

    public static string WindowsText()
    {
        try { return RuntimeInformation.OSDescription + " (build " + Environment.OSVersion.Version.Build + ", " + (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit") + ")"; }
        catch (Exception) { return "unknown"; }
    }

    public static string BuildSummary(ReportInput i, Func<string?, string> scrub)
    {
        var sb = new StringBuilder();
        sb.AppendLine("NeoDM " + i.AppVersion + (i.AfterCrash ? " (after an unexpected close)" : ""));
        sb.AppendLine("Windows: " + WindowsText());
        sb.AppendLine(".NET: " + RuntimeInformation.FrameworkDescription);
        sb.AppendLine("Language: " + i.Settings.Language + ", theme: " + i.Settings.Theme + ", UI culture: " + System.Globalization.CultureInfo.CurrentUICulture.Name);
        sb.AppendLine("Browser extensions seen: " + (i.Settings.BrowserExtensions.Count == 0 ? "none" : string.Join(", ", i.Settings.BrowserExtensions.Select(b => b.Browser + " " + b.Version))));
        sb.AppendLine("Proxy: " + i.Settings.ProxyType + (i.Settings.ProxyType == ProxyKind.None ? "" : " (address and login not included)"));
        sb.AppendLine("Report made: " + i.Now.ToString("yyyy-MM-dd HH:mm"));
        sb.AppendLine();
        sb.Append(QueueSummary(i.StateDirectory));
        return scrub(sb.ToString());
    }

    public static string SettingsText(AppSettings settings)
    {
        var node = new JsonObject();
        foreach (var p in typeof(AppSettings).GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).Length == 0))
        {
            if (Left.Contains(p.Name)) { var v = p.GetValue(settings); node[p.Name] = v is System.Collections.ICollection c ? "<" + c.Count + " items>" : "<left out>"; continue; }
            if (Replaced.TryGetValue(p.Name, out var stand)) { var v = p.GetValue(settings); node[p.Name] = v is null || (v is string t && t.Length == 0) ? "<empty>" : stand; continue; }
            node[p.Name] = JsonSerializer.SerializeToNode(p.GetValue(settings), p.PropertyType, Indented);
        }
        return node.ToJsonString(Indented);
    }

    /// <summary>Counts only: downloads by state and queue, schedules, torrents. No names, no links.</summary>
    public static string QueueSummary(string state)
    {
        var sb = new StringBuilder("Downloads (counts only)\r\n");
        try
        {
            var queue = Path.Combine(state, "queue.json");
            if (File.Exists(queue) && new FileInfo(queue).Length <= FileLimits.MaxQueueBytes)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(queue), new JsonDocumentOptions { MaxDepth = 64 });
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var items = doc.RootElement.EnumerateArray().ToList();
                    sb.AppendLine("  total: " + items.Count);
                    foreach (var g in items.Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("Status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "?" : "?").GroupBy(x => x).OrderBy(g => g.Key))
                        sb.AppendLine("  " + new string(g.Key.Where(char.IsLetter).Take(20).ToArray()) + ": " + g.Count());
                }
            }
            else sb.AppendLine("  none saved");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { sb.AppendLine("  the saved list could not be read"); }
        try
        {
            var queues = Path.Combine(state, "queues.json");
            if (File.Exists(queues) && new FileInfo(queues).Length <= FileLimits.MaxSettingsBytes)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(queues), new JsonDocumentOptions { MaxDepth = 32 });
                if (doc.RootElement.TryGetProperty("Queues", out var list) && list.ValueKind == JsonValueKind.Array)
                    sb.AppendLine("Queues: " + list.GetArrayLength() + ", with a schedule: " + list.EnumerateArray().Count(q => q.TryGetProperty("Schedule", out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("Enabled", out var en) && en.ValueKind == JsonValueKind.True) + ", held: " + list.EnumerateArray().Count(q => q.TryGetProperty("Hold", out var h) && h.ValueKind == JsonValueKind.True));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        try { var t = Path.Combine(state, "torrent"); if (Directory.Exists(t)) sb.AppendLine("Torrent files kept: " + Directory.EnumerateFiles(t, "*", SearchOption.TopDirectoryOnly).Count()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return sb.ToString();
    }

    private static IEnumerable<(string Name, string Path)> LogFiles(string state)
    {
        var list = new List<(string, string)>();
        void Add(string name, string path) { if (File.Exists(path)) list.Add((name, path)); }
        Add("bridge-watchdog.txt", Path.Combine(state, "bridge-watchdog.log"));
        Add("thumbnails-trace.txt", Path.Combine(state, "thumbs", "trace.log"));
        Add("cleanup-pending.txt", Path.Combine(state, "cleanup-pending.txt"));
        try
        {
            var torrent = Path.Combine(state, "torrent");
            if (Directory.Exists(torrent))
                foreach (var f in Directory.EnumerateFiles(torrent).Where(f => Regex.IsMatch(Path.GetFileName(f), @"(?i)\.(log|txt)$")).Take(3)) list.Add(("torrent-" + SafeName(Path.GetFileName(f)), f));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }
    private static string SafeName(string n) => new string(n.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').Take(40).ToArray());

    /// <summary>The last lines of a text file (at most <see cref="TailLines"/> lines and <see cref="TailBytes"/> bytes); a missing or locked file gives an empty text.</summary>
    public static string Tail(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, fs.Length - TailBytes); fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, true);
            var lines = reader.ReadToEnd().Split('\n');
            if (start > 0 && lines.Length > 1) lines = lines.Skip(1).ToArray();
            return string.Join("\r\n", lines.TakeLast(TailLines).Select(l => l.TrimEnd('\r')));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    public static string IssueUrl(string version, string summary, bool afterCrash)
    {
        var title = afterCrash ? "NeoDM " + version + " closed unexpectedly" : "Problem with NeoDM " + version;
        var env = summary.Length > 1400 ? summary[..1400] : summary;
        return IssueBase + "?template=bug.yml&title=" + Uri.EscapeDataString(title) + "&environment=" + Uri.EscapeDataString(env);
    }
}
