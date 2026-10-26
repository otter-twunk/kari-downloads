using Cove.Core.DTOs;
using Cove.Plugins;
using KariDownloads.Tests.Infrastructure;
using JustTheGaysComScraper;

namespace KariDownloads.Tests;

public sealed class JustTheGaysComScraperTests
{
    [Fact]
    public async Task ScrapeVideoAsync_ExtractsStructuredMetadataFromFixture()
    {
        const string pageUrl = "https://justthegays.com/videos/ABC-123-sample-scene";
        const string sampleHtml = """
            <!doctype html>
            <html>
            <head>
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@type": "VideoObject",
                  "name": "Sample Scene Title",
                  "description": "Full scene synopsis.",
                  "uploadDate": "2026-05-14",
                  "thumbnailUrl": "https://justthegays.com/thumbs/abc-123.jpg",
                  "actor": [{"@type": "Person", "name": "Alex Performer"}],
                  "keywords": "Action, Feature"
                }
                </script>
            </head>
            <body></body>
            </html>
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(pageUrl, sampleHtml);

        var extension = new JustTheGaysComScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                JustTheGaysComScraperExtension.PrimaryScraperId,
                new VideoScrapeInput { Url = pageUrl },
                new ScraperPermissions(["justthegays.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Sample Scene Title", scraped.Title);
        Assert.Equal("ABC-123", scraped.Code);
        Assert.Equal("2026-05-14", scraped.Date);
        Assert.Equal("Full scene synopsis.", scraped.Details);
        Assert.Equal("https://justthegays.com/thumbs/abc-123.jpg", scraped.ImageUrl);
        Assert.Contains("Alex Performer", scraped.PerformerNames);
        Assert.Contains("Action", scraped.TagNames);
    }
}
