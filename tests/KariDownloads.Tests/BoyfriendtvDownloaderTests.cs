using Cove.Plugins;
using KariDownloads.Tests.Infrastructure;
using BoyfriendtvDownloader;

namespace KariDownloads.Tests;

public sealed class BoyfriendtvDownloaderTests
{
    private const string SampleVideoPageUrl = "https://www.boyfriendtv.com/videos/1426460/selector/";
    private const string Stream1080Url = "https://cdn.boyfriendtv.com/media/stream_1080p.mp4";
    private const string Stream720Url = "https://cdn.boyfriendtv.com/key=abc123xyz/media=mp4/stream_720p.mp4";

    private const string RealisticPageHtml = """
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <title>Handsome Twink First Audition | BoyFriendTV</title>
            <meta property="og:title" content="Handsome Twink First Audition | BoyFriendTV" />
            <meta property="og:description" content="Exclusive bedroom audition with hot action." />
            <meta property="og:image" content="https://cdn.boyfriendtv.com/thumbs/1426460.jpg" />
            <script type="application/ld+json">
            {
                "@context": "https://schema.org",
                "@type": "VideoObject",
                "name": "Handsome Twink First Audition",
                "description": "Exclusive bedroom audition with hot action.",
                "uploadDate": "2024-03-20T10:30:00Z",
                "thumbnailUrl": ["https://cdn.boyfriendtv.com/thumbs/1426460.jpg"],
                "keywords": ["Audition", "Bareback"],
                "actor": [
                    {"@type": "Person", "name": "Joey Mills"}
                ]
            }
            </script>
        </head>
        <body>
            <div class="player-container">
                <script>
                    var mediaDefinitions = [
                        {"defaultQuality": false, "format": "mp4", "quality": "1080", "videoUrl": "https://cdn.boyfriendtv.com/media/stream_1080p.mp4"},
                        {"defaultQuality": true, "format": "mp4", "quality": "720", "videoUrl": "/key=abc123xyz/media=mp4/stream_720p.mp4"},
                        {"defaultQuality": false, "format": "hls", "quality": "720", "videoUrl": "https://cdn.boyfriendtv.com/media/master.m3u8"}
                    ];
                </script>
            </div>
            <div class="video-info">
                <a href="/pornstars/joey-mills/videos/">Joey Mills</a>
                <a href="/pornstars/tyler-hill/videos/">Tyler Hill</a>
                <a href="/tags/audition">Audition</a>
                <a href="/tags/twink">Twink</a>
            </div>
        </body>
        </html>
        """;

    [Fact]
    public void IsSupportedUrl_ValidatesCorrectly()
    {
        Assert.True(BoyfriendtvDownloaderExtension.IsSupportedUrl("https://www.boyfriendtv.com/videos/1426460/selector/"));
        Assert.True(BoyfriendtvDownloaderExtension.IsSupportedUrl("https://boyfriendtv.com/videos/999/"));
        Assert.False(BoyfriendtvDownloaderExtension.IsSupportedUrl("https://example.com/videos/1426460/"));
        Assert.False(BoyfriendtvDownloaderExtension.IsSupportedUrl("not-a-url"));
    }

    [Fact]
    public async Task MatchAsync_DiscoversMetadataAndQualityOptions()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(SampleVideoPageUrl, RealisticPageHtml);

        var extension = new BoyfriendtvDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var match = await extension.MatchAsync(SampleVideoPageUrl, CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(BoyfriendtvDownloaderExtension.PrimaryDownloaderId, match.DownloaderId);
        Assert.Equal("Handsome Twink First Audition", match.Label);
        Assert.NotNull(match.QualityOptions);
        Assert.Contains(match.QualityOptions, q => q.Id == "1080" && q.Height == 1080);
        Assert.Contains(match.QualityOptions, q => q.Id == "720" && q.Height == 720);
    }

    [Fact]
    public async Task DownloadAsync_DownloadsDefaultHighestMp4_AndReportsMetadata()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(SampleVideoPageUrl, RealisticPageHtml)
            .MapBytes(Stream1080Url, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], "video/mp4");

        var extension = new BoyfriendtvDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                BoyfriendtvDownloaderExtension.PrimaryDownloaderId,
                SampleVideoPageUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["boyfriendtv.com", "cdn.boyfriendtv.com"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Handsome Twink First Audition.mp4", result.OriginalFilename);
        Assert.True(File.Exists(Path.Combine(harness.TempDirectory, result.LocalPath)));

        var metadata = result.InlineVideoMetadata;
        Assert.NotNull(metadata);
        Assert.Equal("Handsome Twink First Audition", metadata.Title);
        Assert.Equal("BoyFriendTV", metadata.StudioName);
        Assert.Equal("2024-03-20", metadata.Date);
        Assert.Equal("Exclusive bedroom audition with hot action.", metadata.Details);
        Assert.Contains("Joey Mills", metadata.PerformerNames);
        Assert.Contains("Tyler Hill", metadata.PerformerNames);
        Assert.Contains("Audition", metadata.TagNames);
        Assert.Contains("Twink", metadata.TagNames);
        Assert.Contains("Bareback", metadata.TagNames);
        Assert.Equal("https://cdn.boyfriendtv.com/thumbs/1426460.jpg", metadata.ImageUrl);

        Assert.True(harness.ProgressUpdates.Count > 0);
        Assert.Equal(100.0, harness.ProgressUpdates.Last().Progress);
    }

    [Fact]
    public async Task DownloadAsync_RespectsQualitySelectionAndResolvesKeyPrefix()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(SampleVideoPageUrl, RealisticPageHtml)
            .MapBytes(Stream720Url, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], "video/mp4");

        var extension = new BoyfriendtvDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                BoyfriendtvDownloaderExtension.PrimaryDownloaderId,
                SampleVideoPageUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["boyfriendtv.com", "cdn.boyfriendtv.com"]),
                QualityId: "720"),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Handsome Twink First Audition.mp4", result.OriginalFilename);
        Assert.True(File.Exists(Path.Combine(harness.TempDirectory, result.LocalPath)));
    }

    [Fact]
    public void IsSupportedUrl_SupportsBoyfriendTvAndRejectsNonVideoPaths()
    {
        Assert.True(BoyfriendtvDownloaderExtension.IsSupportedUrl("https://www.boyfriend.tv/videos/1757468/dirty-daddy-massage/"));
        Assert.True(BoyfriendtvDownloaderExtension.IsSupportedUrl("https://www.boyfriend.tv/embed/1757468/63758/"));
        Assert.False(BoyfriendtvDownloaderExtension.IsSupportedUrl("https://www.boyfriend.tv/pornstars/tyler-saint-378/"));
    }

    [Fact]
    public void ExtractMediaStreams_SupportsPlayerConfigSources()
    {
        const string html = """
            <script>
                var playerConfig = {
                    sources: {"hlsAuto":"https:\/\/cdn.boyfriend.tv\/key=abc,end=123\/media=hls4A\/2026-11\/_TPL_.mp4"},
                    aspectRatio: '852/480'
                };
            </script>
            """;

        var streams = BoyfriendtvDownloaderExtension.ExtractMediaStreams(
            html,
            "https://www.boyfriend.tv/videos/1757468/dirty-daddy-massage/");

        Assert.Single(streams);
        Assert.Equal("hls", streams[0].Format);
        Assert.Equal("https://cdn.boyfriend.tv/key=abc,end=123/media=hls4A/2026-11/_TPL_.mp4", streams[0].VideoUrl);
    }

    [Fact]
    public async Task DownloadAsync_LogsInWithCredentials_AndSendsCookieToCdn()
    {
        const string videoUrl = "https://www.boyfriend.tv/videos/1426460/selector/";
        const string loginUrl = "https://www.boyfriend.tv/login/";
        string? observedCdnCookie = null;

        using var harness = new FakeDownloaderHost();
        harness.MapResponse(loginUrl, req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        <form action="/login/" method="post">
                            <input type="text" name="username" />
                            <input type="password" name="pass" />
                        </form>
                        """)
                };
            }

            var resp = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
            resp.Headers.Add("Set-Cookie", "PHPSESSID=dl_sess_99; path=/");
            return resp;
        });

        harness.MapResponse(videoUrl, req =>
        {
            var cookie = req.Headers.TryGetValues("Cookie", out var vals) ? string.Join("; ", vals) : string.Empty;
            if (cookie.Contains("PHPSESSID=dl_sess_99", StringComparison.Ordinal))
            {
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(RealisticPageHtml)
                };
            }

            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Age verification required</body></html>")
            };
        });

        harness.MapResponse(Stream1080Url, req =>
        {
            observedCdnCookie = req.Headers.TryGetValues("Cookie", out var vals) ? string.Join("; ", vals) : null;
            var content = new ByteArrayContent([0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            content.Headers.ContentLength = 8;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
        });

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(harness.HttpClients);
        services.AddSingleton<Cove.Plugins.IDownloaderSiteLoginProvider>(
            new StubLoginProvider(new Cove.Plugins.DownloaderSiteLogin("boyfriend.tv", "kari", "secret")));
        using var provider = services.BuildServiceProvider();

        var extension = new BoyfriendtvDownloaderExtension();
        await extension.InitializeAsync(provider);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                BoyfriendtvDownloaderExtension.PrimaryDownloaderId,
                videoUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["boyfriend.tv", "cdn.boyfriendtv.com"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("1426460", result.InlineVideoMetadata?.Code);
        Assert.Contains("PHPSESSID=dl_sess_99", observedCdnCookie);
    }

    private sealed class StubLoginProvider(Cove.Plugins.DownloaderSiteLogin login) : Cove.Plugins.IDownloaderSiteLoginProvider
    {
        public Cove.Plugins.DownloaderSiteLogin? FindForUrl(string url) => login;
    }
}
