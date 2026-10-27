using System.Runtime.InteropServices;
using System.Windows;

namespace NDM.App;

/// <summary>
/// The one place that writes to the Windows clipboard. Another program can hold the clipboard locked for a moment (a COMException); the write is tried once more after a short pause.
/// It never throws: a failure returns false so the caller can say so in a notice. <see cref="Write"/> is a test seam.
/// </summary>
public static class SafeClipboard
{
    public static Action<string> Write { get; set; } = text => Clipboard.SetText(text);

    public static bool TrySetText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { Write(text); return true; }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException or System.Threading.ThreadStateException)
            {
                if (attempt == 0) System.Threading.Thread.Sleep(50);
            }
        }
        return false;
    }
}
