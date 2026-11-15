using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoyfriendtvDownloader;

public sealed class BoyfriendtvDownloaderExtension : CoveExtensionBase, IDownloaderProvider
{
    public const string PrimaryDownloaderId = "karinauts.downloaders.boyfriendtv.video";
    public const string StudioName = "BoyFriendTV";
    private static readonly string[] SupportedDomains = BoyfriendtvAuthHelper.SupportedDomains;

    private static readonly Regex VideoOrEmbedPathRegex = new(
        @"^/(?:[a-z]{2}/)?(?:videos|embed)/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VideoIdRegex = new(
        @"/(?:videos|embed)/(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MediaDefinitionsRegex = new(
        @"(?is)mediaDefinitions\s*[:=]\s*(?<json>\[.*?\])\s*[,;]",
        RegexOptions.Compiled);

    private static readonly Regex FlashvarsRegex = new(
        @"(?is)flashvars_\d+\s*=\s*(?<json>\{.*?\});",
        RegexOptions.Compiled);

    private static readonly Regex PlayerConfigSourcesRegex = new(
        @"(?is)sources\s*:\s*(?<json>\{[^{}]*\})",
        RegexOptions.Compiled);

    private static readonly Regex VideoUrlPropertyRegex = new(
        @"(?is)""videoUrl""\s*:\s*""(?<url>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex PerformerAnchorRegex = new(
        @"(?is)<a\b[^>]*href=['""](?:https?://[^'""]+)?/pornstars/[^'""]+(?:/videos)?/['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.Compiled);

    private static readonly Regex TagAnchorRegex = new(
        @"(?is)<a\b[^>]*href=['""](?:https?://[^'""]+)?/tags/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.Compiled);

    private static readonly Regex TitleSuffixRegex = new(
        @"\s*(?:\||-)\s*BoyFriendTV(?:\.com)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (ResolvedMediaPage Page, DateTime Expiry)> _pageCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly BoyfriendtvAuthHelper _auth = new();
    private IServiceProvider? _services;
    private IConfiguration? _configuration;

    private static readonly DownloaderDescriptor PrimaryDescriptor = new(
        PrimaryDownloaderId,
        "BoyfriendTV Downloader",
        DownloaderEntity.Video,
        [
            "https://boyfriend.tv/*",
            "https://*.boyfriend.tv/*",
            "https://boyfriendtv.com/*",
            "https://*.boyfriendtv.com/*"
        ],
        DownloaderCapabilities.InlineMetadata | DownloaderCapabilities.MultiQuality);

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        _configuration = context.Configuration;
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _services = services;
        _configuration ??= services.GetService<IConfiguration>();
        return Task.CompletedTask;
    }

    public IReadOnlyList<DownloaderDescriptor> GetDownloaders() => [PrimaryDescriptor];

    public async Task<DownloaderUrlMatch?> MatchAsync(string url, CancellationToken ct)
    {
        if (!IsSupportedUrl(url))
            return null;

        var pageInfo = await TryInspectPageAsync(url, host: null, ct);
        var qualityOptions = pageInfo?.Streams
            .Where(s => !string.IsNullOrWhiteSpace(s.QualityId) && s.Format.Equals("mp4", StringComparison.OrdinalIgnoreCase))
            .Select(s => new DownloaderQualityOption(s.QualityId, s.Height != null ? $"{s.Height}p" : s.QualityId) { Height = s.Height })
            .DistinctBy(q => q.Id)
            .ToList();

        return new DownloaderUrlMatch(
            PrimaryDescriptor.Id,
            url,
            QualityOptions: qualityOptions is { Count: > 0 } ? qualityOptions : null,
            Label: pageInfo?.Title ?? DownloaderUtilities.DeriveTitleFromUrl(url, "BoyfriendTV Downloader"));
    }

    public async Task<DownloaderResult?> DownloadAsync(
        DownloaderRequest request,
        IDownloaderHost host,
        CancellationToken ct)
    {
        if (!string.Equals(request.DownloaderId, PrimaryDescriptor.Id, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!IsSupportedUrl(request.Url))
            throw new InvalidOperationException($"Unsupported URL for BoyfriendTV Downloader: {request.Url}");

        host.ReportProgress(5.0, "Inspecting BoyfriendTV media page...");
        var client = CreateHttpClient(host);
        var sessionHeaders = await _auth.GetSessionHeadersAsync(
            client,
            request.Url,
            _services,
            _configuration,
            Id ?? PrimaryDownloaderId,
            Name ?? nameof(BoyfriendtvDownloaderExtension),
            ct);

        var pageInfo = await InspectPageAsync(request.Url, client, sessionHeaders, ct);

        var selectedStream = SelectStream(pageInfo.Streams, request.QualityId, request.MaxHeight)
            ?? throw new InvalidOperationException($"Could not resolve a suitable video stream for {request.Url}");

        var extension = DownloaderUtilities.EnsureExtensionFromUrl(selectedStream.VideoUrl, ".mp4");
        var originalFileName = DownloaderUtilities.SanitizeFileName(pageInfo.Title) + extension;
        var localFileName = "downloaded" + extension;

        var downloadHeaders = new Dictionary<string, string>(sessionHeaders, StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = pageInfo.PageUrl
        };

        var localPath = await DownloaderUtilities.DownloadToTempFileAsync(
            client,
            selectedStream.VideoUrl,
            host,
            localFileName,
            ct,
            headers: downloadHeaders);

        var codeMatch = VideoIdRegex.Match(request.Url);
        var code = codeMatch.Success ? codeMatch.Groups["id"].Value : null;

        host.ReportProgress(100.0, "Completed download.");
        return new DownloaderResult(
            LocalPath: Path.GetFileName(localPath),
            OriginalFilename: originalFileName,
            InlineVideoMetadata: new ScrapedVideoDto
            {
                Title = pageInfo.Title,
                Code = code,
                Details = pageInfo.Description,
                Date = pageInfo.Date,
                Urls = [request.SourceUrl ?? request.Url],
                StudioName = StudioName,
                PerformerNames = pageInfo.PerformerNames,
                TagNames = pageInfo.TagNames,
                ImageUrl = pageInfo.ImageUrl
            });
    }

    public static bool IsSupportedUrl(string? url)
    {
        return DownloaderUtilities.IsHttpUrl(url)
            && DownloaderUtilities.IsAnyHost(url, SupportedDomains)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && VideoOrEmbedPathRegex.IsMatch(uri.AbsolutePath);
    }

    internal async Task<ResolvedMediaPage?> TryInspectPageAsync(string url, IDownloaderHost? host, CancellationToken ct)
    {
        try
        {
            var client = CreateHttpClient(host);
            var headers = await _auth.GetSessionHeadersAsync(
                client,
                url,
                _services,
                _configuration,
                Id ?? PrimaryDownloaderId,
                Name ?? nameof(BoyfriendtvDownloaderExtension),
                ct);
            return await InspectPageAsync(url, client, headers, ct);
        }
        catch
        {
            return null;
        }
    }

    internal async Task<ResolvedMediaPage> InspectPageAsync(string url, IDownloaderHost? host, CancellationToken ct)
    {
        var client = CreateHttpClient(host);
        var headers = await _auth.GetSessionHeadersAsync(
            client,
            url,
            _services,
            _configuration,
            Id ?? PrimaryDownloaderId,
            Name ?? nameof(BoyfriendtvDownloaderExtension),
            ct);
        return await InspectPageAsync(url, client, headers, ct);
    }

    private async Task<ResolvedMediaPage> InspectPageAsync(
        string url,
        HttpClient client,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        var cookieKey = headers.TryGetValue("Cookie", out var c) ? c : string.Empty;
        var cacheKey = $"{url}|{cookieKey}";
        if (_pageCache.TryGetValue(cacheKey, out var entry) && entry.Expiry > DateTime.UtcNow)
            return entry.Page;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.TryParseAdd(DownloaderUtilities.DefaultUserAgent);
        request.Headers.AcceptLanguage.TryParseAdd("en-US,en;q=0.9");
        foreach (var (key, value) in headers)
            request.Headers.TryAddWithoutValidation(key, value);

        using var response = await client.SendAsync(request, ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        BoyfriendtvAuthHelper.ThrowIfBlockedOrAgeGated(response.StatusCode, html, url);
        response.EnsureSuccessStatusCode();

        var parsed = ParsePage(html, url);
        _pageCache[cacheKey] = (parsed, DateTime.UtcNow.AddMinutes(5));
        return parsed;
    }

    internal static ResolvedMediaPage ParsePage(string html, string pageUrl)
    {
        var rawTitle = DownloaderUtilities.ExtractHtmlTitle(html)
            ?? DownloaderUtilities.DeriveTitleFromUrl(pageUrl, "BoyfriendTV Video");
        var cleanTitle = CleanTitle(rawTitle);

        var structured = ScraperUtilities.ExtractStructuredMetadata(html, StudioName);
        var finalTitle = !string.IsNullOrWhiteSpace(structured.Title)
            ? CleanTitle(structured.Title)
            : cleanTitle;

        var performers = new List<string>(structured.PerformerNames);
        foreach (Match match in PerformerAnchorRegex.Matches(html))
        {
            var name = DownloaderUtilities.CleanHtmlFragment(match.Groups["name"].Value);
            if (!string.IsNullOrWhiteSpace(name) && !performers.Contains(name, StringComparer.OrdinalIgnoreCase))
                performers.Add(name);
        }

        var tags = new List<string>(structured.TagNames);
        foreach (Match match in TagAnchorRegex.Matches(html))
        {
            var tag = DownloaderUtilities.CleanHtmlFragment(match.Groups["name"].Value);
            if (!string.IsNullOrWhiteSpace(tag) && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                tags.Add(tag);
        }

        var streams = ExtractMediaStreams(html, pageUrl);
        if (streams.Count == 0)
        {
            var fallbackUrl = ExtractFallbackMediaUrl(html, pageUrl);
            if (!string.IsNullOrWhiteSpace(fallbackUrl))
            {
                streams.Add(new MediaStream(fallbackUrl, "default", "mp4", null, true));
            }
        }

        if (streams.Count == 0)
            throw new InvalidOperationException($"Could not locate a media stream on {pageUrl}");

        return new ResolvedMediaPage(
            pageUrl,
            finalTitle,
            structured.Description,
            structured.Date,
            structured.ImageUrl,
            performers,
            tags,
            streams);
    }

    internal static string CleanTitle(string title)
    {
        var cleaned = TitleSuffixRegex.Replace(title.Trim(), string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned;
    }

    internal static List<MediaStream> ExtractMediaStreams(string html, string pageUrl)
    {
        var streams = new List<MediaStream>();

        // 1. Try mediaDefinitions JSON array
        var mediaDefMatch = MediaDefinitionsRegex.Match(html);
        if (mediaDefMatch.Success)
        {
            ParseMediaDefinitionsJson(mediaDefMatch.Groups["json"].Value, pageUrl, streams);
        }

        // 2. Try flashvars JSON object
        if (streams.Count == 0)
        {
            var flashvarsMatch = FlashvarsRegex.Match(html);
            if (flashvarsMatch.Success)
            {
                try
                {
                    using var doc = JsonDocument.Parse(flashvarsMatch.Groups["json"].Value);
                    if (doc.RootElement.TryGetProperty("mediaDefinitions", out var definitionsElem)
                        && definitionsElem.ValueKind == JsonValueKind.Array)
                    {
                        ParseMediaDefinitionsArray(definitionsElem, pageUrl, streams);
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed flashvars JSON
                }
            }
        }

        // 3. Try playerConfig.sources object (e.g. {"hlsAuto": "https://cdn.boyfriend.tv/..."} or {"mp4": ...})
        if (streams.Count == 0)
        {
            foreach (Match sourcesMatch in PlayerConfigSourcesRegex.Matches(html))
            {
                ParsePlayerConfigSourcesJson(sourcesMatch.Groups["json"].Value, pageUrl, streams);
            }
        }

        // 4. Fallback: videoUrl regex property matches
        if (streams.Count == 0)
        {
            foreach (Match match in VideoUrlPropertyRegex.Matches(html))
            {
                var raw = match.Groups["url"].Value.Replace(@"\/", "/");
                var normalized = NormalizeMediaUrl(raw, pageUrl);
                if (!string.IsNullOrWhiteSpace(normalized) && streams.All(s => s.VideoUrl != normalized))
                {
                    streams.Add(new MediaStream(normalized, "default", "mp4", null, true));
                }
            }
        }

        return streams;
    }

    private static void ParsePlayerConfigSourcesJson(string json, string pageUrl, List<MediaStream> streams)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String)
                    continue;

                var normalizedUrl = NormalizeMediaUrl(prop.Value.GetString(), pageUrl);
                if (string.IsNullOrWhiteSpace(normalizedUrl) || streams.Any(s => s.VideoUrl == normalizedUrl))
                    continue;

                var key = prop.Name;
                var isHls = key.Contains("hls", StringComparison.OrdinalIgnoreCase)
                    || normalizedUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                    || normalizedUrl.Contains("/media=hls", StringComparison.OrdinalIgnoreCase);

                int? height = int.TryParse(key.TrimEnd('p', 'P'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;

                var format = isHls ? "hls" : "mp4";
                var qualityId = height != null ? height.Value.ToString(CultureInfo.InvariantCulture) : key;
                streams.Add(new MediaStream(normalizedUrl, qualityId, format, height, key.Equals("hlsAuto", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (JsonException)
        {
            // Ignore malformed sources JSON
        }
    }

    private static void ParseMediaDefinitionsJson(string json, string pageUrl, List<MediaStream> streams)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                ParseMediaDefinitionsArray(doc.RootElement, pageUrl, streams);
            }
        }
        catch (JsonException)
        {
            // Ignore malformed JSON array
        }
    }

    private static void ParseMediaDefinitionsArray(JsonElement arrayElem, string pageUrl, List<MediaStream> streams)
    {
        foreach (var item in arrayElem.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            string? videoUrl = null;
            if (item.TryGetProperty("videoUrl", out var vUrl) && vUrl.ValueKind == JsonValueKind.String)
                videoUrl = vUrl.GetString();
            else if (item.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                videoUrl = urlProp.GetString();

            if (string.IsNullOrWhiteSpace(videoUrl))
                continue;

            var normalizedUrl = NormalizeMediaUrl(videoUrl, pageUrl);
            if (string.IsNullOrWhiteSpace(normalizedUrl))
                continue;

            string? format = null;
            if (item.TryGetProperty("format", out var formatProp) && formatProp.ValueKind == JsonValueKind.String)
                format = formatProp.GetString()?.ToLowerInvariant();

            string? quality = null;
            int? height = null;
            if (item.TryGetProperty("quality", out var qProp))
            {
                if (qProp.ValueKind == JsonValueKind.String)
                {
                    quality = qProp.GetString();
                }
                else if (qProp.ValueKind == JsonValueKind.Number && qProp.TryGetInt32(out var qNum))
                {
                    quality = qNum.ToString(CultureInfo.InvariantCulture);
                    height = qNum;
                }
            }

            if (height == null && int.TryParse(quality, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHeight))
                height = parsedHeight;

            var isDefault = item.TryGetProperty("defaultQuality", out var defProp)
                && defProp.ValueKind is JsonValueKind.True;

            var streamFormat = format ?? (normalizedUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? "hls" : "mp4");
            var qualityId = quality ?? (height != null ? $"{height}p" : "default");

            streams.Add(new MediaStream(normalizedUrl, qualityId, streamFormat, height, isDefault));
        }
    }

    internal static string? NormalizeMediaUrl(string? rawUrl, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return null;

        var trimmed = rawUrl.Trim().Replace(@"\/", "/");

        // Handle BoyfriendTV /key= CDN paths
        if (trimmed.StartsWith("/key=", StringComparison.OrdinalIgnoreCase))
            return $"https://cdn.boyfriendtv.com{trimmed}";

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return $"https:{trimmed}";

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absUri))
            return absUri.ToString();

        if (Uri.TryCreate(new Uri(pageUrl), trimmed, out var relUri))
            return relUri.ToString();

        return null;
    }

    internal static string? ExtractFallbackMediaUrl(string html, string pageUrl)
    {
        var candidate = DownloaderUtilities.ExtractMetaContent(html, "og:video:secure_url")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:video:url")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:video")
            ?? DownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<video[^>]+src\s*=\s*['""]([^'""]+)['""]")
            ?? DownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<source[^>]+src\s*=\s*['""]([^'""]+)['""]")
            ?? DownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)(https?://[^'""<>\s]+\.(?:mp4|m4v)(?:\?[^'""<>\s]*)?)");

        return NormalizeMediaUrl(candidate, pageUrl);
    }

    internal static MediaStream? SelectStream(IReadOnlyList<MediaStream> streams, string? requestedQualityId, int? maxHeight)
    {
        if (streams.Count == 0)
            return null;

        // 1. Exact quality ID match if requested
        if (!string.IsNullOrWhiteSpace(requestedQualityId))
        {
            var match = streams.FirstOrDefault(s => string.Equals(s.QualityId, requestedQualityId, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        // 2. Filter by maxHeight if requested
        var candidates = streams.AsEnumerable();
        if (maxHeight is > 0)
        {
            var withinLimit = candidates.Where(s => s.Height == null || s.Height <= maxHeight.Value).ToList();
            if (withinLimit.Count > 0)
                candidates = withinLimit;
        }

        // 3. Prefer MP4 over HLS, then highest resolution, then defaultQuality
        return candidates
            .OrderByDescending(s => s.Format.Equals("mp4", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(s => s.Height ?? 0)
            .ThenByDescending(s => s.IsDefault)
            .FirstOrDefault(s => s.Format.Equals("mp4", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    private HttpClient CreateHttpClient(IDownloaderHost? host = null)
    {
        if (host != null)
            return host.HttpClients.CreateClient(PrimaryDownloaderId);

        var factory = _services?.GetService<IHttpClientFactory>();
        return factory != null ? factory.CreateClient(PrimaryDownloaderId) : DownloaderUtilities.CreateFallbackHttpClient();
    }

    internal sealed record MediaStream(
        string VideoUrl,
        string QualityId,
        string Format,
        int? Height,
        bool IsDefault);

    internal sealed record ResolvedMediaPage(
        string PageUrl,
        string Title,
        string? Description,
        string? Date,
        string? ImageUrl,
        List<string> PerformerNames,
        List<string> TagNames,
        List<MediaStream> Streams);
}
