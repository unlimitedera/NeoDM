// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.IO;
using System.Windows;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>What the add guards decided: carry on with <see cref="Request"/> (the name or folder may have changed), or stop.</summary>
public sealed record GuardResult(bool Proceed, DownloadRequest Request);

/// <summary>2.0.0: the questions asked before a plain file download is added. A duplicate (the same address, or a file with the same name and size already in the list or in the save folder)
/// offers Download again / Rename / Skip; a drive without the room for the file offers another folder, a temporary files folder on another drive, or going on anyway.
/// Streams, YouTube and torrents are not asked. Tests answer through <see cref="ChoiceWindow.Override"/>.</summary>
public static class AddGuard
{
    /// <summary>Asks the new file name (owner, title, prompt, suggestion, validator); tests replace it. Null = cancelled.</summary>
    public static Func<Window?, string, string, string, Func<string, string?>, string?>? AskName { get; set; }
    /// <summary>Picks a folder (owner, title); tests replace it. Null = cancelled.</summary>
    public static Func<Window?, string, string?>? PickFolder { get; set; }

    public static GuardResult Check(DownloadManager manager, AppSettings? settings, DownloadRequest request, Window? owner, Action? saveSettings = null)
    {
        if (request.Youtube is not null || request.StreamKind != StreamKind.None) return new(true, request);
        string folder;
        try { folder = manager.FolderFor(request); } catch (Exception ex) when (ex is ArgumentException or UriFormatException or IOException) { return new(true, request); }
        var dup = manager.FindDuplicate(request.Url, request.FileName, request.ExpectedSize, folder);
        if (dup is not null)
        {
            var text = dup.Kind switch
            {
                DuplicateKind.Downloaded => Loc.T("dup.downloaded", dup.FileName),
                DuplicateKind.InList => Loc.T("dup.in_list", dup.FileName),
                _ => Loc.T("dup.same_file", dup.FileName)
            };
            var answer = ChoiceWindow.Ask(owner, Loc.T("dup.title"), text, Loc.T("dup.btn.again"), Loc.T("dup.btn.rename"), Loc.T("dup.btn.skip"));
            if (answer < 0) return new(false, request);
            if (answer == 1)
            {
                var suggestion = manager.FreeFileName(request.FileName ?? dup.FileName, folder);
                var name = (AskName ?? DefaultAskName)(owner, Loc.T("dup.rename.title"), Loc.T("dup.rename.text"), suggestion, ValidateName);
                if (name is null) return new(false, request);
                request = request with { FileName = name, AutoName = false };
            }
        }
        return CheckSpace(manager, settings, request, owner, saveSettings);
    }

    /// <summary>The low-space question for a download whose size is known. Up to three rounds: each choice of another folder is checked again.</summary>
    public static GuardResult CheckSpace(DownloadManager manager, AppSettings? settings, DownloadRequest request, Window? owner, Action? saveSettings = null)
    {
        if (request.ExpectedSize is not > 0) return new(true, request);
        for (var round = 0; round < 3; round++)
        {
            var folder = manager.FolderFor(request);
            var shortages = manager.CheckSpace(request.ExpectedSize.Value, folder);
            if (shortages.Count == 0) return new(true, request);
            var first = shortages[0];
            var text = Loc.T(first.IsTemporaryFolder ? "space.short_temp" : "space.short", first.Drive, Loc.Size(first.FreeBytes), Loc.Size(first.NeededBytes));
            var others = OtherDrives(first.Drive, first.NeededBytes);
            if (others.Length > 0) text += " " + Loc.T("space.other_drives", string.Join(", ", others));
            var answer = ChoiceWindow.Ask(owner, Loc.T("space.title"), text, Loc.T("space.btn.folder"), Loc.T("space.btn.temp"), Loc.T("space.btn.anyway"), Loc.T("common.btn.cancel"));
            if (answer == 0)
            {
                var picked = (PickFolder ?? DefaultPick)(owner, Loc.T("space.picker.folder"));
                if (picked is null) return new(false, request);
                request = request with { TargetDirectory = picked };
            }
            else if (answer == 1)
            {
                var picked = (PickFolder ?? DefaultPick)(owner, Loc.T("space.picker.temp"));
                if (picked is null) return new(false, request);
                manager.TemporaryFolder = picked;
                if (settings is not null) { settings.TemporaryFolder = picked; try { saveSettings?.Invoke(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
                Dialogs.Show(owner, Loc.T("space.temp_note", picked), Loc.T("space.title"));
            }
            else if (answer == 2) return new(true, request);
            else return new(false, request);
        }
        return new(true, request);
    }

    /// <summary>A batch (the link grabber): one question for every entry that looks already downloaded or listed. Null = the user cancelled the whole add.</summary>
    public static List<DownloadRequest>? FilterBatch(DownloadManager manager, IReadOnlyList<DownloadRequest> requests, Window? owner)
    {
        var duplicates = new HashSet<int>();
        for (var i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            if (r.Youtube is not null || r.StreamKind != StreamKind.None) continue;
            try { if (manager.FindDuplicate(r.Url, r.FileName, r.ExpectedSize, manager.FolderFor(r)) is not null) duplicates.Add(i); }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException or IOException) { }
        }
        if (duplicates.Count == 0) return requests.ToList();
        var answer = ChoiceWindow.Ask(owner, Loc.T("dup.title"), Loc.T("dup.batch", duplicates.Count, requests.Count), Loc.T("dup.btn.skip_those"), Loc.T("dup.btn.all"), Loc.T("common.btn.cancel"));
        if (answer < 0) return null;
        return answer == 0 ? requests.Where((_, i) => !duplicates.Contains(i)).ToList() : requests.ToList();
    }

    /// <summary>Fixed drives other than <paramref name="skipDrive"/> with at least <paramref name="needed"/> bytes free, as "D: 120 GB free".</summary>
    public static string[] OtherDrives(string skipDrive, long needed)
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed && !d.Name.TrimEnd('\\').Equals(skipDrive, StringComparison.OrdinalIgnoreCase) && d.AvailableFreeSpace >= needed)
                .Select(d => Loc.T("space.drive_free", d.Name.TrimEnd('\\'), Loc.Size(d.AvailableFreeSpace))).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static string? ValidateName(string name) =>
        name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ? Loc.T("error.file_name_invalid") : null;

    private static string? DefaultAskName(Window? owner, string title, string prompt, string initial, Func<string, string?> validate)
    {
        if (Dialogs.TestMode) return null;
        var window = new TextPromptWindow(title, prompt, initial, validate);
        PromptWindows.Prepare(window, owner);
        return window.ShowDialog() == true ? window.Value : null;
    }

    private static string? DefaultPick(Window? owner, string title)
    {
        if (Dialogs.TestMode) return null;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true ? dialog.FolderName : null;
    }
}

/// <summary>2.0.0: the folders in which an unfinished work folder can be left behind, and the cleanup that Options and the start of the app use.</summary>
public static class PartialSweep
{
    public static IReadOnlyList<string> Folders(AppSettings settings)
    {
        var list = new List<string> { settings.DownloadDirectory };
        foreach (var sample in new[] { "a.zip", "a.exe", "a.mp3", "a.mp4", "a.pdf", "a.jpg" })
        {
            try { list.Add(CategoryHelper.GetDownloadPath(sample, settings)); } catch (Exception ex) when (ex is ArgumentException or IOException) { }
        }
        if (!string.IsNullOrWhiteSpace(settings.TemporaryFolder)) list.Add(settings.TemporaryFolder);
        return list.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Removes unfinished data that no download in the list owns. Called off the UI thread at start.</summary>
    public static (int Folders, long Bytes) CleanNow(DownloadManager manager, AppSettings settings) => manager.CleanOrphanedPartials(Folders(settings));
    public static long SizeNow(DownloadManager manager, AppSettings settings) => manager.FindOrphanedPartials(Folders(settings)).Sum(f => f.Bytes);
}
