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
        @"href\s*=\s*['""]https?://(?:www\.)?justthegays\.com/performers/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CategoryLinkRegex = new(
        @"href\s*=\s*['""]https?://(?:www\.)?justthegays\.com/categories/[^'""]+['""][^>]*rel\s*=\s*['""]tag['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

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
            ImageUrl: null,
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

    private static ResolvedMediaPage MergeComHtmlAndWpJson(ResolvedMediaPage htmlPage, ResolvedMediaPage apiPage)
    {
        return new ResolvedMediaPage(
            PageUrl: htmlPage.PageUrl,
            Title: !string.IsNullOrWhiteSpace(apiPage.Title) ? apiPage.Title : htmlPage.Title,
            MediaUrl: !string.IsNullOrWhiteSpace(htmlPage.MediaUrl) ? htmlPage.MediaUrl : apiPage.MediaUrl,
            Code: htmlPage.Code ?? apiPage.Code,
            Date: htmlPage.Date ?? apiPage.Date,
            Description: !string.IsNullOrWhiteSpace(htmlPage.Description) ? htmlPage.Description : apiPage.Description,
            ImageUrl: htmlPage.ImageUrl ?? apiPage.ImageUrl,
            PerformerNames: htmlPage.PerformerNames.Count > 0 ? htmlPage.PerformerNames : apiPage.PerformerNames,
            TagNames: htmlPage.TagNames.Count > 0 ? htmlPage.TagNames : apiPage.TagNames);
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
