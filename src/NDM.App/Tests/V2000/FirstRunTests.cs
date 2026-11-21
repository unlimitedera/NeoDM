// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>2.0.0 stage S4b-4: the first-run setup.</summary>
internal static partial class V2000Tests
{
    private static void FirstRun(string scratch)
    {
        var root = Path.Combine(scratch, "fr-" + Guid.NewGuid().ToString("N")); var state = Path.Combine(root, "NDM"); Directory.CreateDirectory(state);
        // a new install: no settings file, so the setup is due; it is marked at once, saved, and never asked for again
        var fresh = AppSettings.Load(state);
        Check(!fresh.SetupDone, "a new install has not seen the setup");
        int before = FirstRunWindow.Requested;
        FirstRunWindow.RunAtStart(null, fresh, state);
        Check(FirstRunWindow.Requested == before + 1 && fresh.SetupDone && AppSettings.Load(state).SetupDone, "the first start asks for the window once and marks it as seen on disk before it is shown");
        FirstRunWindow.RunAtStart(null, AppSettings.Load(state), state);
        Check(FirstRunWindow.Requested == before + 1, "the next start does not show it again");
        var upgraded = Path.Combine(root, "old"); Directory.CreateDirectory(upgraded); File.WriteAllText(Path.Combine(upgraded, "settings.json"), "{\"MaxSegments\":8}");
        FirstRunWindow.RunAtStart(null, AppSettings.Load(upgraded), upgraded);
        Check(FirstRunWindow.Requested == before + 1, "someone who already used NeoDM is not welcomed again");

        // choices
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "A"), Theme = "Light" };
        var chosen = Path.Combine(root, "Chosen", "Files"); var guide = 0; var oldGuide = FirstRunWindow.OpenGuide; FirstRunWindow.OpenGuide = () => guide++;
        FirstRunWindow.FolderPicker = () => chosen;
        try
        {
            var w = new FirstRunWindow(settings, state) { ShowInTaskbar = false, Left = 40, Top = 40 }; w.Show(); Pump(250);
            var status = (TextBlock)w.FindName("ExtensionState");
            Check(status.Text == Loc.T("firstrun.ext.waiting"), "the extension line says it is not connected yet");
            settings.BrowserExtensions.Add(new BrowserExtensionRecord { Browser = "Chrome", Version = "2.0.0" }); Pump(2400);
            Check(status.Text == "Connected: Chrome 2.0.0", "the line turns to 'Connected' by itself when the extension reports in: " + status.Text);
            ((Button)w.FindName("GuideButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(guide == 1, "the install guide button opens the guide");
            ((Button)w.FindName("BrowseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var box = (TextBox)w.FindName("FolderBox"); Check(box.Text == chosen, "Browse puts the chosen folder in the box");
            var themes = (ComboBox)w.FindName("ThemeBox"); themes.SelectedIndex = themes.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == "DeepNavy"); Pump(150);
            Check(ThemeManager.CurrentId == "DeepNavy", "picking a theme shows it at once");
            Shot2(w, "firstrun-navy");
            // a folder that cannot be used stays on the page
            box.Text = "relative\\path"; ((Button)w.FindName("FinishButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100);
            Check(!w.Finished && w.IsVisible && ((TextBlock)w.FindName("ErrorText")).Text == Loc.T("firstrun.folder.error") && settings.DownloadDirectory.EndsWith("A"), "a folder that is not a full path is refused with a message and nothing is saved");
            box.Text = chosen; ((Button)w.FindName("FinishButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(150);
            var onDisk = AppSettings.Load(state);
            Check(w.Finished && Directory.Exists(chosen) && settings.DownloadDirectory == chosen && settings.Theme == "DeepNavy" && onDisk.DownloadDirectory == chosen && onDisk.Theme == "DeepNavy" && onDisk.SetupDone, "Finish creates the folder and saves folder and theme");

            // skip: nothing is kept and the preview goes back
            ThemeManager.Apply(Application.Current, "Light", null);
            var s2 = new AppSettings { DownloadDirectory = Path.Combine(root, "B"), Theme = "Light" };
            var w2 = new FirstRunWindow(s2, state) { ShowInTaskbar = false }; w2.Show(); Pump(150);
            var t2 = (ComboBox)w2.FindName("ThemeBox"); t2.SelectedIndex = t2.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == "DeepNavy"); Pump(100);
            ((TextBox)w2.FindName("FolderBox")).Text = Path.Combine(root, "Never");
            ((Button)w2.FindName("SkipButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(150);
            Check(!w2.Finished && !w2.IsVisible && ThemeManager.CurrentId == "Light" && s2.DownloadDirectory.EndsWith("B") && !Directory.Exists(Path.Combine(root, "Never")), "Skip keeps nothing and puts the theme back");
            // closing the window by its X is the same as skip
            var w3 = new FirstRunWindow(new AppSettings { Theme = "Light" }, state) { ShowInTaskbar = false }; w3.Show(); Pump(100);
            var t3 = (ComboBox)w3.FindName("ThemeBox"); t3.SelectedIndex = t3.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == "DeepNavy"); Pump(100); w3.Close(); Pump(100);
            Check(ThemeManager.CurrentId == "Light", "closing the window puts the theme back");
        }
        finally { FirstRunWindow.OpenGuide = oldGuide; FirstRunWindow.FolderPicker = null; ThemeManager.Apply(Application.Current, "Light", null); }

        // reachable from Help
        using var manager = new DownloadManager(Path.Combine(root, "f"), Path.Combine(root, "m"), new DownloadOptions());
        var main = new MainWindow(manager, new AppSettings { SetupDone = true }, () => { }, enableTray: false) { ShowInTaskbar = false }; main.Show(); Pump(200);
        int n = FirstRunWindow.Requested;
        ((MenuItem)main.FindName("SetupMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(100);
        Check(FirstRunWindow.Requested == n + 1, "Help > Welcome and setup opens it again"); main.Close();
        Check(Langs.All(l => { Loc.SetLanguage(l); return new[] { "firstrun.title", "firstrun.intro", "firstrun.folder", "firstrun.folder.a11y", "firstrun.folder.error", "firstrun.ext.title", "firstrun.ext.connected", "firstrun.ext.waiting", "firstrun.ext.guide", "firstrun.theme", "firstrun.btn.finish", "firstrun.btn.skip", "main.menu.setup" }.All(k => Loc.T(k) != k); }), "the setup texts exist in ten languages");
        Loc.SetLanguage("en");
    }

    private static void Shot2(Window w, string name) => ShotS4b(w, name);

    private static void FirstRunShots(string scratch)
    {
        var state = Path.Combine(scratch, "frs-" + Guid.NewGuid().ToString("N"), "NDM"); Directory.CreateDirectory(state);
        foreach (var (theme, lang, width, tag) in new[] { ("Light", "en", 560.0, "light"), ("DeepNavy", "en", 560.0, "navy"), ("Light", "ar", 560.0, "rtl"), ("Light", "en", 340.0, "narrow") })
        {
            Loc.SetLanguage(lang); ThemeManager.Apply(Application.Current, theme, null);
            var w = new FirstRunWindow(new AppSettings { Theme = theme, DownloadDirectory = @"C:\Users\Someone\Downloads" }, state) { ShowInTaskbar = false, Left = 40, Top = 40, Width = width }; w.Show(); Pump(300);
            try { ShotS4b(w, "firstrun-" + tag); } finally { w.Close(); }
        }
        ThemeManager.Apply(Application.Current, "Light", null); Loc.SetLanguage("en");
        Check(true, "first-run screenshots written");
    }
}

internal static partial class V2000Tests
{
    private static void ReportShots(string scratch)
    {
        var state = Path.Combine(scratch, "rs-" + Guid.NewGuid().ToString("N"), "NDM"); Directory.CreateDirectory(state);
        foreach (var (theme, lang, width, tag) in new[] { ("Light", "en", 560.0, "light"), ("DeepNavy", "en", 560.0, "navy"), ("Light", "ar", 560.0, "rtl"), ("Light", "en", 330.0, "narrow") })
        {
            Loc.SetLanguage(lang); ThemeManager.Apply(Application.Current, theme, null);
            var w = new ReportWindow(new AppSettings(), state, tag == "navy") { ShowInTaskbar = false, Left = 40, Top = 40, Width = width }; w.Show(); Pump(300);
            try { if (tag == "light") { w.Desktop = Path.Combine(scratch, "desk-" + Guid.NewGuid().ToString("N")); var old = UrlOpener.Open; UrlOpener.Open = _ => { }; try { w.Create(); } finally { UrlOpener.Open = old; } Pump(200); } ShotS4b(w, "report-" + tag); } finally { w.Close(); }
        }
        // the backup group on the General page
        ThemeManager.Apply(Application.Current, "DeepNavy", null); Loc.SetLanguage("en");
        var o = OpenOptions(scratch, "*", 760);
        try { var r = o.SearchFor("backup"); o.Jump(r.First(x => x.Weight == 3)); Pump(300); ShotS4b(o, "options-backup-navy"); } finally { OptionsSearch.Clear(); o.Close(); }
        ThemeManager.Apply(Application.Current, "Light", null);
        Check(true, "report and backup screenshots written");
    }
}
