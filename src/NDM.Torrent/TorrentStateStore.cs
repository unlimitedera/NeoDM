// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Text.Json;

namespace NDM.Torrent;

sealed class SavedTorrent
{
    public string Id { get; set; } = "";
    public string SavePath { get; set; } = "";
    /// <summary>File name inside the state "torrents" directory.</summary>
    public string TorrentFile { get; set; } = "";
    public int[] Priorities { get; set; } = [];
    public long[] FileDownloaded { get; set; } = [];
}

sealed class SavedState
{
    public int Version { get; set; } = 1;
    public List<SavedTorrent> Torrents { get; set; } = [];
}

/// <summary>Torrent list persisted as JSON plus a copy of each .torrent in the state directory.</summary>
sealed class TorrentStateStore(string stateDir)
{
    public string StateDir { get; } = stateDir;
    public string TorrentsDir => Path.Combine(StateDir, "torrents");
    public string CacheDir => Path.Combine(StateDir, "cache");
    string StatePath => Path.Combine(StateDir, "torrents.json");

    public void EnsureDirs() { Directory.CreateDirectory(TorrentsDir); Directory.CreateDirectory(CacheDir); }

    public string TorrentPath(string id) => Path.Combine(TorrentsDir, id + ".torrent");

    public void WriteTorrent(string id, byte[] data) => AtomicWrite(TorrentPath(id), data);

    public SavedState Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(StatePath)) return new SavedState();
        try
        {
            return JsonSerializer.Deserialize<SavedState>(File.ReadAllBytes(StatePath)) ?? new SavedState();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Fail closed: keep the unreadable file for inspection and start empty.
            var bad = StatePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try { File.Move(StatePath, bad, true); } catch { /* ignore */ }
            warning = "Torrent state was unreadable and was moved to " + bad + ": " + ex.Message;
            return new SavedState();
        }
    }

    public void Save(SavedState state) =>
        AtomicWrite(StatePath, JsonSerializer.SerializeToUtf8Bytes(state, new JsonSerializerOptions { WriteIndented = true }));

    static void AtomicWrite(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, true);
    }
}
