using Cove.Plugins;
using KariDownloads.Tests.Infrastructure;
using ThisvidDownloader;

namespace KariDownloads.Tests;

public sealed class ThisvidDownloaderTests
{
    private const string VideoPageUrl = "https://thisvid.com/videos/fixture-thisvid-scene/";
    private const string PlaylistPageUrl = "https://www.thisvid.com/playlist/385827/video/poppers-anal-training/";
    private const string Video1080Url = "https://media.thisvid.com/contents/videos/1000/1234/1234_1080p.mp4";
    private const string Video720Url = "https://media.thisvid.com/contents/videos/1000/1234/1234_720p.mp4";

    private const string RealisticThisvidHtml = """
        <!doctype html>
        <html lang="en">
          <head>
            <meta charset="utf-8" />
            <meta property="og:title" content="Fixture ThisVid Scene - ThisVid.com" />
            <meta property="og:url" content="https://thisvid.com/videos/fixture-thisvid-scene/" />
            <meta property="og:description" content="Generic og fallback description." />
            <meta property="og:image" content="https://media.thisvid.com/contents/videos_screenshots/1000/1234/preview.jpg" />
            <title>Fixture ThisVid Scene - ThisVid.com</title>
          </head>
          <body>
            <div class="video-holder">
              <div class="video-id" data-video-id="3456789"></div>
              <script>
                var flashvars = {
                    video_id: '3456789',
                    video_url: 'https://media.thisvid.com/contents/videos/1000/1234/1234_480p.mp4',
                    video_url_text: '480p',
                    video_alt_url: 'https://media.thisvid.com/contents/videos/1000/1234/1234_720p.mp4',
                    video_alt_url_text: '720p',
                    video_alt_url2: 'https://media.thisvid.com/contents/videos/1000/1234/1234_1080p.mp4',
                    video_alt_url2_text: '1080p',
                    postfix: '.mp4'
                };
              </script>
              <ul class="description">
                <li>
                  <p>Fixture ThisVid description paragraph.</p>
                </li>
                <li>
                  <span>Added:</span> 22 March 2021 by <a href="/members/111/">uploader</a>
                </li>
                <li>
                  <span>Categories:</span>
                  <a href="/categories/amateurs/">Amateurs</a>
                  <a href="/categories/anal/">Anal</a>
                </li>
                <li>
                  <span>Tags:</span>
                  <a href="/tags/blonde/">Blonde</a>
                </li>
              </ul>
            </div>
          </body>
        </html>
        """;

    private const string AgeGateHtml = """
        <!doctype html>
        <html lang="en" class="html-locked">
        <head>
            <title>Age verification required</title>
            <script>if (window.location.pathname !== '/age-verification/') {}</script>
        </head>
        <body>
            <div class="agegate-container">Age verification required</div>
        </body>
        </html>
        """;

    [Fact]
    public void IsSupportedUrl_And_NormalizeVideoUrl_HandleAllVariants()
    {
        Assert.True(ThisvidDownloaderExtension.IsSupportedUrl("https://thisvid.com/videos/fixture-thisvid-scene/"));
        Assert.True(ThisvidDownloaderExtension.IsSupportedUrl("https://www.thisvid.com/videos/12345/"));
        Assert.True(ThisvidDownloaderExtension.IsSupportedUrl("https://thisvid.com/embed/12345/"));
        Assert.True(ThisvidDownloaderExtension.IsSupportedUrl("https://www.thisvid.com/playlist/385827/video/poppers-anal-training/"));
        Assert.False(ThisvidDownloaderExtension.IsSupportedUrl("https://thisvid.com/members/111/"));
        Assert.False(ThisvidDownloaderExtension.IsSupportedUrl("https://other.com/videos/12345/"));
        Assert.False(ThisvidDownloaderExtension.IsSupportedUrl("not-a-url"));

        Assert.Equal(
            "https://thisvid.com/videos/poppers-anal-training/",
            ThisvidDownloaderExtension.NormalizeVideoUrl("https://www.thisvid.com/playlist/385827/video/poppers-anal-training/"));
    }

    [Fact]
    public async Task MatchAsync_DiscoversMetadataAndQualityOptions()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(VideoPageUrl, RealisticThisvidHtml);

        var extension = new ThisvidDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var match = await extension.MatchAsync(VideoPageUrl, CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(ThisvidDownloaderExtension.PrimaryDownloaderId, match.DownloaderId);
        Assert.Equal("Fixture ThisVid Scene", match.Label);
        Assert.NotNull(match.QualityOptions);
        Assert.Contains(match.QualityOptions, q => q.Id == "1080p" && q.Height == 1080);
        Assert.Contains(match.QualityOptions, q => q.Id == "720p" && q.Height == 720);
        Assert.Contains(match.QualityOptions, q => q.Id == "480p" && q.Height == 480);
    }

    [Fact]
    public async Task MatchAsync_NormalizesPlaylistUrls()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml("https://thisvid.com/videos/poppers-anal-training/", RealisticThisvidHtml);

        var extension = new ThisvidDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var match = await extension.MatchAsync(PlaylistPageUrl, CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(ThisvidDownloaderExtension.PrimaryDownloaderId, match.DownloaderId);
        Assert.Equal("https://thisvid.com/videos/poppers-anal-training/", match.NormalizedUrl);
        Assert.Equal(PlaylistPageUrl, match.SourceUrl);
    }

    [Fact]
    public async Task DownloadAsync_DownloadsHighestQualityByDefault_WithInlineMetadata()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(VideoPageUrl, RealisticThisvidHtml)
            .MapBytes(Video1080Url, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], "video/mp4");

        var extension = new ThisvidDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                ThisvidDownloaderExtension.PrimaryDownloaderId,
                VideoPageUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["thisvid.com", "media.thisvid.com"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Fixture ThisVid Scene.mp4", result.OriginalFilename);
        Assert.True(File.Exists(Path.Combine(harness.TempDirectory, result.LocalPath)));

        var meta = result.InlineVideoMetadata;
        Assert.NotNull(meta);
        Assert.Equal("Fixture ThisVid Scene", meta.Title);
        Assert.Equal("3456789", meta.Code);
        Assert.Equal("2021-03-22", meta.Date);
        Assert.Equal("Fixture ThisVid description paragraph.", meta.Details);
        Assert.Equal("https://media.thisvid.com/contents/videos_screenshots/1000/1234/preview.jpg", meta.ImageUrl);
        Assert.Equal("ThisVid", meta.StudioName);
        Assert.Contains("Amateurs", meta.TagNames);
        Assert.Contains("Anal", meta.TagNames);
        Assert.Contains("Blonde", meta.TagNames);
    }

    [Fact]
    public async Task DownloadAsync_ThrowsDescriptiveError_OnAgeVerificationGate()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(VideoPageUrl, AgeGateHtml);

        var extension = new ThisvidDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            extension.DownloadAsync(
                new DownloaderRequest(
                    ThisvidDownloaderExtension.PrimaryDownloaderId,
                    VideoPageUrl,
                    DownloaderEntity.Video,
                    new DownloaderPermissions(["thisvid.com"])),
                harness,
                CancellationToken.None));

        Assert.Contains("age-verification gate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadAsync_RespectsQualityIdRequest()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(VideoPageUrl, RealisticThisvidHtml)
            .MapBytes(Video720Url, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], "video/mp4");

        var extension = new ThisvidDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                ThisvidDownloaderExtension.PrimaryDownloaderId,
                VideoPageUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["thisvid.com", "media.thisvid.com"]),
                QualityId: "720p"),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(File.Exists(Path.Combine(harness.TempDirectory, result.LocalPath)));
    }
}
