using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace ThisvidDownloader;

public sealed class ThisvidDownloaderExtension : CoveExtensionBase, IDownloaderProvider
{
    public const string PrimaryDownloaderId = "karinauts.downloaders.thisvid.video";
    public const string DefaultStudioName = "ThisVid";
    private static readonly string[] SupportedDomains = ["thisvid.com"];

    private static readonly Regex VideoPathRegex = new(
        @"^/(?:videos|embed)/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VideoIdRegex = new(
        @"(?:data-video-id=[""'](?<id>\d+)[""']|videoId:\s*['""]?(?<id>\d+)|/(?:videos|embed)/(?<id>\d+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleSuffixRegex = new(
        @"\s*-\s*ThisVid(?:\.com)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DescriptionParagraphRegex = new(
        @"<ul[^>]*class=[""'][^""']*\bdescription\b[^""']*[""'][^>]*>.*?<li>\s*<p>(?<desc>.*?)</p>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AddedDateRegex = new(
        @"<span>\s*Added:\s*</span>\s*(?<date>\d{1,2}\s+[A-Za-z]+\s+\d{4})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CategoriesBlockRegex = new(
        @"<li>\s*<span>\s*Categories:\s*</span>(?<content>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TagsBlockRegex = new(
        @"<li>\s*<span>\s*Tags:\s*</span>(?<content>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AnchorRegex = new(
        @"<a\b[^>]*>(?<text>[^<]+)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FlashvarsBlockRegex = new(
        @"(?is)(?:var\s+)?flashvars(?:\w*)\s*=\s*\{(?<content>.*?)\};",
        RegexOptions.Compiled);

    private static readonly Regex FlashvarsPostfixRegex = new(
        @"(?is)postfix\s*:\s*['""](?<postfix>[^'""]+)['""]",
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
        @"(?is)https?://[^'""<>\s]+/get_file/\d+/[a-f0-9]+/\d+/\d+/\d+\.mp4[^\s'""]*",
        RegexOptions.Compiled);

    private IServiceProvider? _services;

    private static readonly DownloaderDescriptor PrimaryDescriptor = new(
        PrimaryDownloaderId,
        "ThisVid Downloader",
        DownloaderEntity.Video,
        ["https://thisvid.com/videos/*", "https://*.thisvid.com/videos/*", "https://thisvid.com/embed/*", "https://*.thisvid.com/embed/*"],
        DownloaderCapabilities.InlineMetadata | DownloaderCapabilities.MultiQuality);

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
        var qualityOptions = pageInfo?.Streams
            .Where(s => !string.IsNullOrWhiteSpace(s.QualityId))
            .Select(s => new DownloaderQualityOption(s.QualityId, s.Height != null ? $"{s.Height}p" : s.QualityId) { Height = s.Height })
            .DistinctBy(q => q.Id)
            .ToList();

        return new DownloaderUrlMatch(
            PrimaryDescriptor.Id,
            url,
            QualityOptions: qualityOptions is { Count: > 0 } ? qualityOptions : null,
            Label: pageInfo?.Title ?? DownloaderUtilities.DeriveTitleFromUrl(url, "ThisVid Downloader"));
    }

    public async Task<DownloaderResult?> DownloadAsync(
        DownloaderRequest request,
        IDownloaderHost host,
        CancellationToken ct)
    {
        if (!string.Equals(request.DownloaderId, PrimaryDescriptor.Id, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!IsSupportedUrl(request.Url))
            throw new InvalidOperationException($"Unsupported URL for ThisVid Downloader: {request.Url}");

        host.ReportProgress(5.0, "Inspecting ThisVid media page...");
        var pageInfo = await InspectPageAsync(request.Url, ct);

        var selectedStream = SelectStream(pageInfo.Streams, request.QualityId, request.MaxHeight)
            ?? throw new InvalidOperationException($"Could not locate a suitable video stream for {request.Url}");

        var extension = DownloaderUtilities.EnsureExtensionFromUrl(selectedStream.VideoUrl, ".mp4");
        var originalFileName = DownloaderUtilities.SanitizeFileName(pageInfo.Title) + extension;
        var localFileName = "downloaded" + extension;

        var headers = new Dictionary<string, string>
        {
            ["Referer"] = pageInfo.PageUrl
        };

        host.ReportProgress(15.0, "Downloading video stream...");
        var localPath = await DownloaderUtilities.DownloadToTempFileAsync(
            CreateHttpClient(host),
            selectedStream.VideoUrl,
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
                StudioName = DefaultStudioName,
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

    internal async Task<ResolvedMediaPage?> TryInspectPageAsync(string url, CancellationToken ct)
    {
        try
        {
            return await InspectPageAsync(url, ct);
        }
        catch
        {
            return null;
        }
    }

    internal async Task<ResolvedMediaPage> InspectPageAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.TryParseAdd(DownloaderUtilities.DefaultUserAgent);

        using var response = await CreateHttpClient().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);
        return ParsePage(html, url);
    }

    public static ResolvedMediaPage ParsePage(string html, string url)
    {
        var rawTitle = DownloaderUtilities.ExtractHtmlTitle(html)
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:title")
            ?? DownloaderUtilities.ExtractFirstMatch(html, @"(?is)<h1[^>]*>(?<title>[^<]+)</h1>")
            ?? DownloaderUtilities.DeriveTitleFromUrl(url, "ThisVid Video");

        var cleanTitle = TitleSuffixRegex.Replace(rawTitle, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
            cleanTitle = DownloaderUtilities.DeriveTitleFromUrl(url, "ThisVid Video");

        string? details = null;
        var descMatch = DescriptionParagraphRegex.Match(html);
        if (descMatch.Success)
        {
            var rawParagraph = descMatch.Groups["desc"].Value;
            var cleanedDesc = ScraperUtilities.CleanText(Regex.Replace(rawParagraph, "<[^>]+>", " "));
            if (!string.IsNullOrWhiteSpace(cleanedDesc))
                details = cleanedDesc;
        }

        if (string.IsNullOrWhiteSpace(details))
        {
            details = DownloaderUtilities.ExtractMetaContent(html, "og:description")
                ?? DownloaderUtilities.ExtractMetaContent(html, "description");
        }

        string? date = null;
        var dateMatch = AddedDateRegex.Match(html);
        if (dateMatch.Success)
        {
            var rawDate = dateMatch.Groups["date"].Value;
            date = ScraperUtilities.NormalizeIsoDate(rawDate);
        }

        var streams = ExtractMediaStreams(html, url);
        if (streams.Count == 0)
            throw new InvalidOperationException($"Could not locate a direct media stream on {url}");

        var posterUrl = DownloaderUtilities.ExtractMetaContent(html, "og:image")
            ?? VideoPosterRegex.Match(html).Groups["url"].Value;
        if (string.IsNullOrWhiteSpace(posterUrl))
            posterUrl = null;
        else if (Uri.TryCreate(new Uri(url), posterUrl, out var fullPoster))
            posterUrl = fullPoster.ToString();

        var videoIdMatch = VideoIdRegex.Match(html);
        var videoId = videoIdMatch.Success ? videoIdMatch.Groups["id"].Value : null;
        if (string.IsNullOrWhiteSpace(videoId) && VideoIdRegex.Match(url) is { Success: true } urlId)
            videoId = urlId.Groups["id"].Value;

        var tags = new List<string>();

        void ExtractAnchorsFromBlock(Regex blockRegex)
        {
            var match = blockRegex.Match(html);
            if (!match.Success)
                return;

            foreach (Match anchor in AnchorRegex.Matches(match.Groups["content"].Value))
            {
                var text = ScraperUtilities.CleanText(anchor.Groups["text"].Value);
                if (!string.IsNullOrWhiteSpace(text))
                    tags.Add(text);
            }
        }

        ExtractAnchorsFromBlock(CategoriesBlockRegex);
        ExtractAnchorsFromBlock(TagsBlockRegex);

        var deduplicatedTags = ScraperUtilities.DeduplicateStrings(tags);

        return new ResolvedMediaPage(
            url,
            cleanTitle,
            streams,
            details,
            date,
            posterUrl,
            videoId,
            deduplicatedTags);
    }

    public static List<MediaStream> ExtractMediaStreams(string html, string pageUrl)
    {
        var streams = new List<MediaStream>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Flashvars configuration extraction (standard KVS format)
        var postfix = string.Empty;
        var postfixMatch = FlashvarsPostfixRegex.Match(html);
        if (postfixMatch.Success)
            postfix = postfixMatch.Groups["postfix"].Value.Trim();

        // Flashvars keys to search: video_alt_url3, video_alt_url2, video_alt_url, video_url
        var qualityKeys = new[]
        {
            ("video_alt_url3", "video_alt_url3_text"),
            ("video_alt_url2", "video_alt_url2_text"),
            ("video_alt_url", "video_alt_url_text"),
            ("video_url", "video_url_text")
        };

        foreach (var (urlKey, textKey) in qualityKeys)
        {
            var urlMatch = Regex.Match(html, $@"(?is){urlKey}\s*:\s*['""](?<url>[^'""]+)['""]");
            if (urlMatch.Success)
            {
                var rawUrl = urlMatch.Groups["url"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(rawUrl))
                {
                    if (!string.IsNullOrWhiteSpace(postfix) && !rawUrl.EndsWith(postfix, StringComparison.OrdinalIgnoreCase) && !rawUrl.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        rawUrl = rawUrl.EndsWith('/') ? rawUrl.TrimEnd('/') + postfix : rawUrl + postfix;
                    }

                    var resolved = ResolveUrl(rawUrl, pageUrl);
                    if (!string.IsNullOrWhiteSpace(resolved) && seenUrls.Add(resolved))
                    {
                        var textMatch = Regex.Match(html, $@"(?is){textKey}\s*:\s*['""](?<text>[^'""]+)['""]");
                        var label = textMatch.Success ? textMatch.Groups["text"].Value.Trim() : null;
                        var height = ParseHeight(label);
                        var qualityId = label ?? (height != null ? $"{height}p" : urlKey);
                        streams.Add(new MediaStream(resolved, qualityId, height, streams.Count == 0));
                    }
                }
            }
        }

        // 2. HTML5 <source> tag
        var sourceMatch = VideoSourceRegex.Match(html);
        if (sourceMatch.Success)
        {
            var raw = sourceMatch.Groups["url"].Value.Trim();
            var resolved = ResolveUrl(raw, pageUrl);
            if (!string.IsNullOrWhiteSpace(resolved) && seenUrls.Add(resolved))
            {
                streams.Add(new MediaStream(resolved, "default", null, streams.Count == 0));
            }
        }

        // 3. HTML5 <video src="..."> tag
        var videoSrcMatch = VideoTagSrcRegex.Match(html);
        if (videoSrcMatch.Success)
        {
            var raw = videoSrcMatch.Groups["url"].Value.Trim();
            var resolved = ResolveUrl(raw, pageUrl);
            if (!string.IsNullOrWhiteSpace(resolved) && seenUrls.Add(resolved))
            {
                streams.Add(new MediaStream(resolved, "default", null, streams.Count == 0));
            }
        }

        // 4. Direct /get_file/ URL matching
        var getFileMatch = GetFileUrlRegex.Match(html);
        if (getFileMatch.Success)
        {
            var raw = getFileMatch.Value.Trim();
            var resolved = ResolveUrl(raw, pageUrl);
            if (!string.IsNullOrWhiteSpace(resolved) && seenUrls.Add(resolved))
            {
                streams.Add(new MediaStream(resolved, "default", null, streams.Count == 0));
            }
        }

        // 5. OpenGraph video meta
        var ogVideo = DownloaderUtilities.ExtractMetaContent(html, "og:video:secure_url")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:video:url")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:video");

        if (!string.IsNullOrWhiteSpace(ogVideo))
        {
            var resolved = ResolveUrl(ogVideo, pageUrl);
            if (!string.IsNullOrWhiteSpace(resolved) && seenUrls.Add(resolved))
            {
                streams.Add(new MediaStream(resolved, "default", null, streams.Count == 0));
            }
        }

        // 6. Generic mp4 stream URL pattern
        if (streams.Count == 0)
        {
            var rawMatch = Regex.Match(html, @"(?is)(https?://[^'""<>\s]+\.(?:mp4|m4v)(?:[/?][^'""<>\s]*)?)");
            if (rawMatch.Success)
            {
                var resolved = ResolveUrl(rawMatch.Value, pageUrl);
                if (!string.IsNullOrWhiteSpace(resolved) && seenUrls.Add(resolved))
                {
                    streams.Add(new MediaStream(resolved, "default", null, true));
                }
            }
        }

        return streams;
    }

    internal static MediaStream? SelectStream(IReadOnlyList<MediaStream> streams, string? qualityId, int? maxHeight)
    {
        if (streams.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(qualityId))
        {
            var exact = streams.FirstOrDefault(s => string.Equals(s.QualityId, qualityId, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;
        }

        if (maxHeight != null)
        {
            var matchingHeight = streams
                .Where(s => s.Height != null && s.Height <= maxHeight)
                .OrderByDescending(s => s.Height)
                .FirstOrDefault();

            if (matchingHeight != null)
                return matchingHeight;
        }

        // Highest quality by height or first available stream
        return streams
            .OrderByDescending(s => s.Height ?? 0)
            .FirstOrDefault();
    }

    private static int? ParseHeight(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var numMatch = Regex.Match(text, @"(\d{3,4})");
        if (numMatch.Success && int.TryParse(numMatch.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
            return h;

        if (text.Contains("4k", StringComparison.OrdinalIgnoreCase))
            return 2160;
        if (text.Contains("1080", StringComparison.OrdinalIgnoreCase) || text.Contains("fhd", StringComparison.OrdinalIgnoreCase))
            return 1080;
        if (text.Contains("720", StringComparison.OrdinalIgnoreCase) || text.Contains("hd", StringComparison.OrdinalIgnoreCase))
            return 720;
        if (text.Contains("480", StringComparison.OrdinalIgnoreCase) || text.Contains("sd", StringComparison.OrdinalIgnoreCase))
            return 480;
        if (text.Contains("360", StringComparison.OrdinalIgnoreCase))
            return 360;
        if (text.Contains("240", StringComparison.OrdinalIgnoreCase))
            return 240;

        return null;
    }

    private static string? ResolveUrl(string candidate, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        var clean = candidate.Trim().Replace(@"\/", "/");

        if (clean.StartsWith("//", StringComparison.Ordinal))
            return "https:" + clean;

        if (Uri.TryCreate(clean, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        if (Uri.TryCreate(new Uri(pageUrl), clean, out var relative))
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

    public sealed record MediaStream(
        string VideoUrl,
        string QualityId,
        int? Height,
        bool IsDefault);

    public sealed record ResolvedMediaPage(
        string PageUrl,
        string Title,
        List<MediaStream> Streams,
        string? Description,
        string? Date,
        string? PosterUrl,
        string? VideoId,
        List<string> TagNames);
}
