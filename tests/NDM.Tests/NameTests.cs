using NDM.Core;

namespace NDM.Tests;

/// <summary>File name detection for captured / pasted links: Content-Disposition, redirects, query names, Content-Type, magic bytes, fallbacks.</summary>
internal static class NameTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Is(string? actual, string expected, string what) { if (actual != expected) throw new Exception($"{what}: expected '{expected}' but got '{actual}'"); }

    public static Task Parsing()
    {
        Is(FileNameResolver.FromContentDisposition("attachment; filename=\"a b.pdf\""), "a b.pdf", "quoted");
        Is(FileNameResolver.FromContentDisposition("attachment; filename=plain.zip"), "plain.zip", "unquoted");
        Is(FileNameResolver.FromContentDisposition("attachment; filename=\"fallback.txt\"; filename*=UTF-8''%E6%97%A5%E6%9C%AC.zip"), "\u65e5\u672c.zip", "filename* wins over filename");
        Is(FileNameResolver.FromContentDisposition("attachment; filename*=UTF-8''%E6%97%A5%E6%9C%AC.zip; filename=\"x.txt\""), "\u65e5\u672c.zip", "filename* wins when first");
        Is(FileNameResolver.FromContentDisposition("attachment; filename*=iso-8859-1'en'caf%E9.txt"), "caf\u00e9.txt", "filename* latin1");
        Is(FileNameResolver.FromContentDisposition("attachment; filename=\"C:\\Users\\x\\evil.exe\""), "evil.exe", "windows path stripped");
        Is(FileNameResolver.FromContentDisposition("attachment; filename=\"../../etc/passwd\""), "passwd", "unix path stripped");
        Is(FileNameResolver.FromContentDisposition("attachment; filename=\"semi;colon.zip\"; size=5"), "semi;colon.zip", "semicolon inside quotes");
        Check(FileNameResolver.FromContentDisposition("attachment; filename=\"...\"") is null, "only dots yields nothing");
        Check(FileNameResolver.FromContentDisposition("inline") is null && FileNameResolver.FromContentDisposition(null) is null, "no filename yields nothing");
        Is(FileNameResolver.FromUrl(new Uri("https://h/dl/My%20File%20%C3%A9.zip?x=1#frag")), "My File \u00e9.zip", "url segment decoded, query and fragment ignored");
        Is(FileNameResolver.FromUrl(new Uri("https://h/get?id=5&filename=archive.7z")), "archive.7z", "filename= query");
        Is(FileNameResolver.FromUrl(new Uri("https://h/get?file=a.iso")), "a.iso", "file= query");
        Is(FileNameResolver.FromUrl(new Uri("https://h/get?fn=b.mp3")), "b.mp3", "fn= query");
        Is(FileNameResolver.FromUrl(new Uri("https://h/get?name=c.png")), "c.png", "name= query");
        Is(FileNameResolver.FromUrl(new Uri("https://h/o/x?response-content-disposition=attachment%3B%20filename%3D%22s3.rar%22")), "s3.rar", "response-content-disposition");
        Is(FileNameResolver.FromUrl(new Uri("https://h/o/x?response-content-disposition=attachment%3Bfilename*%3DUTF-8%27%27%25E2%2582%25AC.zip")), "\u20ac.zip", "response-content-disposition filename*");
        Is(FileNameResolver.ExtensionForMime("application/zip"), ".zip", "mime zip");
        Is(FileNameResolver.ExtensionForMime("Video/MP4; codecs=avc1"), ".mp4", "mime with parameters");
        Check(FileNameResolver.ExtensionForMime("application/octet-stream") is null && FileNameResolver.ExtensionForMime("text/html") is null, "octet-stream and html give no extension");
        foreach (var (mime, ext) in new[] { ("application/x-rar-compressed", ".rar"), ("application/x-7z-compressed", ".7z"), ("application/gzip", ".gz"), ("application/x-tar", ".tar"), ("application/x-msdownload", ".exe"), ("application/x-msi", ".msi"),
            ("application/vnd.android.package-archive", ".apk"), ("application/x-iso9660-image", ".iso"), ("application/x-apple-diskimage", ".dmg"), ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx"),
            ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx"), ("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx"), ("video/x-matroska", ".mkv"), ("video/webm", ".webm"),
            ("audio/mpeg", ".mp3"), ("audio/flac", ".flac"), ("audio/wav", ".wav"), ("audio/ogg", ".ogg"), ("audio/mp4", ".m4a"), ("image/jpeg", ".jpg"), ("image/png", ".png"), ("image/gif", ".gif"), ("image/webp", ".webp"),
            ("image/svg+xml", ".svg"), ("text/plain", ".txt"), ("text/csv", ".csv"), ("application/json", ".json"), ("application/xml", ".xml"), ("application/x-bittorrent", ".torrent") })
            Is(FileNameResolver.ExtensionForMime(mime), ext, "mime " + mime);
        Is(FileNameResolver.ExtensionFromBytes(new byte[] { 0x50, 0x4B, 3, 4, 0 }), ".zip", "magic zip");
        Is(FileNameResolver.ExtensionFromBytes("Rar!\u001a\a\0"u8.ToArray()), ".rar", "magic rar");
        Is(FileNameResolver.ExtensionFromBytes(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0 }), ".7z", "magic 7z");
        Is(FileNameResolver.ExtensionFromBytes("%PDF-1.4"u8.ToArray()), ".pdf", "magic pdf");
        Is(FileNameResolver.ExtensionFromBytes("MZ\u0090\0\u0003\0"u8.ToArray()), ".exe", "magic exe");
        Is(FileNameResolver.ExtensionFromBytes(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0 }), ".mkv", "magic ebml");
        Is(FileNameResolver.ExtensionFromBytes(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32, 0 }), ".mp4", "magic mp4");
        Is(FileNameResolver.ExtensionFromBytes("ID3\u0003\0\0"u8.ToArray()), ".mp3", "magic id3");
        Is(FileNameResolver.ExtensionFromBytes(new byte[] { 0xFF, 0xFB, 0x90, 0 }), ".mp3", "magic mpeg audio frame");
        Is(FileNameResolver.ExtensionFromBytes(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D }), ".png", "magic png");
        Is(FileNameResolver.ExtensionFromBytes(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }), ".jpg", "magic jpeg");
        Is(FileNameResolver.ExtensionFromBytes("GIF89a"u8.ToArray()), ".gif", "magic gif");
        Is(FileNameResolver.ExtensionFromBytes(new byte[] { 0x1F, 0x8B, 8, 0 }), ".gz", "magic gzip");
        Check(FileNameResolver.ExtensionFromBytes("hello world"u8.ToArray()) is null, "plain text has no signature");
        return Task.CompletedTask;
    }

    private static NameProbeResult Seen(string url, string? type = null, string? disposition = null, byte[]? head = null) => new(url, disposition, type, null, head ?? Array.Empty<byte>(), 200);

    /// <summary>The exact requests the extension sends for a video (menu entry or button): a feed-style signed address, an extensionless one, a very long one,
    /// and the post page address that older versions sent, which is what produced facebook.com-download.bin.</summary>
    public static Task MediaRequests()
    {
        var token = new string('A', 40) + "_" + new string('b', 40);
        var signed = "https://video-dus1-1.xx.fbcdn.net/o1/v/t2/f2/m412/" + token + ".mp4?_nc_cat=101&ccb=1-7&_nc_sid=" + new string('9', 60) + "&efg=" + new string('e', 400) + "&bitrate=580000&tag=hd&oh=" + new string('c', 100) + "&oe=6A0B1C2D";
        Check(signed.Length > 600, "the sample address is longer than 600 characters");
        Is(FileNameResolver.Resolve(new Uri(signed), null, null, null).Name, token + ".mp4", "signed .mp4 address with no name hint keeps its own file name");
        Is(FileNameResolver.Resolve(new Uri(signed), "Post 6 by Someone 720p.mp4", "video/mp4", null).Name, "Post 6 by Someone 720p.mp4", "the title with extension sent by the extension wins");
        Check(!FileNameResolver.Resolve(new Uri(signed), "Clip.mp4", null, null).Name.EndsWith(".bin"), "never .bin for a video address");
        // Optional: a real address saved from a public page (set NEODM_SAMPLE_VIDEO_URL_FILE); not required for the suite to pass.
        var real = Environment.GetEnvironmentVariable("NEODM_SAMPLE_VIDEO_URL_FILE");
        if (real is not null && File.Exists(real))
        {
            var u = new Uri(File.ReadAllText(real).Trim());
            var n = FileNameResolver.Resolve(u, null, null, null).Name; Console.WriteLine("real sample address (" + u.AbsoluteUri.Length + " chars), no hint: " + n);
            var n2 = FileNameResolver.Resolve(u, "Facebook video 720p.mp4", "video/mp4", null).Name; Console.WriteLine("real sample address, extension hint: " + n2);
            Check(n.EndsWith(".mp4") && n2 == "Facebook video 720p.mp4", "real sample address never .bin");
        }
        var post = new Uri("https://www.facebook.com/facebook/videos/10153231379946729/");
        Is(FileNameResolver.Resolve(post, null, null, null).Name, "facebook.com-download.bin", "ROOT CAUSE: the post page address as a download gives a .bin name");
        var plain = new Uri("https://cdn.example.com/stream/a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5" + "?sig=" + new string('a', 300));
        Is(FileNameResolver.Resolve(plain, null, null, null).Name, "cdn.example.com-download.bin", "an extensionless address with no hint ends as .bin");
        Is(FileNameResolver.Resolve(plain, "Holiday clip.mp4", null, null).Name, "Holiday clip.mp4", "extensionless address with the file name from the extension");
        Is(FileNameResolver.Resolve(plain, null, "video/mp4", null).Name, "cdn.example.com-download.mp4", "extensionless address with only the type hint is .mp4, not .bin");
        Is(FileNameResolver.Resolve(plain, "Holiday clip", "video/mp4", null).Name, "Holiday clip.mp4", "a name without extension takes the type hint");
        return Task.CompletedTask;
    }

    public static Task Decisions()
    {
        var url = new Uri("https://files.example.com/dl/data");
        Is(FileNameResolver.Resolve(url, "chosen.bin", null, Seen(url.AbsoluteUri, "application/zip", "attachment; filename=\"server.zip\"")).Name, "chosen.bin", "browser name beats Content-Disposition");
        Is(FileNameResolver.Resolve(url, null, null, Seen(url.AbsoluteUri, null, "attachment; filename=\"server.zip\"")).Name, "server.zip", "Content-Disposition beats URL");
        Is(FileNameResolver.Resolve(url, null, null, Seen("https://cdn.example.com/x/Final%20Name.pdf")).Name, "Final Name.pdf", "final URL after redirects beats the original");
        Is(FileNameResolver.Resolve(new Uri("https://h/dl/data"), null, null, Seen("https://h/dl/data", "application/zip")).Name, "data.zip", "extension from Content-Type");
        Is(FileNameResolver.Resolve(new Uri("https://h/dl/data"), null, null, Seen("https://h/dl/data", "application/octet-stream")).Name, "data", "octet-stream adds nothing");
        Is(FileNameResolver.Resolve(new Uri("https://h/dl/data"), null, null, Seen("https://h/dl/data", "application/octet-stream", head: new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D })).Name, "data.pdf", "magic bytes as last resort");
        Is(FileNameResolver.Resolve(new Uri("https://h/dl/report.pdf"), null, null, Seen("https://h/dl/report.pdf", "application/zip")).Name, "report.pdf", "a valid extension is never replaced");
        Is(FileNameResolver.Resolve(new Uri("https://h/download.php?id=3"), null, null, Seen("https://h/download.php?id=3", "application/zip")).Name, "download.zip", ".php replaced by the real type");
        Is(FileNameResolver.Resolve(new Uri("https://h/get.aspx"), null, null, Seen("https://h/get.aspx", "application/octet-stream", head: new byte[] { 0x50, 0x4B, 3, 4 })).Name, "get.zip", ".aspx replaced when the bytes prove a zip");
        Is(FileNameResolver.Resolve(new Uri("https://h/page.php"), null, null, Seen("https://h/page.php", "text/html")).Name, "page.php", "html pages keep their name");
        Is(FileNameResolver.Resolve(new Uri("https://h/a/3f2504e0-4f89-11d3-9a0c-0305e82c3301"), null, null, Seen("https://h/a/3f2504e0-4f89-11d3-9a0c-0305e82c3301", "video/mp4")).Name, "h-download.mp4", "GUID path falls back to host-based name with the type's extension");
        Is(FileNameResolver.Resolve(new Uri("https://www.example.org/"), null, null, null).Name, "example.org-download.bin", "empty path");
        Is(FileNameResolver.Resolve(new Uri("https://h/x"), "../..\\sneaky.zip", null, null).Name, "sneaky.zip", "browser name loses path parts");
        Is(FileNameResolver.Resolve(new Uri("https://h/x"), "noext", "application/pdf", null).Name, "noext.pdf", "browser mime adds a missing extension");
        var longName = FileNameResolver.Resolve(new Uri("https://h/x"), new string('b', 400) + ".zip", null, null).Name;
        Check(longName.Length <= 180 && longName.EndsWith(".zip"), "over-long names are shortened and keep the extension");
        return Task.CompletedTask;
    }

    private static DownloadManager Manager((string Download, string State) paths) =>
        new(paths.Download, paths.State, new DownloadOptions { MaxConcurrentDownloads = 2, MaxSegments = 4, MinimumSegmentSize = 65536, RetryCount = 1, RequestTimeout = TimeSpan.FromSeconds(3) },
            name => Path.GetExtension(name) == ".zip" ? Path.Combine(paths.Download, "Compressed") : paths.Download);

    private static async Task<DownloadJob> Finish(DownloadManager m, string url, string? name = null)
    {
        var job = await m.AddAsync(new DownloadRequest(url, name));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var now = m.Jobs.Single(j => j.Id == job.Id);
            if (now.Status == "Completed") return now;
            if (now.Status == "Failed") throw new Exception("Download failed: " + now.Error);
            if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Timed out on " + url);
            await Task.Delay(40);
        }
    }

    public static async Task Downloads((string Download, string State) paths)
    {
        using var server = new FixtureServer(); using var m = Manager(paths);
        async Task Expect(string route, string expected, string what, string? typed = null)
        {
            var job = await Finish(m, server.BaseUrl + route, typed);
            Is(job.FileName, expected, what);
            Check(File.Exists(job.FilePath) && Path.GetFileName(job.FilePath) == expected, what + ": file exists on disk under that name");
        }
        await Expect("/n/cd-plain", "report final.pdf", "Content-Disposition quoted");
        await Expect("/n/cd-star", "\u65e5\u672c \u20ac.zip", "filename* UTF-8 percent-encoded");
        Check(Path.GetFileName(Path.GetDirectoryName(m.Jobs.Single(j => j.FileName == "\u65e5\u672c \u20ac.zip").FilePath)!) == "Compressed", "category folder follows the FINAL extension (.zip)");
        await Expect("/n/cd-unquoted", "unquoted.csv", "Content-Disposition unquoted");
        await Expect("/n/cd-traversal", "evil.txt", "path traversal in Content-Disposition");
        await Expect("/n/cd-dots", "cd-dots.zip", "only-dots Content-Disposition is ignored: URL name + type");
        var longJob = await Finish(m, server.BaseUrl + "/n/cd-long");
        Check(longJob.FileName.Length <= 180 && longJob.FileName.EndsWith(".zip") && File.Exists(longJob.FilePath), "over-long name shortened, extension kept");
        await Expect("/n/cd-raw-utf8", "caf\u00e9.txt", "raw UTF-8 bytes in Content-Disposition");
        await Expect("/n/redirect-name", "Real Name.zip", "redirect to a different final name");
        await Expect("/n/redirect-bare", "blob.zip", "redirect to an extension-less final URL + application/zip");
        await Expect("/n/nozip", "nozip.zip", "no extension + application/zip");
        await Expect("/n/nopdf", "nopdf.pdf", "no extension + application/pdf");
        await Expect("/n/video", "video.mp4", "no extension + video/mp4");
        await Expect("/n/magic-zip", "magic-zip.zip", "octet-stream + zip magic bytes");
        await Expect("/n/magic-png", "magic-png.png", "octet-stream + png magic bytes");
        await Expect("/n/magic-mp4", "magic-mp4.mp4", "octet-stream + mp4 magic bytes");
        await Expect("/n/q?id=7&filename=archive%20one.7z", "archive one.7z", "filename= query parameter");
        await Expect("/n/q?response-content-disposition=attachment%3B%20filename%3D%22viaquery.rar%22", "viaquery.rar", "response-content-disposition query parameter");
        await Expect("/n/script.php?id=3", "script.zip", ".php replaced by the Content-Type's extension");
        await Expect("/n/page.php", "page.php", "html page keeps its name");
        await Expect("/n/keep.txt", "keep.txt", "valid extension is never replaced");
        await Expect("/n/" + Guid.NewGuid().ToString("D"), "127.0.0.1-download.mp4", "GUID-like path + video/mp4");
        await Expect("/n/head405", "from-get.pdf", "HEAD refused (405), the engine still reads Content-Disposition from GET");
        await Expect("/n/cd-plain?again=1", "report final (1).pdf", "collision-safe");
        await Expect("/n/cd-plain?typed=1", "mine.dat", "explicit user name is kept", typed: "mine.dat");
    }

    public static async Task Probe((string Download, string State) paths)
    {
        using var server = new FixtureServer(); using var m = Manager(paths);
        async Task<NameProbeResult> P(string route) => (await m.ProbeNameAsync(new DownloadRequest(server.BaseUrl + route)))!;
        var cd = await P("/n/cd-plain");
        Check(FileNameResolver.Resolve(new Uri(server.BaseUrl + "/n/cd-plain"), null, null, cd).Name == "report final.pdf" && server.NamedRequests.Any(r => r.Method == "HEAD"), "HEAD probe resolves Content-Disposition");
        var redirect = await P("/n/redirect-name");
        Check(redirect.FinalUrl.EndsWith("/n/final/Real%20Name.zip"), "probe follows redirects and reports the final URL");
        var fallback = await P("/n/head405");
        Check(fallback.ContentDisposition!.Contains("from-get.pdf") && server.NamedRequests.Any(r => r.Method == "GET"), "HEAD 405 falls back to a ranged GET");
        var sniff = await P("/n/magic-zip");
        Check(sniff.Head.Length > 3 && FileNameResolver.ExtensionFromBytes(sniff.Head) == ".zip", "probe returns the first bytes when HEAD cannot settle the extension");
        var sized = await P("/n/nozip");
        Check(sized.ContentType == "application/zip" && sized.Length is > 0, "probe reports type and size");
        Check(await m.ProbeNameAsync(new DownloadRequest("http://127.0.0.1:1/never")) is null, "unreachable host yields null quickly");
    }
}
