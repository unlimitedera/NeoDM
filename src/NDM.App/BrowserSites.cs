using System.IO;
using System.Collections.Concurrent;
using NDM.Core;

namespace NDM.App;

/// <summary>Sites NeoDM could not fetch (a captured download came back as a web page, or the user chose "Download in browser instead").
/// The app appends the host name to <c>browser-sites.txt</c> in its state folder; the native host hands the list to the extension once and removes it,
/// and the extension from then on leaves downloads from that site to the browser. Host names only, at most 100.</summary>
public static class BrowserSites
{
    public const string FileName = "browser-sites.txt";
    public const int Maximum = 100;
    private const string MutexName = @"Local\NDM.BrowserSites"; // same name as in the native host

    /// <summary>The lower-case host name of an http(s) address; null when there is none or it is not a plain name or address.</summary>
    public static string? HostOf(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        return host.Length <= 253 && host.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-') ? host : null;
    }

    /// <summary>Adds the host of the address to the list file (no duplicates); true when the file was changed.</summary>
    public static bool Add(string stateDirectory, string? url)
    {
        if (HostOf(url) is not { } host) return false;
        try
        {
            using var gate = new Mutex(false, MutexName);
            bool held; try { held = gate.WaitOne(2000); } catch (AbandonedMutexException) { held = true; }
            try
            {
                Directory.CreateDirectory(stateDirectory);
                var file = Path.Combine(stateDirectory, FileName);
                var lines = File.Exists(file) ? File.ReadAllLines(file).Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0).ToList() : new List<string>();
                if (lines.Contains(host)) return false;
                lines.Add(host);
                File.WriteAllLines(file, lines.TakeLast(Maximum));
                return true;
            }
            finally { if (held) gate.ReleaseMutex(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The host names currently waiting in the file for the extension to fetch (for tests and diagnostics).</summary>
    public static IReadOnlyList<string> Pending(string stateDirectory)
    {
        var file = Path.Combine(stateDirectory, FileName);
        try { return File.Exists(file) ? File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray() : Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }
}

/// <summary>Watches the download list: a download the browser handed over that fails with "wrong-content" puts its site on the list, once.</summary>
public sealed class WrongContentWatcher
{
    private readonly ConcurrentDictionary<string, bool> _captured = new(), _done = new();
    private readonly Action<string> _remember;
    public WrongContentWatcher(Action<string> remember) { _remember = remember; }
    /// <summary>Marks a job as started from the browser (a download added by hand never puts a site on the list).</summary>
    public void Captured(string jobId) => _captured[jobId] = true;
    public void Observe(IEnumerable<DownloadJob> jobs)
    {
        foreach (var job in jobs)
            if (job.Status == "Failed" && job.ErrorCode == ContentGuard.WrongContent && _captured.ContainsKey(job.Id) && _done.TryAdd(job.Id, true)) _remember(job.Url);
    }
}
