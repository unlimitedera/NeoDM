using System.Net;
using System.Net.Http.Headers;

namespace NDM.Core;

public enum LinkAvailability { Online, Offline, Unknown }

/// <summary>What a link check learned without downloading the file. <see cref="IsPage"/>: the address answered with a web page and not a file (the download would fail with the wrong-content message).</summary>
public sealed record LinkCheckResult(LinkAvailability Availability, long? Length, int? Status, string? FileName, string? ContentType, bool IsPage);

public sealed partial class DownloadManager
{
    private static readonly TimeSpan CheckTotalTimeout = TimeSpan.FromSeconds(25);
    private const int CheckMaxAttempts = 3;

    /// <summary>1.9.0 link check: HEAD, then (when HEAD is refused) a ranged GET of the first byte, climbing the same compatibility steps as a download (no Range, browser identity, referer shapes) for at most three attempts.
    /// Uses the manager's proxy, identity, referer and cookie handling. Only headers are read; no body is saved. 404 and 410 mean offline; every other failure is unknown, never offline.
    /// Never follows page content into other links and never solves anything: it reports what the server answered.</summary>
    public async Task<LinkCheckResult> CheckLinkAsync(DownloadRequest request, CancellationToken cancellation = default)
    {
        ValidateRequest(request);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(CheckTotalTimeout);
        var uri = new Uri(request.Url);
        var memory = MemoryOf(request.Url);
        int? lastStatus = null;
        try
        {
            if (!memory.HeadRefused)
            {
                using var head = BuildMessage(HttpMethod.Head, request, (memory.Working ?? default) with { NoRange = true });
                using var response = await SendAsync(head, limit.Token).ConfigureAwait(false);
                lastStatus = (int)response.StatusCode;
                if (response.IsSuccessStatusCode) return Describe(uri, response);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return new(LinkAvailability.Offline, null, lastStatus, null, null, false);
                memory.HeadRefused = true;
            }
            var variant = (memory.Working ?? default) with { NoRange = false };
            for (int attempt = 1; ; attempt++)
            {
                using var message = BuildMessage(HttpMethod.Get, request, variant);
                if (!variant.NoRange) message.Headers.Range = new RangeHeaderValue(0, 0);
                using var response = await SendAsync(message, limit.Token).ConfigureAwait(false);
                lastStatus = (int)response.StatusCode;
                if (response.IsSuccessStatusCode) return Describe(uri, response);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return new(LinkAvailability.Offline, null, lastStatus, null, null, false);
                var next = attempt < CheckMaxAttempts && CompatibilityLadder.IsLadderStatus(response.StatusCode, response.Headers.WwwAuthenticate.Count > 0)
                    ? CompatibilityLadder.Next(variant, options.Identity, request.Referer, uri) : null;
                if (next is null) return new(LinkAvailability.Unknown, null, lastStatus, null, null, false);
                var delay = CompatibilityLadder.Delay(response.StatusCode, response.Headers.RetryAfter, DateTimeOffset.UtcNow, options.LadderPause, options.MaxRetryAfter);
                variant = next.Value;
                await Task.Delay(delay > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : delay, limit.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellation.IsCancellationRequested))
        {
            return new(LinkAvailability.Unknown, null, lastStatus, null, null, false);
        }
    }

    private static LinkCheckResult Describe(Uri uri, HttpResponseMessage response)
    {
        var probe = Describe(response, Array.Empty<byte>());
        var type = probe.ContentType;
        bool attachment = probe.ContentDisposition is { } disposition && disposition.Contains("attachment", StringComparison.OrdinalIgnoreCase);
        bool page = !attachment && type is not null && (type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) || type.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase));
        string? name = null;
        try { name = FileNameResolver.Resolve(uri, null, null, probe).Name; } catch (Exception ex) when (ex is ArgumentException or FormatException) { }
        return new(LinkAvailability.Online, probe.Length, (int)response.StatusCode, name, type, page);
    }
}
