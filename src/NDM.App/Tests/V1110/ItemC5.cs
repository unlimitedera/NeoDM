using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>Item 18, title bars: captures the window the way DWM composes it (PrintWindow with full content, so the caption is included) and checks the pixels of the title bar against the theme. Only the window's own surface is read; nothing else on the screen, no clipboard, no mouse.</summary>
internal static partial class V1110Tests
{
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[]? bits, ref BITMAPINFO info, uint usage);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant; }

    /// <summary>The full window (frame and title bar included) as BGRA pixels, top row first.</summary>
    private static (byte[] Px, int W, int H)? CaptureWindow(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle; if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
        int W = r.Right - r.Left, H = r.Bottom - r.Top; if (W <= 0 || H <= 0) return null;
        var screen = GetDC(IntPtr.Zero); var dc = CreateCompatibleDC(screen); var bmp = CreateCompatibleBitmap(screen, W, H); var old = SelectObject(dc, bmp);
        try
        {
            if (!PrintWindow(hwnd, dc, 2)) return null; // PW_RENDERFULLCONTENT
            var info = new BITMAPINFO { Size = Marshal.SizeOf<BITMAPINFO>(), Width = W, Height = -H, Planes = 1, BitCount = 32 };
            var bits = new byte[W * H * 4]; GetDIBits(dc, bmp, 0, (uint)H, bits, ref info, 0);
            return (bits, W, H);
        }
        finally { SelectObject(dc, old); DeleteObject(bmp); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen); }
    }

    private static void C18TitleBars(string scratch)
    {
        var m = CManager(scratch, "c18t"); var job = Wait(m.AddPausedAsync(new DownloadRequest("http://127.0.0.1:9/t.bin", "t.bin")));
        int checkedWindows = 0;
        try
        {
            foreach (var theme in ThemeCatalog.Concrete)
            {
                ThemeManager.Apply(Application.Current, theme.Id, null);
                var makers = new (string, Func<Window>)[] { ("Scheduler", () => new JobOptionsWindow(m, job.Id, true)), ("Add URL", () => new AddDownloadWindow("")), ("Message", () => new MessageWindow("Text", "Title", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.OK)), ("Playlist", () => NewPlaylistDialogNoShow(scratch, 5)) };
                foreach (var (name, make) in makers)
                {
                    var w = make(); w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = 30; w.Top = 30; w.Show(); Pump(250);
                    if (CaptureWindow(w) is not { } shot) { Console.WriteLine($"TITLEBAR {name} [{theme.Id}] capture not possible"); w.Close(); continue; }
                    // the caption strip: rows 10..24 from the top, a column a quarter of the way in clear of the icon and buttons, plus one near the right of the title text
                    int y = 16; double lum(int x) { int i = (y * shot.W + x) * 4; return Lum(shot.Px[i + 2], shot.Px[i + 1], shot.Px[i]); }
                    double mid = lum(shot.W / 2), q = lum(shot.W * 3 / 4);
                    var canvas = BrushColor("CanvasBrush"); double expected = Lum(canvas.R, canvas.G, canvas.B);
                    bool darkTheme = theme.IsDark;
                    bool ok = darkTheme ? (mid < 0.35 && q < 0.35) : true;
                    Console.WriteLine($"TITLEBAR {name} [{theme.Id}] caption luminance mid={mid:0.00} right={q:0.00} theme canvas={expected:0.00} {(ok ? "ok" : "LIGHT TITLE BAR")}");
                    Check(ok, $"{name} in {theme.Id}: the title bar is dark (luminance {mid:0.00}) when the theme is dark");
                    if (!darkTheme) Check(mid > 0.6, $"{name} in {theme.Id}: the title bar of a light theme stays light (luminance {mid:0.00})");
                    checkedWindows++;
                    if (ShotsFolder is { Length: > 0 } f && name == "Scheduler" && theme.Id is "DeepNavy" or "Light")
                    {
                        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(shot.W, shot.H, 96, 96, PixelFormats.Bgra32, null, shot.Px, shot.W * 4);
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp)); using var fs = File.Create(Path.Combine(f, $"c18-titlebar-scheduler-{theme.Id}.png")); enc.Save(fs);
                    }
                    w.Close();
                }
            }
        }
        finally { ThemeManager.Apply(Application.Current, "Light", null); m.Dispose(); }
        Check(checkedWindows >= 4 * 8, $"title bars captured for {checkedWindows} window/theme pairs");
    }
}
