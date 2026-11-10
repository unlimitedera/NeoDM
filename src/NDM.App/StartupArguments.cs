namespace NDM.App;

/// <summary>Command-line contract: <c>--tray</c>/<c>--background</c>, <c>--startup</c> (written by the Windows startup entry), plus magnet: links and .torrent paths to hand to the running instance.</summary>
public sealed record StartupArguments(bool Tray, IReadOnlyList<string> Magnets, IReadOnlyList<string> TorrentFiles, bool Startup = false)
{
    public bool HasWork => Magnets.Count + TorrentFiles.Count > 0;
    public static StartupArguments Parse(IEnumerable<string> args)
    {
        bool tray = false, startup = false; var magnets = new List<string>(); var files = new List<string>();
        foreach (var raw in args)
        {
            var arg = raw?.Trim() ?? "";
            if (arg.Length == 0) continue;
            if (arg.Equals("--tray", StringComparison.OrdinalIgnoreCase) || arg.Equals("--background", StringComparison.OrdinalIgnoreCase)) tray = true;
            else if (arg.Equals(StartupEntry.Marker, StringComparison.OrdinalIgnoreCase)) startup = true;
            else if (arg.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) magnets.Add(arg);
            else if (arg.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && !arg.StartsWith('-')) files.Add(arg);
        }
        return new StartupArguments(tray, magnets, files, startup);
    }
}
