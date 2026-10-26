using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace JustTheGaysTvScraper;

public sealed class JustTheGaysTvScraperExtension : CoveExtensionBase, IScraperProvider
{
    public const string VideoScraperId = "karinauts.scrapers.just-the-gays-tv.video";
    public const string PerformerScraperId = "karinauts.scrapers.just-the-gays-tv.performer";
    public const string DefaultStudioName = "JustTheGays";

    private static readonly string[] SupportedDomains =
    [
        "justthegays.tv"
    ];

    private static readonly Regex SceneOrClipPathRegex = new(
        @"^/(?:p\d+/)?(?<kind>video|embed|clip)/(?<slug>[a-zA-Z0-9_-]+)/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PerformerPathRegex = new(
        @"^/performer/(?<slug>[a-zA-Z0-9_-]+)/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TvBootstrapVideoRegex = new(
        @"video:\{(?<video>.*?)\},(?:inQuickies|related):",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TvCategoryLinkRegex = new(
        @"href\s*=\s*['""]/(?:\d+/)?category/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TvPerformerLinkRegex = new(
        @"href\s*=\s*['""]/performer/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private IServiceProvider? _services;

    private static readonly ScraperDescriptor VideoDescriptor = new(
        VideoScraperId,
        "JustTheGays.tv Video Scraper",
        ScraperEntity.Video,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
        ["justthegays.tv/video/", "justthegays.tv/embed/", "justthegays.tv/clip/"]);

    private static readonly ScraperDescriptor PerformerDescriptor = new(
        PerformerScraperId,
        "JustTheGays.tv Performer Scraper",
        ScraperEntity.Performer,
        ScraperCapabilities.ByUrl,
        ["justthegays.tv/performer/"]);

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

        var normalizedUrl = NormalizeVideoUrl(sourceUrl!);
        var html = await ScraperUtilities.FetchHtmlAsync(CreateHttpClient(), normalizedUrl, ct);
        return ParseVideoHtml(html, normalizedUrl, request.Input);
    }

    public async Task<IReadOnlyList<ScrapedVideoDto>> SearchVideosAsync(
        ScraperRequest<string> request,
        CancellationToken ct)
    {
        var query = request.Input?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var searchUrl = $"https://justthegays.tv/search?q={Uri.EscapeDataString(query)}";
        var html = await ScraperUtilities.FetchHtmlAsync(CreateHttpClient(), searchUrl, ct);
        return ParseSearchResultsHtml(html, searchUrl);
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

        return SceneOrClipPathRegex.IsMatch(uri.AbsolutePath);
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

        var match = SceneOrClipPathRegex.Match(uri.AbsolutePath);
        if (match.Success)
        {
            var kind = match.Groups["kind"].Value.ToLowerInvariant();
            var slug = match.Groups["slug"].Value;
            if (kind == "embed")
                return $"https://justthegays.tv/video/{slug}";
            if (kind == "clip")
                return $"https://justthegays.tv/clip/{slug}";
            return $"https://justthegays.tv/video/{slug}";
        }

        return uri.ToString();
    }

    internal static ScrapedVideoDto ParseVideoHtml(string html, string pageUrl, VideoScrapeInput? fallbackInput = null)
    {
        string? title = null;
        string? description = null;
        string? date = null;
        string? imageUrl = null;
        var performers = new List<string>();
        var tags = new List<string>();

        foreach (var doc in DownloaderUtilities.ExtractJsonLdBlocks(html))
        {
            using (doc)
            {
                InspectTvJsonLd(doc.RootElement, ref title, ref description, ref date, ref imageUrl, performers);
            }
        }

        var bootstrapMatch = TvBootstrapVideoRegex.Match(html);
        if (bootstrapMatch.Success)
        {
            var videoBlock = bootstrapMatch.Groups["video"].Value;

            title ??= ExtractJsStringProperty(videoBlock, "jtg_title")
                ?? ExtractJsStringProperty(videoBlock, "title");

            date ??= ScraperUtilities.NormalizeIsoDate(ExtractJsStringProperty(videoBlock, "upload_date"));

            var categoriesMatch = Regex.Match(videoBlock, @"categories\s*:\s*\[(?<items>.*?)\]", RegexOptions.Singleline);
            if (categoriesMatch.Success)
            {
                foreach (Match nameMatch in Regex.Matches(categoriesMatch.Groups["items"].Value, @"name\s*:\s*""(?<val>(?:[^""\\]|\\.)+)"""))
                {
                    tags.Add(Regex.Unescape(nameMatch.Groups["val"].Value));
                }
            }

            var performersMatch = Regex.Match(videoBlock, @"performers\s*:\s*\[(?<items>.*?)\]", RegexOptions.Singleline);
            if (performersMatch.Success)
            {
                foreach (Match nameMatch in Regex.Matches(performersMatch.Groups["items"].Value, @"name\s*:\s*""(?<val>(?:[^""\\]|\\.)+)"""))
                {
                    var name = Regex.Unescape(nameMatch.Groups["val"].Value).Trim();
                    if (!string.IsNullOrWhiteSpace(name) && !name.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                        performers.Add(name);
                }
            }
        }

        foreach (Match catMatch in TvCategoryLinkRegex.Matches(html))
            tags.Add(catMatch.Groups["name"].Value);

        foreach (Match perfMatch in TvPerformerLinkRegex.Matches(html))
            performers.Add(perfMatch.Groups["name"].Value);

        title ??= CleanTvTitle(DownloaderUtilities.ExtractHtmlTitle(html))
            ?? fallbackInput?.Title
            ?? "JustTheGays Video";

        description ??= DownloaderUtilities.ExtractMetaContent(html, "og:description")
            ?? DownloaderUtilities.ExtractMetaContent(html, "description")
            ?? fallbackInput?.Details;
        var cleanedDescription = CleanTvDetails(description);

        imageUrl ??= DownloaderUtilities.ExtractMetaContent(html, "og:image")
            ?? DownloaderUtilities.ExtractMetaContent(html, "twitter:image");
        if (!string.IsNullOrWhiteSpace(imageUrl) && !imageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            imageUrl = $"https://static.jtg.network/thumbnails/{imageUrl.TrimStart('/')}";

        var code = ExtractTvCode(pageUrl) ?? fallbackInput?.Code;

        return new ScrapedVideoDto
        {
            SourceScraperId = VideoScraperId,
            Title = CleanTvTitle(title) ?? "JustTheGays Video",
            Code = code,
            Date = date ?? fallbackInput?.Date,
            Details = cleanedDescription,
            ImageUrl = imageUrl,
            StudioName = DefaultStudioName,
            PerformerNames = ScraperUtilities.DeduplicateStrings(performers),
            TagNames = ScraperUtilities.DeduplicateStrings(tags),
            Urls = [pageUrl]
        };
    }

    internal static IReadOnlyList<ScrapedVideoDto> ParseSearchResultsHtml(string html, string baseSearchUrl)
    {
        var results = new List<ScrapedVideoDto>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First check JSON-LD SearchResultsPage / ItemList
        foreach (var doc in DownloaderUtilities.ExtractJsonLdBlocks(html))
        {
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("mainEntity", out var mainEntity)
                    && mainEntity.TryGetProperty("itemListElement", out var itemList)
                    && itemList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in itemList.EnumerateArray())
                    {
                        if (entry.TryGetProperty("item", out var item))
                        {
                            var name = ScraperUtilities.CleanText(TryGetJsonString(item, "name"));
                            var url = TryGetJsonString(item, "url");
                            if (string.IsNullOrWhiteSpace(name)
                                || name.Equals("null", StringComparison.OrdinalIgnoreCase)
                                || string.IsNullOrWhiteSpace(url)
                                || !IsSupportedVideoUrl(url))
                            {
                                continue;
                            }

                            if (seenUrls.Add(url))
                            {
                                var thumb = TryGetJsonString(item, "thumbnailUrl");
                                if (!string.IsNullOrWhiteSpace(thumb) && !thumb.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                    thumb = $"https://static.jtg.network/thumbnails/{thumb.TrimStart('/')}";

                                results.Add(new ScrapedVideoDto
                                {
                                    SourceScraperId = VideoScraperId,
                                    Title = CleanTvTitle(name) ?? name,
                                    Code = ExtractTvCode(url),
                                    Date = ScraperUtilities.NormalizeIsoDate(TryGetJsonString(item, "uploadDate")),
                                    Details = CleanTvDetails(TryGetJsonString(item, "description")),
                                    ImageUrl = thumb,
                                    StudioName = DefaultStudioName,
                                    Urls = [url]
                                });
                            }
                        }
                    }
                }
            }
        }

        if (results.Count > 0)
            return results;

        // Fallback: parse video cards/links from HTML
        foreach (Match match in Regex.Matches(html, @"(?is)<a\b[^>]*href\s*=\s*['""](?<href>/(?:video|clip)/[^'""]+)['""][^>]*>(?<inner>.*?)</a>"))
        {
            var href = match.Groups["href"].Value.Trim();
            var fullUrl = $"https://justthegays.tv{href}";
            if (!IsSupportedVideoUrl(fullUrl) || !seenUrls.Add(fullUrl))
                continue;

            var title = CleanTvTitle(ScraperUtilities.CleanText(match.Groups["inner"].Value));
            if (string.IsNullOrWhiteSpace(title))
                continue;

            results.Add(new ScrapedVideoDto
            {
                SourceScraperId = VideoScraperId,
                Title = title,
                Code = ExtractTvCode(fullUrl),
                StudioName = DefaultStudioName,
                Urls = [fullUrl]
            });
        }

        return results;
    }

    internal static ScrapedPerformerDto ParsePerformerHtml(string html, string pageUrl, PerformerScrapeInput? fallbackInput = null)
    {
        string? name = null;
        string? imageUrl = null;
        string? description = null;

        foreach (var doc in DownloaderUtilities.ExtractJsonLdBlocks(html))
        {
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && string.Equals(TryGetJsonString(root, "@type"), "Person", StringComparison.OrdinalIgnoreCase))
                {
                    name ??= TryGetJsonString(root, "name");
                    description ??= TryGetJsonString(root, "description");
                    imageUrl ??= TryGetJsonString(root, "image");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var rawTitle = DownloaderUtilities.ExtractHtmlTitle(html);
            if (!string.IsNullOrWhiteSpace(rawTitle))
                name = Regex.Replace(rawTitle, @"\s+videos\s*-\s*.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        }

        name = ScraperUtilities.CleanText(name) ?? fallbackInput?.Name ?? "Unknown";

        imageUrl ??= DownloaderUtilities.ExtractMetaContent(html, "og:image");
        if (!string.IsNullOrWhiteSpace(imageUrl) && !imageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            imageUrl = $"https://static.jtg.network/thumbnails/{imageUrl.TrimStart('/')}";

        description ??= DownloaderUtilities.ExtractMetaContent(html, "og:description")
            ?? fallbackInput?.Details;

        return new ScrapedPerformerDto
        {
            SourceScraperId = PerformerScraperId,
            Name = name,
            Gender = "Male",
            Details = CleanTvDetails(description),
            ImageUrl = imageUrl,
            Urls = [pageUrl]
        };
    }

    private static void InspectTvJsonLd(
        JsonElement element,
        ref string? title,
        ref string? description,
        ref string? date,
        ref string? imageUrl,
        List<string> performers)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                InspectTvJsonLd(item, ref title, ref description, ref date, ref imageUrl, performers);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        var type = TryGetJsonString(element, "@type");
        if (!string.Equals(type, "VideoObject", StringComparison.OrdinalIgnoreCase))
            return;

        title ??= TryGetJsonString(element, "name");
        description ??= TryGetJsonString(element, "description");
        date ??= ScraperUtilities.NormalizeIsoDate(TryGetJsonString(element, "uploadDate") ?? TryGetJsonString(element, "datePublished"));

        if (imageUrl == null && element.TryGetProperty("thumbnailUrl", out var thumbProp))
        {
            if (thumbProp.ValueKind == JsonValueKind.String)
                imageUrl = thumbProp.GetString();
            else if (thumbProp.ValueKind == JsonValueKind.Array)
                imageUrl = thumbProp.EnumerateArray().FirstOrDefault(x => x.ValueKind == JsonValueKind.String).GetString();
        }

        if (element.TryGetProperty("actor", out var actorProp))
        {
            if (actorProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var actor in actorProp.EnumerateArray())
                {
                    var actorName = actor.ValueKind == JsonValueKind.String
                        ? actor.GetString()
                        : TryGetJsonString(actor, "name");
                    if (!string.IsNullOrWhiteSpace(actorName))
                        performers.Add(actorName);
                }
            }
            else if (actorProp.ValueKind == JsonValueKind.Object)
            {
                var actorName = TryGetJsonString(actorProp, "name");
                if (!string.IsNullOrWhiteSpace(actorName))
                    performers.Add(actorName);
            }
        }
    }

    private static string? CleanTvTitle(string? rawTitle)
    {
        var cleaned = ScraperUtilities.CleanText(rawTitle);
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        cleaned = Regex.Replace(cleaned, @"\s*\|\s*JustTheGays.*$", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*-\s*(?:gay porn|stream amateur gay porn|[^-|]*\bPorn\b).*$", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*-\s*JustTheGays(?:\.tv)?\s*$", string.Empty, RegexOptions.IgnoreCase);
        return ScraperUtilities.CleanText(cleaned);
    }

    private static string? CleanTvDetails(string? rawDetails)
    {
        var text = ScraperUtilities.CleanText(rawDetails);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = Regex.Replace(text, @"\s+Find it here on JustTheGays.*$", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"^Get off while streaming .*? in the (?:steamy|erotic|hot) new video [""“].*?[""”]\.?\s*", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(?:^|\s+)This video has plenty of (?:amazing|exciting|mind-blowing) .*? action that will .*?\.(?:\s+|$)", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s*Stream '.*?' and more hot .*?$", string.Empty, RegexOptions.IgnoreCase);
        return ScraperUtilities.CleanText(text);
    }

    private static string? ExtractTvCode(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            return null;

        var match = SceneOrClipPathRegex.Match(uri.AbsolutePath);
        if (!match.Success)
            return null;

        var kind = match.Groups["kind"].Value;
        var slug = match.Groups["slug"].Value;
        if (string.Equals(kind, "clip", StringComparison.OrdinalIgnoreCase))
            return slug;

        var suffixMatch = Regex.Match(slug, @"-([a-z0-9]{2,8})$", RegexOptions.IgnoreCase);
        return suffixMatch.Success ? suffixMatch.Groups[1].Value : slug;
    }

    private static string? ExtractJsStringProperty(string jsObjectText, string propertyName)
    {
        var match = Regex.Match(
            jsObjectText,
            $@"\b{Regex.Escape(propertyName)}\s*:\s*""(?<val>(?:[^""\\]|\\.)*)""");
        return match.Success ? ScraperUtilities.CleanText(Regex.Unescape(match.Groups["val"].Value)) : null;
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

    private HttpClient CreateHttpClient()
    {
        var factory = _services?.GetService<IHttpClientFactory>();
        return factory != null ? factory.CreateClient(VideoScraperId) : new HttpClient();
    }
}
