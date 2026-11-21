// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NDM.App.Localization;

namespace NDM.App;

public partial class SettingsWindow
{
    private List<SearchEntry> _searchIndex = new();
    private bool _searchBusy;

    /// <summary>The search index read from the controls as they are now (tests read it too).</summary>
    public List<SearchEntry> BuildSearchIndex()
    {
        var extra = new List<(FrameworkElement, string)>();
        foreach (var card in ThemeGallery.Items.OfType<ThemeCardModel>()) { extra.Add((ThemeGallery, card.Name)); }
        if (LanguageBox.ItemsSource is System.Collections.IEnumerable langs)
            foreach (var l in langs.OfType<LanguageChoice>()) extra.Add((LanguageBox, l.Primary + " " + l.Secondary));
        return OptionsSearch.Build(OptionsTabs, extra);
    }

    /// <summary>Types into the box and shows the results (tests call this instead of sending keys).</summary>
    public IReadOnlyList<SearchEntry> SearchFor(string text) { SearchBox.Text = text; return (IReadOnlyList<SearchEntry>)SearchResults.ItemsSource ?? Array.Empty<SearchEntry>(); }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (SearchBox.Text.Trim().Length == 0) { SearchResults.ItemsSource = null; SearchResults.Visibility = Visibility.Collapsed; OptionsSearch.Clear(); return; }
        _searchIndex = BuildSearchIndex();
        var found = OptionsSearch.Find(_searchIndex, SearchBox.Text);
        _searchBusy = true;
        try
        {
            if (found.Count == 0) found = new() { new SearchEntry(OptionsTabs.Items.OfType<TabItem>().First(), SearchResults, Loc.T("options.search.none"), 0) };
            SearchResults.ItemsSource = found; SearchResults.SelectedIndex = -1; SearchResults.Visibility = Visibility.Visible;
        }
        finally { _searchBusy = false; }
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchResults.ItemsSource is List<SearchEntry> { Count: > 0 } list) { e.Handled = true; Jump(list[0]); }
        else if (e.Key == Key.Down && SearchResults.Items.Count > 0) { e.Handled = true; SearchResults.SelectedIndex = 0; (SearchResults.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus(); }
        else if (e.Key == Key.Escape && SearchBox.Text.Length > 0) { e.Handled = true; SearchBox.Clear(); }
    }

    private void SearchResults_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_searchBusy && SearchResults.SelectedItem is SearchEntry entry) Jump(entry);
    }

    /// <summary>Opens the page of the entry and outlines the setting.</summary>
    public void Jump(SearchEntry entry)
    {
        if (entry.Weight == 0) return; // the 'nothing found' line
        OptionsSearch.Show(OptionsTabs, entry);
    }
}
