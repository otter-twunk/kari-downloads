using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoyfriendtvScraper;

public sealed class BoyfriendtvScraperExtension : CoveExtensionBase, IScraperProvider
{
    public const string VideoScraperId = "karinauts.scrapers.boyfriendtv.video";
    public const string PrimaryScraperId = VideoScraperId;
    public const string PerformerScraperId = "karinauts.scrapers.boyfriendtv.performer";
    public const string DefaultStudioName = "BoyFriendTV";

    private static readonly string[] SupportedDomains = BoyfriendtvAuthHelper.SupportedDomains;

    private static readonly Regex VideoIdRegex = new(
        @"/videos/(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PerformerAnchorRegex = new(
        @"(?is)<a\b[^>]*href=['""](?:https?://[^'""]+)?/pornstars/[^'""]+(?:/videos)?/['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.Compiled);

    private static readonly Regex TagAnchorRegex = new(
        @"(?is)<a\b[^>]*href=['""](?:https?://[^'""]+)?/tags/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.Compiled);

    private static readonly Regex VideoTitleSuffixRegex = new(
        @"\s*(?:\||-)\s*BoyFriendTV(?:\.com)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PerformerTitleSuffixRegex = new(
        @"\s*(?:Gay Pornstar\s*-\s*BoyFriendTV(?:\.com)?|(?:\||-)\s*BoyFriendTV(?:\.com)?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PerformerSlugRegex = new(
        @"/pornstars/(?<slug>[^/]+)/?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VideoLinkRegex = new(
        @"(?is)<a\b[^>]*href=['""](?<href>(?:https?://[^'""]+)?/videos/\d+/[^'""]*)['""][^>]*>(?<text>.*?)</a>",
        RegexOptions.Compiled);

    private static readonly Regex PerformerLinkRegex = new(
        @"(?is)<a\b[^>]*href=['""](?<href>(?:https?://[^'""]+)?/pornstars/[^'""]+/)['""][^>]*>(?<text>.*?)</a>",
        RegexOptions.Compiled);

    private readonly BoyfriendtvAuthHelper _auth = new();
    private IServiceProvider? _services;
    private IConfiguration? _configuration;

    private static readonly ScraperDescriptor VideoDescriptor = new(
        VideoScraperId,
        "BoyfriendTV Video Scraper",
        ScraperEntity.Video,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
        ["boyfriend.tv/videos/", "boyfriendtv.com/videos/"]);

    private static readonly ScraperDescriptor PerformerDescriptor = new(
        PerformerScraperId,
        "BoyfriendTV Performer Scraper",
        ScraperEntity.Performer,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
        ["boyfriend.tv/pornstars/", "boyfriendtv.com/pornstars/"]);

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

    public IReadOnlyList<ScraperDescriptor> GetScrapers() => [VideoDescriptor, PerformerDescriptor];

    public async Task<ScrapedVideoDto?> ScrapeVideoAsync(
        ScraperRequest<VideoScrapeInput> request,
        CancellationToken ct)
    {
        var sourceUrl = request.Input.Url ?? request.Input.Urls.FirstOrDefault();
        if (!IsSupportedVideoUrl(sourceUrl))
        {
            var fallbackTitle = CleanVideoTitle(request.Input.Title ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(fallbackTitle) && !fallbackTitle.Equals("Untitled", StringComparison.OrdinalIgnoreCase))
            {
                var searchResults = await SearchVideosAsync(
                    new ScraperRequest<string>(VideoScraperId, fallbackTitle, request.Permissions),
                    ct);

                var resolvedUrl = searchResults.FirstOrDefault()?.Urls.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(resolvedUrl))
                {
                    return await ScrapeVideoAsync(
                        new ScraperRequest<VideoScrapeInput>(
                            request.ScraperId,
                            new VideoScrapeInput
                            {
                                Url = resolvedUrl,
                                Title = fallbackTitle,
                                Code = request.Input.Code,
                                Date = request.Input.Date,
                                Details = request.Input.Details,
                                Director = request.Input.Director
                            },
                            request.Permissions),
                        ct);
                }
            }

            return null;
        }

        var html = await FetchPageHtmlAsync(sourceUrl!, ct);
        return ParseVideoHtml(html, sourceUrl!, request.Input);
    }

    public async Task<IReadOnlyList<ScrapedVideoDto>> SearchVideosAsync(
        ScraperRequest<string> request,
        CancellationToken ct)
    {
        var query = request.Input?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var searchUrl = $"https://www.boyfriend.tv/search/videos/{Uri.EscapeDataString(query)}/";
        var html = await FetchPageHtmlAsync(searchUrl, ct);
        return ParseVideoSearchResultsHtml(html, searchUrl);
    }

    public async Task<ScrapedPerformerDto?> ScrapePerformerAsync(
        ScraperRequest<PerformerScrapeInput> request,
        CancellationToken ct)
    {
        var sourceUrl = request.Input.Url ?? request.Input.Urls.FirstOrDefault();
        if (!IsSupportedPerformerUrl(sourceUrl))
        {
            var fallbackName = ScraperUtilities.CleanText(request.Input.Name);
            if (!string.IsNullOrWhiteSpace(fallbackName))
            {
                var searchResults = await SearchPerformersAsync(
                    new ScraperRequest<string>(PerformerScraperId, fallbackName, request.Permissions),
                    ct);

                var resolvedUrl = searchResults.FirstOrDefault()?.Urls.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(resolvedUrl))
                {
                    return await ScrapePerformerAsync(
                        new ScraperRequest<PerformerScrapeInput>(
                            request.ScraperId,
                            new PerformerScrapeInput
                            {
                                Url = resolvedUrl,
                                Name = fallbackName,
                                Details = request.Input.Details
                            },
                            request.Permissions),
                        ct);
                }
            }

            return null;
        }

        string? html = null;
        try
        {
            html = await FetchPageHtmlAsync(sourceUrl!, ct);
        }
        catch (HttpRequestException)
        {
            // Fall back to slug parsing if page is unreachable
        }

        return ParsePerformerHtml(html ?? string.Empty, sourceUrl!, request.Input);
    }

    public async Task<IReadOnlyList<ScrapedPerformerDto>> SearchPerformersAsync(
        ScraperRequest<string> request,
        CancellationToken ct)
    {
        var query = request.Input?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var searchUrl = $"https://www.boyfriend.tv/search/pornstars/{Uri.EscapeDataString(query)}/";
        var html = await FetchPageHtmlAsync(searchUrl, ct);
        return ParsePerformerSearchResultsHtml(html, searchUrl);
    }

    public static bool IsSupportedVideoUrl(string? url)
    {
        return IsSupportedUrl(url) && (url?.Contains("/videos/", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public static bool IsSupportedPerformerUrl(string? url)
    {
        return IsSupportedUrl(url) && (url?.Contains("/pornstars/", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public static bool IsSupportedUrl(string? url)
    {
        return ScraperUtilities.CleanText(url) is { } cleaned
            && Uri.TryCreate(cleaned, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            && SupportedDomains.Any(d =>
                uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    internal static ScrapedVideoDto ParseVideoHtml(string html, string sourceUrl, VideoScrapeInput? fallbackInput = null)
    {
        var structured = ScraperUtilities.ExtractStructuredMetadata(html, DefaultStudioName);
        var rawTitle = structured.Title ?? fallbackInput?.Title ?? "Untitled";
        var title = CleanVideoTitle(rawTitle);

        var codeMatch = VideoIdRegex.Match(sourceUrl);
        var code = codeMatch.Success ? codeMatch.Groups["id"].Value : (fallbackInput?.Code ?? ScraperUtilities.ExtractCode(title));

        var performers = new List<string>(structured.PerformerNames);
        foreach (Match match in PerformerAnchorRegex.Matches(html))
        {
            var name = ScraperUtilities.CleanText(match.Groups["name"].Value);
            if (!string.IsNullOrWhiteSpace(name) && !performers.Contains(name, StringComparer.OrdinalIgnoreCase))
                performers.Add(name);
        }

        var tags = new List<string>(structured.TagNames);
        foreach (Match match in TagAnchorRegex.Matches(html))
        {
            var tag = ScraperUtilities.CleanText(match.Groups["name"].Value);
            if (!string.IsNullOrWhiteSpace(tag) && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                tags.Add(tag);
        }

        return new ScrapedVideoDto
        {
            SourceScraperId = VideoScraperId,
            Title = title,
            Code = code,
            Date = structured.Date ?? ScraperUtilities.NormalizeIsoDate(fallbackInput?.Date),
            Details = structured.Description ?? fallbackInput?.Details,
            Director = structured.Director ?? fallbackInput?.Director,
            ImageUrl = structured.ImageUrl,
            Urls = [sourceUrl],
            StudioName = structured.StudioName ?? DefaultStudioName,
            PerformerNames = performers,
            TagNames = tags
        };
    }

    internal static ScrapedPerformerDto ParsePerformerHtml(string html, string sourceUrl, PerformerScrapeInput? fallbackInput = null)
    {
        string? name = null;
        string? details = null;
        string? imageUrl = null;
        string? eyeColor = null;
        string? hairColor = null;
        string? ethnicity = null;

        // 1. Try JSON-LD Person or ProfilePage.mainEntity block
        foreach (var block in DownloaderUtilities.ExtractJsonLdBlocks(html))
        {
            using (block)
            {
                if (TryFindPersonElement(block.RootElement, out var personElem))
                {
                    name ??= GetJsonString(personElem, "name");
                    details ??= GetJsonString(personElem, "description");
                    imageUrl ??= ExtractJsonLdImageUrl(personElem);
                    eyeColor ??= GetJsonString(personElem, "eyeColor");
                    hairColor ??= GetJsonString(personElem, "hairColor");
                    ethnicity ??= GetJsonString(personElem, "ethnicity");
                }
            }
        }

        // 2. OpenGraph / Title fallback
        name ??= ScraperUtilities.ExtractMetaContent(html, "og:title")
            ?? ScraperUtilities.ExtractMetaContent(html, "twitter:title")
            ?? DownloaderUtilities.ExtractHtmlTitle(html);

        if (!string.IsNullOrWhiteSpace(name))
            name = CleanPerformerName(name);

        // 3. URL slug fallback if name is still empty
        if (string.IsNullOrWhiteSpace(name))
            name = SlugToPerformerName(sourceUrl) ?? fallbackInput?.Name ?? "Unknown Performer";

        imageUrl ??= ScraperUtilities.ExtractMetaContent(html, "og:image")
            ?? ScraperUtilities.ExtractMetaContent(html, "twitter:image");

        details ??= ScraperUtilities.ExtractMetaContent(html, "og:description")
            ?? ScraperUtilities.ExtractMetaContent(html, "description")
            ?? fallbackInput?.Details;

        return new ScrapedPerformerDto
        {
            SourceScraperId = PerformerScraperId,
            Name = name,
            Gender = "Male",
            EyeColor = eyeColor,
            HairColor = hairColor,
            Ethnicity = ethnicity,
            Details = details,
            ImageUrl = imageUrl,
            Urls = [sourceUrl]
        };
    }

    internal static IReadOnlyList<ScrapedVideoDto> ParseVideoSearchResultsHtml(string html, string baseSearchUrl)
    {
        var results = new List<ScrapedVideoDto>();
        var baseUri = new Uri(baseSearchUrl);

        foreach (Match match in VideoLinkRegex.Matches(html))
        {
            var href = match.Groups["href"].Value.Trim();
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#'))
                continue;

            if (!Uri.TryCreate(baseUri, href, out var fullUri) || !IsSupportedVideoUrl(fullUri.ToString()))
                continue;

            var rawTitle = ScraperUtilities.CleanText(match.Groups["text"].Value);
            var title = string.IsNullOrWhiteSpace(rawTitle) ? null : CleanVideoTitle(rawTitle);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var fullUrl = fullUri.ToString();
            if (results.Any(r => r.Urls.Contains(fullUrl, StringComparer.OrdinalIgnoreCase)))
                continue;

            var codeMatch = VideoIdRegex.Match(fullUrl);
            var code = codeMatch.Success ? codeMatch.Groups["id"].Value : null;

            string? thumb = null;
            var imgMatch = Regex.Match(match.Groups["text"].Value, @"(?is)<img\b[^>]*\b(?:data-src|src)=['""](?<src>[^'""]+)['""]");
            if (imgMatch.Success)
            {
                var src = imgMatch.Groups["src"].Value.Trim();
                if (!src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    thumb = src;
            }

            results.Add(new ScrapedVideoDto
            {
                SourceScraperId = VideoScraperId,
                Title = title,
                Code = code,
                ImageUrl = thumb,
                Urls = [fullUrl],
                StudioName = DefaultStudioName
            });
        }

        return results;
    }

    internal static IReadOnlyList<ScrapedPerformerDto> ParsePerformerSearchResultsHtml(string html, string baseSearchUrl)
    {
        var results = new List<ScrapedPerformerDto>();
        var baseUri = new Uri(baseSearchUrl);

        foreach (Match match in PerformerLinkRegex.Matches(html))
        {
            var href = match.Groups["href"].Value.Trim();
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#'))
                continue;

            if (!Uri.TryCreate(baseUri, href, out var fullUri) || !IsSupportedPerformerUrl(fullUri.ToString()))
                continue;

            var rawName = ScraperUtilities.CleanText(match.Groups["text"].Value);
            var name = string.IsNullOrWhiteSpace(rawName) ? SlugToPerformerName(fullUri.ToString()) : CleanPerformerName(rawName);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var fullUrl = fullUri.ToString();
            if (results.Any(r => r.Urls.Contains(fullUrl, StringComparer.OrdinalIgnoreCase)))
                continue;

            results.Add(new ScrapedPerformerDto
            {
                SourceScraperId = PerformerScraperId,
                Name = name,
                Gender = "Male",
                Urls = [fullUrl]
            });
        }

        return results;
    }

    internal static string CleanVideoTitle(string title)
    {
        var cleaned = VideoTitleSuffixRegex.Replace(title.Trim(), string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned;
    }

    internal static string CleanPerformerName(string name)
    {
        var cleaned = PerformerTitleSuffixRegex.Replace(name.Trim(), string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown Performer" : cleaned;
    }

    internal static string? SlugToPerformerName(string url)
    {
        var match = PerformerSlugRegex.Match(url);
        if (!match.Success)
            return null;

        var slug = match.Groups["slug"].Value;
        slug = Regex.Replace(slug, @"-\d+$", string.Empty);
        var parts = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        return string.Join(" ", parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    private async Task<string> FetchPageHtmlAsync(string url, CancellationToken ct)
    {
        var client = CreateHttpClient();
        var headers = await _auth.GetSessionHeadersAsync(
            client,
            url,
            _services,
            _configuration,
            Id ?? VideoScraperId,
            Name ?? nameof(BoyfriendtvScraperExtension),
            ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.TryParseAdd(ScraperUtilities.DefaultUserAgent);
        request.Headers.AcceptLanguage.TryParseAdd("en-US,en;q=0.9");
        foreach (var (key, value) in headers)
            request.Headers.TryAddWithoutValidation(key, value);

        using var response = await client.SendAsync(request, ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        BoyfriendtvAuthHelper.ThrowIfBlockedOrAgeGated(response.StatusCode, html, url);
        response.EnsureSuccessStatusCode();
        return html;
    }

    private static bool TryFindPersonElement(JsonElement root, out JsonElement personElem)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (HasJsonLdType(root, "Person"))
            {
                personElem = root;
                return true;
            }

            if (HasJsonLdType(root, "ProfilePage")
                && root.TryGetProperty("mainEntity", out var mainEntity)
                && mainEntity.ValueKind == JsonValueKind.Object
                && HasJsonLdType(mainEntity, "Person"))
            {
                personElem = mainEntity;
                return true;
            }

            if (root.TryGetProperty("@graph", out var graph) && graph.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in graph.EnumerateArray())
                {
                    if (TryFindPersonElement(item, out personElem))
                        return true;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (TryFindPersonElement(item, out personElem))
                    return true;
            }
        }

        personElem = default;
        return false;
    }

    private static bool HasJsonLdType(JsonElement elem, string expectedType)
    {
        if (!elem.TryGetProperty("@type", out var typeProp))
            return false;

        if (typeProp.ValueKind == JsonValueKind.String)
            return string.Equals(typeProp.GetString(), expectedType, StringComparison.OrdinalIgnoreCase);

        if (typeProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typeProp.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && string.Equals(item.GetString(), expectedType, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? GetJsonString(JsonElement elem, string propertyName)
    {
        if (elem.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return ScraperUtilities.CleanText(prop.GetString());

        return null;
    }

    private static string? ExtractJsonLdImageUrl(JsonElement elem)
    {
        if (!elem.TryGetProperty("image", out var imgProp))
            return null;

        if (imgProp.ValueKind == JsonValueKind.String)
            return imgProp.GetString();

        if (imgProp.ValueKind == JsonValueKind.Object)
            return GetJsonString(imgProp, "url") ?? GetJsonString(imgProp, "contentUrl");

        if (imgProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in imgProp.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    return item.GetString();
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var url = GetJsonString(item, "url") ?? GetJsonString(item, "contentUrl");
                    if (!string.IsNullOrWhiteSpace(url))
                        return url;
                }
            }
        }

        return null;
    }

    private HttpClient CreateHttpClient()
    {
        var factory = _services?.GetService<IHttpClientFactory>();
        return factory != null ? factory.CreateClient(VideoScraperId) : ScraperUtilities.CreateFallbackHttpClient();
    }
}
