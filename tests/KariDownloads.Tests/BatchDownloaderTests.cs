using System.Text.Json;
using BatchDownloader;
using Cove.Plugins;
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
        Assert.Equal("0.1.0", root.GetProperty("version").GetString());
        Assert.Equal("BatchDownloader.dll", root.GetProperty("entryDll").GetString());
        Assert.Equal("dist/bundle.js", root.GetProperty("jsBundle").GetString());
        Assert.Equal("dist/bundle.css", root.GetProperty("cssBundle").GetString());
        Assert.Equal("1.7.0", root.GetProperty("minCoveVersion").GetString());

        var categories = root.GetProperty("categories").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("ui", categories);
        Assert.Contains("tools", categories);
        Assert.Contains("content-management", categories);
    }
}
