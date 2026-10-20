using System.Security.Cryptography;
using System.Text;
using NDM.App;
using NDM.Core;

/// <summary>Test-side release signer and installer factory: the app trusts the embedded public key, tests trust a key made here.</summary>
internal static class TestTrust
{
    public static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public static string PublicSpki => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
    public static string Sign(string sums) => Sign(Encoding.UTF8.GetBytes(sums));
    public static string Sign(string sums, ECDsa key) => Sign(Encoding.UTF8.GetBytes(sums), key);
    public static string Sign(byte[] sums, ECDsa? key = null) => Convert.ToBase64String((key ?? Key).SignData(sums, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    /// <summary>Plain http on 127.0.0.1 may start a download; redirects only where <paramref name="redirect"/> says (default: nowhere).</summary>
    public static UpdateHostPolicy Loopback(Func<Uri, bool>? redirect = null) => new(u => u.Scheme == "http" && u.Host == "127.0.0.1", redirect ?? (_ => false));
    public static Func<string, MsiIdentity?> FakeMsi(string version, string? upgradeCode = null) => _ => new MsiIdentity(version, upgradeCode ?? UpdateTrust.ProductUpgradeCode, "{00000000-0000-0000-0000-000000000000}");
    public static UpdateInstaller Installer(string dir, string running, IInstallerLauncher? launcher = null, UpdateHostPolicy? hosts = null, Func<string, MsiIdentity?>? reader = null, ECDsa? signer = null) =>
        new(dir, running, launcher: launcher, hosts: hosts ?? Loopback(), publicKeySpki: signer is null ? PublicSpki : Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()), readMsi: reader);
}
