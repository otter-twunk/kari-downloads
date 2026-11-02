using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JustTheGaysTvDownloader;

public sealed class JustTheGaysTvDownloaderExtension : CoveExtensionBase, IDownloaderProvider
{
    public const string PrimaryDownloaderId = "karinauts.downloaders.just-the-gays-tv.video";
    public const string DefaultStudioName = "JustTheGays";

    private static readonly string[] SupportedDomains =
    [
        "justthegays.tv",
        "gff.network",
        "jtg.network"
    ];

    private static readonly Regex SceneOrClipPathRegex = new(
        @"^/(?:p\d+/)?(?<kind>video|embed|clip)/(?<slug>[a-zA-Z0-9_-]+)/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TvVideoSrcRegex = new(
        @"videoSrc\s*:\s*""(?<url>https?://[^""\\]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TvBootstrapVideoRegex = new(
        @"video:\{(?<video>.*?)\},(?:inQuickies|related):",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TvBootstrapClipRegex = new(
        @"clip:\{(?<clip>.*?)\},(?:siblings|pindex|related|uses):",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TvPerformerSpanRegex = new(
        @"(?is)<span\b[^>]*class\s*=\s*['""][^'""]*performer-with-follow[^'""]*['""][^>]*>.*?<a\b[^>]*href\s*=\s*['""]/(?:\d+/)?performer/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.Compiled);

    private static readonly Regex TvFeaturingParagraphRegex = new(
        @"(?is)<p\b[^>]*class\s*=\s*['""][^'""]*featuring[^'""]*['""][^>]*>(?<block>.*?)</p>",
        RegexOptions.Compiled);

    private static readonly Regex TvAnchorPerformerRegex = new(
        @"(?is)<a\b[^>]*href\s*=\s*['""]/(?:\d+/)?performer/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.Compiled);

    private static readonly Regex TvCategorySpanRegex = new(
        @"(?is)<span\b[^>]*class\s*=\s*['""][^'""]*badge[^'""]*['""][^>]*>.*?<a\b[^>]*href\s*=\s*['""]/(?:\d+/)?category/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.Compiled);

    private static readonly Regex TvFiledUnderParagraphRegex = new(
        @"(?is)<p\b[^>]*class\s*=\s*['""][^'""]*filed-under[^'""]*['""][^>]*>(?<block>.*?)</p>",
        RegexOptions.Compiled);

    private static readonly Regex TvAnchorCategoryRegex = new(
        @"(?is)<a\b[^>]*href\s*=\s*['""]/(?:\d+/)?category/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.Compiled);

    private static readonly Regex HlsStreamInfRegex = new(
        @"#EXT-X-STREAM-INF:(?<attrs>[^\r\n]+)\r?\n(?<uri>[^\r\n#]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HlsMapUriRegex = new(
        @"#EXT-X-MAP:[^\r\n]*URI=""(?<uri>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FfmpegTimeRegex = new(
        @"time=(?<hours>\d{2}):(?<mins>\d{2}):(?<secs>\d{2}(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, (ResolvedMediaPage Page, DateTime Expiry)> _inspectedPageCache =
        new(StringComparer.OrdinalIgnoreCase);

    private IServiceProvider? _services;
    private IConfiguration? _configuration;

    private static readonly DownloaderDescriptor PrimaryDescriptor = new(
        PrimaryDownloaderId,
        "JustTheGays.tv Video Downloader",
        DownloaderEntity.Video,
        [
            "https://justthegays.tv/*",
            "https://*.justthegays.tv/*",
            "http://justthegays.tv/*",
            "http://*.justthegays.tv/*",
            "https://*.gff.network/*",
            "https://*.jtg.network/*"
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
            Label: pageInfo?.Title ?? DownloaderUtilities.DeriveTitleFromUrl(normalizedUrl, "JustTheGays.tv Video"),
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
            throw new InvalidOperationException($"Unsupported URL for JustTheGays.tv Downloader: {request.Url}");

        host.ReportProgress(5.0, "Inspecting JustTheGays.tv media page...");
        var client = CreateHttpClient(host);
        var normalizedUrl = NormalizeSupportedUrl(request.Url);
        var pageInfo = await InspectPageAsync(normalizedUrl, client, ct);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = "https://justthegays.tv/"
        };

        string localPath;
        if (IsHlsUrl(pageInfo.MediaUrl))
        {
            localPath = await DownloadHlsStreamAsync(
                client,
                pageInfo.MediaUrl,
                pageInfo.DurationSeconds,
                request,
                host,
                headers,
                ct);
        }
        else
        {
            var extension = DownloaderUtilities.EnsureExtensionFromUrl(pageInfo.MediaUrl, ".mp4");
            var localFileName = "downloaded" + extension;
            localPath = await DownloadDirectWithResumeAsync(
                client,
                pageInfo.MediaUrl,
                host,
                localFileName,
                headers,
                ct);
        }

        var finalExtension = Path.GetExtension(localPath);
        if (string.IsNullOrWhiteSpace(finalExtension))
            finalExtension = ".mp4";

        var originalFileName = DownloaderUtilities.SanitizeFileName(pageInfo.Title, "justthegays-tv-video") + finalExtension;
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

        if (DownloaderUtilities.IsHost(uri, "gff.network") || DownloaderUtilities.IsHost(uri, "jtg.network"))
        {
            return uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
        }

        if (DownloaderUtilities.IsHost(uri, "justthegays.tv"))
            return SceneOrClipPathRegex.IsMatch(uri.AbsolutePath);

        return false;
    }

    public static string NormalizeSupportedUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url;

        if (DownloaderUtilities.IsHost(uri, "justthegays.tv"))
        {
            var match = SceneOrClipPathRegex.Match(uri.AbsolutePath);
            if (match.Success)
            {
                var kind = match.Groups["kind"].Value.ToLowerInvariant();
                var slug = match.Groups["slug"].Value;
                if (kind == "embed")
                    return $"https://justthegays.tv/video/{slug}";
                if (kind == "clip")
                {
                    var pMatch = Regex.Match(uri.AbsolutePath, @"^/(p\d+)/");
                    return pMatch.Success
                        ? $"https://justthegays.tv/{pMatch.Groups[1].Value}/clip/{slug}"
                        : $"https://justthegays.tv/clip/{slug}";
                }
                return $"https://justthegays.tv/video/{slug}";
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
        if (_inspectedPageCache.TryGetValue(url, out var entry) && entry.Expiry > DateTime.UtcNow)
            return entry.Page;

        var uri = new Uri(url);

        if (DownloaderUtilities.IsHost(uri, "gff.network")
            || DownloaderUtilities.IsHost(uri, "jtg.network")
            || uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var fallbackTitle = DownloaderUtilities.DeriveTitleFromUrl(url, DefaultStudioName);
            var page = new ResolvedMediaPage(
                PageUrl: url,
                Title: fallbackTitle,
                MediaUrl: url,
                StudioName: DefaultStudioName,
                Code: null,
                Date: null,
                Description: null,
                ImageUrl: null,
                DurationSeconds: null,
                PerformerNames: [],
                TagNames: [],
                QualityOptions: null);

            _inspectedPageCache[url] = (page, DateTime.UtcNow.AddMinutes(5));
            return page;
        }

        var html = await FetchHtmlWithRetryAsync(client, url, ct);
        var parsed = ParseJustTheGaysTvHtml(html, url);
        _inspectedPageCache[url] = (parsed, DateTime.UtcNow.AddMinutes(5));
        return parsed;
    }

    internal static ResolvedMediaPage ParseJustTheGaysTvHtml(string html, string pageUrl)
    {
        string? title = null;
        string? description = null;
        string? date = null;
        string? imageUrl = null;
        string? mediaUrl = null;
        double? durationSeconds = null;
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
                    ref durationSeconds,
                    performers);
            }
        }

        var has720P = false;
        var has1080P = false;

        var bootstrapMatch = TvBootstrapVideoRegex.Match(html);
        if (bootstrapMatch.Success)
        {
            var videoBlock = bootstrapMatch.Groups["video"].Value;

            title ??= ExtractJsStringProperty(videoBlock, "jtg_title")
                ?? ExtractJsStringProperty(videoBlock, "title");

            date ??= ScraperUtilities.NormalizeIsoDate(ExtractJsStringProperty(videoBlock, "upload_date"));

            var durationMatch = Regex.Match(videoBlock, @"\bduration\s*:\s*(?<sec>\d+(?:\.\d+)?)");
            if (durationMatch.Success && double.TryParse(durationMatch.Groups["sec"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSec))
                durationSeconds ??= parsedSec;

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

        var clipMatch = TvBootstrapClipRegex.Match(html);
        if (clipMatch.Success)
        {
            var clipBlock = clipMatch.Groups["clip"].Value;

            title ??= ExtractJsStringProperty(clipBlock, "title");
            date ??= ScraperUtilities.NormalizeIsoDate(
                ExtractJsStringProperty(clipBlock, "readyAt")
                ?? ExtractJsStringProperty(clipBlock, "createdAt"));

            var clipDurMatch = Regex.Match(clipBlock, @"\bduration\s*:\s*(?<sec>\d+(?:\.\d+)?)");
            if (clipDurMatch.Success && double.TryParse(clipDurMatch.Groups["sec"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cSec))
                durationSeconds ??= cSec;

            var clipCategoriesMatch = Regex.Match(clipBlock, @"categories\s*:\s*\[(?<items>.*?)\]", RegexOptions.Singleline);
            if (clipCategoriesMatch.Success)
            {
                foreach (Match nameMatch in Regex.Matches(clipCategoriesMatch.Groups["items"].Value, @"name\s*:\s*""(?<val>(?:[^""\\]|\\.)+)"""))
                {
                    tags.Add(Regex.Unescape(nameMatch.Groups["val"].Value));
                }
            }

            var clipPerformersMatch = Regex.Match(clipBlock, @"performers\s*:\s*\[(?<items>.*?)\]", RegexOptions.Singleline);
            if (clipPerformersMatch.Success)
            {
                foreach (Match nameMatch in Regex.Matches(clipPerformersMatch.Groups["items"].Value, @"name\s*:\s*""(?<val>(?:[^""\\]|\\.)+)"""))
                {
                    var name = Regex.Unescape(nameMatch.Groups["val"].Value).Trim();
                    if (!string.IsNullOrWhiteSpace(name) && !name.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                        performers.Add(name);
                }
            }

            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                var clipUrl = ExtractJsStringProperty(clipBlock, "url");
                if (!string.IsNullOrWhiteSpace(clipUrl) && clipUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    mediaUrl = clipUrl;
            }
        }

        var videoSrcMatch = TvVideoSrcRegex.Match(html);
        if (videoSrcMatch.Success && string.IsNullOrWhiteSpace(mediaUrl))
            mediaUrl = WebUtility.HtmlDecode(videoSrcMatch.Groups["url"].Value).Trim();

        mediaUrl ??= DownloaderUtilities.ExtractMetaContent(html, "og:video")
            ?? DownloaderUtilities.ExtractMetaContent(html, "twitter:player:stream")
            ?? ExtractGenericMediaUrl(html, pageUrl);

        if (string.IsNullOrWhiteSpace(mediaUrl))
            throw new InvalidOperationException($"Could not locate a video stream URL on {pageUrl}");

        if (performers.Count == 0)
        {
            foreach (Match match in TvPerformerSpanRegex.Matches(html))
            {
                var name = ScraperUtilities.CleanText(Regex.Replace(match.Groups["name"].Value, "<.*?>", string.Empty));
                if (!string.IsNullOrWhiteSpace(name) && !name.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                    performers.Add(name);
            }

            if (performers.Count == 0)
            {
                var featuringMatch = TvFeaturingParagraphRegex.Match(html);
                if (featuringMatch.Success)
                {
                    foreach (Match match in TvAnchorPerformerRegex.Matches(featuringMatch.Groups["block"].Value))
                    {
                        var name = ScraperUtilities.CleanText(Regex.Replace(match.Groups["name"].Value, "<.*?>", string.Empty));
                        if (!string.IsNullOrWhiteSpace(name) && !name.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                            performers.Add(name);
                    }
                }
            }
        }

        if (tags.Count == 0)
        {
            foreach (Match match in TvCategorySpanRegex.Matches(html))
            {
                if (match.Value.Contains("performer-with-follow", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = ScraperUtilities.CleanText(Regex.Replace(match.Groups["name"].Value, "<.*?>", string.Empty));
                if (!string.IsNullOrWhiteSpace(name))
                    tags.Add(name);
            }

            if (tags.Count == 0)
            {
                var filedUnderMatch = TvFiledUnderParagraphRegex.Match(html);
                if (filedUnderMatch.Success)
                {
                    foreach (Match match in TvAnchorCategoryRegex.Matches(filedUnderMatch.Groups["block"].Value))
                    {
                        var name = ScraperUtilities.CleanText(Regex.Replace(match.Groups["name"].Value, "<.*?>", string.Empty));
                        if (!string.IsNullOrWhiteSpace(name))
                            tags.Add(name);
                    }
                }
            }
        }

        title ??= CleanTvTitle(DownloaderUtilities.ExtractHtmlTitle(html))
            ?? DownloaderUtilities.DeriveTitleFromUrl(pageUrl, DefaultStudioName);

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
            Title: CleanTvTitle(title) ?? "JustTheGays.tv Video",
            MediaUrl: mediaUrl,
            StudioName: DefaultStudioName,
            Code: code,
            Date: date,
            Description: cleanedDescription,
            ImageUrl: imageUrl,
            DurationSeconds: durationSeconds,
            PerformerNames: ScraperUtilities.DeduplicateStrings(performers),
            TagNames: ScraperUtilities.DeduplicateStrings(tags),
            QualityOptions: qualityOptions);
    }

    private async Task<string> DownloadHlsStreamAsync(
        HttpClient client,
        string m3u8Url,
        double? durationSeconds,
        DownloaderRequest request,
        IDownloaderHost host,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        Directory.CreateDirectory(host.TempDirectory);
        host.ReportProgress(8.0, "Fetching HLS playlist manifest...");

        var manifestText = await FetchHtmlWithRetryAsync(client, m3u8Url, ct, headers);
        var activePlaylistUrl = m3u8Url;

        if (manifestText.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
        {
            var variantUrl = SelectHlsVariantUrl(manifestText, m3u8Url, request.QualityId, request.MaxHeight);
            if (!string.IsNullOrWhiteSpace(variantUrl))
            {
                activePlaylistUrl = variantUrl;
                manifestText = await FetchHtmlWithRetryAsync(client, activePlaylistUrl, ct, headers);
            }
        }

        var mp4Path = Path.Combine(host.TempDirectory, "downloaded.mp4");
        if (File.Exists(mp4Path))
            File.Delete(mp4Path);

        var ffmpegBinary = ResolveFfmpegPath(_configuration);
        if (IsFfmpegAvailable(ffmpegBinary))
        {
            host.ReportProgress(12.0, "Streaming and remuxing HLS via FFmpeg...");
            var ffmpegSuccess = await TryDownloadHlsWithFfmpegAsync(
                ffmpegBinary,
                activePlaylistUrl,
                mp4Path,
                durationSeconds,
                headers,
                host,
                ct);

            if (ffmpegSuccess && File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0)
                return mp4Path;
        }

        // Resilient fallback segment downloader (used when FFmpeg is not installed or when running offline tests)
        return await DownloadHlsSegmentsDirectAsync(
            client,
            activePlaylistUrl,
            manifestText,
            mp4Path,
            host,
            headers,
            ct);
    }

    private async Task<bool> TryDownloadHlsWithFfmpegAsync(
        string ffmpegBinary,
        string m3u8Url,
        string outputMp4Path,
        double? totalDurationSeconds,
        IReadOnlyDictionary<string, string> headers,
        IDownloaderHost host,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegBinary,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("info");

            var headerSb = new StringBuilder();
            headerSb.Append($"User-Agent: {DownloaderUtilities.DefaultUserAgent}\r\n");
            foreach (var (k, v) in headers)
                headerSb.Append($"{k}: {v}\r\n");

            psi.ArgumentList.Add("-headers");
            psi.ArgumentList.Add(headerSb.ToString());

            psi.ArgumentList.Add("-reconnect");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-reconnect_at_eof");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-reconnect_streamed");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-reconnect_delay_max");
            psi.ArgumentList.Add("5");

            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(m3u8Url);

            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-bsf:a");
            psi.ArgumentList.Add("aac_adtstoasc");
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
            psi.ArgumentList.Add(outputMp4Path);

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return false;

            var readErrorTask = Task.Run(async () =>
            {
                while (!process.HasExited && !ct.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync(ct);
                    if (line == null) break;

                    var timeMatch = FfmpegTimeRegex.Match(line);
                    if (timeMatch.Success)
                    {
                        var h = double.Parse(timeMatch.Groups["hours"].Value, CultureInfo.InvariantCulture);
                        var m = double.Parse(timeMatch.Groups["mins"].Value, CultureInfo.InvariantCulture);
                        var s = double.Parse(timeMatch.Groups["secs"].Value, CultureInfo.InvariantCulture);
                        var currentSecs = (h * 3600) + (m * 60) + s;

                        if (totalDurationSeconds is > 0)
                        {
                            var fraction = Math.Clamp(currentSecs / totalDurationSeconds.Value, 0.0, 1.0);
                            var progress = 12.0 + (82.0 * fraction);
                            host.ReportProgress(progress, $"Streaming HLS {currentSecs:F0}s / {totalDurationSeconds.Value:F0}s");
                        }
                        else
                        {
                            host.ReportProgress(50.0, $"Streaming HLS {currentSecs:F0}s processed...");
                        }
                    }
                }
            }, ct);

            await process.WaitForExitAsync(ct);
            await readErrorTask;

            return process.ExitCode == 0
                && File.Exists(outputMp4Path)
                && new FileInfo(outputMp4Path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> DownloadHlsSegmentsDirectAsync(
        HttpClient client,
        string activePlaylistUrl,
        string manifestText,
        string mp4Path,
        IDownloaderHost host,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        var segmentUrls = ParseHlsSegmentUrls(manifestText, activePlaylistUrl);
        if (segmentUrls.Count == 0)
            throw new InvalidOperationException($"HLS playlist at {activePlaylistUrl} contained no downloadable media segments.");

        var stagingTsPath = Path.Combine(host.TempDirectory, "downloaded_segments.ts");
        if (File.Exists(stagingTsPath))
            File.Delete(stagingTsPath);

        const int batchSize = 4;
        await using (var output = File.Create(stagingTsPath))
        {
            for (var i = 0; i < segmentUrls.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var currentBatchSize = Math.Min(batchSize, segmentUrls.Count - i);
                var batchTasks = new Task<byte[]>[currentBatchSize];

                for (var b = 0; b < currentBatchSize; b++)
                {
                    var segIdx = i + b;
                    var segUrl = segmentUrls[segIdx];
                    batchTasks[b] = DownloadSegmentWithRetryAsync(client, segUrl, segIdx, segmentUrls.Count, headers, ct);
                }

                var batchResults = await Task.WhenAll(batchTasks);
                foreach (var segBytes in batchResults)
                {
                    await output.WriteAsync(segBytes, ct);
                }

                var downloadedCount = i + currentBatchSize;
                var progress = 10.0 + (82.0 * downloadedCount / segmentUrls.Count);
                host.ReportProgress(progress, $"Downloaded HLS segment {downloadedCount}/{segmentUrls.Count}");
            }
        }

        if (await TryRemuxTsToMp4Async(stagingTsPath, mp4Path, host, ct))
        {
            try { File.Delete(stagingTsPath); } catch { /* best effort cleanup */ }
            return mp4Path;
        }

        File.Move(stagingTsPath, mp4Path, overwrite: true);
        return mp4Path;
    }

    private static async Task<byte[]> DownloadSegmentWithRetryAsync(
        HttpClient client,
        string segmentUrl,
        int segmentIndex,
        int totalSegments,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 1)
            {
                var delayMs = (500 * (1 << (attempt - 1))) + Random.Shared.Next(100, 300);
                await Task.Delay(delayMs, ct);
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, segmentUrl);
                request.Headers.UserAgent.TryParseAdd(DownloaderUtilities.DefaultUserAgent);
                request.Headers.AcceptLanguage.TryParseAdd("en-US,en;q=0.9");
                foreach (var (key, value) in headers)
                    request.Headers.TryAddWithoutValidation(key, value);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfterSeconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 2.0;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(retryAfterSeconds, 10.0)), ct);
                    continue;
                }

                if (response.Headers.TryGetValues("x-ratelimit-remaining", out var rateLimitVals))
                {
                    var firstVal = rateLimitVals.FirstOrDefault();
                    if (int.TryParse(firstVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rem) && rem < 50)
                    {
                        await Task.Delay(1000, ct);
                    }
                }

                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    if (code is >= 500 and <= 504)
                        continue;

                    response.EnsureSuccessStatusCode();
                }

                return await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
            {
                lastException = ex;
            }
        }

        throw new InvalidOperationException(
            $"Failed to download HLS segment {segmentIndex + 1}/{totalSegments} after 4 attempts from {segmentUrl}: {lastException?.Message}",
            lastException);
    }

    private static async Task<string> DownloadDirectWithResumeAsync(
        HttpClient client,
        string mediaUrl,
        IDownloaderHost host,
        string localFileName,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        Directory.CreateDirectory(host.TempDirectory);
        var sanitizedLocalName = DownloaderUtilities.SanitizeFileName(localFileName, "downloaded.mp4");
        var destinationPath = Path.Combine(host.TempDirectory, sanitizedLocalName);

        Exception? lastEx = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 1)
            {
                var delayMs = (1000 * (1 << (attempt - 1))) + Random.Shared.Next(100, 300);
                await Task.Delay(delayMs, ct);
            }

            try
            {
                var existingLength = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0L;

                using var request = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
                request.Headers.UserAgent.TryParseAdd(DownloaderUtilities.DefaultUserAgent);
                request.Headers.AcceptLanguage.TryParseAdd("en-US,en;q=0.9");
                foreach (var (k, v) in headers)
                    request.Headers.TryAddWithoutValidation(k, v);

                if (existingLength > 0)
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfterSecs = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 3.0;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(retryAfterSecs, 10.0)), ct);
                    continue;
                }

                if (existingLength > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    // Server didn't honor range request; reset file and download full stream
                    File.Delete(destinationPath);
                    existingLength = 0;
                }

                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength.GetValueOrDefault() + existingLength;
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(destinationPath, existingLength > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[64 * 1024];
                    var totalRead = existingLength;
                    int bytesRead;

                    while ((bytesRead = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        totalRead += bytesRead;

                        if (totalBytes > 0)
                        {
                            var fraction = Math.Clamp((double)totalRead / totalBytes, 0.0, 1.0);
                            var progress = 10.0 + (85.0 * fraction);
                            host.ReportProgress(progress, $"Downloaded {totalRead / 1024:N0} KB of {totalBytes / 1024:N0} KB");
                        }
                    }
                }

                return destinationPath;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
            {
                lastEx = ex;
            }
        }

        throw new InvalidOperationException($"Failed to download direct media stream after 4 attempts from {mediaUrl}: {lastEx?.Message}", lastEx);
    }

    private static async Task<string> FetchHtmlWithRetryAsync(
        HttpClient client,
        string url,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        Exception? lastEx = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 1)
            {
                var delay = (400 * (1 << (attempt - 1))) + Random.Shared.Next(50, 200);
                await Task.Delay(delay, ct);
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.TryParseAdd(DownloaderUtilities.DefaultUserAgent);
                request.Headers.AcceptLanguage.TryParseAdd("en-US,en;q=0.9");
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
                request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
                request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"macOS\"");
                request.Headers.TryAddWithoutValidation("sec-fetch-dest", "document");
                request.Headers.TryAddWithoutValidation("sec-fetch-mode", "navigate");
                request.Headers.TryAddWithoutValidation("sec-fetch-site", "none");
                request.Headers.TryAddWithoutValidation("upgrade-insecure-requests", "1");

                if (headers != null)
                {
                    foreach (var (k, v) in headers)
                        request.Headers.TryAddWithoutValidation(k, v);
                }

                using var response = await client.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 2.0;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(retryAfter, 8.0)), ct);
                    continue;
                }

                if ((int)response.StatusCode is >= 500 and <= 504)
                    continue;

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
            {
                lastEx = ex;
            }
        }

        throw new InvalidOperationException($"Failed to fetch HTML from {url} after 3 attempts: {lastEx?.Message}", lastEx);
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
            var ffmpegBinary = ResolveFfmpegPath(_configuration);
            if (!IsFfmpegAvailable(ffmpegBinary))
                return false;

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
            process.StartInfo.ArgumentList.Add("-bsf:a");
            process.StartInfo.ArgumentList.Add("aac_adtstoasc");
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

    public static string ResolveFfmpegPath(IConfiguration? configuration)
    {
        var configured = configuration?["ffmpegPath"]
            ?? configuration?["Extensions:karinauts.downloaders.just-the-gays-tv:ffmpegPath"];
        if (!string.IsNullOrWhiteSpace(configured) && (File.Exists(configured) || !configured.Contains(Path.DirectorySeparatorChar)))
            return configured.Trim();

        string[] candidates = OperatingSystem.IsWindows()
            ? [
                "ffmpeg.exe",
                @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
                @"C:\ffmpeg\bin\ffmpeg.exe"
              ]
            : [
                "ffmpeg",
                "/opt/homebrew/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "/usr/bin/ffmpeg"
              ];

        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate) && File.Exists(candidate))
                return candidate;
        }

        return "ffmpeg";
    }

    internal static bool IsFfmpegAvailable(string ffmpegBinary)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegBinary,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (proc == null) return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
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
        ref double? durationSeconds,
        List<string> performers)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                InspectTvJsonLd(item, ref title, ref description, ref date, ref imageUrl, ref mediaUrl, ref durationSeconds, performers);
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

        var durationIso = TryGetJsonString(element, "duration");
        if (!string.IsNullOrWhiteSpace(durationIso))
        {
            try
            {
                var ts = System.Xml.XmlConvert.ToTimeSpan(durationIso);
                durationSeconds ??= ts.TotalSeconds;
            }
            catch
            {
                // ignore unparseable ISO durations
            }
        }

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
        var options = new List<DownloaderQualityOption>();
        if (has1080P)
            options.Add(new DownloaderQualityOption("height:1080", "1080p Full HD") { Width = 1920, Height = 1080 });
        if (has720P || !has1080P)
            options.Add(new DownloaderQualityOption("height:720", "720p HD") { Width = 1280, Height = 720 });
        options.Add(new DownloaderQualityOption("height:480", "Standard") { Width = 854, Height = 480 });
        return options;
    }

    private static string? ExtractGenericMediaUrl(string html, string pageUrl)
    {
        var rawMp4 = DownloaderUtilities.ExtractFirstRawMatch(
            html,
            @"(https?://[^'""<>\s\\]+\.(?:mp4|m4v|m3u8)(?:\?[^'""<>\s\\]+)?)");

        return string.IsNullOrWhiteSpace(rawMp4) ? null : WebUtility.HtmlDecode(rawMp4).Trim();
    }

    private static string? CleanTvTitle(string? rawTitle)
    {
        var cleaned = ScraperUtilities.CleanText(rawTitle);
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        cleaned = Regex.Replace(cleaned, @"\s*\|\s*JustTheGays.*$", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*[-–—]\s*(?:gay porn|stream amateur gay porn|[^-–—|]*\bPorn\b).*$", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*[-–—]\s*JustTheGays(?:\.tv)?\s*$", string.Empty, RegexOptions.IgnoreCase);
        return ScraperUtilities.CleanText(cleaned);
    }

    private static string? CleanTvDetails(string? rawDetails)
    {
        var text = ScraperUtilities.CleanText(rawDetails);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = Regex.Replace(text, @"\s+Find it here on JustTheGays.*$", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"^Get off while streaming .*? in the (?:steamy|erotic|hot|sexy|wild|insane|horny|sensual|naughty|mind-blowing) new video [""“].*?[""”]\.?\s*", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(?:^|\s+)This video has plenty of (?:amazing|exciting|mind-blowing|wild|insane|hot|unbelievable|raw|intense|steamy) .*? action that will .*?\.(?:\s+|$)", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s*Stream '.*?' and more hot .*?$", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s*Watch all \d+ .*? videos streaming .*?$", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s*All \d+ .*? scenes stream free .*?$", string.Empty, RegexOptions.IgnoreCase);
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
        string? Code,
        string? Date,
        string? Description,
        string? ImageUrl,
        double? DurationSeconds,
        List<string> PerformerNames,
        List<string> TagNames,
        IReadOnlyList<DownloaderQualityOption>? QualityOptions);
}
