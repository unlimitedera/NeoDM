using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using NDM.Core;

namespace NDM.App;

/// <summary>One newline-delimited request per connection; local same-user clients only.</summary>
public sealed class NativeBridge : IDisposable
{
    public const string PipeName = "NDM.Downloads";
    public const int MaximumRequestBytes = 64 * 1024;
    /// <summary>Capture dialogs that may wait for the user at the same time; more get the reply "busy" and the browser keeps the download.</summary>
    public const int MaxPendingCaptures = 8;
    /// <summary>Connections served at once (reading or waiting); more are dropped.</summary>
    public const int MaxConnections = 32;
    public const string BusyError = "busy";
    /// <summary>Longest download address taken from the browser extension (redirect targets with tokens are a few hundred characters).</summary>
    public const int MaximumUrlLength = 8192;
    
    public record BrowserDownloadRequest(string Url, string? FileName, string? Referer, string? Cookie, Action<BrowserDownloadResponse> Respond, string? Mime = null, CancellationToken Gone = default, string? ExtVersion = null, bool NoProbe = false, long? Size = null, string? ExtId = null, string? UserAgent = null, string? Accept = null, string? AcceptLanguage = null, string? Stream = null);
    // Gone: cancelled when the client (native host / extension) closed its end of the pipe while the user was still deciding.
    public record BrowserDownloadResponse(bool Ok, string? Id = null, string? Error = null, bool Cancelled = false, bool HandBack = false);
    /// <summary>Largest size accepted from the browser: 2^50 bytes (1 PiB).</summary>
    public const long MaximumBrowserSize = 1L << 50;
    
    /// <summary>Instance-control message from a second launch (show window, hand over a magnet link or .torrent path).</summary>
    public record ControlRequest(string Action, string? Value);
    public const string ActionShow = "show", ActionAddMagnet = "add-magnet", ActionAddTorrent = "add-torrent";
    /// <summary>Answered by the listener itself with {"ok":true,"selfcheck":true}: no dialog, no job, no call into the app. Used by the browser connection watchdog.</summary>
    public const string ActionSelfCheck = "selfcheck";
    private readonly Func<ControlRequest, string?>? _onControl;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private readonly string _pipeName;
    private readonly Action<BrowserDownloadRequest> _onRequestReceived;
    private int _pendingCaptures, _connections;
    /// <summary>How often creating the first pipe instance is tried (200 ms apart) before the name counts as taken by someone else.</summary>
    public int FirstInstanceAttempts { get; set; } = 10;
    /// <summary>True when the pipe name belongs to another process, so this instance accepts no captures.</summary>
    public bool PipeNameTaken { get; private set; }
    /// <summary>Raised once (on a pool thread) when the first server instance could not be created because the name is taken.</summary>
    public Action<Exception>? OnPipeNameTaken { get; set; }
    public int PendingCaptures => Volatile.Read(ref _pendingCaptures);
    
    public NativeBridge(Action<BrowserDownloadRequest> onRequestReceived, string pipeName = PipeName, Func<ControlRequest, string?>? onControl = null)
    { 
        _onRequestReceived = onRequestReceived;
        _pipeName = pipeName;
        _onControl = onControl;
    }

    /// <summary>Second-launch client: sends one control message to the running instance. Retries while the pipe is still starting.</summary>
    public static async Task<(bool Ok, string? Error)> SendControlAsync(string pipeName, string action, string? value, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        Exception? last = null;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(2000, cts.Token);
                var message = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { action, value }) + "\n");
                await client.WriteAsync(message, cts.Token); await client.FlushAsync(cts.Token);
                var reply = await ReadRequestAsync(client, cts.Token);
                using var json = JsonDocument.Parse(reply);
                bool ok = json.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                string? error = json.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                return (ok, error);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or FormatException)
            {
                last = ex;
                try { await Task.Delay(150, cts.Token); } catch (OperationCanceledException) { break; }
            }
        }
        return (false, "Could not reach the running instance" + (last is null ? "." : ": " + last.Message));
    }
    
    /// <summary>Raised (on a pool thread) when accepting a connection failed. The watchdog uses it to check the browser connection soon.</summary>
    public Action? OnListenerFault { get; set; }

    public void Start() => _loop ??= Task.Run(RunAsync);

    /// <summary>The first instance is created with FirstPipeInstance: if the name already exists, another process (a second NeoDM or something squatting on the name) owns it and no capture is accepted here.</summary>
    private async Task<NamedPipeServerStream?> CreateFirstInstanceAsync()
    {
        Exception? last = null;
        for (int attempt = 0; attempt < Math.Max(1, FirstInstanceAttempts) && !_stop.IsCancellationRequested; attempt++)
        {
            try
            {
                return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                last = ex; // a listener being replaced may need a moment to let go of the name
                try { await Task.Delay(200, _stop.Token); } catch (OperationCanceledException) { return null; }
            }
        }
        if (last is not null && !_stop.IsCancellationRequested)
        {
            PipeNameTaken = true;
            try { OnPipeNameTaken?.Invoke(last); } catch (Exception) { }
        }
        return null;
    }

    private async Task RunAsync()
    {
        bool first = true;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                // Several captures can wait for their dialogs at once, so every connection is served on its own and the next one is accepted right away.
                NamedPipeServerStream? pipe;
                if (first) { pipe = await CreateFirstInstanceAsync(); if (pipe is null) return; first = false; }
                else pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(_stop.Token); }
                catch { pipe.Dispose(); throw; }
                if (Interlocked.Increment(ref _connections) > MaxConnections) { Interlocked.Decrement(ref _connections); pipe.Dispose(); continue; }
                _ = Task.Run(async () =>
                {
                    try { await HandleAsync(pipe, _stop.Token); }
                    catch (Exception) { }
                    finally { pipe.Dispose(); Interlocked.Decrement(ref _connections); }
                });
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception) when (!_stop.IsCancellationRequested)
            {
                // Any failure while accepting (not only I/O errors) must not end the loop, otherwise the app stops listening without a sign.
                try { OnListenerFault?.Invoke(); } catch (Exception) { }
                try { await Task.Delay(100, _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleAsync(Stream pipe, CancellationToken stopToken)
    {
        // Reading the request is bounded (a silent client must not hold the pipe); waiting for the user's decision is not.
        using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        readLimit.CancelAfter(TimeSpan.FromSeconds(30));
        var cancellationToken = readLimit.Token;
        TaskCompletionSource<BrowserDownloadResponse> responseSource = new();
        bool counted = false;
        
        try
        {
            var payload = await ReadRequestAsync(pipe, cancellationToken);
            cancellationToken = stopToken; // from here on only app shutdown ends the wait
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("Request must be a JSON object.");
            var root = json.RootElement;
            if (root.TryGetProperty("action", out var actionElement))
            {
                if (actionElement.ValueKind == JsonValueKind.String && actionElement.GetString() == ActionSelfCheck)
                {
                    var pong = Encoding.UTF8.GetBytes("{\"ok\":true,\"selfcheck\":true}\n");
                    await pipe.WriteAsync(pong, cancellationToken); await pipe.FlushAsync(cancellationToken);
                    return;
                }
                string? problem = actionElement.ValueKind == JsonValueKind.String && _onControl is not null
                    ? HandleControl(actionElement.GetString()!, ReadString(root, "value")) : "Unsupported request.";
                var controlReply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(problem is null ? (object)new { ok = true } : new { ok = false, error = problem }) + "\n");
                await pipe.WriteAsync(controlReply, cancellationToken); await pipe.FlushAsync(cancellationToken);
                return;
            }
            var url = ReadString(root, "url", required: true)!;
            ValidateUrl(url);
            if (url.Length > MaximumUrlLength) throw new FormatException("Address too long.");
            bool noProbe = false;
            if (root.TryGetProperty("noProbe", out var noProbeElement) && noProbeElement.ValueKind != JsonValueKind.Null)
            {
                if (noProbeElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException("Expected a boolean.");
                noProbe = noProbeElement.ValueKind == JsonValueKind.True;
            }
            
            long? size = null; // optional: the size the browser itself reported (DownloadItem.totalBytes or fileSize)
            if (root.TryGetProperty("size", out var sizeElement) && sizeElement.ValueKind != JsonValueKind.Null)
            {
                if (sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out var parsedSize) || parsedSize < 0 || parsedSize > MaximumBrowserSize) throw new FormatException("Invalid size.");
                if (parsedSize > 0) size = parsedSize;
            }
            var fileName = ReadString(root, "fileName");
            var referer = ReadString(root, "referer");
            var cookie = ReadString(root, "cookie");
            if (cookie is not null && (cookie.Length > MaximumCookieLength || cookie.Any(char.IsControl))) throw new FormatException("Invalid cookie."); // a Cookie header the browser sent: no control characters, at most 32768 characters
            var mime = ReadString(root, "mime");
            // 1.5.7, optional: the request identity the browser used for this download. Printable ASCII only, so no control character can reach a header; an older extension sends nothing.
            var userAgent = ReadString(root, "userAgent"); var accept = ReadString(root, "accept"); var acceptLanguage = ReadString(root, "acceptLanguage");
            if (userAgent is not null && !RequestIdentity.IsHeaderValue(userAgent, RequestIdentity.MaxUserAgent)) throw new FormatException("Invalid User-Agent.");
            if (accept is not null && !RequestIdentity.IsHeaderValue(accept, RequestIdentity.MaxAccept)) throw new FormatException("Invalid Accept.");
            if (acceptLanguage is not null && !RequestIdentity.IsHeaderValue(acceptLanguage, RequestIdentity.MaxAcceptLanguage)) throw new FormatException("Invalid Accept-Language.");
            // 1.6.0, optional: "hls", "dash" or "auto" marks the address as a streaming playlist or manifest (the stream dialog is shown instead of the file dialog).
            var stream = ReadString(root, "stream");
            if (stream is not null && !ValidStream(stream)) throw new FormatException("Invalid stream kind.");
            var extVersion = ReadString(root, "extVersion"); // optional: the browser extension's manifest version; older extensions send nothing
            if (extVersion is not null && !ValidExtVersion(extVersion)) throw new FormatException("Invalid extension version.");
            var extId = ReadString(root, "extId"); // optional (1.5.6): the ID of the extension that called the host (store or development copy); an older host sends nothing
            if (extId is not null && !ValidExtId(extId)) throw new FormatException("Invalid extension id.");
            if (fileName is { Length: > 512 }) throw new FormatException("File name too long.");
            if (mime is not null && !ValidMime(mime)) throw new FormatException("Invalid content type.");
            
            // At most MaxPendingCaptures dialogs wait at the same time: a page that fires many downloads cannot bury the user in windows.
            if (Interlocked.Increment(ref _pendingCaptures) > MaxPendingCaptures)
            {
                Interlocked.Decrement(ref _pendingCaptures);
                var busy = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = false, error = BusyError, cancelled = false }) + "\n");
                await pipe.WriteAsync(busy, cancellationToken); await pipe.FlushAsync(cancellationToken);
                return;
            }
            counted = true;
            // The client sends nothing after its request, so a read that completes means it hung up (browser restarted the download or the host died).
            using var gone = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            _ = WatchForHangUpAsync(pipe, gone, stopToken);
            // Queue the request and notify the UI thread
            var browserRequest = new BrowserDownloadRequest(url, fileName, referer, cookie, response => responseSource.TrySetResult(response), mime, gone.Token, extVersion, noProbe, size, extId, userAgent, accept, acceptLanguage, stream);
            _onRequestReceived(browserRequest);
            
            // Wait for the UI thread to respond. No time limit: the user decides when to; the wait ends early only if the client hangs up.
            BrowserDownloadResponse response;
            try { response = await responseSource.Task.WaitAsync(gone.Token); }
            catch (OperationCanceledException) when (!stopToken.IsCancellationRequested) { return; } // nobody is listening any more; the dialog was told
            
            var replyObject = response.Ok 
                ? (object)new { ok = true, id = response.Id }
                : response.HandBack ? new { ok = false, error = response.Error ?? "Download left to the browser.", cancelled = false, handBack = true }
                : (object)new { ok = false, error = response.Error ?? "The download could not be saved.", cancelled = response.Cancelled };
            
            var reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(replyObject) + "\n");
            await pipe.WriteAsync(reply, cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) 
        { 
            try
            {
                // Send error response if dialog was cancelled/timed out
                var errorResponse = new { ok = false, error = "The download dialog was cancelled or timed out." };
                var reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(errorResponse) + "\n");
                await pipe.WriteAsync(reply, cancellationToken);
                await pipe.FlushAsync(cancellationToken);
            }
            catch { }
            return; 
        }
        catch (Exception ex)
        {
            // Do not echo engine exceptions: they may include authenticated URLs or cookies.
            var errorResponse = new { ok = false, error = ex is FormatException or JsonException ? "Invalid request. Send an HTTP(S) URL in a newline-terminated JSON object, at most 64 KiB." : "The download could not be saved. Check NeoDM and try again." };
            try
            {
                var reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(errorResponse) + "\n");
                await pipe.WriteAsync(reply, cancellationToken);
                await pipe.FlushAsync(cancellationToken);
            }
            catch (Exception pipeEx) when (pipeEx is IOException or OperationCanceledException) { }
        }
        finally { if (counted) Interlocked.Decrement(ref _pendingCaptures); }
    }

    private static async Task WatchForHangUpAsync(Stream pipe, CancellationTokenSource gone, CancellationToken stop)
    {
        try { var one = new byte[1]; await pipe.ReadAsync(one, stop); }
        catch (Exception) { }
        if (!stop.IsCancellationRequested) { try { gone.Cancel(); } catch (ObjectDisposedException) { } }
    }

    private string? HandleControl(string action, string? value)
    {
        switch (action)
        {
            case ActionShow: break;
            case ActionAddMagnet:
                if (value is null || value.Length > 8192 || !value.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) || value.Any(char.IsControl)) return "Not a valid magnet link.";
                break;
            case ActionAddTorrent:
                if (value is null || value.Length > 32767 || !value.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(value) || value.Any(char.IsControl)) return "Not a valid .torrent path.";
                if (!SourcePaths.IsPlainLocal(value)) return "Network (UNC) and device paths are not accepted for a .torrent file.";
                break;
            default: return "Unknown action.";
        }
        try { return _onControl!(new ControlRequest(action, value)); }
        catch (Exception) { return "The running instance could not handle the request."; }
    }

    /// <summary>Extension version on the wire: 2 to 4 numeric parts of 1 to 4 digits, at most 24 characters (same rule as the native host).</summary>
    /// <summary>An extension ID as the browser makes it: 32 letters a-p.</summary>
    /// <summary>The optional stream field of a capture request: exactly "hls", "dash" or "auto".</summary>
    public static bool ValidStream(string value) => value is "hls" or "dash" or "auto";
    public static bool ValidExtId(string value) => value.Length == 32 && value.All(c => c is >= 'a' and <= 'p');
    public static bool ValidExtVersion(string value) => value.Length <= 24 && System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{1,4}(\.\d{1,4}){1,3}\z");

    /// <summary>type/subtype only: the browser's content type for the download, at most 100 characters of token characters.</summary>
    public static bool ValidMime(string value) => value.Length is > 2 and <= 100 && System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*/[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*$");

    /// <summary>True when the capture dialog must not request the address just to learn the name: a long address with a token-like path segment
    /// (24 or more letters, digits, '-' or '_' and no dot) is usually a single-use or per-client redirect target.</summary>
    public static bool LooksLikeTokenUrl(string url)
    {
        if (url.Length <= 200 || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        return parsed.AbsolutePath.Split('/').Any(s => s.Length >= 24 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }
    public static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || (parsed.Scheme != "http" && parsed.Scheme != "https") || string.IsNullOrEmpty(parsed.Host))
            throw new FormatException("Enter a complete http:// or https:// download URL.");
    }
    /// <summary>Longest Cookie header value accepted from the extension (the native host enforces the same limit; the extension itself sends at most 8192).</summary>
    public const int MaximumCookieLength = 32768;
    private static string? ReadString(JsonElement root, string key, bool required = false)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new FormatException("Missing URL.");
            return null;
        }
        if (value.ValueKind != JsonValueKind.String) throw new FormatException("Expected a string.");
        var text = value.GetString();
        return !required && string.IsNullOrEmpty(text) ? null : text;
    }
    public static async Task<byte[]> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) throw new FormatException("Missing newline.");
            int newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            int count = newline >= 0 ? newline : read;
            if (buffer.Length + count > MaximumRequestBytes) throw new FormatException("Request exceeds 64 KiB.");
            buffer.Write(chunk, 0, count);
            if (newline >= 0) return buffer.ToArray();
        }
    }
    public void Dispose() { _stop.Cancel(); /* Never block the UI while a dispatched acceptance finishes. */ }
}
