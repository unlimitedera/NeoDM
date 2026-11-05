using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace NDM.App;

/// <summary>What DWM holds for a window's caption now (tests compare it with the theme).</summary>
public readonly record struct ChromeReadback(bool DarkMode, System.Windows.Media.Color? Caption, System.Windows.Media.Color? Text, System.Windows.Media.Color? Border);

/// <summary>The one place that colours window title bars. Immersive dark mode (attribute 20, and 19 on early Windows 10 builds) follows the theme; on Windows 11 the caption, text and border colours match the theme surface.
/// High contrast keeps the system colours. Every failure is ignored: an old Windows simply keeps its normal caption.</summary>
public static class WindowChrome
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const int DarkMode = 20, DarkModeOld = 19, Round = 33, BorderColor = 34, CaptionColor = 35, TextColor = 36;
    private const uint FrameChanged = 0x0020 | 0x0002 | 0x0001 | 0x0004 | 0x0010; // SWP_FRAMECHANGED | NOMOVE | NOSIZE | NOZORDER | NOACTIVATE
    private static readonly int NoColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_DEFAULT

    /// <summary>The HRESULT DWM gave for the last caption colour (0 = accepted). DWM does not let the colours be read back on current Windows, so tests check this instead.</summary>
    public static int LastCaptionResult { get; private set; } = -1;

    private static int Colorref(System.Windows.Media.Color c) => c.R | (c.G << 8) | (c.B << 16);
    private static System.Windows.Media.Color FromColorref(int v) => System.Windows.Media.Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));

    /// <summary>Immersive dark mode follows the theme; themes also colour the caption, text and border (Windows 11 22000+; older systems ignore those attributes). Null colours restore the system default.</summary>
    public static void Apply(Window window, bool dark, System.Windows.Media.Color? caption = null, System.Windows.Media.Color? text = null, System.Windows.Media.Color? border = null)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        if (SystemParameters.HighContrast) { dark = false; caption = text = border = null; } // the user's own high contrast colours win
        int theme = dark ? 1 : 0, round = 2;
        if (DwmSetWindowAttribute(handle, DarkMode, ref theme, sizeof(int)) != 0) _ = DwmSetWindowAttribute(handle, DarkModeOld, ref theme, sizeof(int));
        _ = DwmSetWindowAttribute(handle, Round, ref round, sizeof(int));
        int value = caption is { } cap ? Colorref(cap) : NoColor; LastCaptionResult = DwmSetWindowAttribute(handle, CaptionColor, ref value, sizeof(int));
        value = text is { } txt ? Colorref(txt) : NoColor; _ = DwmSetWindowAttribute(handle, TextColor, ref value, sizeof(int));
        value = border is { } brd ? Colorref(brd) : NoColor; _ = DwmSetWindowAttribute(handle, BorderColor, ref value, sizeof(int));
        _ = SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, FrameChanged); // makes a window that is already visible repaint its caption now
    }

    /// <summary>The attributes DWM holds for the window now; a colour that was never set (or restored to the default) reads as null.</summary>
    public static ChromeReadback? Read(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return null;
        DwmGetWindowAttribute(handle, DarkMode, out int dark, sizeof(int));
        System.Windows.Media.Color? Get(int attribute) => DwmGetWindowAttribute(handle, attribute, out int v, sizeof(int)) == 0 && v != NoColor && unchecked((uint)v) != 0xFFFFFFFF ? FromColorref(v) : null;
        return new ChromeReadback(dark != 0, Get(CaptionColor), Get(TextColor), Get(BorderColor));
    }
}

/// <summary>1.11.0: set through the Window style, so a window gets the themed caption before it is first shown and new windows need nothing extra.</summary>
public static class WindowChromeBehavior
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(WindowChromeBehavior), new PropertyMetadata(false, OnEnabled));
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);
    private static void OnEnabled(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true) return;
        window.SourceInitialized -= Initialized; window.SourceInitialized += Initialized;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ThemeManager.ApplyChrome(window);
    }
    private static void Initialized(object? sender, EventArgs e) { if (sender is Window window) ThemeManager.ApplyChrome(window); }
}
