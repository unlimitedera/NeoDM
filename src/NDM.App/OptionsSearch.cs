// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>One thing the Options search can jump to: the words it is found by, the control to show and the page it is on.</summary>
public sealed record SearchEntry(TabItem Tab, FrameworkElement Target, string Text, int Weight)
{
    public string Where => TabName(Tab);
    public string Display => Text.Length > 90 ? Text[..87] + "…" : Text;
    public static string TabName(TabItem tab) => OptionsSearch.Clean(tab.Header as string ?? "");
}

/// <summary>2.0.0, Options search: the index is read from the real controls of the open window (labels, check boxes, group titles, buttons, help lines, input names, page names) every time the person types,
/// so a setting that is added to a page is found without anyone keeping a list, and the words follow the language that is on.</summary>
public static class OptionsSearch
{
    public const int Max = 10;
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>Text without access keys (_ and &amp;), without a trailing ellipsis, trimmed and on one line.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Replace("__", "\u0001").Replace("_", "").Replace("\u0001", "_").Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.TrimEnd('…', '.').Trim();
    }

    /// <summary>Reads the index. <paramref name="skip"/> lets a test leave one control out to prove that the coverage check notices.</summary>
    public static List<SearchEntry> Build(TabControl tabs, IEnumerable<(FrameworkElement Target, string Text)>? extra = null, Func<FrameworkElement, bool>? skip = null)
    {
        var list = new List<SearchEntry>();
        foreach (var tab in tabs.Items.OfType<TabItem>())
        {
            var name = Clean(tab.Header as string);
            if (name.Length > 0) list.Add(new SearchEntry(tab, tab, name, 4));
            if (tab.Content is DependencyObject content) Walk(content, tab, list, skip);
        }
        foreach (var (target, text) in extra ?? Array.Empty<(FrameworkElement, string)>())
            if (FindTab(target) is { } tab && Clean(text) is { Length: > 0 } t) list.Add(new SearchEntry(tab, target, t, 2));
        // one entry per control and text
        return list.GroupBy(e => (e.Target, e.Text)).Select(g => g.First()).ToList();
    }

    private static TabItem? FindTab(DependencyObject d)
    {
        for (DependencyObject? p = d; p is not null; p = LogicalTreeHelper.GetParent(p) ?? (p is FrameworkElement f ? f.Parent : null)) if (p is TabItem t) return t;
        return null;
    }

    private static void Walk(DependencyObject node, TabItem tab, List<SearchEntry> list, Func<FrameworkElement, bool>? skip)
    {
        if (node is FrameworkElement fe)
        {
            if (fe.Visibility != Visibility.Visible) return;      // not on the page right now (for example a note that only shows in some cases)
            if (skip?.Invoke(fe) == true) return;
            Add(fe, tab, list);
        }
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Walk(child, tab, list, skip);
    }

    private static void Add(FrameworkElement fe, TabItem tab, List<SearchEntry> list)
    {
        void Put(string? text, int weight) { var t = Clean(text); if (t.Length > 0) list.Add(new SearchEntry(tab, fe, t, weight)); }
        switch (fe)
        {
            case TextBlock tb:
                var text = tb.Inlines.Count > 0 && string.IsNullOrEmpty(tb.Text) ? string.Concat(tb.Inlines.SelectMany(Runs)) : tb.Text;
                Put(string.IsNullOrWhiteSpace(text) ? string.Concat(tb.Inlines.SelectMany(Runs)) : text, tb.FontSize >= 15 && tb.FontWeight >= FontWeights.SemiBold ? 3 : 1);
                break;
            case GroupBox g: Put(g.Header as string, 3); break;
            case Label l: Put(l.Content as string, 2); break;
            case ContentControl c when fe is CheckBox or RadioButton or Button: Put(c.Content as string, 2); break;
        }
        if (fe.ToolTip is string tip) Put(tip, 1);
        if (fe is TextBox or ComboBox or PasswordBox or ListBox or ItemsControl or CheckBox or RadioButton or Slider)
            Put(System.Windows.Automation.AutomationProperties.GetName(fe), 2);
    }
    private static IEnumerable<string> Runs(Inline inline) => inline switch
    {
        Run r => new[] { r.Text },
        Span s => s.Inlines.SelectMany(Runs),
        _ => Array.Empty<string>()
    };

    public static bool Matches(string text, string[] terms)
    {
        foreach (var term in terms) if (Compare.IndexOf(text, term, Loose) < 0) return false;
        return true;
    }

    /// <summary>Entries whose words contain every typed word (any order, any case, accents and width ignored), best first: titles and labels before help lines, then the page order.</summary>
    public static List<SearchEntry> Find(IReadOnlyList<SearchEntry> index, string? query, int max = Max)
    {
        var terms = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Clean).Where(t => t.Length > 0).ToArray();
        if (terms.Length == 0) return new();
        return index.Select((e, i) => (e, i)).Where(x => Matches(x.e.Text, terms))
            .OrderByDescending(x => x.e.Weight * 10 + (Compare.IsPrefix(x.e.Text, terms[0], Loose) ? 5 : 0) + (terms.Length == 1 && Compare.Compare(x.e.Text, terms[0], Loose) == 0 ? 20 : 0)).ThenBy(x => x.i)
            .Select(x => x.e).Take(max).ToList();
    }

    /// <summary>Settings whose label is not found when its own words are typed (empty = nothing is missed). <paramref name="labels"/> are the words to look for.</summary>
    public static List<string> Missing(IReadOnlyList<SearchEntry> index, IEnumerable<string> labels) =>
        labels.Select(Clean).Where(l => l.Length > 0).Distinct().Where(l => !Find(index, l, 500).Any(e => Compare.Compare(e.Text, l, Loose) == 0)).ToList();

    // ---------- showing the result ----------
    private static SearchGlow? _glow;
    /// <summary>Opens the page, scrolls the control into view and outlines it for a few seconds.</summary>
    public static void Show(TabControl tabs, SearchEntry entry, TimeSpan? lasts = null)
    {
        Clear();
        tabs.SelectedItem = entry.Tab; tabs.UpdateLayout();
        entry.Target.BringIntoView(); entry.Target.UpdateLayout();
        // put the setting near the top of the page so the outline is never cut off by the bottom edge
        for (DependencyObject? p = entry.Target; p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer sv && entry.Target != sv)
            {
                var y = entry.Target.TranslatePoint(new Point(0, 0), sv).Y;
                sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset + y - 48)); sv.UpdateLayout(); break;
            }
        var layer = AdornerLayer.GetAdornerLayer(entry.Target);
        if (layer is null) return;
        _glow = new SearchGlow(entry.Target, layer, lasts ?? TimeSpan.FromSeconds(4));
    }
    public static void Clear() { _glow?.Remove(); _glow = null; }
    public static SearchGlow? Current => _glow;
}

/// <summary>The outline drawn over the control that was found (an accent frame with a light fill); it goes away by itself.</summary>
public sealed class SearchGlow : Adorner
{
    private readonly AdornerLayer _layer; private readonly DispatcherTimer _timer;
    public FrameworkElement Element { get; }
    public SearchGlow(FrameworkElement element, AdornerLayer layer, TimeSpan lasts) : base(element)
    {
        Element = element; _layer = layer; IsHitTestVisible = false; layer.Add(this);
        _timer = new DispatcherTimer { Interval = lasts }; _timer.Tick += (_, _) => Remove(); _timer.Start();
    }
    public void Remove() { _timer.Stop(); try { _layer.Remove(this); } catch (Exception) { } }
    protected override void OnRender(DrawingContext dc)
    {
        var accent = (Brush?)Element.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;
        var fill = accent.CloneCurrentValue(); fill.Opacity = 0.14; fill.Freeze();
        var rect = new Rect(-4, -3, Element.ActualWidth + 8, Element.ActualHeight + 6);
        dc.DrawRoundedRectangle(fill, new Pen(accent, 2), rect, 6, 6);
    }
}
