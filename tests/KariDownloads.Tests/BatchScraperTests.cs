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
        Assert.Equal("0.1.0", root.GetProperty("version").GetString());
        Assert.Equal("BatchScraper.dll", root.GetProperty("entryDll").GetString());
        Assert.Equal("dist/bundle.js", root.GetProperty("jsBundle").GetString());
        Assert.Equal("dist/bundle.css", root.GetProperty("cssBundle").GetString());
        Assert.Equal("1.7.0", root.GetProperty("minCoveVersion").GetString());

        var categories = root.GetProperty("categories").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("ui", categories);
        Assert.Contains("tools", categories);
        Assert.Contains("content-management", categories);
    }
}
