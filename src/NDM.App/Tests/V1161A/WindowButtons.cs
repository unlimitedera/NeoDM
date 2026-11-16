using System.Windows;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static partial class V1161ATests
{
    /// <summary>A scripted page that also offers the window buttons; its load can be made to never finish.</summary>
    private sealed class NavSession : IPageSession, IPageNavigation
    {
        public TaskCompletionSource<bool> Gate = new(); public bool Hang; public int Reloads, Backs, Stops;
        private string _url = "";
        public string? CurrentAddress => _url.Length > 0 ? _url : null; public bool IsClosed { get; private set; }
        public event Action? UserClosed; public event Action? NavigationChanged;
        public bool CanGoBack => true; public bool CanGoForward => false; public bool Loading => Hang;
        public void GoBack() => Backs++; public void GoForward() { } public void Reload() => Reloads++; public void StopLoading() => Stops++;
        public void CloseByPerson() { IsClosed = true; UserClosed?.Invoke(); }
        public Task LoadAsync(string url, CancellationToken token) { _url = url; NavigationChanged?.Invoke(); return Hang ? Gate.Task : Task.CompletedTask; }
        public Task<PageSnapshot> ScanAsync(CancellationToken token) => Task.FromResult(new PageSnapshot(_url, false, "reCAPTCHA", Array.Empty<PageControl>(), null, false));
        public Task<bool> ClickAsync(string controlId, CancellationToken token) => Task.FromResult(false);
        public BrowserGrab? TakeCapture() => null; public string? TakeRefusal() => null; public void NoteClick() { } public void Present(bool front) { } public void SetHumanMessage(string key) { }
        public void Dispose() { }
    }

    private static void WindowButtons()
    {
        var opened = new List<string>(); var copied = new List<string>();
        PageWindowActions.Launcher = u => opened.Add(u); PageWindowActions.ClipboardSetter = t => copied.Add(t);

        // 1) page-links window: every button is there, with plain labels, and works on the link on screen
        var nav = new NavSession { Hang = true };
        var co = new PageResolveCoordinator(() => nav, (_, _) => Task.CompletedTask, new PageResolveEngine { Tick = TimeSpan.FromMilliseconds(30) });
        var item = Item("https://datanodes.example/abc123/Part_1.zip", "Part_1.zip"); co.Add(new[] { item });
        var win = new PageLinksWindow(co, "Pack") { Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual }; win.Show(); Pump(150);
        Check((string)win.BackBtn.Content == "Back" && (string)win.ForwardBtn.Content == "Forward" && (string)win.ReloadBtn.Content == "Reload" && (string)win.StopBtn.Content == "Stop"
            && (string)win.OpenBrowserBtn.Content == "Open in your browser" && (string)win.CopyBtn.Content == "Copy address", "page-links window: Back, Forward, Reload, Stop, Open in your browser, Copy address");
        Check(win.CloseBtn.IsEnabled && (string)win.CloseBtn.Content == "_Close", "page-links window: the Close button is always enabled");
        Task run = Task.CompletedTask; Application.Current.Dispatcher.Invoke(() => run = co.RunAsync());
        PumpUntil(() => item.State == PageLinkState.Loading && co.Navigation is not null, "link is loading in the NeoDM browser window", 5000); Pump(150);
        Check(win.BackBtn.IsEnabled && !win.ForwardBtn.IsEnabled && win.ReloadBtn.IsEnabled && win.StopBtn.IsEnabled && win.OpenBrowserBtn.IsEnabled && win.CopyBtn.IsEnabled, "buttons enable from the page state while the page loads");
        Click(win.ReloadBtn); Click(win.BackBtn); Click(win.StopBtn); Check(nav.Reloads == 1 && nav.Backs == 1 && nav.Stops == 1, "Reload, Back and Stop reach the page");
        Click(win.OpenBrowserBtn); Click(win.CopyBtn);
        Check(opened.SequenceEqual(new[] { "https://datanodes.example/abc123/Part_1.zip" }) && copied.SequenceEqual(new[] { "https://datanodes.example/abc123/Part_1.zip" }), "Open in your browser and Copy address use the page address");
        Check(!PageWindowActions.OpenInBrowser("file:///c:/windows/system32/calc.exe") && !PageWindowActions.OpenInBrowser("javascript:alert(1)") && opened.Count == 1, "only web addresses are opened in the browser");
        // 2) watchdog: a page that never loads does not hold the resolver: Skip this link ends it at once
        co.SkipCurrent(); PumpUntil(() => run.IsCompleted, "skip ends a link whose page never answers", 5000);
        Check(item.State == PageLinkState.Skipped, "watchdog: Skip works while the page never answers");

        // 3) close during a human step: the window closing skips the link, the next one carries on, cancel ends the run
        var nav2 = new NavSession { Hang = false }; var it2 = Item("https://datanodes.example/z/Part_2.zip", "Part_2.zip"); var it3 = Item("https://datanodes.example/z/Part_3.zip", "Part_3.zip");
        var co2 = new PageResolveCoordinator(() => nav2, (_, _) => Task.CompletedTask, new PageResolveEngine { Tick = TimeSpan.FromMilliseconds(30) }); co2.Add(new[] { it2, it3 });
        var win2 = new PageLinksWindow(co2) { Left = 20, Top = 20, WindowStartupLocation = WindowStartupLocation.Manual }; win2.Show(); Pump(100);
        Task run2 = Task.CompletedTask; Application.Current.Dispatcher.Invoke(() => run2 = co2.RunAsync());
        PumpUntil(() => it2.State == PageLinkState.NeedsYou, "link waits for the person", 5000); Pump(200);
        win2.Close(); PumpUntil(() => it2.State == PageLinkState.Skipped, "closing the window while the link needs the person skips it", 5000);
        PumpUntil(() => it3.State == PageLinkState.NeedsYou, "the next link carries on", 5000); co2.CancelAll(); PumpUntil(() => run2.IsCompleted, "cancel ends the run", 5000);
        Check(it3.State == PageLinkState.Cancelled, "close during a human step never leaves the resolver hung");
        win.Close();

        // 4) the real NeoDM browser window with a fixture page and a private profile
        using var host = new XfsHost { Countdown = 30 }; var data = Path.Combine(Path.GetTempPath(), "NDM-wb-" + Guid.NewGuid().ToString("N"));
        PageResolverWindow? real = null; var it4 = Item(host.FileUrl("wb0001"), "W.zip");
        var co3 = new PageResolveCoordinator(() => real = new PageResolverWindow(data), (_, _) => Task.CompletedTask, new PageResolveEngine { Tick = TimeSpan.FromMilliseconds(100) }); co3.Add(new[] { it4 });
        Task run3 = Task.CompletedTask; Application.Current.Dispatcher.Invoke(() => run3 = co3.RunAsync());
        PumpUntil(() => real is not null && real.CurrentAddress is not null && real.ReloadButton.IsEnabled && it4.State != PageLinkState.Waiting, "real window loaded", 40000); Pump(800);
        Check(real!.ReloadButton.IsEnabled && real.OpenBrowserButton.IsEnabled && real.CopyButton.IsEnabled && real.CloseSkipButton.IsEnabled && !real.ForwardButton.IsEnabled, "NeoDM browser window: Reload, Open in your browser, Copy address, Close are live");
        Click(real.OpenBrowserButton); Click(real.CopyButton); Click(real.ReloadButton); Pump(300);
        Check(opened.Contains(real.CurrentAddress!) && copied.Contains(real.CurrentAddress!), "NeoDM browser window: Open in your browser and Copy address use the current page");
        SaveShot(real, "browser-window-light.png");
        foreach (var theme in new[] { "DeepNavy", "Dark" }) { ThemeManager.Apply(Application.Current, theme, null); Pump(300); SaveShot(real, "browser-window-" + theme.ToLowerInvariant() + ".png"); }
        ThemeManager.Apply(Application.Current, "Light", null); Loc.SetLanguage("ar"); real.FlowDirection = FlowDirection.RightToLeft; Pump(300); SaveShot(real, "browser-window-rtl-ar.png"); Loc.SetLanguage("en"); real.FlowDirection = FlowDirection.LeftToRight;
        real.Close(); PumpUntil(() => run3.IsCompleted, "closing the NeoDM browser window ends the link", 15000);
        Check(it4.State == PageLinkState.Skipped, "closing the NeoDM browser window skips the link and the resolver moves on");
        try { Directory.Delete(data, true); } catch (IOException) { }

        // 5) strings in ten languages
        foreach (var lang in new[] { "en", "ar", "de", "es", "fr", "ja", "pt-BR", "ru", "tr", "zh-Hans" })
        { Loc.SetLanguage(lang); Check(new[] { "pagelink.btn.open_browser", "pagelink.btn.copy_address", "pagelink.btn.close_skip" }.All(k => Loc.T(k) != k), "locale " + lang + ": window button strings"); }
        Loc.SetLanguage("en"); PageWindowActions.Launcher = _ => { };
    }
}
