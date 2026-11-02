using Cove.Plugins;
using KariDownloads.Tests.Infrastructure;
using Gay0dayDownloader;

namespace KariDownloads.Tests;

public sealed class Gay0dayDownloaderTests
{
    private const string VideoPageUrl = "https://gay0day.com/videos/320814/surfer-boys-ander-big-ian-tested/";
    private const string MediaStreamUrl = "https://gay0day.com/get_file/3/46e6992f5069357eedfa794668b56d7e/320000/320814/320814_720p.mp4/";

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
            <div class="block-video">
                <div class="player">
                    <div id="kt_player">
                        <video id="video-id-kvs" controls poster="https://gay0day.com/contents/videos_screenshots/320000/320814/preview.jpg">
                            <source src="https://gay0day.com/get_file/3/46e6992f5069357eedfa794668b56d7e/320000/320814/320814_720p.mp4/" type="video/mp4" />
                        </video>
                    </div>
                </div>
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
                        </div>
                    </div>
                </div>
            </div>
        </div>
        </body>
        </html>
        """;

    [Theory]
    [InlineData("https://gay0day.com/videos/320814/surfer-boys-ander-big-ian-tested/", true)]
    [InlineData("https://www.gay0day.com/videos/320814/", true)]
    [InlineData("http://gay0day.com/videos/99999", true)]
    [InlineData("https://othertube.com/videos/320814/", false)]
    [InlineData("not-a-url", false)]
    [InlineData(null, false)]
    public void IsSupportedUrl_ValidatesDomainCorrectly(string? url, bool expected)
    {
        var result = Gay0dayDownloaderExtension.IsSupportedUrl(url);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task MatchAsync_ReturnsValidMatchAndLabel()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(VideoPageUrl, RealisticVideoHtml);

        var extension = new Gay0dayDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var match = await extension.MatchAsync(VideoPageUrl, CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(Gay0dayDownloaderExtension.PrimaryDownloaderId, match.DownloaderId);
        Assert.Contains("Surfer boys Ander, Big", match.Label);
    }

    [Fact]
    public async Task DownloadAsync_StreamsMediaAndPopulatesInlineMetadata()
    {
        var dummyBytes = new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D };

        using var harness = new FakeDownloaderHost()
            .MapHtml(VideoPageUrl, RealisticVideoHtml)
            .MapBytes(MediaStreamUrl, dummyBytes, "video/mp4");

        var extension = new Gay0dayDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var request = new DownloaderRequest(
            Gay0dayDownloaderExtension.PrimaryDownloaderId,
            VideoPageUrl,
            DownloaderEntity.Video,
            new DownloaderPermissions(["gay0day.com"]));

        var result = await extension.DownloadAsync(request, harness, CancellationToken.None);

        Assert.NotNull(result);
        Assert.EndsWith(".mp4", result.LocalPath);
        Assert.EndsWith(".mp4", result.OriginalFilename);
        Assert.True(File.Exists(Path.Combine(harness.TempDirectory, result.LocalPath)));

        // Verify progress reporting
        Assert.Contains(harness.ProgressUpdates, p => p.Progress >= 100.0);

        // Verify inline metadata
        var meta = result.InlineVideoMetadata;
        Assert.NotNull(meta);
        Assert.Equal("320814", meta.Code);
        Assert.Equal("2026-11-02", meta.Date);
        Assert.Equal("Ander Borges", meta.StudioName);
        Assert.Contains("Surfer boys Ander, Big", meta.Title);
        Assert.Contains("Big Marcos", meta.PerformerNames);
        Assert.Contains("Ian Surfista", meta.PerformerNames);
        Assert.Contains("surfer", meta.TagNames);
        Assert.Contains("beach", meta.TagNames);
        Assert.Equal("https://gay0day.com/contents/videos_screenshots/320000/320814/preview.jpg", meta.ImageUrl);
    }

    [Fact]
    public async Task DownloadAsync_FallsBackToDefaultStudioWhenNoSponsor()
    {
        const string pageUrl = "https://gay0day.com/videos/12345/clip/";
        const string mediaUrl = "https://gay0day.com/media/clip.mp4";
        const string minimalHtml = """
            <!DOCTYPE html>
            <html>
            <head>
                <title>Minimal Clip @ Gay0Day</title>
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@type": "VideoObject",
                  "name": "Minimal Clip",
                  "contentUrl": "https://gay0day.com/media/clip.mp4"
                }
                </script>
            </head>
            <body></body>
            </html>
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(pageUrl, minimalHtml)
            .MapBytes(mediaUrl, [0x01, 0x02, 0x03, 0x04], "video/mp4");

        var extension = new Gay0dayDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                Gay0dayDownloaderExtension.PrimaryDownloaderId,
                pageUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["gay0day.com"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result.InlineVideoMetadata);
        Assert.Equal("Minimal Clip", result.InlineVideoMetadata.Title);
        Assert.Equal("Gay0Day", result.InlineVideoMetadata.StudioName);
    }

    [Fact]
    public async Task DownloadAsync_DoesNotExtractPerformersFromFooter()
    {
        const string pageUrl = "https://gay0day.com/videos/12345/scene/";
        const string mediaUrl = "https://gay0day.com/media/scene.mp4";
        const string htmlWithFooter = """
            <!DOCTYPE html>
            <html>
            <head>
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@type": "VideoObject",
                  "name": "Scene Title",
                  "contentUrl": "https://gay0day.com/media/scene.mp4"
                }
                </script>
            </head>
            <body>
            <div id="tab_video_info" class="tab-content hidden">
                <div class="block-details">
                    <div class="info">
                        <div class="item">
                            Models:
                            <a href="https://gay0day.com/models/real-actor/">Real Actor</a>
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
            .MapHtml(pageUrl, htmlWithFooter)
            .MapBytes(mediaUrl, [0x01, 0x02, 0x03, 0x04], "video/mp4");

        var extension = new Gay0dayDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                Gay0dayDownloaderExtension.PrimaryDownloaderId,
                pageUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["gay0day.com"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result.InlineVideoMetadata);
        Assert.Single(result.InlineVideoMetadata.PerformerNames);
        Assert.Contains("Real Actor", result.InlineVideoMetadata.PerformerNames);
        Assert.DoesNotContain("Austin Young", result.InlineVideoMetadata.PerformerNames);
        Assert.DoesNotContain("Cutler X", result.InlineVideoMetadata.PerformerNames);
    }
}
