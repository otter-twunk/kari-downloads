using Cove.Core.DTOs;
using Cove.Plugins;
using JustTheGaysTvScraper;
using KariDownloads.Tests.Infrastructure;

namespace KariDownloads.Tests;

public sealed class JustTheGaysTvScraperTests
{
    [Theory]
    [InlineData("https://justthegays.tv/video/alphabottomx-gets-double-penetrated-by-gael-riok-pxeg5", true)]
    [InlineData("https://www.justthegays.tv/embed/alphabottomx-gets-double-penetrated-by-gael-riok-pxeg5", true)]
    [InlineData("https://justthegays.tv/clip/9mxPTAL3qskf", true)]
    [InlineData("https://justthegays.tv/categories", false)]
    [InlineData("https://justthegays.com/video/15307-danny-senpai-and-trent-benz-fuck-outside/", false)]
    [InlineData("https://example.com/video/123", false)]
    public void IsSupportedVideoUrl_ValidatesCorrectly(string url, bool expected)
    {
        Assert.Equal(expected, JustTheGaysTvScraperExtension.IsSupportedVideoUrl(url));
    }

    [Theory]
    [InlineData("https://justthegays.tv/performer/alejandro-hernandez-53", true)]
    [InlineData("https://www.justthegays.tv/performer/gael-riok-100/", true)]
    [InlineData("https://justthegays.com/performers/alex-doe-102617/", false)]
    [InlineData("https://example.com/performer/123", false)]
    public void IsSupportedPerformerUrl_ValidatesCorrectly(string url, bool expected)
    {
        Assert.Equal(expected, JustTheGaysTvScraperExtension.IsSupportedPerformerUrl(url));
    }

    [Fact]
    public async Task ScrapeVideoAsync_ExtractsStructuredMetadataFromFixture()
    {
        const string pageUrl = "https://justthegays.tv/video/alphabottomx-gets-double-penetrated-by-gael-riok-and-big-rod-pxeg5";
        const string sampleHtml = """
            <!doctype html>
            <html lang="en">
            <head>
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@type": "VideoObject",
                  "name": "AlphaBottomX gets double penetrated by Gael Riok and Big Rod",
                  "description": "Get off while streaming AlphaBottom, Big Rod and Gael kRiok in the erotic new video \"AlphaBottomX gets double penetrated and gaped by the big dicks of Gael kRiok and Big Rod\". This video has plenty of mind-blowing Anal, Bareback, Big Cock, Double Penetration, Group and Muscle action that will make you hard and horny. Find it here on JustTheGays - justthegays.tv, the best free amateur gay porn and OnlyFans streaming site of 2026",
                  "thumbnailUrl": "https://cdn.gff.network/site/thumbnails/2026/10/alphabottomx.jpg",
                  "uploadDate": "2026-10-24T03:48:48.416Z",
                  "actor": [
                    {"@type": "Person", "name": "AlphaBottom"},
                    {"@type": "Person", "name": "Big Rod"}
                  ]
                }
                </script>
                <meta property="og:title" content="AlphaBottomX gets double penetrated by Gael Riok and Big Rod - gay porn, OnlyFans &amp; nude videos | JustTheGays" />
            </head>
            <body>
                <script>
                    __sveltekit_1967x15 = {
                        data: [{type:"data",data:{}},{type:"data",data:{video:{id:"1792806-cp2l",slug:"alphabottomx-gets-double-penetrated-by-gael-riok-and-big-rod-pxeg5",title:"AlphaBottomX gets double penetrated by Gael Riok and Big Rod",jtg_title:"AlphaBottomX gets double penetrated by Gael Riok and Big Rod",upload_date:"2026-10-24T03:48:48.416Z",categories:[{id:"anal",name:"Anal"},{id:"bareback",name:"Bareback"},{id:"group",name:"Group"}],performers:[{id:"alphabottom",name:"AlphaBottom",slug:"alphabottom-101"},{id:"big-rod",name:"Big Rod",slug:"big-rod-23"},{id:"gael-riok",name:"Gael Riok",slug:"gael-riok-100"}]},inQuickies:false,related:[]}}]
                    };
                </script>
            </body>
            </html>
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(pageUrl, sampleHtml);

        var extension = new JustTheGaysTvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                JustTheGaysTvScraperExtension.VideoScraperId,
                new VideoScrapeInput { Url = pageUrl },
                new ScraperPermissions(["justthegays.tv"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("AlphaBottomX gets double penetrated by Gael Riok and Big Rod", scraped.Title);
        Assert.Equal("pxeg5", scraped.Code);
        Assert.Equal("2026-10-24", scraped.Date);
        Assert.Equal("JustTheGays", scraped.StudioName);
        Assert.Equal("https://cdn.gff.network/site/thumbnails/2026/10/alphabottomx.jpg", scraped.ImageUrl);
        Assert.Equal(["AlphaBottom", "Big Rod", "Gael Riok"], scraped.PerformerNames);
        Assert.Equal(["Anal", "Bareback", "Group"], scraped.TagNames);
        Assert.Equal([pageUrl], scraped.Urls);
    }

    [Fact]
    public async Task SearchVideosAsync_ReturnsCandidateVideos()
    {
        const string searchUrl = "https://justthegays.tv/search?q=Alejandro";
        const string searchHtml = """
            <!doctype html>
            <html>
            <head>
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@type": "SearchResultsPage",
                  "mainEntity": {
                    "@type": "ItemList",
                    "itemListElement": [
                      {
                        "@type": "ListItem",
                        "position": 1,
                        "item": {
                          "@type": "VideoObject",
                          "name": "Alejandro Belmont Impales A Hole",
                          "url": "https://justthegays.tv/video/alejandro-belmont-impales-a-hole-818",
                          "thumbnailUrl": "/thumbnails/alejandro-818.jpg",
                          "uploadDate": "2024-04-18T14:22:00Z",
                          "description": "Watch Alejandro Belmont impale a hole in this hot amateur scene. Find it here on JustTheGays."
                        }
                      }
                    ]
                  }
                }
                </script>
            </head>
            <body></body>
            </html>
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(searchUrl, searchHtml);

        var extension = new JustTheGaysTvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var results = await extension.SearchVideosAsync(
            new ScraperRequest<string>(
                JustTheGaysTvScraperExtension.VideoScraperId,
                "Alejandro",
                new ScraperPermissions(["justthegays.tv"])),
            CancellationToken.None);

        Assert.Single(results);
        var item = results[0];
        Assert.Equal("Alejandro Belmont Impales A Hole", item.Title);
        Assert.Equal("818", item.Code);
        Assert.Equal("2024-04-18", item.Date);
        Assert.Equal("https://justthegays.tv/video/alejandro-belmont-impales-a-hole-818", item.Urls[0]);
        Assert.Equal("https://static.jtg.network/thumbnails/alejandro-818.jpg", item.ImageUrl);
    }

    [Fact]
    public async Task ScrapePerformerAsync_ExtractsPerformerDetails()
    {
        const string performerUrl = "https://justthegays.tv/performer/alejandro-hernandez-alejandrobelmont-53";
        const string sampleHtml = """
            <!doctype html>
            <html>
            <head>
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@type": "Person",
                  "name": "Alejandro Belmont",
                  "description": "Hot latin performer and amateur creator.",
                  "image": "https://cdn.gff.network/site/performers/alejandro.jpg"
                }
                </script>
            </head>
            <body></body>
            </html>
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(performerUrl, sampleHtml);

        var extension = new JustTheGaysTvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapePerformerAsync(
            new ScraperRequest<PerformerScrapeInput>(
                JustTheGaysTvScraperExtension.PerformerScraperId,
                new PerformerScrapeInput { Url = performerUrl },
                new ScraperPermissions(["justthegays.tv"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Alejandro Belmont", scraped.Name);
        Assert.Equal("Male", scraped.Gender);
        Assert.Equal("Hot latin performer and amateur creator.", scraped.Details);
        Assert.Equal("https://cdn.gff.network/site/performers/alejandro.jpg", scraped.ImageUrl);
        Assert.Equal([performerUrl], scraped.Urls);
    }
}
