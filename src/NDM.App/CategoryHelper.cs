using System.IO;

namespace NDM.App;

/// <summary>
/// Maps file extensions to download categories for automatic folder organization.
/// Categorizes downloads into Programs, Compressed, Music, Video, Documents, or General
/// based on file extension, enabling automatic subfolder organization.
/// </summary>
public static class CategoryHelper
{
    public enum Category { Programs, Compressed, Music, Video, Documents, General }

    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".msi", ".dmg", ".pkg", ".deb", ".rpm" };
    
    private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz" };
    
    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".wma" };
    
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" };
    
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt" };

    public static Category GetCategory(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension)) return Category.General;
        if (ProgramExtensions.Contains(extension)) return Category.Programs;
        if (CompressedExtensions.Contains(extension)) return Category.Compressed;
        if (MusicExtensions.Contains(extension)) return Category.Music;
        if (VideoExtensions.Contains(extension)) return Category.Video;
        if (DocumentExtensions.Contains(extension)) return Category.Documents;
        return Category.General;
    }

    public static string GetCategoryFolder(Category category, AppSettings settings)
    {
        if (!settings.EnableCategoryFolders) return settings.DownloadDirectory;
        
        var subfolder = category switch
        {
            Category.Programs => settings.ProgramsFolder,
            Category.Compressed => settings.CompressedFolder,
            Category.Music => settings.MusicFolder,
            Category.Video => settings.VideoFolder,
            Category.Documents => settings.DocumentsFolder,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(subfolder)) return settings.DownloadDirectory;
        
        // If absolute path, use as-is; otherwise relative to base download directory
        return Path.IsPathFullyQualified(subfolder) 
            ? subfolder 
            : Path.Combine(settings.DownloadDirectory, subfolder);
    }

    /// <summary>The folder for a file (1.9.0): custom categories, then built-in ones, then the download folder. <paramref name="url"/> and <paramref name="referer"/> feed the address rules.</summary>
    public static string GetDownloadPath(string fileName, AppSettings settings, string? url = null, string? referer = null) =>
        CategoryRules.Resolve(settings, fileName, url, referer).Folder;
}
