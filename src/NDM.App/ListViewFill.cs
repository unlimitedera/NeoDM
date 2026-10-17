using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NDM.App;

/// <summary>
/// Lets one column of a GridView take whatever width the other columns leave, so a list fills its window instead of ending in an empty
/// header cell. Set <c>ListViewFill.FillColumn="2"</c> on the ListView; the column keeps <c>MinFillWidth</c> as its lower bound.
/// </summary>
public static class ListViewFill
{
    public static readonly DependencyProperty FillColumnProperty = DependencyProperty.RegisterAttached("FillColumn", typeof(int), typeof(ListViewFill), new PropertyMetadata(-1, OnFillColumnChanged));
    public static readonly DependencyProperty MinFillWidthProperty = DependencyProperty.RegisterAttached("MinFillWidth", typeof(double), typeof(ListViewFill), new PropertyMetadata(80.0));
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached("Attached", typeof(bool), typeof(ListViewFill), new PropertyMetadata(false));
    private static readonly DependencyProperty BusyProperty = DependencyProperty.RegisterAttached("Busy", typeof(bool), typeof(ListViewFill), new PropertyMetadata(false));

    public static int GetFillColumn(DependencyObject d) => (int)d.GetValue(FillColumnProperty);
    public static void SetFillColumn(DependencyObject d, int value) => d.SetValue(FillColumnProperty, value);
    public static double GetMinFillWidth(DependencyObject d) => (double)d.GetValue(MinFillWidthProperty);
    public static void SetMinFillWidth(DependencyObject d, double value) => d.SetValue(MinFillWidthProperty, value);

    private static void OnFillColumnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list) return;
        if (!(bool)list.GetValue(AttachedProperty))
        {
            list.SetValue(AttachedProperty, true);
            list.SizeChanged += (_, _) => Apply(list);
            list.Loaded += (_, _) => { Hook(list); Apply(list); };
            list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, args) => { if (args.ViewportWidthChange != 0) Apply(list); }));
        }
        if (list.IsLoaded) { Hook(list); Apply(list); }
    }

    private static void Hook(ListView list)
    {
        if (list.View is not GridView grid) return;
        foreach (var column in grid.Columns)
        {
            if (column is not INotifyPropertyChanged notify) continue;
            notify.PropertyChanged -= Changed; notify.PropertyChanged += Changed;
        }
        void Changed(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(GridViewColumn.ActualWidth)) Apply(list); }
    }

    /// <summary>Width the fill column should have: the viewport minus every other column, never below the minimum.</summary>
    public static double Compute(double available, IEnumerable<double> otherColumns, double minimum) => Math.Max(minimum, Math.Floor(available - otherColumns.Sum() - 1));

    public static void Apply(ListView list)
    {
        if (list.View is not GridView grid || (bool)list.GetValue(BusyProperty)) return;
        int index = GetFillColumn(list);
        if (index < 0 || index >= grid.Columns.Count || list.ActualWidth <= 0) return;
        var scroll = Descendant<ScrollViewer>(list);
        double available = scroll is { ViewportWidth: > 0 } ? scroll.ViewportWidth : list.ActualWidth - list.BorderThickness.Left - list.BorderThickness.Right;
        var widths = new List<double>();
        for (int i = 0; i < grid.Columns.Count; i++)
            if (i != index) widths.Add(double.IsNaN(grid.Columns[i].Width) ? grid.Columns[i].ActualWidth : grid.Columns[i].Width);
        double width = Compute(available, widths, GetMinFillWidth(list));
        var fill = grid.Columns[index];
        if (Math.Abs((double.IsNaN(fill.Width) ? fill.ActualWidth : fill.Width) - width) < 0.5) return;
        list.SetValue(BusyProperty, true);
        try { fill.Width = width; } finally { list.SetValue(BusyProperty, false); }
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
