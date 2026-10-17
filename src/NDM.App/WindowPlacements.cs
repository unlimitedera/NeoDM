using System.IO;
using System.Text.Json;
using System.Windows;

namespace NDM.App;

/// <summary>One remembered window rectangle in device-independent units. <see cref="Maximized"/> restores the maximised state over the stored normal size.</summary>
public sealed class PlacementRecord
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Remembers the size and position of each window type in <c>window-placement.json</c> inside the state directory, and applies it before the window is shown.
/// A stored rectangle is validated against the current screen: the size is limited to the work area and a window whose title bar would not be reachable
/// is centred again. Unreadable or invalid entries are dropped. Nothing happens until <see cref="Configure"/> names a state directory, so tests and tools stay unaffected.
/// </summary>
public static class WindowPlacements
{
    public const string FileName = "window-placement.json";
    private const double TitleStrip = 32, MinVisibleWidth = 96, MinVisibleHeight = 24;
    private static string? file;
    private static Dictionary<string, PlacementRecord> records = new(StringComparer.Ordinal);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, PlacementRecord> applied = new();

    /// <summary>Work area and full virtual screen in device-independent units. Replaceable so tests can simulate other screens.</summary>
    public static Func<Rect> WorkArea { get; set; } = () => SystemParameters.WorkArea;
    public static Func<Rect> VirtualScreen { get; set; } = () => new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

    /// <summary>Points the store at a state directory (or disables it with null) and loads what was saved there.</summary>
    public static void Configure(string? stateDirectory)
    {
        file = stateDirectory is { Length: > 0 } ? Path.Combine(stateDirectory, FileName) : null;
        records = Load(file);
    }

    public static PlacementRecord? Get(string key) => records.TryGetValue(key, out var r) ? Copy(r) : null;

    private static PlacementRecord Copy(PlacementRecord r) => new() { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height, Maximized = r.Maximized };
    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    private static Dictionary<string, PlacementRecord> Load(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path)) return new(StringComparer.Ordinal);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, PlacementRecord>>(File.ReadAllText(path));
            return loaded is null ? new(StringComparer.Ordinal) : new(loaded.Where(p => p.Value is not null), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return new(StringComparer.Ordinal); }
    }

    /// <summary>Returns a rectangle that is safe to apply, or null when the stored values are unusable.</summary>
    public static PlacementRecord? Validate(PlacementRecord stored, Size minimum, Rect work, Rect screen)
    {
        if (!Finite(stored.Width) || !Finite(stored.Height) || stored.Width < 100 || stored.Height < 80) return null;
        double maxW = Math.Max(200, work.Width - WindowSizing.Margin), maxH = Math.Max(160, work.Height - WindowSizing.Margin);
        double width = Math.Min(Math.Max(stored.Width, Math.Min(minimum.Width, maxW)), maxW);
        double height = Math.Min(Math.Max(stored.Height, Math.Min(minimum.Height, maxH)), maxH);
        double left = stored.Left, top = stored.Top;
        var title = new Rect(Finite(left) ? left : double.NaN, Finite(top) ? top : double.NaN, width, TitleStrip);
        var visible = Finite(left) && Finite(top) ? Rect.Intersect(title, screen) : Rect.Empty;
        bool reachable = !visible.IsEmpty && visible.Width >= Math.Min(MinVisibleWidth, width) && visible.Height >= MinVisibleHeight;
        if (!reachable)
        {
            left = work.Left + (work.Width - width) / 2; top = work.Top + (work.Height - height) / 2;
        }
        else
        {
            // keep the whole window on the screen when it fits there
            if (width <= screen.Width) left = Math.Min(Math.Max(left, screen.Left), screen.Right - width);
            if (height <= screen.Height) top = Math.Min(Math.Max(top, screen.Top), screen.Bottom - height);
        }
        return new PlacementRecord { Left = left, Top = top, Width = width, Height = height, Maximized = stored.Maximized };
    }

    /// <summary>
    /// Applies the stored placement of <paramref name="key"/> to <paramref name="window"/> (call right after InitializeComponent) and saves the
    /// placement when the window closes. <paramref name="adjustBeforeSave"/> lets a window correct the height it reports, for example a progress window with collapsed details.
    /// </summary>
    public static void Track(Window window, string key, Func<PlacementRecord, PlacementRecord>? adjustBeforeSave = null)
    {
        if (file is null) return;
        if (records.TryGetValue(key, out var stored))
        {
            var safe = Validate(stored, new Size(window.MinWidth, window.MinHeight), WorkArea(), VirtualScreen());
            if (safe is null) { records.Remove(key); Persist(); }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.SizeToContent = SizeToContent.Manual;
                window.Width = safe.Width; window.Height = safe.Height; window.Left = safe.Left; window.Top = safe.Top;
                applied.AddOrUpdate(window, safe);
                // maximise once the native window exists, so RestoreBounds keeps the stored normal rectangle
                if (safe.Maximized) window.SourceInitialized += (_, _) => window.WindowState = WindowState.Maximized;
            }
        }
        window.Closing += (_, _) => Save(window, key, adjustBeforeSave);
    }

    /// <summary>Stores the current placement of a window. Minimised or maximised windows store the bounds they return to.</summary>
    public static void Save(Window window, string key, Func<PlacementRecord, PlacementRecord>? adjust = null)
    {
        if (file is null) return;
        bool normal = window.WindowState == WindowState.Normal;
        var bounds = normal ? new Rect(window.Left, window.Top, window.ActualWidth > 0 ? window.ActualWidth : window.Width, window.ActualHeight > 0 ? window.ActualHeight : window.Height) : window.RestoreBounds;
        // A window that was opened maximised reports the maximised size as its restore size; keep the rectangle that was applied to it.
        if (!normal && applied.TryGetValue(window, out var origin) && (bounds.Width >= WorkArea().Width - 1 || bounds.Height >= WorkArea().Height - 1)) bounds = new Rect(origin.Left, origin.Top, origin.Width, origin.Height);
        if (!Finite(bounds.Left) || !Finite(bounds.Top) || !Finite(bounds.Width) || !Finite(bounds.Height) || bounds.Width < 100 || bounds.Height < 80) return;
        var record = new PlacementRecord { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height, Maximized = window.WindowState == WindowState.Maximized };
        if (adjust is not null) record = adjust(record);
        records[key] = record;
        Persist();
    }

    /// <summary>Forgets every stored placement (used by tests; the next open uses the built-in size).</summary>
    public static void Reset() { records.Clear(); Persist(); }

    private static void Persist()
    {
        if (file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a placement that cannot be saved is not worth an error dialog */ }
    }
}
