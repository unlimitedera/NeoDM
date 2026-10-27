using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.Core;

internal static class OptionsTests
{
    private static int passed;
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        passed++; Console.WriteLine("PASS: " + name);
    }
    private static T Field<T>(SettingsWindow window, string name) where T : class => (T)window.FindName(name);
    private static bool LogicalUnder(DependencyObject node, DependencyObject ancestor) { for (var p = node; p is not null; p = LogicalTreeHelper.GetParent(p)) if (ReferenceEquals(p, ancestor)) return true; return false; }
    private static void Click(SettingsWindow window, string name) => Field<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Edit(SettingsWindow window)
    {
        Field<TextBox>(window, "Segments").Text = "32";
        Field<TextBox>(window, "Concurrent").Text = "16";
        Field<TextBox>(window, "Retries").Text = "0";
        Field<TextBox>(window, "Timeout").Text = "300";
        Field<CheckBox>(window, "DynamicSegmentation").IsChecked = false;
        Field<CheckBox>(window, "CompletionDialog").IsChecked = false;
        Check(Field<CheckBox>(window, "TrayActiveCount").IsChecked == true, "Tray badge checkbox starts on");
        Field<CheckBox>(window, "TrayActiveCount").IsChecked = false;
        Field<CheckBox>(window, "CompletionSound").IsChecked = true;
        Field<ListBox>(window, "ThemeGallery").SelectedIndex = 1; // Dark
    }
    private static void Capture(SettingsWindow window, string path)
    {
        window.UpdateLayout();
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    [STAThread]
    public static int Main()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-OptionsTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
        try
        {
            var defaults = AppSettings.Load(Path.Combine(root, "missing"));
            Check(defaults.MaxSegments == 8 && defaults.MaxConcurrentDownloads == 3 && defaults.RetryCount == 3 && defaults.RequestTimeoutSeconds == 30, "Missing settings preserve startup defaults");
            Check(defaults.EnableDynamicSegmentation && defaults.ShowCompletionDialog && defaults.PlayCompletionSound && defaults.Theme == "Light", "Boolean and theme defaults");
            Check(defaults.TrayShowActiveCount, "Tray badge defaults to on");
            string oldDir = Path.Combine(root, "old-settings"); Directory.CreateDirectory(oldDir);
            File.WriteAllText(Path.Combine(oldDir, "settings.json"), "{\"SettingsSchema\":4,\"Theme\":\"Dark\",\"PlayCompletionSound\":false}");
            Check(AppSettings.Load(oldDir).TrayShowActiveCount, "A settings.json written before 1.4.6 (no TrayShowActiveCount key) loads with the badge on");
            File.WriteAllText(Path.Combine(oldDir, "settings.json"), "{\"TrayShowActiveCount\":false}");
            Check(!AppSettings.Load(oldDir).TrayShowActiveCount, "An explicit TrayShowActiveCount=false is kept");
            Check(new DownloadOptions().MaxSegments == 4 && OptionsHelper.CreateDownloadOptions(defaults).MaxSegments == 8, "Core default four unchanged; application startup uses eight");
            var state = Path.Combine(root, "state"); Directory.CreateDirectory(state);
            var path = Path.Combine(state, "settings.json");
            var destination = Path.Combine(root, "files"); Directory.CreateDirectory(destination);
            File.WriteAllText(path, JsonSerializer.Serialize(new { DownloadDirectory = destination, Theme = "Dark", FuturePreference = "keep" }));
            var settings = AppSettings.Load(state);
            Check(settings.DownloadDirectory == destination && settings.Theme == "Dark" && settings.MaxSegments == 8, "Legacy migration keeps directory/theme and new defaults");
            Check(settings.AdditionalSettings!["FuturePreference"].GetString() == "keep", "Unrelated persisted preferences preserved");
            foreach (string json in new[] { "{", "null", "[]" })
            {
                File.WriteAllText(path, json);
                Check(AppSettings.Load(state).MaxSegments == 8, "Malformed/root settings fallback: " + json);
            }
            File.WriteAllText(path, "{\"MaxSegments\":33,\"MaxConcurrentDownloads\":0,\"RetryCount\":9,\"RequestTimeoutSeconds\":301,\"Theme\":\"Dark\",\"ShowCompletionDialog\":false}");
            var bad = AppSettings.Load(state);
            Check(bad.MaxSegments == 8 && bad.MaxConcurrentDownloads == 3 && bad.RetryCount == 3 && bad.RequestTimeoutSeconds == 30, "Out-of-range values restore per-field defaults");
            Check(bad.Theme == "Dark" && !bad.ShowCompletionDialog, "Bad numeric values do not reset valid preferences");
            File.WriteAllText(path, "{\"MaxSegments\":\"oops\",\"RetryCount\":null,\"Theme\":\"Dark\",\"PlayCompletionSound\":true}");
            bad = AppSettings.Load(state);
            Check(bad.MaxSegments == 8 && bad.RetryCount == 3 && bad.Theme == "Dark" && bad.PlayCompletionSound, "Mistyped fields ignored independently");
            settings.Save(state);
            var copy = settings.Copy(); copy.MaxSegments = 12; copy.Save(state);
            Check(AppSettings.Load(state).MaxSegments == 12 && settings.MaxSegments == 8, "Atomic replacement and detached edit copy");
            settings.Save(state);
            var before = File.ReadAllText(path);
            var cancel = new SettingsWindow(settings, state);
            cancel.Loaded += (_, _) => { Edit(cancel); Click(cancel, "CancelButton"); };
            Check(cancel.ShowDialog() != true && settings.MaxSegments == 8 && settings.Theme == "Dark" && File.ReadAllText(path) == before, "Cancel leaves shared and persisted settings unchanged");
            var dialog = new SettingsWindow(settings, state);
            dialog.Loaded += (_, _) =>
            {
                Check(Field<TabControl>(dialog, "OptionsTabs").Items.Count == 6 && !Field<TabControl>(dialog, "OptionsTabs").Items.OfType<TabItem>().Any(i => (string)i.Header == "_Connection"), "Six real option sections (General, Appearance, Save To, Downloads, Proxy, Torrents); no Connection tab");
                var downloadsTab = Field<TabControl>(dialog, "OptionsTabs").Items.OfType<TabItem>().First(i => (string)i.Header == "_Downloads");
                var group = Field<GroupBox>(dialog, "ConnectionsGroup");
                Check((string)group.Header == "Connections" && LogicalUnder(group, (DependencyObject)downloadsTab.Content) && new[] { "Segments", "Concurrent", "Retries", "Timeout", "DynamicSegmentation" }.All(n => LogicalUnder(Field<FrameworkElement>(dialog, n), group)), "Connection settings live in the Downloads tab 'Connections' group");
                Check(Field<TextBox>(dialog, "Destination").Text == destination && ((ThemeCardModel)Field<ListBox>(dialog, "ThemeGallery").SelectedItem).Id == "Dark", "Compatible named controls initialize correctly");
                Check(Field<TextBox>(dialog, "Segments").Text == "8" && Field<TextBox>(dialog, "Concurrent").Text == "3" && Field<TextBox>(dialog, "Retries").Text == "3" && Field<TextBox>(dialog, "Timeout").Text == "30", "All numeric editors display persisted values");
                foreach (var item in Field<TabControl>(dialog, "OptionsTabs").Items.Cast<TabItem>())
                { item.IsSelected = true; dialog.UpdateLayout(); Check(item.Content is FrameworkElement element && element.ActualHeight > 0, "Tab renders: " + item.Header); }
                Field<TabControl>(dialog, "OptionsTabs").SelectedIndex = 0;
                Capture(dialog, Path.Combine(root, "options-general.png"));
                Field<TabControl>(dialog, "OptionsTabs").SelectedIndex = 3;
                Capture(dialog, Path.Combine(root, "options-downloads.png"));
                app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Dark.xaml") };
                Capture(dialog, Path.Combine(root, "options-downloads-dark.png"));
                Check(((SolidColorBrush)dialog.Background).Color == ((SolidColorBrush)app.Resources["CanvasBrush"]).Color, "Dark theme updates options window live");
                Edit(dialog);
                foreach (var entry in new[] { ("Segments", "0"), ("Segments", "33"), ("Concurrent", "17"), ("Retries", "-1"), ("Timeout", "0"), ("Timeout", "301"), ("Timeout", "abc") })
                {
                    var field = Field<TextBox>(dialog, entry.Item1); var valid = field.Text; field.Text = entry.Item2;
                    Click(dialog, "SaveButton");
                    Check(dialog.IsVisible && settings.MaxSegments == 8 && File.ReadAllText(path) == before && !string.IsNullOrWhiteSpace(Field<TextBlock>(dialog, "ErrorText").Text), "Validation rejects " + entry);
                    field.Text = valid;
                }
                Field<TextBox>(dialog, "Destination").Text = "relative"; Click(dialog, "SaveButton");
                Check(dialog.IsVisible && settings.MaxSegments == 8, "Invalid destination cannot commit edits");
                Field<TextBox>(dialog, "Destination").Text = path; Click(dialog, "SaveButton");
                Check(dialog.IsVisible && settings.MaxSegments == 8 && File.ReadAllText(path) == before, "Destination that is an existing file cannot commit edits");
                Field<TextBox>(dialog, "Destination").Text = destination;
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Click(dialog, "SaveButton");
                    Check(dialog.IsVisible && settings.MaxSegments == 8 && !string.IsNullOrEmpty(Field<TextBlock>(dialog, "ErrorText").Text), "Missing write access cannot mutate shared preferences");
                }
                Check(File.ReadAllText(path) == before && !Directory.EnumerateFiles(state, ".settings-*.tmp").Any(), "Failed replacement preserves old file and cleans temporary file");
                Click(dialog, "SaveButton");
            };
            Check(dialog.ShowDialog() == true, "Save closes modal successfully");
            var restored = AppSettings.Load(state);
            Check(settings.MaxSegments == 32 && settings.MaxConcurrentDownloads == 16 && settings.RetryCount == 0 && settings.RequestTimeoutSeconds == 300, "Save updates original shared instance in place");
            Check(!settings.EnableDynamicSegmentation && !settings.ShowCompletionDialog && settings.PlayCompletionSound, "All behavior checkboxes committed");
            Check(!settings.TrayShowActiveCount && !AppSettings.Load(state).TrayShowActiveCount, "Turning the tray badge off is committed and persisted");
            Check(restored.MaxSegments == 32 && !restored.ShowCompletionDialog && restored.PlayCompletionSound && restored.AdditionalSettings!["FuturePreference"].GetString() == "keep", "Round trip includes checkbox edits and unrelated fields");
            var options = OptionsHelper.CreateDownloadOptions(settings);
            Check(options.MaxSegments == 32 && options.MaxConcurrentDownloads == 16 && options.RetryCount == 0 && options.RequestTimeout == TimeSpan.FromSeconds(300) && !options.EnableDynamicSegmentation, "Engine snapshot maps every connection field");
            using (var manager = new DownloadManager(destination, Path.Combine(root, "engine"), options))
                Check(manager.Jobs.Count == 0, "Real engine accepts max bounds without any downloads");
            var blockedState = Path.Combine(root, "blocked"); File.WriteAllText(blockedState, "not a folder");
            var failed = new SettingsWindow(settings, blockedState);
            failed.Loaded += (_, _) => { Field<TextBox>(failed, "Segments").Text = "1"; Click(failed, "SaveButton"); Check(settings.MaxSegments == 32 && failed.IsVisible, "Uncreatable settings directory leaves instance untouched"); failed.Close(); };
            failed.ShowDialog();
            var minimum = new SettingsWindow(settings, state);
            minimum.Loaded += (_, _) =>
            {
                Field<TextBox>(minimum, "Segments").Text = "1"; Field<TextBox>(minimum, "Concurrent").Text = "1";
                Field<TextBox>(minimum, "Retries").Text = "8"; Field<TextBox>(minimum, "Timeout").Text = "1";
                Click(minimum, "SaveButton");
            };
            Check(minimum.ShowDialog() == true && AppSettings.Load(state).MaxSegments == 1 && settings.MaxConcurrentDownloads == 1 && settings.RetryCount == 8 && settings.RequestTimeoutSeconds == 1, "UI accepts remaining minimum/maximum numeric boundaries");
            // ---- 1.5.7: Options > Downloads > Identify as (Browser / NeoDM / Custom) ----
            Check(defaults.IdentifyAs == IdentityMode.Browser && defaults.CustomUserAgent == "" && AppSettings.CurrentSchema == 7, "Identify as defaults to Browser (schema 7)");
            var identityOptions = OptionsHelper.CreateDownloadOptions(defaults);
            Check(identityOptions.Identity == IdentityMode.Browser && identityOptions.CustomUserAgent is null, "Engine options carry the browser identity by default");
            string identityDir = Path.Combine(root, "identity"); Directory.CreateDirectory(identityDir);
            void Write(string json) => File.WriteAllText(Path.Combine(identityDir, "settings.json"), json);
            Write("{\"SettingsSchema\":5,\"Theme\":\"Dark\",\"MaxSegments\":6}");
            var migrated = AppSettings.Load(identityDir);
            Check(migrated.IdentifyAs == IdentityMode.Browser && migrated.SettingsSchema == 7 && migrated.Theme == "Dark" && migrated.MaxSegments == 6, "A schema 5 file migrates to Identify as = Browser and keeps its other values");
            Write("{\"SettingsSchema\":5,\"IdentifyAs\":\"NeoDM\"}");
            Check(AppSettings.Load(identityDir).IdentifyAs == IdentityMode.Browser, "The one-time migration sets Browser");
            Write("{\"SettingsSchema\":6,\"IdentifyAs\":\"NeoDM\"}");
            Check(AppSettings.Load(identityDir).IdentifyAs == IdentityMode.NeoDM, "An explicit NeoDM choice at schema 6 is kept");
            Write("{\"SettingsSchema\":6,\"IdentifyAs\":\"Custom\",\"CustomUserAgent\":\" MyAgent/2 \"}");
            var custom = AppSettings.Load(identityDir);
            Check(custom.IdentifyAs == IdentityMode.Custom && custom.CustomUserAgent == "MyAgent/2" && OptionsHelper.CreateDownloadOptions(custom).CustomUserAgent == "MyAgent/2", "A custom User-Agent is kept, trimmed and handed to the engine");
            Write("{\"SettingsSchema\":6,\"IdentifyAs\":\"Custom\",\"CustomUserAgent\":\"a\\u0001b\"}");
            Check(AppSettings.Load(identityDir) is { IdentifyAs: IdentityMode.Browser, CustomUserAgent: "" }, "A custom User-Agent with control characters is dropped and the choice falls back to Browser");
            Write("{\"SettingsSchema\":6,\"IdentifyAs\":\"Custom\",\"CustomUserAgent\":\"\"}");
            Check(AppSettings.Load(identityDir).IdentifyAs == IdentityMode.Browser, "Custom without text falls back to Browser");
            Write("{\"SettingsSchema\":6,\"IdentifyAs\":\"Nonsense\"}");
            Check(AppSettings.Load(identityDir).IdentifyAs == IdentityMode.Browser, "An unknown value falls back to Browser");
            var identitySettings = AppSettings.Load(state);
            var identityDialog = new SettingsWindow(identitySettings, state);
            identityDialog.Loaded += (_, _) =>
            {
                var downloadsTab = Field<TabControl>(identityDialog, "OptionsTabs").Items.OfType<TabItem>().First(i => (string)i.Header == "_Downloads");
                var box = Field<ComboBox>(identityDialog, "IdentifyBox"); var text = Field<TextBox>(identityDialog, "CustomAgent");
                Check(LogicalUnder(box, (DependencyObject)downloadsTab.Content) && LogicalUnder(text, (DependencyObject)downloadsTab.Content) && (string)Field<GroupBox>(identityDialog, "IdentityGroup").Header == "Identify as", "The Identify as group is in the Downloads tab");
                Check(box.Items.Cast<ComboBoxItem>().Select(i => (string)i.Content).SequenceEqual(new[] { "Browser (recommended)", "NeoDM", "Custom" }), "Choices: Browser (recommended), NeoDM, Custom");
                Check(((ComboBoxItem)box.SelectedItem).Tag is IdentityMode.Browser && !text.IsEnabled, "Browser is selected and the text box is off");
                box.SelectedIndex = 2; Check(text.IsEnabled, "Custom turns the text box on");
                text.Text = "bad\u0001agent"; Click(identityDialog, "SaveButton");
                Check(identityDialog.IsVisible && identitySettings.IdentifyAs == IdentityMode.Browser && !string.IsNullOrWhiteSpace(Field<TextBlock>(identityDialog, "ErrorText").Text), "A custom User-Agent with a control character is refused");
                text.Text = ""; Click(identityDialog, "SaveButton");
                Check(identityDialog.IsVisible && identitySettings.IdentifyAs == IdentityMode.Browser, "Custom without text is refused");
                text.MaxLength = 600; text.Text = new string('a', 513); Click(identityDialog, "SaveButton");
                Check(identityDialog.IsVisible && identitySettings.IdentifyAs == IdentityMode.Browser, "513 characters are refused");
                text.Text = "MyAgent/3 (test)"; Click(identityDialog, "SaveButton");
            };
            Check(identityDialog.ShowDialog() == true && identitySettings.IdentifyAs == IdentityMode.Custom && identitySettings.CustomUserAgent == "MyAgent/3 (test)" && AppSettings.Load(state).CustomUserAgent == "MyAgent/3 (test)", "A valid custom User-Agent is saved and persisted");
            Check(OptionsHelper.CreateDownloadOptions(identitySettings).Identity == IdentityMode.Custom && OptionsHelper.CreateDownloadOptions(identitySettings).CustomUserAgent == "MyAgent/3 (test)", "The engine receives Custom with its text");
            var neodmDialog = new SettingsWindow(identitySettings, state);
            neodmDialog.Loaded += (_, _) =>
            {
                var box = Field<ComboBox>(neodmDialog, "IdentifyBox");
                Check(((ComboBoxItem)box.SelectedItem).Tag is IdentityMode.Custom && Field<TextBox>(neodmDialog, "CustomAgent").Text == "MyAgent/3 (test)", "The saved choice and text come back");
                box.SelectedIndex = 1; Click(neodmDialog, "SaveButton");
            };
            Check(neodmDialog.ShowDialog() == true && identitySettings.IdentifyAs == IdentityMode.NeoDM && OptionsHelper.CreateDownloadOptions(identitySettings).CustomUserAgent is null, "NeoDM can be chosen; its engine options carry no custom text");
            Console.WriteLine($"ALL {passed} OPTIONS TESTS PASSED");
            Console.WriteLine("Isolated state and render captures: " + root);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close(); app.Shutdown(); }
    }
}
