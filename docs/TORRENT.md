# NeoDM torrent engine (`src/NDM.Torrent`)

`TorrentService` is a validating wrapper around the MonoTorrent 3.0.2 engine
(NuGet, MIT license, taken from the package's `LICENSE`; transitive `Mono.Nat` 3.0.0 is MIT and
`ReusableTasks` 4.0.0 is MIT-style). No code from other torrent clients is used. `NDM.Core` is untouched and
stays NuGet-free; `NDM.Torrent` does not reference it.

## API sketch

```csharp
await using var t = await TorrentService.CreateAsync(stateDir, new TorrentServiceOptions { ListenPort = 6881 });
t.Changed += (_, _) => Refresh(t.List());            // thread-pool thread; handler exceptions are swallowed

TorrentJob a = await t.AddTorrentFileAsync(@"C:\x.torrent", saveDir);   // or (byte[] , saveDir)
TorrentJob b = await t.AddMagnetAsync("magnet:?xt=urn:btih:...", saveDir, TimeSpan.FromSeconds(90));
TorrentMetadata m = await t.FetchMetadataAsync(magnet);                 // preview files before adding

await t.SkipFileAsync(a.Id, 2);  await t.SetFilePriorityAsync(a.Id, 0, FilePriority.High);
await t.PauseAsync(a.Id);  await t.ResumeAsync(a.Id);  await t.RemoveAsync(a.Id, deleteFiles: true);
await t.SetRateLimitsAsync(down, up); await t.SetMaxConnectionsAsync(n); await t.SetListenPortAsync(p);
await t.SetDiscoveryAsync(dht, pex, lsd);  await t.SetEncryptionAsync(TorrentEncryption.Required);
await t.UpdateOptionsAsync(o => ...);        // anything in TorrentServiceOptions (also UPnP/NAT-PMP toggle)
```

`TorrentJob` snapshot (immutable): `Id` (v1 info-hash hex), `Name`, `SavePath`, `TotalBytes`, `SelectedBytes`,
`DownloadedBytes`, `Progress` (0 to 100 over selected files), `DownloadSpeed`, `UploadSpeed`, `Seeds`, `Peers`,
`Connections`, `Ratio` (session bytes sent / downloaded), `State`, `Error`, `Files[]`
(`Index, Path, Length, DownloadedBytes, Progress, Priority, FullPath`).

`State` values: `Queued`, `Checking`, `Downloading`, `Seeding`, `Paused`, `Completed`, `Failed`.
`Queued/Downloading/Paused/Completed/Failed` are the existing NeoDM statuses; `Seeding` (complete and running)
and `Checking` (hash verification) are torrent-specific. Paused-and-complete reports `Completed`.

## Behaviour

- Magnets: metadata is fetched first (DHT/trackers/LSD, `TimeoutException` after the timeout, default 90 s),
  validated, and only then is the torrent added. Nothing touches the disk before validation. Unresolved magnets
  are not persisted (the add call fails or times out). Tracker URLs from the magnet are kept in the stored
  `.torrent`.
- Magnet metadata (1.11.0): the search runs up to five attempts inside the total limit (10 s, 15 s, 20 s, 25 s, then what is left), with a 1 s, 2 s, 3 s, 4 s pause between them; each attempt announces again and looks up peers again.
  `MetadataProgress` and `CurrentMetadataStatus` report the attempt, the DHT node count and the open connections about once a second. A timeout is a `MetadataTimeoutException` (a `TimeoutException`) whose `Reason` is
  `DhtOff`, `DhtUnreachable` (DHT on, no node answered), `NoPeersAnswered` (nodes, no peer) or `PeersWithoutMetadata` (peers connected, no file list). The metadata fetched for a preview is kept for ten minutes, so adding the same link right after does not search again.
- DHT bootstrap (1.11.0): the engine knows one router. Measured on a network where that router never answered, the DHT stayed at 0 nodes, a link with only an info hash (no tracker) found no peers for 120 s and a link with a tracker or a `.torrent` file worked. `DhtBootstrap` now asks the routers in `TorrentServiceOptions.DhtRouters` (default `dht.libtorrent.org:25401`) for nodes with a plain `find_node` query whenever the DHT knows fewer than 4 nodes (at start, before a search, and again with growing gaps), keeps only public IPv4 nodes (at most 64) and hands them to the engine. The DHT node table is saved next to the engine's own cache every five minutes and at shutdown.
- Extra trackers (1.11.0): `UseExtraTrackers` and `ExtraTrackers` (at most 20 `udp`, `http` or `https` addresses, validated by `TrackerList`) are added to the magnet while it is searched. The stored `.torrent` keeps them unless the torrent is private. In the app: Options > Torrents, on by default with a small list of open trackers.
- Persistence: `stateDir/torrents.json` + `torrents/<hash>.torrent` + `cache/` (MonoTorrent fast-resume,
  written on pause/dispose). Every restored torrent comes back paused with its file priorities and last
  known progress; `Resume` verifies via fast-resume. A corrupt `torrents.json` is renamed `*.corrupt-<time>`
  and the service starts empty (`RestoreWarnings` reports it). Engine options are not persisted. The host
  owns settings and passes them to `CreateAsync`.
- Disposal: `DisposeAsync`/`Dispose` is idempotent; it stops all torrents (saving fast-resume), saves the
  list and releases the engine. Calls after disposal throw `ObjectDisposedException`.
- A duplicate add throws `InvalidOperationException`. Bad input throws `TorrentRejectedException` (an `ArgumentException`).

## Security validation

Measured on MonoTorrent 3.0.2: it rejects `..`, `a/../..` and rooted entries in multi-file `path` lists, but
it accepts a torrent *name* of `..`, `../../evil`, `C:\Windows\x`, and a single-file name like `..\evil.bin`,
as well as `:` streams and reserved names (`CON`). NeoDM therefore:

1. rejects non-`magnet:` URIs, control characters, missing/invalid `btih`/`btmh`, > 8192 chars;
2. rejects `.torrent` files > 8 MiB (`MaxTorrentFileBytes`; file length checked before reading);
3. validates the torrent name and every path segment (no `.`/`..`, separators, invalid Windows characters,
   trailing dot/space, device names, rooted paths);
4. after adding (still before any start/disk write) re-resolves every file's final path from the engine and
   requires it to be strictly inside the save directory (`TorrentValidator.EnsureInside`), otherwise the
   torrent is removed and rejected;
5. `Remove(deleteFiles)` deletes only paths re-verified inside the save dir, plus now-empty folders (never the
   save dir itself; foreign files in the torrent folder are kept).

Not covered: symlinks/junctions already present inside the save directory.

## Rate limits

MonoTorrent 3.0.2's own limiter did not work in our measurements: `EngineSettings.MaximumDownloadRate/UploadRate` is
not enforced (4 MiB at 200 KiB/s limit arrived in 0.7 s, loopback and LAN IP), and with per-torrent limits,
stopping a limited torrent mid-transfer starves every other limited torrent in the process (reproduced in a
bare-MonoTorrent program, not caused by NeoDM code). NeoDM therefore never passes rates to MonoTorrent; it
wraps every peer connection (outgoing and accepted) in a service-wide token bucket (`RateGate.cs`). The limit
is global across torrents; measured: 1 MiB at 256 KiB/s took 4.2 s. `EffectiveRateLimits` shows the active values.

## Tests

```
dotnet run --project tests/NDM.Torrent.Tests            # all (~25 s, no internet)
dotnet run --project tests/NDM.Torrent.Tests -- magnet  # name filter, '|' separated
```

Real in-process swarm on loopback: seeder service + downloader service, `.torrent` created by `TorrentCreator`,
direct peer via `AddPeerAsync`, DHT/LSD/UPnP off. Magnet test uses a tiny local HTTP tracker that returns the
seeder, so metadata is fetched over the real ut_metadata path. Not covered offline: DHT lookups, web seeds, uTP,
encryption negotiation with real-world clients, UPnP/NAT-PMP, IPv6.

## Package size impact

`dotnet publish -c Release` (framework-dependent) of `NDM.Torrent`: 13 dependency DLLs (MonoTorrent.* 11 files,
Mono.Nat, ReusableTasks) = 1,279,006 bytes (~1.22 MiB) + NDM.Torrent.dll 70 KB. Self-contained size
unchanged apart from this (no native binaries).

## Proxy support (verified against MonoTorrent 3.0.2)

MonoTorrent 3.0.2 has no proxy support of its own (no proxy/SOCKS types anywhere in the package). NeoDM adds it through the engine's factory hooks:

| Traffic | Proxied? | How |
|---|---|---|
| Outgoing peer connections (TCP) | Yes, SOCKS5 and HTTP CONNECT | `WithPeerConnectionCreator` returning `ProxiedPeerConnection`; read per connection, so Options changes apply to new connections at once |
| HTTP/HTTPS tracker announces, scrapes | Yes | `WithTrackerCreator("http"/"https")` builds the tracker with a proxied `HttpClient` (the stock creators ignore `WithHttpClientCreator`) |
| UDP trackers | No | UDP cannot be tunnelled; with a tracker proxy set they are replaced by an offline stand-in instead of leaking the real address |
| DHT, local peer discovery, UPnP/NAT-PMP | No | UDP/multicast; switched off while peer connections use the proxy |
| Incoming peer connections | No | The listener is unchanged; peers can still connect to you directly |

Tests (`tests/NDM.Torrent.Tests`): a local fixture proxy (SOCKS5 with auth, HTTP CONNECT, HTTP forward) proves that a full torrent download and an HTTP tracker announce really pass through it, that a bad proxy login does not fall back to a direct connection, and that changing the proxy applies live.
