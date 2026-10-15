using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace WinShot.App.Updates;

// Our feed is a static, pinned directory. SimpleWebSource adds platform/version
// query parameters, which our downloader deliberately rejects. Request only the
// fixed index; package downloads retain the existing origin and integrity checks.
internal sealed class StaticUpdateSource(Uri baseUri, IFileDownloader downloader, double timeout)
    : SimpleWebSource(baseUri, downloader, timeout)
{
    public override async Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (channel != VelopackUpdateBackend.Channel)
            throw new ArgumentException("Only the stable update channel is supported.", nameof(channel));

        string json = await Downloader.DownloadString(
            new Uri(BaseUri, "releases.stable.json").AbsoluteUri,
            timeout: Timeout).ConfigureAwait(false);
        return VelopackAssetFeed.FromJson(json);
    }
}
