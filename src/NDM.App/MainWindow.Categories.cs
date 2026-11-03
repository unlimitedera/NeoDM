using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>1.10.0: the category list below All downloads in the sidebar (see <see cref="CategoryTree"/>).</summary>
public partial class MainWindow
{
    private string _catSignature = "", _catEntriesSignature = "";
    private int _catRevision;
    private bool _rebuildingNav;
    private IReadOnlyList<CategoryEntry> _catEntries = Array.Empty<CategoryEntry>();
    private readonly Dictionary<string, ListBoxItem> _catItems = new();
    private readonly Dictionary<string, int> _catCounts = new();
    private NavTreeItem AllItem => (NavTreeItem)Navigation.Items[0];
    /// <summary>The All downloads entry (tests read and drive it).</summary>
    public NavTreeItem AllDownloadsItem => AllItem;
    /// <summary>Category entries shown below All downloads, in order: (key, shown count).</summary>
    public IReadOnlyList<(string Key, int Count)> CategoryEntries => _catItems.Select(p => (p.Key, _catCounts.GetValueOrDefault(p.Key))).OrderBy(p => _catOrder(p.Key)).ToArray();
    private int _catOrder(string key) { for (int i = 0; i < _catEntries.Count; i++) if (_catEntries[i].Key == key) return i; return int.MaxValue; }
    /// <summary>Active category key, or null when no category filter is on.</summary>
    public string? ActiveCategory => _filter.StartsWith(CategoryTree.FilterPrefix, StringComparison.Ordinal) ? _filter[CategoryTree.FilterPrefix.Length..] : null;
    public string ActiveFilter => _filter;
    public ListBoxItem? CategoryItem(string key) => _catItems.GetValueOrDefault(key);
    /// <summary>Selects a category entry (the list expands first when it is collapsed).</summary>
    public void SelectCategory(string key)
    {
        if (!AllItem.IsExpanded) AllItem.IsExpanded = true;
        if (_catItems.TryGetValue(key, out var item)) { Navigation.SelectedItem = item; }
    }

    private void InitCategoryTree()
    {
        AllItem.IsExpanded = _settings.CategoryTreeExpanded;
        AllItem.Expanded += (_, expanded) => OnTreeExpanded(expanded);
        _catSignature = CategoryTree.Signature(_settings);
        SyncChevronTip();
        RebuildCategoryItems();
        Navigation.PreviewKeyDown += Navigation_PreviewKeyDown;
    }

    private void OnTreeExpanded(bool expanded)
    {
        if (_closed || _rebuildingNav) return;
        _settings.CategoryTreeExpanded = expanded;
        try { _saveSettings(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // collapsing while a category is chosen goes back to All downloads: a highlighted entry that is hidden would be confusing
        bool leave = !expanded && ActiveCategory is not null;
        RebuildCategoryItems();
        if (leave) Navigation.SelectedItem = AllItem;
        SyncChevronTip(); UpdateAllAutomation();
    }

    private void SyncChevronTip()
    {
        AllItem.ApplyTemplate();
        if (AllItem.Template?.FindName("Chevron", AllItem) is FrameworkElement chevron) chevron.ToolTip = Loc.T(AllItem.IsExpanded ? "nav.tip.collapse" : "nav.tip.expand");
    }

    private string CategoryOf(DownloadRow row)
    {
        if (row.CatRevision == _catRevision && ReferenceEquals(row.CatName, row.FileName) && ReferenceEquals(row.CatUrl, row.Url) && row.CatKey.Length > 0) return row.CatKey;
        row.CatKey = CategoryTree.KeyOf(_settings, row.FileName, row.Url, row.Referer);
        row.CatRevision = _catRevision; row.CatName = row.FileName; row.CatUrl = row.Url;
        return row.CatKey;
    }

    /// <summary>Reads the category setup; returns true when it changed (the rows are sorted into categories again).</summary>
    private bool CategorySetupChanged()
    {
        var signature = CategoryTree.Signature(_settings);
        if (signature == _catSignature) return false;
        _catSignature = signature; _catRevision++;
        return true;
    }

    private void RebuildCategoryItems(bool force = false)
    {
        var entries = CategoryTree.Entries(_settings);
        var entriesSignature = string.Join("\n", entries.Select(e => e.Key + "\t" + e.Name)) + "|" + AllItem.IsExpanded;
        if (!force && entriesSignature == _catEntriesSignature) return;
        _catEntriesSignature = entriesSignature; _catEntries = entries;
        var wanted = ActiveCategory;
        _rebuildingNav = true;
        try
        {
            foreach (var old in Navigation.Items.OfType<ListBoxItem>().Where(i => i.Tag is string t && t.StartsWith(CategoryTree.FilterPrefix, StringComparison.Ordinal)).ToArray()) Navigation.Items.Remove(old);
            _catItems.Clear();
            if (AllItem.IsExpanded)
            {
                int at = 1;
                foreach (var entry in entries)
                {
                    var item = MakeCategoryItem(entry);
                    Navigation.Items.Insert(at++, item); _catItems[entry.Key] = item;
                }
            }
            if (wanted is not null && _catItems.TryGetValue(wanted, out var keep)) Navigation.SelectedItem = keep;
        }
        finally { _rebuildingNav = false; }
        if (wanted is not null && !_catItems.ContainsKey(wanted) && AllItem.IsExpanded)
        {
            // the chosen category was deleted, renamed or switched off: fall back to All downloads
            Navigation.SelectedItem = AllItem;
            if (_filter.StartsWith(CategoryTree.FilterPrefix, StringComparison.Ordinal)) FilterChanged(Navigation, null!);
        }
        UpdateCategoryCounts(true);
    }

    private ListBoxItem MakeCategoryItem(CategoryEntry entry)
    {
        var name = new TextBlock { Text = entry.Name, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Tag = "count" };
        if (TryFindResource("Muted") is Style muted) count.Style = muted;
        count.SetValue(DockPanel.DockProperty, Dock.Right);
        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(count); panel.Children.Add(name);
        var item = new ListBoxItem { Tag = CategoryTree.FilterPrefix + entry.Key, Content = panel, Padding = new Thickness(10, 7, 8, 7), Margin = new Thickness(0, 0, 0, 2) };
        NavIcon.SetData(item, TryFindResource("IconCat" + entry.IconKey) as System.Windows.Media.Geometry);
        NavIcon.SetIndent(item, new Thickness(8, 0, 0, 0));
        AutomationProperties.SetItemType(item, "category");
        return item;
    }

    private void UpdateCategoryCounts(bool force = false)
    {
        var counts = new Dictionary<string, int>();
        foreach (var row in Rows) if (!row.IsTorrent) { var key = CategoryOf(row); counts[key] = counts.GetValueOrDefault(key) + 1; }
        bool changed = force;
        foreach (var key in _catItems.Keys) { int n = counts.GetValueOrDefault(key); if (_catCounts.GetValueOrDefault(key, -1) != n) { _catCounts[key] = n; changed = true; } }
        if (!changed) return;
        foreach (var (key, item) in _catItems)
        {
            int n = _catCounts.GetValueOrDefault(key);
            if (item.Content is DockPanel p && p.Children.OfType<TextBlock>().FirstOrDefault(t => t.Tag as string == "count") is { } c) c.Text = n.ToString(System.Globalization.CultureInfo.CurrentCulture);
            AutomationProperties.SetName(item, Loc.T("nav.a11y.category_item", CategoryTree.NameOf(key), n));
        }
        UpdateAllAutomation();
    }

    private void UpdateAllAutomation()
    {
        int total = Rows.Count(r => !r.IsTorrent);
        AutomationProperties.SetName(AllItem, Loc.T("nav.a11y.all_item", Loc.T("main.text.all_downloads"), total, Loc.T(AllItem.IsExpanded ? "nav.state.expanded" : "nav.state.collapsed")));
    }

    /// <summary>Left and Right collapse and expand the All downloads entry (swapped in right-to-left); Left on a category goes up to All downloads. Up, Down and Enter are the list's own.</summary>
    private void Navigation_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || Keyboard.Modifiers != ModifierKeys.None) return;
        bool rtl = Navigation.FlowDirection == FlowDirection.RightToLeft;
        var key = e.Key;
        if (key is Key.Left or Key.Right) { bool expandKey = (key == Key.Right) != rtl; HandleTreeKey(expandKey, e); }
        else if (key is Key.Enter or Key.Space && Keyboard.FocusedElement is ListBoxItem focused && !ReferenceEquals(Navigation.SelectedItem, focused)) { Navigation.SelectedItem = focused; e.Handled = true; }
    }
    private void HandleTreeKey(bool expandKey, KeyEventArgs e)
    {
        var current = Keyboard.FocusedElement as ListBoxItem ?? Navigation.SelectedItem as ListBoxItem;
        if (ReferenceEquals(current, AllItem))
        {
            if (expandKey && !AllItem.IsExpanded) { AllItem.IsExpanded = true; e.Handled = true; }
            else if (expandKey && AllItem.IsExpanded && Navigation.Items.Count > 1 && Navigation.Items[1] is ListBoxItem first && first.Tag is string t && t.StartsWith(CategoryTree.FilterPrefix, StringComparison.Ordinal)) { Navigation.SelectedItem = first; first.Focus(); e.Handled = true; }
            else if (!expandKey && AllItem.IsExpanded) { AllItem.IsExpanded = false; e.Handled = true; }
        }
        else if (current?.Tag is string tag && tag.StartsWith(CategoryTree.FilterPrefix, StringComparison.Ordinal) && !expandKey) { Navigation.SelectedItem = AllItem; AllItem.Focus(); e.Handled = true; }
    }
}
