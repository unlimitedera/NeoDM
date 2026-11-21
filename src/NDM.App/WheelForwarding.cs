// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NDM.App;

/// <summary>
/// 2.0.0: one shared rule for the mouse wheel inside scrolling pages. A list, a text box or any other control that carries its own scroll viewer takes the wheel even when it has nothing to scroll
/// (a scroll viewer marks the turn as handled whenever it exists), so the page behind it stood still: the theme list on Options > Appearance was the large example.
/// Now a scroll viewer that cannot move in the direction of the turn passes it to the nearest scroll viewer around it that can. A list that does scroll keeps the wheel until it reaches its end.
/// Installed once when the program starts; it covers every window (Options, Save to, Proxy, Torrents, About, the video and playlist windows, the link grabber).
/// </summary>
public static class WheelForwarding
{
    [ModuleInitializer]
    internal static void Install()
    {
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
    }

    /// <summary>True when this scroll viewer can move a step in the direction of the turn (negative delta = down).</summary>
    public static bool CanScroll(ScrollViewer viewer, int delta)
    {
        if (viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) return false;
        if (viewer.TemplatedParent is System.Windows.Controls.Primitives.TextBoxBase { AcceptsReturn: false }) return false; // a one-line box moves a few pixels to follow its text: not a list
        if (viewer.ScrollableHeight < 8) return false; // a rounding-sized overflow is not something to scroll
        return delta < 0 ? viewer.VerticalOffset < viewer.ScrollableHeight - 0.5 : viewer.VerticalOffset > 0.5;
    }

    /// <summary>The nearest scroll viewer above <paramref name="start"/> that can take the turn; null when there is none.</summary>
    public static ScrollViewer? Receiver(DependencyObject start, int delta)
    {
        for (var parent = ParentOf(start); parent is not null; parent = ParentOf(parent))
            if (parent is ScrollViewer outer && CanScroll(outer, delta)) return outer;
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject element)
    {
        if (element is Visual or System.Windows.Media.Media3D.Visual3D) { var visual = VisualTreeHelper.GetParent(element); if (visual is not null) return visual; }
        return element is FrameworkElement fe ? fe.Parent ?? fe.TemplatedParent : element is FrameworkContentElement fce ? fce.Parent : null;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not ScrollViewer viewer) return;
        if (CanScroll(viewer, e.Delta)) return; // this one scrolls: it keeps the turn
        if (Receiver(viewer, e.Delta) is not { } outer) return;
        e.Handled = true;
        outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = outer });
    }
}
