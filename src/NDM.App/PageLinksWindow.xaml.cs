// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
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
    public System.Windows.Controls.Button OpenAgainBtn => OpenAgainButton;
    public System.Windows.Controls.Button CancelAllBtn => CancelAllButton;
    public System.Windows.Controls.Button BackBtn => BackButton; public System.Windows.Controls.Button ForwardBtn => ForwardButton; public System.Windows.Controls.Button ReloadBtn => ReloadButton;
    public System.Windows.Controls.Button StopBtn => StopButton; public System.Windows.Controls.Button OpenBrowserBtn => OpenBrowserButton; public System.Windows.Controls.Button CopyBtn => CopyButton;
    public System.Windows.Controls.Button CloseBtn => CloseButton;
    private IPageNavigation? _hooked;

    public PageLinksWindow(PageResolveCoordinator coordinator, string? package = null)
    {
        _coordinator = coordinator; _package = package; InitializeComponent();
        LinksList.ItemsSource = coordinator.Items;
        coordinator.Changed += OnChanged; Loc.LanguageChanged += OnLanguage;
        foreach (var item in coordinator.Items) item.PropertyChanged += Item_Changed;
        coordinator.Items.CollectionChanged += Items_CollectionChanged; // links added while the window is open
        Closed += (_, _) => { coordinator.Items.CollectionChanged -= Items_CollectionChanged; _closed = true; if (_hooked is not null) _hooked.NavigationChanged -= OnNav; coordinator.Changed -= OnChanged; Loc.LanguageChanged -= OnLanguage; foreach (var item in coordinator.Items) item.PropertyChanged -= Item_Changed; };
        Update();
    }
    private void Items_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (PageLinkItem item in e.NewItems) { item.PropertyChanged -= Item_Changed; item.PropertyChanged += Item_Changed; }
    }
    private void OnChanged() { if (!_closed) Dispatcher.BeginInvoke(new Action(Update)); }
    private void Item_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (!_closed && e.PropertyName is "State" or "Challenge" or "InBrowser") Dispatcher.BeginInvoke(new Action(Update)); }
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
            bool noControl = current!.State == PageLinkState.NoControl, inBrowser = current.InBrowser && !noControl;
            NeedsTitle.Text = Loc.T(noControl ? "pagelink.no_control.title" : "pagelink.needs_you.title");
            NeedsBody.Text = Loc.T(noControl ? "pagelink.no_control.body" : inBrowser ? "pagelink.needs_you_browser.body" : "pagelink.needs_you.body");
            OpenAgainButton.Visibility = inBrowser ? Visibility.Visible : Visibility.Collapsed; ShowButton.Visibility = inBrowser ? Visibility.Collapsed : Visibility.Visible;
        }
        CancelAllButton.IsEnabled = _coordinator.Running && finished < total;
        var nav = _coordinator.Navigation;
        if (!ReferenceEquals(nav, _hooked)) { if (_hooked is not null) _hooked.NavigationChanged -= OnNav; _hooked = nav; if (nav is not null) nav.NavigationChanged += OnNav; }
        BackButton.IsEnabled = nav?.CanGoBack == true; ForwardButton.IsEnabled = nav?.CanGoForward == true; ReloadButton.IsEnabled = nav is { IsClosed: false }; StopButton.IsEnabled = nav?.Loading == true;
        bool hasAddress = _coordinator.CurrentAddress is not null; OpenBrowserButton.IsEnabled = CopyButton.IsEnabled = hasAddress;
    }
    private void Show_Click(object sender, RoutedEventArgs e) => _coordinator.ShowWindow();
    private void OpenAgain_Click(object sender, RoutedEventArgs e) => _coordinator.OpenAgain();
    private void Skip_Click(object sender, RoutedEventArgs e) => _coordinator.SkipCurrent();
    private void CancelAll_Click(object sender, RoutedEventArgs e) => _coordinator.CancelAll();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void OnNav() { if (!_closed) Dispatcher.BeginInvoke(new Action(Update)); }
    private void Back_Click(object sender, RoutedEventArgs e) => _coordinator.Navigation?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _coordinator.Navigation?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => _coordinator.Navigation?.Reload();
    private void Stop_Click(object sender, RoutedEventArgs e) => _coordinator.Navigation?.StopLoading();
    private void OpenBrowser_Click(object sender, RoutedEventArgs e) => PageWindowActions.OpenInBrowser(_coordinator.CurrentAddress);
    private void Copy_Click(object sender, RoutedEventArgs e) => PageWindowActions.CopyAddress(_coordinator.CurrentAddress);
    /// <summary>Closing always works. If the link on screen is waiting for the person, closing gives that link up (skips it) so nothing waits for a window nobody sees.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_coordinator.Running && _coordinator.Current is { State: PageLinkState.NeedsYou or PageLinkState.NoControl }) _coordinator.SkipCurrent();
        base.OnClosing(e);
    }
}
