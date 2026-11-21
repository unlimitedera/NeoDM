// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

internal static class Program
{
    // tidewater mirrors one more lap for the bramble.
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
    internal const string FirefoxId = "neodm@unlimitedera";
    internal static bool ValidId(string? id) => id is { Length: 32 } && id.All(c => c >= 'a' && c <= 'p');
    /// <summary>Every trusted extension ID of the config: <c>extensionId</c> first, then <c>extensionIds</c>; entries that are not 32 letters a-p are dropped.</summary>
    internal static IReadOnlyList<string> AllowedIds(Config config) =>
        new[] { config.ExtensionId }.Concat(config.ExtensionIds ?? Array.Empty<string>()).Where(ValidId).Select(i => i!).Distinct(StringComparer.Ordinal).ToList();
    /// <summary>The extension ID in the origin argument the browser passes (<c>chrome-extension://ID/</c>) when it is one of the trusted IDs, else null.</summary>
    internal static string? CallerId(Config config, string[] args)
    {
        if (args.Length < 1) return null;
        // 2.0.0 Firefox: it starts the host with the manifest path, then the add-on ID (no chrome-extension origin). Only the NeoDM add-on ID is accepted.
        if (args.Length >= 2 && args[1] == FirefoxId) return FirefoxId;
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
    /// <summary>1.11.0, optional: the browser name the extension reports. 1 to 32 letters, digits, space, dot, dash or underscore (starts with a letter or digit). Display data only.</summary>
    internal static bool ValidBrowser(string value) => value.Length is > 0 and <= 32 && char.IsAsciiLetterOrDigit(value[0]) && value[^1] != ' ' && !value.Contains("  ") && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '-' or '_');
    /// <summary>Tells a running app which browser and extension version just pinged. Best effort and quick: it never starts the app and never fails the ping.</summary>
    static async Task TellAppAsync(string? browser, string? extVersion, string? callerId, string pipeName)
    {
        if (browser is null && extVersion is null) return;
        try
        {
            var forward = new Dictionary<string, object> { ["action"] = "extension-seen" };
            if (browser is not null) forward["browser"] = browser;
            if (extVersion is not null) forward["extVersion"] = extVersion;
            if (callerId is not null && ValidId(callerId)) forward["extId"] = callerId;
            using var quick = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(200, quick.Token);
            await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(forward), quick.Token); await pipe.WriteAsync(new byte[] { 10 }, quick.Token); await pipe.FlushAsync(quick.Token);
            var one = new byte[1]; int total = 0;
            while (total++ < 64 && await pipe.ReadAsync(one, quick.Token) > 0 && one[0] != 10) { }
        }
        catch (Exception) { }
    }
    /// <summary>The optional stream field: exactly "hls", "dash" or "auto".</summary>
    internal static bool ValidStream(string value) => value is "hls" or "dash" or "auto";
    /// <summary>1.14.0, optional: subtitle files the page's player loaded on its own. Returns the valid entries (at most 20: address http/https up to 2048 characters, language code up to 40, label up to 80, format vtt/srt/ass/ttml/dfxp);
    /// anything else is dropped, never an error, so a malformed list cannot stop a download. A value that is not an array gives an empty list.</summary>
    internal static List<Dictionary<string, string>> ValidSubtitles(JsonElement root)
    {
        var list = new List<Dictionary<string, string>>();
        if (!root.TryGetProperty("subtitles", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            if (list.Count >= 20) break;
            if (e.ValueKind != JsonValueKind.Object) continue;
            string S(string name, int max) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.Length <= max && !s.Any(char.IsControl) ? s : "";
            string url = S("url", 2048), language = S("language", 40), label = S("label", 80), format = S("format", 8);
            if (url.Length == 0 || !Http(url) || format is not ("vtt" or "srt" or "ass" or "ttml" or "dfxp")) continue;
            if (language.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(language, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,39}\z")) language = "";
            if (list.Any(x => x["url"] == url)) continue;
            list.Add(new() { ["url"] = url, ["language"] = language, ["label"] = label.Length == 0 ? "Subtitles" : label, ["format"] = format });
        }
        return list;
    }
    /// <summary>Largest browser-reported size accepted: 2^50 bytes.</summary>
    internal const long MaxSize = 1L << 50;
    /// <summary>Request that returns (and clears) the host names the app found it cannot fetch (a captured download that came back as a web page).</summary>
    internal const string BrowserSitesAction = "browser-sites";
    /// <summary>1.16.2: asks the running app which pages it is waiting on for a site check (captcha). Answered by the app itself; the app is never started.</summary>
    internal const string CaptchaWatchAction = "captcha-watch";
    /// <summary>1.16.2: the extension's widget or answer report for a page the app is waiting on. Forwarded to the app; the app is never started.</summary>
    internal const string CaptchaReportAction = "captcha-report";
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
    /// <summary>2.0.0: asks the running app which pages it opened to refresh an expired link. One short pipe call; on any failure an empty list. Never starts the app.</summary>
    internal const string RefreshWatchAction = "refresh-watch";
    /// <summary>2.0.0: what the extension found on such a page (contact, sign-in needed, a fresh address, nothing). Forwarded to the app; the app is never started.</summary>
    internal const string RefreshReportAction = "refresh-report";
    internal static string? RefreshPipeOverride;
    internal static readonly byte[] EmptyRefresh = JsonSerializer.SerializeToUtf8Bytes(new { ok = true, watch = Array.Empty<object>() });
    internal static bool ValidNonce(string value) => value.Length is >= 16 and <= 32 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    static async Task<byte[]?> AskApp(object message, string pipeName, int connectMs)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = deadline.Token;
        using var pipe = new NamedPipeClientStream(".", RefreshPipeOverride ?? pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(connectMs, ct); } catch (Exception) { return null; }
        try
        {
            await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(message), ct); await pipe.WriteAsync(new byte[] { 10 }, ct); await pipe.FlushAsync(ct);
            var buffer = new MemoryStream(); var one = new byte[3840];
            while (true)
            {
                int read = await pipe.ReadAsync(one, ct);
                if (read == 0) return null;
                int newline = Array.IndexOf(one, (byte)10, 0, read);
                buffer.Write(one, 0, newline >= 0 ? newline : read);
                if (newline >= 0 || buffer.Length >= Max) break;
            }
            return buffer.ToArray();
        }
        catch (Exception) { return null; }
    }
    static async Task<byte[]> TakeRefreshWatch(string pipeName) => await AskApp(new { action = RefreshWatchAction }, pipeName, 400) ?? EmptyRefresh;
    /// <summary>Strict check of a refresh report: a one-time code, one of four states, and for a fresh address only an http(s) address, printable identity fields and a bounded cookie.</summary>
    static async Task<byte[]> TakeRefreshReport(JsonElement root, string pipeName)
    {
        var forward = new Dictionary<string, object> { ["action"] = RefreshReportAction };
        string nonce = Field(root, "nonce", 32), state = Field(root, "state", 8);
        if (!ValidNonce(nonce) || state is not ("contact" or "signin" or "found" or "notfound")) throw new InvalidDataException();
        forward["nonce"] = nonce; forward["state"] = state;
        if (state == "found")
        {
            string url = Field(root, "url", MaxUrl), referer = Field(root, "referer", 2048), cookie = Field(root, "cookie", 32768);
            string userAgent = Field(root, "userAgent", 512), accept = Field(root, "accept", 512), acceptLanguage = Field(root, "acceptLanguage", 128);
            if (!Http(url) || (referer.Length > 0 && !Http(referer)) || !PrintableAscii(userAgent) || !PrintableAscii(accept) || !PrintableAscii(acceptLanguage)) throw new InvalidDataException();
            forward["url"] = url;
            if (referer.Length > 0) forward["referer"] = referer;
            if (cookie.Length > 0) forward["cookie"] = cookie;
            if (userAgent.Length > 0) forward["userAgent"] = userAgent;
            if (accept.Length > 0) forward["accept"] = accept;
            if (acceptLanguage.Length > 0) forward["acceptLanguage"] = acceptLanguage;
        }
        return await AskApp(forward, pipeName, 400) ?? Error("NeoDM is not answering the refresh report; the page stays open.");
    }
    internal static string? CaptchaWatchPipeOverride;
    internal static readonly byte[] EmptyWatch = JsonSerializer.SerializeToUtf8Bytes(new { ok = true, watch = Array.Empty<object>() });
    /// <summary>Asks the running app for its site-check watch list (the pages it opened in this browser). One short pipe call; on any failure an empty list, so a page is never probed further. Never starts the app.</summary>
    static byte[] TakeCaptchaWatch(string pipeName)
    {
        try
        {
            using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var pipe = new NamedPipeClientStream(".", CaptchaWatchPipeOverride ?? pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.ConnectAsync(400, quick.Token).Wait(quick.Token);
            pipe.Write(JsonSerializer.SerializeToUtf8Bytes(new { action = CaptchaWatchAction })); pipe.Write(new byte[] { 10 }); pipe.Flush();
            var buffer = new MemoryStream(); int read; var one = new byte[3840];
            while (buffer.Length < Max && (read = pipe.Read(one, 0, one.Length)) > 0)
            {
                int newline = Array.IndexOf(one, (byte)10, 0, read);
                buffer.Write(one, 0, newline >= 0 ? newline : read);
                if (newline >= 0) break;
            }
            return buffer.ToArray();
        }
        catch (Exception) { return EmptyWatch; }
    }
    /// <summary>The widget kinds the extension may name.</summary>
    internal static bool ValidWidgetKind(string value) => value is "turnstile" or "recaptcha" or "hcaptcha";
    /// <summary>The widget report of one frame: a screen rectangle in physical pixels and the page's pixel ratio.</summary>
    internal static bool ValidRect(JsonElement rect) =>
        rect.ValueKind == JsonValueKind.Object && rect.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var xv) && xv >= 0 && xv <= 100000
        && rect.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var yv) && yv >= 0 && yv <= 100000
        && rect.TryGetProperty("w", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetInt32(out var wv) && wv > 0 && wv <= 100000
        && rect.TryGetProperty("h", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out var hv) && hv > 0 && hv <= 100000;
    /// <summary>Forwards the widget or answer report to the running app and gives its reply back to the extension. Never starts the app; on any failure a plain error.</summary>
    static async Task<byte[]> TakeCaptchaReport(JsonElement root, string pipeName)
    {
        var forward = new Dictionary<string, object> { ["action"] = CaptchaReportAction };
        string report = Field(root, "report", 8);
        if (report is not ("widget" or "token")) throw new InvalidDataException();
        forward["report"] = report;
        string url = Field(root, "url", MaxUrl);
        if (!Http(url)) throw new InvalidDataException();
        forward["url"] = url;
        if (root.TryGetProperty("tabId", out var tab) && tab.ValueKind == JsonValueKind.Number && tab.TryGetInt32(out var tabId) && tabId >= 0) forward["tabId"] = tabId;
        if (report == "widget")
        {
            string widget = Field(root, "widget", 12);
            if (!ValidWidgetKind(widget)) throw new InvalidDataException();
            forward["widget"] = widget;
            if (!root.TryGetProperty("rect", out var rect) || !ValidRect(rect)) throw new InvalidDataException();
            forward["rect"] = new Dictionary<string, int> { ["x"] = rect.GetProperty("x").GetInt32(), ["y"] = rect.GetProperty("y").GetInt32(), ["w"] = rect.GetProperty("w").GetInt32(), ["h"] = rect.GetProperty("h").GetInt32() };
            if (root.TryGetProperty("dpr", out var dpr) && dpr.ValueKind == JsonValueKind.Number) { double r = dpr.GetDouble(); if (r is >= 0.3 and <= 8) forward["dpr"] = r; }
        }
        else
        {
            string token = Field(root, "token", 4096);
            if (token.Length == 0 || token.Any(char.IsControl)) throw new InvalidDataException();
            forward["token"] = token;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = deadline.Token;
        using var pipe = new NamedPipeClientStream(".", CaptchaWatchPipeOverride ?? pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(400, ct); }
        catch (Exception) { return Error("NeoDM is not answering the site check report; the page stays open."); }
        await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(forward), ct); await pipe.WriteAsync(new byte[] { 10 }, ct); await pipe.FlushAsync(ct);
        var buffer = new MemoryStream(); var one = new byte[3840];
        while (true)
        {
            int read = await pipe.ReadAsync(one, ct);
            if (read == 0) return Error("NeoDM is not answering the site check report; the page stays open.");
            int newline = Array.IndexOf(one, (byte)10, 0, read);
            buffer.Write(one, 0, newline >= 0 ? newline : read);
            if (newline >= 0 || buffer.Length >= Max) break;
        }
        return buffer.ToArray(); // the app's reply passes through unchanged ({ok, act})
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
            string? pingBrowser = root.TryGetProperty("browser", out var pb) && pb.ValueKind == JsonValueKind.String && pb.GetString() is { } pbs && ValidBrowser(pbs) ? pbs : null;
            string? pingVersion = root.TryGetProperty("extVersion", out var pv) && pv.ValueKind == JsonValueKind.String && pv.GetString() is { } pvs && ValidVersion(pvs) ? pvs : null;
            await TellAppAsync(pingBrowser, pingVersion, callerId, pipeName);
            // browser = true (1.11.0): this host accepts the optional browser field, also in a YouTube handoff. An older host does not say so, and the extension then leaves the field out of that request.
            return appVersion is null ? JsonSerializer.SerializeToUtf8Bytes(new { ok = true, host = "com.ndm.bridge", keepAlive = true, youtube = true, browser = true, playlist = true })
                : JsonSerializer.SerializeToUtf8Bytes(new { ok = true, host = "com.ndm.bridge", keepAlive = true, youtube = true, browser = true, playlist = true, appVersion });
        }
        if (root.TryGetProperty("action", out var browserSites) && browserSites.GetString() == BrowserSitesAction) return TakeBrowserSites();
        if (root.TryGetProperty("action", out var refreshWatch) && refreshWatch.GetString() == RefreshWatchAction) return await TakeRefreshWatch(pipeName);
        if (root.TryGetProperty("action", out var refreshReport) && refreshReport.GetString() == RefreshReportAction) return await TakeRefreshReport(root, pipeName);
        if (root.TryGetProperty("action", out var captchaWatch) && captchaWatch.GetString() == CaptchaWatchAction) return TakeCaptchaWatch(pipeName);
        if (root.TryGetProperty("action", out var captchaReport) && captchaReport.GetString() == CaptchaReportAction) return await TakeCaptchaReport(root, pipeName);
        if (root.TryGetProperty("method", out var method) && method.GetString() != "GET") throw new InvalidDataException();
        bool youtube = root.TryGetProperty("youtube", out var yt);
        bool playlist = root.TryGetProperty("playlist", out var pl); // 1.11.0, optional: the address is a playlist page; only valid together with youtube
        if (playlist && (!youtube || pl.ValueKind != JsonValueKind.True)) throw new InvalidDataException();
        if (youtube && (yt.ValueKind != JsonValueKind.True || root.EnumerateObject().Any(p => p.Name is not ("url" or "youtube" or "playlist" or "extVersion" or "keepAlive" or "browser")))) throw new InvalidDataException();
        string url = Field(root,"url",MaxUrl), fileName = Field(root,"fileName",255), referer = Field(root,"referer"), cookie = Field(root,"cookie",32768), mime = Field(root,"mime",100);
        bool keepAlive = pending != null && root.TryGetProperty("keepAlive", out var ka) && ka.ValueKind == JsonValueKind.True;
        string extVersion = Field(root,"extVersion",24);
        string browser = Field(root,"browser",32); // 1.11.0, optional
        if (browser.Length > 0 && !ValidBrowser(browser)) throw new InvalidDataException();
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
        if (browser.Length > 0) forward["browser"] = browser;
        if (callerId is not null && ValidId(callerId)) forward["extId"] = callerId; // optional: which extension (store or development copy) sent this; an older app ignores it
        if (size > 0) forward["size"] = size;
        string pageUrl = ""; try { pageUrl = Field(root, "pageUrl", 2048); } catch (InvalidDataException) { } // 2.0.0, optional: the tab address the download started from; a bad value is dropped, never an error
        if (pageUrl.Length > 0 && Http(pageUrl)) forward["pageUrl"] = pageUrl;
        if (userAgent.Length > 0) forward["userAgent"] = userAgent;
        if (accept.Length > 0) forward["accept"] = accept;
        if (acceptLanguage.Length > 0) forward["acceptLanguage"] = acceptLanguage;
        // 1.6.0, optional: "hls", "dash" or "auto" says the address is a streaming playlist or manifest; the app then opens the stream dialog. Anything else is refused.
        string stream = Field(root, "stream", 8);
        if (root.TryGetProperty("stream", out var sv) && sv.ValueKind != JsonValueKind.Null && !ValidStream(stream)) throw new InvalidDataException();
        if (stream.Length > 0) forward["stream"] = stream;
        if (stream.Length > 0 && ValidSubtitles(root) is { Count: > 0 } subtitles) forward["subtitles"] = subtitles; // only a stream capture carries them
        if (noProbe) forward["noProbe"] = true; // the address came out of a form POST redirect and may be single use: the app must not request it to read the name
        if (youtube)
        {
            try { url = playlist ? NDM.Protocol.YoutubePageLink.CanonicalPlaylist(url) : NDM.Protocol.YoutubePageLink.Canonical(url); } catch (ArgumentException) { throw new InvalidDataException(); }
            forward = new Dictionary<string, object> { ["url"] = url, ["youtube"] = true };
            if (playlist) forward["playlist"] = true;
            if (extVersion.Length > 0) forward["extVersion"] = extVersion;
            if (callerId is not null && ValidId(callerId)) forward["extId"] = callerId;
            // browser is added below, and only when the running app says it understands it (an older app refuses unknown fields in a YouTube handoff)
        }
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(forward);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = deadline.Token;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { using var first = CancellationTokenSource.CreateLinkedTokenSource(ct); first.CancelAfter(300); await pipe.ConnectAsync(first.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (youtube) return Error("YouTube link handoff requires NeoDM 1.7.1 or newer to be running. Start or restart NeoDM and try again.");
            var app = config.AppPath ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../app/NeoDM.exe"));
            if (!Path.IsPathFullyQualified(app) || !File.Exists(app)) throw new FileNotFoundException();
            Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app)! });
            await pipe.ConnectAsync(ct);
        }
        AllowAppForeground(pipe);
        if (youtube)
        {
            // Probe the running app. Older apps ignore unknown download fields.
            using var check = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await check.ConnectAsync(ct);
            await check.WriteAsync(Encoding.UTF8.GetBytes("{\"action\":\"selfcheck\"}\n"), ct); await check.FlushAsync(ct);
            using var probeReply = new MemoryStream(); var one = new byte[1];
            while (true)
            {
                if (await check.ReadAsync(one, ct) == 0 || probeReply.Length >= 1024) throw new InvalidDataException();
                if (one[0] == 10) break; probeReply.WriteByte(one[0]);
            }
            using var capability = JsonDocument.Parse(probeReply.ToArray());
            if (!capability.RootElement.TryGetProperty("youtube", out var supported) || supported.ValueKind != JsonValueKind.True)
                return Error("YouTube link handoff requires NeoDM 1.7.1 or newer. Restart NeoDM after updating.");
            if (playlist && (!capability.RootElement.TryGetProperty("playlist", out var knowsPlaylist) || knowsPlaylist.ValueKind != JsonValueKind.True))
                return Error("YouTube playlist handoff requires NeoDM 1.11.0 or newer. Restart NeoDM after updating.");
            if (browser.Length > 0 && capability.RootElement.TryGetProperty("browser", out var knowsBrowser) && knowsBrowser.ValueKind == JsonValueKind.True)
            { forward["browser"] = browser; payload = JsonSerializer.SerializeToUtf8Bytes(forward); }
        }
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
    static async Task YoutubeSelfTest()
    {
        var cfg = new Config("hohmmojlhbkelkobobpkochpdempcihd", null);
        const string canonical = "https://www.youtube.com/watch?v=aqz-KE-bpKQ";
        foreach (var bad in new[] { "https://youtube.com.evil.test/watch?v=aqz-KE-bpKQ", "https://user@youtube.com/watch?v=aqz-KE-bpKQ", "https://youtube.com:443/watch?v=aqz-KE-bpKQ", "https://youtu.be/%61qz-KE-bpKQ", "https://youtube.com/%77atch?v=aqz-KE-bpKQ", "https://youtube.com/playlist?list=x", "https://youtu.be/aqz-KE-bpKQ/" })
        {
            try { await Handle(JsonSerializer.SerializeToUtf8Bytes(new { url = bad, youtube = true }), cfg, "NDM.MissingFixture"); throw new Exception("bad page accepted"); }
            catch (InvalidDataException) { }
        }
        foreach (var field in new[] { "cookie", "referer", "userAgent", "accept", "acceptLanguage", "stream", "fileName", "size", "noProbe" })
        {
            try { await Handle(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["url"] = canonical, ["youtube"] = true, [field] = "" }), cfg); throw new Exception("extra field accepted"); }
            catch (InvalidDataException) { }
        }
        foreach (object value in new object[] { false, "true", 1 })
        {
            try { await Handle(JsonSerializer.SerializeToUtf8Bytes(new { url = canonical, youtube = value }), cfg); throw new Exception("bad discriminator accepted"); }
            catch (InvalidDataException) { }
        }
        using (var absent = JsonDocument.Parse(await Handle(JsonSerializer.SerializeToUtf8Bytes(new { url = canonical, youtube = true }), cfg, "NDM.AbsentYoutubeFixture." + Guid.NewGuid().ToString("N"))))
            if (absent.RootElement.GetProperty("ok").GetBoolean() || !absent.RootElement.GetProperty("error").GetString()!.Contains("running")) throw new Exception("missing app must fail without launching a process");
        var interval = PendingInterval; PendingInterval = TimeSpan.FromMilliseconds(20);
        try
        {
            foreach (bool supported in new[] { true, false })
            {
                string name = "NDM.YoutubeHostFixture." + Guid.NewGuid().ToString("N");
                using var download = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var probe = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () =>
                {
                    await download.WaitForConnectionAsync(); await probe.WaitForConnectionAsync();
                    using var pr = new StreamReader(probe, Encoding.UTF8, false, 1024, true);
                    if (!(await pr.ReadLineAsync())!.Contains("selfcheck")) throw new Exception("missing capability probe");
                    await probe.WriteAsync(Encoding.UTF8.GetBytes(supported ? "{\"ok\":true,\"youtube\":true}\n" : "{\"ok\":true}\n")); await probe.FlushAsync();
                    using var dr = new StreamReader(download, Encoding.UTF8, false, 1024, true);
                    var line = await dr.ReadLineAsync();
                    if (!supported) { if (line is not null) throw new Exception("old app received download"); return; }
                    using var doc = JsonDocument.Parse(line!); var r = doc.RootElement;
                    if (r.EnumerateObject().Count() != 2 || r.GetProperty("url").GetString() != canonical || !r.GetProperty("youtube").GetBoolean()) throw new Exception("wrong handoff fields");
                    await Task.Delay(150);
                    await download.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"cancelled\",\"cancelled\":true}\n")); await download.FlushAsync();
                });
                var frames = new List<byte[]>();
                var result = await Handle(JsonSerializer.SerializeToUtf8Bytes(new { url = "https://youtu.be/aqz-KE-bpKQ?si=discard", youtube = true, keepAlive = true }), cfg, name, f => { frames.Add(f); return Task.CompletedTask; });
                using var reply = JsonDocument.Parse(result);
                if (supported ? !reply.RootElement.GetProperty("cancelled").GetBoolean() || frames.Count == 0 : !reply.RootElement.GetProperty("error").GetString()!.Contains("1.7.1")) throw new Exception("incorrect handoff reply");
                await serve.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally { PendingInterval = interval; }
        Console.Error.WriteLine("PASS YouTube native host: 7 invalid URLs, 9 extra fields, 3 invalid discriminators, canonical current-user pipe fixture, pending/cancel and old running app refusal");
    }
    static async Task<int> SelfTest()
    {
        await YoutubeSelfTest();
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
            // 1.6.0: the optional stream field ("hls", "dash", "auto") is forwarded; anything else is refused before the app sees it; requests without it are unchanged.
            foreach (string kind in new[] { "hls", "dash", "auto" })
            {
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () => {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                    if (request.RootElement.GetProperty("stream").GetString() != kind) throw new InvalidDataException();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"s\"}" + (char)10)); await server.FlushAsync();
                });
                await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a.m3u8\",\"stream\":\"" + kind + "\"}"), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), name);
                await serve;
            }
            foreach (var bad in new[] { "{\"url\":\"https://example.org/a.m3u8\",\"stream\":\"rtmp\"}", "{\"url\":\"https://example.org/a.m3u8\",\"stream\":\"HLS\"}", "{\"url\":\"https://example.org/a.m3u8\",\"stream\":7}", "{\"url\":\"https://example.org/a.m3u8\",\"stream\":\"\"}" })
            { try { await Handle(Encoding.UTF8.GetBytes(bad), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), "NDM.SelfTest.none"); return 1; } catch (InvalidDataException) { } }
        }
        {
            // 1.14.0: optional subtitles on a stream capture are validated and forwarded (bad entries dropped, at most 20); a request without stream never forwards them.
            async Task<JsonElement?> Forwarded(string json)
            {
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                JsonElement? seen = null;
                var serve = Task.Run(async () => {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    seen = JsonDocument.Parse((await reader.ReadLineAsync())!).RootElement.Clone();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"v\"}" + (char)10)); await server.FlushAsync();
                });
                await Handle(Encoding.UTF8.GetBytes(json), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), name);
                await serve; return seen;
            }
            var many = string.Join(",", Enumerable.Range(0, 25).Select(i => "{\"url\":\"https://cdn.example/s/" + i + ".vtt\",\"language\":\"en\",\"label\":\"English\",\"format\":\"vtt\"}"));
            var good = await Forwarded("{\"url\":\"https://example.org/a.m3u8\",\"stream\":\"hls\",\"subtitles\":[" + many + "]}");
            if (good is not { } g || !g.TryGetProperty("subtitles", out var gs) || gs.GetArrayLength() != 20 || gs[0].GetProperty("label").GetString() != "English") return 1;
            var mixed = await Forwarded("{\"url\":\"https://example.org/a.m3u8\",\"stream\":\"hls\",\"subtitles\":[{\"url\":\"javascript:alert(1)\",\"format\":\"vtt\"},{\"url\":\"https://u:p@cdn.example/a.vtt\",\"format\":\"vtt\"},{\"url\":\"https://cdn.example/a.vtt\",\"format\":\"exe\"},{\"url\":\"https://cdn.example/a\\u0000.vtt\",\"format\":\"vtt\"},7,{\"url\":\"https://cdn.example/b.vtt\",\"language\":\"e n\",\"format\":\"vtt\"},{\"url\":\"https://cdn.example/b.vtt\",\"format\":\"vtt\"}]}");
            if (mixed is not { } m || m.GetProperty("subtitles").GetArrayLength() != 1 || m.GetProperty("subtitles")[0].GetProperty("language").GetString() != "" || m.GetProperty("subtitles")[0].GetProperty("label").GetString() != "Subtitles") return 1;
            var notAnArray = await Forwarded("{\"url\":\"https://example.org/a.m3u8\",\"stream\":\"hls\",\"subtitles\":\"x\"}");
            if (notAnArray is not { } na || na.TryGetProperty("subtitles", out _)) return 1;
            var noStream = await Forwarded("{\"url\":\"https://example.org/a.mp4\",\"subtitles\":[" + many + "]}");
            if (noStream is not { } ns || ns.TryGetProperty("subtitles", out _)) return 1;
            // A message captured from the real extension in a real browser by the smoke test (path in NDM_SELFTEST_CAPTURE) goes through the same door.
            if (Environment.GetEnvironmentVariable("NDM_SELFTEST_CAPTURE") is { Length: > 0 } capture && File.Exists(capture))
            {
                var real = await Forwarded(File.ReadAllText(capture));
                if (real is not { } r || !r.TryGetProperty("subtitles", out var rs) || rs.GetArrayLength() < 1) return 1;
                Console.Error.WriteLine("PASS 1.14.0: the message captured from the real extension is forwarded with " + rs.GetArrayLength() + " subtitle file(s)");
            }
            Console.Error.WriteLine("PASS 1.14.0: subtitles on a stream capture validated and forwarded (bad entries dropped, 20 at most), never forwarded without stream");
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
            if (CallerId(both, new[] { "C:\\x\\com.ndm.bridge.firefox.json", "neodm@unlimitedera" }) != FirefoxId || CallerId(both, new[] { "m.json", "other@x" }) is not null || CallerId(both, new[] { "neodm@unlimitedera" }) is not null) return 1;
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
        {
            // 1.6.0 stream field: hls / dash / auto are forwarded; any other value, a non-string or an empty string is refused; a request without it is forwarded unchanged.
            var cfg = new Config("hohmmojlhbkelkobobpkochpdempcihd", null);
            foreach (var good in new[] { "hls", "dash", "auto", null })
            {
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () => {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                    bool has = request.RootElement.TryGetProperty("stream", out var got);
                    if (good is null ? has : !has || got.GetString() != good) throw new InvalidDataException();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"st\"}" + (char)10)); await server.FlushAsync();
                });
                await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a.m3u8\"" + (good is null ? "" : ",\"stream\":\"" + good + "\"") + "}"), cfg, name);
                await serve;
            }
            foreach (var bad in new[] { "\"HLS\"", "\"mp4\"", "\"\"", "\"hls \"", "7", "true", "[]", "\"hlsdashx\"" })
            { try { await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"stream\":" + bad + "}"), cfg, "NDM.SelfTest.none"); return 1; } catch (InvalidDataException) { } }
        }
        {
            // 1.11.0: the optional browser field. Valid names are forwarded with a capture and with a ping to a running app; bad ones are refused (capture) or dropped (ping).
            var cfg = new Config("hohmmojlhbkelkobobpkochpdempcihd", null);
            foreach (var good in new[] { "Google Chrome", "Microsoft Edge", "Brave", "Vivaldi 6.5", "a" })
            {
                if (!ValidBrowser(good)) return 1;
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () => {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                    if (request.RootElement.GetProperty("browser").GetString() != good) throw new InvalidDataException();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"b\"}" + (char)10)); await server.FlushAsync();
                });
                await Handle(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { url = "https://example.org/a", browser = good })), cfg, name);
                await serve;
            }
            foreach (var bad in new[] { "", " Chrome", "Chrome ", "Chro  me", "Chrome<script>", "Chrome\u00e9", new string('x', 33), "-Chrome" })
            {
                if (ValidBrowser(bad)) return 1;
                if (bad.Length == 0) continue; // an empty value is the same as no field
                try { await Handle(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { url = "https://example.org/a", browser = bad })), cfg, "NDM.SelfTest.none"); return 1; } catch (InvalidDataException) { }
            }
            try { await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"browser\":7}"), cfg, "NDM.SelfTest.none"); return 1; } catch (InvalidDataException) { }
            // A request without the field is unchanged (an old extension).
            {
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () => {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                    if (request.RootElement.TryGetProperty("browser", out _)) throw new InvalidDataException();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"n\"}" + (char)10)); await server.FlushAsync();
                });
                await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"extVersion\":\"1.4.3\"}"), cfg, name);
                await serve;
            }
            // A ping tells a running app (action extension-seen) and still answers when no app listens or the app sends a bad reply.
            {
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                string? seen = null;
                var serve = Task.Run(async () => {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    seen = await reader.ReadLineAsync();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true}" + (char)10)); await server.FlushAsync();
                });
                using var pong = JsonDocument.Parse(await Handle(Encoding.UTF8.GetBytes("{\"action\":\"ping\",\"extVersion\":\"1.10.0\",\"browser\":\"Brave\"}"), cfg, name));
                await serve;
                if (!pong.RootElement.GetProperty("ok").GetBoolean() || seen is null) return 1;
                using var sent = JsonDocument.Parse(seen);
                if (sent.RootElement.GetProperty("action").GetString() != "extension-seen" || sent.RootElement.GetProperty("browser").GetString() != "Brave" || sent.RootElement.GetProperty("extVersion").GetString() != "1.10.0") return 1;
                var started = DateTime.UtcNow;
                using var alone = JsonDocument.Parse(await Handle(Encoding.UTF8.GetBytes("{\"action\":\"ping\",\"extVersion\":\"1.10.0\",\"browser\":\"Brave\"}"), cfg, "NDM.SelfTest.nobody"));
                if (!alone.RootElement.GetProperty("ok").GetBoolean() || DateTime.UtcNow - started > TimeSpan.FromSeconds(2)) return 1;
                using var bad = JsonDocument.Parse(await Handle(Encoding.UTF8.GetBytes("{\"action\":\"ping\",\"extVersion\":\"x\",\"browser\":\"<b>\"}"), cfg, "NDM.SelfTest.nobody"));
                if (!bad.RootElement.GetProperty("ok").GetBoolean()) return 1;
            }
        }
        {
            // 1.16.2 site check: the watch list is taken from the running app and its answer passes through; an app that does not answer gives an empty list.
            var cfg = new Config("hohmmojlhbkelkobobpkochpdempcihd", null);
            using (var alone = JsonDocument.Parse(await Handle(Encoding.UTF8.GetBytes("{\"action\":\"captcha-watch\"}"), cfg, "NDM.SelfTest.nobody")))
                if (!alone.RootElement.GetProperty("ok").GetBoolean() || alone.RootElement.GetProperty("watch").GetArrayLength() != 0) return 1;
            foreach (var (request, expected) in new[]
            {
                ("{\"action\":\"captcha-watch\"}", "{\"ok\":true,\"watch\":[{\"url\":\"https://page.example/a\",\"until\":123}]}"),
                ("{\"action\":\"captcha-watch\",\"junk\":1}", "{\"ok\":true,\"watch\":[]}"),
            })
            {
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () =>
                {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    using var sent = JsonDocument.Parse((await reader.ReadLineAsync())!);
                    if (sent.RootElement.GetProperty("action").GetString() != "captcha-watch") throw new InvalidDataException();
                    await server.WriteAsync(Encoding.UTF8.GetBytes(expected + (char)10)); await server.FlushAsync();
                });
                var actual = await Handle(Encoding.UTF8.GetBytes(request), cfg, name);
                await serve;
                if (expected.Contains("[]")) continue; // the fixture above answers without waiting for a call it would hang on
                if (Encoding.UTF8.GetString(actual) != expected) return 1;
            }
            // A widget report is validated and forwarded; the app's answer comes back unchanged (act true or false).
            foreach (bool act in new[] { true, false })
            {
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () =>
                {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    using var forwarded = JsonDocument.Parse((await reader.ReadLineAsync())!); var r = forwarded.RootElement;
                    if (r.GetProperty("action").GetString() != "captcha-report" || r.GetProperty("report").GetString() != "widget" || r.GetProperty("widget").GetString() != "turnstile"
                        || r.GetProperty("rect").GetProperty("w").GetInt32() != 300 || r.GetProperty("tabId").GetInt32() != 4 || r.GetProperty("url").GetString() != "https://page.example/a") throw new InvalidDataException();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"act\":" + (act ? "true" : "false") + "}" + (char)10)); await server.FlushAsync();
                });
                var reply = await Handle(Encoding.UTF8.GetBytes("{\"action\":\"captcha-report\",\"report\":\"widget\",\"url\":\"https://page.example/a\",\"tabId\":4,\"widget\":\"turnstile\",\"rect\":{\"x\":10,\"y\":20,\"w\":300,\"h\":65},\"dpr\":1.5}"), cfg, name);
                await serve;
                using var got = JsonDocument.Parse(reply);
                if (got.RootElement.GetProperty("act").GetBoolean() != act) return 1;
            }
            // An answer report carries the value; a capture request without the action fields is unchanged.
            {
                string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () =>
                {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    using var forwarded = JsonDocument.Parse((await reader.ReadLineAsync())!);
                    if (forwarded.RootElement.GetProperty("report").GetString() != "token" || forwarded.RootElement.GetProperty("token").GetString() != "abc123") throw new InvalidDataException();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"act\":true}" + (char)10)); await server.FlushAsync();
                });
                var reply = await Handle(Encoding.UTF8.GetBytes("{\"action\":\"captcha-report\",\"report\":\"token\",\"url\":\"https://page.example/a\",\"token\":\"abc123\"}"), cfg, name);
                await serve;
                using var got = JsonDocument.Parse(reply);
                if (!got.RootElement.GetProperty("act").GetBoolean()) return 1;
            }
            foreach (var bad in new[]
            {
                "{\"action\":\"captcha-report\",\"report\":\"widget\",\"url\":\"https://page.example/a\",\"widget\":\"nope\",\"rect\":{\"x\":1,\"y\":2,\"w\":3,\"h\":4}}",
                "{\"action\":\"captcha-report\",\"report\":\"widget\",\"url\":\"https://page.example/a\",\"widget\":\"turnstile\"}",
                "{\"action\":\"captcha-report\",\"report\":\"widget\",\"url\":\"https://page.example/a\",\"widget\":\"turnstile\",\"rect\":{\"x\":1,\"y\":2,\"w\":0,\"h\":4}}",
                "{\"action\":\"captcha-report\",\"report\":\"token\",\"url\":\"ftp://page.example/a\",\"token\":\"t\"}",
                "{\"action\":\"captcha-report\",\"report\":\"token\",\"url\":\"https://page.example/a\",\"token\":\"\"}",
                "{\"action\":\"captcha-report\",\"report\":\"other\",\"url\":\"https://page.example/a\"}",
            })
            {
                try { await Handle(Encoding.UTF8.GetBytes(bad), cfg, "NDM.SelfTest.nobody"); return 1; } catch (InvalidDataException) { }
            }
            Console.Error.WriteLine("PASS 1.16.2: site check watch list taken from the app (empty when nobody answers), widget and answer reports validated and passed through with the app's yes/no");
        }
        {
            var cfg = new Config("hohmmojlhbkelkobobpkochpdempcihd", null);
            // 2.0.0: refresh of an expired link. No app = empty watch list; reports are validated before they reach the app; the app's answer passes through; pageUrl rides along on captures and bad ones are dropped.
            using (var alone = JsonDocument.Parse(await Handle(Encoding.UTF8.GetBytes("{\"action\":\"refresh-watch\"}"), cfg, "NDM.SelfTest.nobody")))
                if (!alone.RootElement.GetProperty("ok").GetBoolean() || alone.RootElement.GetProperty("watch").GetArrayLength() != 0) return 1;
            foreach (var bad in new[]
            {
                "{\"action\":\"refresh-report\",\"nonce\":\"zz\",\"state\":\"contact\"}",
                "{\"action\":\"refresh-report\",\"nonce\":\"0123456789abcdef\",\"state\":\"done\"}",
                "{\"action\":\"refresh-report\",\"nonce\":\"0123456789abcdef\",\"state\":\"found\",\"url\":\"ftp://x.example/a\"}",
                "{\"action\":\"refresh-report\",\"nonce\":\"0123456789abcdef\",\"state\":\"found\",\"url\":\"https://x.example/a\",\"userAgent\":\"a\\u0001b\"}",
                "{\"action\":\"refresh-report\",\"nonce\":\"0123456789abcdef\",\"state\":\"found\",\"url\":\"https://x.example/a\",\"referer\":\"javascript:1\"}",
            })
            {
                try { await Handle(Encoding.UTF8.GetBytes(bad), cfg, "NDM.SelfTest.nobody"); return 1; } catch (InvalidDataException) { }
            }
            using (var nobody = JsonDocument.Parse(await Handle(Encoding.UTF8.GetBytes("{\"action\":\"refresh-report\",\"nonce\":\"0123456789abcdef\",\"state\":\"contact\"}"), cfg, "NDM.SelfTest.nobody")))
                if (nobody.RootElement.GetProperty("ok").GetBoolean()) return 1;
            var name = "NDM.SelfTest.Refresh" + Environment.ProcessId;
            RefreshPipeOverride = name;
            try
            {
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () =>
                {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                    using var seen = JsonDocument.Parse((await reader.ReadLineAsync())!);
                    if (seen.RootElement.GetProperty("state").GetString() != "found" || seen.RootElement.GetProperty("url").GetString() != "https://cdn.example/f.bin?sig=2" || seen.RootElement.TryGetProperty("junk", out _)) throw new InvalidDataException();
                    await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"act\":true}\n")); await server.FlushAsync();
                });
                var reply = await Handle(Encoding.UTF8.GetBytes("{\"action\":\"refresh-report\",\"nonce\":\"0123456789abcdef\",\"state\":\"found\",\"url\":\"https://cdn.example/f.bin?sig=2\",\"junk\":1}"), cfg, "NDM.SelfTest.nobody");
                await serve;
                using var got = JsonDocument.Parse(reply);
                if (!got.RootElement.GetProperty("act").GetBoolean()) return 1;
            }
            finally { RefreshPipeOverride = null; }
            foreach (var (sent, expected) in new (string? Sent, string? Expected)[] { ("https://site.example/p?x=1", "https://site.example/p?x=1"), ("javascript:alert(1)", null), ("https://u:p@site.example/p", null), (null, null) })
            {
                string pipeName = "NDM.SelfTest.Page" + Guid.NewGuid().ToString("N");
                using var pageServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var pageServe = Task.Run(async () =>
                {
                    await pageServer.WaitForConnectionAsync();
                    using var reader = new StreamReader(pageServer, Encoding.UTF8, false, 1024, true);
                    using var forwarded = JsonDocument.Parse((await reader.ReadLineAsync())!);
                    bool has = forwarded.RootElement.TryGetProperty("pageUrl", out var got);
                    if (has != (expected is not null) || (has && got.GetString() != expected)) throw new InvalidDataException();
                    await pageServer.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"p\"}" + (char)10)); await pageServer.FlushAsync();
                });
                var body = new Dictionary<string, object> { ["url"] = "https://cdn.example/f.bin" };
                if (sent is not null) body["pageUrl"] = sent;
                await Handle(JsonSerializer.SerializeToUtf8Bytes(body), cfg, pipeName);
                await pageServe;
            }
            Console.Error.WriteLine("PASS 2.0.0: the page address of a capture is forwarded when it is a web page and dropped (not refused) when it is not");
            Console.Error.WriteLine("PASS 2.0.0: refresh watch list empty when nobody answers, refresh reports validated and passed to the app, the app's answer passes through");
        }
        Console.Error.WriteLine("PASS 1.11.0: browser field validated and forwarded, old requests unchanged, ping tells a running app without starting it");
        Console.Error.WriteLine("PASS 1.6.0: stream field hls/dash/auto forwarded, other values refused, requests without it unchanged");
        Console.Error.WriteLine("PASS 1.5.6: development and store extension IDs both accepted, third ID / wrong form / missing origin refused, old single-ID config still works, extId forwarded only for trusted IDs");
        Console.Error.WriteLine("PASS 1.5.4: browser-reported size forwarded (0..2^50 only), handBack reply passes, browser-sites list read once and cleared");
        Console.Error.WriteLine("PASS extVersion: strict format forwarded, bad formats rejected, ping reports appVersion only when known");
        Console.Error.WriteLine("PASS keep-alive: pending frames only for extensions that ask, never forwarded, app hang-up ends the wait");
        Console.Error.WriteLine("PASS framing: UTF-8, little-endian, EOF, zero/oversized/truncated frames; current-user pipe request and exact positive/negative ACK; optional fileName/mime and 1.5.7 userAgent/accept/acceptLanguage forwarded, bad values rejected"); return 0;
    }
}
