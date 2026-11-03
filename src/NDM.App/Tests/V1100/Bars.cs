using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static partial class V1100Tests
{
    private static ProgressBar NewBar(string? kind, double value = 50, double width = 200, double height = 10, bool active = false)
    {
        var bar = new ProgressBar { Width = width, Height = height, Maximum = 100, Value = value };
        if (kind is not null) ProgressStatus.SetKind(bar, kind);
        ProgressStatus.SetActive(bar, active);
        return bar;
    }
    private static Color TokenColour(ResourceDictionary d, string kind) => ((SolidColorBrush)d[ProgressStatus.TokenFor(kind)]).Color;

    // ===== item 3: status colours =====
    private static void BarColours()
    {
        Motion.SystemAnimations = () => true;
        // status of a row -> kind
        var map = new (string Status, string Kind)[] { ("Downloading", "Downloading"), ("Connecting", "Downloading"), ("Checking", "Downloading"), ("Paused", "Paused"), ("Stopped", "Paused"), ("Canceled", "Paused"), ("Failed", "Failed"), ("Queued", "Queued"), ("Scheduled", "Queued"), ("Completed", "Completed") };
        foreach (var (status, kind) in map) Check(RowOf(Job("a.bin", status)).StatusKind == kind, $"Status {status} uses the {kind} colour");
        var merging = Job("v.mp4", "Downloading"); typeof(DownloadJob).GetProperty("IsYoutube")!.SetValue(merging, true); typeof(DownloadJob).GetProperty("YoutubePhase")!.SetValue(merging, "merging");
        Check(RowOf(merging).StatusKind == "Downloading", "Merging, converting and joining use the Downloading blue (work in progress, no tone of their own)");
        Check(new DownloadRow(new NDM.Torrent.TorrentJob("h", "x.iso", "C:\\x", 1, 1, 1, 100, 0, 5, 0, 0, 0, 1, "Seeding", null, Array.Empty<NDM.Torrent.TorrentFileJob>())).StatusKind == "Seeding", "A seeding torrent uses the teal colour");
        // table: kind -> token brush in every theme
        int cells = 0;
        foreach (var theme in ThemeCatalog.Concrete)
        {
            ThemeManager.Apply(Application.Current, theme.Id, null);
            var dict = ThemeManager.LoadFile(theme.Id);
            foreach (var kind in ProgressStatus.Kinds)
            {
                var bar = NewBar(kind);
                Check(ProgressStatus.ShownColour(bar) == TokenColour(dict, kind) && ProgressStatus.TargetColour(bar) == TokenColour(dict, kind), $"{theme.Id}: a {kind} bar is filled with {ProgressStatus.TokenFor(kind)}");
                cells++;
            }
            var plain = new ProgressBar(); var plainHost = new Border { Child = plain, Width = 100, Height = 10 }; plainHost.Measure(new Size(100, 10)); plainHost.Arrange(new Rect(0, 0, 100, 10)); plainHost.UpdateLayout();
            Check(((SolidColorBrush)plain.Foreground).Color == TokenColour(dict, "Downloading"), $"{theme.Id}: a bar without a state is Downloading blue (no old accent fill left)");
        }
        Check(cells == ThemeCatalog.Concrete.Count() * 6, "Every state of every theme was checked");
        // contrast: fill against track and rows, all themes, plus custom accents
        string?[] accents = { null, "#E91E63", "#FFC107", "#00BCD4", "#2D6CDF" };
        int pairs = 0;
        foreach (var theme in ThemeCatalog.Concrete) foreach (var accent in accents)
        {
            var d = ThemeManager.Build(theme.Id, accent);
            Color Of(string key) => ((SolidColorBrush)d[key]).Color;
            var backgrounds = new[] { ("track", Of("BorderBrush")), ("row", Of("SurfaceBrush")), ("hover", Of("ItemHoverBrush")), ("selected", Of("ItemSelectedBrush")), ("unfocused-selected", Of("ItemSelectedInactiveBrush")) };
            foreach (var kind in ProgressStatus.Kinds) foreach (var (label, bg) in backgrounds)
            {
                double c = ThemeManager.Contrast(Of(ProgressStatus.TokenFor(kind)), bg);
                if (c < 3) throw new Exception($"FAIL: {theme.Id} {accent ?? "default"}: {kind} fill against {label} is {c:0.00}:1 (3:1 needed)");
                pairs++;
            }
        }
        Check(pairs == ThemeCatalog.Concrete.Count() * accents.Length * 6 * 5, $"{pairs} fill and background pairs are at least 3:1 (all themes, custom accents, normal, hover, selected, unfocused-selected)");
        // the main list bars follow the row state
        ThemeManager.Apply(Application.Current, "Light", null);
    }

    private static void BarRender()
    {
        foreach (var theme in ThemeCatalog.Concrete)
        {
            ThemeManager.Apply(Application.Current, theme.Id, null);
            foreach (var rtl in new[] { false, true })
            {
                foreach (var kind in ProgressStatus.Kinds)
                {
                    var bar = NewBar(kind, 50, 200, 12);
                    var host = new Border { Background = (Brush)Application.Current.FindResource("SurfaceBrush"), Child = bar, Width = 220, Height = 24, FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
                    host.Measure(new Size(220, 24)); host.Arrange(new Rect(0, 0, 220, 24)); host.UpdateLayout();
                    var px = Pixels(Capture(host)); int stride = 220 * 4;
                    Color At(int x, int y) { int i = y * stride + x * 4; return Color.FromRgb(px[i + 2], px[i + 1], px[i]); }
                    int filledX = rtl ? 220 - 10 - 50 : 10 + 50, trackX = rtl ? 220 - 10 - 150 : 10 + 150;
                    var fill = At(filledX, 12); var track = At(trackX, 12);
                    var token = ((SolidColorBrush)Application.Current.FindResource(ProgressStatus.TokenFor(kind))).Color;
                    Check(Math.Abs(fill.R - token.R) + Math.Abs(fill.G - token.G) + Math.Abs(fill.B - token.B) <= 6, $"{theme.Id}{(rtl ? " RTL" : "")} {kind}: the rendered fill {FileTypeIcons.Hex(fill)} is the token {FileTypeIcons.Hex(token)}");
                    Check(ThemeManager.Contrast(fill, track) >= 3, $"{theme.Id} {kind}: rendered fill against the rendered track is {ThemeManager.Contrast(fill, track):0.0}:1");
                }
            }
        }
        // an indeterminate bar (unknown total) uses the same colour and is a full bar
        ThemeManager.Apply(Application.Current, "Dark", null);
        var ind = NewBar("Downloading", 0); ind.IsIndeterminate = true;
        var h2 = new Border { Background = (Brush)Application.Current.FindResource("SurfaceBrush"), Child = ind, Width = 220, Height = 24 };
        h2.Measure(new Size(220, 24)); h2.Arrange(new Rect(0, 0, 220, 24)); h2.UpdateLayout();
        var p2 = Pixels(Capture(h2)); var c2 = Color.FromRgb(p2[(12 * 220 + 200) * 4 + 2], p2[(12 * 220 + 200) * 4 + 1], p2[(12 * 220 + 200) * 4]);
        var blue = ((SolidColorBrush)Application.Current.FindResource("StatusDownloadingBrush")).Color;
        Check(Math.Abs(c2.R - blue.R) + Math.Abs(c2.G - blue.G) + Math.Abs(c2.B - blue.B) <= 6, "An indeterminate bar is filled with the Downloading colour over its whole length");
        ThemeManager.Apply(Application.Current, "Light", null);
        // the bars of the main list
    }

    private static void BarTransitions()
    {
        ThemeManager.Apply(Application.Current, "Light", null);
        Motion.SystemAnimations = () => true;
        var bar = NewBar("Downloading"); var win = new Window { Content = bar, Width = 300, Height = 80, ShowInTaskbar = false, Left = 10, Top = 10 }; win.Show(); Pump(150);
        var blue = ProgressStatus.ShownColour(bar);
        ProgressStatus.SetKind(bar, "Completed"); var green = ProgressStatus.TargetColour(bar);
        Check(green != blue && ProgressStatus.TargetColour(bar) == ((SolidColorBrush)Application.Current.FindResource("StatusCompletedBrush")).Color, "A state change sets the new target colour at once");
        Pump(70);
        var mid = ProgressStatus.ShownColour(bar);
        Check(mid != blue && mid != green, $"The shown colour is between the two while it fades ({FileTypeIcons.Hex(mid)})");
        Pump(500);
                Check(FileTypeIcons.Hex(ProgressStatus.ShownColour(bar)) == FileTypeIcons.Hex(green), "The fade ends on the new colour");
        // reduced motion: no fade
        Motion.SystemAnimations = () => false;
        ProgressStatus.SetKind(bar, "Failed"); var red = ProgressStatus.TargetColour(bar);
        Check(ProgressStatus.ShownColour(bar) == red, "With Windows animations off the colour changes at once");
        // a theme change recolours without a fade
        Motion.SystemAnimations = () => true;
        ThemeManager.Apply(Application.Current, "Dark", null); Pump(80);
        Check(ProgressStatus.ShownColour(bar) == ((SolidColorBrush)Application.Current.FindResource("StatusFailedBrush")).Color, "A theme change recolours the bar to the new token");
        ThemeManager.Apply(Application.Current, "Light", null);
        win.Close(); Pump(50);
    }

    // ===== item 5: the sweep =====
    private static Window BarWindow(out ProgressBar[] bars, int count = 2, bool rtl = false, double width = 300)
    {
        var panel = new StackPanel { Margin = new Thickness(10) };
        bars = new ProgressBar[count];
        for (int i = 0; i < count; i++) { bars[i] = NewBar("Downloading", 80, width - 40, 12, true); bars[i].Margin = new Thickness(0, 4, 0, 4); panel.Children.Add(bars[i]); }
        var win = new Window { Content = panel, Width = width, Height = 90 + count * 26, ShowInTaskbar = false, Left = 10, Top = 10, FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, Background = (Brush)Application.Current.FindResource("SurfaceBrush") };
        return win;
    }
    private static void WaveClock(string scratch)
    {
        Motion.SystemAnimations = () => true; ProgressWaveClock.UserEnabled = true; ThemeManager.Apply(Application.Current, "Light", null);
        Check(!ProgressWaveClock.IsRunning, "Nothing runs before a bar is shown");
        var win = BarWindow(out var bars); win.Show(); Pump(250);
        Check(ProgressWaveClock.IsRunning && ProgressStatus.WavingCount == 2 && ProgressStatus.IsWaving(bars[0]), "A visible downloading bar starts the one shared clock");
        int f0 = ProgressWaveClock.FrameCount; Pump(1000); int frames = ProgressWaveClock.FrameCount - f0;
        Check(frames is >= 15 and <= 36, $"The clock runs at about 30 frames a second ({frames} frames in one second, capped)");
        var tick = Stopwatch.StartNew(); long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++) ProgressWaveClock.SetTime(i * 0.001);
        long perFrame = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / 2000;
        Console.WriteLine($"MEASURE: brush transform update allocates {perFrame} bytes per frame, {tick.Elapsed.TotalMilliseconds / 2000 * 1000:0.0} microseconds");
        Check(perFrame <= 64, $"A frame moves one shared transform and allocates {perFrame} bytes");
        // the toggle
        ProgressWaveClock.UserEnabled = false; Pump(120);
        Check(!ProgressWaveClock.IsRunning && ProgressStatus.WavingCount == 0 && !ProgressStatus.IsWaving(bars[0]), "Switching the option off stops the clock and the sweep at once");
        ProgressWaveClock.UserEnabled = true; Pump(150);
        Check(ProgressWaveClock.IsRunning && ProgressStatus.IsWaving(bars[0]), "Switching it on starts it again without a restart");
        // Windows animations off: overrides the option
        Motion.SystemAnimations = () => false; Pump(200);
        Check(!ProgressWaveClock.IsRunning && ProgressStatus.WavingCount == 0 && ProgressWaveClock.OverriddenBySystem, "When Windows animations are off the sweep stops even though the option is on");
        Motion.SystemAnimations = () => true; ProgressWaveClock.WindowStateChanged(); ProgressStatus.RefreshAll(); Pump(150);
        Check(ProgressWaveClock.IsRunning, "It resumes when Windows animations come back");
        // bars that are not downloading do not sweep
        ProgressStatus.SetActive(bars[0], false); ProgressStatus.SetActive(bars[1], false); Pump(150);
        Check(!ProgressWaveClock.IsRunning && ProgressStatus.WavingCount == 0, "When no bar is receiving data the clock stops");
        ProgressStatus.SetActive(bars[0], true); Pump(100);
        Check(ProgressWaveClock.IsRunning && ProgressStatus.WavingCount == 1, "Only the active bar counts");
        bars[0].Visibility = Visibility.Collapsed; Pump(100);
        Check(!ProgressWaveClock.IsRunning, "A bar that is not visible does not keep the clock running");
        bars[0].Visibility = Visibility.Visible; Pump(100);
        // minimised and hidden
        win.WindowState = WindowState.Minimized; Pump(250);
        Check(!ProgressWaveClock.IsRunning, "Nothing runs while the window is minimised");
        win.WindowState = WindowState.Normal; Pump(250);
        Check(ProgressWaveClock.IsRunning, "It runs again when the window is restored");
        win.Hide(); Pump(200);
        Check(!ProgressWaveClock.IsRunning, "Nothing runs while the window is hidden (tray)");
        win.Show(); Pump(200);
        Check(ProgressWaveClock.IsRunning, "It runs again when the window is shown");
        // one clock for many bars: no timer per bar
        var many = BarWindow(out var list, 150, false, 340); many.Show(); Pump(400);
        int m0 = ProgressWaveClock.FrameCount; Pump(1000); int manyFrames = ProgressWaveClock.FrameCount - m0;
        Check(ProgressStatus.WavingCount == 151 && manyFrames is >= 12 and <= 36, $"150 more bars share the same clock ({manyFrames} frames a second, {ProgressStatus.WavingCount} bars sweeping)");
        many.Close(); Pump(150);
        Check(ProgressStatus.WavingCount == 1, "Closed windows leave nothing behind in the clock");
        // handles and memory stay flat over many start and stop cycles
        var proc = Process.GetCurrentProcess(); proc.Refresh(); int h0 = proc.HandleCount; long mem0 = GC.GetTotalMemory(true);
        for (int i = 0; i < 300; i++) { ProgressWaveClock.UserEnabled = i % 2 == 0; ProgressWaveClock.UserEnabled = true; }
        for (int i = 0; i < 30; i++) { var w = BarWindow(out _, 3); w.Show(); Pump(15); w.Close(); }
        Pump(200); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); proc.Refresh(); long mem1 = GC.GetTotalMemory(true);
        Console.WriteLine($"MEASURE: handles {h0} -> {proc.HandleCount}, managed memory {mem0 / 1024} KB -> {mem1 / 1024} KB after 300 toggles and 30 windows");
        Check(proc.HandleCount - h0 <= 25 && mem1 - mem0 < 8 * 1024 * 1024, "Handles and memory stay flat across start, stop and window cycles");
        win.Close(); Pump(100);
        Check(!ProgressWaveClock.IsRunning, "Nothing runs once every window is closed");
        // the rows decide
        string[] still = { "Paused", "Queued", "Scheduled", "Failed", "Completed", "Connecting" };
        foreach (var st in still) Check(!RowOf(Job("a.bin", st, 1000, 10, 500)).IsTransferring, $"A {st} row does not sweep");
        Check(RowOf(Job("a.bin", "Downloading", 1000, 10, 500)).IsTransferring, "A downloading row with bytes moving sweeps");
        Check(!RowOf(Job("a.bin", "Downloading", 1000, 10, 0)).IsTransferring, "A downloading row whose bytes do not move (merging, waiting) does not sweep");
        Check(RowOf(Job("a.bin", "Downloading", 0, 10, 500)).IsUnknownTotal && RowOf(Job("a.bin", "Downloading", 0, 10, 500)).IsTransferring, "An unknown total sweeps as a full bar");
    }

    private static void WaveLook()
    {
        double Hue(Color c) { double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn; if (d < 1e-6) return 0; double h = mx == r ? ((g - b) / d) % 6 : mx == g ? (b - r) / d + 2 : (r - g) / d + 4; return (h * 60 + 360) % 360; }
        Check(ProgressWaveClock.PeriodSeconds is >= 1.6 and <= 2.4 && ProgressWaveClock.FrameMilliseconds >= 33, "One sweep takes about two seconds and the frame rate is capped at 30");
        Check(Math.Abs(ProgressWaveClock.OffsetAt(0) + 0.7) < 1e-9 && Math.Abs(ProgressWaveClock.OffsetAt(1.0)) < 1e-9 && ProgressWaveClock.OffsetAt(1.999) > 0.69, "The band travels from before the left edge to past the right edge");
        double prev = -1; bool rising = true; for (double t = 0; t < 2.0; t += 0.05) { var v = ProgressWaveClock.OffsetAt(t); rising &= v >= prev; prev = v; }
        Check(rising, "The motion only goes forward within a sweep");
        Check(Math.Abs(ProgressWaveClock.OffsetAt(0.05) - ProgressWaveClock.OffsetAt(0)) * 3 < Math.Abs(ProgressWaveClock.OffsetAt(1.05) - ProgressWaveClock.OffsetAt(1.0)), "The edges are eased (slow start and end)");
        int n = 0;
        foreach (var theme in ThemeCatalog.Concrete) foreach (var accent in new string?[] { null, "#E91E63", "#00BCD4" })
        {
            var d = ThemeManager.Build(theme.Id, accent);
            Color Of(string key) => ((SolidColorBrush)d[key]).Color;
            var fill = Of("StatusDownloadingBrush"); bool dark = ThemeManager.Luminance(Of("SurfaceBrush")) < 0.3;
            var band = ProgressWaveClock.BandColour(fill, dark);
            double dh = Math.Abs(Hue(band) - Hue(fill)); dh = Math.Min(dh, 360 - dh);
            Check(dh <= 4, $"{theme.Id} {accent ?? "default"}: the band is the same hue as the fill (difference {dh:0.0} degrees)");
            foreach (var bgKey in new[] { "BorderBrush", "SurfaceBrush", "ItemHoverBrush", "ItemSelectedBrush", "ItemSelectedInactiveBrush" })
                if (ThemeManager.Contrast(band, Of(bgKey)) < 3) throw new Exception($"FAIL: {theme.Id} {accent}: the band against {bgKey} is {ThemeManager.Contrast(band, Of(bgKey)):0.00}:1");
            Check(ThemeManager.Contrast(band, Of("BorderBrush")) >= ThemeManager.Contrast(fill, Of("BorderBrush")), $"{theme.Id}: the band never lowers the contrast of the fill");
            n++;
        }
        Check(n == ThemeCatalog.Concrete.Count() * 3, "Every theme was checked with and without custom accents");
        // pictures: the band is where the clock says, mirrored in right-to-left layouts, absent when not downloading
        Motion.SystemAnimations = () => true; ProgressWaveClock.UserEnabled = true; ThemeManager.Apply(Application.Current, "Dark", null);
        double PeakColumn(bool rtl, bool active, double time)
        {
            var win = BarWindow(out var bs, 1, rtl, 340); ProgressStatus.SetActive(bs[0], active); bs[0].Value = 100; win.Show(); Pump(200); win.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight; Pump(120);
            var shown = ProgressWaveClock.AnyWindowShown; ProgressWaveClock.AnyWindowShown = () => false; Pump(120); /* the clock stops itself: a still frame */ ProgressWaveClock.SetTime(time); Pump(40);
            var bmp = Capture(win); var px = Pixels(bmp);
            double x1 = bs[0].TranslatePoint(new Point(0, 6), win).X, x2 = bs[0].TranslatePoint(new Point(bs[0].ActualWidth, 6), win).X; double left = Math.Min(x1, x2), barW = Math.Abs(x2 - x1);
            int y = (int)bs[0].TranslatePoint(new Point(0, 6), win).Y; double best = -1; int bestX = 0;
            for (int x = (int)left + 4; x < (int)(left + barW) - 4; x++) { int i = (y * bmp.PixelWidth + x) * 4; double l = px[i] + px[i + 1] + px[i + 2]; if (l > best + 0.5) { best = l; bestX = x; } }
            if (Shots is { Length: > 0 } dir && active) { Save(bmp, Path.Combine(dir, $"wave-{(rtl ? "rtl" : "ltr")}.png")); Console.WriteLine("SHOT: wave-" + (rtl ? "rtl" : "ltr")); }
            ProgressWaveClock.AnyWindowShown = shown; win.Close(); Pump(40);
            return (bestX - left) / barW;
        }
        double ltr = PeakColumn(false, true, 0.8), rtl = PeakColumn(true, true, 0.8);
        Check(ltr is > 0.18 and < 0.42, $"At 0.8 s the band is in the left part ({ltr:0.00} of the width)");
        Check(rtl is > 0.58 and < 0.82 && Math.Abs((1 - rtl) - ltr) < 0.08, $"In a right-to-left layout it is mirrored and moves right to left ({rtl:0.00})");
        var flat = BarWindow(out var fb, 1, false, 340); ProgressStatus.SetActive(fb[0], false); fb[0].Value = 100; flat.Show(); Pump(200);
        var fbmp = Capture(flat); var fpx = Pixels(fbmp); int fy = (int)fb[0].TranslatePoint(new Point(0, 6), flat).Y; int fl = (int)fb[0].TranslatePoint(new Point(0, 6), flat).X;
        int minL = 9999, maxL = 0; for (int x = fl + 6; x < fl + (int)fb[0].ActualWidth - 6; x++) { int i = (fy * fbmp.PixelWidth + x) * 4; int l = fpx[i] + fpx[i + 1] + fpx[i + 2]; minL = Math.Min(minL, l); maxL = Math.Max(maxL, l); }
        Check(maxL - minL <= 3, "A bar that is not downloading is a plain fill (no band)");
        flat.Close(); Pump(50);
        ThemeManager.Apply(Application.Current, "Light", null);
    }

    private static void WaveSettings(string scratch)
    {
        Motion.SystemAnimations = () => true; ProgressWaveClock.UserEnabled = true;
        var settings = Settings(Path.Combine(scratch, "opt", "dl"));
        var state = Path.Combine(scratch, "opt", "NDM"); Directory.CreateDirectory(state);
        Check(settings.AnimateProgressBars, "The animation option is on by default");
        var win = new SettingsWindow(settings, state) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 }; win.Show(); Pump(200);
        var box = Field<CheckBox>(win, "AnimateBars"); var note = Field<TextBlock>(win, "AnimateBarsNote"); var help = Field<TextBlock>(win, "AnimateBarsHelp");
        win.FindName("OptionsTabs"); ((TabControl)win.FindName("OptionsTabs")).SelectedItem = win.FindName("AppearanceTab"); Pump(100);
        Check(box.IsChecked == true && box.Content as string == "Animate progress bars while downloading" && help.Text.Length > 20 && note.Visibility == Visibility.Collapsed, "Options > Appearance has the checkbox, on, with its help text");
        box.IsChecked = false; Pump(60);
        Check(!ProgressWaveClock.UserEnabled, "Unchecking applies at once, no restart");
        win.Close(); Pump(100);
        Check(ProgressWaveClock.UserEnabled && settings.AnimateProgressBars, "Closing without Save puts the setting back");
        // Save keeps it
        var win2 = new SettingsWindow(settings, state) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 }; win2.Show(); Pump(200);
        Field<CheckBox>(win2, "AnimateBars").IsChecked = false; Pump(60);
        var save = Visual<Button>(win2).First(b => b.IsDefault); save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(300);
        Check(!settings.AnimateProgressBars && !ProgressWaveClock.UserEnabled && AppSettings.Load(state).AnimateProgressBars == false, "Save keeps the choice in the settings file");
        // system override note
        settings.AnimateProgressBars = true; ProgressWaveClock.UserEnabled = true; Motion.SystemAnimations = () => false;
        var win3 = new SettingsWindow(settings, state) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 }; win3.Show(); Pump(200);
        Check(Field<TextBlock>(win3, "AnimateBarsNote").Visibility == Visibility.Visible && Field<TextBlock>(win3, "AnimateBarsNote").Text.Contains("Windows"), "When Windows animations are off the checkbox shows a note that the system setting wins");
        if (Shots is { Length: > 0 } dir) { ((TabControl)win3.FindName("OptionsTabs")).SelectedItem = win3.FindName("AppearanceTab"); Pump(150); foreach (var sv in Visual<ScrollViewer>((FrameworkElement)win3.FindName("AppearanceTab")).Take(1)) sv.ScrollToEnd(); Pump(150); Save(Capture(win3), Path.Combine(dir, "options-appearance.png")); Console.WriteLine("SHOT: options-appearance"); }
        win3.Close(); Pump(60); Motion.SystemAnimations = () => true; ProgressWaveClock.UserEnabled = true;
        // every language has the three texts
        string[] langs = { "en", "ar", "es", "fr", "de", "pt-BR", "ru", "tr", "zh-Hans", "ja" };
        foreach (var l in langs)
        {
            Loc.SetLanguage(l);
            foreach (var key in new[] { "options.chk.animate_bars", "options.text.animate_bars_help", "options.text.animate_bars_system", "nav.cat.other", "cat.builtin.pictures", "disk.activity_rw", "filetype.a11y" })
                if (Loc.T(key) == key) throw new Exception($"FAIL: {l} lacks {key}");
        }
        Loc.SetLanguage("en");
        Check(true, "All new texts exist in the ten languages");
        // the main window follows the saved setting live
        using var m = NewManager(scratch, "live");
        var s = Settings(Path.Combine(scratch, "live", "dl")); var main = NewMain(m, s); main.Show(); Pump(200);
        s.AnimateProgressBars = false; Pump(700);
        Check(!ProgressWaveClock.UserEnabled, "The main window applies a changed option without a restart");
        s.AnimateProgressBars = true; Pump(700); main.Close(); Pump(60);
        Check(ProgressWaveClock.UserEnabled, "...and switches it back on");
    }
}
