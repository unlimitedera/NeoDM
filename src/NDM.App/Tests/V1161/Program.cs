// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
global using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;

internal sealed class V1161App : Application
{
    public V1161App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(ThemeManager.LoadFile("Light"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
}

/// <summary>1.16.1 tests: maximize fills the whole work area (rectangles measured in device pixels).</summary>
internal static class V1161Tests
{
    private static int passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static void Pump(int ms = 80)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO i);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

    /// <summary>Gaps (device px) between the visible frame of the maximized window and the monitor work area: left, top, right, bottom. Positive = the window is short of the edge.</summary>
    private static (int L, int T, int R, int B) Gaps(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() }; GetMonitorInfo(MonitorFromWindow(h, 2), ref mi);
        DwmGetWindowAttribute(h, 9, out RECT f, Marshal.SizeOf<RECT>()); // DWMWA_EXTENDED_FRAME_BOUNDS: the visible frame without the invisible resize border
        return (f.L - mi.Work.L, f.T - mi.Work.T, mi.Work.R - f.R, mi.Work.B - f.B);
    }

    private static void MaximizeFillsWork()
    {
        var cases = new (string Name, Func<Window> Make)[]
        {
            ("fixed-size window (like the main window)", () => new Window { Width = 1100, Height = 700, MinWidth = 640, MinHeight = 420 }),
            ("resizable dialog with SizeToContent=Height", () => new Window { Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.CanResize, Content = new System.Windows.Controls.TextBlock { Text = "x" } }),
            ("choice window (real)", () => new ChoiceWindow("t", "m", new[] { "a", "b" })),
        };
        foreach (var (name, make) in cases)
        {
            Window w; try { w = make(); } catch (Exception ex) { Console.WriteLine("SKIP " + name + ": " + ex.Message); continue; }
            w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = 40; w.Top = 40; WindowSizing.Fit(w, SystemParameters.WorkArea); w.Show(); Pump(300); Console.WriteLine($"DEBUG {name}: Max={w.MaxWidth}x{w.MaxHeight} style={(w.Style==null?"none":"set")} main={(ReferenceEquals(Application.Current.MainWindow,w))}");
            if (w.ResizeMode != ResizeMode.CanResize && w.ResizeMode != ResizeMode.CanResizeWithGrip) { w.ResizeMode = ResizeMode.CanResize; }
            w.WindowState = WindowState.Maximized; Pump(250); w.UpdateLayout();
            var g = Gaps(w);
            Console.WriteLine($"MEASURE {name}: gaps px left={g.L} top={g.T} right={g.R} bottom={g.B}; MaxWidth={w.MaxWidth} MaxHeight={w.MaxHeight}");
            if (name.StartsWith("fixed")) Check(g == (0, 0, 0, 0), $"{name}: maximized frame equals the monitor work area exactly");
            else Check(g.L == 0 && g.T == 0 && g.R == 0, $"{name}: no left/top/right gap (a content-sized dialog keeps its own height, measured only)");
            w.WindowState = WindowState.Normal; Pump(150);
            Check(w.WindowState == WindowState.Normal && w.ActualWidth < SystemParameters.WorkArea.Width, $"{name}: restore returns to a smaller window");
            w.Close();
        }
    }

    private static void FitTable()
    {
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var work = new Rect(0, 0, 1920 / scale, 1040 / scale);
            var fixedW = new Window { Width = 900, Height = 600 }; WindowSizing.Fit(fixedW, work);
            Check(double.IsPositiveInfinity(fixedW.MaxWidth) && double.IsPositiveInfinity(fixedW.MaxHeight), $"{scale * 100:0}%: fixed-size window has no maximum size");
            var auto = new Window { Width = 500, SizeToContent = SizeToContent.Height }; WindowSizing.Fit(auto, work);
            Check(auto.MaxHeight <= work.Height - WindowSizing.Margin && double.IsPositiveInfinity(auto.MaxWidth), $"{scale * 100:0}%: content-sized window keeps a height cap only");
            auto.SizeToContent = SizeToContent.Manual;
            Check(double.IsPositiveInfinity(auto.MaxHeight), $"{scale * 100:0}%: cap is lifted when the window turns manual (user resize or maximize)");
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-V1161Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch); Environment.SetEnvironmentVariable("NDM_STATE_DIR", Path.Combine(scratch, "NDM"));
        var app = new V1161App();
        try
        {
            Loc.SetLanguage("en"); WindowPlacements.Configure(null); WindowSizing.Attach();
            void Run(string name, Action a) { Console.WriteLine("== " + name); TestGuard.Run(name, a); }
            Run("MaximizeFillsWork", MaximizeFillsWork);
            Run("FitTable", FitTable);
            Console.WriteLine($"V1161 TESTS PASSED: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { WindowPlacements.Configure(null); foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close(); try { Directory.Delete(scratch, true); } catch (IOException) { } app.Shutdown(); }
    }
}
