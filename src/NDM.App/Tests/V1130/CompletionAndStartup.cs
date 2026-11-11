using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static partial class V1130Tests
{
    // ------------------------------------------------------------------ item 1: Open / Open folder close the completion window unless Options keeps it open
    private static void CompletionWindow(string root)
    {
        var d = new AppSettings(); Check(!d.KeepCompletionWindowOpen && AppSettings.CurrentSchema == 11, "the keep-open option defaults to off (schema 11)");
        var state = Path.Combine(root, "c-state"); Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "settings.json"), "{\"SettingsSchema\":10,\"Theme\":\"Dark\"}");
        var old = AppSettings.Load(state); Check(!old.KeepCompletionWindowOpen && old.StartupWindow == StartupWindowMode.Tray && old.Theme == "Dark" && old.SettingsSchema == 11, "a schema 10 file loads with the option off and the other values kept");
        old.KeepCompletionWindowOpen = true; old.Save(state); Check(AppSettings.Load(state).KeepCompletionWindowOpen, "an explicit 'keep open' survives the next load");
        var copy = new AppSettings(); copy.CopyFrom(old); Check(copy.KeepCompletionWindowOpen, "CopyFrom carries the option");
        Check(DownloadCompleteWindow.ClosesAfterOpen(new AppSettings()) && !DownloadCompleteWindow.ClosesAfterOpen(new AppSettings { KeepCompletionWindowOpen = true }), "mode table: off closes, on keeps");

        var priorPrompt = DownloadFileActions.Prompt; var priorLauncher = DownloadFileActions.Launcher; var priorFolder = DownloadFileActions.FolderLauncher;
        var launched = new List<string>(); var folders = new List<string>(); bool fail = false;
        DownloadFileActions.Prompt = (_, _, _) => new OpenConfirmResult(true, false);
        DownloadFileActions.Launcher = p => { if (fail) throw new IOException("no"); launched.Add(p); };
        DownloadFileActions.FolderLauncher = p => { if (fail) throw new IOException("no"); folders.Add(p); };
        var manager = new DownloadManager(Path.Combine(root, "c-files"), Path.Combine(root, "c-mstate"), new DownloadOptions());
        try
        {
            var job = Sync(manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/x.txt"), DateTimeOffset.UtcNow.AddHours(1)));
            var file = Path.Combine(root, "c-files", "x.txt"); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "hello");
            typeof(DownloadJob).GetProperty("FilePath")!.SetValue(job, file); typeof(DownloadJob).GetProperty("FileName")!.SetValue(job, "x.txt");
            void Click(Window w, string name) => ((Button)w.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Window Make(bool keep)
            {
                var w = new DownloadCompleteWindow(job, new AppSettings { KeepCompletionWindowOpen = keep }, () => { }) { ShowInTaskbar = false };
                w.Show(); Pump(); return w;
            }
            foreach (var (keep, button) in new[] { (false, "OpenButton"), (false, "FolderButton"), (true, "OpenButton"), (true, "FolderButton") })
            {
                var w = Make(keep); bool closed = false; w.Closed += (_, _) => closed = true;
                int before = launched.Count + folders.Count;
                Click(w, button); Pump(300);
                Check(launched.Count + folders.Count == before + 1, $"keep={keep} {button}: the action ran");
                Check(closed == !keep, $"keep={keep} {button}: window {(keep ? "stays open" : "closes")}");
                if (!closed) w.Close();
            }
            fail = true;
            foreach (var button in new[] { "OpenButton", "FolderButton" })
            {
                var w = Make(false); bool closed = false; w.Closed += (_, _) => closed = true;
                Click(w, button); Pump(300);
                Check(!closed, $"{button}: a failed open keeps the window with its message");
                w.Close();
            }
            Check(launched.All(p => p == file) && folders.All(p => p == Path.GetDirectoryName(file)), "the file and its folder were the ones opened (fakes only)");
            var settingsWindow = new SettingsWindow(new AppSettings { KeepCompletionWindowOpen = true }) { ShowInTaskbar = false };
            var box = (CheckBox)settingsWindow.FindName("KeepCompletionOpen");
            Check(box.IsChecked == true, "Options > Downloads shows the option, reflecting the setting"); settingsWindow.Close();
        }
        finally { DownloadFileActions.Prompt = priorPrompt; DownloadFileActions.Launcher = priorLauncher; DownloadFileActions.FolderLauncher = priorFolder; }
        CheckLocales(new[] { "options.chk.keep_completion_open" }, "keep-open");
    }

    // ------------------------------------------------------------------ item 2: start with Windows
    private sealed class FakeStartup : IStartupEntryStore
    {
        public readonly Dictionary<StartupScope, string?> Values = new(); public bool MachineWritable = true; public int Writes;
        public string? Get(StartupScope s) => Values.TryGetValue(s, out var v) ? v : null;
        public bool Set(StartupScope s, string v) { if (s == StartupScope.Machine && !MachineWritable) return false; Values[s] = v; Writes++; return true; }
    }
    private static void StartupLogic(string root)
    {
        var d = new AppSettings(); Check(d.StartupWindow == StartupWindowMode.Tray, "default: minimized to tray");
        Check(Enum.GetNames<StartupWindowMode>().SequenceEqual(new[] { "Tray", "Normal", "Maximized" }), "three option values");
        Check(StartupArguments.Parse(new[] { "--startup" }).Startup && !StartupArguments.Parse(new[] { "--startup" }).Tray, "--startup is parsed");
        Check(StartupArguments.Parse(new[] { "--STARTUP", "x.torrent" }).Startup, "the marker ignores case and combines with a file");
        Check(!StartupArguments.Parse(Array.Empty<string>()).Startup && !StartupArguments.Parse(new[] { "--tray" }).Startup, "no marker = manual launch");
        foreach (var (args, mode, expect) in new (string[], StartupWindowMode, LaunchView)[]
        {
            (Array.Empty<string>(), StartupWindowMode.Tray, LaunchView.Normal), (Array.Empty<string>(), StartupWindowMode.Maximized, LaunchView.Normal), (Array.Empty<string>(), StartupWindowMode.Normal, LaunchView.Normal),
            (new[] { "--startup" }, StartupWindowMode.Tray, LaunchView.Tray), (new[] { "--startup" }, StartupWindowMode.Normal, LaunchView.Normal), (new[] { "--startup" }, StartupWindowMode.Maximized, LaunchView.Maximized),
            (new[] { "--tray" }, StartupWindowMode.Maximized, LaunchView.Tray), (new[] { "--startup", "--tray" }, StartupWindowMode.Normal, LaunchView.Tray),
        })
            Check(StartupLaunch.Decide(StartupArguments.Parse(args), mode) == expect, $"[{string.Join(' ', args)}] + {mode} -> {expect}");
        const string exe = @"C:\Program Files\NeoDM\NeoDM.exe";
        const string quoted = "\"" + exe + "\"";
        Check(StartupEntry.Build(exe) == quoted + " --startup", "the entry is the quoted program plus the marker");
        Check(StartupEntry.IsOwn(quoted, exe) && StartupEntry.IsOwn(exe.ToLowerInvariant(), exe) && !StartupEntry.IsOwn("\"C:\\Other\\App.exe\"", exe), "only NeoDM's own program counts");
        Check(!StartupEntry.HasMarker(quoted) && StartupEntry.HasMarker(StartupEntry.Build(exe)), "an old entry has no marker, a new one has");
        var fake = new FakeStartup(); fake.Values[StartupScope.User] = quoted; fake.Values[StartupScope.Machine] = quoted;
        Check(StartupEntry.Migrate(fake, exe) == 2 && fake.Values[StartupScope.User] == StartupEntry.Build(exe) && fake.Values[StartupScope.Machine] == StartupEntry.Build(exe), "old entries in both scopes are rewritten with the marker");
        int writes = fake.Writes; Check(StartupEntry.Migrate(fake, exe) == 0 && fake.Writes == writes, "a second pass changes nothing");
        var other = new FakeStartup(); other.Values[StartupScope.User] = "\"C:\\Other\\App.exe\" --x"; Check(StartupEntry.Migrate(other, exe) == 0 && other.Writes == 0, "another program's entry is never touched");
        var none = new FakeStartup(); Check(StartupEntry.Migrate(none, exe) == 0 && none.Values.Count == 0, "no entry = no entry is created");
        var locked = new FakeStartup { MachineWritable = false }; locked.Values[StartupScope.Machine] = quoted; Check(StartupEntry.Migrate(locked, exe) == 0 && locked.Values[StartupScope.Machine] == quoted, "a machine entry without rights stays as it was (the installer upgrade rewrites it)");
        var wxs = File.ReadAllText(FindUp("installer/NeoDM.wxs"));
        Check(wxs.Contains("[#AppExeFile]&quot; --startup"), "installer: the Run value carries --startup");
        var manager = new DownloadManager(Path.Combine(root, "s-files"), Path.Combine(root, "s-state"), new DownloadOptions());
        foreach (var (view, state) in new[] { (LaunchView.Normal, WindowState.Normal), (LaunchView.Maximized, WindowState.Maximized) })
        {
            var main = new MainWindow(manager, new AppSettings { DownloadDirectory = Path.Combine(root, "s-files") }, () => { }, enableTray: false) { ShowInTaskbar = false };
            try { main.ShowForLaunch(view); Pump(200); Check(main.IsVisible && main.WindowState == state, $"{view}: window shown {state}"); }
            finally { main.Close(); }
        }
        CheckLocales(new[] { "options.startup.heading", "options.startup.label", "options.startup.tray", "options.startup.normal", "options.startup.maximized", "options.startup.note" }, "startup");
    }
    private static string FindUp(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative))) dir = dir.Parent;
        return dir is null ? throw new FileNotFoundException(relative) : Path.Combine(dir.FullName, relative);
    }
    private static void StartupOptions(string root)
    {
        var state = Path.Combine(root, "o-state"); Directory.CreateDirectory(state);
        var s = new AppSettings { StartupWindow = StartupWindowMode.Maximized };
        var w = new SettingsWindow(s, state) { ShowInTaskbar = false }; w.Show(); Pump();
        var combo = (ComboBox)w.FindName("StartupWindowBox");
        Check(combo.Items.Count == 3 && ((ComboBoxItem)combo.SelectedItem).Tag is StartupWindowMode.Maximized, "Options > General lists three choices and selects the saved one");
        combo.SelectedIndex = 1;
        ((CheckBox)w.FindName("KeepCompletionOpen")).IsChecked = true;
        typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, new object[] { w, new RoutedEventArgs() });
        Pump();
        var saved = AppSettings.Load(state);
        Check(saved.StartupWindow == StartupWindowMode.Normal && saved.KeepCompletionWindowOpen, "Save keeps both new options");
        try { w.Close(); } catch (InvalidOperationException) { }
        var corrupt = Path.Combine(root, "o-bad"); Directory.CreateDirectory(corrupt); File.WriteAllText(Path.Combine(corrupt, "settings.json"), "{\"StartupWindow\":\"Sideways\"}");
        Check(AppSettings.Load(corrupt).StartupWindow == StartupWindowMode.Tray, "an unknown stored value falls back to tray");
    }
}
