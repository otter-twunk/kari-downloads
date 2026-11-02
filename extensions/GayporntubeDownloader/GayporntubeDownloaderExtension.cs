using System.Net;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace GayporntubeDownloader;

public sealed class GayporntubeDownloaderExtension : CoveExtensionBase, IDownloaderProvider
{
    public const string PrimaryDownloaderId = "karinauts.downloaders.gayporntube.video";
    public const string StudioName = "GayPornTube";
    private static readonly string[] SupportedDomains = ["gayporntube.com"];

    private static readonly Regex VideoPathRegex = new(
        @"^/(?:video|embed)/\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VideoIdRegex = new(
        @"/(?:video|embed)/(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleSuffixRegex = new(
        @"\s*(?:\||-)\s*GayPornTube(?:\.com)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CategoryAnchorRegex = new(
        @"(?is)<div[^>]*class=""[^""]*video-info-categories[^""]*""[^>]*>(?<content>.*?)</div>",
        RegexOptions.Compiled);

    private static readonly Regex AnchorTextRegex = new(
        @"(?is)<a\b[^>]*>(?<name>[^<]+)</a>",
        RegexOptions.Compiled);

    private static readonly Regex VideoSourceRegex = new(
        @"(?is)<source\b[^>]*src=['""](?<url>[^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex VideoTagSrcRegex = new(
        @"(?is)<video\b[^>]*src=['""](?<url>[^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex VideoPosterRegex = new(
        @"(?is)<video\b[^>]*poster=['""](?<url>[^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex GetFileUrlRegex = new(
        @"(?is)(?:https?://[^'""<>\s]+)?/get_file/\d+/[a-f0-9]+/\d+/\d+/\d+\.mp4[^\s'""]*",
        RegexOptions.Compiled);

    private IServiceProvider? _services;

    private static readonly DownloaderDescriptor PrimaryDescriptor = new(
        PrimaryDownloaderId,
        "GayPornTube Downloader",
        DownloaderEntity.Video,
        ["https://gayporntube.com/video/*", "https://*.gayporntube.com/video/*", "https://gayporntube.com/embed/*", "https://*.gayporntube.com/embed/*"],
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

        var pageInfo = await TryInspectPageAsync(url, ct);
        return new DownloaderUrlMatch(
            PrimaryDescriptor.Id,
            url,
            QualityOptions: null,
            Label: pageInfo?.Title ?? DownloaderUtilities.DeriveTitleFromUrl(url, "GayPornTube Downloader"));
    }

    public async Task<DownloaderResult?> DownloadAsync(
        DownloaderRequest request,
        IDownloaderHost host,
        CancellationToken ct)
    {
        if (!string.Equals(request.DownloaderId, PrimaryDescriptor.Id, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!IsSupportedUrl(request.Url))
            throw new InvalidOperationException($"Unsupported URL for GayPornTube Downloader: {request.Url}");

        host.ReportProgress(5.0, "Inspecting GayPornTube video page...");
        var pageInfo = await InspectPageAsync(request.Url, host, ct);

        var extension = DownloaderUtilities.EnsureExtensionFromUrl(pageInfo.MediaUrl, ".mp4");
        var originalFileName = DownloaderUtilities.SanitizeFileName(pageInfo.Title) + extension;
        var localFileName = "downloaded" + extension;

        var headers = new Dictionary<string, string>
        {
            ["Referer"] = pageInfo.PageUrl
        };

        host.ReportProgress(15.0, "Downloading video stream...");
        var localPath = await DownloaderUtilities.DownloadToTempFileAsync(
            CreateHttpClient(host),
            pageInfo.MediaUrl,
            host,
            localFileName,
            ct,
            headers: headers,
            startProgress: 15.0,
            endProgress: 95.0);

        host.ReportProgress(100.0, "Completed download.");
        return new DownloaderResult(
            LocalPath: Path.GetFileName(localPath),
            OriginalFilename: originalFileName,
            InlineVideoMetadata: new ScrapedVideoDto
            {
                Title = pageInfo.Title,
                Code = pageInfo.VideoId,
                Date = pageInfo.Date,
                Details = pageInfo.Description,
                ImageUrl = pageInfo.PosterUrl,
                Urls = [request.SourceUrl ?? request.Url],
                StudioName = StudioName,
                TagNames = pageInfo.TagNames
            });
    }

    public static bool IsSupportedUrl(string? url)
    {
        if (!DownloaderUtilities.IsHttpUrl(url) || !DownloaderUtilities.IsAnyHost(url, SupportedDomains))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return VideoPathRegex.IsMatch(uri.AbsolutePath);
    }

    public static string NormalizeVideoUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url;

        var host = uri.Host;
        if (host.Equals("gayporntube.com", StringComparison.OrdinalIgnoreCase))
            host = "www.gayporntube.com";

        var path = uri.AbsolutePath;
        var videoMatch = Regex.Match(path, @"^/video/(?<id>\d+)(?:/(?<slug>[^/?#]+))?/?$", RegexOptions.IgnoreCase);
        if (videoMatch.Success)
        {
            var id = videoMatch.Groups["id"].Value;
            var slug = videoMatch.Groups["slug"].Value;
            if (string.IsNullOrWhiteSpace(slug))
                slug = "video";

            return $"https://{host}/video/{id}/{slug}";
        }

        var embedMatch = Regex.Match(path, @"^/embed/(?<id>\d+)/?$", RegexOptions.IgnoreCase);
        if (embedMatch.Success)
        {
            var id = embedMatch.Groups["id"].Value;
            return $"https://{host}/video/{id}/video";
        }

        return uri.ToString();
    }

    internal async Task<ResolvedMediaPage?> TryInspectPageAsync(string url, CancellationToken ct)
    {
        try
        {
            return await InspectPageAsync(url, host: null, ct);
        }
        catch
        {
            return null;
        }
    }

    internal async Task<ResolvedMediaPage> InspectPageAsync(string url, IDownloaderHost? host, CancellationToken ct)
    {
        var targetUrl = NormalizeVideoUrl(url);
        var client = CreateHttpClient(host);

        using var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
        request.Headers.UserAgent.TryParseAdd(DownloaderUtilities.DefaultUserAgent);

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // If the server responded with a redirect location (e.g. 404 with Location header)
            if (response.Headers.Location is { } loc)
            {
                var redirectUrl = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(targetUrl), loc).ToString();
                return await InspectPageAsync(redirectUrl, host, ct);
            }

            // Fallback to embed endpoint if video page returned non-success
            var idMatch = VideoIdRegex.Match(url);
            if (idMatch.Success && !targetUrl.Contains("/embed/"))
            {
                var embedUrl = $"https://www.gayporntube.com/embed/{idMatch.Groups["id"].Value}";
                using var embedReq = new HttpRequestMessage(HttpMethod.Get, embedUrl);
                embedReq.Headers.UserAgent.TryParseAdd(DownloaderUtilities.DefaultUserAgent);
                using var embedRes = await client.SendAsync(embedReq, ct);
                if (embedRes.IsSuccessStatusCode)
                {
                    var embedHtml = await embedRes.Content.ReadAsStringAsync(ct);
                    return ParsePage(embedHtml, embedUrl);
                }
            }

            response.EnsureSuccessStatusCode();
        }

        var html = await response.Content.ReadAsStringAsync(ct);
        return ParsePage(html, targetUrl);
    }

    public static ResolvedMediaPage ParsePage(string html, string url)
    {
        var rawTitle = DownloaderUtilities.ExtractHtmlTitle(html)
            ?? DownloaderUtilities.ExtractFirstMatch(html, @"(?is)<h1[^>]*class=""[^""]*title[^""]*""[^>]*>(?<title>[^<]+)</h1>")
            ?? DownloaderUtilities.DeriveTitleFromUrl(url, "GayPornTube Video");

        var cleanTitle = TitleSuffixRegex.Replace(rawTitle, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
            cleanTitle = DownloaderUtilities.DeriveTitleFromUrl(url, "GayPornTube Video");

        var rawDescription = DownloaderUtilities.ExtractMetaContent(html, "og:description")
            ?? DownloaderUtilities.ExtractMetaContent(html, "description");
        if (string.Equals(rawDescription?.Trim(), ".", StringComparison.Ordinal))
            rawDescription = null;

        var rawDate = DownloaderUtilities.ExtractMetaContent(html, "video:release_date");
        var date = ScraperUtilities.NormalizeIsoDate(rawDate);

        var mediaUrl = ExtractMediaUrl(html, url)
            ?? throw new InvalidOperationException($"Could not locate a direct media stream on {url}");

        var posterUrl = DownloaderUtilities.ExtractMetaContent(html, "og:image")
            ?? VideoPosterRegex.Match(html).Groups["url"].Value;
        if (string.IsNullOrWhiteSpace(posterUrl))
            posterUrl = null;
        else if (Uri.TryCreate(new Uri(url), posterUrl, out var fullPoster))
            posterUrl = fullPoster.ToString();

        var tags = new List<string>();
        foreach (Match meta in Regex.Matches(html, @"(?is)<meta\b[^>]*property=['""]video:tag['""][^>]*content=['""](?<content>[^'""]+)['""]"))
        {
            var val = WebUtility.HtmlDecode(meta.Groups["content"].Value).Trim();
            if (!string.IsNullOrWhiteSpace(val) && !tags.Contains(val, StringComparer.OrdinalIgnoreCase))
                tags.Add(val);
        }

        var catMatch = CategoryAnchorRegex.Match(html);
        if (catMatch.Success)
        {
            foreach (Match anchor in AnchorTextRegex.Matches(catMatch.Groups["content"].Value))
            {
                var val = WebUtility.HtmlDecode(anchor.Groups["name"].Value).Trim();
                if (!string.IsNullOrWhiteSpace(val) && !tags.Contains(val, StringComparer.OrdinalIgnoreCase))
                    tags.Add(val);
            }
        }

        var keywords = DownloaderUtilities.ExtractMetaContent(html, "keywords");
        if (!string.IsNullOrWhiteSpace(keywords))
        {
            foreach (var kw in keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!tags.Contains(kw, StringComparer.OrdinalIgnoreCase))
                    tags.Add(kw);
            }
        }

        var videoIdMatch = VideoIdRegex.Match(url);
        var videoId = videoIdMatch.Success ? videoIdMatch.Groups["id"].Value : null;

        return new ResolvedMediaPage(
            url,
            cleanTitle,
            mediaUrl,
            rawDescription,
            date,
            posterUrl,
            videoId,
            tags);
    }

    public static string? ExtractMediaUrl(string html, string pageUrl)
    {
        var candidate = VideoSourceRegex.Match(html).Groups["url"].Value;
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = VideoTagSrcRegex.Match(html).Groups["url"].Value;
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = GetFileUrlRegex.Match(html).Value;
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = DownloaderUtilities.ExtractMetaContent(html, "og:video:secure_url")
                ?? DownloaderUtilities.ExtractMetaContent(html, "og:video:url")
                ?? DownloaderUtilities.ExtractMetaContent(html, "og:video");

        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        candidate = WebUtility.HtmlDecode(candidate.Trim());

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        if (Uri.TryCreate(new Uri(pageUrl), candidate, out var relative))
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

    public sealed record ResolvedMediaPage(
        string PageUrl,
        string Title,
        string MediaUrl,
        string? Description,
        string? Date,
        string? PosterUrl,
        string? VideoId,
        List<string> TagNames);
}
