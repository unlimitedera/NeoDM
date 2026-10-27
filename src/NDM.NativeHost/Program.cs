using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

internal static class Program
{
    const int Max = 65536;
    /// <summary>Longest address accepted from the extension. Redirect targets carry long tokens (a few hundred characters); with the cookie (32768) and referer (16384) fields a request still fits the 64 KiB frame.</summary>
    internal const int MaxUrl = 8192;
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint processId);
    /// <summary>The browser (foreground) started this host, so the host may pass the right to take the foreground to the app process that will show the options dialog.</summary>
    static void AllowAppForeground(NamedPipeClientStream pipe)
    {
        try { if (GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid) && pid != 0) AllowSetForegroundWindow(pid); } catch { }
    }
    /// <summary>bridge-config.json. <c>extensionId</c> is the single (development) ID of every config written before 1.5.6 and stays valid on its own;
    /// <c>extensionIds</c> (1.5.6) lists every trusted ID, for example the development ID and the Chrome Web Store ID.</summary>
    internal record Config(string? ExtensionId, string? AppPath, string[]? ExtensionIds = null);
    internal static bool ValidId(string? id) => id is { Length: 32 } && id.All(c => c >= 'a' && c <= 'p');
    /// <summary>Every trusted extension ID of the config: <c>extensionId</c> first, then <c>extensionIds</c>; entries that are not 32 letters a-p are dropped.</summary>
    internal static IReadOnlyList<string> AllowedIds(Config config) =>
        new[] { config.ExtensionId }.Concat(config.ExtensionIds ?? Array.Empty<string>()).Where(ValidId).Select(i => i!).Distinct(StringComparer.Ordinal).ToList();
    /// <summary>The extension ID in the origin argument the browser passes (<c>chrome-extension://ID/</c>) when it is one of the trusted IDs, else null.</summary>
    internal static string? CallerId(Config config, string[] args)
    {
        if (args.Length < 1) return null;
        foreach (var id in AllowedIds(config)) if (args[0] == $"chrome-extension://{id}/") return id;
        return null;
    }
    static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") return await SelfTest();
        try
        {
            var config = JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "bridge-config.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException();
            var callerId = CallerId(config, args);
            if (callerId is null) return 2;
            using var input = Console.OpenStandardInput(); using var output = Console.OpenStandardOutput();
            while (true)
            {
                byte[]? request;
                try { request = await ReadFrame(input); } catch { await WriteFrame(output, Error("Invalid or oversized native frame")); return 3; }
                if (request == null) return 0;
                byte[] reply;
                try { reply = await Handle(request, config, pending: frame => WriteFrame(output, frame), callerId: callerId); }
                catch { reply = Error("Bridge unavailable or request rejected; browser download remains available"); }
                await WriteFrame(output, reply);
            }
        }
        catch { return 1; } // Never emit diagnostics or secrets on stdout.
    }
    static byte[] Error(string text) => JsonSerializer.SerializeToUtf8Bytes(new { ok = false, error = text });
    internal static async Task<byte[]?> ReadFrame(Stream input)
    {
        byte[] header = new byte[4];
        int first = await input.ReadAsync(header.AsMemory(0, 4));
        if (first == 0) return null;
        await input.ReadExactlyAsync(header.AsMemory(first, 4-first));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > Max) throw new InvalidDataException();
        byte[] payload = new byte[(int)length]; await input.ReadExactlyAsync(payload); return payload;
    }
    internal static async Task WriteFrame(Stream output, byte[] payload)
    {
        byte[] header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await output.WriteAsync(header); await output.WriteAsync(payload); await output.FlushAsync();
    }
    static string Field(JsonElement root, string name, int max = 16384)
    {
        if (!root.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return "";
        if (field.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        var value = field.GetString()!; if (value.Length > max || value.Any(char.IsControl)) throw new InvalidDataException(); return value;
    }
    /// <summary>1.5.7 request identity fields (userAgent, accept, acceptLanguage): printable ASCII only (space to tilde), so nothing but plain text can reach a header.</summary>
    internal static bool PrintableAscii(string value) => value.All(c => c >= 0x20 && c <= 0x7e);
    /// <summary>Version of the extension as sent in the optional extVersion field: 2 to 4 numeric parts of 1 to 4 digits, at most 24 characters.</summary>
    internal static bool ValidVersion(string value) => value.Length <= 24 && System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{1,4}(\.\d{1,4}){1,3}\z");
    /// <summary>Largest browser-reported size accepted: 2^50 bytes.</summary>
    internal const long MaxSize = 1L << 50;
    /// <summary>Request that returns (and clears) the host names the app found it cannot fetch (a captured download that came back as a web page).</summary>
    internal const string BrowserSitesAction = "browser-sites";
    internal static string? BrowserSitesFileOverride;
    /// <summary>The app writes one host name per line to browser-sites.txt in its state folder (NDM_STATE_DIR when set); this reads and removes the file. Never starts the app.</summary>
    static byte[] TakeBrowserSites()
    {
        var sites = new List<string>();
        try
        {
            var dir = Environment.GetEnvironmentVariable("NDM_STATE_DIR") is { Length: > 0 } custom ? Path.GetFullPath(custom) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NDM");
            var file = BrowserSitesFileOverride ?? Path.Combine(dir, "browser-sites.txt");
            using var gate = new Mutex(false, @"Local\NDM.BrowserSites");
            bool held = false;
            try { held = gate.WaitOne(2000); } catch (AbandonedMutexException) { held = true; }
            try
            {
                if (File.Exists(file) && new FileInfo(file).Length <= 16384)
                {
                    foreach (var line in File.ReadAllLines(file))
                    {
                        var host = line.Trim().ToLowerInvariant();
                        if (host.Length is > 0 and <= 253 && host.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-') && !sites.Contains(host) && sites.Count < 100) sites.Add(host);
                    }
                    File.Delete(file);
                }
            }
            finally { if (held) gate.ReleaseMutex(); }
        }
        catch { }
        return JsonSerializer.SerializeToUtf8Bytes(new { ok = true, sites });
    }
    internal static string? AppVersionOverride;
    /// <summary>The installed app's product version (x.y.z) from NeoDM.exe, or null if the file is missing or has no usable version.</summary>
    static string? AppVersion(Config config)
    {
        if (AppVersionOverride is not null) return AppVersionOverride;
        try
        {
            var app = config.AppPath ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../app/NeoDM.exe"));
            if (!Path.IsPathFullyQualified(app) || !File.Exists(app)) return null;
            var text = FileVersionInfo.GetVersionInfo(app).ProductVersion ?? "";
            text = text.Split('+')[0].Split('-')[0];
            return ValidVersion(text) ? text : null;
        }
        catch { return null; }
    }
    static bool Http(string value) => Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") && !string.IsNullOrEmpty(u.Host) && string.IsNullOrEmpty(u.UserInfo);
    /// <summary>Sent to the extension every <see cref="PendingInterval"/> while the app's dialog waits for the user: "NeoDM is alive, still deciding". Only sent to
    /// extensions that asked for it (<c>keepAlive:true</c> in the request); an older extension gets exactly one reply frame as before.</summary>
    internal static readonly byte[] PendingFrame = Encoding.UTF8.GetBytes("{\"pending\":true}");
    internal static TimeSpan PendingInterval = TimeSpan.FromSeconds(15);
    static async Task<byte[]> Handle(byte[] message, Config config, string pipeName = "NDM.Downloads", Func<byte[], Task>? pending = null, string? callerId = null)
    {
        using var doc = JsonDocument.Parse(message); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        if (root.TryGetProperty("action", out var action) && action.GetString() == "ping")
        {
            // appVersion is read from the app executable's file version (no pipe call, so a ping never starts or wakes the app); omitted when unknown.
            var appVersion = AppVersion(config);
            return appVersion is null ? JsonSerializer.SerializeToUtf8Bytes(new { ok = true, host = "com.ndm.bridge", keepAlive = true })
                : JsonSerializer.SerializeToUtf8Bytes(new { ok = true, host = "com.ndm.bridge", keepAlive = true, appVersion });
        }
        if (root.TryGetProperty("action", out var browserSites) && browserSites.GetString() == BrowserSitesAction) return TakeBrowserSites();
        if (root.TryGetProperty("method", out var method) && method.GetString() != "GET") throw new InvalidDataException();
        string url = Field(root,"url",MaxUrl), fileName = Field(root,"fileName",255), referer = Field(root,"referer"), cookie = Field(root,"cookie",32768), mime = Field(root,"mime",100);
        bool keepAlive = pending != null && root.TryGetProperty("keepAlive", out var ka) && ka.ValueKind == JsonValueKind.True;
        string extVersion = Field(root,"extVersion",24);
        // 1.5.7, optional: the User-Agent / Accept / Accept-Language the browser used for this download (not sensitive; never logged). Older extensions send none of them.
        string userAgent = Field(root,"userAgent",512), accept = Field(root,"accept",512), acceptLanguage = Field(root,"acceptLanguage",128);
        if (!PrintableAscii(userAgent) || !PrintableAscii(accept) || !PrintableAscii(acceptLanguage)) throw new InvalidDataException();
        bool noProbe = false;
        if (root.TryGetProperty("noProbe", out var np) && np.ValueKind != JsonValueKind.Null) { if (np.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException(); noProbe = np.ValueKind == JsonValueKind.True; }
        if (extVersion.Length > 0 && !ValidVersion(extVersion)) throw new InvalidDataException();
        long size = 0; // optional: the size the browser reported (DownloadItem.totalBytes or fileSize), 0..2^50; 0 = unknown
        if (root.TryGetProperty("size", out var sz) && sz.ValueKind != JsonValueKind.Null) { if (sz.ValueKind != JsonValueKind.Number || !sz.TryGetInt64(out size) || size < 0 || size > MaxSize) throw new InvalidDataException(); }
        if (!Http(url) || (referer.Length > 0 && !Http(referer))) throw new InvalidDataException();
        if (fileName.IndexOfAny(new[] {'/','\\',':','<','>','"','|','?','*'}) >= 0 || fileName is "." or "..") throw new InvalidDataException();
        if (mime.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(mime, @"^[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*/[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*$")) throw new InvalidDataException();
        // mime is optional: older extensions never send it and the app receives exactly the four original fields then.
        // extVersion is optional too: forwarded as the fifth/sixth field only when the extension sent a valid one.
        var forward = new Dictionary<string, object> { ["url"] = url, ["fileName"] = fileName, ["referer"] = referer, ["cookie"] = cookie };
        if (mime.Length > 0) forward["mime"] = mime;
        if (extVersion.Length > 0) forward["extVersion"] = extVersion;
        if (callerId is not null && ValidId(callerId)) forward["extId"] = callerId; // optional: which extension (store or development copy) sent this; an older app ignores it
        if (size > 0) forward["size"] = size;
        if (userAgent.Length > 0) forward["userAgent"] = userAgent;
        if (accept.Length > 0) forward["accept"] = accept;
        if (acceptLanguage.Length > 0) forward["acceptLanguage"] = acceptLanguage;
        if (noProbe) forward["noProbe"] = true; // the address came out of a form POST redirect and may be single use: the app must not request it to read the name
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(forward);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = deadline.Token;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { using var first = CancellationTokenSource.CreateLinkedTokenSource(ct); first.CancelAfter(300); await pipe.ConnectAsync(first.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var app = config.AppPath ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../app/NeoDM.exe"));
            if (!Path.IsPathFullyQualified(app) || !File.Exists(app)) throw new FileNotFoundException();
            Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app)! });
            await pipe.ConnectAsync(ct);
        }
        AllowAppForeground(pipe);
        // The app shows an options dialog and replies only after the user decides. A keep-alive extension waits without a limit while the app stays
        // connected (pending frames tell it so); a failed app connection (pipe closed / error) ends the wait at once. Older extensions: 320 s, one reply.
        using var decision = new CancellationTokenSource(keepAlive ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(320)); var rt = decision.Token;
        await pipe.WriteAsync(payload, rt); await pipe.WriteAsync(new byte[] {10}, rt); await pipe.FlushAsync(rt);
        async Task<MemoryStream> ReadReply()
        {
            var buffer = new MemoryStream(); byte[] one = new byte[1];
            while (true) { int n = await pipe.ReadAsync(one, rt); if (n == 0) throw new EndOfStreamException(); if (one[0] == 10) break; if (buffer.Length >= Max) throw new InvalidDataException(); buffer.WriteByte(one[0]); }
            return buffer;
        }
        var read = ReadReply();
        while (keepAlive)
        {
            if (await Task.WhenAny(read, Task.Delay(PendingInterval)) == read) break;
            await pending!(PendingFrame);
        }
        using var response = await read;
        var bytes = response.ToArray(); using var result = JsonDocument.Parse(bytes); var r = result.RootElement;
        if (!r.TryGetProperty("ok", out var ok) || (ok.ValueKind != JsonValueKind.True && ok.ValueKind != JsonValueKind.False)) throw new InvalidDataException();
        if (ok.GetBoolean()) { if (!r.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())) throw new InvalidDataException(); }
        else if (!r.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        if (r.TryGetProperty("handBack", out var hb) && hb.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException(); // the user chose "Download in browser instead"
        return bytes; // Preserve the application's acknowledgement JSON exactly.
    }
    static async Task<int> SelfTest()
    {
        var payload = Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"α\"}"); using var stream = new MemoryStream(); await WriteFrame(stream,payload); stream.Position=0;
        if (!(await ReadFrame(stream))!.SequenceEqual(payload) || await ReadFrame(stream) != null) return 1;
        foreach (var bytes in new[] {new byte[]{0,0,0,0}, new byte[]{1,0,1,0}, new byte[]{1,0}, new byte[]{2,0,0,0,1}})
        { try { await ReadFrame(new MemoryStream(bytes)); return 1; } catch (Exception e) when (e is InvalidDataException or EndOfStreamException) {} }
        foreach (string expected in new[] { "{\"ok\":true, \"id\":\"saved\"}", "{\"ok\":false,\"error\":\"disk full\"}" })
        {
            string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var serve = Task.Run(async () => {
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                var line = await reader.ReadLineAsync(); using var request = JsonDocument.Parse(line!);
                var r = request.RootElement;
                if (r.GetProperty("url").GetString() != "https://example.org/a" || r.GetProperty("cookie").GetString() != "test=1" || r.EnumerateObject().Count() != 4) throw new InvalidDataException();
                await server.WriteAsync(Encoding.UTF8.GetBytes(expected + "\n")); await server.FlushAsync();
            });
            var actual = await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"cookie\":\"test=1\"}"), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), name);
            await serve;
            if (Encoding.UTF8.GetString(actual) != expected) return 1;
        }
        {
            // Optional browser file name + mime are forwarded; malformed ones are rejected before anything reaches the app.
            string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var serve = Task.Run(async () => {
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                var r = request.RootElement;
                if (r.GetProperty("fileName").GetString() != "tool.zip" || r.GetProperty("mime").GetString() != "application/zip") throw new InvalidDataException();
                await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"m\"}" + (char)10)); await server.FlushAsync();
            });
            await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"fileName\":\"tool.zip\",\"mime\":\"application/zip\"}"), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), name);
            await serve;
            foreach (var bad in new[] { "{\"url\":\"https://example.org/a\",\"mime\":\"not a mime\"}", "{\"url\":\"https://example.org/a\",\"mime\":\"" + new string('a', 101) + "/b\"}", "{\"url\":\"https://example.org/a\",\"fileName\":\"a/b.zip\"}", "{\"url\":\"https://example.org/a\",\"mime\":5}" })
            { try { await Handle(Encoding.UTF8.GetBytes(bad), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), "NDM.SelfTest.none"); return 1; } catch (InvalidDataException) { } }
        }
        {
            // 1.5.4: the Cookie header the browser sent travels as the cookie field: up to 8192 characters as the extension sends it, 32768 at most; control characters are refused.
            foreach (int length in new[] { 8192, 32768 })
            {
                string big = "g2cn=1; " + new string('x', length - 8);
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () => {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                    if (request.RootElement.GetProperty("cookie").GetString() != big) throw new InvalidDataException();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"c\"}" + (char)10)); await server.FlushAsync();
                });
                await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"cookie\":" + JsonSerializer.Serialize(big) + "}"), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), name);
                await serve;
            }
            foreach (var bad in new[] { "{\"url\":\"https://example.org/a\",\"cookie\":\"a=b\\r\\nX: y\"}", "{\"url\":\"https://example.org/a\",\"cookie\":\"a=b\\u0000\"}", "{\"url\":\"https://example.org/a\",\"cookie\":\"" + new string('x', 32769) + "\"}", "{\"url\":\"https://example.org/a\",\"cookie\":7}" })
            { try { await Handle(Encoding.UTF8.GetBytes(bad), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), "NDM.SelfTest.none"); return 1; } catch (InvalidDataException) { } }
        }
        {
            // 1.5.7: the browser's request identity is forwarded as userAgent / accept / acceptLanguage (printable ASCII, 512 / 512 / 128 characters at most); others are refused.
            string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36", acc = "text/html,application/xhtml+xml,*/*;q=0.8", lang = "de-DE,de;q=0.9,en;q=0.8";
            foreach (var edge in new[] { (ua, acc, lang), (new string('u', 512), new string('a', 512), new string('l', 128)) })
            {
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () => {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                    var r = request.RootElement;
                    if (r.GetProperty("userAgent").GetString() != edge.Item1 || r.GetProperty("accept").GetString() != edge.Item2 || r.GetProperty("acceptLanguage").GetString() != edge.Item3) throw new InvalidDataException();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"i\"}" + (char)10)); await server.FlushAsync();
                });
                await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"userAgent\":" + JsonSerializer.Serialize(edge.Item1) + ",\"accept\":" + JsonSerializer.Serialize(edge.Item2) + ",\"acceptLanguage\":" + JsonSerializer.Serialize(edge.Item3) + "}"), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), name);
                await serve;
            }
            foreach (var bad in new[] { "userAgent", "accept", "acceptLanguage" })
                foreach (var value in new[] { "\"a\\r\\nB: c\"", "\"a\\u0000b\"", "\"a\\u007fb\"", "\"caf\\u00e9\"", "\"\\u2028\"", "7", "\"" + new string('x', bad == "acceptLanguage" ? 129 : 513) + "\"" })
                { try { await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"" + bad + "\":" + value + "}"), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), "NDM.SelfTest.none"); return 1; } catch (InvalidDataException) { } }
        }
        {
            // Keep-alive: a slow decision yields pending frames, then the app's real reply; without keepAlive the reply is a single frame and nothing else.
            var saved = PendingInterval; PendingInterval = TimeSpan.FromMilliseconds(40);
            try
            {
                foreach (var asked in new[] { true, false })
                {
                    string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                    using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    var serve = Task.Run(async () => {
                        await server.WaitForConnectionAsync();
                        using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                        using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                        if (request.RootElement.EnumerateObject().Count() != 4) throw new InvalidDataException(); // keepAlive is never forwarded to the app
                        await Task.Delay(400);
                        await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"slow\"}" + (char)10)); await server.FlushAsync();
                    });
                    var frames = new List<string>();
                    var request2 = "{\"url\":\"https://example.org/a\",\"cookie\":\"test=1\"" + (asked ? ",\"keepAlive\":true" : "") + "}";
                    var final = await Handle(Encoding.UTF8.GetBytes(request2), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), name, f => { frames.Add(Encoding.UTF8.GetString(f)); return Task.CompletedTask; });
                    await serve;
                    if (Encoding.UTF8.GetString(final) != "{\"ok\":true,\"id\":\"slow\"}") return 1;
                    if (asked ? frames.Count < 3 || frames.Any(f => f != "{\"pending\":true}") : frames.Count != 0) return 1;
                }
                {
                    // The app goes away while the user is deciding: the wait ends with an error instead of hanging.
                    string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                    using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    var serve = Task.Run(async () => { await server.WaitForConnectionAsync(); await Task.Delay(150); server.Dispose(); });
                    try { await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"keepAlive\":true}"), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), name, _ => Task.CompletedTask); return 1; }
                    catch (Exception e) when (e is EndOfStreamException or IOException) { }
                    await serve;
                }
            }
            finally { PendingInterval = saved; }
        }
        {
            // extVersion: valid values are forwarded to the app, malformed ones are rejected, a missing one leaves the original four fields.
            foreach (var good in new[] { "1.4.3", "1.4", "0.0.1", "1234.5678.9012.3456" })
            {
                if (!ValidVersion(good)) return 1;
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () => {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                    if (request.RootElement.GetProperty("extVersion").GetString() != good) throw new InvalidDataException();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"v\"}" + (char)10)); await server.FlushAsync();
                });
                await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"extVersion\":\"" + good + "\"}"), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), name);
                await serve;
            }
            foreach (var bad in new[] { "1", "1.4.3.2.1", "1.4.x", "v1.4.3", "1.4.3-beta", "12345.1", "1..2", " 1.4.3", "1.4.3\n", "1111.2222.3333.4444.5" , "1.4.3+abc", "" + new string('1', 25) })
            {
                if (bad.Length > 0 && ValidVersion(bad)) return 1;
                if (bad.Length == 0) continue;
                try { await Handle(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { url = "https://example.org/a", extVersion = bad })), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), "NDM.SelfTest.none"); return 1; } catch (InvalidDataException) { }
            }
            try { await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"extVersion\":14}"), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), "NDM.SelfTest.none"); return 1; } catch (InvalidDataException) { }
            // Address length: 400 and exactly 8192 characters reach the app unchanged, 8193 is rejected; noProbe is forwarded only when true and must be a boolean.
            foreach (var length in new[] { 400, 8192 })
            {
                string longUrl = "https://tunnel.example/download/" + new string('a', length - "https://tunnel.example/download/".Length);
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () => {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                    if (request.RootElement.GetProperty("url").GetString() != longUrl || !request.RootElement.GetProperty("noProbe").GetBoolean()) throw new InvalidDataException();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"l\"}" + (char)10)); await server.FlushAsync();
                });
                await Handle(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { url = longUrl, noProbe = true })), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), name);
                await serve;
            }
            try { await Handle(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { url = "https://tunnel.example/download/" + new string('a', 8193 - 32) })), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), "NDM.SelfTest.none"); return 1; } catch (InvalidDataException) { }
            try { await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"noProbe\":\"yes\"}"), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), "NDM.SelfTest.none"); return 1; } catch (InvalidDataException) { }
            // ping: appVersion comes from the app file version when known and is absent otherwise; old extensions ignore extra fields.
            AppVersionOverride = "1.4.3";
            using (var pong = JsonDocument.Parse(await Handle(Encoding.UTF8.GetBytes("{\"action\":\"ping\",\"extVersion\":\"1.4.2\"}"), new Config("hohmmojlhbkelkobobpkochpdempcihd", null))))
                if (pong.RootElement.GetProperty("appVersion").GetString() != "1.4.3" || !pong.RootElement.GetProperty("ok").GetBoolean()) return 1;
            AppVersionOverride = null;
            using (var pong = JsonDocument.Parse(await Handle(Encoding.UTF8.GetBytes("{\"action\":\"ping\"}"), new Config("hohmmojlhbkelkobobpkochpdempcihd", Path.Combine(Path.GetTempPath(), "missing", "NeoDM.exe")))))
                if (pong.RootElement.TryGetProperty("appVersion", out _) || !pong.RootElement.GetProperty("ok").GetBoolean()) return 1;
        }
        {
            // 1.5.4 size: the browser-reported size (0..2^50) is forwarded only when positive; negative, fractional, text and too large values are rejected.
            var cfg = new Config("hohmmojlhbkelkobobpkochpdempcihd", null);
            foreach (var (sent, expected) in new (string, long?)[] { ("108003328", 108003328L), ("1125899906842624", 1125899906842624L), ("0", null), ("null", null) })
            {
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () => {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                    bool has = request.RootElement.TryGetProperty("size", out var got);
                    if (expected is null ? has : !has || got.GetInt64() != expected) throw new InvalidDataException();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"s\"}" + (char)10)); await server.FlushAsync();
                });
                await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"size\":" + sent + "}"), cfg, name);
                await serve;
            }
            foreach (var bad in new[] { "-1", "1125899906842625", "1.5", "\"5\"", "true", "9223372036854775808" })
            { try { await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"size\":" + bad + "}"), cfg, "NDM.SelfTest.none"); return 1; } catch (InvalidDataException) { } }
            // handBack: the app's "hand the download back to the browser" reply passes through unchanged; a non-boolean value is refused.
            foreach (var (reply, good) in new[] { ("{\"ok\":false,\"error\":\"left\",\"cancelled\":false,\"handBack\":true}", true), ("{\"ok\":false,\"error\":\"left\",\"handBack\":\"yes\"}", false) })
            {
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () => { await server.WaitForConnectionAsync(); using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true); await reader.ReadLineAsync(); await server.WriteAsync(Encoding.UTF8.GetBytes(reply + (char)10)); await server.FlushAsync(); });
                try { var actual = await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\"}"), cfg, name); if (!good || Encoding.UTF8.GetString(actual) != reply) return 1; }
                catch (InvalidDataException) { if (good) return 1; }
                await serve;
            }
            // browser-sites: reads the app's list (host names only), de-duplicates, drops bad lines, removes the file; an absent file gives an empty list.
            var listFile = Path.Combine(Path.GetTempPath(), "ndm-sites-" + Guid.NewGuid().ToString("N") + ".txt");
            BrowserSitesFileOverride = listFile;
            try
            {
                using (var none = JsonDocument.Parse(await Handle(Encoding.UTF8.GetBytes("{\"action\":\"browser-sites\"}"), cfg))) if (none.RootElement.GetProperty("sites").GetArrayLength() != 0) return 1;
                File.WriteAllText(listFile, "mediafire.com\n" + "MediaFire.com\n" + "bad host!\n" + "\n" + "example.co.uk\n" + "<script>\n");
                using (var got = JsonDocument.Parse(await Handle(Encoding.UTF8.GetBytes("{\"action\":\"browser-sites\"}"), cfg)))
                    if (string.Join(",", got.RootElement.GetProperty("sites").EnumerateArray().Select(x => x.GetString())) != "mediafire.com,example.co.uk" || File.Exists(listFile)) return 1;
            }
            finally { BrowserSitesFileOverride = null; try { File.Delete(listFile); } catch { } }
        }
        {
            // 1.5.6 trusted IDs: the development ID and the Chrome Web Store ID are both accepted; a third ID, a wrong form and a missing origin are not.
            const string dev = "hohmmojlhbkelkobobpkochpdempcihd", store = "ojbaibjfbdjodgmfbiajkigpanbdkiid", third = "abcdefghijklmnopabcdefghijklmnop";
            var both = new Config(dev, null, new[] { dev, store });
            if (CallerId(both, new[] { $"chrome-extension://{dev}/" }) != dev || CallerId(both, new[] { $"chrome-extension://{store}/" }) != store) return 1;
            foreach (var bad in new[] { $"chrome-extension://{third}/", $"chrome-extension://{store}", $"chrome-extension://{store}/x", $"chrome-extension://{store.ToUpperInvariant()}/", store, "", "chrome-extension:///" })
                if (CallerId(both, new[] { bad }) is not null) return 1;
            if (CallerId(both, Array.Empty<string>()) is not null) return 1;
            // an old config with the single extensionId key still works and trusts only that ID
            var old = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(new { extensionId = dev, appPath = "C:/x/NeoDM.exe" }), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            if (CallerId(old, new[] { $"chrome-extension://{dev}/" }) != dev || CallerId(old, new[] { $"chrome-extension://{store}/" }) is not null) return 1;
            // a config with only extensionIds (no extensionId) works; invalid entries are dropped; nothing valid = nobody is trusted
            var listOnly = JsonSerializer.Deserialize<Config>("{\"extensionIds\":[\"" + store + "\",\"bad\",\"" + store + "\"]}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            if (AllowedIds(listOnly).Count != 1 || CallerId(listOnly, new[] { $"chrome-extension://{store}/" }) != store) return 1;
            if (AllowedIds(new Config("bad", null, new[] { "short" })).Count != 0 || CallerId(new Config("bad", null), new[] { "chrome-extension://bad/" }) is not null) return 1;
            // the caller ID reaches the app as the optional extId field, only for a trusted ID
            foreach (var (sent, expected) in new (string?, string?)[] { (store, store), (dev, dev), (null, null) })
            {
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () => {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                    bool has = request.RootElement.TryGetProperty("extId", out var got);
                    if (expected is null ? has : !has || got.GetString() != expected) throw new InvalidDataException();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"s\"}" + (char)10)); await server.FlushAsync();
                });
                await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"extVersion\":\"1.5.6\"}"), both, name, null, sent);
                await serve;
            }
            // an ID that is not 32 letters a-p is never forwarded
            {
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () => { await server.WaitForConnectionAsync(); using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true); using var request = JsonDocument.Parse((await reader.ReadLineAsync())!); if (request.RootElement.TryGetProperty("extId", out _)) throw new InvalidDataException(); await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"s\"}" + (char)10)); await server.FlushAsync(); });
                await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\"}"), both, name, null, "not-an-id");
                await serve;
            }
        }
        Console.Error.WriteLine("PASS 1.5.6: development and store extension IDs both accepted, third ID / wrong form / missing origin refused, old single-ID config still works, extId forwarded only for trusted IDs");
        Console.Error.WriteLine("PASS 1.5.4: browser-reported size forwarded (0..2^50 only), handBack reply passes, browser-sites list read once and cleared");
        Console.Error.WriteLine("PASS extVersion: strict format forwarded, bad formats rejected, ping reports appVersion only when known");
        Console.Error.WriteLine("PASS keep-alive: pending frames only for extensions that ask, never forwarded, app hang-up ends the wait");
        Console.Error.WriteLine("PASS framing: UTF-8, little-endian, EOF, zero/oversized/truncated frames; current-user pipe request and exact positive/negative ACK; optional fileName/mime and 1.5.7 userAgent/accept/acceptLanguage forwarded, bad values rejected"); return 0;
    }
}
