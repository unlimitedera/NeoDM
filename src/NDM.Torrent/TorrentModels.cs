using System.Net;

namespace NDM.Torrent;

/// <summary>Per-file download priority. <see cref="Skip"/> means the file is not downloaded.</summary>
public enum FilePriority { Skip = 0, Low = 1, Normal = 2, High = 3 }

public enum TorrentEncryption
{
    /// <summary>Only plain-text peer connections.</summary>
    Disabled,
    /// <summary>Prefer encrypted connections but accept plain-text peers.</summary>
    Enabled,
    /// <summary>Encrypted connections only.</summary>
    Required
}

/// <summary>Engine-wide settings. Rates are bytes per second, 0 = unlimited.</summary>
public sealed class TorrentServiceOptions
{
    public int ListenPort { get; set; } = 6881;
    public IPAddress ListenAddress { get; set; } = IPAddress.Any;
    public bool EnableDht { get; set; } = true;
    public bool EnablePex { get; set; } = true;
    public bool EnableLsd { get; set; } = true;
    /// <summary>UPnP / NAT-PMP port mapping.</summary>
    public bool EnablePortForwarding { get; set; } = true;
    public TorrentEncryption Encryption { get; set; } = TorrentEncryption.Enabled;
    public int MaxDownloadRate { get; set; }
    public int MaxUploadRate { get; set; }
    public int MaxConnections { get; set; } = 150;
    /// <summary>Write the Mark of the Web (Zone.Identifier) on files that finish downloading.</summary>
    public bool MarkOfTheWeb { get; set; } = true;
    /// <summary>Route outgoing peer connections (TCP) through this proxy. Incoming connections, DHT, LSD and uTP are not proxied.</summary>
    public NDM.Core.ProxyOptions? PeerProxy { get; set; }
    /// <summary>Route HTTP/HTTPS tracker announces and web seeds through this proxy. UDP trackers cannot be proxied and are skipped while it is set.</summary>
    public NDM.Core.ProxyOptions? TrackerProxy { get; set; }

    public TorrentServiceOptions Clone() => (TorrentServiceOptions)MemberwiseClone();

    public void Validate()
    {
        if (ListenPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(ListenPort), "Port must be 1-65535.");
        if (ListenAddress is null) throw new ArgumentNullException(nameof(ListenAddress));
        if (MaxDownloadRate < 0) throw new ArgumentOutOfRangeException(nameof(MaxDownloadRate));
        if (MaxUploadRate < 0) throw new ArgumentOutOfRangeException(nameof(MaxUploadRate));
        if (MaxConnections is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(MaxConnections), "Connections must be 1-10000.");
        PeerProxy?.Validate(); TrackerProxy?.Validate();
    }
}

public sealed record TorrentFileJob(
    int Index,
    string Path,
    long Length,
    long DownloadedBytes,
    double Progress,
    FilePriority Priority,
    string FullPath)
{
    public bool Selected => Priority != FilePriority.Skip;
}

/// <summary>Immutable snapshot of one torrent. <c>State</c> uses NeoDM status names
/// (Queued, Downloading, Paused, Completed, Failed) plus Checking and Seeding.</summary>
public sealed record TorrentJob(
    string Id,
    string Name,
    string SavePath,
    long TotalBytes,
    long SelectedBytes,
    long DownloadedBytes,
    double Progress,
    long DownloadSpeed,
    long UploadSpeed,
    int Seeds,
    int Peers,
    int Connections,
    double Ratio,
    string State,
    string? Error,
    IReadOnlyList<TorrentFileJob> Files);

public sealed record TorrentMetadataFile(string Path, long Length);

/// <summary>Result of resolving a magnet link without adding it.</summary>
public sealed record TorrentMetadata(string Id, string Name, long TotalBytes, IReadOnlyList<TorrentMetadataFile> Files);

/// <summary>Input was rejected (bad magnet, oversize or unsafe torrent, bad path).</summary>
public sealed class TorrentRejectedException(string message) : ArgumentException(message);
