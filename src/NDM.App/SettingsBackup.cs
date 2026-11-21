// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NDM.Core;

namespace NDM.App;

/// <summary>2.0.0, Options > General > Backup and restore: settings, categories, queues and schedules in one versioned file.
/// Never exported and never imported: the proxy login, the program to run at the end, update and extension notices, counters, and anything from this machine that the file could change blindly.
/// Folders are written with the user profile replaced by %USERPROFILE% and used on import only when the folder exists on this PC.</summary>
public static class SettingsBackup
{
    public const string Format = "neodm-backup";
    public const int CurrentVersion = 1;
    public const long MaxFileBytes = 1024 * 1024;
    private const string Profile = "%USERPROFILE%";

    /// <summary>Settings that stay as they are on this PC: secrets, things that run programs, notices and counters.</summary>
    private static readonly HashSet<string> Never = new(StringComparer.Ordinal)
    {
        nameof(AppSettings.ProxyPasswordProtected), nameof(AppSettings.ProxyUsername), nameof(AppSettings.RunProgramEnabled), nameof(AppSettings.RunProgramPath), nameof(AppSettings.RunProgramArguments),
        nameof(AppSettings.UpdateToastVersion), nameof(AppSettings.LastUpdateCheckUtc), nameof(AppSettings.SkippedUpdateVersion), nameof(AppSettings.MutedExtensionVersions),
        nameof(AppSettings.ExtensionNoticeTimes), nameof(AppSettings.BrowserExtensions), nameof(AppSettings.RecentPackageNames), nameof(AppSettings.SettingsSchema), nameof(AppSettings.AdditionalSettings),
        nameof(AppSettings.DownloadsStarted), nameof(AppSettings.TabsOfferAt), nameof(AppSettings.SetupDone), nameof(AppSettings.SessionCompletionAction),
        nameof(AppSettings.CustomCategories), nameof(AppSettings.BuiltInCategories), nameof(AppSettings.CompletionAction), nameof(AppSettings.CompletionRemember)
    };
    private static readonly string[] FolderSettings = { nameof(AppSettings.DownloadDirectory), nameof(AppSettings.TemporaryFolder), nameof(AppSettings.TorrentDownloadDirectory) };
    private static readonly JsonSerializerOptions Names = new() { Converters = { new JsonStringEnumConverter() }, WriteIndented = true };

    public static IEnumerable<string> BackedUpNames() =>
        typeof(AppSettings).GetProperties().Where(p => p.CanWrite && p.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).Length == 0 && !Never.Contains(p.Name)).Select(p => p.Name);

    // ---------- export ----------
    public static string Export(AppSettings settings, IEnumerable<DownloadQueue>? queues, string? defaultQueueId, string? profileFolder = null)
    {
        profileFolder ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = new JsonObject { ["format"] = Format, ["version"] = CurrentVersion, ["app"] = AboutInfo.Version, ["created"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") };
        var set = new JsonObject();
        foreach (var name in BackedUpNames())
        {
            var property = typeof(AppSettings).GetProperty(name)!;
            var node = JsonSerializer.SerializeToNode(property.GetValue(settings), property.PropertyType, Names);
            if (FolderSettings.Contains(name) && node is JsonValue v && v.TryGetValue<string>(out var text)) node = Tokenize(text, profileFolder);
            set[name] = node;
        }
        root["settings"] = set;
        var cats = new JsonObject
        {
            ["custom"] = new JsonArray(settings.CustomCategories.Select(c => { var copy = c.Clone(); copy.Folder = Tokenize(copy.Folder, profileFolder); return JsonSerializer.SerializeToNode(copy, Names); }).ToArray()),
            ["builtIn"] = JsonSerializer.SerializeToNode(settings.BuiltInCategories, Names)
        };
        root["categories"] = cats;
        if (queues is not null)
        {
            var list = new JsonArray();
            foreach (var q in queues)
                list.Add(new JsonObject
                {
                    ["id"] = q.Id, ["name"] = q.Name, ["limit"] = q.Limit, ["allowParallel"] = q.AllowParallel, ["resumeOnSchedule"] = q.ResumeOnSchedule, ["whenDone"] = q.WhenDone,
                    ["schedule"] = q.Schedule is null ? null : JsonSerializer.SerializeToNode(q.Schedule, Names)
                });
            root["queues"] = new JsonObject { ["default"] = defaultQueueId, ["list"] = list };
        }
        return root.ToJsonString(Names);
    }

    private static string Tokenize(string path, string profile) =>
        !string.IsNullOrEmpty(path) && profile.Length > 3 && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase) && (path.Length == profile.Length || path[profile.Length] is '\\' or '/') ? Profile + path[profile.Length..] : path;
    private static string Expand(string path, string profile) =>
        path.StartsWith(Profile, StringComparison.OrdinalIgnoreCase) && (path.Length == Profile.Length || path[Profile.Length] is '\\' or '/') ? profile + path[Profile.Length..] : path;

    // ---------- import (check only; nothing is changed until Apply) ----------
    public sealed class Preview
    {
        public bool Ok => Error is null;
        public string? Error { get; init; }
        public int FileVersion { get; init; }
        public AppSettings? Result { get; init; }
        public List<string> Changed { get; } = new();
        public List<string> Skipped { get; } = new();
        public List<DownloadQueue> Queues { get; } = new();
        public string? DefaultQueueId { get; set; }
        public int CustomCategories => Result?.CustomCategories.Count ?? 0;
        public string? AppVersion { get; init; }
    }
    /// <summary>Error codes are stable text ("notfile", "toobig", "format", "newer", "empty"); the window turns them into the user's language.</summary>
    public static Preview Check(string path, AppSettings current, string? profileFolder = null)
    {
        string text;
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) return Fail("toobig");
            text = FileLimits.ReadAllTextCapped(path, MaxFileBytes, "backup");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return Fail("notfile"); }
        return CheckText(text, current, profileFolder);
    }
    private static Preview Fail(string code) => new() { Error = code };

    public static Preview CheckText(string text, AppSettings current, string? profileFolder = null)
    {
        profileFolder ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 24, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return Fail("format"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var f) || f.ValueKind != JsonValueKind.String || f.GetString() != Format) return Fail("format");
            int version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
            if (version > CurrentVersion) return new Preview { Error = "newer", FileVersion = version };
            if (version < 1) version = 1; // a file without a number is read as the first format
            string? app = root.TryGetProperty("app", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() is { Length: <= 24 } av ? av : null;

            var candidate = current.Copy();
            var changed = new List<string>(); var skipped = new List<string>();
            var properties = typeof(AppSettings).GetProperties().ToDictionary(p => p.Name);
            var allowed = BackedUpNames().ToHashSet();
            if (root.TryGetProperty("settings", out var set) && set.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in set.EnumerateObject())
                {
                    if (!allowed.Contains(field.Name)) { if (properties.ContainsKey(field.Name) || field.Name.Length < 64) skipped.Add(Safe(field.Name)); continue; }
                    var property = properties[field.Name];
                    var element = field.Value;
                    if (element.ValueKind == JsonValueKind.String && (element.GetString()?.Length ?? 0) > 4096) { skipped.Add(field.Name); continue; }
                    if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 500) { skipped.Add(field.Name); continue; }
                    try
                    {
                        object? value = element.Deserialize(property.PropertyType, Names);
                        if (FolderSettings.Contains(field.Name))
                        {
                            var folder = value as string ?? "";
                            if (folder.Length == 0) { if (field.Name != nameof(AppSettings.DownloadDirectory)) property.SetValue(candidate, field.Name == nameof(AppSettings.TemporaryFolder) ? null : ""); continue; }
                            folder = Expand(folder.Trim(), profileFolder);
                            if (!UsableFolder(folder)) { skipped.Add(field.Name); continue; }
                            value = folder;
                        }
                        var before = JsonSerializer.Serialize(property.GetValue(candidate), Names);
                        property.SetValue(candidate, value);
                        if (before != JsonSerializer.Serialize(property.GetValue(candidate), Names)) changed.Add(field.Name);
                    }
                    catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NotSupportedException or FormatException) { skipped.Add(field.Name); }
                }
            }
            if (root.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Object)
            {
                if (cats.TryGetProperty("custom", out var custom) && custom.ValueKind == JsonValueKind.Array && custom.GetArrayLength() <= 200)
                {
                    var list = new List<CustomCategory>();
                    foreach (var item in custom.EnumerateArray())
                    {
                        try
                        {
                            var c = item.Deserialize<CustomCategory>(Names);
                            if (c is null || c.Name is not { Length: > 0 and <= 60 } || (c.Folder?.Length ?? 0) > 260) { skipped.Add("category"); continue; }
                            c.Folder = Expand((c.Folder ?? "").Trim(), profileFolder);
                            if (Path.IsPathFullyQualified(c.Folder) ? !UsableFolder(c.Folder) : (c.Folder.Contains("..") || c.Folder.StartsWith("\\") || c.Folder.StartsWith("/") || c.Folder.Contains(':') || c.Folder.Any(char.IsControl))) { skipped.Add("category " + Safe(c.Name)); continue; }
                            c.Extensions = (c.Extensions ?? new()).Take(200).Where(x => x is { Length: > 0 and <= 20 }).ToList();
                            c.UrlRules = (c.UrlRules ?? new()).Take(200).Where(x => x is { Length: > 0 and <= 200 }).ToList();
                            list.Add(c);
                        }
                        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException) { skipped.Add("category"); }
                    }
                    candidate.CustomCategories = list; changed.Add("categories");
                }
                if (cats.TryGetProperty("builtIn", out var built) && built.ValueKind == JsonValueKind.Object && built.EnumerateObject().Count() <= 20)
                {
                    try { var d = built.Deserialize<Dictionary<string, BuiltInCategorySetting>>(Names); if (d is not null) { candidate.BuiltInCategories = d; if (!changed.Contains("categories")) changed.Add("categories"); } }
                    catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException) { skipped.Add("builtIn"); }
                }
            }
            // Validate and clamp everything the way a normal start does: write the candidate where only this check reads it.
            AppSettings result;
            var scratch = Path.Combine(Path.GetTempPath(), ".neodm-import-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(scratch); candidate.SettingsSchema = AppSettings.CurrentSchema; candidate.Save(scratch);
                result = AppSettings.Load(scratch);
            }
            finally { try { Directory.Delete(scratch, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            // Load is for files on disk: keep what this PC owns even if it normalised something.
            result.ProxyPasswordProtected = current.ProxyPasswordProtected; result.ProxyUsername = current.ProxyUsername;
            result.SessionCompletionAction = current.SessionCompletionAction;
            result.CompletionAction = current.CompletionAction; result.CompletionRemember = current.CompletionRemember;
            result.RunProgramEnabled = current.RunProgramEnabled; result.RunProgramPath = current.RunProgramPath; result.RunProgramArguments = current.RunProgramArguments;
            result.AdditionalSettings = current.AdditionalSettings;
            var p2 = new Preview { FileVersion = version, Result = result, AppVersion = app };
            p2.Changed.AddRange(changed.Distinct()); p2.Skipped.AddRange(skipped.Distinct().Take(20));
            if (root.TryGetProperty("queues", out var q) && q.ValueKind == JsonValueKind.Object) ReadQueues(q, p2);
            return p2;
        }
    }

    private static string Safe(string s) => new string(s.Where(c => !char.IsControl(c)).Take(60).ToArray());

    /// <summary>A folder is used only when it is a plain local folder that exists on this PC (no network path, no device path, no '..').</summary>
    public static bool UsableFolder(string folder)
    {
        try
        {
            if (folder.Length is 0 or > 260 || folder.StartsWith(@"\\") || folder.StartsWith("//") || folder.Contains("..") || folder.Any(char.IsControl) || folder.Skip(2).Contains(':')) return false;
            return Path.IsPathFullyQualified(folder) && Directory.Exists(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static void ReadQueues(JsonElement q, Preview into)
    {
        if (q.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 and <= 40 } did && did.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) into.DefaultQueueId = did;
        if (!q.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return;
        foreach (var item in list.EnumerateArray())
        {
            if (into.Queues.Count >= QueueIds.MaxQueues) { into.Skipped.Add("queues"); break; }
            if (item.ValueKind != JsonValueKind.Object) continue;
            try
            {
                string id = item.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
                string name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                if (!QueueIds.ValidName(name) || id.Length is 0 or > 40 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) { into.Skipped.Add("queue"); continue; }
                int? limit = item.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var li) && li is >= 1 and <= 16 ? li : null;
                var queue = new DownloadQueue
                {
                    Id = id, Name = name.Trim(), Limit = limit,
                    AllowParallel = item.TryGetProperty("allowParallel", out var ap) && ap.ValueKind == JsonValueKind.True,
                    ResumeOnSchedule = item.TryGetProperty("resumeOnSchedule", out var rs) && rs.ValueKind == JsonValueKind.True
                };
                if (item.TryGetProperty("whenDone", out var wd) && wd.ValueKind == JsonValueKind.String && Enum.TryParse<CompletionActionKind>(wd.GetString(), false, out var kind) && Enum.IsDefined(kind)
                    && kind != CompletionActionKind.None && !CompletionActionInfo.IsPower(kind)) queue.WhenDone = kind.ToString();
                else if (wd.ValueKind == JsonValueKind.String) into.Skipped.Add("queue end action");
                if (item.TryGetProperty("schedule", out var s) && s.ValueKind == JsonValueKind.Object)
                {
                    var schedule = s.Deserialize<QueueSchedule>(Names);
                    if (schedule is not null && schedule.Problem() is null) queue.Schedule = schedule; else into.Skipped.Add("schedule of " + Safe(name));
                }
                into.Queues.Add(queue);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException or FormatException) { into.Skipped.Add("queue"); }
        }
    }

    // ---------- apply ----------
    public static void Apply(Preview preview, AppSettings live, string stateDirectory, IQueueBackup? queues)
    {
        if (!preview.Ok || preview.Result is null) throw new InvalidOperationException();
        preview.Result.Save(stateDirectory);   // on disk first: a failure here changes nothing in memory
        live.CopyFrom(preview.Result);
        if (queues is not null && preview.Queues.Count > 0) queues.Apply(preview.Queues);
    }
}

/// <summary>What backup and restore needs from the engine's queues (the real app passes the download manager; tests pass a fake).</summary>
public interface IQueueBackup
{
    IReadOnlyList<DownloadQueue> List();
    string DefaultId { get; }
    void Apply(IReadOnlyList<DownloadQueue> imported);
}

public sealed class ManagerQueueBackup : IQueueBackup
{
    private readonly DownloadManager _manager;
    public ManagerQueueBackup(DownloadManager manager) => _manager = manager;
    public IReadOnlyList<DownloadQueue> List() => _manager.Queues;
    public string DefaultId => _manager.DefaultQueueId;
    /// <summary>Queues of the file update the queue with the same id (or the same name) and are created when missing; nothing is deleted and no download moves.</summary>
    public void Apply(IReadOnlyList<DownloadQueue> imported)
    {
        foreach (var item in imported)
        {
            var existing = _manager.Queues.FirstOrDefault(x => x.Id == item.Id) ?? _manager.Queues.FirstOrDefault(x => string.Equals(x.Name, item.Name, StringComparison.CurrentCultureIgnoreCase));
            try
            {
                string id = existing?.Id ?? _manager.CreateQueue(item.Name).Id;
                _manager.UpdateQueue(id, q => { if (existing is null) q.Name = item.Name; q.Limit = item.Limit; q.AllowParallel = item.AllowParallel; q.Schedule = item.Schedule?.Clone(); q.WhenDone = item.WhenDone; q.ResumeOnSchedule = item.ResumeOnSchedule; });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { /* a queue that cannot be created (limit of 50, name clash) is left out */ }
        }
    }
}
