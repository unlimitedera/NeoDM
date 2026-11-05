global using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NDM.App;
using NDM.Core;
using NDM.Tests;

internal sealed class TestApplication : Application
{
    public TestApplication()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
    public void ApplyTheme(string theme) => Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/NeoDM;component/Themes/{theme}.xaml") };
}

internal static class AdvancedTests
{
    private static int checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
    private static T Control<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Children(child)) yield return descendant;
        }
    }
    private static void Click(Window window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(int milliseconds = 650)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> ready, string name, int timeout = 15000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeout);
        while (!ready() && DateTime.UtcNow < end) Pump(50);
        Check(ready(), name);
    }
    private static void CompleteDialog(Window dialog, Action action)
    {
        Exception? error = null;
        dialog.Dispatcher.BeginInvoke(new Action(() => { try { action(); } catch (Exception ex) { error = ex; dialog.Close(); } }));
        bool? result = dialog.ShowDialog();
        if (error is not null) throw error;
        Check(result == true, dialog.Title + " accepts valid input");
    }
    private static void Capture(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            drawing.DrawRectangle(window.Background, null, bounds);
            drawing.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.Fill }, null, bounds);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
        Console.WriteLine("Rendered WPF capture: " + path);
    }
    private static long? SavedLimit(string state, string id)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "queue.json")));
        var value = json.RootElement.EnumerateArray().Single(j => j.GetProperty("Id").GetString() == id).GetProperty("SpeedLimit");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
    }
    [STAThread]
    public static int Main()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-Advanced-" + Guid.NewGuid().ToString("N"));
        string state = Path.Combine(root, "state");
        var app = new TestApplication();
        using var fixture = new FixtureServer(); fixture.RedirectTarget = fixture.BaseUrl;
        using var manager = new DownloadManager(Path.Combine(root, "files"), state, new DownloadOptions { MaxConcurrentDownloads = 1, MaxSegments = 1, RetryCount = 0 });
        var window = new MainWindow(manager, new AppSettings { DownloadDirectory = Path.Combine(root, "files") }, () => { }, enableTray: false) { ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            Check(window.Rows.Count == 0 && !Control<Button>(window, "PauseAllButton").IsEnabled && !Control<Button>(window, "ResumeAllButton").IsEnabled, "Empty real queue disables bulk status actions");
            var add = new AddDownloadWindow(fixture.BaseUrl + "/range?scheduled") { Owner = window };
            add.Show(); Pump(50);
            Control<CheckBox>(add, "ScheduleEnabled").IsChecked = true;
            Control<SchedulePicker>(add, "ScheduleInput").Text = "2000-01-01 12:00";
            var addButton = Children(add).OfType<Button>().Single(b => Equals(b.Content, "Add download"));
            addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(add.Request is null && Control<TextBlock>(add, "ValidationError").Text.Contains("future"), "Past local scheduling rejected inline");
            Control<SchedulePicker>(add, "ScheduleInput").Text = "not-a-date";
            addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(add.Request is null && Control<TextBlock>(add, "ValidationError").Text.Contains("cannot be read") && Control<SchedulePicker>(add, "ScheduleInput").ErrorText.Visibility == Visibility.Visible, "Malformed local scheduling rejected inline (message with an example in the local format, shown under the field as well)");
            add.Close();
            string future = DateTime.Now.AddMinutes(5).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            add = new AddDownloadWindow(fixture.BaseUrl + "/range?scheduled") { Owner = window };
            CompleteDialog(add, () =>
            {
                Control<CheckBox>(add, "ScheduleEnabled").IsChecked = true;
                Control<SchedulePicker>(add, "ScheduleInput").Text = future;
                Control<TextBox>(add, "SpeedInput").Text = "32";
                Children(add).OfType<Button>().Single(b => Equals(b.Content, "Add download")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            var scheduled = add.AddToAsync(manager).GetAwaiter().GetResult(); Pump();
            Check(manager.Jobs.Single().Status == "Scheduled" && fixture.Requests("/range") == 0, "Add scheduling prevents any early HTTP request");
            Check(SavedLimit(state, scheduled.Id) == 32768, "Add speed converted from KiB/s and durably saved before start");
            var list = Control<ListBox>(window, "DownloadList"); list.SelectedIndex = 0;
            Check(Control<Button>(window, "ScheduleButton").IsEnabled && Control<Button>(window, "SpeedButton").IsEnabled, "Selected scheduled job exposes both option controls");
            app.ApplyTheme("Dark"); Pump(50);
            var speed = new JobOptionsWindow(manager, scheduled.Id, false) { Owner = window }; speed.Show(); Pump(50);
            Check(((SolidColorBrush)speed.Background).Color == ((SolidColorBrush)window.Background).Color, "New options dialog inherits dark tokens");
            foreach (string invalid in new[] { "0", "-1", "1.5", "9223372036854775807", "abc" })
            {
                Control<TextBox>(speed, "ValueInput").Text = invalid; Click(speed, "ApplyButton");
                Check(Control<TextBlock>(speed, "ValidationError").Text.Contains("positive"), "Speed rejects " + invalid);
            }
            Check(SavedLimit(state, scheduled.Id) == 32768, "Rejected limits leave real engine setting unchanged"); speed.Close();
            speed = new JobOptionsWindow(manager, scheduled.Id, false) { Owner = window };
            CompleteDialog(speed, () => { Control<TextBox>(speed, "ValueInput").Text = "64"; Click(speed, "ApplyButton"); });
            Check(SavedLimit(state, scheduled.Id) == 65536, "Selected-job speed replacement persists");
            speed = new JobOptionsWindow(manager, scheduled.Id, false) { Owner = window };
            CompleteDialog(speed, () => { Control<TextBox>(speed, "ValueInput").Clear(); Click(speed, "ApplyButton"); });
            Check(SavedLimit(state, scheduled.Id) is null, "Blank selected-job limit removes cap");
            var schedule = new JobOptionsWindow(manager, scheduled.Id, true) { Owner = window };
            CompleteDialog(schedule, () => { schedule.Picker!.Text = DateTime.Now.AddMinutes(8).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); Click(schedule, "ApplyButton"); });
            Check(manager.Jobs.Single().ScheduledAt > add.ScheduledAt, "Selected-job rescheduling applies to real engine");
            Click(window, "PauseAllButton");
            Check(manager.Jobs.Single().Status == "Paused", "Pause All includes future scheduled jobs");
            Click(window, "ResumeAllButton");
            Check(manager.Jobs.Single().Status == "Scheduled" && fixture.Requests("/range") == 0, "Resume All preserves future due times");
            var active = manager.AddAsync(new DownloadRequest(fixture.BaseUrl + "/range?active")).GetAwaiter().GetResult();
            manager.SetSpeedLimit(active.Id, 65536);
            Until(() => manager.Jobs.Single(j => j.Id == active.Id).DownloadedBytes > 0, "Real HTTP transfer starts"); Pump();
            var queued = manager.AddAsync(new DownloadRequest(fixture.BaseUrl + "/range?queued")).GetAwaiter().GetResult(); Pump();
            Check(manager.Jobs.Single(j => j.Id == queued.Id).Status == "Queued", "Fixture creates actual capacity-waiting job");
            Control<TextBox>(window, "Search").Text = "no-match"; Pump();
            Click(window, "PauseAllButton");
            Check(manager.Jobs.All(j => j.Status == "Paused"), "Pause All pauses active, queued and scheduled jobs despite hidden filter");
            long before = manager.Jobs.Single(j => j.Id == active.Id).DownloadedBytes; Pump(750);
            Check(manager.Jobs.Single(j => j.Id == active.Id).DownloadedBytes == before, "Pause stops actual transfer writes");
            manager.SetSpeedLimit(active.Id, null); Control<TextBox>(window, "Search").Clear();
            Click(window, "ResumeAllButton");
            Until(() => manager.Jobs.Single(j => j.Id == active.Id).Status == "Completed" && manager.Jobs.Single(j => j.Id == queued.Id).Status == "Completed", "Resume All completes paused real HTTP jobs");
            Check(File.ReadAllBytes(active.FilePath).SequenceEqual(fixture.Payload) && File.ReadAllBytes(queued.FilePath).SequenceEqual(fixture.Payload), "Resumed output byte-exact against fixture");
            var failed = manager.AddAsync(new DownloadRequest(fixture.BaseUrl + "/fail")).GetAwaiter().GetResult();
            Until(() => manager.Jobs.Single(j => j.Id == failed.Id).Status == "Failed", "Fixture produces real failed job");
            int failures = fixture.Requests("/fail"); Click(window, "PauseAllButton"); Click(window, "ResumeAllButton"); Pump();
            Check(manager.Jobs.Single(j => j.Id == failed.Id).Status == "Failed" && fixture.Requests("/fail") == failures, "Bulk actions do not restart failed jobs");
            Check(manager.Jobs.Single(j => j.Id == active.Id).Status == "Completed", "Bulk actions preserve completed jobs");
            schedule = new JobOptionsWindow(manager, scheduled.Id, true) { Owner = window };
            CompleteDialog(schedule, () => Click(schedule, "StartNowButton"));
            Until(() => manager.Jobs.Single(j => j.Id == scheduled.Id).Status == "Completed", "Start now bypasses future schedule and downloads");
            Pump(); list.SelectedItem = window.Rows.Single(r => r.Id == scheduled.Id);
            Check(!Control<Button>(window, "ScheduleButton").IsEnabled && !Control<Button>(window, "SpeedButton").IsEnabled, "Completed selection disables mutation controls");
            var bulk = new BulkImportWindow(manager) { Owner = window }; bulk.Show(); Pump(50);
            Control<TextBox>(bulk, "UrlsInput").Text = fixture.BaseUrl + "/range?bulk-one\ninvalid\n" + fixture.BaseUrl + "/range?bulk-one\nftp://example.test/file\n" + active.Url + "\n\n" + fixture.BaseUrl + "/range?bulk-two";
            Click(bulk, "ImportButton");
            Until(() => Control<Button>(bulk, "ImportButton").IsEnabled, "Bulk import control finishes");
            string summary = Control<TextBlock>(bulk, "Summary").Text;
            Check(summary.Contains("2 added") && summary.Contains("2 invalid") && summary.Contains("2 duplicates skipped") && summary.Contains("1 blank"), "Bulk summary explicitly reports partial success, invalid and duplicate lines");
            Check(Control<ListBox>(bulk, "Results").Items.Count == 6, "Bulk has one numbered result per nonblank line");
            Until(() => manager.Jobs.Where(j => j.Url.Contains("bulk-")).All(j => j.Status == "Completed"), "Bulk valid links reach real engine and complete");
            Check(manager.Jobs.Count(j => j.Url.Contains("bulk-")) == 2, "Bulk duplicates never silently added");
            Click(bulk, "ImportButton"); Until(() => Control<Button>(bulk, "ImportButton").IsEnabled, "Second bulk import completes");
            Check(Control<TextBlock>(bulk, "Summary").Text.Contains("0 added") && manager.Jobs.Count(j => j.Url.Contains("bulk-")) == 2, "Retry does not duplicate previous successful lines");
            bulk.Close();
            var restrictedRoot = Path.Combine(root, "restricted");
            using (var restricted = new DownloadManager(Path.Combine(restrictedRoot, "files"), Path.Combine(restrictedRoot, "state")))
            {
                restricted.Dispose();
                bulk = new BulkImportWindow(restricted) { Owner = window }; bulk.Show(); Pump(50);
                Control<TextBox>(bulk, "UrlsInput").Text = fixture.BaseUrl + "/range?cannot-save\nbad";
                Click(bulk, "ImportButton"); Until(() => Control<Button>(bulk, "ImportButton").IsEnabled, "Failed-save bulk import finishes");
                Check(Control<TextBlock>(bulk, "Summary").Text.Contains("1 failed") && Control<TextBlock>(bulk, "Summary").Text.Contains("1 invalid"), "Bulk persistence failure reported separately from validation");
                bulk.Close();
            }
            add = new AddDownloadWindow(fixture.BaseUrl + "/range?immediate-capped") { Owner = window };
            CompleteDialog(add, () =>
            {
                Control<TextBox>(add, "SpeedInput").Text = "32";
                Children(add).OfType<Button>().Single(b => Equals(b.Content, "Add download")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            var capped = add.AddToAsync(manager).GetAwaiter().GetResult(); Pump(1800);
            var cappedNow = manager.Jobs.Single(j => j.Id == capped.Id);
            Check(cappedNow.Status == "Downloading" && cappedNow.DownloadedBytes > 0 && cappedNow.DownloadedBytes < 196608, "Immediate Add limit throttles actual fixture bytes before completion");
            speed = new JobOptionsWindow(manager, capped.Id, false) { Owner = window };
            CompleteDialog(speed, () => { Control<TextBox>(speed, "ValueInput").Clear(); Click(speed, "ApplyButton"); });
            Until(() => manager.Jobs.Single(j => j.Id == capped.Id).Status == "Completed", "Removing cap on active download completes actual transfer");
            Check(File.ReadAllBytes(capped.FilePath).SequenceEqual(fixture.Payload), "Capped then uncapped output remains byte-exact");
            app.ApplyTheme("Light"); window.Width = 900; window.Height = 560; Pump();
            Check(Control<Button>(window, "BulkButton").ActualWidth > 0 && Control<Button>(window, "ResumeAllButton").ActualHeight >= 36, "Wrapped action toolbar remains usable at minimum window size");
            Capture(window, Path.Combine(root, "main-minimum.png"));
            add = new AddDownloadWindow("") { Owner = window }; add.Show(); Pump(50);
            Capture(add, Path.Combine(root, "add-options.png")); add.Close();
            Console.WriteLine($"ALL {checks} ADVANCED WPF CHECKS PASSED");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            foreach (var job in manager.Jobs.Where(j => j.Status is "Scheduled" or "Queued" or "Downloading")) manager.Pause(job.Id);
            Pump(); window.Close(); Console.WriteLine("Isolated state: " + root);
        }
    }
}
