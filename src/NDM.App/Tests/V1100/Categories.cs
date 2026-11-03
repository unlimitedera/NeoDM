using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static partial class V1100Tests
{
    // ===== item 1: the category model =====
    private static void CategoryModel(string scratch)
    {
        Check(AppSettings.CurrentSchema == 10, "Settings schema is 10");
        var s = new AppSettings { DownloadDirectory = Path.Combine(scratch, "dl") };
        (string File, string Key)[] builtIns =
        {
            ("a.zip", "b:Compressed"), ("a.7z", "b:Compressed"), ("setup.exe", "b:Programs"), ("setup.msi", "b:Programs"), ("clip.mp4", "b:Video"), ("clip.mkv", "b:Video"),
            ("song.mp3", "b:Music"), ("song.flac", "b:Music"), ("photo.png", "b:Pictures"), ("photo.JPG", "b:Pictures"), ("doc.pdf", "b:Documents"), ("sheet.xlsx", "b:Documents"),
            ("data.xyz", CategoryTree.Other), ("noextension", CategoryTree.Other), ("archive.tar.gz", "b:Compressed"),
        };
        foreach (var (file, key) in builtIns) Check(CategoryTree.KeyOf(s, file, "http://x.invalid/" + file, null) == key, $"{file} belongs to {key}");
        Check(CategoryRules.BuiltInIds.Contains("Pictures") && CategoryRules.DefaultExtensions("Pictures").Contains("png") && s.PicturesFolder == "Pictures", "Pictures is a built-in category with default extensions and folder");
        // custom categories and address rules: the filter and the folder always agree
        s.CustomCategories.Add(new CustomCategory { Name = "Books", Folder = "Books", Extensions = new() { "epub", "pdf" } });
        s.CustomCategories.Add(new CustomCategory { Name = "From Media", Folder = "Media", UrlRules = new() { "media.invalid" } });
        s.CustomCategories.Add(new CustomCategory { Name = "Off", Folder = "Off", Extensions = new() { "txt" }, Enabled = false });
        s.BuiltInCategories["Music"] = new BuiltInCategorySetting { UrlRules = new() { "radio.invalid" } };
        (string File, string? Url, string Key)[] custom =
        {
            ("b.epub", "http://x.invalid/b.epub", "c:Books"), ("doc.pdf", "http://x.invalid/doc.pdf", "c:Books"),
            ("clip.mp4", "http://media.invalid/clip.mp4", "c:From Media"), ("a.zip", "http://cdn.media.invalid/a.zip", "c:From Media"),
            ("notes.txt", "http://x.invalid/notes.txt", "b:Documents"), ("show.bin", "http://radio.invalid/show.bin", "b:Music"), ("clip.mp4", "http://x.invalid/clip.mp4", "b:Video"),
        };
        foreach (var (file, url, key) in custom) Check(CategoryTree.KeyOf(s, file, url, null) == key, $"{file} from {url} belongs to {key}");
        // same answer as the save-folder logic
        string[] names = { "a.zip", "b.epub", "doc.pdf", "clip.mp4", "song.mp3", "photo.png", "show.bin", "data.xyz", "notes.txt", "setup.exe" };
        string?[] urls = { "http://x.invalid/f", "http://media.invalid/f", "http://radio.invalid/f", "http://sub.media.invalid/p/f", null };
        bool agree = true; int compared = 0;
        foreach (var name in names) foreach (var url in urls)
        {
            var folder = CategoryRules.Resolve(s, name, url, null); var cat = CategoryRules.Classify(s, name, url, null);
            agree &= folder.Category == cat.Category && folder.IsCustom == cat.IsCustom; compared++;
        }
        Check(agree && compared == 50, "Classify and Resolve pick the same category for 50 file and address combinations");
        s.EnableCategoryFolders = false;
        Check(CategoryRules.Resolve(s, "a.zip").Category is null && CategoryRules.Classify(s, "a.zip").Category == "Compressed", "With category folders off the list still sorts files into categories");
        s.EnableCategoryFolders = true;
        // entries
        var entries = CategoryTree.Entries(s);
        Check(string.Join(",", entries.Select(e => e.Key)) == "b:Compressed,b:Programs,b:Video,b:Music,b:Pictures,b:Documents,c:Books,c:From Media,other", "Entries: built-in categories, custom categories that are on, then Other");
        s.BuiltInCategories["Pictures"] = new BuiltInCategorySetting { Enabled = false };
        Check(!CategoryTree.Entries(s).Any(e => e.Key == "b:Pictures") && CategoryTree.KeyOf(s, "photo.png", null, null) == CategoryTree.Other, "A built-in category that is switched off is hidden and its files go to Other");
        // settings migration
        string dir = Path.Combine(scratch, "settings"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"SettingsSchema\":9,\"CustomCategories\":[{\"Name\":\"Pictures\",\"Folder\":\"Pics\",\"Extensions\":[\"raw\"]}],\"MusicFolder\":\"Tunes\"}");
        var old = AppSettings.Load(dir);
        Check(old.SettingsSchema == 10 && old.AnimateProgressBars && old.CategoryTreeExpanded && old.PicturesFolder == "Pictures" && old.MusicFolder == "Tunes", "Schema 9 settings load with the new options on and old folders kept");
        Check(old.CustomCategories.Count == 1 && old.CustomCategories[0].Name == "Pictures (custom)" && old.CustomCategories[0].Extensions.Contains("raw"), "A custom category named Pictures is kept under another name");
        old.AnimateProgressBars = false; old.CategoryTreeExpanded = false; old.Save(dir);
        var again = AppSettings.Load(dir);
        Check(!again.AnimateProgressBars && !again.CategoryTreeExpanded, "The animation switch and the list state are saved and restored");
        var copy = new AppSettings(); copy.CopyFrom(again);
        Check(!copy.AnimateProgressBars && !copy.CategoryTreeExpanded && copy.PicturesFolder == "Pictures", "CopyFrom carries the new settings");
    }

    private static string[] SeedNames = { "a.zip", "b.zip", "setup.exe", "clip.mp4", "clip2.mkv", "clip3.webm", "song.mp3", "photo.png", "doc.pdf", "book.epub", "data.xyz" };
    private static void Seed(DownloadManager m, params string[] names)
    {
        foreach (var n in names) Wait(m.AddPausedAsync(new DownloadRequest("http://files.invalid/" + n, FileName: n)));
    }
    private static (string Key, int Count)[] CountsOf(MainWindow w) => w.CategoryEntries.Select(e => (e.Key, e.Count)).ToArray();
    private static int ListCount(MainWindow w) => Field<ListView>(w, "DownloadList").Items.Count;

    // ===== item 1: the sidebar =====
    private static void Sidebar(string scratch)
    {
        Loc.SetLanguage("en");
        using var m = NewManager(scratch, "side");
        var s = Settings(Path.Combine(scratch, "side", "dl"));
        s.CustomCategories.Add(new CustomCategory { Name = "Books", Folder = "Books", Extensions = new() { "epub" } });
        int saves = 0;
        var w = NewMain(m, s, () => saves++); w.Show(); Pump(150);
        var nav = Field<ListBox>(w, "Navigation");
        Check(w.AllDownloadsItem.IsExpanded && s.CategoryTreeExpanded, "The category list starts expanded");
        Check(string.Join(",", w.CategoryEntries.Select(e => e.Key)) == "b:Compressed,b:Programs,b:Video,b:Music,b:Pictures,b:Documents,c:Books,other", "The list shows the six built-in categories, the custom category and Other");
        Check(nav.Items.Cast<object>().Take(9).OfType<ListBoxItem>().Select(i => i.Tag as string).SequenceEqual(new[] { "All", "C:b:Compressed", "C:b:Programs", "C:b:Video", "C:b:Music", "C:b:Pictures", "C:b:Documents", "C:c:Books", "C:other" }), "The entries sit directly below All downloads in the sidebar");
        Seed(m, SeedNames); Pump(900);
        var counts = CountsOf(w).ToDictionary(c => c.Key, c => c.Count);
        Check(counts["b:Compressed"] == 2 && counts["b:Programs"] == 1 && counts["b:Video"] == 3 && counts["b:Music"] == 1 && counts["b:Pictures"] == 1 && counts["b:Documents"] == 1 && counts["c:Books"] == 1 && counts["other"] == 1, "Counts per category are right");
        Check(ListCount(w) == 11, "All downloads shows every download");
        var video = w.CategoryItem("b:Video")!;
        Check(video.Content is DockPanel p && p.Children.OfType<TextBlock>().Any(t => t.Text == "3") && p.Children.OfType<TextBlock>().Any(t => t.Text == "Video"), "An entry shows its name and its count");
        Check(NavIcon.GetData(video) is not null && NavIcon.GetData(w.CategoryItem("c:Books")!) is not null && NavIcon.GetData(w.CategoryItem("other")!) is not null, "Every entry has a line icon");
        // choosing a category
        w.SelectCategory("b:Video"); Pump(200);
        Check(w.ActiveCategory == "b:Video" && ListCount(w) == 3 && Field<TextBlock>(w, "Heading").Text == "Category: Video", "Choosing Video shows its three downloads and names the category in the heading");
        w.SelectCategory("b:Compressed"); Pump(200);
        Check(ListCount(w) == 2, "Choosing Compressed shows its two downloads");
        w.SelectCategory("other"); Pump(200);
        Check(ListCount(w) == 1, "Other shows the file that matches nothing");
        w.SelectCategory("c:Books"); Pump(200);
        Check(ListCount(w) == 1, "A custom category filters too");
        // search narrows inside the category
        w.SelectCategory("b:Video"); Pump(150);
        Field<TextBox>(w, "Search").Text = "clip2"; Pump(250);
        Check(ListCount(w) == 1, "The search box narrows inside the chosen category");
        Field<TextBox>(w, "Search").Text = ""; Pump(200);
        Check(ListCount(w) == 3, "Clearing the search shows the whole category again");
        // status entries reset the category, All clears it
        var paused = nav.Items.OfType<ListBoxItem>().First(i => (i.Tag as string) == "Paused");
        nav.SelectedItem = paused; Pump(200);
        Check(w.ActiveCategory is null && w.ActiveFilter == "Paused" && ListCount(w) == 11, "Choosing a status entry clears the category filter");
        w.SelectCategory("b:Music"); Pump(150);
        nav.SelectedItem = w.AllDownloadsItem; Pump(200);
        Check(w.ActiveCategory is null && ListCount(w) == 11, "Choosing All downloads clears the category filter");
        // live counts
        Seed(m, "late.mp4"); Pump(900);
        Check(CountsOf(w).First(c => c.Key == "b:Video").Count == 4, "A new download is counted at once");
        // collapse and remember
        saves = 0;
        double expandedWidth = nav.ActualWidth;
        w.AllDownloadsItem.IsExpanded = false; Pump(150);
        Console.WriteLine($"MEASURE: sidebar list width expanded {expandedWidth:0} px, collapsed {nav.ActualWidth:0} px");
        Check(expandedWidth <= 176, $"The category list keeps the sidebar narrow ({expandedWidth:0} px with its scroll bar)");
        Check(!s.CategoryTreeExpanded && saves == 1 && nav.Items.OfType<ListBoxItem>().All(i => !(i.Tag as string ?? "").StartsWith("C:")), "Collapsing hides the entries and saves the state");
        w.AllDownloadsItem.IsExpanded = true; Pump(150);
        Check(s.CategoryTreeExpanded && saves == 2 && w.CategoryEntries.Count == 8, "Expanding shows them again and saves the state");
        // a selected category collapses back to All
        w.SelectCategory("b:Video"); Pump(150);
        w.AllDownloadsItem.IsExpanded = false; Pump(200);
        Check(w.ActiveCategory is null && ReferenceEquals(nav.SelectedItem, w.AllDownloadsItem), "Collapsing while a category is chosen returns to All downloads");
        w.AllDownloadsItem.IsExpanded = true; Pump(100);
        // a category that empties stays chosen
        w.SelectCategory("c:Books"); Pump(150);
        var books = m.Jobs.First(j => j.FileName == "book.epub");
        m.Remove(books.Id); Pump(900);
        Check(w.ActiveCategory == "c:Books" && ListCount(w) == 0 && CountsOf(w).First(c => c.Key == "c:Books").Count == 0 && ReferenceEquals(nav.SelectedItem, w.CategoryItem("c:Books")), "A category that becomes empty stays chosen and shows a count of 0");
        // rename and delete
        s.CustomCategories[0].Name = "Library"; Pump(900);
        Check(w.ActiveCategory is null && w.CategoryItem("c:Library") is not null && w.CategoryItem("c:Books") is null && ReferenceEquals(nav.SelectedItem, w.AllDownloadsItem), "Renaming the chosen custom category returns to All downloads and shows the new name");
        w.SelectCategory("c:Library"); Pump(150);
        s.CustomCategories.Clear(); Pump(900);
        Check(w.ActiveCategory is null && w.CategoryItem("c:Library") is null && ListCount(w) > 0, "Deleting the chosen custom category returns to All downloads");
        Check(CountsOf(w).First(c => c.Key == "other").Count == 1, "Files of a deleted category move to Other");
        // changing the rules moves downloads
        s.CustomCategories.Add(new CustomCategory { Name = "Clips", Folder = "Clips", UrlRules = new() { "files.invalid" } }); Pump(900);
        Check(CountsOf(w).First(c => c.Key == "c:Clips").Count == 11 && CountsOf(w).First(c => c.Key == "b:Video").Count == 0, "A new address rule sorts the existing downloads again");
        w.Close(); Pump(100);
        // a collapsed setting is honoured at start
        var s2 = Settings(Path.Combine(scratch, "side", "dl")); s2.CategoryTreeExpanded = false;
        var w2 = NewMain(m, s2); w2.Show(); Pump(200);
        Check(!w2.AllDownloadsItem.IsExpanded && w2.CategoryEntries.Count == 0, "A saved collapsed state is restored at start");
        w2.Close(); Pump(100);
    }

    // ===== item 1: keyboard, semantics =====
    private static void TreeKeyboard(string scratch)
    {
        using var m = NewManager(scratch, "keys");
        var s = Settings(Path.Combine(scratch, "keys", "dl"));
        var w = NewMain(m, s); w.Show(); Pump(150); Seed(m, "a.zip", "b.mp4"); Pump(800);
        var nav = Field<ListBox>(w, "Navigation");
        nav.SelectedItem = w.AllDownloadsItem; w.AllDownloadsItem.Focus(); Pump(60);
        Press(nav, Key.Left); Pump(120);
        Check(!w.AllDownloadsItem.IsExpanded, "Left collapses All downloads");
        Press(nav, Key.Right); Pump(120);
        Check(w.AllDownloadsItem.IsExpanded, "Right expands All downloads");
        Press(nav, Key.Right); Pump(120);
        Check(nav.SelectedItem is ListBoxItem { Tag: "C:b:Compressed" }, "Right on an expanded All downloads moves into the first category");
        var focused = (ListBoxItem)nav.SelectedItem; focused.Focus(); Pump(60);
        Press(nav, Key.Left); Pump(120);
        Check(ReferenceEquals(nav.SelectedItem, w.AllDownloadsItem), "Left on a category goes up to All downloads");
        // arrows walk through the entries and select them; Enter keeps the choice
        w.CategoryItem("b:Compressed")!.Focus(); nav.SelectedItem = w.CategoryItem("b:Compressed"); Pump(80);
        Check(w.ActiveCategory == "b:Compressed", "Selecting an entry with the keyboard filters the list");
        // a11y
        var peer = (ListBoxAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(nav);
        var children = peer.GetChildren();
        var allPeer = children.OfType<IExpandCollapseProvider>().FirstOrDefault();
        Check(allPeer is not null && allPeer.ExpandCollapseState == ExpandCollapseState.Expanded, "All downloads reports the expand and collapse pattern (expanded)");
        allPeer!.Collapse(); Pump(120);
        Check(!w.AllDownloadsItem.IsExpanded && allPeer.ExpandCollapseState == ExpandCollapseState.Collapsed, "Collapsing through automation works and reports collapsed");
        allPeer.Expand(); Pump(120);
        string allName = AutomationProperties.GetName(w.AllDownloadsItem);
        Check(allName.Contains("expanded") && allName.Contains("All downloads") && allName.Contains("2"), "All downloads has a name with its count and state: " + allName);
        string childName = AutomationProperties.GetName(w.CategoryItem("b:Video")!);
        Check(childName == "Video, 1 downloads" && AutomationProperties.GetItemType(w.CategoryItem("b:Video")!) == "category", "An entry has a name with its count: " + childName);
        // right to left: the keys swap
        w.FlowDirection = FlowDirection.RightToLeft; Pump(100);
        nav.SelectedItem = w.AllDownloadsItem; w.AllDownloadsItem.Focus(); Pump(60);
        Press(nav, Key.Right); Pump(120);
        Check(!w.AllDownloadsItem.IsExpanded, "In right-to-left layouts Right collapses");
        Press(nav, Key.Left); Pump(120);
        Check(w.AllDownloadsItem.IsExpanded, "In right-to-left layouts Left expands");
        w.Close(); Pump(100);
    }

    // ===== item 1: themes, right to left, small windows =====
    private static void TreeThemes(string scratch)
    {
        using var m = NewManager(scratch, "themes");
        var s = Settings(Path.Combine(scratch, "themes", "dl"));
        s.CustomCategories.Add(new CustomCategory { Name = "Books", Folder = "Books", Extensions = new() { "epub" } });
        string? shots = Shots;
        foreach (var theme in ThemeCatalog.Concrete)
        {
            ThemeManager.Apply(Application.Current, theme.Id, null);
            foreach (var rtl in new[] { false, true })
            {
                var w = NewMain(m, s); w.Show(); Pump(120); w.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight; Pump(80);
                if (m.Jobs.Count == 0) { Seed(m, SeedNames); Pump(900); }
                w.SelectCategory("b:Video"); Pump(150);
                var nav = Field<ListBox>(w, "Navigation");
                var item = w.CategoryItem("b:Video")!; var all = w.AllDownloadsItem;
                Check(item.ActualHeight > 20 && item.ActualWidth > 60 && all.ActualHeight > 20, $"{theme.Id} {(rtl ? "RTL" : "LTR")}: entries have a size");
                var chevron = Visual<Border>(all).FirstOrDefault(b => b.Name == "Chevron");
                Check(chevron is { Visibility: Visibility.Visible, ActualWidth: > 10 }, $"{theme.Id} {(rtl ? "RTL" : "LTR")}: the chevron is shown");
                var chevronX = chevron!.PointToScreen(new Point(chevron.ActualWidth / 2, chevron.ActualHeight / 2)).X; var navX = nav.PointToScreen(new Point(nav.ActualWidth / 2, nav.ActualHeight / 2)).X;
                Console.WriteLine($"DEBUG chevron {chevronX:0} nav {navX:0} win {w.FlowDirection} nav {nav.FlowDirection} item {all.FlowDirection}");
                Check(rtl ? chevronX < navX : chevronX > navX, $"{theme.Id} {(rtl ? "RTL" : "LTR")}: the chevron sits at the end side");
                // selected item contrast: text on the selected background
                var selectedBg = ((SolidColorBrush)w.FindResource("ItemSelectedBrush")).Color; var ink = ((SolidColorBrush)w.FindResource("ItemSelectedForegroundBrush")).Color;
                Check(ThemeManager.Contrast(ink, selectedBg) >= 4.5, $"{theme.Id}: selected entry text is readable");
                if (shots is { Length: > 0 } && (theme.Id is "DeepNavy" or "Light" or "Midnight") && (!rtl || theme.Id == "DeepNavy"))
                {
                    Save(Capture(w), Path.Combine(shots, $"tree-{theme.Id}{(rtl ? "-rtl" : "")}.png")); Console.WriteLine($"SHOT: tree-{theme.Id}{(rtl ? "-rtl" : "")}");
                }
                w.Close(); Pump(60);
            }
        }
        ThemeManager.Apply(Application.Current, "Light", null);
        // small window: the list scrolls and nothing is cut off
        var small = NewMain(m, s, null, 900, 480); small.Show(); Pump(200);
        var sv = Visual<ScrollViewer>(Field<ListBox>(small, "Navigation")).First();
        Check(sv.ExtentHeight >= sv.ViewportHeight, "At the smallest window height the sidebar list can scroll");
        var last = small.CategoryItem("other")!; last.BringIntoView(); Pump(80);
        var top = last.TranslatePoint(new Point(0, last.ActualHeight), Field<ListBox>(small, "Navigation")).Y;
        Check(top <= Field<ListBox>(small, "Navigation").ActualHeight + 1, "The last entry can be scrolled into view");
        small.Close(); Pump(60);
    }
}
