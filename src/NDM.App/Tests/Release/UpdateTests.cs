using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using NDM.App;
using NDM.Core;

/// <summary>Self-updater: version compare, release parsing, asset/hash handling, throttle, skip memory, windows. A loopback HTTP server stands in for the GitHub API and the asset downloads.</summary>
internal static partial class ReleaseTests
{
    private sealed class LocalServer : IDisposable
    {
        private readonly HttpListener listener = new();
        public string Url { get; }
        public int Port { get; }
        public List<(string Path, string Host, string? UserAgent, string? Accept, string? Authorization)> Requests { get; } = new();
        public LocalServer(Action<HttpListenerContext> handle)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = $"http://127.0.0.1:{Port}/";
            listener.Prefixes.Add(Url); listener.Start();
            Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); } catch (Exception) { return; }
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            lock (Requests) Requests.Add((ctx.Request.Url!.AbsolutePath, ctx.Request.Url.Host, ctx.Request.UserAgent, ctx.Request.Headers["Accept"], ctx.Request.Headers["Authorization"]));
                            handle(ctx);
                        }
                        catch (Exception) { try { ctx.Response.Abort(); } catch (Exception) { } }
                    });
                }
            });
        }
        public int Count { get { lock (Requests) return Requests.Count; } }
        public void Dispose() { try { listener.Stop(); listener.Close(); } catch (Exception) { } }
    }

    private static void Reply(HttpListenerContext ctx, int status, string body, string type = "application/json") => Reply(ctx, status, System.Text.Encoding.UTF8.GetBytes(body), type);
    private static void Reply(HttpListenerContext ctx, int status, byte[] body, string type = "application/json")
    {
        ctx.Response.StatusCode = status; ctx.Response.ContentType = type; ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body); ctx.Response.Close();
    }
    private static Version V(string s) { SemVer.TryParseTag(s, out var v); return v; }
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "release-latest.json"));
    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static byte[] FakeMsi(int seed)
    {
        var data = new byte[120_000]; new Random(seed).NextBytes(data);
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(data, 0); return data;
    }

    private static void UpdateTests(TestApp app, string root)
    {
        SemVerTests(); ParseTests(); ReleaseNotesTests();
        CheckerTests();
        InstallerTests(root);
        ControllerTests(root);
        WindowTests(app, root);
    }

    private static void SemVerTests()
    {
        Check(SemVer.IsNewer(V("1.3.0"), SemVer.Running("1.2.1")), "Version compare: 1.2.1 < 1.3.0");
        Check(SemVer.IsNewer(V("1.10.0"), SemVer.Running("1.9.0")) && !SemVer.IsNewer(V("1.9.0"), SemVer.Running("1.10.0")), "Version compare: 1.10.0 > 1.9.0 (numeric, not text)");
        Check(!SemVer.IsNewer(V("1.3.0"), SemVer.Running("1.3.0")) && !SemVer.IsNewer(V("1.2.9"), SemVer.Running("1.3.0")), "Version compare: same or older is not an update");
        Check(SemVer.Running("1.3.0+abc123") == V("1.3.0") && SemVer.Running("1.3.0-beta.1") == V("1.3.0") && SemVer.Running("garbage") == new Version(0, 0, 0), "Running version: build metadata and pre-release suffix are dropped");
        Check(SemVer.TryParseTag("v1.2.3", out var v) && v == V("1.2.3") && SemVer.TryParseTag("1.2.3", out _) && SemVer.TryParseTag(" V2.0.0 ", out _), "Tags v1.2.3 / 1.2.3 parse");
        foreach (var bad in new[] { "1.3", "v1", "1.3.0-rc1", "v1.3.0-beta", "1.3.0+build5", "v1.3.0.1", "latest", "", "v", "1.x.0", "v-1.2.3", "1.2.3 4" })
            Check(!SemVer.TryParseTag(bad, out _), $"Tag '{bad}' is ignored (pre-release/invalid)");
        Check(!SemVer.TryParseTag(null, out _), "Null tag is ignored");
        var info = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        Check(!info.Contains('+') && !AboutInfo.Version.Contains('+') && SemVer.Running(info) == SemVer.Running(AboutInfo.Version) && SemVer.Running(info).ToString(3) == AboutInfo.Version, "Built informational version carries no git revision (+hash): About, updater compare and installer see a clean x.y.z (" + info + ")");
        Check(SemVer.Running("1.3.0+0123abcd") == V("1.3.0") && !SemVer.IsNewer(V("1.3.0"), SemVer.Running("1.3.0+0123abcd")), "A '+build' suffix on the running version never makes the same release look outdated");
    }

    private static void ParseTests()
    {
        var r = UpdateChecker.Parse(Fixture)!;
        Check(r.Tag == "v1.3.1" && r.Version == V("1.3.1") && r.VersionText == "1.3.1" && r.Name == "NeoDM 1.3.1" && r.HtmlUrl == "https://github.com/unlimitedera/NeoDM/releases/tag/v1.3.1" && r.Body.Contains("Dark menus"), "Release JSON: tag, name, html_url and body are parsed");
        Check(r.Installer is { Name: "NeoDM-Setup-1.3.1.msi", Size: 71303168 } && r.Installer.Url.EndsWith("/v1.3.1/NeoDM-Setup-1.3.1.msi") && r.Checksums is { Name: "SHA256SUMS.txt" } && r.Signature is { Name: "SHA256SUMS.txt.sig" }, "Asset selection: NeoDM-Setup-*.msi, SHA256SUMS.txt and SHA256SUMS.txt.sig (the extension and other files are not picked)");
        var spoof = Fixture.Replace("https://github.com/unlimitedera/NeoDM/releases/download/v1.3.1/NeoDM-Setup-1.3.1.msi", "https://evil.example/NeoDM-Setup-1.3.1.msi");
        Check(UpdateChecker.Parse(spoof)!.Installer is null, "Asset selection: an installer hosted outside github.com is never offered");
        var plain = Fixture.Replace("NeoDM-Setup-1.3.1.msi", "NeoDM-Setup.msi");
        Check(UpdateChecker.Parse(plain)!.Installer?.Name == "NeoDM-Setup.msi", "Asset selection: the unversioned NeoDM-Setup.msi name is accepted too");
        Check(UpdateChecker.Parse(Fixture.Replace("\"prerelease\": false", "\"prerelease\": true")) is null && UpdateChecker.Parse(Fixture.Replace("\"draft\": false", "\"draft\": true")) is null, "Drafts and pre-releases are ignored");
        Check(UpdateChecker.Parse(Fixture.Replace("\"tag_name\": \"v1.3.1\"", "\"tag_name\": \"v1.3.1-rc1\"")) is null && UpdateChecker.Parse(Fixture.Replace("\"tag_name\": \"v1.3.1\"", "\"tag_name\": \"nightly\"")) is null, "Pre-release and invalid tags are ignored");
        Check(UpdateChecker.Parse("[]") is null && UpdateChecker.Parse("{}") is null, "Non-release documents are ignored");
        var noAssets = UpdateChecker.Parse("{\"tag_name\":\"v2.0.0\"}")!;
        Check(noAssets.Installer is null && noAssets.Checksums is null && noAssets.HtmlUrl.EndsWith("/releases/tag/v2.0.0"), "A release without assets still parses (page link falls back to the tag page)");
        Check(UpdateChecker.IsGitHubUrl(new Uri("https://github.com/x")) && UpdateChecker.IsGitHubUrl(new Uri("https://objects.githubusercontent.com/x")) && !UpdateChecker.IsGitHubUrl(new Uri("http://github.com/x")) && !UpdateChecker.IsGitHubUrl(new Uri("https://github.com.evil.example/x")), "Trusted URL rule: https on github.com / githubusercontent.com only");
        Check(UpdateChecker.UserAgent("1.3.0") == "NeoDM/1.3.0" && UpdateChecker.ChangelogUrl == "https://github.com/unlimitedera/NeoDM/blob/main/CHANGELOG.md" && UpdateChecker.RepositoryUrl == "https://github.com/unlimitedera/NeoDM", "User agent and repository URLs");
    }

    private static void ReleaseNotesTests()
    {
        var blocks = ReleaseNotes.Render(UpdateChecker.Parse(Fixture)!.Body);
        var text = blocks.Select(b => b.Text).ToList();
        Check(blocks[0] is { Kind: ReleaseNotes.Kind.Heading, Text: "What's new" } && blocks.Count(b => b.Kind == ReleaseNotes.Kind.Heading) == 3 && blocks.Count(b => b.Kind == ReleaseNotes.Kind.Bullet) == 3, "Release notes: headings and bullets are recognised");
        Check(text.Contains("Dark menus no longer show white border lines.") && text.Contains("Check for updates in the Help menu.") && text.Any(t => t.StartsWith("Browser downloads are cancelled") && t.EndsWith("opens in front of the browser.")), "Release notes: emphasis/code marks removed, wrapped bullet lines joined");
        Check(text.Last() == "See the changelog." && !text.Any(t => t.Contains('*') || t.Contains('`') || t.Contains("](")), "Release notes: links are shown as their text, no Markdown syntax left");
    }

    private static void CheckerTests()
    {
        string json = Fixture;
        int mode = 0; string tag = "v1.3.1";
        using var server = new LocalServer(ctx =>
        {
            switch (mode)
            {
                case 0: Reply(ctx, 200, json.Replace("\"v1.3.1\"", $"\"{tag}\"")); break;
                case 1: Reply(ctx, 403, "{\"message\":\"API rate limit exceeded\"}"); break;
                case 2: Reply(ctx, 429, "slow down"); break;
                case 3: Reply(ctx, 404, "{\"message\":\"Not Found\"}"); break;
                case 4: Reply(ctx, 200, "<html>captive portal</html>"); break;
                case 5: Thread.Sleep(2500); Reply(ctx, 200, json); break;
                default: Reply(ctx, 500, "boom"); break;
            }
        });
        UpdateChecker Make(string version = "1.3.0", string? baseUrl = null, ProxyOptions? proxy = null) => new(version, () => proxy, baseUrl ?? server.Url.TrimEnd('/'), trustedUrl: _ => true) { Timeout = TimeSpan.FromMilliseconds(700) };
        var available = Sync(Make().CheckAsync());
        Check(available is { Outcome: UpdateOutcome.Available, Release.VersionText: "1.3.1" }, "Checker: a newer release is reported as available");
        var request = server.Requests[0];
        Check(request.Path == "/repos/unlimitedera/NeoDM/releases/latest" && request.UserAgent == "NeoDM/1.3.0" && request.Accept == "application/vnd.github+json" && request.Authorization is null, "Checker: GET /repos/unlimitedera/NeoDM/releases/latest with User-Agent NeoDM/<version>, no authentication");
        tag = "v1.3.0"; Check(Sync(Make().CheckAsync()).Outcome == UpdateOutcome.UpToDate, "Checker: same version is up to date");
        tag = "v1.2.0"; Check(Sync(Make().CheckAsync()).Outcome == UpdateOutcome.UpToDate, "Checker: an older release is up to date");
        tag = "v1.10.0"; Check(Sync(Make("1.9.0").CheckAsync()).Outcome == UpdateOutcome.Available, "Checker: 1.10.0 is newer than 1.9.0");
        foreach (var (m, what) in new[] { (1, "rate limit (403)"), (2, "rate limit (429)"), (3, "404 / no release"), (4, "garbage reply"), (6, "server error (500)") })
        {
            mode = m; var r = Sync(Make().CheckAsync());
            Check(r.Outcome == UpdateOutcome.Unavailable && r.Release is null && !string.IsNullOrEmpty(r.Reason), $"Checker: {what} is a quiet 'unavailable', not an exception");
        }
        mode = 5; var timeout = Sync(Make().CheckAsync());
        Check(timeout.Outcome == UpdateOutcome.Unavailable, "Checker: a hanging server ends in 'unavailable' after the timeout");
        var closed = new TcpListener(IPAddress.Loopback, 0); closed.Start(); int deadPort = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
        Check(Sync(Make(baseUrl: $"http://127.0.0.1:{deadPort}").CheckAsync()).Outcome == UpdateOutcome.Unavailable, "Checker: offline / connection refused is a quiet 'unavailable'");
        Check(new UpdateChecker("1.3.0").Timeout == TimeSpan.FromSeconds(10), "Checker: default timeout is 10 seconds");
        // proxy from Options is used: the request to a made-up host arrives at the 'proxy' (our server) in absolute form
        mode = 0; tag = "v1.3.1"; int before = server.Count;
        var viaProxy = Sync(Make(baseUrl: "http://api.example.test", proxy: new ProxyOptions { Kind = ProxyKind.Http, Host = "127.0.0.1", Port = server.Port }).CheckAsync());
        Check(viaProxy.Outcome == UpdateOutcome.Available && server.Count == before + 1 && server.Requests.Last().Host == "api.example.test", "Checker: honours the proxy configured in Options (request went through the proxy)");
    }

    private static void InstallerTests(string root)
    {
        var msi = FakeMsi(1); string good = Sha(msi); string sums = "";
        int sumsStatus = 200; byte[] served = msi;
        using var server = new LocalServer(ctx =>
        {
            if (ctx.Request.Url!.AbsolutePath.EndsWith("SHA256SUMS.txt")) Reply(ctx, sumsStatus, sums, "text/plain");
            else if (ctx.Request.Url.AbsolutePath.EndsWith("SHA256SUMS.txt.sig")) Reply(ctx, 200, TestTrust.Sign(sums), "text/plain");
            else Reply(ctx, 200, served, "application/x-msi");
        });
        var json = Fixture.Replace("https://github.com/unlimitedera/NeoDM/releases/download/v1.3.1/", server.Url + "dl/");
        var release = UpdateChecker.Parse(json, _ => true)!;
        string dir = Path.Combine(root, "updates");
        UpdateInstaller Make() => TestTrust.Installer(dir, "1.3.0", reader: TestTrust.FakeMsi("1.3.1"));
        Check(UpdateInstaller.ParseChecksums($"{good} *NeoDM-Setup-1.3.1.msi\n{new string('A', 64)}  ./other.zip\r\nnot a line\n{new string('b', 63)}  short.bin\n").Count == 2, "SHA256SUMS: 'hash *name' and 'hash  name' lines parse; malformed lines are ignored");
        var parsed = UpdateInstaller.ParseChecksums($"{good.ToUpperInvariant()} *dir/NeoDM-Setup-1.3.1.msi");
        Check(parsed["NeoDM-Setup-1.3.1.msi"] == good, "SHA256SUMS: digests are lower-cased and paths reduced to the file name");

        Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "NeoDM-Setup-1.0.0.msi"), "old"); File.WriteAllText(Path.Combine(dir, "NeoDM-Setup-1.2.0.msi.partial"), "half");
        sums = $"{good} *NeoDM-Setup-1.3.1.msi\n";
        var steps = new List<(long, long)>(); var progress = new Progress<(long, long)>(p => steps.Add(p));
        var verified = Sync(Make().DownloadAndVerifyAsync(release, progress));
        string path = verified.Path;
        Pump(50);
        Check(Path.GetFileName(path) == "NeoDM-Setup-1.3.1.msi" && Path.GetDirectoryName(Path.GetDirectoryName(path)) == dir && File.ReadAllBytes(path).SequenceEqual(msi), "Update download: verified installer is saved byte-for-byte in its own folder below the updates folder");
        Check(Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length == 1 && !Directory.GetFiles(dir, "*.partial", SearchOption.AllDirectories).Any(), "Update download: old updates and partial files were cleaned up, no .partial left");
        Check(steps.Count > 0 && steps.Last().Item1 == msi.Length && steps.Last().Item2 == msi.Length, "Update download: progress reaches the full size");
        Check(UpdateInstaller.LooksLikeMsi(path) && !UpdateInstaller.LooksLikeMsi(Path.Combine(dir, "nope.msi")), "MSI check: Windows Installer (OLE) signature");
        verified.Dispose(); Make().CleanUp();

        void Refused(string what, Action arrange, string expected)
        {
            arrange();
            try { Sync(Make().DownloadAndVerifyAsync(release)); Check(false, what + ": must be refused"); }
            catch (UpdateException ex) { Check(ex.Message.Contains(expected, StringComparison.OrdinalIgnoreCase) && !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(), $"{what}: refused with '{ex.Message}' and nothing is left on disk"); }
        }
        Refused("Hash mismatch", () => { sums = $"{Sha(FakeMsi(2))} *NeoDM-Setup-1.3.1.msi\n"; }, "does not match");
        Refused("Missing hash entry", () => { sums = $"{good} *other-file.zip\n"; }, "no entry");
        Refused("Empty hash file", () => { sums = ""; }, "no entry");
        Refused("Hash file download fails", () => { sums = good; sumsStatus = 500; }, "SHA256SUMS.txt could not be downloaded");
        sumsStatus = 200;
        var notMsi = new byte[50_000]; new Random(5).NextBytes(notMsi); notMsi[0] = 0x4D; notMsi[1] = 0x5A;
        Refused("Not an MSI (hash matches)", () => { served = notMsi; sums = $"{Sha(notMsi)} *NeoDM-Setup-1.3.1.msi\n"; }, "not a Windows Installer");
        try { Sync(Make().DownloadAndVerifyAsync(release with { Checksums = null })); Check(false, "No SHA256SUMS asset must be refused"); }
        catch (UpdateException ex) { Check(ex.Message.Contains("cannot be verified") && !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(), "Release without SHA256SUMS.txt: refused clearly, nothing downloaded"); }
        try { Sync(Make().DownloadAndVerifyAsync(release with { Installer = null })); Check(false, "No installer asset"); }
        catch (UpdateException ex) { Check(ex.Message.Contains("no installer"), "Release without an installer asset: clear message"); }
        try { Sync(Make().DownloadAndVerifyAsync(release with { Installer = release.Installer! with { Name = "..\\evil.msi" } })); Check(false, "Path in the installer name"); }
        catch (UpdateException) { Check(true, "Installer names containing a path are refused"); }
        File.WriteAllText(Path.Combine(dir, "leftover.partial"), "x"); Directory.CreateDirectory(Path.Combine(dir, "abc")); File.WriteAllText(Path.Combine(dir, "abc", "NeoDM-Setup-0.1.0.msi"), "x"); Make().CleanUp(); Check(!Directory.EnumerateFileSystemEntries(dir).Any(), "CleanUp removes partial and old update files and the per-download folders");

        var info = MsiexecLauncher.BuildStartInfo("C:\\Users\\me\\AppData\\Local\\NDM\\updates\\NeoDM-Setup-1.3.1.msi");
        Check(info.FileName == "msiexec.exe" && info.Arguments == "/i \"C:\\Users\\me\\AppData\\Local\\NDM\\updates\\NeoDM-Setup-1.3.1.msi\" /passive" && info.UseShellExecute && info.Verb == "runas", "msiexec launch is 'msiexec /i <msi> /passive' elevated through the UAC verb (command built here; never executed by the tests)");
    }

    private sealed class FakeLauncher : IInstallerLauncher
    {
        public LaunchResult Result = LaunchResult.Started; public List<string> Paths = new();
        public Action<string>? OnLaunch;
        public LaunchResult Launch(string msiPath) { Paths.Add(msiPath); OnLaunch?.Invoke(msiPath); return Result; }
    }

    private static void ControllerTests(string root)
    {
        var s = new AppSettings();
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        Check(s.CheckForUpdates && s.LastUpdateCheckUtc is null && s.SkippedUpdateVersion == "", "Settings: automatic update checks default to on, nothing skipped");
        Check(UpdateSchedule.StartDelay == TimeSpan.FromSeconds(20) && UpdateSchedule.Period == TimeSpan.FromHours(24) && UpdateSchedule.MinimumGap == TimeSpan.FromHours(12), "Schedule: first check ~20 s after start, then every 24 h, never closer than 12 h");
        Check(UpdateSchedule.ShouldAutoCheck(s, now), "Throttle: never checked -> check");
        s.LastUpdateCheckUtc = now - TimeSpan.FromHours(11) - TimeSpan.FromMinutes(59); Check(!UpdateSchedule.ShouldAutoCheck(s, now), "Throttle: 11 h 59 min ago -> no check");
        s.LastUpdateCheckUtc = now - TimeSpan.FromHours(12); Check(UpdateSchedule.ShouldAutoCheck(s, now), "Throttle: exactly 12 h ago -> check");
        s.LastUpdateCheckUtc = now + TimeSpan.FromDays(3); Check(UpdateSchedule.ShouldAutoCheck(s, now), "Throttle: a stored time in the far future (clock moved) does not block checks forever");
        s.LastUpdateCheckUtc = now - TimeSpan.FromDays(9); s.CheckForUpdates = false; Check(!UpdateSchedule.ShouldAutoCheck(s, now), "Throttle: automatic checks switched off -> never");
        var release = ReleaseInfoFor("1.3.1");
        s.CheckForUpdates = true; s.SkippedUpdateVersion = "1.3.1"; Check(!UpdateSchedule.ShouldNotify(s, release) && UpdateSchedule.ShouldNotify(s, ReleaseInfoFor("1.3.2")), "Skip memory: the skipped version stays quiet, a newer one is announced again");

        // persistence round trip
        string dir = Path.Combine(root, "upd-settings"); Directory.CreateDirectory(dir);
        var saved = new AppSettings { CheckForUpdates = false, LastUpdateCheckUtc = now, SkippedUpdateVersion = "1.3.1" }; saved.Save(dir);
        var loaded = AppSettings.Load(dir);
        Check(!loaded.CheckForUpdates && loaded.LastUpdateCheckUtc == now && loaded.SkippedUpdateVersion == "1.3.1", "Settings: last-check time, skipped version and the checkbox persist");

        // controller against the fixture server
        string tag = "v1.3.1"; int mode = 0;
        using var server = new LocalServer(ctx => { if (mode == 0) Reply(ctx, 200, Fixture.Replace("\"v1.3.1\"", $"\"{tag}\"")); else Reply(ctx, 503, "down"); });
        var settings = new AppSettings(); int saves = 0; var clock = now;
        var shown = new List<ReleaseInfo>(); var messages = new List<string>();
        var controller = new UpdateController(settings, () => saves++, "1.3.0", () => new UpdateChecker("1.3.0", null, server.Url.TrimEnd('/'), trustedUrl: _ => true) { Timeout = TimeSpan.FromSeconds(3) }, new UpdateInstaller(Path.Combine(root, "upd-dl"), "1.3.0"), () => clock)
        { ShowAvailable = shown.Add, ShowMessage = messages.Add };
        var first = Sync(controller.RunAutomaticAsync());
        Check(first?.Outcome == UpdateOutcome.Available && shown.Count == 1 && settings.LastUpdateCheckUtc == clock && saves == 1 && messages.Count == 0, "Automatic check: announces the update, records the time");
        clock = now + TimeSpan.FromHours(11); Check(Sync(controller.RunAutomaticAsync()) is null && server.Count == 1, "Automatic check within 12 h of the last one: no request is made");
        clock = now + TimeSpan.FromHours(12.5); Sync(controller.RunAutomaticAsync()); Check(server.Count == 2 && shown.Count == 2, "Automatic check after 12 h asks again (and reminds, since the version is not skipped)");
        controller.SkipVersion(shown[0]); Check(settings.SkippedUpdateVersion == "1.3.1" && saves >= 3, "Skip this version: stored and saved");
        clock = now + TimeSpan.FromDays(2); Sync(controller.RunAutomaticAsync()); Check(server.Count == 3 && shown.Count == 2 && messages.Count == 0, "A skipped version is not announced again by automatic checks");
        tag = "v1.3.2"; clock = now + TimeSpan.FromDays(4); Sync(controller.RunAutomaticAsync()); Check(shown.Count == 3 && shown[2].VersionText == "1.3.2", "A newer version than the skipped one is announced");
        tag = "v1.3.1"; Sync(controller.CheckNowAsync()); Check(shown.Count == 4 && shown[3].VersionText == "1.3.1", "Manual check shows the update even for a skipped version");
        tag = "v1.3.0"; Sync(controller.CheckNowAsync()); Check(messages.Count == 1 && messages[0].Contains("You are up to date") && shown.Count == 4, "Manual check: 'You are up to date' when nothing newer exists");
        mode = 1; Sync(controller.CheckNowAsync()); Check(messages.Count == 2 && messages[1].Contains("could not check"), "Manual check while GitHub is unreachable: says so");
        int shownBefore = shown.Count, messagesBefore = messages.Count; clock = now + TimeSpan.FromDays(9); Sync(controller.RunAutomaticAsync());
        Check(shown.Count == shownBefore && messages.Count == messagesBefore, "Automatic check while GitHub is unreachable stays completely silent");
        settings.CheckForUpdates = false; clock = now + TimeSpan.FromDays(20); int count = server.Count; Check(Sync(controller.RunAutomaticAsync()) is null && server.Count == count, "Automatic check disabled in Options: no network call at all");
        settings.CheckForUpdates = true; mode = 0; tag = "v1.3.5"; clock = now + TimeSpan.FromDays(30);
        var viaTimer = new UpdateController(new AppSettings(), () => { }, "1.3.0", () => new UpdateChecker("1.3.0", null, server.Url.TrimEnd('/'), trustedUrl: _ => true), new UpdateInstaller(Path.Combine(root, "upd-dl2"), "1.3.0"));
        var timerShown = new List<ReleaseInfo>(); viaTimer.ShowAvailable = timerShown.Add; viaTimer.Start(TimeSpan.FromMilliseconds(60));
        PumpUntil(() => timerShown.Count == 1, "background timer fires the first check"); viaTimer.Stop();
        Check(timerShown[0].VersionText == "1.3.5", "Background timer: the first check runs on its own shortly after start");
    }

    private static void WindowTests(TestApp app, string root)
    {
        var opened = new List<string>(); var oldOpen = UrlOpener.Open; UrlOpener.Open = opened.Add;
        var oldNative = WindowActivation.Native; var fakeNative = new FakeNative(); WindowActivation.Native = fakeNative;
        try
        {
            var msi = FakeMsi(7); string sums = $"{Sha(msi)} *NeoDM-Setup-1.3.1.msi\n"; byte[] served = msi; string tag = "v1.3.1";
            using var server = new LocalServer(ctx =>
            {
                string p = ctx.Request.Url!.AbsolutePath;
                if (p.EndsWith("SHA256SUMS.txt")) Reply(ctx, 200, sums, "text/plain");
                else if (p.EndsWith("SHA256SUMS.txt.sig")) Reply(ctx, 200, TestTrust.Sign(sums), "text/plain");
                else if (p.EndsWith(".msi")) Reply(ctx, 200, served, "application/x-msi");
                else Reply(ctx, 200, Fixture.Replace("https://github.com/unlimitedera/NeoDM/releases/download/v1.3.1/", $"{ctx.Request.Url.GetLeftPart(UriPartial.Authority)}/dl/").Replace("\"v1.3.1\"", $"\"{tag}\""));
            });
            var release = UpdateChecker.Parse(Fixture.Replace("https://github.com/unlimitedera/NeoDM/releases/download/v1.3.1/", server.Url + "dl/"), _ => true)!;
            var launcher = new FakeLauncher(); var settings = new AppSettings(); int saves = 0;
            var controller = new UpdateController(settings, () => saves++, "1.3.0", () => new UpdateChecker("1.3.0", null, server.Url.TrimEnd('/'), trustedUrl: _ => true), TestTrust.Installer(Path.Combine(root, "win-dl"), "1.3.0", launcher, reader: TestTrust.FakeMsi("1.3.1")));

            var w = new UpdateWindow(release, controller); w.Show(); Pump(150);
            string Text(string name) => w.FindName(name) is TextBox tb ? tb.Text : ((TextBlock)w.FindName(name)).Text;
            Check(Text("HeadingText") == "NeoDM 1.3.1 is available (you have 1.3.0)", "Update window: 'NeoDM 1.3.1 is available (you have 1.3.0)'");
            var notes = Visual<TextBlock>((DependencyObject)w.FindName("NotesPanel")).Select(t => t.Text).ToList();
            Check(notes.Any(t => t.StartsWith("•  Dark menus")) && notes.Contains("What's new") && !notes.Any(t => t.Contains("**")), "Update window: release notes are rendered from the release body");
            Check(w.FindName("UpdateButton") is Button { Content: "_Update now" } && w.FindName("LaterButton") is Button { Content: "Remind me _later" } && w.FindName("SkipButton") is Button { Content: "_Skip this version" }, "Update window: Update now / Remind me later / Skip this version buttons");
            var changelog = (Hyperlink)w.FindName("ChangelogLink"); var page = (Hyperlink)w.FindName("ReleaseLink");
            Check(changelog.NavigateUri.AbsoluteUri == UpdateChecker.ChangelogUrl && page.NavigateUri.AbsoluteUri == release.HtmlUrl, "Update window: 'View full changelog' and 'Release page' links point to CHANGELOG.md on GitHub and the release page");
            changelog.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); page.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Check(opened.SequenceEqual(new[] { UpdateChecker.ChangelogUrl, release.HtmlUrl }), "Update window: link clicks open the default browser (stubbed here)");
            Check(w.ShowInTaskbar && fakeNative.Calls.Contains("setfg"), "Update window: has a taskbar button and is brought to the front like the capture dialog");
            Check(Visual<ScrollViewer>(w).Any(sv => sv.Name == "NotesScroll"), "Update window: notes scroll for long releases");
            Check(w.IsVisible && ((Border)((Grid)w.Content).Children.OfType<Border>().First()).Background is not null, "Update window renders themed");

            // declined UAC
            launcher.Result = LaunchResult.UacDeclined;
            Check(!Sync(w.InstallAsync()) && Text("StatusText").Contains("permission was declined") && ((Button)w.FindName("UpdateButton")).IsEnabled && w.IsVisible && launcher.Paths.Count == 1, "Update now: declining the UAC prompt shows a message, keeps the window and allows a retry");
            launcher.Result = LaunchResult.Failed; Check(!Sync(w.InstallAsync()) && Text("StatusText").Contains("could not be started"), "Update now: installer start failure is reported");
            // hash mismatch
            string good = sums; sums = $"{Sha(FakeMsi(8))} *NeoDM-Setup-1.3.1.msi\n"; int launches = launcher.Paths.Count;
            Check(!Sync(w.InstallAsync()) && Text("StatusText").Contains("does not match") && launcher.Paths.Count == launches, "Update now: a hash mismatch is refused with a clear error and the installer is never started");
            // success
            sums = good; launcher.Result = LaunchResult.Started;
            Check(Sync(w.InstallAsync()), "Update now: verified download starts the installer");
            Check(launcher.Paths.Last().EndsWith("NeoDM-Setup-1.3.1.msi") && File.Exists(launcher.Paths.Last()) && File.ReadAllBytes(launcher.Paths.Last()).SequenceEqual(msi) && !w.IsVisible, "Update now: msiexec stub received the verified file path and the window closed");

            // Skip this version
            var w2 = new UpdateWindow(release, controller); w2.Show(); Pump(80);
            ((Button)w2.FindName("SkipButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(50);
            Check(settings.SkippedUpdateVersion == "1.3.1" && !w2.IsVisible && saves > 0, "Skip this version button: remembered, saved, window closed");
            var w3 = new UpdateWindow(release, controller); w3.Show(); Pump(80);
            ((Button)w3.FindName("LaterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(50);
            Check(!w3.IsVisible && settings.SkippedUpdateVersion == "1.3.1", "Remind me later: closes without skipping anything new");
            var noSig = new UpdateWindow(release with { Signature = null }, controller); noSig.Show(); Pump(80);
            Check(!((Button)noSig.FindName("UpdateButton")).IsEnabled && ((TextBox)noSig.FindName("StatusText")).Text.Contains("signed checksum"), "Release without SHA256SUMS.txt.sig: 'Update now' is disabled and the reason is shown");
            noSig.Close();
            var noSums = new UpdateWindow(release with { Checksums = null }, controller); noSums.Show(); Pump(80);
            Check(!((Button)noSums.FindName("UpdateButton")).IsEnabled && ((TextBox)noSums.FindName("StatusText")).Text.Contains("SHA256SUMS.txt"), "Release without SHA256SUMS.txt: 'Update now' is disabled and the reason is shown");
            noSums.Close();

            // Options: checkbox + skipped memory
            string dir = Path.Combine(root, "upd-options"); Directory.CreateDirectory(dir);
            var opt = new AppSettings { DownloadDirectory = Path.Combine(root, "upd-options", "files"), SkippedUpdateVersion = "1.3.1" };
            var options = new SettingsWindow(opt, dir); options.Show(); Pump(80);
            var check = (CheckBox)options.FindName("CheckForUpdates");
            Check(check.IsChecked == true && check.Content.ToString()!.Replace("_", "").Contains("Check for updates automatically") && ((FrameworkElement)options.FindName("SkippedPanel")).Visibility == Visibility.Visible && ((TextBlock)options.FindName("SkippedText")).Text.Contains("1.3.1"), "Options > General: 'Check for updates automatically' (on) and the skipped version are shown");
            ((Button)options.FindName("ForgetSkipButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); check.IsChecked = false;
            ((Button)options.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var disk = AppSettings.Load(dir);
            Check(!disk.CheckForUpdates && disk.SkippedUpdateVersion == "" && !opt.CheckForUpdates, "Options: unticking the checkbox and forgetting the skipped version are saved");

            // About window
            var about = new AboutWindow { Updates = controller, ShowInTaskbar = false }; about.Show(); Pump(80);
            var repo = (Hyperlink)about.FindName("RepoLink");
            Check(repo.NavigateUri.AbsoluteUri == "https://github.com/unlimitedera/NeoDM" && ((TextBlock)about.FindName("VersionText")).Text == "Version " + AboutInfo.Version, "About: shows the version and the GitHub repository link");
            opened.Clear(); repo.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); Check(opened.SequenceEqual(new[] { "https://github.com/unlimitedera/NeoDM" }), "About: the repository link opens in the default browser (stubbed)");
            Check(about.FindName("CheckUpdatesButton") is Button { Content: "Check for _updates" }, "About: has a 'Check for updates' button");
            tag = "v1.3.0"; var msgs = new List<string>(); controller.ShowMessage = msgs.Add;
            ((Button)about.FindName("CheckUpdatesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => ((TextBox)about.FindName("UpdateStatus")).Text.Contains("up to date"), "About check result");
            Check(msgs.Count == 0, "About: the 'up to date' answer is shown inline in the window");
            about.Close();

            // Help menu
            var main = new MainWindow(NewManager(root, "helpmenu"), new AppSettings { DownloadDirectory = Path.Combine(root, "helpmenu") }, () => { }, enableTray: false) { ShowInTaskbar = false };
            IEnumerable<MenuItem> All(ItemsControl c) => c.Items.OfType<MenuItem>().SelectMany(i => new[] { i }.Concat(All(i)));
            Check(All((ItemsControl)main.FindName("MainMenu")).Any(i => i.Header as string == "_Check for updates…"), "Help menu has 'Check for updates…'");
            main.Close();
        }
        finally { UrlOpener.Open = oldOpen; WindowActivation.Native = oldNative; }
    }
}
