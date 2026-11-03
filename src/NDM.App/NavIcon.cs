using System.Windows;
using System.Windows.Media;
namespace NDM.App;

/// <summary>Line icon (a Geometry on a 24 x 24 grid) shown by the sidebar list items and icon buttons; the template draws it with the control's current text colour.</summary>
public static class NavIcon
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.RegisterAttached("Data", typeof(Geometry), typeof(NavIcon), new PropertyMetadata(null));
    public static Geometry? GetData(DependencyObject d) => (Geometry?)d.GetValue(DataProperty);
    public static void SetData(DependencyObject d, Geometry? value) => d.SetValue(DataProperty, value);
}
