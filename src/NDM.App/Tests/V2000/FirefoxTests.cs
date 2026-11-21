// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using NDM.App;

/// <summary>2.0.0 stage S5: the app recognises the Firefox extension in its per-browser version check.</summary>
internal static partial class V2000Tests
{
    private static void FirefoxRecognised()
    {
        Check(BrowserNames.IsValid("Firefox"), "Firefox is a valid browser name for the per-browser check");
        var settings = new AppSettings(); int saves = 0; var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        var book = new BrowserExtensionBook(settings, () => saves++, "2.0.0", () => now);
        var chrome = book.Seen("Google Chrome", "2.0.0");
        var older = book.Seen("Firefox", "1.16.2");
        Check(older.Record.Browser == "Firefox" && older.Check.Status == ExtensionStatus.Older && older.Check.Browser == "Firefox" && older.NoticeDue, "a Firefox extension older than the app is its own record and gets a notice");
        Check(chrome.Check.Status == ExtensionStatus.Match && !chrome.NoticeDue && book.Records.Count == 2, "Chrome stays separate: no notice, two records");
        Check(ExtensionVersionCheck.Describe(older.Check).Contains("Firefox"), "the notice names Firefox");
        var match = book.Seen("Firefox", "2.0.0");
        Check(match.Check.Status == ExtensionStatus.Match && !match.NoticeDue && match.Record.Browser == "Firefox" && book.Records.Count(r => r.Browser == "Firefox") == 1, "Firefox at 2.0.0 matches the app, same record updated");
        Check(BrowserExtensionRecord.Sanitize(new[] { match.Record }).Count == 1, "the Firefox record survives loading the settings");
    }
}
