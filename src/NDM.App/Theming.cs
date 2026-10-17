using System.Windows;
using System.Windows.Media;
namespace NDM.App;

/// <summary>Builds and applies theme dictionaries (with the optional accent override) to an application.</summary>
public static class ThemeManager
{
    private static readonly Color Near = Color.FromRgb(0x10, 0x11, 0x14);
    /// <summary>Theme id last applied (may be System) and the accent override in force; used by the window chrome hook.</summary>
    public static string CurrentId { get; private set; } = ThemeCatalog.Default;
    public static string? CurrentAccent { get; private set; }
    public static bool CurrentDark { get; private set; }

    public static ResourceDictionary LoadFile(string concreteId) =>
        new() { Source = new Uri($"pack://application:,,,/NeoDM;component/Themes/{concreteId}.xaml", UriKind.Absolute) };

    /// <summary>Theme dictionary for an id with the accent override (if any) layered on top. Same keys as a plain theme file.</summary>
    public static ResourceDictionary Build(string? themeId, string? accent, Func<bool?>? osDark = null)
    {
        var file = LoadFile(ThemeCatalog.Resolve(themeId, osDark));
        if (!ThemeCatalog.TryParseAccent(accent, out var hex)) return file;
        var wrapper = new ResourceDictionary();
        wrapper.MergedDictionaries.Add(file);
        foreach (var (key, brush) in AccentBrushes(file, hex)) wrapper[key] = brush;
        if (wrapper["ItemSelectedBrush"] is Brush selected) wrapper[SystemColors.HighlightBrushKey] = selected; // keeps system-colour readers on the accent-tinted selection
        return wrapper;
    }

    /// <summary>Replaces the theme layer (merged dictionary 0) and re-applies the title-bar mode to every open window.</summary>
    public static void Apply(Application app, string? themeId, string? accent, Func<bool?>? osDark = null)
    {
        CurrentId = ThemeCatalog.Normalize(themeId);
        CurrentAccent = ThemeCatalog.NormalizeAccent(accent);
        CurrentDark = ThemeCatalog.IsDark(CurrentId, osDark);
        app.Resources.MergedDictionaries[0] = Build(CurrentId, CurrentAccent, osDark);
        foreach (Window window in app.Windows) ApplyChrome(window);
    }

    public static void ApplyChrome(Window window)
    {
        string concrete = ThemeCatalog.Resolve(CurrentId);
        bool branded = ThemeCatalog.All.First(t => t.Id == concrete).Branded;
        Color? caption = null, text = null, border = null;
        if (branded && Application.Current is { } app)
        {
            caption = (app.TryFindResource("CanvasBrush") as SolidColorBrush)?.Color;
            text = (app.TryFindResource("InkBrush") as SolidColorBrush)?.Color;
            border = (app.TryFindResource("BorderBrush") as SolidColorBrush)?.Color;
        }
        WindowChrome.Apply(window, CurrentDark, caption, text, border);
    }

    // ---- accent derivation ----
    private static double Channel(byte c) { double s = c / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
    public static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    public static double Contrast(Color a, Color b) { double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
    private static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color Of(ResourceDictionary d, string key) => ((SolidColorBrush)d[key]).Color;
    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Accent-dependent brushes for an accent colour over a theme: the accent is nudged until it reads as text on the surface (4.5:1), the on-accent text is whichever of near-black/white is readable on it.</summary>
    public static IEnumerable<(string Key, Brush Brush)> AccentBrushes(ResourceDictionary theme, string hex)
    {
        var surface = Of(theme, "SurfaceBrush"); var canvas = Of(theme, "CanvasBrush");
        bool dark = Luminance(canvas) < 0.2;
        var accent = Parse(hex);
        var toward = dark ? Colors.White : Colors.Black;
        for (int i = 0; i < 40 && (Contrast(accent, surface) < 4.5 || Contrast(accent, canvas) < 4.5); i++) accent = Mix(accent, toward, 0.06);
        var hover = Mix(accent, dark ? Colors.White : Colors.Black, dark ? 0.25 : 0.2);
        var muted = Of(theme, "MutedBrush");
        double tint = dark ? 0.22 : 0.14; var soft = Mix(surface, accent, tint);
        while (tint > 0.04 && Contrast(muted, soft) < 4.5) { tint -= 0.02; soft = Mix(surface, accent, tint); } // secondary text stays readable on selected rows
        Color Best(params Color[] stops)
        {
            var candidates = new[] { Near, Colors.White, Colors.Black };
            return candidates.FirstOrDefault(c => stops.Min(s => Contrast(c, s)) >= 4.5, candidates.OrderByDescending(c => stops.Min(s => Contrast(c, s))).First());
        }
        var onAccent = Best(accent);
        Brush fill = Frozen(accent);
        if (theme["AccentFillBrush"] is LinearGradientBrush)
        {
            var start = Mix(accent, dark ? Colors.Black : Colors.White, 0.06); var end = accent;
            var on = Best(start, end);
            if (Contrast(on, start) >= 4.5 && Contrast(on, end) >= 4.5)
            {
                onAccent = on;
                var gradient = new LinearGradientBrush(start, end, new Point(0, 0), new Point(1, 1));
                gradient.Freeze(); fill = gradient;
            }
        }
        return new (string, Brush)[]
        {
            ("AccentBrush", Frozen(accent)), ("AccentHoverBrush", Frozen(hover)), ("AccentSoftBrush", Frozen(soft)), ("ItemSelectedBrush", Frozen(soft)),
            ("OnAccentBrush", Frozen(onAccent)), ("AccentFillBrush", fill),
        };
    }
}
