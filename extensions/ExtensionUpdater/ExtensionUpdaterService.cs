using System.Collections.Concurrent;
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
    public string Kind { get; init; } = "static-zip"; // "manifest-feed" | "static-zip" | "gitea-release" | "github-release"
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
    public string? Tag { get; init; }
    public string? Sha256 { get; init; }
    public string? UpdatedAt { get; init; }
    public string? Error { get; init; }
}

public sealed record CheckSourcesResult(
    IReadOnlyList<DiscoveredExtensionItem> Items,
    IReadOnlyList<string> Errors);

public sealed class ExtensionUpdaterService
{
    public const string DefaultGiteaBaseUrl = "https://gitea.tail971353.ts.net";
    public const string DefaultFeedUrl =
        $"{DefaultGiteaBaseUrl}/gitea-admin/kari-downloads/releases/download/latest/latest-manifest.json";

    private const string StoreKey = "sources.v1";
    private const string GiteaTokenKey = "gitea-token.v1";
    private const long MaxZipInspectBytes = 64 * 1024 * 1024;

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
    private string? _memoryGiteaToken;

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
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CoveExtensionUpdater", "0.2.0"));
        return client;
    }

    public static IReadOnlyList<ExtensionSourceEntry> GetDefaultSources() =>
    [
        new ExtensionSourceEntry
        {
            Id = "kari-downloads-feed",
            Kind = "manifest-feed",
            Label = "kari-downloads (Gitea)",
            Url = DefaultFeedUrl,
            Enabled = true,
        },
        new ExtensionSourceEntry
        {
            Id = "cove-librarian-feed",
            Kind = "manifest-feed",
            Label = "Cove Librarian (Gitea)",
            Url = $"{DefaultGiteaBaseUrl}/gitea-admin/cove-librarian/releases/download/latest/latest-manifest.json",
            Enabled = true,
        },
        new ExtensionSourceEntry
        {
            Id = "cove-apple-playback-feed",
            Kind = "manifest-feed",
            Label = "Cove Apple Playback (Gitea)",
            Url = $"{DefaultGiteaBaseUrl}/gitea-admin/cove-apple-playback/releases/download/latest/latest-manifest.json",
            Enabled = true,
        },
        new ExtensionSourceEntry
        {
            Id = "music-montage-app-feed",
            Kind = "manifest-feed",
            Label = "Music Montage (Gitea)",
            Url = $"{DefaultGiteaBaseUrl}/gitea-admin/music-montage-app/releases/download/latest/latest-manifest.json",
            Enabled = true,
        },
        new ExtensionSourceEntry
        {
            Id = "cove-ops-monitor-feed",
            Kind = "manifest-feed",
            Label = "Ops & Backups Monitor (Gitea)",
            Url = $"{DefaultGiteaBaseUrl}/gitea-admin/cove-ops-monitor/releases/download/latest/latest-manifest.json",
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
            if (!TryParseGiteaRepoUrl(rawUrl, out _, out _))
                throw new ArgumentException($"Invalid Gitea repository URL '{rawUrl}'. Expected 'https://<host>/<owner>/<repo>'.");
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
        if (sources.Count == 0)
            return new CheckSourcesResult([], []);

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
                    case "gitea-release":
                        itemsBySource[idx] = [await CheckGiteaReleaseAsync(source, ct)];
                        break;
                    case "github-release":
                        itemsBySource[idx] = [await CheckGitHubReleaseAsync(source, ct)];
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
        for (var i = 0; i < sources.Count; i++)
        {
            if (itemsBySource[i] != null)
                allItems.AddRange(itemsBySource[i]);
            if (errorsBySource[i] is { } err)
                allErrors.Add(err);
        }

        return new CheckSourcesResult(allItems, allErrors);
    }

    public async Task<bool> HasGiteaTokenAsync(CancellationToken ct = default)
        => !string.IsNullOrWhiteSpace(await GetGiteaTokenAsync(ct));

    public async Task<string?> GetGiteaTokenAsync(CancellationToken ct = default)
    {
        if (_store != null)
        {
            var raw = await _store.GetAsync(GiteaTokenKey, ct);
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
        return string.IsNullOrWhiteSpace(_memoryGiteaToken) ? null : _memoryGiteaToken.Trim();
    }

    public async Task SetGiteaTokenAsync(string? token, CancellationToken ct = default)
    {
        var cleaned = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        if (_store != null)
        {
            if (cleaned == null)
                await _store.DeleteAsync(GiteaTokenKey, ct);
            else
                await _store.SetAsync(GiteaTokenKey, cleaned, ct);
            return;
        }
        _memoryGiteaToken = cleaned;
    }

    private async Task AttachGiteaAuthIfApplicableAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (req.RequestUri == null || !req.RequestUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return;
        var giteaHost = new Uri(DefaultGiteaBaseUrl).Host;
        if (!req.RequestUri.Host.Equals(giteaHost, StringComparison.OrdinalIgnoreCase))
            return;
        var token = await GetGiteaTokenAsync(ct);
        if (!string.IsNullOrWhiteSpace(token))
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"token {token}");
        }
    }

    public async Task<(byte[] Bytes, string FileName)> DownloadPackageBytesAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Download URL must be an absolute http:// or https:// URL.");
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        await AttachGiteaAuthIfApplicableAsync(req, ct);

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();

        var mediaType = res.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Remote host returned an HTML sign-in page instead of a ZIP archive. Configure a Gitea Read Token in Extension Updater.");
        }

        if (res.Content.Headers.ContentLength is long len && len > MaxZipInspectBytes)
            throw new InvalidOperationException($"ZIP exceeds {MaxZipInspectBytes / (1024 * 1024)} MB download limit.");

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            totalRead += read;
            if (totalRead > MaxZipInspectBytes)
                throw new InvalidOperationException($"ZIP exceeds {MaxZipInspectBytes / (1024 * 1024)} MB download limit.");
            ms.Write(buffer, 0, read);
        }

        var fileName = Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            fileName = "extension.zip";

        return (ms.ToArray(), fileName);
    }

    private async Task<List<DiscoveredExtensionItem>> CheckManifestFeedAsync(ExtensionSourceEntry source, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, source.Url);
        await AttachGiteaAuthIfApplicableAsync(req, ct);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var mediaType = res.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Feed returned an HTML sign-in page instead of JSON. Configure a Gitea Read Token in Extension Updater.");
        }
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
        var tag = GetString(elem, "tag");
        var sha256 = GetString(elem, "sha256");
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
            Tag = tag,
            Sha256 = sha256,
            UpdatedAt = updatedAt,
        };
    }

    private async Task<DiscoveredExtensionItem> CheckGiteaReleaseAsync(ExtensionSourceEntry source, CancellationToken ct)
    {
        if (!TryParseGiteaRepoUrl(source.Url, out var baseUrl, out var repo))
            throw new InvalidOperationException($"Invalid Gitea repository URL '{source.Url}'.");

        var apiUrl = $"{baseUrl}/api/v1/repos/{repo}/releases?limit=25";
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        await AttachGiteaAuthIfApplicableAsync(req, ct);

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Unexpected Gitea releases response.");

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
            var publishedAt = GetString(release, "published_at") ?? GetString(release, "created_at");
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
                Tag = tagName,
                UpdatedAt = publishedAt,
            };
        }

        throw new InvalidOperationException($"No matching release with a .zip asset found for '{repo}'.");
    }

    public static bool TryParseGiteaRepoUrl(string input, out string baseUrl, out string repoSlug)
    {
        baseUrl = string.Empty;
        repoSlug = string.Empty;
        var trimmed = input.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        var owner = segments[0];
        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];
        var slug = $"{owner}/{repo}";
        if (!GitHubRepoSlugRegex.IsMatch(slug) || slug.Contains("..", StringComparison.Ordinal))
            return false;

        baseUrl = $"{uri.Scheme}://{uri.Authority}";
        repoSlug = slug;
        return true;
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
        await AttachGiteaAuthIfApplicableAsync(req, ct);
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
        "gitea-release" => "gitea-release",
        "github-release" => "github-release",
        _ => "static-zip",
    };

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
