using Cove.Plugins;
using GayFappyDownloader;
using KariDownloads.Tests.Infrastructure;

namespace KariDownloads.Tests;

public sealed class GayFappyDownloaderTests
{
    private const string SampleVideoHtml = """
        <!DOCTYPE html>
        <html lang="en-US">
        <head>
            <title>Tiktoker blake hoffman jerks off - Gay Fappy</title>
            <meta property="og:title" content="Tiktoker blake hoffman jerks off" />
            <meta property="og:description" content="Tiktoker blake hoffman jerks off" />
            <meta property="og:image" content="https://gayfappy.com/wp-content/uploads/2025/09/tiktoker-blake-hoffman-jerks-off.jpg" />
            <meta property="article:published_time" content="2026-10-31T12:05:50+00:00" />
            <link rel="alternate" type="application/json" href="https://gayfappy.com/index.php/wp-json/wp/v2/posts/66189" />
        </head>
        <body>
            <article id="post-66189" class="post-66189 post category-videos tag-blake-hoffman tag-jerk-off tag-solo tag-young-twink">
                <h1 class="post-title entry-title">Tiktoker blake hoffman jerks off</h1>
                <time datetime="2026-10-31" class="post-date entry-date updated">October 31, 2026</time>
                <div class="entry-content">
                    <p class="post_desc">Tiktoker blake hoffman jerks off</p>
                    <p><span id="more-66189"></span></p>
                    <div class="thumbs_wrap">
                        <a class="video-play" href="https://gayfappy.com/f/videos/84862d00b647/4/fb3bb6690f8a951a3853789a64726230/4609c3d08186.mp4" data-fancybox="gallery" data-type="html5video" data-width="700" data-height="450">
                            <img class="a_thumb_img" src="https://gayfappy.com/f/thumbs/84862d00b647/4/fb3bb6690f8a951a3853789a64726230/0_2f2b1c6b4dd1.jpg" width="700" />
                        </a>
                    </div>
                </div>
                <span class="post-category"><a href="https://gayfappy.com/index.php/category/videos/" rel="tag">Videos</a></span>
                <span class="post-tag"><a href="https://gayfappy.com/index.php/tag/blake-hoffman/" rel="tag">blake hoffman</a>, <a href="https://gayfappy.com/index.php/tag/jerk-off/" rel="tag">jerk off</a>, <a href="https://gayfappy.com/index.php/tag/solo/" rel="tag">solo</a>, <a href="https://gayfappy.com/index.php/tag/young-twink/" rel="tag">young/twink</a></span>
            </article>
        </body>
        </html>
        """;

    private const string SampleGalleryHtml = """
        <!DOCTYPE html>
        <html lang="en-US">
        <head>
            <title>Blasian babe - Gay Fappy</title>
            <meta property="og:title" content="Blasian babe" />
            <meta property="og:description" content="Blasian babe leaks" />
            <meta property="og:image" content="https://gayfappy.com/wp-content/uploads/2026/10/blasian-babe.jpg" />
            <meta property="article:published_time" content="2026-11-02T02:43:51+00:00" />
        </head>
        <body>
            <article id="post-95457" class="post-95457 post category-cute category-amateur tag-blasian-babe">
                <h1 class="post-title entry-title">Blasian babe</h1>
                <time datetime="2026-11-02" class="post-date entry-date updated">November 2, 2026</time>
                <div class="entry-content">
                    <p class="post_desc">Blasian babe collection</p>
                    <div class="thumbs_wrap">
                        <a href="https://gayfappy.com/rss/thumbs/oops/img1.jpg" data-fancybox="gallery" class="a_thumb">
                            <img src="https://gayfappy.com/rss/thumbs/oops/img1.jpg" class="a_thumb_img" />
                        </a>
                        <a href="https://gayfappy.com/rss/thumbs/oops/img2.jpg" data-fancybox="gallery" class="a_thumb">
                            <img src="https://gayfappy.com/rss/thumbs/oops/img2.jpg" class="a_thumb_img" />
                        </a>
                    </div>
                </div>
                <span class="post-category"><a href="https://gayfappy.com/index.php/category/cute/" rel="tag">Cute</a>, <a href="https://gayfappy.com/index.php/category/amateur/" rel="tag">Amateur</a></span>
                <span class="post-tag"><a href="https://gayfappy.com/index.php/tag/blasian-babe/" rel="tag">blasian babe</a></span>
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
            "rendered": "<p class=\"post_desc\">Tiktoker blake hoffman jerks off</p><div class=\"thumbs_wrap\"><a class=\"video-play\" href=\"https://gayfappy.com/f/videos/84862d00b647/4/fb3bb6690f8a951a3853789a64726230/4609c3d08186.mp4\" data-fancybox=\"gallery\"><img src=\"https://gayfappy.com/thumb.jpg\" /></a></div>"
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

    [Theory]
    [InlineData("https://gayfappy.com/index.php/66189/tiktoker-blake-hoffman-jerks-off/", true)]
    [InlineData("https://gayfappy.com/66189/tiktoker-blake-hoffman-jerks-off/", true)]
    [InlineData("https://gayfappy.com/index.php/66189/", true)]
    [InlineData("https://gayfappy.com/66189/", true)]
    [InlineData("https://gayfappy.com/?p=66189", true)]
    [InlineData("https://gayfappy.com/index.php/?p=66189", true)]
    [InlineData("https://gayfappy.com/index.php/wp-json/wp/v2/posts/66189", true)]
    [InlineData("https://gayfappy.com/f/videos/84862d00b647/4/fb3bb6690f8a951a3853789a64726230/4609c3d08186.mp4", true)]
    [InlineData("https://gayfappy.com/index.php/category/videos/", false)]
    [InlineData("https://gayfappy.com/index.php/tag/blake-hoffman/", false)]
    [InlineData("https://gayfappy.com/index.php/dmca/", false)]
    [InlineData("https://example.com/video/12345", false)]
    public async Task IsSupportedUrl_And_MatchAsync_FilterUrlsCorrectly(string url, bool expectedSupported)
    {
        Assert.Equal(expectedSupported, GayFappyDownloaderExtension.IsSupportedUrl(url));

        if (!expectedSupported)
        {
            var extension = new GayFappyDownloaderExtension();
            var match = await extension.MatchAsync(url, CancellationToken.None);
            Assert.Null(match);
        }
    }

    [Fact]
    public async Task VideoPostHtml_DownloadsDirectMp4AndExtractsMetadata()
    {
        const string pageUrl = "https://gayfappy.com/index.php/66189/tiktoker-blake-hoffman-jerks-off/";
        const string mp4Url = "https://gayfappy.com/f/videos/84862d00b647/4/fb3bb6690f8a951a3853789a64726230/4609c3d08186.mp4";

        using var harness = new FakeDownloaderHost()
            .MapHtml(pageUrl, SampleVideoHtml)
            .MapBytes(mp4Url, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32], "video/mp4");

        var extension = new GayFappyDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var match = await extension.MatchAsync(pageUrl, CancellationToken.None);
        Assert.NotNull(match);
        Assert.Equal(GayFappyDownloaderExtension.VideoDownloaderId, match.DownloaderId);
        Assert.Equal("Tiktoker blake hoffman jerks off", match.Label);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                GayFappyDownloaderExtension.VideoDownloaderId,
                pageUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["gayfappy.com"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Tiktoker blake hoffman jerks off.mp4", result.OriginalFilename);
        Assert.True(File.Exists(Path.Combine(harness.TempDirectory, result.LocalPath)));

        var meta = result.InlineVideoMetadata;
        Assert.NotNull(meta);
        Assert.Equal("Tiktoker blake hoffman jerks off", meta.Title);
        Assert.Equal("66189", meta.Code);
        Assert.Equal("2026-10-31", meta.Date);
        Assert.Equal("Gay Fappy", meta.StudioName);
        Assert.Contains("Blake Hoffman", meta.PerformerNames);
        Assert.Contains("young/twink", meta.TagNames);
        Assert.Contains("Videos", meta.TagNames);
    }

    [Fact]
    public async Task VideoPostWpJson_DownloadsDirectMp4AndExtractsMetadata()
    {
        const string pageUrl = "https://gayfappy.com/index.php/wp-json/wp/v2/posts/66189";
        const string mp4Url = "https://gayfappy.com/f/videos/84862d00b647/4/fb3bb6690f8a951a3853789a64726230/4609c3d08186.mp4";

        using var harness = new FakeDownloaderHost()
            .MapJson(pageUrl, SampleWpJsonPost)
            .MapBytes(mp4Url, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], "video/mp4");

        var extension = new GayFappyDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                GayFappyDownloaderExtension.VideoDownloaderId,
                pageUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["gayfappy.com"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Tiktoker blake hoffman jerks off.mp4", result.OriginalFilename);
        Assert.NotNull(result.InlineVideoMetadata);
        Assert.Equal("66189", result.InlineVideoMetadata.Code);
        Assert.Equal("2026-10-31", result.InlineVideoMetadata.Date);
        Assert.Contains("Blake Hoffman", result.InlineVideoMetadata.PerformerNames);
    }

    [Fact]
    public async Task GalleryPostHtml_PacksImagesIntoZipArchive()
    {
        const string pageUrl = "https://gayfappy.com/index.php/95457/blasian-babe/";
        const string img1 = "https://gayfappy.com/rss/thumbs/oops/img1.jpg";
        const string img2 = "https://gayfappy.com/rss/thumbs/oops/img2.jpg";

        using var harness = new FakeDownloaderHost()
            .MapHtml(pageUrl, SampleGalleryHtml)
            .MapBytes(img1, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10], "image/jpeg")
            .MapBytes(img2, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10], "image/jpeg");

        var extension = new GayFappyDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var match = await extension.MatchAsync(pageUrl, CancellationToken.None);
        Assert.NotNull(match);
        Assert.Equal(GayFappyDownloaderExtension.GalleryDownloaderId, match.DownloaderId);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                GayFappyDownloaderExtension.GalleryDownloaderId,
                pageUrl,
                DownloaderEntity.Gallery,
                new DownloaderPermissions(["gayfappy.com"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.EndsWith(".zip", result.LocalPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(harness.TempDirectory, result.LocalPath)));

        var galleryMeta = result.InlineGalleryMetadata;
        Assert.NotNull(galleryMeta);
        Assert.Equal("Blasian babe", galleryMeta.Title);
        Assert.Equal("95457", galleryMeta.Code);
        Assert.Equal("2026-11-02", galleryMeta.Date);
        Assert.Equal("Gay Fappy", galleryMeta.StudioName);
        Assert.Contains("Cute", galleryMeta.TagNames);
    }

    [Fact]
    public async Task DirectMp4Url_DownloadsDirectly()
    {
        const string directMp4 = "https://gayfappy.com/f/videos/84862d00b647/4/fb3bb6690f8a951a3853789a64726230/4609c3d08186.mp4";

        using var harness = new FakeDownloaderHost()
            .MapBytes(directMp4, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], "video/mp4");

        var extension = new GayFappyDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var match = await extension.MatchAsync(directMp4, CancellationToken.None);
        Assert.NotNull(match);
        Assert.Equal(GayFappyDownloaderExtension.VideoDownloaderId, match.DownloaderId);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                GayFappyDownloaderExtension.VideoDownloaderId,
                directMp4,
                DownloaderEntity.Video,
                new DownloaderPermissions(["gayfappy.com"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(File.Exists(Path.Combine(harness.TempDirectory, result.LocalPath)));
    }
}
