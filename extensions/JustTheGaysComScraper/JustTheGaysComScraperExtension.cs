using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace JustTheGaysComScraper;

public sealed class JustTheGaysComScraperExtension : CoveExtensionBase, IScraperProvider
{
    public const string PrimaryScraperId = "karinauts.scrapers.just-the-gays-com.video";
    public const string DefaultStudioName = "JustTheGays.com";
    private static readonly string[] SupportedDomains = ["justthegays.com"];

    private IServiceProvider? _services;

    private static readonly ScraperDescriptor PrimaryDescriptor = new(
        PrimaryScraperId,
        "JustTheGays.com Scraper",
        ScraperEntity.Video,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
        ["justthegays.com/"]);

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
        var sourceUrl = request.Input.Url ?? request.Input.Urls.FirstOrDefault();
        if (!IsSupportedUrl(sourceUrl))
            return null;

        var html = await ScraperUtilities.FetchHtmlAsync(CreateHttpClient(), sourceUrl!, ct);
        return ParseVideoHtml(html, sourceUrl!, request.Input);
    }

    public async Task<IReadOnlyList<ScrapedVideoDto>> SearchVideosAsync(
        ScraperRequest<string> request,
        CancellationToken ct)
    {
        var query = request.Input?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var searchUrl = $"https://justthegays.com/search?q={Uri.EscapeDataString(query)}";
        var html = await ScraperUtilities.FetchHtmlAsync(CreateHttpClient(), searchUrl, ct);
        return ParseSearchResultsHtml(html, searchUrl);
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
        var code = ScraperUtilities.ExtractCode(sourceUrl)
            ?? ScraperUtilities.ExtractCode(structured.Title)
            ?? fallbackInput?.Code;

        return new ScrapedVideoDto
        {
            SourceScraperId = PrimaryScraperId,
            Title = structured.Title ?? fallbackInput?.Title ?? "Untitled",
            Code = code,
            Date = structured.Date ?? fallbackInput?.Date,
            Details = structured.Description ?? fallbackInput?.Details,
            Director = structured.Director ?? fallbackInput?.Director,
            ImageUrl = structured.ImageUrl,
            Urls = [sourceUrl],
            StudioName = structured.StudioName ?? DefaultStudioName,
            PerformerNames = structured.PerformerNames.ToList(),
            TagNames = structured.TagNames.ToList()
        };
    }

    internal static IReadOnlyList<ScrapedVideoDto> ParseSearchResultsHtml(string html, string baseSearchUrl)
    {
        var results = new List<ScrapedVideoDto>();
        var baseUri = new Uri(baseSearchUrl);

        foreach (Match match in Regex.Matches(
            html,
            @"(?is)<a[^>]*hrefs*=s*['""](?<href>[^'""]+)['""][^>]*>(?<inner>.*?)</a>"))
        {
            var href = match.Groups["href"].Value.Trim();
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Uri.TryCreate(baseUri, href, out var fullUri) || !IsSupportedUrl(fullUri.ToString()))
                continue;

            var title = ScraperUtilities.CleanText(match.Groups["inner"].Value);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            if (results.Any(r => r.Urls.Contains(fullUri.ToString(), StringComparer.OrdinalIgnoreCase)))
                continue;

            results.Add(new ScrapedVideoDto
            {
                SourceScraperId = PrimaryScraperId,
                Title = title,
                Urls = [fullUri.ToString()],
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
