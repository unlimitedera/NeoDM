using System.Windows;
using System.Windows.Threading;

namespace NDM.App;

/// <summary>Test harness helper (never used by the app): turns on <see cref="Localization.Dialogs.TestMode"/> and watches for hangs.
/// A modal window that stays open (message, choice, busy or queue prompt) is closed after a few seconds and recorded; a test that runs longer than <see cref="Limit"/> gets every other window closed and fails with the titles in the message.
/// <see cref="Run"/> closes all windows after each test.</summary>
public static class TestGuard
{
    public static TimeSpan Limit { get; set; } = TimeSpan.FromSeconds(60);
    public static List<string> Closed { get; } = new();
    private static DispatcherTimer? timer; private static DateTime started = DateTime.UtcNow; private static string current = "";
    private static readonly Dictionary<Window, DateTime> seen = new();

    public static void Enable()
    {
        Localization.Dialogs.TestMode = true;
        if (timer is not null) return;
        timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Watch(); timer.Start();
    }
    private static bool IsPrompt(Window w) => w is MessageWindow or ChoiceWindow or BusyQueuePrompt or QueueChooserWindow or OpenFileConfirmWindow;
    private static void Watch()
    {
        var app = Application.Current; if (app is null) return;
        bool overtime = DateTime.UtcNow - started > Limit;
        foreach (var w in app.Windows.OfType<Window>().Where(x => x.IsVisible).ToArray())
        {
            if (!seen.TryGetValue(w, out var first)) { seen[w] = first = DateTime.UtcNow; }
            bool stuckPrompt = IsPrompt(w) && DateTime.UtcNow - first > TimeSpan.FromSeconds(8);
            if (stuckPrompt || overtime && w != app.MainWindow && IsPrompt(w))
            {
                Closed.Add($"{current}: {w.GetType().Name} '{w.Title}'");
                try { w.DialogResult = false; } catch (InvalidOperationException) { w.Close(); }
            }
        }
        foreach (var gone in seen.Keys.Where(k => !k.IsVisible).ToArray()) seen.Remove(gone);
    }
    /// <summary>Runs one test with the guard on; afterwards closes every window. Throws when a prompt had to be closed or the test ran too long.</summary>
    public static void Run(string name, Action test)
    {
        Enable(); current = name; started = DateTime.UtcNow; Closed.Clear();
        try { test(); }
        finally
        {
            foreach (var w in Application.Current?.Windows.OfType<Window>().ToArray() ?? Array.Empty<Window>()) { try { w.Close(); } catch (InvalidOperationException) { } }
            seen.Clear();
        }
        if (Closed.Count > 0) throw new Exception("FAIL: a test left a modal window open: " + string.Join("; ", Closed));
        if (DateTime.UtcNow - started > Limit) throw new Exception($"FAIL: test '{name}' ran longer than {Limit.TotalSeconds:0} s");
    }
}
