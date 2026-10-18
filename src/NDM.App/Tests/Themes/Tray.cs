using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.Core;
using NDM.Tests;

/// <summary>1.4.6: tray icon renderer, handle cleanup, tooltip text, update controller and the main window wiring.</summary>
internal static partial class ThemeTests
{
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
    /// <summary>GDI objects of this process after a full collection (bitmaps drawn through WPF are released by their finalizers).</summary>
    private static uint GdiObjects() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GetGuiResources(Process.GetCurrentProcess().Handle, 0); }

    private static readonly int[] TraySizes = { 16, 20, 24, 32, 48 };
    private static IReadOnlyList<BitmapSource>? _trayFrames;
    private static IReadOnlyList<BitmapSource> Frames => _trayFrames ??= TrayIconRenderer.LoadAppIconFrames();
    private static TrayBadgeColors ThemeColors() => new(Brush("SelectionBackgroundBrush"), Brush("SelectionForegroundBrush"));
    private static BitmapSource Tray(int count, TrayBadgeColors colors, int size) => TrayIconRenderer.Render(Frames, count, colors, size);
    private static bool SameBytes(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
    private static Color BgraAt(byte[] data, int size, int x, int y) { int i = (y * size + x) * 4; return Color.FromArgb(data[i + 3], data[i + 2], data[i + 1], data[i]); }

    private sealed class FakeTray : ITrayTarget
    {
        public System.Drawing.Icon? Icon; public string Text = ""; public int Icons, Tips;
        public void SetIcon(System.Drawing.Icon icon) { Icon = icon; Icons++; }
        public void SetTooltip(string text) { Text = text; Tips++; }
        public int Count(Color color, int tol = 3) { using var bmp = Icon!.ToBitmap(); int n = 0; for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++) { var c = bmp.GetPixel(x, y); if (c.A == 255 && Same(Color.FromRgb(c.R, c.G, c.B), color, tol)) n++; } return n; }
        public Color Pixel(int x, int y) { using var bmp = Icon!.ToBitmap(); var c = bmp.GetPixel(x, y); return Color.FromArgb(c.A, c.R, c.G, c.B); }
    }

    private static void TrayIcon(string root)
    {
        TooltipText(); BadgeRendering(root); HandleCleanup(); ControllerBehaviour(); MainWindowWiring(root);
        if (Environment.GetEnvironmentVariable("NEODM_SHOTS") is { Length: > 0 } shots) TrayPreview(shots);
    }

    /// <summary>Idle, 1, 3 and 9+ at 32 px, enlarged 6x without smoothing, on a dark and a light taskbar colour.</summary>
    private static void TrayPreview(string dir)
    {
        Use("Midnight"); var colors = ThemeColors(); const int size = 32, zoom = 6, cell = size * zoom, pad = 24;
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, 4 * (cell + pad) + pad, cell + 2 * pad));
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)), null, new Rect(0, cell + 2 * pad, 4 * (cell + pad) + pad, cell + 2 * pad));
            for (int row = 0; row < 2; row++)
                for (int i = 0; i < 4; i++)
                {
                    var image = Tray(new[] { 0, 1, 3, 12 }[i], colors, size);
                    var scaled = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(scaled, BitmapScalingMode.NearestNeighbor);
                    using (var inner = scaled.RenderOpen()) inner.DrawImage(image, new Rect(0, 0, cell, cell));
                    var big = new RenderTargetBitmap(cell, cell, 96, 96, PixelFormats.Pbgra32); big.Render(scaled);
                    ctx.DrawImage(big, new Rect(pad + i * (cell + pad), pad + row * (cell + 2 * pad), cell, cell));
                }
        }
        var sheet = new RenderTargetBitmap(4 * (cell + pad) + pad, 2 * (cell + 2 * pad), 96, 96, PixelFormats.Pbgra32); sheet.Render(visual);
        Save(sheet, Path.Combine(dir, "tray-icon-states.png")); Console.WriteLine("SHOT: tray-icon-states");
        Use("Light");
    }

    private static void TooltipText()
    {
        var table = new (int Down, int Seed, double Speed, string Expected)[]
        {
            (0, 0, 0, "NeoDM"), (0, 0, 5_000_000, "NeoDM"), (1, 0, 0, "NeoDM, 1 downloading"), (1, 0, 1_500_000, "NeoDM, 1 downloading, 1.4 MB/s"),
            (2, 0, 5.4 * 1048576, "NeoDM, 2 downloading, 5.4 MB/s"), (2, 1, 5.4 * 1048576, "NeoDM, 2 downloading, 1 seeding, 5.4 MB/s"),
            (0, 3, 0, "NeoDM, 3 seeding"), (12, 0, 300, "NeoDM, 12 downloading, 300 B/s"), (0, 1, 900_000, "NeoDM, 1 seeding"),
        };
        foreach (var row in table) Check(TrayIconRenderer.Tooltip(row.Down, row.Seed, row.Speed) == row.Expected, $"Tooltip for {row.Down} downloading, {row.Seed} seeding, {row.Speed:0} B/s is \"{row.Expected}\"");
        string longText = TrayIconRenderer.Tooltip(int.MaxValue, int.MaxValue, 1e15, _ => new string('x', 300));
        Check(longText.Length <= 127 && longText.StartsWith("NeoDM, 2147483647 downloading"), "Tooltip never exceeds the 127 characters Windows allows");
        Check(TrayIconRenderer.Tooltip(1, 0, double.NaN) == "NeoDM, 1 downloading" && TrayIconRenderer.Tooltip(1, 0, -5) == "NeoDM, 1 downloading", "Tooltip ignores an invalid speed");
        Check(new[] { (0, ""), (-3, ""), (1, "1"), (9, "9"), (10, "9+"), (500, "9+") }.All(t => TrayIconRenderer.BadgeText(t.Item1) == t.Item2), "Badge text is empty for 0, the number for 1 to 9 and 9+ above");
    }

    private static void BadgeRendering(string root)
    {
        var accents = new string?[] { null, "#00FFFF", "#FFFFFF", "#FFF4B0", "#000000", "#0A0A3C", "#FF8800", "#8A4B1F" };
        foreach (int size in TraySizes)
        {
            Use("Midnight"); var colors = ThemeColors();
            var plain = TrayIconRenderer.ToBgra(TrayIconRenderer.RenderBase(Frames, size));
            var idle = TrayIconRenderer.ToBgra(Tray(0, colors, size));
            Check(Tray(0, colors, size).PixelWidth == size && Tray(3, colors, size).PixelHeight == size, $"{size} px: rendered icons are {size}x{size}");
            Check(SameBytes(plain, idle) && SameBytes(idle, TrayIconRenderer.ToBgra(Tray(-4, colors, size))), $"{size} px: idle (0 or fewer active downloads) is the plain NeoDM icon, pixel for pixel");
            var images = Enumerable.Range(0, 11).Select(n => TrayIconRenderer.ToBgra(Tray(n, colors, size))).ToArray();
            bool distinct = true; for (int a = 0; a < 10; a++) for (int b = a + 1; b < 10; b++) distinct &= !SameBytes(images[a], images[b]);
            Check(distinct, $"{size} px: idle and the counts 1 to 9 render ten different images");
            Check(!SameBytes(images[9], images[10]) && SameBytes(images[10], TrayIconRenderer.ToBgra(Tray(10, colors, size))) && SameBytes(images[10], TrayIconRenderer.ToBgra(Tray(250, colors, size))), $"{size} px: 10 and above render the same 9+ image, different from 9");
            Check(SameBytes(images[3], TrayIconRenderer.ToBgra(Tray(3, colors, size))), $"{size} px: rendering is deterministic");
            foreach (var accent in accents)
            {
                Use("Midnight", accent); colors = ThemeColors(); string tag = $"{size} px, accent {accent ?? "theme"}: ";
                foreach (int count in new[] { 1, 7, 12 })
                {
                    var data = TrayIconRenderer.ToBgra(Tray(count, colors, size));
                    // pixels that match the badge fill exactly (the ring and glyph are the other colour, the tinted icon never equals the fill)
                    long sx = 0, sy = 0; int fill = 0, outer = TrayIconRenderer.BadgeDiameter(size), ring = TrayIconRenderer.RingWidth(size);
                    for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { var c = BgraAt(data, size, x, y); if (c.A == 255 && Same(Color.FromRgb(c.R, c.G, c.B), colors.Fill, 2)) { fill++; sx += x; sy += y; } }
                    double inner = outer - 2 * ring, expectedArea = Math.PI * inner * inner / 4;
                    Check(fill >= expectedArea * 0.25, tag + $"count {count}: badge fill pixels match the accent ({fill} of about {expectedArea:0})");
                    double cx = (double)sx / fill, cy = (double)sy / fill;
                    Check(cx >= size / 2.0 && cy < size / 2.0, tag + $"count {count}: badge sits in the upper right quadrant (centre {cx:0.0},{cy:0.0})");
                    double best = 0; double r = inner / 2, bx = size - outer / 2.0, by = outer / 2.0;
                    for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                    {
                        if (Math.Sqrt((x + 0.5 - bx) * (x + 0.5 - bx) + (y + 0.5 - by) * (y + 0.5 - by)) > r - 0.5) continue;
                        var c = BgraAt(data, size, x, y); best = Math.Max(best, Contrast(Color.FromRgb(c.R, c.G, c.B), colors.Fill));
                    }
                    Check(best >= 4.5, tag + $"count {count}: number pixels read against the badge (best {best:0.0}:1)");
                }
                Check(Contrast(colors.Text, colors.Fill) >= 4.5, tag + "text colour is readable on the badge fill");
            }
            // the badge only touches the upper right: the lower left quarter is the plain icon
            Use("Midnight"); colors = ThemeColors();
            var badged = TrayIconRenderer.ToBgra(Tray(5, colors, size)); int differs = 0;
            for (int y = size / 2 + 1; y < size; y++) for (int x = 0; x < size / 2 - 1; x++) { var a = BgraAt(badged, size, x, y); var b = BgraAt(plain, size, x, y); if (a.A != b.A) differs++; }
            Check(differs == 0, $"{size} px: the badge leaves the lower left of the icon alone");
        }
        Use("Light");
    }

    private static void HandleCleanup()
    {
        Use("Midnight"); var colors = ThemeColors();
        var image = Tray(4, colors, 32);
        for (int i = 0; i < 20; i++) TrayIconRenderer.CreateIcon(image).Dispose(); // warm up
        uint before = GdiObjects();
        for (int i = 0; i < 200; i++) { using var handle = TrayIconRenderer.CreateIcon(Tray(1 + i % 12, colors, TraySizes[i % TraySizes.Length])); if (handle.Icon.Width <= 0) throw new Exception("icon has no size"); }
        uint after = GdiObjects();
        Check(after <= before + 3, $"Creating and disposing 200 tray icons keeps the process GDI object count flat ({before} to {after})");
        var leaked = new List<TrayIconHandle>(); uint start = GdiObjects();
        for (int i = 0; i < 30; i++) leaked.Add(TrayIconRenderer.CreateIcon(image));
        uint held = GdiObjects();
        Check(held >= start + 30, $"The GDI counter does see live icons (30 held raise it from {start} to {held}), so the flat count above is meaningful");
        foreach (var h in leaked) h.Dispose();
        Check(GdiObjects() <= start + 3 && leaked.All(h => h.IsDisposed), "Disposing the held icons returns the GDI count");
        var twice = TrayIconRenderer.CreateIcon(image); twice.Dispose(); twice.Dispose();
        Check(twice.IsDisposed, "Disposing a tray icon handle twice is harmless");
        Use("Light");
    }

    private static void ControllerBehaviour()
    {
        var target = new FakeTray(); var clock = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc); bool show = true; int size = 32;
        Use("Midnight");
        using var controller = new TrayIconController(Frames, target, () => show, ThemeColors, () => size, () => clock);
        controller.Update(0, 0, 0);
        Check(target.Icons == 1 && target.Tips == 1 && target.Text == "NeoDM", "First update sets the plain icon and the idle tooltip");
        controller.Update(0, 0, 0); controller.Update(0, 0, 0);
        Check(controller.IconRenders == 1 && controller.TooltipSets == 1, "Repeating the idle state redraws nothing");
        uint gdiBefore = GdiObjects();
        clock = clock.AddSeconds(1); controller.Update(1, 0, 1_000_000); clock = clock.AddMilliseconds(500);
        Check(controller.IconRenders == 2 && target.Text == "NeoDM, 1 downloading, 976.6 KB/s", "One active download draws the badge icon and sets the tooltip");
        Check(target.Count(Brush("SelectionBackgroundBrush")) >= 40, "The icon handed to the tray shows the accent badge");
        controller.Update(1, 0, 2_000_000);
        Check(controller.IconRenders == 2 && controller.TooltipSets == 2 && target.Text == "NeoDM, 1 downloading, 976.6 KB/s", "A speed change inside one second keeps the tooltip and does not redraw the icon");
        clock = clock.AddMilliseconds(600); controller.Update(1, 0, 2_000_000);
        Check(controller.TooltipSets == 3 && target.Text == "NeoDM, 1 downloading, 1.9 MB/s" && controller.IconRenders == 2, "The tooltip follows after a second, still without redrawing the icon");
        controller.Update(2, 0, 2_000_000);
        Check(controller.IconRenders == 3, "A new count redraws the icon");
        controller.Update(10, 0, 0); controller.Update(11, 0, 0); controller.Update(40, 0, 0);
        Check(controller.IconRenders == 4, "10, 11 and 40 share the 9+ image and redraw it once");
        controller.Update(0, 2, 0);
        Check(controller.IconRenders == 5, "Seeding alone shows the plain icon");
        clock = clock.AddSeconds(2); controller.Update(0, 2, 0);
        Check(target.Text == "NeoDM, 2 seeding", "Seeding alone is mentioned in the tooltip but not counted on the icon");
        controller.Update(3, 1, 0); show = false; controller.Update(3, 1, 0);
        Check(controller.IconRenders == 7, "Turning the setting off redraws the plain icon on the next update");
        controller.Update(5, 1, 0); Check(controller.IconRenders == 7, "With the setting off the count never reaches the icon");
        show = true; controller.Update(5, 1, 0); Check(controller.IconRenders == 8, "Turning the setting back on draws the badge again");
        int renders = controller.IconRenders; size = 20; controller.Update(5, 1, 0);
        Check(controller.IconRenders == renders + 1 && target.Icon!.Width == 20, "A DPI change (new small icon size) redraws at the new size");
        string accentBefore = $"{target.Pixel(size - 3, 4)}"; renders = controller.IconRenders;
        Use("Midnight", "#FF8800"); controller.Refresh();
        Check(controller.IconRenders == renders + 1 && $"{target.Pixel(size - 3, 4)}" != accentBefore, "An accent change redraws the badge in the new colour");
        renders = controller.IconRenders; Use("Paper"); controller.Refresh();
        Check(controller.IconRenders == renders + 1, "A theme change redraws the badge");
        Use("Midnight");
        for (int i = 0; i < 300; i++) { clock = clock.AddMilliseconds(300); controller.Update(i % 13, i % 3, i * 1000.0); }
        Check(GdiObjects() <= gdiBefore + 6, $"300 updates through the controller leave the GDI count flat ({gdiBefore} to {GdiObjects()})");
        Check(controller.TooltipSets < 300, $"The tooltip was set {controller.TooltipSets} times for 300 updates (at most once a second)");
        Use("Light");
    }

    private static void MainWindowWiring(string root)
    {
        Use("Midnight");
        using var fixture = new FixtureServer();
        string files = Path.Combine(root, "tray-files"), state = Path.Combine(root, "tray-state"); Directory.CreateDirectory(files); Directory.CreateDirectory(state);
        using var manager = new DownloadManager(files, state, new DownloadOptions { MaxConcurrentDownloads = 2, MaxSegments = 1, RetryCount = 0 });
        var settings = new AppSettings { DownloadDirectory = files, ShowCompletionDialog = false, ShowProgressOnCompletion = false };
        var main = new MainWindow(manager, settings, () => { }, enableTray: true) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
        main.Show(); Pump(200);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var icon = (Hardcodet.Wpf.TaskbarNotification.TaskbarIcon)typeof(MainWindow).GetField("_trayIcon", flags)!.GetValue(main)!;
        var controller = (TrayIconController?)typeof(MainWindow).GetField("_trayController", flags)!.GetValue(main);
        Check(controller is not null && icon.ToolTipText == "NeoDM", "The main window starts with the idle tray tooltip \"NeoDM\"");
        int renders = controller!.IconRenders;
        var job = manager.AddScheduledAsync(new DownloadRequest(fixture.BaseUrl + "/range?tray.bin", "tray.bin"), DateTimeOffset.UtcNow.AddHours(1)).GetAwaiter().GetResult();
        manager.SetSpeedLimit(job.Id, 40 * 1024); manager.Schedule(job.Id, DateTimeOffset.UtcNow.AddSeconds(-1));
        var end = DateTime.UtcNow.AddSeconds(20);
        while (!(icon.ToolTipText?.Contains("1 downloading") ?? false) && DateTime.UtcNow < end) Pump(100);
        Check(icon.ToolTipText.StartsWith("NeoDM, 1 downloading") && controller.IconRenders > renders, "A download that starts changes the tray tooltip and icon within the refresh interval");
        settings.TrayShowActiveCount = false; renders = controller.IconRenders; Pump(700);
        Check(controller.IconRenders == renders + 1, "Switching the option off restores the plain icon at the next refresh");
        settings.TrayShowActiveCount = true; Pump(700);
        manager.Pause(job.Id);
        end = DateTime.UtcNow.AddSeconds(15);
        while (icon.ToolTipText != "NeoDM" && DateTime.UtcNow < end) Pump(100);
        Check(icon.ToolTipText == "NeoDM", "A paused download leaves the idle tooltip \"NeoDM\"");
        typeof(MainWindow).GetField("_exitWithoutPrompt", flags)!.SetValue(main, true);
        main.Close(); Pump(200);
        Check(!main.IsVisible, "The main window with a tray icon closes cleanly");
        Use("Light");
    }
}
