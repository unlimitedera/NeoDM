using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>1.10.0: a drawn document icon (page with a folded corner) with the file extension written inside, tinted by type group. The drawing is a frozen vector shared by every row with the same extension and theme colours, so a refresh allocates nothing and the icon stays crisp at any DPI.</summary>
public sealed class FileTypeIcon : FrameworkElement
{
    public const double IconWidth = 22, IconHeight = 26;
    public static readonly DependencyProperty ExtensionProperty = DependencyProperty.Register(nameof(Extension), typeof(string), typeof(FileTypeIcon), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, OnExtensionChanged));
    public static readonly DependencyProperty FolderProperty = DependencyProperty.Register(nameof(Folder), typeof(bool), typeof(FileTypeIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnExtensionChanged));
    private static readonly DependencyProperty SurfaceProperty = DependencyProperty.Register("Surface", typeof(Brush), typeof(FileTypeIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty TintProperty = DependencyProperty.Register("Tint", typeof(Brush), typeof(FileTypeIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>The extension without a dot ("zip"); empty shows the generic page.</summary>
    public string Extension { get => (string)GetValue(ExtensionProperty); set => SetValue(ExtensionProperty, value); }
    /// <summary>Show a folder instead of a page (a torrent that has no file extension).</summary>
    public bool Folder { get => (bool)GetValue(FolderProperty); set => SetValue(FolderProperty, value); }

    public FileTypeIcon()
    {
        FlowDirection = FlowDirection.LeftToRight; // the letters are never mirrored; the icon sits at the start side of the name in either direction
        SnapsToDevicePixels = true; IsHitTestVisible = false; Focusable = false;
        VerticalAlignment = VerticalAlignment.Center; HorizontalAlignment = HorizontalAlignment.Left;
        SetResourceReference(SurfaceProperty, "SurfaceBrush");
        Describe(); BindTint();
    }
    private static void OnExtensionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { var icon = (FileTypeIcon)d; icon.BindTint(); icon.Describe(); }
    private void BindTint() => SetResourceReference(TintProperty, FileTypeIcons.TokenKey(Folder ? FileGroup.Other : FileTypeIcons.GroupOf(Extension)));
    private void Describe()
    {
        var label = FileTypeIcons.Label(Extension);
        AutomationProperties.SetName(this, Folder ? Loc.T("filetype.a11y_folder") : label.Length == 0 ? Loc.T("filetype.a11y_plain") : Loc.T("filetype.a11y", label));
    }
    protected override Size MeasureOverride(Size availableSize) => new(IconWidth, IconHeight);

    protected override void OnRender(DrawingContext dc)
    {
        if (Surface is not SolidColorBrush surface || Tint is not SolidColorBrush tint) return;
        dc.DrawDrawing(Get(FileTypeIcons.Label(Extension), Folder ? FileGroup.Other : FileTypeIcons.GroupOf(Extension), Folder, surface.Color, tint.Color, VisualTreeHelper.GetDpi(this).PixelsPerDip));
    }
    private Brush? Surface => (Brush?)GetValue(SurfaceProperty);
    private Brush? Tint => (Brush?)GetValue(TintProperty);

    // ---- shared drawing cache ----
    private readonly record struct Key(string Label, FileGroup Group, bool Folder, int Surface, int Tint);
    private static readonly Dictionary<Key, Drawing> Cache = new();
    private static readonly object Gate = new();
    /// <summary>Number of drawings built so far (tests check that identical icons are built once).</summary>
    public static int BuildCount { get; private set; }
    public static int CachedCount { get { lock (Gate) return Cache.Count; } }
    public static void ClearCache() { lock (Gate) Cache.Clear(); }
    private static int Pack(Color c) => c.R << 16 | c.G << 8 | c.B;

    /// <summary>The drawing for an icon (frozen, 22 x 26 units). Same arguments return the same instance.</summary>
    public static Drawing Get(string label, FileGroup group, bool folder, Color surface, Color tint, double pixelsPerDip = 1)
    {
        var key = new Key(label, group, folder, Pack(surface), Pack(tint));
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var found)) return found;
            if (Cache.Count > 600) Cache.Clear();
            var made = Build(label, folder, surface, tint);
            Cache[key] = made; BuildCount++;
            return made;
        }
    }

    /// <summary>Where the label may be drawn inside the page (tests check the drawn pixels stay in the page).</summary>
    public static readonly Rect LabelArea = new(2.5, 10, 17, 13.5);
    public static Geometry PageGeometry()
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(1.5, 1.5), true, true);
            c.LineTo(new Point(14.5, 1.5), true, false); c.LineTo(new Point(20.5, 7.5), true, false);
            c.LineTo(new Point(20.5, 24.5), true, false); c.LineTo(new Point(1.5, 24.5), true, false);
        }
        g.Freeze(); return g;
    }
    private static Drawing Build(string label, bool folder, Color surface, Color tint)
    {
        var page = FileTypeIcons.PageFill(surface, tint);
        var fill = new SolidColorBrush(page); var line = new SolidColorBrush(tint);
        var pen = new Pen(line, 1) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        fill.Freeze(); line.Freeze(); pen.Freeze();
        var group = new DrawingGroup();
        if (folder)
        {
            var f = new StreamGeometry();
            using (var c = f.Open())
            {
                c.BeginFigure(new Point(1.5, 5.5), true, true);
                c.LineTo(new Point(8.5, 5.5), true, false); c.LineTo(new Point(10.5, 8.5), true, false); c.LineTo(new Point(20.5, 8.5), true, false);
                c.LineTo(new Point(20.5, 22.5), true, false); c.LineTo(new Point(1.5, 22.5), true, false);
            }
            f.Freeze();
            group.Children.Add(new GeometryDrawing(fill, pen, f));
        }
        else
        {
            group.Children.Add(new GeometryDrawing(fill, pen, PageGeometry()));
            var foldFill = new SolidColorBrush(FileTypeIcons.Mix(page, tint, 0.38)); foldFill.Freeze();
            var fold = new StreamGeometry();
            using (var c = fold.Open())
            {
                c.BeginFigure(new Point(14.5, 1.5), true, true);
                c.LineTo(new Point(14.5, 7.5), true, false); c.LineTo(new Point(20.5, 7.5), true, false);
            }
            fold.Freeze();
            group.Children.Add(new GeometryDrawing(foldFill, pen, fold));
            if (label.Length == 0)
            {
                var marks = new StreamGeometry();
                using (var c = marks.Open())
                {
                    foreach (var y in new[] { 13.5, 17.5, 21.0 }) { c.BeginFigure(new Point(5.5, y), false, false); c.LineTo(new Point(y > 20 ? 12 : 16.5, y), true, false); }
                }
                marks.Freeze();
                group.Children.Add(new GeometryDrawing(null, pen, marks));
            }
            else
            {
                var face = new Typeface(new FontFamily("Cascadia Mono, Consolas, Courier New"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                double size = label.Length >= 4 ? 7.2 : 8.2;
                var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, line, 1.0);
                var origin = new Point(LabelArea.X + (LabelArea.Width - text.WidthIncludingTrailingWhitespace) / 2, LabelArea.Y + (LabelArea.Height - text.Height) / 2 + 0.5);
                var geometry = text.BuildGeometry(origin); geometry.Freeze();
                group.Children.Add(new GeometryDrawing(line, null, geometry));
            }
        }
        group.Freeze();
        return group;
    }
}
