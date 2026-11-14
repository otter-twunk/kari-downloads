using System.Text.Json;
using BatchDownloader;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KariDownloads.Tests;

public sealed class BatchDownloaderTests
{
    private static string GetManifestPath()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extensions", "BatchDownloader", "extension.json"));
    }

    private static BatchDownloaderExtension CreateExtension()
    {
        var manifestPath = GetManifestPath();
        var json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<ExtensionManifestFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var extension = new BatchDownloaderExtension();
        ((IManifestAware)extension).ApplyManifest(manifest);
        return extension;
    }

    [Fact]
    public void GetUIManifest_RegistersBatchDownloadSidebarPage()
    {
        var extension = CreateExtension();
        var manifest = extension.GetUIManifest();

        Assert.NotNull(manifest);
        Assert.Equal("v1", manifest.FrontendRuntimeVersion);

        var page = Assert.Single(manifest.Pages);
        Assert.Equal("batch-download", page.Route);
        Assert.Equal("Paste & Download", page.Label);
        Assert.Equal("BatchDownloadPage", page.ComponentName);
        Assert.Equal("download", page.Icon);
        Assert.True(page.ShowInNav);
        Assert.Equal(45, page.NavOrder);
    }

    [Fact]
    public void ExtensionManifest_JsonIsValidAndMatchesCatalog()
    {
        var manifestPath = GetManifestPath();
        Assert.True(File.Exists(manifestPath), $"Manifest missing at {manifestPath}");

        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("karinauts.tools.batch-downloader", root.GetProperty("id").GetString());
        Assert.Equal("Batch URL Downloader", root.GetProperty("name").GetString());
        Assert.Equal("0.1.4", root.GetProperty("version").GetString());
        Assert.Equal("BatchDownloader.dll", root.GetProperty("entryDll").GetString());
        Assert.Equal("dist/bundle.js", root.GetProperty("jsBundle").GetString());
        Assert.Equal("dist/bundle.css", root.GetProperty("cssBundle").GetString());
        Assert.Equal("1.8.0", root.GetProperty("minCoveVersion").GetString());

        var categories = root.GetProperty("categories").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("ui", categories);
        Assert.Contains("tools", categories);
        Assert.Contains("content-management", categories);
    }

    [Fact]
    public void Extension_Implements_IApiExtension_And_IStatefulExtension()
    {
        var extension = CreateExtension();
        Assert.IsAssignableFrom<IApiExtension>(extension);
        Assert.IsAssignableFrom<IStatefulExtension>(extension);
    }

    [Fact]
    public void ExtractUrls_ParsesAndDeduplicatesUrlsCorrectly()
    {
        const string rawText = """
            Here are some links from iOS:
            https://site.com/video1,
            [Markdown Link](https://site.com/video2)
            Check out https://site.com/video1 again.
            Also an invalid one: not_a_url and ftp://unsupported.com
            """;

        var explicitUrls = new[] { "https://site.com/video3", "https://site.com/video1" };

        var extracted = BatchDownloaderExtension.ExtractUrls(rawText, explicitUrls);

        Assert.Equal(3, extracted.Count);
        Assert.Contains("https://site.com/video1", extracted);
        Assert.Contains("https://site.com/video2", extracted);
        Assert.Contains("https://site.com/video3", extracted);
    }

    [Fact]
    public async Task QueueService_SupportsAppendSkipPauseAndFinishAfterCurrent()
    {
        var store = new InMemoryExtensionStore();
        var downloaderService = new ControlledDownloaderService();
        var scopeFactory = new FakeExtensionScopeFactory(downloaderService);

        var queueService = new BatchDownloadQueueService();
        queueService.AttachStore(store);

        // 1. Enqueue 2 items in paused mode (AutoStart = false)
        var res1 = await queueService.EnqueueAsync(
            new BatchQueueApiRequest
            {
                Urls = ["https://site.com/v1", "https://site.com/v2"],
                AutoStart = false,
            },
            downloaderService,
            scopeFactory: null,
            jobService: null);

        Assert.Equal(2, res1.QueuedCount);
        Assert.Equal(2, res1.Queue.PendingCount);

        // 2. Append a 3rd item to the persistent queue
        var res2 = await queueService.EnqueueAsync(
            new BatchQueueApiRequest
            {
                Urls = ["https://site.com/v3"],
                AutoStart = false,
            },
            downloaderService,
            scopeFactory: null,
            jobService: null);

        Assert.Equal(1, res2.QueuedCount);
        Assert.Equal(3, res2.Queue.TotalCount);

        // 3. Skip the 2nd item while pending
        var secondId = res2.Queue.Items[1].Id;
        var afterSkip = await queueService.SkipItemAsync(secondId);
        Assert.Equal("Skipped", afterSkip.Items[1].Status);
        Assert.Equal(2, afterSkip.PendingCount);
        Assert.Equal(1, afterSkip.SkippedCount);

        // 4. Start processing item #1 and enable "Finish After Current Job" while item #1 is running
        var gate1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        downloaderService.OnDownload = async (req, ct) =>
        {
            if (req.Url == "https://site.com/v1")
            {
                started1.TrySetResult();
                await gate1.Task.WaitAsync(ct);
            }
            return new DownloaderImportResult($"/library/{Path.GetFileName(req.Url)}.mp4", 501);
        };

        await queueService.ResumeAsync();
        var loopTask = queueService.RunQueueProcessorLoopAsync(scopeFactory, null, CancellationToken.None);

        await started1.Task;
        var drainState = await queueService.SetFinishAfterCurrentAsync(true);
        Assert.True(drainState.FinishAfterCurrent);

        // Release item #1 -> it should finish to Completed, and queue should pause BEFORE starting item #3
        gate1.SetResult();
        await loopTask;

        var afterDrain = await queueService.GetStateAsync();
        Assert.Equal("Paused", afterDrain.RunnerStatus);
        Assert.False(afterDrain.FinishAfterCurrent);
        Assert.Equal("Completed", afterDrain.Items[0].Status);
        Assert.Equal("Skipped", afterDrain.Items[1].Status);
        Assert.Equal("Pending", afterDrain.Items[2].Status);

        // 5. Resume queue to process remaining item #3, then Pause mid-download -> returns item #3 to Pending
        var gate3 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started3 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        downloaderService.OnDownload = async (req, ct) =>
        {
            started3.TrySetResult();
            await gate3.Task.WaitAsync(ct);
            return new DownloaderImportResult("/library/v3.mp4", 503);
        };

        await queueService.ResumeAsync();
        var loopTask2 = queueService.RunQueueProcessorLoopAsync(scopeFactory, null, CancellationToken.None);
        await started3.Task;

        var pausedMidFlight = await queueService.PauseAsync();
        await loopTask2;

        Assert.Equal("Paused", pausedMidFlight.RunnerStatus);
        var finalState = await queueService.GetStateAsync();
        Assert.Equal("Pending", finalState.Items[2].Status);

        // 6. Resume and let item #3 complete
        downloaderService.OnDownload = (_, _) =>
            Task.FromResult<DownloaderImportResult?>(new DownloaderImportResult("/library/v3.mp4", 503));

        await queueService.ResumeAsync();
        await queueService.RunQueueProcessorLoopAsync(scopeFactory, null, CancellationToken.None);

        var completedState = await queueService.GetStateAsync();
        Assert.Equal("Idle", completedState.RunnerStatus);
        Assert.Equal(2, completedState.CompletedCount);
        Assert.Equal(1, completedState.SkippedCount);
        Assert.Equal(0, completedState.PendingCount);
    }

    private sealed class ControlledDownloaderService : IDownloaderService
    {
        public Func<DownloaderImportRequest, CancellationToken, Task<DownloaderImportResult?>>? OnDownload { get; set; }

        public Task<IReadOnlyList<DownloaderMatchDto>> MatchUrlAsync(string url, CancellationToken ct)
        {
            IReadOnlyList<DownloaderMatchDto> matches =
            [
                new DownloaderMatchDto(
                    DownloaderId: "test-dl",
                    DownloaderName: "Test Downloader",
                    SupportedEntity: "Video",
                    NormalizedUrl: url,
                    QualityOptions: [],
                    Label: $"Clip {url.Split('/').Last()}",
                    SourceUrl: url)
            ];
            return Task.FromResult(matches);
        }

        public async Task<DownloaderImportResult?> DownloadAndImportAsync(
            DownloaderImportRequest request,
            Cove.Core.Interfaces.IJobProgress? progress,
            CancellationToken ct)
        {
            if (OnDownload != null)
                return await OnDownload(request, ct);

            return new DownloaderImportResult("/library/video.mp4", 100);
        }
    }

    private sealed class FakeExtensionScopeFactory(IDownloaderService downloaderService) : IExtensionServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            var services = new ServiceCollection();
            services.AddSingleton(downloaderService);
            var sp = services.BuildServiceProvider();
            return sp.CreateScope();
        }

        public AsyncServiceScope CreateAsyncScope() => new(CreateScope());
    }

    private sealed class InMemoryExtensionStore : IExtensionStore
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_data.TryGetValue(key, out var val) ? val : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            _data[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            _data.Remove(key);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, string>(_data));
    }
}
