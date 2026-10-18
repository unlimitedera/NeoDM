using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;

/// <summary>1.4.6: selected text must stay readable in every control, theme and accent, focused and unfocused. Measured from rendered pixels.</summary>
internal static partial class ThemeTests
{
    private static Color SelBg() => Brush("SelectionBackgroundBrush");
    private static Color SelFg() => Brush("SelectionForegroundBrush");

    /// <summary>Renders the control with its whole text selected and measures the selection pixels. Returns (selection pixels found, best text contrast against the selection fill, fill colour).</summary>
    private static (int Fill, double Contrast, Color Bg) MeasureSelection(Func<Control> make, bool focused, bool inactiveHighlight = true)
    {
        var control = make();
        var host = new Window { Width = 420, Height = 160, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 30, Top = 30, Content = new Grid { Margin = new Thickness(20), Children = { control } } };
        host.Show(); Pump(60);
        var inner = control is ComboBox combo ? (Control)(FindNamed<TextBox>(combo, "PART_EditableTextBox") ?? throw new Exception("editable combo has no text part")) : control;
        if (inner is TextBoxBase tb && inactiveHighlight) tb.IsInactiveSelectionHighlightEnabled = true;
        host.Activate();
        inner.Focus(); Keyboard.Focus(inner); Pump(60);
        switch (inner) { case TextBox t: t.SelectAll(); break; case PasswordBox p: p.SelectAll(); break; case RichTextBox r: r.SelectAll(); break; }
        if (!focused) { Keyboard.ClearFocus(); Keyboard.Focus(host); }
        Pump(70); host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(420, 160, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host.Content as Visual);
        var data = new byte[420 * 160 * 4]; bitmap.CopyPixels(data, 420 * 4, 0);
        Color At(int x, int y) { int i = (y * 420 + x) * 4; return Color.FromRgb(data[i + 2], data[i + 1], data[i]); }
        var surface = Brush("SurfaceBrush"); int bottom = (int)Math.Min(158, 20 + control.ActualHeight - 3);
        var histogram = new Dictionary<Color, int>();
        for (int y = 23; y < bottom; y++) for (int x = 23; x < 397; x++) { var c = At(x, y); if (!Same(c, surface, 6)) histogram[c] = histogram.GetValueOrDefault(c) + 1; }
        var top = histogram.OrderByDescending(kv => kv.Value).FirstOrDefault();
        var bg = top.Value >= 200 ? top.Key : Colors.Transparent; int fill = 0; int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        if (top.Value >= 200) for (int y = 23; y < bottom; y++) for (int x = 23; x < 397; x++) if (Same(At(x, y), bg, 3)) { fill++; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        double best = 0;
        if (fill > 0) for (int y = minY; y <= maxY; y++) for (int x = minX; x <= maxX; x++) best = Math.Max(best, Contrast(At(x, y), bg));
        if (Environment.GetEnvironmentVariable("NEODM_SELDUMP") is { Length: > 0 } dump) Save(bitmap, Path.Combine(dump, $"sel-{ThemeManager.CurrentId}-{control.GetType().Name}-{(focused ? "f" : "u")}.png"));
        host.Close();
        return (fill, best, bg);
    }
    private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement => Visual<T>(root).FirstOrDefault(e => e.Name == name);

    private static IEnumerable<(string Name, Func<Control> Make)> SelectionControls() => new (string, Func<Control>)[]
    {
        ("TextBox single line", () => new TextBox { Text = "Selected sample text", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Top }),
        ("TextBox multi line", () => new TextBox { Text = "Selected sample text\nsecond line here", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 20, FontWeight = FontWeights.SemiBold, Height = 100, VerticalAlignment = VerticalAlignment.Top }),
        ("PasswordBox", () => new PasswordBox { Password = "secret value", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Top }),
        ("Editable ComboBox", () => new ComboBox { IsEditable = true, Text = "Selected sample text", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Top }),
        ("RichTextBox", () => new RichTextBox { Document = new FlowDocument(new Paragraph(new Run("Selected sample text"))), FontSize = 20, FontWeight = FontWeights.SemiBold, Height = 70, VerticalAlignment = VerticalAlignment.Top }),
    };

    private static void SelectedTextReadable()
    {
        var accents = new string?[] { null, "#00FFFF", "#FFFFFF", "#FFF4B0", "#000000", "#0A0A3C", "#FF8800" };
        int combos = 0;
        foreach (var theme in ThemeCatalog.Concrete)
        {
            Use(theme.Id);
            Check(Contrast(SelFg(), SelBg()) >= 4.5 && Contrast(SelBg(), Brush("SurfaceBrush")) >= 3.0, $"{theme.Id}: selection pair is >= 4.5:1 and the fill stands out from the field (>= 3:1)");
            var wrong = new List<string>();
            for (int i = 0; i < 48; i++)
            {
                string hex = $"#{(i * 37) % 256:X2}{(i * 91 + 17) % 256:X2}{(i * 53 + 101) % 256:X2}"; if (i < 4) hex = new[] { "#000000", "#FFFFFF", "#00FFFF", "#FFFF00" }[i];
                Use(theme.Id, hex);
                if (Contrast(SelFg(), SelBg()) < 4.5 || !Same(SelBg(), Brush("AccentBrush"), 0) || Contrast(SelBg(), Brush("SurfaceBrush")) < 3.0) wrong.Add(hex);
            }
            Check(wrong.Count == 0, $"{theme.Id}: 48 custom accents give a readable selection pair (failing: {string.Join(",", wrong)})");
        }
        foreach (var theme in ThemeCatalog.Concrete)
            foreach (var accent in accents)
            {
                Use(theme.Id, accent);
                foreach (var (name, make) in SelectionControls())
                    foreach (bool focused in new[] { true, false })
                    {
                        var (fill, contrast, bg) = MeasureSelection(make, focused);
                        if (!focused && name is "PasswordBox" or "RichTextBox" && fill == 0) { combos++; continue; }
                        if (fill < 200 || contrast < 4.5) throw new Exception($"FAIL: {theme.Id} accent {accent ?? "theme"} {name} {(focused ? "focused" : "unfocused")}: selection pixels {fill}, text contrast {contrast:0.0}:1 on #{bg.R:X2}{bg.G:X2}{bg.B:X2}");
                        if (focused && name != "RichTextBox" && !Same(bg, SelBg(), 3)) throw new Exception($"FAIL: {theme.Id} accent {accent ?? "theme"} {name}: rendered selection fill #{bg.R:X2}{bg.G:X2}{bg.B:X2} is not SelectionBackgroundBrush");
                        combos++;
                    }
            }
        Use("Light");
        Check(true, $"Selected text is readable (>= 4.5:1 measured from rendered pixels) in {combos} theme x accent x control x focus combinations");
    }

    /// <summary>Capture dialog with the file name selected (the dialog selects it when it opens). Saved when NEODM_SHOTS is set; <paramref name="phase"/> names the file.</summary>
    private static void SelectionShots(string phase, string dir, string root)
    {
        foreach (var id in new[] { "Midnight", "Dark" })
        {
            Use(id);
            var dialog = new DownloadOptionsDialog("https://example.org/dir/archive-2026.zip", "archive-2026.zip", "", "", @"C:\Downloads") { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
            dialog.Show(); dialog.Activate(); Pump(250);
            var name = Field<TextBox>(dialog, "NameInput"); name.Focus(); Keyboard.Focus(name); name.SelectAll(); Pump(150);
            Save(Capture(dialog), Path.Combine(dir, $"selected-text-{id.ToLowerInvariant()}-{phase}.png")); Console.WriteLine("SHOT: selected-text-" + id.ToLowerInvariant() + "-" + phase);
            dialog.Close();
        }
        Use("Light");
    }
}
