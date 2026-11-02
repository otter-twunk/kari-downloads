using System.Net;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace GayporntubeScraper;

public sealed class GayporntubeScraperExtension : CoveExtensionBase, IScraperProvider
{
    public const string PrimaryScraperId = "karinauts.scrapers.gayporntube.video";
    public const string DefaultStudioName = "GayPornTube";
    private static readonly string[] SupportedDomains = ["gayporntube.com"];

    private static readonly Regex VideoPathRegex = new(
        @"^/(?:video|embed)/(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VideoIdRegex = new(
        @"/(?:video|embed)/(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleSuffixRegex = new(
        @"\s*(?:\||-)\s*GayPornTube(?:\.com)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex H1TitleRegex = new(
        @"(?is)<h1[^>]*class=""[^""]*title[^""]*""[^>]*>(?<title>[^<]+)</h1>",
        RegexOptions.Compiled);

    private static readonly Regex CategoryAnchorRegex = new(
        @"(?is)<div[^>]*class=""[^""]*video-info-categories[^""]*""[^>]*>(?<content>.*?)</div>",
        RegexOptions.Compiled);

    private static readonly Regex AnchorTextRegex = new(
        @"(?is)<a\b[^>]*>(?<name>[^<]+)</a>",
        RegexOptions.Compiled);

    private static readonly Regex SearchItemRegex = new(
        @"(?is)<div\b[^>]*class=""[^""]*\bitem\b[^""]*""[^>]*data-video-id=""(?<id>\d+)""[^>]*>(?<content>.*?)</div>\s*(?=<div\b[^>]*class=""[^""]*\bitem\b|$)",
        RegexOptions.Compiled);

    private static readonly Regex SearchLinkRegex = new(
        @"(?is)<a\b[^>]*href=['""](?<url>https?://(?:www\.)?gayporntube\.com/video/\d+/[^'""]*)['""][^>]*class=""title""[^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled);

    private static readonly Regex SearchImageRegex = new(
        @"(?is)<img\b[^>]*data-(?:webp|src)=['""](?<url>[^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex SearchDurationRegex = new(
        @"(?is)<div\b[^>]*class=""[^""]*duration[^""]*""[^>]*>(?<dur>[^<]+)</div>",
        RegexOptions.Compiled);

    private static readonly Regex SlugNonWordRegex = new(
        @"[^a-z0-9]+",
        RegexOptions.Compiled);

    private IServiceProvider? _services;

    private static readonly ScraperDescriptor PrimaryDescriptor = new(
        PrimaryScraperId,
        "GayPornTube Scraper",
        ScraperEntity.Video,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
        ["gayporntube.com/video/"]);

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _services = services;
        return Task.CompletedTask;
    }

    public IReadOnlyList<ScraperDescriptor> GetScrapers() => [PrimaryDescriptor];

    public async Task<ScrapedVideoDto?> ScrapeVideoAsync(
        ScraperRequest<VideoScrapeInput> request,
        CancellationToken ct)
    {
        var rawUrl = request.Input.Url ?? request.Input.Urls.FirstOrDefault();
        if (!IsSupportedUrl(rawUrl))
            return null;

        var targetUrl = ToCanonicalVideoUrl(rawUrl!) ?? rawUrl!;
        var client = CreateHttpClient();

        string html;
        try
        {
            html = await ScraperUtilities.FetchHtmlAsync(client, targetUrl, ct);
        }
        catch (HttpRequestException) when (VideoIdRegex.Match(targetUrl) is { Success: true } idMatch && !targetUrl.Contains("/embed/"))
        {
            var embedUrl = $"https://www.gayporntube.com/embed/{idMatch.Groups["id"].Value}";
            html = await ScraperUtilities.FetchHtmlAsync(client, embedUrl, ct);
            targetUrl = embedUrl;
        }

        return ParseVideoHtml(html, targetUrl, request.Input, rawUrl);
    }

    public async Task<IReadOnlyList<ScrapedVideoDto>> SearchVideosAsync(
        ScraperRequest<string> request,
        CancellationToken ct)
    {
        var query = request.Input?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var searchUrl = $"https://www.gayporntube.com/search/videos/?q={Uri.EscapeDataString(query)}";
        var html = await ScraperUtilities.FetchHtmlAsync(CreateHttpClient(), searchUrl, ct);
        return ParseSearchResultsHtml(html, searchUrl);
    }

    public static bool IsSupportedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;

        var isDomain = SupportedDomains.Any(d =>
            uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));

        return isDomain && VideoPathRegex.IsMatch(uri.AbsolutePath);
    }

    public static string? ToCanonicalVideoUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;

        var match = VideoPathRegex.Match(uri.AbsolutePath);
        if (!match.Success)
            return null;

        var id = match.Groups["id"].Value;
        var host = uri.Host.Equals("gayporntube.com", StringComparison.OrdinalIgnoreCase)
            ? "www.gayporntube.com"
            : uri.Host;

        var videoSlugMatch = Regex.Match(uri.AbsolutePath, @"^/video/\d+/(?<slug>[^/?#]+)", RegexOptions.IgnoreCase);
        if (videoSlugMatch.Success && !string.IsNullOrWhiteSpace(videoSlugMatch.Groups["slug"].Value))
        {
            var slug = videoSlugMatch.Groups["slug"].Value;
            return $"https://{host}/video/{id}/{slug}";
        }

        return $"https://{host}/video/{id}/video";
    }

    public static string GenerateSlug(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "video";

        var slug = SlugNonWordRegex.Replace(text.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "video" : slug;
    }

    public static ScrapedVideoDto ParseVideoHtml(
        string html,
        string canonicalUrl,
        VideoScrapeInput? fallbackInput = null,
        string? originalUrl = null)
    {
        var structured = ScraperUtilities.ExtractStructuredMetadata(html, DefaultStudioName);

        var title = structured.Title;
        if (H1TitleRegex.Match(html) is { Success: true } h1Match)
        {
            var h1 = ScraperUtilities.CleanText(h1Match.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(h1))
                title = h1;
        }

        if (!string.IsNullOrWhiteSpace(title))
            title = TitleSuffixRegex.Replace(title, string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(title))
            title = fallbackInput?.Title ?? "Untitled";

        var videoIdMatch = VideoIdRegex.Match(canonicalUrl);
        var code = videoIdMatch.Success ? videoIdMatch.Groups["id"].Value : fallbackInput?.Code;

        var description = structured.Description;
        if (string.Equals(description, ".", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(description))
            description = fallbackInput?.Details;

        var tags = new List<string>(structured.TagNames);

        var catMatch = CategoryAnchorRegex.Match(html);
        if (catMatch.Success)
        {
            foreach (Match anchor in AnchorTextRegex.Matches(catMatch.Groups["content"].Value))
            {
                var val = WebUtility.HtmlDecode(anchor.Groups["name"].Value).Trim();
                if (!string.IsNullOrWhiteSpace(val) && !tags.Contains(val, StringComparer.OrdinalIgnoreCase))
                    tags.Add(val);
            }
        }

        var urls = new List<string>();

        if (!string.IsNullOrWhiteSpace(code))
        {
            var slug = GenerateSlug(title);
            var bestCanonicalUrl = $"https://www.gayporntube.com/video/{code}/{slug}";
            urls.Add(bestCanonicalUrl);
        }

        if (!urls.Contains(canonicalUrl, StringComparer.OrdinalIgnoreCase))
            urls.Add(canonicalUrl);

        if (!string.IsNullOrWhiteSpace(originalUrl) && !urls.Contains(originalUrl, StringComparer.OrdinalIgnoreCase))
            urls.Add(originalUrl);

        return new ScrapedVideoDto
        {
            SourceScraperId = PrimaryScraperId,
            Title = title,
            Code = code,
            Date = structured.Date ?? fallbackInput?.Date,
            Details = description,
            Director = structured.Director ?? fallbackInput?.Director,
            ImageUrl = structured.ImageUrl,
            Urls = urls,
            StudioName = DefaultStudioName,
            PerformerNames = structured.PerformerNames.ToList(),
            TagNames = tags
        };
    }

    public static IReadOnlyList<ScrapedVideoDto> ParseSearchResultsHtml(string html, string baseSearchUrl)
    {
        var results = new List<ScrapedVideoDto>();
        var baseUri = new Uri(baseSearchUrl);

        foreach (Match match in SearchLinkRegex.Matches(html))
        {
            var href = match.Groups["url"].Value.Trim();
            if (!Uri.TryCreate(baseUri, href, out var fullUri) || !IsSupportedUrl(fullUri.ToString()))
                continue;

            var fullUrl = fullUri.ToString();
            if (results.Any(r => r.Urls.Contains(fullUrl, StringComparer.OrdinalIgnoreCase)))
                continue;

            var title = ScraperUtilities.CleanText(match.Groups["title"].Value);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var idMatch = VideoIdRegex.Match(fullUrl);
            var code = idMatch.Success ? idMatch.Groups["id"].Value : null;

            results.Add(new ScrapedVideoDto
            {
                SourceScraperId = PrimaryScraperId,
                Title = title,
                Code = code,
                Urls = [fullUrl],
                StudioName = DefaultStudioName
            });
        }

        return results;
    }

    private HttpClient CreateHttpClient()
    {
        var factory = _services?.GetService<IHttpClientFactory>();
        return factory != null ? factory.CreateClient(PrimaryScraperId) : new HttpClient();
    }
}
