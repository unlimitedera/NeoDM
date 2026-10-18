using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace NDM.App.Localization;

/// <summary>Binding source for XAML: <c>Loc.Source["main.menu.tasks"]</c>. Changing the language raises PropertyChanged("Item[]"), so every bound string updates at once.</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public string this[string key] => Loc.Lookup(key);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// <summary>Localisation entry point. Strings live in Locales/&lt;tag&gt;.json (embedded). A key missing from a language falls back to English; a key missing everywhere returns the key text (a bug that the completeness test catches).</summary>
public static class Loc
{
    public const string ResourcePrefix = LanguageCatalog.ResourcePrefix;
    /// <summary>Direction marks. Inserted values (file names, paths, URLs, numbers, sizes) are wrapped in a left-to-right mark on both sides inside right-to-left sentences.
    /// WPF's text engine ignores the newer isolate characters (U+2066 to U+2069) and draws gaps instead, so the mark is the form that renders correctly.</summary>
    public const char Lrm = '\u200E', Rlm = '\u200F', Lre = '\u202A', Pdf = '\u202C';

    /// <summary>Wraps a run so it reads left to right inside right-to-left text: an embedding (U+202A to U+202C) around the run with a left-to-right mark inside each end. Tested in WPF: marks alone leave "0 B" reversed and the brackets of "(40%)" mirrored; the embedding alone leaves the run's last words scrambled; both together read correctly.</summary>
    public static string Wrap(string text) => Lre.ToString() + Lrm + text + Lrm + Pdf;
    private static bool IsWrapped(string text) => text.Length > 3 && text[0] == Lre && text[1] == Lrm && text[^2] == Lrm && text[^1] == Pdf;
    /// <summary>Removes the direction characters again, for plain-text targets such as the tray tooltip.</summary>
    public static string Strip(string? text) => text is null ? "" : text.Replace(Lre.ToString(), "").Replace(Pdf.ToString(), "").Replace(Lrm.ToString(), "");

    private sealed class State
    {
        public required LanguageInfo Info { get; init; }
        public required IReadOnlyDictionary<string, string> Strings { get; init; }
        public required CultureInfo Culture { get; init; }
        public bool Pseudo { get; init; }
        public required IsolatingFormatter Formatter { get; init; }
    }

    private static readonly object Gate = new();
    private static readonly Lazy<IReadOnlyDictionary<string, string>> EnglishStrings = new(() => LoadFile("en"));
    private static volatile State _state = Build(LanguageCatalog.Find(InitialTag())!);
    public static LocSource Source { get; } = new();

    /// <summary>Raised after the active language changed (UI thread when called from the UI).</summary>
    public static event Action? LanguageChanged;

    /// <summary>NEODM_PSEUDO=1 selects the pseudo language, NEODM_LANG forces a tag (tests set en). Otherwise English until the app applies the saved setting.</summary>
    private static string InitialTag()
    {
        if (PseudoLocale.Requested) return LanguageCatalog.Pseudo;
        var forced = Environment.GetEnvironmentVariable("NEODM_LANG");
        return LanguageCatalog.Find(forced)?.Tag ?? LanguageCatalog.English;
    }

    /// <summary>The language forced by NEODM_LANG or NEODM_PSEUDO, if any. The saved setting does not override it.</summary>
    public static LanguageInfo? ForcedLanguage
    {
        get
        {
            if (PseudoLocale.Requested) return LanguageCatalog.Find(LanguageCatalog.Pseudo);
            var forced = Environment.GetEnvironmentVariable("NEODM_LANG");
            return string.IsNullOrWhiteSpace(forced) ? null : LanguageCatalog.Find(forced);
        }
    }

    public static string Language => _state.Info.Tag;
    public static LanguageInfo Info => _state.Info;
    public static bool IsRtl => _state.Info.IsRtl;
    public static CultureInfo Culture => _state.Culture;
    public static System.Windows.FlowDirection FlowDirection => IsRtl ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;

    /// <summary>Switches the language. An unknown tag or a tag without a locale file is ignored (returns false).</summary>
    public static bool SetLanguage(string? tag)
    {
        var info = LanguageCatalog.Find(tag);
        if (info is null) return false;
        if (info.Tag != LanguageCatalog.Pseudo && !LanguageCatalog.Installed().Any(l => l.Tag == info.Tag)) return false;
        lock (Gate) { _state = Build(info); }
        Source.Raise();
        LanguageChanged?.Invoke();
        return true;
    }

    /// <summary>Applies a settings value ("system" or a tag), unless the environment forces a language.</summary>
    public static LanguageInfo Apply(string? setting)
    {
        var info = ForcedLanguage ?? LanguageCatalog.Resolve(setting);
        if (info.Tag != Language) SetLanguage(info.Tag);
        return info;
    }

    // ---- lookup ----
    public static string Lookup(string key)
    {
        var s = _state;
        if (s.Strings.TryGetValue(key, out var v) && v.Length > 0) return v;
        if (EnglishStrings.Value.TryGetValue(key, out var en) && en.Length > 0) return s.Pseudo ? PseudoLocale.Transform(en) : en;
        return key;
    }

    public static bool Has(string key) => EnglishStrings.Value.ContainsKey(key);
    /// <summary>All English keys (the reference set).</summary>
    public static IEnumerable<string> EnglishKeys => EnglishStrings.Value.Keys;

    public static string T(string key) => Lookup(key);

    public static string T(string key, params object?[] args)
    {
        var s = _state;
        return Format(s, Lookup(key), args);
    }

    /// <summary>Plural lookup: tries key_&lt;category&gt;, then key_other. Without args the count is {0}.</summary>
    public static string Plural(string key, long n, params object?[] args)
    {
        var s = _state;
        string category = PluralRules.Category(s.Info.Plural, n);
        string? pattern = Pick(s.Strings, key, category);
        if (pattern is null)
        {
            // Not in this language at all: use the English forms with the English rule.
            pattern = Pick(EnglishStrings.Value, key, PluralRules.Category("en", n)) ?? key;
            if (s.Pseudo) pattern = PseudoLocale.Transform(pattern);
        }
        return Format(s, pattern, args.Length == 0 ? new object?[] { n } : args);
    }

    private static string? Pick(IReadOnlyDictionary<string, string> strings, string key, string category)
    {
        if (strings.TryGetValue(key + "_" + category, out var v) && v.Length > 0) return v;
        if (strings.TryGetValue(key + "_other", out v) && v.Length > 0) return v;
        return null;
    }

    private static string Format(State s, string pattern, object?[] args)
    {
        if (args.Length == 0) return pattern;
        try { return string.Format(s.Formatter, pattern, args); }
        catch (FormatException) { return pattern; }
    }

    // ---- RTL helpers ----
    /// <summary>Wraps text of unknown direction in left-to-right marks when the active language is right-to-left; otherwise returns it unchanged.</summary>
    public static string Iso(string? text) => IsRtl && !string.IsNullOrEmpty(text) ? Wrap(text) : text ?? "";

    // ---- culture-correct formatting (Western digits 0-9 in every language, see docs/TRANSLATING.md) ----
    // These return plain text. Loc.T / Loc.Plural mark each argument in right-to-left languages, so a size or number placed in a sentence keeps its direction.
    public static string Number(long value) => value.ToString("N0", Culture);
    public static string Number(double value, int decimals = 1) => value.ToString("N" + decimals, Culture);
    public static string Percent(double fraction, int decimals = 0) => (fraction * 100).ToString("N" + decimals, Culture) + "%";
    public static string DateTime(DateTimeOffset value, string format = "g") => value.LocalDateTime.ToString(format, Culture);

    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB" };
    /// <summary>"1.5 MB" with the active culture's decimal separator. Units stay Latin symbols in every language.</summary>
    public static string Size(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0) bytes = 0;
        int unit = 0;
        while (bytes >= 1024 && unit < SizeUnits.Length - 1) { bytes /= 1024; unit++; }
        return Ltr(bytes.ToString("0.#", Culture) + " " + SizeUnits[unit]);
    }

    public static string Speed(double bytesPerSecond) => Ltr(Plain(Size(bytesPerSecond)) + "/s");

    /// <summary>Keeps a number with a Latin unit ("1.5 MB", "20 KB/s") in reading order inside right-to-left text: left-to-right marks around it. No change in left-to-right languages.</summary>
    public static string Ltr(string text) => IsRtl && text.Length > 0 ? Wrap(text) : text;
    private static string Plain(string text) => Strip(text);

    /// <summary>Elapsed or remaining time in the two largest units: "2h 5m", "3m 4s", "5s" (unit letters are localised).</summary>
    public static string Duration(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        if (time.TotalDays >= 1) return T("time.days_hours", (long)time.TotalDays, time.Hours);
        if (time.TotalHours >= 1) return T("time.hours_minutes", (long)time.TotalHours, time.Minutes);
        return time.TotalMinutes >= 1 ? T("time.minutes_seconds", (long)time.TotalMinutes, time.Seconds) : T("time.seconds", Math.Ceiling(time.TotalSeconds));
    }

    /// <summary>Display text of a download status id (Downloading, Completed, ...). Unknown ids are shown as they are.</summary>
    public static string Status(string? status)
    {
        if (string.IsNullOrEmpty(status)) return "";
        string key = "status." + status.ToLowerInvariant();
        return Has(key) ? Lookup(key) : status;
    }

    // ---- loading ----
    private static State Build(LanguageInfo info)
    {
        IReadOnlyDictionary<string, string> strings = info.Tag == LanguageCatalog.Pseudo
            ? EnglishStrings.Value.ToDictionary(p => p.Key, p => PseudoLocale.Transform(p.Value))
            : info.Tag == LanguageCatalog.English ? EnglishStrings.Value : LoadFile(info.Tag);
        var culture = MakeCulture(info);
        return new State { Info = info, Strings = strings, Culture = culture, Pseudo = info.Tag == LanguageCatalog.Pseudo, Formatter = new IsolatingFormatter(culture, info.IsRtl) };
    }

    /// <summary>Culture for numbers and dates: Gregorian calendar, Western digits and ASCII separators for the Arabic-script languages, so "1,234.5" and "2026-10-19" read the same everywhere that script is used.</summary>
    private static CultureInfo MakeCulture(LanguageInfo info)
    {
        CultureInfo culture;
        try { culture = (CultureInfo)CultureInfo.GetCultureInfo(info.Culture).Clone(); }
        catch (CultureNotFoundException) { culture = (CultureInfo)CultureInfo.InvariantCulture.Clone(); }
        var dt = culture.DateTimeFormat;
        if (dt.Calendar is not GregorianCalendar)
        {
            var gregorian = culture.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault();
            if (gregorian is not null) { try { dt.Calendar = gregorian; } catch (ArgumentOutOfRangeException) { } }
        }
        var nf = culture.NumberFormat;
        nf.DigitSubstitution = DigitShapes.None;
        nf.NativeDigits = new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };
        if (info.Plural is "ar" or "fa")
        {
            nf.NumberDecimalSeparator = "."; nf.NumberGroupSeparator = ",";
            nf.PercentDecimalSeparator = "."; nf.PercentGroupSeparator = ",";
            nf.CurrencyDecimalSeparator = "."; nf.CurrencyGroupSeparator = ",";
            nf.NegativeSign = "-";
            dt.AMDesignator = info.Plural == "ar" ? "ص" : "ق.ظ"; dt.PMDesignator = info.Plural == "ar" ? "م" : "ب.ظ";
        }
        culture.NumberFormat.PercentPositivePattern = 1;
        return culture;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { ReadCommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false };

    /// <summary>Reads Locales/&lt;tag&gt;.json from the embedded resources. A missing or damaged file gives an empty map, so the language shows English.</summary>
    /// <summary>Test hook: replaces the embedded file reader (returns null to use the embedded file). Lets tests feed a language with missing keys.</summary>
    public static Func<string, IReadOnlyDictionary<string, string>?>? LoaderOverride { get; set; }

    internal static IReadOnlyDictionary<string, string> LoadFile(string tag)
    {
        try
        {
            if (LoaderOverride?.Invoke(tag) is { } injected) return injected;
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream(ResourcePrefix + tag + ".json");
            if (stream is null) return new Dictionary<string, string>();
            return Parse(stream);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or NotSupportedException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static IReadOnlyDictionary<string, string> Parse(Stream stream) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(stream, JsonOptions) ?? new Dictionary<string, string>();

    /// <summary>Formats arguments with the language's culture and, in right-to-left languages, wraps each one in left-to-right marks so file names, paths, URLs and numbers keep their own direction.</summary>
    private sealed class IsolatingFormatter : IFormatProvider, ICustomFormatter
    {
        private readonly CultureInfo _culture; private readonly bool _isolate;
        public IsolatingFormatter(CultureInfo culture, bool isolate) { _culture = culture; _isolate = isolate; }
        public object? GetFormat(Type? formatType) =>
            formatType == typeof(ICustomFormatter) ? this : formatType == typeof(NumberFormatInfo) ? _culture.NumberFormat : formatType == typeof(DateTimeFormatInfo) ? _culture.DateTimeFormat : null;
        public string Format(string? format, object? arg, IFormatProvider? provider)
        {
            string text = arg is IFormattable f ? f.ToString(format, _culture) : arg?.ToString() ?? "";
            return _isolate && text.Length > 0 && !IsWrapped(text) ? Wrap(text) : text;
        }
    }
}
