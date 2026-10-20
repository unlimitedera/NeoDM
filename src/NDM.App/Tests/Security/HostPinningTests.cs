using System.IO;
using System.Net;
using NDM.App;
using NDM.Core;

internal static partial class SecurityTests
{
    private static void HostPinningTests(string root)
    {
        bool Asset(string u) => UpdateTrust.IsPinnedAssetUrl(new Uri(u));
        bool Redirect(string u) => UpdateTrust.IsPinnedRedirectTarget(new Uri(u));
        Check(Asset("https://github.com/unlimitedera/NeoDM/releases/download/v1.5.3/NeoDM-Setup-1.5.3.msi") && Asset("https://GitHub.com/UnlimitedEra/neodm/releases/download/v1.5.3/SHA256SUMS.txt"), "Pinned: the official release download address is accepted");
        foreach (var bad in new[] { "http://github.com/unlimitedera/NeoDM/releases/download/v1.5.3/a.msi", "https://github.com/evil/NeoDM/releases/download/v1.5.3/a.msi", "https://github.com/unlimitedera/NeoDM/archive/main.zip",
            "https://github.com/unlimitedera/NeoDM-fork/releases/download/v1/a.msi", "https://github.com.evil.example/unlimitedera/NeoDM/releases/download/v1/a.msi", "https://evil.example/unlimitedera/NeoDM/releases/download/v1/a.msi",
            "https://objects.githubusercontent.com/x", "https://www.github.com/unlimitedera/NeoDM/releases/download/v1/a.msi", "https://github.com:8443/unlimitedera/NeoDM/releases/download/v1/a.msi",
            "https://user:pw@github.com/unlimitedera/NeoDM/releases/download/v1/a.msi", "https://api.github.com/repos/unlimitedera/NeoDM/releases/latest", "ftp://github.com/unlimitedera/NeoDM/releases/download/v1/a.msi" })
            Check(!Asset(bad), "Pinned: not a download start address: " + bad);
        Check(Redirect("https://objects.githubusercontent.com/github-production-release-asset/1/x?sig=1") && Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/1/x") && Redirect("https://github.com/unlimitedera/NeoDM/releases/download/v1/a.msi"), "Pinned: GitHub's release storage hosts are accepted as redirect targets");
        foreach (var bad in new[] { "http://objects.githubusercontent.com/x", "http://release-assets.githubusercontent.com/x", "https://raw.githubusercontent.com/x", "https://evil.githubusercontent.com/x", "https://objects.githubusercontent.com.evil.example/x",
            "https://evil.example/x", "https://objects.githubusercontent.com:444/x", "https://user@objects.githubusercontent.com/x", "https://127.0.0.1/x", "https://localhost/x" })
            Check(!Redirect(bad), "Pinned: not an allowed redirect target: " + bad);
        Check(UpdateTrust.MaxInstallerBytes == 200L * 1024 * 1024, "Download cap is 200 MB");
        // Parse: assets that are not under the official repository are not offered at all
        string fx = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "release-latest.json"));
        Check(UpdateChecker.Parse(fx.Replace("github.com/unlimitedera/NeoDM/releases/download/", "github.com/evil/NeoDM/releases/download/"))!.Installer is null, "Release parsing: an installer outside the official repository is never offered");
        Check(UpdateChecker.Parse(fx)!.Signature is { Name: "SHA256SUMS.txt.sig" } && UpdateChecker.Parse(fx.Replace("\"name\": \"SHA256SUMS.txt.sig\"", "\"name\": \"other.sig\""))!.Signature is null, "Release parsing: the signature asset is found by its name");

        // end to end: default (pinned) policy refuses the loopback address and requests nothing
        string dir = Path.Combine(root, "pin-upd");
        using (var rel = new Release())
        {
            var msi = FakeMsiBytes(31); rel.Serve("NeoDM-Setup-1.5.3.msi", msi);
            var release = Rel(rel.Server.Url, "1.5.3", "NeoDM-Setup-1.5.3.msi", msi.Length);
            var pinned = new UpdateInstaller(dir, "1.5.2", publicKeySpki: TestTrust.PublicSpki, readMsi: TestTrust.FakeMsi("1.5.3"));
            Refuses(() => pinned.DownloadAndVerifyAsync(release), "not an official NeoDM download address", "Default policy and a non-GitHub address");
            Check(rel.Server.Count == 0, "Default policy: no request reached the server");
            Refuses(() => pinned.DownloadAndVerifyAsync(release with { Checksums = release.Checksums! with { Url = "http://github.com/unlimitedera/NeoDM/releases/download/v1.5.3/SHA256SUMS.txt" } }), "not an official", "http:// even on github.com");
        }

        // redirects: every hop is checked, https only, host pinned
        string target = ""; int mode = 0; int redirects = 0;
        using var targetServer = new LocalServer(ctx => Reply(ctx, 200, Convert.FromBase64String(target)));
        using (var rel = new Release())
        using (var server = new LocalServer(ctx =>
        {
            string p = ctx.Request.Url!.AbsolutePath;
            if (p.EndsWith("SHA256SUMS.txt.sig")) { Reply(ctx, 200, rel.Signature()); return; }
            if (p.EndsWith("SHA256SUMS.txt")) { Reply(ctx, 200, rel.Sums); return; }
            Interlocked.Increment(ref redirects);
            string? location = mode switch { 1 => "http://127.0.0.1:" + targetServer.Port + "/m.msi", 2 => "https://evil.example/m.msi", 3 => "https://127.0.0.1:" + targetServer.Port + "/m.msi", 4 => "/dl/loop", 5 => "http://127.0.0.1:" + targetServer.Port + "/m.msi", _ => null };
            if (location is null) { Reply(ctx, 200, rel.Msi!); return; }
            ctx.Response.StatusCode = 302; ctx.Response.Headers["Location"] = location; ctx.Response.ContentLength64 = 0; ctx.Response.Close();
        }))
        {
            var msi = FakeMsiBytes(32); rel.Serve("NeoDM-Setup-1.5.3.msi", msi); target = Convert.ToBase64String(msi);
            // the policy's start hop is the first server; redirects may go nowhere, or (mode 5) to the second loopback server
            var release = Rel(server.Url, "1.5.3", "NeoDM-Setup-1.5.3.msi", msi.Length);
            UpdateInstaller With(Func<Uri, bool> redirectPolicy) => TestTrust.Installer(dir, "1.5.2", hosts: TestTrust.Loopback(redirectPolicy), reader: TestTrust.FakeMsi("1.5.3"));
            mode = 0; using (var plain = Sync(With(_ => false).DownloadAndVerifyAsync(release))) Check(plain.Sha256 == Hex(msi), "Redirects: a download without redirect works");
            mode = 1; Refuses(() => With(UpdateTrust.IsPinnedRedirectTarget).DownloadAndVerifyAsync(release), "not allowed (http://127.0.0.1", "Redirect to http://");
            Check(targetServer.Count == 0, "Redirect to http://: the redirect target was never contacted");
            mode = 2; Refuses(() => With(UpdateTrust.IsPinnedRedirectTarget).DownloadAndVerifyAsync(release), "not allowed (https://evil.example", "Redirect to another host over https");
            mode = 3; Refuses(() => With(UpdateTrust.IsPinnedRedirectTarget).DownloadAndVerifyAsync(release), "not allowed", "Redirect to https on a non-GitHub host");
            mode = 4; int before = redirects; Refuses(() => With(_ => true).DownloadAndVerifyAsync(release), "too many", "Redirect loop"); Check(redirects - before is > 1 and <= 8, "Redirect loop: bounded number of hops (" + (redirects - before) + ")");
            mode = 5; var allowed = Sync(With(u => u.Host == "127.0.0.1" && u.Port == targetServer.Port).DownloadAndVerifyAsync(release));
            Check(allowed.Sha256 == Hex(msi) && targetServer.Count == 1, "Redirects: a hop that the policy allows is followed and verified like any other download"); allowed.Dispose();
            Check(Directory.Exists(dir) && !Directory.EnumerateFiles(dir, "*.partial", SearchOption.AllDirectories).Any(), "Redirect refusals leave no partial files");
        }

        // size caps
        using (var rel = new Release())
        {
            var msi = FakeMsiBytes(33); rel.Serve("NeoDM-Setup-1.5.3.msi", msi);
            // declared smaller than what the server sends
            Refuses(() => TestTrust.Installer(dir, "1.5.2", reader: TestTrust.FakeMsi("1.5.3")).DownloadAndVerifyAsync(Rel(rel.Server.Url, "1.5.3", "NeoDM-Setup-1.5.3.msi", msi.Length - 1000)), "larger than the allowed size", "Body larger than the declared asset size");
            Check(!Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(), "Oversized body: nothing left on disk");
            using (var exact = Sync(TestTrust.Installer(dir, "1.5.2", reader: TestTrust.FakeMsi("1.5.3")).DownloadAndVerifyAsync(Rel(rel.Server.Url, "1.5.3", "NeoDM-Setup-1.5.3.msi", msi.Length)))) Check(exact.Sha256 == Hex(msi), "Exactly the declared size is fine");
            // sums larger than 1 MB
            rel.Sums = new string('a', 1_000_001); rel.Signature = () => TestTrust.Sign(rel.Sums);
            Refuses(() => TestTrust.Installer(dir, "1.5.2", reader: TestTrust.FakeMsi("1.5.3")).DownloadAndVerifyAsync(Rel(rel.Server.Url, "1.5.3", "NeoDM-Setup-1.5.3.msi", msi.Length)), "not valid", "SHA256SUMS.txt over 1 MB");
        }
        // a server that announces more than 200 MB although the release declares even more: refused on the header, before any body
        long announced = 250L * 1024 * 1024; int bodyStarted = 0;
        using (var big = new LocalServer(ctx =>
        {
            string p = ctx.Request.Url!.AbsolutePath;
            if (p.EndsWith("SHA256SUMS.txt.sig")) { Reply(ctx, 200, TestTrust.Sign(new string('a', 64) + " *NeoDM-Setup-1.5.3.msi\n")); return; }
            if (p.EndsWith("SHA256SUMS.txt")) { Reply(ctx, 200, new string('a', 64) + " *NeoDM-Setup-1.5.3.msi\n"); return; }
            ctx.Response.ContentLength64 = announced; ctx.Response.OutputStream.WriteByte(0); ctx.Response.OutputStream.Flush(); Interlocked.Increment(ref bodyStarted); Thread.Sleep(300); ctx.Response.Abort();
        }))
        {
            var rel = Rel(big.Url, "1.5.3", "NeoDM-Setup-1.5.3.msi", 300L * 1024 * 1024);
            Refuses(() => TestTrust.Installer(dir, "1.5.2", reader: TestTrust.FakeMsi("1.5.3")).DownloadAndVerifyAsync(rel), "200 MB", "Announced 250 MB with a declared 300 MB");
        }
    }
}
