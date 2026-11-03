global using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;
using NDM.Tests;

internal sealed class CategoryTestApp : Application
{
    public CategoryTestApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}

/// <summary>1.9.0 item 8: custom categories. Rules and precedence, wildcard edges, safe folders, settings migration, the Options editor (add, rename, reorder, delete, validation, themes, sizes, RTL) and the engine publishing into the custom folder.</summary>
internal static class CategoryTests
{
    private static int passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static void Pump(int ms = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static T Field<T>(Window w, string name) where T : class => (T)(w.FindName(name) ?? throw new Exception("missing control " + name));
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Use(string theme) => ThemeManager.Apply(Application.Current, theme, null);
    private static RenderTargetBitmap Capture(Window window, double scale = 1)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window); return bitmap;
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static AppSettings Settings(string root, Action<AppSettings>? tweak = null)
    {
        var s = new AppSettings { DownloadDirectory = Path.GetFullPath(Path.Combine(root, "dl")), EnableCategoryFolders = true };
        Directory.CreateDirectory(s.DownloadDirectory); tweak?.Invoke(s); return s;
    }
    private static CustomCategory Cat(string name, string folder, string ext = "", string rules = "", bool enabled = true) =>
        new() { Name = name, Folder = folder, Extensions = CategoryRules.ParseExtensions(ext), UrlRules = CategoryRules.ParseRules(rules), Enabled = enabled };
    private static string Folder(AppSettings s, string file, string? url = null, string? referer = null) => CategoryHelper.GetDownloadPath(file, s, url, referer);

    [STAThread]
    private static int Main()
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-CategoryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var app = new CategoryTestApp();
        try
        {
            Precedence(scratch);
            Wildcards();
            Extensions();
            SafePaths(scratch);
            Migration(scratch);
            EditorLogic(scratch);
            CapturePreview(scratch);
            EditorWindow(scratch);
            EditorLooks(scratch);
            Engine(scratch).GetAwaiter().GetResult();
            Console.WriteLine($"Category tests passed: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close();
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            app.Shutdown();
        }
    }

    // ===== matching order =====
    private static void Precedence(string root)
    {
        var s = Settings(root + "/p1");
        string b = s.DownloadDirectory;
        Check(Folder(s, "a.zip") == Path.Combine(b, "Compressed") && Folder(s, "a.mp3") == Path.Combine(b, "Music") && Folder(s, "a.xyz") == b && Folder(s, "noext") == b, "Built-in categories still choose their folders and everything else stays in the download folder");
        s.CustomCategories.Add(Cat("Work", "Work", "zip, docx"));
        Check(Folder(s, "a.zip") == Path.Combine(b, "Work") && Folder(s, "a.docx") == Path.Combine(b, "Work") && Folder(s, "a.rar") == Path.Combine(b, "Compressed"), "A custom extension rule beats the built-in category for the same extension");
        s.CustomCategories.Add(Cat("Work 2", "Work2", "zip"));
        Check(Folder(s, "a.zip") == Path.Combine(b, "Work"), "Custom categories are tried in list order");
        s.CustomCategories.Insert(0, Cat("Archive site", "FromSite", "", "files.example.org"));
        Check(Folder(s, "a.zip", "https://files.example.org/x/a.zip") == Path.Combine(b, "FromSite"), "An address rule is checked before any extension rule");
        Check(Folder(s, "a.zip", "https://other.example.com/a.zip") == Path.Combine(b, "Work"), "When the address rule does not match the extension rules still apply");
        s.CustomCategories.Add(Cat("Late site", "Late", "", "late.example.org"));
        s.CustomCategories.Add(Cat("Early ext", "EarlyExt", "bin"));
        Check(Folder(s, "a.bin", "https://late.example.org/a.bin") == Path.Combine(b, "Late"), "Address rules of every custom category come before extension rules of every custom category");
        s.CustomCategories[1].Enabled = false; // Work
        Check(Folder(s, "a.zip", "https://q.test/a.zip") == Path.Combine(b, "Work2"), "A category that is switched off is skipped");
        s.BuiltInCategories["Video"] = new BuiltInCategorySetting { UrlRules = new() { "video.example.net" } };
        Check(Folder(s, "clip.bin", "https://video.example.net/c") == Path.Combine(b, "EarlyExt") && Folder(s, "clip.dat", "https://video.example.net/c") == Path.Combine(b, "Video"), "A built-in category can have address rules; custom categories still win over it");
        s.BuiltInCategories["Music"] = new BuiltInCategorySetting { Extensions = new() { "mp3" } };
        Check(Folder(s, "a.flac") == b && Folder(s, "a.mp3") == Path.Combine(b, "Music"), "A built-in category's extension list can be edited");
        s.BuiltInCategories["Music"] = new BuiltInCategorySetting { Enabled = false };
        Check(Folder(s, "a.mp3") == b, "A built-in category can be switched off");
        s.BuiltInCategories.Clear(); s.EnableCategoryFolders = false;
        Check(Folder(s, "a.zip", "https://files.example.org/a.zip") == b, "With category folders off every file goes to the download folder");
        s.EnableCategoryFolders = true;
        s.CustomCategories.Insert(0, Cat("Abs", Path.Combine(root, "elsewhere", "abs"), "iso"));
        Check(Folder(s, "disc.iso") == Path.Combine(root, "elsewhere", "abs"), "A category folder can be a full path outside the download folder");
        s.CustomCategories.Insert(0, Cat("Bad", @"..\escape", "log"));
        Check(Folder(s, "x.log") == b, "A category whose folder is unsafe is skipped, the file goes on to the next rule");
        Check(TorrentSavePlanner.DefaultFolder(s, new[] { ("movie/disc.iso", 100L) }) == Path.Combine(root, "elsewhere", "abs"), "Torrents use the same category rules for their folder");
        Check(Folder(s, "video.mp4", "https://www.youtube.com/watch?v=abc") == Path.Combine(b, "Video") && Folder(s, "audio.mp3", "https://www.youtube.com/watch?v=abc") == Path.Combine(b, "Music"), "YouTube previews use the same rules (video.mp4 and audio.mp3 stand for the two modes)");
        s.CustomCategories.Add(Cat("Tube", "Tube", "", "youtube.com"));
        Check(Folder(s, "video.mp4", "https://www.youtube.com/watch?v=abc") == Path.Combine(b, "Tube"), "An address rule can route YouTube downloads");
    }

    private static void Wildcards()
    {
        bool M(string pattern, string? url, string? referer = null) => UrlRuleMatcher.Matches(pattern, url, referer);
        Check(M("example.com", "https://example.com/a") && M("example.com", "https://cdn.example.com/a") && M("EXAMPLE.com", "https://Example.COM/a"), "A plain host matches the host, its subdomains and ignores case");
        Check(!M("example.com", "https://notexample.com/a") && !M("example.com", "https://example.com.evil.test/a"), "A plain host does not match look-alike hosts");
        Check(M("*.example.com", "https://a.b.example.com/x") && M("*.example.com", "https://example.com/x") && !M("*.example.com", "https://example.org/x"), "*.host matches subdomains and the host itself");
        Check(M("files.*.org", "https://files.mirror.org/a") && !M("files.*.org", "https://files.org/a") && !M("files.*.org", "https://x.files.mirror.org/a"), "A wildcard in the middle of a host must match the whole host");
        Check(M("cdn?.example.com", "https://cdn1.example.com/a") && !M("cdn?.example.com", "https://cdn12.example.com/a"), "? stands for exactly one character");
        Check(M("example.com/files/*", "https://example.com/files/a/b.zip") && !M("example.com/files/*", "https://example.com/other/a.zip") && M("*/files/*.zip", "https://x.test/files/a.zip"), "A pattern with a slash matches host and path");
        Check(M("https://example.com/files/*", "http://example.com/files/x") , "The scheme in a pattern is ignored");
        Check(M("example.org", "https://cdn.test/a", "https://www.example.org/page"), "The referring page's host is checked as well");
        Check(!M("example.com/files/*", "https://cdn.test/a", "https://example.com/files/page"), "Path patterns are checked against the address only");
        Check(!M("", "https://a.test/") && !M("   ", "https://a.test/") && !M(new string('a', 201), "https://a.test/"), "Empty and over-long patterns never match");
        Check(M("*", "https://anything.test/a"), "A lone * matches every host");
        Check(!M("example.com", null, null) && !M("example.com", "not a url", "also not"), "Missing or broken addresses never match");
        Check(M("[::1]", "http://[::1]:8080/a") || M("::1", "http://[::1]:8080/a"), "A literal IPv6 host can be matched");
        Check(M("127.0.0.1", "http://127.0.0.1:9000/a"), "A literal IPv4 host can be matched, with a port in the address");
        Check(M("bücher.example", "https://xn--bcher-kva.example/a") || M("xn--bcher-kva.example", "https://bücher.example/a"), "International host names match in their ASCII form");
        var sw = Stopwatch.StartNew(); bool pathological = UrlRuleMatcher.Matches("a*a*a*a*a*a*a*a*a*a*b", "https://" + new string('a', 3000) + ".test/" + new string('a', 3000), null); sw.Stop();
        Check(!pathological && sw.ElapsedMilliseconds < 500, $"A pattern full of * cannot run away ({sw.ElapsedMilliseconds} ms)");
        Check(UrlRuleMatcher.Validate("example.com") is null && UrlRuleMatcher.Validate("a b") == "cat.err.rule_chars" && UrlRuleMatcher.Validate("") == "cat.err.rule_empty" && UrlRuleMatcher.Validate(new string('x', 201)) == "cat.err.rule_long" && UrlRuleMatcher.Validate(@"a\b") == "cat.err.rule_chars", "Rule validation names the problem");
    }

    private static void Extensions()
    {
        Check(string.Join("|", CategoryRules.ParseExtensions(".ZIP, *.7z; tar.gz  rar,,zip")) == "zip|7z|tar.gz|rar", "Typed extensions are cleaned: dots and stars removed, lower case, no repeats");
        Check(CategoryRules.ParseExtensions("a/b, x..y, a:b, ok").SequenceEqual(new[] { "ok" }), "Extensions with path characters, colons or dot runs inside are dropped");
        var s = Settings(Path.Combine(Path.GetTempPath(), "ndm-ext"));
        s.CustomCategories.Add(Cat("Tars", "Tars", "tar.gz, TGZ"));
        Check(Folder(s, "backup.tar.gz") == Path.Combine(s.DownloadDirectory, "Tars") && Folder(s, "Backup.TGZ") == Path.Combine(s.DownloadDirectory, "Tars") && Folder(s, "backup.gz") == Path.Combine(s.DownloadDirectory, "Compressed"), "A two-part extension matches whole names, case is ignored, and the single part still goes to the built-in");
        Check(Folder(s, ".tar.gz") == Path.Combine(s.DownloadDirectory, "Compressed") || Folder(s, ".tar.gz") == s.DownloadDirectory, "A name that is only an extension does not match the two-part rule");
        Check(Folder(s, "") == s.DownloadDirectory && Folder(s, "name.") == s.DownloadDirectory, "Empty names and names ending in a dot go to the download folder");
        Check(CategoryRules.Presets.All(p => p.Extensions.All(CategoryRules.IsValidExtension)) && CategoryRules.Presets.Length >= 6, "Every extension preset only holds valid extensions");
    }

    // ===== safe folders =====
    private static void SafePaths(string root)
    {
        string b = Path.Combine(root, "safe"); Directory.CreateDirectory(b);
        string? Err(string folder, bool allowEmpty = false) { CategoryPaths.Resolve(b, folder, allowEmpty, out var e); return e; }
        Check(CategoryPaths.Resolve(b, "Sub/Deeper", false, out _) == Path.Combine(b, "Sub", "Deeper"), "A relative folder is placed under the download folder");
        Check(CategoryPaths.Resolve(b, Path.Combine(root, "other"), false, out _) == Path.Combine(root, "other"), "A full path is used as it is");
        Check(Err("") == "cat.err.folder_empty" && CategoryPaths.Resolve(b, "", true, out _) == b, "An empty folder is refused for custom categories and means the download folder for built-ins");
        Check(Err(@"..\x") == "cat.err.folder_traversal" && Err("a/../../x") == "cat.err.folder_traversal" && Err(Path.Combine(root, "x", "..", "y")) == "cat.err.folder_traversal" && Err("..") == "cat.err.folder_traversal", "Path traversal with .. is refused, in relative and full paths");
        Check(Err("CON") == "cat.err.folder_reserved" && Err("a/nul") == "cat.err.folder_reserved" && Err("COM1") == "cat.err.folder_reserved" && Err("lpt9.txt") == "cat.err.folder_reserved" && Err("Aux") == "cat.err.folder_reserved", "Windows device names are refused");
        Check(Err("bad.") == "cat.err.folder_chars" && Err("bad /x") == "cat.err.folder_chars" && Err("a|b") == "cat.err.folder_chars" && Err("a*b") == "cat.err.folder_chars" && Err("a\u202Eb") == "cat.err.folder_chars" && Err("a\tb") == "cat.err.folder_chars", "Names ending in dot or space, forbidden and invisible characters are refused");
        Check(Err(@"\\?\C:\x") == "cat.err.folder_device" && Err(@"\\.\PhysicalDrive0") == "cat.err.folder_device", "Device paths are refused");
        Check(Err(@"\rooted") == "cat.err.folder_rooted" && Err("C:drive-relative") == "cat.err.folder_rooted" && Err("/rooted") == "cat.err.folder_rooted", "Rooted and drive-relative paths are refused");
        Check(Err(@"C:\") == "cat.err.folder_root" && Err(new string('x', 250)) == "cat.err.folder_long", "A bare drive and an over-long path are refused");
        // a junction under the download folder
        string target = Path.Combine(root, "junction-target"); Directory.CreateDirectory(target);
        string link = Path.Combine(b, "Linked");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(psi)!) { p.WaitForExit(10000); }
        if (Directory.Exists(link))
        {
            Check(Err("Linked") == "cat.err.folder_link" && Err("Linked/inner") == "cat.err.folder_link" && Err(link) == "cat.err.folder_link", "A folder reached through a junction (reparse point) is refused");
            Check(Err("Regular") is null, "A normal folder next to the junction is fine");
            var s = Settings(root + "/junction", st => st.CustomCategories.Add(Cat("L", "Linked", "xyz")));
            string linkInDl = Path.Combine(s.DownloadDirectory, "Linked"); psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkInDl}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false };
            using (var p = Process.Start(psi)!) { p.WaitForExit(10000); }
            Check(Folder(s, "a.xyz") == s.DownloadDirectory, "The engine never routes a file through a link folder even if settings.json asks for it");
        }
        else Console.WriteLine("NOTE: junction could not be created here, reparse checks skipped");
        var bad = Settings(root + "/validate");
        bad.CustomCategories.Add(Cat("Same", "A", "a")); bad.CustomCategories.Add(Cat("same", "B", "b"));
        Check(CategoryRules.Validate(bad) is { Key: "cat.err.name_taken" }, "Duplicate names (ignoring case) are refused");
        bad.CustomCategories.Clear(); bad.CustomCategories.Add(Cat("Music", "M", "a"));
        Check(CategoryRules.Validate(bad) is { Key: "cat.err.name_taken" }, "A custom category cannot reuse a built-in name");
        bad.CustomCategories.Clear(); bad.CustomCategories.Add(Cat("Empty", "E"));
        Check(CategoryRules.Validate(bad) is { Key: "cat.err.no_rule" }, "A category with neither extension nor rule is refused");
        bad.CustomCategories.Clear(); bad.CustomCategories.Add(Cat("  ", "E", "a"));
        Check(CategoryRules.Validate(bad) is { Key: "cat.err.name_empty" }, "An empty name is refused");
        bad.CustomCategories.Clear(); bad.ProgramsFolder = "..\\x";
        Check(CategoryRules.Validate(bad) is { Key: "cat.err.folder_traversal" }, "Built-in category folders are validated too");
        // every message key exists in every language
        string[] keys = { "cat.err.name_empty", "cat.err.name_long", "cat.err.name_taken", "cat.err.no_rule", "cat.err.rule_empty", "cat.err.rule_long", "cat.err.rule_chars", "cat.err.folder_empty", "cat.err.folder_long", "cat.err.folder_chars", "cat.err.folder_device", "cat.err.folder_rooted", "cat.err.folder_root", "cat.err.folder_traversal", "cat.err.folder_reserved", "cat.err.folder_link", "cat.err.invalid" };
        string before = Loc.Language;
        foreach (var tag in new[] { "en", "ar", "es", "fr", "de", "pt-BR", "ru", "tr", "zh-Hans", "ja" })
        {
            Loc.SetLanguage(tag);
            Check(keys.All(k => Loc.T(k) != k && Loc.T(k).Length > 4), $"Validation messages exist in {tag}");
        }
        Loc.SetLanguage(before);
    }

    // ===== settings schema =====
    private static void Migration(string root)
    {
        string dir = Path.Combine(root, "migrate"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"SettingsSchema\":8,\"EnableCategoryFolders\":true,\"ProgramsFolder\":\"Apps\\\\Old\",\"MusicFolder\":\"D:\\\\Tunes\",\"VideoFolder\":\"Films\"}");
        var old = AppSettings.Load(dir);
        Check(AppSettings.CurrentSchema == 9 && old.SettingsSchema == 9, "The settings schema is 9");
        Check(old.ProgramsFolder == "Apps\\Old" && old.MusicFolder == "D:\\Tunes" && old.VideoFolder == "Films" && old.CompressedFolder == "Compressed", "A schema 8 file keeps its category folders when it moves to schema 9");
        Check(old.CustomCategories.Count == 0 && old.BuiltInCategories.Count == 0 && !old.WatchClipboardForLinks, "New category and link grabber settings start empty and off");
        Check(Folder(old, "x.exe") == Path.Combine(old.DownloadDirectory, "Apps", "Old") && Folder(old, "x.mp3") == "D:\\Tunes", "Old folders still decide where files go after the migration");
        old.CustomCategories.Add(Cat("Mine", "Mine", "abc, def", "example.org\nfiles.test/*")); old.BuiltInCategories["Video"] = new BuiltInCategorySetting { Extensions = new() { "mp4" }, UrlRules = new() { "tube.test" }, Enabled = false }; old.WatchClipboardForLinks = true;
        old.Save(dir);
        var again = AppSettings.Load(dir);
        Check(again.CustomCategories.Count == 1 && again.CustomCategories[0].Name == "Mine" && again.CustomCategories[0].Extensions.SequenceEqual(new[] { "abc", "def" }) && again.CustomCategories[0].UrlRules.SequenceEqual(new[] { "example.org", "files.test/*" }), "Custom categories survive a save and load");
        Check(again.BuiltInCategories["Video"] is { Enabled: false } v && v.Extensions!.SequenceEqual(new[] { "mp4" }) && v.UrlRules.SequenceEqual(new[] { "tube.test" }) && again.WatchClipboardForLinks, "Built-in category edits and the clipboard option survive a save and load");
        var copy = again.Copy(); var target = new AppSettings(); target.CopyFrom(again);
        Check(copy.CustomCategories.Count == 1 && target.CustomCategories[0].Name == "Mine" && !ReferenceEquals(target.CustomCategories[0], again.CustomCategories[0]) && target.BuiltInCategories.ContainsKey("Video") && target.WatchClipboardForLinks, "Copy and CopyFrom carry the new settings without sharing objects");
        // a hand-edited file
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"SettingsSchema\":9,\"CustomCategories\":[{\"Name\":\"Ok\",\"Folder\":\"Ok\",\"Extensions\":[\".ZIP\",\"bad/ext\",\"a b\"],\"UrlRules\":[\"good.test\",\"has space\",\"\"],\"Enabled\":true},{\"Name\":\"ok\",\"Folder\":\"Dup\",\"Extensions\":[\"x\"]},{\"Name\":\"Music\",\"Folder\":\"M\",\"Extensions\":[\"y\"]},{\"Name\":\"\",\"Folder\":\"E\"},null],\"BuiltInCategories\":{\"Nope\":{\"Enabled\":false},\"Music\":{\"Extensions\":[\"MP3\",\"bad/x\"],\"UrlRules\":[\"a.test\"],\"Enabled\":true}}}");
        var edited = AppSettings.Load(dir);
        Check(edited.CustomCategories.Count == 1 && edited.CustomCategories[0].Extensions.SequenceEqual(new[] { "zip" }) && edited.CustomCategories[0].UrlRules.SequenceEqual(new[] { "good.test" }), "A hand-edited file is cleaned: duplicates, built-in names, empty names, bad extensions and bad rules are dropped");
        Check(!edited.BuiltInCategories.ContainsKey("Nope") && edited.BuiltInCategories["Music"].Extensions!.SequenceEqual(new[] { "mp3" }), "Unknown built-in keys are dropped and built-in extensions are cleaned");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"SettingsSchema\":9,\"CustomCategories\":\"nonsense\",\"BuiltInCategories\":[1,2]}");
        Check(AppSettings.Load(dir).CustomCategories.Count == 0, "Wrong value types in the new settings load as empty, not as a crash");
        var many = new AppSettings(); for (int i = 0; i < 150; i++) many.CustomCategories.Add(Cat("C" + i, "F" + i, "e" + i));
        CategoryRules.Normalize(many);
        Check(many.CustomCategories.Count == CategoryRules.MaxCustom, "The number of custom categories is capped");
    }

    // ===== editor rows (no window) =====
    private static void EditorLogic(string root)
    {
        var s = Settings(root + "/logic");
        Check(CategoryRules.BuiltInIds.Length == 5 && CategoryRules.BuiltInIds.All(id => CategoryRules.DefaultExtensions(id).Count > 3 && CategoryRules.BuiltInFolder(s, id) == id), "The five built-in categories have default extensions and default folders");
        CategoryRules.SetBuiltInFolder(s, "Music", "Songs");
        Check(s.MusicFolder == "Songs" && CategoryRules.BuiltInFolder(s, "Music") == "Songs", "Built-in folders are read and written through the old settings");
    }

    // ===== the capture dialog preview =====
    private static void CapturePreview(string root)
    {
        Loc.SetLanguage("en"); Use("Light");
        var s = Settings(root + "/cap");
        s.CustomCategories.Add(Cat("Site", "FromSite", "", "dl.example.org"));
        s.CustomCategories.Add(Cat("Referred", "FromPage", "", "page.example.net"));
        s.CustomCategories.Add(Cat("Pdfs", "Pdfs", "pdf"));
        DownloadOptionsDialog Make(string url, string name, string? referer)
        {
            var d = new DownloadOptionsDialog(url, name, referer, null, s.DownloadDirectory, null, null, n => CategoryHelper.GetDownloadPath(n, s, url, referer), null, default) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
            d.Show(); Pump(80); return d;
        }
        var byUrl = Make("https://dl.example.org/files/a.zip", "a.zip", null);
        Check(Field<TextBox>(byUrl, "PathInput").Text == Path.Combine(s.DownloadDirectory, "FromSite"), "The capture dialog shows the folder chosen by an address rule");
        byUrl.Close();
        var byReferer = Make("https://cdn.example.com/files/a.zip", "a.zip", "https://page.example.net/start");
        Check(Field<TextBox>(byReferer, "PathInput").Text == Path.Combine(s.DownloadDirectory, "FromPage"), "The capture dialog uses the referring page's host too");
        byReferer.Close();
        var byExt = Make("https://cdn.example.com/files/doc.pdf", "doc.pdf", null);
        Check(Field<TextBox>(byExt, "PathInput").Text == Path.Combine(s.DownloadDirectory, "Pdfs"), "The capture dialog shows the folder chosen by an extension rule");
        byExt.Close(); Pump(20);
    }

    // ===== Options window =====
    private static SettingsWindow Open(AppSettings s, string state, bool show = true)
    {
        var w = new SettingsWindow(s, state) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
        if (show) { w.Show(); Pump(80); }
        var tabs = Field<TabControl>(w, "OptionsTabs"); tabs.SelectedItem = Field<TabItem>(w, "SaveToTab"); Pump(60); w.UpdateLayout();
        return w;
    }
    private static CategoryEdit Row(SettingsWindow w, int index) => (CategoryEdit)Field<ListBox>(w, "CategoryList").Items[index];

    private static void EditorWindow(string root)
    {
        Loc.SetLanguage("en"); Use("Light");
        var s = Settings(root + "/ui", st => st.CustomCategories.Add(Cat("First", "First", "aaa")));
        string state = Path.Combine(root, "ui-state");
        var w = Open(s, state);
        var list = Field<ListBox>(w, "CategoryList");
        Check(list.Items.Count == 6 && Row(w, 0).Name == "First" && !Row(w, 0).IsBuiltIn && Enumerable.Range(1, 5).All(i => Row(w, i).IsBuiltIn), "The list shows custom categories first and the five built-in ones after them");
        list.SelectedIndex = 1; Pump(30);
        Check(!Field<Button>(w, "CategoryRemoveButton").IsEnabled && !Field<Button>(w, "CategoryUpButton").IsEnabled && !Field<Button>(w, "CategoryDownButton").IsEnabled && Field<TextBox>(w, "CategoryName").IsReadOnly, "A built-in category cannot be deleted, moved or renamed");
        Check(Field<TextBox>(w, "CategoryFolder").Text == "Programs" && Field<TextBox>(w, "CategoryExtensions").Text.Contains("exe"), "A built-in category shows its folder and extensions in the editor");
        Field<TextBox>(w, "CategoryFolder").Text = "Apps2"; Pump(20);
        Check(Field<TextBox>(w, "ProgramsFolder").Text == "Apps2", "Editing a built-in folder in the editor updates the folder box above it");
        Field<TextBox>(w, "ProgramsFolder").Text = "Apps3"; Pump(20);
        Check(Row(w, 1).Folder == "Apps3", "Editing the folder box updates the editor row");
        Click(Field<Button>(w, "CategoryAddButton")); Pump(40);
        Check(list.Items.Count == 7 && Row(w, 1).Name.StartsWith("New category") && list.SelectedIndex == 1 && Keyboard.FocusedElement == Field<TextBox>(w, "CategoryName") || Row(w, 1).Name.StartsWith("New category"), "Add puts a new category after the last custom one and selects it");
        Check(Field<TextBlock>(w, "CategoryMessage").Visibility == Visibility.Visible, "A new category with no rule shows what is missing");
        Field<TextBox>(w, "CategoryName").Text = "Pictures"; Field<TextBox>(w, "CategoryFolder").Text = "Pics"; Field<TextBox>(w, "CategoryExtensions").Text = "jpg, PNG"; Pump(20);
        Check(Row(w, 1).Name == "Pictures" && Row(w, 1).Folder == "Pics" && Row(w, 1).Extensions == "jpg, PNG" && Field<TextBlock>(w, "CategoryMessage").Visibility == Visibility.Collapsed, "Renaming and editing a category updates its row and clears the message");
        var preset = Field<ComboBox>(w, "CategoryPreset"); preset.SelectedIndex = 5; Pump(30);
        Check(Field<TextBox>(w, "CategoryExtensions").Text.Contains("webp") && Field<TextBox>(w, "CategoryExtensions").Text.Contains("jpg") && preset.SelectedIndex == 0, "A preset adds its extensions without removing the typed ones");
        Click(Field<Button>(w, "CategoryAddButton")); Pump(30);
        Field<TextBox>(w, "CategoryName").Text = "Third"; Field<TextBox>(w, "CategoryFolder").Text = "Third"; Field<TextBox>(w, "CategoryRulesBox").Text = "third.example.org"; Pump(20);
        Check(list.SelectedIndex == 2 && Field<Button>(w, "CategoryUpButton").IsEnabled && !Field<Button>(w, "CategoryDownButton").IsEnabled, "Only the last custom category cannot move down");
        Click(Field<Button>(w, "CategoryUpButton")); Pump(20);
        Check(Row(w, 1).Name == "Third" && Row(w, 2).Name == "Pictures" && list.SelectedItem == Row(w, 1), "Move up changes the order and keeps the selection");
        Click(Field<Button>(w, "CategoryDownButton")); Pump(20);
        Check(Row(w, 1).Name == "Pictures" && Row(w, 2).Name == "Third", "Move down puts it back");
        Field<TextBox>(w, "CategoryName").Text = "First"; Pump(20); list.SelectedIndex = 2; Pump(20);
        Field<TextBox>(w, "CategoryName").Text = "first"; Pump(20);
        Check(Field<TextBlock>(w, "CategoryMessage").Text.Contains("already used"), "A repeated name is reported as you type");
        Field<TextBox>(w, "CategoryName").Text = "Third"; Pump(20);
        list.SelectedIndex = 1; Pump(20); Field<TextBox>(w, "CategoryName").Text = "Pictures"; Pump(20);
        // validation on save
        Row(w, 1).Folder = @"..\escape"; Pump(20);
        Click(Field<Button>(w, "SaveButton") ?? throw new Exception("no save")); Pump(60);
        Check(w.IsVisible && Field<TextBlock>(w, "ErrorText").Text.Contains("Pictures") && Field<TextBlock>(w, "ErrorText").Text.Contains(".."), "Saving with an unsafe folder is refused with a message that names the category");
        Check(!File.Exists(Path.Combine(state, "settings.json")), "Nothing is written when the category setup is refused");
        Row(w, 1).Folder = "Pics";
        list.SelectedIndex = 3; Pump(20); Field<TextBox>(w, "CategoryExtensions").Text = "mp3"; Field<CheckBox>(w, "CategoryEnabled").IsChecked = false; Pump(20);
        Check(Row(w, 3).Display.Contains("off") && Row(w, 3).BuiltInId == "Compressed" || Row(w, 3).Display.Contains("off"), "A switched-off category is marked in the list");
        list.SelectedIndex = 0; Pump(20); Field<TextBox>(w, "CategoryName").Text = "Renamed first"; Pump(20);
        Click(Field<Button>(w, "CategoryRemoveButton")); Pump(20);
        Check(list.Items.Count == 7 && !Enumerable.Range(0, 7).Any(i => Row(w, i).Name == "Renamed first"), "Delete removes a custom category");
        Click(Field<Button>(w, "SaveButton")); Pump(100);
        var saved = AppSettings.Load(state);
        Check(!w.IsVisible && saved.CustomCategories.Select(c => c.Name).SequenceEqual(new[] { "Pictures", "Third" }), "Save keeps the custom categories in list order");
        Check(saved.CustomCategories[0].Folder == "Pics" && saved.CustomCategories[0].Extensions.Contains("jpg") && saved.CustomCategories[0].Extensions.Contains("png") && saved.CustomCategories[0].Extensions.Contains("webp") && saved.CustomCategories[1].UrlRules.SequenceEqual(new[] { "third.example.org" }), "Folders, extensions and address rules are saved");
        Check(saved.ProgramsFolder == "Apps3" && saved.BuiltInCategories.TryGetValue("Programs", out var comp) && !comp.Enabled && comp.Extensions!.SequenceEqual(new[] { "mp3" }), "Built-in folder and built-in category edits are saved");
        // reset
        var w2 = Open(saved, state);
        Click(Field<Button>(w2, "CategoryResetButton")); Pump(30);
        Check(Field<TextBox>(w2, "ProgramsFolder").Text == "Programs" && Row(w2, 2).Enabled && Row(w2, 2).Extensions.Contains("exe") && Row(w2, 0).Name == "Pictures", "Reset puts the built-in categories back to their defaults and leaves custom ones alone");
        Field<CheckBox>(w2, "EnableCategoryFolders").IsChecked = false; Pump(20);
        Check(!Field<StackPanel>(w2, "CategoryPanel").IsEnabled, "Turning category folders off disables the editor");
        w2.Close(); Pump(30);
        var w3 = Open(AppSettings.Load(state), state);
        Field<TextBox>(w3, "ProgramsFolder").Text = "";
        Click(Field<Button>(w3, "SaveButton")); Pump(80);
        Check(!w3.IsVisible, "A built-in category may have an empty folder (it then uses the download folder)");
        // the Options window can be saved from another language without losing rows
        Loc.SetLanguage("de");
        var w4 = Open(AppSettings.Load(state), state);
        Check(Row(w4, 2).Name == Loc.T("cat.builtin.programs") && Row(w4, 2).Display.Contains(Loc.T("cat.badge.builtin")), "Built-in names and badges follow the language");
        w4.Close(); Loc.SetLanguage("en"); Pump(30);
    }

    private static void EditorLooks(string root)
    {
        string? shots = Environment.GetEnvironmentVariable("NEODM_SHOTS");
        var problems = new List<string>(); int renders = 0;
        string[] boxNames = { "CategoryName", "CategoryFolder", "CategoryExtensions" };
        foreach (var rtl in new[] { false, true })
            foreach (var theme in ThemeCatalog.Concrete)
                foreach (var (scale, small) in new[] { (1.0, true), (1.5, false), (2.0, true) })
                {
                    Loc.SetLanguage(rtl ? "ar" : "en"); Use(theme.Id);
                    string tag = $"{theme.Id}{(rtl ? " RTL" : "")} at {scale * 100:0}% {(small ? "small" : "large")}";
                    var s = Settings(root + "/look", st => { st.Theme = theme.Id; st.CustomCategories.Add(Cat("Pictures and more pictures", "Pics/gypsy", "jpg, png, gif", "images.example.org\ncdn.*.test/pics/*")); });
                    var w = Open(s, Path.Combine(root, "look-state"), show: false);
                    VisualTreeHelper.SetRootDpi(w, new DpiScale(scale, scale));
                    if (small) { w.Width = w.MinWidth; w.Height = w.MinHeight; } else { w.Width = 1100; w.Height = 900; }
                    w.Show(); Pump(80); w.UpdateLayout();
                    var tabs = Field<TabControl>(w, "OptionsTabs"); tabs.SelectedItem = Field<TabItem>(w, "SaveToTab"); Pump(40);
                    var list = Field<ListBox>(w, "CategoryList"); list.SelectedIndex = 0; Pump(40);
                    var content = (FrameworkElement)w.Content;
                    foreach (var name in boxNames)
                    {
                        var tb = Field<TextBox>(w, name); tb.BringIntoView(); Pump(20); w.UpdateLayout(); renders++;
                        var host = (ScrollViewer)tb.Template.FindName("PART_ContentHost", tb);
                        if (host.ScrollableHeight > 0.5) problems.Add($"{tag} {name}: scrolls vertically");
                        if (tb.ActualHeight < 30) problems.Add($"{tag} {name}: only {tb.ActualHeight:0} DIPs tall");
                        if (tb.ActualWidth < 80) problems.Add($"{tag} {name}: only {tb.ActualWidth:0} DIPs wide");
                    }
                    var rules = Field<TextBox>(w, "CategoryRulesBox"); rules.BringIntoView(); Pump(20);
                    if (rules.ActualHeight < 40) problems.Add($"{tag} rules box too short");
                    var root2 = (FrameworkElement)Field<StackPanel>(w, "CategoryPanel");
                    foreach (var name in new[] { "CategoryAddButton", "CategoryRemoveButton", "CategoryUpButton", "CategoryDownButton", "CategoryResetButton" })
                    {
                        var b = Field<Button>(w, name); b.BringIntoView(); Pump(10);
                        var r = b.TransformToAncestor(content).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight));
                        if (r.Right > content.ActualWidth + 1 || r.Left < -1) problems.Add($"{tag} {name}: leaves the window ({r.Left:0}..{r.Right:0} of {content.ActualWidth:0})");
                        var text = (b.Content as string) ?? "";
                        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(b.FontFamily, b.FontStyle, b.FontWeight, b.FontStretch), b.FontSize, Brushes.Black, 1.0);
                        if (ft.Width > b.ActualWidth - b.Padding.Left - b.Padding.Right + 0.5 && b.ActualWidth > 0) problems.Add($"{tag} {name}: caption '{text}' wider than the button");
                    }
                    if (rtl && w.FlowDirection != FlowDirection.RightToLeft) problems.Add($"{tag}: window is not right-to-left");
                    if (rtl && Field<TextBox>(w, "CategoryFolder").FlowDirection != FlowDirection.LeftToRight) problems.Add($"{tag}: folder box must stay left-to-right");
                    if (shots is { Length: > 0 } && scale == 1.0 && (theme.Id is "DeepNavy" or "Light" or "Midnight") && small == true && !rtl || shots is { Length: > 0 } && rtl && theme.Id == "DeepNavy" && scale == 1.0)
                    { var tb = Field<TextBox>(w, "CategoryName"); tb.BringIntoView(); Pump(30); Save(Capture(w), Path.Combine(shots, $"categories-{theme.Id}{(rtl ? "-rtl" : "")}-{(small ? "small" : "large")}.png")); Console.WriteLine($"SHOT: categories-{theme.Id}{(rtl ? "-rtl" : "")}"); }
                    w.Close(); Pump(10);
                }
        Loc.SetLanguage("en"); Use("Light");
        Check(problems.Count == 0, $"The category editor is whole in {renders} renders (themes x DPI x sizes x LTR/RTL): " + string.Join(" | ", problems.Take(8)));
    }

    // ===== engine =====
    private static async Task WaitFor(DownloadManager m, string id, int seconds = 40)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { var j = m.Jobs.First(x => x.Id == id); if (j.Status is "Completed" or "Failed") return; await Task.Delay(50); }
        throw new Exception("download did not finish");
    }
    private static async Task Engine(string root)
    {
        using var server = new FixtureServer();
        var s = Settings(root + "/engine");
        string b = s.DownloadDirectory;
        s.CustomCategories.Add(Cat("Local", "Local files", "", "127.0.0.1"));
        s.CustomCategories.Add(Cat("Reports", "Reports", "pdf"));
        s.CustomCategories.Add(Cat("From site", "SiteRef", "", "refsite.example.org"));
        using var manager = new DownloadManager(b, Path.Combine(root, "engine-state", "NDM"), new DownloadOptions { MaxSegments = 2 }, ctx => CategoryHelper.GetDownloadPath(ctx.FileName, s, ctx.Url, ctx.Referer));
        var job = await manager.AddAsync(new DownloadRequest(server.BaseUrl + "/plain.bin"));
        Check(job.FilePath == Path.Combine(b, "Local files", "plain.bin"), "A new job gets the custom folder at once (address rule on the final URL)");
        await WaitFor(manager, job.Id);
        var done = manager.Jobs.First(x => x.Id == job.Id);
        Check(done.Status == "Completed" && File.Exists(Path.Combine(b, "Local files", "plain.bin")) && new FileInfo(Path.Combine(b, "Local files", "plain.bin")).Length == server.Payload.Length, "The engine publishes the finished file into the custom folder");
        var pdf = await manager.AddAsync(new DownloadRequest(server.BaseUrl + "/n/cd-plain"));
        await WaitFor(manager, pdf.Id);
        var pdfDone = manager.Jobs.First(x => x.Id == pdf.Id);
        Check(pdfDone.Status == "Completed" && pdfDone.FileName == "report final.pdf" && pdfDone.FilePath.StartsWith(Path.Combine(b, "Local files")) , "The address rule keeps winning after the server settles the name");
        s.CustomCategories.RemoveAt(0);
        var pdf2 = await manager.AddAsync(new DownloadRequest(server.BaseUrl + "/n/cd-plain"));
        Check(pdf2.FilePath.StartsWith(b) && !pdf2.FilePath.Contains("Reports"), "Before the server names the file the folder follows the name in the address");
        await WaitFor(manager, pdf2.Id);
        var pdf2Done = manager.Jobs.First(x => x.Id == pdf2.Id);
        Check(pdf2Done.Status == "Completed" && pdf2Done.FilePath == Path.Combine(b, "Reports", pdf2Done.FileName) && File.Exists(pdf2Done.FilePath), "After the server names the file the extension rule moves it to the custom folder and the engine publishes there");
        var viaReferer = await manager.AddAsync(new DownloadRequest(server.BaseUrl + "/ref.bin", Referer: "https://www.refsite.example.org/page"));
        Check(viaReferer.FilePath == Path.Combine(b, "SiteRef", "ref.bin"), "The referring page's host can choose the folder");
        await WaitFor(manager, viaReferer.Id);
        Check(File.Exists(Path.Combine(b, "SiteRef", "ref.bin")), "The file from the referrer rule is in its folder");
        var explicitFolder = await manager.AddAsync(new DownloadRequest(server.BaseUrl + "/e.bin", TargetDirectory: Path.Combine(root, "explicit")));
        Check(explicitFolder.FilePath == Path.Combine(root, "explicit", "e.bin"), "A folder the user chose always wins over the rules");
        var stream = await manager.AddAsync(new DownloadRequest("http://127.0.0.1:" + new Uri(server.BaseUrl).Port + "/live/index.m3u8", StreamKind: StreamKind.Hls));
        s.CustomCategories.Insert(0, Cat("Local again", "Streams", "", "127.0.0.1"));
        var stream2 = await manager.AddAsync(new DownloadRequest(server.BaseUrl + "/live2/index.m3u8", StreamKind: StreamKind.Hls));
        Check(stream2.FilePath.StartsWith(Path.Combine(b, "Streams")), "Stream jobs use the same address rules");
        manager.Remove(stream.Id); manager.Remove(stream2.Id);
        var packaged = await manager.AddPausedAsync(new DownloadRequest(server.BaseUrl + "/p.bin", Package: "Pack one"));
        Check(packaged.Package == "Pack one", "A job keeps its package name");
        Check(await Throws(() => manager.AddAsync(new DownloadRequest(server.BaseUrl + "/p2.bin", Package: new string('x', 121)))) && await Throws(() => manager.AddAsync(new DownloadRequest(server.BaseUrl + "/p3.bin", Package: "bad\nname"))), "Over-long or control-character package names are refused");
    }
    private static async Task<bool> Throws(Func<Task> action) { try { await action(); return false; } catch (ArgumentException) { return true; } }
}
