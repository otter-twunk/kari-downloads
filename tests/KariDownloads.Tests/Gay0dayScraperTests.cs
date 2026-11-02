using Cove.Core.DTOs;
using Cove.Plugins;
using KariDownloads.Tests.Infrastructure;
using Gay0dayScraper;

namespace KariDownloads.Tests;

public sealed class Gay0dayScraperTests
{
    private const string VideoPageUrl = "https://gay0day.com/videos/320814/surfer-boys-ander-big-ian-tested/";
    private const string RealisticVideoHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <title>Surfer boys Ander, Big &amp; Ian get their gifted talents tested by a hung stud @ Gay0Day</title>
        <meta property="og:title" content="Surfer boys Ander, Big &amp; Ian get their gifted talents tested by a hung stud @ Gay0Day">
        <meta property="og:description" content="Watch Surfer boys Ander, Big &amp; Ian get their gifted talents tested by a hung stud.">
        <meta property="og:image" content="https://gay0day.com/contents/videos_screenshots/320000/320814/preview.jpg">

        <script type="application/ld+json">
        {
          "@context": "https://schema.org",
          "@type": "VideoObject",
          "name": "Surfer boys Ander, Big & Ian get their gifted talents tested by a hung stud",
          "description": "Watch Surfer boys Ander, Big & Ian get their gifted talents tested by a hung stud gay porn video hd as completely free.",
          "thumbnailUrl": "https://gay0day.com/contents/videos_screenshots/320000/320814/preview.jpg",
          "uploadDate": "2026-11-02T04:51:07+00:00",
          "duration": "PT11M20S",
          "contentUrl": "https://gay0day.com/get_file/3/46e6992f5069357eedfa794668b56d7e/320000/320814/320814_720p.mp4/"
        }
        </script>
        </head>
        <body>
        <div class="content">
            <div class="headline">
                <h1>Surfer boys Ander, Big &amp; Ian get their gifted talents tested by a hung stud</h1>
            </div>
            <div class="video-info">
                <div class="block-details">
                    <div class="info">
                        <div class="item">
                            Sponsored by:
                            <a href="https://gay0day.com/sites/ander-borges/">Ander Borges</a>
                        </div>
                        <div class="item">
                            Tags:
                            <a href="https://gay0day.com/tags/surfer/" title="surfer">surfer</a>
                            <a href="https://gay0day.com/tags/beach/" title="beach">beach</a>
                        </div>
                        <div class="item">
                            Models:
                            <a href="https://gay0day.com/models/big-marcos/">Big Marcos</a>
                            <a href="https://gay0day.com/models/ian-surfista/">Ian Surfista</a>
                            <a href="https://gay0day.com/models/ander-borges/">Ander Borges</a>
                        </div>
                    </div>
                </div>
            </div>
        </div>
        </body>
        </html>
        """;

    private const string RealisticSearchResultsHtml = """
        <!DOCTYPE html>
        <html>
        <body>
        <div class="list-videos">
            <div class="item">
                <a href="https://gay0day.com/videos/250021/corbin-fisher-surfer-boy-dane/" title="Corbin Fisher - Surfer boy Dane takes Wesley's raw cock">
                    <div class="img">
                        <img class="thumb lazy-load" data-original="https://gay0day.com/contents/videos_screenshots/250000/250021/385x217/14.jpg" alt="Corbin Fisher - Surfer boy Dane" />
                    </div>
                </a>
            </div>
            <div class="item">
                <a href="https://gay0day.com/videos/259227/aussie-20yo-leeroy-surfer/" title="Aussie 20yo Leeroy Queensland Surfer Lad">
                    <div class="img">
                        <img class="thumb lazy-load" data-original="https://gay0day.com/contents/videos_screenshots/259000/259227/385x217/8.jpg" alt="Aussie 20yo Leeroy Queensland Surfer Lad" />
                    </div>
                </a>
            </div>
        </div>
        </body>
        </html>
        """;

    private const string RealisticPerformerHtml = """
        <!DOCTYPE html>
        <html>
        <head>
            <title>Ander Borges's Gay porn model @ Gay0Day</title>
            <meta property="og:title" content="Ander Borges's Gay porn model">
            <meta property="og:description" content="Ander Borges's gay porn star profile. Find all his sex videos right here!">
            <meta property="og:image" content="https://gay0day.com/contents/models/ander-borges/photo.jpg">
        </head>
        <body>
            <div class="headline">
                <h2>Gay actors Ander Borges</h2>
            </div>
            <div class="block-model">
                <div class="img">
                    <img src="https://gay0day.com/contents/models/ander-borges/photo.jpg" alt="Ander Borges" />
                </div>
                <ul class="model-list">
                    <li>Country: <span>Brazil</span></li>
                    <li>City: <span>Rio de Janeiro</span></li>
                    <li>Age: <span>26</span></li>
                </ul>
                <div class="desc">Ander Borges - Brazilian gay porn actor and stud.</div>
            </div>
        </body>
        </html>
        """;

    [Theory]
    [InlineData("https://gay0day.com/videos/320814/surfer-boys/", true)]
    [InlineData("https://gay0day.com/videos/12345", true)]
    [InlineData("https://www.gay0day.com/videos/12345/", true)]
    [InlineData("https://gay0day.com/models/ander-borges/", false)]
    [InlineData("https://othertube.com/videos/12345/", false)]
    [InlineData(null, false)]
    public void IsSupportedVideoUrl_ValidatesCorrectly(string? url, bool expected)
    {
        var result = Gay0dayScraperExtension.IsSupportedVideoUrl(url);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("https://gay0day.com/models/ander-borges/", true)]
    [InlineData("https://gay0day.com/models/big-marcos", true)]
    [InlineData("https://www.gay0day.com/models/ander-borges/", true)]
    [InlineData("https://gay0day.com/videos/320814/", false)]
    [InlineData("https://othertube.com/models/ander-borges/", false)]
    [InlineData(null, false)]
    public void IsSupportedPerformerUrl_ValidatesCorrectly(string? url, bool expected)
    {
        var result = Gay0dayScraperExtension.IsSupportedPerformerUrl(url);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ScrapeVideoAsync_ExtractsAllFieldsFromRealisticHtml()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(VideoPageUrl, RealisticVideoHtml);

        var extension = new Gay0dayScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                Gay0dayScraperExtension.VideoScraperId,
                new VideoScrapeInput { Url = VideoPageUrl },
                new ScraperPermissions(["gay0day.com"])),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(Gay0dayScraperExtension.VideoScraperId, result.SourceScraperId);
        Assert.Equal("320814", result.Code);
        Assert.Equal("2026-11-02", result.Date);
        Assert.Equal("Ander Borges", result.StudioName);
        Assert.Contains("Surfer boys Ander, Big & Ian", result.Title);
        Assert.DoesNotContain("@ Gay0Day", result.Title);
        Assert.Contains("Big Marcos", result.PerformerNames);
        Assert.Contains("Ian Surfista", result.PerformerNames);
        Assert.Contains("Ander Borges", result.PerformerNames);
        Assert.Contains("surfer", result.TagNames);
        Assert.Contains("beach", result.TagNames);
        Assert.Equal("https://gay0day.com/contents/videos_screenshots/320000/320814/preview.jpg", result.ImageUrl);
    }

    [Fact]
    public async Task SearchVideosAsync_ParsesSearchResults()
    {
        const string searchUrl = "https://gay0day.com/search/surfer/";

        using var harness = new FakeDownloaderHost()
            .MapHtml(searchUrl, RealisticSearchResultsHtml);

        var extension = new Gay0dayScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var results = await extension.SearchVideosAsync(
            new ScraperRequest<string>(
                Gay0dayScraperExtension.VideoScraperId,
                "surfer",
                new ScraperPermissions(["gay0day.com"])),
            CancellationToken.None);

        Assert.NotNull(results);
        Assert.Equal(2, results.Count);

        var first = results[0];
        Assert.Equal("250021", first.Code);
        Assert.Contains("Corbin Fisher - Surfer boy Dane", first.Title);
        Assert.Equal("https://gay0day.com/videos/250021/corbin-fisher-surfer-boy-dane/", first.Urls[0]);
        Assert.Equal("https://gay0day.com/contents/videos_screenshots/250000/250021/385x217/14.jpg", first.ImageUrl);

        var second = results[1];
        Assert.Equal("259227", second.Code);
        Assert.Equal("Aussie 20yo Leeroy Queensland Surfer Lad", second.Title);
    }

    [Fact]
    public async Task ScrapeVideoAsync_WithTitleOnly_FallsBackToSearchAndScrapes()
    {
        const string searchUrl = "https://gay0day.com/search/surfer%20boys/";
        const string firstResultUrl = "https://gay0day.com/videos/320814/surfer-boys-ander-big-ian-tested/";

        using var harness = new FakeDownloaderHost()
            .MapHtml(searchUrl, $"""
                <div class="list-videos">
                    <div class="item">
                        <a href="{firstResultUrl}" title="Surfer boys Ander, Big &amp; Ian"></a>
                    </div>
                </div>
                """)
            .MapHtml(firstResultUrl, RealisticVideoHtml);

        var extension = new Gay0dayScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                Gay0dayScraperExtension.VideoScraperId,
                new VideoScrapeInput { Title = "surfer boys" },
                new ScraperPermissions(["gay0day.com"])),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("320814", result.Code);
        Assert.Contains("Surfer boys Ander, Big & Ian", result.Title);
    }

    [Fact]
    public async Task ScrapePerformerAsync_ExtractsPerformerDetails()
    {
        const string performerUrl = "https://gay0day.com/models/ander-borges/";

        using var harness = new FakeDownloaderHost()
            .MapHtml(performerUrl, RealisticPerformerHtml);

        var extension = new Gay0dayScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.ScrapePerformerAsync(
            new ScraperRequest<PerformerScrapeInput>(
                Gay0dayScraperExtension.PerformerScraperId,
                new PerformerScrapeInput { Url = performerUrl },
                new ScraperPermissions(["gay0day.com"])),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(Gay0dayScraperExtension.PerformerScraperId, result.SourceScraperId);
        Assert.Equal("Ander Borges", result.Name);
        Assert.Equal("Male", result.Gender);
        Assert.Equal("Brazil", result.Country);
        Assert.Equal("Ander Borges - Brazilian gay porn actor and stud.", result.Details);
        Assert.Equal("https://gay0day.com/contents/models/ander-borges/photo.jpg", result.ImageUrl);
        Assert.Equal(performerUrl, result.Urls[0]);
    }

    [Fact]
    public async Task ScrapeVideoAsync_DoesNotExtractPerformersFromFooterOrNavMenu()
    {
        const string pageUrl = "https://gay0day.com/videos/320814/test-scene/";
        const string htmlWithFooter = """
            <!DOCTYPE html>
            <html>
            <body>
            <div class="header">
                <a href="https://gay0day.com/models/" id="item9">PornStars</a>
            </div>
            <div id="tab_video_info" class="tab-content hidden">
                <div class="block-details">
                    <div class="info">
                        <div class="item">
                            Models:
                            <a href="https://gay0day.com/models/big-marcos/">Big Marcos</a>
                            <a href="https://gay0day.com/models/ian-surfista/">Ian Surfista</a>
                        </div>
                    </div>
                </div>
            </div>
            <div class="footer-browse">
                <h3>Pornstars &amp; Models</h3>
                <ul>
                    <li><a href="https://gay0day.com/models/austin-young/">Austin Young</a></li>
                    <li><a href="https://gay0day.com/models/cutler-x/">Cutler X</a></li>
                    <li><a href="https://gay0day.com/models/blake-mitchell/">Blake Mitchell</a></li>
                </ul>
            </div>
            </body>
            </html>
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(pageUrl, htmlWithFooter);

        var extension = new Gay0dayScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                Gay0dayScraperExtension.VideoScraperId,
                new VideoScrapeInput { Url = pageUrl },
                new ScraperPermissions(["gay0day.com"])),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.PerformerNames.Count);
        Assert.Contains("Big Marcos", result.PerformerNames);
        Assert.Contains("Ian Surfista", result.PerformerNames);

        // Strict isolation assertions:
        Assert.DoesNotContain("Austin Young", result.PerformerNames);
        Assert.DoesNotContain("Cutler X", result.PerformerNames);
        Assert.DoesNotContain("Blake Mitchell", result.PerformerNames);
        Assert.DoesNotContain("PornStars", result.PerformerNames);
    }

    [Fact]
    public async Task ScrapeVideoAsync_ReturnsEmptyPerformers_WhenVideoHasNoModels_EvenIfPageHasFooterModels()
    {
        const string pageUrl = "https://gay0day.com/videos/45678/solo-clip/";
        const string htmlNoVideoModels = """
            <!DOCTYPE html>
            <html>
            <body>
            <div id="tab_video_info" class="tab-content hidden">
                <div class="block-details">
                    <div class="info">
                        <div class="item">
                            Tags:
                            <a href="https://gay0day.com/tags/solo/" title="solo">solo</a>
                        </div>
                    </div>
                </div>
            </div>
            <div class="footer-browse">
                <h3>Pornstars &amp; Models</h3>
                <ul>
                    <li><a href="https://gay0day.com/models/austin-young/">Austin Young</a></li>
                    <li><a href="https://gay0day.com/models/cutler-x/">Cutler X</a></li>
                </ul>
            </div>
            </body>
            </html>
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(pageUrl, htmlNoVideoModels);

        var extension = new Gay0dayScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                Gay0dayScraperExtension.VideoScraperId,
                new VideoScrapeInput { Url = pageUrl },
                new ScraperPermissions(["gay0day.com"])),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.PerformerNames);
    }
}
