using System.ComponentModel;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>1.9.0 link grabber: paste any text, see the web links in a table, optionally check them, choose a package name and folder, then add them to the queue (or paused). Staging only: nothing downloads until Add.</summary>
public partial class LinkGrabberWindow : Window
{
    private readonly DownloadManager _manager; private readonly AppSettings _settings; private readonly Action? _saveSettings;
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _check;
    private bool _loading, _adding, _closed;
    private string _sortProperty = ""; private ListSortDirection _sortDirection;
    private GrabberParseSummary? _last;
    private const string AllHostsTag = "*";
    public LinkGrabberLogic Logic { get; }
    public GrabberAddOutcome? Outcome { get; private set; }
    /// <summary>The running (or last) check pass; tests await it.</summary>
    public Task CheckTask { get; private set; } = Task.CompletedTask;
    public bool Checking => _check is not null;

    public LinkGrabberWindow(DownloadManager manager, AppSettings settings, string? initialText = null, Action? saveSettings = null)
    {
        _manager = manager; _settings = settings; _saveSettings = saveSettings;
        Logic = new LinkGrabberLogic(manager, settings);
        InitializeComponent(); WindowPlacements.Track(this, "LinkGrabber");
        _view = CollectionViewSource.GetDefaultView(Logic.Entries); _view.Filter = Matches;
        LinksList.ItemsSource = _view;
        PackageBox.ItemsSource = settings.RecentPackageNames.ToList();
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => { _debounce.Stop(); ParseNow(); }, Dispatcher) { IsEnabled = false };
        PackageBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateDestination()));
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; AddQueue_Click(this, new RoutedEventArgs()); } };
        Loc.LanguageChanged += OnLanguageChanged; Closed += (_, _) => { _closed = true; Loc.LanguageChanged -= OnLanguageChanged; _check?.Cancel(); _debounce.Stop(); };
        UpdateDestination(); UpdateCounts();
        if (!string.IsNullOrEmpty(initialText)) { LinksInput.Text = initialText; ParseNow(); }
        Loaded += (_, _) => { if (Logic.Entries.Count == 0) LinksInput.Focus(); else LinksList.Focus(); };
    }

    private void OnLanguageChanged()
    {
        if (_closed) return;
        foreach (var e in Logic.Entries) e.RaiseLanguage();
        ShowSummary(); UpdateCounts();
    }

    // ---- parsing ----
    private void LinksInput_Changed(object sender, TextChangedEventArgs e) { if (_loading) return; _debounce.Stop(); _debounce.Start(); }
    private void Find_Click(object sender, RoutedEventArgs e) { _debounce.Stop(); ParseNow(); }
    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.ExternalException) { ErrorText.Text = Loc.T("main.notice.clipboard_unavailable"); return; }
        if (text.Length == 0) { ErrorText.Text = Loc.T("grab.err.clipboard_empty"); return; }
        ErrorText.Text = "";
        LinksInput.Text = (LinksInput.Text.Length > 0 && !LinksInput.Text.EndsWith('\n') ? LinksInput.Text + Environment.NewLine : LinksInput.Text) + text;
        _debounce.Stop(); ParseNow();
    }
    /// <summary>Reads the text box and adds new rows. Safe to call repeatedly: links already in the table are kept as they are.</summary>
    public GrabberParseSummary ParseNow()
    {
        ErrorText.Text = "";
        var summary = Logic.Load(LinksInput.Text);
        _last = LinkParserTotals(LinksInput.Text, summary);
        RebuildHosts(); ShowSummary(); UpdateCounts(); UpdateDestination(); _view.Refresh(); _ = WarnPrivateAsync();
        return summary;
    }
    private static GrabberParseSummary LinkParserTotals(string text, GrabberParseSummary loaded)
    {
        var all = LinkParser.Parse(text);
        return loaded with { Duplicates = all.Duplicates, Ignored = all.Ignored, TooLong = all.TooLong, Truncated = all.Truncated || loaded.Truncated };
    }
    private void ShowSummary()
    {
        if (_last is null || Logic.Entries.Count == 0 && LinksInput.Text.Trim().Length == 0) { Summary.Text = Loc.T("grab.summary.empty"); return; }
        var parts = new List<string> { Loc.T("grab.summary.found", Logic.Entries.Count) };
        if (_last.Duplicates > 0) parts.Add(Loc.T("grab.summary.duplicates", _last.Duplicates));
        int queued = Logic.Entries.Count(e => e.State == LinkState.Duplicate); if (queued > 0) parts.Add(Loc.T("grab.summary.queued", queued));
        if (_last.Ignored > 0) parts.Add(Loc.T("grab.summary.ignored", _last.Ignored));
        if (_last.TooLong > 0) parts.Add(Loc.T("grab.summary.too_long", _last.TooLong));
        if (_last.Truncated) parts.Add(Loc.T("grab.summary.capped", LinkParser.MaxLinks));
        int youtube = Logic.Entries.Count(e => e.State == LinkState.Unsupported); if (youtube > 0) parts.Add(Loc.T("grab.summary.youtube", youtube));
        Summary.Text = string.Join("  ·  ", parts);
    }
    private async Task WarnPrivateAsync()
    {
        try
        {
            var hosts = Logic.Entries.Select(e => e.Url).GroupBy(u => new Uri(u).IdnHost).Take(50).Select(g => g.First()).ToList();
            bool any = false;
            foreach (var url in hosts)
                if (await PrivateNetwork.IsPrivateUrlAsync(url, (_, _) => Task.FromResult(Array.Empty<IPAddress>()), TimeSpan.FromMilliseconds(200))) { any = true; break; }
            if (_closed) return;
            WarnText.Text = any ? Loc.T("grab.warn.private") : ""; WarnText.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException) { }
    }

    // ---- table ----
    private bool Matches(object item)
    {
        var e = (LinkEntry)item;
        if (HostFilter.SelectedItem is ComboBoxItem { Tag: string host } && host != AllHostsTag && !e.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) return false;
        var q = FilterBox.Text.Trim();
        if (q.Length == 0) return true;
        foreach (var word in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word.TrimStart('.');
            if (!(e.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Host.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Url.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Extension.Equals(w, StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }
    private void Filter_Changed(object sender, TextChangedEventArgs e) { _view.Refresh(); UpdateCounts(); }
    private void HostFilter_Changed(object sender, SelectionChangedEventArgs e) { if (_loading) return; _view.Refresh(); UpdateCounts(); }
    private void RebuildHosts()
    {
        _loading = true;
        try
        {
            var current = (HostFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? AllHostsTag;
            var items = new List<ComboBoxItem> { new() { Content = Loc.T("grab.filter.all_hosts"), Tag = AllHostsTag } };
            foreach (var group in Logic.Entries.GroupBy(e => e.Host, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Take(200))
                items.Add(new ComboBoxItem { Content = $"{group.Key} ({group.Count()})", Tag = group.Key });
            HostFilter.ItemsSource = items; HostFilter.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == current) ?? items[0];
        }
        finally { _loading = false; }
    }
    private IEnumerable<LinkEntry> Visible => _view.Cast<LinkEntry>();
    public IReadOnlyList<LinkEntry> VisibleEntries => Visible.ToList();
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var r in Visible.Where(r => r.State != LinkState.Unsupported)) r.Selected = true; UpdateCounts(); }
    private void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var r in Visible) r.Selected = false; UpdateCounts(); }
    private void Invert_Click(object sender, RoutedEventArgs e) { foreach (var r in Visible.Where(r => r.State != LinkState.Unsupported)) r.Selected = !r.Selected; UpdateCounts(); }
    private void AllBox_Click(object sender, RoutedEventArgs e)
    {
        bool on = Visible.Any(r => !r.Selected && r.State != LinkState.Unsupported);
        foreach (var r in Visible.Where(r => r.State != LinkState.Unsupported)) r.Selected = on;
        UpdateCounts();
    }
    private void RowBox_Changed(object sender, RoutedEventArgs e) => UpdateCounts();
    private void RemoveRows_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in LinksList.SelectedItems.Cast<LinkEntry>().ToList()) Logic.Entries.Remove(row);
        RebuildHosts(); ShowSummary(); UpdateCounts();
    }
    private void LinksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _loading = true;
        try
        {
            var single = LinksList.SelectedItems.Count == 1 ? LinksList.SelectedItem as LinkEntry : null;
            RenameBox.IsEnabled = single is not null; RenameBox.Text = single?.Name ?? "";
        }
        finally { _loading = false; }
    }
    private void Rename_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || LinksList.SelectedItems.Count != 1 || LinksList.SelectedItem is not LinkEntry row) return;
        var name = RenameBox.Text.Trim();
        if (name.Length > 0) row.Name = name;
    }
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader { Tag: string property }) return;
        _sortDirection = _sortProperty == property && _sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending; _sortProperty = property;
        _view.SortDescriptions.Clear(); _view.SortDescriptions.Add(new SortDescription(property, _sortDirection));
    }
    private void UpdateCounts()
    {
        if (CountText is null) return;
        int visible = Visible.Count(), selected = Logic.Entries.Count(e => e.Selected && e.State != LinkState.Unsupported), visibleSelected = Visible.Count(r => r.Selected);
        CountText.Text = visible == Logic.Entries.Count ? Loc.T("grab.count", selected, Logic.Entries.Count) : Loc.T("grab.count_filtered", selected, visible, Logic.Entries.Count);
        AllBox.IsChecked = visible == 0 || visibleSelected == 0 ? false : visibleSelected == visible ? true : null;
        AddButton.IsEnabled = PausedButton.IsEnabled = selected > 0 && !_adding;
        CheckButton.IsEnabled = Logic.Entries.Count > 0 || Checking;
    }

    // ---- destination ----
    private void Dest_Changed(object sender, RoutedEventArgs e) => UpdateDestination();
    private void Folder_Changed(object sender, TextChangedEventArgs e) { if (FolderBox.IsKeyboardFocusWithin && FolderRadio.IsChecked != true) FolderRadio.IsChecked = true; }
    private void UpdateDestination()
    {
        if (SubfolderBox is null || FolderBox is null) return;
        bool folder = FolderRadio.IsChecked == true;
        FolderBox.IsEnabled = BrowseButton.IsEnabled = folder;
        SubfolderBox.IsEnabled = LinkGrabberLogic.CleanPackage(PackageBox.Text) is not null;
        if (!SubfolderBox.IsEnabled) SubfolderBox.IsChecked = false;
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = Loc.T("options.picker.destination"), Multiselect = false };
        try { if (Path.IsPathFullyQualified(FolderBox.Text.Trim()) && Directory.Exists(FolderBox.Text.Trim())) picker.InitialDirectory = FolderBox.Text.Trim(); } catch (ArgumentException) { }
        if (picker.ShowDialog(this) == true) { FolderBox.Text = picker.FolderName; FolderRadio.IsChecked = true; }
    }
    public GrabberDestination CurrentDestination => new(PackageBox.Text, FolderRadio.IsChecked == true ? FolderBox.Text : null, SubfolderBox.IsChecked == true);
    /// <summary>The folder files will go to, or null when the category rules decide (shown as a hint under the package fields by tests and the status line).</summary>
    public string? PlannedFolder { get { try { return Logic.PlanFolder(CurrentDestination); } catch (UserMessageException) { return null; } } }

    // ---- check ----
    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_check is not null) { _check.Cancel(); return; }
        CheckTask = CheckAsync(); await CheckTask;
    }
    public async Task CheckAsync()
    {
        if (_check is not null || Logic.Entries.Count == 0) return;
        if (SynchronizationContext.Current is null) await Dispatcher.Yield(DispatcherPriority.Normal); // started before the dispatcher ran a frame (tests): continue on the UI thread
        _check = new CancellationTokenSource(); var token = _check.Token;
        CheckButton.Content = Loc.T("grab.btn.stop_check"); CheckBar.Visibility = Visibility.Visible; CheckBar.Value = 0; ErrorText.Text = "";
        var progress = new Progress<(int Done, int Total)>(p => { if (_closed) return; CheckBar.Value = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total; StatusText.Text = Loc.T("grab.status.checking", p.Done, p.Total); });
        try { await Logic.CheckAsync(Visible.ToList(), token, progress); }
        finally
        {
            bool cancelled = token.IsCancellationRequested;
            _check.Dispose(); _check = null;
            if (!_closed)
            {
                foreach (var row in Logic.Entries.Where(r => r.State == LinkState.Checking)) row.State = LinkState.Ready;
                CheckButton.Content = Loc.T("grab.btn.check"); CheckBar.Visibility = Visibility.Collapsed;
                var all = Logic.Entries;
                StatusText.Text = Loc.T(cancelled ? "grab.status.check_stopped" : "grab.status.check_done", all.Count(r => r.State == LinkState.Online), all.Count(r => r.State == LinkState.Offline), all.Count(r => r.State is LinkState.Unknown or LinkState.NotChecked or LinkState.Page));
                UpdateCounts(); _view.Refresh();
            }
        }
    }

    // ---- add ----
    private async void AddQueue_Click(object sender, RoutedEventArgs e) => await AddAsync(true);
    private async void AddPaused_Click(object sender, RoutedEventArgs e) => await AddAsync(false);
    /// <summary>Adds the ticked rows in table order. Returns true when the window can close.</summary>
    public async Task<bool> AddAsync(bool start)
    {
        if (_adding) return false;
        if (SynchronizationContext.Current is null) await Dispatcher.Yield(DispatcherPriority.Normal);
        if (!Logic.Entries.Any(r => r.Selected && r.State != LinkState.Unsupported)) { ErrorText.Text = Loc.T("grab.err.none_selected"); return false; }
        _adding = true; ErrorText.Text = ""; UpdateCounts();
        try
        {
            _check?.Cancel();
            var outcome = await Logic.AddSelectedAsync(CurrentDestination, start);
            Outcome = outcome;
            if (outcome.Package is { } package)
            {
                _settings.RecentPackageNames = new[] { package }.Concat(_settings.RecentPackageNames.Where(n => !n.Equals(package, StringComparison.OrdinalIgnoreCase))).Take(25).ToList();
                try { _saveSettings?.Invoke(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (outcome.Added == 0) { ErrorText.Text = Loc.T("grab.err.none_added"); return false; }
            try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
            return true;
        }
        catch (UserMessageException ex) { ErrorText.Text = ErrorMessages.Describe(ex); return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { ErrorText.Text = Loc.T("grab.err.not_added"); return false; }
        finally { _adding = false; UpdateCounts(); }
    }
}
