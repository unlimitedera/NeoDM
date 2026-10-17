using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NDM.App;
using NDM.Core;

internal sealed class LayoutTestApp : NDM.App.App
{
    public LayoutTestApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
    protected override void OnStartup(StartupEventArgs e) { }
    public void Theme(string theme) => Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/NeoDM;component/Themes/{theme}.xaml") };
}

/// <summary>1.4.4: every window and dialog scales its content with the window (small, default and 1.6x sizes at 100/125/150 % work areas), keeps its buttons in the corners and clips nothing; window placement is stored, validated and restored.</summary>
internal static class LayoutTests
{
    private static int passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static void Pump(int ms = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static T Sync<T>(Task<T> task) { var end = DateTime.UtcNow.AddSeconds(20); while (!task.IsCompleted) { if (DateTime.UtcNow > end) throw new TimeoutException(); Pump(40); } return task.GetAwaiter().GetResult(); }
    private static IEnumerable<T> Visual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Visual<T>(child)) yield return nested;
        }
    }
    private static T Named<T>(Window w, string name) where T : class => (T)(w.FindName(name) ?? throw new Exception("missing control " + name));
    private static ScrollViewer FirstScroll(Window w) => Visual<ScrollViewer>(w).First(s => s.IsVisible && s.TemplatedParent is null);

    private sealed record Case(string Name, Func<Window> Make, Func<Window, FrameworkElement> Main, Func<Window, Button?>? Corner = null, bool Vertical = true, bool Horizontal = true, bool Resizable = true, bool BottomCorner = true, double Factor = 0.85);
    private sealed record Metrics(double WinW, double WinH, double MainW, double MainH, double RootW, double RootH);

    [STAThread]
    private static int Main()
    {
        string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-LayoutTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var app = new LayoutTestApp();
        try
        {
            ResizeEveryWindow(scratch); ProgressWindowLayout(scratch); ListViewFillRule(); PlacementStore(scratch); PlacementInWindows(scratch);
            Console.WriteLine($"Layout tests passed: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { WindowPlacements.Configure(null); foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close(); try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } app.Shutdown(); }
    }

    private static DownloadManager NewManager(string root, string label) =>
        new(Path.Combine(root, label, "files"), Path.Combine(root, label, "state"), new DownloadOptions { RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(4) });

    private static Metrics Measure(Window w, FrameworkElement main)
    {
        w.UpdateLayout(); var root = (FrameworkElement)w.Content;
        return new Metrics(w.ActualWidth, w.ActualHeight, main.ActualWidth, main.ActualHeight, root.ActualWidth, root.ActualHeight);
    }
    private static void Resize(Window w, double width, double height)
    {
        w.SizeToContent = SizeToContent.Manual; w.Width = width; w.Height = height; Pump(50); w.UpdateLayout(); Pump(30); w.UpdateLayout();
    }
    private static bool InsideScroll(DependencyObject c, FrameworkElement root)
    {
        for (var p = VisualTreeHelper.GetParent(c); p is not null && !ReferenceEquals(p, root); p = VisualTreeHelper.GetParent(p)) if (p is ScrollViewer) return true;
        return false;
    }
    private static List<string> Clipped(Window w)
    {
        var root = (FrameworkElement)w.Content; var issues = new List<string>();
        foreach (var c in Visual<Control>(w).Where(c => c.IsVisible && c.TemplatedParent is null && c.ActualWidth > 0 && c.ActualHeight > 0 && c is Button or TextBox or ComboBox or CheckBox or RadioButton or ListView or ListBox or TreeView or TabControl or PasswordBox))
        {
            var r = c.TransformToAncestor(root).TransformBounds(new Rect(0, 0, c.ActualWidth, c.ActualHeight));
            string label = $"{c.GetType().Name} {(string.IsNullOrEmpty(c.Name) ? "" : c.Name)}".Trim();
            if (r.Left < -0.5 || r.Right > root.ActualWidth + 0.5) issues.Add($"{label} horizontally outside ({r.Left:0}..{r.Right:0} of {root.ActualWidth:0})");
            if (!InsideScroll(c, root) && (r.Top < -0.5 || r.Bottom > root.ActualHeight + 0.5)) issues.Add($"{label} vertically outside ({r.Top:0}..{r.Bottom:0} of {root.ActualHeight:0})");
        }
        return issues;
    }
    private static (double Right, double Bottom) CornerGap(Window w, Button b)
    {
        var root = (FrameworkElement)w.Content; var r = b.TransformToAncestor(root).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight));
        return (root.ActualWidth - r.Right, root.ActualHeight - r.Bottom);
    }

    // ===== 1. every window: content grows with the window, nothing clipped, buttons stay in their corners =====
    private static void ResizeEveryWindow(string root)
    {
        using var manager = NewManager(root, "layout");
        var job = Sync(manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/some/long/path/file-name.iso"), DateTimeOffset.UtcNow.AddDays(1)));
        var meta = new NDM.Torrent.TorrentMetadata("id", "Pack", 600, new[] { new NDM.Torrent.TorrentMetadataFile("Pack/a/x.bin", 100), new NDM.Torrent.TorrentMetadataFile("Pack/z.bin", 500) });
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "layout", "files") };
        var torrents = new TorrentController(Path.Combine(root, "layout", "torrent"), settings);
        Button? Default(Window w) => Visual<Button>(w).FirstOrDefault(b => b.IsDefault && b.IsVisible);
        var cases = new List<Case>
        {
            new("Progress", () => new DownloadProgressWindow(manager, job.Id, new JobCompletionStore(), new AppSettings()), w => Named<ListView>(w, "ConnectionList"), w => Named<Button>(w, "CancelButton"), BottomCorner: false),
            new("Main", () => new MainWindow(manager, settings, () => { }, enableTray: false), w => Named<ListView>(w, "DownloadList")),
            new("Options", () => new SettingsWindow(new AppSettings { DownloadDirectory = Path.Combine(root, "layout") }, Path.Combine(root, "layout-state")), w => Named<TabControl>(w, "OptionsTabs"), w => Named<Button>(w, "SaveButton")),
            new("Add download", () => new AddDownloadWindow("https://example.org/file.zip"), FirstScroll, Default),
            new("Capture dialog", () => new DownloadOptionsDialog("https://example.org/dir/file.zip", "file.zip", "", "", root), FirstScroll, w => Named<Button>(w, "StartButton")),
            new("Bulk import", () => new BulkImportWindow(manager), w => Named<TextBox>(w, "UrlsInput"), w => Named<Button>(w, "ImportButton"), Factor: 0.5), // the URL box and the result list share the spare height 1.2 : 1
            new("Job options", () => new JobOptionsWindow(manager, job.Id, false), FirstScroll, w => Named<Button>(w, "ApplyButton")),
            new("Properties", () => new PropertiesWindow(new DownloadRow(job)), FirstScroll, Default),
            new("Download complete", () => new DownloadCompleteWindow(job, new AppSettings(), () => { }), FirstScroll, Default),
            new("Add torrent", () => new TorrentAddWindow(new AppSettings { DownloadDirectory = root }, "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", null, (_, _, _) => Task.FromResult(meta)), w => Named<TreeView>(w, "FileTree"), w => Named<Button>(w, "StartButton")),
            new("Torrent details", () => new TorrentDetailsWindow(torrents, "missing"), w => Named<ListView>(w, "FileList"), w => Visual<Button>(w).First(b => (b.Content as string) == "_Hide")),
            new("Remove confirm", () => new RemoveConfirmWindow(new[] { new RemoveItem("file-a.zip", "C:\\Downloads\\file-a.zip", 1, false), new RemoveItem("file-b.iso", "C:\\Downloads\\file-b.iso", 1, false), new RemoveItem("file-c.mp4", "C:\\Downloads\\file-c.mp4", 1, false) }), FirstScroll, w => Named<Button>(w, "RemoveButton")),
            new("Update", () => new UpdateWindow(ReleaseInfoFor("1.3.1"), new UpdateController(new AppSettings(), () => { }, "1.3.0", () => throw new InvalidOperationException(), new UpdateInstaller(Path.Combine(root, "u"), "1.3.0"))), w => Named<ScrollViewer>(w, "NotesScroll"), w => Named<Button>(w, "UpdateButton")),
            new("About", () => new AboutWindow(), w => Named<ScrollViewer>(w, "ChangelogScroll"), w => Named<Button>(w, "CloseButton")),
            new("Extension guide", () => new ExtensionGuideWindow("1.4.4", new ExtensionFolder(ExtensionFolderKind.Development, @"F:\proj\extension")), w => Named<ScrollViewer>(w, "GuideScroll"), w => Named<Button>(w, "CloseButton")),
            new("Extension notice", () => new ExtensionNoticeWindow(new ExtensionCheck(ExtensionStatus.Older, "1.0.0", "1.4.4"), _ => { }), w => (FrameworkElement)w.FindName("Banner")!, Vertical: false),
            new("Text prompt", () => new TextPromptWindow("Rename", "New name", "file.zip", _ => null), w => Named<TextBox>(w, "Input"), w => Named<Button>(w, "OkButton"), Vertical: false),
            new("Countdown", () => new CountdownWindow(CompletionActionKind.Shutdown, 30), w => (FrameworkElement)w.Content, Resizable: false),
        };
        foreach (double scale in new[] { 1.0, 1.25, 1.5 })
        {
            var work = new Rect(0, 0, 1920 / scale, 1040 / scale); string tag = $"{scale * 100:0}%: ";
            foreach (var c in cases)
            {
                var w = c.Make(); w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = 0; w.Top = 0; w.Owner = null;
                WindowSizing.Fit(w, work); w.Show(); Pump(150); w.UpdateLayout();
                var main = c.Main(w); string label = tag + c.Name;
                var def = Measure(w, main);
                Check(Clipped(w).Count == 0 && def.WinW <= work.Width + 0.5 && def.WinH <= work.Height + 0.5, $"{label}: default size {def.WinW:0}x{def.WinH:0} fits the work area and clips nothing ({string.Join("; ", Clipped(w))})");
                if (!c.Resizable) { Check(w.ResizeMode == ResizeMode.NoResize || w.ResizeMode == ResizeMode.CanMinimize, label + ": auto-sized countdown stays fixed (its text wraps to the window)"); w.Close(); continue; }
                Check(w.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip, $"{label}: window can be resized ({w.ResizeMode})");
                double smallW = Math.Min(Math.Max(w.MinWidth, 300), def.WinW), smallH = Math.Min(Math.Max(w.MinHeight, 200), def.WinH);
                double largeW = Math.Min(def.WinW * 1.6, Math.Min(w.MaxWidth, work.Width - WindowSizing.Margin)), largeH = Math.Min(def.WinH * 1.6, Math.Min(w.MaxHeight, work.Height - WindowSizing.Margin));
                Resize(w, largeW, largeH); var large = Measure(w, main);
                Resize(w, smallW, smallH); var small = Measure(w, main);
                Resize(w, def.WinW, def.WinH); var back = Measure(w, main);
                double dW = large.WinW - def.WinW, dH = large.WinH - def.WinH;
                if (c.Horizontal && dW >= 60) Check(large.MainW - def.MainW >= dW * c.Factor - 2, $"{label}: main area width grows with the window ({def.MainW:0} to {large.MainW:0} for +{dW:0})");
                if (c.Vertical && dH >= 60) Check(large.MainH - def.MainH >= dH * c.Factor - 2, $"{label}: main area height grows with the window ({def.MainH:0} to {large.MainH:0} for +{dH:0})");
                Check(small.MainW <= def.MainW + 1 && small.MainH <= def.MainH + 1 && small.MainW > 0 && small.MainH > 0, $"{label}: main area shrinks with the window and stays visible ({small.MainW:0}x{small.MainH:0})");
                Check(Math.Abs(back.MainW - def.MainW) <= 2 && (!c.Vertical || Math.Abs(back.MainH - def.MainH) <= 2), $"{label}: returning to the default size restores the layout");
                Resize(w, largeW, largeH); var bigClip = Clipped(w);
                Resize(w, smallW, smallH); var smallClip = Clipped(w);
                Check(bigClip.Count == 0 && smallClip.Count == 0, $"{label}: no control outside the window at the small and large size ({string.Join("; ", bigClip.Concat(smallClip))})");
                if (c.Corner?.Invoke(w) is { } button)
                {
                    Resize(w, def.WinW, def.WinH); var gapDefault = CornerGap(w, button);
                    Resize(w, largeW, largeH); var gapLarge = CornerGap(w, button);
                    Resize(w, smallW, smallH); var gapSmall = CornerGap(w, button);
                    bool right = Math.Abs(gapLarge.Right - gapDefault.Right) <= 2 && Math.Abs(gapSmall.Right - gapDefault.Right) <= 2 && gapDefault.Right < 50;
                    bool bottom = !c.BottomCorner || (Math.Abs(gapLarge.Bottom - gapDefault.Bottom) <= 2 && Math.Abs(gapSmall.Bottom - gapDefault.Bottom) <= 2 && gapDefault.Bottom < 50);
                    Check(right && bottom, $"{label}: {button.Content ?? button.Name} stays {(c.BottomCorner ? "in the bottom right corner" : "at the right edge")} (gap right {gapSmall.Right:0}/{gapDefault.Right:0}/{gapLarge.Right:0}, bottom {gapSmall.Bottom:0}/{gapDefault.Bottom:0}/{gapLarge.Bottom:0})");
                }
                // The main window asks "Quit NeoDM?" when a row is busy (the scheduled job is); the prompt would block the test forever, so mark it as an explicit exit.
                if (w is MainWindow) typeof(MainWindow).GetField("_exitWithoutPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(w, true);
                w.Close();
            }
        }
    }

    private static NDM.App.ReleaseInfo ReleaseInfoFor(string version) => UpdateChecker.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "release-latest.json")).Replace("1.3.1", version))!;

    // ===== 2. progress window: the table takes the spare height, hide/show details resizes the window, the trailing blank header is gone =====
    private static void ProgressWindowLayout(string root)
    {
        using var manager = NewManager(root, "progress");
        var job = Sync(manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/some/long/path/file-name-with-a-very-long-name-that-has-to-be-trimmed-somewhere.iso"), DateTimeOffset.UtcNow.AddDays(1)));
        foreach (double scale in new[] { 1.0, 1.25, 1.5 })
        {
            var work = new Rect(0, 0, 1920 / scale, 1040 / scale); string tag = $"{scale * 100:0}%: progress window ";
            var w = new DownloadProgressWindow(manager, job.Id, new JobCompletionStore(), new AppSettings()) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            WindowSizing.Fit(w, work); w.Show(); Pump(150);
            var table = Named<ListView>(w, "ConnectionList"); var content = (FrameworkElement)w.Content; var pane = Named<Grid>(w, "DetailsPane");
            Check(table.ActualHeight >= 90 && table.TransformToAncestor(content).TransformBounds(new Rect(0, 0, table.ActualWidth, table.ActualHeight)).Bottom >= content.ActualHeight - 1, tag + $"connection table reaches the bottom edge ({table.ActualHeight:0} high)");
            double largeH = Math.Min(w.ActualHeight * 1.5, work.Height - WindowSizing.Margin), largeW = Math.Min(w.ActualWidth * 1.5, work.Width - WindowSizing.Margin);
            double before = table.ActualHeight, beforeW = table.ActualWidth;
            Resize(w, largeW, largeH);
            var bottom = table.TransformToAncestor(content).TransformBounds(new Rect(0, 0, table.ActualWidth, table.ActualHeight)).Bottom;
            if (largeH - 100 > 0 && w.ActualHeight > 0) Check(bottom >= content.ActualHeight - 1 && table.ActualHeight >= before - 1, tag + $"table fills the added height (bottom {bottom:0} of {content.ActualHeight:0}, {before:0} to {table.ActualHeight:0})");
            var tabs = Named<TabControl>(w, "Tabs"); var bar = Named<ProgressBar>(w, "OverallBar"); var seg = Named<Canvas>(w, "SegmentBar");
            Check(Math.Abs(tabs.ActualWidth - content.ActualWidth) <= 1 && Math.Abs(bar.ActualWidth - content.ActualWidth) <= 1 && Math.Abs(table.ActualWidth - content.ActualWidth) <= 1, tag + "tabs, progress bar and table span the full width");
            var barBorder = (FrameworkElement)seg.Parent; Check(Math.Abs(barBorder.ActualWidth - content.ActualWidth) <= 1 && Math.Abs(seg.ActualWidth - (barBorder.ActualWidth - 2)) <= 1, tag + "segment bar spans the full width");
            var grid = (GridView)table.View; double viewport = Visual<ScrollViewer>(table).First().ViewportWidth;
            Check(Math.Abs(grid.Columns.Sum(c => c.Width) - viewport) <= 2, tag + $"Info column fills the table ({grid.Columns.Sum(c => c.Width):0} of {viewport:0})");
            var padding = Visual<GridViewColumnHeader>(table).Where(h => h.Role == GridViewColumnHeaderRole.Padding).ToList();
            Check(padding.All(h => h.ActualWidth <= 3), tag + "no blank trailing header cell (padding header " + string.Join(",", padding.Select(h => h.ActualWidth.ToString("0.0"))) + " wide)");
            // the Info column keeps filling after the user drags another column
            grid.Columns[1].Width = 200; Pump(80); w.UpdateLayout();
            Check(Math.Abs(grid.Columns.Sum(c => c.Width) - Visual<ScrollViewer>(table).First().ViewportWidth) <= 2, tag + "widening another column shrinks the Info column instead of adding a scroll bar");
            Check(Named<TextBlock>(w, "UrlText").ToolTip is string full && full.Length > 60 && Named<TextBlock>(w, "UrlText").TextTrimming == TextTrimming.CharacterEllipsis, tag + "long URL is trimmed with an ellipsis and carries the full address as a tooltip");
            // hide / show details
            Resize(w, w.ActualWidth, Math.Min(w.ActualHeight, largeH)); double expanded = w.ActualHeight;
            Click(w, "DetailsButton"); Pump(150); w.UpdateLayout();
            Check(pane.Visibility == Visibility.Collapsed && w.ActualHeight < expanded - 100, tag + $"hiding the details shrinks the window ({expanded:0} to {w.ActualHeight:0})");
            Click(w, "DetailsButton"); Pump(150); w.UpdateLayout();
            Check(pane.Visibility == Visibility.Visible && Math.Abs(w.ActualHeight - expanded) <= 3 && table.ActualHeight >= 90, tag + $"showing them again restores the size ({w.ActualHeight:0})");
            // the user's own resize is not undone
            Resize(w, w.ActualWidth, Math.Max(w.MinHeight, w.ActualHeight - 40)); double manual = w.ActualHeight; Pump(600);
            Check(Math.Abs(w.ActualHeight - manual) <= 1, tag + "a manual resize is left alone by the refresh timer");
            w.Close();
        }
    }
    private static void Click(Window w, string name) => Named<Button>(w, name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    // ===== 3. the fill rule on its own =====
    private static void ListViewFillRule()
    {
        Check(ListViewFill.Compute(500, new[] { 50.0, 140.0 }, 80) == 309, "Fill column = viewport minus the other columns (500 - 190 - 1)");
        Check(ListViewFill.Compute(200, new[] { 50.0, 140.0 }, 80) == 80, "Fill column never drops below its minimum width");
        var list = new ListView { Width = 600, Height = 120, View = new GridView() }; var grid = (GridView)list.View;
        grid.Columns.Add(new GridViewColumn { Header = "A", Width = 100 }); grid.Columns.Add(new GridViewColumn { Header = "B", Width = 100 }); grid.Columns.Add(new GridViewColumn { Header = "C", Width = 50 });
        ListViewFill.SetFillColumn(list, 1);
        for (int i = 0; i < 40; i++) list.Items.Add("row " + i);
        var host = new Window { Width = 700, Height = 220, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Content = new Border { Child = list } }; host.Show(); Pump(150);
        double viewport = Visual<ScrollViewer>(list).First().ViewportWidth; Check(Math.Abs(grid.Columns.Sum(c => c.Width) - viewport) <= 2, $"A list with a vertical scroll bar still fits exactly ({grid.Columns.Sum(c => c.Width):0} of {viewport:0})");
        host.Width = 900; Pump(100); list.Width = 800; Pump(100); host.UpdateLayout();
        Check(Math.Abs(grid.Columns.Sum(c => c.Width) - Visual<ScrollViewer>(list).First().ViewportWidth) <= 2 && grid.Columns[1].Width > 400, "Widening the list widens only the fill column");
        host.Close();
    }

    // ===== 4. window placement store =====
    private static void PlacementStore(string root)
    {
        var screen = new Rect(0, 0, 1920, 1080); var work = new Rect(0, 0, 1920, 1040); var min = new Size(500, 400);
        WindowPlacements.WorkArea = () => work; WindowPlacements.VirtualScreen = () => screen;
        var ok = WindowPlacements.Validate(new PlacementRecord { Left = 100, Top = 80, Width = 900, Height = 700 }, min, work, screen)!;
        Check(ok.Left == 100 && ok.Top == 80 && ok.Width == 900 && ok.Height == 700, "A placement inside the screen is kept as stored");
        var off = WindowPlacements.Validate(new PlacementRecord { Left = 5000, Top = 3000, Width = 900, Height = 700 }, min, work, screen)!;
        Check(off.Left >= 0 && off.Top >= 0 && off.Left + off.Width <= screen.Right && off.Top + off.Height <= work.Bottom && off.Width == 900, $"A window stored far off the screen is centred on the work area again ({off.Left:0},{off.Top:0})");
        var edge = WindowPlacements.Validate(new PlacementRecord { Left = 1800, Top = 900, Width = 900, Height = 700 }, min, work, screen)!;
        Check(edge.Left + edge.Width <= screen.Right && edge.Top + edge.Height <= screen.Bottom, $"A window that hangs over the screen edge is moved back inside ({edge.Left:0},{edge.Top:0})");
        var huge = WindowPlacements.Validate(new PlacementRecord { Left = 0, Top = 0, Width = 9000, Height = 9000 }, min, work, screen)!;
        Check(huge.Width <= work.Width - WindowSizing.Margin && huge.Height <= work.Height - WindowSizing.Margin, $"An oversized window is limited to the work area ({huge.Width:0}x{huge.Height:0})");
        var tiny = WindowPlacements.Validate(new PlacementRecord { Left = 0, Top = 0, Width = 150, Height = 120 }, min, work, screen)!;
        Check(tiny.Width >= min.Width && tiny.Height >= min.Height, "A stored size below the window's minimum is raised to the minimum");
        Check(WindowPlacements.Validate(new PlacementRecord { Left = 0, Top = 0, Width = double.NaN, Height = 500 }, min, work, screen) is null && WindowPlacements.Validate(new PlacementRecord { Left = 0, Top = 0, Width = -5, Height = 500 }, min, work, screen) is null && WindowPlacements.Validate(new PlacementRecord { Left = 0, Top = 0, Width = 800, Height = double.PositiveInfinity }, min, work, screen) is null, "NaN, negative and infinite sizes are rejected");
        var nan = WindowPlacements.Validate(new PlacementRecord { Left = double.NaN, Top = 10, Width = 800, Height = 600 }, min, work, screen)!;
        Check(!double.IsNaN(nan.Left) && nan.Left >= 0, "A missing position is replaced by the centre of the work area");
        var wide = new Rect(-1920, 0, 3840, 1080);
        var second = WindowPlacements.Validate(new PlacementRecord { Left = -1800, Top = 50, Width = 800, Height = 600 }, min, new Rect(-1920, 0, 1920, 1040), wide)!;
        Check(second.Left == -1800, "A window on a monitor left of the primary one (negative coordinates) stays where it was");
        var gone = WindowPlacements.Validate(new PlacementRecord { Left = -1800, Top = 50, Width = 800, Height = 600 }, min, work, screen)!;
        Check(gone.Left >= 0, "The same window is brought back when that monitor is no longer connected");

        string dir = Path.Combine(root, "placement"); Directory.CreateDirectory(dir);
        WindowPlacements.Configure(dir);
        Check(WindowPlacements.Get("X") is null, "Nothing is stored before a window closes");
        File.WriteAllText(Path.Combine(dir, WindowPlacements.FileName), "{ not json");
        WindowPlacements.Configure(dir);
        Check(WindowPlacements.Get("X") is null, "A damaged placement file is ignored");
        WindowPlacements.Configure(null);
    }

    // ===== 5. windows remember their size and position =====
    private static void PlacementInWindows(string root)
    {
        string dir = Path.Combine(root, "placement-windows"); Directory.CreateDirectory(dir);
        var screen = new Rect(0, 0, 1920, 1080); var work = new Rect(0, 0, 1920, 1040);
        WindowPlacements.WorkArea = () => work; WindowPlacements.VirtualScreen = () => screen;
        WindowPlacements.Configure(dir); WindowPlacements.Reset();
        using var manager = NewManager(root, "placement"); var job = Sync(manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/p.bin"), DateTimeOffset.UtcNow.AddDays(1)));
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "placement", "files") };
        var makers = new (string Key, Func<Window> Make)[]
        {
            ("Progress", () => new DownloadProgressWindow(manager, job.Id, new JobCompletionStore(), settings)),
            ("Options", () => new SettingsWindow(settings, Path.Combine(root, "placement-state"))),
            ("Add", () => new AddDownloadWindow("https://example.org/a.zip")),
            ("Capture", () => new DownloadOptionsDialog("https://example.org/dir/file.zip", "file.zip", "", "", root)),
            ("Properties", () => new PropertiesWindow(new DownloadRow(job))),
            ("TorrentAdd", () => new TorrentAddWindow(settings, "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", null, (_, _, _) => new TaskCompletionSource<NDM.Torrent.TorrentMetadata>().Task)),
            ("TorrentDetails", () => new TorrentDetailsWindow(new TorrentController(Path.Combine(root, "placement", "torrent"), settings), "missing")),
            ("Guide", () => new ExtensionGuideWindow("1.4.4", new ExtensionFolder(ExtensionFolderKind.Development, @"F:\proj\extension"))),
            ("Update", () => new UpdateWindow(ReleaseInfoFor("1.3.1"), new UpdateController(new AppSettings(), () => { }, "1.3.0", () => throw new InvalidOperationException(), new UpdateInstaller(Path.Combine(root, "u"), "1.3.0")))),
        };
        int n = 0;
        foreach (var (key, make) in makers)
        {
            n++; double left = 40 + n * 12, top = 30 + n * 9, width = 760 + n * 6, height = 640 + n * 8;
            var first = make(); first.ShowInTaskbar = false; first.WindowStartupLocation = WindowStartupLocation.Manual; first.Left = 5; first.Top = 5;
            first.Show(); Pump(120);
            first.SizeToContent = SizeToContent.Manual; first.Left = left; first.Top = top; first.Width = width; first.Height = height; Pump(80);
            first.Close(); Pump(30);
            var stored = WindowPlacements.Get(key);
            Check(stored is not null && Math.Abs(stored.Width - width) < 1.5 && Math.Abs(stored.Height - height) < 1.5 && Math.Abs(stored.Left - left) < 1.5 && Math.Abs(stored.Top - top) < 1.5, $"{key}: closing the window stores its rectangle ({stored?.Left:0},{stored?.Top:0} {stored?.Width:0}x{stored?.Height:0})");
            WindowPlacements.Configure(dir); // reload from disk, as after a restart
            var second = make(); second.ShowInTaskbar = false; second.Show(); Pump(150);
            Check(Math.Abs(second.Left - left) < 1.5 && Math.Abs(second.Top - top) < 1.5 && Math.Abs(second.ActualWidth - width) < 1.5 && Math.Abs(second.ActualHeight - height) < 1.5, $"{key}: reopening restores size and position ({second.Left:0},{second.Top:0} {second.ActualWidth:0}x{second.ActualHeight:0})");
            second.Close();
        }
        // each window type has its own entry
        Check(makers.Select(m => WindowPlacements.Get(m.Key)).Select(r => r!.Width).Distinct().Count() == makers.Length, "Every window type keeps its own rectangle");
        string json = File.ReadAllText(Path.Combine(dir, WindowPlacements.FileName));
        Check(makers.All(m => json.Contains($"\"{m.Key}\"")) && !File.Exists(Path.Combine(dir, WindowPlacements.FileName + ".tmp")), "The store is one JSON file with one entry per window type and no leftover temp file");

        // off-screen entry: the window opens inside the work area
        var records = new Dictionary<string, PlacementRecord> { ["Add"] = new() { Left = 9000, Top = 9000, Width = 700, Height = 650 } };
        File.WriteAllText(Path.Combine(dir, WindowPlacements.FileName), JsonSerializer.Serialize(records)); WindowPlacements.Configure(dir);
        var lost = new AddDownloadWindow("https://example.org/a.zip") { ShowInTaskbar = false }; lost.Show(); Pump(150);
        Check(lost.Left >= 0 && lost.Top >= 0 && lost.Left + lost.ActualWidth <= screen.Right + 0.5 && lost.Top + lost.ActualHeight <= work.Bottom + 0.5 && Math.Abs(lost.ActualWidth - 700) < 1.5, $"A window stored off-screen opens fully on the screen ({lost.Left:0},{lost.Top:0})");
        lost.Close();
        // invalid entry: dropped, built-in size used
        File.WriteAllText(Path.Combine(dir, WindowPlacements.FileName), "{\"Add\":{\"Left\":10,\"Top\":10,\"Width\":-5,\"Height\":650,\"Maximized\":false}}"); WindowPlacements.Configure(dir);
        var fresh = new AddDownloadWindow("https://example.org/a.zip") { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 50, Top = 50 }; fresh.Show(); Pump(150);
        Check(Math.Abs(fresh.ActualWidth - 580) < 1.5 && WindowPlacements.Get("Add") is null, "An invalid entry is discarded and the window opens at its built-in size");
        fresh.Close(); WindowPlacements.Reset();
        // maximised windows reopen maximised and keep their normal size
        var big = new AddDownloadWindow("https://example.org/a.zip") { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 100, Top = 60, Width = 800, Height = 700 }; big.Show(); Pump(100);
        big.WindowState = WindowState.Maximized; Pump(150); big.Close(); Pump(30);
        var m = WindowPlacements.Get("Add");
        Check(m is { Maximized: true } && Math.Abs(m.Width - 800) < 1.5 && Math.Abs(m.Height - 700) < 1.5, $"A maximised window stores its normal size and the maximised flag ({m?.Width:0}x{m?.Height:0})");
        WindowPlacements.Configure(dir);
        var again = new AddDownloadWindow("https://example.org/a.zip") { ShowInTaskbar = false }; again.Show(); Pump(150);
        Check(again.WindowState == WindowState.Maximized, "A maximised window reopens maximised");
        again.Close(); Pump(30);
        var kept = WindowPlacements.Get("Add");
        Check(kept is { Maximized: true } && Math.Abs(kept.Width - 800) < 1.5 && Math.Abs(kept.Height - 700) < 1.5, $"Closing it again keeps the normal size, not the maximised one ({kept?.Width:0}x{kept?.Height:0})");
        WindowPlacements.Reset();
        // progress window closed with its details hidden reopens with the table
        var pw = new DownloadProgressWindow(manager, job.Id, new JobCompletionStore(), settings) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
        pw.Show(); Pump(150); double expanded = pw.ActualHeight; Click(pw, "DetailsButton"); Pump(150); double collapsed = pw.ActualHeight; pw.Close(); Pump(30);
        var stored2 = WindowPlacements.Get("Progress");
        Check(collapsed < expanded - 100 && stored2 is not null && stored2.Height >= expanded - 3, $"A progress window closed with hidden details remembers the expanded height ({stored2?.Height:0} vs {expanded:0})");
        string before = File.ReadAllText(Path.Combine(dir, WindowPlacements.FileName));
        WindowPlacements.Configure(null);
        var untracked = new AddDownloadWindow("https://example.org/a.zip") { ShowInTaskbar = false, Width = 620, Height = 640 }; untracked.Show(); Pump(80); untracked.Close(); Pump(30);
        Check(File.ReadAllText(Path.Combine(dir, WindowPlacements.FileName)) == before && WindowPlacements.Get("Add") is null, "Without a configured state directory windows neither read nor write placements");
    }
}
