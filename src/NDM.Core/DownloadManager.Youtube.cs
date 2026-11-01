namespace NDM.Core;

public sealed partial class DownloadManager
{
    private async Task RunYoutubeAsync(Work w, CancellationToken token)
    {
        // A helper restart may reuse its partial files, but byte-range resume is not promised.
        lock (gate) { w.Job.ResumeSupported = null; w.Job.DownloadedBytes = 0; w.Job.TotalBytes = 0; }
        var folder = PartDirectory(w);
        var selection = w.Request.Youtube!;
        var source = await Youtube.DownloadAsync(w.Request.Url, selection, folder, tools, w.SpeedLimit, (done, total) =>
        {
            lock (gate)
            {
                if (w.Removed || token.IsCancellationRequested) return;
                w.Job.DownloadedBytes = done; w.Job.TotalBytes = total > 0 ? Math.Max(done, total) : 0; w.Total = w.Job.TotalBytes;
            }
        }, token).ConfigureAwait(false);
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (w.Removed || w.Job.Status != "Downloading") throw new OperationCanceledException(token);
            var target = Path.GetDirectoryName(w.Job.FilePath) ?? downloads;
            Directory.CreateDirectory(target);
            var name = StreamNaming.Stem(w.Job.FileName) + Path.GetExtension(source);
            var result = PlaceUniqueLocked(w, source, name, target);
            w.Job.FileName = Path.GetFileName(result); w.Job.FilePath = result;
            w.Total = w.Job.TotalBytes = w.Job.DownloadedBytes = new FileInfo(result).Length;
            DeletePartial(w);
            w.Job.Status = "Completed"; w.Job.Error = null; ClearErrorCode(w.Job); SaveLocked();
        }
    }
}
