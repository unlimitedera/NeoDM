using System.Windows;
using System.Windows.Media;
namespace NDM.App;

/// <summary>Line icon (a Geometry on a 24 x 24 grid) shown by the sidebar list items and icon buttons; the template draws it with the control's current text colour.</summary>
public static class NavIcon
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.RegisterAttached("Data", typeof(Geometry), typeof(NavIcon), new PropertyMetadata(null));
    public static Geometry? GetData(DependencyObject d) => (Geometry?)d.GetValue(DataProperty);
    public static void SetData(DependencyObject d, Geometry? value) => d.SetValue(DataProperty, value);
    /// <summary>Moves a list entry to the side (the category entries below All downloads).</summary>
    public static readonly DependencyProperty IndentProperty = DependencyProperty.RegisterAttached("Indent", typeof(Thickness), typeof(NavIcon), new PropertyMetadata(new Thickness(0)));
    public static Thickness GetIndent(DependencyObject d) => (Thickness)d.GetValue(IndentProperty);
    public static void SetIndent(DependencyObject d, Thickness value) => d.SetValue(IndentProperty, value);
    /// <summary>Shows the expand arrow of a tree parent.</summary>
    public static readonly DependencyProperty ChevronProperty = DependencyProperty.RegisterAttached("Chevron", typeof(bool), typeof(NavIcon), new PropertyMetadata(false));
    public static bool GetChevron(DependencyObject d) => (bool)d.GetValue(ChevronProperty);
    public static void SetChevron(DependencyObject d, bool value) => d.SetValue(ChevronProperty, value);
    /// <summary>Turns the expand arrow: true = pointing down.</summary>
    public static readonly DependencyProperty ExpandedProperty = DependencyProperty.RegisterAttached("Expanded", typeof(bool), typeof(NavIcon), new PropertyMetadata(true));
    public static bool GetExpanded(DependencyObject d) => (bool)d.GetValue(ExpandedProperty);
    public static void SetExpanded(DependencyObject d, bool value) => d.SetValue(ExpandedProperty, value);
}
