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
        var studioName = ExtractStudio(html, structured.StudioName ?? DefaultStudioName);

        // Performers: only from explicit VideoObject.actor or from the video details "Models:" block
        var performers = new List<string>(structured.PerformerNames);
        performers.AddRange(ExtractPerformers(html));

        // Tags: from the video details "Tags:" block, falling back to non-generic structured tags
        var tags = ExtractTags(html, structured.TagNames);

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

    internal static string ExtractVideoDetailsSection(string html)
    {
        // 1. Specific tab_video_info container
        var tabMatch = Regex.Match(
            html,
            @"(?is)<div\b[^>]*\bid=['""]tab_video_info['""][^>]*>(?<content>.*?)(?=<div\b[^>]*\bid=['""]tab_|<div\b[^>]*class=['""][^'""]*\bfooter|<div\b[^>]*class=['""][^'""]*\blist-videos|<!-- BROWSE|</body>|$)");
        if (tabMatch.Success)
            return tabMatch.Groups["content"].Value;

        // 2. block-details container
        var blockMatch = Regex.Match(
            html,
            @"(?is)<div\b[^>]*class=['""][^'""]*\bblock-details\b[^'""]*['""][^>]*>(?<content>.*?)(?=<div\b[^>]*\bid=['""]tab_|<div\b[^>]*class=['""][^'""]*\bfooter|<div\b[^>]*class=['""][^'""]*\blist-videos|<!-- BROWSE|</body>|$)");
        if (blockMatch.Success)
            return blockMatch.Groups["content"].Value;

        // 3. video-info container
        var infoMatch = Regex.Match(
            html,
            @"(?is)<div\b[^>]*class=['""][^'""]*\bvideo-info\b[^'""]*['""][^>]*>(?<content>.*?)(?=<div\b[^>]*class=['""][^'""]*\bfooter|<div\b[^>]*class=['""][^'""]*\blist-videos|<!-- BROWSE|</body>|$)");
        if (infoMatch.Success)
            return infoMatch.Groups["content"].Value;

        return string.Empty;
    }

    internal static List<string> ExtractPerformers(string html)
    {
        var performers = new List<string>();

        // Only inspect the video's details container, NEVER the whole page
        var detailsSection = ExtractVideoDetailsSection(html);
        if (string.IsNullOrWhiteSpace(detailsSection))
            return performers;

        // Look specifically for the Models item within the video details
        var modelsItemMatch = Regex.Match(
            detailsSection,
            @"(?is)<div\b[^>]*class=['""][^'""]*\bitem\b[^'""]*['""][^>]*>\s*Models:\s*(?<content>.*?)</div>");

        var modelsBlock = modelsItemMatch.Success
            ? modelsItemMatch.Groups["content"].Value
            : Regex.Match(detailsSection, @"(?is)\bModels:\s*(?<content>.*?)(?:</div>|<div\b|Tags:|Sponsored by:)").Groups["content"].Value;

        if (!string.IsNullOrWhiteSpace(modelsBlock))
        {
            foreach (Match anchor in Regex.Matches(modelsBlock, @"(?is)<a\b[^>]*href=['""][^'""]*/models/[^/'""]+/?['""][^>]*>(?<name>[^<]+)</a>"))
            {
                var name = ScraperUtilities.CleanText(anchor.Groups["name"].Value);
                if (!string.IsNullOrWhiteSpace(name) && !name.Equals("PornStars", StringComparison.OrdinalIgnoreCase))
                    performers.Add(name);
            }
        }

        return ScraperUtilities.DeduplicateStrings(performers);
    }

    internal static string ExtractStudio(string html, string defaultStudio = DefaultStudioName)
    {
        var detailsSection = ExtractVideoDetailsSection(html);
        if (!string.IsNullOrWhiteSpace(detailsSection))
        {
            var sponsorMatch = Regex.Match(
                detailsSection,
                @"(?is)<div\b[^>]*class=['""][^'""]*\bitem\b[^'""]*['""][^>]*>\s*Sponsored by:\s*<a\b[^>]*>(?<studio>[^<]+)</a>");

            if (!sponsorMatch.Success)
                sponsorMatch = Regex.Match(detailsSection, @"(?is)Sponsored by:\s*<a\b[^>]*>(?<studio>[^<]+)</a>");

            if (sponsorMatch.Success)
            {
                var studio = ScraperUtilities.CleanText(sponsorMatch.Groups["studio"].Value);
                if (!string.IsNullOrWhiteSpace(studio))
                    return studio;
            }
        }

        return defaultStudio;
    }

    internal static List<string> ExtractTags(string html, IReadOnlyList<string>? fallbackTags = null)
    {
        var tags = new List<string>();
        var detailsSection = ExtractVideoDetailsSection(html);
        if (!string.IsNullOrWhiteSpace(detailsSection))
        {
            var tagsItemMatch = Regex.Match(
                detailsSection,
                @"(?is)<div\b[^>]*class=['""][^'""]*\bitem\b[^'""]*['""][^>]*>\s*Tags:\s*(?<content>.*?)</div>");

            var tagsBlock = tagsItemMatch.Success
                ? tagsItemMatch.Groups["content"].Value
                : Regex.Match(detailsSection, @"(?is)\bTags:\s*(?<content>.*?)(?:</div>|<div\b|Models:|Sponsored by:)").Groups["content"].Value;

            if (!string.IsNullOrWhiteSpace(tagsBlock))
            {
                foreach (Match anchor in Regex.Matches(tagsBlock, @"(?is)<a\b[^>]*href=['""][^'""]*/tags/[^/'""]+/?['""][^>]*>(?<tag>[^<]+)</a>"))
                {
                    var tag = ScraperUtilities.CleanText(anchor.Groups["tag"].Value);
                    if (!string.IsNullOrWhiteSpace(tag))
                        tags.Add(tag);
                }
            }
        }

        if (tags.Count == 0 && fallbackTags != null)
        {
            foreach (var t in fallbackTags)
            {
                if (!t.Equals("gay porn", StringComparison.OrdinalIgnoreCase)
                    && !t.Equals("gayporn", StringComparison.OrdinalIgnoreCase)
                    && !t.Equals("gay xxx", StringComparison.OrdinalIgnoreCase)
                    && !t.Equals("gay0day", StringComparison.OrdinalIgnoreCase)
                    && !t.Equals("gay sex", StringComparison.OrdinalIgnoreCase)
                    && !t.Equals("gay tube", StringComparison.OrdinalIgnoreCase)
                    && !t.Equals("gay porn stars", StringComparison.OrdinalIgnoreCase))
                {
                    tags.Add(t);
                }
            }
        }

        return ScraperUtilities.DeduplicateStrings(tags);
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
