using Velopack;
using Velopack.Sources;
using WinShot.App.Updates;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class StaticUpdateSourceTests
{
    [Fact]
    public async Task ReleaseCheckUsesPinnedIndexWithoutPlatformQuery()
    {
        var downloader = new RecordingDownloader();
        var source = new StaticUpdateSource(new Uri("https://example.com/releases/stable/"), downloader, 0.5);

        await source.GetReleaseFeed(null!, "RelicScreenshot", "stable", Guid.NewGuid());

        Assert.Equal("https://example.com/releases/stable/releases.stable.json", downloader.RequestedUrl);
        await Assert.ThrowsAsync<ArgumentException>(() => source.GetReleaseFeed(null!, "RelicScreenshot", "beta"));
    }

    private sealed class RecordingDownloader : HttpClientFileDownloader
    {
        public string? RequestedUrl { get; private set; }

        public override Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout)
        {
            RequestedUrl = url;
            return Task.FromResult("{\"Assets\":[]}");
        }
    }
}
