using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NDM.App;

/// <summary>Options > General: the NeoDM extension in each browser that has connected (1.11.0), with its version and whether it matches the app.</summary>
public partial class SettingsWindow
{
    /// <summary>The lines shown, in order (for tests and screen readers).</summary>
    public IReadOnlyList<BrowserExtensionRow> BrowserExtensionRows { get; private set; } = Array.Empty<BrowserExtensionRow>();

    private void FillBrowserExtensions(AppSettings settings)
    {
        BrowserExtensionList.Children.Clear();
        var rows = settings.BrowserExtensions.OrderByDescending(r => r.LastSeen).Take(BrowserExtensionBook.MaxBrowsers)
            .Select(r => BrowserExtensionRow.From(r, AboutInfo.Version, DateTimeOffset.UtcNow)).ToList();
        BrowserExtensionRows = rows;
        BrowserExtensionEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in rows)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 6, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, row.Ok ? "StatusCompletedBrush" : row.Status == ExtensionStatus.Newer ? "StatusQueuedBrush" : "StatusPausedBrush");
            var text = new StackPanel();
            var head = SelectableText.Create($"{row.Browser}   {row.VersionText}", wrap: false);
            head.FontWeight = FontWeights.SemiBold;
            text.Children.Add(head);
            text.Children.Add(SelectableText.Create(row.StatusText + " " + row.LastSeenText, muted: true));
            if (row.FixText.Length > 0) text.Children.Add(SelectableText.Create(row.FixText, muted: true));
            var line = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(text, 1);
            line.Children.Add(dot); line.Children.Add(text);
            System.Windows.Automation.AutomationProperties.SetName(line, row.Summary);
            BrowserExtensionList.Children.Add(line);
        }
    }
}
