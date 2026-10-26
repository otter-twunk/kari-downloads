using Cove.Plugins;
using JustTheGaysComDownloader;
using KariDownloads.Tests.Infrastructure;

namespace KariDownloads.Tests;

public sealed class JustTheGaysComDownloaderTests
{
    [Theory]
    [InlineData("https://justthegays.com/video/15307-alex-doe-and-sam-roe-fuck-outside/", true)]
    [InlineData("https://www.justthegays.com/video/15307-alex-doe-and-sam-roe-fuck-outside/embed/", true)]
    [InlineData("https://justthegays.com/wp-json/wp/v2/video/15307", true)]
    [InlineData("https://justthegays.hr1alfri.xyz/l/l/09.26/mig/172367w.mp4", true)]
    [InlineData("https://justthegays.com/categories/bareback-082611/", false)]
    [InlineData("https://justthegays.com/performers/alex-doe-102617/", false)]
    [InlineData("https://justthegays.tv/video/alex-doe-fucks-sam-roe-pxeg5", false)]
    [InlineData("https://example.com/video/12345", false)]
    public async Task IsSupportedUrl_And_MatchAsync_FilterUrlsCorrectly(string url, bool expectedSupported)
    {
        Assert.Equal(expectedSupported, JustTheGaysComDownloaderExtension.IsSupportedUrl(url));

        if (!expectedSupported)
        {
            var extension = new JustTheGaysComDownloaderExtension();
            var match = await extension.MatchAsync(url, CancellationToken.None);
            Assert.Null(match);
        }
    }

    [Fact]
    public async Task JustTheGaysCom_VideoSceneHtml_DownloadsDirectMp4AndExtractsMetadata()
    {
        const string pageUrl = "https://justthegays.com/video/15307-danny-senpai-and-trent-benz-fuck-outside-102622/";
        const string mp4Url = "https://justthegays.hr1alfri.xyz/l/l/04.23/mig/107167.mp4";
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
                    <div class="player-embed">
                        <video id="video_kgvid_0" playsinline controls preload="auto">
                            <source src="https://justthegays.hr1alfri.xyz/l/l/04.23/mig/107167.mp4" type="video/mp4" data-res="Full" />
                        </video>
                    </div>
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
            .MapHtml(pageUrl, comHtml)
            .MapBytes(mp4Url, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32], "video/mp4");

        var extension = new JustTheGaysComDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var match = await extension.MatchAsync(pageUrl, CancellationToken.None);
        Assert.NotNull(match);
        Assert.Equal("Danny Senpai and Trent Benz fuck outside", match.Label);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                JustTheGaysComDownloaderExtension.PrimaryDownloaderId,
                pageUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["justthegays.com", "justthegays.hr1alfri.xyz"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Danny Senpai and Trent Benz fuck outside.mp4", result.OriginalFilename);
        Assert.True(File.Exists(Path.Combine(harness.TempDirectory, result.LocalPath)));

        var meta = result.InlineVideoMetadata;
        Assert.NotNull(meta);
        Assert.Equal("Danny Senpai and Trent Benz fuck outside", meta.Title);
        Assert.Equal("15307", meta.Code);
        Assert.Equal("2023-04-26", meta.Date);
        Assert.Equal("Hot outdoor bareback scene.", meta.Details);
        Assert.Equal("https://justthegays.com/wp-content/uploads/2023/04/107167p.jpg", meta.ImageUrl);
        Assert.Equal("Just The Gays", meta.StudioName);
        Assert.Equal(["Danny Senpai", "Trent Benz"], meta.PerformerNames);
        Assert.Equal(["Anal", "Outdoor", "Young/Twink"], meta.TagNames);
    }

    [Fact]
    public async Task JustTheGaysCom_WithoutInlineSource_FallsBackToWpRestApiVideopack()
    {
        const string pageUrl = "https://justthegays.com/video/98765-marco-polo-fucks-bastian-moreyra/";
        const string apiUrl = "https://justthegays.com/wp-json/wp/v2/video/98765";
        const string mp4Url = "https://justthegays.hr1alfri.xyz/l/l/05.24/mig/98765w.mp4";

        const string htmlWithoutVideoTag = """
            <!doctype html>
            <html lang="en">
            <head>
                <meta property="og:title" content="Marco Polo Fucks Bastian Moreyra - JustTheGays" />
                <meta property="og:image" content="https://justthegays.com/wp-content/uploads/2024/05/marco-polo.jpg" />
                <link rel="alternate" type="application/json" href="https://justthegays.com/wp-json/wp/v2/video/98765" />
            </head>
            <body>
                <article class="post-98765">
                    <h1>Marco Polo Fucks Bastian Moreyra</h1>
                </article>
            </body>
            </html>
            """;

        const string wpJson = """
            {
              "id": 98765,
              "date": "2024-05-12T09:15:00",
              "slug": "98765-marco-polo-fucks-bastian-moreyra",
              "link": "https://justthegays.com/video/98765-marco-polo-fucks-bastian-moreyra/",
              "title": { "rendered": "Marco Polo Fucks Bastian Moreyra" },
              "content": { "rendered": "<p>Marco Polo and Bastian Moreyra go at it in this amateur clip. Jerk to hot gay porn from Marco Polo, Bastian Moreyra. Thousands of hours of amateur Bareback, Latin videos. Stream the hottest and newest gay porn videos for free at JustTheGays.</p>" },
              "playbacks": "[videopack]https:\\/\\/justthegays.hr1alfri.xyz\\/l\\/l\\/05.24\\/mig\\/98765w.mp4[\\/videopack]"
            }
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(pageUrl, htmlWithoutVideoTag)
            .MapJson(apiUrl, wpJson)
            .MapBytes(mp4Url, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], "video/mp4");

        var extension = new JustTheGaysComDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                JustTheGaysComDownloaderExtension.PrimaryDownloaderId,
                pageUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["justthegays.com", "justthegays.hr1alfri.xyz"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Marco Polo Fucks Bastian Moreyra.mp4", result.OriginalFilename);
        Assert.NotNull(result.InlineVideoMetadata);
        Assert.Equal("98765", result.InlineVideoMetadata.Code);
        Assert.Equal("2024-05-12", result.InlineVideoMetadata.Date);
        Assert.Equal(["Marco Polo", "Bastian Moreyra"], result.InlineVideoMetadata.PerformerNames);
        Assert.Equal(["Bareback", "Latin"], result.InlineVideoMetadata.TagNames);
    }
}
