using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>Colours the dot of a page link row by state (status tokens, so every theme reads).</summary>
public sealed class PageLinkDotConverter : IValueConverter
{
    public static readonly PageLinkDotConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is PageLinkState s ? s switch
        {
            PageLinkState.Resolved => "StatusCompletedBrush", PageLinkState.Failed => "StatusFailedBrush",
            PageLinkState.NeedsYou or PageLinkState.NoControl => "StatusPausedBrush",
            PageLinkState.Loading or PageLinkState.WaitingTimer or PageLinkState.Clicked => "StatusDownloadingBrush", _ => "StatusQueuedBrush",
        } : "StatusQueuedBrush";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>1.16.0: progress of the page links of a package (resolved, waiting for the site's timer, needs you, failed) and the "This site needs you" panel with Show the window and Skip this link.</summary>
public partial class PageLinksWindow : Window
{
    private readonly PageResolveCoordinator _coordinator; private readonly string? _package; private bool _closed;
    public PageResolveCoordinator Coordinator => _coordinator;
    public bool NeedsPanelVisible => NeedsPanel.Visibility == Visibility.Visible;
    public string NeedsText => NeedsTitle.Text + " " + NeedsBody.Text;
    public string SummaryText => Summary.Text;
    public System.Windows.Controls.Button ShowWindowButton => ShowButton;
    public System.Windows.Controls.Button SkipLinkButton => SkipButton;

    public PageLinksWindow(PageResolveCoordinator coordinator, string? package = null)
    {
        _coordinator = coordinator; _package = package; InitializeComponent();
        LinksList.ItemsSource = coordinator.Items;
        coordinator.Changed += OnChanged; Loc.LanguageChanged += OnLanguage;
        foreach (var item in coordinator.Items) item.PropertyChanged += Item_Changed;
        Closed += (_, _) => { _closed = true; coordinator.Changed -= OnChanged; Loc.LanguageChanged -= OnLanguage; foreach (var item in coordinator.Items) item.PropertyChanged -= Item_Changed; };
        Update();
    }
    private void OnChanged() { if (!_closed) Dispatcher.BeginInvoke(new Action(Update)); }
    private void Item_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (!_closed && e.PropertyName is "State" or "Challenge") Dispatcher.BeginInvoke(new Action(Update)); }
    private void OnLanguage() { if (_closed) return; foreach (var i in _coordinator.Items) i.RaiseLanguage(); Update(); }

    public void Update()
    {
        if (_closed) return;
        Heading.Text = _package is { Length: > 0 } ? Loc.T("pagelink.heading_package", _package) : Loc.T("pagelink.heading");
        var items = _coordinator.Items; int total = items.Count, done = items.Count(i => i.State == PageLinkState.Resolved), finished = items.Count(i => i.Finished);
        Summary.Text = _coordinator.Running ? Loc.T("pagelink.summary_running", done, total) : Loc.T("pagelink.summary_done", done, total);
        var current = _coordinator.Current;
        bool needs = current is { State: PageLinkState.NeedsYou or PageLinkState.NoControl };
        NeedsPanel.Visibility = needs ? Visibility.Visible : Visibility.Collapsed;
        if (needs)
        {
            bool noControl = current!.State == PageLinkState.NoControl;
            NeedsTitle.Text = Loc.T(noControl ? "pagelink.no_control.title" : "pagelink.needs_you.title");
            NeedsBody.Text = Loc.T(noControl ? "pagelink.no_control.body" : "pagelink.needs_you.body");
        }
        CancelAllButton.IsEnabled = _coordinator.Running && finished < total;
    }
    private void Show_Click(object sender, RoutedEventArgs e) => _coordinator.ShowWindow();
    private void Skip_Click(object sender, RoutedEventArgs e) => _coordinator.SkipCurrent();
    private void CancelAll_Click(object sender, RoutedEventArgs e) => _coordinator.CancelAll();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
