using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using NDM.App;

internal static partial class SecurityTests
{
    private static string PipeId() => "NDM.Test." + Guid.NewGuid().ToString("N");
    private static async Task<string> Ask(string pipe, string json, int timeoutMs = 4000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeoutMs, cts.Token);
        await client.WriteAsync(Encoding.UTF8.GetBytes(json + "\n"), cts.Token); await client.FlushAsync(cts.Token);
        return Encoding.UTF8.GetString(await NativeBridge.ReadRequestAsync(client, cts.Token));
    }

    private static void PipeTests()
    {
        // source level: both ends carry the flags
        string bridgeSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", "NativeBridge.cs"));
        Check(bridgeSource.Contains("PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance") && bridgeSource.Contains("new NamedPipeClientStream(\".\", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)"), "Pipe: the server's first instance is FirstPipeInstance + CurrentUserOnly, the control client is CurrentUserOnly");

        // normal start: first instance created, self-check and a capture round trip work
        string name = PipeId(); var received = new List<NativeBridge.BrowserDownloadRequest>();
        using (var bridge = new NativeBridge(r => { lock (received) received.Add(r); r.Respond(new NativeBridge.BrowserDownloadResponse(true, "job-1")); }, name, c => null))
        {
            bridge.Start();
            var self = Sync(NativeBridge.SendControlAsync(name, NativeBridge.ActionSelfCheck, null, TimeSpan.FromSeconds(5)));
            Check(self.Ok && !bridge.PipeNameTaken, "Pipe: a normal start listens; the CurrentUserOnly client connects to it and the self-check answers");
            var reply = Sync(Ask(name, "{\"url\":\"https://example.org/a.zip\",\"fileName\":\"a.zip\"}"));
            Check(reply.Contains("\"ok\":true") && reply.Contains("job-1") && received.Count == 1, "Pipe: a capture request is delivered and answered: " + reply);
        }

        // someone already owns the name: no captures are accepted and the owner is told about it
        string taken = PipeId(); int delivered = 0; Exception? reported = null;
        using (var squatter = new NamedPipeServerStream(taken, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        using (var bridge = new NativeBridge(r => Interlocked.Increment(ref delivered), taken, c => null) { FirstInstanceAttempts = 3, OnPipeNameTaken = e => reported = e })
        {
            bridge.Start();
            PumpUntil(() => reported is not null, "the squatted name was not detected", 15);
            Check(bridge.PipeNameTaken && reported is UnauthorizedAccessException or IOException, "Pipe squatter: the first-instance create fails when the name is taken (" + reported?.GetType().Name + ") and the bridge says so");
            Pump(300);
            Check(Volatile.Read(ref delivered) == 0, "Pipe squatter: no capture is ever delivered while the name is owned by someone else");
            // the squatter's pipe was not used as ours
            Check(Throws<UnauthorizedAccessException>(() => { using var second = new NamedPipeServerStream(taken, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance); }) || Throws<IOException>(() => { using var second = new NamedPipeServerStream(taken, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance); }), "Pipe squatter: FirstPipeInstance is what detects it (control)");
        }

        // restart on the same name right away (the watchdog replaces the listener): the old instance lets go and the new one becomes the first
        string again = PipeId(); NativeBridge? last = null;
        for (int i = 0; i < 3; i++)
        {
            var next = new NativeBridge(r => r.Respond(new NativeBridge.BrowserDownloadResponse(true, "x")), again, c => null); next.Start();
            Check(Sync(NativeBridge.SendControlAsync(again, NativeBridge.ActionSelfCheck, null, TimeSpan.FromSeconds(8))).Ok && !next.PipeNameTaken, $"Pipe: replacing the listener {i + 1} on the same name works");
            last?.Dispose(); last = next;
        }
        last?.Dispose();

        // cap: at most 8 capture dialogs wait; the 9th request gets "busy" at once
        string cap = PipeId(); var pending = new List<NativeBridge.BrowserDownloadRequest>();
        using (var bridge = new NativeBridge(r => { lock (pending) pending.Add(r); }, cap, c => null))
        {
            bridge.Start();
            var open = new List<(NamedPipeClientStream Pipe, Task<string> Reply)>();
            for (int i = 0; i < NativeBridge.MaxPendingCaptures; i++)
            {
                var client = new NamedPipeClientStream(".", cap, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); client.Connect(5000);
                var line = Encoding.UTF8.GetBytes($"{{\"url\":\"https://example.org/f{i}.zip\"}}\n"); client.Write(line, 0, line.Length); client.Flush();
                open.Add((client, ReadLine(client)));
            }
            PumpUntil(() => { lock (pending) return pending.Count == NativeBridge.MaxPendingCaptures; }, "the first 8 captures were not queued");
            Check(bridge.PendingCaptures == NativeBridge.MaxPendingCaptures && open.All(o => !o.Reply.IsCompleted), "Cap: 8 captures wait for the user, none was answered");
            var busy = Sync(Ask(cap, "{\"url\":\"https://example.org/f9.zip\"}", 5000));
            using (var doc = JsonDocument.Parse(busy)) Check(!doc.RootElement.GetProperty("ok").GetBoolean() && doc.RootElement.GetProperty("error").GetString() == "busy" && !doc.RootElement.GetProperty("cancelled").GetBoolean(), "Cap: the 9th request gets {ok:false,error:\"busy\"} (the extension restores the download in the browser): " + busy);
            lock (pending) Check(pending.Count == NativeBridge.MaxPendingCaptures, "Cap: the busy request never reached the dialog code");
            // answering one frees a slot
            NativeBridge.BrowserDownloadRequest first; lock (pending) first = pending[0];
            first.Respond(new NativeBridge.BrowserDownloadResponse(true, "done"));
            PumpUntil(() => open[0].Reply.IsCompleted && bridge.PendingCaptures == NativeBridge.MaxPendingCaptures - 1, "a finished capture did not free its slot");
            var again2 = ReadyForNext(cap);
            PumpUntil(() => { lock (pending) return pending.Count == NativeBridge.MaxPendingCaptures + 1; }, "a new request after a slot was freed was not accepted");
            Check(!again2.IsCompleted, "Cap: after a slot is freed the next request waits for its dialog again");
            foreach (var o in open) o.Pipe.Dispose();
            PumpUntil(() => bridge.PendingCaptures <= 1, "hung-up captures must be released", 10);
        }

        // control messages for torrent files: network and device paths are refused before they reach the app
        string ctl = PipeId(); var controls = new List<NativeBridge.ControlRequest>();
        using (var bridge = new NativeBridge(r => { }, ctl, c => { lock (controls) controls.Add(c); return null; }))
        {
            bridge.Start();
            foreach (var bad in new[] { @"\\\\server\\share\\a.torrent", @"\\\\?\\C:\\a.torrent", @"\\\\.\\C:\\a.torrent", @"//server/share/a.torrent" })
            {
                var r = Sync(NativeBridge.SendControlAsync(ctl, NativeBridge.ActionAddTorrent, bad, TimeSpan.FromSeconds(5)));
                Check(!r.Ok && r.Error is not null, "Control add-torrent: refused for " + bad + " (" + r.Error + ")");
            }
            lock (controls) Check(controls.Count == 0, "Control add-torrent: refused paths never reached the app");
            var good = Sync(NativeBridge.SendControlAsync(ctl, NativeBridge.ActionAddTorrent, @"C:\\Users\\me\\Downloads\\a.torrent", TimeSpan.FromSeconds(5)));
            lock (controls) Check(good.Ok && controls.Count == 1 && controls[0].Value == @"C:\\Users\\me\\Downloads\\a.torrent", "Control add-torrent: a plain local path goes through");
        }
        Check(NDM.Torrent.TorrentValidator.IsPlainLocalPath(@"C:\\x\\a.torrent") && !NDM.Torrent.TorrentValidator.IsPlainLocalPath(@"\\\\?\\C:\\x\\a.torrent"), "Command line: the same rule decides (TorrentValidator.IsPlainLocalPath)");
        var appSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", "App.xaml.cs"));
        Check(appSource.Contains("IsPlainLocalPath(file)") && appSource.Contains("AddTorrentFromCommandLine") && appSource.Contains("torrent.dialog.path_not_local"), "Command line: both the first launch and a second launch apply the rule");
        Task<string> ReadyForNext(string pipe)
        {
            var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); client.Connect(5000);
            var line = Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/next.zip\"}\n"); client.Write(line, 0, line.Length); client.Flush();
            return ReadLine(client);
        }
    }

    private static async Task<string> ReadLine(Stream stream)
    {
        try { return Encoding.UTF8.GetString(await NativeBridge.ReadRequestAsync(stream, CancellationToken.None)); }
        catch (Exception ex) when (ex is IOException or FormatException or ObjectDisposedException) { return "closed"; }
    }
}
