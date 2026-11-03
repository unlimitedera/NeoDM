using System.Net.Http;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

public enum LinkState { Ready, Checking, Online, Offline, Unknown, Page, NotChecked, Duplicate, Unsupported }

/// <summary>One row of the link grabber table. Nothing here downloads; it is a staging area.</summary>
public sealed class LinkEntry : INotifyPropertyChanged
{
    private bool _selected; private string _name = ""; private LinkState _state; private long? _size; private int? _status;
    public string Url { get; init; } = "";
    public string Host { get; init; } = "";
    public string GuessedName { get; init; } = "";
    public StreamKind Stream { get; init; }
    /// <summary>The name the server gave (Content-Disposition, redirect) after a check; null before.</summary>
    public string? ServerName { get; private set; }
    public bool Renamed { get; private set; }
    public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; Raise(nameof(Selected)); } }
    public string Name { get => _name; set { var v = value ?? ""; if (v == _name) return; _name = v; Renamed = v != GuessedName && v != ServerName; Raise(nameof(Name)); Raise(nameof(Extension)); } }
    public string Extension => Path.GetExtension(_name).TrimStart('.').ToLowerInvariant();
    public LinkState State { get => _state; set { _state = value; Raise(nameof(State)); Raise(nameof(StatusText)); } }
    public long? Size { get => _size; private set { _size = value; Raise(nameof(Size)); Raise(nameof(SizeText)); } }
    public int? HttpStatus { get => _status; private set { _status = value; Raise(nameof(StatusText)); } }
    public string SizeText => Size is { } s ? Loc.Size(s) : "—";
    public string StatusText => State switch
    {
        LinkState.Ready => Loc.T("grab.state.ready"), LinkState.Checking => Loc.T("grab.state.checking"), LinkState.Online => Loc.T("grab.state.online"),
        LinkState.Offline => Loc.T("grab.state.offline"), LinkState.Page => Loc.T("grab.state.page"), LinkState.NotChecked => Loc.T("grab.state.not_checked"),
        LinkState.Duplicate => Loc.T("grab.state.duplicate"), LinkState.Unsupported => Loc.T("grab.state.youtube"),
        _ => HttpStatus is { } code ? Loc.T("grab.state.unknown_status", code) : Loc.T("grab.state.unknown")
    };
    public void ApplyCheck(LinkCheckResult result)
    {
        Size = result.Length; HttpStatus = result.Status;
        if (result.FileName is { Length: > 0 } serverName && !Renamed) { ServerName = DownloadFileNames.Sanitize(serverName); _name = ServerName; Raise(nameof(Name)); Raise(nameof(Extension)); }
        State = result.Availability switch { LinkAvailability.Offline => LinkState.Offline, LinkAvailability.Unknown => LinkState.Unknown, _ => result.IsPage ? LinkState.Page : LinkState.Online };
    }
    public void RaiseLanguage() { Raise(nameof(StatusText)); Raise(nameof(SizeText)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Where the files go: the category rules (nothing set), a folder, or a new folder named after the package.</summary>
public sealed record GrabberDestination(string? PackageName, string? Folder, bool SubfolderForPackage);

public sealed record GrabberParseSummary(int Added, int Duplicates, int AlreadyQueued, int Ignored, int TooLong, bool Truncated);
public sealed record GrabberAddOutcome(int Added, int Failed, string? Package, string? Folder, IReadOnlyList<DownloadJob> Jobs);

/// <summary>UI-free logic of the link grabber (parse, check, plan, add), so it can be tested without a window. Generic by design: direct web links only; pages are not scraped, nothing is solved, no waiting time is skipped.</summary>
public sealed class LinkGrabberLogic
{
    public const int CheckConcurrency = 4;
    private readonly DownloadManager _manager; private readonly AppSettings _settings;
    public ObservableCollection<LinkEntry> Entries { get; } = new();
    public LinkGrabberLogic(DownloadManager manager, AppSettings settings) { _manager = manager; _settings = settings; }

    /// <summary>Parses text and appends new rows (links already in the table are not added twice). Links already in the download list are marked and start unselected.</summary>
    public GrabberParseSummary Load(string? text)
    {
        var parsed = LinkParser.Parse(text);
        var known = Entries.Select(e => e.Url).ToHashSet(StringComparer.Ordinal);
        var queued = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in _manager.Jobs) { if (Uri.TryCreate(job.Url, UriKind.Absolute, out var u)) queued.Add(u.GetLeftPart(UriPartial.Path) + u.Query); }
        int added = 0, dup = parsed.Duplicates, already = 0;
        foreach (var link in parsed.Links)
        {
            if (!known.Add(link.Url)) continue; // already in the table from an earlier look at the same text
            if (Entries.Count >= LinkParser.MaxLinks) return new(added, dup, already, parsed.Ignored, parsed.TooLong, true);
            var kind = StreamMessages.KindFromAddress(link.Url);
            var name = LinkParser.GuessName(link.Url);
            var state = YoutubeUrls.IsHost(link.Url) ? LinkState.Unsupported : queued.Contains(link.Url) ? LinkState.Duplicate : LinkState.Ready;
            if (state == LinkState.Duplicate) already++;
            Entries.Add(new LinkEntry { Url = link.Url, Host = link.Host, GuessedName = name, Name = name, Stream = kind, State = state, Selected = state == LinkState.Ready });
            added++;
        }
        return new(added, dup, already, parsed.Ignored, parsed.TooLong, parsed.Truncated);
    }

    /// <summary>The check pass. Skipped by default for addresses that look single use (the same rule the browser capture uses), for YouTube links and for stream playlists. Concurrency is small and the run can be cancelled; rows that were not reached stay as they were.</summary>
    public async Task CheckAsync(IEnumerable<LinkEntry> rows, CancellationToken cancellation, IProgress<(int Done, int Total)>? progress = null, int concurrency = CheckConcurrency)
    {
        var todo = new List<LinkEntry>();
        foreach (var row in rows)
        {
            if (row.State == LinkState.Unsupported) continue;
            if (NativeBridge.LooksLikeTokenUrl(row.Url) || row.Stream != StreamKind.None) { row.State = LinkState.NotChecked; continue; }
            todo.Add(row);
        }
        int done = 0, total = todo.Count; progress?.Report((0, total));
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 8));
        var tasks = todo.Select(async row =>
        {
            try { await gate.WaitAsync(cancellation); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (cancellation.IsCancellationRequested) return;
                var before = row.State; row.State = LinkState.Checking;
                try
                {
                    var result = await _manager.CheckLinkAsync(new DownloadRequest(row.Url), cancellation);
                    row.ApplyCheck(result);
                    if (result.Availability == LinkAvailability.Offline || result.IsPage) row.Selected = false;
                }
                catch (OperationCanceledException) { row.State = before == LinkState.Checking ? LinkState.Ready : before; }
                catch (Exception ex) when (ex is ArgumentException or HttpRequestException or IOException or InvalidOperationException) { row.State = LinkState.Unknown; }
                progress?.Report((Interlocked.Increment(ref done), total));
            }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks);
    }

    /// <summary>Package name cleaned for use as a group name and a folder name: no control characters, no path characters, at most 120 characters; null when empty.</summary>
    public static string? CleanPackage(string? name)
    {
        var n = ReservedNames.StripInvisible((name ?? "").Trim());
        if (n.Length == 0) return null;
        n = System.Text.RegularExpressions.Regex.Replace(new string(n.Select(c => char.IsControl(c) ? ' ' : c).ToArray()), @"\s+", " ").Trim();
        if (n.Length > 120) n = n[..120].TrimEnd();
        return n.Length == 0 ? null : n;
    }

    /// <summary>The explicit folder for the jobs, or null for 'use the category rules'. A subfolder named after the package goes under the chosen folder (or the download folder). Throws UserMessageException for an unusable choice.</summary>
    public string? PlanFolder(GrabberDestination destination)
    {
        string? folder = string.IsNullOrWhiteSpace(destination.Folder) ? null : destination.Folder.Trim();
        if (folder is not null && !Path.IsPathFullyQualified(folder)) throw new UserMessageException(Loc.T("error.folder_incomplete"));
        var package = CleanPackage(destination.PackageName);
        if (destination.SubfolderForPackage && package is not null)
        {
            var segment = DownloadFileNames.Sanitize(package);
            if (ReservedNames.IsReserved(segment) || segment is "." or "..") throw new UserMessageException(Loc.T("grab.err.package_folder"));
            var parent = folder ?? _settings.DownloadDirectory;
            var combined = CategoryPaths.Resolve(parent, segment, false, out var error) ?? throw new UserMessageException(Loc.T(error ?? "grab.err.package_folder"));
            return combined;
        }
        return folder;
    }

    public IReadOnlyList<DownloadRequest> BuildRequests(IEnumerable<LinkEntry> rows, GrabberDestination destination)
    {
        var folder = PlanFolder(destination); var package = CleanPackage(destination.PackageName);
        return rows.Select(row =>
        {
            bool named = row.Renamed || row.ServerName is not null;
            return new DownloadRequest(row.Url, named ? DownloadFileNames.Sanitize(row.Name) : null, null, null, AutoName: !named, TargetDirectory: folder, StreamKind: row.Stream, Package: package);
        }).ToList();
    }

    /// <summary>Adds the selected rows to the download list in table order. 'Start' queues them (the connection limit decides how many run at once); otherwise they are added paused.</summary>
    public async Task<GrabberAddOutcome> AddSelectedAsync(GrabberDestination destination, bool start)
    {
        var rows = Entries.Where(e => e.Selected && e.State != LinkState.Unsupported).ToList();
        var requests = BuildRequests(rows, destination);
        var result = await _manager.AddManyAsync(requests, startPaused: !start);
        return new(result.Added.Count, result.Failed.Count, CleanPackage(destination.PackageName), PlanFolder(destination), result.Added);
    }
}
