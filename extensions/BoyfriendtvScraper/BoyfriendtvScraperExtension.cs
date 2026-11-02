using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace BoyfriendtvScraper;

public sealed class BoyfriendtvScraperExtension : CoveExtensionBase, IScraperProvider
{
    public const string VideoScraperId = "karinauts.scrapers.boyfriendtv.video";
    public const string PrimaryScraperId = VideoScraperId;
    public const string PerformerScraperId = "karinauts.scrapers.boyfriendtv.performer";
    public const string DefaultStudioName = "BoyFriendTV";

    private static readonly string[] SupportedDomains = ["boyfriendtv.com"];

    private static readonly Regex VideoIdRegex = new(
        @"/videos/(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PerformerAnchorRegex = new(
        @"href=""/pornstars/[^""]+/videos/""[^>]*>(?<name>[^<]+)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagAnchorRegex = new(
        @"href=""/tags/[^""]+""[^>]*>(?<name>[^<]+)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VideoTitleSuffixRegex = new(
        @"\s*(?:\||-)\s*BoyFriendTV(?:\.com)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PerformerTitleSuffixRegex = new(
        @"\s*(?:Gay Pornstar\s*-\s*BoyFriendTV\.com|(?:\||-)\s*BoyFriendTV(?:\.com)?)\s*$",
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

    private IServiceProvider? _services;

    private static readonly ScraperDescriptor VideoDescriptor = new(
        VideoScraperId,
        "BoyfriendTV Video Scraper",
        ScraperEntity.Video,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
        ["boyfriendtv.com/videos/"]);

    private static readonly ScraperDescriptor PerformerDescriptor = new(
        PerformerScraperId,
        "BoyfriendTV Performer Scraper",
        ScraperEntity.Performer,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
        ["boyfriendtv.com/pornstars/"]);

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _services = services;
        return Task.CompletedTask;
    }

    public IReadOnlyList<ScraperDescriptor> GetScrapers() => [VideoDescriptor, PerformerDescriptor];

    public async Task<ScrapedVideoDto?> ScrapeVideoAsync(
        ScraperRequest<VideoScrapeInput> request,
        CancellationToken ct)
    {
        var sourceUrl = request.Input.Url ?? request.Input.Urls.FirstOrDefault();
        if (!IsSupportedVideoUrl(sourceUrl))
            return null;

        var html = await ScraperUtilities.FetchHtmlAsync(
            CreateHttpClient(),
            sourceUrl!,
            ct,
            GetDefaultHeaders());

        return ParseVideoHtml(html, sourceUrl!, request.Input);
    }

    public async Task<IReadOnlyList<ScrapedVideoDto>> SearchVideosAsync(
        ScraperRequest<string> request,
        CancellationToken ct)
    {
        var query = request.Input?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var searchUrl = $"https://www.boyfriendtv.com/search/videos/{Uri.EscapeDataString(query)}/";
        var html = await ScraperUtilities.FetchHtmlAsync(
            CreateHttpClient(),
            searchUrl,
            ct,
            GetDefaultHeaders());

        return ParseVideoSearchResultsHtml(html, searchUrl);
    }

    public async Task<ScrapedPerformerDto?> ScrapePerformerAsync(
        ScraperRequest<PerformerScrapeInput> request,
        CancellationToken ct)
    {
        var sourceUrl = request.Input.Url ?? request.Input.Urls.FirstOrDefault();
        if (!IsSupportedPerformerUrl(sourceUrl))
            return null;

        string? html = null;
        try
        {
            html = await ScraperUtilities.FetchHtmlAsync(
                CreateHttpClient(),
                sourceUrl!,
                ct,
                GetDefaultHeaders());
        }
        catch
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

        var searchUrl = $"https://www.boyfriendtv.com/search/pornstars/{Uri.EscapeDataString(query)}/";
        var html = await ScraperUtilities.FetchHtmlAsync(
            CreateHttpClient(),
            searchUrl,
            ct,
            GetDefaultHeaders());

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
            Date = structured.Date ?? fallbackInput?.Date,
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

        // 1. Try JSON-LD Person block
        foreach (var block in DownloaderUtilities.ExtractJsonLdBlocks(html))
        {
            using (block)
            {
                if (block.RootElement.ValueKind == JsonValueKind.Object
                    && block.RootElement.TryGetProperty("@type", out var typeProp)
                    && string.Equals(typeProp.GetString(), "Person", StringComparison.OrdinalIgnoreCase))
                {
                    if (block.RootElement.TryGetProperty("name", out var nProp))
                        name = nProp.GetString();
                    if (block.RootElement.TryGetProperty("description", out var dProp))
                        details = dProp.GetString();
                    if (block.RootElement.TryGetProperty("image", out var iProp))
                        imageUrl = iProp.GetString();
                }
            }
        }

        // 2. OpenGraph / Title fallback
        name ??= ScraperUtilities.ExtractMetaContent(html, "og:title")
            ?? ScraperUtilities.ExtractMetaContent(html, "twitter:title")
            ?? DownloaderUtilities.ExtractHtmlTitle(html);

        if (!string.IsNullOrWhiteSpace(name))
            name = CleanPerformerName(name);

        // 3. URL slug fallback if name is still empty (e.g. Cloudflare blocked or minimal HTML)
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

            results.Add(new ScrapedVideoDto
            {
                SourceScraperId = VideoScraperId,
                Title = title,
                Code = code,
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
        // Strip trailing numeric ID like "-2077"
        slug = Regex.Replace(slug, @"-\d+$", string.Empty);
        var parts = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        return string.Join(" ", parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    private static Dictionary<string, string> GetDefaultHeaders() => new()
    {
        ["Cookie"] = "age_verified=1",
        ["Referer"] = "https://www.boyfriendtv.com/"
    };

    private HttpClient CreateHttpClient()
    {
        var factory = _services?.GetService<IHttpClientFactory>();
        return factory != null ? factory.CreateClient(VideoScraperId) : new HttpClient();
    }
}
