using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

using NDM.App.Localization;
namespace NDM.App;

/// <summary>Help > Browser connection: the last watchdog report with a line per check, and buttons to check again, repair, copy and open the install guide.</summary>
public partial class BridgeStatusWindow : Window
{
    private static BridgeStatusWindow? _open;
    /// <summary>Test seams. The default clipboard retries because another program can hold it for a moment.</summary>
    public static Func<string, bool> SetClipboard { get; set; } = text => ExtensionGuideWindow.SetClipboard(text);
    public static Action OpenGuide { get; set; } = () => ExtensionGuideWindow.ShowOrActivate();

    private readonly IBridgeWatchdog _watchdog;
    private readonly Action<WatchdogReport> _changed;
    private bool _busy;

    public WatchdogReport? Shown { get; private set; }
    public string OverallLine => OverallText.Text;
    public string StatusMessage => StatusText.Text;
    public StackPanel Rows => ItemsPanel;
    public Button Check => CheckButton;
    public Button Repair => RepairButton;
    public Button Copy => CopyButton;
    public Button Guide => GuideButton;
    public Border Badge => OverallBadge;

    public static BridgeStatusWindow ShowOrActivate(IBridgeWatchdog watchdog)
    {
        if (_open is { IsLoaded: true } open) { WindowActivation.BringToFront(open); return open; }
        var window = new BridgeStatusWindow(watchdog);
        _open = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_open, window)) _open = null; };
        window.Show();
        WindowActivation.BringToFront(window);
        return window;
    }

    public BridgeStatusWindow(IBridgeWatchdog watchdog)
    {
        _watchdog = watchdog;
        InitializeComponent(); WindowPlacements.Track(this, "BridgeStatus");
        _changed = report => Dispatcher.BeginInvoke(new Action(() => Render(report)));
        _watchdog.ReportChanged += _changed;
        Closed += (_, _) => _watchdog.ReportChanged -= _changed;
        if (_watchdog.Last is { } last) Render(last);
        else { Render(null); _ = RunAsync(WatchdogRunMode.CheckNow); }
    }

    private static string BrushKey(CheckState state) => state switch { CheckState.Ok => "SuccessBrush", CheckState.Repaired => "WarningBrush", CheckState.NotInstalled => "MutedBrush", _ => "ErrorBrush" };
    private static string Glyph(CheckState state) => state switch { CheckState.Ok => "\u2713", CheckState.Repaired => "\u2713", CheckState.NotInstalled => "\u2013", _ => "!" };

    /// <summary>Fills the window from a report (null = nothing checked yet).</summary>
    public void Render(WatchdogReport? report)
    {
        Shown = report;
        ItemsPanel.Children.Clear();
        if (report is null)
        {
            OverallText.Text = Loc.T("bridge.win.checking_connection");
            WhenText.Text = "";
            OverallGlyph.Text = "?";
            OverallBadge.SetResourceReference(Border.BackgroundProperty, "MutedBrush");
            return;
        }
        OverallText.Text = report.OverallText;
        WhenText.Text = Loc.T("bridge.win.last_checked", Loc.DateTime(report.TimeUtc.ToLocalTime()));
        var overallState = report.Overall switch { WatchdogOverall.Ok => CheckState.Ok, WatchdogOverall.Repaired => CheckState.Repaired, _ => CheckState.Problem };
        OverallGlyph.Text = Glyph(overallState);
        OverallBadge.SetResourceReference(Border.BackgroundProperty, BrushKey(overallState));
        AutomationProperties.SetName(OverallBadge, WatchdogReport.StateText(overallState));
        foreach (var item in report.Items) ItemsPanel.Children.Add(BuildRow(item));
        if (report.RepairsMade.Count > 0)
        {
            var repairs = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            repairs.Children.Add(new TextBlock { Text = Loc.T("bridge.win.repairs_made"), FontWeight = FontWeights.SemiBold });
            foreach (var line in report.RepairsMade)
            {
                var text = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                repairs.Children.Add(text);
            }
            ItemsPanel.Children.Add(repairs);
        }
    }

    private static FrameworkElement BuildRow(CheckItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var glyph = new TextBlock { Text = Glyph(item.State), FontWeight = FontWeights.Bold, FontSize = 16, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, BrushKey(item.State));
        AutomationProperties.SetName(glyph, WatchdogReport.StateText(item.State));
        var body = new StackPanel();
        var head = new TextBlock { TextWrapping = TextWrapping.Wrap };
        head.Inlines.Add(new System.Windows.Documents.Run(item.Name) { FontWeight = FontWeights.SemiBold });
        head.Inlines.Add(new System.Windows.Documents.Run("  " + WatchdogReport.StateText(item.State)) { FontSize = 12 });
        body.Children.Add(head);
        body.Children.Add(new TextBlock { Text = item.Summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        if (item.Detail.Length > 0)
        {
            var detail = new TextBlock { Text = item.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            body.Children.Add(detail);
        }
        Grid.SetColumn(body, 1);
        grid.Children.Add(glyph); grid.Children.Add(body);
        AutomationProperties.SetName(grid, Loc.T("bridge.report.item", item.Name, WatchdogReport.StateText(item.State), item.Summary));
        return grid;
    }

    /// <summary>Runs a check through the watchdog and shows the result. Buttons stay off while it works.</summary>
    public async Task RunAsync(WatchdogRunMode mode)
    {
        if (_busy) return;
        _busy = true; SetButtons(false);
        StatusText.Text = Loc.T(mode == WatchdogRunMode.Repair ? "bridge.win.repairing" : "bridge.win.checking");
        try
        {
            var report = await _watchdog.RunAsync(mode);
            Render(report);
            StatusText.Text = "";
        }
        catch (Exception ex) { StatusText.Text = Loc.T("bridge.win.check_failed", ErrorMessages.OneLine(ex.Message)); }
        finally { _busy = false; SetButtons(true); }
    }

    private void SetButtons(bool enabled) { CheckButton.IsEnabled = RepairButton.IsEnabled = enabled; }

    private async void Check_Click(object sender, RoutedEventArgs e) => await RunAsync(WatchdogRunMode.CheckNow);
    private async void Repair_Click(object sender, RoutedEventArgs e) => await RunAsync(WatchdogRunMode.Repair);
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Shown is null) { StatusText.Text = Loc.T("bridge.win.no_report"); return; }
        StatusText.Text = SetClipboard(Shown.ToText()) ? Loc.T("bridge.win.copied") : Loc.T("bridge.win.clipboard_busy");
    }
    private void Guide_Click(object sender, RoutedEventArgs e) => OpenGuide();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
