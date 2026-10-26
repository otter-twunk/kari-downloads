using Cove.Core.DTOs;
using Cove.Plugins;
using JustTheGaysComScraper;
using KariDownloads.Tests.Infrastructure;

namespace KariDownloads.Tests;

public sealed class JustTheGaysComScraperTests
{
    [Theory]
    [InlineData("https://justthegays.com/video/15307-danny-senpai-and-trent-benz-fuck-outside-102622/", true)]
    [InlineData("https://www.justthegays.com/video/15307-danny-senpai-and-trent-benz-fuck-outside-102622/embed/", true)]
    [InlineData("https://justthegays.com/wp-json/wp/v2/video/15307", true)]
    [InlineData("https://justthegays.com/categories/anal-102617/", false)]
    [InlineData("https://justthegays.tv/video/alphabottomx-gets-double-penetrated-by-gael-riok-pxeg5", false)]
    [InlineData("https://example.com/video/123", false)]
    public void IsSupportedVideoUrl_ValidatesCorrectly(string url, bool expected)
    {
        Assert.Equal(expected, JustTheGaysComScraperExtension.IsSupportedVideoUrl(url));
    }

    [Theory]
    [InlineData("https://justthegays.com/performers/danny-senpai-102617/", true)]
    [InlineData("https://www.justthegays.com/performer/trent-benz-102617/", true)]
    [InlineData("https://justthegays.tv/performer/gael-riok-100", false)]
    [InlineData("https://example.com/performer/123", false)]
    public void IsSupportedPerformerUrl_ValidatesCorrectly(string url, bool expected)
    {
        Assert.Equal(expected, JustTheGaysComScraperExtension.IsSupportedPerformerUrl(url));
    }

    [Fact]
    public async Task ScrapeVideoAsync_ExtractsMetadataFromHtmlFixture()
    {
        const string pageUrl = "https://justthegays.com/video/15307-danny-senpai-and-trent-benz-fuck-outside-102622/";
        const string comHtml = """
            <!doctype html>
            <html lang="en-US">
            <head>
                <meta property="og:title" content="Danny Senpai and Trent Benz fuck outside - JustTheGays.com - Best Free Gay Porn" />
                <meta property="og:image" content="https://justthegays.com/wp-content/uploads/2023/04/107167p.jpg" />
                <link rel="alternate" type="application/json" href="https://justthegays.com/wp-json/wp/v2/video/15307" />
            </head>
            <body>
                <article id="post-15307" class="post-15307 video type-video">
                    <h1 class="post-title">Danny Senpai and Trent Benz fuck outside</h1>
                    <div class="post-meta">
                        <time datetime="2023-04-26 07:19:00" class="date">3 years</time>
                        <div class="post-terms post-categories">
                            <a href="https://justthegays.com/categories/anal-102617/" rel="tag">Anal</a>
                            <a href="https://justthegays.com/categories/outdoor-102617/" rel="tag">Outdoor</a>
                            <a href="https://justthegays.com/categories/young-twink-102617/" rel="tag">Young/Twink</a>
                        </div>
                    </div>
                    <div class="post-content">
                        <p>Danny Senpai and Trent Benz fuck outside &#8211; JustTheGays.com | Just The Gays &#8211; Hot outdoor bareback scene. Stream the hottest and newest gay porn videos for free from your favorite guys on OnlyFans.</p>
                        <div class="post-tags">
                            <a href="https://justthegays.com/performers/danny-senpai-102617/" rel="tag">Danny Senpai</a>
                            <a href="https://justthegays.com/performers/trent-benz-102617/" rel="tag">Trent Benz</a>
                        </div>
                    </div>
                </article>
            </body>
            </html>
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(pageUrl, comHtml);

        var extension = new JustTheGaysComScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                JustTheGaysComScraperExtension.VideoScraperId,
                new VideoScrapeInput { Url = pageUrl },
                new ScraperPermissions(["justthegays.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Danny Senpai and Trent Benz fuck outside", scraped.Title);
        Assert.Equal("15307", scraped.Code);
        Assert.Equal("2023-04-26", scraped.Date);
        Assert.Equal("Hot outdoor bareback scene.", scraped.Details);
        Assert.Equal("https://justthegays.com/wp-content/uploads/2023/04/107167p.jpg", scraped.ImageUrl);
        Assert.Equal("Just The Gays", scraped.StudioName);
        Assert.Equal(["Danny Senpai", "Trent Benz"], scraped.PerformerNames);
        Assert.Equal(["Anal", "Outdoor", "Young/Twink"], scraped.TagNames);
        Assert.Equal([pageUrl], scraped.Urls);
    }

    [Fact]
    public async Task ScrapeVideoAsync_ExtractsMetadataFromWpJsonApiDirectly()
    {
        const string apiUrl = "https://justthegays.com/wp-json/wp/v2/video/98765";
        const string wpJson = """
            {
              "id": 98765,
              "date": "2024-05-12T09:15:00",
              "slug": "98765-marco-polo-fucks-bastian-moreyra",
              "link": "https://justthegays.com/video/98765-marco-polo-fucks-bastian-moreyra/",
              "title": { "rendered": "Marco Polo Fucks Bastian Moreyra &#8211; JustTheGays.com" },
              "content": { "rendered": "<p>Marco Polo and Bastian Moreyra go at it in this amateur clip. Jerk to hot gay porn from Marco Polo, Bastian Moreyra. Thousands of hours of amateur Bareback, Latin videos. Stream the hottest and newest gay porn videos for free at JustTheGays.</p>" }
            }
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(apiUrl, wpJson);

        var extension = new JustTheGaysComScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                JustTheGaysComScraperExtension.VideoScraperId,
                new VideoScrapeInput { Url = apiUrl },
                new ScraperPermissions(["justthegays.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Marco Polo Fucks Bastian Moreyra", scraped.Title);
        Assert.Equal("98765", scraped.Code);
        Assert.Equal("2024-05-12", scraped.Date);
        Assert.Equal("Marco Polo and Bastian Moreyra go at it in this amateur clip.", scraped.Details);
        Assert.Equal(["Marco Polo", "Bastian Moreyra"], scraped.PerformerNames);
        Assert.Equal(["Bareback", "Latin"], scraped.TagNames);
        Assert.Equal("Just The Gays", scraped.StudioName);
    }

    [Fact]
    public async Task SearchVideosAsync_ReturnsCandidateVideosFromWpSearchApi()
    {
        const string searchApiUrl = "https://justthegays.com/wp-json/wp/v2/search?subtype=video&per_page=20&search=Trent";
        const string searchJson = """
            [
              {
                "id": 15307,
                "title": "Danny Senpai and Trent Benz fuck outside - JustTheGays.com",
                "url": "https://justthegays.com/video/15307-danny-senpai-and-trent-benz-fuck-outside-102622/",
                "type": "video",
                "subtype": "video"
              }
            ]
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(searchApiUrl, searchJson);

        var extension = new JustTheGaysComScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var results = await extension.SearchVideosAsync(
            new ScraperRequest<string>(
                JustTheGaysComScraperExtension.VideoScraperId,
                "Trent",
                new ScraperPermissions(["justthegays.com"])),
            CancellationToken.None);

        Assert.Single(results);
        var item = results[0];
        Assert.Equal("Danny Senpai and Trent Benz fuck outside", item.Title);
        Assert.Equal("15307", item.Code);
        Assert.Equal("https://justthegays.com/video/15307-danny-senpai-and-trent-benz-fuck-outside-102622/", item.Urls[0]);
    }

    [Fact]
    public async Task ScrapePerformerAsync_ExtractsPerformerDetails()
    {
        const string performerUrl = "https://justthegays.com/performers/danny-senpai-102617/";
        const string sampleHtml = """
            <!doctype html>
            <html>
            <head>
                <meta property="og:title" content="Danny Senpai - JustTheGays.com" />
                <meta property="og:image" content="https://justthegays.com/wp-content/uploads/2023/04/danny-senpai.jpg" />
                <meta property="og:description" content="Danny Senpai porn videos and amateur scenes." />
            </head>
            <body>
                <h1>Danny Senpai</h1>
            </body>
            </html>
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(performerUrl, sampleHtml);

        var extension = new JustTheGaysComScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapePerformerAsync(
            new ScraperRequest<PerformerScrapeInput>(
                JustTheGaysComScraperExtension.PerformerScraperId,
                new PerformerScrapeInput { Url = performerUrl },
                new ScraperPermissions(["justthegays.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Danny Senpai", scraped.Name);
        Assert.Equal("Male", scraped.Gender);
        Assert.Equal("https://justthegays.com/wp-content/uploads/2023/04/danny-senpai.jpg", scraped.ImageUrl);
        Assert.Equal([performerUrl], scraped.Urls);
    }

    [Theory]
    [InlineData("SupremeXAlpha - RAW Pledge Train - JustTheGays.com - Hot Onlyfans and gay porn videos — justthegays.com.mp4", "SupremeXAlpha - RAW Pledge Train")]
    [InlineData("TenInchTopXXX x Jhon Guevara - JustTheGays.com - Hot Onlyfans and gay porn videos — justthegays.com.mp4", "TenInchTopXXX x Jhon Guevara")]
    [InlineData("SupremeXAlpha - RAW Pledge Train - JustTheGays.com", "SupremeXAlpha - RAW Pledge Train")]
    [InlineData("SupremeXAlpha - RAW Pledge Train.mp4", "SupremeXAlpha - RAW Pledge Train")]
    [InlineData("TenInchTopXXX x Jhon Guevara — justthegays.com - xxx", "TenInchTopXXX x Jhon Guevara")]
    public void CleanSearchQuery_StripsTailingJustTheGaysComPhraseAndFollowing(string rawInput, string expectedOutput)
    {
        var cleaned = JustTheGaysComScraperExtension.CleanSearchQuery(rawInput);
        Assert.Equal(expectedOutput, cleaned);
    }

    [Fact]
    public async Task SearchVideosAsync_StripsTailingPhraseAndExecutesSearch()
    {
        const string rawQuery = "SupremeXAlpha - RAW Pledge Train - JustTheGays.com - Hot Onlyfans and gay porn videos — justthegays.com.mp4";
        const string expectedCleanSearch = "SupremeXAlpha - RAW Pledge Train";
        var searchApiUrl = $"https://justthegays.com/wp-json/wp/v2/search?subtype=video&per_page=20&search={Uri.EscapeDataString(expectedCleanSearch)}";
        const string searchJson = """
            [
              {
                "id": 99999,
                "title": "SupremeXAlpha - RAW Pledge Train - JustTheGays.com",
                "url": "https://justthegays.com/video/99999-supremexalpha-raw-pledge-train/",
                "type": "video",
                "subtype": "video"
              }
            ]
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(searchApiUrl, searchJson);

        var extension = new JustTheGaysComScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var results = await extension.SearchVideosAsync(
            new ScraperRequest<string>(
                JustTheGaysComScraperExtension.VideoScraperId,
                rawQuery,
                new ScraperPermissions(["justthegays.com"])),
            CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("SupremeXAlpha - RAW Pledge Train", results[0].Title);
        Assert.Equal("99999", results[0].Code);
        Assert.Equal("https://justthegays.com/video/99999-supremexalpha-raw-pledge-train/", results[0].Urls[0]);
    }
}
