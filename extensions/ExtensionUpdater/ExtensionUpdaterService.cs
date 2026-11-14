using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cove.Plugins;

namespace ExtensionUpdater;

public sealed record ExtensionSourceEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Kind { get; init; } = "static-zip"; // "manifest-feed" | "static-zip" | "github-release"
    public string Label { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? TagPrefix { get; init; }
    public string? AssetPattern { get; init; }
    public string? ExtensionId { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record DiscoveredExtensionItem
{
    public string SourceId { get; init; } = string.Empty;
    public string SourceLabel { get; init; } = string.Empty;
    public string SourceKind { get; init; } = string.Empty;
    public string? ExtensionId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? RemoteVersion { get; init; }
    public string? MinCoveVersion { get; init; }
    public IReadOnlyList<string>? Categories { get; init; }
    public string? Description { get; init; }
    public string DownloadUrl { get; init; } = string.Empty;
    public string? UpdatedAt { get; init; }
    public string? Error { get; init; }
    public bool LocalCached { get; init; }
    public string? LocalVersion { get; init; }
    public string? LocalFileName { get; init; }
    public string? LocalPackageUrl { get; init; }
}

public sealed record LocalPackageEntry
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? MinCoveVersion { get; init; }
    public IReadOnlyList<string>? Categories { get; init; }
    public string FileName { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public string? SourceId { get; init; }
    public string? DownloadUrl { get; init; }
    public string? UpdatedAt { get; init; }
}

public sealed record LocalCacheStatus(
    string CacheDir,
    string SyncScriptPath,
    bool SyncScriptExists,
    string SourcesFilePath,
    string? LastSyncAt,
    IReadOnlyList<LocalPackageEntry> Packages);

public sealed record LocalSyncResult(
    bool Success,
    string? SyncedAt,
    string CacheDir,
    int TotalPackages,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Errors,
    string? RawError);

public sealed record CheckSourcesResult(
    IReadOnlyList<DiscoveredExtensionItem> Items,
    IReadOnlyList<string> Errors);

public sealed class ExtensionUpdaterService
{
    public const string DefaultFeedUrl =
        "https://github.com/otter-twunk/kari-downloads/releases/download/latest/latest-manifest.json";

    private const string StoreKey = "sources.v1";
    private const long MaxZipInspectBytes = 64 * 1024 * 1024;
    private static readonly Regex SafeExtensionIdRegex = new(
        @"^[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string CacheDir { get; set; } =
        Environment.GetEnvironmentVariable("COVE_EXTENSION_CACHE_DIR")
        ?? @"C:\Users\harls\Server\cove-data\extension-packages";

    public string SyncScriptPath { get; } =
        Environment.GetEnvironmentVariable("COVE_EXTENSION_SYNC_SCRIPT")
        ?? @"C:\Users\harls\Server\cove-stack\scripts\Sync-ExtensionPackages.ps1";

    public string SourcesFilePath { get; } =
        Environment.GetEnvironmentVariable("COVE_EXTENSION_SOURCES_FILE")
        ?? @"C:\Users\harls\Server\cove-data\extension-sources.json";

    private static readonly Regex GitHubRepoSlugRegex = new(
        @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _storeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedZipEntry> _zipCache = new(StringComparer.OrdinalIgnoreCase);
    private IExtensionStore? _store;
    private List<ExtensionSourceEntry>? _memoryFallback;

    private sealed record CachedZipEntry(
        string? ETag,
        DateTimeOffset? LastModified,
        DiscoveredExtensionItem Item);

    public ExtensionUpdaterService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? CreateDefaultHttpClient();
    }

    public void AttachStore(IExtensionStore store) => _store = store;

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CoveExtensionUpdater", "0.1.2"));
        return client;
    }

    public static IReadOnlyList<ExtensionSourceEntry> GetDefaultSources() =>
    [
        new ExtensionSourceEntry
        {
            Id = "kari-downloads-feed",
            Kind = "manifest-feed",
            Label = "kari-downloads (All Repo Extensions)",
            Url = DefaultFeedUrl,
            Enabled = true,
        },
    ];

    public async Task<List<ExtensionSourceEntry>> GetSourcesAsync(CancellationToken ct = default)
    {
        await _storeLock.WaitAsync(ct);
        try
        {
            return await GetSourcesUnlockedAsync(ct);
        }
        finally
        {
            _storeLock.Release();
        }
    }

    private async Task<List<ExtensionSourceEntry>> GetSourcesUnlockedAsync(CancellationToken ct)
    {
        if (_store != null)
        {
            var raw = await _store.GetAsync(StoreKey, ct);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parsed = JsonSerializer.Deserialize<List<ExtensionSourceEntry>>(raw, JsonOpts);
                if (parsed != null)
                    return parsed;
            }

            var defaults = GetDefaultSources().ToList();
            await SaveSourcesUnlockedAsync(defaults, ct);
            return defaults;
        }

        _memoryFallback ??= GetDefaultSources().ToList();
        return [.. _memoryFallback];
    }

    public async Task SaveSourcesAsync(List<ExtensionSourceEntry> sources, CancellationToken ct = default)
    {
        await _storeLock.WaitAsync(ct);
        try
        {
            await SaveSourcesUnlockedAsync(sources, ct);
        }
        finally
        {
            _storeLock.Release();
        }
    }

    private async Task SaveSourcesUnlockedAsync(List<ExtensionSourceEntry> sources, CancellationToken ct)
    {
        if (_store != null)
        {
            var json = JsonSerializer.Serialize(sources, JsonOpts);
            await _store.SetAsync(StoreKey, json, ct);
            return;
        }

        _memoryFallback = [.. sources];
    }

    public async Task<ExtensionSourceEntry> UpsertSourceAsync(ExtensionSourceEntry input, CancellationToken ct = default)
    {
        var kind = NormalizeKind(input.Kind);
        var rawUrl = input.Url?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl))
            throw new ArgumentException("Source URL or GitHub owner/repo is required.");

        if (kind == "github-release")
        {
            var repoSlug = NormalizeGitHubRepo(rawUrl);
            if (!GitHubRepoSlugRegex.IsMatch(repoSlug) || repoSlug.Contains("..", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid GitHub repository '{rawUrl}'. Expected 'owner/repo' or a GitHub repo URL.");
        }
        else if (kind == "gitea-release")
        {
            var isSlug = GitHubRepoSlugRegex.IsMatch(rawUrl) && !rawUrl.Contains("..", StringComparison.Ordinal);
            var isHttp = Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) &&
                         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            if (!isSlug && !isHttp)
                throw new ArgumentException($"Invalid Gitea repository '{rawUrl}'. Expected 'owner/repo' or an http(s):// Gitea repo URL.");
        }
        else
        {
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException($"Invalid URL '{rawUrl}'. Must be an absolute http:// or https:// URL.");
            }
        }

        var normalized = input with
        {
            Id = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString("N")[..12] : input.Id.Trim(),
            Kind = kind,
            Label = string.IsNullOrWhiteSpace(input.Label) ? rawUrl : input.Label.Trim(),
            Url = rawUrl,
            TagPrefix = string.IsNullOrWhiteSpace(input.TagPrefix) ? null : input.TagPrefix.Trim(),
            AssetPattern = string.IsNullOrWhiteSpace(input.AssetPattern) ? null : input.AssetPattern.Trim(),
            ExtensionId = string.IsNullOrWhiteSpace(input.ExtensionId) ? null : input.ExtensionId.Trim(),
        };

        await _storeLock.WaitAsync(ct);
        try
        {
            var sources = await GetSourcesUnlockedAsync(ct);
            var idx = sources.FindIndex(s => string.Equals(s.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                sources[idx] = normalized;
            else
                sources.Add(normalized);

            await SaveSourcesUnlockedAsync(sources, ct);
            return normalized;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<bool> DeleteSourceAsync(string sourceId, CancellationToken ct = default)
    {
        await _storeLock.WaitAsync(ct);
        try
        {
            var sources = await GetSourcesUnlockedAsync(ct);
            var removed = sources.RemoveAll(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            await SaveSourcesUnlockedAsync(sources, ct);
            return true;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<CheckSourcesResult> CheckSourcesAsync(bool inspectStaticZips = true, CancellationToken ct = default)
    {
        var sources = (await GetSourcesAsync(ct)).Where(s => s.Enabled).ToList();
        var localCache = await LoadLocalManifestMapAsync(ct);

        var itemsBySource = new List<DiscoveredExtensionItem>[sources.Count];
        var errorsBySource = new string?[sources.Count];
        using var semaphore = new SemaphoreSlim(4, 4);

        var tasks = sources.Select(async (source, idx) =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                switch (NormalizeKind(source.Kind))
                {
                    case "manifest-feed":
                        itemsBySource[idx] = await CheckManifestFeedAsync(source, ct);
                        break;
                    case "github-release":
                        itemsBySource[idx] = [await CheckGitHubReleaseAsync(source, ct)];
                        break;
                    case "gitea-release":
                        itemsBySource[idx] = CheckGiteaFromLocalCache(source, localCache);
                        break;
                    default:
                        itemsBySource[idx] = [await CheckStaticZipAsync(source, inspectStaticZips, ct)];
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errorsBySource[idx] = $"{source.Label}: {ex.Message}";
                itemsBySource[idx] =
                [
                    new DiscoveredExtensionItem
                    {
                        SourceId = source.Id,
                        SourceLabel = source.Label,
                        SourceKind = source.Kind,
                        ExtensionId = source.ExtensionId,
                        Name = source.Label,
                        DownloadUrl = source.Url,
                        Error = ex.Message,
                    },
                ];
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        var allItems = new List<DiscoveredExtensionItem>();
        var allErrors = new List<string>();
        var seenExtIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < sources.Count; i++)
        {
            if (itemsBySource[i] != null)
            {
                foreach (var item in itemsBySource[i])
                {
                    var enriched = EnrichWithLocalCache(item, localCache);
                    allItems.Add(enriched);
                    if (!string.IsNullOrWhiteSpace(enriched.ExtensionId))
                        seenExtIds.Add(enriched.ExtensionId);
                }
            }
            if (errorsBySource[i] is { } err)
                allErrors.Add(err);
        }

        // Include any locally cached packages from external sources (e.g. extension-sources.json)
        foreach (var pkg in localCache.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (seenExtIds.Contains(pkg.Id)) continue;
            allItems.Add(new DiscoveredExtensionItem
            {
                SourceId = string.IsNullOrWhiteSpace(pkg.SourceId) ? "local-cache" : pkg.SourceId,
                SourceLabel = "Local ZIP Cache",
                SourceKind = "local-cache",
                ExtensionId = pkg.Id,
                Name = pkg.Name,
                RemoteVersion = pkg.Version,
                MinCoveVersion = pkg.MinCoveVersion,
                Categories = pkg.Categories,
                Description = pkg.Description,
                DownloadUrl = pkg.DownloadUrl ?? string.Empty,
                UpdatedAt = pkg.UpdatedAt,
                LocalCached = true,
                LocalVersion = pkg.Version,
                LocalFileName = pkg.FileName,
                LocalPackageUrl = BuildLocalPackageUrl(pkg.Id),
            });
        }

        return new CheckSourcesResult(allItems, allErrors);
    }

    private async Task<List<DiscoveredExtensionItem>> CheckManifestFeedAsync(ExtensionSourceEntry source, CancellationToken ct)
    {
        using var res = await _http.GetAsync(source.Url, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var results = new List<DiscoveredExtensionItem>();

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("extensions", out var extsProp))
        {
            if (extsProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in extsProp.EnumerateObject())
                    results.Add(ParseFeedEntry(source, prop.Value, prop.Name));
            }
            else if (extsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in extsProp.EnumerateArray())
                    results.Add(ParseFeedEntry(source, elem, null));
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in root.EnumerateArray())
                results.Add(ParseFeedEntry(source, elem, null));
        }
        else
        {
            throw new InvalidOperationException("Feed JSON must contain an 'extensions' object/array or be a JSON array.");
        }

        return results;
    }

    private static DiscoveredExtensionItem ParseFeedEntry(ExtensionSourceEntry source, JsonElement elem, string? fallbackId)
    {
        var id = GetString(elem, "id") ?? fallbackId;
        var name = GetString(elem, "name") ?? id ?? source.Label;
        var version = GetString(elem, "version");
        var minCoveVersion = GetString(elem, "minCoveVersion");
        var description = GetString(elem, "description");
        var downloadUrl = GetString(elem, "downloadUrl") ?? GetString(elem, "url") ?? string.Empty;
        var updatedAt = GetString(elem, "updatedAt");
        var categories = GetStringList(elem, "categories");

        return new DiscoveredExtensionItem
        {
            SourceId = source.Id,
            SourceLabel = source.Label,
            SourceKind = source.Kind,
            ExtensionId = id,
            Name = name,
            RemoteVersion = version,
            MinCoveVersion = minCoveVersion,
            Categories = categories,
            Description = description,
            DownloadUrl = downloadUrl,
            UpdatedAt = updatedAt,
        };
    }

    private async Task<DiscoveredExtensionItem> CheckGitHubReleaseAsync(ExtensionSourceEntry source, CancellationToken ct)
    {
        var repo = NormalizeGitHubRepo(source.Url);
        if (!GitHubRepoSlugRegex.IsMatch(repo) || repo.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException($"Invalid GitHub repository '{repo}'.");

        var apiUrl = $"https://api.github.com/repos/{repo}/releases?per_page=25";
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Unexpected GitHub releases response.");

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean())
                continue;

            var tagName = GetString(release, "tag_name") ?? string.Empty;
            if (string.Equals(tagName, "latest", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(source.TagPrefix) &&
                !tagName.StartsWith(source.TagPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;

            string? chosenUrl = null;
            string? chosenName = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var assetName = GetString(asset, "name") ?? string.Empty;
                var assetUrl = GetString(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(assetUrl) || !assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(source.AssetPattern) &&
                    !WildcardMatch(assetName, source.AssetPattern))
                {
                    continue;
                }

                chosenName = assetName;
                chosenUrl = assetUrl;
                break;
            }

            if (chosenUrl == null)
                continue;

            var version = ExtractVersionFromTag(tagName, source.TagPrefix);
            var publishedAt = GetString(release, "published_at");
            var releaseName = GetString(release, "name");

            return new DiscoveredExtensionItem
            {
                SourceId = source.Id,
                SourceLabel = source.Label,
                SourceKind = source.Kind,
                ExtensionId = source.ExtensionId ?? InferExtensionIdFromAsset(chosenName, version),
                Name = string.IsNullOrWhiteSpace(source.Label) ? (releaseName ?? repo) : source.Label,
                RemoteVersion = version,
                DownloadUrl = chosenUrl,
                UpdatedAt = publishedAt,
            };
        }

        throw new InvalidOperationException($"No matching release with a .zip asset found for '{repo}'.");
    }

    private async Task<DiscoveredExtensionItem> CheckStaticZipAsync(
        ExtensionSourceEntry source,
        bool inspectZip,
        CancellationToken ct)
    {
        if (!inspectZip)
        {
            return new DiscoveredExtensionItem
            {
                SourceId = source.Id,
                SourceLabel = source.Label,
                SourceKind = source.Kind,
                ExtensionId = source.ExtensionId,
                Name = source.Label,
                DownloadUrl = source.Url,
            };
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, source.Url);
        if (_zipCache.TryGetValue(source.Url, out var cached))
        {
            if (!string.IsNullOrWhiteSpace(cached.ETag))
                req.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
            if (cached.LastModified.HasValue)
                req.Headers.IfModifiedSince = cached.LastModified.Value;
        }

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotModified && cached != null)
        {
            return cached.Item with
            {
                SourceId = source.Id,
                SourceLabel = source.Label,
                SourceKind = source.Kind,
            };
        }

        res.EnsureSuccessStatusCode();

        if (res.Content.Headers.ContentLength is long len && len > MaxZipInspectBytes)
            throw new InvalidOperationException($"ZIP exceeds {MaxZipInspectBytes / (1024 * 1024)} MB inspection limit.");

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            totalRead += read;
            if (totalRead > MaxZipInspectBytes)
                throw new InvalidOperationException($"ZIP exceeds {MaxZipInspectBytes / (1024 * 1024)} MB inspection limit.");
            ms.Write(buffer, 0, read);
        }
        ms.Position = 0;

        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        var manifestEntry = archive.Entries.FirstOrDefault(e =>
            e.FullName.Equals("extension.json", StringComparison.OrdinalIgnoreCase) ||
            (e.FullName.EndsWith("/extension.json", StringComparison.OrdinalIgnoreCase) &&
             e.FullName.Count(c => c == '/') == 1));

        if (manifestEntry == null)
            throw new InvalidOperationException("Remote ZIP does not contain an extension.json manifest.");

        await using var entryStream = manifestEntry.Open();
        using var doc = await JsonDocument.ParseAsync(entryStream, cancellationToken: ct);
        var root = doc.RootElement;

        var discovered = new DiscoveredExtensionItem
        {
            SourceId = source.Id,
            SourceLabel = source.Label,
            SourceKind = source.Kind,
            ExtensionId = source.ExtensionId ?? GetString(root, "id"),
            Name = GetString(root, "name") ?? source.Label,
            RemoteVersion = GetString(root, "version"),
            MinCoveVersion = GetString(root, "minCoveVersion"),
            Categories = GetStringList(root, "categories"),
            Description = GetString(root, "description"),
            DownloadUrl = source.Url,
        };

        var etag = res.Headers.ETag?.Tag;
        var lastModified = res.Content.Headers.LastModified;
        if (!string.IsNullOrWhiteSpace(etag) || lastModified.HasValue)
        {
            _zipCache[source.Url] = new CachedZipEntry(etag, lastModified, discovered);
        }

        return discovered;
    }

    public static string NormalizeKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "manifest-feed" => "manifest-feed",
        "github-release" => "github-release",
        "gitea-release" => "gitea-release",
        _ => "static-zip",
    };

    private static string BuildLocalPackageUrl(string extensionId) =>
        $"/api/ext/extension-updater/local-cache/package/{Uri.EscapeDataString(extensionId)}";

    private static List<DiscoveredExtensionItem> CheckGiteaFromLocalCache(
        ExtensionSourceEntry source,
        Dictionary<string, LocalPackageEntry> localCache)
    {
        var matches = localCache.Values
            .Where(p => string.Equals(p.SourceId, source.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            return
            [
                new DiscoveredExtensionItem
                {
                    SourceId = source.Id,
                    SourceLabel = source.Label,
                    SourceKind = source.Kind,
                    ExtensionId = source.ExtensionId,
                    Name = source.Label,
                    DownloadUrl = source.Url,
                    Error = "Not synced locally yet. Click 'Sync Local Cache' to fetch from Gitea via gt.",
                },
            ];
        }

        return matches.Select(pkg => new DiscoveredExtensionItem
        {
            SourceId = source.Id,
            SourceLabel = source.Label,
            SourceKind = source.Kind,
            ExtensionId = pkg.Id,
            Name = pkg.Name,
            RemoteVersion = pkg.Version,
            MinCoveVersion = pkg.MinCoveVersion,
            Categories = pkg.Categories,
            Description = pkg.Description,
            DownloadUrl = pkg.DownloadUrl ?? source.Url,
            UpdatedAt = pkg.UpdatedAt,
            LocalCached = true,
            LocalVersion = pkg.Version,
            LocalFileName = pkg.FileName,
            LocalPackageUrl = BuildLocalPackageUrl(pkg.Id),
        }).ToList();
    }

    private static DiscoveredExtensionItem EnrichWithLocalCache(
        DiscoveredExtensionItem item,
        Dictionary<string, LocalPackageEntry> localCache)
    {
        if (string.IsNullOrWhiteSpace(item.ExtensionId))
            return item;

        if (!localCache.TryGetValue(item.ExtensionId.Trim(), out var cached))
            return item;

        return item with
        {
            LocalCached = true,
            LocalVersion = cached.Version,
            LocalFileName = cached.FileName,
            LocalPackageUrl = BuildLocalPackageUrl(cached.Id),
            RemoteVersion = string.IsNullOrWhiteSpace(item.RemoteVersion) ? cached.Version : item.RemoteVersion,
            Error = item.Error != null && !string.IsNullOrWhiteSpace(cached.Version) ? null : item.Error,
        };
    }

    public async Task<LocalCacheStatus> GetLocalCacheStatusAsync(CancellationToken ct = default)
    {
        var map = await LoadLocalManifestMapAsync(ct);
        string? lastSyncAt = null;
        var statePath = Path.Combine(CacheDir, "sync-state.json");
        if (File.Exists(statePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(statePath, ct));
                lastSyncAt = GetString(doc.RootElement, "lastSyncAt");
            }
            catch
            {
                // Ignore malformed state file
            }
        }

        return new LocalCacheStatus(
            CacheDir,
            SyncScriptPath,
            File.Exists(SyncScriptPath),
            SourcesFilePath,
            lastSyncAt,
            map.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public bool TryGetLocalPackagePath(string extensionId, out string filePath, out string fileName)
    {
        filePath = string.Empty;
        fileName = string.Empty;

        if (string.IsNullOrWhiteSpace(extensionId) || !SafeExtensionIdRegex.IsMatch(extensionId.Trim()))
            return false;

        var normalizedId = extensionId.Trim().ToLowerInvariant();
        var manifestPath = Path.Combine(CacheDir, "latest-manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (doc.RootElement.TryGetProperty("extensions", out var exts) &&
                    exts.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in exts.EnumerateObject())
                    {
                        if (!string.Equals(prop.Name, normalizedId, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var candidateName = GetString(prop.Value, "fileName");
                        if (!string.IsNullOrWhiteSpace(candidateName))
                        {
                            var safeName = Path.GetFileName(candidateName);
                            var candidatePath = Path.Combine(CacheDir, safeName);
                            if (File.Exists(candidatePath))
                            {
                                filePath = candidatePath;
                                fileName = safeName;
                                return true;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fall back to conventional filename
            }
        }

        var fallbackName = $"{normalizedId}-latest.zip";
        var fallbackPath = Path.Combine(CacheDir, fallbackName);
        if (File.Exists(fallbackPath))
        {
            filePath = fallbackPath;
            fileName = fallbackName;
            return true;
        }

        return false;
    }

    public async Task<LocalSyncResult> TriggerLocalSyncAsync(bool force = false, CancellationToken ct = default)
    {
        if (!File.Exists(SyncScriptPath))
        {
            return new LocalSyncResult(
                Success: false,
                SyncedAt: null,
                CacheDir: CacheDir,
                TotalPackages: 0,
                Updated: [],
                Skipped: [],
                Errors: [$"Sync script not found at '{SyncScriptPath}'."],
                RawError: $"Sync script not found at '{SyncScriptPath}'.");
        }

        await SyncSourcesFileFromStoreAsync(ct);

        var args = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", SyncScriptPath,
            "-CacheDir", CacheDir,
            "-SourcesFile", SourcesFilePath,
            "-Json",
        };
        if (force)
            args.Add("-Force");

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start PowerShell sync process.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
        {
            var err = string.IsNullOrWhiteSpace(stderr) ? $"Sync script exited with code {proc.ExitCode}." : stderr;
            return new LocalSyncResult(false, null, CacheDir, 0, [], [], [err], err);
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var syncedAt = GetString(root, "syncedAt");
            var totalPackages = root.TryGetProperty("totalPackages", out var tp) && tp.TryGetInt32(out var n) ? n : 0;
            var updated = GetStringList(root, "updated") ?? [];
            var skipped = GetStringList(root, "skipped") ?? [];
            var errors = GetStringList(root, "errors") ?? [];
            return new LocalSyncResult(
                Success: proc.ExitCode == 0 && errors.Count == 0,
                SyncedAt: syncedAt,
                CacheDir: CacheDir,
                TotalPackages: totalPackages,
                Updated: updated,
                Skipped: skipped,
                Errors: errors,
                RawError: string.IsNullOrWhiteSpace(stderr) ? null : stderr);
        }
        catch (Exception ex)
        {
            return new LocalSyncResult(
                Success: false,
                SyncedAt: null,
                CacheDir: CacheDir,
                TotalPackages: 0,
                Updated: [],
                Skipped: [],
                Errors: [$"Failed to parse sync output: {ex.Message}"],
                RawError: string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        }
    }

    private async Task SyncSourcesFileFromStoreAsync(CancellationToken ct)
    {
        var storeSources = await GetSourcesAsync(ct);
        var merged = new List<ExtensionSourceEntry>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in storeSources)
        {
            merged.Add(s);
            seenIds.Add(s.Id);
        }

        if (File.Exists(SourcesFilePath))
        {
            try
            {
                var existingJson = await File.ReadAllTextAsync(SourcesFilePath, ct);
                var existing = JsonSerializer.Deserialize<List<ExtensionSourceEntry>>(existingJson, JsonOpts);
                if (existing != null)
                {
                    foreach (var s in existing)
                    {
                        if (seenIds.Add(s.Id))
                            merged.Add(s);
                    }
                }
            }
            catch
            {
                // Overwrite malformed sources file with store sources
            }
        }

        var parent = Path.GetDirectoryName(SourcesFilePath);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        await File.WriteAllTextAsync(SourcesFilePath, JsonSerializer.Serialize(merged, JsonOpts), ct);
    }

    private async Task<Dictionary<string, LocalPackageEntry>> LoadLocalManifestMapAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, LocalPackageEntry>(StringComparer.OrdinalIgnoreCase);
        var manifestPath = Path.Combine(CacheDir, "latest-manifest.json");
        if (!File.Exists(manifestPath))
            return result;

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("extensions", out var exts) || exts.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var prop in exts.EnumerateObject())
            {
                var elem = prop.Value;
                var id = GetString(elem, "id") ?? prop.Name;
                var version = GetString(elem, "version");
                var fileName = GetString(elem, "fileName") ?? $"{id.ToLowerInvariant()}-latest.zip";
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                    continue;

                var safeFile = Path.GetFileName(fileName);
                var fullPath = Path.Combine(CacheDir, safeFile);
                if (!File.Exists(fullPath))
                    continue;

                long sizeBytes = elem.TryGetProperty("sizeBytes", out var sb) && sb.TryGetInt64(out var s)
                    ? s
                    : new FileInfo(fullPath).Length;

                result[id] = new LocalPackageEntry
                {
                    Id = id,
                    Name = GetString(elem, "name") ?? id,
                    Version = version,
                    Description = GetString(elem, "description"),
                    MinCoveVersion = GetString(elem, "minCoveVersion"),
                    Categories = GetStringList(elem, "categories"),
                    FileName = safeFile,
                    SizeBytes = sizeBytes,
                    Sha256 = GetString(elem, "sha256"),
                    SourceId = GetString(elem, "sourceId"),
                    DownloadUrl = GetString(elem, "downloadUrl"),
                    UpdatedAt = GetString(elem, "updatedAt"),
                };
            }
        }
        catch
        {
            // Ignore malformed manifest
        }

        return result;
    }

    public static string NormalizeGitHubRepo(string input)
    {
        var trimmed = input.Trim().TrimEnd('/');
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
                return $"{segments[0]}/{segments[1].Replace(".git", "", StringComparison.OrdinalIgnoreCase)}";
        }

        return trimmed.Trim('/');
    }

    public static string? ExtractVersionFromTag(string tagName, string? tagPrefix)
    {
        var raw = tagName.Trim();
        if (!string.IsNullOrWhiteSpace(tagPrefix) &&
            raw.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[tagPrefix.Length..];
        }
        else if (raw.Contains('/'))
        {
            raw = raw[(raw.LastIndexOf('/') + 1)..];
        }

        if (raw.StartsWith('v') || raw.StartsWith('V'))
            raw = raw[1..];

        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static string? InferExtensionIdFromAsset(string? assetName, string? version)
    {
        if (string.IsNullOrWhiteSpace(assetName)) return null;
        var stem = Path.GetFileNameWithoutExtension(assetName);
        if (!string.IsNullOrWhiteSpace(version) && stem.EndsWith("-" + version, StringComparison.OrdinalIgnoreCase))
            return stem[..^(version.Length + 1)];
        if (stem.EndsWith("-latest", StringComparison.OrdinalIgnoreCase))
            return stem[..^"-latest".Length];
        return null;
    }

    private static bool WildcardMatch(string input, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase);
    }

    private static string? GetString(JsonElement elem, string propName)
    {
        if (elem.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in elem.EnumerateObject())
        {
            if (string.Equals(prop.Name, propName, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }
        return null;
    }

    private static IReadOnlyList<string>? GetStringList(JsonElement elem, string propName)
    {
        if (elem.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in elem.EnumerateObject())
        {
            if (string.Equals(prop.Name, propName, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                        list.Add(s);
                }
                return list;
            }
        }
        return null;
    }
}
