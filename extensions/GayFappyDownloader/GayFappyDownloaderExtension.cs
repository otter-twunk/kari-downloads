using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace GayFappyDownloader;

public sealed class GayFappyDownloaderExtension : CoveExtensionBase, IDownloaderProvider
{
    public const string VideoDownloaderId = "karinauts.downloaders.gay-fappy.video";
    public const string GalleryDownloaderId = "karinauts.downloaders.gay-fappy.gallery";
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

    private static readonly Regex VideoPlayLinkRegex = new(
        @"<a\b[^>]*\bclass\s*=\s*['""][^'""]*\bvideo-play\b[^'""]*['""][^>]*\bhref\s*=\s*['""](?<url>[^'""]+\.mp4[^'""]*)['""]" +
        @"|<a\b[^>]*\bhref\s*=\s*['""](?<url>[^'""]+\.mp4[^'""]*)['""][^>]*\bclass\s*=\s*['""][^'""]*\bvideo-play\b[^'""]*['""]" +
        @"|<a\b[^>]*\bhref\s*=\s*['""](?<url>https?://(?:www\.)?gayfappy\.com/f/videos/[^'""]+\.mp4[^'""]*)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VideoSourceRegex = new(
        @"<(?:video|source)\b[^>]*\bsrc\s*=\s*['""](?<url>https?://[^'""\s<>]+\.(?:mp4|m4v|webm)(?:\?[^'""\s<>]*)?)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GalleryThumbRegex = new(
        @"<a\b[^>]*\bhref\s*=\s*['""](?<url>https?://[^'""\s<>]+\.(?:jpg|jpeg|png|webp)(?:\?[^'""\s<>]*)?)['""][^>]*\bdata-fancybox\s*=\s*['""]gallery['""]" +
        @"|<a\b[^>]*\bdata-fancybox\s*=\s*['""]gallery['""][^>]*\bhref\s*=\s*['""](?<url>https?://[^'""\s<>]+\.(?:jpg|jpeg|png|webp)(?:\?[^'""\s<>]*)?)['""]" +
        @"|<a\b[^>]*\bclass\s*=\s*['""][^'""]*\ba_thumb\b[^'""]*['""][^>]*\bhref\s*=\s*['""](?<url>https?://[^'""\s<>]+\.(?:jpg|jpeg|png|webp)(?:\?[^'""\s<>]*)?)['""]",
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

    private static readonly DownloaderDescriptor VideoDescriptor = new(
        VideoDownloaderId,
        "GayFappy Video Downloader",
        DownloaderEntity.Video,
        [
            "https://gayfappy.com/*",
            "https://*.gayfappy.com/*",
            "http://gayfappy.com/*",
            "http://*.gayfappy.com/*"
        ],
        DownloaderCapabilities.InlineMetadata);

    private static readonly DownloaderDescriptor GalleryDescriptor = new(
        GalleryDownloaderId,
        "GayFappy Gallery Downloader",
        DownloaderEntity.Gallery,
        [
            "https://gayfappy.com/*",
            "https://*.gayfappy.com/*",
            "http://gayfappy.com/*",
            "http://*.gayfappy.com/*"
        ],
        DownloaderCapabilities.InlineMetadata);

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _services = services;
        return Task.CompletedTask;
    }

    public IReadOnlyList<DownloaderDescriptor> GetDownloaders() => [VideoDescriptor, GalleryDescriptor];

    public async Task<DownloaderUrlMatch?> MatchAsync(string url, CancellationToken ct)
    {
        var matches = await MatchAllAsync(url, ct);
        return matches.FirstOrDefault();
    }

    public async Task<IReadOnlyList<DownloaderUrlMatch>> MatchAllAsync(string url, CancellationToken ct)
    {
        if (!IsSupportedUrl(url))
            return [];

        var normalizedUrl = NormalizeSupportedUrl(url);
        var pageInfo = await TryInspectPageAsync(normalizedUrl, host: null, ct);

        var list = new List<DownloaderUrlMatch>();

        if (pageInfo != null)
        {
            if (!string.IsNullOrWhiteSpace(pageInfo.VideoMediaUrl))
            {
                list.Add(new DownloaderUrlMatch(
                    VideoDescriptor.Id,
                    pageInfo.PageUrl,
                    QualityOptions: null,
                    Label: pageInfo.Title,
                    SourceUrl: string.Equals(url, pageInfo.PageUrl, StringComparison.OrdinalIgnoreCase) ? null : url));
            }

            if (pageInfo.GalleryImageUrls.Count > 0)
            {
                list.Add(new DownloaderUrlMatch(
                    GalleryDescriptor.Id,
                    pageInfo.PageUrl,
                    QualityOptions: null,
                    Label: $"{pageInfo.Title} (Gallery - {pageInfo.GalleryImageUrls.Count} images)",
                    SourceUrl: string.Equals(url, pageInfo.PageUrl, StringComparison.OrdinalIgnoreCase) ? null : url));
            }
        }
        else
        {
            // Direct media fallback
            if (IsDirectMediaUrl(url))
            {
                list.Add(new DownloaderUrlMatch(
                    VideoDescriptor.Id,
                    normalizedUrl,
                    QualityOptions: null,
                    Label: DownloaderUtilities.DeriveTitleFromUrl(normalizedUrl, DefaultStudioName)));
            }
            else
            {
                // Default match to video descriptor
                list.Add(new DownloaderUrlMatch(
                    VideoDescriptor.Id,
                    normalizedUrl,
                    QualityOptions: null,
                    Label: DownloaderUtilities.DeriveTitleFromUrl(normalizedUrl, DefaultStudioName)));
            }
        }

        return list;
    }

    public async Task<DownloaderResult?> DownloadAsync(
        DownloaderRequest request,
        IDownloaderHost host,
        CancellationToken ct)
    {
        var isVideo = string.Equals(request.DownloaderId, VideoDescriptor.Id, StringComparison.OrdinalIgnoreCase)
            || request.Entity == DownloaderEntity.Video;
        var isGallery = string.Equals(request.DownloaderId, GalleryDescriptor.Id, StringComparison.OrdinalIgnoreCase)
            || request.Entity == DownloaderEntity.Gallery;

        if (!isVideo && !isGallery)
            return null;

        if (!IsSupportedUrl(request.Url))
            throw new InvalidOperationException($"Unsupported URL for GayFappy Downloader: {request.Url}");

        var client = CreateHttpClient(host);
        var normalizedUrl = NormalizeSupportedUrl(request.Url);

        host.ReportProgress(5.0, "Inspecting GayFappy page...");
        var pageInfo = await InspectPageAsync(normalizedUrl, client, ct);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = "https://gayfappy.com/"
        };

        if (isGallery || (pageInfo.GalleryImageUrls.Count > 0 && string.IsNullOrWhiteSpace(pageInfo.VideoMediaUrl)))
        {
            if (pageInfo.GalleryImageUrls.Count == 0)
                throw new InvalidOperationException($"No gallery images found on {request.Url}");

            host.ReportProgress(10.0, $"Downloading {pageInfo.GalleryImageUrls.Count} gallery images...");
            var safeZipBase = DownloaderUtilities.SanitizeFileName(pageInfo.Title, "gayfappy-gallery");
            var zipPath = await DownloaderUtilities.CreateGalleryZipAsync(
                client,
                pageInfo.GalleryImageUrls,
                host,
                safeZipBase,
                ct,
                headers: headers);

            host.ReportProgress(100.0, "Completed gallery download.");
            return new DownloaderResult(
                LocalPath: Path.GetFileName(zipPath),
                OriginalFilename: Path.GetFileName(zipPath),
                InlineGalleryMetadata: new ScrapedGalleryDto
                {
                    Title = pageInfo.Title,
                    Code = pageInfo.Code,
                    Date = pageInfo.Date,
                    Details = pageInfo.Description,
                    ImageUrl = pageInfo.ImageUrl,
                    StudioName = DefaultStudioName,
                    PerformerNames = pageInfo.PerformerNames,
                    TagNames = pageInfo.TagNames,
                    Urls = [request.SourceUrl ?? pageInfo.PageUrl]
                });
        }

        // Video download
        if (string.IsNullOrWhiteSpace(pageInfo.VideoMediaUrl))
            throw new InvalidOperationException($"Could not locate a direct video stream on {request.Url}");

        host.ReportProgress(10.0, "Downloading video stream...");
        var extension = DownloaderUtilities.EnsureExtensionFromUrl(pageInfo.VideoMediaUrl, ".mp4");
        var localFileName = "downloaded" + extension;

        var localPath = await DownloaderUtilities.DownloadToTempFileAsync(
            client,
            pageInfo.VideoMediaUrl,
            host,
            localFileName,
            ct,
            headers: headers);

        var finalExtension = Path.GetExtension(localPath);
        if (string.IsNullOrWhiteSpace(finalExtension))
            finalExtension = ".mp4";

        var originalFileName = DownloaderUtilities.SanitizeFileName(pageInfo.Title, "gayfappy-video") + finalExtension;
        var canonicalSourceUrl = request.SourceUrl ?? pageInfo.PageUrl;

        host.ReportProgress(100.0, "Completed download.");
        return new DownloaderResult(
            LocalPath: Path.GetFileName(localPath),
            OriginalFilename: originalFileName,
            InlineVideoMetadata: new ScrapedVideoDto
            {
                Title = pageInfo.Title,
                Code = pageInfo.Code,
                Date = pageInfo.Date,
                Details = pageInfo.Description,
                ImageUrl = pageInfo.ImageUrl,
                StudioName = DefaultStudioName,
                PerformerNames = pageInfo.PerformerNames,
                TagNames = pageInfo.TagNames,
                Urls = [canonicalSourceUrl]
            });
    }

    public static bool IsSupportedUrl(string? url)
    {
        if (!DownloaderUtilities.IsHttpUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!DownloaderUtilities.IsAnyHost(uri.ToString(), SupportedDomains))
            return false;

        if (IsDirectMediaUrl(url))
            return true;

        if (PostPathRegex.IsMatch(uri.AbsolutePath) || WpJsonPostRegex.IsMatch(uri.AbsolutePath))
            return true;

        return PostQueryRegex.IsMatch(uri.Query);
    }

    public static bool IsDirectMediaUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!DownloaderUtilities.IsAnyHost(uri.ToString(), SupportedDomains))
            return false;

        var path = uri.AbsolutePath;
        return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/f/videos/", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeSupportedUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url;

        if (IsDirectMediaUrl(url))
            return uri.ToString();

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

    internal async Task<ResolvedMediaPage?> TryInspectPageAsync(string url, IDownloaderHost? host, CancellationToken ct)
    {
        try
        {
            return await InspectPageAsync(url, CreateHttpClient(host), ct);
        }
        catch
        {
            return null;
        }
    }

    internal async Task<ResolvedMediaPage> InspectPageAsync(string url, HttpClient client, CancellationToken ct)
    {
        var uri = new Uri(url);

        if (IsDirectMediaUrl(url))
        {
            var fallbackTitle = DownloaderUtilities.DeriveTitleFromUrl(url, DefaultStudioName);
            return new ResolvedMediaPage(
                PageUrl: url,
                Title: fallbackTitle,
                VideoMediaUrl: url,
                GalleryImageUrls: [],
                Code: null,
                Date: null,
                Description: null,
                ImageUrl: null,
                PerformerNames: [],
                TagNames: []);
        }

        if (WpJsonPostRegex.IsMatch(uri.AbsolutePath))
        {
            var json = await ScraperUtilities.FetchHtmlAsync(client, url, ct);
            return ParseGayFappyWpJson(json, url);
        }

        var html = await ScraperUtilities.FetchHtmlAsync(client, url, ct);
        var parsedFromHtml = ParseGayFappyHtml(html, url);

        if (string.IsNullOrWhiteSpace(parsedFromHtml.VideoMediaUrl) && parsedFromHtml.GalleryImageUrls.Count == 0)
        {
            // Check if there is an alternate WP REST API JSON link to enrich
            var wpJsonUrl = ExtractWpJsonUrl(html, url);
            if (!string.IsNullOrWhiteSpace(wpJsonUrl))
            {
                try
                {
                    var apiJson = await ScraperUtilities.FetchHtmlAsync(client, wpJsonUrl, ct);
                    return MergeHtmlAndWpJson(parsedFromHtml, ParseGayFappyWpJson(apiJson, url));
                }
                catch
                {
                    // Fall back to html result
                }
            }
        }

        return parsedFromHtml;
    }

    internal static ResolvedMediaPage ParseGayFappyHtml(string html, string pageUrl)
    {
        var articleHtml = ExtractPrimaryArticleHtml(html);

        var rawTitle = DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)<h1[^>]*>(.*?)</h1>")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:title")
            ?? DownloaderUtilities.ExtractHtmlTitle(html);

        var title = CleanTitle(rawTitle) ?? DownloaderUtilities.DeriveTitleFromUrl(pageUrl, DefaultStudioName);

        var rawDate = DownloaderUtilities.ExtractFirstRawMatch(articleHtml, @"(?is)<time\b[^>]*datetime\s*=\s*['""]([^'""]+)['""]")
            ?? DownloaderUtilities.ExtractMetaContent(html, "article:published_time")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:updated_time");
        var date = ScraperUtilities.NormalizeIsoDate(rawDate);

        var rawDesc = DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)<p\b[^>]*\bclass\s*=\s*['""][^'""]*\bpost_desc\b[^'""]*['""][^>]*>(.*?)</p>")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:description")
            ?? DownloaderUtilities.ExtractMetaContent(html, "description");
        var description = CleanDetails(rawDesc, title);

        var imageUrl = ExtractThumbnail(html);
        var (performers, tags) = ExtractPerformersAndTagsFromHtml(articleHtml, html);

        var videoUrl = ExtractVideoUrl(articleHtml, pageUrl) ?? ExtractVideoUrl(html, pageUrl);
        var galleryUrls = ExtractGalleryImages(articleHtml, pageUrl);
        if (galleryUrls.Count == 0)
            galleryUrls = ExtractGalleryImages(html, pageUrl);

        var code = ExtractCode(pageUrl, html);

        return new ResolvedMediaPage(
            PageUrl: pageUrl,
            Title: title,
            VideoMediaUrl: videoUrl,
            GalleryImageUrls: galleryUrls,
            Code: code,
            Date: date,
            Description: description,
            ImageUrl: imageUrl,
            PerformerNames: performers,
            TagNames: tags);
    }

    internal static ResolvedMediaPage ParseGayFappyWpJson(string json, string fallbackPageUrl)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0)
                throw new InvalidOperationException($"Empty WordPress post response for {fallbackPageUrl}");
            root = root[0];
        }

        var pageUrl = TryGetJsonString(root, "link") ?? fallbackPageUrl;
        var rawTitle = TryGetRenderedText(root, "title");
        var title = CleanTitle(rawTitle) ?? DownloaderUtilities.DeriveTitleFromUrl(pageUrl, DefaultStudioName);

        var date = ScraperUtilities.NormalizeIsoDate(
            TryGetJsonString(root, "date") ?? TryGetJsonString(root, "date_gmt"));

        var contentHtml = TryGetRenderedText(root, "content") ?? string.Empty;
        var excerptHtml = TryGetRenderedText(root, "excerpt") ?? string.Empty;

        var rawDesc = DownloaderUtilities.ExtractFirstMatch(contentHtml, @"(?is)<p\b[^>]*\bclass\s*=\s*['""][^'""]*\bpost_desc\b[^'""]*['""][^>]*>(.*?)</p>")
            ?? TryGetYoastDescription(root)
            ?? excerptHtml;
        var description = CleanDetails(rawDesc, title);

        var videoUrl = ExtractVideoUrl(contentHtml, pageUrl);
        var galleryUrls = ExtractGalleryImages(contentHtml, pageUrl);

        var (performers, tags) = ExtractPerformersAndTagsFromWpJson(root);

        var imageUrl = TryGetYoastImage(root);
        if (string.IsNullOrWhiteSpace(imageUrl) && galleryUrls.Count > 0)
            imageUrl = galleryUrls[0];

        string? code = null;
        if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
            code = idProp.GetInt64().ToString(CultureInfo.InvariantCulture);
        code ??= ExtractCode(pageUrl, null);

        return new ResolvedMediaPage(
            PageUrl: pageUrl,
            Title: title,
            VideoMediaUrl: videoUrl,
            GalleryImageUrls: galleryUrls,
            Code: code,
            Date: date,
            Description: description,
            ImageUrl: imageUrl,
            PerformerNames: performers,
            TagNames: tags);
    }

    private static string? ExtractVideoUrl(string html, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var match = VideoPlayLinkRegex.Match(html);
        if (match.Success)
        {
            var raw = match.Groups["url"].Value;
            if (!string.IsNullOrWhiteSpace(raw))
                return ResolveUrl(WebUtility.HtmlDecode(raw).Trim(), pageUrl);
        }

        var srcMatch = VideoSourceRegex.Match(html);
        if (srcMatch.Success)
        {
            var raw = srcMatch.Groups["url"].Value;
            if (!string.IsNullOrWhiteSpace(raw))
                return ResolveUrl(WebUtility.HtmlDecode(raw).Trim(), pageUrl);
        }

        var directMatch = Regex.Match(html, @"https?://(?:www\.)?gayfappy\.com/f/videos/[^'""<>\s]+\.mp4", RegexOptions.IgnoreCase);
        if (directMatch.Success)
            return WebUtility.HtmlDecode(directMatch.Value).Trim();

        return null;
    }

    private static List<string> ExtractGalleryImages(string html, string pageUrl)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(html))
            return list;

        foreach (Match match in GalleryThumbRegex.Matches(html))
        {
            var raw = match.Groups["url"].Value;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var resolved = ResolveUrl(WebUtility.HtmlDecode(raw).Trim(), pageUrl);
                if (!list.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                    list.Add(resolved);
            }
        }

        return list;
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

        // If not found in spans, check article class names
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

        // Check JSON-LD keywords if available
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
                // ignore invalid json-ld block
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

        // Skip tags that look like pure numbers or too short
        if (tag.Length < 3 || tag.All(char.IsDigit))
            return false;

        // Tags with spaces or hyphens usually indicate performer names in gayfappy
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

    private static string ExtractPrimaryArticleHtml(string html)
    {
        var match = Regex.Match(html, @"(?is)<article\b[^>]*>.*?</article>");
        return match.Success ? match.Value : html;
    }

    private static string ResolveUrl(string candidate, string baseUrl)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var abs))
            return abs.ToString();

        if (Uri.TryCreate(new Uri(baseUrl), candidate, out var rel))
            return rel.ToString();

        return candidate;
    }

    private static ResolvedMediaPage MergeHtmlAndWpJson(ResolvedMediaPage htmlPage, ResolvedMediaPage apiPage)
    {
        return new ResolvedMediaPage(
            PageUrl: htmlPage.PageUrl,
            Title: !string.IsNullOrWhiteSpace(apiPage.Title) ? apiPage.Title : htmlPage.Title,
            VideoMediaUrl: !string.IsNullOrWhiteSpace(htmlPage.VideoMediaUrl) ? htmlPage.VideoMediaUrl : apiPage.VideoMediaUrl,
            GalleryImageUrls: htmlPage.GalleryImageUrls.Count > 0 ? htmlPage.GalleryImageUrls : apiPage.GalleryImageUrls,
            Code: htmlPage.Code ?? apiPage.Code,
            Date: htmlPage.Date ?? apiPage.Date,
            Description: !string.IsNullOrWhiteSpace(htmlPage.Description) ? htmlPage.Description : apiPage.Description,
            ImageUrl: htmlPage.ImageUrl ?? apiPage.ImageUrl,
            PerformerNames: htmlPage.PerformerNames.Count > 0 ? htmlPage.PerformerNames : apiPage.PerformerNames,
            TagNames: htmlPage.TagNames.Count > 0 ? htmlPage.TagNames : apiPage.TagNames);
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

    private HttpClient CreateHttpClient(IDownloaderHost? host = null)
    {
        if (host != null)
            return host.HttpClients.CreateClient(VideoDownloaderId);

        var factory = _services?.GetService<IHttpClientFactory>();
        return factory != null ? factory.CreateClient(VideoDownloaderId) : new HttpClient();
    }

    internal sealed record ResolvedMediaPage(
        string PageUrl,
        string Title,
        string? VideoMediaUrl,
        List<string> GalleryImageUrls,
        string? Code,
        string? Date,
        string? Description,
        string? ImageUrl,
        List<string> PerformerNames,
        List<string> TagNames);
}
