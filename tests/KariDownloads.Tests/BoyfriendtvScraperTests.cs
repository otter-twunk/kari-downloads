using Cove.Core.DTOs;
using Cove.Plugins;
using KariDownloads.Tests.Infrastructure;
using BoyfriendtvScraper;

namespace KariDownloads.Tests;

public sealed class BoyfriendtvScraperTests
{
    private const string VideoUrl = "https://www.boyfriendtv.com/videos/1426460/handsome-twink-audition/";
    private const string PerformerUrl = "https://www.boyfriendtv.com/pornstars/scott-demarco-2077/";

    private const string VideoPageHtml = """
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <title>Handsome Twink Audition | BoyFriendTV</title>
            <meta property="og:title" content="Handsome Twink Audition | BoyFriendTV" />
            <meta property="og:description" content="Exclusive bedroom audition with hot action." />
            <script type="application/ld+json">
            {
                "@context": "https://schema.org",
                "@type": "VideoObject",
                "name": "Handsome Twink Audition",
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
            <a href="/pornstars/joey-mills/videos/">Joey Mills</a>
            <a href="/pornstars/tyler-hill/videos/">Tyler Hill</a>
            <a href="/tags/audition">Audition</a>
            <a href="/tags/twink">Twink</a>
        </body>
        </html>
        """;

    private const string VideoSearchHtml = """
        <!doctype html>
        <html>
        <body>
            <div class="video-item">
                <a href="/videos/1426460/handsome-twink-audition/">
                    <img src="https://cdn.boyfriendtv.com/thumbs/1426460.jpg" alt="Handsome Twink Audition" />
                    Handsome Twink Audition | BoyFriendTV
                </a>
            </div>
            <div class="video-item">
                <a href="/videos/1500000/gym-buddies-workout/">Gym Buddies Workout - BoyFriendTV.com</a>
            </div>
        </body>
        </html>
        """;

    private const string PerformerPageHtml = """
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <title>Scott Demarco Gay Pornstar - BoyFriendTV.com</title>
            <meta property="og:title" content="Scott Demarco Gay Pornstar - BoyFriendTV.com" />
            <meta property="og:description" content="Popular performer profile." />
            <meta property="og:image" content="https://cdn.boyfriendtv.com/pornstars/scott-demarco.jpg" />
            <script type="application/ld+json">
            {
                "@context": "https://schema.org",
                "@type": "Person",
                "name": "Scott Demarco",
                "description": "Popular performer profile.",
                "image": "https://cdn.boyfriendtv.com/pornstars/scott-demarco.jpg"
            }
            </script>
        </head>
        <body></body>
        </html>
        """;

    private const string PerformerSearchHtml = """
        <!doctype html>
        <html>
        <body>
            <div class="performer-item">
                <a href="/pornstars/scott-demarco-2077/">Scott Demarco Gay Pornstar - BoyFriendTV.com</a>
            </div>
            <div class="performer-item">
                <a href="/pornstars/joey-mills/">Joey Mills | BoyFriendTV</a>
            </div>
        </body>
        </html>
        """;

    [Fact]
    public void IsSupportedVideoUrl_And_IsSupportedPerformerUrl_Work()
    {
        Assert.True(BoyfriendtvScraperExtension.IsSupportedVideoUrl("https://www.boyfriendtv.com/videos/1426460/selector/"));
        Assert.False(BoyfriendtvScraperExtension.IsSupportedVideoUrl("https://www.boyfriendtv.com/pornstars/scott-demarco/"));
        Assert.True(BoyfriendtvScraperExtension.IsSupportedPerformerUrl("https://www.boyfriendtv.com/pornstars/scott-demarco/"));
        Assert.False(BoyfriendtvScraperExtension.IsSupportedPerformerUrl("https://www.boyfriendtv.com/videos/1426460/"));
    }

    [Fact]
    public async Task ScrapeVideoAsync_ExtractsMetadataFromFixture()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(VideoUrl, VideoPageHtml);

        var extension = new BoyfriendtvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                BoyfriendtvScraperExtension.VideoScraperId,
                new VideoScrapeInput { Url = VideoUrl },
                new ScraperPermissions(["boyfriendtv.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Handsome Twink Audition", scraped.Title);
        Assert.Equal("1426460", scraped.Code);
        Assert.Equal("2024-03-20", scraped.Date);
        Assert.Equal("Exclusive bedroom audition with hot action.", scraped.Details);
        Assert.Equal("BoyFriendTV", scraped.StudioName);
        Assert.Equal("https://cdn.boyfriendtv.com/thumbs/1426460.jpg", scraped.ImageUrl);
        Assert.Contains("Joey Mills", scraped.PerformerNames);
        Assert.Contains("Tyler Hill", scraped.PerformerNames);
        Assert.Contains("Audition", scraped.TagNames);
        Assert.Contains("Twink", scraped.TagNames);
        Assert.Contains("Bareback", scraped.TagNames);
    }

    [Fact]
    public async Task SearchVideosAsync_ParsesVideoResults()
    {
        const string searchUrl = "https://www.boyfriend.tv/search/videos/twink/";
        using var harness = new FakeDownloaderHost()
            .MapHtml(searchUrl, VideoSearchHtml);

        var extension = new BoyfriendtvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var results = await extension.SearchVideosAsync(
            new ScraperRequest<string>(
                BoyfriendtvScraperExtension.VideoScraperId,
                "twink",
                new ScraperPermissions(["boyfriend.tv", "boyfriendtv.com"])),
            CancellationToken.None);

        Assert.NotNull(results);
        Assert.Equal(2, results.Count);
        Assert.Equal("Handsome Twink Audition", results[0].Title);
        Assert.Equal("1426460", results[0].Code);
        Assert.Equal("https://cdn.boyfriendtv.com/thumbs/1426460.jpg", results[0].ImageUrl);
        Assert.Equal("Gym Buddies Workout", results[1].Title);
        Assert.Equal("1500000", results[1].Code);
    }

    [Fact]
    public async Task ScrapeVideoAsync_WhenUrlMissing_SearchesByTitleAndScrapesCandidate()
    {
        const string searchUrl = "https://www.boyfriend.tv/search/videos/twink/";
        using var harness = new FakeDownloaderHost()
            .MapHtml(searchUrl, VideoSearchHtml)
            .MapHtml("https://www.boyfriend.tv/videos/1426460/handsome-twink-audition/", VideoPageHtml);

        var extension = new BoyfriendtvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                BoyfriendtvScraperExtension.VideoScraperId,
                new VideoScrapeInput { Title = "twink" },
                new ScraperPermissions(["boyfriend.tv", "boyfriendtv.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Handsome Twink Audition", scraped.Title);
        Assert.Equal("1426460", scraped.Code);
    }

    [Fact]
    public async Task ScrapePerformerAsync_ExtractsPerformerMetadata()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(PerformerUrl, PerformerPageHtml);

        var extension = new BoyfriendtvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapePerformerAsync(
            new ScraperRequest<PerformerScrapeInput>(
                BoyfriendtvScraperExtension.PerformerScraperId,
                new PerformerScrapeInput { Url = PerformerUrl },
                new ScraperPermissions(["boyfriendtv.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Scott Demarco", scraped.Name);
        Assert.Equal("Male", scraped.Gender);
        Assert.Equal("Popular performer profile.", scraped.Details);
        Assert.Equal("https://cdn.boyfriendtv.com/pornstars/scott-demarco.jpg", scraped.ImageUrl);
    }

    [Fact]
    public async Task ScrapePerformerAsync_FallsBackToSlugWhenPageFailsOrBlocked()
    {
        // Unmapped URL simulates 404 / network unreachable
        using var harness = new FakeDownloaderHost();

        var extension = new BoyfriendtvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapePerformerAsync(
            new ScraperRequest<PerformerScrapeInput>(
                BoyfriendtvScraperExtension.PerformerScraperId,
                new PerformerScrapeInput { Url = PerformerUrl },
                new ScraperPermissions(["boyfriendtv.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Scott Demarco", scraped.Name);
        Assert.Equal("Male", scraped.Gender);
    }

    [Fact]
    public async Task SearchPerformersAsync_ParsesPerformerResults()
    {
        const string searchUrl = "https://www.boyfriend.tv/search/pornstars/scott/";
        using var harness = new FakeDownloaderHost()
            .MapHtml(searchUrl, PerformerSearchHtml);

        var extension = new BoyfriendtvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var results = await extension.SearchPerformersAsync(
            new ScraperRequest<string>(
                BoyfriendtvScraperExtension.PerformerScraperId,
                "scott",
                new ScraperPermissions(["boyfriend.tv", "boyfriendtv.com"])),
            CancellationToken.None);

        Assert.NotNull(results);
        Assert.Equal(2, results.Count);
        Assert.Equal("Scott Demarco", results[0].Name);
        Assert.Equal("Male", results[0].Gender);
        Assert.Equal("Joey Mills", results[1].Name);
    }

    [Fact]
    public void IsSupportedUrls_SupportBoyfriendTvDomain()
    {
        Assert.True(BoyfriendtvScraperExtension.IsSupportedVideoUrl("https://www.boyfriend.tv/videos/1757468/dirty-daddy-massage/"));
        Assert.True(BoyfriendtvScraperExtension.IsSupportedPerformerUrl("https://www.boyfriend.tv/pornstars/tyler-saint-378/"));
    }

    [Fact]
    public void ParseVideoHtml_ExtractsModelCardPerformersWithNestedImg()
    {
        const string liveStyleHtml = """
            <html>
            <head><title>dirty daddy massage | BoyFriendTV</title></head>
            <body>
                <a class="model-card" href="/pornstars/tyler-saint-378/" target="_blank">
                    <img src="https://cc.boyfriend.tv/bftv/models/7b/tylersaint.jpg" alt="Photo">
                    Tyler Saint
                </a>
                <a class="btn-xs btn-tag" href="/tags/big-cock/" title="big cock">big cock</a>
            </body>
            </html>
            """;

        var scraped = BoyfriendtvScraperExtension.ParseVideoHtml(
            liveStyleHtml,
            "https://www.boyfriend.tv/videos/1757468/dirty-daddy-massage/");

        Assert.Equal("1757468", scraped.Code);
        Assert.Contains("Tyler Saint", scraped.PerformerNames);
        Assert.Contains("big cock", scraped.TagNames);
    }

    [Fact]
    public void ParsePerformerHtml_SupportsProfilePageAndImageObjectJsonLd()
    {
        const string profilePageHtml = """
            <html>
            <head>
                <title>Tyler Saint Gay Pornstar - BoyFriendTV.com</title>
                <meta property="og:description" content="Top collection of Tyler Saint gay porn videos." />
                <script type="application/ld+json">
                {
                    "@context": "https://schema.org",
                    "@type": "ProfilePage",
                    "mainEntity": {
                        "@type": "Person",
                        "name": "Tyler Saint",
                        "image": {
                            "@type": "ImageObject",
                            "url": "https://cc.boyfriend.tv/bftv/models/7b/tylersaint-6-480x640.jpg"
                        },
                        "eyeColor": "Brown",
                        "hairColor": "Brown",
                        "ethnicity": "White"
                    }
                }
                </script>
            </head>
            </html>
            """;

        var scraped = BoyfriendtvScraperExtension.ParsePerformerHtml(
            profilePageHtml,
            "https://www.boyfriend.tv/pornstars/tyler-saint-378/");

        Assert.Equal("Tyler Saint", scraped.Name);
        Assert.Equal("https://cc.boyfriend.tv/bftv/models/7b/tylersaint-6-480x640.jpg", scraped.ImageUrl);
        Assert.Equal("Brown", scraped.EyeColor);
        Assert.Equal("Brown", scraped.HairColor);
        Assert.Equal("White", scraped.Ethnicity);
        Assert.Equal("Top collection of Tyler Saint gay porn videos.", scraped.Details);
    }

    [Fact]
    public async Task ScrapePerformerAsync_WhenUrlMissing_SearchesByNameAndScrapesCandidate()
    {
        const string searchUrl = "https://www.boyfriend.tv/search/pornstars/Scott%20Demarco/";
        using var harness = new FakeDownloaderHost()
            .MapHtml(searchUrl, PerformerSearchHtml)
            .MapHtml("https://www.boyfriend.tv/pornstars/scott-demarco-2077/", PerformerPageHtml);

        var extension = new BoyfriendtvScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapePerformerAsync(
            new ScraperRequest<PerformerScrapeInput>(
                BoyfriendtvScraperExtension.PerformerScraperId,
                new PerformerScrapeInput { Name = "Scott Demarco" },
                new ScraperPermissions(["boyfriend.tv"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Scott Demarco", scraped.Name);
        Assert.Equal("Popular performer profile.", scraped.Details);
    }

    [Fact]
    public async Task ScrapeVideoAsync_LogsInWithDownloaderSiteCredentials_AndThrowsOnAgeGateWithoutSession()
    {
        const string targetUrl = "https://www.boyfriend.tv/videos/1426460/handsome-twink-audition/";
        const string loginUrl = "https://www.boyfriend.tv/login/";
        string? observedVideoCookie = null;

        using var harness = new FakeDownloaderHost();

        // Handle GET vs POST on /login/
        harness.MapResponse(loginUrl, req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        <form action="/login/" method="post">
                            <input type="hidden" name="csrf" value="tok123" />
                            <input type="text" name="username" />
                            <input type="password" name="pass" />
                        </form>
                        """)
                };
            }

            var resp = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Welcome</body></html>")
            };
            resp.Headers.Add("Set-Cookie", "PHPSESSID=auth_session_xyz; path=/; HttpOnly");
            return resp;
        });

        harness.MapResponse(targetUrl, req =>
        {
            observedVideoCookie = req.Headers.TryGetValues("Cookie", out var vals) ? string.Join("; ", vals) : null;
            if (observedVideoCookie?.Contains("PHPSESSID=auth_session_xyz", StringComparison.Ordinal) == true)
            {
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(VideoPageHtml)
                };
            }

            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body><h1>Age verification required</h1><p>Please verify your age.</p></body></html>")
            };
        });

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(harness.HttpClients);
        services.AddSingleton<IDownloaderSiteLoginProvider>(new StubLoginProvider(
            new DownloaderSiteLogin("boyfriend.tv", "kari", "secret")));
        using var provider = services.BuildServiceProvider();

        var extension = new BoyfriendtvScraperExtension();
        await extension.InitializeAsync(provider);

        var scraped = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                BoyfriendtvScraperExtension.VideoScraperId,
                new VideoScrapeInput { Url = targetUrl },
                new ScraperPermissions(["boyfriend.tv"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Handsome Twink Audition", scraped.Title);
        Assert.Contains("PHPSESSID=auth_session_xyz", observedVideoCookie);

        // Without credentials, age-verification wall raises an actionable InvalidOperationException
        var unauthExtension = new BoyfriendtvScraperExtension();
        await unauthExtension.InitializeAsync(harness.Services);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unauthExtension.ScrapeVideoAsync(
                new ScraperRequest<VideoScrapeInput>(
                    BoyfriendtvScraperExtension.VideoScraperId,
                    new VideoScrapeInput { Url = targetUrl },
                    new ScraperPermissions(["boyfriend.tv"])),
                CancellationToken.None));
    }

    private sealed class StubLoginProvider(DownloaderSiteLogin login) : IDownloaderSiteLoginProvider
    {
        public DownloaderSiteLogin? FindForUrl(string url) => login;
    }
}
