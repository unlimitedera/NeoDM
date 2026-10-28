using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NDM.Core;

namespace NDM.App;

public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    /// <summary>Theme id from <see cref="ThemeCatalog"/> (Light, Dark, System, Midnight, ...). Unknown values load as Light.</summary>
    public string Theme { get; set; } = "Light";
    /// <summary>Optional #RRGGBB accent that replaces the theme's own accent; null = the theme's accent.</summary>
    public string? AccentOverride { get; set; }
    /// <summary>"system" (follow the Windows display language) or a language tag from <see cref="Localization.LanguageCatalog"/>. Unknown or missing values load as "system", so old settings files need no migration.</summary>
    public string Language { get; set; } = Localization.LanguageCatalog.System;
    public int MaxSegments { get; set; } = 8;
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int RetryCount { get; set; } = 3;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public bool EnableDynamicSegmentation { get; set; } = true;
    /// <summary>1.5.7, Options > Downloads > Identify as. Browser (the default) = a download started in the browser repeats the browser's own User-Agent, Accept and Accept-Language, other downloads use a browser-style User-Agent that names NeoDM at the end; NeoDM = the product name; Custom = <see cref="CustomUserAgent"/>. Schema 6 sets Browser once.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))] public IdentityMode IdentifyAs { get; set; } = IdentityMode.Browser;
    /// <summary>The text used when <see cref="IdentifyAs"/> is Custom: 1 to 512 printable ASCII characters.</summary>
    public string CustomUserAgent { get; set; } = "";
    public bool ShowCompletionDialog { get; set; } = true;
    public bool ShowProgressForCapturedDownloads { get; set; } = true;
    /// <summary>Re-open (or restore) a download's own window when it finishes, in the Complete state. A download can override this in its 'Options on completion' tab.</summary>
    public bool ShowProgressOnCompletion { get; set; } = true;
    public bool PlayCompletionSound { get; set; } = true;
    /// <summary>Show the number of active downloads as a badge on the tray icon. A settings file without the key loads as on.</summary>
    public bool TrayShowActiveCount { get; set; } = true;
    /// <summary>Show a tray balloon when a download finishes. A settings file without the key loads as on.</summary>
    public bool TrayNotifyOnComplete { get; set; } = true;
    // Updates: the only network traffic NeoDM starts by itself is the release check (api.github.com) and the download of the update the user accepts.
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Check the browser connection (listener, registrations, helper start) after start and then at growing intervals. A settings file without the key loads as on.</summary>
    public bool BridgeWatchdogEnabled { get; set; } = true;
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public string SkippedUpdateVersion { get; set; } = "";
    /// <summary>Extension versions the user chose not to be reminded about ("none" = an extension that reports no version). No schema change: a missing list loads as empty.</summary>
    public List<string> MutedExtensionVersions { get; set; } = new();
    /// <summary>When the version notice for the Chrome Web Store extension was last shown, by "store|extension version|app version" (at most once a day for each pair). A missing value loads as empty.</summary>
    public Dictionary<string, DateTimeOffset> ExtensionNoticeTimes { get; set; } = new();
    /// <summary>Settings schema marker. 2 = category folders default to on; 3 = completion sound defaults to on (a persisted false from the old default is switched once on load); 4 = 'open the download window when a download finishes' defaults to on; 6 = 'Identify as' is set to Browser. The migrations run once; later explicit choices carry the new marker.</summary>
    public int SettingsSchema { get; set; } = CurrentSchema;
    public const int CurrentSchema = 8;
    /// <summary>1.5.9: kinds of finished downloads (Video, Music, Pictures, Documents, Archives) that open without the confirmation window. Empty = always ask. Programs, scripts, shortcuts, Office files and unknown types can never be listed: <see cref="OpenFilePolicy"/> ignores unknown names and never maps a risky file to a kind. Schema 7.</summary>
    public List<string> OpenWithoutAsking { get; set; } = new();
    /// <summary>1.6.0, Options > Downloads: pieces of a streaming download (HLS, DASH) fetched at the same time, 1 to 16. Schema 8.</summary>
    public int StreamConnections { get; set; } = 8;
    /// <summary>1.6.0: use ffmpeg.exe found in PATH when the pinned video tool is not installed. Off by default; the PATH program cannot be checked against a fingerprint. Schema 8.</summary>
    public bool UseFfmpegFromPath { get; set; } = false;
    public bool EnableCategoryFolders { get; set; } = true;
    /// <summary>Write the Mark of the Web (Zone.Identifier) on finished downloads so Windows treats the files as coming from the internet. Schema 5 turns it on once.</summary>
    public bool MarkDownloadedFiles { get; set; } = true;
    /// <summary>Set when settings.json could not be used (for example too large); the file is moved aside, never overwritten.</summary>
    public static string? LoadProblem { get; private set; }
    public string ProgramsFolder { get; set; } = "Programs";
    public string CompressedFolder { get; set; } = "Compressed";
    public string MusicFolder { get; set; } = "Music";
    public string VideoFolder { get; set; } = "Video";
    public string DocumentsFolder { get; set; } = "Documents";
    // 'When all downloads finish'. CompletionAction is the remembered choice; SessionCompletionAction is what is live in this run.
    [JsonConverter(typeof(JsonStringEnumConverter))] public CompletionActionKind CompletionAction { get; set; } = CompletionActionKind.None;
    public bool CompletionRemember { get; set; } = false;
    public bool CompletionForceClose { get; set; } = false;
    public bool CompletionIgnoreScheduled { get; set; } = false;
    public bool RunProgramEnabled { get; set; } = false;
    public string RunProgramPath { get; set; } = "";
    public string RunProgramArguments { get; set; } = "";
    // Torrents. Defaults keep seeding forever; "stop at ratio" is an explicit opt-in. Empty folder = general rules.
    public string TorrentDownloadDirectory { get; set; } = "";
    public int TorrentListenPort { get; set; } = 6881;
    public bool TorrentEnableDht { get; set; } = true;
    public bool TorrentEnablePex { get; set; } = true;
    public bool TorrentEnableLsd { get; set; } = true;
    public bool TorrentEnablePortForwarding { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))] public TorrentEncryptionMode TorrentEncryption { get; set; } = TorrentEncryptionMode.Enabled;
    public int TorrentMaxConnections { get; set; } = 150;
    public int TorrentMaxDownloadKiB { get; set; } = 0;
    public int TorrentMaxUploadKiB { get; set; } = 0;
    public bool TorrentStopSeedingAtRatio { get; set; } = false;
    public double TorrentSeedRatioLimit { get; set; } = 1.0;
    // Proxy. The password is never stored in clear text: ProxyPasswordProtected is a Windows DPAPI (current user) blob.
    [JsonConverter(typeof(JsonStringEnumConverter))] public ProxyKind ProxyType { get; set; } = ProxyKind.None;
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 8080;
    public string ProxyUsername { get; set; } = "";
    public string ProxyPasswordProtected { get; set; } = "";
    public bool ProxyResolveHostsThroughProxy { get; set; } = true;
    public bool ProxyForDownloads { get; set; } = true;
    public bool ProxyForPeers { get; set; } = true;
    public bool ProxyForTrackers { get; set; } = true;
    [JsonIgnore] public CompletionActionKind SessionCompletionAction { get; set; } = CompletionActionKind.None;
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public static AppSettings Load(string directory)
    {
        try
        {
            LoadProblem = null;
            var file = Path.Combine(directory, "settings.json");
            string text;
            try { text = FileLimits.ReadAllTextCapped(file, FileLimits.MaxSettingsBytes, "settings.json"); }
            catch (InvalidDataException ex)
            {
                // Too large to be real settings: keep it (moved next to the original) so nothing is lost, and start from defaults.
                LoadProblem = ex.Message;
                try { File.Move(file, Path.Combine(directory, "settings.oversized-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".json"), false); } catch (Exception move) when (move is IOException or UnauthorizedAccessException) { }
                return new();
            }
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new();
            var settings = new AppSettings { SettingsSchema = 0 }; // a file without the marker predates schema 2
            var properties = typeof(AppSettings).GetProperties().Where(p => p.Name != nameof(AdditionalSettings)).ToDictionary(p => p.Name);
            foreach (var field in document.RootElement.EnumerateObject())
            {
                if (!properties.TryGetValue(field.Name, out var property))
                {
                    (settings.AdditionalSettings ??= new())[field.Name] = field.Value.Clone();
                    continue;
                }
                try { property.SetValue(settings, field.Value.Deserialize(property.PropertyType, EnumNames)); }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException) { }
            }
            if (settings.SettingsSchema < 2) settings.EnableCategoryFolders = true; // one-time switch; explicit choices saved afterwards carry schema 2
            if (settings.SettingsSchema < 3) settings.PlayCompletionSound = true; // one-time: the old default was off
            if (settings.SettingsSchema < 4) settings.ShowProgressOnCompletion = true; // one-time: the option did not exist before
            if (settings.SettingsSchema < 5) settings.MarkDownloadedFiles = true; // one-time: the option did not exist before
            if (settings.SettingsSchema < 6) { settings.IdentifyAs = IdentityMode.Browser; settings.CustomUserAgent = settings.CustomUserAgent ?? ""; } // one-time: the option did not exist before
            if (settings.SettingsSchema < 7) settings.OpenWithoutAsking = new(); // one-time: the option did not exist before, every kind asks
            if (settings.SettingsSchema < 8) { settings.StreamConnections = 8; settings.UseFfmpegFromPath = false; } // one-time: the options did not exist before
            settings.OpenWithoutAsking = OpenFilePolicy.Normalize(settings.OpenWithoutAsking);
            settings.SettingsSchema = CurrentSchema;
            if (string.IsNullOrWhiteSpace(settings.DownloadDirectory) || !Path.IsPathFullyQualified(settings.DownloadDirectory)) settings.DownloadDirectory = new AppSettings().DownloadDirectory;
            else if (string.Equals(Path.GetFileName(settings.DownloadDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "New Download Manager", StringComparison.OrdinalIgnoreCase)) settings.DownloadDirectory = new AppSettings().DownloadDirectory;
            settings.Theme = ThemeCatalog.Normalize(settings.Theme);
            settings.AccentOverride = ThemeCatalog.NormalizeAccent(settings.AccentOverride);
            settings.Language = Localization.LanguageCatalog.NormalizeSetting(settings.Language);
            if (!Enum.IsDefined(settings.IdentifyAs)) settings.IdentifyAs = IdentityMode.Browser;
            settings.CustomUserAgent = (settings.CustomUserAgent ?? "").Trim();
            if (settings.CustomUserAgent.Length > 0 && !RequestIdentity.IsHeaderValue(settings.CustomUserAgent, RequestIdentity.MaxUserAgent)) settings.CustomUserAgent = "";
            if (settings.IdentifyAs == IdentityMode.Custom && settings.CustomUserAgent.Length == 0) settings.IdentifyAs = IdentityMode.Browser;
            settings.MaxSegments = Bound(settings.MaxSegments, 1, 32, 8);
            settings.MaxConcurrentDownloads = Bound(settings.MaxConcurrentDownloads, 1, 16, 3);
            settings.RetryCount = Bound(settings.RetryCount, 0, 8, 3);
            settings.StreamConnections = Bound(settings.StreamConnections, 1, 16, 8);
            settings.RequestTimeoutSeconds = Bound(settings.RequestTimeoutSeconds, 1, 300, 30);
            settings.TorrentListenPort = Bound(settings.TorrentListenPort, 1, 65535, 6881);
            settings.TorrentMaxConnections = Bound(settings.TorrentMaxConnections, 1, 10000, 150);
            settings.TorrentMaxDownloadKiB = Bound(settings.TorrentMaxDownloadKiB, 0, 2_000_000, 0);
            settings.TorrentMaxUploadKiB = Bound(settings.TorrentMaxUploadKiB, 0, 2_000_000, 0);
            if (!double.IsFinite(settings.TorrentSeedRatioLimit) || settings.TorrentSeedRatioLimit < 0.1 || settings.TorrentSeedRatioLimit > 1000) settings.TorrentSeedRatioLimit = 1.0;
            if (!Enum.IsDefined(settings.TorrentEncryption)) settings.TorrentEncryption = TorrentEncryptionMode.Enabled;
            if (!Enum.IsDefined(settings.ProxyType)) settings.ProxyType = ProxyKind.None;
            settings.ProxyHost = (settings.ProxyHost ?? "").Trim(); settings.ProxyUsername ??= ""; settings.ProxyPasswordProtected ??= "";
            settings.ProxyPort = Bound(settings.ProxyPort, 1, 65535, 8080);
            settings.TorrentDownloadDirectory = (settings.TorrentDownloadDirectory ?? "").Trim();
            if (settings.TorrentDownloadDirectory.Length > 0 && !Path.IsPathFullyQualified(settings.TorrentDownloadDirectory)) settings.TorrentDownloadDirectory = "";
            if (!Enum.IsDefined(settings.CompletionAction)) settings.CompletionAction = CompletionActionKind.None;
            settings.RunProgramPath ??= ""; settings.RunProgramArguments ??= ""; settings.SkippedUpdateVersion ??= "";
            settings.MutedExtensionVersions = (settings.MutedExtensionVersions ?? new()).Where(v => v is { Length: <= 24 }).Distinct().Take(50).ToList();
            settings.ExtensionNoticeTimes = (settings.ExtensionNoticeTimes ?? new()).Where(e => e.Key is { Length: <= 80 }).OrderByDescending(e => e.Value).Take(20).ToDictionary(e => e.Key, e => e.Value);
            settings.SessionCompletionAction = settings.CompletionRemember ? settings.CompletionAction : CompletionActionKind.None;
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return new(); }
    }
    private static readonly JsonSerializerOptions EnumNames = new() { Converters = { new JsonStringEnumConverter() } };
    private static int Bound(int value, int min, int max, int fallback) => value >= min && value <= max ? value : fallback;
    public AppSettings Copy() { var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!; copy.SessionCompletionAction = SessionCompletionAction; return copy; }
    // Update the shared instance only after durable persistence succeeds.
    public void CopyFrom(AppSettings source)
    {
        DownloadDirectory = source.DownloadDirectory; Theme = source.Theme; AccentOverride = source.AccentOverride; Language = source.Language;
        MaxSegments = source.MaxSegments; MaxConcurrentDownloads = source.MaxConcurrentDownloads;
        RetryCount = source.RetryCount; RequestTimeoutSeconds = source.RequestTimeoutSeconds; StreamConnections = source.StreamConnections; UseFfmpegFromPath = source.UseFfmpegFromPath;
        EnableDynamicSegmentation = source.EnableDynamicSegmentation; IdentifyAs = source.IdentifyAs; CustomUserAgent = source.CustomUserAgent;
        ShowCompletionDialog = source.ShowCompletionDialog; ShowProgressForCapturedDownloads = source.ShowProgressForCapturedDownloads; ShowProgressOnCompletion = source.ShowProgressOnCompletion; PlayCompletionSound = source.PlayCompletionSound; TrayShowActiveCount = source.TrayShowActiveCount; TrayNotifyOnComplete = source.TrayNotifyOnComplete;
        EnableCategoryFolders = source.EnableCategoryFolders; MarkDownloadedFiles = source.MarkDownloadedFiles; SettingsSchema = source.SettingsSchema;
        ProgramsFolder = source.ProgramsFolder; CompressedFolder = source.CompressedFolder;
        MusicFolder = source.MusicFolder; VideoFolder = source.VideoFolder; DocumentsFolder = source.DocumentsFolder;
        CompletionAction = source.CompletionAction; CompletionRemember = source.CompletionRemember;
        CompletionForceClose = source.CompletionForceClose; CompletionIgnoreScheduled = source.CompletionIgnoreScheduled;
        RunProgramEnabled = source.RunProgramEnabled; RunProgramPath = source.RunProgramPath; RunProgramArguments = source.RunProgramArguments;
        TorrentDownloadDirectory = source.TorrentDownloadDirectory; TorrentListenPort = source.TorrentListenPort;
        TorrentEnableDht = source.TorrentEnableDht; TorrentEnablePex = source.TorrentEnablePex; TorrentEnableLsd = source.TorrentEnableLsd;
        TorrentEnablePortForwarding = source.TorrentEnablePortForwarding; TorrentEncryption = source.TorrentEncryption;
        TorrentMaxConnections = source.TorrentMaxConnections; TorrentMaxDownloadKiB = source.TorrentMaxDownloadKiB; TorrentMaxUploadKiB = source.TorrentMaxUploadKiB;
        TorrentStopSeedingAtRatio = source.TorrentStopSeedingAtRatio; TorrentSeedRatioLimit = source.TorrentSeedRatioLimit;
        ProxyType = source.ProxyType; ProxyHost = source.ProxyHost; ProxyPort = source.ProxyPort; ProxyUsername = source.ProxyUsername;
        ProxyPasswordProtected = source.ProxyPasswordProtected; ProxyResolveHostsThroughProxy = source.ProxyResolveHostsThroughProxy;
        ProxyForDownloads = source.ProxyForDownloads; ProxyForPeers = source.ProxyForPeers; ProxyForTrackers = source.ProxyForTrackers;
        CheckForUpdates = source.CheckForUpdates; BridgeWatchdogEnabled = source.BridgeWatchdogEnabled; LastUpdateCheckUtc = source.LastUpdateCheckUtc; SkippedUpdateVersion = source.SkippedUpdateVersion;
        MutedExtensionVersions = source.MutedExtensionVersions.ToList();
        OpenWithoutAsking = OpenFilePolicy.Normalize(source.OpenWithoutAsking);
        ExtensionNoticeTimes = new Dictionary<string, DateTimeOffset>(source.ExtensionNoticeTimes);
        SessionCompletionAction = source.SessionCompletionAction;
        AdditionalSettings = source.AdditionalSettings;
    }
    /// <summary>Stores the password protected for the current Windows user; an empty value clears it. Never writes clear text.</summary>
    public void SetProxyPassword(string? password) => ProxyPasswordProtected = string.IsNullOrEmpty(password) ? "" : ProxySecret.Protect(password);
    public bool HasProxyPassword => ProxyPasswordProtected.Length > 0;
    /// <summary>The configured proxy (with the decrypted password, kept in memory only), or null when none is set.
    /// A password that cannot be decrypted (other user/machine) is treated as absent rather than an error.</summary>
    public ProxyOptions? BuildProxy()
    {
        if (ProxyType == ProxyKind.None || string.IsNullOrWhiteSpace(ProxyHost)) return null;
        var proxy = new ProxyOptions
        {
            Kind = ProxyType, Host = ProxyHost.Trim(), Port = ProxyPort,
            Username = ProxyUsername.Length > 0 ? ProxyUsername : null,
            Password = ProxyUsername.Length > 0 ? ProxySecret.TryUnprotect(ProxyPasswordProtected) : null,
            ResolveHostsThroughProxy = ProxyResolveHostsThroughProxy
        };
        return proxy.IsEnabled ? proxy : null;
    }
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        string temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public enum TorrentEncryptionMode { Disabled, Enabled, Required }

/// <summary>Windows DPAPI (CurrentUser) wrapper: secrets are readable only by the same Windows account on the same machine.</summary>
public static class ProxySecret
{
    private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("NeoDM.Proxy.v1");
    public static string Protect(string plain) =>
        Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(plain), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));
    public static string? TryUnprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return null;
        try { return System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException) { return null; }
    }
}
