using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NDM.App;
using NDM.Core;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
string testPipe = "NDM.ProtocolTest." + Guid.NewGuid().ToString("N");
async Task<JsonElement> Send(string message)
{
    using var client = new NamedPipeClientStream(".", testPipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await client.ConnectAsync(timeout.Token);
    await client.WriteAsync(Encoding.UTF8.GetBytes(message + "\n"), timeout.Token);
    await client.FlushAsync(timeout.Token);
    using var reader = new StreamReader(client);
    var reply = await reader.ReadLineAsync(timeout.Token) ?? throw new Exception("No reply");
    return JsonDocument.Parse(reply).RootElement.Clone();
}

// Independent temp state; no fixture downloads are inserted in the real user's queue.
string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch");
string root = Path.Combine(scratch, "NDM-AppTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var serverStop = new CancellationTokenSource();
var server = new TcpListener(IPAddress.Loopback, 0);
server.Start();
int port = ((IPEndPoint)server.LocalEndpoint).Port;
byte[] content = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("NDM genuine fixture download\n", 1000)));
var serverTask = Task.Run(async () =>
{
    try
    {
        while (!serverStop.IsCancellationRequested)
        {
            using var client = await server.AcceptTcpClientAsync(serverStop.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string? line;
            do { line = await reader.ReadLineAsync(serverStop.Token); } while (!string.IsNullOrEmpty(line));
            byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {content.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, serverStop.Token);
            await stream.WriteAsync(content, serverStop.Token);
        }
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
});
try
{
    NativeBridge.ValidateUrl($"http://127.0.0.1:{port}/fixture.bin");
    foreach (var bad in new[] { "file:///C:/secret", "javascript:alert(1)", "not a url" })
    {
        bool rejected = false;
        try { NativeBridge.ValidateUrl(bad); } catch (FormatException) { rejected = true; }
        Check(rejected, "Reject URL: " + bad);
    }
    using (var memory = new MemoryStream(Encoding.UTF8.GetBytes("{}\ntrailing")))
        Check(Encoding.UTF8.GetString(await NativeBridge.ReadRequestAsync(memory, default)) == "{}", "Read exactly one newline-delimited request");
    using (var memory = new MemoryStream(Enumerable.Repeat((byte)'x', NativeBridge.MaximumRequestBytes).Append((byte)'\n').ToArray()))
        Check((await NativeBridge.ReadRequestAsync(memory, default)).Length == NativeBridge.MaximumRequestBytes, "Accept 64 KiB framing boundary");
    bool oversize = false;
    using (var memory = new MemoryStream(Enumerable.Repeat((byte)'x', NativeBridge.MaximumRequestBytes + 1).Append((byte)'\n').ToArray()))
        try { await NativeBridge.ReadRequestAsync(memory, default); } catch (FormatException) { oversize = true; }
    Check(oversize, "Reject oversized framing");
    bool missingNewline = false;
    using (var memory = new MemoryStream(Encoding.UTF8.GetBytes("{}")))
        try { await NativeBridge.ReadRequestAsync(memory, default); } catch (FormatException) { missingNewline = true; }
    Check(missingNewline, "Reject unterminated request");
    var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "files"), Theme = "Dark" };
    settings.Save(root);
    var restored = AppSettings.Load(root);
    Check(restored.Theme == "Dark" && restored.DownloadDirectory == settings.DownloadDirectory, "Settings durable round-trip");
    File.WriteAllText(Path.Combine(root, "settings.json"), "invalid");
    Check(AppSettings.Load(root).Theme == "Light", "Recover malformed settings");
    File.WriteAllText(Path.Combine(root, "settings.json"), "{\"Theme\":\"Midnight\",\"AccentOverride\":\"#7c6cf2\"}");
    Check(AppSettings.Load(root) is { Theme: "Midnight", AccentOverride: "#7C6CF2" }, "Theme id and accent override load and normalise");
    File.WriteAllText(Path.Combine(root, "settings.json"), "{\"Theme\":\"Neon\",\"AccentOverride\":\"red\"}");
    Check(AppSettings.Load(root) is { Theme: "Light", AccentOverride: null }, "Unknown theme falls back to Light and a bad accent is dropped");
    Check(ThemeCatalog.Resolve("System", () => true) == "Dark" && ThemeCatalog.Resolve("System", () => false) == "Light", "System theme resolves from the injected OS setting");
    using var manager = new DownloadManager(Path.Combine(root, "files"), Path.Combine(root, "state"));
    
    // Create a bridge that auto-accepts all requests (simulating user clicking "Start Download")
    using var bridge = new NativeBridge(request =>
    {
        Task.Run(async () =>
        {
            try
            {
                var downloadRequest = new DownloadRequest(request.Url, request.FileName, request.Referer, request.Cookie);
                var job = await manager.AddAsync(downloadRequest);
                request.Respond(new NativeBridge.BrowserDownloadResponse(true, job.Id));
            }
            catch (Exception ex)
            {
                request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, ex.Message));
            }
        });
    }, testPipe);
    bridge.Start();
    Check(!(await Send("not json")).GetProperty("ok").GetBoolean(), "Pipe replies to malformed JSON");
    Check(!(await Send("{\"url\":42}")).GetProperty("ok").GetBoolean(), "Pipe rejects non-string URL");
    Check(!(await Send("{\"url\":\"file:///C:/secret\"}")).GetProperty("ok").GetBoolean(), "Pipe rejects non-HTTP scheme");
    var accepted = await Send(JsonSerializer.Serialize(new { url = $"http://127.0.0.1:{port}/fixture.bin", fileName = "fixture.bin", referer = $"http://127.0.0.1:{port}/", cookie = "test=fixture" }));
    Check(accepted.GetProperty("ok").GetBoolean(), "Pipe accepts real download");
    string id = accepted.GetProperty("id").GetString()!;
    Check(File.ReadAllText(Path.Combine(root, "state", "queue.json")).Contains(id), "Success reply follows durable queue acceptance");
    using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (manager.Jobs.Single(job => job.Id == id).Status != "Completed") await Task.Delay(100, completion.Token);
    var job = manager.Jobs.Single(job => job.Id == id);
    Check(File.ReadAllBytes(job.FilePath).SequenceEqual(content), "Accepted download completes with exact fixture bytes");
    var emptyHeaders = await Send(JsonSerializer.Serialize(new { url = $"http://127.0.0.1:{port}/empty.bin", fileName = "empty.bin", referer = "", cookie = "" }));
    Check(emptyHeaders.GetProperty("ok").GetBoolean(), "Native-host empty optional headers normalize to null");
    Check(manager.Jobs.Any(j => j.Id == emptyHeaders.GetProperty("id").GetString()), "Empty-header acceptance belongs to isolated test manager");
    
    // ---- 1.5.4: the Cookie header the browser sent (up to 8192 characters) reaches the app intact; control characters and over-long values are refused
    {
        string cookiePipe = "NDM.ProtocolCookie." + Guid.NewGuid().ToString("N");
        string? seenCookie = null;
        using var cookieBridge = new NativeBridge(r => { seenCookie = r.Cookie; r.Respond(new NativeBridge.BrowserDownloadResponse(true, "cookie-job")); }, cookiePipe);
        cookieBridge.Start();
        async Task<JsonElement> Ask(object payload)
        {
            using var c = new NamedPipeClientStream(".", cookiePipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await c.ConnectAsync(t.Token);
            await c.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload) + (char)10)); await c.FlushAsync();
            using var reader = new StreamReader(c); return JsonDocument.Parse((await reader.ReadLineAsync())!).RootElement.Clone();
        }
        var big = "g2cn=1; ukey=" + new string('k', 8192 - 13);
        Check((await Ask(new { url = "http://127.0.0.1/dl/a.dat", cookie = big })).GetProperty("ok").GetBoolean() && seenCookie == big && seenCookie!.Length == 8192, "8192 character cookie reaches the app intact");
        seenCookie = "unchanged";
        Check(!(await Ask(new { url = "http://127.0.0.1/dl/a.dat", cookie = "a=b\r\nHost: evil" })).GetProperty("ok").GetBoolean() && seenCookie == "unchanged", "cookie with a line break is refused");
        Check(!(await Ask(new { url = "http://127.0.0.1/dl/a.dat", cookie = new string('x', NativeBridge.MaximumCookieLength + 1) })).GetProperty("ok").GetBoolean() && seenCookie == "unchanged", "over-long cookie is refused");
    }
    // ---- 1.5.6: the optional extId (extension that called the host: store or development copy) is accepted when it is 32 letters a-p; an older host sends none
    {
        string idPipe = "NDM.ProtocolExtId." + Guid.NewGuid().ToString("N");
        string? seenId = "unset"; int seenCount = 0;
        using var idBridge = new NativeBridge(r => { seenId = r.ExtId; seenCount++; r.Respond(new NativeBridge.BrowserDownloadResponse(true, "id-job")); }, idPipe);
        idBridge.Start();
        async Task<JsonElement> AskId(string json)
        {
            using var c = new NamedPipeClientStream(".", idPipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await c.ConnectAsync(t.Token);
            await c.WriteAsync(Encoding.UTF8.GetBytes(json + (char)10)); await c.FlushAsync();
            using var reader = new StreamReader(c); return JsonDocument.Parse((await reader.ReadLineAsync())!).RootElement.Clone();
        }
        Check((await AskId("{\"url\":\"http://127.0.0.1/dl/a.dat\",\"extVersion\":\"1.5.6\",\"extId\":\"ojbaibjfbdjodgmfbiajkigpanbdkiid\"}")).GetProperty("ok").GetBoolean() && seenId == "ojbaibjfbdjodgmfbiajkigpanbdkiid", "protocol: the store extension id reaches the app");
        Check((await AskId("{\"url\":\"http://127.0.0.1/dl/a.dat\",\"extId\":\"hohmmojlhbkelkobobpkochpdempcihd\"}")).GetProperty("ok").GetBoolean() && seenId == "hohmmojlhbkelkobobpkochpdempcihd", "protocol: the development extension id reaches the app");
        Check((await AskId("{\"url\":\"http://127.0.0.1/dl/a.dat\"}")).GetProperty("ok").GetBoolean() && seenId is null, "protocol: a request without extId (older host) still works");
        int before = seenCount;
        Check(!(await AskId("{\"url\":\"http://127.0.0.1/dl/a.dat\",\"extId\":\"nope\"}")).GetProperty("ok").GetBoolean() && !(await AskId("{\"url\":\"http://127.0.0.1/dl/a.dat\",\"extId\":12}")).GetProperty("ok").GetBoolean() && seenCount == before, "protocol: an invalid extId is refused before it reaches the app");
    }
    // ---- 1.5.7: the optional userAgent / accept / acceptLanguage (the browser's request identity) reach the app; control characters, non-ASCII and over-long values are refused
    {
        string identityPipe = "NDM.ProtocolIdentity." + Guid.NewGuid().ToString("N");
        string? seenAgent = "unset", seenAccept = "unset", seenLanguage = "unset"; int seen = 0;
        using var identityBridge = new NativeBridge(r => { seenAgent = r.UserAgent; seenAccept = r.Accept; seenLanguage = r.AcceptLanguage; seen++; r.Respond(new NativeBridge.BrowserDownloadResponse(true, "identity-job")); }, identityPipe);
        identityBridge.Start();
        async Task<JsonElement> AskIdentity(string json)
        {
            using var c = new NamedPipeClientStream(".", identityPipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await c.ConnectAsync(t.Token);
            await c.WriteAsync(Encoding.UTF8.GetBytes(json + (char)10)); await c.FlushAsync();
            using var reader = new StreamReader(c); return JsonDocument.Parse((await reader.ReadLineAsync())!).RootElement.Clone();
        }
        string agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";
        Check((await AskIdentity(JsonSerializer.Serialize(new { url = "http://127.0.0.1/dl/a.dat", userAgent = agent, accept = "text/html,*/*;q=0.8", acceptLanguage = "de-DE,de;q=0.9" }))).GetProperty("ok").GetBoolean() && seenAgent == agent && seenAccept == "text/html,*/*;q=0.8" && seenLanguage == "de-DE,de;q=0.9", "protocol: the browser's User-Agent, Accept and Accept-Language reach the app");
        Check((await AskIdentity("{\"url\":\"http://127.0.0.1/dl/a.dat\"}")).GetProperty("ok").GetBoolean() && seenAgent is null && seenAccept is null && seenLanguage is null, "protocol: a request without them (older extension) still works");
        Check((await AskIdentity(JsonSerializer.Serialize(new { url = "http://127.0.0.1/dl/a.dat", userAgent = new string('u', 512), accept = new string('a', 512), acceptLanguage = new string('l', 128) }))).GetProperty("ok").GetBoolean() && seenAgent!.Length == 512 && seenAccept!.Length == 512 && seenLanguage!.Length == 128, "protocol: the limits themselves (512, 512, 128) are accepted");
        int identityBefore = seen; bool allRefused = true;
        foreach (var field in new[] { "userAgent", "accept", "acceptLanguage" })
            foreach (var value in new object[] { "a\r\nHost: evil", "a\u0000b", "a\u007fb", "caf\u00e9", "\u2028", 7, new string('x', field == "acceptLanguage" ? 129 : 513) })
                allRefused &= !(await AskIdentity(JsonSerializer.Serialize(new Dictionary<string, object> { ["url"] = "http://127.0.0.1/dl/a.dat", [field] = value }))).GetProperty("ok").GetBoolean();
        Check(allRefused && seen == identityBefore, "protocol: control characters, non-ASCII text, non-strings and over-long values are refused before they reach the app");
    }
    // ---- 1.3.3: waiting for the user is unbounded (past the old 30 s pipe ceiling), several captures wait at once, a hang-up is reported
    {
        string slowPipe = "NDM.ProtocolSlow." + Guid.NewGuid().ToString("N");
        var pending = new System.Collections.Concurrent.ConcurrentDictionary<string, NativeBridge.BrowserDownloadRequest>();
        using var slow = new NativeBridge(r => pending[r.Url] = r, slowPipe);
        slow.Start();
        async Task<(NamedPipeClientStream Pipe, StreamReader Reader)> Open(string url)
        {
            var c = new NamedPipeClientStream(".", slowPipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await c.ConnectAsync(t.Token);
            await c.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { url }) + (char)10)); await c.FlushAsync();
            return (c, new StreamReader(c));
        }
        async Task WaitFor(string url) { for (int i = 0; i < 200 && !pending.ContainsKey(url); i++) await Task.Delay(50); }
        var (pa, ra) = await Open("http://127.0.0.1/a"); var (pb, rb) = await Open("http://127.0.0.1/b");
        await WaitFor("http://127.0.0.1/a"); await WaitFor("http://127.0.0.1/b");
        Check(pending.Count == 2, "Two captures wait for their dialogs at the same time");
        var lineA = ra.ReadLineAsync();
        await Task.Delay(TimeSpan.FromSeconds(32));
        Check(!lineA.IsCompleted && !pending["http://127.0.0.1/a"].Gone.IsCancellationRequested, "Still waiting after 32 s (no pipe time limit while the user decides)");
        pending["http://127.0.0.1/b"].Respond(new NativeBridge.BrowserDownloadResponse(true, "idB"));
        Check(JsonDocument.Parse((await rb.ReadLineAsync())!).RootElement.GetProperty("id").GetString() == "idB", "Second capture answered independently of the first");
        pb.Dispose();
        pending["http://127.0.0.1/a"].Respond(new NativeBridge.BrowserDownloadResponse(true, "idA"));
        Check(JsonDocument.Parse((await lineA)!).RootElement.GetProperty("id").GetString() == "idA", "First capture answered after the long wait");
        pa.Dispose();
        var (pc, rc) = await Open("http://127.0.0.1/c"); await WaitFor("http://127.0.0.1/c");
        var gone = pending["http://127.0.0.1/c"].Gone;
        Check(!gone.IsCancellationRequested, "Connected client: Gone not signalled");
        pc.Dispose();
        for (int i = 0; i < 100 && !gone.IsCancellationRequested; i++) await Task.Delay(50);
        Check(gone.IsCancellationRequested, "Client hang-up while the dialog is open is signalled (dialog is told the capture expired)");
    }

    // Run category folder organization tests
    CategoryTest.Run();
    
    Console.WriteLine("ALL APP PROTOCOL TESTS PASSED");
}
finally
{
    serverStop.Cancel(); server.Stop(); await serverTask;
    // Engine disposal and bridge cancellation release resources after this scope exits.
    Console.WriteLine("Isolated test artifacts: " + root);
}
