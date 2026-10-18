# NDM desktop design

## Design summary
The desktop app is an original workspace for the .NET 8 WPF download manager. All artwork and code are original. The main job is operating the queue, and live transfer inspection comes second. The queue is the workspace, not a dashboard.

The layout is a menu, a command toolbar, a status navigation rail and a sortable table. Frequent actions sit next to the real download data. There are no hero cards, invented statistics or demo jobs.

## Components
| Component | Purpose | Tokens / structure |
| --- | --- | --- |
| Native window frame | Standard minimize, maximize, close, resize and Windows snap | Existing OS chrome, native DPI manifest; no custom titlebar |
| Menu | Tasks / File / Downloads / View / Options / Help | Access keys; real add/import, scheduler, resume/stop, filters and help handlers. Options is a top-level item without a submenu and opens the Options window |
| Command toolbar | Add URL, Resume, Stop, Stop all, Remove, Remove completed, Options, Scheduler | Compact 30 DIP buttons, wraps if needed; destructive actions confirm and keep saved files |
| Status navigation | DOWNLOADS: All, Downloading, Paused, Completed, Failed, Queued, Scheduled (non-torrent items only). TORRENTS: All torrents, Downloading, Seeding, Completed | 166 DIP rail; filters match actual engine status, not guessed extension categories |
| Queue table | Filename, Size, Status, Progress, Remaining, Speed | Virtualized ListView/GridView; 30 DIP rows, sortable typed values, reorderable/resizable columns, horizontal and vertical scroll |
| Search | Match filename or original URL within current filter | Accessible name; Ctrl+F; no synthetic matches |
| Selection strip | Filename/path/error; compatible Pause/Resume, Speed, Open file, Open folder | Small inline strip; legacy named controls retained |
| Progress window | Per-download live inspection without blocking the queue | Modeless, one view per job; total/downloaded, core speed/ETA, resume support and connection/segment snapshots |
| Completion window | Actual disk size/path, trusted open, chooser, folder, copy path, close | Modeless serialized notifications; missing/removed-job guards; preference checkbox |
| Options and forms | Existing add, schedule/speed and import workflows plus tabbed Options | Reuse existing dialogs and engine APIs; no duplicate download logic |
| Empty state | Real absence of jobs or filtered matches | Simple contextual instructions, no sample downloads |

## Tokens and accessibility
- Reuse `Themes/Light.xaml`, `Themes/Dark.xaml` and `Themes/Controls.xaml`: Canvas, Sidebar, Surface, SurfaceHover, Ink, Muted, Border, Accent, AccentSoft and semantic brushes. Dynamic resources update already-open windows.
- Segoe UI; main table and workspace 13 DIP, secondary labels 11 to 12 DIP, dialog headings 18 to 20 DIP. The UI uses native text and plain borders, with no gradients, decorative cards, blur or automatic animation.
- Main minimum size is 900 × 480 DIP. All six default columns, including Remaining and Speed, fit at this width; user-resized columns can scroll. Status navigation scrolls at short heights.
- Native window buttons remain unobstructed. Tab traversal, list arrows, visible focus outlines, menu access keys and named accessible search/queue controls are retained.
- Ctrl+N adds, Ctrl+F searches, Ctrl+V pastes a URL outside text inputs, F5 refreshes, Enter inspects the selected incomplete download or guarded-opens a completed one, Delete confirms removal. Escape closes progress/completion views without stopping transfers.
- `DownloadList` is now a `ListView`, which derives from `ListBox`; `Rows`, `DownloadRow`, two-argument `MainWindow` construction and named controls/handlers remain compatible with existing tests.

## Snapshot, filtering and sorting
- The main window polls detached `DownloadManager.Jobs` snapshots every 500 ms and captures state-change snapshots when the engine notifies. The window drains captured transitions before the latest polls, so a new zero-byte or sub-tick completion is not mistaken for restored history.
- Existing row objects remain stable. `DownloadRow.Update` returns true and notifies bindings only when actual observable values change, including ETA and segment telemetry. Filter-relevant or active sort changes refresh the collection view; selection is restored by job ID.
- Status filters match exactly the displayed engine status. Downloading does not silently include Scheduled/Queued; these have their own entries. Search composes with the selected status filter.
- Column headers sort filename/status text, numeric bytes/progress/speed and nullable `TimeSpan` ETA, with a direction indicator. No date column is fabricated because the core does not expose an added date.
- Unknown size, speed and ETA show Unknown or a dash placeholder. Speed and ETA come directly from the core contract, not from a separate UI estimator. Finished rows show Done; paused/waiting rows do not retain a misleading ETA.
- Visible URLs omit query, credentials and fragments; search matches the original URL locally. Tooltips use the redacted display URL.

## Modeless progress and completion lifecycle
- Double-clicking an actual incomplete list item or pressing Enter opens its modeless progress window; double-clicking headers/blank space does not accidentally open the previous selection. Completed-file opening requires existence and explicit trust confirmation.
- The progress window polls fresh snapshots and renders `ActiveConnectionCount`, `ConnectionLimit`, nullable `ResumeSupported` and each actual `DownloadSegmentSnapshot` byte range, bytes, progress and activity. It uses public pause/resume and speed-options APIs. Hide, Escape and titlebar close dispose only the view/timer, never cancel or remove the download.
- `CompletionController` seeds restored history, recognizes each transition from non-complete to Completed once per ID and queues one modeless completion window at a time. No historical-completed popups, repeated-tick duplicates or simultaneous popup flood. Removed pending jobs are skipped against a fresh lookup; an already-visible removed or missing-file job disables opening.
- Completion size is read from the saved file when available; missing-file metadata is labelled recorded. Open is confirmed for all file types, including executable/script/shortcut/document risks. Open with passes a full path to the Windows `SHOpenWithDialog` API, not a concatenated shell/rundll32 command. Opening a folder uses its full existing directory path.
- Don't show again maps to `AppSettings.ShowCompletionDialog` and persists through the injected save hook. Production defaults to `settings.Save(App.StateDirectory)`; tests inject isolated state. A failed save restores the setting and checkbox. The preference can be restored in Options > Downloads.
- `PlayCompletionSound` is independent of dialog visibility and defaults off; historical jobs never sound. Sound is requested only for a newly observed completion.
- Event handlers unsubscribe and timers stop on close. Completion and progress dialogs never own or dispose the shared manager.

## Native bridge contract
- App singleton: `Local\NDM.App` mutex; a second process exits with no second manager or pipe server.
- Pipe: `NDM.Downloads`, byte mode, asynchronous, `PipeOptions.CurrentUserOnly`; one connected client and one request/reply per connection.
- Input: UTF-8 newline-terminated JSON object, maximum 65,536 bytes before LF. Optional CR from CRLF is legal JSON whitespace. Extra bytes after the first line are ignored and the connection is closed after the reply.
- Required string: `url`, complete HTTP(S) URL. Optional strings or null: `fileName`, `referer`, `cookie`.
- Success: `{"ok":true,"id":"..."}\n`, only after awaited `AddAsync` completes its durable queue save.
- Failure: `{"ok":false,"error":"..."}\n`. Failure messages are generic on purpose. The app does not echo authenticated URLs, cookies or engine exception text to the native host.
- Each client has a 30-second read/write timeout; incomplete or disconnected clients cannot block the server indefinitely. Native host callers should retry transient connection failures while startup is in progress.
- On shutdown, bridge cancellation is nonblocking to avoid deadlocking a pending UI-dispatched acceptance.
- Browser installation/registration belongs to the separately supplied native host and extension. Settings explains those steps without claiming that an extension is already installed.

## Advanced queue workflows
- Add accepts a future local `yyyy-MM-dd HH:mm` value when Schedule for later is checked. Invalid, past, nonexistent and ambiguous daylight-saving times are rejected. The time is converted to an offset-bearing instant before calling the core; no background service or wake-from-sleep feature is implied.
- Selected-job Schedule uses `Schedule`; it stops an active attempt and moves the job to its due time. Start now overrides a future schedule and starts/resumes it. Completed jobs cannot be edited.
- Speed inputs accept positive whole KiB/s, convert using 1024 bytes/KiB with overflow validation, and use `SetSpeedLimit`. Blank means unlimited. Limits are per-job aggregate limits across segments, not a global bandwidth budget. The core snapshots do not expose current limits, so the selected-job dialog explicitly asks for a new limit rather than showing a guessed current value.
- Add with a cap first durably adds a temporarily future-scheduled job, sets its cap, then applies the actual requested schedule or starts now. This prevents the engine starting uncapped before the UI can configure it. These public API calls are not one transaction: an options failure attempts to leave the saved job paused, and the error tells the user to inspect the queue before retrying.
- Pause All includes Downloading, Queued, Connecting and Scheduled jobs across the whole queue, including jobs hidden by search/filter. Resume All resumes Paused jobs only, keeps future schedules and does not retry Failed jobs. The selected Resume action remains available for an individual failed job. Each bulk action rechecks fresh status and reports changed/skipped/failed counts.
- Import accepts newline-separated HTTP(S) URLs and ignores blank lines. Each nonblank line gets a numbered added/invalid/duplicate/not-saved result. Duplicate comparison uses normalized absolute URI strings with ordinal comparison against both this import and the existing queue; it is not a content-identity or arbitrary URL-equivalence detector. Duplicate lines are explicitly skipped, never silently added. Successful lines stay saved even if other lines fail; retrying the same input skips earlier successes. Results intentionally omit the original URLs so credential-bearing links are not echoed.
- Import starts valid jobs immediately in the current destination, without per-line names, schedules, cookies or limits. Added means durably queued, not successfully downloaded. The import button, input and close action are disabled during execution; the summary stays visible until the user closes the dialog. Import yields to the Dispatcher between saves.
- The compact command toolbar wraps when needed. Add and selected-job forms scroll while their action footer remains visible. Controls reuse the existing light/dark palette, keyboard focus, access keys, accessible names and live validation/result announcements.

## Verification
No package-based test framework or external website is required. All new fixtures use isolated `%LOCALAPPDATA%\hermes\cache\scratch\NDM-Fidelity-*` state and a real loopback TCP HTTP server, not the user queue. A plain STA test `Application` loads the real WPF resource dictionaries without production startup, singleton, pipe or user settings. Tests never launch downloaded files.

```bash
dotnet build F:/NDM/src/NDM.App/NDM.App.csproj -c Release --nologo
dotnet run --project F:/NDM/src/NDM.App/Tests/NDM.App.ProtocolTests.csproj -c Release
dotnet run --project F:/NDM/src/NDM.App/Tests/Ui/NDM.App.UiTests.csproj -c Release
dotnet run --project F:/NDM/src/NDM.App/Tests/Advanced/NDM.App.AdvancedTests.csproj -c Release
dotnet run --project F:/NDM/src/NDM.App/Tests/Options/NDM.App.OptionsTests.csproj -c Release
dotnet run --project F:/NDM/src/NDM.App/Tests/OptionsDialog/NDM.App.Tests.OptionsDialog.csproj -c Release
dotnet run --project F:/NDM/src/NDM.App/Tests/Fidelity/NDM.App.FidelityTests.csproj -c Release
```

The Fidelity executable passes 38 checks: menu order/wiring, legacy controls, sortable columns, live HTTP speed/ETA and segment bytes, pause/resume, unchanged snapshot notification discipline, real filters/search, Enter/F5, close-without-cancel, byte-exact outputs, exactly-once serialized transitions, independent completion signal, modeless production notification queue, removed pending jobs, sub-tick zero-byte completion, actual restored historical queue suppression, missing/removed safe-open guards, isolated preference persistence/rollback, minimum-column fit and dark token updates.

The original UI executable passed its checks; the original Advanced executable passed 49 checks, including the retained 36 DIP Resume All compatibility target. Protocol checks also passed after the parent project excluded nested test source globs. WPF-rendered main light/dark minimum-size, progress and completion captures were inspected. Captures use an explicit VisualBrush viewport so offscreen descendant bounds cannot shrink the rendered resized window.

No running user application was killed or closed, no production queue/settings were modified, and no unknown downloaded file was launched. Native Open with is wired and its API signature/flags checked against Microsoft documentation; an actual external application launch/chooser interaction is intentionally not part of unattended tests. Safe publication, browser integration and full-root release acceptance remain owned by the parent integration workstream. This is not a claim of full feature parity with other download managers.

## Tray, captured downloads, categories and context menu
- Tray: `MainWindow(manager, settings, save, enableTray)` owns a `Hardcodet.NotifyIcon.Wpf` icon. Closing or minimizing hides the window and keeps the process (and transfers) running; tray double-click/Show restores; tray Exit (or File > Exit) sets an exit request and really closes. Tests pass `enableTray: false`.
- Browser capture: `NativeBridge(Action<BrowserDownloadRequest>, pipeName)` hands each validated request to the app, which shows `DownloadOptionsDialog` (file name, save-to, category, speed limit, Start / Add to Queue / Schedule / Cancel). The pipe client receives exactly one ack: `{ok:true,id}` after the job is persisted, or `{ok:false,error}` on Cancel, timeout (5 min) or failure.
- Categories: `EnableCategoryFolders` is on by default (settings schema 2 migrates an old persisted `false` once; later choices are kept). With it, `CategoryHelper` maps extensions to Programs, Compressed, Music, Video and Documents subfolders of the download directory (relative names, or absolute paths, configurable in Options > Downloads); everything else stays in the base folder.
- Queue context menu: Open file, Open folder, Copy path, Delete file, Remove from list. Right-click selects the row under the pointer first. Items enable only when valid (Open/Delete need an existing completed file). Delete file confirms, deletes from disk, then removes the queue entry; a failed delete leaves the entry.
- Isolated runs: `NDM_STATE_DIR` relocates settings/queue and gives that instance its own singleton mutex and pipe name (`NDM.Downloads.<hash>`), so an isolated app can run beside a live one without touching its queue.
- Test harness rule: `App`'s constructor queues `OnStartup` on the dispatcher. Any test that pumps the dispatcher with an `App` instance must override `OnStartup` (the UI test uses a `TestApp` subclass); otherwise the real startup takes the machine-wide singleton mutex and either shuts the test application down (another NeoDM is running, which then breaks later pack-URI loads with "The URI prefix is not recognized") or starts the production manager against the user's real queue.

## Browser connection watchdog
`BridgeWatchdog` (src/NDM.App) checks the app's pipe listener, the native messaging registration of Chrome, Edge, Brave and Chromium (current user key first, then the machine key, as the browser does) and a real start of `NDM.NativeHost.exe` with one framed ping. The result is a `WatchdogReport` shown in Help > Browser connection. A stale per-user key whose manifest or helper is gone is deleted, and a missing or invalid registration is replaced by a manifest in `%LOCALAPPDATA%\NDM\host` registered for the current user, so no administrator rights are needed. Each repair is limited to once per 10 minutes per problem unless the user presses Repair, and isolated runs (`NDM_STATE_DIR`) check but never repair.

Resource budget: the first check runs 10 seconds after start, then every 5 minutes for the first 3 healthy results, every 15 minutes for the next 3 and every 60 minutes after that; a problem, a repair, a capture failure or Check now returns to 5 minutes. It uses one `System.Threading.Timer` and one below-normal background thread per run, skips the run during a full-screen app, a presentation, a locked session or battery saver, skips the host start when a browser already runs the helper, starts at most one helper process (below-normal priority, 3 second limit, only that child is killed), reads at most a few keys and files (manifest reads capped at 64 KB, no directory scans) and keeps only the last report and 20 log lines.
