using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using NDM.App;

internal static class YoutubeHandoffTests
{
    public static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS YouTube handoff: " + name); }
        var name = "NDM.YoutubeTest." + Guid.NewGuid().ToString("N");
        var seen = new System.Collections.Concurrent.ConcurrentQueue<NativeBridge.BrowserDownloadRequest>();
        using var bridge = new NativeBridge(r => seen.Enqueue(r), name); bridge.Start();
        async Task<NamedPipeClientStream> Open(object payload)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload) + "\n"), timeout.Token); await pipe.FlushAsync(timeout.Token); return pipe;
        }
        async Task<JsonElement> Reply(NamedPipeClientStream pipe)
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!).RootElement.Clone();
        }
        async Task Wait(Func<bool> condition) { for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(10); Check(condition(), "bounded wait"); }
        const string canonical = "https://www.youtube.com/watch?v=aqz-KE-bpKQ";
        foreach (var bad in new[] { "https://youtube.com.evil.test/watch?v=aqz-KE-bpKQ", "https://evil.youtube.com/watch?v=aqz-KE-bpKQ", "https://user@youtube.com/watch?v=aqz-KE-bpKQ", "https://youtube.com:443/watch?v=aqz-KE-bpKQ", "https://youtu.be/%61qz-KE-bpKQ", "https://youtube.com/%77atch?v=aqz-KE-bpKQ", "https://youtube.com/watch?v=aqz-KE-bpKQ&v=aqz-KE-bpKQ", "https://youtube.com/embed/aqz-KE-bpKQ", "https://youtube.com/playlist?list=x", "https://youtu.be/aqz-KE-bpKQ/", "https://youtube.com/watch?v=short", "https://youtube.com\\@evil.test/watch?v=aqz-KE-bpKQ" })
        { using var p = await Open(new { url = bad, youtube = true }); Check(!(await Reply(p)).GetProperty("ok").GetBoolean(), "invalid address refused"); }
        foreach (object marker in new object[] { false, "true", 1 })
        { using var p = await Open(new { url = canonical, youtube = marker }); Check(!(await Reply(p)).GetProperty("ok").GetBoolean(), "non-true discriminator refused"); }
        foreach (var field in new[] { "cookie", "referer", "userAgent", "accept", "acceptLanguage", "stream", "fileName", "size", "noProbe" })
        { using var p = await Open(new Dictionary<string, object> { ["url"] = canonical, ["youtube"] = true, [field] = "" }); Check(!(await Reply(p)).GetProperty("ok").GetBoolean(), "extra capture field refused: " + field); }
        Check(seen.IsEmpty, "invalid messages never reach UI");
        using (var p = await Open(new { url = "https://youtu.be/aqz-KE-bpKQ?si=discard#discard", youtube = true }))
        {
            await Wait(() => seen.Count == 1);
            seen.TryPeek(out var r);
            Check(r!.Youtube && r.Url == canonical && r.Cookie is null && r.Referer is null && r.UserAgent is null && r.Stream is null, "canonical page only reaches UI");
            Check(bridge.PendingCaptures == 1, "pending budget shared");
            using var duplicate = await Open(new { url = canonical, youtube = true });
            Check((await Reply(duplicate)).GetProperty("error").GetString() == "busy" && seen.Count == 1, "duplicate pending page rejected");
            r.Respond(new(false, Error: "Cancelled", Cancelled: true));
            Check((await Reply(p)).GetProperty("cancelled").GetBoolean(), "desktop cancel preserved");
        }
        await Wait(() => bridge.PendingCaptures == 0); seen.Clear();
        using (var p = await Open(new { url = canonical, youtube = true }))
        { await Wait(() => seen.Count == 1); seen.TryPeek(out var r); p.Dispose(); await Wait(() => r!.Gone.IsCancellationRequested && bridge.PendingCaptures == 0); }
        seen.Clear();
        var held = new List<NamedPipeClientStream>();
        try
        {
            for (int i = 0; i < NativeBridge.MaxPendingCaptures; i++) held.Add(await Open(new { url = "https://youtu.be/" + i.ToString("D11"), youtube = true }));
            await Wait(() => bridge.PendingCaptures == NativeBridge.MaxPendingCaptures);
            using var ninth = await Open(new { url = "https://youtu.be/99999999999", youtube = true });
            Check((await Reply(ninth)).GetProperty("error").GetString() == "busy", "ninth dialog refused");
        }
        finally { foreach (var p in held) p.Dispose(); }
        await Wait(() => bridge.PendingCaptures == 0);
        Console.WriteLine("YouTube pipe handoff tests passed: " + checks);
    }
}
