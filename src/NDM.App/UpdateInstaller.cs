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

/// <summary>An installer that passed every check and is still held open: no other process can write to, replace, rename or delete it while this object lives.</summary>
public sealed class VerifiedInstaller : IDisposable
{
    private readonly FileStream held;
    public string Path { get; }
    public string Sha256 { get; }
    public MsiIdentity Identity { get; }
    internal VerifiedInstaller(string path, FileStream held, string sha256, MsiIdentity identity) { Path = path; this.held = held; Sha256 = sha256; Identity = identity; }
    /// <summary>Hashes the file again through the held handle (not through the path).</summary>
    public string Rehash()
    {
        held.Position = 0;
        return Convert.ToHexString(SHA256.HashData(held)).ToLowerInvariant();
    }
    public void Dispose() => held.Dispose();
}

/// <summary>
/// Downloads the release installer and refuses anything that is not exactly what the release signer published.
/// Trust chain: SHA256SUMS.txt must carry a valid ECDSA signature (key embedded in the app) -> the installer hash comes from that file ->
/// the installer is hashed through a handle that stays open (so it cannot be swapped before msiexec reads it) -> its ProductVersion/UpgradeCode
/// must match the release and be newer than the running version. Downloads only start at the pinned GitHub release URL and follow redirects only to GitHub's asset hosts over https.
/// </summary>
public sealed class UpdateInstaller
{
    private static readonly byte[] OleSignature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
    private const int MaxRedirects = 5;
    private readonly string directory, version, publicKey;
    private readonly Func<ProxyOptions?> proxy;
    private readonly Func<HttpMessageHandler?> handlerFactory;
    private readonly UpdateHostPolicy hosts;
    private readonly Func<string, MsiIdentity?> readMsi;
    private VerifiedInstaller? launched;
    public IInstallerLauncher Launcher { get; set; }
    public static string DefaultDirectory => Path.Combine(App.StateDirectory, "updates");

    /// <param name="hosts">Test seam; the app always uses the pinned GitHub hosts.</param>
    /// <param name="publicKeySpki">Test seam; the app always uses the embedded release key.</param>
    /// <param name="readMsi">Test seam for the MSI property reader.</param>
    public UpdateInstaller(string directory, string version, Func<ProxyOptions?>? proxy = null, Func<HttpMessageHandler?>? handlerFactory = null, IInstallerLauncher? launcher = null,
        UpdateHostPolicy? hosts = null, string? publicKeySpki = null, Func<string, MsiIdentity?>? readMsi = null)
    {
        this.directory = directory; this.version = version; this.proxy = proxy ?? (() => null); this.handlerFactory = handlerFactory ?? (() => null); Launcher = launcher ?? new MsiexecLauncher();
        this.hosts = hosts ?? UpdateTrust.PinnedHosts; publicKey = publicKeySpki ?? UpdateTrust.PublicKeySpkiBase64; this.readMsi = readMsi ?? MsiDatabase.Read;
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
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return LooksLikeMsi(stream); }
        catch (IOException) { return false; }
    }
    public static bool LooksLikeMsi(FileStream stream)
    {
        var head = new byte[OleSignature.Length];
        stream.Position = 0;
        return stream.Length > 4096 && stream.Read(head, 0, head.Length) == head.Length && head.AsSpan().SequenceEqual(OleSignature);
    }

    /// <summary>Removes partial and old update files and the per-download folders. Files that are in use (installer still running) are left alone.</summary>
    public void CleanUp()
    {
        if (!Directory.Exists(directory)) return;
        foreach (var dir in Directory.EnumerateDirectories(directory))
        {
            try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>GET with every redirect hop checked against the host policy (https only). Returns the final successful-or-error response; the caller disposes it.</summary>
    private async Task<HttpResponseMessage> GetPinnedAsync(HttpClient client, string url, HttpCompletionOption option, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !hosts.Initial(uri)) throw new UpdateException(Loc.T("update.err.host", Uri.TryCreate(url, UriKind.Absolute, out var shown) ? shown.Host : "?"));
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            var response = await client.GetAsync(uri, option, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!hosts.Redirect(next)) throw new UpdateException(Loc.T("update.err.redirect", next.Scheme, next.Host));
                uri = next; continue;
            }
            if (response.RequestMessage?.RequestUri is { } final && final != uri && !hosts.Redirect(final) && !hosts.Initial(final)) { response.Dispose(); throw new UpdateException(Loc.T("update.err.redirect", final.Scheme, final.Host)); }
            return response;
        }
        throw new UpdateException(Loc.T("update.err.redirect", "?", "too many"));
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, int cap, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > cap) throw new UpdateException(Loc.T("update.err.sums_invalid"));
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > cap) throw new UpdateException(Loc.T("update.err.sums_invalid"));
        }
        return buffer.ToArray();
    }

    private static string CreateDownloadFolder(string root)
    {
        string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try // only this user (and the administrators who elevate the installer) may touch it; best effort, the held handle is the real protection
        {
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var inherit = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            void Grant(System.Security.Principal.SecurityIdentifier sid) => security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl, inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            Grant(System.Security.Principal.WindowsIdentity.GetCurrent().User!);
            Grant(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null));
            Grant(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null));
            new DirectoryInfo(folder).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PlatformNotSupportedException) { }
        return folder;
    }

    /// <summary>Downloads and verifies. Returns the held, verified installer; throws <see cref="UpdateException"/> with a user-readable reason. Dispose the result if the installer is not started.</summary>
    public async Task<VerifiedInstaller> DownloadAndVerifyAsync(ReleaseInfo release, IProgress<(long Done, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (release.Installer is null) throw new UpdateException(Loc.T("update.err.no_installer"));
        if (release.Checksums is null) throw new UpdateException(Loc.T("update.err.no_sums"));
        if (release.Signature is null) throw new UpdateException(Loc.T("update.err.no_sig"));
        string name = Path.GetFileName(release.Installer.Name);
        if (name != release.Installer.Name || name.Length == 0) throw new UpdateException(Loc.T("update.err.bad_name"));
        Directory.CreateDirectory(directory);
        CleanUp();
        string folder = CreateDownloadFolder(directory);
        string partial = Path.Combine(folder, name + ".partial"), final = Path.Combine(folder, name);
        FileStream? held = null; bool keep = false;
        try
        {
            using var client = UpdateChecker.CreateClient(handlerFactory(), proxy(), TimeSpan.FromMinutes(30), version, followRedirects: false);
            byte[] sumsBytes, sigBytes;
            using (var response = await GetPinnedAsync(client, release.Checksums.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new UpdateException(Loc.T("update.err.sums_download", (int)response.StatusCode));
                sumsBytes = await ReadCappedAsync(response, UpdateTrust.MaxSumsBytes, cancellationToken).ConfigureAwait(false);
            }
            using (var response = await GetPinnedAsync(client, release.Signature.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new UpdateException(Loc.T("update.err.sig_download", (int)response.StatusCode));
                sigBytes = await ReadCappedAsync(response, UpdateTrust.MaxSignatureBytes, cancellationToken).ConfigureAwait(false);
            }
            if (!UpdateTrust.VerifySignature(sumsBytes, System.Text.Encoding.ASCII.GetString(sigBytes), publicKey)) throw new UpdateException(Loc.T("update.err.sig_invalid"));
            if (!ParseChecksums(System.Text.Encoding.UTF8.GetString(sumsBytes)).TryGetValue(name, out var expected)) throw new UpdateException(Loc.T("update.err.sums_entry", name));

            long cap = release.Installer.Size > 0 ? Math.Min(release.Installer.Size, UpdateTrust.MaxInstallerBytes) : UpdateTrust.MaxInstallerBytes;
            using (var response = await GetPinnedAsync(client, release.Installer.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new UpdateException(Loc.T("update.err.installer_download", (int)response.StatusCode));
                if (response.Content.Headers.ContentLength is { } declared && declared > cap) throw new UpdateException(Loc.T("update.err.too_large", cap / 1048576));
                long total = response.Content.Headers.ContentLength ?? release.Installer.Size;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920]; long done = 0; int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > cap) throw new UpdateException(Loc.T("update.err.too_large", cap / 1048576));
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report((done, total));
                }
            }
            File.Move(partial, final, false);
            // From here on the file is verified THROUGH this handle: it denies other writers and deleters, so what was hashed is what msiexec will read.
            held = new FileStream(final, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(held, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new UpdateException(Loc.T("update.err.hash_mismatch"));
            if (!LooksLikeMsi(held)) throw new UpdateException(Loc.T("update.err.not_msi"));
            var identity = readMsi(final) ?? throw new UpdateException(Loc.T("update.err.msi_unreadable"));
            if (!string.Equals(identity.UpgradeCode, UpdateTrust.ProductUpgradeCode, StringComparison.OrdinalIgnoreCase)) throw new UpdateException(Loc.T("update.err.msi_product"));
            if (!Version.TryParse(identity.ProductVersion, out var packaged) || packaged.Revision > 0 || packaged.Build < 0 || new Version(packaged.Major, packaged.Minor, packaged.Build) != release.Version)
                throw new UpdateException(Loc.T("update.err.msi_version", identity.ProductVersion, release.VersionText));
            if (!SemVer.IsNewer(release.Version, SemVer.Running(version))) throw new UpdateException(Loc.T("update.err.msi_downgrade", release.VersionText, version));
            var verified = new VerifiedInstaller(final, held, actual, identity);
            keep = true;
            return verified;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or ProxyException or InvalidOperationException)
        {
            throw new UpdateException(Loc.T("update.err.download_failed", ex is HttpRequestException ? Loc.T("update.err.network") : ErrorMessages.OneLine(ex.Message)));
        }
        finally
        {
            if (!keep)
            {
                held?.Dispose();
                try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Re-checks the held file (hash through the handle) and starts msiexec. The handle stays open for the life of the process so the file cannot change under the installer; on a failed start it is released.</summary>
    public LaunchResult Launch(VerifiedInstaller installer)
    {
        if (!string.Equals(installer.Rehash(), installer.Sha256, StringComparison.Ordinal)) { installer.Dispose(); throw new UpdateException(Loc.T("update.err.hash_mismatch")); }
        var result = Launcher.Launch(installer.Path);
        if (result == LaunchResult.Started) launched = installer; else installer.Dispose();
        return result;
    }
}
