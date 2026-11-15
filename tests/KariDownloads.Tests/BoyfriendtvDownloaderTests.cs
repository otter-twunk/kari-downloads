using Cove.Plugins;
using KariDownloads.Tests.Infrastructure;
using BoyfriendtvDownloader;
using Microsoft.Extensions.DependencyInjection;

namespace KariDownloads.Tests;

public sealed class BoyfriendtvDownloaderTests
{
    private const string SampleVideoPageUrl = "https://www.boyfriendtv.com/videos/1426460/selector/";
    private const string CanonicalVideoPageUrl = "https://www.boyfriend.tv/videos/1426460/selector/";
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
            .MapHtml(CanonicalVideoPageUrl, RealisticPageHtml);

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
            .MapHtml(CanonicalVideoPageUrl, RealisticPageHtml)
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
            .MapHtml(CanonicalVideoPageUrl, RealisticPageHtml)
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
        Assert.Contains("rta_terms_accepted=1", observedCdnCookie);
    }

    [Fact]
    public async Task DownloadAsync_ThrowsActionableDiagnostic_WhenRtaGateIsReturned()
    {
        const string videoUrl = "https://www.boyfriend.tv/videos/1426460/selector/";
        const string rtaGateHtml = """
            <html>
            <head><title>Gay Tube - Free Gay Porn Videos at BoyFriendTV</title></head>
            <body><div class="rta-section"><a href="?rta_terms_accepted=true">Enter</a></div></body>
            </html>
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(videoUrl, rtaGateHtml);

        var extension = new BoyfriendtvDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => extension.DownloadAsync(
            new DownloaderRequest(
                BoyfriendtvDownloaderExtension.PrimaryDownloaderId,
                videoUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["boyfriend.tv"])),
            harness,
            CancellationToken.None));

        Assert.Contains("age-verification or login wall", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://www.boyfriendtv.com/videos/1426460/selector/")]
    [InlineData("https://boyfriendtv.com/videos/1426460/selector/")]
    public async Task DownloadAsync_RewritesLegacyBoyfriendtvComHost_ToAvoidCloudflare(string legacyUrl)
    {
        const string canonicalUrl = "https://www.boyfriend.tv/videos/1426460/selector/";
        const string cloudflareHtml = """
            <html><head><title>Just a moment...</title></head>
            <body><script src="https://challenges.cloudflare.com/x.js"></script></body></html>
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(legacyUrl, cloudflareHtml, System.Net.HttpStatusCode.Forbidden)
            .MapHtml(canonicalUrl, RealisticPageHtml)
            .MapBytes(Stream1080Url, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], "video/mp4");

        var extension = new BoyfriendtvDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                BoyfriendtvDownloaderExtension.PrimaryDownloaderId,
                legacyUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["boyfriend.tv", "boyfriendtv.com", "cdn.boyfriendtv.com"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("1426460", result.InlineVideoMetadata?.Code);
    }

    [Fact]
    public async Task DownloadAsync_RetriesOn429TooManyRequests()
    {
        const string videoUrl = "https://www.boyfriend.tv/videos/1426460/selector/";
        var attempts = 0;

        using var harness = new FakeDownloaderHost();
        harness.MapResponse(videoUrl, _ =>
        {
            attempts++;
            if (attempts == 1)
            {
                var rateLimited = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
                rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return rateLimited;
            }

            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(RealisticPageHtml)
            };
        });
        harness.MapBytes(Stream1080Url, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], "video/mp4");

        var extension = new BoyfriendtvDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                BoyfriendtvDownloaderExtension.PrimaryDownloaderId,
                videoUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["boyfriend.tv", "cdn.boyfriendtv.com"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task DownloadAsync_UsesCookiePastedInDownloadsPageLogin_SavedUnderLegacyDomain()
    {
        // Mirrors the real saved entry: site "https://www.boyfriendtv.com/", queued URL on the legacy domain.
        const string legacyUrl = "https://www.boyfriendtv.com/videos/1426460/selector/";
        string? observedPageCookie = null;

        using var harness = new FakeDownloaderHost();
        harness.MapResponse(CanonicalVideoPageUrl, req =>
        {
            observedPageCookie = req.Headers.TryGetValues("Cookie", out var vals) ? string.Join("; ", vals) : null;
            var html = observedPageCookie?.Contains("PHPSESSID=browser_sess", StringComparison.Ordinal) == true
                ? RealisticPageHtml
                : "<html><body><div class=\"rta-section\">Adults only</div></body></html>";
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(html) };
        });
        harness.MapBytes(Stream1080Url, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], "video/mp4");

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(harness.HttpClients);
        services.AddSingleton<Cove.Plugins.IDownloaderSiteLoginProvider>(new HostMatchingLoginProvider(
            new Cove.Plugins.DownloaderSiteLogin("https://www.boyfriendtv.com/", "kari", "Cookie: PHPSESSID=browser_sess; kt_member=abc")));
        using var provider = services.BuildServiceProvider();

        var extension = new BoyfriendtvDownloaderExtension();
        await extension.InitializeAsync(provider);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                BoyfriendtvDownloaderExtension.PrimaryDownloaderId,
                legacyUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["boyfriend.tv", "boyfriendtv.com", "cdn.boyfriendtv.com"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("PHPSESSID=browser_sess", observedPageCookie);
        Assert.Contains("kt_member=abc", observedPageCookie);
        Assert.DoesNotContain("Cookie:", observedPageCookie);
    }

    [Fact]
    public async Task DownloadAsync_PasswordLoginBlockedByCloudflare_ExplainsCookiePasteAndCachesFailure()
    {
        const string loginUrl = "https://www.boyfriend.tv/login/";
        var loginAttempts = 0;

        using var harness = new FakeDownloaderHost();
        harness.MapResponse(loginUrl, _ =>
        {
            loginAttempts++;
            var resp = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html><head><title>Just a moment...</title></head><body></body></html>")
            };
            resp.Headers.Add("Cf-Mitigated", "challenge");
            return resp;
        });
        harness.MapHtml(CanonicalVideoPageUrl, RealisticPageHtml);

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(harness.HttpClients);
        services.AddSingleton<Cove.Plugins.IDownloaderSiteLoginProvider>(new HostMatchingLoginProvider(
            new Cove.Plugins.DownloaderSiteLogin("https://www.boyfriendtv.com/", "kari", "plain-password-123")));
        using var provider = services.BuildServiceProvider();

        var extension = new BoyfriendtvDownloaderExtension();
        await extension.InitializeAsync(provider);

        DownloaderRequest Request() => new(
            BoyfriendtvDownloaderExtension.PrimaryDownloaderId,
            SampleVideoPageUrl,
            DownloaderEntity.Video,
            new DownloaderPermissions(["boyfriend.tv", "boyfriendtv.com"]));

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => extension.DownloadAsync(Request(), harness, CancellationToken.None));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => extension.DownloadAsync(Request(), harness, CancellationToken.None));

        Assert.Contains("Cloudflare", first.Message);
        Assert.Contains("Password field", first.Message);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(1, loginAttempts);
    }

    [Fact]
    public async Task DownloadAsync_PasswordLoginWithoutNewSessionCookie_FailsInsteadOfFalseSuccess()
    {
        const string loginUrl = "https://www.boyfriend.tv/login/";

        using var harness = new FakeDownloaderHost();
        harness.MapResponse(loginUrl, req => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(req.Method == HttpMethod.Get
                ? """<form action="/login/" method="post"><input type="text" name="username" /><input type="password" name="pass" /></form>"""
                : "<html>try again</html>")
        });
        harness.MapHtml(CanonicalVideoPageUrl, RealisticPageHtml);

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(harness.HttpClients);
        services.AddSingleton<Cove.Plugins.IDownloaderSiteLoginProvider>(
            new StubLoginProvider(new Cove.Plugins.DownloaderSiteLogin("boyfriend.tv", "kari", "wrong-password")));
        using var provider = services.BuildServiceProvider();

        var extension = new BoyfriendtvDownloaderExtension();
        await extension.InitializeAsync(provider);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => extension.DownloadAsync(
            new DownloaderRequest(
                BoyfriendtvDownloaderExtension.PrimaryDownloaderId,
                CanonicalVideoPageUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["boyfriend.tv"])),
            harness,
            CancellationToken.None));

        Assert.Contains("login failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PHPSESSID=abc123", true, "PHPSESSID=abc123")]
    [InlineData("Cookie: PHPSESSID=abc; kt_member=x", true, "PHPSESSID=abc; kt_member=x")]
    [InlineData("PHPSESSID=abc; kt_member=x;", true, "PHPSESSID=abc; kt_member=x")]
    [InlineData("hunter2", false, "")]
    [InlineData("my pass=word", false, "")]
    [InlineData("", false, "")]
    public void TryParseCookieHeader_DetectsPastedCookies(string input, bool expected, string expectedCookie)
    {
        // The shared helper is compiled into both BoyfriendTV assemblies, so resolve it from the downloader's.
        var helper = typeof(BoyfriendtvDownloaderExtension).Assembly.GetType("KariDownloads.Shared.BoyfriendtvAuthHelper", throwOnError: true)!;
        var method = helper.GetMethod("TryParseCookieHeader", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
        object?[] args = [input, null];
        var ok = (bool)method.Invoke(null, args)!;
        Assert.Equal(expected, ok);
        Assert.Equal(expectedCookie, (string)args[1]!);
    }

    private sealed class StubLoginProvider(Cove.Plugins.DownloaderSiteLogin login) : Cove.Plugins.IDownloaderSiteLoginProvider
    {
        public Cove.Plugins.DownloaderSiteLogin? FindForUrl(string url) => login;
    }

    /// <summary>Matches by host like Cove does, so a login saved for one domain does not match the other.</summary>
    private sealed class HostMatchingLoginProvider(Cove.Plugins.DownloaderSiteLogin login) : Cove.Plugins.IDownloaderSiteLoginProvider
    {
        public Cove.Plugins.DownloaderSiteLogin? FindForUrl(string url)
        {
            static string Host(string value) => (Uri.TryCreate(value, UriKind.Absolute, out var u) ? u.Host : value)
                .ToLowerInvariant().Replace("www.", string.Empty);
            return Host(url) == Host(login.Site) ? login : null;
        }
    }
}
