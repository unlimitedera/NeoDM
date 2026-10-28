using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using NDM.Core;
using static NDM.Tests.StreamKit;

namespace NDM.Tests;

/// <summary>Builds a ToolManager whose "ffmpeg.exe" is this test program (see <see cref="FakeFfmpeg"/>), installed through the real install path from a fake download.</summary>
internal static class FakeTool
{
    public const string PinnedUrl = "https://tools.example.test/builds/ffmpeg-test-essentials.zip";
    public const string Entry = "ffmpeg-test/bin/ffmpeg.exe";

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "NDM.Tests.exe");

    public static (byte[] Zip, ToolPin Pin) BuildArchive(byte[]? exe = null, Action<ZipArchive>? extra = null, string entryName = Entry)
    {
        exe ??= File.ReadAllBytes(ExePath);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var readme = zip.CreateEntry("ffmpeg-test/README.txt"); using (var w = new StreamWriter(readme.Open())) w.Write("readme");
            var e = zip.CreateEntry(entryName, CompressionLevel.Fastest); using (var s = e.Open()) s.Write(exe);
            extra?.Invoke(zip);
        }
        var bytes = ms.ToArray();
        return (bytes, new ToolPin(PinnedUrl, "0.0-test", bytes.Length, Sha(bytes).ToLowerInvariant(), Entry, exe.Length, Sha(exe).ToLowerInvariant()));
    }

    public static FakeDownloadHandler Serve(byte[] zip)
    {
        var handler = new FakeDownloadHandler();
        handler.Respond = r => r.RequestUri!.AbsoluteUri == PinnedUrl ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) } : new HttpResponseMessage(HttpStatusCode.NotFound);
        return handler;
    }

    /// <summary>Installs the fake and makes it runnable (the program needs its own libraries beside it).</summary>
    public static async Task<ToolManager> Install(string stateDirectory)
    {
        var (zip, pin) = BuildArchive();
        var tools = new ToolManager(stateDirectory, pin, handlerFactory: () => Serve(zip));
        await tools.InstallAsync();
        MakeRunnable(tools);
        return tools;
    }

    public static void MakeRunnable(ToolManager tools)
    {
        foreach (var name in new[] { "NDM.Tests.dll", "NDM.Tests.runtimeconfig.json", "NDM.Tests.deps.json", "NDM.Core.dll" })
        {
            var from = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(from)) File.Copy(from, Path.Combine(tools.ToolDirectory, name), true);
        }
    }

    public static string NewLog(string directory)
    {
        Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, "ffmpeg-calls-" + Guid.NewGuid().ToString("N") + ".log");
        Environment.SetEnvironmentVariable(FakeFfmpeg.LogVariable, log);
        Environment.SetEnvironmentVariable(FakeFfmpeg.ModeVariable, null);
        Environment.SetEnvironmentVariable(FakeFfmpeg.DurationVariable, null);
        return log;
    }

    public static void ResetEnvironment()
    {
        foreach (var v in new[] { FakeFfmpeg.LogVariable, FakeFfmpeg.ModeVariable, FakeFfmpeg.DurationVariable }) Environment.SetEnvironmentVariable(v, null);
    }
}

internal static class ToolTests
{
    private static async Task<ToolException> Refused(Func<Task> action)
    {
        try { await action(); }
        catch (ToolException ex) { return ex; }
        throw new Exception("The operation should have been refused");
    }

    public static async Task InstallAndVerify((string Download, string State) paths)
    {
        var (zip, pin) = FakeTool.BuildArchive();
        var handler = FakeTool.Serve(zip);
        var tools = new ToolManager(paths.State, pin, handlerFactory: () => handler);
        Ensure(!tools.IsInstalled && !tools.IsAvailable && tools.GetVersion() is null, "nothing installed at first");
        var reports = new List<(long, long)>();
        await tools.InstallAsync(new Progress<(long Done, long Total)>(p => { lock (reports) reports.Add(p); }));
        await Task.Delay(100);
        Ensure(tools.IsInstalled && tools.GetVersion() == "0.0-test" && File.Exists(tools.FfmpegPath), "installed");
        Ensure(File.ReadAllBytes(tools.FfmpegPath).Length == pin.ExeSize && ShaFile(tools.FfmpegPath).ToLowerInvariant() == pin.ExeSha256, "the extracted program has the pinned hash");
        Ensure(reports.Count > 0 && reports.Any(r => r.Item1 == pin.ArchiveSize) && reports.All(r => r.Item2 == pin.ArchiveSize), "progress reported up to the archive size");
        Ensure(handler.Requests.Count == 1 && handler.Requests[0].AbsoluteUri == FakeTool.PinnedUrl, "one request, to the pinned address");
        var left = Directory.GetFiles(tools.ToolDirectory).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Ensure(left.SequenceEqual(new[] { "ffmpeg.exe", "ffmpeg.json" }), "only the program and its record remain: " + string.Join(",", left));
        Ensure(File.ReadAllText(Path.Combine(tools.ToolDirectory, "ffmpeg.json")).Contains(pin.ExeSha256), "the verified hash is recorded next to the program");
        // a pin from a newer app version makes the old install count as outdated
        var newer = new ToolManager(paths.State, pin with { ExeSha256 = new string('a', 64) });
        Ensure(!newer.IsInstalled, "a different pinned hash = not installed");
        var ex = await Refused(async () => { using var _ = newer.OpenVerified(); await Task.CompletedTask; });
        Ensure(ex.Code == ToolException.Outdated || ex.Code == ToolException.NotInstalled, "outdated install is not run: " + ex.Code);
        tools.Uninstall();
        Ensure(!tools.IsInstalled && !Directory.Exists(tools.ToolDirectory), "uninstalled");
    }

    public static async Task InstallRefusals((string Download, string State) paths)
    {
        var (zip, pin) = FakeTool.BuildArchive();
        string Fresh() => Path.Combine(paths.State, Guid.NewGuid().ToString("N"));
        async Task<ToolException> TryInstall(ToolManager tools) => await Refused(() => tools.InstallAsync());
        void NothingInstalled(ToolManager t) => Ensure(!t.IsInstalled && !File.Exists(t.FfmpegPath) && (!Directory.Exists(t.ToolDirectory) || Directory.GetFiles(t.ToolDirectory).All(f => Path.GetFileName(f) == ".install.lock")), "nothing was installed or left behind: " + (Directory.Exists(t.ToolDirectory) ? string.Join(",", Directory.GetFiles(t.ToolDirectory).Select(Path.GetFileName)) : ""));

        // a changed archive (one byte) is refused on its hash
        var bad = (byte[])zip.Clone(); bad[bad.Length / 2] ^= 0xFF;
        var t1 = new ToolManager(Fresh(), pin, handlerFactory: () => FakeTool.Serve(bad));
        Ensure((await TryInstall(t1)).Code == ToolException.HashMismatch, "tampered archive"); NothingInstalled(t1);
        // a different size is refused before it is read in full
        var t2 = new ToolManager(Fresh(), pin, handlerFactory: () => FakeTool.Serve(zip.Concat(new byte[10]).ToArray()));
        Ensure((await TryInstall(t2)).Code == ToolException.HashMismatch, "longer archive"); NothingInstalled(t2);
        // not found
        var h404 = new FakeDownloadHandler();
        var t3 = new ToolManager(Fresh(), pin, handlerFactory: () => h404);
        Ensure((await TryInstall(t3)).Code == ToolException.Download, "404"); NothingInstalled(t3);
        // only https
        var t4 = new ToolManager(Fresh(), pin with { Url = "http://tools.example.test/x.zip" }, handlerFactory: () => FakeTool.Serve(zip));
        Ensure((await TryInstall(t4)).Code == ToolException.Host, "http refused");
        // redirects: same host is followed, another host or http is refused
        var hOk = new FakeDownloadHandler { Respond = r => r.RequestUri!.AbsolutePath == "/builds/ffmpeg-test-essentials.zip" ? Redirect("https://tools.example.test/mirror/a.zip") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) } };
        var t5 = new ToolManager(Fresh(), pin, handlerFactory: () => hOk);
        await t5.InstallAsync(); Ensure(t5.IsInstalled && hOk.Requests.Count == 2, "same-host redirect followed");
        var hEvil = new FakeDownloadHandler { Respond = _ => Redirect("https://evil.example.org/ffmpeg.zip") };
        var t6 = new ToolManager(Fresh(), pin, handlerFactory: () => hEvil);
        Ensure((await TryInstall(t6)).Code == ToolException.Host && hEvil.Requests.Count == 1, "other host refused without a request to it"); NothingInstalled(t6);
        var hHttp = new FakeDownloadHandler { Respond = _ => Redirect("http://tools.example.test/x.zip") };
        var t7 = new ToolManager(Fresh(), pin, handlerFactory: () => hHttp);
        Ensure((await TryInstall(t7)).Code == ToolException.Host, "downgrade to http refused");
        var hLoop = new FakeDownloadHandler { Respond = _ => Redirect("https://tools.example.test/loop") };
        var t8 = new ToolManager(Fresh(), pin, handlerFactory: () => hLoop);
        Ensure((await TryInstall(t8)).Code == ToolException.Download && hLoop.Requests.Count == ToolManager.MaxRedirects + 1, "redirect limit");

        // archives that carry the right hash for themselves but are not what we expect
        var exe = File.ReadAllBytes(FakeTool.ExePath);
        (byte[] Zip, ToolPin Pin) Custom(byte[] content, Action<ZipArchive>? extra = null, string name = FakeTool.Entry, long? declaredExe = null)
        {
            var (z, p) = FakeTool.BuildArchive(content, extra, name);
            return (z, p with { ExeSize = declaredExe ?? p.ExeSize, ExeSha256 = declaredExe is null ? p.ExeSha256 : Sha(exe).ToLowerInvariant() });
        }
        // wrong program inside (its hash differs from the pinned one)
        var (zWrong, pWrong) = FakeTool.BuildArchive(exe.Select(x => (byte)(x ^ 0x5A)).ToArray());
        var t9 = new ToolManager(Fresh(), pWrong with { ExeSha256 = pin.ExeSha256 }, handlerFactory: () => FakeTool.Serve(zWrong));
        Ensure((await TryInstall(t9)).Code == ToolException.HashMismatch, "extracted program hash differs"); NothingInstalled(t9);
        // the wanted entry is missing
        var (zMissing, pMissing) = Custom(exe, name: "other/path/ffmpeg.exe");
        var t10 = new ToolManager(Fresh(), pMissing, handlerFactory: () => FakeTool.Serve(zMissing));
        Ensure((await TryInstall(t10)).Code == ToolException.Archive, "entry missing"); NothingInstalled(t10);
        // zip-slip names anywhere in the archive
        foreach (var evil in new[] { "../evil.exe", "ffmpeg-test/../../evil.exe", "/abs/evil.exe", "C:/evil.exe" })
        {
            var (zSlip, pSlip) = Custom(exe, z => { var e = z.CreateEntry(evil); using var s = e.Open(); s.WriteByte(1); });
            var t = new ToolManager(Fresh(), pSlip, handlerFactory: () => FakeTool.Serve(zSlip));
            Ensure((await TryInstall(t)).Code == ToolException.Archive, "unsafe entry name: " + evil); NothingInstalled(t);
            Ensure(!File.Exists(Path.Combine(t.ToolDirectory, "..", "evil.exe")) && !File.Exists(Path.Combine(t.ToolDirectory, "evil.exe")), "nothing written outside");
        }
        // too many entries
        var (zMany, pMany) = Custom(exe, z => { for (int i = 0; i < ToolManager.MaxArchiveEntries + 5; i++) z.CreateEntry("x/" + i); });
        var t11 = new ToolManager(Fresh(), pMany, handlerFactory: () => FakeTool.Serve(zMany));
        Ensure((await TryInstall(t11)).Code == ToolException.Archive, "too many entries"); NothingInstalled(t11);
        // size caps: the entry is not the size the pin says
        var (zSize, pSize) = FakeTool.BuildArchive(exe.Concat(new byte[100]).ToArray());
        var t12 = new ToolManager(Fresh(), pSize with { ExeSize = exe.Length }, handlerFactory: () => FakeTool.Serve(zSize));
        Ensure((await TryInstall(t12)).Code == ToolException.Archive, "entry larger than the pinned size"); NothingInstalled(t12);
        // zip bomb: an entry that compresses to almost nothing
        var zeros = new byte[48 * 1024 * 1024];
        var (zBomb, pBomb) = FakeTool.BuildArchive(zeros);
        var t13 = new ToolManager(Fresh(), pBomb, handlerFactory: () => FakeTool.Serve(zBomb));
        Ensure((await TryInstall(t13)).Code == ToolException.Archive, "compression ratio cap"); NothingInstalled(t13);
        // a pin with absurd sizes
        var t14 = new ToolManager(Fresh(), pin with { ExeSize = ToolManager.MaxExeBytes + 1 }, handlerFactory: () => FakeTool.Serve(zip));
        Ensure((await TryInstall(t14)).Code == ToolException.Archive, "pinned size over the cap");
        static HttpResponseMessage Redirect(string to) { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new Uri(to); return r; }
    }

    public static async Task RunAndLaunchChecks((string Download, string State) paths)
    {
        var log = FakeTool.NewLog(paths.State);
        try
        {
            var tools = await FakeTool.Install(paths.State);
            // arguments reach the program one by one, exactly: no shell, no splitting, no expansion
            var hostile = new[] { "-i", "C:\\a b\\in put.ts", "x & del *.*", "say \"hi\"", "$(whoami)", "%PATH%", "a;b|c>d", "", "ünï.mp4" };
            var run = await tools.RunAsync(hostile, TimeSpan.FromSeconds(30));
            // the fake treats "-i <x>" followed by more as a mux call: the output is the last argument and does not exist as a folder, so a failure is acceptable; the arguments are what we check
            var calls = FakeFfmpeg.ReadLog(log);
            Ensure(calls.Count == 1 && calls[0].SequenceEqual(hostile), "the arguments arrive unchanged: " + string.Join(" | ", calls.FirstOrDefault() ?? Array.Empty<string>()));
            File.Delete(Path.Combine(tools.ToolDirectory, "ünï.mp4"));
            // version call works through the same path
            var v = await tools.RunAsync(new[] { "-version" }, TimeSpan.FromSeconds(30));
            Ensure(v.ExitCode == 0 && v.Output.Contains("fake-1"), "version output captured: " + v.Output);

            // the file stays open and protected while the program runs
            Environment.SetEnvironmentVariable(FakeFfmpeg.ModeVariable, "hang");
            var clock = Stopwatch.StartNew();
            var hang = tools.RunAsync(new[] { "-hide_banner" }, TimeSpan.FromSeconds(4));
            await WaitUntil(() => FakeFfmpeg.ReadLog(log).Count >= 3, "the program did not start", 20);
            Ensure(Throws<IOException>(() => { using var f = new FileStream(tools.FfmpegPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }), "cannot be opened for writing while it runs");
            Ensure(Throws<IOException>(() => File.Delete(tools.FfmpegPath)) || Throws<UnauthorizedAccessException>(() => File.Delete(tools.FfmpegPath)), "cannot be deleted while it runs");
            Ensure(Throws<IOException>(() => File.Move(tools.FfmpegPath, tools.FfmpegPath + ".moved")) || Throws<UnauthorizedAccessException>(() => File.Move(tools.FfmpegPath, tools.FfmpegPath + ".moved")), "cannot be renamed while it runs");
            var result = await hang;
            Ensure(result.TimedOut && result.ExitCode == -1 && clock.Elapsed < TimeSpan.FromSeconds(20), "timeout kills the program: " + clock.Elapsed);
            var pid = System.Text.Json.JsonDocument.Parse(File.ReadAllLines(log).Last()).RootElement.GetProperty("pid").GetInt32();
            await Task.Delay(300);
            bool alive; try { alive = !Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { alive = false; }
            Ensure(!alive, "the program is gone after the timeout");
            Environment.SetEnvironmentVariable(FakeFfmpeg.ModeVariable, null);
            // cancellation also stops it
            Environment.SetEnvironmentVariable(FakeFfmpeg.ModeVariable, "hang");
            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500)))
            {
                bool canceled = false; try { await tools.RunAsync(new[] { "-hide_banner" }, TimeSpan.FromSeconds(60), cts.Token); } catch (OperationCanceledException) { canceled = true; }
                Ensure(canceled, "cancellation is passed on");
            }
            Environment.SetEnvironmentVariable(FakeFfmpeg.ModeVariable, null);

            // the hash is checked each time before it runs
            var before = FakeFfmpeg.ReadLog(log).Count;
            await File.AppendAllTextAsync(tools.FfmpegPath, "x"); // appending changes the content (the program is not running now)
            var ex = await Refused(() => tools.RunAsync(new[] { "-version" }, TimeSpan.FromSeconds(10)));
            Ensure(ex.Code == ToolException.HashMismatch && FakeFfmpeg.ReadLog(log).Count == before, "a changed program is not started: " + ex.Code);
            // same length, other bytes
            tools.Uninstall(); tools = await FakeTool.Install(paths.State);
            var bytes = File.ReadAllBytes(tools.FfmpegPath); bytes[bytes.Length / 2] ^= 1; File.WriteAllBytes(tools.FfmpegPath, bytes);
            ex = await Refused(() => tools.RunAsync(new[] { "-version" }, TimeSpan.FromSeconds(10)));
            Ensure(ex.Code == ToolException.HashMismatch, "same size, changed bytes: " + ex.Code);
            // swapped for a different file after install
            File.WriteAllBytes(tools.FfmpegPath, new byte[bytes.Length]);
            ex = await Refused(() => tools.RunAsync(new[] { "-version" }, TimeSpan.FromSeconds(10)));
            Ensure(ex.Code == ToolException.HashMismatch, "replaced file");
            // a missing program
            File.Delete(tools.FfmpegPath);
            ex = await Refused(() => tools.RunAsync(new[] { "-version" }, TimeSpan.FromSeconds(10)));
            Ensure(ex.Code == ToolException.NotInstalled, "deleted program");
        }
        finally { FakeTool.ResetEnvironment(); }
    }

    private static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }

    public static async Task PathSettingDefaultsOff((string Download, string State) paths)
    {
        var dir = Path.Combine(paths.State, "pathdir"); Directory.CreateDirectory(dir);
        File.Copy(FakeTool.ExePath, Path.Combine(dir, "ffmpeg.exe"));
        _ = new ToolManagerShim(dir);
        var old = Environment.GetEnvironmentVariable("PATH");
        var log = FakeTool.NewLog(paths.State);
        try
        {
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + old);
            var tools = new ToolManager(Path.Combine(paths.State, "s"));
            Ensure(!tools.UseFromPath && !tools.IsAvailable && !tools.IsInstalled, "PATH is not used by default");
            var ex = await Refused(() => tools.RunAsync(new[] { "-version" }, TimeSpan.FromSeconds(10)));
            Ensure(ex.Code == ToolException.NotInstalled && FakeFfmpeg.ReadLog(log).Count == 0, "not started from PATH when the setting is off");
            tools.UseFromPath = true;
            Ensure(tools.IsAvailable && !tools.IsInstalled && tools.GetVersion() == "PATH", "available once the user enabled it");
            var run = await tools.RunAsync(new[] { "-version" }, TimeSpan.FromSeconds(30));
            Ensure(run.ExitCode == 0 && FakeFfmpeg.ReadLog(log).Count == 1, "runs from PATH when enabled");
        }
        finally { Environment.SetEnvironmentVariable("PATH", old); FakeTool.ResetEnvironment(); }
    }

    // helper: makes a folder runnable like an installed tool (the libraries beside the program)
    private sealed class ToolManagerShim
    {
        public ToolManager Manager { get; }
        public ToolManagerShim(string dir)
        {
            foreach (var name in new[] { "NDM.Tests.dll", "NDM.Tests.runtimeconfig.json", "NDM.Tests.deps.json", "NDM.Core.dll" })
            { var from = Path.Combine(AppContext.BaseDirectory, name); if (File.Exists(from)) File.Copy(from, Path.Combine(dir, name), true); }
            Manager = new ToolManager(Path.Combine(dir, "unused-state"));
        }
    }

    public static Task Commands()
    {
        var mux = FfmpegCommands.MuxTracks("C:\\w\\video.mp4", "C:\\w\\audio.m4a", "C:\\w\\out.mp4", false);
        Ensure(mux.SequenceEqual(new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", "C:\\w\\video.mp4", "-i", "C:\\w\\audio.m4a", "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", "-movflags", "+faststart", "C:\\w\\out.mp4" }), "mux template: " + string.Join(" ", mux));
        var muxTs = FfmpegCommands.MuxTracks("v.ts", "a.aac", "o.mp4", true);
        Ensure(muxTs.SequenceEqual(new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", "v.ts", "-i", "a.aac", "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", "-bsf:a", "aac_adtstoasc", "-movflags", "+faststart", "o.mp4" }), "mux template with ADTS: " + string.Join(" ", muxTs));
        var webm = FfmpegCommands.MuxTracks("v.webm", "a.webm", "o.webm", false);
        Ensure(!webm.Contains("-movflags") && webm.Contains("-c") && webm[webm.IndexOf("-c") + 1] == "copy", "no faststart for webm; always copy");
        var wrap = FfmpegCommands.Rewrap("in.ts", "out.mp4", false, true);
        Ensure(wrap.SequenceEqual(new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", "in.ts", "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-bsf:a", "aac_adtstoasc", "-movflags", "+faststart", "out.mp4" }), "rewrap template: " + string.Join(" ", wrap));
        var wrapAudio = FfmpegCommands.Rewrap("in.ts", "out.m4a", true, false);
        Ensure(wrapAudio.SequenceEqual(new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", "in.ts", "-vn", "-c", "copy", "-movflags", "+faststart", "out.m4a" }), "audio rewrap: " + string.Join(" ", wrapAudio));
        Ensure(!mux.Concat(muxTs).Concat(wrap).Any(a => a is "-c:v" or "-c:a" or "libx264" or "libx265" or "-vf" or "-af" or "-crf" or "-b:v"), "never re-encodes");
        Ensure(FfmpegCommands.Probe("f.mp4").SequenceEqual(new[] { "-hide_banner", "-nostdin", "-i", "f.mp4" }), "probe");
        Ensure(FfmpegCommands.ParseDuration("  Duration: 00:01:02.50, start: 0.0, bitrate: 5 kb/s") is { } d && Math.Abs(d - 62.5) < 1e-9, "duration text");
        Ensure(FfmpegCommands.ParseDuration("Duration: N/A") is null && FfmpegCommands.ParseDuration("") is null, "no duration");
        return Task.CompletedTask;
    }

    public static Task PinConstants()
    {
        var pin = ToolManager.FfmpegPin;
        var uri = new Uri(pin.Url);
        Ensure(uri.Scheme == "https" && uri.Host == "www.gyan.dev" && uri.AbsolutePath.Contains(pin.Version) && uri.AbsolutePath.EndsWith(".zip"), "pinned address is a versioned https release: " + pin.Url);
        Ensure(pin.ArchiveSha256.Length == 64 && pin.ExeSha256.Length == 64 && pin.ArchiveSha256.All(Uri.IsHexDigit) && pin.ExeSha256.All(Uri.IsHexDigit) && pin.ArchiveSha256 == pin.ArchiveSha256.ToLowerInvariant(), "hashes are lowercase SHA-256");
        Ensure(pin.ArchiveSize is > 1_000_000 and < ToolManager.MaxArchiveBytes && pin.ExeSize is > 1_000_000 and < ToolManager.MaxExeBytes && pin.EntryName.EndsWith("/bin/ffmpeg.exe") && pin.EntryName.Contains(pin.Version), "sizes and entry");
        var root = FindRepoRoot();
        if (root is null) { Console.WriteLine("    (docs not found; THIRD-PARTY.md check skipped)"); return Task.CompletedTask; }
        var doc = File.ReadAllText(Path.Combine(root, "docs", "THIRD-PARTY.md"));
        Ensure(doc.Contains(pin.Url) && doc.Contains(pin.ArchiveSha256) && doc.Contains(pin.ExeSha256) && doc.Contains(pin.Version) && doc.Contains(pin.ArchiveSize.ToString()), "docs/THIRD-PARTY.md states the pinned address, version, size and both hashes");
        Ensure(doc.Contains("GPL") && doc.Contains("https://ffmpeg.org") && doc.Contains("source", StringComparison.OrdinalIgnoreCase), "licence and source offer are stated");
        return Task.CompletedTask;
    }

    public static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        return dir?.FullName;
    }
}
