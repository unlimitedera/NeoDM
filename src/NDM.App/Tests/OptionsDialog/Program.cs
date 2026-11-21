// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using NDM.App;
using NDM.Core;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

// Test NativeBridge queuing behavior
string testPipe = "NDM.OptionsDialogTest." + Guid.NewGuid().ToString("N");
string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch");
string root = Path.Combine(scratch, "NDM-OptionsDialogTest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    // Test that bridge queues requests and waits for responses
    NativeBridge.BrowserDownloadRequest? capturedRequest = null;
    var receivedSignal = new TaskCompletionSource<bool>();
    
    using var bridge = new NativeBridge(request =>
    {
        capturedRequest = request;
        receivedSignal.SetResult(true);
    }, testPipe);
    bridge.Start();

    // Send a request from a client
    var clientTask = Task.Run(async () =>
    {
        using var client = new NamedPipeClientStream(".", testPipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        
        var message = JsonSerializer.Serialize(new 
        { 
            url = "http://example.com/file.zip",
            fileName = "file.zip",
            referer = "http://example.com/",
            cookie = "session=test",
            mime = "application/zip"
        });
        
        await client.WriteAsync(Encoding.UTF8.GetBytes(message + "\n"), timeout.Token);
        await client.FlushAsync(timeout.Token);
        
        using var reader = new StreamReader(client);
        return await reader.ReadLineAsync(timeout.Token) ?? throw new Exception("No reply");
    });

    // Wait for request to be captured
    using var waitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await receivedSignal.Task.WaitAsync(waitTimeout.Token);
    
    Check(capturedRequest != null, "Bridge received and queued the request");
    Check(capturedRequest!.Url == "http://example.com/file.zip", "Request URL preserved");
    Check(capturedRequest.FileName == "file.zip", "Request filename preserved");
    Check(capturedRequest.Referer == "http://example.com/", "Request referer preserved");
    Check(capturedRequest.Cookie == "session=test", "Request cookie preserved");
    Check(capturedRequest.Mime == "application/zip", "Optional browser mime forwarded to the app");

    // Simulate user accepting the download
    capturedRequest.Respond(new NativeBridge.BrowserDownloadResponse(true, "test-job-id"));

    // Client should receive the response
    using var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var reply = await clientTask.WaitAsync(responseTimeout.Token);
    var response = JsonDocument.Parse(reply).RootElement;
    
    Check(response.GetProperty("ok").GetBoolean(), "Response indicates success");
    Check(response.GetProperty("id").GetString() == "test-job-id", "Response contains job ID");

    // Test cancellation
    capturedRequest = null;
    receivedSignal = new TaskCompletionSource<bool>();
    
    var cancelTask = Task.Run(async () =>
    {
        using var client = new NamedPipeClientStream(".", testPipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        
        var message = JsonSerializer.Serialize(new { url = "http://example.com/cancel.zip" });
        await client.WriteAsync(Encoding.UTF8.GetBytes(message + "\n"), timeout.Token);
        await client.FlushAsync(timeout.Token);
        
        using var reader = new StreamReader(client);
        return await reader.ReadLineAsync(timeout.Token) ?? throw new Exception("No reply");
    });

    await receivedSignal.Task.WaitAsync(waitTimeout.Token);
    Check(capturedRequest != null, "Second request received");
    
    // Simulate user cancelling
    capturedRequest!.Respond(new NativeBridge.BrowserDownloadResponse(false, null, "User cancelled"));
    
    var cancelReply = await cancelTask.WaitAsync(responseTimeout.Token);
    var cancelResponse = JsonDocument.Parse(cancelReply).RootElement;
    
    Check(!cancelResponse.GetProperty("ok").GetBoolean(), "Cancelled response indicates failure");
    Check(cancelResponse.GetProperty("error").GetString() == "User cancelled", "Error message preserved");

    // Optional fields are validated: a malformed mime is rejected without reaching the UI; old four-field requests still work.
    capturedRequest = null;
    foreach (var bad in new[] { "{\"url\":\"http://example.com/a.zip\",\"mime\":\"not a mime\"}", "{\"url\":\"http://example.com/a.zip\",\"mime\":\"" + new string('a', 120) + "/b\"}", "{\"url\":\"http://example.com/a.zip\",\"fileName\":\"" + new string('b', 600) + "\"}" })
    {
        using var badClient = new NamedPipeClientStream(".", testPipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var badTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await badClient.ConnectAsync(badTimeout.Token);
        await badClient.WriteAsync(Encoding.UTF8.GetBytes(bad + (char)10), badTimeout.Token); await badClient.FlushAsync(badTimeout.Token);
        using var badReader = new StreamReader(badClient);
        var badReply = JsonDocument.Parse(await badReader.ReadLineAsync(badTimeout.Token) ?? throw new Exception("No reply")).RootElement;
        Check(!badReply.GetProperty("ok").GetBoolean() && capturedRequest is null, "Malformed mime / over-long name is rejected before the UI sees it");
    }
    Console.WriteLine("ALL DOWNLOAD OPTIONS DIALOG TESTS PASSED");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}
