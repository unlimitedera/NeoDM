using System.Runtime.InteropServices;
using NDM.Core;

namespace NDM.App;

/// <summary>1.9.0: offers to open the link grabber when a copied text holds two or more web links. OFF by default (Options > Downloads). It looks at the clipboard only when Windows says the clipboard changed (a counter, no content read), ignores whatever was on the clipboard when it was switched on, ignores texts NeoDM copied itself, reads at most <see cref="MaxTextLength"/> characters, offers the same text only once a minute and at most once every few seconds. Nothing is requested from the network.</summary>
public sealed class ClipboardLinkWatcher
{
    public const int MaxTextLength = 1_000_000, MinLinks = 2;
    public static readonly TimeSpan SameTextPause = TimeSpan.FromSeconds(60), AnyTextPause = TimeSpan.FromSeconds(4);
    private readonly Func<bool> _enabled; private readonly Func<uint> _sequence; private readonly Func<string?> _read; private readonly Action<string> _found; private readonly Func<DateTime> _now;
    private uint _last; private bool _primed; private string? _ignored; private string? _lastOffered; private DateTime _lastOfferedAt = DateTime.MinValue, _lastAnyAt = DateTime.MinValue;
    public int ReadCount { get; private set; }

    public ClipboardLinkWatcher(Func<bool> enabled, Func<uint> sequence, Func<string?> readText, Action<string> found, Func<DateTime>? now = null)
    { _enabled = enabled; _sequence = sequence; _read = readText; _found = found; _now = now ?? (() => DateTime.UtcNow); }

    /// <summary>Text NeoDM put on the clipboard itself (copy URL, copy path): never offered.</summary>
    public void IgnoreOwn(string? text) { _ignored = text; }

    public void Poll()
    {
        uint sequence = _sequence();
        if (!_enabled()) { _primed = false; _last = sequence; return; }
        if (!_primed) { _primed = true; _last = sequence; return; } // whatever is on the clipboard now was there before the switch was turned on
        if (sequence == _last) return;
        _last = sequence;
        var now = _now();
        if (now - _lastAnyAt < AnyTextPause) return;
        string? text;
        try { text = _read(); } catch (Exception ex) when (ex is ExternalException or InvalidOperationException or System.Threading.ThreadStateException) { return; }
        ReadCount++;
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength || text == _ignored) return;
        if (text == _lastOffered && now - _lastOfferedAt < SameTextPause) return;
        if (LinkParser.Parse(text, maxLinks: 50).Links.Count < MinLinks) return;
        _lastOffered = text; _lastOfferedAt = now; _lastAnyAt = now;
        _found(text);
    }

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    public static uint SystemSequence() => GetClipboardSequenceNumber();
}

/// <summary>What a drop onto the main window means: .torrent files are added as torrents; text, links and list files (.txt and similar) open the link grabber with the text.</summary>
public sealed record DropPlan(IReadOnlyList<string> Torrents, string? Text, int Skipped)
{
    public bool HasContent => Torrents.Count > 0 || !string.IsNullOrWhiteSpace(Text);
    public const int MaxFiles = 50;

    public static DropPlan Read(System.Windows.IDataObject? data)
    {
        var torrents = new List<string>(); var text = new System.Text.StringBuilder(); int skipped = 0;
        if (data is null) return new(torrents, null, 0);
        try
        {
            if (data.GetDataPresent(System.Windows.DataFormats.FileDrop) && data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
            {
                foreach (var file in files.Take(MaxFiles))
                {
                    var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".torrent") torrents.Add(file);
                    else if (LinkParser.ListExtensions.Contains(ext))
                    {
                        try { if (text.Length < LinkParser.MaxInputCharacters) text.AppendLine(LinkParser.ReadListFile(file)); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { skipped++; }
                    }
                    else skipped++;
                }
            }
            else if (data.GetDataPresent(System.Windows.DataFormats.UnicodeText) && data.GetData(System.Windows.DataFormats.UnicodeText) is string unicode) text.Append(unicode.Length > LinkParser.MaxInputCharacters ? unicode[..LinkParser.MaxInputCharacters] : unicode);
            else if (data.GetDataPresent(System.Windows.DataFormats.Text) && data.GetData(System.Windows.DataFormats.Text) is string plain) text.Append(plain.Length > LinkParser.MaxInputCharacters ? plain[..LinkParser.MaxInputCharacters] : plain);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException) { }
        return new(torrents, text.Length == 0 ? null : text.ToString(), skipped);
    }
}
