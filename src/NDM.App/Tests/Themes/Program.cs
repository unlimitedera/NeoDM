global using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.Core;
using NDM.Tests;

internal sealed class ThemeTestApp : Application
{
    public ThemeTestApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}

/// <summary>1.4.0: theme registry, per-theme key parity, contrast matrix, accent override, System resolution, persistence, Options Appearance tab, per-theme render + screenshots.</summary>
internal static partial class ThemeTests
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
    private static void Click(Window w, string name) => Field<Button>(w, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static IEnumerable<T> Visual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Visual<T>(child)) yield return nested;
        }
    }
    // ---- colours ----
    private static Color Brush(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;
    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static bool Same(Color a, Color b, int tol = 2) => Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;
    private static double Contrast(Color a, Color b) => ThemeManager.Contrast(a, b);
    private static IEnumerable<Color> FillStops()
    {
        var fill = (Brush)Application.Current.FindResource("AccentFillBrush");
        return fill is LinearGradientBrush g ? g.GradientStops.Select(s => s.Color) : new[] { ((SolidColorBrush)fill).Color };
    }
    private static (byte R, byte G, byte B, byte A)[] Pixels(BitmapSource bitmap)
    {
        var data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0);
        var result = new (byte, byte, byte, byte)[data.Length / 4];
        for (int i = 0; i < result.Length; i++) result[i] = (data[i * 4 + 2], data[i * 4 + 1], data[i * 4], data[i * 4 + 3]);
        return result;
    }
    private static Color Px(BitmapSource bitmap, int x, int y) { var p = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), p, 4, 0); return Color.FromRgb(p[2], p[1], p[0]); }
    private static RenderTargetBitmap Render(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element); return bitmap;
    }
    private static RenderTargetBitmap Capture(Window window)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        int w = (int)Math.Ceiling(content.ActualWidth), h = (int)Math.Ceiling(content.ActualHeight);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen()) { ctx.DrawRectangle(window.Background, null, new Rect(0, 0, w, h)); ctx.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight)); }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); return bitmap;
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "NDM.App", "Themes"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root");
    }
    private static void Use(string theme, string? accent = null) => ThemeManager.Apply(Application.Current, theme, accent);

    [STAThread]
    private static int Main()
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-ThemeTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var app = new ThemeTestApp();
        if (Environment.GetEnvironmentVariable("NEODM_SHOTS_BEFORE") is { Length: > 0 } before) // diagnostic: reproduce the 1.4.5 selection rendering (adorner painted over the text) for the before screenshots
        {
            AppContext.SetSwitch(SelectionRendering.SwitchName, true); SelectionShots("before", before, scratch); app.Shutdown(); return 0;
        }
        try
        {
            Registry(); KeyParity(); ContrastMatrix(); SystemColourOverride(); SelectedRows(scratch); AccentOverride(); SystemTheme(); ApplyAndRevert(); Persistence(scratch); OptionsUi(scratch);
            Surfaces(); SelectedTextReadable(); TrayIcon(scratch); if (Environment.GetEnvironmentVariable("NEODM_SHOTS") is { Length: > 0 } selShots) { Directory.CreateDirectory(selShots); SelectionShots("after", selShots, scratch); } RenderEverything(scratch);
            Console.WriteLine($"Theme tests passed: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close(); try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } app.Shutdown(); }
    }

    // ===== 1. registry =====
    private static void Registry()
    {
        var ids = ThemeCatalog.All.Select(t => t.Id).ToArray();
        Check(ids.Length == 9 && ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ids.Length, "Theme ids are unique (9 themes)");
        Check(new[] { "Light", "Dark", "System", "Midnight", "Graphite", "Ocean", "Forest", "Rose", "Paper" }.SequenceEqual(ids), "Registry order: Light, Dark, System, Midnight, Graphite, Ocean, Forest, Rose, Paper");
        Check(ThemeCatalog.All.All(t => t.Name.Length > 0 && t.Description.Length > 0), "Every theme has a name and description for the gallery");
        Check(ThemeCatalog.Concrete.All(t => File.Exists(Path.Combine(RepoRoot(), "src", "NDM.App", "Themes", t.Id + ".xaml"))) && ThemeCatalog.Concrete.Count() == 8, "Every concrete theme has its own dictionary file; System has none");
        Check(ThemeCatalog.Normalize("Dark") == "Dark" && ThemeCatalog.Normalize("light") == "Light" && ThemeCatalog.Normalize("MIDNIGHT") == "Midnight" && ThemeCatalog.Normalize(" Paper ") == "Paper", "Known ids normalise case-insensitively");
        Check(new string?[] { null, "", "Neon", "Dark2", "../Dark", "System.Windows" }.All(v => ThemeCatalog.Normalize(v) == "Light") && !ThemeCatalog.IsKnown("Neon"), "Unknown/legacy-damaged ids fall back to Light");
        Check(ThemeCatalog.TryParseAccent("#7c6cf2", out var a) && a == "#7C6CF2" && ThemeCatalog.TryParseAccent("7C6CF2", out a) && a == "#7C6CF2" && ThemeCatalog.TryParseAccent(" #00ff00 ", out a) && a == "#00FF00", "Accent accepts #RRGGBB (case/space/missing # tolerant) and canonicalises");
        Check(new string?[] { null, "", "#", "#12", "#1234567", "#GGGGGG", "red", "#12 456", "rgb(1,2,3)", "#12345G" }.All(v => !ThemeCatalog.TryParseAccent(v, out _)), "Accent rejects non-#RRGGBB text");
    }

    // ===== 2. every theme has exactly the same keys (and value types), and every key the XAML uses exists =====
    private static void KeyParity()
    {
        var light = ThemeManager.LoadFile("Light"); var baseKeys = light.Keys.OfType<string>().OrderBy(k => k).ToArray();
        Check(baseKeys.Length == 30 && new[] { "CanvasBrush", "AccentFillBrush", "ControlRadius", "CardRadius", "ControlPadding", "CommandPadding", "CommandMinHeight", "ItemHoverBrush", "ItemSelectedBrush", "ItemSelectedInactiveBrush", "ItemSelectedForegroundBrush", "SelectionBackgroundBrush", "SelectionForegroundBrush" }.All(baseKeys.Contains), $"Light defines the full key set ({baseKeys.Length} named keys incl. radius/density and the four list item keys)");
        foreach (var theme in ThemeCatalog.Concrete)
        {
            var dict = ThemeManager.LoadFile(theme.Id);
            var keys = dict.Keys.OfType<string>().OrderBy(k => k).ToArray();
            var missing = baseKeys.Except(keys).ToArray(); var extra = keys.Except(baseKeys).ToArray();
            Check(missing.Length == 0 && extra.Length == 0, $"{theme.Id}: same key set as Light (missing: [{string.Join(",", missing)}] extra: [{string.Join(",", extra)}])");
            Check(baseKeys.All(k => dict[k]?.GetType().BaseType == light[k]?.GetType().BaseType || dict[k]?.GetType() == light[k]?.GetType() || (dict[k] is Brush && light[k] is Brush)), $"{theme.Id}: every key has the same value type as in Light");
            var systemKeys = SystemKeys();
            Check(systemKeys.All(k => dict.Contains(k) && dict[k] is SolidColorBrush), $"{theme.Id}: overrides all {systemKeys.Length} system colour keys (Highlight, HighlightText, InactiveSelectionHighlight, InactiveSelectionHighlightText, Control, ControlText)");
            Check(Same(((SolidColorBrush)dict[SystemColors.HighlightBrushKey]).Color, ((SolidColorBrush)dict["ItemSelectedBrush"]).Color, 0) && Same(((SolidColorBrush)dict[SystemColors.InactiveSelectionHighlightBrushKey]).Color, ((SolidColorBrush)dict["ItemSelectedInactiveBrush"]).Color, 0)
                && Same(((SolidColorBrush)dict[SystemColors.HighlightTextBrushKey]).Color, ((SolidColorBrush)dict["ItemSelectedForegroundBrush"]).Color, 0) && Same(((SolidColorBrush)dict[SystemColors.InactiveSelectionHighlightTextBrushKey]).Color, ((SolidColorBrush)dict["ItemSelectedForegroundBrush"]).Color, 0)
                && Same(((SolidColorBrush)dict[SystemColors.ControlBrushKey]).Color, ((SolidColorBrush)dict["SurfaceBrush"]).Color, 0) && Same(((SolidColorBrush)dict[SystemColors.ControlTextBrushKey]).Color, ((SolidColorBrush)dict["InkBrush"]).Color, 0), $"{theme.Id}: system colours equal the matching theme brushes");
        }
        // every {DynamicResource X}/{StaticResource X} in the shipped XAML resolves to a theme key, a Controls key or a local x:Key
        string themes = Path.Combine(RepoRoot(), "src", "NDM.App");
        var files = Directory.GetFiles(themes, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("\\Tests\\")).ToArray();
        var defined = new HashSet<string>(baseKeys);
        foreach (var f in files) foreach (Match m in Regex.Matches(File.ReadAllText(f), "x:Key=\"([^\"]+)\"")) defined.Add(m.Groups[1].Value);
        var used = new HashSet<string>(); var themeLike = new Regex("(Brush|Radius|Padding|MinHeight)$");
        foreach (var f in files) foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\{(?:Dynamic|Static)Resource\s+([A-Za-z0-9_]+)\}")) used.Add(m.Groups[1].Value);
        var unresolved = used.Where(u => !defined.Contains(u)).ToArray();
        Check(unresolved.Length == 0, "Every resource key used by the XAML exists in the themes or Controls (unresolved: " + string.Join(",", unresolved) + ")");
        Check(used.Where(u => themeLike.IsMatch(u)).All(baseKeys.Concat(defined).Contains), "All Brush/Radius/Padding keys referenced are defined");
    }

    private static object[] SystemKeys() => new object[] { SystemColors.HighlightBrushKey, SystemColors.HighlightTextBrushKey, SystemColors.InactiveSelectionHighlightBrushKey, SystemColors.InactiveSelectionHighlightTextBrushKey, SystemColors.ControlBrushKey, SystemColors.ControlTextBrushKey };

    // ===== 3. contrast matrix over ALL themes =====
    private static void ContrastMatrix()
    {
        var pairs = new (string Fg, string Bg)[] { ("InkBrush", "CanvasBrush"), ("InkBrush", "SidebarBrush"), ("InkBrush", "SurfaceBrush"), ("InkBrush", "SurfaceHoverBrush"), ("InkBrush", "AccentSoftBrush"), ("MutedBrush", "CanvasBrush"), ("MutedBrush", "SurfaceBrush"), ("MutedBrush", "SidebarBrush"), ("MutedBrush", "SurfaceHoverBrush"), ("OnAccentBrush", "AccentBrush"), ("AccentBrush", "CanvasBrush"), ("AccentBrush", "SurfaceBrush"), ("AccentBrush", "SidebarBrush"), ("ErrorBrush", "SurfaceBrush"), ("SuccessBrush", "SurfaceBrush"), ("WarningBrush", "SurfaceBrush") };
        foreach (var theme in ThemeCatalog.All)
        {
            Use(theme.Id); string t = theme.Id + ": ";
            var failing = pairs.Where(p => Contrast(Brush(p.Fg), Brush(p.Bg)) < 4.5).Select(p => $"{p.Fg} on {p.Bg}={Contrast(Brush(p.Fg), Brush(p.Bg)):0.0}").ToArray();
            Check(failing.Length == 0, t + $"text contrast >= 4.5:1 for all {pairs.Length} pairs (failing: {string.Join("; ", failing)})");
            Check(new[] { "SurfaceBrush", "SidebarBrush", "CanvasBrush" }.All(bg => Contrast(Brush("DisabledInkBrush"), Brush(bg)) >= 3.0), t + "disabled text >= 3:1 on surface, sidebar and canvas");
            var states = new[] { "ItemHoverBrush", "ItemSelectedBrush", "ItemSelectedInactiveBrush" };
            var weak = states.SelectMany(bg => new[] { "ItemSelectedForegroundBrush", "InkBrush", "MutedBrush" }.Select(fg => (fg, bg))).Where(p => Contrast(Brush(p.fg), Brush(p.bg)) < 4.5).Select(p => $"{p.fg} on {p.bg}={Contrast(Brush(p.fg), Brush(p.bg)):0.0}").ToArray();
            Check(weak.Length == 0, t + $"list rows: selected, hover and inactive-selected text (foreground, ink, muted) >= 4.5:1 ({string.Join("; ", weak)})");
            Check(Contrast(Brush("ItemSelectedBrush"), Brush("SurfaceBrush")) >= 1.15 && Contrast(Brush("ItemSelectedInactiveBrush"), Brush("SurfaceBrush")) >= 1.15 && Contrast(Brush("ItemHoverBrush"), Brush("SurfaceBrush")) >= 1.08, t + "selected, inactive-selected and hover rows stand out from the list surface");
            bool darkTheme = ThemeCatalog.IsDark(theme.Id, () => ThemeManager.Luminance(Brush("CanvasBrush")) < 0.2);
            Check(states.All(k => darkTheme ? ThemeManager.Luminance(Brush(k)) < 0.2 : ThemeManager.Luminance(Brush(k)) > 0.5), t + (darkTheme ? "dark theme row states are dark (no white-grey selection)" : "light theme row states are light"));
            Check(FillStops().All(s => Contrast(Brush("OnAccentBrush"), s) >= 4.5), t + "text on the primary button fill (every gradient stop) >= 4.5:1");
            Check(Contrast(Brush("BorderBrush"), Brush("SurfaceBrush")) >= 1.1 && Contrast(Brush("ThumbBrush"), Brush("SurfaceBrush")) >= 1.3, t + "borders and scroll thumbs are visible on the surface");
            Check(ThemeCatalog.IsDark(theme.Id, () => ThemeManager.Luminance(Brush("CanvasBrush")) < 0.2) == (ThemeManager.Luminance(Brush("CanvasBrush")) < 0.2), t + "dark flag matches canvas lightness");
        }
        Use("Light");
    }

    // ===== 3b. the theme dictionary beats the Windows colours =====
    private static void SystemColourOverride()
    {
        foreach (var theme in ThemeCatalog.Concrete)
        {
            Use(theme.Id);
            var pairs = new (object Key, string Theme)[] { (SystemColors.HighlightBrushKey, "ItemSelectedBrush"), (SystemColors.InactiveSelectionHighlightBrushKey, "ItemSelectedInactiveBrush"), (SystemColors.HighlightTextBrushKey, "ItemSelectedForegroundBrush"), (SystemColors.ControlBrushKey, "SurfaceBrush"), (SystemColors.ControlTextBrushKey, "InkBrush") };
            Check(pairs.All(p => Same(((SolidColorBrush)Application.Current.FindResource(p.Key)).Color, Brush(p.Theme), 0)), theme.Id + ": resource lookup of the Windows highlight/control colours returns the theme colours");
            Use(theme.Id, "#FF8800");
            Check(Same(((SolidColorBrush)Application.Current.FindResource(SystemColors.HighlightBrushKey)).Color, Brush("AccentSoftBrush"), 0) && Same(Brush("ItemSelectedBrush"), Brush("AccentSoftBrush"), 0), theme.Id + ": with a custom accent the selection colour follows the accent");
        }
        Use("Light");
    }

    // ===== 3c. selected rows render with the theme colours (focused and inactive window), never the Windows gradient =====
    private static Color Average(BitmapSource bitmap, int x, int y, int w, int h)
    {
        var data = new byte[w * h * 4]; bitmap.CopyPixels(new Int32Rect(x, y, w, h), data, w * 4, 0);
        long r = 0, g = 0, b = 0; for (int i = 0; i < w * h; i++) { b += data[i * 4]; g += data[i * 4 + 1]; r += data[i * 4 + 2]; }
        return Color.FromRgb((byte)(r / (w * h)), (byte)(g / (w * h)), (byte)(b / (w * h)));
    }
    /// <summary>Checks one selected row of a rendered list: its empty right-hand area has the expected background, no white-grey (dark) or near-black (light) pixels sit on its edges, and its text is readable.</summary>
    private static string RowProblems(BitmapSource shot, Rect row, Color expected, Color ink, bool dark)
    {
        var problems = new List<string>();
        int x = (int)row.Right - 36, w = 24, y = (int)(row.Y + row.Height / 2) - 2;
        var mid = Average(shot, x, y, w, 4);
        if (!Same(mid, expected, 4)) problems.Add($"background #{mid.R:X2}{mid.G:X2}{mid.B:X2} instead of #{expected.R:X2}{expected.G:X2}{expected.B:X2}");
        for (int yy = (int)row.Y + 2; yy < (int)(row.Y + row.Height) - 2; yy++)
            for (int xx = x; xx < x + w; xx++)
            {
                var c = Px(shot, xx, yy);
                if (dark ? NearWhite(c) : NearBlack(c)) { problems.Add($"{(dark ? "near-white" : "near-black")} pixel at {xx},{yy}"); yy = int.MaxValue - 1; break; }
            }
        double best = 0; var text = new Rect(row.X + 6, row.Y + 3, 90, Math.Max(4, row.Height - 6));
        for (int yy = (int)text.Y; yy < (int)(text.Y + text.Height); yy++) for (int xx = (int)text.X; xx < (int)(text.X + text.Width); xx++) best = Math.Max(best, Contrast(Px(shot, xx, yy), mid));
        if (best < 4.5) problems.Add($"text not readable (best pixel contrast {best:0.0}:1)");
        return string.Join("; ", problems);
    }
    private static void SelectedRows(string root)
    {
        int checkedStates = 0; int skippedFocus = 0;
        foreach (var theme in ThemeCatalog.Concrete)
            foreach (string? accent in new string?[] { null, "#FF8800" })
            {
                Use(theme.Id, accent); string t = theme.Id + (accent is null ? "" : " + custom accent") + ": ";
                var list = new ListView { Width = 380, Height = 150, Margin = new Thickness(0), View = new GridView() };
                var grid = (GridView)list.View;
                grid.Columns.Add(new GridViewColumn { Header = "N.", Width = 50, DisplayMemberBinding = new System.Windows.Data.Binding("Number") });
                grid.Columns.Add(new GridViewColumn { Header = "Downloaded", Width = 120, DisplayMemberBinding = new System.Windows.Data.Binding("Downloaded") });
                grid.Columns.Add(new GridViewColumn { Header = "Info", Width = 200, DisplayMemberBinding = new System.Windows.Data.Binding("Info") });
                ListViewFill.SetFillColumn(list, 2);
                list.ItemsSource = new[] { new { Number = 1, Downloaded = "12.9 MB", Info = "Receiving data" }, new { Number = 2, Downloaded = "3.4 MB", Info = "Waiting" }, new { Number = 3, Downloaded = "0 Bytes", Info = "Idle" } };
                var host = new Window { Width = 420, Height = 210, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 30, Top = 30, Content = new Border { Padding = new Thickness(10), Child = list } };
                host.Show(); Pump(120); host.UpdateLayout();
                list.SelectedIndex = 0; Pump(60);
                bool dark = theme.IsDark; var ink = Brush("ItemSelectedForegroundBrush");
                // inactive: the list has no keyboard focus
                Keyboard.ClearFocus(); Pump(60); host.UpdateLayout();
                bool active = Selector.GetIsSelectionActive(list);
                var inactiveShot = Capture(host);
                var item0 = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var box = item0.TransformToAncestor((Visual)host.Content).TransformBounds(new Rect(0, 0, item0.ActualWidth, item0.ActualHeight));
                string bad = RowProblems(inactiveShot, box, Brush("ItemSelectedInactiveBrush"), ink, dark);
                Check(!active && bad.Length == 0, t + $"selected row in an inactive window uses ItemSelectedInactiveBrush, text readable ({bad})");
                checkedStates++;
                // focused: give the list keyboard focus; skipped (and counted) when the desktop refuses to activate the test window
                host.Activate(); item0.Focus(); Keyboard.Focus(item0); Pump(80); host.UpdateLayout();
                if (Selector.GetIsSelectionActive(list))
                {
                    var focusedShot = Capture(host);
                    box = item0.TransformToAncestor((Visual)host.Content).TransformBounds(new Rect(0, 0, item0.ActualWidth, item0.ActualHeight));
                    bad = RowProblems(focusedShot, box, Brush("ItemSelectedBrush"), ink, dark);
                    Check(bad.Length == 0, t + $"selected row with keyboard focus uses ItemSelectedBrush, text readable ({bad})");
                    checkedStates++;
                }
                else { skippedFocus++; Console.WriteLine("NOTE: " + t + "keyboard focus could not be set in this session; focused state not rendered"); }
                // the fill column leaves no empty trailing header cell: columns add up to the viewport
                var scroll = Visual<ScrollViewer>(list).First();
                Check(Math.Abs(grid.Columns.Sum(c => c.Width) - scroll.ViewportWidth) <= 2, t + $"last column fills the list ({grid.Columns.Sum(c => c.Width):0} of {scroll.ViewportWidth:0})");
                host.Close();
            }
        Console.WriteLine($"Selected-row renders: {checkedStates} states checked, {skippedFocus} focused states skipped");
        Use("Light");
    }

    // ===== 4. accent override =====
    private static void AccentOverride()
    {
        var accents = new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FFFFFF", "#000000", "#808080", "#7C6CF2", "#2DD4BF", "#F5B83D" };
        foreach (var theme in ThemeCatalog.Concrete)
        {
            int bad = 0; string firstBad = "";
            foreach (var accent in accents)
            {
                Use(theme.Id, accent);
                bool ok = Contrast(Brush("OnAccentBrush"), Brush("AccentBrush")) >= 4.5 && FillStops().All(s => Contrast(Brush("OnAccentBrush"), s) >= 4.5)
                    && Contrast(Brush("AccentBrush"), Brush("SurfaceBrush")) >= 4.5 && Contrast(Brush("AccentBrush"), Brush("CanvasBrush")) >= 4.5
                    && Contrast(Brush("InkBrush"), Brush("AccentSoftBrush")) >= 4.5 && Contrast(Brush("MutedBrush"), Brush("AccentSoftBrush")) >= 4.5;
                if (!ok) { bad++; firstBad = $"{accent}: onAccent/accent {Contrast(Brush("OnAccentBrush"), Brush("AccentBrush")):0.0} stops {string.Join("/", FillStops().Select(x => Contrast(Brush("OnAccentBrush"), x).ToString("0.0")))} accent/surface {Contrast(Brush("AccentBrush"), Brush("SurfaceBrush")):0.0} accent/canvas {Contrast(Brush("AccentBrush"), Brush("CanvasBrush")):0.0} ink/soft {Contrast(Brush("InkBrush"), Brush("AccentSoftBrush")):0.0} muted/soft {Contrast(Brush("MutedBrush"), Brush("AccentSoftBrush")):0.0}"; }
            }
            Check(bad == 0, $"{theme.Id}: {accents.Length} custom accents (incl. pure red/green/blue/yellow/white/black) keep every text contrast >= 4.5:1 {firstBad}");
        }
        Use("Midnight", "#FF8800");
        Check(Same(Brush("AccentBrush"), Hex("#FF8800")) && Application.Current.FindResource("AccentFillBrush") is LinearGradientBrush, "Midnight keeps its gradient primary fill with a custom accent");
        Check(Same(Brush("AccentHoverBrush"), Hex("#FF8800")) == false && Same(Brush("AccentSoftBrush"), Hex("#FF8800")) == false, "Hover and soft accent are derived, not copied");
        Use("Light", "#FF8800");
        Check(Application.Current.FindResource("AccentFillBrush") is SolidColorBrush && Contrast(Brush("AccentBrush"), Brush("SurfaceBrush")) >= 4.5, "Light with a bright custom accent darkens it until it is readable as text");
        Use("Light", "not a colour");
        Check(Same(Brush("AccentBrush"), Hex("#006C67")), "An invalid accent override is ignored (theme accent stays)");
        Use("Midnight", null);
        Check(Same(Brush("AccentBrush"), Hex("#8B7CFF")), "Reset accent (null) restores the theme accent");
        Use("Light");
    }

    // ===== 5. System resolution with an injected OS provider =====
    private static void SystemTheme()
    {
        Check(ThemeCatalog.Resolve("System", () => true) == "Dark" && ThemeCatalog.Resolve("system", () => false) == "Light" && ThemeCatalog.Resolve("System", () => null) == "Light", "System resolves to Dark/Light/Light(unknown) from the OS setting");
        Check(ThemeCatalog.Resolve("Midnight", () => false) == "Midnight" && ThemeCatalog.Resolve("Paper", () => true) == "Paper", "A concrete theme ignores the OS setting");
        Check(ThemeCatalog.IsDark("System", () => true) && !ThemeCatalog.IsDark("System", () => false), "System reports the resolved dark flag");
        bool os = true;
        ThemeManager.Apply(Application.Current, "System", null, () => os);
        Check(Same(Brush("CanvasBrush"), Hex("#171B1E")) && ThemeManager.CurrentId == "System" && ThemeManager.CurrentDark, "System + dark OS applies the Dark dictionary and stays 'System'");
        os = false; ThemeManager.Apply(Application.Current, "System", null, () => os);
        Check(Same(Brush("CanvasBrush"), Hex("#F3F4F6")) && ThemeManager.CurrentId == "System" && !ThemeManager.CurrentDark, "System re-applied after the OS flips to light applies Light");
        var original = ThemeCatalog.OsDarkProvider; ThemeCatalog.OsDarkProvider = () => true;
        try { Use("System"); Check(Same(Brush("CanvasBrush"), Hex("#171B1E")), "The injectable default provider drives System"); }
        finally { ThemeCatalog.OsDarkProvider = original; }
        Check(ThemeCatalog.OsDarkProvider() is true or false or null, "The real registry provider returns without throwing");
        Use("Light");
    }

    // ===== 6. apply / revert =====
    private static void ApplyAndRevert()
    {
        Use("Light"); var window = new Window { Width = 200, Height = 100, ShowInTaskbar = false }; window.Show(); Pump();
        Check(Same(((SolidColorBrush)window.Background).Color, Hex("#F3F4F6")), "Open window starts Light");
        Use("Midnight"); Pump();
        Check(Same(((SolidColorBrush)window.Background).Color, Hex("#16181B")) && ThemeManager.CurrentDark, "Applying Midnight repaints an already-open window at once (no restart)");
        Use("Paper"); Check(Same(((SolidColorBrush)window.Background).Color, Hex("#F4EEE3")) && !ThemeManager.CurrentDark, "Paper is a light-type theme");
        Use("Light"); Check(Same(((SolidColorBrush)window.Background).Color, Hex("#F3F4F6")), "Reverting restores Light");
        Check(Same(Brush("AccentBrush"), Hex("#006C67")) && Application.Current.Resources.MergedDictionaries.Count == 2, "Theme layer stays one dictionary at index 0 (Controls untouched)");
        var midnight = ThemeManager.LoadFile("Midnight");
        Check(((CornerRadius)midnight["CardRadius"]).TopLeft == 8 && ((CornerRadius)midnight["ControlRadius"]).TopLeft == 8 && ((CornerRadius)ThemeManager.LoadFile("Light")["ControlRadius"]).TopLeft == 6, "Midnight has 8px cards/controls; Light keeps 6px (theme-level radius keys)");
        Check(Same(((SolidColorBrush)midnight["CanvasBrush"]).Color, Hex("#16181B")) && Same(((SolidColorBrush)midnight["SurfaceBrush"]).Color, Hex("#1E2125")) && Same(((SolidColorBrush)midnight["BorderBrush"]).Color, Hex("#2A2D33")) && Same(((SolidColorBrush)midnight["InkBrush"]).Color, Hex("#E6E8EB")), "Midnight palette: window #16181B, panels #1E2125, hairline #2A2D33, text #E6E8EB");
        window.Close();
    }

    // ===== 7. persistence and legacy migration =====
    private static void Persistence(string root)
    {
        var dir = Path.Combine(root, "settings"); Directory.CreateDirectory(dir); string file = Path.Combine(dir, "settings.json");
        foreach (var theme in ThemeCatalog.All)
        {
            new AppSettings { DownloadDirectory = Path.Combine(root, "files"), Theme = theme.Id, AccentOverride = "#12AB34" }.Save(dir);
            var loaded = AppSettings.Load(dir);
            if (loaded.Theme != theme.Id || loaded.AccentOverride != "#12AB34") throw new Exception("FAIL: round trip " + theme.Id);
        }
        Check(true, "Every theme id and an accent override round-trip through settings.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new { DownloadDirectory = Path.Combine(root, "files"), Theme = "Dark", SettingsSchema = 4 }));
        var legacyDark = AppSettings.Load(dir);
        File.WriteAllText(file, JsonSerializer.Serialize(new { DownloadDirectory = Path.Combine(root, "files"), Theme = "Light" }));
        var legacyLight = AppSettings.Load(dir);
        Check(legacyDark.Theme == "Dark" && legacyDark.AccentOverride is null && legacyLight.Theme == "Light" && legacyLight.SettingsSchema == AppSettings.CurrentSchema, "Legacy 'Dark'/'Light' (no accent field) keep their choice; accent defaults to none; no schema bump needed");
        Check(AppSettings.CurrentSchema == 7, "Settings schema is 7");
        foreach (var (json, theme, accent, label) in new[]
        {
            ("{\"Theme\":\"Neon\"}", "Light", (string?)null, "unknown theme"), ("{\"Theme\":\"midnight\",\"AccentOverride\":\"#abcdef\"}", "Midnight", "#ABCDEF", "lower-case ids and accents are canonicalised"),
            ("{\"Theme\":5}", "Light", null, "numeric theme"), ("{\"Theme\":\"Dark\",\"AccentOverride\":\"red\"}", "Dark", null, "named colour accent"),
            ("{\"AccentOverride\":\"#12\"}", "Light", null, "short accent"), ("{\"Theme\":\"Ocean\",\"AccentOverride\":null}", "Ocean", null, "null accent"), ("{\"Theme\":\"System\"}", "System", null, "System"),
        })
        {
            File.WriteAllText(file, json); var s = AppSettings.Load(dir);
            Check(s.Theme == theme && s.AccentOverride == accent, $"Load: {label} -> {theme} / {accent ?? "none"}");
        }
        var copy = new AppSettings { Theme = "Forest", AccentOverride = "#112233" }.Copy(); var target = new AppSettings(); target.CopyFrom(copy);
        Check(copy.Theme == "Forest" && copy.AccentOverride == "#112233" && target.Theme == "Forest" && target.AccentOverride == "#112233", "Copy/CopyFrom carry theme and accent");
    }

    // ===== 8. Options > Appearance =====
    private static void OptionsUi(string root)
    {
        var state = Path.Combine(root, "options"); Directory.CreateDirectory(state); string file = Path.Combine(state, "settings.json");
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "options-files"), Theme = "Light" };
        settings.Save(state); Use("Light");
        string before = File.ReadAllText(file);
        var w = new SettingsWindow(settings, state) { ShowInTaskbar = false }; w.Show(); Pump();
        var tabs = Field<TabControl>(w, "OptionsTabs");
        var headers = tabs.Items.OfType<TabItem>().Select(i => ((string)i.Header).Replace("_", "")).ToArray();
        Check(headers.SequenceEqual(new[] { "General", "Appearance", "Save To", "Downloads", "Proxy", "Torrents" }), "Options has an Appearance tab right after General");
        var gallery = Field<ListBox>(w, "ThemeGallery"); var cards = gallery.Items.OfType<ThemeCardModel>().ToArray();
        Check(cards.Select(c => c.Id).SequenceEqual(ThemeCatalog.All.Select(t => t.Id)) && ((ThemeCardModel)gallery.SelectedItem).Id == "Light", "Gallery lists all 9 themes with the saved one selected");
        tabs.SelectedItem = Field<TabItem>(w, "AppearanceTab"); Pump(100); w.UpdateLayout();
        Check(Visual<ListBoxItem>(gallery).Count() == 9 && Visual<ListBoxItem>(gallery).All(i => i.ActualWidth > 100 && i.ActualHeight > 100), "Gallery renders nine cards with their swatches");
        gallery.SelectedItem = cards.First(c => c.Id == "Midnight"); Pump();
        Check(Same(Brush("CanvasBrush"), Hex("#16181B")) && Same(((SolidColorBrush)w.Background).Color, Hex("#16181B")), "Selecting a card applies it instantly, including to the Options window itself");
        Check(settings.Theme == "Light" && File.ReadAllText(file) == before, "Preview does not touch the saved settings");
        Click(w, "CancelButton"); Pump();
        Check(!w.IsVisible && Same(Brush("CanvasBrush"), Hex("#F3F4F6")) && ThemeManager.CurrentId == "Light" && settings.Theme == "Light" && File.ReadAllText(file) == before, "Cancel reverts the preview to the saved theme and saves nothing");

        w = new SettingsWindow(settings, state) { ShowInTaskbar = false }; w.Show(); Pump();
        gallery = Field<ListBox>(w, "ThemeGallery"); gallery.SelectedItem = gallery.Items.OfType<ThemeCardModel>().First(c => c.Id == "Ocean"); Pump();
        w.Close(); Pump();
        Check(Same(Brush("CanvasBrush"), Hex("#F3F4F6")), "Closing the window another way (title-bar X / Esc) also reverts the preview");

        w = new SettingsWindow(settings, state) { ShowInTaskbar = false }; w.Show(); Pump();
        gallery = Field<ListBox>(w, "ThemeGallery"); gallery.SelectedItem = gallery.Items.OfType<ThemeCardModel>().First(c => c.Id == "Ocean"); Pump();
        var hex = Field<TextBox>(w, "AccentHex");
        Field<TabControl>(w, "OptionsTabs").SelectedItem = Field<TabItem>(w, "AppearanceTab"); Pump(100); w.UpdateLayout();
        var swatch = Visual<Button>(Field<ItemsControl>(w, "AccentSwatches")).First();
        swatch.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
        Check(hex.Text == "#8B7CFF" && Same(Brush("AccentBrush"), Hex("#8B7CFF")) && Field<TextBlock>(w, "AccentError").Text == "", "A preset swatch fills the hex box and applies the accent immediately");
        hex.Text = "#FF8800"; Pump();
        Check(Same(Brush("AccentBrush"), Hex("#FF8800")) && w.PreviewAccent == "#FF8800", "A custom #RRGGBB applies live");
        hex.Text = "#12"; Pump();
        Check(Field<TextBlock>(w, "AccentError").Text.Length > 0 && Same(Brush("AccentBrush"), Hex("#FF8800")), "An invalid hex shows an error and keeps the last valid preview");
        Click(w, "SaveButton"); Pump();
        Check(w.IsVisible && Field<TextBlock>(w, "ErrorText").Text.Contains("hex") && settings.Theme == "Light" && File.ReadAllText(file) == before, "Save refuses an invalid accent and writes nothing");
        hex.Text = "ff8800"; Pump();
        Check(Field<TextBlock>(w, "AccentError").Text == "", "The # is optional while typing");
        Click(w, "SaveButton"); Pump();
        var saved = AppSettings.Load(state);
        Check(!w.IsVisible && settings.Theme == "Ocean" && settings.AccentOverride == "#FF8800" && saved.Theme == "Ocean" && saved.AccentOverride == "#FF8800", "Save persists the theme and accent and updates the shared settings");
        Check(Same(Brush("CanvasBrush"), Hex("#0B1622")) && Same(Brush("AccentBrush"), Hex("#FF8800")), "After Save the chosen theme + accent stay applied (not reverted by closing)");

        w = new SettingsWindow(settings, state) { ShowInTaskbar = false }; w.Show(); Pump();
        Check(((ThemeCardModel)Field<ListBox>(w, "ThemeGallery").SelectedItem).Id == "Ocean" && Field<TextBox>(w, "AccentHex").Text == "#FF8800", "Reopening shows the saved theme and accent");
        Click(w, "ResetAccentButton"); Pump();
        Check(Field<TextBox>(w, "AccentHex").Text == "" && w.PreviewAccent is null && Same(Brush("AccentBrush"), Hex("#3CC8F0")), "Reset accent returns to the theme's own accent");
        Click(w, "SaveButton"); Pump();
        Check(AppSettings.Load(state).AccentOverride is null && AppSettings.Load(state).Theme == "Ocean", "Saving after Reset persists 'no override'");
        w = new SettingsWindow(settings, state) { ShowInTaskbar = false }; w.Show(); Pump();
        var g2 = Field<ListBox>(w, "ThemeGallery"); g2.SelectedItem = g2.Items.OfType<ThemeCardModel>().First(c => c.Id == "Paper"); Pump();
        Click(w, "SaveButton"); Pump();
        Check(AppSettings.Load(state).Theme == "Paper" && Same(Brush("CanvasBrush"), Hex("#F4EEE3")), "Saving a light-type theme (Paper) persists and applies it");
        // System card
        w = new SettingsWindow(settings, state) { ShowInTaskbar = false }; w.Show(); Pump();
        g2 = Field<ListBox>(w, "ThemeGallery"); g2.SelectedItem = g2.Items.OfType<ThemeCardModel>().First(c => c.Id == "System"); Pump();
        Check(ThemeManager.CurrentId == "System", "Selecting the System card applies 'System' (follows Windows)");
        Click(w, "CancelButton"); Pump();
        Check(ThemeManager.CurrentId == "Paper", "Cancel after previewing System restores Paper");
        Use("Light");
    }

    // ===== 9. surfaces in every theme: menus, tool tips, controls sampler; light themes have no near-black surprises =====
    private static bool NearWhite(Color c) => c.R >= 0xDC && c.G >= 0xDC && c.B >= 0xDC;
    private static bool NearBlack(Color c) => c.R <= 0x28 && c.G <= 0x28 && c.B <= 0x28;
    private static FrameworkElement Sampler()
    {
        var combo = new ComboBox { Width = 160, SelectedIndex = 0 }; combo.Items.Add(new ComboBoxItem { Content = "First choice" });
        var tabs = new TabControl { Width = 300, Height = 80 }; tabs.Items.Add(new TabItem { Header = "One", Content = new TextBlock { Text = "a" } }); tabs.Items.Add(new TabItem { Header = "Two", Content = new TextBlock { Text = "b" } });
        var list = new ListView { Width = 300, Height = 90, View = new GridView() };
        ((GridView)list.View).Columns.Add(new GridViewColumn { Header = "Name", Width = 140 }); ((GridView)list.View).Columns.Add(new GridViewColumn { Header = "Size", Width = 100 });
        list.Items.Add("row one"); list.Items.Add("row two");
        var panel = new StackPanel { Margin = new Thickness(12), Width = 320 };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { new Button { Content = "Button", Margin = new Thickness(0, 0, 8, 0) }, new Button { Content = "Primary", Style = (Style)Application.Current.FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 8, 0) }, new Button { Content = "Off", IsEnabled = false } }, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBox { Text = "text box", Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { new CheckBox { Content = "Checked", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) }, new CheckBox { Content = "Empty", Margin = new Thickness(0, 0, 12, 0) }, new RadioButton { Content = "Radio", IsChecked = true } }, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new ProgressBar { Value = 60, Maximum = 100, Height = 8, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(combo); panel.Children.Add(new Border { Height = 8 }); panel.Children.Add(tabs); panel.Children.Add(new Border { Height = 8 }); panel.Children.Add(list);
        return panel;
    }
    private static void Surfaces()
    {
        foreach (var theme in ThemeCatalog.Concrete)
        {
            Use(theme.Id); string t = theme.Id + ": "; bool dark = theme.IsDark;
            Color surface = Brush("SurfaceBrush"), border = Brush("BorderBrush");
            var menu = new ContextMenu(); menu.Items.Add(new MenuItem { Header = "_Open" }); menu.Items.Add(new MenuItem { Header = "Off", IsEnabled = false }); menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = "_Checked", IsCheckable = true, IsChecked = true, InputGestureText = "Ctrl+K" });
            var shot = Render(menu, 240, 130); int w = shot.PixelWidth, h = shot.PixelHeight; bool wrong = false;
            Func<Color, bool> surprise = c => dark ? NearWhite(c) : NearBlack(c);
            for (int i = 0; i < w; i++) foreach (var y in new[] { 0, 1, h - 2, h - 1 }) wrong |= surprise(Px(shot, i, y));
            for (int j = 0; j < h; j++) foreach (var x in new[] { 0, 1, w - 2, w - 1 }) wrong |= surprise(Px(shot, x, j));
            Check(!wrong && Same(Px(shot, w / 2, 0), border, 10) && Same(Px(shot, w / 2, 1), surface, 10), t + "context menu border is the theme hairline" + (dark ? " (no near-white edge)" : " (no near-black edge)"));
            var tip = new ToolTip { Content = "C:\\Downloads\\archive.zip" }; var tipShot = Render(tip, 220, 40);
            Check(Same(Px(tipShot, 100, 3), surface, 10) && Same(Px(tipShot, 0, 20), border, 10), t + "tooltip uses the surface and border colours");
            var host = new Window { Width = 380, Height = 460, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20, Content = Sampler() }; host.Show(); Pump(80);
            var sampler = Capture(host); var all = Pixels(sampler);
            int bad = all.Count(p => surprise(Color.FromRgb(p.R, p.G, p.B)));
            if (Environment.GetEnvironmentVariable("NEODM_SAMPLER") is { Length: > 0 } sdir) Save(sampler, Path.Combine(sdir, "sampler-" + theme.Id + ".png"));
            Check(bad < all.Length * 0.04, t + $"controls sampler has no {(dark ? "near-white" : "near-black")} surprise surfaces ({bad * 100.0 / all.Length:0.0}% of pixels, limit 4%)");
            Check(Visual<CheckBox>(host).All(c => c.ActualHeight > 0) && Visual<ProgressBar>(host).Single().ActualWidth > 100, t + "checkboxes, radio, progress bar, combo, tabs and list header render");
            host.Close();
            Use("Light");
        }
    }

    // ===== 10. every theme renders main window, Options (all tabs), progress window and the small dialogs; optional screenshots =====
    private static void RenderEverything(string root)
    {
        string? shots = Environment.GetEnvironmentVariable("NEODM_SHOTS");
        using var fixture = new FixtureServer();
        string files = shots is { Length: > 0 } ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "..", "Downloads", "NeoDM-theme-shots") : Path.Combine(root, "main-files");
        files = Path.GetFullPath(files); string state = Path.Combine(root, "main-state"); Directory.CreateDirectory(state); Directory.CreateDirectory(files);
        var now = DateTimeOffset.Now;
        var rows = new[]
        {
            ("ubuntu-24.04.3-desktop-amd64.iso", "Completed", 6_200_000_000L, now.AddHours(-30), (string?)null), ("holiday-footage-1080p.mp4", "Completed", 1_450_000_000L, now.AddHours(-8), null),
            ("quarterly-report-2026.pdf", "Completed", 8_400_000L, now.AddHours(-3), null), ("project-backup.zip", "Failed", 210_000_000L, now.AddHours(-2), "The server closed the connection."),
            ("podcast-episode-42.mp3", "Paused", 96_000_000L, now.AddHours(-1), null), ("nightly-build-setup.exe", "Scheduled", 310_000_000L, now.AddMinutes(-20), null),
        };
        var json = rows.Select(r => new
        {
            Id = Guid.NewGuid().ToString("N"), Request = new DownloadRequest("https://downloads.example.org/files/" + r.Item1, r.Item1), FileName = r.Item1, Status = r.Item2, Total = r.Item3,
            Segments = Array.Empty<object>(), Error = r.Item5, FilePath = Path.Combine(files, r.Item1), AddedAt = r.Item4, ScheduledAt = r.Item2 == "Scheduled" ? now.AddDays(1) : (DateTimeOffset?)null,
        });
        File.WriteAllText(Path.Combine(state, "queue.json"), JsonSerializer.Serialize(json));
        using var manager = new DownloadManager(files, state, new DownloadOptions { MaxConcurrentDownloads = 2, MaxSegments = 2, RetryCount = 0 });
        Check(manager.Jobs.Count == rows.Length, "Fixture queue restored (" + manager.Jobs.Count + " rows)");
        foreach (var name in new[] { "a-large-archive.7z", "release-notes-and-assets.tar" })
        {
            var live = manager.AddScheduledAsync(new DownloadRequest(fixture.BaseUrl + "/range?" + name, name), DateTimeOffset.UtcNow.AddHours(1)).GetAwaiter().GetResult();
            manager.SetSpeedLimit(live.Id, 60 * 1024); manager.Schedule(live.Id, DateTimeOffset.UtcNow.AddSeconds(-1));
        }
        var end = DateTime.UtcNow.AddSeconds(20);
        while (!manager.Jobs.Any(j => j.Status == "Downloading" && j.DownloadedBytes > 0) && DateTime.UtcNow < end) Pump(100);
        var settings = new AppSettings { DownloadDirectory = files, ShowCompletionDialog = false, ShowProgressOnCompletion = false };
        var main = new MainWindow(manager, settings, () => { }, enableTray: false) { ShowInTaskbar = false, Width = 1100, Height = 640, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
        main.Show(); Pump(700);
        var downloading = manager.Jobs.First(j => j.Status == "Downloading");
        var list = (ListView)main.FindName("DownloadList"); list.SelectedItem = main.Rows.First(r => r.Id == downloading.Id); Pump();
        var completed = manager.Jobs.First(j => j.Status == "Completed");
        foreach (var theme in ThemeCatalog.Concrete)
        {
            Use(theme.Id); Pump(80); string t = theme.Id + ": ";
            var shot = Capture(main);
            var pixels = Pixels(shot); var canvas = Brush("CanvasBrush");
            Check(shot.PixelWidth > 800 && pixels.Count(p => Same(Color.FromRgb(p.R, p.G, p.B), Brush("SidebarBrush"), 3)) > 2000 && pixels.Count(p => Same(Color.FromRgb(p.R, p.G, p.B), Brush("ItemSelectedBrush"), 3) || Same(Color.FromRgb(p.R, p.G, p.B), Brush("ItemSelectedInactiveBrush"), 3)) > 300, t + "main window renders with the theme's sidebar and selected-row colours (selected, or inactive-selected while the list has no focus)");
            Check(main.Rows.Count >= rows.Length && list.Items.Count >= rows.Length, t + "main window shows the fixture rows");
            if (shots is { Length: > 0 }) { Save(shot, Path.Combine(shots, theme.Id + ".png")); Console.WriteLine("SHOT: " + Path.Combine(shots, theme.Id + ".png")); }

            var options = new SettingsWindow(new AppSettings { DownloadDirectory = files, Theme = theme.Id }, Path.Combine(root, "o-" + theme.Id)) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 30, Top = 30 };
            options.Show(); Pump(80);
            var tabs = Field<TabControl>(options, "OptionsTabs"); int rendered = 0;
            foreach (TabItem item in tabs.Items) { tabs.SelectedItem = item; options.UpdateLayout(); if (item.Content is FrameworkElement { ActualHeight: > 0 }) rendered++; if (item == Field<TabItem>(options, "AppearanceTab") && shots is { Length: > 0 } && theme.Id is "Midnight" or "Light") Save(Capture(options), Path.Combine(shots, "options-appearance-" + theme.Id.ToLowerInvariant() + ".png")); }
            Check(rendered == tabs.Items.Count, t + $"Options renders all {rendered} tabs");
            options.Close(); Use(theme.Id);

            var progress = new DownloadProgressWindow(manager, downloading.Id, new JobCompletionStore(), settings) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
            progress.Show(); Pump(150);
            var pshot = Capture(progress);
            Check(pshot.PixelWidth > 300 && Pixels(pshot).Count(p => Same(Color.FromRgb(p.R, p.G, p.B), Brush("CanvasBrush"), 3)) > 5000, t + "progress window renders in the theme");
            var conn = Field<ListView>(progress, "ConnectionList");
            Check(conn.Items.Count > 0, t + "progress window shows connection rows for the live download");
            conn.SelectedIndex = 0; Pump(60); Keyboard.ClearFocus(); progress.UpdateLayout();
            ListViewItem ConnItem() { if (conn.SelectedIndex != 0) conn.SelectedIndex = 0; progress.UpdateLayout(); return (ListViewItem)conn.ItemContainerGenerator.ContainerFromIndex(0); }
            Rect Box() { var item = ConnItem(); return item.TransformToAncestor((Visual)progress.Content).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight)); }
            var boxInactive = Box(); string badRow = RowProblems(Capture(progress), boxInactive, Brush("ItemSelectedInactiveBrush"), Brush("ItemSelectedForegroundBrush"), theme.IsDark);
            Check(!Selector.GetIsSelectionActive(conn) && badRow.Length == 0, t + $"progress window: selected connection row (window without focus) is themed, not the Windows grey ({badRow})");
            progress.Activate(); var focusTarget = ConnItem(); focusTarget.Focus(); Keyboard.Focus(focusTarget); Pump(80); progress.UpdateLayout();
            if (Selector.GetIsSelectionActive(conn))
            {
                var boxActive = Box(); badRow = RowProblems(Capture(progress), boxActive, Brush("ItemSelectedBrush"), Brush("ItemSelectedForegroundBrush"), theme.IsDark);
                Check(badRow.Length == 0, t + $"progress window: selected connection row with keyboard focus is themed ({badRow})");
            }
            var gridView = (GridView)conn.View; var viewport = Visual<ScrollViewer>(conn).First().ViewportWidth;
            Check(Math.Abs(gridView.Columns.Sum(c => c.Width) - viewport) <= 2, t + $"progress window: the Info column fills the table, no empty trailing header cell ({gridView.Columns.Sum(c => c.Width):0} of {viewport:0})");
            if (shots is { Length: > 0 } && theme.Id is "Midnight" or "Dark")
            {
                string tag = theme.Id.ToLowerInvariant();
                Save(Capture(progress), Path.Combine(shots, $"progress-selected-{tag}.png")); Console.WriteLine("SHOT: progress-selected-" + tag);
                double smallHeight = conn.ActualHeight, smallWidth = conn.ActualWidth;
                progress.Width = 1000; progress.Height = 900; Pump(200); progress.UpdateLayout();
                Check(conn.ActualHeight > smallHeight + 100 && conn.ActualWidth > smallWidth + 100, t + $"progress window enlarged: table grows from {smallWidth:0}x{smallHeight:0} to {conn.ActualWidth:0}x{conn.ActualHeight:0}");
                Save(Capture(progress), Path.Combine(shots, $"progress-selected-{tag}-large.png")); Console.WriteLine("SHOT: progress-selected-" + tag + "-large");
            }
            progress.Close();

            var small = new List<Window> { new AboutWindow(), new AddDownloadWindow("https://example.org/file.zip"), new TextPromptWindow("Rename", "New name", "file.zip", _ => null), new DownloadCompleteWindow(completed, settings, () => { }), new PropertiesWindow(main.Rows.First(r => r.Id == completed.Id)), new OpenFileConfirmWindow("holiday.mp4", OpenKind.Video), new OpenFileConfirmWindow("setup.exe", null) };
            foreach (var win in small) { win.ShowInTaskbar = false; win.WindowStartupLocation = WindowStartupLocation.Manual; win.Left = 50; win.Top = 50; win.Show(); }
            Pump(150);
            Check(small.All(win => win.IsVisible && ((FrameworkElement)win.Content).ActualWidth > 100 && Same(((SolidColorBrush)win.Background).Color, canvas)), t + "About, Add, prompt, completion, Properties and Open confirmation windows render in the theme");
            var openWindow = small.OfType<OpenFileConfirmWindow>().First();
            var openShot = Capture(openWindow); var openPixels = Pixels(openShot);
            Check(openPixels.Count(p => Same(Color.FromRgb(p.R, p.G, p.B), canvas, 3)) > 3000 && !(theme.IsDark && openPixels.Count(p => p.R > 235 && p.G > 235 && p.B > 235) > 12000) && !(!theme.IsDark && openPixels.Count(p => p.R < 25 && p.G < 25 && p.B < 25) > 12000), t + "Open confirmation: themed surface, no white block in a dark theme and no black block in a light one");
            var openMessage = Field<TextBox>(openWindow, "MessageText"); var openBox = Field<CheckBox>(openWindow, "DontAskBox"); var openNo = Field<Button>(openWindow, "NoButton");
            Check(openMessage.Foreground is SolidColorBrush mf && Same(mf.Color, Brush("InkBrush"), 3) && openMessage.SelectionBrush is SolidColorBrush && openBox.Foreground is SolidColorBrush bf && Same(bf.Color, Brush("InkBrush"), 3) && openNo.Style is not null, t + "Open confirmation: message and box use the theme ink, selection pair is themed, No is the primary button");
            foreach (var win in small) win.Close();
            Use("Light");
        }
        foreach (var job in manager.Jobs.Where(j => j.Status is "Downloading" or "Connecting")) manager.Pause(job.Id);
        var settle = DateTime.UtcNow.AddSeconds(15);
        while (manager.Jobs.Any(j => j.Status is "Downloading" or "Connecting" or "Queued") && DateTime.UtcNow < settle) Pump(100);
        Pump(700); // the main window's close prompt only appears while a transfer is still active
        typeof(MainWindow).GetField("_exitWithoutPrompt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(main, true); // a Scheduled row counts as busy and would raise the quit prompt
        main.Close();
        if (shots is { Length: > 0 }) { manager.Dispose(); try { Directory.Delete(files, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
