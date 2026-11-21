// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;

/// <summary>2.0.0 stage S4b-3: the search box in Options.</summary>
internal static partial class V2000Tests
{
    /// <summary>Texts of the Options window that are not on the page when it opens (they show in some cases only), or that are not a setting's own words.</summary>
    private static readonly HashSet<string> NotOnPageAtOpen = new()
    {
        "common.a11y.options",                                    // the window title
        "options.language.ai_note", "options.language.ai_link",   // shown only for languages that were machine translated
        "options.text.animate_bars_system",                       // shown only when Windows turns animations off
        "options.btn.forget_skipped_version",                     // shown only after a version was skipped
        "common.btn.cancel", "stream.btn.copy", "tool.installing", // shown only while a tool installs or after a message
        "options.tool.install", "options.tool.remove",            // hidden when the video tool is part of the installation
        "options.search.label", "options.search.hint",            // the search box itself
        "common.btn.cancel_2", "options.btn.save"                 // the buttons under the pages, not settings
    };

    private static IEnumerable<string> XamlKeys() =>
        Regex.Matches(File.ReadAllText(Path.Combine(FindRepo(), "src", "NDM.App", "SettingsWindow.xaml")), @"\{loc:T ([\w.]+)\}").Select(m => m.Groups[1].Value).Distinct();

    private static void SearchCoverage(string scratch)
    {
        var total = 0;
        foreach (var lang in Langs)
        {
            Loc.SetLanguage(lang);
            var w = OpenOptions(scratch, "*", 800);
            try
            {
                var index = w.BuildSearchIndex();
                var keys = XamlKeys().Where(k => !NotOnPageAtOpen.Contains(k)).ToList(); var labels = keys.Select(k => Loc.T(k)).ToList();
                var missing = OptionsSearch.Missing(index, labels).Select(t => keys[labels.FindIndex(x => OptionsSearch.Clean(x) == t)]).ToList();
                Check(missing.Count == 0, $"[{lang}] every label on every page is found by its own words ({labels.Count} texts, {index.Count} index entries)" + (missing.Count > 0 ? ": missing " + string.Join(" | ", missing.Take(8)) : ""));
                total += labels.Count;
                foreach (var tab in ((TabControl)w.FindName("OptionsTabs")).Items.OfType<TabItem>())
                {
                    var found = OptionsSearch.Find(index, OptionsSearch.Clean((string)tab.Header));
                    Check(found.Any(e => e.Target == tab), $"[{lang}] the page name '{OptionsSearch.Clean((string)tab.Header)}' finds its page");
                }
            }
            finally { w.Close(); }
        }
        Loc.SetLanguage("en");
        Console.WriteLine("  texts checked in all languages: " + total);
    }

    private static void SearchControls(string scratch)
    {
        var w = OpenOptions(scratch, "*", 800);
        try
        {
            var tabs = (TabControl)w.FindName("OptionsTabs"); var index = w.BuildSearchIndex();
            var inputs = new List<FrameworkElement>();
            foreach (var tab in tabs.Items.OfType<TabItem>())
            {
                tabs.SelectedItem = tab; Pump(60);
                foreach (var d in Descendants((DependencyObject)tab.Content).OfType<FrameworkElement>())
                    if ((d is TextBox or ComboBox or CheckBox or RadioButton or PasswordBox) && IsOnPage(d, tab) && d is not TextBox { IsReadOnly: true }) inputs.Add(d);
            }
            Check(inputs.Count > 60, "the Options pages hold many inputs: " + inputs.Count);
            var unnamed = new List<string>();
            foreach (var input in inputs)
            {
                var label = index.Where(e => e.Target == input).Select(e => e.Text).FirstOrDefault();
                label ??= index.Where(e => e.Target is Label l && ReferenceEquals(LabelTarget(l), input)).Select(e => e.Text).FirstOrDefault();
                if (label is null || !OptionsSearch.Find(index, label).Any()) unnamed.Add(input.Name.Length > 0 ? input.Name : input.GetType().Name);
            }
            Check(unnamed.Count == 0, "every input has words to be found by" + (unnamed.Count > 0 ? ": " + string.Join(", ", unnamed) : ""));

            var without = OptionsSearch.Build(tabs, null, fe => fe.Name == "ShowServerColumnBox");
            Check(OptionsSearch.Missing(without, new[] { Loc.T("options.chk.show_server_column") }).Count == 1, "a setting that is left out of the index is reported as missing (the check can fail)");
            var segLabel = Loc.T("options.label.maximum_segments_per_file_1_to");
            var withoutLabel = OptionsSearch.Build(tabs, null, fe => fe is Label { Content: string s } && s == segLabel);
            Check(OptionsSearch.Missing(withoutLabel, new[] { segLabel }).Count == 1, "same for a label");
        }
        finally { w.Close(); }
    }

    private static object? LabelTarget(Label l)
    {
        var b = System.Windows.Data.BindingOperations.GetBinding(l, Label.TargetProperty);
        return b?.ElementName is { } n ? (l.FindName(n) ?? (Window.GetWindow(l)?.FindName(n))) : l.Target;
    }
    private static bool IsOnPage(FrameworkElement d, TabItem tab)
    {
        for (DependencyObject? p = d; p is not null && p != tab; p = VisualTreeHelper.GetParent(p)) if (p is FrameworkElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static void SearchBehaviour(string scratch)
    {
        foreach (var lang in Langs)
        {
            Loc.SetLanguage(lang);
            var w = OpenOptions(scratch, "*", 700);
            try
            {
                var tabs = (TabControl)w.FindName("OptionsTabs"); var box = (TextBox)w.FindName("SearchBox"); var captcha = (CheckBox)w.FindName("CaptchaAuto");
                var label = OptionsSearch.Clean((string)captcha.Content);
                var results = w.SearchFor(label);
                Check(results.Count > 0 && results.Take(3).Any(e => e.Target == captcha), $"[{lang}] typing a setting's words finds it: '{(label.Length > 30 ? label[..30] : label)}'");
                Check(((ListBox)w.FindName("SearchResults")).Visibility == Visibility.Visible, $"[{lang}] the results show under the box");
                w.Jump(results.First(e => e.Target == captcha)); Pump(250);
                var downloads = tabs.Items.OfType<TabItem>().First(t => t.Content is DependencyObject c && Descendants(c).Contains(captcha));
                Check(tabs.SelectedItem == downloads && OptionsSearch.Current?.Element == captcha, $"[{lang}] the click opens the Downloads page and outlines the setting");
                var page = (ScrollViewer)downloads.Content; var top = captcha.TranslatePoint(new Point(0, 0), page).Y;
                Check(top >= -1 && top + captcha.ActualHeight <= page.ViewportHeight + 1, $"[{lang}] the setting is scrolled into view (top {top:0}, page {page.ViewportHeight:0})");
                var spot = captcha.TranslatePoint(new Point(0, 0), w); var rtl = w.FlowDirection == FlowDirection.RightToLeft;
                Check(spot.X > -1 && spot.X <= w.ActualWidth + 1 && rtl == (lang == "ar"), $"[{lang}] reading direction {(rtl ? "right to left" : "left to right")} and the setting is inside the window");
                box.Clear(); Pump(60);
                Check(OptionsSearch.Current is null && ((ListBox)w.FindName("SearchResults")).Visibility == Visibility.Collapsed, $"[{lang}] clearing the box removes the outline and the list");
            }
            finally { OptionsSearch.Clear(); w.Close(); }
        }
        Loc.SetLanguage("en");
        var e1 = OpenOptions(scratch, "*", 700);
        try
        {
            var tabs = (TabControl)e1.FindName("OptionsTabs");
            Check(e1.SearchFor("PORT proxy").Count > 0, "words in any order and any case");
            Check(e1.SearchFor("backup").Any(x => x.Text == "Backup and restore" && x.Weight == 3), "Backup and restore is found by 'backup'");
            var navy = e1.SearchFor("navy");
            Check(navy.Any(x => x.Target.Name == "ThemeGallery"), "a theme is found by its name");
            e1.Jump(navy.First(x => x.Target.Name == "ThemeGallery")); Pump(200);
            Check(((TabItem)tabs.SelectedItem).Name == "AppearanceTab", "and its page opens");
            var none = e1.SearchFor("zzzqqq");
            Check(none.Count == 1 && none[0].Weight == 0 && none[0].Text == Loc.T("options.search.none"), "nothing found says so");
            OptionsSearch.Clear(); e1.Jump(none[0]);
            Check(OptionsSearch.Current is null, "and does not jump");
            Loc.SetLanguage("de"); var de = e1.BuildSearchIndex();
            Check(OptionsSearch.Find(de, "übersicht").Count == OptionsSearch.Find(de, "UBERSICHT").Count, "umlauts and capitals do not matter");
            Loc.SetLanguage("ja"); var ja = e1.BuildSearchIndex();
            Check(OptionsSearch.Find(ja, "ﾌﾟﾛｷｼ").Count == OptionsSearch.Find(ja, "プロキシ").Count && OptionsSearch.Find(ja, "プロキシ").Count > 0, "half-width and full-width Japanese match the same");
        }
        finally { OptionsSearch.Clear(); Loc.SetLanguage("en"); e1.Close(); }
        Check(Langs.All(l => { Loc.SetLanguage(l); return new[] { "options.search.label", "options.search.hint", "options.search.none" }.All(k => Loc.T(k) != k); }), "the search texts exist in ten languages");
        Loc.SetLanguage("en");
    }

    private static void ShotS4b(Window w, string name)
    {
        var dir = Path.Combine(FindRepo(), "artifacts", "s4b-shots"); Directory.CreateDirectory(dir);
        w.UpdateLayout(); Pump(200);
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32); rtb.Render(w);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(dir, name + ".png")); enc.Save(fs);
    }

    private static void SearchShots(string scratch)
    {
        foreach (var (theme, lang, width, tag) in new[] { ("Light", "en", 720.0, "light"), ("DeepNavy", "en", 720.0, "navy"), ("Light", "ar", 720.0, "rtl"), ("Light", "en", 620.0, "narrow") })
        {
            Loc.SetLanguage(lang); ThemeManager.Apply(Application.Current, theme, null);
            var w = OpenOptions(scratch, "*", 760); w.Width = width; Pump(150);
            try
            {
                var target = (CheckBox)w.FindName("CaptchaAuto"); var results = w.SearchFor(OptionsSearch.Clean((string)target.Content).Split(' ')[0]); Pump(100);
                w.Jump(results.First(e => e.Target == target)); Pump(300);
                ShotS4b(w, "options-search-" + tag);
            }
            finally { OptionsSearch.Clear(); w.Close(); }
        }
        ThemeManager.Apply(Application.Current, "Light", null); Loc.SetLanguage("en");
        Check(true, "screenshots written to artifacts/s4b-shots");
    }
}
