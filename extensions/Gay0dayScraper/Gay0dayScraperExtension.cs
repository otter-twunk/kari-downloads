using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Gay0dayScraper;

public sealed class Gay0dayScraperExtension : CoveExtensionBase, IScraperProvider
{
    public const string VideoScraperId = "karinauts.scrapers.gay0day.video";
    public const string PerformerScraperId = "karinauts.scrapers.gay0day.performer";
    public const string DefaultStudioName = "Gay0Day";
    private static readonly string[] SupportedDomains = ["gay0day.com"];

    private static readonly Regex VideoPathRegex = new(@"^/videos/(?<id>\d+)(?:/[^/]+)?/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PerformerPathRegex = new(@"^/models/(?<slug>[^/]+)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SponsoredByRegex = new(@"Sponsored by:\s*<a\b[^>]*>(?<studio>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ModelsBlockRegex = new(@"Models:\s*(?<models>.*?)(?:</div>|Tags:|Sponsored by:)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagsBlockRegex = new(@"Tags:\s*(?<tags>.*?)(?:</div>|Models:|Sponsored by:)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AnchorTextRegex = new(@"<a\b[^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private IServiceProvider? _services;

    private static readonly ScraperDescriptor VideoDescriptor = new(
        VideoScraperId,
        "Gay0day Video Scraper",
        ScraperEntity.Video,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
        ["gay0day.com/videos/"]);

    private static readonly ScraperDescriptor PerformerDescriptor = new(
        PerformerScraperId,
        "Gay0day Performer Scraper",
        ScraperEntity.Performer,
        ScraperCapabilities.ByUrl,
        ["gay0day.com/models/"]);

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
        {
            var fallbackTitle = ScraperUtilities.CleanText(request.Input.Title);
            if (!string.IsNullOrWhiteSpace(fallbackTitle))
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
                                Details = request.Input.Details
                            },
                            request.Permissions),
                        ct);
                }
            }

            return null;
        }

        var normalizedUrl = NormalizeVideoUrl(sourceUrl!);
        var html = await ScraperUtilities.FetchHtmlAsync(CreateHttpClient(VideoScraperId), normalizedUrl, ct);
        return ParseVideoHtml(html, normalizedUrl, request.Input);
    }

    public async Task<IReadOnlyList<ScrapedVideoDto>> SearchVideosAsync(
        ScraperRequest<string> request,
        CancellationToken ct)
    {
        var query = ScraperUtilities.CleanText(request.Input);
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var searchUrl = $"https://gay0day.com/search/{Uri.EscapeDataString(query)}/";
        var html = await ScraperUtilities.FetchHtmlAsync(CreateHttpClient(VideoScraperId), searchUrl, ct);
        return ParseSearchResultsHtml(html, searchUrl);
    }

    public async Task<ScrapedPerformerDto?> ScrapePerformerAsync(
        ScraperRequest<PerformerScrapeInput> request,
        CancellationToken ct)
    {
        var sourceUrl = request.Input.Url ?? request.Input.Urls.FirstOrDefault();
        if (!IsSupportedPerformerUrl(sourceUrl))
            return null;

        var html = await ScraperUtilities.FetchHtmlAsync(CreateHttpClient(PerformerScraperId), sourceUrl!, ct);
        return ParsePerformerHtml(html, sourceUrl!, request.Input);
    }

    public static bool IsSupportedVideoUrl(string? url)
    {
        if (!DownloaderUtilities.IsHttpUrl(url))
            return false;

        var uri = new Uri(url!);
        if (!DownloaderUtilities.IsAnyHost(url, SupportedDomains))
            return false;

        return VideoPathRegex.IsMatch(uri.AbsolutePath);
    }

    public static bool IsSupportedPerformerUrl(string? url)
    {
        if (!DownloaderUtilities.IsHttpUrl(url))
            return false;

        var uri = new Uri(url!);
        if (!DownloaderUtilities.IsAnyHost(url, SupportedDomains))
            return false;

        return PerformerPathRegex.IsMatch(uri.AbsolutePath);
    }

    public static string NormalizeVideoUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url;

        var match = VideoPathRegex.Match(uri.AbsolutePath);
        if (match.Success)
        {
            var id = match.Groups["id"].Value;
            var path = uri.AbsolutePath.TrimEnd('/');
            return $"https://gay0day.com{path}/";
        }

        return uri.ToString();
    }

    internal static ScrapedVideoDto ParseVideoHtml(string html, string sourceUrl, VideoScrapeInput? fallbackInput = null)
    {
        var structured = ScraperUtilities.ExtractStructuredMetadata(html, DefaultStudioName);

        var rawTitle = DownloaderUtilities.ExtractFirstMatch(html, @"(?is)<div\b[^>]*class\s*=\s*['""][^'""]*\bheadline\b[^'""]*['""][^>]*>\s*<h1[^>]*>(.*?)</h1>")
            ?? DownloaderUtilities.ExtractFirstMatch(html, @"(?is)<h1[^>]*>(.*?)</h1>")
            ?? structured.Title
            ?? fallbackInput?.Title
            ?? "Untitled";

        var title = CleanVideoTitle(rawTitle);

        string? code = null;
        var codeMatch = Regex.Match(sourceUrl, @"/videos/(?<id>\d+)");
        if (codeMatch.Success)
            code = codeMatch.Groups["id"].Value;
        code ??= fallbackInput?.Code;

        string? date = structured.Date ?? fallbackInput?.Date;

        var details = structured.Description ?? fallbackInput?.Details;

        string? studioName = null;
        var sponsorMatch = SponsoredByRegex.Match(html);
        if (sponsorMatch.Success)
        {
            var parsedStudio = ScraperUtilities.CleanText(sponsorMatch.Groups["studio"].Value);
            if (!string.IsNullOrWhiteSpace(parsedStudio))
                studioName = parsedStudio;
        }

        studioName ??= structured.StudioName ?? DefaultStudioName;

        var performers = new List<string>(structured.PerformerNames);
        var modelsMatch = ModelsBlockRegex.Match(html);
        if (modelsMatch.Success)
        {
            foreach (Match anchor in AnchorTextRegex.Matches(modelsMatch.Groups["models"].Value))
            {
                var name = ScraperUtilities.CleanText(anchor.Groups["text"].Value);
                if (!string.IsNullOrWhiteSpace(name))
                    performers.Add(name);
            }
        }

        // Additional fallback: model links
        if (performers.Count == 0)
        {
            foreach (Match match in Regex.Matches(html, @"(?is)<a\b[^>]*href=['""]https?://(?:www\.)?gay0day\.com/models/[^'""]+['""][^>]*>(?<name>[^<]+)</a>"))
            {
                var name = ScraperUtilities.CleanText(match.Groups["name"].Value);
                if (!string.IsNullOrWhiteSpace(name) && !name.Equals("PornStars", StringComparison.OrdinalIgnoreCase))
                    performers.Add(name);
            }
        }

        var tags = new List<string>(structured.TagNames);
        var tagsMatch = TagsBlockRegex.Match(html);
        if (tagsMatch.Success)
        {
            foreach (Match anchor in AnchorTextRegex.Matches(tagsMatch.Groups["tags"].Value))
            {
                var tag = ScraperUtilities.CleanText(anchor.Groups["text"].Value);
                if (!string.IsNullOrWhiteSpace(tag))
                    tags.Add(tag);
            }
        }

        var imageUrl = structured.ImageUrl;
        if (IsGenericLogo(imageUrl))
            imageUrl = null;

        return new ScrapedVideoDto
        {
            SourceScraperId = VideoScraperId,
            Title = title,
            Code = code,
            Date = date,
            Details = details,
            Director = structured.Director ?? fallbackInput?.Director,
            ImageUrl = imageUrl,
            Urls = [sourceUrl],
            StudioName = studioName,
            PerformerNames = ScraperUtilities.DeduplicateStrings(performers),
            TagNames = ScraperUtilities.DeduplicateStrings(tags)
        };
    }

    internal static IReadOnlyList<ScrapedVideoDto> ParseSearchResultsHtml(string html, string baseSearchUrl)
    {
        var results = new List<ScrapedVideoDto>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Matches video link cards in Gay0day search / listings:
        // <a href="https://gay0day.com/videos/..." title="..." ...>...</a>
        var anchorMatches = Regex.Matches(
            html,
            @"(?is)<a\b[^>]*href=['""](?<url>https?://(?:www\.)?gay0day\.com/videos/\d+/[^'""]*)['""][^>]*>(?<inner>.*?)</a>");

        foreach (Match match in anchorMatches)
        {
            var url = match.Groups["url"].Value.Trim();
            if (!seenUrls.Add(url))
                continue;

            var aTag = match.Value;
            var titleMatch = Regex.Match(aTag, @"(?is)\btitle=['""](?<title>[^'""]+)['""]");
            var rawTitle = titleMatch.Success ? titleMatch.Groups["title"].Value : null;

            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                var titleElementMatch = Regex.Match(match.Groups["inner"].Value, @"(?is)<(?:strong|span)\b[^>]*class=['""][^'""]*\btitle\b[^'""]*['""][^>]*>(?<t>.*?)</(?:strong|span)>");
                if (titleElementMatch.Success)
                    rawTitle = titleElementMatch.Groups["t"].Value;
            }

            var title = CleanVideoTitle(rawTitle);
            if (string.IsNullOrWhiteSpace(title) || title.Equals("Untitled", StringComparison.OrdinalIgnoreCase))
                title = DownloaderUtilities.DeriveTitleFromUrl(url, "Gay0day Video");

            string? code = null;
            var cMatch = Regex.Match(url, @"/videos/(?<id>\d+)");
            if (cMatch.Success)
                code = cMatch.Groups["id"].Value;

            string? thumbUrl = null;
            var imgMatch = Regex.Match(match.Groups["inner"].Value, @"(?is)<img\b[^>]*\b(?:data-original|src)=['""](?<src>[^'""]+)['""]");
            if (imgMatch.Success)
            {
                var src = imgMatch.Groups["src"].Value.Trim();
                if (!src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    thumbUrl = src;
            }

            results.Add(new ScrapedVideoDto
            {
                SourceScraperId = VideoScraperId,
                Title = title,
                Code = code,
                ImageUrl = thumbUrl,
                Urls = [url],
                StudioName = DefaultStudioName
            });
        }

        return results;
    }

    internal static ScrapedPerformerDto ParsePerformerHtml(string html, string pageUrl, PerformerScrapeInput? fallbackInput = null)
    {
        var rawName = DownloaderUtilities.ExtractFirstMatch(html, @"(?is)<div\b[^>]*class=['""][^'""]*\bheadline\b[^'""]*['""][^>]*>\s*<h2[^>]*>(?:Gay actors\s+)?(.*?)</h2>")
            ?? DownloaderUtilities.ExtractFirstMatch(html, @"(?is)<h2[^>]*>(?:Gay actors\s+)?(.*?)</h2>")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:title")
            ?? fallbackInput?.Name;

        var name = CleanPerformerName(rawName) ?? fallbackInput?.Name ?? "Unknown";

        var details = DownloaderUtilities.ExtractFirstMatch(html, @"(?is)<div\b[^>]*class=['""][^'""]*\bdesc\b[^'""]*['""][^>]*>(.*?)</div>")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:description")
            ?? fallbackInput?.Details;

        string? imageUrl = null;
        var imgMatch = Regex.Match(html, @"(?is)<div\b[^>]*class=['""][^'""]*\bblock-model\b[^'""]*['""][^>]*>.*?<img\b[^>]*src=['""](?<src>[^'""]+)['""]");
        if (imgMatch.Success)
        {
            var src = imgMatch.Groups["src"].Value.Trim();
            if (!IsGenericLogo(src))
                imageUrl = src;
        }

        imageUrl ??= DownloaderUtilities.ExtractMetaContent(html, "og:image");
        if (IsGenericLogo(imageUrl))
            imageUrl = null;

        string? country = null;
        var countryMatch = Regex.Match(html, @"(?is)<li>Country:\s*<span>(?<c>[^<]+)</span></li>");
        if (countryMatch.Success && !countryMatch.Groups["c"].Value.Trim().Equals("N/A", StringComparison.OrdinalIgnoreCase))
            country = ScraperUtilities.CleanText(countryMatch.Groups["c"].Value);

        return new ScrapedPerformerDto
        {
            SourceScraperId = PerformerScraperId,
            Name = name,
            Gender = "Male",
            Details = ScraperUtilities.CleanText(details),
            Country = country,
            ImageUrl = imageUrl,
            Urls = [pageUrl]
        };
    }

    private static string CleanVideoTitle(string? title)
    {
        var cleaned = ScraperUtilities.CleanText(title) ?? "Untitled";
        cleaned = Regex.Replace(cleaned, @"\s*@\s*Gay0Day\b.*$", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*-\s*Gay0Day\b.*$", string.Empty, RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }

    private static string? CleanPerformerName(string? raw)
    {
        var cleaned = ScraperUtilities.CleanText(raw);
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        cleaned = Regex.Replace(cleaned, @"^Gay actors\s+", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"'s Gay porn model\b.*$", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"'s gay xxx videos\b.*$", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*@\s*Gay0Day\b.*$", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*-\s*Gay0Day\b.*$", string.Empty, RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }

    private static bool IsGenericLogo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        return url.Contains("og-default.jpg", StringComparison.OrdinalIgnoreCase)
            || url.Contains("logo.png", StringComparison.OrdinalIgnoreCase)
            || url.Contains("no-thumb", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient CreateHttpClient(string scraperId)
    {
        var factory = _services?.GetService<IHttpClientFactory>();
        return factory != null ? factory.CreateClient(scraperId) : new HttpClient();
    }
}
