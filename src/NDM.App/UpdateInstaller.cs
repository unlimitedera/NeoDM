using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

public sealed class UpdateException : Exception { public UpdateException(string message) : base(message) { } }

public enum LaunchResult { Started, UacDeclined, Failed }

/// <summary>Starts the installer. Behind an interface so tests never run msiexec.</summary>
public interface IInstallerLauncher { LaunchResult Launch(string msiPath); }

/// <summary>Runs <c>msiexec /i &lt;msi&gt; /passive</c> elevated (UAC prompt). The MSI itself closes the running NeoDM and starts the new one.</summary>
public sealed class MsiexecLauncher : IInstallerLauncher
{
    public static ProcessStartInfo BuildStartInfo(string msiPath) => new("msiexec.exe", $"/i \"{msiPath}\" /passive") { UseShellExecute = true, Verb = "runas" };
    public LaunchResult Launch(string msiPath)
    {
        try { Process.Start(BuildStartInfo(msiPath))?.Dispose(); return LaunchResult.Started; }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return LaunchResult.UacDeclined; } // ERROR_CANCELLED: the user said No to the UAC prompt
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return LaunchResult.Failed; }
    }
}

/// <summary>
/// Downloads the release installer, checks it against the release's SHA256SUMS.txt and refuses anything that does not match.
/// The MSI is not Authenticode-signed, so the trust anchor is the hash published in the same GitHub release, fetched over HTTPS.
/// </summary>
public sealed class UpdateInstaller
{
    private static readonly byte[] OleSignature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
    private readonly string directory, version;
    private readonly Func<ProxyOptions?> proxy;
    private readonly Func<HttpMessageHandler?> handlerFactory;
    public IInstallerLauncher Launcher { get; set; }
    public static string DefaultDirectory => Path.Combine(App.StateDirectory, "updates");

    public UpdateInstaller(string directory, string version, Func<ProxyOptions?>? proxy = null, Func<HttpMessageHandler?>? handlerFactory = null, IInstallerLauncher? launcher = null)
    {
        this.directory = directory; this.version = version; this.proxy = proxy ?? (() => null); this.handlerFactory = handlerFactory ?? (() => null); Launcher = launcher ?? new MsiexecLauncher();
    }

    /// <summary>name -> lowercase hex digest from a SHA256SUMS file ("hex *name" or "hex  name").</summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var m = Regex.Match(raw.Trim(), @"^([0-9a-fA-F]{64})\s+[*]?(?:\./)?(.+)$");
            if (m.Success) map[Path.GetFileName(m.Groups[2].Value.Trim())] = m.Groups[1].Value.ToLowerInvariant();
        }
        return map;
    }

    public static bool LooksLikeMsi(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[OleSignature.Length];
            return stream.Length > 4096 && stream.Read(head, 0, head.Length) == head.Length && head.AsSpan().SequenceEqual(OleSignature);
        }
        catch (IOException) { return false; }
    }

    /// <summary>Removes partial and old update files. Files that are in use (installer still running) are left alone.</summary>
    public void CleanUp()
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Downloads and verifies. Returns the path of the verified MSI; throws <see cref="UpdateException"/> with a user-readable reason.</summary>
    public async Task<string> DownloadAndVerifyAsync(ReleaseInfo release, IProgress<(long Done, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (release.Installer is null) throw new UpdateException(Loc.T("update.err.no_installer"));
        if (release.Checksums is null) throw new UpdateException(Loc.T("update.err.no_sums"));
        string name = Path.GetFileName(release.Installer.Name);
        if (name != release.Installer.Name || name.Length == 0) throw new UpdateException(Loc.T("update.err.bad_name"));
        Directory.CreateDirectory(directory);
        CleanUp();
        string partial = Path.Combine(directory, name + ".partial"), final = Path.Combine(directory, name);
        try
        {
            using var client = UpdateChecker.CreateClient(handlerFactory(), proxy(), TimeSpan.FromMinutes(30), version);
            string sumsText;
            using (var response = await client.GetAsync(release.Checksums.Url, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new UpdateException(Loc.T("update.err.sums_download", (int)response.StatusCode));
                sumsText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            if (sumsText.Length > 1_000_000) throw new UpdateException(Loc.T("update.err.sums_invalid"));
            if (!ParseChecksums(sumsText).TryGetValue(name, out var expected)) throw new UpdateException(Loc.T("update.err.sums_entry", name));

            using (var response = await client.GetAsync(release.Installer.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new UpdateException(Loc.T("update.err.installer_download", (int)response.StatusCode));
                long total = response.Content.Headers.ContentLength ?? release.Installer.Size;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920]; long done = 0; int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read; progress?.Report((done, total));
                }
            }
            string actual;
            await using (var check = File.OpenRead(partial)) actual = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new UpdateException(Loc.T("update.err.hash_mismatch"));
            if (!LooksLikeMsi(partial)) throw new UpdateException(Loc.T("update.err.not_msi"));
            File.Move(partial, final, true);
            return final;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or ProxyException or InvalidOperationException)
        {
            throw new UpdateException(Loc.T("update.err.download_failed", ex is HttpRequestException ? Loc.T("update.err.network") : ErrorMessages.OneLine(ex.Message)));
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
