namespace NDM.Core;

/// <summary>Exact private runtime files from the pinned supplier archive, including ffprobe used by the extractor.</summary>
public sealed record ToolFilePin(string Name, long Size, string Sha256);

internal static class FfmpegRuntime
{
    internal static readonly IReadOnlyList<ToolFilePin> Files = Array.AsReadOnly(new ToolFilePin[]
    {
        new("avcodec-63.dll", 71996928L, "1cee2dd945376aec754456e6d1fe777367c3eeb16505847132d494412f96d924"),
        new("avdevice-63.dll", 153088L, "b3dcf2db9d38578d51ca362bdc2a29fb3f938a8591386694754a6c8f14ecef25"),
        new("avfilter-12.dll", 13451264L, "ad4ae19a97d0eec8409d11275fd6b005280ab996129386413a9717525d3be04f"),
        new("avformat-63.dll", 3944448L, "f8a164392c9684a6a7d467f3c02044265593c3e3a8ffc36bc740ec292f057225"),
        new("avutil-61.dll", 2737152L, "8aa1abdfe0c95821ae93419b4413d37fac95bf4a7e2eab20c73f4b1ee5c73d96"),
        new("ffprobe.exe", 219136L, "30c3821153c8f3f2d6aba364aa92df4b5021d2c62b7956a6bf61780b535c6dac"),
        new("swresample-7.dll", 178688L, "0e5f9d881466d39fff9ea5e77823f91dabe69be78b3a405fe09e73c6f960a7f9"),
        new("swscale-10.dll", 2304512L, "41b3262668474b43194b9ef42f0304b92b8a407f11dda6ecea250b6dc36c5c47"),
    });
}
