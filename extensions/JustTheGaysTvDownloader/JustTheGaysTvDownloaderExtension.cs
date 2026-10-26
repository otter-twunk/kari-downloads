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

namespace JustTheGaysTvDownloader;

public sealed class JustTheGaysTvDownloaderExtension : CoveExtensionBase, IDownloaderProvider
{
    public const string PrimaryDownloaderId = "karinauts.downloaders.just-the-gays-tv.video";
    public const string DefaultStudioName = "JustTheGays";

    private static readonly string[] SupportedDomains =
    [
        "justthegays.tv",
        "cdn.gff.network"
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

    private static readonly Regex TvCategoryLinkRegex = new(
        @"href\s*=\s*['""]/(?:\d+/)?category/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TvPerformerLinkRegex = new(
        @"href\s*=\s*['""]/performer/[^'""]+['""][^>]*>(?<name>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

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
        "JustTheGays.tv Video Downloader",
        DownloaderEntity.Video,
        [
            "https://justthegays.tv/*",
            "https://*.justthegays.tv/*",
            "http://justthegays.tv/*",
            "http://*.justthegays.tv/*",
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

        if (DownloaderUtilities.IsHost(uri, "cdn.gff.network"))
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
                    return $"https://justthegays.tv/clip/{slug}";
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
        var uri = new Uri(url);

        if (DownloaderUtilities.IsHost(uri, "cdn.gff.network")
            || uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var fallbackTitle = DownloaderUtilities.DeriveTitleFromUrl(url, DefaultStudioName);
            return new ResolvedMediaPage(
                PageUrl: url,
                Title: fallbackTitle,
                MediaUrl: url,
                StudioName: DefaultStudioName,
                Code: null,
                Date: null,
                Description: null,
                ImageUrl: null,
                PerformerNames: [],
                TagNames: [],
                QualityOptions: null);
        }

        var html = await ScraperUtilities.FetchHtmlAsync(client, url, ct);
        return ParseJustTheGaysTvHtml(html, url);
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
            PerformerNames: ScraperUtilities.DeduplicateStrings(performers),
            TagNames: ScraperUtilities.DeduplicateStrings(tags),
            QualityOptions: qualityOptions);
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
                ?? _configuration?["Extensions:karinauts.downloaders.just-the-gays-tv:ffmpegPath"];
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
        List<string> PerformerNames,
        List<string> TagNames,
        IReadOnlyList<DownloaderQualityOption>? QualityOptions);
}
