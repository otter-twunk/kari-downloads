using System.IO.Compression;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace XcomDownloader;

public sealed class XcomDownloaderExtension : CoveExtensionBase, IDownloaderProvider
{
    public const string PrimaryDownloaderId = "karinauts.downloaders.xcom.video";
    public const string ImageDownloaderId = "karinauts.downloaders.xcom.image";
    public const string GalleryDownloaderId = "karinauts.downloaders.xcom.gallery";
    private readonly YtDlpResolver _resolver;
    private IConfiguration? _configuration;
    private IHttpClientFactory? _clients;
    public XcomDownloaderExtension() : this(new XcomProcessRunner()) { }
    public XcomDownloaderExtension(IXcomProcessRunner runner) => _resolver = new(runner);
    public override void ConfigureServices(IServiceCollection services, ExtensionContext context) => _configuration = context.Configuration;
    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _configuration ??= services.GetService<IConfiguration>();
        _clients = services.GetService<IHttpClientFactory>();
        return Task.CompletedTask;
    }
    private string Executable => YtDlpLocator.Resolve(_configuration, Environment.GetEnvironmentVariable("COVE_HOME"), typeof(XcomDownloaderExtension).Assembly.Location);
    private bool PhotoFallback => bool.TryParse(_configuration?["enablePublicPhotoFallback"], out var enabled) && enabled;
    private string? CookiesFile => _configuration?["cookiesFile"];
    public static bool IsSupportedUrl(string? url) => XcomUrlParser.TryParse(url, out _);
    public IReadOnlyList<DownloaderDescriptor> GetDownloaders()
    {
        string[] patterns = ["https://x.com/*", "https://*.x.com/*", "https://twitter.com/*", "https://*.twitter.com/*",
            "http://x.com/*", "http://*.x.com/*", "http://twitter.com/*", "http://*.twitter.com/*"];
        return [new(PrimaryDownloaderId, "X / Twitter video", DownloaderEntity.Video, patterns,
                    DownloaderCapabilities.InlineMetadata | DownloaderCapabilities.MultiQuality),
                new(ImageDownloaderId, "X / Twitter public photo", DownloaderEntity.Image, patterns, DownloaderCapabilities.InlineMetadata),
                new(GalleryDownloaderId, "X / Twitter public photos", DownloaderEntity.Gallery, patterns, DownloaderCapabilities.InlineMetadata)];
    }
    public async Task<DownloaderUrlMatch?> MatchAsync(string url, CancellationToken ct) => (await MatchAllAsync(url, ct)).FirstOrDefault();
    public async Task<IReadOnlyList<DownloaderUrlMatch>> MatchAllAsync(string url, CancellationToken ct)
    {
        if (!XcomUrlParser.TryParse(url, out var status)) return [];
        string? failure = null;
        try
        {
            var media = await _resolver.ResolveAsync(status, Executable, ct, CookiesFile);
            if (status.MediaId != null) media = media.Where(m => m.Id == status.MediaId).ToArray();
            if (media.Count > 0)
                return media.Select(m => new DownloaderUrlMatch(PrimaryDownloaderId,
                    media.Count > 1 ? status.CanonicalUrl + "#cove-media=" + m.Id : urlFor(m),
                    m.Formats.OrderByDescending(f => f.Height).Select(f => new DownloaderQualityOption(f.Id,
                        f.Height is { } h ? $"{h}p ({f.Id})" : f.Id) { Height = f.Height, Width = f.Width }).ToArray(),
                    Label: m.Title, SourceUrl: status.CanonicalUrl)).ToArray();
            string urlFor(XcomMedia m) => status.MediaId == null ? status.CanonicalUrl : status.CanonicalUrl + "#cove-media=" + m.Id;
        }
        catch (InvalidOperationException ex) { failure = ex.Message; }
        if (PhotoFallback && _clients != null && status.MediaId == null)
        {
            try
            {
                using var client = _clients.CreateClient(ImageDownloaderId);
                var photos = await InspectPhotosAsync(client, status, ct);
                return [new(photos.Urls.Count == 1 ? ImageDownloaderId : GalleryDownloaderId,
                    status.CanonicalUrl, Label: photos.Title, SourceUrl: status.CanonicalUrl)];
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { failure = ex.Message; }
        }
        return [new(PrimaryDownloaderId, status.CanonicalUrl + (status.MediaId == null ? "" : "#cove-media=" + status.MediaId), Label: failure ?? "No supported X media found")];
    }

    public async Task<DownloaderResult?> DownloadAsync(DownloaderRequest request, IDownloaderHost host, CancellationToken ct)
    {
        var expected = request.DownloaderId switch
        {
            PrimaryDownloaderId => DownloaderEntity.Video, ImageDownloaderId => DownloaderEntity.Image,
            GalleryDownloaderId => DownloaderEntity.Gallery, _ => (DownloaderEntity?)null
        };
        if (expected == null) return null;
        if (request.Entity != expected) throw new InvalidOperationException("The selected downloader does not support this media type.");
        if (!XcomUrlParser.TryParse(request.Url, out var status)) throw new InvalidOperationException("Unsupported X status URL.");
        host.ReportProgress(0, "Resolving X media...");
        // Own a unique directory; failure/cancellation cleanup can never delete another download's files.
        var work = Path.Combine(host.TempDirectory, "xcom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            using var client = host.HttpClients.CreateClient(PrimaryDownloaderId);
            if (request.Entity != DownloaderEntity.Video)
            {
                if (!PhotoFallback) throw new InvalidOperationException("Enable the public photo fallback to download photos.");
                var photos = await InspectPhotosAsync(client, status, ct);
                if ((request.Entity == DownloaderEntity.Image) != (photos.Urls.Count == 1))
                    throw new InvalidOperationException("Post media changed; match the URL again to select image or gallery intake.");
                var images = Path.Combine(work, "images");
                Directory.CreateDirectory(images);
                for (var i = 0; i < photos.Urls.Count; i++)
                    await SaveAsync(client, photos.Urls[i], Path.Combine(images, $"{i + 1:D2}" + PhotoExtension(photos.Urls[i])),
                        host, 5 + 90.0 * i / photos.Urls.Count, 90.0 / photos.Urls.Count, ct);
                string path;
                if (photos.Urls.Count == 1) path = Directory.GetFiles(images).Single();
                else
                {
                    path = Path.Combine(work, "gallery.zip");
                    ZipFile.CreateFromDirectory(images, path);
                    Directory.Delete(images, true);
                }
                ct.ThrowIfCancellationRequested();
                host.ReportProgress(100, "Completed download.");
                return new(path, SafeName(photos.Title, status.StatusId) + Path.GetExtension(path),
                    InlineImageMetadata: photos.Urls.Count == 1 ? new ScrapedImageDto { Title = photos.Title, Urls = [status.CanonicalUrl] } : null,
                    InlineGalleryMetadata: photos.Urls.Count > 1 ? new ScrapedGalleryDto { Title = photos.Title, Urls = [status.CanonicalUrl], StudioName = "X / Twitter" } : null);
            }
            var media = await _resolver.ResolveAsync(status, Executable, ct, CookiesFile);
            var selected = status.MediaId == null ? media.FirstOrDefault() : media.FirstOrDefault(m => m.Id == status.MediaId);
            if (selected == null) throw new InvalidOperationException("No downloadable progressive MP4 found for this status. Photo posts require the public photo fallback.");
            IEnumerable<XcomFormat> formats = selected.Formats;
            if (!string.IsNullOrWhiteSpace(request.QualityId)) formats = formats.Where(f => f.Id == request.QualityId);
            else if (request.MaxHeight is > 0) formats = formats.Where(f => f.Height.HasValue && f.Height <= request.MaxHeight);
            var format = formats.OrderByDescending(f => f.Height ?? 0).ThenByDescending(f => f.Bitrate).FirstOrDefault()
                ?? throw new InvalidOperationException("The selected quality or height limit is unavailable; choose another quality.");
            var localPath = Path.Combine(work, "video.mp4");
            await SaveAsync(client, format.Url, localPath, host, 5, 90, ct);
            ct.ThrowIfCancellationRequested();
            host.ReportProgress(100, "Completed download.");
            return new(localPath, SafeName(selected.Title, status.StatusId) + ".mp4", InlineVideoMetadata: new ScrapedVideoDto
            {
                Title = selected.Title, Details = selected.Details, Date = selected.Date,
                StudioName = selected.Uploader, Urls = [status.CanonicalUrl]
            });
        }
        catch
        {
            Directory.Delete(work, true);
            throw;
        }
    }

    private static string SafeName(string title, string id) => DownloaderUtilities.SanitizeFileName(
        title[..Math.Min(title.Length, 120)], "X post") + "-" + id;

    private static async Task SaveAsync(HttpClient client, string url, string path, IDownloaderHost host,
        double start, double span, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(DownloaderUtilities.DefaultUserAgent);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"X media download failed (HTTP {(int)response.StatusCode}); retry the status URL to refresh media links.");
        var final = response.RequestMessage?.RequestUri;
        var originalHost = new Uri(url).Host;
        if (final != null && !YtDlpResolver.IsMediaUrl(final.AbsoluteUri, originalHost))
            throw new InvalidOperationException("X media redirected outside its permitted CDN.");
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType != null && contentType != "application/octet-stream" &&
            !(originalHost == "video.twimg.com" ? contentType.StartsWith("video/") : contentType.StartsWith("image/")))
            throw new InvalidOperationException("X returned a non-media response.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        var length = response.Content.Headers.ContentLength;
        int read;
        host.ReportProgress(start, "Downloading media...");
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
            if (length is > 0) host.ReportProgress(start + span * Math.Min(1, (double)total / length.Value), "Downloading media...");
        }
        if (total == 0 || (length.HasValue && total != length)) throw new InvalidOperationException("X returned an empty or incomplete media file; retry the status URL.");
        host.ReportProgress(start + span, "Media saved.");
    }

    private sealed record PublicPhotos(string Title, IReadOnlyList<string> Urls);
    private static async Task<PublicPhotos> InspectPhotosAsync(HttpClient client, XStatusRef status, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, status.CanonicalUrl);
        request.Headers.UserAgent.ParseAdd(DownloaderUtilities.DefaultUserAgent);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("This post is not publicly accessible.");
        var html = await response.Content.ReadAsStringAsync(ct);
        if (html.Contains("TweetTombstone", StringComparison.Ordinal) || html.Contains("BlurredMediaTombstone", StringComparison.Ordinal))
            throw new InvalidOperationException("This post is restricted or login-gated on X; a public preview is not downloadable post media.");
        var urls = new List<string>();
        foreach (Match tag in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var url = DownloaderUtilities.ExtractMetaContent(tag.Value, "og:image")
                ?? DownloaderUtilities.ExtractMetaContent(tag.Value, "og:image:secure_url")
                ?? DownloaderUtilities.ExtractMetaContent(tag.Value, "twitter:image")
                ?? DownloaderUtilities.ExtractMetaContent(tag.Value, "twitter:image:src");
            if (YtDlpResolver.IsMediaUrl(url, "pbs.twimg.com") && new Uri(url!).AbsolutePath.StartsWith("/media/", StringComparison.Ordinal))
                urls.Add(url!);
        }
        var unique = urls.DistinctBy(u => new Uri(u).AbsolutePath).Take(4).ToArray();
        var photoSelection = Regex.Match(new Uri(status.CanonicalUrl).AbsolutePath, @"/photo/([1-9][0-9]?)$");
        if (photoSelection.Success)
        {
            var index = int.Parse(photoSelection.Groups[1].Value) - 1;
            unique = index < unique.Length ? [unique[index]] : [];
        }
        if (unique.Length == 0) throw new InvalidOperationException("No public photo metadata found; this post may require login. The public photo fallback cannot resolve it.");
        return new(DownloaderUtilities.ExtractHtmlTitle(html) ?? "X post " + status.StatusId, unique);
    }
    private static string PhotoExtension(string url)
    {
        var uri = new Uri(url);
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (extension is ".jpg" or ".jpeg" or ".png" or ".webp") return extension;
        var format = Regex.Match(uri.Query, @"(?:[?&])format=(jpg|jpeg|png|webp)(?:&|$)", RegexOptions.IgnoreCase);
        return format.Success ? "." + format.Groups[1].Value.ToLowerInvariant() : ".jpg";
    }
}
