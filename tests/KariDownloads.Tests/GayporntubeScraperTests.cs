using Cove.Core.DTOs;
using Cove.Plugins;
using KariDownloads.Tests.Infrastructure;
using GayporntubeScraper;

namespace KariDownloads.Tests;

public sealed class GayporntubeScraperTests
{
    private const string VideoUrl = "https://www.gayporntube.com/video/2144881/frat-guy-pretend-to-ride-naked";
    private const string BareUrl = "https://www.gayporntube.com/video/2144881/";
    private const string EmbedUrl = "https://www.gayporntube.com/embed/2144881";
    private const string NormalizedBareUrl = "https://www.gayporntube.com/video/2144881/video";

    private const string VideoDetailHtml = """
        <!DOCTYPE html>
        <html lang="en" class="view_video">
        <head>
            <title>Frat guy pretend to ride naked | GayPornTube</title>
            <meta property="og:title" content="Frat guy pretend to ride naked"/>
            <meta property="og:image" content="https://static.gayporntube.com/contents/videos_screenshots/2144000/2144881/preview.jpg?v=1"/>
            <meta property="og:description" content="."/>
            <meta property="video:release_date" content="2026-11-02T06:27:27Z"/>
            <meta property="video:duration" content="29"/>
            <meta property="video:tag" content="Frat Guys"/>
            <meta property="video:tag" content="Muscle Boys"/>
            <meta name="keywords" content="Frat Guys, Muscle Boys"/>
            <script type="application/ld+json">
            {
                "@context": "https://schema.org",
                "@type": "VideoObject",
                "name": "Frat guy pretend to ride naked",
                "description": ".",
                "thumbnailUrl": "https://static.gayporntube.com/contents/videos_screenshots/2144000/2144881/preview.jpg?v=1",
                "uploadDate": "2026-11-02T06:27:27Z",
                "duration": "PT0H0M29S"
            }
            </script>
        </head>
        <body>
            <div class="video-info">
                <h1 class="title">Frat guy pretend to ride naked</h1>
                <div class="video-info-categories">
                    <a class="link12" href="https://www.gayporntube.com/channels/12/frat-guys/page1.html">Frat Guys</a>
                    <a class="link12" href="https://www.gayporntube.com/channels/18/muscle-boys/page1.html">Muscle Boys</a>
                </div>
            </div>
        </body>
        </html>
        """;

    private const string SearchResultsHtml = """
        <!DOCTYPE html>
        <html>
        <body>
            <div class="content">
                <div class="item item-col" data-video-id="1501731">
                    <a class="image image-ar" href="https://www.gayporntube.com/video/1501731/twink-bro-4" title="Twinkie Tuesday">
                        <img data-webp="https://static.gayporntube.com/contents/videos_screenshots/1501000/1501731/246x183/2.jpg" alt="Twinkie Tuesday" />
                        <div class="duration">12:34</div>
                    </a>
                    <div class="item-wrapper">
                        <a href="https://www.gayporntube.com/video/1501731/twink-bro-4" class="title">
                            Twinkie Tuesday
                        </a>
                    </div>
                </div>
                <div class="item item-col" data-video-id="1520626">
                    <a class="image image-ar" href="https://www.gayporntube.com/video/1520626/winter-heating-up" title="Winter Heating Up">
                        <img data-webp="https://static.gayporntube.com/contents/videos_screenshots/1520000/1520626/246x183/1.jpg" alt="Winter Heating Up" />
                        <div class="duration">08:15</div>
                    </a>
                    <div class="item-wrapper">
                        <a href="https://www.gayporntube.com/video/1520626/winter-heating-up" class="title">
                            Winter Heating Up
                        </a>
                    </div>
                </div>
            </div>
        </body>
        </html>
        """;

    [Fact]
    public void IsSupportedUrl_ValidatesCorrectly()
    {
        Assert.True(GayporntubeScraperExtension.IsSupportedUrl("https://www.gayporntube.com/video/2144881/frat-guy-pretend-to-ride-naked"));
        Assert.True(GayporntubeScraperExtension.IsSupportedUrl("https://gayporntube.com/video/2144881/"));
        Assert.True(GayporntubeScraperExtension.IsSupportedUrl("https://www.gayporntube.com/embed/2144881"));
        Assert.False(GayporntubeScraperExtension.IsSupportedUrl("https://www.gayporntube.com/channels/12/frat-guys/page1.html"));
        Assert.False(GayporntubeScraperExtension.IsSupportedUrl("https://www.gayporntube.com/blogs/"));
        Assert.False(GayporntubeScraperExtension.IsSupportedUrl("https://example.com/video/2144881"));
        Assert.False(GayporntubeScraperExtension.IsSupportedUrl(""));
    }

    [Fact]
    public void ToCanonicalVideoUrl_HandlesBareAndEmbedUrls()
    {
        Assert.Equal("https://www.gayporntube.com/video/2144881/video", GayporntubeScraperExtension.ToCanonicalVideoUrl("https://gayporntube.com/video/2144881/"));
        Assert.Equal("https://www.gayporntube.com/video/2144881/video", GayporntubeScraperExtension.ToCanonicalVideoUrl("https://www.gayporntube.com/embed/2144881"));
        Assert.Equal("https://www.gayporntube.com/video/2144881/frat-guy-pretend-to-ride-naked", GayporntubeScraperExtension.ToCanonicalVideoUrl("https://www.gayporntube.com/video/2144881/frat-guy-pretend-to-ride-naked"));
    }

    [Fact]
    public async Task ScrapeVideoAsync_ExtractsStructuredMetadataFromFixture()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(VideoUrl, VideoDetailHtml);

        var extension = new GayporntubeScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                GayporntubeScraperExtension.PrimaryScraperId,
                new VideoScrapeInput { Url = VideoUrl },
                new ScraperPermissions(["gayporntube.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Frat guy pretend to ride naked", scraped.Title);
        Assert.Equal("2144881", scraped.Code);
        Assert.Equal("2026-11-02", scraped.Date);
        Assert.Null(scraped.Details); // "." stripped
        Assert.Equal("https://static.gayporntube.com/contents/videos_screenshots/2144000/2144881/preview.jpg?v=1", scraped.ImageUrl);
        Assert.Equal("GayPornTube", scraped.StudioName);
        Assert.Contains("Frat Guys", scraped.TagNames);
        Assert.Contains("Muscle Boys", scraped.TagNames);
        Assert.Contains(VideoUrl, scraped.Urls);
    }

    [Fact]
    public async Task ScrapeVideoAsync_WithBareUrlWithoutSlug_NormalizesAndProducesSluggedCanonical()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(NormalizedBareUrl, VideoDetailHtml);

        var extension = new GayporntubeScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                GayporntubeScraperExtension.PrimaryScraperId,
                new VideoScrapeInput { Url = BareUrl },
                new ScraperPermissions(["gayporntube.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Frat guy pretend to ride naked", scraped.Title);
        Assert.Equal("2144881", scraped.Code);
        Assert.Contains("https://www.gayporntube.com/video/2144881/frat-guy-pretend-to-ride-naked", scraped.Urls);
        Assert.Contains(BareUrl, scraped.Urls);
    }

    [Fact]
    public async Task SearchVideosAsync_ParsesSearchResults()
    {
        const string query = "twink";
        const string searchUrl = "https://www.gayporntube.com/search/videos/?q=twink";

        using var harness = new FakeDownloaderHost()
            .MapHtml(searchUrl, SearchResultsHtml);

        var extension = new GayporntubeScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var results = await extension.SearchVideosAsync(
            new ScraperRequest<string>(
                GayporntubeScraperExtension.PrimaryScraperId,
                query,
                new ScraperPermissions(["gayporntube.com"])),
            CancellationToken.None);

        Assert.NotNull(results);
        Assert.Equal(2, results.Count);

        var first = results[0];
        Assert.Equal("Twinkie Tuesday", first.Title);
        Assert.Equal("1501731", first.Code);
        Assert.Equal("https://www.gayporntube.com/video/1501731/twink-bro-4", first.Urls.First());
        Assert.Equal("GayPornTube", first.StudioName);

        var second = results[1];
        Assert.Equal("Winter Heating Up", second.Title);
        Assert.Equal("1520626", second.Code);
        Assert.Equal("https://www.gayporntube.com/video/1520626/winter-heating-up", second.Urls.First());
    }
}
