using Cove.Core.DTOs;
using Cove.Plugins;
using KariDownloads.Tests.Infrastructure;
using BoyfriendtvScraper;

namespace KariDownloads.Tests;

public sealed class BoyfriendtvScraperTests
{
    private const string VideoUrl = "https://www.boyfriendtv.com/videos/1426460/handsome-twink-audition/";
    private const string PerformerUrl = "https://www.boyfriendtv.com/pornstars/scott-demarco-2077/";

    private const string VideoPageHtml = """
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <title>Handsome Twink Audition | BoyFriendTV</title>
            <meta property="og:title" content="Handsome Twink Audition | BoyFriendTV" />
            <meta property="og:description" content="Exclusive bedroom audition with hot action." />
            <script type="application/ld+json">
            {
                "@context": "https://schema.org",
                "@type": "VideoObject",
                "name": "Handsome Twink Audition",
                "description": "Exclusive bedroom audition with hot action.",
                "uploadDate": "2024-03-20T10:30:00Z",
                "thumbnailUrl": ["https://cdn.boyfriendtv.com/thumbs/1426460.jpg"],
                "keywords": ["Audition", "Bareback"],
                "actor": [
                    {"@type": "Person", "name": "Joey Mills"}
                ]
            }
            </script>
        </head>
        <body>
            <a href="/pornstars/joey-mills/videos/">Joey Mills</a>
            <a href="/pornstars/tyler-hill/videos/">Tyler Hill</a>
            <a href="/tags/audition">Audition</a>
            <a href="/tags/twink">Twink</a>
        </body>
        </html>
        """;

    private const string VideoSearchHtml = """
        <!doctype html>
        <html>
        <body>
            <div class="video-item">
                <a href="/videos/1426460/handsome-twink-audition/">Handsome Twink Audition | BoyFriendTV</a>
            </div>
            <div class="video-item">
                <a href="/videos/1500000/gym-buddies-workout/">Gym Buddies Workout - BoyFriendTV.com</a>
            </div>
        </body>
        </html>
        """;

    private const string PerformerPageHtml = """
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <title>Scott Demarco Gay Pornstar - BoyFriendTV.com</title>
            <meta property="og:title" content="Scott Demarco Gay Pornstar - BoyFriendTV.com" />
            <meta property="og:description" content="Popular performer profile." />
            <meta property="og:image" content="https://cdn.boyfriendtv.com/pornstars/scott-demarco.jpg" />
            <script type="application/ld+json">
            {
                "@context": "https://schema.org",
                "@type": "Person",
                "name": "Scott Demarco",
                "description": "Popular performer profile.",
                "image": "https://cdn.boyfriendtv.com/pornstars/scott-demarco.jpg"
            }
            </script>
        </head>
        <body></body>
        </html>
        """;

    private const string PerformerSearchHtml = """
        <!doctype html>
        <html>
        <body>
            <div class="performer-item">
                <a href="/pornstars/scott-demarco-2077/">Scott Demarco Gay Pornstar - BoyFriendTV.com</a>
            </div>
            <div class="performer-item">
                <a href="/pornstars/joey-mills/">Joey Mills | BoyFriendTV</a>
            </div>
        </body>
        </html>
        """;

    [Fact]
    public void IsSupportedVideoUrl_And_IsSupportedPerformerUrl_Work()
    {
        Assert.True(BoyfriendtvScraperExtension.IsSupportedVideoUrl("https://www.boyfriendtv.com/videos/1426460/selector/"));
        Assert.False(BoyfriendtvScraperExtension.IsSupportedVideoUrl("https://www.boyfriendtv.com/pornstars/scott-demarco/"));
        Assert.True(BoyfriendtvScraperExtension.IsSupportedPerformerUrl("https://www.boyfriendtv.com/pornstars/scott-demarco/"));
        Assert.False(BoyfriendtvScraperExtension.IsSupportedPerformerUrl("https://www.boyfriendtv.com/videos/1426460/"));
    }

    [Fact]
    public async Task ScrapeVideoAsync_ExtractsMetadataFromFixture()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(VideoUrl, VideoPageHtml);

        var extension = new BoyfriendtvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                BoyfriendtvScraperExtension.VideoScraperId,
                new VideoScrapeInput { Url = VideoUrl },
                new ScraperPermissions(["boyfriendtv.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Handsome Twink Audition", scraped.Title);
        Assert.Equal("1426460", scraped.Code);
        Assert.Equal("2024-03-20", scraped.Date);
        Assert.Equal("Exclusive bedroom audition with hot action.", scraped.Details);
        Assert.Equal("BoyFriendTV", scraped.StudioName);
        Assert.Equal("https://cdn.boyfriendtv.com/thumbs/1426460.jpg", scraped.ImageUrl);
        Assert.Contains("Joey Mills", scraped.PerformerNames);
        Assert.Contains("Tyler Hill", scraped.PerformerNames);
        Assert.Contains("Audition", scraped.TagNames);
        Assert.Contains("Twink", scraped.TagNames);
        Assert.Contains("Bareback", scraped.TagNames);
    }

    [Fact]
    public async Task SearchVideosAsync_ParsesVideoResults()
    {
        const string searchUrl = "https://www.boyfriendtv.com/search/videos/twink/";
        using var harness = new FakeDownloaderHost()
            .MapHtml(searchUrl, VideoSearchHtml);

        var extension = new BoyfriendtvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var results = await extension.SearchVideosAsync(
            new ScraperRequest<string>(
                BoyfriendtvScraperExtension.VideoScraperId,
                "twink",
                new ScraperPermissions(["boyfriendtv.com"])),
            CancellationToken.None);

        Assert.NotNull(results);
        Assert.Equal(2, results.Count);
        Assert.Equal("Handsome Twink Audition", results[0].Title);
        Assert.Equal("1426460", results[0].Code);
        Assert.Equal("Gym Buddies Workout", results[1].Title);
        Assert.Equal("1500000", results[1].Code);
    }

    [Fact]
    public async Task ScrapePerformerAsync_ExtractsPerformerMetadata()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(PerformerUrl, PerformerPageHtml);

        var extension = new BoyfriendtvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapePerformerAsync(
            new ScraperRequest<PerformerScrapeInput>(
                BoyfriendtvScraperExtension.PerformerScraperId,
                new PerformerScrapeInput { Url = PerformerUrl },
                new ScraperPermissions(["boyfriendtv.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Scott Demarco", scraped.Name);
        Assert.Equal("Male", scraped.Gender);
        Assert.Equal("Popular performer profile.", scraped.Details);
        Assert.Equal("https://cdn.boyfriendtv.com/pornstars/scott-demarco.jpg", scraped.ImageUrl);
    }

    [Fact]
    public async Task ScrapePerformerAsync_FallsBackToSlugWhenPageFailsOrBlocked()
    {
        // Unmapped URL simulates Cloudflare 403 / network block
        using var harness = new FakeDownloaderHost();

        var extension = new BoyfriendtvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapePerformerAsync(
            new ScraperRequest<PerformerScrapeInput>(
                BoyfriendtvScraperExtension.PerformerScraperId,
                new PerformerScrapeInput { Url = PerformerUrl },
                new ScraperPermissions(["boyfriendtv.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Scott Demarco", scraped.Name);
        Assert.Equal("Male", scraped.Gender);
    }

    [Fact]
    public async Task SearchPerformersAsync_ParsesPerformerResults()
    {
        const string searchUrl = "https://www.boyfriendtv.com/search/pornstars/scott/";
        using var harness = new FakeDownloaderHost()
            .MapHtml(searchUrl, PerformerSearchHtml);

        var extension = new BoyfriendtvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var results = await extension.SearchPerformersAsync(
            new ScraperRequest<string>(
                BoyfriendtvScraperExtension.PerformerScraperId,
                "scott",
                new ScraperPermissions(["boyfriendtv.com"])),
            CancellationToken.None);

        Assert.NotNull(results);
        Assert.Equal(2, results.Count);
        Assert.Equal("Scott Demarco", results[0].Name);
        Assert.Equal("Male", results[0].Gender);
        Assert.Equal("Joey Mills", results[1].Name);
    }
}
