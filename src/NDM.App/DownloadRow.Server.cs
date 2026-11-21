// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
namespace NDM.App;

public sealed partial class DownloadRow
{
    private string? _serverHost, _serverAddress, _serverProxy; private int? _serverMs; private bool _serverViaProxy;
    private bool ServerDiffers(NDM.Core.DownloadJob job) => !Sources.SequenceEqual(job.Sources) || MirrorChoice != job.MirrorChoice || ChecksumKnown != job.ChecksumKnown || ChecksumVerified != job.ChecksumVerified || MirrorUnverified != job.MirrorUnverified || _serverHost != job.ServerHost || _serverAddress != job.ServerAddress || _serverMs != job.ServerLatencyMs || _serverViaProxy != job.ServerViaProxy || _serverProxy != job.ServerProxy;
    private void ApplyServer(NDM.Core.DownloadJob job) { Sources = job.Sources; MirrorChoice = job.MirrorChoice; ChecksumKnown = job.ChecksumKnown; ChecksumVerified = job.ChecksumVerified; MirrorUnverified = job.MirrorUnverified; _serverHost = job.ServerHost; _serverAddress = job.ServerAddress; _serverMs = job.ServerLatencyMs; _serverViaProxy = job.ServerViaProxy; _serverProxy = job.ServerProxy; }
    /// <summary>The sources of the file, the original address first (empty without mirrors), the user's choice (null = Automatic) and what is known about the checksum.</summary>
    public IReadOnlyList<NDM.Core.MirrorInfo> Sources { get; private set; } = Array.Empty<NDM.Core.MirrorInfo>();
    public string? MirrorChoice { get; private set; }
    public bool ChecksumKnown { get; private set; }
    public bool ChecksumVerified { get; private set; }
    public bool MirrorUnverified { get; private set; }
    public bool ServerViaProxy => _serverViaProxy;
    /// <summary>The Server row (flag, country, answer time); null for torrents and when the country is unknown.</summary>
    public ServerView? Server => IsTorrent ? null : ServerInfo.Build(_serverHost, _serverAddress, _serverMs, _serverViaProxy, _serverProxy);
    /// <summary>Text of the optional Server column of the list.</summary>
    public string ServerText => Server?.Text ?? "";
}
