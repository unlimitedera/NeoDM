using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static partial class LocalizationTests
{
    private static bool InsideScroll(DependencyObject c, FrameworkElement root)
    {
        for (var p = VisualTreeHelper.GetParent(c); p is not null && !ReferenceEquals(p, root); p = VisualTreeHelper.GetParent(p)) if (p is ScrollViewer or ListView or TreeView or ListBox or ComboBox) return true;
        return false;
    }
    private static Rect Bounds(FrameworkElement c, FrameworkElement root) => c.TransformToAncestor(root).TransformBounds(new Rect(0, 0, c.ActualWidth, c.ActualHeight));

    /// <summary>Controls outside the window, controls that overlap a sibling button, and text that is arranged smaller than it needs (clipped) without a designed trimming.</summary>
    internal static List<string> LayoutIssues(Window w)
    {
        var root = (FrameworkElement)w.Content; var issues = new List<string>();
        var controls = Visual<Control>(w).Where(c => c.IsVisible && c.TemplatedParent is null && c.ActualWidth > 0 && c.ActualHeight > 0 && c is Button or TextBox or ComboBox or CheckBox or RadioButton or ListView or ListBox or TreeView or TabControl or PasswordBox).ToList();
        foreach (var c in controls)
        {
            var r = Bounds(c, root); string label = $"{c.GetType().Name} {c.Name}".Trim();
            if (r.Left < -0.5 || r.Right > root.ActualWidth + 0.5) issues.Add($"{label} horizontally outside ({r.Left:0}..{r.Right:0} of {root.ActualWidth:0})");
            if (!InsideScroll(c, root) && (r.Top < -0.5 || r.Bottom > root.ActualHeight + 0.5)) issues.Add($"{label} vertically outside ({r.Top:0}..{r.Bottom:0} of {root.ActualHeight:0})");
        }
        var buttons = controls.OfType<ButtonBase>().Where(b => b is not ToggleButton || b is Button).ToList();
        for (int i = 0; i < buttons.Count; i++)
            for (int j = i + 1; j < buttons.Count; j++)
            {
                if (buttons[i].IsDescendantOf(buttons[j]) || buttons[j].IsDescendantOf(buttons[i])) continue;
                var a = Bounds(buttons[i], root); var b = Bounds(buttons[j], root); a.Inflate(-1, -1); b.Inflate(-1, -1);
                if (a.IntersectsWith(b)) issues.Add($"buttons {buttons[i].Name}/{buttons[j].Name} overlap");
            }
        foreach (var t in Visual<TextBlock>(w).Where(t => t.IsVisible && t.ActualWidth > 0 && t.TextTrimming == TextTrimming.None && !string.IsNullOrWhiteSpace(t.Text)))
        {
            if (InsideScroll(t, root) || t.TemplatedParent is not null && Visual<Popup>(w).Any()) { }
            if (t.TemplatedParent is not null) { if (t.TemplatedParent is not (Button or CheckBox or RadioButton or ContentPresenter)) continue; }
            else if (InsideScroll(t, root)) continue;
            var d = new Size(Math.Max(0, t.DesiredSize.Width - t.Margin.Left - t.Margin.Right), Math.Max(0, t.DesiredSize.Height - t.Margin.Top - t.Margin.Bottom)); // DesiredSize includes the margin
            if (t.ActualWidth + 1 < d.Width || t.ActualHeight + 1 < d.Height) issues.Add($"text clipped: \"{(t.Text.Length > 30 ? t.Text[..30] : t.Text)}\" ({t.ActualWidth:0}x{t.ActualHeight:0} < {d.Width:0}x{d.Height:0})");
        }
        return issues;
    }

    // ===== every window in the pseudo-locale and in Arabic RTL at 100/125/150 % =====
    private static void LayoutSweep(string root)
    {
        using var manager = NewManager(root, "sweep");
        var job = Sync(manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/some/long/path/file-name.iso"), DateTimeOffset.UtcNow.AddDays(1)));
        var cases = Cases(root, manager, job);
        var failures = new List<string>(); int windows = 0;
        foreach (var (language, scales) in new[] { (LanguageCatalog.Pseudo, new[] { 1.0 }), ("ar", new[] { 1.0, 1.25, 1.5 }) })
        {
            Loc.SetLanguage(language);
            try
            {
                foreach (double scale in scales)
                {
                    var work = new Rect(0, 0, 1920 / scale, 1040 / scale);
                    foreach (var c in cases)
                    {
                        var w = Open(c, work);
                        try
                        {
                            var issues = LayoutIssues(w); windows++;
                            if (issues.Count > 0 || w.ActualWidth > work.Width + 0.5 || w.ActualHeight > work.Height + 0.5)
                                failures.Add($"{language} {scale * 100:0}%: {c.Name} {w.ActualWidth:0}x{w.ActualHeight:0}: {string.Join("; ", issues.Take(6))}");
                        }
                        finally { CloseWindow(w); }
                    }
                }
            }
            finally { Loc.SetLanguage("en"); }
        }
        Check(failures.Count == 0, $"pseudo-locale (100%) and Arabic RTL (100/125/150%): {windows} window openings fit the work area and clip nothing\n  " + string.Join("\n  ", failures));
    }

    // ===== Arabic screenshots =====
    /// <summary>Renders an element exactly as it is drawn on screen. A window is rendered directly (a VisualBrush of its content would apply the right-to-left mirror a second time and flip the glyphs).</summary>
    internal static RenderTargetBitmap Snapshot(FrameworkElement element)
    {
        element.UpdateLayout();
        int w = (int)Math.Ceiling(element.ActualWidth), h = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        if (element is Window) { bitmap.Render(element); return bitmap; }
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen()) ctx.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(visual); return bitmap;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectNative rect);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }
    /// <summary>A popup is its own window, so it is copied from the screen (a visual snapshot of its content would be mirrored twice).</summary>
    private static void ScreenCopy(FrameworkElement popupChild, string path)
    {
        if (PresentationSource.FromVisual(popupChild) is not System.Windows.Interop.HwndSource source || !GetWindowRect(source.Handle, out var r)) return;
        int width = r.Right - r.Left, height = r.Bottom - r.Top; if (width <= 0 || height <= 0) return;
        using var bitmap = new System.Drawing.Bitmap(width, height); using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(width, height));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bitmap)); using var f = File.Create(path); enc.Save(f);
    }

    private static void Screenshots(string root)
    {
        string? shots = Environment.GetEnvironmentVariable("NEODM_SHOTS");
        using var manager = NewManager(root, "shots");
        var job = Sync(manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/some/path/%D8%AA%D9%86%D8%B2%D9%8A%D9%84-ubuntu-24.04.iso"), DateTimeOffset.UtcNow.AddDays(1)));
        Sync(manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/archive/project-source.zip"), DateTimeOffset.UtcNow.AddDays(2)));
        var cases = Cases(root, manager, job).ToDictionary(c => c.Name);
        var wanted = new[] { ("Main", "main"), ("Options", "options"), ("Progress", "progress"), ("Capture dialog", "capture"), ("About", "about") };
        Loc.SetLanguage("ar");
        try
        {
            foreach (var theme in new[] { "Midnight", "Light" })
            {
                ThemeManager.Apply(Application.Current, theme, null);
                foreach (var (name, file) in wanted)
                {
                    var w = Open(cases[name], new Rect(0, 0, 1920, 1040));
                    try
                    {
                        if (name == "Options") { var combo = Named<ComboBox>(w, "LanguageBox"); var choices = ((System.Collections.IEnumerable)combo.ItemsSource).Cast<SettingsWindow.LanguageChoice>().ToList();
                            Check(choices[0].Tag == "system" && choices.Any(c => c.Tag == "ar" && c.Primary == "العربية" && c.Secondary == " - Arabic") && choices.Any(c => c.Tag == "en") && choices.Count == 1 + LanguageCatalog.Installed().Count, "Options language row: system default plus every installed language, English name muted"); }
                        var bitmap = Snapshot(w);
                        Check(bitmap.PixelWidth > 300 && bitmap.PixelHeight > 150, $"Arabic {theme} {name} rendered {bitmap.PixelWidth}x{bitmap.PixelHeight}");
                        if (!string.IsNullOrEmpty(shots)) SavePng(bitmap, Path.Combine(shots, $"arabic-{file}-{theme.ToLowerInvariant()}.png"));
                        if (name == "Options" && !string.IsNullOrEmpty(shots))
                        {
                            var combo = Named<ComboBox>(w, "LanguageBox"); combo.IsDropDownOpen = true; Pump(250);
                            try
                            {
                                combo.ApplyTemplate(); var popup = (combo.Template.FindName("PART_Popup", combo) as Popup)?.Child as FrameworkElement;
                                if (popup is not null && popup.ActualWidth > 0) ScreenCopy(popup, Path.Combine(shots, $"arabic-options-language-list-{theme.ToLowerInvariant()}.png"));
                            }
                            finally { combo.IsDropDownOpen = false; }
                        }
                    }
                    finally { CloseWindow(w); }
                }
            }
        }
        finally { Loc.SetLanguage("en"); ThemeManager.Apply(Application.Current, "Light", null); }
    }
}
