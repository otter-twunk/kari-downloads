using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cove.Plugins;
using ExtensionUpdater;
using KariDownloads.Tests.Infrastructure;

namespace KariDownloads.Tests;

public sealed class ExtensionUpdaterTests
{
    [Fact]
    public async Task GetUIManifest_RegistersPageDefinition()
    {
        using var harness = new FakeDownloaderHost();
        var extension = new ExtensionUpdaterExtension();
        var manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extensions", "ExtensionUpdater", "extension.json"));
        var manifest = JsonSerializer.Deserialize<ExtensionManifestFile>(
            await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        ((IManifestAware)extension).ApplyManifest(manifest);
        await extension.InitializeAsync(harness.Services);

        var ui = extension.GetUIManifest();
        Assert.NotNull(ui);
        Assert.Contains(ui.Pages, p => p.ComponentName == "ExtensionUpdaterPage");
        Assert.Equal("0.2.0", extension.Version);
    }

    [Fact]
    public async Task Sources_SeedsDefaultFeed_AndValidatesUpsertAndDelete()
    {
        var store = new InMemoryExtensionStore();
        var service = new ExtensionUpdaterService();
        service.AttachStore(store);

        var initial = await service.GetSourcesAsync();
        Assert.Equal(5, initial.Count);
        Assert.All(initial, s => Assert.Equal("manifest-feed", s.Kind));
        Assert.Contains(initial, s => s.Url.Contains("gitea.tail971353.ts.net/gitea-admin/kari-downloads"));

        var added = await service.UpsertSourceAsync(new ExtensionSourceEntry
        {
            Id = "ext-custom-zip",
            Kind = "static-zip",
            Label = "External Plugin",
            Url = "https://example.com/external-plugin-latest.zip",
        });

        Assert.Equal("ext-custom-zip", added.Id);
        var afterAdd = await service.GetSourcesAsync();
        Assert.Equal(6, afterAdd.Count);

        // Reject invalid URLs or bad GitHub/Gitea repo slugs
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpsertSourceAsync(new ExtensionSourceEntry
        {
            Kind = "static-zip",
            Url = "ftp://example.com/bad.zip",
        }));

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpsertSourceAsync(new ExtensionSourceEntry
        {
            Kind = "github-release",
            Url = "invalid/../slug",
        }));

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpsertSourceAsync(new ExtensionSourceEntry
        {
            Kind = "gitea-release",
            Url = "not-a-url",
        }));

        var deleted = await service.DeleteSourceAsync("ext-custom-zip");
        Assert.True(deleted);
        var afterDelete = await service.GetSourcesAsync();
        Assert.Equal(5, afterDelete.Count);
    }

    [Fact]
    public async Task CheckSourcesAsync_ResolvesManifestFeed_GitHubRelease_AndStaticZipWithETagCache()
    {
        var zipBytes = BuildTestExtensionZip("com.external.sample", "Sample External", "2.4.1");
        var zipDownloads = 0;

        var handler = new StubHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url == "https://feeds.example.com/latest-manifest.json")
            {
                return JsonResponse("""
                {
                  "schemaVersion": 1,
                  "extensions": {
                    "karinauts.downloaders.thisvid": {
                      "id": "karinauts.downloaders.thisvid",
                      "name": "ThisVid Downloader",
                      "version": "0.1.5",
                      "minCoveVersion": "1.8.0",
                      "categories": ["downloader"],
                      "description": "Downloads videos from ThisVid",
                      "downloadUrl": "https://github.com/otter-twunk/kari-downloads/releases/download/latest/karinauts.downloaders.thisvid-latest.zip"
                    }
                  }
                }
                """);
            }

            if (url == "https://api.github.com/repos/someone/cool-plugin/releases?per_page=25")
            {
                return JsonResponse("""
                [
                  {
                    "tag_name": "latest",
                    "draft": false,
                    "assets": [
                      { "name": "cool-latest.zip", "browser_download_url": "https://github.com/someone/cool-plugin/releases/download/latest/cool-latest.zip" }
                    ]
                  },
                  {
                    "tag_name": "cool/v1.3.0",
                    "draft": false,
                    "published_at": "2026-11-10T00:00:00Z",
                    "assets": [
                      { "name": "com.someone.cool-1.3.0.zip", "browser_download_url": "https://github.com/someone/cool-plugin/releases/download/cool/v1.3.0/com.someone.cool-1.3.0.zip" }
                    ]
                  }
                ]
                """);
            }

            if (url == "https://cdn.example.com/sample-latest.zip")
            {
                if (req.Headers.IfNoneMatch.Any(t => t.Tag == "\"v241\""))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }

                Interlocked.Increment(ref zipDownloads);
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(zipBytes),
                };
                resp.Headers.ETag = new EntityTagHeaderValue("\"v241\"");
                return resp;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = new ExtensionUpdaterService(new HttpClient(handler));
        await service.SaveSourcesAsync(
        [
            new ExtensionSourceEntry
            {
                Id = "feed-1",
                Kind = "manifest-feed",
                Label = "Repo Feed",
                Url = "https://feeds.example.com/latest-manifest.json",
            },
            new ExtensionSourceEntry
            {
                Id = "gh-1",
                Kind = "github-release",
                Label = "External GH Plugin",
                Url = "https://github.com/someone/cool-plugin",
                TagPrefix = "cool/",
                AssetPattern = "com.someone.*.zip",
            },
            new ExtensionSourceEntry
            {
                Id = "zip-1",
                Kind = "static-zip",
                Label = "External Static ZIP",
                Url = "https://cdn.example.com/sample-latest.zip",
            },
        ]);

        var result = await service.CheckSourcesAsync(inspectStaticZips: true);

        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(1, zipDownloads);

        var feedItem = Assert.Single(result.Items, i => i.SourceId == "feed-1");
        Assert.Equal("karinauts.downloaders.thisvid", feedItem.ExtensionId);
        Assert.Equal("0.1.5", feedItem.RemoteVersion);
        Assert.Equal("1.8.0", feedItem.MinCoveVersion);

        var ghItem = Assert.Single(result.Items, i => i.SourceId == "gh-1");
        Assert.Equal("com.someone.cool", ghItem.ExtensionId);
        Assert.Equal("1.3.0", ghItem.RemoteVersion);

        var zipItem = Assert.Single(result.Items, i => i.SourceId == "zip-1");
        Assert.Equal("com.external.sample", zipItem.ExtensionId);
        Assert.Equal("Sample External", zipItem.Name);
        Assert.Equal("2.4.1", zipItem.RemoteVersion);

        // Second check reuses ETag cache (304 NotModified) without re-downloading ZIP bytes
        var cachedResult = await service.CheckSourcesAsync(inspectStaticZips: true);
        Assert.Empty(cachedResult.Errors);
        Assert.Equal(1, zipDownloads);
        var cachedZipItem = Assert.Single(cachedResult.Items, i => i.SourceId == "zip-1");
        Assert.Equal("2.4.1", cachedZipItem.RemoteVersion);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static byte[] BuildTestExtensionZip(string id, string name, string version)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("extension.json");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write($$"""{"id":"{{id}}","name":"{{name}}","version":"{{version}}"}""");
        }
        return ms.ToArray();
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class InMemoryExtensionStore : IExtensionStore
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_data.TryGetValue(key, out var val) ? val : null);

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

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken ct = default)
        {
            IReadOnlyList<string> keys = string.IsNullOrEmpty(prefix)
                ? _data.Keys.ToList()
                : _data.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult(keys);
        }

        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, string>(_data, StringComparer.OrdinalIgnoreCase));
    }
}
