using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Gay0dayDownloader;

public sealed class Gay0dayDownloaderExtension : CoveExtensionBase, IDownloaderProvider
{
    public const string PrimaryDownloaderId = "karinauts.downloaders.gay0day.video";
    public const string DefaultStudioName = "Gay0Day";
    private static readonly string[] SupportedDomains = ["gay0day.com"];

    private static readonly Regex VideoIdRegex = new(@"/videos/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SponsoredByRegex = new(@"Sponsored by:\s*<a\b[^>]*>(?<studio>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ModelsBlockRegex = new(@"Models:\s*(?<models>.*?)(?:</div>|Tags:|Sponsored by:)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagsBlockRegex = new(@"Tags:\s*(?<tags>.*?)(?:</div>|Models:|Sponsored by:)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AnchorTextRegex = new(@"<a\b[^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private IServiceProvider? _services;

    private static readonly DownloaderDescriptor PrimaryDescriptor = new(
        PrimaryDownloaderId,
        "Gay0day Downloader",
        DownloaderEntity.Video,
        ["https://gay0day.com/*", "https://*.gay0day.com/*"],
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
            Label: pageInfo?.Title ?? DownloaderUtilities.DeriveTitleFromUrl(url, "Gay0day Downloader"));
    }

    public async Task<DownloaderResult?> DownloadAsync(
        DownloaderRequest request,
        IDownloaderHost host,
        CancellationToken ct)
    {
        if (!string.Equals(request.DownloaderId, PrimaryDescriptor.Id, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!IsSupportedUrl(request.Url))
            throw new InvalidOperationException($"Unsupported URL for Gay0day Downloader: {request.Url}");

        host.ReportProgress(5.0, "Inspecting media page...");
        var pageInfo = await InspectPageAsync(request.Url, ct);

        host.ReportProgress(10.0, "Starting media stream download...");
        var extension = DownloaderUtilities.EnsureExtensionFromUrl(pageInfo.MediaUrl.TrimEnd('/'), ".mp4");
        var originalFileName = DownloaderUtilities.SanitizeFileName(pageInfo.Title) + extension;
        var localFileName = "downloaded" + extension;

        var localPath = await DownloaderUtilities.DownloadToTempFileAsync(
            CreateHttpClient(host),
            pageInfo.MediaUrl,
            host,
            localFileName,
            ct);

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
                Urls = [request.SourceUrl ?? request.Url],
                StudioName = pageInfo.StudioName ?? DefaultStudioName,
                PerformerNames = pageInfo.PerformerNames,
                TagNames = pageInfo.TagNames
            });
    }

    public static bool IsSupportedUrl(string? url)
    {
        return DownloaderUtilities.IsHttpUrl(url)
            && DownloaderUtilities.IsAnyHost(url, SupportedDomains);
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

    internal static ResolvedMediaPage ParsePage(string html, string url)
    {
        var mediaUrl = ExtractMediaUrl(html, url)
            ?? throw new InvalidOperationException($"Could not locate a direct media stream on {url}");

        var fallbackTitle = DownloaderUtilities.DeriveTitleFromUrl(url, "Gay0day Video");
        var rawTitle = DownloaderUtilities.ExtractFirstMatch(html, @"(?is)<div\b[^>]*class\s*=\s*['""][^'""]*\bheadline\b[^'""]*['""][^>]*>\s*<h1[^>]*>(.*?)</h1>")
            ?? DownloaderUtilities.ExtractFirstMatch(html, @"(?is)<h1[^>]*>(.*?)</h1>")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:title")
            ?? DownloaderUtilities.ExtractHtmlTitle(html);

        var title = DownloaderUtilities.SanitizeFileName(rawTitle ?? fallbackTitle, fallbackTitle);
        if (title.EndsWith(" @ Gay0Day", StringComparison.OrdinalIgnoreCase))
            title = title[..^10].Trim();

        var description = DownloaderUtilities.ExtractMetaContent(html, "og:description")
            ?? DownloaderUtilities.ExtractMetaContent(html, "description");

        string? code = null;
        var codeMatch = VideoIdRegex.Match(url);
        if (codeMatch.Success)
            code = codeMatch.Groups["id"].Value;

        string? date = null;
        string? imageUrl = DownloaderUtilities.ExtractMetaContent(html, "og:image");
        string? studioName = null;
        var performers = new List<string>();
        var tags = new List<string>();

        // Inspect JSON-LD blocks
        foreach (var doc in DownloaderUtilities.ExtractJsonLdBlocks(html))
        {
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("@type", out var typeProp) && typeProp.ValueEquals("VideoObject"))
                {
                    if (string.IsNullOrWhiteSpace(title) || title == fallbackTitle)
                    {
                        if (root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                            title = nameProp.GetString() ?? title;
                    }

                    if (string.IsNullOrWhiteSpace(description) && root.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String)
                        description = descProp.GetString();

                    if (root.TryGetProperty("uploadDate", out var uploadDateProp) && uploadDateProp.ValueKind == JsonValueKind.String)
                        date = ScraperUtilities.NormalizeIsoDate(uploadDateProp.GetString());

                    if (string.IsNullOrWhiteSpace(imageUrl) && root.TryGetProperty("thumbnailUrl", out var thumbProp) && thumbProp.ValueKind == JsonValueKind.String)
                        imageUrl = thumbProp.GetString();
                }
            }
        }

        // HTML fallbacks / specifics for Gay0day
        var sponsorMatch = SponsoredByRegex.Match(html);
        if (sponsorMatch.Success)
        {
            var cleanedStudio = ScraperUtilities.CleanText(sponsorMatch.Groups["studio"].Value);
            if (!string.IsNullOrWhiteSpace(cleanedStudio))
                studioName = cleanedStudio;
        }

        var modelsMatch = ModelsBlockRegex.Match(html);
        if (modelsMatch.Success)
        {
            foreach (Match anchor in AnchorTextRegex.Matches(modelsMatch.Groups["models"].Value))
            {
                var name = ScraperUtilities.CleanText(anchor.Groups["text"].Value);
                if (!string.IsNullOrWhiteSpace(name))
                    performers.Add(name);
            }
        }

        var tagsMatch = TagsBlockRegex.Match(html);
        if (tagsMatch.Success)
        {
            foreach (Match anchor in AnchorTextRegex.Matches(tagsMatch.Groups["tags"].Value))
            {
                var tag = ScraperUtilities.CleanText(anchor.Groups["text"].Value);
                if (!string.IsNullOrWhiteSpace(tag))
                    tags.Add(tag);
            }
        }

        // If performers are still empty, check links with /models/
        if (performers.Count == 0)
        {
            foreach (Match match in Regex.Matches(html, @"(?is)<a\b[^>]*href=['""]https?://(?:www\.)?gay0day\.com/models/[^'""]+['""][^>]*>(?<name>[^<]+)</a>"))
            {
                var name = ScraperUtilities.CleanText(match.Groups["name"].Value);
                if (!string.IsNullOrWhiteSpace(name) && !name.Equals("PornStars", StringComparison.OrdinalIgnoreCase))
                    performers.Add(name);
            }
        }

        return new ResolvedMediaPage(
            PageUrl: url,
            Title: title,
            Code: code,
            MediaUrl: mediaUrl,
            Description: description,
            Date: date,
            ImageUrl: imageUrl,
            StudioName: studioName ?? DefaultStudioName,
            PerformerNames: ScraperUtilities.DeduplicateStrings(performers),
            TagNames: ScraperUtilities.DeduplicateStrings(tags));
    }

    internal static string? ExtractMediaUrl(string html, string pageUrl)
    {
        // 1. <video ...> <source src="..." type="video/mp4" /> </video>
        var sourceMatch = Regex.Match(html, @"(?is)<video\b[^>]*>.*?<source\b[^>]*\bsrc\s*=\s*['""](?<url>[^'""]+)['""]");
        if (sourceMatch.Success)
        {
            var raw = sourceMatch.Groups["url"].Value.Trim();
            if (ResolveUrl(raw, pageUrl) is { } resolved)
                return resolved;
        }

        // 2. Generic <source src="..." />
        var directSource = Regex.Match(html, @"(?is)<source\b[^>]*\bsrc\s*=\s*['""](?<url>[^'""]+)['""][^>]*type\s*=\s*['""]video/mp4['""]");
        if (directSource.Success)
        {
            var raw = directSource.Groups["url"].Value.Trim();
            if (ResolveUrl(raw, pageUrl) is { } resolved)
                return resolved;
        }

        // 3. JSON-LD contentUrl
        foreach (var doc in DownloaderUtilities.ExtractJsonLdBlocks(html))
        {
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("contentUrl", out var contentUrlProp) && contentUrlProp.ValueKind == JsonValueKind.String)
                {
                    var raw = contentUrlProp.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(raw) && ResolveUrl(raw, pageUrl) is { } resolved)
                        return resolved;
                }
            }
        }

        // 4. OpenGraph video meta
        var candidate = DownloaderUtilities.ExtractMetaContent(html, "og:video:secure_url")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:video:url")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:video");

        if (!string.IsNullOrWhiteSpace(candidate) && ResolveUrl(candidate, pageUrl) is { } ogResolved)
            return ogResolved;

        // 5. Raw mp4 stream link
        var rawMatch = Regex.Match(html, @"(?is)(https?://[^'""<>\s]+\.(?:mp4|m4v)(?:[/?][^'""<>\s]*)?)");
        if (rawMatch.Success && ResolveUrl(rawMatch.Value, pageUrl) is { } linkResolved)
            return linkResolved;

        return null;
    }

    private static string? ResolveUrl(string candidate, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

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

    internal sealed record ResolvedMediaPage(
        string PageUrl,
        string Title,
        string? Code,
        string MediaUrl,
        string? Description,
        string? Date,
        string? ImageUrl,
        string? StudioName,
        List<string> PerformerNames,
        List<string> TagNames);
}
