using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>1.11.0 Part C item 18: audit of every window in every dark theme for light bars (scrollbars, corners, panels, title bars). Renders each window and looks for runs of near-white pixels that a dark theme should never contain.</summary>
internal static partial class V1110Tests
{
    private static readonly List<string> AuditFindings = new();
    private static double Lum(byte r, byte g, byte b) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;

    /// <summary>Finds light bars in a dark theme: horizontal runs of at least minRun near-white pixels that are at least 2 rows thick, and vertical runs of at least minRun x 4 pixels wide. Returns descriptions with pixel positions.</summary>
    private static List<string> LightBars(FrameworkElement w, double threshold = 0.80, int minRun = 40)
    {
        w.UpdateLayout();
        int W = (int)Math.Ceiling(w.ActualWidth), H = (int)Math.Ceiling(w.ActualHeight);
        var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32); bmp.Render(w);
        var px = new byte[W * H * 4]; bmp.CopyPixels(px, W * 4, 0);
        bool Light(int x, int y) { int i = (y * W + x) * 4; byte a = px[i + 3]; if (a < 250) return false; return Lum(px[i + 2], px[i + 1], px[i]) > threshold; }
        var found = new List<string>();
        var covered = new bool[W, H];
        for (int y = 0; y < H - 1; y++)
        {
            int x = 0;
            while (x < W)
            {
                if (!Light(x, y)) { x++; continue; }
                int start = x; while (x < W && Light(x, y)) x++;
                int len = x - start; if (len < minRun || covered[start, y]) continue;
                // thickness: grow downward while the same span stays light
                int thick = 1; while (y + thick < H && Enumerable.Range(start, len).All(xx => Light(xx, y + thick))) thick++;
                if (thick < 2) continue;
                for (int yy = y; yy < y + thick; yy++) for (int xx = start; xx < start + len; xx++) covered[xx, yy] = true;
                found.Add($"light block {len}x{thick} at ({start},{y})");
            }
        }
        // vertical bars: columns at least 4 px wide and minRun tall that are light (a scrollbar track)
        for (int x = 0; x < W - 3; x++)
        {
            int y = 0;
            while (y < H)
            {
                if (!(Light(x, y) && Light(x + 1, y) && Light(x + 2, y) && Light(x + 3, y))) { y++; continue; }
                int start = y; while (y < H && Light(x, y) && Light(x + 1, y) && Light(x + 2, y) && Light(x + 3, y)) y++;
                if (y - start >= minRun && !covered[x, start]) { found.Add($"light column 4x{y - start} at ({x},{start})"); for (int yy = start; yy < y; yy++) for (int xx = x; xx < x + 4; xx++) covered[xx, yy] = true; }
            }
        }
        return found;
    }

    private static void AuditWindow(string name, Func<Window> make, string? shotsName = null, Action<Window>? prepare = null)
    {
        foreach (var theme in ThemeCatalog.Concrete.Where(t => t.IsDark))
        {
            ThemeManager.Apply(Application.Current, theme.Id, null);
            var w = make(); w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = 10; w.Top = 10; w.Show(); Pump(150); prepare?.Invoke(w); Pump(100);
            var bars = LightBars(w);
            var chrome = WindowChrome.Read(w);
            ThemeManager.ApplyChrome(w); if (WindowChrome.LastCaptionResult != 0) bars.Add($"DWM refused the themed caption colour (0x{WindowChrome.LastCaptionResult:X})");
            string cap = chrome is { } c ? $"dark={c.DarkMode} captionResult=0x{WindowChrome.LastCaptionResult:X}" : "no handle";
            if (chrome is { } cr && !cr.DarkMode) bars.Add("title bar is not in immersive dark mode");
            if (ShotsFolder is { Length: > 0 } f && (shotsName is not null) && theme.Id is "DeepNavy" or "Dark") SaveShot(w, f, $"c18-{shotsName}-{theme.Id}.png");
            // open the drop-downs and look at their pop-ups as well
            foreach (var combo in Visual<ComboBox>(w).Where(c => c.IsVisible && c.Items.Count > 0).Take(3).ToList())
            {
                combo.IsDropDownOpen = true; Pump(120);
                if (combo.Template.FindName("PART_Popup", combo) is System.Windows.Controls.Primitives.Popup { Child: FrameworkElement pc } && pc.ActualWidth > 20) bars.AddRange(LightBars(pc).Select(b => "drop-down list: " + b));
                combo.IsDropDownOpen = false; Pump(40);
            }
            foreach (var b in bars) AuditFindings.Add($"{name} [{theme.Id}]: {b}");
            Console.WriteLine($"AUDIT {name} [{theme.Id}] {w.ActualWidth:0}x{w.ActualHeight:0} {cap}: {(bars.Count == 0 ? "clean" : string.Join("; ", bars.Take(6)))}");
            w.Close();
        }
        ThemeManager.Apply(Application.Current, "Light", null);
    }

    private static void C18Audit(string scratch)
    {
        AuditFindings.Clear();
        var settings = CSettings(Path.Combine(scratch, "c18", "files"));
        var m = CManager(scratch, "c18");
        var many = string.Join("\n", Enumerable.Range(0, 120).Select(i => $"http://files.invalid/pkg/file{i:D3}.zip"));
        AuditWindow("Add URL", () => new AddDownloadWindow(""), "addurl");
        AuditWindow("About", () => new AboutWindow());
        AuditWindow("Remove confirmation (50 rows)", () => new RemoveConfirmWindow(Enumerable.Range(0, 50).Select(i => new RemoveItem($"file{i}.bin", "Paused", 1, false)).ToList()));
        AuditWindow("Choice", () => new ChoiceWindow("Title", "Message text here", new[] { "One", "Two", "Cancel" }));
        AuditWindow("YouTube bulk", () => new YoutubeBulkWindow(3));
        AuditWindow("Message window", () => new MessageWindow("A themed message with some more words so it wraps over more than a single line of text in the window.", "NeoDM", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes), "message");
        AuditWindow("Link grabber (120 links, scrollbars)", () => new LinkGrabberWindow(m, settings, many), "grabber");
        AuditWindow("Bulk import (long text)", () => { var b = new BulkImportWindow(m, null, settings); ((TextBox)b.FindName("UrlsInput")).Text = many; return b; });
        AuditWindow("Playlist dialog (200 videos, scrollbars)", () => NewPlaylistDialogNoShow(scratch, 200), "playlist", w => { Wait(((PlaylistOptionsDialog)w).ReadAsync()); Pump(300); });
        AuditWindow("YouTube dialog", () => NewVideoDialogNoShow(scratch), "youtube");
        AuditWindow("Schedule (Scheduler window)", () => { var job = Wait(m.AddPausedAsync(new DownloadRequest("http://127.0.0.1:9/audit" + Guid.NewGuid().ToString("N")[..4] + ".bin", "a.bin"))); return new JobOptionsWindow(m, job.Id, true); }, "scheduler");
        AuditWindow("Schedule calendar popup", () => { var h = new Window { Width = 460, Height = 380, Content = new SchedulePicker { Margin = new Thickness(16), Zone = TimeZoneInfo.Utc, Now = () => new DateTimeOffset(2026, 11, 4, 10, 30, 0, TimeSpan.Zero) } }; return h; }, "schedule-popup", w => { var p = (SchedulePicker)w.Content; p.Box.Text = "11/12/2026 11:00 PM"; p.DropPopup.IsOpen = true; Pump(150); });
        AuditWindow("Properties", () => new PropertiesWindow(RowOf("Completed")));
        AuditWindow("Countdown", () => new CountdownWindow(CompletionActionKind.Shutdown, 30));
        AuditWindow("Options (all tabs)", () => new SettingsWindow(new AppSettings(), null, null), "options");
        AuditWindow("Main window (120 rows, scrollbars)", () =>
        {
            var w = CMain(m, settings, width: 1100, height: 600);
            foreach (var i in Enumerable.Range(0, 120)) Wait(m.AddPausedAsync(new DownloadRequest($"http://127.0.0.1:9/m{i:D3}.bin", $"m{i:D3}.bin")));
            return w;
        }, "main", w => { ((MainWindow)w).Rows.Take(3).ToList().ForEach(r => r.IsChecked = true); Pump(300); });
        m.Dispose();
        foreach (var f in AuditFindings) Console.WriteLine("FINDING " + f);
        Check(AuditFindings.Count == 0, $"audit: no light bars, light corners or light title bars in any dark theme ({AuditFindings.Count} findings)");
    }

    private static Window NewPlaylistDialogNoShow(string scratch, int n)
    {
        var d = new PlaylistOptionsDialog("https://www.youtube.com/playlist?list=" + ListId, scratch, null, new AppSettings { DownloadDirectory = scratch }, (u, t, l) => Task.FromResult(SamplePlaylist(n)), null) { Width = 900, Height = 700 };
        return d;
    }
    private static Window NewVideoDialogNoShow(string scratch)
    {
        var d = new YoutubeOptionsDialog(new DownloadRequest("https://www.youtube.com/watch?v=" + Vid), scratch, null!, null!, settings: new AppSettings { DownloadDirectory = scratch }, lineInspector: (u, t, l) => Task.FromResult(new YoutubeMetadata("Sample", new[] { new YoutubeFormat("137", "mp4", 1080, 0, true, false, 0), new YoutubeFormat("140", "m4a", 0, 128, false, true, 0) }))) { Width = 640, Height = 640 };
        return d;
    }
}
