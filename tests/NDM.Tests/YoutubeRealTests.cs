using NDM.Core;

namespace NDM.Tests;

/// <summary>Opt-in network test, never called by the ordinary suite. Big Buck Bunny is the public Blender Foundation sample.</summary>
internal static class YoutubeRealTests
{
    public static async Task<int> Run(string scratch)
    {
        var service = new YoutubeService(scratch);
        try
        {
            Console.WriteLine("REAL_YOUTUBE_STATE=" + scratch);
            if (!service.IsInstalled) await service.InstallAsync();
            using (var extractor = service.Extractor.OpenVerified()) Console.WriteLine("EXTRACTOR_SHA256=" + extractor.Sha256);
            using (var runtime = service.Runtime.OpenVerified()) Console.WriteLine("RUNTIME_SHA256=" + runtime.Sha256);
            var metadata = await service.InspectAsync(YoutubeTests.Url);
            Console.WriteLine("REAL_YOUTUBE_FORMATS=" + metadata.Formats.Count);
            var choice = metadata.Formats.Where(f => f.Audio && !f.Video).OrderBy(f => f.Bitrate).FirstOrDefault() ?? metadata.Formats.First(f => f.Video && f.Audio);
            var tools = new ToolManager(scratch); if (!tools.IsInstalled) await tools.InstallAsync();
            var selections = new List<YoutubeSelection> { new(choice.Id) };
            var video = metadata.Formats.Where(f => f.Video && !f.Audio && f.Height > 0).OrderBy(f => f.Height).FirstOrDefault();
            if (video is not null && !choice.Video) selections.Add(new(video.Id, choice.Id));
            if (!choice.Video) selections.Add(new(choice.Id, Mp3: true));
            for (int i = 0; i < selections.Count; i++)
            {
                var output = await service.DownloadAsync(YoutubeTests.Url, selections[i], Path.Combine(scratch, "output-" + i), tools, null, (_, _) => { }, default);
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
