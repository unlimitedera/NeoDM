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
    public int MaxSegments { get; set; } = 8;
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int RetryCount { get; set; } = 3;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public bool EnableDynamicSegmentation { get; set; } = true;
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
    /// <summary>Settings schema marker. 2 = category folders default to on; 3 = completion sound defaults to on (a persisted false from the old default is switched once on load); 4 = 'open the download window when a download finishes' defaults to on. The migrations run once; later explicit choices carry the new marker.</summary>
    public int SettingsSchema { get; set; } = CurrentSchema;
    public const int CurrentSchema = 4;
    public bool EnableCategoryFolders { get; set; } = true;
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
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")));
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
            settings.SettingsSchema = CurrentSchema;
            if (string.IsNullOrWhiteSpace(settings.DownloadDirectory) || !Path.IsPathFullyQualified(settings.DownloadDirectory)) settings.DownloadDirectory = new AppSettings().DownloadDirectory;
            else if (string.Equals(Path.GetFileName(settings.DownloadDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "New Download Manager", StringComparison.OrdinalIgnoreCase)) settings.DownloadDirectory = new AppSettings().DownloadDirectory;
            settings.Theme = ThemeCatalog.Normalize(settings.Theme);
            settings.AccentOverride = ThemeCatalog.NormalizeAccent(settings.AccentOverride);
            settings.MaxSegments = Bound(settings.MaxSegments, 1, 32, 8);
            settings.MaxConcurrentDownloads = Bound(settings.MaxConcurrentDownloads, 1, 16, 3);
            settings.RetryCount = Bound(settings.RetryCount, 0, 8, 3);
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
        DownloadDirectory = source.DownloadDirectory; Theme = source.Theme; AccentOverride = source.AccentOverride;
        MaxSegments = source.MaxSegments; MaxConcurrentDownloads = source.MaxConcurrentDownloads;
        RetryCount = source.RetryCount; RequestTimeoutSeconds = source.RequestTimeoutSeconds;
        EnableDynamicSegmentation = source.EnableDynamicSegmentation;
        ShowCompletionDialog = source.ShowCompletionDialog; ShowProgressForCapturedDownloads = source.ShowProgressForCapturedDownloads; ShowProgressOnCompletion = source.ShowProgressOnCompletion; PlayCompletionSound = source.PlayCompletionSound; TrayShowActiveCount = source.TrayShowActiveCount; TrayNotifyOnComplete = source.TrayNotifyOnComplete;
        EnableCategoryFolders = source.EnableCategoryFolders; SettingsSchema = source.SettingsSchema;
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
