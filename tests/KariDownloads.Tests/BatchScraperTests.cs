using System.Text.Json;
using BatchScraper;
using Cove.Plugins;
using Xunit;

namespace KariDownloads.Tests;

public sealed class BatchScraperTests
{
    private static string GetManifestPath()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extensions", "BatchScraper", "extension.json"));
    }

    private static BatchScraperExtension CreateExtension()
    {
        var manifestPath = GetManifestPath();
        var json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<ExtensionManifestFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var extension = new BatchScraperExtension();
        ((IManifestAware)extension).ApplyManifest(manifest);
        return extension;
    }

    [Fact]
    public void GetActions_RegistersBulkVideoScrapeAction()
    {
        var extension = CreateExtension();
        var actions = extension.GetActions();

        var action = Assert.Single(actions);
        Assert.Equal("batch-video-scrape", action.Id);
        Assert.Equal("Scrape All", action.Label);
        Assert.Equal("bulk", action.ActionType);
        Assert.Contains("video", action.EntityTypes);
        Assert.Equal("openBatchScraper", action.HandlerName);
        Assert.True(action.SuppressSuccessAlert);
    }

    [Fact]
    public void GetUIManifest_RegistersFloatingSlotAndBulkAction()
    {
        var extension = CreateExtension();
        var manifest = extension.GetUIManifest();

        Assert.NotNull(manifest);
        Assert.Equal("v1", manifest.FrontendRuntimeVersion);
        Assert.Equal("dist/bundle.js", manifest.JsBundleUrl);
        Assert.Equal("dist/bundle.css", manifest.CssBundleUrl);

        var slot = Assert.Single(manifest.Slots);
        Assert.Equal("app-floating-ui", slot.Slot);
        Assert.Equal("BatchScraperModal", slot.ComponentName);

        var action = Assert.Single(manifest.Actions);
        Assert.Equal("batch-video-scrape", action.Id);
        Assert.Equal("Scrape All", action.Label);
        Assert.Equal("bulk", action.ActionType);
        Assert.Contains("video", action.EntityTypes);
        Assert.Equal("openBatchScraper", action.HandlerName);
    }

    [Fact]
    public void ExtensionManifest_JsonIsValidAndMatchesCatalog()
    {
        var manifestPath = GetManifestPath();
        Assert.True(File.Exists(manifestPath), $"Manifest missing at {manifestPath}");

        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("karinauts.tools.batch-scraper", root.GetProperty("id").GetString());
        Assert.Equal("Batch Video Scraper", root.GetProperty("name").GetString());
        Assert.Equal("0.1.4", root.GetProperty("version").GetString());
        Assert.Equal("BatchScraper.dll", root.GetProperty("entryDll").GetString());
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
    public async Task QueueService_PersistsAppendsSkipsPausesAndFinishesAfterCurrent()
    {
        var store = new InMemoryExtensionStore();
        var service = new BatchScraperQueueService();
        service.AttachStore(store);

        // 1. Add initial videos
        var (state1, added1) = await service.AddVideosAsync([101, 102]);
        Assert.Equal(2, added1);
        Assert.Equal(2, state1.Items.Count);
        Assert.All(state1.Items, i => Assert.Equal("pending", i.Status));

        // 2. Append more videos while queue already has items (deduplicating 102)
        var (state2, added2) = await service.AddVideosAsync([102, 103]);
        Assert.Equal(1, added2);
        Assert.Equal(3, state2.Items.Count);

        // 3. Skip video #102
        var state3 = await service.SkipItemAsync(102);
        Assert.Equal("skipped", state3.Items.Single(i => i.VideoId == 102).Status);

        // 4. Re-adding a skipped video resets it back to pending
        var (state4, added4) = await service.AddVideosAsync([102]);
        Assert.Equal(1, added4);
        Assert.Equal("pending", state4.Items.Single(i => i.VideoId == 102).Status);

        // 5. Simulate running state with an in-flight item and toggle FinishAfterCurrent
        await service.SaveQueueAsync(state4 with
        {
            QueueState = "running",
            Items =
            [
                state4.Items[0] with { Status = "scraping", StatusMessage = "Querying scraper..." },
                state4.Items[1] with { Status = "applied" },
                state4.Items[2] with { Status = "pending" },
            ]
        });

        var finishState = await service.SetFinishAfterCurrentAsync(true);
        Assert.True(finishState.FinishAfterCurrent);

        // 6. Pause queue -> returns in-flight item #101 to pending and clears FinishAfterCurrent
        var pausedState = await service.PauseQueueAsync();
        Assert.Equal("paused", pausedState.QueueState);
        Assert.False(pausedState.FinishAfterCurrent);
        Assert.Equal("pending", pausedState.Items.Single(i => i.VideoId == 101).Status);

        // 7. Clear only finished items -> removes #102 (applied), keeps #101 and #103 (pending)
        var clearedFinished = await service.ClearQueueAsync(onlyFinished: true);
        Assert.Equal(2, clearedFinished.Items.Count);
        Assert.DoesNotContain(clearedFinished.Items, i => i.VideoId == 102);

        // 8. Verify store persistence across new service instance
        var reloadedService = new BatchScraperQueueService();
        reloadedService.AttachStore(store);
        var recovered = await reloadedService.GetQueueAsync(recoverInFlight: true);
        Assert.Equal(2, recovered.Items.Count);
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
