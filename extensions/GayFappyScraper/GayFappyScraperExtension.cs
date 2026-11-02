using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace GayFappyScraper;

public sealed class GayFappyScraperExtension : CoveExtensionBase, IScraperProvider
{
    public const string PrimaryVideoScraperId = "karinauts.scrapers.gay-fappy.video";
    public const string PrimaryGalleryScraperId = "karinauts.scrapers.gay-fappy.gallery";
    public const string DefaultStudioName = "Gay Fappy";

    private static readonly string[] SupportedDomains =
    [
        "gayfappy.com"
    ];

    private static readonly Regex PostPathRegex = new(
        @"^(?:/index\.php)?/(?<id>\d+)(?:/(?<slug>[a-zA-Z0-9_-]+))?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PostQueryRegex = new(
        @"(?:^\?|&)(?:p)=(?<id>\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WpJsonPostRegex = new(
        @"^(?:/index\.php)?/wp-json/wp/v2/posts/(?<id>\d+)/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PostTagLinkRegex = new(
        @"<span\b[^>]*\bclass\s*=\s*['""][^'""]*\bpost-tag\b[^'""]*['""][^>]*>(?<content>.*?)</span>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex PostCategoryLinkRegex = new(
        @"<span\b[^>]*\bclass\s*=\s*['""][^'""]*\bpost-category\b[^'""]*['""][^>]*>(?<content>.*?)</span>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex WpJsonLinkRegex = new(
        @"<link\b[^>]+rel\s*=\s*['""]alternate['""][^>]+type\s*=\s*['""]application/json['""][^>]+href\s*=\s*['""](?<href>https?://[^'""]+/wp-json/wp/v2/posts/\d+)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> GenericCategoryTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "adult", "amateur", "anal", "bareback", "bathhouse", "bear", "big cock", "big dick",
        "blowjob", "bubble butt", "chub", "clip", "clips", "compilation", "costume", "creampie",
        "cum", "cute", "cut cock", "daddy", "dildo", "erotic", "fansly", "free", "gay", "group",
        "guys", "hairy", "hd", "huge cock", "huge dick", "hunk", "interracial", "jerk off",
        "justforfans", "kink", "leak", "leaks", "massage", "masturbation", "men", "muscle",
        "muscular", "onlyfans", "oral", "orgy", "otter", "outdoor", "public", "rough", "sensual",
        "sex", "shaved", "skinny", "slim", "smooth", "solo", "spanking", "straight", "thick",
        "threesome", "toys", "trailer", "twink", "uncut", "uncut cock", "verified-amateurs",
        "video", "videos", "vr", "webcam", "white", "xxx", "young/twink"
    };

    private IServiceProvider? _services;

    private static readonly ScraperDescriptor VideoDescriptor = new(
        PrimaryVideoScraperId,
        "GayFappy Video Scraper",
        ScraperEntity.Video,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
        ["gayfappy.com/"]);

    private static readonly ScraperDescriptor GalleryDescriptor = new(
        PrimaryGalleryScraperId,
        "GayFappy Gallery Scraper",
        ScraperEntity.Gallery,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
        ["gayfappy.com/"]);

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _services = services;
        return Task.CompletedTask;
    }

    public IReadOnlyList<ScraperDescriptor> GetScrapers() => [VideoDescriptor, GalleryDescriptor];

    public async Task<ScrapedVideoDto?> ScrapeVideoAsync(
        ScraperRequest<VideoScrapeInput> request,
        CancellationToken ct)
    {
        var sourceUrl = request.Input.Url ?? request.Input.Urls.FirstOrDefault();
        if (!IsSupportedUrl(sourceUrl))
        {
            var fallbackTitle = CleanSearchQuery(request.Input.Title);
            if (!string.IsNullOrWhiteSpace(fallbackTitle))
            {
                var searchResults = await SearchVideosAsync(
                    new ScraperRequest<string>(PrimaryVideoScraperId, fallbackTitle, request.Permissions),
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
        var normalizedUrl = NormalizeUrl(sourceUrl!);

        if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) && WpJsonPostRegex.IsMatch(uri.AbsolutePath))
        {
            var json = await ScraperUtilities.FetchHtmlAsync(client, normalizedUrl, ct);
            return ParseVideoWpJson(json, normalizedUrl, request.Input);
        }

        var html = await ScraperUtilities.FetchHtmlAsync(client, normalizedUrl, ct);
        var parsedFromHtml = ParseVideoHtml(html, normalizedUrl, request.Input);

        if (parsedFromHtml.PerformerNames.Count == 0 || parsedFromHtml.TagNames.Count == 0)
        {
            var wpJsonUrl = ExtractWpJsonUrl(html, normalizedUrl);
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
                    // Fall back to html result
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
        var searchApiUrl = $"https://gayfappy.com/index.php/wp-json/wp/v2/posts?search={Uri.EscapeDataString(query)}&per_page=20";

        try
        {
            var json = await ScraperUtilities.FetchHtmlAsync(client, searchApiUrl, ct);
            return ParseSearchApiJson(json);
        }
        catch
        {
            var searchHtmlUrl = $"https://gayfappy.com/?s={Uri.EscapeDataString(query)}";
            var html = await ScraperUtilities.FetchHtmlAsync(client, searchHtmlUrl, ct);
            return ParseSearchHtml(html, searchHtmlUrl);
        }
    }

    public async Task<ScrapedGalleryDto?> ScrapeGalleryAsync(
        ScraperRequest<GalleryScrapeInput> request,
        CancellationToken ct)
    {
        var sourceUrl = request.Input.Url ?? request.Input.Urls.FirstOrDefault();
        if (!IsSupportedUrl(sourceUrl))
        {
            var fallbackTitle = CleanSearchQuery(request.Input.Title);
            if (!string.IsNullOrWhiteSpace(fallbackTitle))
            {
                var searchResults = await SearchGalleriesAsync(
                    new ScraperRequest<string>(PrimaryGalleryScraperId, fallbackTitle, request.Permissions),
                    ct);

                var resolvedUrl = searchResults.FirstOrDefault()?.Urls.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(resolvedUrl))
                {
                    return await ScrapeGalleryAsync(
                        new ScraperRequest<GalleryScrapeInput>(
                            request.ScraperId,
                            new GalleryScrapeInput
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
        var normalizedUrl = NormalizeUrl(sourceUrl!);

        if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) && WpJsonPostRegex.IsMatch(uri.AbsolutePath))
        {
            var json = await ScraperUtilities.FetchHtmlAsync(client, normalizedUrl, ct);
            var videoDto = ParseVideoWpJson(json, normalizedUrl, null);
            return new ScrapedGalleryDto
            {
                Title = videoDto.Title,
                Code = videoDto.Code,
                Date = videoDto.Date,
                Details = videoDto.Details,
                ImageUrl = videoDto.ImageUrl,
                StudioName = DefaultStudioName,
                PerformerNames = videoDto.PerformerNames,
                TagNames = videoDto.TagNames,
                Urls = videoDto.Urls
            };
        }

        var html = await ScraperUtilities.FetchHtmlAsync(client, normalizedUrl, ct);
        var parsedVideo = ParseVideoHtml(html, normalizedUrl, null);

        return new ScrapedGalleryDto
        {
            Title = parsedVideo.Title,
            Code = parsedVideo.Code,
            Date = parsedVideo.Date,
            Details = parsedVideo.Details,
            ImageUrl = parsedVideo.ImageUrl,
            StudioName = DefaultStudioName,
            PerformerNames = parsedVideo.PerformerNames,
            TagNames = parsedVideo.TagNames,
            Urls = parsedVideo.Urls
        };
    }

    public async Task<IReadOnlyList<ScrapedGalleryDto>> SearchGalleriesAsync(
        ScraperRequest<string> request,
        CancellationToken ct)
    {
        var videos = await SearchVideosAsync(request, ct);
        return videos.Select(v => new ScrapedGalleryDto
        {
            Title = v.Title,
            Code = v.Code,
            Date = v.Date,
            Details = v.Details,
            ImageUrl = v.ImageUrl,
            StudioName = DefaultStudioName,
            PerformerNames = v.PerformerNames,
            TagNames = v.TagNames,
            Urls = v.Urls
        }).ToList();
    }

    public static bool IsSupportedUrl(string? url)
    {
        if (!DownloaderUtilities.IsHttpUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!DownloaderUtilities.IsAnyHost(uri.ToString(), SupportedDomains))
            return false;

        if (PostPathRegex.IsMatch(uri.AbsolutePath) || WpJsonPostRegex.IsMatch(uri.AbsolutePath))
            return true;

        return PostQueryRegex.IsMatch(uri.Query);
    }

    public static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url;

        var match = PostPathRegex.Match(uri.AbsolutePath);
        if (match.Success)
        {
            var id = match.Groups["id"].Value;
            var slug = match.Groups["slug"].Value;
            return !string.IsNullOrWhiteSpace(slug)
                ? $"https://gayfappy.com/index.php/{id}/{slug}/"
                : $"https://gayfappy.com/index.php/{id}/";
        }

        var qMatch = PostQueryRegex.Match(uri.Query);
        if (qMatch.Success)
        {
            var id = qMatch.Groups["id"].Value;
            return $"https://gayfappy.com/index.php/?p={id}";
        }

        return uri.ToString();
    }

    internal static ScrapedVideoDto ParseVideoHtml(string html, string sourceUrl, VideoScrapeInput? fallbackInput)
    {
        var articleHtml = ExtractPrimaryArticleHtml(html);

        var rawTitle = DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)<h1[^>]*>(.*?)</h1>")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:title")
            ?? DownloaderUtilities.ExtractHtmlTitle(html);

        var title = CleanTitle(rawTitle) ?? fallbackInput?.Title ?? "Untitled";

        var rawDate = DownloaderUtilities.ExtractFirstRawMatch(articleHtml, @"(?is)<time\b[^>]*datetime\s*=\s*['""]([^'""]+)['""]")
            ?? DownloaderUtilities.ExtractMetaContent(html, "article:published_time")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:updated_time");
        var date = ScraperUtilities.NormalizeIsoDate(rawDate) ?? fallbackInput?.Date;

        var rawDesc = DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)<p\b[^>]*\bclass\s*=\s*['""][^'""]*\bpost_desc\b[^'""]*['""][^>]*>(.*?)</p>")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:description")
            ?? DownloaderUtilities.ExtractMetaContent(html, "description");
        var description = CleanDetails(rawDesc, title) ?? fallbackInput?.Details;

        var imageUrl = ExtractThumbnail(html);
        var (performers, tags) = ExtractPerformersAndTagsFromHtml(articleHtml, html);

        var code = ExtractCode(sourceUrl, html) ?? fallbackInput?.Code;

        return new ScrapedVideoDto
        {
            SourceScraperId = PrimaryVideoScraperId,
            Title = title,
            Code = code,
            Date = date,
            Details = description,
            Director = fallbackInput?.Director,
            ImageUrl = imageUrl,
            Urls = [sourceUrl],
            StudioName = DefaultStudioName,
            PerformerNames = performers,
            TagNames = tags
        };
    }

    internal static ScrapedVideoDto ParseVideoWpJson(string json, string sourceUrl, VideoScrapeInput? fallbackInput)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0)
                throw new InvalidOperationException($"Empty WordPress post response for {sourceUrl}");
            root = root[0];
        }

        var pageUrl = TryGetJsonString(root, "link") ?? sourceUrl;
        var rawTitle = TryGetRenderedText(root, "title");
        var title = CleanTitle(rawTitle) ?? fallbackInput?.Title ?? "Untitled";

        var date = ScraperUtilities.NormalizeIsoDate(
            TryGetJsonString(root, "date") ?? TryGetJsonString(root, "date_gmt")) ?? fallbackInput?.Date;

        var contentHtml = TryGetRenderedText(root, "content") ?? string.Empty;
        var excerptHtml = TryGetRenderedText(root, "excerpt") ?? string.Empty;

        var rawDesc = DownloaderUtilities.ExtractFirstMatch(contentHtml, @"(?is)<p\b[^>]*\bclass\s*=\s*['""][^'""]*\bpost_desc\b[^'""]*['""][^>]*>(.*?)</p>")
            ?? TryGetYoastDescription(root)
            ?? excerptHtml;
        var description = CleanDetails(rawDesc, title) ?? fallbackInput?.Details;

        var (performers, tags) = ExtractPerformersAndTagsFromWpJson(root);

        var imageUrl = TryGetYoastImage(root);

        string? code = null;
        if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
            code = idProp.GetInt64().ToString(CultureInfo.InvariantCulture);
        code ??= ExtractCode(pageUrl, null) ?? fallbackInput?.Code;

        return new ScrapedVideoDto
        {
            SourceScraperId = PrimaryVideoScraperId,
            Title = title,
            Code = code,
            Date = date,
            Details = description,
            Director = fallbackInput?.Director,
            ImageUrl = imageUrl,
            Urls = [pageUrl],
            StudioName = DefaultStudioName,
            PerformerNames = performers,
            TagNames = tags
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
            var pageUrl = TryGetJsonString(item, "link");
            if (string.IsNullOrWhiteSpace(pageUrl))
                continue;

            var rawTitle = TryGetRenderedText(item, "title");
            var title = CleanTitle(rawTitle) ?? "Untitled";

            var date = ScraperUtilities.NormalizeIsoDate(
                TryGetJsonString(item, "date") ?? TryGetJsonString(item, "date_gmt"));

            var rawDesc = TryGetYoastDescription(item) ?? TryGetRenderedText(item, "excerpt");
            var details = CleanDetails(rawDesc, title);

            var (performers, tags) = ExtractPerformersAndTagsFromWpJson(item);
            var imageUrl = TryGetYoastImage(item);

            string? code = null;
            if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                code = idProp.GetInt64().ToString(CultureInfo.InvariantCulture);
            code ??= ExtractCode(pageUrl, null);

            results.Add(new ScrapedVideoDto
            {
                SourceScraperId = PrimaryVideoScraperId,
                Title = title,
                Code = code,
                Date = date,
                Details = details,
                ImageUrl = imageUrl,
                Urls = [pageUrl],
                StudioName = DefaultStudioName,
                PerformerNames = performers,
                TagNames = tags
            });
        }

        return results;
    }

    internal static IReadOnlyList<ScrapedVideoDto> ParseSearchHtml(string html, string baseSearchUrl)
    {
        var results = new List<ScrapedVideoDto>();
        var baseUri = new Uri(baseSearchUrl);

        foreach (Match match in Regex.Matches(html, @"(?is)<article\b[^>]*\bid=['""]post-(?<id>\d+)['""][^>]*>.*?</article>"))
        {
            var articleHtml = match.Value;
            var id = match.Groups["id"].Value;

            var linkMatch = Regex.Match(articleHtml, @"(?is)<a\b[^>]*\bhref=['""](?<url>https?://[^'""]+)['""]");
            if (!linkMatch.Success)
                continue;

            var rawUrl = linkMatch.Groups["url"].Value;
            if (!Uri.TryCreate(baseUri, rawUrl, out var fullUri) || !IsSupportedUrl(fullUri.ToString()))
                continue;

            var pageUrl = fullUri.ToString();
            var rawTitle = DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)<h[12][^>]*>(.*?)</h[12]>")
                ?? DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)title=['""]([^'""]+)['""]");
            var title = CleanTitle(rawTitle) ?? DownloaderUtilities.DeriveTitleFromUrl(pageUrl, DefaultStudioName);

            var rawDate = DownloaderUtilities.ExtractFirstRawMatch(articleHtml, @"(?is)<time\b[^>]*datetime\s*=\s*['""]([^'""]+)['""]");
            var date = ScraperUtilities.NormalizeIsoDate(rawDate);

            var (performers, tags) = ExtractPerformersAndTagsFromHtml(articleHtml, articleHtml);

            var imgMatch = Regex.Match(articleHtml, @"(?is)<img\b[^>]*\bsrc=['""](?<src>https?://[^'""]+)['""]");
            var imageUrl = imgMatch.Success ? imgMatch.Groups["src"].Value : null;

            results.Add(new ScrapedVideoDto
            {
                SourceScraperId = PrimaryVideoScraperId,
                Title = title,
                Code = id,
                Date = date,
                ImageUrl = imageUrl,
                Urls = [pageUrl],
                StudioName = DefaultStudioName,
                PerformerNames = performers,
                TagNames = tags
            });
        }

        return results;
    }

    private static (List<string> Performers, List<string> Tags) ExtractPerformersAndTagsFromHtml(string articleHtml, string fullHtml)
    {
        var rawTags = new List<string>();

        var catMatch = PostCategoryLinkRegex.Match(articleHtml);
        if (catMatch.Success)
        {
            foreach (Match m in Regex.Matches(catMatch.Groups["content"].Value, @"(?is)<a\b[^>]*>(.*?)</a>"))
            {
                var text = ScraperUtilities.CleanText(m.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(text))
                    rawTags.Add(text);
            }
        }

        var tagMatch = PostTagLinkRegex.Match(articleHtml);
        if (tagMatch.Success)
        {
            foreach (Match m in Regex.Matches(tagMatch.Groups["content"].Value, @"(?is)<a\b[^>]*>(.*?)</a>"))
            {
                var text = ScraperUtilities.CleanText(m.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(text))
                    rawTags.Add(text);
            }
        }

        var classMatch = Regex.Match(articleHtml, @"class=['""](?<cls>[^'""]+)['""]");
        if (classMatch.Success)
        {
            foreach (var token in classMatch.Groups["cls"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("tag-", StringComparison.OrdinalIgnoreCase))
                    rawTags.Add(FormatTaxonomySlug(token["tag-".Length..]));
                else if (token.StartsWith("category-", StringComparison.OrdinalIgnoreCase))
                    rawTags.Add(FormatTaxonomySlug(token["category-".Length..]));
            }
        }

        var jsonLdMatches = Regex.Matches(fullHtml, @"(?is)<script\b[^>]*type\s*=\s*['""]application/ld\+json['""][^>]*>(.*?)</script>");
        foreach (Match jm in jsonLdMatches)
        {
            try
            {
                using var doc = JsonDocument.Parse(jm.Groups[1].Value);
                ExtractKeywordsFromJsonLd(doc.RootElement, rawTags);
            }
            catch
            {
                // ignore
            }
        }

        var distinctTags = ScraperUtilities.DeduplicateStrings(rawTags);
        var performers = new List<string>();

        foreach (var tag in distinctTags)
        {
            if (!GenericCategoryTags.Contains(tag) && IsLikelyPerformerName(tag))
            {
                performers.Add(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tag));
            }
        }

        return (performers, distinctTags);
    }

    private static (List<string> Performers, List<string> Tags) ExtractPerformersAndTagsFromWpJson(JsonElement root)
    {
        var rawTags = new List<string>();

        if (root.TryGetProperty("class_list", out var classList) && classList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in classList.EnumerateArray())
            {
                var str = item.GetString() ?? string.Empty;
                if (str.StartsWith("tag-", StringComparison.OrdinalIgnoreCase))
                    rawTags.Add(FormatTaxonomySlug(str["tag-".Length..]));
                else if (str.StartsWith("category-", StringComparison.OrdinalIgnoreCase))
                    rawTags.Add(FormatTaxonomySlug(str["category-".Length..]));
            }
        }

        if (root.TryGetProperty("yoast_head_json", out var yoast) && yoast.ValueKind == JsonValueKind.Object)
        {
            if (yoast.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Object)
            {
                ExtractKeywordsFromJsonLd(schema, rawTags);
            }
        }

        var distinctTags = ScraperUtilities.DeduplicateStrings(rawTags);
        var performers = new List<string>();

        foreach (var tag in distinctTags)
        {
            if (!GenericCategoryTags.Contains(tag) && IsLikelyPerformerName(tag))
            {
                performers.Add(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tag));
            }
        }

        return (performers, distinctTags);
    }

    private static void ExtractKeywordsFromJsonLd(JsonElement element, List<string> tags)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ExtractKeywordsFromJsonLd(item, tags);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        if (element.TryGetProperty("@graph", out var graph) && graph.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in graph.EnumerateArray())
                ExtractKeywordsFromJsonLd(item, tags);
        }

        if (element.TryGetProperty("keywords", out var kwProp))
        {
            if (kwProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in kwProp.EnumerateArray())
                {
                    var text = ScraperUtilities.CleanText(item.GetString());
                    if (!string.IsNullOrWhiteSpace(text))
                        tags.Add(text);
                }
            }
            else if (kwProp.ValueKind == JsonValueKind.String)
            {
                var str = kwProp.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    foreach (var part in str.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var text = ScraperUtilities.CleanText(part);
                        if (!string.IsNullOrWhiteSpace(text))
                            tags.Add(text);
                    }
                }
            }
        }

        if (element.TryGetProperty("articleSection", out var secProp) && secProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in secProp.EnumerateArray())
            {
                var text = ScraperUtilities.CleanText(item.GetString());
                if (!string.IsNullOrWhiteSpace(text))
                    tags.Add(text);
            }
        }
    }

    private static bool IsLikelyPerformerName(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        if (tag.Length < 3 || tag.All(char.IsDigit))
            return false;

        return true;
    }

    private static string FormatTaxonomySlug(string slug)
    {
        var parts = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }

    private static string? ExtractThumbnail(string html)
    {
        var ogImage = DownloaderUtilities.ExtractMetaContent(html, "og:image");
        if (!string.IsNullOrWhiteSpace(ogImage))
            return ogImage;

        var twitterImage = DownloaderUtilities.ExtractMetaContent(html, "twitter:image");
        if (!string.IsNullOrWhiteSpace(twitterImage))
            return twitterImage;

        var jsonLdMatches = Regex.Matches(html, @"(?is)<script\b[^>]*type\s*=\s*['""]application/ld\+json['""][^>]*>(.*?)</script>");
        foreach (Match match in jsonLdMatches)
        {
            try
            {
                using var doc = JsonDocument.Parse(match.Groups[1].Value);
                var thumb = FindThumbnailInJsonLd(doc.RootElement);
                if (!string.IsNullOrWhiteSpace(thumb))
                    return thumb;
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static string? FindThumbnailInJsonLd(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindThumbnailInJsonLd(item);
                if (!string.IsNullOrWhiteSpace(found))
                    return found;
            }
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (element.TryGetProperty("@graph", out var graph) && graph.ValueKind == JsonValueKind.Array)
        {
            var found = FindThumbnailInJsonLd(graph);
            if (!string.IsNullOrWhiteSpace(found))
                return found;
        }

        if (element.TryGetProperty("thumbnailUrl", out var thumbProp) && thumbProp.ValueKind == JsonValueKind.String)
            return thumbProp.GetString();

        if (element.TryGetProperty("image", out var imageProp))
        {
            if (imageProp.ValueKind == JsonValueKind.String)
                return imageProp.GetString();
            if (imageProp.ValueKind == JsonValueKind.Object && imageProp.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                return urlProp.GetString();
        }

        return null;
    }

    private static string? ExtractWpJsonUrl(string html, string pageUrl)
    {
        var linkMatch = WpJsonLinkRegex.Match(html);
        if (linkMatch.Success)
            return WebUtility.HtmlDecode(linkMatch.Groups["href"].Value).Trim();

        var id = ExtractCode(pageUrl, html);
        return !string.IsNullOrWhiteSpace(id) ? $"https://gayfappy.com/index.php/wp-json/wp/v2/posts/{id}" : null;
    }

    private static string? ExtractCode(string pageUrl, string? html)
    {
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
        {
            var match = PostPathRegex.Match(uri.AbsolutePath);
            if (match.Success)
                return match.Groups["id"].Value;

            var qMatch = PostQueryRegex.Match(uri.Query);
            if (qMatch.Success)
                return qMatch.Groups["id"].Value;

            var wpMatch = WpJsonPostRegex.Match(uri.AbsolutePath);
            if (wpMatch.Success)
                return wpMatch.Groups["id"].Value;
        }

        if (!string.IsNullOrWhiteSpace(html))
        {
            var idMatch = Regex.Match(html, @"\bpost-(?<id>\d+)\b");
            if (idMatch.Success)
                return idMatch.Groups["id"].Value;
        }

        return null;
    }

    private static string? CleanTitle(string? rawTitle)
    {
        var cleaned = ScraperUtilities.CleanText(rawTitle);
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        cleaned = Regex.Replace(cleaned, @"\s*[-|–]\s*Gay\s*Fappy\b.*$", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*-\s*Free OnlyFans Videos\b.*$", string.Empty, RegexOptions.IgnoreCase);
        return ScraperUtilities.CleanText(cleaned);
    }

    private static string? CleanDetails(string? rawDetails, string? title)
    {
        var text = ScraperUtilities.CleanText(rawDetails);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (!string.IsNullOrWhiteSpace(title))
        {
            var escaped = Regex.Escape(title);
            text = Regex.Replace(text, $@"^{escaped}\s*[-|–]?\s*", string.Empty, RegexOptions.IgnoreCase);
        }

        text = Regex.Replace(text, @"<!--more-->.*$", string.Empty, RegexOptions.IgnoreCase);
        return ScraperUtilities.CleanText(text);
    }

    private static string? CleanSearchQuery(string? query)
    {
        var cleaned = ScraperUtilities.CleanText(query);
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        cleaned = Regex.Replace(cleaned, @"\s*[-|–]\s*Gay\s*Fappy\b.*$", string.Empty, RegexOptions.IgnoreCase);
        return ScraperUtilities.CleanText(cleaned);
    }

    private static string ExtractPrimaryArticleHtml(string html)
    {
        var match = Regex.Match(html, @"(?is)<article\b[^>]*>.*?</article>");
        return match.Success ? match.Value : html;
    }

    private static ScrapedVideoDto MergeVideoResults(ScrapedVideoDto htmlDto, ScrapedVideoDto apiDto)
    {
        return new ScrapedVideoDto
        {
            SourceScraperId = PrimaryVideoScraperId,
            Title = !string.IsNullOrWhiteSpace(apiDto.Title) && apiDto.Title != "Untitled" ? apiDto.Title : htmlDto.Title,
            Code = htmlDto.Code ?? apiDto.Code,
            Date = htmlDto.Date ?? apiDto.Date,
            Details = !string.IsNullOrWhiteSpace(htmlDto.Details) ? htmlDto.Details : apiDto.Details,
            Director = htmlDto.Director ?? apiDto.Director,
            ImageUrl = htmlDto.ImageUrl ?? apiDto.ImageUrl,
            Urls = htmlDto.Urls.Count > 0 ? htmlDto.Urls : apiDto.Urls,
            StudioName = DefaultStudioName,
            PerformerNames = htmlDto.PerformerNames.Count > 0 ? htmlDto.PerformerNames : apiDto.PerformerNames,
            TagNames = htmlDto.TagNames.Count > 0 ? htmlDto.TagNames : apiDto.TagNames
        };
    }

    private static string? TryGetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static string? TryGetRenderedText(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Object)
            return null;

        return prop.TryGetProperty("rendered", out var rendered) && rendered.ValueKind == JsonValueKind.String
            ? rendered.GetString()
            : null;
    }

    private static string? TryGetYoastDescription(JsonElement root)
    {
        if (root.TryGetProperty("yoast_head_json", out var yoast) && yoast.ValueKind == JsonValueKind.Object)
        {
            if (yoast.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
                return desc.GetString();
            if (yoast.TryGetProperty("og_description", out var ogDesc) && ogDesc.ValueKind == JsonValueKind.String)
                return ogDesc.GetString();
        }

        return null;
    }

    private static string? TryGetYoastImage(JsonElement root)
    {
        if (root.TryGetProperty("yoast_head_json", out var yoast) && yoast.ValueKind == JsonValueKind.Object)
        {
            if (yoast.TryGetProperty("og_image", out var ogImages) && ogImages.ValueKind == JsonValueKind.Array && ogImages.GetArrayLength() > 0)
            {
                var first = ogImages[0];
                if (first.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                    return urlProp.GetString();
            }
        }

        return null;
    }

    private HttpClient CreateHttpClient()
    {
        var factory = _services?.GetService<IHttpClientFactory>();
        return factory != null ? factory.CreateClient(PrimaryVideoScraperId) : new HttpClient();
    }
}
