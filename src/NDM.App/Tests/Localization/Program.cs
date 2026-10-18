using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;

internal sealed class LocalizationTestApp : NDM.App.App
{
    public LocalizationTestApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
    protected override void OnStartup(StartupEventArgs e) { }
}

/// <summary>1.5.0 localisation: catalog and fallback, plural rules, formatting, locale file completeness and placeholder parity, the hard-coded-string guard,
/// live switching, RTL flip, pseudo-locale and Arabic layout of every window, and the Arabic screenshots (NEODM_SHOTS).</summary>
internal static partial class LocalizationTests
{
    private static int passed;
    internal static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    internal static void Pump(int ms = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }

    internal static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Directory.Build.props"))) dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
        return dir ?? throw new Exception("repository root not found");
    }
    internal static string AppDir => Path.Combine(RepoRoot(), "src", "NDM.App");
    internal static string LocalesDir => Path.Combine(AppDir, "Locales");

    internal static Dictionary<string, string> ReadLocale(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();

    [STAThread]
    private static int Main()
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-LocTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("NEODM_LANG", null); Environment.SetEnvironmentVariable("NEODM_PSEUDO", null); // this suite drives the language itself
        var app = new LocalizationTestApp();
        try
        {
            Catalog(); Fallback(); Plurals(); Formatting(); Isolates(); Pseudo(); SettingsAndOs(scratch);
            LocaleFiles(); Guard();
            LiveSwitching(scratch); PopupsFollowWindow(); RtlFlip(scratch);
            LayoutSweep(scratch);
            Screenshots(scratch);
            Console.WriteLine($"Localization tests passed: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            Loc.LoaderOverride = null; LanguageCatalog.InstalledSource = () => new HashSet<string>(); Loc.SetLanguage("en");
            foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close();
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            app.Shutdown();
        }
    }

    // ===== catalog =====
    private static void Catalog()
    {
        string[] shipped = { "en", "ar", "es", "fr", "de", "pt-BR", "ru", "zh-Hans", "ja", "tr", "hi", "it", "ko", "id", "nl", "pl", "fa" };
        Check(shipped.All(t => LanguageCatalog.Find(t) is not null), "catalog knows every planned language tag");
        var installed = LanguageCatalog.Installed().Select(l => l.Tag).ToArray();
        var files = Directory.GetFiles(LocalesDir, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(x => x).ToArray();
        Check(installed.OrderBy(x => x).SequenceEqual(files!), $"installed languages are exactly the locale files ({string.Join(", ", installed)})");
        Check(installed.Contains("en") && installed.Contains("ar"), "English and Arabic are installed");
        Check(LanguageCatalog.Installed()[0].Tag == "en", "English is listed first");
        var ar = LanguageCatalog.Find("ar")!;
        Check(ar.IsRtl && ar.NativeName == "العربية" && ar.EnglishName == "Arabic" && ar.Fonts.StartsWith("Segoe UI") && ar.Fonts.Contains("Tahoma"), "Arabic: RTL, native name, Segoe UI then Tahoma/Arial chain");
        Check(LanguageCatalog.Find("fa")!.IsRtl && LanguageCatalog.Find("he")!.IsRtl, "Persian and Hebrew are RTL");
        Check(shipped.Where(t => t is not ("ar" or "fa")).All(t => !LanguageCatalog.Find(t)!.IsRtl), "no other planned language is RTL");
        Check(!LanguageCatalog.Find("en")!.AiTranslated, "English is not flagged AI translated");
        Check(LanguageCatalog.Find("zh-Hans")!.Fonts.StartsWith("Microsoft YaHei UI") && LanguageCatalog.Find("ja")!.Fonts.StartsWith("Yu Gothic UI") && LanguageCatalog.Find("ko")!.Fonts.StartsWith("Malgun Gothic") && LanguageCatalog.Find("hi")!.Fonts.StartsWith("Nirmala UI"), "CJK and Hindi font chains");

        // A language without a file is hidden; adding the file makes it appear (the translator workflow).
        var saved = LanguageCatalog.InstalledSource;
        try
        {
            LanguageCatalog.InstalledSource = () => new HashSet<string> { "en", "ar", "fr" };
            Check(LanguageCatalog.Installed().Select(l => l.Tag).SequenceEqual(new[] { "en", "ar", "fr" }), "a language appears as soon as its file exists");
            LanguageCatalog.InstalledSource = () => new HashSet<string> { "en" };
            Check(LanguageCatalog.Installed().Count == 1 && LanguageCatalog.Resolve("ar").Tag == "en", "a language whose file is missing is hidden and resolves to English");
        }
        finally { LanguageCatalog.InstalledSource = saved; }
    }

    // ===== per-key fallback =====
    private static void Fallback()
    {
        var savedSource = LanguageCatalog.InstalledSource;
        try
        {
            LanguageCatalog.InstalledSource = () => new HashSet<string> { "en", "ar", "fr" };
            Loc.LoaderOverride = tag => tag == "fr" ? new Dictionary<string, string> { ["main.menu.file"] = "Fichier", ["main.menu.help"] = "" } : null;
            Check(Loc.SetLanguage("fr"), "a language with a partial file can be selected");
            Check(Loc.T("main.menu.file") == "Fichier", "present key is translated");
            Check(Loc.T("main.menu.view") == Loc.EnglishKeys.Where(k => k == "main.menu.view").Select(_ => LocalizationTests.English("main.menu.view")).First(), "missing key falls back to English");
            Check(Loc.T("main.menu.help") == English("main.menu.help"), "empty value falls back to English");
            Check(Loc.T("does.not.exist") == "does.not.exist", "a key that exists nowhere returns its key text and does not throw");
            Check(Loc.T("about.version", "1.5.0") == English("about.version").Replace("{0}", "1.5.0"), "fallback strings are still formatted");
            Check(Loc.Plural("main.count_downloads", 3) == English("main.count_downloads_other").Replace("{0}", "3"), "missing plural group falls back to the English forms");
            Check(Loc.T("about.version", "a", "b") is { Length: > 0 }, "extra arguments do not throw");
            Check(Loc.T("about.version") == English("about.version"), "missing arguments do not throw");
            Loc.LoaderOverride = tag => tag == "fr" ? throw new InvalidOperationException() : null;
            Check(Loc.SetLanguage("fr") && Loc.T("main.menu.file") == English("main.menu.file"), "a damaged file shows English and does not throw");
        }
        finally { Loc.LoaderOverride = null; LanguageCatalog.InstalledSource = savedSource; Loc.SetLanguage("en"); }
    }

    internal static string English(string key) => LocaleCache.English[key];
    internal static class LocaleCache { public static readonly Dictionary<string, string> English = ReadLocale(Path.Combine(LocalesDir, "en.json")); }

    // ===== plural rules =====
    private static void Plurals()
    {
        int[] n = { 0, 1, 2, 3, 10, 11, 99, 100, 101, 102 };
        string Row(string tag) => string.Join(",", n.Select(x => PluralRules.Category(tag, x)));
        Check(Row("en") == "other,one,other,other,other,other,other,other,other,other", "plural en: 0,1,2,3,10,11,99,100,101,102");
        Check(Row("ar") == "zero,one,two,few,few,many,many,other,other,other", $"plural ar: all six categories ({Row("ar")})");
        Check(Row("es") == Row("en") && Row("de") == Row("en") && Row("it") == Row("en") && Row("nl") == Row("en") && Row("tr") == Row("en"), "plural es/de/it/nl/tr follow one/other");
        Check(Row("fr") == "one,one,other,other,other,other,other,other,other,other" && Row("pt-BR") == Row("fr"), "plural fr/pt: 0 and 1 are one");
        Check(Row("ru") == "many,one,few,few,many,many,many,many,one,few" && PluralRules.Category("ru", 21) == "one" && PluralRules.Category("ru", 111) == "many" && PluralRules.Category("ru", 24) == "few", $"plural ru ({Row("ru")})");
        Check(Row("pl") == "many,one,few,few,many,many,many,many,many,few" && PluralRules.Category("pl", 21) == "many" && PluralRules.Category("pl", 22) == "few", $"plural pl ({Row("pl")})");
        foreach (var tag in new[] { "zh-Hans", "ja", "ko", "id" }) Check(n.All(x => PluralRules.Category(tag, x) == "other"), $"plural {tag}: always other");
        Check(Row("hi") == Row("fr") && Row("fa") == Row("fr"), "plural hi/fa: 0 and 1 are one");
        Check(PluralRules.Category("xx", 1) == "one" && PluralRules.Category("ar", -2) == "two", "unknown language uses the English rule; the sign is ignored");

        // Lookup through Loc.Plural in Arabic: every category has its own sentence.
        Loc.SetLanguage("ar");
        try
        {
            var forms = n.Select(x => Loc.Plural("main.count_downloads", x)).ToArray();
            Check(forms[0].Contains("لا توجد") && forms[1].Contains("واحد") && forms[2].Contains("تنزيلان") && forms[3].Contains("تنزيلات") && forms[5].Contains("تنزيلًا") && forms[7].Contains("تنزيل") && !forms[7].Contains("تنزيلًا"), $"Arabic Loc.Plural picks zero/one/two/few/many/other ({string.Join(" | ", forms)})");
        }
        finally { Loc.SetLanguage("en"); }
        Check(Loc.Plural("main.count_downloads", 1) == "1 download" && Loc.Plural("main.count_downloads", 5) == "5 downloads", "English Loc.Plural");
    }

    // ===== formatting =====
    private static void Formatting()
    {
        Loc.SetLanguage("ar");
        try
        {
            Check(Loc.Culture.NumberFormat.NativeDigits.SequenceEqual("0123456789".Select(c => c.ToString())) && Loc.Culture.NumberFormat.DigitSubstitution == DigitShapes.None, "Arabic culture uses Western digits");
            string size = Loc.Size(1536 * 1024), number = Loc.Number(1234567L), date = Loc.DateTime(new DateTimeOffset(2026, 10, 19, 14, 5, 0, TimeSpan.Zero), "yyyy-MM-dd");
            Check(size == Loc.Wrap("1.5 MB"), $"Arabic size is wrapped in left-to-right marks: {size}");
            Check(number == "1,234,567", $"Arabic number: {number}");
            Check(date == "2026-10-19", $"Arabic date keeps Gregorian ASCII digits ({date})");
            Check(Regex.IsMatch(Loc.Percent(0.256, 1), "^[0-9.]+%$"), $"Arabic percent has Western digits ({Loc.Percent(0.256, 1)})");
            Check(Loc.Speed(2048) == Loc.Wrap("2 KB/s"), $"Arabic speed is one left-to-right unit ({Loc.Speed(2048)})");
            Check(!Regex.IsMatch(Loc.T("main.summary.active", 12), "[\u0660-\u0669\u06F0-\u06F9]") && Loc.T("main.summary.active", 12).Contains("12"), "Arabic strings contain no Arabic-Indic digits");
            Check(Loc.Duration(TimeSpan.FromSeconds(125)).Contains("2") && Loc.Duration(TimeSpan.FromSeconds(125)).Contains("5"), $"Arabic duration ({Loc.Duration(TimeSpan.FromSeconds(125))})");
        }
        finally { Loc.SetLanguage("en"); }
        Check(Loc.Size(1536 * 1024) == "1.5 MB" && Loc.Number(1234567L) == "1,234,567", "English formatting");
        var de = LanguageCatalog.InstalledSource; LanguageCatalog.InstalledSource = () => new HashSet<string> { "en", "de" };
        try { Loc.LoaderOverride = _ => new Dictionary<string, string>(); Loc.SetLanguage("de"); Check(Loc.Size(1536 * 1024) == "1,5 MB" && Loc.Number(1234567L) == "1.234.567", $"German separators ({Loc.Size(1536 * 1024)}, {Loc.Number(1234567L)})"); }
        finally { Loc.LoaderOverride = null; LanguageCatalog.InstalledSource = de; Loc.SetLanguage("en"); }
    }

    // ===== direction marks (WPF ignores the isolate characters, see Loc.Lrm) =====
    private static void Isolates()
    {
        Loc.SetLanguage("ar");
        try
        {
            string path = @"C:\Users\Omar\Downloads\file.zip";
            string text = Loc.T("props.downloaded_value", "1.5 MB", "40%");
            Check(text.Contains(Loc.Wrap("1.5 MB")) && text.Contains(Loc.Wrap("40%")), "RTL: arguments are wrapped in left-to-right marks");
            string moved = Loc.T("progress.location.changed", path);
            Check(moved.Contains(Loc.Wrap(path)), "RTL: a path argument is marked");
            Check(Loc.T("main.notice.bulk_paused", 3, 1, 0).Split('\u200E').Length >= 7, "RTL: every number argument is marked");
            Check(Loc.Iso("a.zip") == Loc.Wrap("a.zip") && Loc.Iso("") == "", "Loc.Iso wraps non-empty text");
            Check(!ReadLocale(Path.Combine(LocalesDir, "ar.json")).Values.Any(v => v.IndexOfAny(new[] { '\u2066', '\u2067', '\u2068', '\u2069' }) >= 0), "ar.json contains no isolate characters (WPF draws gaps for them)");
        }
        finally { Loc.SetLanguage("en"); }
        Check(Loc.T("props.downloaded_value", "1.5 MB", "40%") == "1.5 MB (40%)" && Loc.Iso("a.zip") == "a.zip", "LTR: no marks are added in English");
    }

    // ===== pseudo-locale =====
    private static void Pseudo()
    {
        string sample = PseudoLocale.Transform("Download {0} files");
        Check(sample.StartsWith("[") && sample.EndsWith("]") && sample.Contains("{0}") && sample.Length >= "Download {0} files".Length * 1.3, $"pseudo-locale brackets, keeps placeholders, about 30% longer ({sample})");
        Check(!Regex.IsMatch(PseudoLocale.Transform("Hello"), "[A-Za-z]{3}"), "pseudo-locale replaces Latin letters with accented ones");
        Check(LanguageCatalog.Find(LanguageCatalog.Pseudo) is not null && LanguageCatalog.Installed().All(l => l.Tag != LanguageCatalog.Pseudo), "pseudo-locale exists but is never listed");
        Loc.SetLanguage(LanguageCatalog.Pseudo);
        try { Check(Loc.T("main.menu.file").StartsWith("[") && Loc.T("about.version", "1.5.0").Contains("1.5.0"), "Loc serves pseudo text for every key"); }
        finally { Loc.SetLanguage("en"); }
    }

    // ===== settings and OS language =====
    private static void SettingsAndOs(string scratch)
    {
        Check(LanguageCatalog.NormalizeSetting(null) == "system" && LanguageCatalog.NormalizeSetting("") == "system" && LanguageCatalog.NormalizeSetting("klingon") == "system" && LanguageCatalog.NormalizeSetting("AR") == "ar" && LanguageCatalog.NormalizeSetting("pt-br") == "pt-BR" && LanguageCatalog.NormalizeSetting("SYSTEM") == "system", "setting validation: unknown -> system, case-insensitive tags");
        Check(new AppSettings().Language == "system", "default Language is system");
        string dir = Path.Combine(scratch, "settings"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Theme\":\"Dark\",\"SettingsSchema\":4}");
        var old = AppSettings.Load(dir);
        Check(old.Language == "system" && old.Theme == "Dark", "an old settings file without Language loads as system");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Language\":\"xx-YY\"}");
        Check(AppSettings.Load(dir).Language == "system", "an unknown stored language loads as system");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Language\":\"ar\"}");
        var ar = AppSettings.Load(dir); Check(ar.Language == "ar", "a stored language is kept");
        ar.Save(dir); Check(AppSettings.Load(dir).Language == "ar" && File.ReadAllText(Path.Combine(dir, "settings.json")).Contains("\"Language\""), "Language round-trips through Save and Load");
        Check(ar.Copy().Language == "ar", "Copy copies Language");

        (string Os, string? Tag)[] table =
        {
            ("ar-SA", "ar"), ("ar-EG", "ar"), ("ar", "ar"), ("pt-PT", "pt-BR"), ("pt-BR", "pt-BR"), ("pt", "pt-BR"), ("zh-CN", "zh-Hans"), ("zh-Hans-CN", "zh-Hans"), ("zh-SG", "zh-Hans"),
            ("zh-TW", null), ("zh-Hant", null), ("zh-HK", null), ("en-GB", "en"), ("fr-CA", "fr"), ("de-AT", "de"), ("es-MX", "es"), ("ru-RU", "ru"), ("ja-JP", "ja"), ("ko-KR", "ko"),
            ("tr-TR", "tr"), ("hi-IN", "hi"), ("it-IT", "it"), ("id-ID", "id"), ("nl-BE", "nl"), ("pl-PL", "pl"), ("fa-IR", "fa"), ("he-IL", "he"), ("sv-SE", null), ("", null),
        };
        foreach (var (os, tag) in table) Check(LanguageCatalog.MapOsLanguage(os) == tag, $"OS language {(os.Length == 0 ? "(empty)" : os)} maps to {tag ?? "nothing"}");
        var provider = LanguageCatalog.OsLanguageProvider;
        try
        {
            LanguageCatalog.OsLanguageProvider = () => "ar-SA";
            Check(LanguageCatalog.Resolve("system").Tag == "ar" && Loc.Apply("system").Tag == "ar" && Loc.Language == "ar" && Loc.IsRtl, "first start: system + Arabic Windows -> Arabic with RTL");
            LanguageCatalog.OsLanguageProvider = () => "it-IT";
            Check(LanguageCatalog.Resolve("system").Tag == "en", "system + Italian Windows without an Italian file -> English");
            LanguageCatalog.OsLanguageProvider = () => throw new InvalidOperationException();
            Check(LanguageCatalog.Resolve("system").Tag == "en", "a failing OS language provider falls back to English");
            LanguageCatalog.OsLanguageProvider = () => "ar-SA";
            Check(LanguageCatalog.Resolve("en").Tag == "en" && LanguageCatalog.Resolve("xx").Tag == "ar", "an explicit tag wins over the OS language; an unknown tag behaves like system");
        }
        finally { LanguageCatalog.OsLanguageProvider = provider; Loc.SetLanguage("en"); }
    }
}
