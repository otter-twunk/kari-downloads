using System.IO.Compression;
using System.Text.Json;
using Cove.Plugins;
using KariDownloads.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XcomDownloader;

namespace KariDownloads.Tests;

public sealed class XcomDownloaderTests
{
    [Fact]
    public void FindsExistingNativeToolThroughShadowLoadedAssembly()
    {
        var root = Directory.CreateTempSubdirectory("xcom-locator-test-").FullName;
        try
        {
            var instance = Directory.CreateDirectory(Path.Combine(root, "instance")).FullName;
            File.WriteAllText(Path.Combine(instance, "cove-config.json"), "{}");
            var tools = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;
            var executable = Path.Combine(tools, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
            File.WriteAllText(executable, "fixture");
            var loadedAssembly = Path.Combine(instance, "extensions", ".load-cache", "xcom", "123", "XcomDownloader.dll");
            Assert.Equal(executable, YtDlpLocator.Resolve(null, null, loadedAssembly));
            Assert.Equal(executable, YtDlpLocator.Resolve(null, instance, null));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ytDlpPath"] = "custom-yt-dlp" }).Build();
            Assert.Equal("custom-yt-dlp", YtDlpLocator.Resolve(config, instance, loadedAssembly));
        }
        finally { Directory.Delete(root, true); }
    }
    private const string Page = "https://x.com/test/status/123456789";
    [Fact]
    public async Task ExplicitCookieExportUsesTemporaryCopyAndPreservesOriginal()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "# Netscape HTTP Cookie File\n# fixture only\n");
            var original = await File.ReadAllTextAsync(path);
            var runner = new Runner();
            Assert.True(XcomUrlParser.TryParse(Page, out var status));
            await new YtDlpResolver(runner).ResolveAsync(status, "yt-dlp", default, path);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            var arguments = runner.Arguments!.ToList();
            Assert.DoesNotContain("--no-cookies", arguments);
            var tempPath = arguments[arguments.IndexOf("--cookies") + 1];
            Assert.NotEqual(path, tempPath);
            Assert.False(File.Exists(tempPath));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RestrictedPageNeverImportsPreviewImage()
    {
        using var host = new FakeDownloaderHost().MapHtml(Page,
            "<meta property='og:image' content='https://pbs.twimg.com/media/preview.jpg'><script>BlurredMediaTombstone</script>");
        using var services = new ServiceCollection().AddSingleton(host.HttpClients)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["enablePublicPhotoFallback"] = "true" }).Build()).BuildServiceProvider();
        var extension = new XcomDownloaderExtension(new Runner { Output = new(0, "{}", "") });
        await extension.InitializeAsync(services);
        var match = await extension.MatchAsync(Page, default);
        Assert.NotNull(match);
        Assert.Equal(XcomDownloaderExtension.PrimaryDownloaderId, match.DownloaderId);
        Assert.Contains("login-gated", match.Label);
        await Assert.ThrowsAsync<InvalidOperationException>(() => extension.DownloadAsync(
            new(XcomDownloaderExtension.ImageDownloaderId, Page, DownloaderEntity.Image, new DownloaderPermissions()), host, default));
        Assert.Empty(Directory.GetFileSystemEntries(host.TempDirectory));
    }
    private const string Low = "https://video.twimg.com/test/low.mp4";
    private const string High = "https://video.twimg.com/test/high.mp4";
    private static object Video(string id = "111") => new
    {
        id, title = "A title: with / unsafe? characters", description = "Fixture description", upload_date = "20261124", uploader = "Test author",
        formats = new object[]
        {
            new { format_id = "low", url = Low, ext = "mp4", protocol = "https", height = 360, width = 640, tbr = 100 },
            new { format_id = "high", url = High, ext = "mp4", protocol = "https", height = 720, width = 1280, tbr = 500 },
            new { format_id = "hls", url = "https://video.twimg.com/test/index.m3u8", ext = "mp4", protocol = "m3u8_native", height = 1080 },
            new { format_id = "foreign", url = "https://evil.test/film.mp4", ext = "mp4", protocol = "https", height = 2160 }
        }
    };
    private sealed class Runner : IXcomProcessRunner
    {
        public ResolverOutput Output { get; set; } = new(0, JsonSerializer.Serialize(Video()), "");
        public IReadOnlyList<string>? Arguments { get; private set; }
        public string? Executable { get; private set; }
        public bool Cancel { get; set; }
        public Task<ResolverOutput> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Cancel) throw new OperationCanceledException(ct);
            Executable = executable; Arguments = arguments;
            return Task.FromResult(Output);
        }
    }
    private static DownloaderRequest Request(string? quality = null, int? maxHeight = null) =>
        new(XcomDownloaderExtension.PrimaryDownloaderId, Page, DownloaderEntity.Video,
            new DownloaderPermissions(["video.twimg.com"]), quality) { MaxHeight = maxHeight };

    [Theory]
    [InlineData("https://x.com/test/status/123")]
    [InlineData("https://www.twitter.com/test/status/123/?s=20&t=tracking")]
    [InlineData("http://mobile.twitter.com/test/status/123/video/2")]
    [InlineData("https://m.x.com/i/web/status/123")]
    [InlineData("https://x.com/test/status/123/photo/1")]
    public void AcceptsStatusUrls(string url) => Assert.True(XcomDownloaderExtension.IsSupportedUrl(url));

    [Theory]
    [InlineData("https://x.com/test")]
    [InlineData("https://x.com/test/status/123evil")]
    [InlineData("https://x.com/test/status/123/anything")]
    [InlineData("https://x.com.evil.test/test/status/123")]
    [InlineData("https://evil.x.com/test/status/123")]
    [InlineData("https://user:pass@x.com/test/status/123")]
    [InlineData("file:///test/status/123")]
    [InlineData("https://x.com:8443/test/status/123")]
    public void RejectsOtherUrls(string url) => Assert.False(XcomDownloaderExtension.IsSupportedUrl(url));

    [Fact]
    public async Task MatchesActualQualitiesAndNormalizesTracking()
    {
        var runner = new Runner();
        var extension = new XcomDownloaderExtension(runner);
        var match = await extension.MatchAsync("https://twitter.com/test/status/123456789?s=20&t=secret", default);
        Assert.NotNull(match);
        Assert.Equal(Page, match.NormalizedUrl);
        Assert.Equal(new[] { "high", "low" }, match.QualityOptions!.Select(q => q.Id));
        Assert.Equal(Page, runner.Arguments!.Last());
        Assert.Contains("--ignore-config", runner.Arguments!);
        Assert.Contains("--no-cookies-from-browser", runner.Arguments!);
        Assert.Contains("twitter.*", runner.Arguments!);
        Assert.DoesNotContain(runner.Arguments!, a => a.Contains("secret"));
    }

    [Theory]
    [InlineData(null, null, "high")]
    [InlineData(null, 480, "low")]
    [InlineData("low", 1080, "low")]
    [InlineData("high", 360, "high")]
    public async Task DownloadsSelectedProgressiveVideoAndMetadata(string? quality, int? maxHeight, string expected)
    {
        using var host = new FakeDownloaderHost().MapBytes(Low, [1, 2], "video/mp4").MapBytes(High, [3, 4], "video/mp4");
        var extension = new XcomDownloaderExtension(new Runner());
        var result = await extension.DownloadAsync(Request(quality, maxHeight), host, default);
        Assert.NotNull(result);
        Assert.StartsWith(host.TempDirectory + Path.DirectorySeparatorChar, Path.GetFullPath(result.LocalPath));
        Assert.Equal(expected == "low" ? new byte[] { 1, 2 } : new byte[] { 3, 4 }, await File.ReadAllBytesAsync(result.LocalPath));
        Assert.DoesNotContain('/', result.OriginalFilename!);
        Assert.DoesNotContain(':', result.OriginalFilename!);
        Assert.Equal("2026-11-24", result.InlineVideoMetadata!.Date);
        Assert.Equal("Test author", result.InlineVideoMetadata.StudioName);
        Assert.Equal(new[] { Page }, result.InlineVideoMetadata.Urls);
        Assert.Equal(0, host.ProgressUpdates.First().Progress);
        Assert.Equal(100, host.ProgressUpdates.Last().Progress);
        Assert.True(host.ProgressUpdates.Zip(host.ProgressUpdates.Skip(1)).All(p => p.First.Progress <= p.Second.Progress));
    }

    [Fact]
    public async Task MatchesAndDownloadsEachVideoByStableMediaId()
    {
        var runner = new Runner { Output = new(0, JsonSerializer.Serialize(new { entries = new[] { Video("111"), Video("222") } }), "") };
        var extension = new XcomDownloaderExtension(runner);
        var matches = await extension.MatchAllAsync(Page, default);
        Assert.Equal(2, matches.Count);
        Assert.EndsWith("#cove-media=222", matches[1].NormalizedUrl);
        using var host = new FakeDownloaderHost().MapBytes(High, [1], "video/mp4");
        var result = await extension.DownloadAsync(Request() with { Url = matches[1].NormalizedUrl }, host, default);
        Assert.NotNull(result);
        Assert.Equal(Page, runner.Arguments!.Last());
    }

    [Theory]
    [InlineData(1, "ERROR login required cookie=secret", "publicly accessible")]
    [InlineData(1, "ERROR HTTP 429 token=secret", "rate limit")]
    [InlineData(1, "ERROR No video could be found", "No downloadable video")]
    [InlineData(1, "ERROR random token=secret", "could not resolve")]
    [InlineData(0, "", "incompatible")]
    public async Task ResolverFailuresAreActionableAndDoNotLeakStderr(int code, string error, string expected)
    {
        var extension = new XcomDownloaderExtension(new Runner { Output = new(code, "not json", error) });
        using var host = new FakeDownloaderHost();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => extension.DownloadAsync(Request(), host, default));
        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", exception.Message);
        Assert.Empty(Directory.GetFileSystemEntries(host.TempDirectory));
    }

    [Fact]
    public async Task EmptyMetadataUnavailableQualityAndCancellationLeaveNoFiles()
    {
        using var host = new FakeDownloaderHost();
        var runner = new Runner { Output = new(0, "{}", "") };
        var extension = new XcomDownloaderExtension(runner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => extension.DownloadAsync(Request(), host, default));
        runner.Output = new(0, JsonSerializer.Serialize(Video()), "");
        await Assert.ThrowsAsync<InvalidOperationException>(() => extension.DownloadAsync(Request("missing"), host, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => extension.DownloadAsync(Request(maxHeight: 100), host, default));
        runner.Cancel = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extension.DownloadAsync(Request(), host, default));
        Assert.Empty(Directory.GetFileSystemEntries(host.TempDirectory));
    }

    [Fact]
    public async Task IncompleteDownloadCleansOnlyOwnedDirectory()
    {
        using var host = new FakeDownloaderHost().MapResponse(High, _ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
            response.Content.Headers.ContentLength = 50;
            return response;
        });
        var existing = Path.Combine(host.TempDirectory, "existing.txt");
        await File.WriteAllTextAsync(existing, "keep");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new XcomDownloaderExtension(new Runner()).DownloadAsync(Request(), host, default));
        Assert.Equal(new[] { existing }, Directory.GetFileSystemEntries(host.TempDirectory));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task PublicPhotoFallbackCreatesImageOrGallery(int count)
    {
        var html = "<meta property='og:title' content='Photos: test'>";
        using var host = new FakeDownloaderHost();
        for (var i = 1; i <= count; i++)
        {
            var url = $"https://pbs.twimg.com/media/photo{i}?format=jpg&name=orig";
            html += $"<meta content='{url.Replace("&", "&amp;")}' property='og:image'>";
            host.MapBytes(url, [(byte)i], "image/jpeg");
        }
        html += "<meta property='og:image' content='https://evil.test/photo.jpg'>";
        host.MapHtml(Page, html);
        using var services = new ServiceCollection().AddSingleton(host.HttpClients)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["enablePublicPhotoFallback"] = "true", ["ytDlpPath"] = "custom-yt-dlp" }).Build()).BuildServiceProvider();
        var runner = new Runner { Output = new(0, "{}", "") };
        var extension = new XcomDownloaderExtension(runner);
        await extension.InitializeAsync(services);
        var match = await extension.MatchAsync(Page, default);
        Assert.NotNull(match);
        var entity = count == 1 ? DownloaderEntity.Image : DownloaderEntity.Gallery;
        var result = await extension.DownloadAsync(new(match.DownloaderId, Page, entity, new DownloaderPermissions()), host, default);
        Assert.NotNull(result);
        Assert.True(File.Exists(result.LocalPath));
        Assert.Equal("custom-yt-dlp", runner.Executable);
        if (count == 4)
        {
            using var zip = ZipFile.OpenRead(result.LocalPath);
            Assert.Equal(4, zip.Entries.Count);
            Assert.Equal("X / Twitter", result.InlineGalleryMetadata!.StudioName);
        }
        else Assert.NotNull(result.InlineImageMetadata);
    }

    [Fact]
    public async Task MissingExecutableHasUsefulError()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new XcomProcessRunner().RunAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), [], default));
        Assert.Contains("install yt-dlp", error.Message);
    }
}
