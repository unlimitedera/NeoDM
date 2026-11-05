using NDM.App.Localization;

namespace NDM.App;

/// <summary>The outcome of one sighting.</summary>
public sealed record BrowserSighting(BrowserExtensionRecord Record, ExtensionCheck Check, bool NoticeDue);

/// <summary>
/// The small per-browser memory (1.11.0): at most <see cref="MaxBrowsers"/> browsers, kept in memory and in the settings file. It compares each browser's
/// extension version with the app and says whether a notice is due. A notice is due at most once per browser and (extension version, app version) pair,
/// at most once per browser in one app run, and never for the Chrome Web Store copy when it is ahead of the app (the store may lead by a few hours).
/// </summary>
public sealed class BrowserExtensionBook
{
    public const int MaxBrowsers = BrowserExtensionRecord.MaxBrowsers;
    public const string UnknownBrowser = BrowserExtensionRecord.UnknownBrowser;
    private readonly AppSettings settings;
    private readonly Action save;
    private readonly string appVersion;
    private readonly Func<DateTimeOffset> clock;
    private readonly HashSet<string> noticedThisRun = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    public BrowserExtensionBook(AppSettings settings, Action save, string appVersion, Func<DateTimeOffset>? clock = null)
    { this.settings = settings; this.save = save; this.appVersion = appVersion; this.clock = clock ?? (() => DateTimeOffset.UtcNow); }

    /// <summary>The records, newest sighting first.</summary>
    public IReadOnlyList<BrowserExtensionRecord> Records { get { lock (gate) return settings.BrowserExtensions.OrderByDescending(r => r.LastSeen).ToList(); } }

    /// <summary>Notes that the extension in <paramref name="browser"/> reported <paramref name="extVersion"/>. Bad values are treated as "not reported".</summary>
    public BrowserSighting Seen(string? browser, string? extVersion, string? extensionId = null)
    {
        string name = BrowserNames.IsValid(browser) ? browser!.Trim() : UnknownBrowser;
        string? version = ExtensionVersionCheck.IsValid(extVersion) ? extVersion : null;
        var channel = ExtensionChannels.Of(extensionId);
        BrowserExtensionRecord record;
        ExtensionCheck check;
        bool due;
        lock (gate)
        {
            var list = settings.BrowserExtensions;
            record = list.FirstOrDefault(r => string.Equals(r.Browser, name, StringComparison.OrdinalIgnoreCase)) ?? new BrowserExtensionRecord { Browser = name };
            if (!list.Contains(record)) list.Add(record);
            record.Version = version; record.LastSeen = clock(); if (channel != ExtensionChannel.Unknown || record.Channel == ExtensionChannel.Unknown) record.Channel = channel;
            while (list.Count > MaxBrowsers) list.Remove(list.OrderBy(r => r.LastSeen).First());
            check = ExtensionVersionCheck.Compare(version, appVersion, record.Channel, name == UnknownBrowser ? null : name);
            string key = (version ?? ExtensionNotice.MissingKey) + "|" + check.AppVersion;
            bool storeAhead = record.Channel == ExtensionChannel.Store && check.Status == ExtensionStatus.Newer;
            due = check.Status != ExtensionStatus.Match && !storeAhead && record.NoticedKey != key && !noticedThisRun.Contains(name) && !settings.MutedExtensionVersions.Contains(version ?? ExtensionNotice.MissingKey);
        }
        try { save(); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        return new BrowserSighting(record, check, due);
    }

    /// <summary>Call after the notice was shown (or dismissed): the same pair is not announced again for this browser, now or after a restart.</summary>
    public void NoticeShown(BrowserExtensionRecord record)
    {
        lock (gate)
        {
            noticedThisRun.Add(record.Browser);
            record.NoticedKey = (record.Version ?? ExtensionNotice.MissingKey) + "|" + appVersion.Split('+')[0];
        }
        try { save(); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The check for a browser that sent a capture, whether or not a notice is due (used for the hint in the capture dialog).</summary>
    public ExtensionCheck? CheckFor(string? browser, string? extVersion, string? extensionId = null)
    {
        var check = ExtensionVersionCheck.Compare(ExtensionVersionCheck.IsValid(extVersion) ? extVersion : null, appVersion, ExtensionChannels.Of(extensionId), BrowserNames.IsValid(browser) ? browser!.Trim() : null);
        return check.Status == ExtensionStatus.Match ? null : check;
    }

    /// <summary>Clears the records (nothing else). Used by tests and by the settings page "Forget".</summary>
    public void Clear() { lock (gate) { settings.BrowserExtensions.Clear(); noticedThisRun.Clear(); } try { save(); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { } }
}

/// <summary>One line of the "extension in your browsers" list in Options > General.</summary>
public sealed record BrowserExtensionRow(string Browser, string VersionText, string StatusText, string LastSeenText, string FixText, ExtensionStatus Status)
{
    public bool Ok => Status == ExtensionStatus.Match;
    /// <summary>The whole line as plain text (what Copy takes and what a screen reader reads).</summary>
    public string Summary => $"{Browser}: {VersionText}. {StatusText}. {LastSeenText}" + (FixText.Length > 0 ? " " + FixText : "");

    public static BrowserExtensionRow From(BrowserExtensionRecord record, string appVersion, DateTimeOffset now)
    {
        string shown = record.Browser == BrowserExtensionBook.UnknownBrowser ? Loc.T("extver.row.unknown_browser") : record.Browser;
        var check = ExtensionVersionCheck.Compare(record.Version, appVersion, record.Channel);
        string status = check.Status switch
        {
            ExtensionStatus.Match => Loc.T("extver.row.ok"),
            ExtensionStatus.Older => Loc.T("extver.row.older", appVersion.Split('+')[0]),
            ExtensionStatus.Newer => Loc.T("extver.row.newer", appVersion.Split('+')[0]),
            _ => Loc.T("extver.row.missing", appVersion.Split('+')[0]),
        };
        string fix = ExtensionVersionCheck.FixHint(check.Status, record.Channel);
        string version = record.Version is null ? Loc.T("extver.row.no_version") : Loc.T("extver.row.version", record.Version);
        return new BrowserExtensionRow(shown, version, status, Loc.T("extver.row.last_seen", Ago(now - record.LastSeen)), fix, check.Status);
    }

    /// <summary>"just now", "5 min ago", "3 h ago", "2 d ago". A time in the future counts as now.</summary>
    public static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return Loc.T("extver.row.ago_now");
        if (span < TimeSpan.FromHours(1)) return Loc.T("extver.row.ago_minutes", (int)span.TotalMinutes);
        if (span < TimeSpan.FromDays(1)) return Loc.T("extver.row.ago_hours", (int)span.TotalHours);
        return Loc.T("extver.row.ago_days", (int)Math.Min(span.TotalDays, 9999));
    }
}
