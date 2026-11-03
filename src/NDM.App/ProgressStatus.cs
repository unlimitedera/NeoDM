using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NDM.App;

/// <summary>1.10.0: status colours and the downloading sweep of progress bars.
/// Colours: every bar takes the Status{Kind}Brush token of the active theme (Completed green, Downloading blue, Paused amber, Failed red, Queued and Scheduled neutral, Seeding teal). Merging, converting and joining are Downloading (blue): they are work in progress and carry no tone of their own.
/// Sweep: a bar whose <see cref="ActiveProperty"/> is true shows a soft band of its own hue that moves across the filled part; one shared clock drives every bar through a single brush transform.</summary>
public static class ProgressStatus
{
    public static readonly string[] Kinds = { "Completed", "Downloading", "Paused", "Failed", "Queued", "Seeding" };
    public static string TokenFor(string? kind) => "Status" + (Array.IndexOf(Kinds, kind) >= 0 ? kind : "Queued") + "Brush";

    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached("Kind", typeof(string), typeof(ProgressStatus), new PropertyMetadata(null, (d, e) => { if (d is ProgressBar bar) { Track(bar); ApplyColour(bar, true); } }));
    public static string? GetKind(DependencyObject d) => (string?)d.GetValue(KindProperty);
    public static void SetKind(DependencyObject d, string? value) => d.SetValue(KindProperty, value);
    /// <summary>True while bytes move for this bar: the sweep may run.</summary>
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached("Active", typeof(bool), typeof(ProgressStatus), new PropertyMetadata(false, (d, e) => { if (d is ProgressBar bar) { Track(bar); UpdateWave(bar); } }));
    public static bool GetActive(DependencyObject d) => (bool)d.GetValue(ActiveProperty);
    public static void SetActive(DependencyObject d, bool value) => d.SetValue(ActiveProperty, value);

    private static readonly List<WeakReference<ProgressBar>> Bars = new();
    private static readonly ConditionalWeakTable<ProgressBar, SolidColorBrush> Own = new();
    private static readonly ConditionalWeakTable<ProgressBar, object> Hooked = new();
    private static bool themeHooked;

    private static void Track(ProgressBar bar)
    {
        if (Hooked.TryGetValue(bar, out _)) return;
        Hooked.Add(bar, new object());
        Bars.Add(new(bar));
        bar.IsVisibleChanged += (_, _) => UpdateWave(bar);
        bar.Loaded += (_, _) => { ApplyColour(bar, false); UpdateWave(bar); };
        bar.Unloaded += (_, _) => UpdateWave(bar);
        if (!themeHooked)
        {
            themeHooked = true; ThemeManager.Applied += RefreshAll;
            // WPF raises this when Windows changes a system setting (the animation switch included); no extra hidden window or thread is needed
            SystemParameters.StaticPropertyChanged += (_, e) => { if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or null) { RefreshAll(); ProgressWaveClock.Evaluate(); } };
        }
    }

    /// <summary>A theme or accent change: every bar takes the new token colour at once (no fade between themes).</summary>
    public static void RefreshAll()
    {
        for (int i = Bars.Count - 1; i >= 0; i--)
        {
            if (!Bars[i].TryGetTarget(out var bar)) { Bars.RemoveAt(i); continue; }
            ApplyColour(bar, false); UpdateWave(bar);
        }
    }

    /// <summary>The colour a bar of this kind uses in the bar's theme.</summary>
    public static Color ColourOf(FrameworkElement scope, string? kind) => (scope.TryFindResource(TokenFor(kind)) as SolidColorBrush)?.Color ?? Colors.SteelBlue;

    private static void ApplyColour(ProgressBar bar, bool animate)
    {
        var kind = GetKind(bar);
        if (kind is null) return;
        var target = ColourOf(bar, kind);
        if (Own.TryGetValue(bar, out var own) && ReferenceEquals(bar.Foreground, own))
        {
            if (animate && Motion.Allowed && bar.IsLoaded && own.Color != target)
            {
                own.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(Motion.FadeMilliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            else { own.BeginAnimation(SolidColorBrush.ColorProperty, null); own.Color = target; }
        }
        else
        {
            own = new SolidColorBrush(target);
            Own.Remove(bar); Own.Add(bar, own);
            bar.Foreground = own;
        }
        UpdateWave(bar);
    }

    /// <summary>Colour the fill of this bar is heading to (tests read it after a state change; the shown colour fades to it).</summary>
    public static Color TargetColour(ProgressBar bar) => GetKind(bar) is { } kind ? ColourOf(bar, kind) : Colors.Transparent;
    /// <summary>Colour the fill shows right now.</summary>
    public static Color ShownColour(ProgressBar bar) => (bar.Foreground as SolidColorBrush)?.Color ?? Colors.Transparent;

    // ---- the sweep ----
    private static Border? Overlay(ProgressBar bar)
    {
        if (!bar.IsLoaded && bar.Template is null) return null;
        bar.ApplyTemplate();
        return bar.Template?.FindName("PART_Wave", bar) as Border;
    }
    private static readonly ConditionalWeakTable<ProgressBar, object> Waving = new();
    private static readonly HashSet<ProgressBar> WavingBars = new();
    public static int WavingCount => WavingBars.Count;
    /// <summary>True when this bar currently shows the sweep.</summary>
    public static bool IsWaving(ProgressBar bar) => WavingBars.Contains(bar);

    internal static void UpdateWave(ProgressBar bar)
    {
        if (bar.IsLoaded) ProgressWaveClock.Watch(Window.GetWindow(bar));
        bool want = GetActive(bar) && ProgressWaveClock.Allowed && bar.IsVisible && bar.IsLoaded && GetKind(bar) is not null;
        var overlay = Overlay(bar);
        if (overlay is not null)
        {
            if (want) overlay.Background = ProgressWaveClock.BrushFor(bar, ColourOf(bar, GetKind(bar)));
            overlay.Visibility = want ? Visibility.Visible : Visibility.Collapsed;
        }
        bool changed = want && overlay is not null ? WavingBars.Add(bar) : WavingBars.Remove(bar);
        if (changed) ProgressWaveClock.Evaluate();
    }
}

/// <summary>Motion preferences of the app. System animation settings of Windows always win over the Options choice.</summary>
public static class Motion
{
    /// <summary>Reads the Windows setting; tests replace it.</summary>
    public static Func<bool> SystemAnimations { get; set; } = () => SystemParameters.ClientAreaAnimation;
    public static bool Allowed => SystemAnimations();
    public const int FadeMilliseconds = 280;
}

/// <summary>The one animation clock of every progress bar. It runs only while the option is on, Windows animations are on, a bar in the Downloading state is visible and a window is on screen; it ticks at about 30 frames a second and only moves one brush transform.</summary>
public static class ProgressWaveClock
{
    public const double PeriodSeconds = 2.0;
    public const int FrameMilliseconds = 33;
    private static DispatcherTimer? timer;
    private static readonly TranslateTransform Shift = new(-0.7, 0);
    private static readonly Dictionary<int, LinearGradientBrush> Brushes = new();
    private static bool userEnabled = true;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    /// <summary>The Options choice (Appearance: animate progress bars while downloading).</summary>
    public static bool UserEnabled { get => userEnabled; set { if (userEnabled == value) return; userEnabled = value; RefreshBars(); } }
    /// <summary>The Options choice and the Windows setting together.</summary>
    public static bool Allowed => userEnabled && Motion.Allowed;
    /// <summary>True when Windows animations are off, so the Options checkbox shows its note.</summary>
    public static bool OverriddenBySystem => !Motion.Allowed;
    /// <summary>True while the timer ticks (test hook: it must be false when nothing may move).</summary>
    public static bool IsRunning => timer?.IsEnabled == true;
    public static int FrameCount { get; private set; }
    /// <summary>Replaceable so tests decide when a window counts as shown.</summary>
    public static Func<bool> AnyWindowShown { get; set; } = DefaultAnyWindowShown;
    private static bool DefaultAnyWindowShown()
    {
        if (Application.Current is not { } app) return false;
        foreach (Window w in app.Windows) if (w.IsVisible && w.WindowState != WindowState.Minimized) return true;
        return false;
    }

    /// <summary>Starts or stops the timer for the present state. Called when a bar starts or stops sweeping, when the option or the Windows setting changes and when a window is shown, hidden or minimised.</summary>
    public static void Evaluate()
    {
        bool run = Allowed && ProgressStatus.WavingCount > 0 && AnyWindowShown();
        if (run)
        {
            if (timer is null) { timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(FrameMilliseconds) }; timer.Tick += (_, _) => Tick(); }
            if (!timer.IsEnabled) timer.Start();
        }
        else if (timer is { IsEnabled: true }) timer.Stop();
    }
    private static void RefreshBars() { ProgressStatus.RefreshAll(); Evaluate(); }

    /// <summary>Brush position for a time: eased from just before the left edge to just past the right edge.</summary>
    public static double OffsetAt(double seconds)
    {
        double t = seconds / PeriodSeconds; t -= Math.Floor(t);
        double eased = 0.5 - 0.5 * Math.Cos(Math.PI * t);
        return -0.7 + 1.4 * eased;
    }
    private static void Tick()
    {
        if (!Allowed) ProgressStatus.RefreshAll(); // the option or Windows animations were switched off: the bars go back to a plain fill
        if (!Allowed || ProgressStatus.WavingCount == 0 || !AnyWindowShown()) { Evaluate(); return; }
        Shift.X = OffsetAt(Clock.Elapsed.TotalSeconds);
        FrameCount++;
    }
    /// <summary>Moves the sweep to a given moment (tests render still frames).</summary>
    public static void SetTime(double seconds) => Shift.X = OffsetAt(seconds);

    /// <summary>Window state changes need a fresh decision; windows call this when shown, hidden or minimised.</summary>
    public static void WindowStateChanged() => Evaluate();
    private static readonly ConditionalWeakTable<Window, object> WatchedWindows = new();
    internal static void Watch(Window? window)
    {
        if (window is null || WatchedWindows.TryGetValue(window, out _)) return;
        WatchedWindows.Add(window, new object());
        window.StateChanged += (_, _) => Evaluate();
        window.IsVisibleChanged += (_, _) => Evaluate();
    }

    // ---- brush ----
    /// <summary>Fraction of the way from the fill colour to the band colour at the middle of the band.</summary>
    public const double BandStrength = 0.34;
    public const double BandWidth = 0.4;
    /// <summary>Colour of the band: the same hue as the fill, moved away from the page colour (lighter on dark themes, darker on light ones), so the contrast of the fill never drops where the band passes.</summary>
    public static Color BandColour(Color fill, bool darkTheme) => FileTypeIcons.Mix(fill, darkTheme ? Colors.White : Colors.Black, BandStrength);
    public static bool IsDark(FrameworkElement scope) => scope.TryFindResource("SurfaceBrush") is SolidColorBrush s && ThemeManager.Luminance(s.Color) < 0.3;

    internal static Brush BrushFor(FrameworkElement scope, Color fill)
    {
        bool dark = IsDark(scope);
        int key = fill.R << 16 | fill.G << 8 | fill.B | (dark ? 1 << 24 : 0);
        if (Brushes.TryGetValue(key, out var brush)) return brush;
        if (Brushes.Count > 64) Brushes.Clear();
        var band = BandColour(fill, dark);
        double h = BandWidth / 2;
        var stops = new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0, band.R, band.G, band.B), 0.5 - h),
            new GradientStop(band, 0.5),
            new GradientStop(Color.FromArgb(0, band.R, band.G, band.B), 0.5 + h),
        };
        brush = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0)) { RelativeTransform = Shift, MappingMode = BrushMappingMode.RelativeToBoundingBox };
        Brushes[key] = brush;
        return brush;
    }
}
