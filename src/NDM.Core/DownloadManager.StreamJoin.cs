namespace NDM.Core;

/// <summary>How a later join of saved stream files ended.</summary>
public enum StreamJoinResult
{
    /// <summary>The files were joined into one and the job now points at it.</summary>
    Joined,
    /// <summary>The helper tool is not installed (and not allowed from PATH).</summary>
    ToolMissing,
    /// <summary>The tool ran but its result was refused, or the saved files are gone; the files are left as they were.</summary>
    Failed
}

public sealed partial class DownloadManager
{
    /// <summary>The request a job was added with (address, name hint, referer, cookie and browser identity), or null when the job is gone. Used to start the same address as a stream after a plain download came back as a playlist.</summary>
    public DownloadRequest? RequestOf(string id)
    {
        lock (gate) return jobs.FirstOrDefault(x => x.Job.Id == id)?.Request;
    }

    /// <summary>True when a finished stream job saved separate files because the helper tool was missing, so <see cref="JoinSavedStreamAsync"/> can finish the job.</summary>
    public bool CanJoinSavedStream(string id)
    {
        lock (gate)
        {
            var w = jobs.FirstOrDefault(x => x.Job.Id == id);
            return w is { Job.Status: "Completed" } && w.Job.StreamNote == StreamCodes.NeedsMuxer && w.Job.Stream != StreamKind.None;
        }
    }

    /// <summary>Joins the two files a finished stream job saved without the helper tool (picture and sound, or a transport stream wanted as MP4) into one file
    /// next to them, with the same fixed argument templates and copy-only mode as the download itself. The inputs are copied under fixed names into a work folder,
    /// the result is checked, placed under a free name and only then are the separate files removed. On any failure nothing is changed.</summary>
    public async Task<StreamJoinResult> JoinSavedStreamAsync(string id, CancellationToken cancellation = default)
    {
        ThrowIfDisposed();
        string video, directory, stem; string? audio; Work w;
        lock (gate)
        {
            w = Find(id);
            if (w.Job.Status != "Completed" || w.Job.StreamNote != StreamCodes.NeedsMuxer || w.Job.Stream == StreamKind.None) throw new InvalidOperationException("This download has nothing to join.");
            video = w.Job.FilePath; audio = w.Job.AdditionalFiles.FirstOrDefault();
            directory = Path.GetDirectoryName(video) ?? downloads; stem = StreamNaming.Stem(w.Job.FileName);
        }
        if (!File.Exists(video) || (audio is not null && !File.Exists(audio))) return StreamJoinResult.Failed;
        if (!tools.IsAvailable) return StreamJoinResult.ToolMissing;
        var vExt = Path.GetExtension(video).ToLowerInvariant(); var aExt = audio is null ? "" : Path.GetExtension(audio).ToLowerInvariant();
        string outExt = audio is null ? (vExt is ".m4a" or ".aac" or ".mp3" ? ".m4a" : ".mp4") : vExt == ".webm" && aExt == ".webm" ? ".webm" : vExt == ".webm" || aExt == ".webm" ? ".mkv" : ".mp4";
        var work = Path.Combine(state, id + ".join");
        try
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            var vIn = Path.Combine(work, "video" + vExt); File.Copy(video, vIn, true);
            var outPath = Path.Combine(work, "out" + outExt);
            List<string> args;
            if (audio is not null)
            {
                var aIn = Path.Combine(work, "audio" + aExt); File.Copy(audio, aIn, true);
                args = FfmpegCommands.MuxTracks(vIn, aIn, outPath, aExt is ".aac" or ".ts");
            }
            else args = FfmpegCommands.Rewrap(vIn, outPath, vExt is ".m4a" or ".aac" or ".mp3", vExt is ".ts" or ".aac");
            ToolRunResult result;
            try { result = await tools.RunAsync(args, options.ToolTimeout, cancellation, work).ConfigureAwait(false); }
            catch (ToolException ex) { return ex.Code == ToolException.NotInstalled ? StreamJoinResult.ToolMissing : StreamJoinResult.Failed; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return StreamJoinResult.Failed; }
            if (result.TimedOut || result.ExitCode != 0 || !File.Exists(outPath) || new FileInfo(outPath).Length == 0) return StreamJoinResult.Failed;
            lock (gate)
            {
                if (w.Removed || disposed || w.Job.Status != "Completed") return StreamJoinResult.Failed;
                var name = AvailableName(DownloadFileNames.Sanitize(stem + outExt), w, directory);
                var staging = StagingFile(w); Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
                File.Move(outPath, staging, true);
                PlaceLocked(w, staging, name, directory);
                var placed = Path.Combine(directory, name);
                foreach (var old in new[] { video, audio }) { if (old is null) continue; try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
                w.Job.FileName = name; w.Job.FilePath = placed; w.Job.AdditionalFiles = Array.Empty<string>(); w.Job.StreamNote = null;
                w.Total = w.Job.TotalBytes = w.Job.DownloadedBytes = new FileInfo(placed).Length;
                try { SaveLocked(); } catch (Exception ex) { LastPersistenceError = ex.Message; }
            }
            Notify();
            return StreamJoinResult.Joined;
        }
        finally { try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}
