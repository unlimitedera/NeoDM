using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NDM.App;

/// <summary>1.10.0: type groups of the file icons. Each group has its own tint token in every theme (FileIcon{Group}Brush).</summary>
public enum FileGroup { Archive, Video, Audio, Program, Document, Picture, Disc, Other }

/// <summary>Maps a file name to an icon group and a short label, and holds the colour rules of the file icons (tokens per theme, page fill, label contrast).</summary>
public static class FileTypeIcons
{
    private static readonly Dictionary<string, FileGroup> Map = Build();
    private static Dictionary<string, FileGroup> Build()
    {
        var map = new Dictionary<string, FileGroup>(StringComparer.OrdinalIgnoreCase);
        void Add(FileGroup g, string list) { foreach (var e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map[e] = g; }
        Add(FileGroup.Archive, "zip rar 7z tar gz bz2 xz tgz tbz2 txz zst lz lzma cab z arj");
        Add(FileGroup.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp ogv vob m2ts mts ts");
        Add(FileGroup.Audio, "mp3 flac wav aac m4a ogg wma opus aiff aif mid midi amr ape");
        Add(FileGroup.Program, "exe msi msix msixbundle appx appxbundle dmg pkg deb rpm apk bat cmd ps1 sh appimage jar com");
        Add(FileGroup.Document, "pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp csv epub mobi azw3 djvu md");
        Add(FileGroup.Picture, "jpg jpeg png gif webp bmp svg tif tiff heic ico psd avif raw");
        Add(FileGroup.Disc, "iso img bin cue nrg mdf vhd vhdx vmdk wim");
        return map;
    }
    private static readonly Dictionary<string, string> Alias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["torrent"] = "TOR", ["appimage"] = "APP", ["sqlite"] = "DB", ["msixbundle"] = "MSIX", ["appxbundle"] = "APPX", ["webvtt"] = "VTT", ["crdownload"] = "CRDL",
    };
    public const int MaxLabel = 4;
    /// <summary>All extensions with an explicit group.</summary>
    public static IReadOnlyCollection<string> KnownExtensions => Map.Keys;

    /// <summary>The last extension of a file name, lower case, without the dot. Empty when there is none or it holds odd characters.</summary>
    public static string ExtensionOf(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "";
        var name = fileName.TrimEnd();
        int slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return ""; // ".gitignore" and "name." have no extension
        var ext = name[(dot + 1)..];
        if (ext.Length > 16) return "";
        foreach (var c in ext) if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')) return "";
        return ext.ToLowerInvariant();
    }
    public static FileGroup GroupOf(string? extension) => !string.IsNullOrEmpty(extension) && Map.TryGetValue(extension.TrimStart('.'), out var g) ? g : FileGroup.Other;
    public static FileGroup GroupOfFile(string? fileName) => GroupOf(ExtensionOf(fileName));

    /// <summary>Text written inside the page: upper case, up to <see cref="MaxLabel"/> characters. A longer extension uses a fixed short form (torrent = TOR) or its first four characters. No extension = empty (the page shows a generic mark).</summary>
    public static string Label(string? extension)
    {
        var e = (extension ?? "").TrimStart('.').Trim();
        if (e.Length == 0) return "";
        if (e.Length > MaxLabel) e = Alias.TryGetValue(e, out var a) ? a : e[..MaxLabel];
        return e.ToUpperInvariant();
    }

    /// <summary>Extension the icon of a download shows. Playlist and manifest names of stream jobs show the container the job ends as; a torrent with no extension gets the folder icon (the empty string).</summary>
    public static string DisplayExtension(string? fileName, bool isStream, bool isYoutube)
    {
        var ext = ExtensionOf(fileName);
        if ((isStream || isYoutube) && ext is "m3u8" or "m3u" or "mpd" or "") return isStream || isYoutube ? "mp4" : ext;
        return ext;
    }

    // ---- colours ----
    public const double PageTint = 0.16;
    public static string TokenKey(FileGroup g) => "FileIcon" + g switch { FileGroup.Disc => "Disc", var x => x.ToString() } + "Brush";
    public static readonly FileGroup[] Groups = Enum.GetValues<FileGroup>();
    public static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    /// <summary>Page fill: the surface with a little of the tint.</summary>
    public static Color PageFill(Color surface, Color tint) => Mix(surface, tint, PageTint);
    /// <summary>Tint token of a group from a theme dictionary or the running application; gray when the token is missing.</summary>
    public static Color Tint(FileGroup g, Func<string, object?> find) => (find(TokenKey(g)) as SolidColorBrush)?.Color ?? Color.FromRgb(0x88, 0x88, 0x88);
    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
