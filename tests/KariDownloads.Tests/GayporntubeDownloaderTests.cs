using Cove.Plugins;
using KariDownloads.Tests.Infrastructure;
using GayporntubeDownloader;

namespace KariDownloads.Tests;

public sealed class GayporntubeDownloaderTests
{
    private const string SamplePageUrl = "https://www.gayporntube.com/video/2144881/frat-guy-pretend-to-ride-naked";
    private const string SampleEmbedUrl = "https://www.gayporntube.com/embed/2144881";
    private const string MediaStreamUrl = "https://www.gayporntube.com/get_file/5/69b67e5b6dd82274f66a6c7058db77e9/2144000/2144881/2144881.mp4/?br=689";

    private const string RealisticVideoHtml = """
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
            <div class="player-holder">
                <video id="play-video" controls width="100%" height="100%" poster="https://static.gayporntube.com/contents/videos_screenshots/2144000/2144881/preview.jpg?v=1">
                    <source src="https://www.gayporntube.com/get_file/5/69b67e5b6dd82274f66a6c7058db77e9/2144000/2144881/2144881.mp4/?br=689" type="video/mp4" />
                </video>
            </div>
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

    [Fact]
    public void IsSupportedUrl_ValidatesCorrectly()
    {
        Assert.True(GayporntubeDownloaderExtension.IsSupportedUrl("https://www.gayporntube.com/video/2144881/frat-guy-pretend-to-ride-naked"));
        Assert.True(GayporntubeDownloaderExtension.IsSupportedUrl("https://gayporntube.com/video/2144881/"));
        Assert.True(GayporntubeDownloaderExtension.IsSupportedUrl("https://www.gayporntube.com/embed/2144881"));
        Assert.False(GayporntubeDownloaderExtension.IsSupportedUrl("https://www.gayporntube.com/channels/12/frat-guys/page1.html"));
        Assert.False(GayporntubeDownloaderExtension.IsSupportedUrl("https://www.gayporntube.com/photos/"));
        Assert.False(GayporntubeDownloaderExtension.IsSupportedUrl("https://example.com/video/2144881"));
        Assert.False(GayporntubeDownloaderExtension.IsSupportedUrl("not-a-url"));
    }

    [Fact]
    public async Task MatchAsync_DiscoversMetadataAndLabel()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(SamplePageUrl, RealisticVideoHtml);

        var extension = new GayporntubeDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var match = await extension.MatchAsync(SamplePageUrl, CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(GayporntubeDownloaderExtension.PrimaryDownloaderId, match.DownloaderId);
        Assert.Equal("Frat guy pretend to ride naked", match.Label);
        Assert.Null(match.QualityOptions);
    }

    [Fact]
    public async Task DownloadAsync_StreamsVideo_AndPopulatesInlineMetadata()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(SamplePageUrl, RealisticVideoHtml)
            .MapBytes(MediaStreamUrl, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], "video/mp4");

        var extension = new GayporntubeDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                GayporntubeDownloaderExtension.PrimaryDownloaderId,
                SamplePageUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["gayporntube.com", "static.gayporntube.com"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Frat guy pretend to ride naked.mp4", result.OriginalFilename);
        Assert.True(File.Exists(Path.Combine(harness.TempDirectory, result.LocalPath)));

        var meta = result.InlineVideoMetadata;
        Assert.NotNull(meta);
        Assert.Equal("Frat guy pretend to ride naked", meta.Title);
        Assert.Equal("2144881", meta.Code);
        Assert.Equal("2026-11-02", meta.Date);
        Assert.Equal("GayPornTube", meta.StudioName);
        Assert.Contains("Frat Guys", meta.TagNames);
        Assert.Contains("Muscle Boys", meta.TagNames);
        Assert.Contains(SamplePageUrl, meta.Urls);
    }

    [Fact]
    public void ParsePage_StripsTitleSuffix_AndExtractsDirectStream()
    {
        var resolved = GayporntubeDownloaderExtension.ParsePage(RealisticVideoHtml, SamplePageUrl);

        Assert.Equal("Frat guy pretend to ride naked", resolved.Title);
        Assert.Equal("2144881", resolved.VideoId);
        Assert.Equal(MediaStreamUrl, resolved.MediaUrl);
        Assert.Equal("2026-11-02", resolved.Date);
        Assert.Null(resolved.Description); // "." stripped
        Assert.Contains("Frat Guys", resolved.TagNames);
        Assert.Contains("Muscle Boys", resolved.TagNames);
    }
}
