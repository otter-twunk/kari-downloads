using System.IO.Compression;
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
    public string? Description { get; init; }
    public string DownloadUrl { get; init; } = string.Empty;
    public string? UpdatedAt { get; init; }
    public string? Error { get; init; }
}

public sealed record CheckSourcesResult(
    IReadOnlyList<DiscoveredExtensionItem> Items,
    IReadOnlyList<string> Errors);

public sealed class ExtensionUpdaterService
{
    public const string DefaultFeedUrl =
        "https://github.com/otter-twunk/kari-downloads/releases/download/latest/latest-manifest.json";

    private const string StoreKey = "sources.v1";
    private const long MaxZipInspectBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private IExtensionStore? _store;
    private List<ExtensionSourceEntry>? _memoryFallback;

    public ExtensionUpdaterService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? CreateDefaultHttpClient();
    }

    public void AttachStore(IExtensionStore store) => _store = store;

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CoveExtensionUpdater", "0.1.0"));
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
            await SaveSourcesAsync(defaults, ct);
            return defaults;
        }

        _memoryFallback ??= GetDefaultSources().ToList();
        return [.. _memoryFallback];
    }

    public async Task SaveSourcesAsync(List<ExtensionSourceEntry> sources, CancellationToken ct = default)
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
        var sources = await GetSourcesAsync(ct);
        var normalized = input with
        {
            Id = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString("N")[..12] : input.Id.Trim(),
            Kind = NormalizeKind(input.Kind),
            Label = string.IsNullOrWhiteSpace(input.Label) ? input.Url.Trim() : input.Label.Trim(),
            Url = input.Url.Trim(),
            TagPrefix = string.IsNullOrWhiteSpace(input.TagPrefix) ? null : input.TagPrefix.Trim(),
            AssetPattern = string.IsNullOrWhiteSpace(input.AssetPattern) ? null : input.AssetPattern.Trim(),
            ExtensionId = string.IsNullOrWhiteSpace(input.ExtensionId) ? null : input.ExtensionId.Trim(),
        };

        var idx = sources.FindIndex(s => string.Equals(s.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            sources[idx] = normalized;
        else
            sources.Add(normalized);

        await SaveSourcesAsync(sources, ct);
        return normalized;
    }

    public async Task<bool> DeleteSourceAsync(string sourceId, CancellationToken ct = default)
    {
        var sources = await GetSourcesAsync(ct);
        var removed = sources.RemoveAll(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;
        await SaveSourcesAsync(sources, ct);
        return true;
    }

    public async Task<CheckSourcesResult> CheckSourcesAsync(bool inspectStaticZips = true, CancellationToken ct = default)
    {
        var sources = (await GetSourcesAsync(ct)).Where(s => s.Enabled).ToList();
        var items = new List<DiscoveredExtensionItem>();
        var errors = new List<string>();

        foreach (var source in sources)
        {
            try
            {
                switch (NormalizeKind(source.Kind))
                {
                    case "manifest-feed":
                        items.AddRange(await CheckManifestFeedAsync(source, ct));
                        break;
                    case "github-release":
                        items.Add(await CheckGitHubReleaseAsync(source, ct));
                        break;
                    default:
                        items.Add(await CheckStaticZipAsync(source, inspectStaticZips, ct));
                        break;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{source.Label}: {ex.Message}");
                items.Add(new DiscoveredExtensionItem
                {
                    SourceId = source.Id,
                    SourceLabel = source.Label,
                    SourceKind = source.Kind,
                    ExtensionId = source.ExtensionId,
                    Name = source.Label,
                    DownloadUrl = source.Url,
                    Error = ex.Message,
                });
            }
        }

        return new CheckSourcesResult(items, errors);
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
        var description = GetString(elem, "description");
        var downloadUrl = GetString(elem, "downloadUrl") ?? GetString(elem, "url") ?? string.Empty;
        var updatedAt = GetString(elem, "updatedAt");

        return new DiscoveredExtensionItem
        {
            SourceId = source.Id,
            SourceLabel = source.Label,
            SourceKind = source.Kind,
            ExtensionId = id,
            Name = name,
            RemoteVersion = version,
            Description = description,
            DownloadUrl = downloadUrl,
            UpdatedAt = updatedAt,
        };
    }

    private async Task<DiscoveredExtensionItem> CheckGitHubReleaseAsync(ExtensionSourceEntry source, CancellationToken ct)
    {
        var repo = NormalizeGitHubRepo(source.Url);
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

        using var res = await _http.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();

        if (res.Content.Headers.ContentLength is long len && len > MaxZipInspectBytes)
            throw new InvalidOperationException($"ZIP exceeds {MaxZipInspectBytes / (1024 * 1024)} MB inspection limit.");

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        ms.Position = 0;

        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        var manifestEntry = archive.Entries.FirstOrDefault(e =>
            e.FullName.Equals("extension.json", StringComparison.OrdinalIgnoreCase) ||
            e.FullName.EndsWith("/extension.json", StringComparison.OrdinalIgnoreCase) &&
            e.FullName.Count(c => c == '/') == 1);

        if (manifestEntry == null)
            throw new InvalidOperationException("Remote ZIP does not contain an extension.json manifest.");

        await using var entryStream = manifestEntry.Open();
        using var doc = await JsonDocument.ParseAsync(entryStream, cancellationToken: ct);
        var root = doc.RootElement;

        return new DiscoveredExtensionItem
        {
            SourceId = source.Id,
            SourceLabel = source.Label,
            SourceKind = source.Kind,
            ExtensionId = source.ExtensionId ?? GetString(root, "id"),
            Name = GetString(root, "name") ?? source.Label,
            RemoteVersion = GetString(root, "version"),
            Description = GetString(root, "description"),
            DownloadUrl = source.Url,
        };
    }

    public static string NormalizeKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "manifest-feed" => "manifest-feed",
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
}
