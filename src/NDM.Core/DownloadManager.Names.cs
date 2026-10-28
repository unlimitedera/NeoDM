using System.Net;
using System.Net.Http.Headers;

namespace NDM.Core;

public sealed partial class DownloadManager
{
    private const int HeadBytes = 1024;
    private static readonly TimeSpan NameProbeTimeout = TimeSpan.FromSeconds(8);

    private static string? RawHeader(HttpResponseMessage response, string name) =>
        response.Content.Headers.TryGetValues(name, out var values) || response.Headers.TryGetValues(name, out values) ? values.FirstOrDefault() : null;

    private static NameProbeResult Describe(HttpResponseMessage response, byte[] head)
    {
        long? length = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        return new NameProbeResult(response.RequestMessage?.RequestUri?.AbsoluteUri ?? "", RawHeader(response, "Content-Disposition"), RawHeader(response, "Content-Type"), length, head, (int)response.StatusCode);
    }

    private static byte[] ReadHead(string file, int count = 64)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[count]; var read = stream.Read(buffer, 0, buffer.Length);
            return buffer[..read];
        }
        catch (IOException) { return Array.Empty<byte>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<byte>(); }
    }

    /// <summary>Quick look at a link before it is downloaded: HEAD, or a ranged GET of the first bytes when HEAD is refused or does not settle the extension.
    /// Redirects are followed, proxy/referer/cookie are honoured, 8 s overall. Null when the server could not be reached.</summary>
    public async Task<NameProbeResult?> ProbeNameAsync(DownloadRequest request, CancellationToken cancellation = default)
    {
        ValidateRequest(request);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(NameProbeTimeout);
        var uri = new Uri(request.Url);
        var memory = MemoryOf(request.Url);
        NameProbeResult? head = null;
        // A site that refused HEAD earlier in this session is not asked with HEAD again.
        if (!memory.HeadRefused) try
        {
            using var message = ProbeMessage(HttpMethod.Head, request, memory);
            using var response = await SendAsync(message, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && (int)response.StatusCode >= 400) memory.HeadRefused = true;
            if (response.IsSuccessStatusCode)
            {
                head = Describe(response, Array.Empty<byte>());
                var resolved = FileNameResolver.Resolve(uri, null, null, head);
                if (resolved.HasExtension || FileNameResolver.FromContentDisposition(head.ContentDisposition) is not null) return head;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellation.IsCancellationRequested)) { }
        if (memory.RangeRefused) return head;
        try
        {
            using var message = ProbeMessage(HttpMethod.Get, request, memory);
            message.Headers.Range = new RangeHeaderValue(0, HeadBytes - 1);
            using var response = await SendAsync(message, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { if (CompatibilityLadder.IsLadderStatus(response.StatusCode, response.Headers.WwwAuthenticate.Count > 0)) memory.RangeRefused = true; return head; }
            var bytes = new byte[HeadBytes]; var count = 0;
            using (var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false))
            {
                while (count < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count), limit.Token).ConfigureAwait(false);
                    if (read == 0) break; count += read;
                }
            }
            var described = Describe(response, bytes[..count]);
            return head is not null && described.Length is null ? described with { Length = head.Length } : described;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellation.IsCancellationRequested)) { return head; }
    }

    private HttpRequestMessage ProbeMessage(HttpMethod method, DownloadRequest request, HostMemory memory) =>
        BuildMessage(method, request, (memory.Working ?? default) with { NoRange = false });

    /// <summary>Gives an auto-named job its real name (server answer and/or file signature). Never touches a name the user chose.</summary>
    private void SettleName(Work w, NameProbeResult? probe, byte[]? head)
    {
        lock (gate)
        {
            if (!w.Request.AutoName || w.Removed || disposed) return;
            var uri = new Uri(w.Request.Url);
            ResolvedName resolved;
            if (probe is null)
            {
                // File-signature pass: keep the name chosen when the transfer started, only add a missing extension.
                var current = w.Job.FileName;
                var extension = head is { Length: > 0 } ? FileNameResolver.ExtensionFromBytes(head) : null;
                w.Request = w.Request with { AutoName = false };
                if (extension is null || FileNameResolver.HasUsefulExtension(current)) return;
                resolved = new ResolvedName(DownloadFileNames.Sanitize(current.TrimEnd('.') + extension), NameSource.Url, true);
            }
            else resolved = FileNameResolver.Resolve(uri, w.Request.FileName, null, probe);
            var name = AvailableName(resolved.Name, w, w.Request.TargetDirectory is null ? null : Path.GetDirectoryName(w.Job.FilePath));
            if (name != w.Job.FileName)
            {
                var oldDirectory = Path.GetDirectoryName(w.Job.FilePath) ?? downloads;
                var autoDirectory = Path.GetFullPath(pathResolver?.Invoke(w.Job.FileName) ?? downloads);
                // The category folder follows the final extension unless the job was moved somewhere else on purpose.
                var directory = w.Request.TargetDirectory is not null ? oldDirectory : Path.GetFullPath(oldDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(autoDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                    ? pathResolver?.Invoke(name) ?? downloads : oldDirectory;
                w.Job.FileName = name; w.Job.FilePath = Path.Combine(directory, name);
            }
            if (probe is not null && resolved.HasExtension) w.Request = w.Request with { AutoName = false };
            try { SaveLocked(); } catch (Exception ex) { LastPersistenceError = ex.Message; }
        }
        Notify();
    }
}
