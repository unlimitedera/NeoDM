using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NDM.Core;

/// <summary>A pinned download of a helper tool: one https address, the SHA-256 of the archive and of the one file taken out of it.</summary>
public sealed record ToolPin(string Url, string Version, long ArchiveSize, string ArchiveSha256, string EntryName, long ExeSize, string ExeSha256);

/// <summary>A failure of the helper tool with a stable code for the app to localize.</summary>
public sealed class ToolException : Exception
{
    public const string NotInstalled = "tool-not-installed", HashMismatch = "tool-hash-mismatch", Download = "tool-download-failed", Host = "tool-host-refused", Archive = "tool-archive-refused", Outdated = "tool-outdated", Space = "tool-disk-space";
    public string Code { get; }
    public ToolException(string code, string message, Exception? inner = null) : base(message, inner) { Code = code; }
}

public sealed record ToolRunResult(int ExitCode, bool TimedOut, string Output, string Error);

/// <summary>The tool file, opened so that nobody can change it while it is used. Dispose after the process ended.</summary>
public sealed class VerifiedTool : IDisposable
{
    private readonly FileStream? held;
    public string Path { get; }
    public string Sha256 { get; }
    /// <summary>False for a program taken from PATH on the user's explicit request (nothing to compare it with).</summary>
    public bool Pinned { get; }
    internal VerifiedTool(string path, FileStream? held, string sha256, bool pinned) { Path = path; this.held = held; Sha256 = sha256; Pinned = pinned; }
    public void Dispose() => held?.Dispose();
}

/// <summary>Finds, installs, verifies and runs ffmpeg for joining picture and sound tracks without re-encoding. The program is downloaded only on request
/// from one pinned address, checked against a fixed SHA-256, and checked again each time before it runs (the file stays open for reading during the run).</summary>
public sealed class ToolManager
{
    /// <summary>The pinned release: gyan.dev "essentials" build, GPL v3 (see docs/THIRD-PARTY.md). Values were measured by downloading the archive once.</summary>
    public static readonly ToolPin FfmpegPin = new(
        "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip", "9.0.2", 114768076L,
        "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba",
        "ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe", 105423872L,
        "3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec");

    public const long MaxArchiveBytes = 400L * 1024 * 1024, MaxExeBytes = 400L * 1024 * 1024;
    public const int MaxArchiveEntries = 4000, MaxRedirects = 3;
    public const string ExeName = "ffmpeg.exe";

    private sealed class Info
    {
        public string Version { get; set; } = "";
        public string Url { get; set; } = "";
        public string ArchiveSha256 { get; set; } = "";
        public string ExeSha256 { get; set; } = "";
        public long ExeSize { get; set; }
        public DateTimeOffset InstalledAt { get; set; }
    }

    private readonly ToolPin pin;
    private readonly ProxyOptions? proxy;
    private readonly Func<HttpMessageHandler>? handlerFactory;
    public string ToolDirectory { get; }

    public string FfmpegPath => Path.Combine(ToolDirectory, ExeName);
    private string InfoPath => Path.Combine(ToolDirectory, "ffmpeg.json");
    /// <summary>User choice "use ffmpeg from PATH" (default off). When on and the pinned tool is not installed, ffmpeg.exe is looked up in PATH and run unverified.</summary>
    public bool UseFromPath { get; set; }
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <param name="stateDirectory">The state folder; the tool lives in <c>tools\ffmpeg</c> below it.</param>
    /// <param name="pin">Test seam; the app uses <see cref="FfmpegPin"/>.</param>
    /// <param name="handlerFactory">Test seam for the network.</param>
    public ToolManager(string stateDirectory, ToolPin? pin = null, bool useFromPath = false, ProxyOptions? proxy = null, Func<HttpMessageHandler>? handlerFactory = null)
    {
        ToolDirectory = Path.Combine(Path.GetFullPath(stateDirectory), "tools", "ffmpeg");
        this.pin = pin ?? FfmpegPin; UseFromPath = useFromPath; this.proxy = proxy; this.handlerFactory = handlerFactory;
    }

    public ToolPin Pin => pin;

    /// <summary>The tool is in place, is the pinned version and has the recorded size. (The hash is checked each time it runs.)</summary>
    public bool IsInstalled
    {
        get
        {
            var info = ReadInfo();
            try { return info is not null && info.ExeSha256 == pin.ExeSha256 && File.Exists(FfmpegPath) && new FileInfo(FfmpegPath).Length == info.ExeSize; }
            catch (IOException) { return false; }
        }
    }

    /// <summary>Installed (pinned), or available from PATH when the user turned that on.</summary>
    public bool IsAvailable => IsInstalled || (UseFromPath && FindOnPath() is not null);

    /// <summary>The installed version, or null.</summary>
    public string? GetVersion() => IsInstalled ? ReadInfo()?.Version : (UseFromPath && FindOnPath() is not null ? "PATH" : null);

    private Info? ReadInfo()
    {
        try
        {
            if (!File.Exists(InfoPath)) return null;
            return JsonSerializer.Deserialize<Info>(FileLimits.ReadAllTextCapped(InfoPath, 64 * 1024, "ffmpeg.json"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    public static string? FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), ExeName);
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Removes the installed tool.</summary>
    public void Uninstall()
    {
        try { if (Directory.Exists(ToolDirectory)) Directory.Delete(ToolDirectory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ToolException(ToolException.Archive, "The tool is in use and could not be removed.", ex); }
    }

    private HttpClient CreateClient()
    {
        HttpMessageHandler handler;
        if (handlerFactory is not null) handler = handlerFactory();
        else
        {
            var h = new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(30) };
            ProxyHttp.Apply(h, proxy);
            handler = h;
        }
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(RequestIdentity.ProductToken);
        return client;
    }

    private Uri CheckUrl(Uri uri, Uri pinned)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals(pinned.Host, StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            throw new ToolException(ToolException.Host, "The tool may only be downloaded from " + pinned.Host + " over https.");
        return uri;
    }

    /// <summary>Downloads the pinned archive (https, pinned host only), checks its SHA-256, takes out only ffmpeg.exe with size limits, checks that file's SHA-256 and records it.</summary>
    public async Task InstallAsync(IProgress<(long Done, long Total)>? progress = null, CancellationToken cancellation = default)
    {
        if (!Uri.TryCreate(pin.Url, UriKind.Absolute, out var pinned)) throw new ToolException(ToolException.Host, "The pinned address is invalid.");
        CheckUrl(pinned, pinned);
        if (pin.ArchiveSize <= 0 || pin.ArchiveSize > MaxArchiveBytes || pin.ExeSize <= 0 || pin.ExeSize > MaxExeBytes) throw new ToolException(ToolException.Archive, "The pinned sizes are invalid.");
        Directory.CreateDirectory(ToolDirectory);
        EnsureSpace(pin.ArchiveSize + pin.ExeSize * 2);
        FileStream gate;
        try { gate = new FileStream(Path.Combine(ToolDirectory, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException ex) { throw new ToolException(ToolException.Download, "The tool is already being installed.", ex); }
        var archive = Path.Combine(ToolDirectory, ".download-" + Guid.NewGuid().ToString("N") + ".zip");
        var staged = Path.Combine(ToolDirectory, ".extract-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            using (gate)
            {
                await DownloadArchiveAsync(pinned, archive, progress, cancellation).ConfigureAwait(false);
                var exeHash = ExtractExe(archive, staged);
                // Verify again from the file on disk, not from the stream that wrote it.
                string reread;
                using (var check = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read)) reread = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellation).ConfigureAwait(false)).ToLowerInvariant();
                if (!reread.Equals(pin.ExeSha256, StringComparison.Ordinal) || !exeHash.Equals(reread, StringComparison.Ordinal)) throw new ToolException(ToolException.HashMismatch, "The program taken out of the archive does not match the expected SHA-256. It was not installed.");
                try { File.Delete(InfoPath); } catch (IOException) { }
                File.Move(staged, FfmpegPath, true);
                var info = new Info { Version = pin.Version, Url = pin.Url, ArchiveSha256 = pin.ArchiveSha256, ExeSha256 = reread, ExeSize = new FileInfo(FfmpegPath).Length, InstalledAt = DateTimeOffset.UtcNow };
                var temporary = InfoPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                File.Move(temporary, InfoPath, true);
            }
        }
        finally
        {
            foreach (var f in new[] { archive, staged }) { try { if (File.Exists(f)) File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        }
    }

    private void EnsureSpace(long bytes)
    {
        try
        {
            var root = Path.GetPathRoot(ToolDirectory);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < bytes) throw new ToolException(ToolException.Space, $"Not enough free disk space to install the tool: about {bytes >> 20} MB is needed and {Math.Max(0, free) >> 20} MB is free.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { }
    }

    private async Task DownloadArchiveAsync(Uri pinned, string archive, IProgress<(long Done, long Total)>? progress, CancellationToken cancellation)
    {
        using var client = CreateClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(DownloadTimeout);
        var uri = pinned;
        for (int hop = 0; ; hop++)
        {
            HttpResponseMessage response;
            try { response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
            catch (HttpRequestException ex) { throw new ToolException(ToolException.Download, "The tool could not be downloaded: " + ex.Message, ex); }
            using (response)
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects) throw new ToolException(ToolException.Download, "Too many redirects while downloading the tool.");
                    uri = CheckUrl(new Uri(uri, location), pinned);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new ToolException(ToolException.Download, $"The tool download failed (HTTP {(int)response.StatusCode}).");
                var declared = response.Content.Headers.ContentLength;
                if (declared is { } d && d != pin.ArchiveSize) throw new ToolException(ToolException.HashMismatch, "The download has a different size than the pinned release. It was not installed.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long done = 0;
                await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
                {
                    var buffer = new byte[1 << 16]; int read;
                    try
                    {
                        while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                        {
                            done += read;
                            if (done > pin.ArchiveSize) throw new ToolException(ToolException.HashMismatch, "The download is larger than the pinned release. It was not installed.");
                            hash.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                            progress?.Report((done, pin.ArchiveSize));
                        }
                    }
                    catch (IOException ex) { throw new ToolException(ToolException.Download, "The tool download was interrupted: " + ex.Message, ex); }
                }
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (done != pin.ArchiveSize || !actual.Equals(pin.ArchiveSha256, StringComparison.Ordinal)) throw new ToolException(ToolException.HashMismatch, "The downloaded archive does not match the expected SHA-256. It was not installed.");
                return;
            }
        }
    }

    /// <summary>Writes the one wanted entry to <paramref name="destination"/> (a fixed name chosen here, never taken from the archive) and returns its SHA-256.</summary>
    private string ExtractExe(string archive, string destination)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            if (zip.Entries.Count > MaxArchiveEntries) throw new ToolException(ToolException.Archive, "The archive has too many entries.");
            long declaredTotal = 0;
            foreach (var e in zip.Entries)
            {
                var n = e.FullName;
                if (n.Contains("..", StringComparison.Ordinal) || n.StartsWith('/') || n.StartsWith('\\') || n.Contains(':') || n.Contains('\\')) throw new ToolException(ToolException.Archive, "The archive has an unsafe entry name.");
                declaredTotal += e.Length;
                if (declaredTotal > 4L * MaxExeBytes) throw new ToolException(ToolException.Archive, "The archive claims too much data.");
            }
            var matches = zip.Entries.Where(e => string.Equals(e.FullName, pin.EntryName, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1) throw new ToolException(ToolException.Archive, "The archive does not contain " + pin.EntryName + " exactly once.");
            var entry = matches[0];
            if (entry.Length != pin.ExeSize || entry.Length > MaxExeBytes) throw new ToolException(ToolException.Archive, "The program in the archive has an unexpected size.");
            if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > 200) throw new ToolException(ToolException.Archive, "The archive entry has a suspicious compression ratio.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            using (var input = entry.Open())
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buffer = new byte[1 << 16]; int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    if (written > pin.ExeSize) throw new ToolException(ToolException.Archive, "The program in the archive is larger than it claims.");
                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }
                output.Flush(true);
            }
            if (written != pin.ExeSize) throw new ToolException(ToolException.Archive, "The program in the archive is smaller than it claims.");
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (InvalidDataException ex) { throw new ToolException(ToolException.Archive, "The archive is damaged.", ex); }
    }

    /// <summary>Opens the tool for use: the installed file must hash to the pinned value (checked through the handle that stays open), or, only when the user allowed it, ffmpeg.exe from PATH.</summary>
    public VerifiedTool OpenVerified()
    {
        var info = ReadInfo();
        if (info is not null && File.Exists(FfmpegPath))
        {
            if (info.ExeSha256 != pin.ExeSha256) throw new ToolException(ToolException.Outdated, "The installed tool is not the version this app expects. Install it again.");
            FileStream held;
            try { held = new FileStream(FfmpegPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan); }
            catch (IOException ex) { throw new ToolException(ToolException.NotInstalled, "The tool is in use or cannot be opened.", ex); }
            var actual = Convert.ToHexString(SHA256.HashData(held)).ToLowerInvariant();
            if (!actual.Equals(info.ExeSha256, StringComparison.Ordinal)) { held.Dispose(); throw new ToolException(ToolException.HashMismatch, "The installed tool has changed since it was installed, so it was not started. Install it again."); }
            held.Position = 0;
            return new VerifiedTool(FfmpegPath, held, actual, true);
        }
        if (UseFromPath && FindOnPath() is { } found) return new VerifiedTool(found, null, "", false);
        throw new ToolException(ToolException.NotInstalled, "The helper tool is not installed.");
    }

    /// <summary>Runs the tool with the given arguments (each its own argument, no shell). Killed with its children after the timeout.</summary>
    public async Task<ToolRunResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellation = default, string? workingDirectory = null)
    {
        using var tool = OpenVerified();
        var start = new ProcessStartInfo(tool.Path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(tool.Path)!
        };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new ToolException(ToolException.NotInstalled, "The tool did not start.");
        try { process.StandardInput.Close(); } catch (IOException) { }
        var output = ReadTailAsync(process.StandardOutput); var error = ReadTailAsync(process.StandardError);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(timeout);
        bool timedOut = false;
        try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
            if (cancellation.IsCancellationRequested) throw;
            timedOut = true;
        }
        string o, e;
        try { o = await output.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); e = await error.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { o = ""; e = ""; }
        return new ToolRunResult(timedOut ? -1 : process.ExitCode, timedOut, o, e);
    }

    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        const int Max = 32 * 1024;
        var sb = new StringBuilder(); var buffer = new char[4096]; int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            sb.Append(buffer, 0, read);
            if (sb.Length > Max * 2) sb.Remove(0, sb.Length - Max);
        }
        if (sb.Length > Max) sb.Remove(0, sb.Length - Max);
        return sb.ToString();
    }
}

/// <summary>Argument lists for the helper tool, built from fixed templates. File names are the work folder's own fixed names; nothing the user or a site typed ends up in an argument.</summary>
public static class FfmpegCommands
{
    private static readonly string[] Prefix = { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" };

    /// <summary>Picture + sound tracks into one MP4 (or WebM/MKV): streams are copied, never re-encoded. AAC from transport streams or ADTS gets the ADTS-to-ASC filter.</summary>
    public static List<string> MuxTracks(string videoPath, string audioPath, string outputPath, bool audioIsAdts)
    {
        var args = new List<string>(Prefix) { "-i", videoPath, "-i", audioPath, "-map", "0:v:0", "-map", "1:a:0", "-c", "copy" };
        if (audioIsAdts) { args.Add("-bsf:a"); args.Add("aac_adtstoasc"); }
        if (outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || outputPath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)) { args.Add("-movflags"); args.Add("+faststart"); }
        args.Add(outputPath);
        return args;
    }

    /// <summary>One transport stream (or ADTS audio) rewrapped as MP4/M4A.</summary>
    public static List<string> Rewrap(string inputPath, string outputPath, bool audioOnly, bool adts)
    {
        var args = new List<string>(Prefix) { "-i", inputPath };
        if (audioOnly) { args.Add("-vn"); } else { args.Add("-map"); args.Add("0:v?"); args.Add("-map"); args.Add("0:a?"); }
        args.Add("-c"); args.Add("copy");
        if (adts) { args.Add("-bsf:a"); args.Add("aac_adtstoasc"); }
        args.Add("-movflags"); args.Add("+faststart"); args.Add(outputPath);
        return args;
    }

    /// <summary>Only reads the file's header; ffmpeg prints "Duration:" and exits with an error code because no output is named.</summary>
    public static List<string> Probe(string inputPath) => new(new[] { "-hide_banner", "-nostdin", "-i", inputPath });

    /// <summary>Seconds from "Duration: 00:01:02.50" in ffmpeg's text, or null.</summary>
    public static double? ParseDuration(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 3600.0 + int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) * 60 + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
