using System.IO;
using System.Net;
using System.Windows;
using NDM.App;

internal static partial class SecurityTests
{
    private static void PrivateNetworkTests()
    {
        // addresses that must be flagged, in every form the Uri class normalises to a literal address
        var yes = new[]
        {
            "http://127.0.0.1/a.zip", "http://127.0.0.1:8080/a.zip", "http://127.255.255.254/", "http://127.1/", "http://localhost/", "http://LOCALHOST:9/x", "http://localhost./x", "http://printer.localhost/x",
            "http://[::1]/", "http://[::1]:3000/x", "http://[0:0:0:0:0:0:0:1]/", "http://10.0.0.1/", "http://10.255.255.255/", "http://172.16.0.1/", "http://172.20.1.1/", "http://172.31.255.255/",
            "http://192.168.0.1/", "http://192.168.255.255/", "http://169.254.0.1/", "http://169.254.169.254/latest/meta-data", "http://[fc00::1]/", "http://[fd12:3456:789a::1]/", "http://[fdff:ffff::1]/",
            "http://[fe80::1]/", "http://[fe80::abcd%25eth0]/", "http://[febf::1]/", "http://[::ffff:127.0.0.1]/", "http://[::ffff:10.1.2.3]/", "http://[::ffff:192.168.1.1]/", "http://[::ffff:7f00:1]/",
            "http://2130706433/", "http://0x7f000001/", "http://0x7f.0.0.1/", "http://0177.0.0.1/", "http://017700000001/", "http://167772161/", "http://3232235777/", "http://0.0.0.0/", "http://[::]/",
            "https://user:pw@127.0.0.1/x", "http://192.168.1.1.:80/x",
        };
        foreach (var url in yes) Check(PrivateNetwork.IsPrivateUrlAsync(url).GetAwaiter().GetResult(), "Private network: " + url + " is flagged" + (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.HostNameType != UriHostNameType.Unknown ? " (host " + u.Host + ")" : ""));
        var no = new[]
        {
            "http://8.8.8.8/", "http://93.184.216.34/f.zip", "http://172.15.255.255/", "http://172.32.0.1/", "http://192.167.1.1/", "http://192.169.1.1/", "http://169.253.1.1/", "http://169.255.1.1/", "http://11.0.0.1/", "http://9.255.255.255/", "http://126.255.255.255/", "http://128.0.0.1/",
            "http://[2001:db8::1]/", "http://[2606:4700::1111]/", "http://[fe00::1]/", "http://[fec0::1]/", "http://[fbff::1]/", "http://[fe7f::1]/", "http://[::ffff:8.8.8.8]/", "http://100.64.0.1/",
            "ftp://127.0.0.1/x", "file:///c:/x", "javascript:alert(1)", "not a url", "", "http://",
        };
        foreach (var url in no) Check(!PrivateNetwork.IsPrivateUrlAsync(url, (_, _) => throw new Exception("literal addresses need no lookup")).GetAwaiter().GetResult(), "Private network: " + url + " is not flagged");
        Check(PrivateNetwork.IsPrivateAddress(IPAddress.Parse("::ffff:172.16.5.5")) && !PrivateNetwork.IsPrivateAddress(IPAddress.Parse("::ffff:172.32.5.5")) && PrivateNetwork.IsPrivateAddress(IPAddress.IPv6Loopback) && !PrivateNetwork.IsPrivateAddress(IPAddress.Parse("2a00:1450:4001::200e")), "IsPrivateAddress: IPv4-mapped and global IPv6");
        // names are resolved; any private answer counts
        string seen = "";
        Func<string, CancellationToken, Task<IPAddress[]>> resolver(params string[] answers) => (host, _) => { seen = host; return Task.FromResult(answers.Select(IPAddress.Parse).ToArray()); };
        Check(!PrivateNetwork.IsPrivateUrlAsync("https://download.example.org/f.iso", resolver("93.184.216.34")).GetAwaiter().GetResult() && seen == "download.example.org", "Private network: a public name is not flagged");
        Check(PrivateNetwork.IsPrivateUrlAsync("https://nas.corp.example/f.iso", resolver("192.168.0.20")).GetAwaiter().GetResult(), "Private network: a name that resolves to 192.168.x is flagged");
        Check(PrivateNetwork.IsPrivateUrlAsync("https://rebind.example/f", resolver("93.184.216.34", "10.0.0.5")).GetAwaiter().GetResult(), "Private network: one private answer among public ones is flagged");
        Check(PrivateNetwork.IsPrivateUrlAsync("https://v6.example/f", resolver("fd00::5")).GetAwaiter().GetResult() && PrivateNetwork.IsPrivateUrlAsync("https://mapped.example/f", resolver("::ffff:10.1.1.1")).GetAwaiter().GetResult(), "Private network: names that resolve to ULA or IPv4-mapped private addresses are flagged");
        Check(!PrivateNetwork.IsPrivateUrlAsync("https://gone.example/f", (_, _) => throw new System.Net.Sockets.SocketException(11001)).GetAwaiter().GetResult(), "Private network: a failed lookup is not flagged and does not throw");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Check(!PrivateNetwork.IsPrivateUrlAsync("https://slow.example/f", async (_, token) => { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return Array.Empty<IPAddress>(); }, TimeSpan.FromMilliseconds(300)).GetAwaiter().GetResult() && clock.ElapsedMilliseconds < 5000, "Private network: a lookup that hangs is cut off");

        // the warning line in the capture dialog
        var dialog = new DownloadOptionsDialog("http://192.168.1.10/file.zip", "file.zip", null, null, Path.GetTempPath()) { ShowInTaskbar = false };
        dialog.Show(); Pump(80);
        var notice = (System.Windows.Controls.TextBlock)dialog.FindName("PrivateNetworkNotice");
        Check(notice.Text == "This address is on your own computer or local network." && !dialog.PrivateNetworkWarningVisible && notice.Visibility == Visibility.Collapsed, "Capture dialog: the warning exists and is hidden for an ordinary address");
        dialog.ShowPrivateNetworkWarning(); Pump(50);
        Check(dialog.PrivateNetworkWarningVisible && notice.IsVisible && notice.ActualHeight > 0 && dialog.FindName("StartButton") is System.Windows.Controls.Button { IsEnabled: true }, "Capture dialog: the warning line is visible and does not block the download buttons");
        dialog.Close();
        var appSource = File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", "App.xaml.cs"));
        Check(appSource.Contains("PrivateNetwork.IsPrivateUrlAsync(request.Url)") && appSource.Contains("ShowPrivateNetworkWarning"), "App: every browser capture is checked and the dialog warned");
    }
}
