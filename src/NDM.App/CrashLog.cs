// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.IO;
using System.Text;
using System.Text.Json;

namespace NDM.App;

/// <summary>2.0.0: when NeoDM closes because of an unhandled error it writes a short, bounded note to its state folder (crash\). On the next start NeoDM offers once to report it.
/// The note is cleaned of links, folders and the user name when it is written. Nothing is sent anywhere.</summary>
public static class CrashLog
{
    public const int MaxFiles = 8, MaxMessage = 1500, MaxStack = 8000, MaxDepth = 4, LoopMinutes = 10;
    public sealed record Pending(string File, DateTime Time, string Type);
    private static string Folder(string state) => Path.Combine(state, "crash");
    private static string PendingPath(string state) => Path.Combine(Folder(state), "pending.json");
    private static string OfferedPath(string state) => Path.Combine(Folder(state), "offered.txt");
    private static string? _lastKey; private static DateTime _lastAt;
    /// <summary>Test seam for the clock.</summary>
    public static Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    public static IEnumerable<string> Files(string state)
    {
        try { return Directory.Exists(Folder(state)) ? Directory.EnumerateFiles(Folder(state), "crash-*.txt").OrderByDescending(f => f, StringComparer.Ordinal).ToList() : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>Hooks the process-wide handlers (called once at start). Dispatcher errors are recorded and left unhandled, so NeoDM closes as it always did.</summary>
    public static void Install(string state, System.Windows.Application app)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record(state, e.ExceptionObject as Exception, e.IsTerminating ? "unhandled" : "unhandled (not closing)");
        app.DispatcherUnhandledException += (_, e) => Record(state, e.Exception, "window thread");
    }

    /// <summary>Writes the note and the 'offer on next start' mark. Never throws. Returns the note's path (null when nothing could be written).</summary>
    public static string? Record(string state, Exception? error, string source)
    {
        try
        {
            var now = Clock();
            var key = (error?.GetType().FullName ?? "?") + "|" + error?.Message;
            if (key == _lastKey && (now - _lastAt).TotalSeconds < 5) return null; // the same crash reported by two handlers
            _lastKey = key; _lastAt = now;
            Directory.CreateDirectory(Folder(state));
            var sb = new StringBuilder();
            sb.AppendLine("NeoDM " + AboutInfo.Version + " closed unexpectedly (" + source + ")");
            sb.AppendLine("Time: " + now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Windows: " + DiagnosticsReport.WindowsText());
            var depth = 0;
            for (var e = error; e is not null && depth < MaxDepth; e = e.InnerException, depth++)
            {
                sb.AppendLine(); sb.AppendLine((depth == 0 ? "" : "Inner: ") + e.GetType().FullName);
                var msg = e.Message ?? ""; sb.AppendLine(msg.Length > MaxMessage ? msg[..MaxMessage] + "…" : msg);
                var stack = e.StackTrace ?? ""; sb.AppendLine(stack.Length > MaxStack ? stack[..MaxStack] + "…" : stack);
            }
            if (error is null) sb.AppendLine("\r\n(no error object)");
            var text = Scrubber.Scrub(sb.ToString());
            if (text.Length > 16 * 1024) text = text[..(16 * 1024)];
            var file = Path.Combine(Folder(state), "crash-" + now.ToString("yyyyMMdd-HHmmss-fff") + ".txt");
            File.WriteAllText(file, text, new UTF8Encoding(false));
            foreach (var old in Files(state).Skip(MaxFiles)) { try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            // A crash loop (it closes again soon after the person was asked) writes notes but does not ask again.
            var offered = LastOffer(state);
            if (offered is null || (now - offered.Value).TotalMinutes >= LoopMinutes)
                File.WriteAllText(PendingPath(state), JsonSerializer.Serialize(new Pending(Path.GetFileName(file), now, error?.GetType().Name ?? "?")));
            return file;
        }
        catch (Exception) { return null; }
    }

    private static DateTime? LastOffer(string state)
    {
        try { return File.Exists(OfferedPath(state)) && DateTime.TryParse(File.ReadAllText(OfferedPath(state)), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The unexpected close that was not offered yet, or null. Taking it removes the mark and notes the time, so it is offered once.</summary>
    public static Pending? TakePending(string state)
    {
        try
        {
            if (!File.Exists(PendingPath(state))) return null;
            var text = File.ReadAllText(PendingPath(state));
            File.Delete(PendingPath(state));
            var pending = text.Length < 4096 ? JsonSerializer.Deserialize<Pending>(text) : null;
            if (pending is null) return null;
            File.WriteAllText(OfferedPath(state), Clock().ToString("o"));
            return pending;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { try { File.Delete(PendingPath(state)); } catch (Exception) { } return null; }
    }
}

/// <summary>Start-up check: offers to report a problem once after an unexpected close.</summary>
public static class CrashOffer
{
    public static bool Run(System.Windows.Window? owner, AppSettings settings, string state)
    {
        if (CrashLog.TakePending(state) is null) return false;
        var choice = ChoiceWindow.Ask(owner, "NeoDM", Localization.Loc.T("crash.message"), Localization.Loc.T("crash.report"), Localization.Loc.T("crash.later"));
        if (choice == 0) ReportWindow.ShowFor(owner, settings, state, afterCrash: true);
        return true;
    }
}
