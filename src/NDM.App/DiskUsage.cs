using System.IO;
using System.Runtime.InteropServices;
using NDM.App.Localization;
namespace NDM.App;

public enum DiskKind { Fixed, Removable, Network, Other }

/// <summary>Space numbers of one disk (a drive letter or a network share). Ready = false: the disk is there but cannot be read now (offline share, empty reader).</summary>
public sealed record DiskInfo(string Root, string Label, DiskKind Kind, bool Ready, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => Ready ? Math.Max(0, TotalBytes - FreeBytes) : 0;
    public double UsedFraction => Ready && TotalBytes > 0 ? Math.Clamp((double)UsedBytes / TotalBytes, 0, 1) : 0;
    public int UsedPercent => (int)Math.Round(UsedFraction * 100);
    public bool SameDisk(DiskInfo? other) => other is not null && string.Equals(Root, other.Root, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads disk space. Both readers are replaceable so tests never depend on the machine's real disks.</summary>
public static class DiskUsage
{
    /// <summary>Every ready fixed and removable disk, sorted by drive letter.</summary>
    public static Func<IReadOnlyList<DiskInfo>> Enumerate { get; set; } = DefaultEnumerate;
    /// <summary>The disk a file or folder path lives on (drive letter or network share); null when the path has no usable root.</summary>
    public static Func<string?, DiskInfo?> Resolve { get; set; } = DefaultResolve;

    /// <summary>"C:" for C:\ and the share name for a network path.</summary>
    public static string LabelOf(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? root : trimmed;
    }
    public static string? RootOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(full);
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) { return null; }
    }

    private static IReadOnlyList<DiskInfo> DefaultEnumerate()
    {
        var list = new List<DiskInfo>();
        string[] names;
        try { names = Environment.GetLogicalDrives(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return list; }
        foreach (var name in names)
        {
            var kind = KindOf(name);
            if (kind is not (DiskKind.Fixed or DiskKind.Removable)) continue; // network shares and optical drives never join the cycle (they can block for seconds)
            var info = Read(name, kind);
            if (info.Ready) list.Add(info);
        }
        return list.OrderBy(d => d.Root, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static DiskInfo? DefaultResolve(string? path)
    {
        var root = RootOf(path);
        return root is null ? null : Read(root, KindOf(root));
    }

    private static DiskInfo Read(string root, DiskKind kind)
    {
        try
        {
            if (GetDiskFreeSpaceEx(root, out long available, out long total, out _) && total > 0)
                return new DiskInfo(root, LabelOf(root), kind, true, total, Math.Min(available, total));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { }
        return new DiskInfo(root, LabelOf(root), kind, false, 0, 0);
    }

    private static DiskKind KindOf(string root)
    {
        try
        {
            return GetDriveType(root) switch { 2 => DiskKind.Removable, 3 => DiskKind.Fixed, 4 => DiskKind.Network, _ => DiskKind.Other };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return DiskKind.Other; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out long freeBytesAvailable, out long totalBytes, out long totalFreeBytes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPathName);

    // ---- text ----
    /// <summary>"C: 120 GB free of 476 GB", "C: not available" or the no-information text.</summary>
    public static string StatusText(DiskInfo? disk) => disk is null ? Loc.T("disk.none")
        : disk.Ready ? Loc.T("disk.status", disk.Label, Loc.Size(disk.FreeBytes), Loc.Size(disk.TotalBytes)) : Loc.T("disk.unavailable", disk.Label);
    public static string TipLine(DiskInfo disk) => disk.Ready ? Loc.T("disk.tip_line", disk.Label, Loc.Size(disk.FreeBytes), Loc.Size(disk.TotalBytes), disk.UsedPercent) : Loc.T("disk.tip_unavailable", disk.Label);
    /// <summary>Plain-words warning when the remaining download does not fit; "" when it fits or either number is unknown.</summary>
    public static string LowSpaceWarning(DiskInfo? disk, long remainingBytes)
        => disk is { Ready: true } && remainingBytes > 0 && disk.FreeBytes < remainingBytes
            ? Loc.T("progress.disk_low", Loc.Size(remainingBytes), Loc.Size(disk.FreeBytes), disk.Label) : "";
}

/// <summary>Which disk the status bar shows: the one of the default download folder until the user clicks, then the next disk each click; Escape goes back.</summary>
public sealed class DiskCycler
{
    private DiskInfo? defaultDisk; private string? chosenRoot;
    private IReadOnlyList<DiskInfo> ordered = Array.Empty<DiskInfo>();
    /// <summary>Default disk first, then every other ready disk by letter.</summary>
    public IReadOnlyList<DiskInfo> Disks => ordered;
    public DiskInfo? Current => chosenRoot is null ? defaultDisk : ordered.FirstOrDefault(d => string.Equals(d.Root, chosenRoot, StringComparison.OrdinalIgnoreCase)) ?? defaultDisk;
    public bool IsDefault => Current is { } c && c.SameDisk(defaultDisk);
    public void Update(IReadOnlyList<DiskInfo> disks, DiskInfo? defaultInfo)
    {
        defaultDisk = defaultInfo;
        var list = new List<DiskInfo>();
        if (defaultInfo is not null) list.Add(defaultInfo);
        foreach (var d in disks.OrderBy(d => d.Root, StringComparer.OrdinalIgnoreCase)) if (!list.Any(x => x.SameDisk(d))) list.Add(d);
        ordered = list;
        if (chosenRoot is not null && !ordered.Any(d => string.Equals(d.Root, chosenRoot, StringComparison.OrdinalIgnoreCase))) chosenRoot = null; // the chosen disk went away (a removed card)
    }
    public void Next()
    {
        if (ordered.Count == 0) { chosenRoot = null; return; }
        int index = Current is { } c ? IndexOf(c) : -1;
        var next = ordered[(index + 1) % ordered.Count];
        chosenRoot = next.SameDisk(defaultDisk) ? null : next.Root;
    }
    public void Reset() => chosenRoot = null;
    private int IndexOf(DiskInfo disk) { for (int i = 0; i < ordered.Count; i++) if (ordered[i].SameDisk(disk)) return i; return -1; }
}
