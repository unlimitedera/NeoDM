using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NDM.App;
using NDM.Core;

// App's constructor queues OnStartup on the dispatcher; the real one takes the machine-wide singleton mutex and either
// shuts down (a live NeoDM owns it) or starts the production manager/pipe against the user's real state. Tests must do neither.
internal sealed class TestApp : NDM.App.App
{
    public TestApp()
    {
        // App.InitializeComponent only loads for the exact generated type, so mirror App.xaml's dictionaries explicitly.
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
    protected override void OnStartup(StartupEventArgs e) { }
}

internal static class UiTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var item in Children(child)) yield return item;
        }
    }
    [STAThread]
    public static int Main()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-UiTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack; var app = new TestApp();
        using var manager = new DownloadManager(Path.Combine(root, "files"), Path.Combine(root, "state"));
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "files") };
        var window = new MainWindow(manager, settings, () => { }, enableTray: false) { ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            Check(((SolidColorBrush)window.Background).Color == (Color)ColorConverter.ConvertFromString("#F3F4F6"), "Light window token applied to derived WPF window");
            Check(window.Rows.Count == 0 && ((Grid)window.FindName("EmptyState")).Visibility == Visibility.Visible, "Genuine empty queue shown");
            app.ApplyTheme("Dark"); Pump();
            Check(((SolidColorBrush)window.Background).Color == (Color)ColorConverter.ConvertFromString("#171B1E"), "Dark token updates live");
            Check(((SolidColorBrush)((TextBox)window.FindName("Search")).Foreground).Color == (Color)ColorConverter.ConvertFromString("#F3F5F6"), "Dark input foreground updates live");
            var dialog = new AddDownloadWindow("invalid") { Owner = window };
            dialog.Show(); Pump();
            Check(((SolidColorBrush)dialog.Background).Color == ((SolidColorBrush)window.Background).Color, "Modal inherits dark palette");
            var accept = Children(dialog).OfType<Button>().Single(button => Equals(button.Content, "Add download"));
            accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!string.IsNullOrWhiteSpace(((TextBlock)dialog.FindName("ValidationError")).Text), "Invalid add URL shows inline error");
            Check(dialog.Request is null, "Invalid add never reaches engine");
            dialog.Close();
            var job = manager.AddAsync(new DownloadRequest("http://127.0.0.1:1/ui-real-job.bin")).GetAwaiter().GetResult();
            manager.Pause(job.Id); Pump();
            Check(window.Rows.Count == 1 && window.Rows[0].Status == "Paused", "Timer polls real detached job snapshot");
            var list = (ListBox)window.FindName("DownloadList");
            list.SelectedIndex = 0; Pump();
            Check(Equals(((Button)window.FindName("PauseButton")).Content, "Resume") && ((Button)window.FindName("PauseButton")).IsEnabled, "Paused selection exposes Resume");
            Check(!((Button)window.FindName("OpenButton")).IsEnabled, "Incomplete download cannot open");
            ((TextBox)window.FindName("Search")).Text = "does-not-match"; Pump();
            Check(list.Items.Count == 0, "Search filters queue");
            ((TextBox)window.FindName("Search")).Clear();
            ((ListBox)window.FindName("Navigation")).SelectedItem = ((ListBox)window.FindName("Navigation")).Items.OfType<ListBoxItem>().First(i => (string)i.Tag == "Completed"); Pump();
            Check(list.Items.Count == 0, "Completed filter excludes paused job");
            ((ListBox)window.FindName("Navigation")).SelectedItem = ((ListBox)window.FindName("Navigation")).Items.OfType<ListBoxItem>().First(i => (string)i.Tag == "Paused"); Pump();
            Check(list.Items.Count == 1, "Paused filter shows paused job");
            var preferences = new SettingsWindow(settings) { Owner = window };
            preferences.Show(); Pump();
            Check(((SolidColorBrush)preferences.Background).Color == ((SolidColorBrush)window.Background).Color, "Settings inherits dark palette");
            Check(((TextBox)preferences.FindName("Destination")).Text == settings.DownloadDirectory, "Settings binds destination");
            Check(preferences.FindName("EnableCategoryFolders") != null, "Settings window has category folders checkbox");
            Check(preferences.FindName("CategoryGrid") != null, "Settings window has category grid");
            Check(preferences.FindName("ProgramsFolder") != null, "Settings window has Programs folder field");
            Check(preferences.FindName("CompressedFolder") != null, "Settings window has Compressed folder field");
            Check(preferences.FindName("MusicFolder") != null, "Settings window has Music folder field");
            Check(preferences.FindName("VideoFolder") != null, "Settings window has Video folder field");
            Check(preferences.FindName("DocumentsFolder") != null, "Settings window has Documents folder field");
            preferences.Close();
            app.ApplyTheme("Light"); Pump();
            Check(((SolidColorBrush)window.Background).Color == (Color)ColorConverter.ConvertFromString("#F3F4F6"), "Light theme can be restored");
            TorrentAddTests(Pump);
            SidebarTests();
            Console.WriteLine("ALL WPF UI TESTS PASSED");
            return 0;
        }
        finally { window.Close(); Console.WriteLine("Isolated UI test state: " + root); }
    }

    private static void Toggle(CheckBox box)
    {
        // Same code path as a real click: the automation toggle provider calls ToggleButton.OnToggle.
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(box);
        ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)!).Toggle();
    }
    private static CheckBox Box(Window w, string name) =>
        Children(w).OfType<CheckBox>().First(c => c.DataContext is TorrentFileNode n && n.Name == name);

    private static void TorrentAddTests(Action pump)
    {
        var meta = new NDM.Torrent.TorrentMetadata("id", "Pack", 600, new[]
        {
            new NDM.Torrent.TorrentMetadataFile("Pack/a/x.bin", 100), new NDM.Torrent.TorrentMetadataFile("Pack/a/y.bin", 200), new NDM.Torrent.TorrentMetadataFile("Pack/z.bin", 300),
        });
        var win = new TorrentAddWindow(new AppSettings { DownloadDirectory = Path.GetTempPath() }, "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
            null, (_, _, _) => Task.FromResult(meta)) { ShowInTaskbar = false };
        try
        {
            win.Show(); pump();
            var tree = (TreeView)win.FindName("FileTree"); var summary = (TextBlock)win.FindName("SelectionText"); var start = (Button)win.FindName("StartButton");
            var roots = (IReadOnlyList<TorrentFileNode>)tree.ItemsSource;
            var pack = roots[0]; var a = pack.Children.First(c => c.Name == "a"); var x = a.Children[0]; var y = a.Children[1]; var z = pack.Children.First(c => c.Name == "z.bin");
            Check(summary.Text.StartsWith("3 of 3 files") && start.IsEnabled, "Torrent add: all files start checked");
            Toggle(Box(win, "x.bin")); pump();
            Check(x.IsChecked == false && Box(win, "x.bin").IsChecked == false, "Torrent add: clicking a checked file unchecks it (single click)");
            Check(a.IsChecked == null && pack.IsChecked == null, "Torrent add: parent folders become mixed");
            Check(summary.Text.StartsWith("2 of 3 files") && summary.Text.Contains("500 B"), "Torrent add: summary updates after single toggle: " + summary.Text);
            Toggle(Box(win, "x.bin")); pump();
            Check(x.IsChecked == true && a.IsChecked == true && pack.IsChecked == true && summary.Text.StartsWith("3 of 3"), "Torrent add: clicking an unchecked file checks it again");
            Toggle(Box(win, "a")); pump();
            Check(a.IsChecked == false && x.IsChecked == false && y.IsChecked == false && z.IsChecked == true && pack.IsChecked == null && summary.Text.StartsWith("1 of 3"), "Torrent add: unchecking a folder unchecks its children");
            Toggle(Box(win, "a")); pump();
            Check(a.IsChecked == true && x.IsChecked == true && y.IsChecked == true && summary.Text.StartsWith("3 of 3"), "Torrent add: checking a folder checks its children");
            Toggle(Box(win, "x.bin")); Toggle(Box(win, "a")); pump();
            Check(a.IsChecked == true && x.IsChecked == true && y.IsChecked == true, "Torrent add: clicking a mixed folder selects everything in it");
            Toggle(Box(win, "Pack")); pump();
            Check(pack.IsChecked == false && TorrentFileTree.SelectedIndexes(roots).Count == 0 && summary.Text.StartsWith("0 of 3 files") && summary.Text.Contains("0 B"), "Torrent add: root folder unchecks everything: " + summary.Text);
            Toggle(Box(win, "z.bin")); pump();
            Check(TorrentFileTree.SelectedIndexes(roots).SequenceEqual(new[] { 2 }) && pack.IsChecked == null && summary.Text.StartsWith("1 of 3"), "Torrent add: single file can be ticked from none");
            var selectNone = Children(win).OfType<Button>().First(b => Equals(b.Content, "Select _none")); var selectAll = Children(win).OfType<Button>().First(b => Equals(b.Content, "Select _all"));
            selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump();
            Check(summary.Text.StartsWith("3 of 3") && Box(win, "x.bin").IsChecked == true, "Torrent add: Select all");
            selectNone.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump();
            Check(summary.Text.StartsWith("0 of 3") && Box(win, "z.bin").IsChecked == false, "Torrent add: Select none");
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump();
            Check(win.Selection is null && ((TextBlock)win.FindName("ErrorText")).Text.Contains("at least one"), "Torrent add: Start with nothing selected is refused");
        }
        finally { win.Close(); }
    }

    private static void SidebarTests()
    {
        DownloadRow R(string status, bool torrent) { var r = (DownloadRow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DownloadRow));
            typeof(DownloadRow).GetProperty("Status")!.SetValue(r, status); typeof(DownloadRow).GetProperty("IsTorrent")!.SetValue(r, torrent); return r; }
        Check(MainWindow.FilterMatches("All", R("Paused", false)) && !MainWindow.FilterMatches("All", R("Seeding", true)), "Sidebar: All downloads excludes torrents");
        Check(MainWindow.FilterMatches("Downloading", R("Connecting", false)) && !MainWindow.FilterMatches("Downloading", R("Downloading", true)), "Sidebar: Downloading excludes torrents");
        Check(MainWindow.FilterMatches("T:All", R("Paused", true)) && MainWindow.FilterMatches("T:All", R("Failed", true)) && !MainWindow.FilterMatches("T:All", R("Paused", false)), "Sidebar: All torrents shows only torrents of any status");
        Check(MainWindow.FilterMatches("T:Downloading", R("Downloading", true)) && MainWindow.FilterMatches("T:Seeding", R("Seeding", true)) && MainWindow.FilterMatches("T:Completed", R("Completed", true)) && !MainWindow.FilterMatches("T:Seeding", R("Downloading", true)), "Sidebar: torrent sub-filters");
        var nav = (ListBox?)null;
        var settings = new AppSettings();
        Check(settings.EnableCategoryFolders && settings.SettingsSchema == AppSettings.CurrentSchema, "Category folders default on for new settings");
        string dir = Path.Combine(Path.GetTempPath(), "ndm-schema-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"EnableCategoryFolders\": false}");
            var old = AppSettings.Load(dir);
            Check(old.EnableCategoryFolders && old.SettingsSchema == AppSettings.CurrentSchema, "Old settings.json with persisted false migrates to true once");
            old.EnableCategoryFolders = false; old.Save(dir);
            Check(!AppSettings.Load(dir).EnableCategoryFolders, "Explicit opt-out after migration is respected");
        }
        finally { Directory.Delete(dir, true); }
    }
}
