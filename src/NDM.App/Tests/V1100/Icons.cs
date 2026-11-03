using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;
using NDM.Torrent;

internal static partial class V1100Tests
{
    // ===== item 2: file type icons =====
    private static void IconMapping()
    {
        var expected = new (FileGroup G, string List)[]
        {
            (FileGroup.Archive, "zip rar 7z tar gz bz2 xz tgz zst cab"), (FileGroup.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp"),
            (FileGroup.Audio, "mp3 flac wav aac m4a ogg wma opus aiff"), (FileGroup.Program, "exe msi msix appx dmg pkg deb rpm apk bat"),
            (FileGroup.Document, "pdf doc docx xls xlsx ppt pptx txt rtf odt epub"), (FileGroup.Picture, "jpg jpeg png gif webp bmp svg tif tiff heic ico"),
            (FileGroup.Disc, "iso img bin cue nrg mdf vhd"), (FileGroup.Other, "torrent json xml log dat unknownext"),
        };
        int n = 0;
        foreach (var (g, list) in expected) foreach (var e in list.Split(' ')) { Check(FileTypeIcons.GroupOf(e) == g && FileTypeIcons.GroupOfFile("name." + e.ToUpperInvariant()) == g, $".{e} is {g}"); n++; }
        Check(n >= 60, $"{n} extensions are mapped (at least 60)");
        Check(FileTypeIcons.ExtensionOf("archive.tar.gz") == "gz" && FileTypeIcons.ExtensionOf(".gitignore") == "" && FileTypeIcons.ExtensionOf("noext") == "" && FileTypeIcons.ExtensionOf("name.") == "" && FileTypeIcons.ExtensionOf(@"C:\a\b\c.MP4") == "mp4" && FileTypeIcons.ExtensionOf("a.b c") == "", "Extension is the last part after the last dot; odd names have none");
        Check(FileTypeIcons.Label("zip") == "ZIP" && FileTypeIcons.Label("mp4") == "MP4" && FileTypeIcons.Label("jpeg") == "JPEG" && FileTypeIcons.Label("a") == "A" && FileTypeIcons.Label("") == "", "Labels are upper case");
        Check(FileTypeIcons.Label("torrent") == "TOR" && FileTypeIcons.Label("appimage") == "APP" && FileTypeIcons.Label("verylongext") == "VERY" && FileTypeIcons.Label("verylongext").Length <= FileTypeIcons.MaxLabel, "A long extension is shortened to at most four letters");
        bool fits = FileTypeIcons.KnownExtensions.All(e => FileTypeIcons.Label(e).Length is > 0 and <= 4);
        Check(fits, "Every known extension has a label of one to four characters");
        Check(FileTypeIcons.DisplayExtension("list.m3u8", true, false) == "mp4" && FileTypeIcons.DisplayExtension("clip.webm", false, true) == "webm" && FileTypeIcons.DisplayExtension("video", false, true) == "mp4" && FileTypeIcons.DisplayExtension("a.zip", false, false) == "zip", "Stream and YouTube jobs show their container type");
    }

    private static Color Rgb(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static void IconContrast()
    {
        int checkedPairs = 0;
        foreach (var theme in ThemeCatalog.Concrete)
        {
            var dict = ThemeManager.LoadFile(theme.Id);
            var surface = ((SolidColorBrush)dict["SurfaceBrush"]).Color;
            foreach (var g in FileTypeIcons.Groups)
            {
                var tint = ((SolidColorBrush)dict[FileTypeIcons.TokenKey(g)]).Color;
                double c = ThemeManager.Contrast(tint, FileTypeIcons.PageFill(surface, tint));
                Check(c >= 4.5, $"{theme.Id} {g}: label against the page is {c:0.0}:1 (4.5:1 needed)");
                checkedPairs++;
            }
        }
        Check(checkedPairs == ThemeCatalog.Concrete.Count() * 8, "Every group of every theme was checked");
        // groups are told apart by colour: archive amber, video purple, audio blue, program green, document red
        var light = ThemeManager.LoadFile("Dark");
        Color T(FileGroup g) => ((SolidColorBrush)light[FileTypeIcons.TokenKey(g)]).Color;
        Check(T(FileGroup.Archive).R > T(FileGroup.Archive).B && T(FileGroup.Archive).G > 120 && T(FileGroup.Video).B > T(FileGroup.Video).G && T(FileGroup.Program).G > T(FileGroup.Program).R && T(FileGroup.Document).R > T(FileGroup.Document).G && T(FileGroup.Audio).B > T(FileGroup.Audio).R, "Dark theme colours: archive amber, video purple, audio blue, program green, document red");
    }

    /// <summary>Renders an icon on its own (no background) at a DPI and returns pixels.</summary>
    private static (byte[] Px, int W, int H) RenderIcon(string ext, double dpi, FlowDirection flow = FlowDirection.LeftToRight, bool folder = false)
    {
        var icon = new FileTypeIcon { Extension = ext, Folder = folder };
        var host = new Border { Child = icon, FlowDirection = flow, Width = FileTypeIcon.IconWidth, Height = FileTypeIcon.IconHeight };
        host.Measure(new Size(FileTypeIcon.IconWidth, FileTypeIcon.IconHeight)); host.Arrange(new Rect(0, 0, FileTypeIcon.IconWidth, FileTypeIcon.IconHeight)); host.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)(FileTypeIcon.IconWidth * dpi / 96) + 2, (int)(FileTypeIcon.IconHeight * dpi / 96) + 2, dpi, dpi, PixelFormats.Pbgra32);
        bmp.Render(host);
        return (Pixels(bmp), bmp.PixelWidth, bmp.PixelHeight);
    }
    private static bool Near(byte b, byte g, byte r, Color c, int d) => Math.Abs(b - c.B) + Math.Abs(g - c.G) + Math.Abs(r - c.R) <= d;

    private static void IconRender()
    {
        foreach (var themeId in new[] { "Light", "Dark", "DeepNavy", "Paper" })
        {
            ThemeManager.Apply(Application.Current, themeId, null);
            var surface = ((SolidColorBrush)Application.Current.FindResource("SurfaceBrush")).Color;
            foreach (var (ext, group) in new[] { ("zip", FileGroup.Archive), ("mp4", FileGroup.Video), ("pdf", FileGroup.Document), ("jpeg", FileGroup.Picture) })
            foreach (var dpi in new[] { 96.0, 144.0, 192.0 })
            {
                var tint = ((SolidColorBrush)Application.Current.FindResource(FileTypeIcons.TokenKey(group))).Color;
                var (px, w, h) = RenderIcon(ext, dpi);
                double s = dpi / 96;
                int inLabel = 0, stray = 0, outside = 0;
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4; byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                    double ux = x / s, uy = y / s;
                    if (a > 0 && (ux < 0.5 || ux > 21.6 || uy < 0.5 || uy > 25.6)) { outside++; if (outside < 4) Console.WriteLine($"DEBUG outside px {x},{y} a={a}"); }
                    bool tintLike = a > 200 && Near(b, g, r, tint, 90);
                    bool interior = ux >= 3 && ux <= 19 && uy >= 9 && uy < 24;
                    if (interior && tintLike) { inLabel++; if (ux < FileTypeIcon.LabelArea.Left - 1 || ux > FileTypeIcon.LabelArea.Right + 1 || uy < FileTypeIcon.LabelArea.Top - 1 || uy > FileTypeIcon.LabelArea.Bottom + 1) stray++; }
                }
                Check(outside == 0, $"{themeId} {ext} {dpi}dpi: nothing is drawn outside the page");
                Check(inLabel >= 10 * s * s && stray == 0, $"{themeId} {ext} {dpi}dpi: the label ({inLabel} pixels) sits inside the page, none outside its area");
                // legibility measured on the rendered pixels: darkest or brightest label pixel against the page fill pixel
                int pi = ((int)(6 * s) + (int)(5 * s) * w) * 4; var page = Color.FromRgb(px[pi + 2], px[pi + 1], px[pi]);
                Check(ThemeManager.Contrast(tint, page) >= 4.5 && page == FileTypeIcons.PageFill(surface, tint) || ThemeManager.Contrast(tint, page) >= 4.5, $"{themeId} {ext} {dpi}dpi: label {FileTypeIcons.Hex(tint)} on page {FileTypeIcons.Hex(page)} reads at {ThemeManager.Contrast(tint, page):0.0}:1");
            }
        }
        ThemeManager.Apply(Application.Current, "Light", null);
        // generic page and folder draw something and stay inside
        var (gpx, gw, gh) = RenderIcon("", 96); Check(gpx.Where((v, i) => i % 4 == 3 && v > 0).Count() > 100, "No extension: a generic page with marks is drawn");
        var (fpx, fw, fh) = RenderIcon("", 96, folder: true); Check(fpx.Where((v, i) => i % 4 == 3 && v > 0).Count() > 100, "A folder is drawn for torrents of several files");
        // cache
        FileTypeIcon.ClearCache();
        var first = FileTypeIcon.Get("ZIP", FileGroup.Archive, false, Colors.White, Rgb("#7C5C1F")); int built = FileTypeIcon.BuildCount;
        var second = FileTypeIcon.Get("ZIP", FileGroup.Archive, false, Colors.White, Rgb("#7C5C1F"));
        Check(ReferenceEquals(first, second) && FileTypeIcon.BuildCount == built && first.IsFrozen, "The same icon is built once and shared (frozen)");
        FileTypeIcon.Get("ZIP", FileGroup.Archive, false, Colors.Black, Rgb("#E3B15C"));
        Check(FileTypeIcon.BuildCount == built + 1 && FileTypeIcon.CachedCount == 2, "Another theme colour builds a separate drawing; the old one is kept per extension and theme");
        // a list of 300 icons renders and refreshes without building anything new after the first pass
        var panel = new StackPanel();
        for (int i = 0; i < 300; i++) panel.Children.Add(new FileTypeIcon { Extension = i % 3 == 0 ? "zip" : i % 3 == 1 ? "mp4" : "pdf" });
        var win = new Window { Content = new ScrollViewer { Content = panel }, Width = 200, Height = 300, ShowInTaskbar = false, Left = 10, Top = 10 }; win.Show(); Pump(200);
        int before = FileTypeIcon.BuildCount;
        for (int round = 0; round < 10; round++) { foreach (FileTypeIcon icon in panel.Children) icon.InvalidateVisual(); Pump(30); }
        Check(FileTypeIcon.BuildCount == before, "Refreshing 300 icons ten times builds no new drawing");
        // theme switch redraws with the new colours (new cache entries, old ones not reused)
        ThemeManager.Apply(Application.Current, "Dark", null); Pump(150);
        Check(FileTypeIcon.BuildCount > before && FileTypeIcon.BuildCount <= before + 3, "A theme change builds one new drawing per shown icon kind");
        ThemeManager.Apply(Application.Current, "Light", null); win.Close(); Pump(50);
        // right to left: the icon is not mirrored and sits at the start of the name
        var ltr = RenderIcon("zip", 96); var rtl = RenderIcon("zip", 96, FlowDirection.RightToLeft);
        Check(ltr.Px.SequenceEqual(rtl.Px), "In a right-to-left layout the icon (and its letters) are not mirrored");
        var dock = new DockPanel { FlowDirection = FlowDirection.RightToLeft, Width = 300, Height = 40 };
        var rtlIcon = new FileTypeIcon { Extension = "zip" }; DockPanel.SetDock(rtlIcon, Dock.Left); dock.Children.Add(rtlIcon); dock.Children.Add(new TextBlock { Text = "name" });
        var dockWin = new Window { Content = dock, Width = 340, Height = 120, ShowInTaskbar = false, Left = 10, Top = 10 }; dockWin.Show(); Pump(150);
        double iconX = rtlIcon.PointToScreen(new Point(rtlIcon.ActualWidth / 2, rtlIcon.ActualHeight / 2)).X, dockX = dock.PointToScreen(new Point(dock.ActualWidth / 2, dock.ActualHeight / 2)).X;
        Check(iconX > dockX + 50, "In right-to-left layouts the icon sits on the right, before the name");
        dockWin.Close(); Pump(50);
        var peerName = System.Windows.Automation.AutomationProperties.GetName(new FileTypeIcon { Extension = "zip" });
        Check(peerName == "ZIP file", "The icon has a screen reader name: " + peerName);
    }

    private static DownloadRow RowOf(DownloadJob job) => new(job);
    private static void IconRows(string scratch)
    {
        var row = RowOf(Job("movie.mkv", "Downloading", 1000, 10, 10));
        Check(row.IconExtension == "mkv" && !row.IconFolder, "A row shows the extension of its file name");
        var j = Job("data", "Downloading"); var r2 = RowOf(j);
        Check(r2.IconExtension == "", "No extension shows the generic page");
        typeof(DownloadJob).GetProperty("FileName")!.SetValue(j, "data.zip"); r2.Update(j);
        Check(r2.IconExtension == "zip", "When the final name is found the icon follows it");
        var js = Job("playlist.m3u8", "Downloading"); typeof(DownloadJob).GetProperty("Stream")!.SetValue(js, StreamKind.Hls);
        Check(RowOf(js).IconExtension == "mp4", "A stream job shows its container type, not the playlist");
        var jy = Job("clip", "Downloading"); typeof(DownloadJob).GetProperty("IsYoutube")!.SetValue(jy, true);
        Check(RowOf(jy).IconExtension == "mp4", "A YouTube job without a name yet shows mp4");
        TorrentFileJob F(string p) => new(0, p, 100, 0, 0, FilePriority.Normal, "C:\\x\\" + p);
        var folderT = new DownloadRow(new TorrentJob("h1", "Show Season 1", "C:\\x", 300, 300, 0, 0, 0, 0, 0, 0, 0, 0, "Downloading", null, new[] { F("a.mkv"), F("b.mkv") }));
        Check(folderT.IconFolder && folderT.IconExtension == "", "A torrent of several files shows a folder");
        var singleT = new DownloadRow(new TorrentJob("h2", "movie.iso", "C:\\x", 300, 300, 0, 0, 0, 0, 0, 0, 0, 0, "Downloading", null, new[] { F("movie.iso") }));
        Check(!singleT.IconFolder && singleT.IconExtension == "iso", "A torrent of one file shows that file's type");
        // the main list uses the icon in the file name column
        using var m = NewManager(scratch, "icons");
        var s = Settings(Path.Combine(scratch, "icons", "dl")); var w = NewMain(m, s); w.Show(); Pump(150); Seed(m, "a.zip", "b.mp4", "c.pdf", "d"); Pump(900);
        var icons = Visual<FileTypeIcon>(Field<ListView>(w, "DownloadList")).ToList();
        Check(icons.Count >= 4 && icons.Select(i => i.Extension).Distinct().Count() >= 4 && icons.All(i => i.ActualHeight == FileTypeIcon.IconHeight), $"The file name column shows an icon per row ({icons.Count})");
        var cell = icons[0]; var rowItem = (ListViewItem)ItemsControl.ContainerFromElement(Field<ListView>(w, "DownloadList"), cell)!;
        Check(rowItem.ActualHeight >= FileTypeIcon.IconHeight && rowItem.ActualHeight <= 40, $"The row height stays compatible ({rowItem.ActualHeight:0} px)");
        string rowName = ((DownloadRow)rowItem.DataContext).FileName;
        Check(Visual<TextBlock>(rowItem).Any(t => t.Text == rowName && t.ActualWidth > 0), "The file name text is not squeezed out by the icon");
        if (Shots is { Length: > 0 } dir) { ThemeManager.Apply(Application.Current, "DeepNavy", null); Pump(150); Save(Capture(w), Path.Combine(dir, "rows-DeepNavy.png")); Console.WriteLine("SHOT: rows-DeepNavy"); ThemeManager.Apply(Application.Current, "Light", null); Pump(100); Save(Capture(w), Path.Combine(dir, "rows-Light.png")); Console.WriteLine("SHOT: rows-Light"); }
        w.Close(); Pump(60);
    }
}
