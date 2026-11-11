using MonoTorrent.Client;

namespace NDM.Torrent;

public enum MetadataPhase { Starting, Searching, Retrying }

/// <summary>What the metadata search for a magnet link is doing right now (1.11.0). Shown as "Looking for peers: DHT nodes N, connections M".</summary>
public sealed record MetadataStatus(int Attempt, int Attempts, TimeSpan Elapsed, TimeSpan Limit, int DhtNodes, int Connections, bool DhtEnabled, int Trackers, MetadataPhase Phase,
    int UdpTrackersTested = 0, int UdpTrackersAnswered = 0, int ListenPort = 0, bool PortMapping = false, string? ProxyEndpoint = null);

/// <summary>Why no metadata arrived, as far as the numbers tell. A guess for the message, never a certainty.</summary>
public enum MetadataFailure { DhtOff, DhtUnreachable, NoPeersAnswered, PeersWithoutMetadata }

public static class MetadataDiagnosis
{
    /// <summary>DHT off -> say so; no DHT node at all -> UDP is probably blocked; nodes but no peer connection -> nobody shares it (or only trackers could help);
    /// peers connected but no metadata -> they did not send it (a rare or stalled torrent).</summary>
    public static MetadataFailure Classify(MetadataStatus status) =>
        status.Connections > 0 ? MetadataFailure.PeersWithoutMetadata
        : !status.DhtEnabled ? MetadataFailure.DhtOff
        : status.DhtNodes <= 0 ? MetadataFailure.DhtUnreachable
        : MetadataFailure.NoPeersAnswered;
}

/// <summary>The metadata did not arrive in time. Is a <see cref="TimeoutException"/>, so older handlers keep working; carries the last numbers for the message.</summary>
public sealed class MetadataTimeoutException(string message, MetadataStatus status) : TimeoutException(message)
{
    public MetadataStatus Status { get; } = status;
    public MetadataFailure Reason => MetadataDiagnosis.Classify(Status);
}

/// <summary>The DHT as NeoDM needs it: how many nodes it knows, adding nodes found elsewhere, and saving the node table. A seam for tests.</summary>
public interface IDhtControl
{
    int NodeCount { get; }
    /// <summary>Hands compact nodes (26 bytes each) to the DHT. False when the engine cannot take them.</summary>
    bool TryAdd(IReadOnlyList<ReadOnlyMemory<byte>> nodes);
    /// <summary>The node table as the engine stores it (a bencoded list of compact nodes), or null when it cannot be read.</summary>
    Task<byte[]?> TrySaveAsync();
}

/// <summary>The real engine. The engine keeps its DHT behind an internal property; the interface it returns is public, so only the lookup uses reflection
/// and a failure there just turns the extra bootstrap off (the engine's own bootstrap still runs).</summary>
sealed class EngineDhtControl(ClientEngine engine) : IDhtControl
{
    MonoTorrent.Dht.IDhtEngine? Inner()
    {
        try { return typeof(ClientEngine).GetProperty("DhtEngine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(engine) as MonoTorrent.Dht.IDhtEngine; }
        catch (Exception) { return null; }
    }
    public int NodeCount { get { try { return engine.Dht.NodeCount; } catch (Exception) { return 0; } } }
    public bool TryAdd(IReadOnlyList<ReadOnlyMemory<byte>> nodes)
    {
        try { var dht = Inner(); if (dht is null) return false; dht.Add(nodes); return true; }
        catch (Exception) { return false; }
    }
    public async Task<byte[]?> TrySaveAsync()
    {
        try { var dht = Inner(); if (dht is null) return null; return (await dht.SaveNodesAsync()).ToArray(); }
        catch (Exception) { return null; }
    }
}
