using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JustTheGaysDownloader;

public sealed class JustTheGaysDownloaderExtension : CoveExtensionBase, IDownloaderProvider
{
    public const string PrimaryDownloaderId = "karinauts.downloaders.just-the-gays.video";
    public const string StudioJustTheGaysTv = "JustTheGays";
    public const string StudioJustTheGaysCom = "Just The Gays";

    private static readonly string[] SupportedDomains =
    [
        "justthegays.tv",
        "justthegays.com",
        "justthegays.hr1alfri.xyz",
        "cdn.gff.network"
    ];

    private static readonly Regex TvSceneOrClipPathRegex = new(
        @"^/(?:p\d+/)?(?<kind>video|embed|clip)/(?<slug>[a-zA-Z0-9_-]+)/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ComVideoPathRegex = new(
        @"^/video/(?<slug>[a-zA-Z0-9_-]+)(?:/embed)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ComWpJsonVideoRegex = new(
        @"^/wp-json/wp/v2/video/(?<id>\d+)/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VideopackUrlRegex = new(
        @"\[videopack\]\s*(?<url>https?://[^\s\[\]""'<>]+)\s*\[/videopack\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlSourceTagRegex = new(
        @"<(?:video|source)\b[^>]*\bsrc\s*=\s*['""](?<url>https?://[^'""\s<>]+\.(?:mp4|m4v|webm|m3u8)(?:\?[^'""\s<>]*)?)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TvVideoSrcRegex = new(
        @"videoSrc\s*:\s*""(?<url>https?://[^""\\]+)""",
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

    private static readonly Regex ComPerformerLinkRegex = new(
        @"href\s*=\s*['""]https?://(?:www\.)?justthegays\.com/performers/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ComCategoryLinkRegex = new(
        @"href\s*=\s*['""]https?://(?:www\.)?justthegays\.com/categories/[^'""]+['""][^>]*rel\s*=\s*['""]tag['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ComWpJsonLinkRegex = new(
        @"<link\b[^>]+rel\s*=\s*['""]alternate['""][^>]+type\s*=\s*['""]application/json['""][^>]+href\s*=\s*['""](?<href>https?://[^'""]+/wp-json/wp/v2/video/\d+)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HlsStreamInfRegex = new(
        @"#EXT-X-STREAM-INF:(?<attrs>[^\r\n]+)\r?\n(?<uri>[^\r\n#]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HlsMapUriRegex = new(
        @"#EXT-X-MAP:[^\r\n]*URI=""(?<uri>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private IServiceProvider? _services;
    private IConfiguration? _configuration;

    private static readonly DownloaderDescriptor PrimaryDescriptor = new(
        PrimaryDownloaderId,
        "JustTheGays Video Downloader",
        DownloaderEntity.Video,
        [
            "https://justthegays.tv/*",
            "https://*.justthegays.tv/*",
            "http://justthegays.tv/*",
            "http://*.justthegays.tv/*",
            "https://justthegays.com/*",
            "https://*.justthegays.com/*",
            "http://justthegays.com/*",
            "http://*.justthegays.com/*",
            "https://justthegays.hr1alfri.xyz/*",
            "https://cdn.gff.network/*"
        ],
        DownloaderCapabilities.InlineMetadata | DownloaderCapabilities.MultiQuality);

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
            QualityOptions: pageInfo?.QualityOptions,
            Label: pageInfo?.Title ?? DownloaderUtilities.DeriveTitleFromUrl(normalizedUrl, "JustTheGays Video"),
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
            throw new InvalidOperationException($"Unsupported URL for JustTheGays Downloader: {request.Url}");

        host.ReportProgress(5.0, "Inspecting JustTheGays media page...");
        var client = CreateHttpClient(host);
        var normalizedUrl = NormalizeSupportedUrl(request.Url);
        var pageInfo = await InspectPageAsync(normalizedUrl, client, ct);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = pageInfo.IsTvSite ? "https://justthegays.tv/" : "https://justthegays.com/"
        };

        string localPath;
        if (IsHlsUrl(pageInfo.MediaUrl))
        {
            localPath = await DownloadHlsStreamAsync(
                client,
                pageInfo.MediaUrl,
                request,
                host,
                headers,
                ct);
        }
        else
        {
            var extension = DownloaderUtilities.EnsureExtensionFromUrl(pageInfo.MediaUrl, ".mp4");
            var localFileName = "downloaded" + extension;
            localPath = await DownloaderUtilities.DownloadToTempFileAsync(
                client,
                pageInfo.MediaUrl,
                host,
                localFileName,
                ct,
                headers: headers);
        }

        var finalExtension = Path.GetExtension(localPath);
        if (string.IsNullOrWhiteSpace(finalExtension))
            finalExtension = ".mp4";

        var originalFileName = DownloaderUtilities.SanitizeFileName(pageInfo.Title, "justthegays-video") + finalExtension;
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
                StudioName = pageInfo.StudioName,
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

        if (DownloaderUtilities.IsHost(uri, "justthegays.hr1alfri.xyz") || DownloaderUtilities.IsHost(uri, "cdn.gff.network"))
        {
            return uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
        }

        if (DownloaderUtilities.IsHost(uri, "justthegays.tv"))
            return TvSceneOrClipPathRegex.IsMatch(uri.AbsolutePath);

        if (DownloaderUtilities.IsHost(uri, "justthegays.com"))
        {
            if (ComVideoPathRegex.IsMatch(uri.AbsolutePath) || ComWpJsonVideoRegex.IsMatch(uri.AbsolutePath))
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

        if (DownloaderUtilities.IsHost(uri, "justthegays.tv"))
        {
            var match = TvSceneOrClipPathRegex.Match(uri.AbsolutePath);
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
        }
        else if (DownloaderUtilities.IsHost(uri, "justthegays.com"))
        {
            var match = ComVideoPathRegex.Match(uri.AbsolutePath);
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

        // Direct media stream URL (.mp4 / .m3u8)
        if (DownloaderUtilities.IsHost(uri, "justthegays.hr1alfri.xyz")
            || DownloaderUtilities.IsHost(uri, "cdn.gff.network")
            || uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var isTvCdn = DownloaderUtilities.IsHost(uri, "cdn.gff.network") || DownloaderUtilities.IsHost(uri, "justthegays.tv");
            var studio = isTvCdn ? StudioJustTheGaysTv : StudioJustTheGaysCom;
            var fallbackTitle = DownloaderUtilities.DeriveTitleFromUrl(url, studio);
            return new ResolvedMediaPage(
                PageUrl: url,
                Title: fallbackTitle,
                MediaUrl: url,
                StudioName: studio,
                IsTvSite: isTvCdn,
                Code: null,
                Date: null,
                Description: null,
                ImageUrl: null,
                PerformerNames: [],
                TagNames: [],
                QualityOptions: null);
        }

        if (DownloaderUtilities.IsHost(uri, "justthegays.tv"))
        {
            var html = await ScraperUtilities.FetchHtmlAsync(client, url, ct);
            return ParseJustTheGaysTvHtml(html, url);
        }

        if (ComWpJsonVideoRegex.IsMatch(uri.AbsolutePath))
        {
            var json = await ScraperUtilities.FetchHtmlAsync(client, url, ct);
            return ParseJustTheGaysComWpJson(json, url);
        }

        var comHtml = await ScraperUtilities.FetchHtmlAsync(client, url, ct);
        var parsedCom = ParseJustTheGaysComHtml(comHtml, url, allowMissingMedia: true);
        if (!string.IsNullOrWhiteSpace(parsedCom.MediaUrl))
            return parsedCom;

        // Fallback to WordPress REST API if the HTML did not include the direct <source> tag
        var wpApiUrl = ExtractComWpJsonUrl(comHtml, url);
        if (!string.IsNullOrWhiteSpace(wpApiUrl))
        {
            var apiJson = await ScraperUtilities.FetchHtmlAsync(client, wpApiUrl, ct);
            return MergeComHtmlAndWpJson(parsedCom, ParseJustTheGaysComWpJson(apiJson, url));
        }

        throw new InvalidOperationException($"Could not locate a media stream on {url}");
    }

    internal static ResolvedMediaPage ParseJustTheGaysTvHtml(string html, string pageUrl)
    {
        string? title = null;
        string? description = null;
        string? date = null;
        string? imageUrl = null;
        string? mediaUrl = null;
        var performers = new List<string>();
        var tags = new List<string>();

        foreach (var doc in DownloaderUtilities.ExtractJsonLdBlocks(html))
        {
            using (doc)
            {
                InspectTvJsonLd(
                    doc.RootElement,
                    ref title,
                    ref description,
                    ref date,
                    ref imageUrl,
                    ref mediaUrl,
                    performers);
            }
        }

        // Inspect SvelteKit bootstrap data for videoSrc, categories, performers, and quality flags
        var has720P = false;
        var has1080P = false;
        var bootstrapMatch = TvBootstrapVideoRegex.Match(html);
        if (bootstrapMatch.Success)
        {
            var videoBlock = bootstrapMatch.Groups["video"].Value;

            title ??= ExtractJsStringProperty(videoBlock, "jtg_title")
                ?? ExtractJsStringProperty(videoBlock, "title");

            date ??= ScraperUtilities.NormalizeIsoDate(ExtractJsStringProperty(videoBlock, "upload_date"));

            has720P = Regex.IsMatch(videoBlock, @"\bhas720P\s*:\s*true\b", RegexOptions.IgnoreCase);
            has1080P = Regex.IsMatch(videoBlock, @"\bhas1080P\s*:\s*true\b", RegexOptions.IgnoreCase);

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

        var videoSrcMatch = TvVideoSrcRegex.Match(html);
        if (videoSrcMatch.Success && string.IsNullOrWhiteSpace(mediaUrl))
            mediaUrl = WebUtility.HtmlDecode(videoSrcMatch.Groups["url"].Value).Trim();

        mediaUrl ??= ExtractGenericMediaUrl(html, pageUrl);
        if (string.IsNullOrWhiteSpace(mediaUrl))
            throw new InvalidOperationException($"Could not locate a video stream URL on {pageUrl}");

        foreach (Match catMatch in TvCategoryLinkRegex.Matches(html))
            tags.Add(catMatch.Groups["name"].Value);

        foreach (Match perfMatch in TvPerformerLinkRegex.Matches(html))
            performers.Add(perfMatch.Groups["name"].Value);

        title ??= CleanTvTitle(DownloaderUtilities.ExtractHtmlTitle(html))
            ?? DownloaderUtilities.DeriveTitleFromUrl(pageUrl, StudioJustTheGaysTv);

        description ??= DownloaderUtilities.ExtractMetaContent(html, "og:description")
            ?? DownloaderUtilities.ExtractMetaContent(html, "description");
        var cleanedDescription = CleanTvDetails(description);

        imageUrl ??= DownloaderUtilities.ExtractMetaContent(html, "og:image")
            ?? DownloaderUtilities.ExtractMetaContent(html, "twitter:image");
        if (!string.IsNullOrWhiteSpace(imageUrl) && !imageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            imageUrl = $"https://static.jtg.network/thumbnails/{imageUrl.TrimStart('/')}";

        var code = ExtractTvCode(pageUrl);
        var qualityOptions = BuildTvQualityOptions(has720P, has1080P);

        return new ResolvedMediaPage(
            PageUrl: pageUrl,
            Title: CleanTvTitle(title) ?? "JustTheGays Video",
            MediaUrl: mediaUrl,
            StudioName: StudioJustTheGaysTv,
            IsTvSite: true,
            Code: code,
            Date: date,
            Description: cleanedDescription,
            ImageUrl: imageUrl,
            PerformerNames: ScraperUtilities.DeduplicateStrings(performers),
            TagNames: ScraperUtilities.DeduplicateStrings(tags),
            QualityOptions: qualityOptions);
    }

    internal static ResolvedMediaPage ParseJustTheGaysComHtml(string html, string pageUrl, bool allowMissingMedia = false)
    {
        // Scope to the primary article when present so related video cards on the page are ignored
        var articleHtml = ExtractPrimaryArticleHtml(html);

        var rawTitle = DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)<h1[^>]*>(.*?)</h1>")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:title")
            ?? DownloaderUtilities.ExtractHtmlTitle(html);

        var title = CleanComTitle(rawTitle)
            ?? DownloaderUtilities.DeriveTitleFromUrl(pageUrl, StudioJustTheGaysCom);

        var rawDate = DownloaderUtilities.ExtractFirstRawMatch(articleHtml, @"(?is)<time\b[^>]*datetime\s*=\s*['""]([^'""]+)['""]")
            ?? DownloaderUtilities.ExtractMetaContent(html, "article:published_time")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:updated_time");
        var date = ScraperUtilities.NormalizeIsoDate(rawDate);

        var rawDetails = DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)<div\b[^>]*class\s*=\s*['""][^'""]*\b(?:post-content|entry-content)\b[^'""]*['""][^>]*>.*?<p>(.*?)</p>")
            ?? DownloaderUtilities.ExtractFirstMatch(articleHtml, @"(?is)<div\b[^>]*class\s*=\s*['""][^'""]*\b(?:post-content|entry-content)\b[^'""]*['""][^>]*>(.*?)</div>")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:description")
            ?? DownloaderUtilities.ExtractMetaContent(html, "description");

        var performers = new List<string>();
        foreach (Match match in ComPerformerLinkRegex.Matches(articleHtml))
            performers.Add(match.Groups["name"].Value);

        var tags = new List<string>();
        foreach (Match match in ComCategoryLinkRegex.Matches(articleHtml))
            tags.Add(match.Groups["name"].Value);

        if (performers.Count == 0 && !string.IsNullOrWhiteSpace(rawDetails))
            performers.AddRange(InferComPerformersFromDetails(rawDetails));

        if (tags.Count == 0 && !string.IsNullOrWhiteSpace(rawDetails))
            tags.AddRange(InferComCategoriesFromDetails(rawDetails));

        var cleanedDetails = CleanComDetails(rawDetails, title);
        var imageUrl = DownloaderUtilities.ExtractMetaContent(html, "og:image")
            ?? DownloaderUtilities.ExtractMetaContent(html, "twitter:image");

        var mediaUrl = ExtractGenericMediaUrl(articleHtml, pageUrl)
            ?? ExtractGenericMediaUrl(html, pageUrl);

        if (string.IsNullOrWhiteSpace(mediaUrl) && !allowMissingMedia)
            throw new InvalidOperationException($"Could not locate a direct video stream on {pageUrl}");

        return new ResolvedMediaPage(
            PageUrl: pageUrl,
            Title: title,
            MediaUrl: mediaUrl ?? string.Empty,
            StudioName: StudioJustTheGaysCom,
            IsTvSite: false,
            Code: ExtractComCode(pageUrl, html),
            Date: date,
            Description: cleanedDetails,
            ImageUrl: imageUrl,
            PerformerNames: ScraperUtilities.DeduplicateStrings(performers),
            TagNames: ScraperUtilities.DeduplicateStrings(tags),
            QualityOptions: null);
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
            ?? DownloaderUtilities.DeriveTitleFromUrl(pageUrl, StudioJustTheGaysCom);

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
            StudioName: StudioJustTheGaysCom,
            IsTvSite: false,
            Code: code,
            Date: date,
            Description: cleanedDetails,
            ImageUrl: null,
            PerformerNames: ScraperUtilities.DeduplicateStrings(performers),
            TagNames: ScraperUtilities.DeduplicateStrings(tags),
            QualityOptions: null);
    }

    private async Task<string> DownloadHlsStreamAsync(
        HttpClient client,
        string m3u8Url,
        DownloaderRequest request,
        IDownloaderHost host,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        Directory.CreateDirectory(host.TempDirectory);
        host.ReportProgress(8.0, "Fetching HLS manifest...");

        var manifestText = await ScraperUtilities.FetchHtmlAsync(client, m3u8Url, ct, headers);
        var activePlaylistUrl = m3u8Url;

        // If this is a master playlist (#EXT-X-STREAM-INF), select the best variant matching request quality/maxHeight
        if (manifestText.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
        {
            var variantUrl = SelectHlsVariantUrl(manifestText, m3u8Url, request.QualityId, request.MaxHeight);
            if (!string.IsNullOrWhiteSpace(variantUrl))
            {
                activePlaylistUrl = variantUrl;
                manifestText = await ScraperUtilities.FetchHtmlAsync(client, activePlaylistUrl, ct, headers);
            }
        }

        var segmentUrls = ParseHlsSegmentUrls(manifestText, activePlaylistUrl);
        if (segmentUrls.Count == 0)
            throw new InvalidOperationException($"HLS playlist at {activePlaylistUrl} contained no downloadable media segments.");

        var stagingTsPath = Path.Combine(host.TempDirectory, "downloaded_segments.ts");
        await using (var output = File.Create(stagingTsPath))
        {
            for (var i = 0; i < segmentUrls.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var segRequest = new HttpRequestMessage(HttpMethod.Get, segmentUrls[i]);
                segRequest.Headers.UserAgent.TryParseAdd(DownloaderUtilities.DefaultUserAgent);
                foreach (var (key, value) in headers)
                    segRequest.Headers.TryAddWithoutValidation(key, value);

                using var segResponse = await client.SendAsync(segRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                segResponse.EnsureSuccessStatusCode();

                await using var segStream = await segResponse.Content.ReadAsStreamAsync(ct);
                await segStream.CopyToAsync(output, ct);

                var progress = 10.0 + (82.0 * (i + 1) / segmentUrls.Count);
                host.ReportProgress(progress, $"Downloaded HLS segment {i + 1}/{segmentUrls.Count}");
            }
        }

        var mp4Path = Path.Combine(host.TempDirectory, "downloaded.mp4");
        if (File.Exists(mp4Path))
            File.Delete(mp4Path);

        if (await TryRemuxTsToMp4Async(stagingTsPath, mp4Path, host, ct))
        {
            try { File.Delete(stagingTsPath); } catch { /* best effort cleanup */ }
            return mp4Path;
        }

        File.Move(stagingTsPath, mp4Path, overwrite: true);
        return mp4Path;
    }

    internal static IReadOnlyList<string> ParseHlsSegmentUrls(string playlistText, string playlistUrl)
    {
        var baseUri = new Uri(playlistUrl);
        var urls = new List<string>();

        var mapMatch = HlsMapUriRegex.Match(playlistText);
        if (mapMatch.Success)
        {
            var mapUriRaw = mapMatch.Groups["uri"].Value.Trim();
            if (Uri.TryCreate(baseUri, mapUriRaw, out var mapUri))
                urls.Add(mapUri.ToString());
        }

        using var reader = new StringReader(playlistText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            if (Uri.TryCreate(baseUri, trimmed, out var segmentUri))
                urls.Add(segmentUri.ToString());
        }

        return urls;
    }

    internal static string? SelectHlsVariantUrl(string masterPlaylist, string masterUrl, string? qualityId, int? maxHeight)
    {
        var baseUri = new Uri(masterUrl);
        var variants = new List<(int Height, long Bandwidth, string Url)>();

        foreach (Match match in HlsStreamInfRegex.Matches(masterPlaylist))
        {
            var attrs = match.Groups["attrs"].Value;
            var uriRaw = match.Groups["uri"].Value.Trim();
            if (!Uri.TryCreate(baseUri, uriRaw, out var resolvedUri))
                continue;

            var height = 0;
            var resMatch = Regex.Match(attrs, @"RESOLUTION=\d+x(?<h>\d+)", RegexOptions.IgnoreCase);
            if (resMatch.Success && int.TryParse(resMatch.Groups["h"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedH))
                height = parsedH;

            long bandwidth = 0;
            var bwMatch = Regex.Match(attrs, @"BANDWIDTH=(?<bw>\d+)", RegexOptions.IgnoreCase);
            if (bwMatch.Success && long.TryParse(bwMatch.Groups["bw"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBw))
                bandwidth = parsedBw;

            variants.Add((height, bandwidth, resolvedUri.ToString()));
        }

        if (variants.Count == 0)
            return null;

        int? targetMaxHeight = maxHeight;
        if (!string.IsNullOrWhiteSpace(qualityId))
        {
            var digits = Regex.Match(qualityId, @"(\d{3,4})");
            if (digits.Success && int.TryParse(digits.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qHeight))
                targetMaxHeight = qHeight;
        }

        if (targetMaxHeight is > 0)
        {
            var withinCeiling = variants
                .Where(v => v.Height > 0 && v.Height <= targetMaxHeight.Value)
                .OrderByDescending(v => v.Height)
                .ThenByDescending(v => v.Bandwidth)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(withinCeiling.Url))
                return withinCeiling.Url;

            var lowest = variants
                .Where(v => v.Height > 0)
                .OrderBy(v => v.Height)
                .ThenBy(v => v.Bandwidth)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(lowest.Url))
                return lowest.Url;
        }

        return variants
            .OrderByDescending(v => v.Height)
            .ThenByDescending(v => v.Bandwidth)
            .First()
            .Url;
    }

    private async Task<bool> TryRemuxTsToMp4Async(
        string inputTsPath,
        string outputMp4Path,
        IDownloaderHost host,
        CancellationToken ct)
    {
        try
        {
            var configuredFfmpeg = _configuration?["ffmpegPath"]
                ?? _configuration?["Extensions:karinauts.downloaders.just-the-gays:ffmpegPath"];
            var ffmpegBinary = string.IsNullOrWhiteSpace(configuredFfmpeg) ? "ffmpeg" : configuredFfmpeg.Trim();

            host.ReportProgress(95.0, "Remuxing HLS transport stream to MP4...");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegBinary,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };

            process.StartInfo.ArgumentList.Add("-y");
            process.StartInfo.ArgumentList.Add("-loglevel");
            process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(inputTsPath);
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add("copy");
            process.StartInfo.ArgumentList.Add("-movflags");
            process.StartInfo.ArgumentList.Add("+faststart");
            process.StartInfo.ArgumentList.Add(outputMp4Path);

            if (!process.Start())
                return false;

            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0
                && File.Exists(outputMp4Path)
                && new FileInfo(outputMp4Path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void InspectTvJsonLd(
        JsonElement element,
        ref string? title,
        ref string? description,
        ref string? date,
        ref string? imageUrl,
        ref string? mediaUrl,
        List<string> performers)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                InspectTvJsonLd(item, ref title, ref description, ref date, ref imageUrl, ref mediaUrl, performers);
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
        mediaUrl ??= TryGetJsonString(element, "contentUrl");

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
                    var name = actor.ValueKind == JsonValueKind.String
                        ? actor.GetString()
                        : TryGetJsonString(actor, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                        performers.Add(name);
                }
            }
            else if (actorProp.ValueKind == JsonValueKind.Object)
            {
                var name = TryGetJsonString(actorProp, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    performers.Add(name);
            }
        }
    }

    private static IReadOnlyList<DownloaderQualityOption>? BuildTvQualityOptions(bool has720P, bool has1080P)
    {
        if (!has720P && !has1080P)
            return null;

        var options = new List<DownloaderQualityOption>();
        if (has1080P)
            options.Add(new DownloaderQualityOption("height:1080", "1080p Full HD") { Width = 1920, Height = 1080 });
        if (has720P)
            options.Add(new DownloaderQualityOption("height:720", "720p HD") { Width = 1280, Height = 720 });
        options.Add(new DownloaderQualityOption("height:480", "Standard") { Width = 854, Height = 480 });
        return options;
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
                && Regex.IsMatch(metaVal, @"\.(?:mp4|m4v|webm|m3u8)(?:\?|$)", RegexOptions.IgnoreCase))
            {
                return ResolveUrl(metaVal, pageUrl);
            }
        }

        var rawMp4 = DownloaderUtilities.ExtractFirstRawMatch(
            html,
            @"(https?://[^'""<>\s\\]+\.(?:mp4|m4v|m3u8)(?:\?[^'""<>\s\\]+)?)");

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
        var linkMatch = ComWpJsonLinkRegex.Match(html);
        if (linkMatch.Success)
            return WebUtility.HtmlDecode(linkMatch.Groups["href"].Value).Trim();

        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
        {
            var pathMatch = ComVideoPathRegex.Match(uri.AbsolutePath);
            if (pathMatch.Success)
            {
                var slug = pathMatch.Groups["slug"].Value;
                return $"https://justthegays.com/wp-json/wp/v2/video?slug={Uri.EscapeDataString(slug)}";
            }
        }

        return null;
    }

    private static ResolvedMediaPage MergeComHtmlAndWpJson(ResolvedMediaPage htmlPage, ResolvedMediaPage apiPage)
    {
        return new ResolvedMediaPage(
            PageUrl: htmlPage.PageUrl,
            Title: !string.IsNullOrWhiteSpace(apiPage.Title) ? apiPage.Title : htmlPage.Title,
            MediaUrl: !string.IsNullOrWhiteSpace(htmlPage.MediaUrl) ? htmlPage.MediaUrl : apiPage.MediaUrl,
            StudioName: StudioJustTheGaysCom,
            IsTvSite: false,
            Code: htmlPage.Code ?? apiPage.Code,
            Date: htmlPage.Date ?? apiPage.Date,
            Description: !string.IsNullOrWhiteSpace(htmlPage.Description) ? htmlPage.Description : apiPage.Description,
            ImageUrl: htmlPage.ImageUrl ?? apiPage.ImageUrl,
            PerformerNames: htmlPage.PerformerNames.Count > 0 ? htmlPage.PerformerNames : apiPage.PerformerNames,
            TagNames: htmlPage.TagNames.Count > 0 ? htmlPage.TagNames : apiPage.TagNames,
            QualityOptions: null);
    }

    private static string ExtractPrimaryArticleHtml(string html)
    {
        var match = Regex.Match(html, @"(?is)<article\b[^>]*>.*?</article>");
        return match.Success ? match.Value : html;
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

    private static string? CleanComTitle(string? rawTitle)
    {
        var cleaned = ScraperUtilities.CleanText(rawTitle);
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        cleaned = Regex.Replace(cleaned, @"\s*-\s*JustTheGays(?:\.com)?\b.*$", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*\|\s*Just The Gays\b.*$", string.Empty, RegexOptions.IgnoreCase);
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

    private static string? ExtractTvCode(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            return null;

        var match = TvSceneOrClipPathRegex.Match(uri.AbsolutePath);
        if (!match.Success)
            return null;

        var kind = match.Groups["kind"].Value;
        var slug = match.Groups["slug"].Value;
        if (string.Equals(kind, "clip", StringComparison.OrdinalIgnoreCase))
            return slug;

        var suffixMatch = Regex.Match(slug, @"-([a-z0-9]{2,8})$", RegexOptions.IgnoreCase);
        return suffixMatch.Success ? suffixMatch.Groups[1].Value : slug;
    }

    private static string? ExtractComCode(string pageUrl, string? html)
    {
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
        {
            var pathMatch = ComVideoPathRegex.Match(uri.AbsolutePath);
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

            var apiMatch = ComWpJsonLinkRegex.Match(html);
            if (apiMatch.Success)
            {
                var idMatch = Regex.Match(apiMatch.Groups["href"].Value, @"/video/(?<id>\d+)");
                if (idMatch.Success)
                    return idMatch.Groups["id"].Value;
            }
        }

        return null;
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

    private static bool IsHlsUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
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
        string StudioName,
        bool IsTvSite,
        string? Code,
        string? Date,
        string? Description,
        string? ImageUrl,
        List<string> PerformerNames,
        List<string> TagNames,
        IReadOnlyList<DownloaderQualityOption>? QualityOptions);
}
