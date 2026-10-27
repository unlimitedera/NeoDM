using NDM.Core;
namespace NDM.App;

public static class OptionsHelper
{
    // The engine owns an immutable startup snapshot; preferences never disturb running jobs.
    public static DownloadOptions CreateDownloadOptions(AppSettings settings) => new()
    {
        MaxSegments = settings.MaxSegments,
        MaxConcurrentDownloads = settings.MaxConcurrentDownloads,
        RetryCount = settings.RetryCount,
        RequestTimeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
        EnableDynamicSegmentation = settings.EnableDynamicSegmentation,
        Identity = settings.IdentifyAs,
        CustomUserAgent = settings.IdentifyAs == IdentityMode.Custom && RequestIdentity.IsHeaderValue(settings.CustomUserAgent, RequestIdentity.MaxUserAgent) ? settings.CustomUserAgent : null,
        MarkOfTheWeb = settings.MarkDownloadedFiles,
        Proxy = settings.ProxyForDownloads ? settings.BuildProxy() : null
    };
}
