using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;

/// <summary>1.9.0 item 4: the five category folder text boxes in Options > Save To must show whole, unclipped text in every theme at 100 to 200 percent DPI and at small and large window sizes. Checked on rendered pixels and on measured text, not only on properties.</summary>
internal static partial class ThemeTests
{
    private static readonly string[] CategoryBoxNames = { "ProgramsFolder", "CompressedFolder", "MusicFolder", "VideoFolder", "DocumentsFolder" };

    /// <summary>Rows of ink (pixels that differ clearly from the box background) inside a rectangle of a rendered window; null when there is no ink.</summary>
    private static (int Top, int Bottom, int Left, int Right)? InkBox(BitmapSource shot, Rect box, Color background)
    {
        int x0 = Math.Max(0, (int)Math.Ceiling(box.Left)), x1 = Math.Min(shot.PixelWidth, (int)Math.Floor(box.Right)), y0 = Math.Max(0, (int)Math.Ceiling(box.Top)), y1 = Math.Min(shot.PixelHeight, (int)Math.Floor(box.Bottom));
        int top = int.MaxValue, bottom = -1, left = int.MaxValue, right = -1;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (Contrast(Px(shot, x, y), background) >= 2.2) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); left = Math.Min(left, x); right = Math.Max(right, x); }
        return bottom < 0 ? null : (top, bottom, left, right);
    }

    private static RenderTargetBitmap CaptureAt(Window window, double scale)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        int w = (int)Math.Ceiling(content.ActualWidth * scale), h = (int)Math.Ceiling(content.ActualHeight * scale);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen()) { ctx.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight)); ctx.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight)); }
        var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(visual); return bitmap;
    }

    private static void CategoryBoxes(string root)
    {
        string? shots = Environment.GetEnvironmentVariable("NEODM_SHOTS");
        string files = Path.Combine(root, "category-files"); Directory.CreateDirectory(files);
        var sample = new[] { "Programs/gypsy Qj", "Compressed/pqy,Jg", "Music\\gyp Qj", "Video/Wgjpqy", "Documents/Hgyp" };
        int boxes = 0; var problems = new List<string>();
        foreach (var theme in ThemeCatalog.Concrete)
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                Use(theme.Id); string tag = $"{theme.Id} at {scale * 100:0}%";
                var settings = new AppSettings { DownloadDirectory = files, Theme = theme.Id, EnableCategoryFolders = true };
                var win = new SettingsWindow(settings, Path.Combine(root, "cat-" + theme.Id + scale.ToString("0.00", CultureInfo.InvariantCulture))) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
                VisualTreeHelper.SetRootDpi(win, new DpiScale(scale, scale));
                win.Show(); Pump(60);
                var tabs = Field<TabControl>(win, "OptionsTabs"); string saveTo = Loc.T("options.tab.save_to");
                tabs.SelectedItem = tabs.Items.Cast<TabItem>().First(t => Equals(t.Header, saveTo)); Pump(60); win.UpdateLayout();
                var grid = Field<Grid>(win, "CategoryGrid");
                if (!grid.IsEnabled) { Field<CheckBox>(win, "EnableCategoryFolders").IsChecked = true; Pump(40); }
                var content = (FrameworkElement)win.Content;
                var textBoxes = CategoryBoxNames.Select(n => Field<TextBox>(win, n)).ToArray();
                for (int i = 0; i < textBoxes.Length; i++) textBoxes[i].Text = sample[i];
                win.UpdateLayout();
                var shot = CaptureAt(win, scale); var surface = Brush("SurfaceBrush"); var ink = Brush("InkBrush");
                var typeface = new Typeface(textBoxes[0].FontFamily, textBoxes[0].FontStyle, textBoxes[0].FontWeight, textBoxes[0].FontStretch);
                double em = textBoxes[0].FontSize;
                Rect? previous = null;
                for (int i = 0; i < textBoxes.Length; i++)
                {
                    var tb = textBoxes[i]; boxes++;
                    tb.BringIntoView(); Pump(20); win.UpdateLayout(); shot = CaptureAt(win, scale); // the tab scrolls: bring each box into view before it is rendered
                    var rect = tb.TransformToAncestor(content).TransformBounds(new Rect(0, 0, tb.ActualWidth, tb.ActualHeight));
                    var host = (ScrollViewer)tb.Template.FindName("PART_ContentHost", tb);
                    double line = new FormattedText("Hgyp", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, em, Brushes.Black, scale).Height;
                    if (host.ActualHeight + 0.5 < line) problems.Add($"{tag} {tb.Name}: text area {host.ActualHeight:0.0} DIPs is shorter than one line ({line:0.0})");
                    if (host.ScrollableHeight > 0.5) problems.Add($"{tag} {tb.Name}: the text box can scroll vertically ({host.ScrollableHeight:0.0})");
                    var inGrid = tb.TransformToAncestor(grid).TransformBounds(new Rect(0, 0, tb.ActualWidth, tb.ActualHeight));
                    if (previous is { } p && inGrid.Top < p.Bottom - 0.5) problems.Add($"{tag} {tb.Name}: overlaps the box above");
                    previous = inGrid;
                    var device = new Rect(rect.Left * scale, rect.Top * scale, rect.Width * scale, rect.Height * scale);
                    var inner = new Rect(device.Left + 3 * scale, device.Top + 2 * scale, device.Width - 6 * scale, device.Height - 4 * scale);
                    var inkBox = InkBox(shot, inner, surface);
                    if (inkBox is null) { problems.Add($"{tag} {tb.Name}: no text pixels at all"); continue; }
                    double inkHeight = (inkBox.Value.Bottom - inkBox.Value.Top + 1) / scale;
                    if (inkHeight < em * 0.78) problems.Add($"{tag} {tb.Name}: text is {inkHeight:0.0} DIPs tall, a whole line of ascenders and descenders needs about {em * 0.78:0.0} (clipped)");
                    if (inkBox.Value.Top <= inner.Top + 0.5 || inkBox.Value.Bottom >= inner.Bottom - 0.5) problems.Add($"{tag} {tb.Name}: text touches the edge of the box (clipped)");
                    var label = Visual<Label>(grid).FirstOrDefault(l => ReferenceEquals(l.Target, tb));
                    if (label is not null)
                    {
                        var lr = label.TransformToAncestor(content).TransformBounds(new Rect(0, 0, label.ActualWidth, label.ActualHeight));
                        if (Math.Abs((lr.Top + lr.Bottom) / 2 - (rect.Top + rect.Bottom) / 2) > 6) problems.Add($"{tag} {tb.Name}: label is not level with its box");
                    }
                }
                if (shots is { Length: > 0 } && theme.Id is "Midnight" or "DeepNavy" or "Light" && scale is 1.0 or 1.5) { Save(shot, Path.Combine(shots, $"category-{theme.Id}-{scale * 100:0}.png")); Console.WriteLine($"SHOT: category-{theme.Id}-{scale * 100:0}"); }
                win.Close(); Pump(20);
            }
        Check(problems.Count == 0, $"Category folder text boxes show whole text in {boxes} renders ({ThemeCatalog.Concrete.Count()} themes x 4 DPI scales x 5 boxes): " + string.Join(" | ", problems.Take(6)));
        Use("Light");
    }

    private static void CategoryBoxesResize(string root)
    {
        string files = Path.Combine(root, "category-files"); Directory.CreateDirectory(files);
        var settings = new AppSettings { DownloadDirectory = files, EnableCategoryFolders = true };
        foreach (var theme in new[] { "Light", "Midnight", "DeepNavy" })
        {
            Use(theme);
            var win = new SettingsWindow(settings, Path.Combine(root, "cat-size-" + theme)) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
            win.Show(); Pump(60);
            var tabs = Field<TabControl>(win, "OptionsTabs"); tabs.SelectedItem = tabs.Items.Cast<TabItem>().First(t => Equals(t.Header, Loc.T("options.tab.save_to"))); Pump(40);
            var box = Field<TextBox>(win, "ProgramsFolder"); box.Text = "Programs/gypsy";
            double smallWidth = 0, largeWidth = 0;
            foreach (var (w, h) in new[] { (win.MinWidth, win.MinHeight), (1000.0, 800.0) })
            {
                win.Width = Math.Max(w, win.MinWidth); win.Height = Math.Max(h, win.MinHeight); Pump(120); win.UpdateLayout();
                var host = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
                var line = new FormattedText("Hgyp", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch), box.FontSize, Brushes.Black, 1.0).Height;
                Check(host.ActualHeight + 0.5 >= line && box.ActualHeight >= 36, $"{theme}: category box keeps a full text line at {win.ActualWidth:0}x{win.ActualHeight:0} (text area {host.ActualHeight:0.0}, line {line:0.0})");
                if (w == 1000.0) largeWidth = box.ActualWidth; else smallWidth = box.ActualWidth;
            }
            Check(largeWidth > smallWidth + 100, $"{theme}: the category boxes grow with the window ({smallWidth:0} to {largeWidth:0})");
            win.Close(); Pump(20);
        }
        Use("Light");
    }
}
