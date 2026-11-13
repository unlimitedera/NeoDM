using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;
using NDM.Tests;

internal static partial class V1150Tests
{
    private static Color BarColour(DownloadTabButton b) => ((SolidColorBrush)((Border)b.Template.FindName("PART_Bar", b)).Background).Color;

    // ------------------------------------------------------------------ themes, RTL, narrow
    private static void ThemesAndRtl(string root)
    {
        using var slow = new SlowServer(); using var fixture = new FixtureServer(); using var manager = NewManager(root, "th");
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "th", "files") };
        var done = Sync(manager.AddAsync(new DownloadRequest(fixture.BaseUrl + "/range", "Holiday-photos.zip")));
        var failed = Sync(manager.AddAsync(new DownloadRequest(fixture.BaseUrl + "/fail", "broken-installer.exe")));
        var paused = Sync(manager.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/Lecture-recording.mp4")));
        var running = Sync(manager.AddAsync(new DownloadRequest(slow.BaseUrl + "/Ubuntu-desktop.iso")));
        PumpUntil(() => Job(manager, done.Id).Status == "Completed" && Job(manager, failed.Id).Status == "Failed" && Job(manager, running.Id).Status == "Downloading", "four downloads in four states", 30000);
        try
        {
            foreach (var theme in new[] { "Light", "DeepNavy", "Dark" })
            {
                ThemeManager.Apply(Application.Current, theme, null); Pump(150);
                var tabs = NewTabs(manager, settings); tabs.Show();
                foreach (var id in new[] { done.Id, failed.Id, paused.Id, running.Id }) tabs.AddOrSelect(id);
                Pump(900); tabs.UpdateLayout();
                var buttons = tabs.TabButtons;
                var expect = new[] { "Completed", "Failed", "Paused", "Downloading" };
                for (int i = 0; i < 4; i++) Check(BarColour(buttons[i]) == ProgressStatus.ColourOf(tabs, expect[i]), $"{theme}: the bar of the {expect[i]} tab has the status colour");
                Check(buttons.Select(BarColour).Distinct().Count() == 4, $"{theme}: four states, four colours");
                Check(buttons[0].StateText.StartsWith("Completed") && buttons[0].StateText.EndsWith("100%"), $"{theme}: completed tab says '{buttons[0].StateText}'");
                Check(buttons[1].StateText.StartsWith("Failed"), $"{theme}: failed tab says '{buttons[1].StateText}'");
                Check(buttons[3].StateText.StartsWith("Downloading"), $"{theme}: running tab says '{buttons[3].StateText}'");
                var selectedBg = ((SolidColorBrush)((Border)buttons[3].Template.FindName("Chrome", buttons[3])).Background).Color;
                var plainBg = ((SolidColorBrush?)((Border)buttons[0].Template.FindName("Chrome", buttons[0])).Background)?.Color ?? Colors.Transparent;
                Check(selectedBg != plainBg, $"{theme}: the selected tab looks different from the others");
                Check(((Border)buttons[3].Template.FindName("SelectedLine", buttons[3])).Visibility == Visibility.Visible, $"{theme}: the selected tab has the accent line");
                SaveShot(tabs, $"tabs-{theme}.png");
                if (theme == "Light" || theme == "DeepNavy")
                {
                    tabs.Width = 560; tabs.Height = 640; Pump(300); tabs.UpdateLayout();
                    Check(tabs.TabButtons.All(b => b.ActualWidth > 40) && ((Button)tabs.FindName("AddButton")).ActualWidth > 20, $"{theme}: the narrowest window still shows tabs and the plus button");
                    var first = tabs.TabButtons[0]; var scroll = (ScrollViewer)tabs.FindName("TabScroll");
                    Check(first.TransformToAncestor(scroll).TransformBounds(new Rect(0, 0, first.ActualWidth, first.ActualHeight)).Width > 0, $"{theme}: narrow strip lays out");
                    SaveShot(tabs, $"tabs-{theme}-narrow.png");
                    tabs.Width = 760; Pump(150);
                    tabs.FlowDirection = FlowDirection.RightToLeft; Pump(400); tabs.UpdateLayout();
                    var add = (Button)tabs.FindName("AddButton");
                    double addX = add.TransformToAncestor(tabs).Transform(new Point(0, 0)).X;
                    double firstX = tabs.TabButtons[0].TransformToAncestor(tabs).Transform(new Point(0, 0)).X;
                    Check(addX < firstX, $"{theme} RTL: the plus button sits on the left, the tabs run from the right");
                    Check(tabs.TabButtons[0].TransformToAncestor(tabs).Transform(new Point(0, 0)).X > tabs.TabButtons[3].TransformToAncestor(tabs).Transform(new Point(0, 0)).X, $"{theme} RTL: tab order is mirrored");
                    SaveShot(tabs, $"tabs-{theme}-rtl.png");
                }
                tabs.ForceClose(); Pump(100);
            }
        }
        finally { ThemeManager.Apply(Application.Current, "Light", null); }
        // reduced motion: the tab strip has no animation of its own; the shared view obeys the system switch
        Check(!typeof(DownloadTabsWindow).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Any(f => f.FieldType.Name.Contains("Storyboard")), "the tab strip owns no animation");
    }

    // ------------------------------------------------------------------ the main window routes progress by the setting
    private static void MainWindowRouting(string root)
    {
        using var slow = new SlowServer(); using var manager = NewManager(root, "mw");
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "mw", "files"), ShowDownloadsAsTabs = false };
        var a = Sync(manager.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/a.bin"))); var b = Sync(manager.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/b.bin")));
        var c = Sync(manager.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/c.bin")));
        var main = new MainWindow(manager, settings, () => { }, enableTray: false) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
        main.Show(); Pump(200);
        try
        {
            Check(main.OpenProgress(a.Id, foreground: false) && main.HasProgressWindow(a.Id) && main.TabsWindow is null, "with the setting off a download opens its own window as before");
            settings.ShowDownloadsAsTabs = true;
            Check(main.OpenProgress(b.Id, foreground: false) && !main.HasProgressWindow(b.Id) && main.TabsWindow is not null && main.TabsWindow.HasTab(b.Id) && main.HasDownloadView(b.Id), "with the setting on a download opens as a tab of the shared window");
            var tabs = main.TabsWindow!;
            Check(main.OpenProgress(c.Id, foreground: false) && ReferenceEquals(main.TabsWindow, tabs) && tabs.TabCount == 2, "the next download joins the same window");
            Check(main.OpenProgress(a.Id, foreground: false) && main.TabsWindow!.TabCount == 2 && main.HasProgressWindow(a.Id), "a download that already has a window keeps using it after the switch");
            Check(!main.OpenProgress("missing", foreground: false), "a missing download reports failure");
            Check(Sync(tabs.RequestCloseTabAsync(c.Id)) && !main.HasDownloadView(c.Id), "closing a tab removes it from the main window's view of open downloads");
            Check(main.OpenProgress(c.Id, foreground: false) && tabs.HasTab(c.Id), "double-clicking the download again reopens it as a tab (reopen after hide)");
            settings.ShowDownloadsAsTabs = false;
            Check(main.OpenProgress(c.Id, foreground: false) && tabs.HasTab(c.Id), "switching the setting off does not move an open tab");
            Sync(tabs.RequestCloseTabAsync(c.Id)); Sync(tabs.RequestCloseTabAsync(b.Id)); Pump(200);
            Check(main.TabsWindow is null, "closing the last tab closes the tabbed window");
            Check(main.OpenProgress(c.Id, foreground: false) && main.HasProgressWindow(c.Id), "with the setting off again the download opens its own window");
            settings.ShowDownloadsAsTabs = true;
            // the completion state of a tab: Open folder after completion closes the tab unless keep-open is on
            using var fixture = new FixtureServer();
            var done = Sync(manager.AddAsync(new DownloadRequest(fixture.BaseUrl + "/range", "complete.bin")));
            PumpUntil(() => Job(manager, done.Id).Status == "Completed", "a download completes", 30000);
            Check(main.OpenProgress(done.Id, foreground: false, completion: true), "a finished download opens as a tab in its Complete state");
            var view = main.TabsWindow!.ViewOf(done.Id)!; Pump(300);
            Check(((TextBox)view.FindName("StatusText")).Text.Length > 0 && ((Button)view.FindName("CloseButton")).Visibility == Visibility.Visible && ((Button)view.FindName("OpenButton")).Visibility == Visibility.Visible, "the tab shows the complete state with Open, Open folder and Close");
            ((Button)view.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(200);
            Check(!main.HasDownloadView(done.Id), "the Close button of a completed tab closes the tab");
        }
        finally { main.TabsWindow?.ForceClose(); foreach (Window w in Application.Current.Windows.OfType<DownloadProgressWindow>().ToArray()) w.Close(); main.Close(); }
    }
}
