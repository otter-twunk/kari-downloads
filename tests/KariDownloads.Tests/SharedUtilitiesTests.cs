using Cove.Core.DTOs;
using Cove.Plugins;
using ExampleDownloader;
using ExampleScraper;
using KariDownloads.Shared;
using KariDownloads.Tests.Infrastructure;

namespace KariDownloads.Tests;

public sealed class SharedUtilitiesTests
{
    [Fact]
    public async Task DownloaderUtilities_DownloadToTempFileAsync_StreamsAndReportsProgress()
    {
        const string mediaUrl = "https://cdn.example.com/videos/clip.mp4";
        var payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);

        using var host = new FakeDownloaderHost()
            .MapBytes(mediaUrl, payload, "video/mp4");

        var localPath = await DownloaderUtilities.DownloadToTempFileAsync(
            host.HttpClients.CreateClient(),
            mediaUrl,
            host,
            "clip.mp4",
            CancellationToken.None);

        Assert.True(File.Exists(localPath));
        Assert.Equal(payload.Length, new FileInfo(localPath).Length);
        Assert.NotEmpty(host.ProgressUpdates);
        Assert.Contains(host.ProgressUpdates, p => p.Progress >= 95.0);
    }

    [Fact]
    public async Task DownloaderUtilities_CreateGalleryZipAsync_CreatesValidZipArchive()
    {
        const string img1 = "https://cdn.example.com/gallery/1.jpg";
        const string img2 = "https://cdn.example.com/gallery/2.png";

        using var host = new FakeDownloaderHost()
            .MapBytes(img1, [1, 2, 3, 4], "image/jpeg")
            .MapBytes(img2, [5, 6, 7, 8], "image/png");

        var zipPath = await DownloaderUtilities.CreateGalleryZipAsync(
            host.HttpClients.CreateClient(),
            [img1, img2],
            host,
            "Test Gallery",
            CancellationToken.None);

        Assert.True(File.Exists(zipPath));
        Assert.EndsWith("Test Gallery.zip", zipPath);
    }

    [Fact]
    public void ScraperUtilities_ExtractStructuredMetadata_ParsesJsonLdAndOpenGraph()
    {
        const string html = """
            <!doctype html>
            <html>
            <head>
                <meta property="og:site_name" content="Fallback Studio" />
                <meta property="video:tag" content="TagFromMeta" />
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@type": "VideoObject",
                  "name": "Structured Title &amp; More",
                  "description": "Scene details.",
                  "uploadDate": "20260815",
                  "thumbnailUrl": "https://example.com/cover.jpg",
                  "actor": [{"@type": "Person", "name": "Performer One"}],
                  "keywords": ["TagFromJsonLd", "TagTwo"]
                }
                </script>
            </head>
            <body></body>
            </html>
            """;

        var metadata = ScraperUtilities.ExtractStructuredMetadata(html, "Default Studio");

        Assert.Equal("Structured Title & More", metadata.Title);
        Assert.Equal("Scene details.", metadata.Description);
        Assert.Equal("2026-08-15", metadata.Date);
        Assert.Equal("https://example.com/cover.jpg", metadata.ImageUrl);
        Assert.Equal("Fallback Studio", metadata.StudioName);
        Assert.Contains("Performer One", metadata.PerformerNames);
        Assert.Contains("TagFromJsonLd", metadata.TagNames);
        Assert.Contains("TagFromMeta", metadata.TagNames);
    }

    [Fact]
    public async Task ExampleExtensions_MatchAndScrape_WorkAsExpected()
    {
        var downloader = new ExampleDownloaderExtension();
        var match = await downloader.MatchAsync("https://example.com/video/123", CancellationToken.None);
        Assert.NotNull(match);
        Assert.Equal("com.example.downloader.file", match.DownloaderId);

        var scraper = new ExampleScraperExtension();
        var scraped = await scraper.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                "com.example.scraper.video",
                new VideoScrapeInput { Url = "https://example.com/videos/abc", Title = "Demo" },
                new ScraperPermissions(["example.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Demo", scraped.Title);
    }
}
