using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace JustTheGaysComDownloader;

public sealed class JustTheGaysComDownloaderExtension : CoveExtensionBase, IDownloaderProvider
{
    public const string PrimaryDownloaderId = "karinauts.downloaders.just-the-gays-com.video";
    public const string DefaultStudioName = "Just The Gays";

    private static readonly string[] SupportedDomains =
    [
        "justthegays.com",
        "justthegays.hr1alfri.xyz"
    ];

    private static readonly Regex VideoPathRegex = new(
        @"^/video/(?<slug>[a-zA-Z0-9_-]+)(?:/embed)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WpJsonVideoRegex = new(
        @"^/wp-json/wp/v2/video/(?<id>\d+)/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VideopackUrlRegex = new(
        @"\[videopack\]\s*(?<url>https?://[^\s\[\]""'<>]+)\s*\[/videopack\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlSourceTagRegex = new(
        @"<(?:video|source)\b[^>]*\bsrc\s*=\s*['""](?<url>https?://[^'""\s<>]+\.(?:mp4|m4v|webm)(?:\?[^'""\s<>]*)?)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PerformerLinkRegex = new(
        @"(?is)<a\b[^>]*href\s*=\s*['""]https?://(?:www\.)?justthegays\.com/performers/[^'""]+['""][^>]*\brel\s*=\s*['""]tag['""][^>]*>(?<name>.*?)</a>" +
        @"|(?is)<a\b[^>]*\brel\s*=\s*['""]tag['""][^>]*href\s*=\s*['""]https?://(?:www\.)?justthegays\.com/performers/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.Compiled);

    private static readonly Regex CategoryLinkRegex = new(
        @"(?is)<a\b[^>]*href\s*=\s*['""]https?://(?:www\.)?justthegays\.com/categories/[^'""]+['""][^>]*\brel\s*=\s*['""]tag['""][^>]*>(?<name>.*?)</a>" +
        @"|(?is)<a\b[^>]*\brel\s*=\s*['""]tag['""][^>]*href\s*=\s*['""]https?://(?:www\.)?justthegays\.com/categories/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.Compiled);

    private static readonly Regex WpJsonLinkRegex = new(
        @"<link\b[^>]+rel\s*=\s*['""]alternate['""][^>]+type\s*=\s*['""]application/json['""][^>]+href\s*=\s*['""](?<href>https?://[^'""]+/wp-json/wp/v2/video/\d+)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private IServiceProvider? _services;

    private static readonly DownloaderDescriptor PrimaryDescriptor = new(
        PrimaryDownloaderId,
        "JustTheGays.com Video Downloader",
        DownloaderEntity.Video,
        [
            "https://justthegays.com/*",
            "https://*.justthegays.com/*",
            "http://justthegays.com/*",
            "http://*.justthegays.com/*",
            "https://justthegays.hr1alfri.xyz/*"
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

    public IReadOnlyList<DownloaderDescriptor> GetDownloaders() => [PrimaryDescriptor];

    public async Task<DownloaderUrlMatch?> MatchAsync(string url, CancellationToken ct)
    {
        if (!IsSupportedUrl(url))
            return null;

        var normalizedUrl = NormalizeSupportedUrl(url);
        var pageInfo = await TryInspectPageAsync(normalizedUrl, host: null, ct);

        return new DownloaderUrlMatch(
            PrimaryDescriptor.Id,
            pageInfo?.PageUrl ?? normalizedUrl,
            QualityOptions: null,
            Label: pageInfo?.Title ?? DownloaderUtilities.DeriveTitleFromUrl(normalizedUrl, "JustTheGays.com Video"),
            SourceUrl: string.Equals(url, pageInfo?.PageUrl ?? normalizedUrl, StringComparison.OrdinalIgnoreCase) ? null : url);
    }

    public async Task<DownloaderResult?> DownloadAsync(
        DownloaderRequest request,
        IDownloaderHost host,
        CancellationToken ct)
    {
        if (!string.Equals(request.DownloaderId, PrimaryDescriptor.Id, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!IsSupportedUrl(request.Url))
            throw new InvalidOperationException($"Unsupported URL for JustTheGays.com Downloader: {request.Url}");

        host.ReportProgress(5.0, "Inspecting JustTheGays.com media page...");
        var client = CreateHttpClient(host);
        var normalizedUrl = NormalizeSupportedUrl(request.Url);
        var pageInfo = await InspectPageAsync(normalizedUrl, client, ct);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = "https://justthegays.com/"
        };

        var extension = DownloaderUtilities.EnsureExtensionFromUrl(pageInfo.MediaUrl, ".mp4");
        var localFileName = "downloaded" + extension;
        var localPath = await DownloaderUtilities.DownloadToTempFileAsync(
            client,
            pageInfo.MediaUrl,
            host,
            localFileName,
            ct,
            headers: headers);

        var finalExtension = Path.GetExtension(localPath);
        if (string.IsNullOrWhiteSpace(finalExtension))
            finalExtension = ".mp4";

        var originalFileName = DownloaderUtilities.SanitizeFileName(pageInfo.Title, "justthegays-com-video") + finalExtension;
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

        if (DownloaderUtilities.IsHost(uri, "justthegays.hr1alfri.xyz"))
        {
            return uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
        }

        if (DownloaderUtilities.IsHost(uri, "justthegays.com"))
        {
            if (VideoPathRegex.IsMatch(uri.AbsolutePath) || WpJsonVideoRegex.IsMatch(uri.AbsolutePath))
                return true;

            if (uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                return true;

            var query = uri.Query;
            return Regex.IsMatch(query, @"(?:^\?|&)(?:p|video_id)=\d+\b", RegexOptions.IgnoreCase);
        }

        return false;
    }

    public static string NormalizeSupportedUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url;

        if (DownloaderUtilities.IsHost(uri, "justthegays.com"))
        {
            var match = VideoPathRegex.Match(uri.AbsolutePath);
            if (match.Success)
            {
                var slug = match.Groups["slug"].Value;
                return $"https://justthegays.com/video/{slug}/";
            }
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

        if (DownloaderUtilities.IsHost(uri, "justthegays.hr1alfri.xyz")
            || uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            var fallbackTitle = DownloaderUtilities.DeriveTitleFromUrl(url, DefaultStudioName);
            return new ResolvedMediaPage(
                PageUrl: url,
                Title: fallbackTitle,
                MediaUrl: url,
                Code: null,
                Date: null,
                Description: null,
                ImageUrl: null,
                PerformerNames: [],
                TagNames: []);
        }

        if (WpJsonVideoRegex.IsMatch(uri.AbsolutePath))
        {
            var json = await ScraperUtilities.FetchHtmlAsync(client, url, ct);
            return ParseJustTheGaysComWpJson(json, url);
        }

        var comHtml = await ScraperUtilities.FetchHtmlAsync(client, url, ct);
        if (Is404Html(comHtml))
        {
            var searchTerms = ExtractSearchTermsFromUrl(url);
            if (!string.IsNullOrWhiteSpace(searchTerms))
            {
                var searchApiUrl = $"https://justthegays.com/wp-json/wp/v2/search?subtype=video&per_page=5&search={Uri.EscapeDataString(searchTerms)}";
                try
                {
                    var searchJson = await ScraperUtilities.FetchHtmlAsync(client, searchApiUrl, ct);
                    using var doc = JsonDocument.Parse(searchJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    {
                        var resolvedUrl = TryGetJsonString(doc.RootElement[0], "url");
                        if (!string.IsNullOrWhiteSpace(resolvedUrl) && !string.Equals(resolvedUrl, url, StringComparison.OrdinalIgnoreCase))
                        {
                            return await InspectPageAsync(resolvedUrl, client, ct);
                        }
                    }
                }
                catch
                {
                    // Fall through to error
                }
            }

            throw new InvalidOperationException($"The video at {url} has moved or is no longer available (404 Page Not Found).");
        }

        var parsedCom = ParseJustTheGaysComHtml(comHtml, url, allowMissingMedia: true);
        if (!string.IsNullOrWhiteSpace(parsedCom.MediaUrl))
            return parsedCom;

        var wpApiUrl = ExtractComWpJsonUrl(comHtml, url);
        if (!string.IsNullOrWhiteSpace(wpApiUrl))
        {
            var apiJson = await ScraperUtilities.FetchHtmlAsync(client, wpApiUrl, ct);
            return MergeComHtmlAndWpJson(parsedCom, ParseJustTheGaysComWpJson(apiJson, url));
        }

        throw new InvalidOperationException($"Could not locate a media stream on {url}");
    }

    internal static ResolvedMediaPage ParseJustTheGaysComHtml(string html, string pageUrl, bool allowMissingMedia = false)
    {
        var articleHtml = ExtractPrimaryArticleHtml(html);

        var rawTitle = DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)<h1[^>]*>(.*?)</h1>")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:title")
            ?? DownloaderUtilities.ExtractHtmlTitle(html);

        var title = CleanComTitle(rawTitle)
            ?? DownloaderUtilities.DeriveTitleFromUrl(pageUrl, DefaultStudioName);

        var rawDate = DownloaderUtilities.ExtractFirstRawMatch(articleHtml, @"(?is)<time\b[^>]*datetime\s*=\s*['""]([^'""]+)['""]")
            ?? DownloaderUtilities.ExtractMetaContent(html, "article:published_time")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:updated_time");
        var date = ScraperUtilities.NormalizeIsoDate(rawDate);

        var rawDetails = DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)<div\b[^>]*class\s*=\s*['""][^'""]*\b(?:post-content|entry-content)\b[^'""]*['""][^>]*>.*?<p>(.*?)</p>")
            ?? DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)<div\b[^>]*class\s*=\s*['""][^'""]*\b(?:post-content|entry-content)\b[^'""]*['""][^>]*>(.*?)</div>")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:description")
            ?? DownloaderUtilities.ExtractMetaContent(html, "description");

        var performers = ExtractPerformersFromHtml(articleHtml);

        var tags = new List<string>();
        foreach (Match match in CategoryLinkRegex.Matches(articleHtml))
            tags.Add(match.Groups["name"].Value);

        if (performers.Count == 0 && !string.IsNullOrWhiteSpace(rawDetails))
            performers.AddRange(InferComPerformersFromDetails(rawDetails));

        if (tags.Count == 0 && !string.IsNullOrWhiteSpace(rawDetails))
            tags.AddRange(InferComCategoriesFromDetails(rawDetails));

        var cleanedDetails = CleanComDetails(rawDetails, title);
        var imageUrl = ExtractVideoThumbnail(html);

        var mediaUrl = ExtractGenericMediaUrl(articleHtml, pageUrl)
            ?? ExtractGenericMediaUrl(html, pageUrl);

        if (string.IsNullOrWhiteSpace(mediaUrl) && !allowMissingMedia)
            throw new InvalidOperationException($"Could not locate a direct video stream on {pageUrl}");

        return new ResolvedMediaPage(
            PageUrl: pageUrl,
            Title: title,
            MediaUrl: mediaUrl ?? string.Empty,
            Code: ExtractComCode(pageUrl, html),
            Date: date,
            Description: cleanedDetails,
            ImageUrl: imageUrl,
            PerformerNames: ScraperUtilities.DeduplicateStrings(performers),
            TagNames: ScraperUtilities.DeduplicateStrings(tags));
    }

    internal static ResolvedMediaPage ParseJustTheGaysComWpJson(string json, string fallbackPageUrl)
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
            ?? DownloaderUtilities.DeriveTitleFromUrl(pageUrl, DefaultStudioName);

        var date = ScraperUtilities.NormalizeIsoDate(TryGetJsonString(root, "date") ?? TryGetJsonString(root, "date_gmt"));
        var rawContent = TryGetRenderedText(root, "content") ?? TryGetRenderedText(root, "excerpt");

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

        string? imageUrl = null;
        if (root.TryGetProperty("_embedded", out var embedded) && embedded.ValueKind == JsonValueKind.Object
            && embedded.TryGetProperty("wp:featuredmedia", out var mediaList) && mediaList.ValueKind == JsonValueKind.Array
            && mediaList.GetArrayLength() > 0)
        {
            imageUrl = TryGetJsonString(mediaList[0], "source_url");
        }

        if (string.IsNullOrWhiteSpace(imageUrl) && root.TryGetProperty("yoast_head_json", out var yoast) && yoast.ValueKind == JsonValueKind.Object
            && yoast.TryGetProperty("og_image", out var ogImageList) && ogImageList.ValueKind == JsonValueKind.Array
            && ogImageList.GetArrayLength() > 0)
        {
            imageUrl = TryGetJsonString(ogImageList[0], "url");
        }

        if (IsGenericComLogo(imageUrl))
            imageUrl = null;

        var cleanedDetails = CleanComDetails(rawContent, title);
        var playbacks = TryGetJsonString(root, "playbacks") ?? string.Empty;
        var mediaUrl = ExtractMediaUrlFromPlaybacks(playbacks)
            ?? ExtractGenericMediaUrl(rawContent ?? string.Empty, pageUrl)
            ?? throw new InvalidOperationException($"WordPress video payload did not contain a playback URL for {pageUrl}");

        string? code = null;
        if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
            code = idProp.GetInt64().ToString(CultureInfo.InvariantCulture);
        code ??= ExtractComCode(pageUrl, null);

        return new ResolvedMediaPage(
            PageUrl: pageUrl,
            Title: title,
            MediaUrl: mediaUrl,
            Code: code,
            Date: date,
            Description: cleanedDetails,
            ImageUrl: imageUrl,
            PerformerNames: ScraperUtilities.DeduplicateStrings(performers),
            TagNames: ScraperUtilities.DeduplicateStrings(tags));
    }

    private static string? ExtractGenericMediaUrl(string html, string pageUrl)
    {
        var playbacksUrl = ExtractMediaUrlFromPlaybacks(html);
        if (!string.IsNullOrWhiteSpace(playbacksUrl))
            return playbacksUrl;

        var sourceMatch = HtmlSourceTagRegex.Match(html);
        if (sourceMatch.Success)
            return WebUtility.HtmlDecode(sourceMatch.Groups["url"].Value).Trim();

        foreach (var metaProp in new[] { "og:video:secure_url", "og:video:url", "og:video" })
        {
            var metaVal = DownloaderUtilities.ExtractMetaContent(html, metaProp);
            if (!string.IsNullOrWhiteSpace(metaVal)
                && Regex.IsMatch(metaVal, @"\.(?:mp4|m4v|webm)(?:\?|$)", RegexOptions.IgnoreCase))
            {
                return ResolveUrl(metaVal, pageUrl);
            }
        }

        var rawMp4 = DownloaderUtilities.ExtractFirstRawMatch(
            html,
            @"(https?://[^'""<>\s\\]+\.(?:mp4|m4v)(?:\?[^'""<>\s\\]+)?)");

        return string.IsNullOrWhiteSpace(rawMp4) ? null : WebUtility.HtmlDecode(rawMp4).Trim();
    }

    private static string? ExtractMediaUrlFromPlaybacks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = text.Replace(@"\/", "/");
        var match = VideopackUrlRegex.Match(normalized);
        if (match.Success)
            return WebUtility.HtmlDecode(match.Groups["url"].Value).Trim();

        return null;
    }

    private static string? ExtractComWpJsonUrl(string html, string pageUrl)
    {
        var linkMatch = WpJsonLinkRegex.Match(html);
        if (linkMatch.Success)
        {
            var raw = WebUtility.HtmlDecode(linkMatch.Groups["href"].Value).Trim();
            return AppendEmbedQuery(raw);
        }

        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
        {
            var pathMatch = VideoPathRegex.Match(uri.AbsolutePath);
            if (pathMatch.Success)
            {
                var slug = pathMatch.Groups["slug"].Value;
                return $"https://justthegays.com/wp-json/wp/v2/video?slug={Uri.EscapeDataString(slug)}&_embed=1";
            }
        }

        return null;
    }

    private static string AppendEmbedQuery(string url)
    {
        return url.Contains('?') ? $"{url}&_embed=1" : $"{url}?_embed=1";
    }

    private static ResolvedMediaPage MergeComHtmlAndWpJson(ResolvedMediaPage htmlPage, ResolvedMediaPage apiPage)
    {
        var htmlImage = !IsGenericComLogo(htmlPage.ImageUrl) ? htmlPage.ImageUrl : null;
        var apiImage = !IsGenericComLogo(apiPage.ImageUrl) ? apiPage.ImageUrl : null;

        return new ResolvedMediaPage(
            PageUrl: htmlPage.PageUrl,
            Title: !string.IsNullOrWhiteSpace(apiPage.Title) ? apiPage.Title : htmlPage.Title,
            MediaUrl: !string.IsNullOrWhiteSpace(htmlPage.MediaUrl) ? htmlPage.MediaUrl : apiPage.MediaUrl,
            Code: htmlPage.Code ?? apiPage.Code,
            Date: htmlPage.Date ?? apiPage.Date,
            Description: !string.IsNullOrWhiteSpace(htmlPage.Description) ? htmlPage.Description : apiPage.Description,
            ImageUrl: htmlImage ?? apiImage,
            PerformerNames: htmlPage.PerformerNames.Count > 0 ? htmlPage.PerformerNames : apiPage.PerformerNames,
            TagNames: htmlPage.TagNames.Count > 0 ? htmlPage.TagNames : apiPage.TagNames);
    }

    private static bool IsGenericComLogo(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return true;

        if (imageUrl.Contains("gravatar.com", StringComparison.OrdinalIgnoreCase))
            return true;

        return Regex.IsMatch(
            imageUrl,
            @"/(?:logo[a-zA-Z0-9_-]*|output-removebg-preview[a-zA-Z0-9_-]*|JustTheGays(?:-[a-zA-Z0-9_]+)?|default[a-zA-Z0-9_-]*|avatar[a-zA-Z0-9_-]*)\.(?:png|jpg|jpeg|webp)(?:\?|$)",
            RegexOptions.IgnoreCase);
    }

    private static List<string> ExtractPerformersFromHtml(string html)
    {
        var performers = new List<string>();

        // 1. Look specifically in post-tags or post-terms container if available
        var postTagsMatch = Regex.Match(
            html,
            @"(?is)<div\b[^>]*class\s*=\s*['""][^'""]*\b(?:post-tags|post-terms)\b[^'""]*['""][^>]*>(?<content>.*?)</div>");

        if (postTagsMatch.Success)
        {
            var content = postTagsMatch.Groups["content"].Value;
            foreach (Match match in Regex.Matches(content, @"(?is)<a\b[^>]*href\s*=\s*['""]https?://(?:www\.)?justthegays\.com/performers/[^'""]+['""][^>]*>(?<name>.*?)</a>"))
            {
                var name = ScraperUtilities.CleanText(match.Groups["name"].Value);
                if (!string.IsNullOrWhiteSpace(name))
                    performers.Add(name);
            }

            if (performers.Count > 0)
                return ScraperUtilities.DeduplicateStrings(performers);
        }

        // 2. Fall back to wider search scope requiring rel="tag" and excluding menus
        var searchScope = Regex.Replace(html, @"(?is)<(?:nav|div|aside)\b[^>]*id\s*=\s*['""][^'""]*(?:sidebar|footer|menu)[^'""]*['""][^>]*>.*?</(?:nav|div|aside)>", string.Empty);
        searchScope = Regex.Replace(searchScope, @"(?is)<ul\b[^>]*class\s*=\s*['""][^'""]*\bmenu\b[^'""]*['""][^>]*>.*?</ul>", string.Empty);

        foreach (Match match in PerformerLinkRegex.Matches(searchScope))
        {
            var rawTag = match.Value;
            if (rawTag.Contains("menu-item", StringComparison.OrdinalIgnoreCase)
                || rawTag.Contains("menu-title", StringComparison.OrdinalIgnoreCase)
                || rawTag.Contains("menu-icon", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = ScraperUtilities.CleanText(match.Groups["name"].Value);
            if (!string.IsNullOrWhiteSpace(name))
                performers.Add(name);
        }

        return ScraperUtilities.DeduplicateStrings(performers);
    }

    private static string? ExtractVideoThumbnail(string html)
    {
        // 1. JSON-LD VideoObject.thumbnailUrl or image
        var jsonLdMatches = Regex.Matches(html, @"(?is)<script\b[^>]*type\s*=\s*['""]application/ld\+json['""][^>]*>(.*?)</script>");
        foreach (Match match in jsonLdMatches)
        {
            var json = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(json))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var thumb = FindThumbnailInJsonLd(doc.RootElement);
                if (!string.IsNullOrWhiteSpace(thumb) && !IsGenericComLogo(thumb))
                    return thumb;
            }
            catch
            {
                // Continue checking other JSON-LD blocks
            }
        }

        // 2. OpenGraph / Twitter meta image
        var ogImage = DownloaderUtilities.ExtractMetaContent(html, "og:image");
        if (!string.IsNullOrWhiteSpace(ogImage) && !IsGenericComLogo(ogImage))
            return ogImage;

        var twitterImage = DownloaderUtilities.ExtractMetaContent(html, "twitter:image");
        if (!string.IsNullOrWhiteSpace(twitterImage) && !IsGenericComLogo(twitterImage))
            return twitterImage;

        // 3. HTML5 <video poster="...">
        var poster = DownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<video\b[^>]*\bposter\s*=\s*['""](?<poster>https?://[^'""]+)['""]");
        if (!string.IsNullOrWhiteSpace(poster) && !IsGenericComLogo(poster))
            return poster;

        // 4. WordPress featured image <img ... class="...wp-post-image..." ... src="...">
        var wpPostImage = DownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<img\b[^>]*\bclass\s*=\s*['""][^'""]*\bwp-post-image\b[^'""]*['""][^>]*\bsrc\s*=\s*['""](?<src>https?://[^'""]+)['""]")
            ?? DownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<img\b[^>]*\bsrc\s*=\s*['""](?<src>https?://[^'""]+)['""][^>]*\bclass\s*=\s*['""][^'""]*\bwp-post-image\b[^'""]*['""]");
        if (!string.IsNullOrWhiteSpace(wpPostImage) && !IsGenericComLogo(wpPostImage))
            return wpPostImage;

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

        if (element.TryGetProperty("thumbnailUrl", out var thumbProp))
        {
            if (thumbProp.ValueKind == JsonValueKind.String)
            {
                var str = thumbProp.GetString();
                if (!string.IsNullOrWhiteSpace(str) && !IsGenericComLogo(str))
                    return str;
            }
            else if (thumbProp.ValueKind == JsonValueKind.Array)
            {
                var candidateUrls = new List<string>();
                foreach (var item in thumbProp.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var url = item.GetString();
                        if (!string.IsNullOrWhiteSpace(url) && !IsGenericComLogo(url))
                            candidateUrls.Add(url);
                    }
                }

                var fullSize = candidateUrls.FirstOrDefault(u => !Regex.IsMatch(u, @"-\d+x\d+\.[a-zA-Z0-9]+(?:\?|$)"));
                var candidate = fullSize ?? candidateUrls.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(candidate))
                    return candidate;
            }
        }

        if (element.TryGetProperty("image", out var imageProp))
        {
            if (imageProp.ValueKind == JsonValueKind.String)
            {
                var str = imageProp.GetString();
                if (!string.IsNullOrWhiteSpace(str) && !IsGenericComLogo(str))
                    return str;
            }
            else if (imageProp.ValueKind == JsonValueKind.Object && imageProp.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
            {
                var str = urlProp.GetString();
                if (!string.IsNullOrWhiteSpace(str) && !IsGenericComLogo(str))
                    return str;
            }
        }

        return null;
    }

    private static string ExtractPrimaryArticleHtml(string html)
    {
        var match = Regex.Match(html, @"(?is)<article\b[^>]*>.*?</article>");
        return match.Success ? match.Value : html;
    }

    private static string? CleanComTitle(string? rawTitle)
    {
        var cleaned = ScraperUtilities.CleanText(rawTitle);
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        cleaned = Regex.Replace(cleaned, @"\s*-\s*JustTheGays(?:\.com)?\b.*$", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*\|\s*Just The Gays\b.*$", string.Empty, RegexOptions.IgnoreCase);
        return ScraperUtilities.CleanText(cleaned);
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
            @"\s*(?:-\s*JustTheGays\s*-\s*)?Jerk to the hottest and best gay porn and sex videos.*$",
            string.Empty,
            RegexOptions.IgnoreCase);
        text = Regex.Replace(
            text,
            @"\s*Stream the hottest and newest gay porn videos for free.*$",
            string.Empty,
            RegexOptions.IgnoreCase);
        text = Regex.Replace(
            text,
            @"\s*Endless hours of gay xxx videos.*$",
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

    private static string? ResolveUrl(string candidate, string baseUrl)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
            return absolute.ToString();
        if (Uri.TryCreate(new Uri(baseUrl), candidate, out var relative))
            return relative.ToString();
        return null;
    }

    public static bool Is404Html(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return true;

        if (html.Contains("/page-404/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Regex.IsMatch(html, @"<body\b[^>]*\bclass\s*=\s*['""][^'""]*\berror404\b", RegexOptions.IgnoreCase))
            return true;

        if (html.Contains("Page Not Found", StringComparison.OrdinalIgnoreCase)
            && (html.Contains("The video you're looking for has moved", StringComparison.OrdinalIgnoreCase)
                || html.Contains("looking for has moved", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    public static string? ExtractSearchTermsFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var pathMatch = VideoPathRegex.Match(uri.AbsolutePath);
        if (!pathMatch.Success)
            return null;

        var slug = pathMatch.Groups["slug"].Value;
        slug = Regex.Replace(slug, @"^\d+-+", string.Empty);
        slug = Regex.Replace(slug, @"-\d+(?:-\d+)*$", string.Empty);

        var terms = slug.Replace('-', ' ').Trim();
        return string.IsNullOrWhiteSpace(terms) ? null : terms;
    }

    private HttpClient CreateHttpClient(IDownloaderHost? host = null)
    {
        if (host != null)
            return host.HttpClients.CreateClient(PrimaryDownloaderId);

        var factory = _services?.GetService<IHttpClientFactory>();
        return factory != null ? factory.CreateClient(PrimaryDownloaderId) : new HttpClient();
    }

    internal sealed record ResolvedMediaPage(
        string PageUrl,
        string Title,
        string MediaUrl,
        string? Code,
        string? Date,
        string? Description,
        string? ImageUrl,
        List<string> PerformerNames,
        List<string> TagNames);
}
