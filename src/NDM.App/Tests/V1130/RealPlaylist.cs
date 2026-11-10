using System.Diagnostics;
using System.Text;
using NDM.App;
using NDM.Core;

internal static partial class V1130Tests
{
    /// <summary>Opt-in (needs the internet and the bundled yt-dlp): lists a real public playlist (metadata only, no media) and loads every thumbnail address NeoDM would use, printing the address, the outcome and the failure reason.</summary>
    private static void RealPlaylist(string scratch, string[] args)
    {
        var url = args.SkipWhile(a => a != "--real-playlist").Skip(1).FirstOrDefault() ?? "https://www.youtube.com/playlist?list=PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI";
        var tool = Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp", "yt-dlp.exe");
        if (!File.Exists(tool)) tool = Path.Combine(FindUpDir("dist/app/tools/yt-dlp/yt-dlp.exe"), "dist", "app", "tools", "yt-dlp", "yt-dlp.exe");
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in new[] { "--ignore-config", "--no-plugin-dirs", "--no-update", "--no-cache-dir", "--yes-playlist", "--use-extractors", "youtube.*", "--socket-timeout", "30", "--no-colors", "--encoding", "utf-8", "--flat-playlist", "--skip-download", "--playlist-end", "12", "--quiet", "--print", YoutubePlaylists.EntryTemplate, "--", url }) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!; var text = process.StandardOutput.ReadToEnd(); process.WaitForExit();
        var list = YoutubePlaylists.Parse(text); Console.WriteLine($"REAL listing: {list.Entries.Count} entries, title '{list.Title}'");
        var state = Path.Combine(scratch, "real-thumbs"); int ok = 0, failed = 0;
        using var loader = new ThumbnailLoader(state);
        loader.Trace = (id, address, outcome) => Console.WriteLine($"  THUMB {id} {outcome} {address}");
        var tasks = list.Entries.Select(e => loader.LoadAsync(e.Id, e.ThumbnailUrl)).ToList();
        PumpUntil(() => tasks.All(t => t.IsCompleted), "all thumbnail requests finished", 90000);
        foreach (var t in tasks) { if (t.Result is not null) ok++; else failed++; }
        Console.WriteLine($"REAL thumbnails: {ok} loaded, {failed} failed of {tasks.Count}");
        Check(ok > 0, "at least one real thumbnail loaded");
    }
    private static string FindUpDir(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative))) dir = dir.Parent;
        return dir?.FullName ?? throw new FileNotFoundException(relative);
    }
}
