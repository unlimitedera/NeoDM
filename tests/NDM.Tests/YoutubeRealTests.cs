using NDM.Core;

namespace NDM.Tests;

/// <summary>Opt-in network test, never called by the ordinary suite. Big Buck Bunny is the public Blender Foundation sample.</summary>
internal static class YoutubeRealTests
{
    public static async Task<int> Run(string scratch, string? bundleDirectory = null)
    {
        bundleDirectory ??= Path.Combine(AppContext.BaseDirectory, "tools");
        Directory.CreateDirectory(scratch);
        var service = new YoutubeService(scratch,
            new ToolManager(scratch, YoutubeService.ExtractorPin, toolName: "yt-dlp", rawExecutable: true, bundleDirectory: bundleDirectory),
            new ToolManager(scratch, YoutubeService.RuntimePin, toolName: "deno", bundleDirectory: bundleDirectory));
        try
        {
            Console.WriteLine("REAL_YOUTUBE_STATE=" + scratch);
            if (!service.IsInstalled) throw new InvalidOperationException("Prepared bundled helpers are required. This test never installs tools.");
            using (var extractor = service.Extractor.OpenVerified()) Console.WriteLine("EXTRACTOR_SHA256=" + extractor.Sha256);
            using (var runtime = service.Runtime.OpenVerified()) Console.WriteLine("RUNTIME_SHA256=" + runtime.Sha256);
            var metadata = await service.InspectAsync(YoutubeTests.Url);
            Console.WriteLine("REAL_YOUTUBE_FORMATS=" + metadata.Formats.Count);
            var choice = metadata.Formats.Where(f => f.Audio && !f.Video).OrderBy(f => f.Bitrate).FirstOrDefault() ?? metadata.Formats.First(f => f.Video && f.Audio);
            var tools = new ToolManager(scratch, bundleDirectory: bundleDirectory);
            using (var muxer = tools.OpenVerified()) Console.WriteLine("FFMPEG_SHA256=" + muxer.Sha256);
            var selections = new List<YoutubeSelection> { new(choice.Id) };
            var video = metadata.Formats.Where(f => f.Video && !f.Audio && f.Height > 0).OrderBy(f => f.Height).FirstOrDefault();
            if (video is not null && !choice.Video) selections.Add(new(video.Id, choice.Id));
            if (!choice.Video) selections.Add(new(choice.Id, Mp3: true));
            for (int i = 0; i < selections.Count; i++)
            {
                var samples = new List<YoutubeProgress>();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                var output = await service.DownloadAsync(YoutubeTests.Url, selections[i], Path.Combine(scratch, "output-" + i), tools, null, (_, _) => { }, timeout.Token, sample =>
                {
                    samples.Add(sample);
                    Console.WriteLine("REAL_YOUTUBE_PROGRESS=" + System.Text.Json.JsonSerializer.Serialize(sample));
                });
                if (!samples.Any(s => s.Downloaded > 0) || samples.Zip(samples.Skip(1)).Any(pair => pair.Second.Downloaded < pair.First.Downloaded)) throw new Exception("Real helper progress must advance without resetting between tracks");
                if (samples.Any(s => s.Total > 0 && s.Total < s.Downloaded)) throw new Exception("Real helper total cannot be smaller than downloaded bytes");
                if (selections[i].AudioId is not null && !samples.Any(s => s.Phase == "merging")) throw new Exception("Real helper merge phase not observed");
                if (selections[i].Mp3 && !samples.Any(s => s.Phase == "converting")) throw new Exception("Real helper conversion phase not observed");
                var probe = await tools.RunAsync(FfmpegCommands.Probe(output), TimeSpan.FromSeconds(60));
                var duration = FfmpegCommands.ParseDuration(probe.Error + probe.Output);
                if (duration is not > 1) throw new Exception("Output duration not verified");
                if (selections[i].AudioId is not null && (!probe.Error.Contains("Video:") || !probe.Error.Contains("Audio:"))) throw new Exception("Merged output tracks not verified");
                using var file = File.OpenRead(output);
                Console.WriteLine("REAL_YOUTUBE_SELECTION=" + System.Text.Json.JsonSerializer.Serialize(selections[i]));
                Console.WriteLine("REAL_YOUTUBE_OUTPUT=" + output);
                Console.WriteLine("REAL_YOUTUBE_BYTES=" + file.Length);
                Console.WriteLine("REAL_YOUTUBE_SHA256=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)).ToLowerInvariant());
                Console.WriteLine("REAL_YOUTUBE_DURATION=" + duration);
            }
            return 0;
        }
        catch (YoutubeException ex) { Console.WriteLine("REAL_YOUTUBE_BLOCKED=" + ex.Code); return 2; }
        catch (Exception ex) { Console.WriteLine("REAL_YOUTUBE_FAILED=" + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
}
