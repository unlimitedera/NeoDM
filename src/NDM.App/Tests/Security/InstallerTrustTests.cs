using System.IO;
using System.Security.Cryptography;
using NDM.App;
using NDM.Core;

internal static partial class SecurityTests
{
    private sealed class Launcher : IInstallerLauncher
    {
        public LaunchResult Result = LaunchResult.Started; public Action<string>? OnLaunch; public int Launches;
        public LaunchResult Launch(string msiPath) { Launches++; OnLaunch?.Invoke(msiPath); return Result; }
    }

    /// <summary>A loopback "release": serves the sums, a signature for them, and the installer bytes or file.</summary>
    private sealed class Release : IDisposable
    {
        public string Sums = ""; public Func<string> Signature = () => ""; public byte[]? Msi; public string? MsiFile; public int SumsStatus = 200, SigStatus = 200;
        public LocalServer Server { get; }
        public Release() => Server = new LocalServer(ctx =>
        {
            string p = ctx.Request.Url!.AbsolutePath;
            if (p.EndsWith("SHA256SUMS.txt.sig")) { if (SigStatus != 200) Reply(ctx, SigStatus, "no"); else Reply(ctx, 200, Signature()); }
            else if (p.EndsWith("SHA256SUMS.txt")) { if (SumsStatus != 200) Reply(ctx, SumsStatus, "no"); else Reply(ctx, 200, Sums); }
            else if (MsiFile is not null) ReplyFile(ctx, MsiFile);
            else Reply(ctx, 200, Msi ?? Array.Empty<byte>());
        });
        public void Serve(string name, byte[] msi) { Msi = msi; MsiFile = null; Sums = $"{Hex(msi)} *{name}\n"; Signature = () => TestTrust.Sign(Sums); }
        public void ServeFile(string name, string path) { MsiFile = path; Msi = null; Sums = $"{HexFile(path)} *{name}\n"; Signature = () => TestTrust.Sign(Sums); }
        public void Dispose() => Server.Dispose();
    }

    /// <summary>True when the operation was refused by the file system (sharing violation or access denied).</summary>
    private static bool Blocked(Action action) { try { action(); return false; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; } }

    private static void InstallerTrustTests(string root)
    {
        // ---- 1: the verified file cannot be swapped between the check and msiexec
        string dir = Path.Combine(root, "trust-upd");
        using (var rel = new Release())
        {
            var msi = FakeMsiBytes(11); rel.Serve("NeoDM-Setup-1.5.3.msi", msi);
            var release = Rel(rel.Server.Url, "1.5.3", "NeoDM-Setup-1.5.3.msi", msi.Length);
            var launcher = new Launcher();
            var installer = TestTrust.Installer(dir, "1.5.2", launcher, reader: TestTrust.FakeMsi("1.5.3"));
            var verified = Sync(installer.DownloadAndVerifyAsync(release));
            string path = verified.Path;
            Check(File.ReadAllBytes(path).SequenceEqual(msi) && verified.Sha256 == Hex(msi) && verified.Rehash() == Hex(msi), "TOCTOU: the verified installer is intact and hashes through the held handle");
            var attacker = new (string What, Action Try)[]
            {
                ("overwrite with another installer", () => File.WriteAllBytes(path, FakeMsiBytes(99))),
                ("append bytes", () => File.AppendAllText(path, "x")),
                ("open for writing", () => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose()),
                ("open for reading and writing", () => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete).Dispose()),
                ("truncate", () => new FileStream(path, FileMode.Truncate).Dispose()),
                ("delete the file", () => File.Delete(path)),
                ("rename the file away", () => File.Move(path, path + ".old")),
                ("replace it by moving another file over it", () => { var other = path + ".new"; File.WriteAllBytes(other, FakeMsiBytes(98)); try { File.Move(other, path, true); } finally { File.Delete(other); } }),
                ("rename the folder", () => Directory.Move(Path.GetDirectoryName(path)!, Path.GetDirectoryName(path)! + "-x")),
                ("delete the folder", () => Directory.Delete(Path.GetDirectoryName(path)!, true)),
            };
            foreach (var (what, attempt) in attacker)
                Check(Blocked(attempt), "TOCTOU: after verification, to " + what + " fails");
            Check(File.ReadAllBytes(path).SequenceEqual(msi) && verified.Rehash() == Hex(msi), "TOCTOU: every swap attempt left the file as it was");
            // while msiexec starts: the launcher can read the file, but not change it
            bool readable = false, writable = true;
            launcher.OnLaunch = p => { readable = File.ReadAllBytes(p).Length == msi.Length; writable = !Blocked(() => File.WriteAllBytes(p, FakeMsiBytes(5))); };
            var result = installer.Launch(verified);
            Check(result == LaunchResult.Started && launcher.Launches == 1 && readable && !writable, "TOCTOU: msiexec can read the held file while it cannot be written during the launch");
            Check(Blocked(() => File.WriteAllBytes(path, FakeMsiBytes(6))), "TOCTOU: the file stays protected after msiexec has started (held until NeoDM exits)");
            var failing = new Launcher { Result = LaunchResult.UacDeclined };
            var second = TestTrust.Installer(dir, "1.5.2", failing, reader: TestTrust.FakeMsi("1.5.3"));
            var v2 = Sync(second.DownloadAndVerifyAsync(release)); string path2 = v2.Path;
            Check(second.Launch(v2) == LaunchResult.UacDeclined && failing.Launches == 1, "a declined UAC prompt is reported");
            File.Delete(path2); Check(!File.Exists(path2), "TOCTOU: when the installer was not started the handle is released");
        }

        // ---- 2: signature in the update flow
        string sdir = Path.Combine(root, "sig-upd");
        using (var rel = new Release())
        {
            var msi = FakeMsiBytes(12); rel.Serve("NeoDM-Setup-1.5.3.msi", msi);
            var release = Rel(rel.Server.Url, "1.5.3", "NeoDM-Setup-1.5.3.msi", msi.Length);
            UpdateInstaller Make(ECDsa? signer = null) => TestTrust.Installer(sdir, "1.5.2", reader: TestTrust.FakeMsi("1.5.3"), signer: signer);
            using (var accepted = Sync(Make().DownloadAndVerifyAsync(release))) Check(accepted.Sha256 == Hex(msi), "Signed release: valid signature, hash and package are accepted");
            Make().CleanUp();
            void NothingInstalled(string what) => Check(!Directory.Exists(sdir) || !Directory.EnumerateFiles(sdir, "*.msi", SearchOption.AllDirectories).Any(), what + ": no installer left on disk");
            int msiRequests() => rel.Server.CountOf(".msi");
            // missing .sig asset: refused before anything is downloaded
            int before = rel.Server.Count;
            Refuses(() => Make().DownloadAndVerifyAsync(release with { Signature = null }), "SHA256SUMS.txt.sig", "Missing signature asset"); NothingInstalled("Missing signature asset");
            Check(rel.Server.Count == before, "Missing signature asset: nothing was requested at all");
            // asset listed but the server has none
            rel.SigStatus = 404; int msiBefore = msiRequests();
            Refuses(() => Make().DownloadAndVerifyAsync(release), "SHA256SUMS.txt.sig could not be downloaded", "Signature download fails (404)"); NothingInstalled("Signature 404");
            Check(msiRequests() == msiBefore, "Signature 404: the installer was never requested"); rel.SigStatus = 200;
            // tampered sums (signature is for the original text)
            string signed = rel.Sums; rel.Signature = () => TestTrust.Sign(signed); rel.Sums = signed.Replace(Hex(msi)[..4], "0000");
            Refuses(() => Make().DownloadAndVerifyAsync(release), "signature of SHA256SUMS.txt is not valid", "Tampered SHA256SUMS.txt"); NothingInstalled("Tampered sums");
            Check(msiRequests() == msiBefore, "Tampered sums: the installer was never requested");
            // an attacker swaps both the installer and the hash list but cannot sign
            var evil = FakeMsiBytes(66); rel.Msi = evil; rel.Sums = $"{Hex(evil)} *NeoDM-Setup-1.5.3.msi\n"; rel.Signature = () => TestTrust.Sign(signed);
            Refuses(() => Make().DownloadAndVerifyAsync(release), "not valid", "Attacker replaces installer and hash list"); NothingInstalled("Replaced installer");
            using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            rel.Signature = () => TestTrust.Sign(rel.Sums, attackerKey);
            Refuses(() => Make().DownloadAndVerifyAsync(release), "not valid", "Signed with the attacker's own key"); NothingInstalled("Wrong key");
            // tampered signature
            rel.Serve("NeoDM-Setup-1.5.3.msi", msi); rel.Signature = () => { var raw = Convert.FromBase64String(TestTrust.Sign(rel.Sums)); raw[10] ^= 0x01; return Convert.ToBase64String(raw); };
            Refuses(() => Make().DownloadAndVerifyAsync(release), "not valid", "Tampered signature"); NothingInstalled("Tampered signature");
            rel.Signature = () => "";
            Refuses(() => Make().DownloadAndVerifyAsync(release), "not valid", "Empty signature file"); NothingInstalled("Empty signature");
            rel.Signature = () => "????";
            Refuses(() => Make().DownloadAndVerifyAsync(release), "not valid", "Unreadable signature file");
            Check(msiRequests() == msiBefore, "No invalid signature ever led to an installer download");
            // the app's own key does not accept the test key
            rel.Serve("NeoDM-Setup-1.5.3.msi", msi);
            Refuses(() => new UpdateInstaller(sdir, "1.5.2", hosts: TestTrust.Loopback(), readMsi: TestTrust.FakeMsi("1.5.3")).DownloadAndVerifyAsync(release), "not valid", "A signature that is valid for a different key than the one built into NeoDM");
            // hash still has to match the signed list
            rel.Msi = FakeMsiBytes(77);
            Refuses(() => Make().DownloadAndVerifyAsync(release), "does not match", "Signed list, different installer");
        }

        // ---- 3: package identity with the real installers in artifacts\
        string old148 = Fixture("NeoDM-Setup-1.4.8.msi"), cur152 = Fixture("NeoDM-Setup-1.5.2.msi");
        var id148 = MsiDatabase.Read(old148); var id152 = MsiDatabase.Read(cur152);
        Check(id148 is { ProductVersion: "1.4.8" } && id152 is { ProductVersion: "1.5.2" }, $"MSI Property table: ProductVersion read from the real installers ({id148?.ProductVersion}, {id152?.ProductVersion})");
        Check(string.Equals(id148!.UpgradeCode, UpdateTrust.ProductUpgradeCode, StringComparison.OrdinalIgnoreCase) && string.Equals(id152!.UpgradeCode, UpdateTrust.ProductUpgradeCode, StringComparison.OrdinalIgnoreCase), "MSI Property table: UpgradeCode of the real installers equals the constant in the app");
        var wxs = File.ReadAllText(Path.Combine(RepoRoot, "installer", "NeoDM.wxs"));
        Check(wxs.Contains("UpgradeCode=\"" + UpdateTrust.ProductUpgradeCode.Trim('{', '}') + "\""), "The constant matches installer/NeoDM.wxs");
        Check(MsiDatabase.Read(Path.Combine(root, "missing.msi")) is null, "MSI reader: a missing file gives null");
        string garbage = Path.Combine(root, "garbage.msi"); File.WriteAllBytes(garbage, FakeMsiBytes(3)); Check(MsiDatabase.Read(garbage) is null, "MSI reader: an OLE-looking file that is not an installer gives null");
        string notOle = Path.Combine(root, "plain.msi"); File.WriteAllText(notOle, "hello"); Check(MsiDatabase.Read(notOle) is null, "MSI reader: a text file gives null");

        string rdir = Path.Combine(root, "real-upd");
        using (var rel = new Release())
        {
            rel.ServeFile("NeoDM-Setup-1.5.2.msi", cur152);
            long size = new FileInfo(cur152).Length;
            var release = Rel(rel.Server.Url, "1.5.2", "NeoDM-Setup-1.5.2.msi", size);
            // the real thing: 1.4.8 running, 1.5.2 released
            var okInstaller = TestTrust.Installer(rdir, "1.4.8");
            var good = Sync(okInstaller.DownloadAndVerifyAsync(release));
            Check(good.Identity.ProductVersion == "1.5.2" && good.Sha256 == HexFile(cur152), "Real installer 1.5.2 over 1.4.8: signature, hash, ProductVersion and UpgradeCode all pass");
            good.Dispose();
            // downgrade / same version
            Refuses(() => TestTrust.Installer(rdir, "1.5.2").DownloadAndVerifyAsync(release), "is not newer", "Same version as running");
            Refuses(() => TestTrust.Installer(rdir, "1.6.0").DownloadAndVerifyAsync(release), "is not newer", "Downgrade (running 1.6.0, package 1.5.2)");
            // a package whose version differs from the release tag (a newer release name carrying an older package)
            string older = Path.Combine(rdir, "x"); 
            rel.ServeFile("NeoDM-Setup-1.5.3.msi", cur152);
            var lying = Rel(rel.Server.Url, "1.5.3", "NeoDM-Setup-1.5.3.msi", size);
            Refuses(() => TestTrust.Installer(rdir, "1.4.8").DownloadAndVerifyAsync(lying), "version 1.5.2, but the release is 1.5.3", "Package says 1.5.2 but the release is 1.5.3");
            Check(!Directory.EnumerateFiles(rdir, "*", SearchOption.AllDirectories).Any(), "Mismatching package: deleted");
            // an old release published as new (rollback attack): 1.4.8 package under a 1.5.3 tag
            rel.ServeFile("NeoDM-Setup-1.5.3.msi", old148);
            Refuses(() => TestTrust.Installer(rdir, "1.5.2").DownloadAndVerifyAsync(Rel(rel.Server.Url, "1.5.3", "NeoDM-Setup-1.5.3.msi", new FileInfo(old148).Length)), "version 1.4.8, but the release is 1.5.3", "Rollback: old package under a new tag");
            // 1.4.8 as its own release is fine when running 1.4.7
            rel.ServeFile("NeoDM-Setup-1.4.8.msi", old148);
            using (var v = Sync(TestTrust.Installer(rdir, "1.4.7").DownloadAndVerifyAsync(Rel(rel.Server.Url, "1.4.8", "NeoDM-Setup-1.4.8.msi", new FileInfo(old148).Length))))
                Check(v.Identity.ProductVersion == "1.4.8", "Real installer 1.4.8 over 1.4.7 is accepted");
            // another product's package (wrong UpgradeCode)
            rel.Serve("NeoDM-Setup-1.5.3.msi", FakeMsiBytes(21));
            Refuses(() => TestTrust.Installer(rdir, "1.5.2", reader: TestTrust.FakeMsi("1.5.3", "{11111111-2222-3333-4444-555555555555}")).DownloadAndVerifyAsync(Rel(rel.Server.Url, "1.5.3", "NeoDM-Setup-1.5.3.msi", 120_000)), "different product", "Wrong UpgradeCode");
            Refuses(() => TestTrust.Installer(rdir, "1.5.2", reader: _ => null).DownloadAndVerifyAsync(Rel(rel.Server.Url, "1.5.3", "NeoDM-Setup-1.5.3.msi", 120_000)), "could not be read", "Unreadable package");
            Refuses(() => TestTrust.Installer(rdir, "1.5.2").DownloadAndVerifyAsync(Rel(rel.Server.Url, "1.5.3", "NeoDM-Setup-1.5.3.msi", 120_000)), "could not be read", "An OLE file that is not an installer, real reader");
            foreach (var bad in new[] { "1.5", "1.5.3.1", "1.5.x", "2.0.0", "1.5.3-rc" })
                Refuses(() => TestTrust.Installer(rdir, "1.5.2", reader: TestTrust.FakeMsi(bad)).DownloadAndVerifyAsync(Rel(rel.Server.Url, "1.5.3", "NeoDM-Setup-1.5.3.msi", 120_000)), "the release is 1.5.3", "ProductVersion '" + bad + "'");
            Check(!Directory.EnumerateFiles(rdir, "*", SearchOption.AllDirectories).Any(), "Rejected packages leave nothing behind");
        }
    }
}
