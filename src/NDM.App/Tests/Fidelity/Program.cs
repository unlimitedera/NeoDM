global using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.Core;
using NDM.Tests;

internal sealed class FidelityApplication : Application
{
    public FidelityApplication()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
    public void Theme(string theme) => Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/NeoDM;component/Themes/{theme}.xaml") };
}
internal static class FidelityTests
{
    private static int checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
    private static T Control<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void Pump(int milliseconds = 70)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> ready, string name, int timeout = 20000)
    {
        var end = Stopwatch.StartNew();
        while (!ready() && end.ElapsedMilliseconds < timeout) Pump();
        Check(ready(), name);
    }
    private static void Key(MainWindow window, System.Windows.Input.Key key)
    {
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) { drawing.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight)); drawing.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, content.ActualWidth, content.ActualHeight) }, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight)); }
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
        Console.WriteLine("CAPTURE: " + path);
    }
    [STAThread]
    public static int Main()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-Fidelity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var app = new FidelityApplication();
        using var fixture = new FixtureServer(); fixture.RedirectTarget = fixture.BaseUrl;
        using var manager = new DownloadManager(Path.Combine(root, "files"), Path.Combine(root, "state"), new DownloadOptions { MaxConcurrentDownloads = 2, MaxSegments = 2, RetryCount = 0 });
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = false };
        var window = new MainWindow(manager, settings, () => settings.Save(Path.Combine(root, "settings")), enableTray: false) { ShowInTaskbar = false };
        var deferred = new Queue<Action>(); var shown = new List<string>(); Action? dismiss = null;
        using var controller = new CompletionController(manager.Jobs, id => manager.Jobs.FirstOrDefault(j => j.Id == id), () => true, (job, closed) => { shown.Add(job.Id); dismiss = closed; }, action => deferred.Enqueue(action));
        var observer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        observer.Tick += (_, _) => controller.Observe(manager.Jobs); observer.Start();
        DownloadCompleteWindow? completion = null;
        try
        {
            window.Show(); Pump();
            var menu = Control<Menu>(window, "MainMenu");
            Check(menu.Items.Cast<MenuItem>().Select(m => m.Header.ToString()!.Replace("_", "")).SequenceEqual(new[] { "Tasks", "File", "Downloads", "View", "Options", "Help" }), "Tasks / File / Downloads / View / Options / Help menu order");
            var optionsItem = menu.Items.Cast<MenuItem>().Single(m => m.Header.ToString() == "_Options");
            Check(optionsItem.Items.Count == 0 && optionsItem.Role == MenuItemRole.TopLevelItem && ((MenuItem)menu.Items[2]).Items.OfType<MenuItem>().All(m => !m.Header.ToString()!.Contains("Options")), "Options is its own top-level item with no submenu and is gone from the Downloads menu");
            Check(Control<Menu>(window, "MainMenu").Items.IndexOf(optionsItem) == menu.Items.Count - 2, "Options sits immediately before Help");
            int dialogsBefore = System.Windows.Application.Current.Windows.OfType<SettingsWindow>().Count();
            var opened = new List<SettingsWindow>();
            var watcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            watcher.Tick += (_, _) => { foreach (var w in System.Windows.Application.Current.Windows.OfType<SettingsWindow>().ToList()) { opened.Add(w); w.Close(); } };
            watcher.Start();
            optionsItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); watcher.Stop();
            Check(opened.Count == 1 && dialogsBefore == 0, "clicking Options opens the Options window directly");
            var list = Control<ListView>(window, "DownloadList");
            Check(list is ListBox && ReferenceEquals(list.ItemsSource, window.Rows), "ListView retains ListBox and public Rows compatibility");
            var grid = (GridView)list.View;
            Check(grid.Columns.Select(c => ((GridViewColumnHeader)c.Header).Content.ToString()).SequenceEqual(new[] { "Filename", "Size", "Status", "Progress", "Remaining", "Speed" }), "Dense sortable columns expose live speed and ETA");
            Check(new[] { "PauseButton", "ScheduleButton", "SpeedButton", "PauseAllButton", "ResumeAllButton", "BulkButton", "Search", "Navigation" }.All(n => window.FindName(n) is not null), "Legacy named controls remain addressable");
            Check(Control<StackPanel>(window, "EmptyState").Visibility == Visibility.Visible, "No fabricated downloads in empty workspace");
            var first = manager.AddScheduledAsync(new DownloadRequest(fixture.BaseUrl + "/range?first"), DateTimeOffset.UtcNow.AddHours(1)).GetAwaiter().GetResult();
            manager.SetSpeedLimit(first.Id, 384 * 1024); manager.Schedule(first.Id, DateTimeOffset.UtcNow.AddSeconds(-1));
            Until(() => manager.Jobs.Any(j => j.Id == first.Id && j.Status == "Downloading" && j.RemainingTime.HasValue && j.SpeedBytesPerSecond > 0), "Real HTTP transfer exposes stable speed and remaining time");
            Until(() => window.Rows.Any(r => r.Id == first.Id && r.Remaining != "—"), "ETA reaches main row from detached core snapshots");
            list.SelectedItem = window.Rows.Single(r => r.Id == first.Id); list.Focus(); Pump(); Key(window, System.Windows.Input.Key.Enter); Pump();
            var progress = System.Windows.Application.Current.Windows.OfType<DownloadProgressWindow>().Single(w => w.IsVisible) /* 1.3.2: progress windows are independent (no owner), so they are no longer in OwnedWindows */;
            Check(progress.Row.Id == first.Id && progress.Row.SegmentRows.Count == 2 && progress.Row.ResumeSupported == true, "Enter opens modeless live progress with real segments and resume support");
            Until(() => progress.Row.DownloadedBytes > 0 && progress.Row.ActiveConnectionCount > 0, "Progress window refreshes live connection bytes");
            Capture(progress, Path.Combine(root, "progress-light.png"));
            Control<Button>(progress, "PauseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(600);
            Check(manager.Jobs.Single(j => j.Id == first.Id).Status == "Paused" && progress.Row.Remaining == "—", "Progress pause uses engine and clears stale ETA");
            var row = window.Rows.Single(r => r.Id == first.Id); int notifications = 0; row.PropertyChanged += (_, _) => notifications++;
            Check(!row.Update(manager.Jobs.Single(j => j.Id == first.Id)) && notifications == 0, "Unchanged snapshot returns false and emits no binding churn");
            var viewMenu = (MenuItem)menu.Items[3]; ((MenuItem)viewMenu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            Check(Control<ListBox>(window, "Navigation").SelectedIndex == 2 && list.Items.Count == 1, "View menu uses actual paused navigation filter");
            Control<TextBox>(window, "Search").Text = "no-matches"; Pump(); Check(list.Items.Count == 0, "Search composes with actual status filter");
            Control<TextBox>(window, "Search").Clear(); Control<ListBox>(window, "Navigation").SelectedIndex = 0; Pump();
            ((GridViewColumnHeader)grid.Columns[1].Header).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(CollectionViewSource.GetDefaultView(window.Rows).SortDescriptions[0].PropertyName == "TotalBytes", "Size column sorts numeric bytes, not formatted text");
            ((GridViewColumnHeader)grid.Columns[1].Header).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(CollectionViewSource.GetDefaultView(window.Rows).SortDescriptions[0].Direction == System.ComponentModel.ListSortDirection.Descending, "Column click toggles sort direction");
            list.SelectedItem = row; Control<Button>(progress, "PauseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); progress.Close(); Pump();
            Check(manager.Jobs.Single(j => j.Id == first.Id).Status != "Paused", "Closing progress window does not pause or cancel transfer");
            var second = manager.AddScheduledAsync(new DownloadRequest(fixture.BaseUrl + "/range?second"), DateTimeOffset.UtcNow.AddHours(1)).GetAwaiter().GetResult();
            manager.SetSpeedLimit(second.Id, 384 * 1024); manager.Schedule(second.Id, DateTimeOffset.UtcNow.AddSeconds(-1));
            Until(() => manager.Jobs.All(j => j.Status == "Completed"), "Two real loopback transfers complete");
            Pump(650);
            Check(window.CompletionNotifications.NotificationCount == 0 && !window.OwnedWindows.OfType<DownloadCompleteWindow>().Any(), "ShowCompletionDialog=false prevents unattended real window notifications");
            Check(shown.Count == 1 && controller.IsShowing && controller.PendingCount == 1, "Completion transitions serialize without notification flood");
            controller.Observe(manager.Jobs); controller.Observe(manager.Jobs);
            Check(shown.Count == 1 && controller.PendingCount == 1, "Repeated completed snapshots cannot duplicate notifications");
            dismiss!(); while (deferred.TryDequeue(out var action)) action();
            Check(shown.Count == 2 && shown.Distinct().Count() == 2 && controller.NotificationCount == 2, "Dismiss advances queue exactly once per completed job");
            dismiss!(); while (deferred.TryDequeue(out var action)) action();
            using var history = new CompletionController(manager.Jobs, id => manager.Jobs.FirstOrDefault(j => j.Id == id), () => true, (_, _) => throw new Exception("Historical popup"), action => action());
            history.Observe(manager.Jobs); Check(history.NotificationCount == 0 && history.PendingCount == 0, "Restored historical completed jobs never notify");
            Check(manager.Jobs.All(j => File.ReadAllBytes(j.FilePath).SequenceEqual(fixture.Payload)), "Saved real fixture files are byte exact");
            settings.ShowCompletionDialog = true;
            completion = new DownloadCompleteWindow(manager.Jobs.Single(j => j.Id == first.Id), settings, () => settings.Save(Path.Combine(root, "settings")), () => manager.Jobs.Any(j => j.Id == first.Id)) { Owner = window, ShowInTaskbar = false };
            completion.Show(); Pump();
            Check(Control<Button>(completion, "OpenButton").IsEnabled && Control<Button>(completion, "OpenWithButton").IsEnabled, "Existing completed file enables explicit safe open actions; no file is launched");
            Check(Control<TextBlock>(completion, "SizeLabel").Text.Contains(fixture.Payload.Length.ToString("N0")) && Control<TextBox>(completion, "PathLabel").Text == first.FilePath, "Completion shows actual on-disk byte size and saved path");
            Control<CheckBox>(completion, "DontShowAgain").IsChecked = true; Pump();
            Check(!AppSettings.Load(Path.Combine(root, "settings")).ShowCompletionDialog, "Don't show again persists only in injected isolated state");
            Capture(completion, Path.Combine(root, "complete-light.png"));
            File.Delete(first.FilePath); completion.RefreshAvailability();
            Check(!Control<Button>(completion, "OpenButton").IsEnabled && !Control<Button>(completion, "OpenWithButton").IsEnabled, "Missing completed file disables safe open and chooser");
            manager.Remove(first.Id); completion.RefreshAvailability();
            Check(!Control<Button>(completion, "FolderButton").IsEnabled, "Removed queue item disables completion folder action");
            completion.Close(); completion = null;
            int completionSignals = 0;
            using (var suppressed = new CompletionController(new[] { second }, id => manager.Jobs.FirstOrDefault(j => j.Id == id), () => false, (_, _) => throw new Exception("Suppressed popup"), action => action(), _ => completionSignals++))
            {
                suppressed.Observe(manager.Jobs); suppressed.Observe(manager.Jobs);
                Check(completionSignals == 1 && suppressed.NotificationCount == 0, "Completion signal is once-only even when dialogs are disabled (independent sound preference)");
            }
            settings.ShowCompletionDialog = true;
            completion = new DownloadCompleteWindow(manager.Jobs.Single(j => j.Id == second.Id), settings, () => throw new IOException("Isolated simulated save failure")) { Owner = window, ShowInTaskbar = false };
            completion.Show(); Control<CheckBox>(completion, "DontShowAgain").IsChecked = true;
            Check(settings.ShowCompletionDialog && Control<CheckBox>(completion, "DontShowAgain").IsChecked == false, "Failed preference save restores settings and checkbox without recursive writes");
            completion.Close(); completion = null;
            foreach (var suffix in new[] { "third", "fourth" })
            {
                var next = manager.AddScheduledAsync(new DownloadRequest(fixture.BaseUrl + "/range?" + suffix), DateTimeOffset.UtcNow.AddHours(1)).GetAwaiter().GetResult();
                manager.SetSpeedLimit(next.Id, 512 * 1024); manager.Schedule(next.Id, DateTimeOffset.UtcNow.AddSeconds(-1));
            }
            Until(() => manager.Jobs.All(j => j.Status == "Completed") && window.CompletionNotifications.PendingCount == 1, "Production controller queues two real completion transitions modelessly");
            var actual = window.OwnedWindows.OfType<DownloadCompleteWindow>().Single();
            Check(window.IsEnabled && window.CompletionNotifications.NotificationCount == 1, "Modeless completion leaves main workspace enabled and shows only one window");
            var shownPath = Control<TextBox>(actual, "PathLabel").Text;
            var removedPending = manager.Jobs.First(j => j.Id != second.Id && j.FilePath != shownPath);
            manager.Remove(removedPending.Id); actual.Close(); Pump(650);
            Check(window.CompletionNotifications.NotificationCount == 1 && !window.OwnedWindows.OfType<DownloadCompleteWindow>().Any(), "Queued notification for removed job is skipped after dismiss");
            var fast = manager.AddAsync(new DownloadRequest(fixture.BaseUrl + "/empty")).GetAwaiter().GetResult();
            // Intentionally let the worker complete before the UI gets a tick.
            System.Threading.Thread.Sleep(400); Key(window, System.Windows.Input.Key.F5);
            Until(() => manager.Jobs.Single(j => j.Id == fast.Id).Status == "Completed" && window.CompletionNotifications.NotificationCount == 2, "Sub-tick zero-byte completion drains captured transitions instead of treating new job as history");
            window.OwnedWindows.OfType<DownloadCompleteWindow>().Single().Close(); Pump();
            string restoreState = Path.Combine(root, "restored-state"); Directory.CreateDirectory(restoreState);
            File.Copy(Path.Combine(root, "state", "queue.json"), Path.Combine(restoreState, "queue.json"));
            using (var restored = new DownloadManager(Path.Combine(root, "files"), restoreState))
            {
                var restoredWindow = new MainWindow(restored, settings, () => settings.Save(Path.Combine(root, "restored-settings")), enableTray: false) { ShowInTaskbar = false };
                restoredWindow.Show(); Pump(650);
                Check(restored.Jobs.All(j => j.Status == "Completed") && restoredWindow.CompletionNotifications.NotificationCount == 0 && !restoredWindow.OwnedWindows.OfType<DownloadCompleteWindow>().Any(), "Actual restored persistent queue creates no historical completion windows");
                restoredWindow.Close();
            }
            window.Width = 900; window.Height = 480; Pump();
            Check(grid.Columns.Sum(c => c.Width) <= list.ActualWidth - 2 && grid.Columns.Skip(1).Sum(c => c.Width) + 205 <= list.ActualWidth - 20 && Control<Button>(window, "ScheduleButton").ActualHeight >= 30, "All speed/ETA columns and command toolbar fit minimum workspace width");
            Capture(window, Path.Combine(root, "main-light-minimum.png"));
            app.Theme("Dark"); Pump(); Capture(window, Path.Combine(root, "main-dark-minimum.png"));
            Check(((SolidColorBrush)Control<TextBox>(window, "Search").Foreground).Color == (Color)ColorConverter.ConvertFromString("#F3F5F6"), "Compact input preserves accessible live dark palette");
            Key(window, System.Windows.Input.Key.F5); Check(Control<TextBlock>(window, "CountLabel").Text.Contains("download"), "F5 keyboard refresh remains wired");
            Console.WriteLine($"ALL {checks} FIDELITY TESTS PASSED"); Console.WriteLine("Isolated artifacts: " + root); return 0;
        }
        finally { observer.Stop(); completion?.Close(); foreach (var job in manager.Jobs.Where(j => j.Status != "Completed")) manager.Pause(job.Id); window.Close(); app.Shutdown(); }
    }
}
