using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NDM.App;

/// <summary>Everything the updater trusts, in one place: the release signing key, the download hosts and the identity of the installer package.</summary>
public static class UpdateTrust
{
    /// <summary>ECDSA P-256 public key (SubjectPublicKeyInfo, base64) that signs SHA256SUMS.txt. The private half lives only on the release machine (keys/neodm-update-signing.pem, never committed). See docs/UPDATES.md.</summary>
    public const string PublicKeySpkiBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEEltuvJz045LRVCrgAfZfYMx1ZtzNDLOvNkX4BEcZiTgPTGp10El7guIeAjOJhBtw7mBDZ7smjph73KlhLrhIIA==";
    /// <summary>UpgradeCode of the NeoDM installer (installer/NeoDM.wxs). An update package with another code is refused.</summary>
    public const string ProductUpgradeCode = "{6B1F0C7E-4A52-4C0B-9B0E-2D5C7A1E9F31}";
    public const long MaxInstallerBytes = 200L * 1024 * 1024;
    public const int MaxSumsBytes = 1_000_000, MaxSignatureBytes = 4096;

    /// <summary>True when <paramref name="signatureBase64"/> is a valid ECDSA P-256 / SHA-256 signature (DER) of <paramref name="data"/> under the key. Never throws.</summary>
    public static bool VerifySignature(byte[] data, string? signatureBase64, string publicKeySpkiBase64 = PublicKeySpkiBase64)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(signatureBase64)) return false;
            var signature = Convert.FromBase64String(signatureBase64.Trim());
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    private static bool PlainHttps(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>The only place an update download may start: https://github.com/unlimitedera/NeoDM/releases/download/...</summary>
    public static bool IsPinnedAssetUrl(Uri uri) =>
        PlainHttps(uri) && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/" + UpdateChecker.Repository + "/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hosts GitHub redirects release downloads to. https only at every hop.</summary>
    public static bool IsPinnedRedirectTarget(Uri uri) =>
        PlainHttps(uri) && (uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || IsPinnedAssetUrl(uri));

    public static readonly UpdateHostPolicy PinnedHosts = new(IsPinnedAssetUrl, IsPinnedRedirectTarget);
}

/// <summary>Which URLs an update download may start at and be redirected to. Tests pass a loopback policy; the app always uses <see cref="UpdateTrust.PinnedHosts"/>.</summary>
public sealed record UpdateHostPolicy(Func<Uri, bool> Initial, Func<Uri, bool> Redirect);

public sealed record MsiIdentity(string ProductVersion, string UpgradeCode, string ProductCode);

/// <summary>Reads the Property table of an MSI with msi.dll (no extra packages).</summary>
public static class MsiDatabase
{
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiDatabaseOpenViewW(IntPtr database, string query, out IntPtr view);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiViewExecute(IntPtr view, IntPtr record);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiRecordGetStringW(IntPtr record, uint field, StringBuilder value, ref uint size);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiCloseHandle(IntPtr handle);

    private static string? FirstString(IntPtr db, string sql)
    {
        if (MsiDatabaseOpenViewW(db, sql, out var view) != 0) return null;
        try
        {
            if (MsiViewExecute(view, IntPtr.Zero) != 0 || MsiViewFetch(view, out var record) != 0) return null;
            try
            {
                uint size = 0; var sb = new StringBuilder();
                MsiRecordGetStringW(record, 1, sb, ref size); // ERROR_MORE_DATA fills size
                sb.Capacity = (int)size + 2; size++;
                return MsiRecordGetStringW(record, 1, sb, ref size) == 0 ? sb.ToString() : null;
            }
            finally { MsiCloseHandle(record); }
        }
        finally { MsiCloseHandle(view); }
    }

    /// <summary>First column of the first row of a read-only query on the package (for example a sequence condition); null when there is none.</summary>
    public static string? QueryString(string path, string sql)
    {
        if (MsiOpenDatabaseW(path, IntPtr.Zero, out var db) != 0) return null;
        try { return FirstString(db, sql); } finally { MsiCloseHandle(db); }
    }

    /// <summary>ProductVersion, UpgradeCode and ProductCode, or null when the file is not a readable installer database.</summary>
    public static MsiIdentity? Read(string path)
    {
        if (MsiOpenDatabaseW(path, IntPtr.Zero, out var db) != 0) return null; // persist 0 = MSIDBOPEN_READONLY
        try
        {
            string? Property(string name) => FirstString(db, "SELECT `Value` FROM `Property` WHERE `Property`='" + name + "'");
            var version = Property("ProductVersion"); var code = Property("UpgradeCode"); var product = Property("ProductCode");
            return version is null || code is null ? null : new MsiIdentity(version, code, product ?? "");
        }
        finally { MsiCloseHandle(db); }
    }
}
