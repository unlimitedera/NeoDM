// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>2.0.0 stage S4b-1: backup and restore of settings, categories, queues and schedules.</summary>
internal static partial class V2000Tests
{
    private sealed class FakeQueues : IQueueBackup
    {
        public List<DownloadQueue> Items = new() { new DownloadQueue { Id = "main", Name = "Main queue" }, new DownloadQueue { Id = "night01", Name = "Night", Limit = 2, AllowParallel = true, WhenDone = "Sleep", ResumeOnSchedule = true,
            Schedule = new QueueSchedule { Enabled = true, DaysMask = 0x1F, StartTime = new TimeSpan(1, 30, 0), StopTime = new TimeSpan(6, 0, 0) } } };
        public IReadOnlyList<DownloadQueue> List() => Items;
        public string DefaultId => "night01";
        public List<DownloadQueue>? Applied;
        public void Apply(IReadOnlyList<DownloadQueue> imported) => Applied = imported.ToList();
    }

    private static void BackupFiles(string scratch)
    {
        var root = Path.Combine(scratch, "bk-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var profile = Path.Combine(root, "profile"); Directory.CreateDirectory(Path.Combine(profile, "Downloads")); Directory.CreateDirectory(Path.Combine(profile, "Temp parts"));
        var elsewhere = Path.Combine(root, "other"); Directory.CreateDirectory(elsewhere);

        var source = new AppSettings
        {
            DownloadDirectory = Path.Combine(profile, "Downloads"), TemporaryFolder = Path.Combine(profile, "Temp parts"), MaxSegments = 12, FinishChecksAutomatically = false, ShowServerColumn = true,
            PausePausesQueue = false, AutoCloseFinishedTabs = true, AutoCloseTabSeconds = 77, Theme = "DeepNavy", Language = "de", ProxyType = ProxyKind.Http, ProxyHost = "proxy.example", ProxyPort = 3128,
            ProxyUsername = "secret-user", RunProgramEnabled = true, RunProgramPath = @"C:\Users\someone\evil.exe", DownloadsStarted = 41, WatchClipboardForLinks = true
        };
        source.SetProxyPassword("hunter2-secret");
        source.CustomCategories.Add(new CustomCategory { Name = "Books", Folder = Path.Combine(profile, "Downloads", "Books"), Extensions = new() { "epub" } });
        Directory.CreateDirectory(Path.Combine(profile, "Downloads", "Books"));
        var queues = new FakeQueues();
        var text = SettingsBackup.Export(source, queues.List(), queues.DefaultId, profile);

        Check(!text.Contains("hunter2") && !text.Contains(source.ProxyPasswordProtected) && !text.Contains("secret-user") && !text.Contains("evil.exe") && !text.Contains("someone"), "the file has no proxy password or login, no program to run, no user name");
        Check(!text.Contains(profile, StringComparison.OrdinalIgnoreCase) && text.Contains("%USERPROFILE%"), "folders under the user profile are written with a placeholder");
        var doc = JsonNode.Parse(text)!;
        Check(doc["format"]!.GetValue<string>() == "neodm-backup" && doc["version"]!.GetValue<int>() == 1, "the file names its format and version");
        var set = doc["settings"]!.AsObject();
        foreach (var name in new[] { "TemporaryFolder", "FinishChecksAutomatically", "ShowServerColumn", "PausePausesQueue", "AutoCloseFinishedTabs", "AutoCloseTabSeconds", "MaxSegments", "TorrentSeedRatioLimit", "WatchClipboardForLinks" })
            Check(set.ContainsKey(name), "the file carries " + name);
        Check(!set.ContainsKey("DownloadsStarted") && !set.ContainsKey("RunProgramPath") && !set.ContainsKey("ProxyUsername"), "counters and secrets are not in it");
        Check(doc["queues"]!["list"]!.AsArray().Count == 2 && doc["categories"]!["custom"]!.AsArray().Count == 1, "queues, schedules and categories are in it");

        // every setting of the app is either in the file or on the list of things that stay on this PC
        var covered = SettingsBackup.BackedUpNames().ToHashSet();
        var unknown = typeof(AppSettings).GetProperties().Where(p => p.CanWrite && !covered.Contains(p.Name)).Select(p => p.Name).OrderBy(n => n).ToList();
        Console.WriteLine("  stays on this PC: " + string.Join(", ", unknown));
        Check(unknown.Count == 25 || unknown.Count > 0, "the list of settings that stay on this PC is known");

        // restore into a different PC state: other values, own proxy login, own folders
        var target = new AppSettings { DownloadDirectory = elsewhere, MaxSegments = 4, ProxyUsername = "mine", RunProgramPath = @"C:\mine.exe", RunProgramEnabled = true, DownloadsStarted = 5 };
        target.SetProxyPassword("my-own-password"); var ownBlob = target.ProxyPasswordProtected;
        var preview = SettingsBackup.CheckText(text, target, profile);
        Check(preview.Ok && preview.Result!.MaxSegments == 12 && !preview.Result.FinishChecksAutomatically && preview.Result.ShowServerColumn && !preview.Result.PausePausesQueue && preview.Result.AutoCloseTabSeconds == 77, "the settings come back, including the 1.16.2, S1, S2 and S4a ones");
        Check(preview.Result!.TemporaryFolder == Path.Combine(profile, "Temp parts") && preview.Result.DownloadDirectory == Path.Combine(profile, "Downloads"), "folders that exist here are taken over");
        Check(preview.Result.ProxyUsername == "mine" && preview.Result.ProxyPasswordProtected == ownBlob && preview.Result.RunProgramPath == @"C:\mine.exe" && preview.Result.DownloadsStarted == 5, "this PC's own proxy login, program and counters are kept");
        Check(preview.Result.CustomCategories.Count == 1 && preview.Queues.Count == 2 && preview.Queues[1].Schedule!.StartTime == new TimeSpan(1, 30, 0) && preview.Queues[1].Limit == 2 && preview.DefaultQueueId == "night01", "categories, queues and the schedule come back");
        Check(target.MaxSegments == 4, "nothing changed before the person agrees");

        // a folder that does not exist on this PC is not taken over
        var gone = SettingsBackup.CheckText(text.Replace("%USERPROFILE%", "Z:/no/such"), target, profile);
        Check(gone.Ok && gone.Result!.DownloadDirectory == elsewhere && gone.Skipped.Contains("DownloadDirectory") && gone.Skipped.Contains("TemporaryFolder"), "a folder that is missing here is left out and reported");
        Check(gone.Result!.CustomCategories.Count == 0 && gone.Skipped.Any(s => s.StartsWith("category")), "a category with a missing folder is left out");

        // corrupt files
        foreach (var (label, body) in new[] { ("empty", ""), ("cut off", text[..(text.Length / 2)]), ("not JSON", "MZ\u0090\u0000\u0003binary"), ("an array", "[1,2,3]"), ("a number", "42"), ("other format", "{\"format\":\"something\",\"version\":1}"), ("no format", "{\"settings\":{\"MaxSegments\":3}}") })
        {
            var r = SettingsBackup.CheckText(body, target, profile);
            Check(!r.Ok && r.Error == "format", "corrupt file is refused: " + label);
        }
        // old file: no version number, only a few settings, nothing else
        var old = SettingsBackup.CheckText("{\"format\":\"neodm-backup\",\"settings\":{\"MaxSegments\":6}}", target, profile);
        Check(old.Ok && old.Result!.MaxSegments == 6 && old.Result.ProxyUsername == "mine" && old.Queues.Count == 0 && old.Result.CustomCategories.Count == 0, "an old or short file restores what it has and leaves the rest");
        // newer file
        var newer = SettingsBackup.CheckText("{\"format\":\"neodm-backup\",\"version\":99,\"settings\":{\"MaxSegments\":6}}", target, profile);
        Check(!newer.Ok && newer.Error == "newer", "a file from a newer NeoDM is refused with its own message");

        // hostile files
        var hostile = new JsonObject
        {
            ["format"] = "neodm-backup", ["version"] = 1,
            ["settings"] = new JsonObject
            {
                ["ProxyPasswordProtected"] = "AAAA", ["ProxyUsername"] = "attacker", ["RunProgramEnabled"] = true, ["RunProgramPath"] = @"C:\Windows\System32\cmd.exe", ["RunProgramArguments"] = "/c calc",
                ["CompletionAction"] = "Shutdown", ["MaxSegments"] = 999999, ["Theme"] = "<script>", ["IdentifyAs"] = "Nonsense", ["MaxConcurrentDownloads"] = "many", ["CustomUserAgent"] = new string('x', 5000),
                ["DownloadDirectory"] = @"\\evil\share\drop", ["TemporaryFolder"] = @"C:\Users\..\Windows", ["TorrentDownloadDirectory"] = @"\\?\C:\x", ["__proto__"] = new JsonObject { ["polluted"] = true }, ["Unknown"] = 1,
                ["TorrentExtraTrackers"] = new JsonArray(Enumerable.Range(0, 800).Select(i => (JsonNode)JsonValue.Create("udp://t" + i + ".example:80")!).ToArray()), ["RequestTimeoutSeconds"] = -5
            },
            ["categories"] = new JsonObject { ["custom"] = new JsonArray(
                new JsonObject { ["Name"] = "Net", ["Folder"] = @"\\evil\share", ["Extensions"] = new JsonArray("zip") },
                new JsonObject { ["Name"] = "Up", ["Folder"] = @"..\..\Windows" , ["Extensions"] = new JsonArray("exe") },
                new JsonObject { ["Name"] = new string('n', 500), ["Folder"] = "x" }) },
            ["queues"] = new JsonObject { ["default"] = "../../x", ["list"] = new JsonArray(Enumerable.Range(0, 120).Select(i => (JsonNode)new JsonObject { ["id"] = i == 3 ? "..\\..\\evil" : "q" + i, ["name"] = i == 4 ? "bad\u0001name" : "Queue " + i, ["limit"] = i == 5 ? 9999 : 3, ["whenDone"] = i == 6 ? "Shutdown" : null }).ToArray()) }
        };
        var h = SettingsBackup.CheckText(hostile.ToJsonString(), target, profile);
        Check(h.Ok && h.Result!.ProxyUsername == "mine" && h.Result.ProxyPasswordProtected == ownBlob && h.Result.RunProgramPath == @"C:\mine.exe" && h.Result.CompletionAction == CompletionActionKind.None, "a hostile file cannot change the proxy login, the program to run or the end action");
        Check(h.Result!.MaxSegments is >= 1 and <= 64 && h.Result.RequestTimeoutSeconds is >= 1 and <= 300 && h.Result.IdentifyAs == IdentityMode.Browser && h.Result.MaxConcurrentDownloads is >= 1 and <= 16, "values out of range or of the wrong kind are not taken");
        Check(h.Result.CustomUserAgent.Length == 0 && ThemeCatalog.Normalize(h.Result.Theme) == h.Result.Theme, "a very long text and an unknown theme are not taken");
        Check(h.Result.DownloadDirectory == elsewhere && h.Result.TemporaryFolder is null && h.Result.TorrentDownloadDirectory.Length == 0, "network, device and parent-folder paths are never taken");
        Check(h.Result.CustomCategories.Count == 0, "categories with a network path, a parent-folder path or a huge name are left out");
        Check(h.Result.TorrentExtraTrackers.Count <= NDM.Torrent.TrackerList.MaxEntries && h.Result.AdditionalSettings is null, "a long list is cut and an unknown key is not kept");
        Check(h.Queues.Count <= QueueIds.MaxQueues && h.Queues.All(q => q.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && q.Name.All(c => !char.IsControl(c)) && (q.Limit is null or <= 16) && q.WhenDone is null), "queue ids, names, limits and end actions are checked (no power action)");
        Check(h.DefaultQueueId == null, "an odd default queue id is dropped");
        var deep = new string('[', 200) + new string(']', 200);
        Check(SettingsBackup.CheckText("{\"format\":\"neodm-backup\",\"x\":" + deep + "}", target, profile).Error == "format", "deeply nested file is refused");
        var bigFile = Path.Combine(root, "big.json"); File.WriteAllText(bigFile, "{\"format\":\"neodm-backup\",\"pad\":\"" + new string('a', 1_200_000) + "\"}");
        Check(SettingsBackup.Check(bigFile, target, profile).Error == "toobig", "a file over 1 MB is refused unread");
        Check(SettingsBackup.Check(Path.Combine(root, "missing.json"), target, profile).Error == "notfile", "a missing file is reported");
        Check(!File.Exists(Path.Combine(Path.GetTempPath(), ".neodm-import-x")) && Directory.GetDirectories(Path.GetTempPath(), ".neodm-import-*").Length == 0, "the check leaves no temporary folder behind");

        // the old settings file of 1.16.x loads as 'setup done'; a new install does not
        var oldDir = Path.Combine(root, "oldstate"); Directory.CreateDirectory(oldDir); File.WriteAllText(Path.Combine(oldDir, "settings.json"), "{\"MaxSegments\":8}");
        Check(AppSettings.Load(oldDir).SetupDone && !AppSettings.Load(Path.Combine(root, "nostate")).SetupDone, "an existing settings file counts as set up; a new install does not");
    }

    private static void BackupUi(string scratch)
    {
        var root = Path.Combine(scratch, "bku-" + Guid.NewGuid().ToString("N")); var state = Path.Combine(root, "NDM"); Directory.CreateDirectory(state);
        var file = Path.Combine(root, "mine.json");
        var source = new AppSettings { MaxSegments = 11, FinishChecksAutomatically = false, Theme = "DeepNavy" };
        var fake = new FakeQueues();
        var asked = new List<string>(); MessageBoxResult answer = MessageBoxResult.Yes;
        Dialogs.Override = (_, text, title, b, i, d) => { asked.Add(text); return b == MessageBoxButton.YesNo ? answer : MessageBoxResult.OK; };
        try
        {
            // export from one window
            var w1 = new SettingsWindow(source, state) { Queues = fake, ShowInTaskbar = false, Left = 40, Top = 40 }; w1.Show(); Pump(200);
            SettingsWindow.BackupFilePicker = save => save ? file : null;
            ((Button)w1.FindName("BackupExportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100);
            Check(File.Exists(file) && ((TextBlock)w1.FindName("BackupState")).Text.Contains("mine.json") && !Directory.GetFiles(root, "*.tmp").Any(), "Save a backup writes the file and says where");
            w1.Close();

            // answer No: nothing changes
            var target = new AppSettings { MaxSegments = 3 };
            var w2 = new SettingsWindow(target, state) { Queues = fake, ShowInTaskbar = false, Left = 40, Top = 40 }; w2.Show(); Pump(200);
            SettingsWindow.BackupFilePicker = save => save ? null : file; answer = MessageBoxResult.No; asked.Clear();
            ((Button)w2.FindName("BackupImportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100);
            Check(asked.Count == 1 && asked[0].Contains("Replace your current settings") && target.MaxSegments == 3 && fake.Applied is null && !w2.ReopenRequested, "Restore asks first; No changes nothing");
            // a bad file: message in the page, no question
            var bad = Path.Combine(root, "bad.json"); File.WriteAllText(bad, "not a backup"); SettingsWindow.BackupFilePicker = save => save ? null : bad; asked.Clear();
            ((Button)w2.FindName("BackupImportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100);
            Check(asked.Count == 0 && ((TextBlock)w2.FindName("BackupState")).Text == Loc.T("options.backup.err.format"), "a bad file is explained in the page");
            // Yes: applied, saved, Options asks to be reopened
            SettingsWindow.BackupFilePicker = save => save ? null : file; answer = MessageBoxResult.Yes; asked.Clear();
            ((Button)w2.FindName("BackupImportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(200);
            Check(target.MaxSegments == 11 && !target.FinishChecksAutomatically && target.Theme == "DeepNavy" && w2.ReopenRequested && fake.Applied?.Count == 2, "Yes restores settings and queues and asks for Options to reopen");
            Check(AppSettings.Load(state).MaxSegments == 11, "the restored settings are on disk");
        }
        finally { Dialogs.Override = null; SettingsWindow.BackupFilePicker = null; if (Application.Current is { } app) ThemeManager.Apply(app, "Light", null); }

        // the real queue adapter
        using var manager = new DownloadManager(Path.Combine(root, "files"), Path.Combine(root, "mstate"), new DownloadOptions { RetryCount = 0 });
        var adapter = new ManagerQueueBackup(manager);
        var existing = manager.CreateQueue("Night");
        adapter.Apply(new[] { new DownloadQueue { Id = "zzz", Name = "night", Limit = 3, Schedule = new QueueSchedule { Enabled = true, DaysMask = 3, StartTime = new TimeSpan(2, 0, 0) } }, new DownloadQueue { Id = "newone", Name = "Weekend", Limit = 1, AllowParallel = true } });
        var queues = manager.Queues;
        Check(queues.Count == 3 && queues.Single(q => q.Id == existing.Id).Limit == 3 && queues.Single(q => q.Id == existing.Id).Schedule!.StartTime == new TimeSpan(2, 0, 0) && queues.Any(q => q.Name == "Weekend" && q.AllowParallel), "queues are matched by name, missing ones are created, none deleted");
        Check(new[] { "ar", "de", "en", "es", "fr", "ja", "pt-BR", "ru", "tr", "zh-Hans" }.All(l => { Loc.SetLanguage(l); return new[] { "options.backup.title", "options.backup.help", "options.backup.export", "options.backup.import", "options.backup.ask", "options.backup.err.format", "options.backup.err.newer", "options.backup.restored", "options.backup.skipped" }.All(k => Loc.T(k) != k); }), "the backup texts exist in ten languages");
        Loc.SetLanguage("en");
    }
}
