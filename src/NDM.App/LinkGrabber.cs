// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Net.Http;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>Youtube (1.11.0): a YouTube video or playlist link; it opens the YouTube dialogs instead of being added as a file. Unsupported: other YouTube pages (channels, mixes).</summary>
public enum LinkState { Ready, Checking, Online, Offline, Unknown, Page, NotChecked, Duplicate, Unsupported, Youtube }

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
        LinkState.Duplicate => Loc.T("grab.state.duplicate"), LinkState.Unsupported => Loc.T("grab.state.youtube"), LinkState.Youtube => Loc.T("grab.state.youtube_open"),
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
/// <summary>1.16.0: where a package goes and whether it starts by itself. <see cref="QueueId"/> is an existing queue; <see cref="NewQueueName"/> makes (or reuses) a queue of that name, normally the package name.</summary>
public sealed record PackagePlan(string? QueueId, string? NewQueueName, bool AutoStart);
public sealed record GrabberAddOutcome(int Added, int Failed, string? Package, string? Folder, IReadOnlyList<DownloadJob> Jobs, IReadOnlyList<PageLinkItem>? PageLinks = null, string? QueueId = null);

/// <summary>UI-free logic of the link grabber (parse, check, plan, add), so it can be tested without a window. Generic by design: direct web links only; pages are not scraped, nothing is solved, no waiting time is skipped.</summary>
public sealed class LinkGrabberLogic
{
    public const int CheckConcurrency = 4;
    private readonly DownloadManager _manager; private readonly AppSettings _settings;
    /// <summary>2.0.0: looks at the batch before it is added (the duplicate question); returns the requests to add, or null to add nothing.</summary>
    public Func<IReadOnlyList<DownloadRequest>, List<DownloadRequest>?>? RequestFilter { get; set; }
    public DownloadManager Manager => _manager;
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
            var state = YoutubeUrls.IsHost(link.Url) ? (YoutubeLinks.Parse(link.Url).Kind != YoutubeLinkKind.None ? LinkState.Youtube : LinkState.Unsupported) : queued.Contains(link.Url) ? LinkState.Duplicate : LinkState.Ready;
            if (state == LinkState.Duplicate) already++;
            Entries.Add(new LinkEntry { Url = link.Url, Host = link.Host, GuessedName = name, Name = name, Stream = kind, State = state, Selected = state is LinkState.Ready or LinkState.Youtube });
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
            if (row.State is LinkState.Unsupported or LinkState.Youtube) continue;
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

    public IReadOnlyList<DownloadRequest> BuildRequests(IEnumerable<LinkEntry> rows, GrabberDestination destination, string? queueId = null)
    {
        var folder = PlanFolder(destination); var package = CleanPackage(destination.PackageName);
        return rows.Select(row =>
        {
            bool named = row.Renamed || row.ServerName is not null;
            return new DownloadRequest(row.Url, named ? DownloadFileNames.Sanitize(row.Name) : null, null, null, AutoName: !named, TargetDirectory: folder, StreamKind: row.Stream, Package: package, QueueId: queueId);
        }).ToList();
    }

    /// <summary>Sets of numbered parts among the given rows (same host, same stem, different part numbers), in part order.</summary>
    public IReadOnlyList<(LinkGroup Group, IReadOnlyList<LinkEntry> Rows)> FindGroups(IEnumerable<LinkEntry> rows)
    {
        var list = rows.Where(r => r.State is not (LinkState.Unsupported or LinkState.Youtube) && r.Stream == StreamKind.None).ToList();
        return LinkGrouping.Group(list.Select(r => (r.Name, r.Host)).ToList()).Select(g => (g, (IReadOnlyList<LinkEntry>)g.Members.Select(i => list[i]).ToList())).ToList();
    }

    public const int ProbeLimit = 40;
    /// <summary>Before adding: a bounded look at the ticked rows nobody has checked yet, so a link that answers with a web page (not a file) can be resolved in the NeoDM browser instead of failing. Rows stay ticked.
    /// At most <see cref="ProbeLimit"/> rows and about 20 seconds; links that look single use are left alone.</summary>
    public async Task ProbeAsync(IEnumerable<LinkEntry> rows, CancellationToken token = default)
    {
        var todo = rows.Where(r => r.State == LinkState.Ready && r.Stream == StreamKind.None && !NativeBridge.LooksLikeTokenUrl(r.Url)).Take(ProbeLimit).ToList();
        if (todo.Count == 0) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token); cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var gate = new SemaphoreSlim(CheckConcurrency);
        await Task.WhenAll(todo.Select(async row =>
        {
            try { await gate.WaitAsync(cts.Token); } catch (OperationCanceledException) { return; }
            try
            {
                var result = await _manager.CheckLinkAsync(new DownloadRequest(row.Url), cts.Token);
                bool keep = row.Selected; row.ApplyCheck(result);
                row.Selected = result.Availability != LinkAvailability.Offline && keep; // a page link stays ticked: the resolver handles it
            }
            catch (Exception ex) when (ex is OperationCanceledException or ArgumentException or HttpRequestException or IOException or InvalidOperationException) { if (row.State == LinkState.Checking) row.State = LinkState.Ready; }
            finally { gate.Release(); }
        }));
    }

    /// <summary>The queue a package goes to: the chosen one, or one named after the package (an existing queue of that name is reused).</summary>
    public string ResolveQueue(PackagePlan plan)
    {
        if (plan.QueueId is { } id && _manager.Queues.Any(q => q.Id == id)) return id;
        var name = CleanPackage(plan.NewQueueName); if (name is null) return _manager.DefaultQueueId;
        if (name.Length > 60) name = name[..60].TrimEnd();
        if (_manager.Queues.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)) is { } same) return same.Id;
        return _manager.CreateQueue(name).Id;
    }

    /// <summary>The ticked YouTube links: they are not added as files; the main window opens the video or playlist dialogs for them.</summary>
    public IReadOnlyList<string> YoutubeSelected() => Entries.Where(e => e.Selected && e.State == LinkState.Youtube).Select(e => e.Url).ToList();

    /// <summary>Adds the selected rows to the download list in table order. 'Start' queues them (the connection limit decides how many run at once); otherwise they are added paused.</summary>
    public async Task<GrabberAddOutcome> AddSelectedAsync(GrabberDestination destination, bool start, PackagePlan? plan = null)
    {
        var rows = Entries.Where(e => e.Selected && e.State is not (LinkState.Unsupported or LinkState.Youtube)).ToList();
        if (plan is not null) await ProbeAsync(rows); // 1.16.0: a package finds out which links are pages
        rows = rows.Where(e => e.Selected).ToList();
        string? queueId = plan is null ? null : ResolveQueue(plan);
        bool startNow = start && (plan?.AutoStart ?? true);
        var pageRows = plan is null ? new List<LinkEntry>() : rows.Where(r => r.State == LinkState.Page).ToList();
        var fileRows = rows.Except(pageRows).ToList();
        var requests = BuildRequests(fileRows, destination, queueId);
        if (RequestFilter is not null) // 2.0.0: one question for every entry that looks already downloaded or listed
        {
            var kept = RequestFilter(requests);
            if (kept is null) return new(0, 0, CleanPackage(destination.PackageName), PlanFolder(destination), Array.Empty<DownloadJob>(), null, queueId);
            requests = kept;
        }
        BusyChoice busy = BusyChoice.Wait;
        if (plan is null && startNow) busy = BusyQueue.Current?.BeforeAddAsyncSync(requests.Count) ?? BusyChoice.Wait; // 1.15.0: the grabber adds a whole batch, asked once; a package already chose its queue
        var result = await _manager.AddManyAsync(requests, startPaused: !startNow);
        if (plan is null && startNow) BusyQueue.Current?.AfterAdd(busy, result.Added);
        var added = result.Added.ToList(); int failed = result.Failed.Count; List<PageLinkItem>? pages = null;
        if (pageRows.Count > 0)
        {
            // page links are added paused: they have no file address yet; each one starts when the NeoDM browser has captured its download
            var pageResult = await _manager.AddManyAsync(BuildRequests(pageRows, destination, queueId), startPaused: true);
            added.AddRange(pageResult.Added); failed += pageResult.Failed.Count;
            var failedIndexes = pageResult.Failed.Select(f => f.Index).ToHashSet(); int k = 0; pages = new();
            for (int i = 0; i < pageRows.Count; i++)
                if (!failedIndexes.Contains(i)) { var job = pageResult.Added[k++]; pages.Add(new PageLinkItem { Url = pageRows[i].Url, Name = pageRows[i].Name, JobId = job.Id }); }
        }
        if (startNow && queueId is not null && _manager.IsQueueHeld(queueId)) _manager.OpenQueue(queueId);
        return new(added.Count, failed, CleanPackage(destination.PackageName), PlanFolder(destination), added, pages, queueId);
    }
}
