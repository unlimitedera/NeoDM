using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NDM.App;
using NDM.Core;

internal static partial class SecurityTests
{
    internal sealed class LocalServer : IDisposable
    {
        private readonly HttpListener listener = new();
        public string Url { get; }
        public int Port { get; }
        public List<string> Requests { get; } = new();
        public LocalServer(Action<HttpListenerContext> handle)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = $"http://127.0.0.1:{Port}/";
            listener.Prefixes.Add(Url); listener.Start();
            Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); } catch (Exception) { return; }
                    _ = Task.Run(() =>
                    {
                        try { lock (Requests) Requests.Add(ctx.Request.Url!.AbsolutePath); handle(ctx); }
                        catch (Exception) { try { ctx.Response.Abort(); } catch (Exception) { } }
                    });
                }
            });
        }
        public int Count { get { lock (Requests) return Requests.Count; } }
        public int CountOf(string suffix) { lock (Requests) return Requests.Count(r => r.EndsWith(suffix, StringComparison.Ordinal)); }
        public void Dispose() { try { listener.Stop(); listener.Close(); } catch (Exception) { } }
    }
    internal static void Reply(HttpListenerContext ctx, int status, byte[] body, string type = "application/octet-stream")
    {
        ctx.Response.StatusCode = status; ctx.Response.ContentType = type; ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body); ctx.Response.Close();
    }
    internal static void Reply(HttpListenerContext ctx, int status, string body) => Reply(ctx, status, Encoding.UTF8.GetBytes(body), "text/plain");
    internal static void ReplyFile(HttpListenerContext ctx, string path)
    {
        using var fs = File.OpenRead(path);
        ctx.Response.StatusCode = 200; ctx.Response.ContentLength64 = fs.Length; fs.CopyTo(ctx.Response.OutputStream); ctx.Response.Close();
    }
    private static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static string HexFile(string path) { using var fs = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant(); }
    internal static byte[] FakeMsiBytes(int seed)
    {
        var data = new byte[120_000]; new Random(seed).NextBytes(data);
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(data, 0); return data;
    }
    internal static ReleaseInfo Rel(string baseUrl, string version, string msiName, long size, bool sig = true) => new("v" + version, new Version(version), "NeoDM " + version, "", "https://github.com/unlimitedera/NeoDM/releases/tag/v" + version,
        new ReleaseAsset(msiName, baseUrl + "dl/" + msiName, size), new ReleaseAsset("SHA256SUMS.txt", baseUrl + "dl/SHA256SUMS.txt", 100), sig ? new ReleaseAsset("SHA256SUMS.txt.sig", baseUrl + "dl/SHA256SUMS.txt.sig", 100) : null);
    internal static string Fixture(string name)
    {
        var path = Path.Combine(RepoRoot, "artifacts", name);
        if (!File.Exists(path)) throw new FileNotFoundException("Test fixture installer missing: " + path);
        return path;
    }
    private static void Refuses(Func<Task> action, string expected, string what)
    {
        try { Sync(action()); Check(false, what + ": must be refused"); }
        catch (UpdateException ex) { Check(ex.Message.Contains(expected, StringComparison.OrdinalIgnoreCase), $"{what}: refused with '{ex.Message}'"); }
    }

    // ---------------------------------------------------------------- 2: signature
    private static void SignatureTests(string root)
    {
        var data = Encoding.UTF8.GetBytes("0123 *NeoDM-Setup-1.5.3.msi\n"); var sig = TestTrust.Sign(data);
        Check(UpdateTrust.VerifySignature(data, sig, TestTrust.PublicSpki), "Signature: a valid signature verifies");
        var tampered = (byte[])data.Clone(); tampered[3] ^= 1;
        Check(!UpdateTrust.VerifySignature(tampered, sig, TestTrust.PublicSpki), "Signature: a changed SHA256SUMS.txt is refused");
        var raw = Convert.FromBase64String(sig); raw[raw.Length / 2] ^= 0x10;
        Check(!UpdateTrust.VerifySignature(data, Convert.ToBase64String(raw), TestTrust.PublicSpki), "Signature: a changed signature is refused");
        Check(!UpdateTrust.VerifySignature(data, null, TestTrust.PublicSpki) && !UpdateTrust.VerifySignature(data, "", TestTrust.PublicSpki) && !UpdateTrust.VerifySignature(data, "   ", TestTrust.PublicSpki), "Signature: a missing signature is refused");
        Check(!UpdateTrust.VerifySignature(data, "not base64 !!", TestTrust.PublicSpki) && !UpdateTrust.VerifySignature(data, Convert.ToBase64String(new byte[64]), TestTrust.PublicSpki) && !UpdateTrust.VerifySignature(data, sig, "bad key"), "Signature: garbage signature or key never throws and is refused");
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Check(!UpdateTrust.VerifySignature(data, TestTrust.Sign(data, other), TestTrust.PublicSpki), "Signature: a signature made with another key is refused");
        var p1363 = Convert.ToBase64String(TestTrust.Key.SignData(data, HashAlgorithmName.SHA256));
        Check(!UpdateTrust.VerifySignature(data, p1363, TestTrust.PublicSpki), "Signature: only the DER form that tools/sign-release.cjs writes is accepted");
        Check(!UpdateTrust.VerifySignature(data, sig), "Signature: a test key's signature does not verify under the key embedded in the app");
        using (var embedded = ECDsa.Create()) { embedded.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateTrust.PublicKeySpkiBase64), out _); Check(embedded.KeySize == 256 && embedded.ExportParameters(false).Curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256", "The embedded public key is an ECDSA P-256 key"); }

        // tools/sign-release.cjs (the release machine's signer, node crypto) produces what the app verifies
        string dir = Path.Combine(root, "sign"); Directory.CreateDirectory(dir);
        string sums = Path.Combine(dir, "SHA256SUMS.txt"), pem = Path.Combine(dir, "test-key.pem");
        File.WriteAllText(sums, "abc *NeoDM-Setup-9.9.9.msi\n", new UTF8Encoding(false)); File.WriteAllText(pem, TestTrust.Key.ExportPkcs8PrivateKeyPem());
        var run = Process.Start(new ProcessStartInfo("node", $"\"{Path.Combine(RepoRoot, "tools", "sign-release.cjs")}\" \"{sums}\" \"{pem}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        string output = run.StandardOutput.ReadToEnd() + run.StandardError.ReadToEnd(); run.WaitForExit();
        Check(run.ExitCode == 0 && output.Contains("verify: OK") && !output.Contains("PRIVATE"), "sign-release.cjs signs with a given key and prints no key material: " + output.Trim());
        Check(File.Exists(sums + ".sig") && UpdateTrust.VerifySignature(File.ReadAllBytes(sums), File.ReadAllText(sums + ".sig"), TestTrust.PublicSpki), "node's ECDSA/DER signature verifies with ECDsa.VerifyData(Rfc3279DerSequence) in the app");
        File.WriteAllText(sums, "abc *NeoDM-Setup-9.9.9.msi \n");
        Check(!UpdateTrust.VerifySignature(File.ReadAllBytes(sums), File.ReadAllText(sums + ".sig"), TestTrust.PublicSpki), "a one-byte change to SHA256SUMS.txt invalidates the node signature");
        var gitignore = File.ReadAllText(Path.Combine(RepoRoot, ".gitignore"));
        Check(gitignore.Contains("keys/") && gitignore.Contains("*.pem"), "The private key folder and *.pem are git-ignored");
    }
}
