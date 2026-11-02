using Cove.Core.DTOs;
using Cove.Plugins;
using GayFappyScraper;
using KariDownloads.Tests.Infrastructure;

namespace KariDownloads.Tests;

public sealed class GayFappyScraperTests
{
    private const string SampleVideoHtml = """
        <!DOCTYPE html>
        <html lang="en-US">
        <head>
            <title>Tiktoker blake hoffman jerks off - Gay Fappy</title>
            <meta property="og:title" content="Tiktoker blake hoffman jerks off" />
            <meta property="og:description" content="Tiktoker blake hoffman jerks off hot solo" />
            <meta property="og:image" content="https://gayfappy.com/wp-content/uploads/2025/09/tiktoker-blake-hoffman-jerks-off.jpg" />
            <meta property="article:published_time" content="2026-10-31T12:05:50+00:00" />
            <link rel="alternate" type="application/json" href="https://gayfappy.com/index.php/wp-json/wp/v2/posts/66189" />
        </head>
        <body>
            <article id="post-66189" class="post-66189 post category-videos tag-blake-hoffman tag-jerk-off tag-solo tag-young-twink">
                <h1 class="post-title entry-title">Tiktoker blake hoffman jerks off</h1>
                <time datetime="2026-10-31" class="post-date entry-date updated">October 31, 2026</time>
                <div class="entry-content">
                    <p class="post_desc">Tiktoker blake hoffman jerks off hot solo</p>
                </div>
                <span class="post-category"><a href="https://gayfappy.com/index.php/category/videos/" rel="tag">Videos</a></span>
                <span class="post-tag"><a href="https://gayfappy.com/index.php/tag/blake-hoffman/" rel="tag">blake hoffman</a>, <a href="https://gayfappy.com/index.php/tag/jerk-off/" rel="tag">jerk off</a>, <a href="https://gayfappy.com/index.php/tag/solo/" rel="tag">solo</a>, <a href="https://gayfappy.com/index.php/tag/young-twink/" rel="tag">young/twink</a></span>
            </article>
        </body>
        </html>
        """;

    private const string SampleWpJsonPost = """
        {
          "id": 66189,
          "date": "2026-10-31T12:05:50",
          "date_gmt": "2026-10-31T12:05:50",
          "slug": "tiktoker-blake-hoffman-jerks-off",
          "link": "https://gayfappy.com/index.php/66189/tiktoker-blake-hoffman-jerks-off/",
          "title": { "rendered": "Tiktoker blake hoffman jerks off" },
          "content": {
            "rendered": "<p class=\"post_desc\">Tiktoker blake hoffman jerks off</p>"
          },
          "excerpt": { "rendered": "<p>Tiktoker blake hoffman jerks off</p>" },
          "class_list": [
            "post-66189", "category-videos", "tag-blake-hoffman", "tag-jerk-off", "tag-solo", "tag-young-twink"
          ],
          "yoast_head_json": {
            "og_image": [{ "url": "https://gayfappy.com/wp-content/uploads/2025/09/tiktoker-blake-hoffman-jerks-off.jpg" }],
            "schema": {
              "@graph": [
                {
                  "@type": "Article",
                  "headline": "Tiktoker blake hoffman jerks off",
                  "keywords": ["blake hoffman", "jerk off", "solo", "young/twink"]
                }
              ]
            }
          }
        }
        """;

    private const string SampleWpJsonSearch = """
        [
          {
            "id": 66189,
            "date": "2026-10-31T12:05:50",
            "slug": "tiktoker-blake-hoffman-jerks-off",
            "link": "https://gayfappy.com/index.php/66189/tiktoker-blake-hoffman-jerks-off/",
            "title": { "rendered": "Tiktoker blake hoffman jerks off" },
            "excerpt": { "rendered": "<p>Tiktoker blake hoffman jerks off</p>" },
            "class_list": [
              "post-66189", "category-videos", "tag-blake-hoffman"
            ],
            "yoast_head_json": {
              "og_image": [{ "url": "https://gayfappy.com/wp-content/uploads/2025/09/tiktoker-blake-hoffman-jerks-off.jpg" }]
            }
          }
        ]
        """;

    private const string SampleHtmlSearch = """
        <!DOCTYPE html>
        <html>
        <head><title>Search Results</title></head>
        <body>
            <article id="post-66189" class="post-66189 post category-videos tag-blake-hoffman">
                <a href="https://gayfappy.com/index.php/66189/tiktoker-blake-hoffman-jerks-off/">
                    <h2 class="post-title">Tiktoker blake hoffman jerks off</h2>
                    <img src="https://gayfappy.com/thumb.jpg" />
                </a>
                <time datetime="2026-10-31">October 31, 2026</time>
            </article>
        </body>
        </html>
        """;

    [Theory]
    [InlineData("https://gayfappy.com/index.php/66189/tiktoker-blake-hoffman-jerks-off/", true)]
    [InlineData("https://gayfappy.com/66189/tiktoker-blake-hoffman-jerks-off/", true)]
    [InlineData("https://gayfappy.com/index.php/66189/", true)]
    [InlineData("https://gayfappy.com/?p=66189", true)]
    [InlineData("https://gayfappy.com/index.php/wp-json/wp/v2/posts/66189", true)]
    [InlineData("https://gayfappy.com/index.php/category/videos/", false)]
    [InlineData("https://otherdomain.com/video/123", false)]
    public void IsSupportedUrl_FiltersUrlsCorrectly(string url, bool expectedSupported)
    {
        Assert.Equal(expectedSupported, GayFappyScraperExtension.IsSupportedUrl(url));
    }

    [Fact]
    public async Task ScrapeVideoAsync_FromHtml_ExtractsMetadata()
    {
        const string pageUrl = "https://gayfappy.com/index.php/66189/tiktoker-blake-hoffman-jerks-off/";

        using var harness = new FakeDownloaderHost()
            .MapHtml(pageUrl, SampleVideoHtml);

        var extension = new GayFappyScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                GayFappyScraperExtension.PrimaryVideoScraperId,
                new VideoScrapeInput { Url = pageUrl },
                new ScraperPermissions(["gayfappy.com"])),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Tiktoker blake hoffman jerks off", result.Title);
        Assert.Equal("66189", result.Code);
        Assert.Equal("2026-10-31", result.Date);
        Assert.Equal("Gay Fappy", result.StudioName);
        Assert.Equal("https://gayfappy.com/wp-content/uploads/2025/09/tiktoker-blake-hoffman-jerks-off.jpg", result.ImageUrl);
        Assert.Contains("Blake Hoffman", result.PerformerNames);
        Assert.Contains("young/twink", result.TagNames);
        Assert.Contains("Videos", result.TagNames);
    }

    [Fact]
    public async Task ScrapeVideoAsync_FromWpJson_ExtractsMetadata()
    {
        const string apiUrl = "https://gayfappy.com/index.php/wp-json/wp/v2/posts/66189";

        using var harness = new FakeDownloaderHost()
            .MapJson(apiUrl, SampleWpJsonPost);

        var extension = new GayFappyScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                GayFappyScraperExtension.PrimaryVideoScraperId,
                new VideoScrapeInput { Url = apiUrl },
                new ScraperPermissions(["gayfappy.com"])),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Tiktoker blake hoffman jerks off", result.Title);
        Assert.Equal("66189", result.Code);
        Assert.Equal("2026-10-31", result.Date);
        Assert.Contains("Blake Hoffman", result.PerformerNames);
    }

    [Fact]
    public async Task SearchVideosAsync_FromWpJson_ReturnsResults()
    {
        const string searchApiUrl = "https://gayfappy.com/index.php/wp-json/wp/v2/posts?search=blake&per_page=20";

        using var harness = new FakeDownloaderHost()
            .MapJson(searchApiUrl, SampleWpJsonSearch);

        var extension = new GayFappyScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var results = await extension.SearchVideosAsync(
            new ScraperRequest<string>(
                GayFappyScraperExtension.PrimaryVideoScraperId,
                "blake",
                new ScraperPermissions(["gayfappy.com"])),
            CancellationToken.None);

        Assert.NotEmpty(results);
        var first = results[0];
        Assert.Equal("Tiktoker blake hoffman jerks off", first.Title);
        Assert.Equal("66189", first.Code);
        Assert.Contains("Blake Hoffman", first.PerformerNames);
    }

    [Fact]
    public async Task SearchVideosAsync_FromHtml_FallsBackWhenApiFails()
    {
        const string searchHtmlUrl = "https://gayfappy.com/?s=blake";

        using var harness = new FakeDownloaderHost()
            .MapHtml(searchHtmlUrl, SampleHtmlSearch);

        var extension = new GayFappyScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var results = await extension.SearchVideosAsync(
            new ScraperRequest<string>(
                GayFappyScraperExtension.PrimaryVideoScraperId,
                "blake",
                new ScraperPermissions(["gayfappy.com"])),
            CancellationToken.None);

        Assert.NotEmpty(results);
        var first = results[0];
        Assert.Equal("Tiktoker blake hoffman jerks off", first.Title);
        Assert.Equal("66189", first.Code);
        Assert.Equal("2026-10-31", first.Date);
    }

    [Fact]
    public async Task ScrapeGalleryAsync_And_SearchGalleriesAsync_Work()
    {
        const string pageUrl = "https://gayfappy.com/index.php/66189/tiktoker-blake-hoffman-jerks-off/";
        const string searchApiUrl = "https://gayfappy.com/index.php/wp-json/wp/v2/posts?search=blake&per_page=20";

        using var harness = new FakeDownloaderHost()
            .MapHtml(pageUrl, SampleVideoHtml)
            .MapJson(searchApiUrl, SampleWpJsonSearch);

        var extension = new GayFappyScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var gallery = await extension.ScrapeGalleryAsync(
            new ScraperRequest<GalleryScrapeInput>(
                GayFappyScraperExtension.PrimaryGalleryScraperId,
                new GalleryScrapeInput { Url = pageUrl },
                new ScraperPermissions(["gayfappy.com"])),
            CancellationToken.None);

        Assert.NotNull(gallery);
        Assert.Equal("Tiktoker blake hoffman jerks off", gallery.Title);
        Assert.Equal("66189", gallery.Code);

        var searchGalleries = await extension.SearchGalleriesAsync(
            new ScraperRequest<string>(
                GayFappyScraperExtension.PrimaryGalleryScraperId,
                "blake",
                new ScraperPermissions(["gayfappy.com"])),
            CancellationToken.None);

        Assert.NotEmpty(searchGalleries);
    }
}
