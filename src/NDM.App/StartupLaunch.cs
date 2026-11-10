using System.IO;
using Microsoft.Win32;

namespace NDM.App;

/// <summary>What the main window does on launch.</summary>
public enum LaunchView { Tray, Normal, Maximized }

/// <summary>1.13.0: how the window starts. A launch from NeoDM's own Windows startup entry carries the <c>--startup</c> marker and follows Options > General > When Windows starts NeoDM;
/// a manual launch (no marker) always shows the normal window. An explicit <c>--tray</c>/<c>--background</c> keeps meaning tray.</summary>
public static class StartupLaunch
{
    public static LaunchView Decide(StartupArguments args, StartupWindowMode mode)
    {
        if (args.Tray) return LaunchView.Tray;
        if (!args.Startup) return LaunchView.Normal;
        return mode switch { StartupWindowMode.Normal => LaunchView.Normal, StartupWindowMode.Maximized => LaunchView.Maximized, _ => LaunchView.Tray };
    }
}

public enum StartupScope { Machine, User }

/// <summary>The Windows startup list (Run key). The real one is <see cref="RegistryStartupStore"/>; tests use a fake and never write the registry.</summary>
public interface IStartupEntryStore
{
    /// <summary>The value of NeoDM's own entry in this scope, or null when there is none.</summary>
    string? Get(StartupScope scope);
    /// <summary>Replaces NeoDM's own entry. False when the scope cannot be written (no rights).</summary>
    bool Set(StartupScope scope, string value);
}

public static class StartupEntry
{
    public const string Marker = "--startup";
    public const string ValueName = "NeoDM";
    public static string Build(string exePath) => "\"" + exePath + "\" " + Marker;

    /// <summary>The program path at the start of a Run value (quoted, or up to the first ".exe").</summary>
    public static string? ProgramOf(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return null;
        if (text[0] == '"') { var end = text.IndexOf('"', 1); return end > 1 ? text[1..end] : null; }
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : null;
    }
    public static bool IsOwn(string? value, string exePath) =>
        ProgramOf(value) is { } program && string.Equals(program.Trim(), exePath, StringComparison.OrdinalIgnoreCase);
    public static bool HasMarker(string? value)
    {
        var program = ProgramOf(value); if (program is null) return false;
        var rest = (value ?? "").Trim();
        rest = rest.StartsWith('"') ? rest[(program.Length + 2)..] : rest[program.Length..];
        return rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(a => a.Equals(Marker, StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>Rewrites an old NeoDM entry (this program, no marker) in each scope. Another program's entry, a missing entry and an entry that already has the marker are left alone.
    /// Returns how many entries were rewritten.</summary>
    public static int Migrate(IStartupEntryStore store, string exePath)
    {
        int changed = 0;
        foreach (var scope in new[] { StartupScope.User, StartupScope.Machine })
        {
            var value = store.Get(scope);
            if (value is null || !IsOwn(value, exePath) || HasMarker(value)) continue;
            if (store.Set(scope, Build(exePath))) changed++;
        }
        return changed;
    }
}

/// <summary>The real Run keys. Only the value named NeoDM is read or written; a scope without rights reports false.</summary>
public sealed class RegistryStartupStore : IStartupEntryStore
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private static RegistryKey Root(StartupScope s) => s == StartupScope.Machine ? Registry.LocalMachine : Registry.CurrentUser;
    public string? Get(StartupScope scope)
    {
        try { using var key = Root(scope).OpenSubKey(RunKey); return key?.GetValue(StartupEntry.ValueName) as string; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
    public bool Set(StartupScope scope, string value)
    {
        try { using var key = Root(scope).OpenSubKey(RunKey, writable: true); if (key is null) return false; key.SetValue(StartupEntry.ValueName, value, RegistryValueKind.String); return true; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }
}
