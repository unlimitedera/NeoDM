// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;

internal static partial class V1160Tests
{
    private static IEnumerable<Color> FillStops(Brush b) => b is LinearGradientBrush g ? g.GradientStops.Select(s => s.Color) : b is SolidColorBrush s1 ? new[] { s1.Color } : Array.Empty<Color>();

    /// <summary>The shared PrimaryButton style takes its text colour from OnAccentBrush; it must reach 4.5:1 on its fill in every theme and custom accent.</summary>
    private static void PrimaryContrast()
    {
        var app = Application.Current; var accents = new string?[] { null, "#006C67", "#FF0000", "#00FF00", "#FFFF00", "#0000FF", "#FFFFFF", "#000000", "#FF8800", "#7A3EFF" };
        var style = (Style)app.FindResource("PrimaryButton"); int n = 0;
        Check(style.Setters.OfType<Setter>().Any(s => s.Property == Control.ForegroundProperty && s.Value is DynamicResourceExtension { ResourceKey: "OnAccentBrush" }), "PrimaryButton reads its text colour from OnAccentBrush");
        Check(!style.Triggers.OfType<Trigger>().Any(t => t.Setters.OfType<Setter>().Any(s => s.Property == Control.ForegroundProperty)), "PrimaryButton has no hover text colour that could break the contrast");
        foreach (var theme in ThemeCatalog.Concrete) foreach (var accent in accents)
        {
            var d = ThemeManager.Build(theme.Id, accent);
            var on = ((SolidColorBrush)d["OnAccentBrush"]).Color;
            foreach (var stop in FillStops((Brush)d["AccentFillBrush"]))
            {
                double c = ThemeManager.Contrast(on, stop); n++;
                if (c < 4.5) throw new Exception($"FAIL: primary text {on} on {stop} in {theme.Id}/{accent} is {c:0.0}:1");
            }
        }
        Check(n > 0, $"primary button text reaches 4.5:1 on its fill in every theme and accent ({n} pairs)");
        // the real button, rendered: the text pixels differ from the fill by the right amount in Light and Deep Navy
        foreach (var id in new[] { "Light", "DeepNavy" })
        {
            ThemeManager.Apply(app, id, null);
            var button = new Button { Content = "Add", Style = (Style)app.FindResource("PrimaryButton"), Width = 160, Height = 48, FontSize = 22, FontWeight = FontWeights.Bold };
            var host = new Window { Content = new Grid { Children = { button }, Margin = new Thickness(20) }, Width = 260, Height = 160, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = 0 };
            host.Show(); Pump(150); host.UpdateLayout();
            var fill = FillStops((Brush)app.FindResource("AccentFillBrush")).First();
            var bmp = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(host); if (Environment.GetEnvironmentVariable("NEODM_SHOTS") is { Length: > 0 } sd) { Directory.CreateDirectory(sd); var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var fs = File.Create(Path.Combine(sd, $"primary-{id}.png")); enc.Save(fs); }
            var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4]; bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
            double best = 0; for (int i = 0; i < px.Length; i += 4) { int x = i / 4 % bmp.PixelWidth, y = i / 4 / bmp.PixelWidth; if (px[i + 3] < 255 || x < 40 || y < 50 || x > bmp.PixelWidth - 40 || y > bmp.PixelHeight - 50) continue; var c = Color.FromRgb(px[i + 2], px[i + 1], px[i]); best = Math.Max(best, ThemeManager.Contrast(c, fill)); }
            Check(best >= 4.5, $"rendered primary button in {id} shows text at {best:0.0}:1 against its fill");
            host.Close();
        }
        ThemeManager.Apply(app, "Light", null);
    }
}
