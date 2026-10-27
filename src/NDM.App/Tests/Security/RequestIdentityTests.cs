using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>1.5.7: a refused request is explained in plain words (list, details, progress window), the browser action appears for browser captures, wrong-content stays as it was.</summary>
internal static partial class SecurityTests
{
    /// <summary>A server that answers every request with the status in the first path segment (/403/file.zip) and nothing else.</summary>
    private sealed class StatusServer : IDisposable
    {
        private readonly HttpListener listener = new();
        public readonly string BaseUrl;
        public int Requests;
        public StatusServer()
        {
            int port; using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
            BaseUrl = $"http://127.0.0.1:{port}"; listener.Prefixes.Add(BaseUrl + "/"); listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await listener.GetContextAsync(); } catch { return; }
                    Interlocked.Increment(ref Requests);
                    c.Response.StatusCode = int.Parse(c.Request.Url!.AbsolutePath.Split('/')[1]); c.Response.ContentLength64 = 0; c.Response.Close();
                }
            });
        }
        public void Dispose() { try { listener.Stop(); } catch { } }
    }

    private static DownloadJob StatusJob(string root, StatusServer server, int status, string? referer, out DownloadManager manager)
    {
        var dir = Path.Combine(root, "st-" + Guid.NewGuid().ToString("N")[..6]);
        var local = manager = new DownloadManager(Path.Combine(dir, "d"), Path.Combine(dir, "s"), new DownloadOptions { RetryCount = 0, LadderPause = TimeSpan.FromMilliseconds(10) });
        var job = Sync(local.AddAsync(new DownloadRequest($"{server.BaseUrl}/{status}/archive.zip", "archive.zip", referer, null, false, null, 1000000, "application/zip")));
        PumpUntil(() => local.Jobs.Single(j => j.Id == job.Id).Status is "Failed" or "Completed", "the job did not finish");
        return local.Jobs.Single(j => j.Id == job.Id);
    }

    private static void RequestFailureTests(string root)
    {
        using var server = new StatusServer();
        var page = "https://www.example.org/file/abc/archive.zip";
        var expected = new Dictionary<int, string>
        {
            [400] = "The server did not accept NeoDM's request (400 Bad Request).",
            [401] = "The server asks you to sign in (401 Unauthorized).",
            [403] = "The server refused NeoDM's request (403 Forbidden). It may need your browser session or a fresh link. Use Download in browser instead, or open the page again to get a new link.",
            [404] = "The server says the file is not there (404 Not Found).",
            [405] = "The server does not allow this kind of request (405 Method Not Allowed).",
            [406] = "The server did not accept the kind of request NeoDM made (406 Not Acceptable).",
            [429] = "The server says there were too many requests (429 Too Many Requests).",
            [500] = "The server had a problem (HTTP 500).",
            [502] = "The server had a problem (HTTP 502).",
            [503] = "The server had a problem (HTTP 503)."
        };
        foreach (var (status, text) in expected)
        {
            var job = StatusJob(root, server, status, page, out var manager);
            using (manager)
            {
                var row = new DownloadRow(job);
                Check(job.Status == "Failed" && job.ErrorCode == CompatibilityLadder.HttpStatusError && job.ErrorStatus == status, $"Engine job {status}: failed with the status in ErrorCode/ErrorStatus ({job.ErrorCode} {job.ErrorStatus})");
                Check(row.IsHttpFailure && row.Error!.StartsWith(text, StringComparison.Ordinal), $"Row {status}: plain words: {row.Error}");
                Check(row.PageToOpen == page && RowMenu.For(new[] { row }).OpenPage && !row.IsWrongContent, $"Row {status}: the browser action is offered for a captured download (it has an https referer)");
                Check(row.Error!.Contains("Use Download in browser instead") || status is 401 or 404 or 429 or >= 500, $"Row {status}: the advice");
                Check(!string.IsNullOrEmpty(row.Attempts) && row.Attempts!.StartsWith("Attempt 1: " + status, StringComparison.Ordinal), $"Row {status}: the attempt trail is kept: {row.Attempts}");
                Check(job.Error!.Contains(status.ToString()) && job.Error.Contains("Attempt 1"), $"Job {status}: the engine's own message has the trail");
            }
        }
        Check(expected[403] == Loc.T("error.status.403", 403), "The 403 text is exactly the agreed sentence");
        // no referer (typed in by hand): the same words, but no page to open
        var manual = StatusJob(root, server, 403, null, out var m1);
        using (m1) { var row = new DownloadRow(manual); Check(row.IsHttpFailure && row.PageToOpen is null && !RowMenu.For(new[] { row }).OpenPage, "A download without a page address has no 'Open the page' action"); }
        var plainHttp = StatusJob(root, server, 403, "http://www.example.org/file", out var m2);
        using (m2) { var row = new DownloadRow(plainHttp); Check(row.IsHttpFailure && row.PageToOpen is null, "An http page is not offered (https only), as for wrong-content"); }
        // a status without an explanation keeps the engine's text
        var odd = StatusJob(root, server, 418, page, out var m3);
        using (m3) { var row = new DownloadRow(odd); Check(!row.IsHttpFailure && row.PageToOpen is null && row.Error == odd.Error, "Other statuses keep the engine message and add no browser action"); }
        // every language has the sentences
        var languageBefore = Loc.Language;
        foreach (var key in new[] { "error.status.400", "error.status.401", "error.status.403", "error.status.404", "error.status.405", "error.status.406", "error.status.429", "error.status.5xx", "progress.attempts.title" })
        {
            Loc.SetLanguage("en"); var english = Loc.T(key, 403);
            foreach (var tag in LanguageCatalog.Installed().Select(l => l.Tag).Where(tag => tag != "en"))
            {
                Loc.SetLanguage(tag);
                Check(Loc.T(key, 403).Length > 3 && Loc.T(key, 403) != english, $"{key} is translated in {tag}");
            }
        }
        Loc.SetLanguage(languageBefore);
        // the progress window: the sentence, the browser action and the attempts in the details
        var failed = StatusJob(root, server, 403, page, out var m4);
        using (m4)
        {
            var window = new DownloadProgressWindow(m4, failed.Id) { ShowInTaskbar = false };
            window.Show(); Pump(150);
            var panel = (StackPanel)window.FindName("WrongContentPanel"); var open = (Button)window.FindName("OpenPageButton"); var attempts = (TextBox)window.FindName("AttemptsText");
            Check(panel.Visibility == Visibility.Visible && open.Visibility == Visibility.Visible, "Progress window: the hint and 'Open the page in the browser' appear for a refused request");
            Check(attempts.Visibility == Visibility.Visible && attempts.Text.StartsWith("Request attempts: Attempt 1: 403", StringComparison.Ordinal), "Progress window: the details area shows the attempt trail: " + attempts.Text);
            Check(((TextBox)window.FindName("StatusText")).Text.Contains("The server refused NeoDM's request (403 Forbidden)."), "Progress window: the status line carries the sentence");
            window.Close();
        }
        // wrong-content is untouched by all this
        var wrong = WrongContentJob(root, page, out var m5);
        using (m5) { var row = new DownloadRow(wrong); Check(row.IsWrongContent && !row.IsHttpFailure && row.OffersBrowser && row.PageToOpen == page, "Wrong-content rules are intact"); }
    }
}
