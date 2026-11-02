using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ThisvidScraper;

public sealed class ThisvidScraperExtension : CoveExtensionBase, IScraperProvider
{
    public const string PrimaryScraperId = "karinauts.scrapers.thisvid.video";
    public const string DefaultStudioName = "ThisVid";
    private static readonly string[] SupportedDomains = ["thisvid.com"];

    private static readonly Regex VideoPathRegex = new(
        @"^/(?:videos|embed|playlist/\d+/video)/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlaylistVideoRegex = new(
        @"^/playlist/\d+/video/(?<slug>[^/?#]+)/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VideoIdRegex = new(
        @"(?:data-video-id=[""'](?<id>\d+)[""']|videoId:\s*['""]?(?<id>\d+)|/(?:videos|embed)/(?<id>\d+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleSuffixRegex = new(
        @"\s*-\s*ThisVid(?:\.com)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DescriptionParagraphRegex = new(
        @"<ul[^>]*class=[""'][^""']*\bdescription\b[^""']*[""'][^>]*>.*?<li>\s*<p>(?<desc>.*?)</p>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AddedDateRegex = new(
        @"<span>\s*Added:\s*</span>\s*(?<date>\d{1,2}\s+[A-Za-z]+\s+\d{4})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CategoriesBlockRegex = new(
        @"<li>\s*<span>\s*Categories:\s*</span>(?<content>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TagsBlockRegex = new(
        @"<li>\s*<span>\s*Tags:\s*</span>(?<content>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AnchorRegex = new(
        @"<a\b[^>]*>(?<text>[^<]+)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SearchItemRegex = new(
        @"(?is)<a\b[^>]*href=[""'](?<url>(?:https?://[^""']*)?/videos/[^""']+)[""'][^>]*>(?:.*?<img\b[^>]*src=[""'](?<thumb>[^""']+)[""'][^>]*>)?(?<text>.*?)</a>",
        RegexOptions.Compiled);

    private IServiceProvider? _services;
    private IConfiguration? _configuration;

    private static readonly ScraperDescriptor PrimaryDescriptor = new(
        PrimaryScraperId,
        "ThisVid Scraper",
        ScraperEntity.Video,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
        ["thisvid.com/*"]);

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

    public IReadOnlyList<ScraperDescriptor> GetScrapers() => [PrimaryDescriptor];

    public async Task<ScrapedVideoDto?> ScrapeVideoAsync(
        ScraperRequest<VideoScrapeInput> request,
        CancellationToken ct)
    {
        var rawUrl = request.Input.Url ?? request.Input.Urls.FirstOrDefault();
        if (!IsSupportedUrl(rawUrl))
            return null;

        var sourceUrl = NormalizeVideoUrl(rawUrl!);
        var headers = GetDefaultHeaders();
        var cookie = GetConfiguredCookie(sourceUrl);
        if (!string.IsNullOrWhiteSpace(cookie))
            headers["Cookie"] = cookie;

        var html = await ScraperUtilities.FetchHtmlAsync(CreateHttpClient(), sourceUrl, ct, headers);
        return ParseVideoHtml(html, sourceUrl, request.Input);
    }

    public async Task<IReadOnlyList<ScrapedVideoDto>> SearchVideosAsync(
        ScraperRequest<string> request,
        CancellationToken ct)
    {
        var query = request.Input?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var searchUrl = $"https://thisvid.com/search/?q={Uri.EscapeDataString(query)}";
        var headers = GetDefaultHeaders();
        var cookie = GetConfiguredCookie(searchUrl);
        if (!string.IsNullOrWhiteSpace(cookie))
            headers["Cookie"] = cookie;

        var html = await ScraperUtilities.FetchHtmlAsync(CreateHttpClient(), searchUrl, ct, headers);
        return ParseSearchResultsHtml(html, searchUrl);
    }

    public static bool IsSupportedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!SupportedDomains.Any(d => uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)))
            return false;

        return VideoPathRegex.IsMatch(uri.AbsolutePath);
    }

    public static string NormalizeVideoUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url;

        var playlistMatch = PlaylistVideoRegex.Match(uri.AbsolutePath);
        if (playlistMatch.Success)
        {
            var slug = playlistMatch.Groups["slug"].Value;
            return $"https://thisvid.com/videos/{slug}/";
        }

        return url;
    }

    internal static ScrapedVideoDto ParseVideoHtml(string html, string sourceUrl, VideoScrapeInput? fallbackInput = null)
    {
        if (html.Contains("Age verification required", StringComparison.OrdinalIgnoreCase)
            || html.Contains("/age-verification/", StringComparison.OrdinalIgnoreCase)
            || html.Contains("agegate", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ThisVid displayed an age-verification gate for {sourceUrl}. " +
                "Please configure your ThisVid cookie/session in Cove Settings to bypass the age gate.");
        }

        var structured = ScraperUtilities.ExtractStructuredMetadata(html, DefaultStudioName);

        // Title
        var rawTitle = structured.Title
            ?? ScraperUtilities.ExtractMetaContent(html, "og:title")
            ?? DownloaderUtilities.ExtractHtmlTitle(html)
            ?? fallbackInput?.Title
            ?? "Untitled";

        var cleanTitle = TitleSuffixRegex.Replace(rawTitle, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
            cleanTitle = rawTitle;

        // Details / Description
        string? details = null;
        var descMatch = DescriptionParagraphRegex.Match(html);
        if (descMatch.Success)
        {
            var rawParagraph = descMatch.Groups["desc"].Value;
            var cleanedDesc = ScraperUtilities.CleanText(Regex.Replace(rawParagraph, "<[^>]+>", " "));
            if (!string.IsNullOrWhiteSpace(cleanedDesc))
                details = cleanedDesc;
        }

        if (string.IsNullOrWhiteSpace(details))
        {
            details = structured.Description
                ?? ScraperUtilities.ExtractMetaContent(html, "og:description")
                ?? ScraperUtilities.ExtractMetaContent(html, "description")
                ?? fallbackInput?.Details;
        }

        // Date
        string? date = null;
        var dateMatch = AddedDateRegex.Match(html);
        if (dateMatch.Success)
        {
            var rawDate = dateMatch.Groups["date"].Value;
            date = ScraperUtilities.NormalizeIsoDate(rawDate);
        }

        if (string.IsNullOrWhiteSpace(date))
            date = structured.Date ?? fallbackInput?.Date;

        // Code
        string? code = null;
        var idMatch = VideoIdRegex.Match(html);
        if (idMatch.Success)
            code = idMatch.Groups["id"].Value;

        if (string.IsNullOrWhiteSpace(code))
        {
            var urlMatch = VideoIdRegex.Match(sourceUrl);
            if (urlMatch.Success)
                code = urlMatch.Groups["id"].Value;
        }

        if (string.IsNullOrWhiteSpace(code))
            code = fallbackInput?.Code;

        // Image URL
        var imageUrl = structured.ImageUrl
            ?? ScraperUtilities.ExtractMetaContent(html, "og:image");

        // Tags & Categories
        var tagNames = new List<string>();

        void ExtractAnchorsFromBlock(Regex blockRegex)
        {
            var match = blockRegex.Match(html);
            if (!match.Success)
                return;

            foreach (Match anchor in AnchorRegex.Matches(match.Groups["content"].Value))
            {
                var text = ScraperUtilities.CleanText(anchor.Groups["text"].Value);
                if (!string.IsNullOrWhiteSpace(text))
                    tagNames.Add(text);
            }
        }

        ExtractAnchorsFromBlock(CategoriesBlockRegex);
        ExtractAnchorsFromBlock(TagsBlockRegex);

        if (structured.TagNames.Count > 0)
            tagNames.AddRange(structured.TagNames);

        var deduplicatedTags = ScraperUtilities.DeduplicateStrings(tagNames);

        // Canonical URL
        var canonicalUrl = ScraperUtilities.ExtractMetaContent(html, "og:url");
        if (string.IsNullOrWhiteSpace(canonicalUrl) || !Uri.TryCreate(canonicalUrl, UriKind.Absolute, out _))
            canonicalUrl = sourceUrl;

        return new ScrapedVideoDto
        {
            SourceScraperId = PrimaryScraperId,
            Title = cleanTitle,
            Code = code,
            Date = date,
            Details = details,
            ImageUrl = imageUrl,
            Urls = [canonicalUrl],
            StudioName = DefaultStudioName,
            TagNames = deduplicatedTags
        };
    }

    internal static IReadOnlyList<ScrapedVideoDto> ParseSearchResultsHtml(string html, string baseSearchUrl)
    {
        var results = new List<ScrapedVideoDto>();
        var baseUri = new Uri(baseSearchUrl);
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in SearchItemRegex.Matches(html))
        {
            var href = match.Groups["url"].Value.Trim();
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var absoluteUrl = Uri.TryCreate(baseUri, href, out var abs) ? abs.ToString() : href;
            if (!seenUrls.Add(absoluteUrl))
                continue;

            var rawTitle = match.Groups["text"].Value;
            var cleanTitle = ScraperUtilities.CleanText(Regex.Replace(rawTitle, "<[^>]+>", " "));
            if (!string.IsNullOrWhiteSpace(cleanTitle))
                cleanTitle = TitleSuffixRegex.Replace(cleanTitle, string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(cleanTitle))
                cleanTitle = DownloaderUtilities.DeriveTitleFromUrl(absoluteUrl, "ThisVid Video");

            string? thumb = null;
            var thumbRaw = match.Groups["thumb"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(thumbRaw) && Uri.TryCreate(baseUri, thumbRaw, out var absThumb))
                thumb = absThumb.ToString();

            var code = VideoIdRegex.Match(absoluteUrl) is { Success: true } cm ? cm.Groups["id"].Value : null;

            results.Add(new ScrapedVideoDto
            {
                SourceScraperId = PrimaryScraperId,
                Title = cleanTitle,
                Code = code,
                ImageUrl = thumb,
                Urls = [absoluteUrl],
                StudioName = DefaultStudioName
            });
        }

        return results;
    }

    private static Dictionary<string, string> GetDefaultHeaders()
    {
        return new Dictionary<string, string>
        {
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            ["Accept-Language"] = "en-US,en;q=0.9"
        };
    }

    private string? GetConfiguredCookie(string url)
    {
        var loginProvider = _services?.GetService<IDownloaderSiteLoginProvider>();
        var login = loginProvider?.FindForUrl(url);
        if (login != null)
        {
            if (login.Password.Contains('=') || login.Password.Contains(';'))
                return login.Password;
            if (!string.IsNullOrWhiteSpace(login.Password))
                return $"PHPSESSID={login.Password}";
        }

        var settingCookie = _configuration?["cookie"]
            ?? _configuration?[$"{Id}:cookie"]
            ?? _configuration?["ThisvidScraper:cookie"];

        return !string.IsNullOrWhiteSpace(settingCookie) ? settingCookie : null;
    }

    private HttpClient CreateHttpClient()
    {
        var factory = _services?.GetService<IHttpClientFactory>();
        return factory != null ? factory.CreateClient(PrimaryScraperId) : new HttpClient();
    }
}
