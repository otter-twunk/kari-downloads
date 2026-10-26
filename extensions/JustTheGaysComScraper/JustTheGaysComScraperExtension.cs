using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace JustTheGaysComScraper;

public sealed class JustTheGaysComScraperExtension : CoveExtensionBase, IScraperProvider
{
    public const string VideoScraperId = "karinauts.scrapers.just-the-gays-com.video";
    public const string PerformerScraperId = "karinauts.scrapers.just-the-gays-com.performer";
    public const string DefaultStudioName = "Just The Gays";

    private static readonly string[] SupportedDomains =
    [
        "justthegays.com"
    ];

    private static readonly Regex VideoPathRegex = new(
        @"^/video/(?<slug>[a-zA-Z0-9_-]+)(?:/embed)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WpJsonVideoRegex = new(
        @"^/wp-json/wp/v2/video/(?<id>\d+)/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PerformerPathRegex = new(
        @"^/performers?/(?<slug>[a-zA-Z0-9_-]+)/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PerformerLinkRegex = new(
        @"href\s*=\s*['""]https?://(?:www\.)?justthegays\.com/performers/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CategoryLinkRegex = new(
        @"href\s*=\s*['""]https?://(?:www\.)?justthegays\.com/categories/[^'""]+['""][^>]*rel\s*=\s*['""]tag['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex WpJsonLinkRegex = new(
        @"<link\b[^>]+rel\s*=\s*['""]alternate['""][^>]+type\s*=\s*['""]application/json['""][^>]+href\s*=\s*['""](?<href>https?://[^'""]+/wp-json/wp/v2/video/\d+)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private IServiceProvider? _services;

    private static readonly ScraperDescriptor VideoDescriptor = new(
        VideoScraperId,
        "JustTheGays.com Video Scraper",
        ScraperEntity.Video,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
        ["justthegays.com/video/"]);

    private static readonly ScraperDescriptor PerformerDescriptor = new(
        PerformerScraperId,
        "JustTheGays.com Performer Scraper",
        ScraperEntity.Performer,
        ScraperCapabilities.ByUrl,
        ["justthegays.com/performers/"]);

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
            var fallbackTitle = CleanSearchQuery(request.Input.Title);
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

        var client = CreateHttpClient();

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) && WpJsonVideoRegex.IsMatch(uri.AbsolutePath))
        {
            var json = await ScraperUtilities.FetchHtmlAsync(client, sourceUrl!, ct);
            return ParseVideoWpJson(json, sourceUrl!, request.Input);
        }

        var normalizedUrl = NormalizeVideoUrl(sourceUrl!);
        var html = await ScraperUtilities.FetchHtmlAsync(client, normalizedUrl, ct);
        var parsedFromHtml = ParseVideoHtml(html, normalizedUrl, request.Input);

        // If the HTML lacked performers or tags, try resolving the WP REST API URL
        if (parsedFromHtml.PerformerNames.Count == 0 || parsedFromHtml.TagNames.Count == 0)
        {
            var wpJsonUrl = ExtractComWpJsonUrl(html, normalizedUrl);
            if (!string.IsNullOrWhiteSpace(wpJsonUrl))
            {
                try
                {
                    var apiJson = await ScraperUtilities.FetchHtmlAsync(client, wpJsonUrl, ct);
                    var parsedFromApi = ParseVideoWpJson(apiJson, normalizedUrl, request.Input);
                    return MergeVideoResults(parsedFromHtml, parsedFromApi);
                }
                catch
                {
                    // Fall back to HTML-only result
                }
            }
        }

        return parsedFromHtml;
    }

    public async Task<IReadOnlyList<ScrapedVideoDto>> SearchVideosAsync(
        ScraperRequest<string> request,
        CancellationToken ct)
    {
        var query = CleanSearchQuery(request.Input);
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var client = CreateHttpClient();
        var searchApiUrl = $"https://justthegays.com/wp-json/wp/v2/search?subtype=video&per_page=20&search={Uri.EscapeDataString(query)}";

        try
        {
            var json = await ScraperUtilities.FetchHtmlAsync(client, searchApiUrl, ct);
            return ParseSearchApiJson(json);
        }
        catch
        {
            // Fallback to HTML search
            var searchHtmlUrl = $"https://justthegays.com/?s={Uri.EscapeDataString(query)}";
            var html = await ScraperUtilities.FetchHtmlAsync(client, searchHtmlUrl, ct);
            return ParseSearchHtml(html, searchHtmlUrl);
        }
    }

    public async Task<ScrapedPerformerDto?> ScrapePerformerAsync(
        ScraperRequest<PerformerScrapeInput> request,
        CancellationToken ct)
    {
        var sourceUrl = request.Input.Url ?? request.Input.Urls.FirstOrDefault();
        if (!IsSupportedPerformerUrl(sourceUrl))
            return null;

        var html = await ScraperUtilities.FetchHtmlAsync(CreateHttpClient(), sourceUrl!, ct);
        return ParsePerformerHtml(html, sourceUrl!, request.Input);
    }

    public static bool IsSupportedVideoUrl(string? url)
    {
        if (!DownloaderUtilities.IsHttpUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!DownloaderUtilities.IsAnyHost(uri.ToString(), SupportedDomains))
            return false;

        return VideoPathRegex.IsMatch(uri.AbsolutePath) || WpJsonVideoRegex.IsMatch(uri.AbsolutePath);
    }

    public static bool IsSupportedPerformerUrl(string? url)
    {
        if (!DownloaderUtilities.IsHttpUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!DownloaderUtilities.IsAnyHost(uri.ToString(), SupportedDomains))
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
            var slug = match.Groups["slug"].Value;
            return $"https://justthegays.com/video/{slug}/";
        }

        return uri.ToString();
    }

    internal static ScrapedVideoDto ParseVideoHtml(string html, string pageUrl, VideoScrapeInput? fallbackInput = null)
    {
        var articleHtml = ExtractPrimaryArticleHtml(html);

        var rawTitle = DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)<h1[^>]*>(.*?)</h1>")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:title")
            ?? DownloaderUtilities.ExtractHtmlTitle(html);

        var title = CleanComTitle(rawTitle)
            ?? fallbackInput?.Title
            ?? "JustTheGays Video";

        var rawDate = DownloaderUtilities.ExtractFirstRawMatch(articleHtml, @"(?is)<time\b[^>]*datetime\s*=\s*['""]([^'""]+)['""]")
            ?? DownloaderUtilities.ExtractMetaContent(html, "article:published_time")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:updated_time");
        var date = ScraperUtilities.NormalizeIsoDate(rawDate) ?? fallbackInput?.Date;

        var rawDetails = DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)<div\b[^>]*class\s*=\s*['""][^'""]*\b(?:post-content|entry-content)\b[^'""]*['""][^>]*>.*?<p>(.*?)</p>")
            ?? DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)<div\b[^>]*class\s*=\s*['""][^'""]*\b(?:post-content|entry-content)\b[^'""]*['""][^>]*>(.*?)</div>")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:description")
            ?? DownloaderUtilities.ExtractMetaContent(html, "description")
            ?? fallbackInput?.Details;

        var performers = new List<string>();
        foreach (Match match in PerformerLinkRegex.Matches(articleHtml))
            performers.Add(match.Groups["name"].Value);

        var tags = new List<string>();
        foreach (Match match in CategoryLinkRegex.Matches(articleHtml))
            tags.Add(match.Groups["name"].Value);

        if (performers.Count == 0 && !string.IsNullOrWhiteSpace(rawDetails))
            performers.AddRange(InferComPerformersFromDetails(rawDetails));

        if (tags.Count == 0 && !string.IsNullOrWhiteSpace(rawDetails))
            tags.AddRange(InferComCategoriesFromDetails(rawDetails));

        var cleanedDetails = CleanComDetails(rawDetails, title);
        var imageUrl = DownloaderUtilities.ExtractMetaContent(html, "og:image")
            ?? DownloaderUtilities.ExtractMetaContent(html, "twitter:image");

        var code = ExtractComCode(pageUrl, html) ?? fallbackInput?.Code;

        return new ScrapedVideoDto
        {
            SourceScraperId = VideoScraperId,
            Title = title,
            Code = code,
            Date = date,
            Details = cleanedDetails,
            ImageUrl = imageUrl,
            StudioName = DefaultStudioName,
            PerformerNames = ScraperUtilities.DeduplicateStrings(performers),
            TagNames = ScraperUtilities.DeduplicateStrings(tags),
            Urls = [pageUrl]
        };
    }

    internal static ScrapedVideoDto ParseVideoWpJson(string json, string fallbackPageUrl, VideoScrapeInput? fallbackInput = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0)
                throw new InvalidOperationException($"Empty WordPress video response for {fallbackPageUrl}");
            root = root[0];
        }

        var pageUrl = TryGetJsonString(root, "link") ?? fallbackPageUrl;
        var rawTitle = TryGetRenderedText(root, "title");
        var title = CleanComTitle(rawTitle)
            ?? fallbackInput?.Title
            ?? "JustTheGays Video";

        var date = ScraperUtilities.NormalizeIsoDate(TryGetJsonString(root, "date") ?? TryGetJsonString(root, "date_gmt"))
            ?? fallbackInput?.Date;

        var rawContent = TryGetRenderedText(root, "content")
            ?? TryGetRenderedText(root, "excerpt")
            ?? fallbackInput?.Details;

        var performers = new List<string>();
        var tags = new List<string>();

        if (root.TryGetProperty("class_list", out var classList) && classList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in classList.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                var cls = item.GetString() ?? string.Empty;
                if (cls.StartsWith("categories-", StringComparison.OrdinalIgnoreCase))
                    tags.Add(FormatComTaxonomySlug(cls["categories-".Length..]));
                else if (cls.StartsWith("video_tag-", StringComparison.OrdinalIgnoreCase))
                    performers.Add(FormatComTaxonomySlug(cls["video_tag-".Length..]));
            }
        }

        if (!string.IsNullOrWhiteSpace(rawContent))
        {
            var plainContent = ScraperUtilities.CleanText(rawContent) ?? string.Empty;
            if (performers.Count == 0)
                performers.AddRange(InferComPerformersFromDetails(plainContent));
            if (tags.Count == 0)
                tags.AddRange(InferComCategoriesFromDetails(plainContent));
        }

        var cleanedDetails = CleanComDetails(rawContent, title);

        string? code = null;
        if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
            code = idProp.GetInt64().ToString(CultureInfo.InvariantCulture);
        code ??= ExtractComCode(pageUrl, null) ?? fallbackInput?.Code;

        return new ScrapedVideoDto
        {
            SourceScraperId = VideoScraperId,
            Title = title,
            Code = code,
            Date = date,
            Details = cleanedDetails,
            StudioName = DefaultStudioName,
            PerformerNames = ScraperUtilities.DeduplicateStrings(performers),
            TagNames = ScraperUtilities.DeduplicateStrings(tags),
            Urls = [pageUrl]
        };
    }

    internal static IReadOnlyList<ScrapedVideoDto> ParseSearchApiJson(string json)
    {
        var results = new List<ScrapedVideoDto>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var rawTitle = TryGetJsonString(item, "title");
            var title = CleanComTitle(rawTitle);
            var url = TryGetJsonString(item, "url");

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url) || !IsSupportedVideoUrl(url))
                continue;

            string? code = null;
            if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                code = idProp.GetInt64().ToString(CultureInfo.InvariantCulture);
            code ??= ExtractComCode(url, null);

            results.Add(new ScrapedVideoDto
            {
                SourceScraperId = VideoScraperId,
                Title = title,
                Code = code,
                StudioName = DefaultStudioName,
                Urls = [url]
            });
        }

        return results;
    }

    internal static IReadOnlyList<ScrapedVideoDto> ParseSearchHtml(string html, string baseSearchUrl)
    {
        var results = new List<ScrapedVideoDto>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(html, @"(?is)<a\b[^>]*href\s*=\s*['""](?<href>https?://(?:www\.)?justthegays\.com/video/[^'""]+)['""][^>]*>(?<inner>.*?)</a>"))
        {
            var href = match.Groups["href"].Value.Trim();
            if (!IsSupportedVideoUrl(href) || !seenUrls.Add(href))
                continue;

            var title = CleanComTitle(ScraperUtilities.CleanText(match.Groups["inner"].Value));
            if (string.IsNullOrWhiteSpace(title))
                continue;

            results.Add(new ScrapedVideoDto
            {
                SourceScraperId = VideoScraperId,
                Title = title,
                Code = ExtractComCode(href, null),
                StudioName = DefaultStudioName,
                Urls = [href]
            });
        }

        return results;
    }

    internal static ScrapedPerformerDto ParsePerformerHtml(string html, string pageUrl, PerformerScrapeInput? fallbackInput = null)
    {
        var rawTitle = DownloaderUtilities.ExtractFirstMatch(html, @"(?is)<h1[^>]*>(.*?)</h1>")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:title")
            ?? DownloaderUtilities.ExtractHtmlTitle(html);

        var name = ScraperUtilities.CleanText(rawTitle);
        if (!string.IsNullOrWhiteSpace(name))
            name = Regex.Replace(name, @"\s*-\s*JustTheGays(?:\.com)?\b.*$", string.Empty, RegexOptions.IgnoreCase).Trim();

        name = ScraperUtilities.CleanText(name) ?? fallbackInput?.Name ?? "Unknown";

        var imageUrl = DownloaderUtilities.ExtractMetaContent(html, "og:image");
        if (IsGenericComLogo(imageUrl))
            imageUrl = null;

        var description = DownloaderUtilities.ExtractMetaContent(html, "og:description")
            ?? fallbackInput?.Details;

        return new ScrapedPerformerDto
        {
            SourceScraperId = PerformerScraperId,
            Name = name,
            Gender = "Male",
            Details = CleanComDetails(description, name),
            ImageUrl = imageUrl,
            Urls = [pageUrl]
        };
    }

    private static bool IsGenericComLogo(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return true;

        return Regex.IsMatch(imageUrl, @"/(?:logo\d*|output-removebg-preview|JustTheGays)\.(?:png|jpg|jpeg|webp)(?:\?|$)", RegexOptions.IgnoreCase);
    }

    private static ScrapedVideoDto MergeVideoResults(ScrapedVideoDto primary, ScrapedVideoDto secondary)
    {
        return new ScrapedVideoDto
        {
            SourceScraperId = VideoScraperId,
            Title = !string.IsNullOrWhiteSpace(primary.Title) && primary.Title != "JustTheGays Video" ? primary.Title : secondary.Title,
            Code = primary.Code ?? secondary.Code,
            Date = primary.Date ?? secondary.Date,
            Details = !string.IsNullOrWhiteSpace(primary.Details) ? primary.Details : secondary.Details,
            ImageUrl = primary.ImageUrl ?? secondary.ImageUrl,
            StudioName = DefaultStudioName,
            PerformerNames = primary.PerformerNames.Count > 0 ? primary.PerformerNames : secondary.PerformerNames,
            TagNames = primary.TagNames.Count > 0 ? primary.TagNames : secondary.TagNames,
            Urls = primary.Urls.Count > 0 ? primary.Urls : secondary.Urls
        };
    }

    private static string ExtractPrimaryArticleHtml(string html)
    {
        var match = Regex.Match(html, @"(?is)<article\b[^>]*>.*?</article>");
        return match.Success ? match.Value : html;
    }

    public static string CleanSearchQuery(string? rawQuery)
    {
        var cleaned = ScraperUtilities.CleanText(rawQuery);
        if (string.IsNullOrWhiteSpace(cleaned))
            return string.Empty;

        // Strip from ' - JustTheGays.com' and all following text to the end
        cleaned = Regex.Replace(
            cleaned,
            @"\s*[-–—]\s*Just\s*The\s*Gays(?:\.com)?\b.*$",
            string.Empty,
            RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(
            cleaned,
            @"\s*\|\s*Just\s*The\s*Gays(?:\.com)?\b.*$",
            string.Empty,
            RegexOptions.IgnoreCase);

        // Strip trailing common video media extensions if present
        cleaned = Regex.Replace(
            cleaned,
            @"\.(?:mp4|m4v|mkv|mov|avi|wmv|webm|flv)$",
            string.Empty,
            RegexOptions.IgnoreCase);

        // Strip any residual trailing dash or punctuation left after stripping
        cleaned = Regex.Replace(cleaned, @"\s*[-–—]\s*$", string.Empty);

        return ScraperUtilities.CleanText(cleaned) ?? string.Empty;
    }

    private static string? CleanComTitle(string? rawTitle)
    {
        var cleaned = CleanSearchQuery(rawTitle);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static string? CleanComDetails(string? rawDetails, string? title)
    {
        var text = ScraperUtilities.CleanText(rawDetails);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (!string.IsNullOrWhiteSpace(title))
        {
            var escapedTitle = Regex.Escape(title);
            text = Regex.Replace(
                text,
                $@"^{escapedTitle}\s*[–-]\s*JustTheGays(?:\.com)?\s*(?:\|\s*Just The Gays\s*)?[–-]\s*",
                string.Empty,
                RegexOptions.IgnoreCase);
        }

        text = Regex.Replace(
            text,
            @"\s*(?:-\s*JustTheGays\s*-\s*)?Jerk to (?:hot gay porn|the hottest and best gay porn).*$",
            string.Empty,
            RegexOptions.IgnoreCase);
        text = Regex.Replace(
            text,
            @"\s*(?:Thousands|Endless) hours of .*?$",
            string.Empty,
            RegexOptions.IgnoreCase);
        text = Regex.Replace(
            text,
            @"\s*Stream the hottest and newest gay porn videos for free.*$",
            string.Empty,
            RegexOptions.IgnoreCase);

        return ScraperUtilities.CleanText(text);
    }

    private static IReadOnlyList<string> InferComPerformersFromDetails(string details)
    {
        var match = Regex.Match(
            details,
            @"\bJerk to hot gay porn from (?<names>.*?)(?:\.| Thousands of hours)",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            return [];

        var parts = Regex.Split(match.Groups["names"].Value, @",|&|\band\b", RegexOptions.IgnoreCase);
        return ScraperUtilities.DeduplicateStrings(parts);
    }

    private static IReadOnlyList<string> InferComCategoriesFromDetails(string details)
    {
        var match = Regex.Match(
            details,
            @"Thousands of hours of amateur (?<cats>.*?) videos",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            return [];

        return ScraperUtilities.DeduplicateStrings(match.Groups["cats"].Value.Split(','));
    }

    private static string FormatComTaxonomySlug(string slug)
    {
        var withoutNumericSuffix = Regex.Replace(slug, @"-\d{4,}$", string.Empty);
        var words = withoutNumericSuffix.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(string.Join(' ', words));
    }

    private static string? ExtractComCode(string pageUrl, string? html)
    {
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
        {
            var pathMatch = VideoPathRegex.Match(uri.AbsolutePath);
            if (pathMatch.Success)
            {
                var slug = pathMatch.Groups["slug"].Value;
                var leadingId = Regex.Match(slug, @"^(\d+)-");
                if (leadingId.Success)
                    return leadingId.Groups[1].Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(html))
        {
            var postIdMatch = Regex.Match(html, @"\bpost-(?<id>\d+)\b");
            if (postIdMatch.Success)
                return postIdMatch.Groups["id"].Value;

            var apiMatch = WpJsonLinkRegex.Match(html);
            if (apiMatch.Success)
            {
                var idMatch = Regex.Match(apiMatch.Groups["href"].Value, @"/video/(?<id>\d+)");
                if (idMatch.Success)
                    return idMatch.Groups["id"].Value;
            }
        }

        return null;
    }

    private static string? ExtractComWpJsonUrl(string html, string pageUrl)
    {
        var linkMatch = WpJsonLinkRegex.Match(html);
        if (linkMatch.Success)
            return WebUtility.HtmlDecode(linkMatch.Groups["href"].Value).Trim();

        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
        {
            var pathMatch = VideoPathRegex.Match(uri.AbsolutePath);
            if (pathMatch.Success)
            {
                var slug = pathMatch.Groups["slug"].Value;
                return $"https://justthegays.com/wp-json/wp/v2/video?slug={Uri.EscapeDataString(slug)}";
            }
        }

        return null;
    }

    private static string? TryGetJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    private static string? TryGetRenderedText(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Object)
        {
            return TryGetJsonString(prop, "rendered");
        }

        return null;
    }

    private HttpClient CreateHttpClient()
    {
        var factory = _services?.GetService<IHttpClientFactory>();
        return factory != null ? factory.CreateClient(VideoScraperId) : new HttpClient();
    }
}
