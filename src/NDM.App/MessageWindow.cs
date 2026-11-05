using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>1.11.0: the themed replacement for the system message box (which ignores the theme: white body, light title bar, its own buttons). Same buttons, default answer and Escape behaviour; text can be selected and copied.</summary>
public sealed class MessageWindow : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;
    public IReadOnlyList<Button> ButtonList { get; }
    public TextBox MessageText { get; }

    public MessageWindow(string text, string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        if (Application.Current?.TryFindResource(typeof(Window)) is Style windowStyle) Style = windowStyle;
        Title = title; Width = 460; MinWidth = 340; MaxWidth = 640; SizeToContent = SizeToContent.Height; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FlowDirection = Loc.FlowDirection;
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new DockPanel();
        if (Glyph(image) is { } glyph) { DockPanel.SetDock(glyph, Dock.Left); top.Children.Add(glyph); }
        MessageText = new TextBox { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxHeight = 360 };
        if (Application.Current?.TryFindResource("SelectableText") is Style selectable) MessageText.Style = selectable;
        AutomationProperties_Name(MessageText, title);
        top.Children.Add(MessageText);
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        Grid.SetRow(row, 1);
        var list = new List<Button>();
        foreach (var (result, label) in Choices(buttons))
        {
            var b = new Button { Content = label, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Tag = result, IsCancel = result is MessageBoxResult.Cancel || (result is MessageBoxResult.No && buttons == MessageBoxButton.YesNo) || (result is MessageBoxResult.OK && buttons == MessageBoxButton.OK) };
            bool isDefault = defaultResult != MessageBoxResult.None ? result == defaultResult : list.Count == 0;
            b.IsDefault = isDefault;
            if (isDefault && Application.Current?.TryFindResource("PrimaryButton") is Style primary) b.Style = primary;
            b.Click += (_, _) => { Result = result; try { DialogResult = true; } catch (InvalidOperationException) { Close(); } };
            list.Add(b); row.Children.Add(b);
        }
        ButtonList = list;
        root.Children.Add(top); root.Children.Add(row);
        Content = root;
        Closing += (_, _) => { if (Result == MessageBoxResult.None) Result = CloseResult(buttons); };
        Loaded += (_, _) => (list.FirstOrDefault(b => b.IsDefault) ?? list.FirstOrDefault())?.Focus();
    }

    private static void AutomationProperties_Name(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);

    private static MessageBoxResult CloseResult(MessageBoxButton b) => b switch { MessageBoxButton.OK => MessageBoxResult.OK, MessageBoxButton.OKCancel => MessageBoxResult.Cancel, MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel, _ => MessageBoxResult.No };

    private static IEnumerable<(MessageBoxResult, string)> Choices(MessageBoxButton b)
    {
        string ok = Loc.T("message.btn.ok"), yes = Loc.T("message.btn.yes"), no = Loc.T("message.btn.no"), cancel = Loc.T("common.btn.cancel");
        return b switch
        {
            MessageBoxButton.OK => new[] { (MessageBoxResult.OK, ok) },
            MessageBoxButton.OKCancel => new[] { (MessageBoxResult.OK, ok), (MessageBoxResult.Cancel, cancel) },
            MessageBoxButton.YesNo => new[] { (MessageBoxResult.Yes, yes), (MessageBoxResult.No, no) },
            _ => new[] { (MessageBoxResult.Yes, yes), (MessageBoxResult.No, no), (MessageBoxResult.Cancel, cancel) },
        };
    }

    private static FrameworkElement? Glyph(MessageBoxImage image)
    {
        string? key = image switch { MessageBoxImage.Error => "IconFailed", MessageBoxImage.Warning => "IconFailed", MessageBoxImage.Question => "IconFailed", MessageBoxImage.Information => "IconCompleted", _ => null };
        string brush = image switch { MessageBoxImage.Error => "ErrorBrush", MessageBoxImage.Warning => "WarningBrush", _ => "AccentBrush" };
        if (key is null || Application.Current?.TryFindResource(key) is not Geometry geometry) return null;
        var path = new System.Windows.Shapes.Path { Data = geometry, StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Fill = Brushes.Transparent };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush);
        return new Viewbox { Width = 34, Height = 34, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top, FlowDirection = FlowDirection.LeftToRight, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
    }

    /// <summary>Shows the window modally (or, when <paramref name="owner"/> is hidden, centred on the screen). Tests set <see cref="Dialogs.Override"/> instead.</summary>
    public static MessageBoxResult Ask(Window? owner, string text, string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        var window = new MessageWindow(text, title, buttons, image, defaultResult);
        if (owner is { IsVisible: true, WindowState: not WindowState.Minimized }) window.Owner = owner; else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return window.Result;
    }
}
