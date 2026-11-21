// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>2.0.0 stage S4a, the seven tab tweaks: smart names, speed and time left, close finished tabs, groups, Hide or Cancel only, the 'Show downloads as tabs?' question,
/// the strip filter and the list for 20 or more tabs. Real tabbed window, real manager, a loopback server for the downloads.</summary>
internal static partial class V2000Tests
{
    /// <summary>/ok = a small file, /slow = a file that trickles (stays Downloading), anything else = 403 (Failed).</summary>
    private sealed class TabServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        public string Url(string path) => "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + path;
        public TabServer()
        {
            listener.Start();
            _ = Task.Run(async () =>
            {
                try { while (!stop.IsCancellationRequested) { var c = await listener.AcceptTcpClientAsync(stop.Token); _ = Task.Run(() => Serve(c)); } }
                catch (Exception) { }
            });
        }
        private async Task Serve(TcpClient c)
        {
            using (c)
            {
                try
                {
                    var s = c.GetStream(); var buf = new byte[4096]; int n = await s.ReadAsync(buf);
                    var line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];
                    if (line.Contains(" /ok")) { await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 20000\r\nConnection: close\r\n\r\n")); await s.WriteAsync(new byte[20000]); }
                    else if (line.Contains(" /slow"))
                    {
                        await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 50000000\r\nConnection: close\r\n\r\n"));
                        var chunk = new byte[8192];
                        while (!stop.IsCancellationRequested) { await s.WriteAsync(chunk, stop.Token); await Task.Delay(40, stop.Token); }
                    }
                    else await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                }
                catch (Exception) { }
            }
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); }
    }

    private static string ShotsFolder()
    {
        var folder = Environment.GetEnvironmentVariable("NEODM_SHOTS") ?? @"F:\NDM\artifacts\s4a-shots";
        try { Directory.CreateDirectory(folder); return folder; } catch (Exception) { return ""; }
    }
    private static void Shot(Window window, string name)
    {
        var folder = ShotsFolder(); if (folder.Length == 0) return;
        var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(window);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using var file = File.Create(Path.Combine(folder, name + ".png")); enc.Save(file);
    }
    private static DownloadManager TabManager(string scratch, out string root, int concurrent = 12)
    {
        root = Path.Combine(scratch, "tab-" + Guid.NewGuid().ToString("N"));
        return new DownloadManager(Path.Combine(root, "files"), Path.Combine(root, "state"), new DownloadOptions { MaxConcurrentDownloads = concurrent, MaxSegments = 1, RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(5) });
    }
    private static void WaitStatus(DownloadManager m, string id, string status, int seconds = 15)
    {
        for (int i = 0; i < seconds * 20 && m.Jobs.Single(j => j.Id == id).Status != status; i++) Pump(50);
        if (m.Jobs.Single(j => j.Id == id).Status != status) throw new Exception($"FAIL: {id} did not reach {status} (it is {m.Jobs.Single(j => j.Id == id).Status})");
    }

    private static void TabRulesPure()
    {
        const string a = "GLM-5.3-Flash-UD-IQ4_NL-00002-of-00005.gguf", b = "GLM-5.3-Flash-UD-IQ4_NL-00003-of-00005.gguf", c = "GLM-5.3-Flash-UD-IQ4_NL-00004-of-00005.gguf";
        var labels = TabRules.Labels(new[] { a, b, c }, 22);
        Check(labels.Distinct().Count() == 3 && labels.All(l => l.Length <= 22 && l.Contains('…')), "three parts of one file get three different labels that fit: " + string.Join(" | ", labels));
        Check(labels[0].Contains("2-of") && labels[1].Contains("3-of") && labels[2].Contains("4-of"), "each label shows its own part number");
        Check(labels.All(l => l.EndsWith(".gguf") || l.EndsWith("-of-00005.gguf") || l.Contains("of-00005")), "the end of the name (extension and total) is kept");
        Check(TabRules.Labels(new[] { "short.zip", "other.iso" }, 22).SequenceEqual(new[] { "short.zip", "other.iso" }), "a name that fits is shown whole");
        var alone = TabRules.Labels(new[] { "A-very-long-single-name-without-neighbours-2026.tar.gz" }, 24)[0];
        Check(alone.Length <= 24 && alone.EndsWith(".tar.gz") && alone.StartsWith("A-very"), "a long name alone keeps its start and its end: " + alone);
        var same = TabRules.Labels(new[] { a, a }, 22);
        Check(same[0] == same[1] && same[0].Length <= 22, "identical names stay identical (nothing to tell apart)");
        var narrow = TabRules.Labels(new[] { a, b }, 8);
        Check(narrow.All(l => l.Length <= 8 && l.Length > 0) && narrow[0] != narrow[1], "a tiny budget still gives a short label that tells the parts apart: " + string.Join(" | ", narrow));
        Check(TabRules.CharBudget(230) > TabRules.CharBudget(128) && TabRules.CharBudget(10) >= 8, "the budget follows the tab width");
        Check(TabRules.Detail("Downloading", "3.2 MB/s", "4 min", (s, l) => s + "|" + l, l => "L" + l) == "3.2 MB/s|4 min", "speed and time left under the percentage");
        Check(TabRules.Detail("Downloading", "—", "4 min", (s, l) => s + "|" + l, l => "L" + l) == "L4 min" && TabRules.Detail("Downloading", "1 MB/s", "—", (s, l) => "x", l => "y") == "1 MB/s", "only what is known is shown");
        Check(TabRules.Detail("Completed", "1 MB/s", "4 min", (s, l) => "x", l => "y") == "" && TabRules.Detail("Failed", "1", "2", (s, l) => "x", l => "y") == "", "nothing under a finished or failed tab");
        Check(TabRules.GroupOf("Pack", QueueIds.Main, null) == "p:Pack" && TabRules.GroupOf(null, QueueIds.Main, "Main") is null && TabRules.GroupOf(null, "q1", "Night") == "q:Night" && TabRules.GroupOf("", "q1", null) == "q:q1", "a package, else a queue other than Main, makes the group");
        Check(TabRules.GroupColor("p:Pack") == TabRules.GroupColor("p:Pack") && Enumerable.Range(0, 40).Select(i => TabRules.GroupColor("p:" + i)).Distinct().Count() >= 4, "colours are stable and spread over the slots");
        Check(TabRules.Matches(TabFilter.Active, "Downloading") && TabRules.Matches(TabFilter.Active, "Paused") && TabRules.Matches(TabFilter.Active, "Queued") && !TabRules.Matches(TabFilter.Active, "Completed") && !TabRules.Matches(TabFilter.Active, "Failed"), "Active = not finished and not failed");
        Check(TabRules.Matches(TabFilter.Failed, "Failed") && !TabRules.Matches(TabFilter.Failed, "Downloading") && TabRules.Matches(TabFilter.All, "Completed"), "Failed and All");
        Check(TabRules.IsFinished("Completed") && TabRules.IsFinished("Seeding") && !TabRules.IsFinished("Failed") && !TabRules.IsFinished("Paused"), "failed and paused tabs are never 'finished'");
        Check(TabRules.ClampCloseSeconds(1) == 5 && TabRules.ClampCloseSeconds(999999) == 3600 && TabRules.ClampCloseSeconds(30) == 30, "the close time is bounded");
        Check(TabRules.OfferDue(false, 3, 3) && !TabRules.OfferDue(false, 2, 3) && !TabRules.OfferDue(true, 9, 3) && !TabRules.OfferDue(false, 9, -1), "the question comes at the third download, never when tabs are on or it was refused for good");
        Check(TabRules.NextOffer(3, true, false) == -1 && TabRules.NextOffer(3, false, true) == -1 && TabRules.NextOffer(3, false, false) == 13, "yes or 'don't ask again' ends the question; 'not now' asks once more ten downloads later");
        var m = new DownloadTabsModel();
        m.Add("p1", true, "p:A"); m.Add("x"); m.Add("p2", true, "p:A"); m.Add("q1", true, "q:B"); m.Add("p3", true, "p:A");
        Check(m.Ids.SequenceEqual(new[] { "p1", "p2", "p3", "x", "q1" }), "tabs of one group sit together: " + string.Join(",", m.Ids));
        Check(m.GroupOf("p2") == "p:A" && m.GroupOf("x") is null, "the model knows each tab's group");
        m.Remove("p2"); Check(m.GroupOf("p2") is null, "a closed tab leaves its group");
    }

    private static void TabsWindowTweaks(string scratch)
    {
        using var server = new TabServer();
        using var manager = TabManager(scratch, out var root);
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "files"), ShowDownloadsAsTabs = true };
        DownloadJob Add(string path, string name, string? package = null) => manager.AddAsync(new DownloadRequest(server.Url(path), name, Package: package)).GetAwaiter().GetResult();
        var p1 = Add("/slow", "GLM-5.3-Flash-UD-IQ4_NL-00002-of-00005.gguf", "GLM pack");
        var done = Add("/ok", "small-report.pdf");
        var p2 = Add("/slow", "GLM-5.3-Flash-UD-IQ4_NL-00003-of-00005.gguf", "GLM pack");
        var bad = Add("/refused", "expired-link.bin");
        var p3 = Add("/slow", "GLM-5.3-Flash-UD-IQ4_NL-00004-of-00005.gguf", "GLM pack");
        WaitStatus(manager, done.Id, "Completed"); WaitStatus(manager, bad.Id, "Failed"); WaitStatus(manager, p3.Id, "Downloading");
        var now = DateTime.UtcNow;
        var window = new DownloadTabsWindow(manager, null, settings, () => { }) { Width = 1000, Height = 640, Left = 20, Top = 20, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, Clock = () => now };
        window.Show();
        foreach (var id in new[] { p1.Id, done.Id, p2.Id, bad.Id, p3.Id }) window.AddOrSelect(id);
        window.Model.Select(p1.Id); window.LayoutTabs(); Pump(900);

        var buttons = window.TabButtons;
        Check(window.Model.Ids.Take(3).SequenceEqual(new[] { p1.Id, p2.Id, p3.Id }), "the three parts of one package sit together although other downloads were opened between them");
        var partLabels = buttons.Take(3).Select(b => b.FileText).ToArray();
        Check(partLabels.Distinct().Count() == 3 && partLabels[0].Contains("2-of") && partLabels[1].Contains("3-of") && partLabels[2].Contains("4-of"), "the real tabs show the part numbers: " + string.Join(" | ", partLabels));
        Check(buttons.Take(3).All(b => b.ToolTip is string tip && tip.Contains("GLM-5.3-Flash-UD-IQ4_NL-0000") && tip.Contains("GLM pack")), "the tooltip carries the whole name and the package");
        Check(buttons.Take(3).All(b => b.GroupMarkVisible && b.GroupKey == "p:GLM pack") && buttons.Take(3).Select(b => b.GroupSlot).Distinct().Count() == 1 && !buttons[3].GroupMarkVisible && !buttons[4].GroupMarkVisible, "the package tabs carry one colour mark, the others none");
        var downloading = buttons[0];
        for (int i = 0; i < 100 && downloading.DetailText.Length == 0; i++) Pump(100);
        Check(downloading.DetailText.Contains("/s") || downloading.DetailText.Contains("left"), "a running tab shows its speed or time left under the percentage: '" + downloading.DetailText + "'");
        Check(buttons.Single(b => b.JobId == done.Id).DetailText == "" && buttons.Single(b => b.JobId == bad.Id).DetailText == "", "a finished or failed tab shows no speed line");
        var detail = Find<TextBlock>(downloading, "PART_Detail");
        Check(detail is not null && detail.Visibility == Visibility.Visible && detail.ActualHeight > 0, "the detail line is drawn");

        // the strip filter
        var tools = (Border)window.FindName("ToolsBorder");
        Check(tools.Visibility == Visibility.Visible, "the filter row shows with several tabs");
        window.SetFilter(TabFilter.Failed); Pump(100);
        Check(window.VisibleIds.OrderBy(x => x).SequenceEqual(new[] { bad.Id, p1.Id }.OrderBy(x => x)), "Failed shows the failed tab and keeps the selected one in view");
        window.SetFilter(TabFilter.Active); Pump(100);
        Check(window.VisibleIds.OrderBy(x => x).SequenceEqual(new[] { p1.Id, p2.Id, p3.Id }.OrderBy(x => x)), "Active shows the running tabs only");
        window.SetFilter(TabFilter.All); Pump(100);
        Check(window.VisibleIds.Count == 5, "All shows every tab again");

        // close completed tabs: only finished ones go
        var closeButton = (Button)window.FindName("CloseCompletedButton");
        Check(closeButton.Visibility == Visibility.Visible, "the Close completed tabs button shows while a tab is finished");
        Check(window.CloseCompletedTabs() == 1 && !window.HasTab(done.Id) && window.HasTab(bad.Id) && window.HasTab(p1.Id), "Close completed tabs closes the finished tab and keeps the failed and running ones");
        Pump(300);
        Check(closeButton.Visibility == Visibility.Collapsed, "the button hides when no tab is finished");

        // the automatic close
        var done2 = Add("/ok", "second-report.pdf"); WaitStatus(manager, done2.Id, "Completed");
        settings.AutoCloseFinishedTabs = true; settings.AutoCloseTabSeconds = 20;
        window.AddOrSelect(done2.Id); Pump(700);
        Check(window.HasTab(done2.Id), "a finished tab stays until its time is up");
        now = now.AddSeconds(25); Pump(900);
        Check(!window.HasTab(done2.Id), "after the chosen seconds the finished tab closes by itself");
        now = now.AddHours(2); Pump(900);
        Check(window.HasTab(bad.Id) && window.HasTab(p1.Id), "failed and running tabs never close by themselves");
        settings.AutoCloseFinishedTabs = false;
        var done3 = Add("/ok", "third-report.pdf"); WaitStatus(manager, done3.Id, "Completed"); window.AddOrSelect(done3.Id); Pump(700); now = now.AddHours(2); Pump(900);
        Check(window.HasTab(done3.Id), "with the option off a finished tab stays");

        // screenshots of the real states
        window.Model.Select(p2.Id); Pump(500);
        Shot(window, "tabs-Light-en");
        foreach (var (theme, lang, width, tag) in new[] { ("DeepNavy", "en", 1000.0, "tabs-DeepNavy-en"), ("Light", "ar", 1000.0, "tabs-Light-ar-rtl"), ("DeepNavy", "en", 460.0, "tabs-DeepNavy-narrow"), ("Light", "de", 460.0, "tabs-Light-de-narrow") })
        {
            Loc.SetLanguage(lang);
            Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/NeoDM;component/Themes/{theme}.xaml") };
            window.FlowDirection = Loc.FlowDirection; window.Width = width; window.LayoutTabs(); Pump(900);
            var labelsNow = window.TabButtons.Select(b => b.FileText).ToArray();
            Check(window.TabButtons.Count(b => b.Visibility == Visibility.Visible) >= 3 && labelsNow.All(l => l.Length > 0), tag + ": the tabs are drawn");
            var strip = (Border)window.FindName("StripBorder"); var toolsNow = (Border)window.FindName("ToolsBorder");
            Check(toolsNow.ActualWidth > 0 && toolsNow.ActualWidth <= window.ActualWidth + 1, tag + ": the filter row fits the window (" + toolsNow.ActualWidth + " of " + window.ActualWidth + ")");
            Shot(window, tag);
        }
        Loc.SetLanguage("en"); Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") };
        window.FlowDirection = FlowDirection.LeftToRight;

        // closing a running tab: only Hide or Cancel, the choice can be remembered
        var closeWindow = new TabCloseWindow("x.bin", 1);
        Check(closeWindow.FindName("KeepButton") is null && closeWindow.FindName("HideButton") is Button && closeWindow.FindName("CancelDownloadButton") is Button && closeWindow.FindName("RememberBox") is CheckBox, "the question has Hide, Cancel and Remember my choice, and no Keep open button");
        TabCloseWindow.Override = (_, _, _) => new TabCloseAnswer(TabCloseAction.Hide, true);
        window.AddOrSelect(p3.Id);
        var closed = window.RequestCloseTabAsync(p3.Id).GetAwaiter().GetResult();
        TabCloseWindow.Override = null;
        Check(closed && !window.HasTab(p3.Id) && settings.TabCloseChoice == "hide", "Hide closes the tab and remembers the choice");
        TabCloseWindow.Override = (_, _, _) => null;
        Check(!window.RequestCloseTabAsync(p1.Id).GetAwaiter().GetResult() == false || window.HasTab(p1.Id), "leaving the question without an answer keeps the tab");
        TabCloseWindow.Override = null; settings.TabCloseChoice = "";
        window.ForceClose();
    }
    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T hit && hit.Name == name) return hit;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Find<T>(VisualTreeHelper.GetChild(root, i), name) is { } found) return found;
        return null;
    }

    private static void TabsOverflow(string scratch)
    {
        using var manager = TabManager(scratch, out var root, 1);
        manager.PauseQueue(QueueIds.Main, false); // a held queue keeps every download Queued: nothing touches the network
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "files"), ShowDownloadsAsTabs = true };
        var ids = Enumerable.Range(1, 22).Select(i => manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/n" + i + ".bin", "archive-name-" + i + ".bin"), null).GetAwaiter().GetResult().Id).ToList();
        var window = new DownloadTabsWindow(manager, null, settings, () => { }) { Width = 900, Height = 600, Left = 20, Top = 20, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        window.Show();
        var overflow = (Button)window.FindName("OverflowButton");
        for (int i = 0; i < 19; i++) window.AddOrSelect(ids[i]);
        window.LayoutTabs(); Pump(500);
        Check(overflow.Visibility == Visibility.Collapsed, "19 tabs: no list button yet");
        window.AddOrSelect(ids[19]); window.LayoutTabs(); Pump(500);
        Check(overflow.Visibility == Visibility.Visible, "20 tabs: the list of all tabs appears");
        window.AddOrSelect(ids[20]); window.AddOrSelect(ids[21]); window.LayoutTabs(); Pump(500);
        var entries = window.OverflowEntries();
        Check(entries.Count == 22 && entries[0].Text.Contains("archive-name-1.bin") && entries[21].Text.Contains("archive-name-22.bin"), "the list names every tab with its state");
        Check(window.ScrollButtonsVisible, "the strip scrolls with so many tabs");
        window.SetFilter(TabFilter.Failed); Pump(200);
        Check(window.VisibleIds.Count == 1, "with nothing failed only the selected tab stays in the strip");
        window.Model.Select(ids[3]); window.SetFilter(TabFilter.All); Pump(300);
        Check(window.Model.SelectedId == ids[3] && window.VisibleIds.Count == 22, "choosing from the list brings the tab back");
        Shot(window, "tabs-overflow-22");
        window.ForceClose();
    }

    private static void TabsOffer(string scratch)
    {
        using var manager = TabManager(scratch, out var root, 1);
        manager.PauseQueue(QueueIds.Main, false);
        var saves = 0;
        TabsOfferAnswer? Ask(Func<TabsOfferAnswer?> answer, out int asked, Action<AppSettings>? tune = null, int downloads = 3)
        {
            var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "files") }; tune?.Invoke(settings);
            int calls = 0; TabsOfferAnswer? given = null;
            TabsOfferWindow.Override = _ => { calls++; given = answer(); return given; };
            var window = new MainWindow(manager, settings, () => saves++, enableTray: false) { ShowInTaskbar = false, Width = 900, Height = 600 };
            window.Show(); Pump(400);
            for (int i = 0; i < downloads; i++) { manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/o" + Guid.NewGuid().ToString("N") + ".bin", "offer-" + i + ".bin"), null).GetAwaiter().GetResult(); Pump(700); }
            Pump(600);
            asked = calls; LastSettings = settings; window.Close(); TabsOfferWindow.Override = null;
            return given;
        }
        Ask(() => new(true, false), out var asked);
        Check(asked == 1 && LastSettings.ShowDownloadsAsTabs && LastSettings.TabsOfferAt == -1 && LastSettings.DownloadsStarted == 3, "after the third download the question comes once; Yes turns the tabs on and ends it");
        Ask(() => new(false, true), out asked);
        Check(asked == 1 && !LastSettings.ShowDownloadsAsTabs && LastSettings.TabsOfferAt == -1, "No with 'Don't ask again' never asks again");
        Ask(() => new(false, false), out asked);
        Check(asked == 1 && !LastSettings.ShowDownloadsAsTabs && LastSettings.TabsOfferAt == LastSettings.DownloadsStarted + 10, "plain No asks once more ten downloads later");
        Ask(() => new(true, false), out asked, s => s.DownloadsStarted = 2, downloads: 1);
        Check(asked == 1, "the count carries over: the third download asks even when the first two were earlier runs");
        Ask(() => new(true, false), out asked, downloads: 2);
        Check(asked == 0 && LastSettings.DownloadsStarted == 2, "two downloads do not ask");
        Ask(() => new(true, false), out asked, s => s.ShowDownloadsAsTabs = true);
        Check(asked == 0, "with tabs already on nothing is asked");
        Ask(() => new(true, false), out asked, s => s.TabsOfferAt = -1);
        Check(asked == 0, "a refused-for-good question stays quiet");
        Ask(() => new(false, false), out asked, s => { s.DownloadsStarted = 12; s.TabsOfferAt = 13; }, downloads: 1);
        Check(asked == 1, "the second chance comes at the planned count");
        Check(saves > 3, "the answers were saved");
        // the window has the three controls and is localized
        var offer = new TabsOfferWindow();
        Check(offer.FindName("YesButton") is Button && offer.FindName("NoButton") is Button && offer.FindName("NeverBox") is CheckBox, "the question has Show as tabs, Not now and Don't ask again");
        foreach (var lang in new[] { "ar", "de", "en", "es", "fr", "ja", "pt-BR", "ru", "tr", "zh-Hans" })
        {
            Loc.SetLanguage(lang);
            foreach (var key in new[] { "tabs.offer.title", "tabs.offer.message", "tabs.offer.yes", "tabs.offer.no", "tabs.offer.never", "tabs.filter.all", "tabs.filter.active", "tabs.filter.failed", "tabs.filter.a11y", "tabs.close_completed", "tabs.overflow", "tabs.group_package", "tabs.group_queue", "tabs.speed_left", "tabs.left_only", "options.chk.tabs_autoclose", "options.a11y.tabs_autoclose_seconds", "options.label.seconds_unit" })
                if (Loc.T(key) == key) throw new Exception($"FAIL: {lang} is missing {key}");
        }
        Loc.SetLanguage("en");
        Check(true, "every new tab text exists in ten languages");
    }
    private static AppSettings LastSettings = new();

    private static void TabsOptions(string scratch)
    {
        var state = Path.Combine(scratch, "tabopt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(state);
        var settings = AppSettings.Load(state);
        Check(!settings.AutoCloseFinishedTabs && settings.AutoCloseTabSeconds == 30 && settings.TabsOfferAt == 3 && settings.DownloadsStarted == 0, "defaults: automatic close off at 30 seconds, the question due at the third download");
        var w = new SettingsWindow(settings, state) { Width = 760, Height = 800, Left = 30, Top = 30, WindowStartupLocation = WindowStartupLocation.Manual };
        w.Show(); Pump(300);
        Check(Field<CheckBox>(w, "AutoCloseTabsBox").IsChecked == false && Field<TextBox>(w, "AutoCloseSecondsBox").Text == "30", "Options shows the automatic close, off, 30 seconds");
        Field<CheckBox>(w, "AutoCloseTabsBox").IsChecked = true; Field<TextBox>(w, "AutoCloseSecondsBox").Text = "90";
        Click(w, "SaveButton");
        var back = AppSettings.Load(state);
        Check(back.AutoCloseFinishedTabs && back.AutoCloseTabSeconds == 90, "the choice is saved");
        var w2 = new SettingsWindow(back, state) { Width = 760, Height = 800, Left = 30, Top = 30, WindowStartupLocation = WindowStartupLocation.Manual };
        w2.Show(); Pump(300);
        Field<TextBox>(w2, "AutoCloseSecondsBox").Text = "1";
        Click(w2, "SaveButton");
        Check(AppSettings.Load(state).AutoCloseTabSeconds == 5, "a time that is too short is raised to 5 seconds");
        var odd = Path.Combine(state, "odd"); Directory.CreateDirectory(odd);
        File.WriteAllText(Path.Combine(odd, "settings.json"), "{\"SettingsSchema\":12,\"AutoCloseTabSeconds\":999999,\"DownloadsStarted\":-4,\"TabsOfferAt\":-9}");
        var fixedUp = AppSettings.Load(odd);
        Check(fixedUp.AutoCloseTabSeconds == 3600 && fixedUp.DownloadsStarted == 0 && fixedUp.TabsOfferAt == 3, "odd saved values are corrected on load");
        var copy = new AppSettings(); copy.CopyFrom(new AppSettings { AutoCloseFinishedTabs = true, AutoCloseTabSeconds = 12, DownloadsStarted = 7, TabsOfferAt = 17 });
        Check(copy.AutoCloseFinishedTabs && copy.AutoCloseTabSeconds == 12 && copy.DownloadsStarted == 7 && copy.TabsOfferAt == 17, "CopyFrom carries the new settings");
    }
}
