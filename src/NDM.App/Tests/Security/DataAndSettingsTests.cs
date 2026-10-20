using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static partial class SecurityTests
{
    private static void DataAndSettingsTests(SecurityApp app, string root)
    {
        // ---- 7: setting, schema, migration
        Check(AppSettings.CurrentSchema == 5 && new AppSettings().MarkDownloadedFiles && new AppSettings().SettingsSchema == 5, "Mark of the Web setting: on by default, schema 5");
        string dir = Path.Combine(root, "motw-settings"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"SettingsSchema\":4,\"MarkDownloadedFiles\":false,\"Theme\":\"Dark\"}");
        var migrated = AppSettings.Load(dir);
        Check(migrated.MarkDownloadedFiles && migrated.SettingsSchema == 5 && migrated.Theme == "Dark", "Settings schema 4 -> 5: the option is turned on once, other values kept");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Theme\":\"Dark\"}");
        Check(AppSettings.Load(dir).MarkDownloadedFiles, "A settings file without the key loads with the option on");
        migrated.MarkDownloadedFiles = false; migrated.Save(dir);
        var kept = AppSettings.Load(dir); Check(!kept.MarkDownloadedFiles && kept.SettingsSchema == 5, "After the migration an explicit 'off' is respected");
        Check(!OptionsHelper.CreateDownloadOptions(kept).MarkOfTheWeb && OptionsHelper.CreateDownloadOptions(new AppSettings()).MarkOfTheWeb, "The setting reaches the download engine's options");
        Check(!TorrentController.ToOptions(kept).MarkOfTheWeb && TorrentController.ToOptions(new AppSettings()).MarkOfTheWeb, "The setting reaches the torrent engine's options");
        var copy = new AppSettings(); copy.CopyFrom(kept); Check(!copy.MarkDownloadedFiles && !kept.Copy().MarkDownloadedFiles, "CopyFrom and Copy carry the option");
        var window = new SettingsWindow(new AppSettings { DownloadDirectory = Path.Combine(root, "motw-files") }, dir); window.Show(); Pump(80);
        var box = (CheckBox)window.FindName("MarkFiles");
        Check(box.IsChecked == true && box.Content.ToString() == "Mark downloaded files as coming from the internet", "Options > Downloads: the checkbox exists, is ticked by default and has the text from the specification");
        Check(((TabItem)((TabControl)window.FindName("OptionsTabs")).Items[3]).Content is ScrollViewer sv && sv.Content is DependencyObject content && Logical<CheckBox>(content).Contains(box), "The checkbox sits on the Downloads tab");
        box.IsChecked = false; ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(80);
        Check(!AppSettings.Load(dir).MarkDownloadedFiles, "Unticking the checkbox and saving stores the choice");
        window.Close();

        // ---- 11: settings.json cap
        string big = Path.Combine(root, "big-settings"); Directory.CreateDirectory(big);
        File.WriteAllText(Path.Combine(big, "settings.json"), "{\"Theme\":\"Dark\",\"Pad\":\"" + new string('x', (int)FileLimits.MaxSettingsBytes) + "\"}");
        long bytes = new FileInfo(Path.Combine(big, "settings.json")).Length;
        var fromBig = AppSettings.Load(big);
        var moved = Directory.GetFiles(big, "settings.oversized-*.json");
        Check(fromBig.Theme == "Light" && AppSettings.LoadProblem is { } problem && problem.Contains("settings.json is larger than") && moved.Length == 1 && new FileInfo(moved[0]).Length == bytes, "Settings over 1 MB: defaults are used, the big file is kept next to it (" + moved.Length + " moved, " + AppSettings.LoadProblem + ")");
        Check(!File.Exists(Path.Combine(big, "settings.json")), "Settings over 1 MB: the original name is free, so a later save cannot overwrite the kept file");
        AppSettings.Load(dir); Check(AppSettings.LoadProblem is null, "LoadProblem is cleared by a normal load");

        // ---- 13: explorer /select
        Check(ExplorerArguments.Select(@"C:\Users\me\My Files\a b,c.zip") == "/select,\"C:\\Users\\me\\My Files\\a b,c.zip\"", "Explorer: /select,\"path\" is one switch with the path in quotes");
        Check(ExplorerArguments.Select("C:\\x\"y\\z.bin") == "/select,\"C:\\xy\\z.bin\"", "Explorer: a quote character can never break out of the argument");
        var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { Arguments = ExplorerArguments.Select(@"C:\a b\c.txt") };
        Check(psi.Arguments == "/select,\"C:\\a b\\c.txt\"" && psi.ArgumentList.Count == 0, "Explorer: built through Arguments, not ArgumentList (which would escape the quotes)");
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "NDM.App"), "*.cs").Where(f => File.ReadAllText(f).Contains("ArgumentList.Add(\"/select")).ToList();
        Check(offenders.Count == 0, "No source file builds /select through ArgumentList (" + string.Join(",", offenders.Select(Path.GetFileName)) + ")");
        var other = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "NDM.App"), "*.cs").Where(f => File.ReadAllText(f).Contains("explorer.exe")).Select(Path.GetFileName).OrderBy(x => x).ToList();
        Check(other.SequenceEqual(new[] { "DownloadCompleteWindow.xaml.cs", "ExplorerArguments.cs", "ExtensionGuide.cs" }.OrderBy(x => x)) && File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", "ExtensionGuide.cs")).Contains("StartProgram(\"explorer.exe\", \"\\\"\" + path + \"\\\"\")"), "Explorer: the other call sites pass a quoted path as one argument string (" + string.Join(",", other) + ")");

        // ---- 12: BinaryFormatter off; clipboard and data objects still work
        string[] candidates = { Path.Combine(RepoRoot, "src", "NDM.App", "bin", "Release", "net8.0-windows", "NeoDM.runtimeconfig.json"), Path.Combine(RepoRoot, "dist", "app", "NeoDM.runtimeconfig.json") };
        var configPath = candidates.First(File.Exists);
        using (var cfg = JsonDocument.Parse(File.ReadAllText(configPath)))
        {
            var props = cfg.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");
            Check(props.TryGetProperty("System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", out var flag) && flag.ValueKind == JsonValueKind.False, "NeoDM.runtimeconfig.json: EnableUnsafeBinaryFormatterSerialization is false (" + configPath + ")");
        }
        Check(AppContext.TryGetSwitch("System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", out var enabled) && !enabled, "This test process runs with the same switch off");
        Check(!File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", "NDM.App.csproj")).Contains("EnableUnsafeBinaryFormatterSerialization>true") && File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", "NDM.App.csproj")).Contains("<EnableUnsafeBinaryFormatterSerialization>false</EnableUnsafeBinaryFormatterSerialization>"), "NDM.App.csproj sets it to false");
        string url = "https://example.org/files/a%20b.zip?token=1";
        Retry(() => Clipboard.SetText(url)); Check(Retry(() => Clipboard.GetText()) == url, "Clipboard: copying a URL still works with BinaryFormatter off");
        Retry(() => Clipboard.SetText("Tab\tand ünïcode 日本語")); Check(Retry(() => Clipboard.GetText()) == "Tab\tand ünïcode 日本語", "Clipboard: Unicode text round trip");
        string file = Path.Combine(root, "dropped.txt"); File.WriteAllText(file, "x");
        var list = new StringCollection { file };
        Retry(() => Clipboard.SetFileDropList(list)); Check(Retry(() => Clipboard.GetFileDropList()).Cast<string>().SequenceEqual(new[] { file }), "Clipboard: a file list (what a drag-drop of a file carries) round trips");
        var data = new DataObject(); data.SetText(url); data.SetData(DataFormats.FileDrop, new[] { file }); data.SetData(DataFormats.UnicodeText, url);
        Check(data.GetDataPresent(DataFormats.UnicodeText) && data.ContainsText(), "Drag-and-drop data object: text is present");
        Check((string)data.GetData(DataFormats.UnicodeText) == url, "Drag-and-drop data object: the link text reads back");
        Check(data.GetDataPresent(DataFormats.FileDrop) && ((string[])data.GetData(DataFormats.FileDrop))[0] == file, "Drag-and-drop data object: the dropped file reads back");
        Check(data.ContainsFileDropList() && data.GetFileDropList().Count == 1, "Drag-and-drop data object: the file list reads back");
        Retry(() => Clipboard.SetDataObject(data, true)); Check(Retry(() => Clipboard.GetDataObject())!.GetDataPresent(DataFormats.FileDrop) && Retry(() => Clipboard.GetText()) == url, "Data object through the clipboard: link and file survive");

        // ---- 9: torrent dialog shows where the file came from
        string src = Path.Combine(root, "tor-src"); Directory.CreateDirectory(src); File.WriteAllBytes(Path.Combine(src, "f.bin"), new byte[40000]);
        var creator = new MonoTorrent.TorrentCreator(); var bytesTorrent = Task.Run(async () => (await creator.CreateAsync(new MonoTorrent.TorrentFileSource(src))).Encode()).GetAwaiter().GetResult();
        string torrentPath = @"C:\Users\me\Downloads\sample.torrent";
        var add = new TorrentAddWindow(new AppSettings { DownloadDirectory = Path.Combine(root, "tor-dl") }, null, bytesTorrent, null, null, torrentPath) { ShowInTaskbar = false }; add.Show(); Pump(150);
        Check(((TextBlock)add.FindName("SourceText")).Text == torrentPath, "Add torrent window: shows the path of the .torrent file it came from");
        add.Close();
        Check(Loc.T("error.disk_full_detail").Length > 0 && ErrorMessages.Describe("Not enough free disk space on C: this download needs about 5.0 GB and 1 MB is free.").Contains("Not enough free disk space on C:"), "Error text for the disk-space failure is shown with its English detail");
    }

    private static T Retry<T>(Func<T> action) { for (int i = 0; ; i++) { try { return action(); } catch (System.Runtime.InteropServices.COMException) when (i < 60) { Thread.Sleep(100); } } }
    private static void Retry(Action action) => Retry(() => { action(); return 0; });
    private static IEnumerable<T> Logical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Logical<T>(child)) yield return nested;
        }
    }
}
