using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;
using NDM.Tests;

/// <summary>1.5.9: the themed "Open downloaded file" confirmation, the per-kind "Don't ask again" and the rule that risky files always ask.</summary>
internal static partial class ManageTests
{
    private static string[] Ext(string list) => list.Split(' ');

    private static void OpenFileTests(string root, FixtureServer server)
    {
        var priorNative = WindowActivation.Native; var native = new CaptureNative(); WindowActivation.Native = native;
        var priorPrompt = DownloadFileActions.Prompt; var priorLauncher = DownloadFileActions.Launcher;
        var prompts = new List<(string Name, OpenKind? Kind)>(); var launched = new List<string>();
        Func<bool, bool, Func<Window?, string, OpenKind?, OpenConfirmResult>> answer = (open, remember) => (_, name, kind) => { prompts.Add((name, kind)); return new OpenConfirmResult(open, remember); };
        DownloadFileActions.Launcher = path => launched.Add(path); // never launch real files
        try
        {
            // ---------- 1. classification: the table, exactly
            var table = new (OpenKind Kind, string List)[]
            {
                (OpenKind.Video, "mp4 mkv webm avi mov m4v wmv flv mpg mpeg 3gp ts"), (OpenKind.Music, "mp3 m4a aac flac wav ogg opus wma"),
                (OpenKind.Pictures, "jpg jpeg png gif webp bmp tif tiff avif heic svg"), (OpenKind.Documents, "pdf txt rtf csv epub md"),
                (OpenKind.Archives, "zip rar 7z tar gz tgz bz2 xz iso"),
            };
            foreach (var (kind, list) in table)
            {
                Check(OpenFilePolicy.Extensions(kind).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(Ext(list).OrderBy(x => x, StringComparer.Ordinal)), $"kind {kind}: the extension list is exactly the specified one");
                foreach (var e in Ext(list))
                    Check(OpenFilePolicy.Classify("My File." + e) == kind && OpenFilePolicy.Classify("MY FILE." + e.ToUpperInvariant()) == kind && OpenFilePolicy.Classify("a.b.c." + e) == kind, $"kind {kind}: .{e} in any case");
            }
            var risky = Ext("exe msi msix appx bat cmd com scr pif ps1 psm1 vbs vbe js jse wsf wsh hta cpl dll sys lnk url reg jar apk dmg pkg deb rpm appimage doc docx docm dot dotx dotm xls xlsx xlsm xlsb xlt xltx xltm ppt pptx pptm pps ppsx potx ppam odt ods odp rtfx html htm xhtml mhtml chm msc inf gadget application vb vbscript ws wsc ade adp mde sct shb lib xnk mht one pub accdb mdb unknown xyz mp4x mp45 bin dat");
            foreach (var e in risky) Check(OpenFilePolicy.Classify("name." + e) is null && OpenFilePolicy.Classify("NAME." + e.ToUpperInvariant()) is null, $"risky or unknown .{e} always asks (no kind)");
            var hostile = new (string Name, OpenKind? Expected, string Why)[]
            {
                ("video.mp4.exe", null, "double extension: the last one decides"), ("video.exe.mp4", OpenKind.Video, "last extension is mp4"), ("song.mp3.lnk", null, "shortcut behind a music name"),
                ("report.pdf.js", null, "script behind a document name"), ("report.pdf.docm", null, "macro document"), ("a.zip.exe", null, "program behind an archive name"),
                ("movie.mp4 ", OpenKind.Video, "Windows drops a trailing space"), ("movie.mp4.", OpenKind.Video, "Windows drops a trailing dot"), ("movie.exe ", null, "trailing space after exe"),
                ("movie.exe.", null, "trailing dot after exe"), ("movie.exe . .", null, "dots and spaces after exe"), ("movie.mp4 .exe", null, "space before the last extension"),
                ("mov\u202Eie.mp4", null, "right-to-left override"), ("movie\u200B.mp4", null, "zero-width space in the name"), ("movie.mp\u200B4", null, "zero-width space in the extension"),
                ("movie.mp4\u200B", null, "zero-width space after the extension"), ("movie\u200E.mp4", null, "left-to-right mark"), ("movie\u2066.mp4\u2069", null, "isolates"), ("movie.mp4\u202E", null, "override at the end"),
                ("movie\uFEFF.mp4", null, "byte order mark"), ("a.mp4\u0000.exe", null, "NUL"), ("a.mp4\r\n.exe", null, "line break"), ("a.mp4:evil.exe", null, "alternate data stream"), ("a.mp4:Zone.Identifier", null, "stream"),
                (@"..\a.mp4", null, "path separator"), ("dir/a.mp4", null, "path separator"), ("a?.mp4", null, "wildcard"), ("a.m\u0440\u0034", null, "Cyrillic letter in the extension"), ("a.mp4\uFF0Eexe", null, "full-width dot"),
                ("noextension", null, "no extension"), ("file.", null, "only a dot"), (".mp4", null, "hidden file without extension"), ("", null, "empty"), ("   ", null, "blank"), ("a." + new string('x', 20), null, "long extension"),
                ("tar.gz", OpenKind.Archives, "the name is tar and the extension gz"), ("backup.tar.gz", OpenKind.Archives, ".tar.gz is an archive"), ("BACKUP.TAR.GZ", OpenKind.Archives, "upper case .tar.gz"),
                ("backup.tar.gz.exe", null, "exe after .tar.gz"), ("Ünïcode ملف فيديو 🎬.mp4", OpenKind.Video, "non-ASCII name, ASCII extension"), ("ملف.PDF", OpenKind.Documents, "Arabic name upper case extension"),
                (new string('a', 300) + ".mp4", null, "longer than a file name can be"),
            };
            foreach (var (name, expected, why) in hostile) Check(OpenFilePolicy.Classify(name) == expected, $"classify {System.Text.Json.JsonSerializer.Serialize(name)} => {(expected?.ToString() ?? "ask, no box")}: {why}");
            Check(OpenFilePolicy.Classify(null) is null, "a missing name asks");
            Check(OpenFilePolicy.KindFor("clip.mp4", @"C:\Downloads\clip.mp4") == OpenKind.Video && OpenFilePolicy.KindFor("clip.mp4", @"C:\Downloads\clip.mp4.exe") is null && OpenFilePolicy.KindFor("clip.mp4", @"C:\Downloads\clip.mkv") == OpenKind.Video && OpenFilePolicy.KindFor("clip.mp4", @"C:\Downloads\clip.mp3") is null && OpenFilePolicy.KindFor("clip.mp4", @"C:\Downloads\clip.pdf") is null, "the name and the file on disk must agree on the kind");
            Check(OpenFilePolicy.KindFor("clip.mp4", "C:\\bad|path\u0000.mp4") is null, "a path with illegal characters asks");

            // ---------- 2. the setting: code enforces it, whatever settings.json says
            var fresh = new AppSettings();
            Check(fresh.OpenWithoutAsking.Count == 0 && AppSettings.CurrentSchema == 8 && !OpenFilePolicy.CanSkipPrompt(fresh, "a.mp4"), "default: empty list, always ask");
            var poisoned = new AppSettings { OpenWithoutAsking = new() { "Video", "Music", "Pictures", "Documents", "Archives", "Programs", "Executables", "exe", "ps1", "Risky", "All", "*", "Office", "docx", "lnk", "html", "", "video.mp4.exe", "video" } };
            foreach (var e in risky.Concat(new[] { "mp4.exe" })) Check(!OpenFilePolicy.CanSkipPrompt(poisoned, "x." + e), $"a hand-edited list cannot skip the question for .{e}");
            foreach (var n in hostile.Where(h => h.Expected is null).Select(h => h.Name)) Check(!OpenFilePolicy.CanSkipPrompt(poisoned, n), "even with every kind listed, this name still asks: " + JsonSerializer.Serialize(n));
            Check(OpenFilePolicy.CanSkipPrompt(poisoned, "x.mp4") && OpenFilePolicy.CanSkipPrompt(poisoned, "x.TAR.GZ"), "listed safe kinds are skipped");
            Check(OpenFilePolicy.Remembered(poisoned).Count == 5, "unknown names in the list are ignored");
            var only = new AppSettings { OpenWithoutAsking = new() { "video" } };
            Check(OpenFilePolicy.CanSkipPrompt(only, "x.mkv") && !OpenFilePolicy.CanSkipPrompt(only, "x.mp3") && !OpenFilePolicy.CanSkipPrompt(only, "x.pdf") && !OpenFilePolicy.CanSkipPrompt(only, "x.zip") && !OpenFilePolicy.CanSkipPrompt(only, "x.jpg"), "one remembered kind does not unlock the others (names are case-insensitive)");

            string state = Path.Combine(root, "openfile-settings"); Directory.CreateDirectory(state);
            var saved = new AppSettings(); OpenFilePolicy.SetRemembered(saved, OpenKind.Music, true); OpenFilePolicy.SetRemembered(saved, OpenKind.Video, true); OpenFilePolicy.SetRemembered(saved, OpenKind.Video, true);
            saved.Save(state);
            Check(File.ReadAllText(Path.Combine(state, "settings.json")).Contains("\"OpenWithoutAsking\"") && AppSettings.Load(state).OpenWithoutAsking.SequenceEqual(new[] { "Video", "Music" }), "round trip: canonical names, no duplicates, stable order");
            Check(saved.Copy().OpenWithoutAsking.SequenceEqual(new[] { "Video", "Music" }), "Copy keeps the list");
            var target = new AppSettings(); target.CopyFrom(saved); OpenFilePolicy.SetRemembered(saved, OpenKind.Music, false);
            Check(target.OpenWithoutAsking.SequenceEqual(new[] { "Video", "Music" }) && saved.OpenWithoutAsking.SequenceEqual(new[] { "Video" }), "CopyFrom copies the list, not the reference");
            File.WriteAllText(Path.Combine(state, "settings.json"), "{\"SettingsSchema\":6,\"Theme\":\"Dark\"}");
            var old = AppSettings.Load(state); Check(old.OpenWithoutAsking.Count == 0 && old.Theme == "Dark" && old.SettingsSchema == AppSettings.CurrentSchema, "an old settings file (schema 6) loads: nothing remembered, other values kept");
            File.WriteAllText(Path.Combine(state, "settings.json"), "{\"SettingsSchema\":6,\"OpenWithoutAsking\":[\"Video\",\"Pictures\"]}");
            Check(AppSettings.Load(state).OpenWithoutAsking.Count == 0, "schema migration: a list that appears before schema 7 is not trusted");
            File.WriteAllText(Path.Combine(state, "settings.json"), "{\"SettingsSchema\":7,\"OpenWithoutAsking\":[\"video\",\"Programs\",\"exe\",\"MUSIC\",\"Music\",null,\"Documents\"]}");
            Check(AppSettings.Load(state).OpenWithoutAsking.SequenceEqual(new[] { "Video", "Music", "Documents" }), "poisoned settings.json: unknown names dropped, case fixed, duplicates removed");
            File.WriteAllText(Path.Combine(state, "settings.json"), "{\"SettingsSchema\":7,\"OpenWithoutAsking\":\"Video\"}");
            Check(AppSettings.Load(state).OpenWithoutAsking.Count == 0, "a list of the wrong type loads as empty");
            File.WriteAllText(Path.Combine(state, "settings.json"), "{\"SettingsSchema\":7,\"OpenWithoutAsking\":[\"Video\",5,{\"a\":1}]}");
            Check(AppSettings.Load(state).OpenWithoutAsking.Count == 0, "a list with non-text entries loads as empty (nothing is trusted)");
            File.WriteAllText(Path.Combine(state, "settings.json"), "{\"SettingsSchema\":7,\"OpenWithoutAsking\":null}");
            Check(AppSettings.Load(state).OpenWithoutAsking.Count == 0, "null loads as empty");

            // ---------- 3. ConfirmOpen with a fake prompt
            var s1 = new AppSettings(); int saves = 0; Action save = () => saves++;
            DownloadFileActions.Prompt = answer(true, true); prompts.Clear();
            Check(DownloadFileActions.ConfirmOpen(null, "clip.mp4", @"C:\d\clip.mp4", s1, save) && prompts.Count == 1 && prompts[0].Kind == OpenKind.Video, "first time: the window is shown and offers the box for Video");
            Check(s1.OpenWithoutAsking.SequenceEqual(new[] { "Video" }) && saves == 1, "Yes + the box: Video is remembered and saved once");
            Check(DownloadFileActions.ConfirmOpen(null, "other.MKV", @"C:\d\other.MKV", s1, save) && prompts.Count == 1, "afterwards every video opens without the window");
            Check(DownloadFileActions.ConfirmOpen(null, "song.mp3", @"C:\d\song.mp3", s1, save) && prompts.Count == 2 && prompts[1].Kind == OpenKind.Music, "other kinds still ask");
            Check(s1.OpenWithoutAsking.SequenceEqual(new[] { "Video", "Music" }), "a second kind is added");
            prompts.Clear();
            Check(DownloadFileActions.ConfirmOpen(null, "setup.exe", @"C:\d\setup.exe", s1, save) && prompts.Count == 1 && prompts[0].Kind is null && s1.OpenWithoutAsking.Count == 2, "a program asks, is offered no box and nothing is remembered even if the fake window says remember");
            Check(DownloadFileActions.ConfirmOpen(null, "setup.exe", @"C:\d\setup.exe", s1, save) && prompts.Count == 2, "...and asks again the next time");
            var s2 = new AppSettings(); saves = 0; DownloadFileActions.Prompt = answer(false, true); prompts.Clear();
            Check(!DownloadFileActions.ConfirmOpen(null, "clip.mp4", @"C:\d\clip.mp4", s2, save) && s2.OpenWithoutAsking.Count == 0 && saves == 0, "No is never remembered, even when the box was ticked");
            Check(!DownloadFileActions.ConfirmOpen(null, "clip.mp4", @"C:\d\clip.mp4", s2, save) && prompts.Count == 2, "after a No the next open asks again");
            DownloadFileActions.Prompt = answer(true, false);
            Check(DownloadFileActions.ConfirmOpen(null, "clip.mp4", @"C:\d\clip.mp4", s2, save) && s2.OpenWithoutAsking.Count == 0 && saves == 0, "Yes without the box remembers nothing");
            DownloadFileActions.Prompt = answer(true, true); prompts.Clear();
            Check(DownloadFileActions.ConfirmOpen(null, "clip.mp4", @"C:\d\clip.mp4", s2, null) && prompts[0].Kind is null && s2.OpenWithoutAsking.Count == 0, "nowhere to save: no box is offered");
            Check(DownloadFileActions.ConfirmOpen(null, "clip.mp4", @"C:\d\clip.mp4", null, save) && prompts[1].Kind is null, "no settings: no box is offered");
            var s3 = new AppSettings();
            Check(DownloadFileActions.ConfirmOpen(null, "clip.mp4", @"C:\d\clip.mp4", s3, () => throw new IOException("disk full")) && s3.OpenWithoutAsking.Count == 0, "a failed save is undone and the file still opens");
            var s4 = new AppSettings { OpenWithoutAsking = new() { "Video" } }; prompts.Clear();
            Check(DownloadFileActions.ConfirmOpen(null, "clip.mp4", @"C:\d\clip.mp4.exe", s4, save) && prompts.Count == 1 && prompts[0].Kind is null, "a Video name on a file that is really .exe asks");
            Check(DownloadFileActions.ConfirmOpen(null, "clip.mp4.exe", @"C:\d\clip.mp4.exe", s4, save) && prompts.Count == 2 && prompts[1].Kind is null, "video.mp4.exe asks, with no box");

            // ---------- 4. the real window
            Loc.SetLanguage("en");
            var w = new OpenFileConfirmWindow("holiday movie.mp4", OpenKind.Video) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
            w.Show(); Pump(250);
            var message = Field<TextBox>(w, "MessageText");
            Check(message.Text == "Open \"holiday movie.mp4\"?\n\nOnly proceed if you trust the file and its source. Executables, scripts, shortcuts and documents can run code.", "same wording as the old message box: " + message.Text.Replace("\n", "|"));
            Check(message.IsReadOnly && SelectableText.GetEnabled(message) && message.Focusable && w.Title == "Open downloaded file", "the question (with the file name) is selectable and copyable with the Copy menu");
            var box = Field<CheckBox>(w, "DontAskBox");
            Check(box.Visibility == Visibility.Visible && box.IsChecked != true && (string)box.Content == "Don't ask again for video files", "safe kind: the box is shown, unticked, and names the kind");
            var yes = Field<Button>(w, "YesButton"); var no = Field<Button>(w, "NoButton");
            Check(no.IsDefault && no.IsCancel && !yes.IsDefault && !yes.IsCancel && (string)yes.Content == "Yes" && (string)no.Content == "No", "No is the default button (Enter) and Esc");
            Check(System.Windows.Input.FocusManager.GetFocusedElement(w) == no || no.IsKeyboardFocused, "the focus starts on No");
            Check(native.Calls.Contains("setfg"), "the window is brought to the front like the other dialogs");
            Check(!w.Result.Open && !w.Result.Remember, "nothing answered yet");
            box.IsChecked = true; Click(w, "YesButton"); Pump(100);
            Check(w.Result == new OpenConfirmResult(true, true) && !w.IsVisible, "Yes with the box ticked: open and remember");
            var w2 = new OpenFileConfirmWindow("holiday movie.mp4", OpenKind.Video) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
            w2.Show(); Pump(150); Field<CheckBox>(w2, "DontAskBox").IsChecked = true; Click(w2, "NoButton"); Pump(100);
            Check(w2.Result == new OpenConfirmResult(false, false), "No with the box ticked: do not open and do not remember");
            var w3 = new OpenFileConfirmWindow("holiday movie.mp4", OpenKind.Video) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
            w3.Show(); Pump(150); Click(w3, "YesButton"); Pump(100);
            Check(w3.Result == new OpenConfirmResult(true, false), "Yes without the box: open, remember nothing");
            var w4 = new OpenFileConfirmWindow("setup.exe", null) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
            w4.Show(); Pump(150);
            Check(Field<CheckBox>(w4, "DontAskBox").Visibility == Visibility.Collapsed && Field<TextBox>(w4, "MessageText").Text.Contains("setup.exe"), "a program: no box at all");
            Field<CheckBox>(w4, "DontAskBox").IsChecked = true; Click(w4, "YesButton"); Pump(100);
            Check(w4.Result == new OpenConfirmResult(true, false), "a hidden box can never produce Remember");
            foreach (var kind in OpenFilePolicy.All)
            {
                var wk = new OpenFileConfirmWindow("x", kind) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
                wk.Show(); Pump(80);
                Check(Field<CheckBox>(wk, "DontAskBox").Content is string text && text.StartsWith("Don't ask again for ") && !text.Contains("openfile."), $"the box text for {kind}: {Field<CheckBox>(wk, "DontAskBox").Content}");
                wk.Close();
            }
            // esc answers No
            var w5 = new OpenFileConfirmWindow("clip.mp4", OpenKind.Video) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
            w5.Show(); Pump(150); Field<CheckBox>(w5, "DontAskBox").IsChecked = true; Click(w5, "NoButton"); Check(!w5.Result.Open, "the cancel button is the No button");

            // ---------- 5. the three places that ask: main list, completion window, progress window
            DownloadFileActions.Prompt = answer(true, true);
            using var m = Manager(root, "openflow");
            var vid = Sync(Done(m, server.BaseUrl + "/empty", "clip.mp4")); var exe = Sync(Done(m, server.BaseUrl + "/empty", "setup.exe"));
            var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "openflow", "files"), ShowCompletionDialog = false };
            int mainSaves = 0;
            var main = new MainWindow(m, settings, () => mainSaves++, enableTray: false) { ShowInTaskbar = false };
            try
            {
                main.Show(); Pump(800);
                var list = Field<ListView>(main, "DownloadList");
                void Select(string name) { list.SelectedItems.Clear(); list.SelectedItems.Add(main.Rows.Single(r => r.FileName == name)); Pump(100); }
                prompts.Clear(); launched.Clear();
                Select("clip.mp4"); Call(main, "Open_Click"); Pump(100);
                Check(prompts.Count == 1 && prompts[0].Kind == OpenKind.Video && launched.Count == 1 && launched[0] == vid.FilePath, "main list: Open asks once, offers the box and opens");
                Check(settings.OpenWithoutAsking.SequenceEqual(new[] { "Video" }) && mainSaves == 1, "main list: the choice is stored in the app settings and saved");
                Select("clip.mp4"); Call(main, "Open_Click"); Pump(100);
                Check(prompts.Count == 1 && launched.Count == 2, "main list: the second open of a video asks nothing and opens");
                Select("setup.exe"); Call(main, "Open_Click"); Pump(100);
                Check(prompts.Count == 2 && prompts[1].Kind is null && launched.Count == 3 && settings.OpenWithoutAsking.Count == 1, "main list: a program asks (no box) even though a kind is remembered");
                settings.OpenWithoutAsking = new() { "Video", "Programs", "exe", "*" };
                Select("setup.exe"); Call(main, "Open_Click"); Pump(100);
                Check(prompts.Count == 3 && launched.Count == 4, "main list: a hand-edited list cannot make a program skip the question");
                DownloadFileActions.Prompt = answer(false, true);
                Select("setup.exe"); Call(main, "Open_Click"); Pump(100);
                Check(prompts.Count == 4 && launched.Count == 4, "main list: No does not open");
                settings.OpenWithoutAsking = new();
                Select("clip.mp4"); Call(main, "Open_Click"); Pump(100);
                Check(prompts.Count == 5 && launched.Count == 4 && settings.OpenWithoutAsking.Count == 0, "main list: after No it asks again and nothing was stored");
                // Open folder does not ask
                prompts.Clear(); var folders = new List<string>();
                // completion window
                DownloadFileActions.Prompt = answer(true, true);
                var vidSettings = new AppSettings(); int cSaves = 0;
                var complete = new DownloadCompleteWindow(vid, vidSettings, () => cSaves++) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 30, Top = 30 };
                complete.Show(); Pump(150); launched.Clear();
                Click(complete, "OpenButton"); Pump(100);
                Check(prompts.Count == 1 && prompts[0].Kind == OpenKind.Video && launched.Count == 1 && vidSettings.OpenWithoutAsking.SequenceEqual(new[] { "Video" }) && cSaves == 1, "completion window: Open uses the same confirmation and remembers");
                Click(complete, "OpenButton"); Pump(100);
                Check(prompts.Count == 1 && launched.Count == 2, "completion window: remembered video opens without asking");
                complete.Close();
                var exeComplete = new DownloadCompleteWindow(exe, vidSettings, () => { }) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 30, Top = 30 };
                exeComplete.Show(); Pump(150); Click(exeComplete, "OpenButton"); Pump(100);
                Check(prompts.Count == 2 && prompts[1].Kind is null && launched.Count == 3, "completion window: a program still asks");
                // folder button never asks
                int before = prompts.Count; DownloadFileActions.Prompt = (_, _, _) => throw new Exception("Open folder must not ask");
                exeComplete.Close();
                // progress window
                DownloadFileActions.Prompt = answer(true, true);
                var pSettings = new AppSettings(); int pSaves = 0;
                var progress = new DownloadProgressWindow(m, vid.Id, new JobCompletionStore(), pSettings, () => pSaves++) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 30, Top = 30 };
                progress.Show(); Pump(250); prompts.Clear(); launched.Clear();
                Call(progress, "Open_Click"); Pump(100);
                Check(prompts.Count == 1 && prompts[0].Kind == OpenKind.Video && launched.Count == 1 && pSettings.OpenWithoutAsking.SequenceEqual(new[] { "Video" }) && pSaves == 1, "progress window: Open uses the same confirmation and remembers");
                Call(progress, "Open_Click"); Pump(100);
                Check(prompts.Count == 1 && launched.Count == 2, "progress window: remembered video opens without asking");
                progress.Close();
                var noSave = new DownloadProgressWindow(m, vid.Id) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 30, Top = 30 };
                noSave.Show(); Pump(250); prompts.Clear(); DownloadFileActions.Prompt = answer(true, true);
                Call(noSave, "Open_Click"); Pump(100);
                Check(prompts.Count == 1 && prompts[0].Kind is null, "progress window without settings: no box is offered");
                noSave.Close();
            }
            finally { main.Close(); }

            OpenFileOptionsTests(root);

            // ---------- 6. no native message box is left for this question, and every caller passes the settings
            string repo = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(repo, "Directory.Build.props"))) repo = Path.GetDirectoryName(repo) ?? throw new Exception("repository root not found");
            var sources = Directory.EnumerateFiles(Path.Combine(repo, "src", "NDM.App"), "*.*", SearchOption.TopDirectoryOnly).Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml")).ToList();
            var users = sources.Where(f => File.ReadAllText(f).Contains("\"complete.confirm_open\"")).Select(Path.GetFileName).ToList();
            Check(users.SequenceEqual(new[] { "OpenFileConfirmWindow.xaml.cs" }), "only the new window uses the open question text: " + string.Join(",", users));
            var callers = sources.SelectMany(f => File.ReadAllLines(f).Where(l => l.Contains("ConfirmOpen(") && !l.Contains("public static bool ConfirmOpen"))).ToList();
            Check(callers.Count == 3 && callers.All(l => l.Contains("settings", StringComparison.OrdinalIgnoreCase) && l.Contains("ave", StringComparison.OrdinalIgnoreCase)), "the three callers (list, completion window, progress window) pass settings and a save action");
            Check(!File.ReadAllText(Path.Combine(repo, "src", "NDM.App", "DownloadCompleteWindow.xaml.cs")).Contains("MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No"), "the native Yes/No message box is gone");
        }
        finally { DownloadFileActions.Prompt = priorPrompt; DownloadFileActions.Launcher = priorLauncher; WindowActivation.Native = priorNative; }
    }
    /// <summary>Options > Downloads > "Open finished downloads without asking": state, apply, reset by unticking, poisoned lists, languages.</summary>
    private static void OpenFileOptionsTests(string root)
    {
        Loc.SetLanguage("en");
        string state = Path.Combine(root, "openfile-options"); Directory.CreateDirectory(state);
        var settings = new AppSettings { DownloadDirectory = Path.Combine(state, "files"), OpenWithoutAsking = new() { "Video", "Archives" } };
        var w = new SettingsWindow(settings, state); w.Show(); Pump(200);
        var group = Field<GroupBox>(w, "OpenGroup");
        var boxes = new[] { "OpenVideo", "OpenMusic", "OpenPictures", "OpenDocuments", "OpenArchives" }.Select(n => Field<CheckBox>(w, n)).ToArray();
        Check((string)group.Header == "Open finished downloads without asking" && boxes.Select(b => (string)b.Content).SequenceEqual(new[] { "Videos", "Music", "Pictures", "Documents", "Archives" }), "Options > Downloads: the group and its five boxes (one per safe kind)");
        Check(boxes.Select(b => b.IsChecked == true).SequenceEqual(new[] { true, false, false, false, true }), "the boxes show the saved kinds");
        var inner = (StackPanel)group.Content;
        Check(inner.Children.OfType<CheckBox>().Count() == 5 && inner.Children.OfType<TextBlock>().Single().Text == "Programs, scripts, shortcuts and Office files always ask first. Untick a box to be asked again.", "exactly five boxes (no box for programs, scripts, shortcuts or Office files) and the note about them");
        DependencyObject up = group; while (up is not null and not TabItem) up = LogicalTreeHelper.GetParent(up);
        Check(up is TabItem tab && (string)tab.Header == Loc.T("common.menu.downloads"), "the group sits in the Downloads tab");
        boxes[1].IsChecked = true; boxes[0].IsChecked = false;
        Click(w, "SaveButton"); Pump(100);
        Check(settings.OpenWithoutAsking.SequenceEqual(new[] { "Music", "Archives" }) && AppSettings.Load(state).OpenWithoutAsking.SequenceEqual(new[] { "Music", "Archives" }), "Save applies the choice at once and writes it");
        Check(OpenFilePolicy.CanSkipPrompt(settings, "song.mp3") && !OpenFilePolicy.CanSkipPrompt(settings, "clip.mp4") && !OpenFilePolicy.CanSkipPrompt(settings, "setup.exe"), "...so music opens without asking, video asks again, programs ask");
        // Cancel changes nothing
        var c = new SettingsWindow(settings, state); c.Show(); Pump(100);
        Field<CheckBox>(c, "OpenPictures").IsChecked = true; Click(c, "CancelButton"); Pump(100);
        Check(settings.OpenWithoutAsking.SequenceEqual(new[] { "Music", "Archives" }) && AppSettings.Load(state).OpenWithoutAsking.SequenceEqual(new[] { "Music", "Archives" }), "Cancel leaves the list as it was");
        // reset by unticking
        var r = new SettingsWindow(settings, state); r.Show(); Pump(100);
        foreach (var n in new[] { "OpenVideo", "OpenMusic", "OpenPictures", "OpenDocuments", "OpenArchives" }) Field<CheckBox>(r, n).IsChecked = false;
        Click(r, "SaveButton"); Pump(100);
        Check(settings.OpenWithoutAsking.Count == 0 && AppSettings.Load(state).OpenWithoutAsking.Count == 0 && !OpenFilePolicy.CanSkipPrompt(settings, "song.mp3"), "unticking everything resets to always ask");
        // a hand-edited list
        var bad = new AppSettings { DownloadDirectory = Path.Combine(state, "files"), OpenWithoutAsking = new() { "Video", "Programs", "exe", "*" } };
        var b = new SettingsWindow(bad, state); b.Show(); Pump(100);
        Check(Field<CheckBox>(b, "OpenVideo").IsChecked == true && new[] { "OpenMusic", "OpenPictures", "OpenDocuments", "OpenArchives" }.All(n => Field<CheckBox>(b, n).IsChecked != true), "a poisoned list shows only the real kind");
        Click(b, "SaveButton"); Pump(100);
        Check(bad.OpenWithoutAsking.SequenceEqual(new[] { "Video" }) && AppSettings.Load(state).OpenWithoutAsking.SequenceEqual(new[] { "Video" }), "saving the Options drops the unknown names");
        // every language: the new texts exist and are translated; the right-to-left languages mirror the group
        var keysAll = new[] { "openfile.chk.dont_ask_video", "options.openfile.title", "options.openfile.note" };
        var english = keysAll.ToDictionary(k => k, k => Loc.T(k));
        foreach (var language in LanguageCatalog.All)
        {
            Loc.SetLanguage(language.Tag);
            var keys = new[] { "openfile.a11y.question", "openfile.btn.yes", "openfile.btn.no", "openfile.chk.dont_ask_video", "openfile.chk.dont_ask_music", "openfile.chk.dont_ask_pictures", "openfile.chk.dont_ask_documents", "openfile.chk.dont_ask_archives", "options.openfile.title", "options.openfile.video", "options.openfile.music", "options.openfile.pictures", "options.openfile.documents", "options.openfile.archives", "options.openfile.note" };
            Check(keys.All(k => Loc.Has(k) && Loc.T(k).Length > 0 && Loc.T(k) != k), $"{language.Tag}: every new text exists");
            if (language.Tag != "en") Check(keysAll.All(k => Loc.T(k) != english[k]), $"{language.Tag}: translated, not the English text");
            var win = new OpenFileConfirmWindow("clip.mp4", OpenKind.Video) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 30, Top = 30 };
            win.Show(); Pump(120);
            Check(win.FlowDirection == Loc.FlowDirection && ((string)Field<CheckBox>(win, "DontAskBox").Content) == Loc.T("openfile.chk.dont_ask_video") && ((string)Field<Button>(win, "YesButton").Content) == Loc.T("openfile.btn.yes") && win.Title == Loc.T("complete.confirm_open_title"), $"{language.Tag}: the confirmation window follows the language and the reading direction");
            win.Close();
        }
        Loc.SetLanguage("en");
        foreach (var win in new Window[] { w, c, r, b }) win.Close();
    }
}
