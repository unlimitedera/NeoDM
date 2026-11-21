// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows;

namespace NDM.App;

/// <summary>
/// Keeps every window inside the usable screen area at any display scale: at 150% a 1920x1080 screen offers only about 1280x690 device-independent
/// units, so windows designed at 100% would otherwise run off the screen (clipped controls, unreachable buttons).
/// </summary>
public static class WindowSizing
{
    private static bool attached;
    public const double Margin = 24;

    /// <summary>Clamps size, minimum size and maximum size of <paramref name="window"/> to <paramref name="work"/> (device-independent units).</summary>
    public static void Fit(Window window, Rect work)
    {
        double maxW = Math.Max(360, work.Width - Margin), maxH = Math.Max(320, work.Height - Margin);
        // 1.16.1: a cap is only for a dimension the content decides (SizeToContent); a window with a fixed size is never capped, so Maximize fills the whole work area
        bool capW = window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight, capH = window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;
        double oldW = window.MaxWidth, oldH = window.MaxHeight;
        if (capW) window.MaxWidth = Math.Min(window.MaxWidth, maxW);
        if (capH) window.MaxHeight = Math.Min(window.MaxHeight, maxH);
        if (capW || capH) ReleaseCapWhenResized(window, capW ? oldW : (double?)null, capH ? oldH : (double?)null);
        if (window.MinWidth > maxW) window.MinWidth = maxW;
        if (window.MinHeight > maxH) window.MinHeight = maxH;
        if (!double.IsNaN(window.Width) && window.SizeToContent is not (SizeToContent.Width or SizeToContent.WidthAndHeight) && window.Width > maxW) window.Width = maxW;
        if (!double.IsNaN(window.Height) && window.SizeToContent is not (SizeToContent.Height or SizeToContent.WidthAndHeight) && window.Height > maxH) window.Height = maxH;
    }

    /// <summary>When the user resizes (or maximizes) a content-sized window WPF switches it to manual sizing; the screen cap is then lifted back to what the window had before.</summary>
    private static void ReleaseCapWhenResized(Window window, double? oldW, double? oldH)
    {
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Window.SizeToContentProperty, typeof(Window));
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (oldW is { } w && window.SizeToContent is not (SizeToContent.Width or SizeToContent.WidthAndHeight)) window.MaxWidth = w;
            if (oldH is { } h && window.SizeToContent is not (SizeToContent.Height or SizeToContent.WidthAndHeight)) window.MaxHeight = h;
            if (window.SizeToContent == SizeToContent.Manual) descriptor.RemoveValueChanged(window, handler!);
        };
        descriptor.AddValueChanged(window, handler);
    }

    /// <summary>Registers the clamp for every window of the application.</summary>
    public static void Attach()
    {
        if (attached) return; attached = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, args) =>
        {
            if (sender is Window window && ReferenceEquals(args.OriginalSource, window)) Fit(window, SystemParameters.WorkArea);
        }));
    }
}
