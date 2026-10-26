using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Plugins;

namespace KariDownloads.Shared;

/// <summary>
/// Shared utilities for Cove <see cref="IDownloaderProvider"/> implementations in this repository.
/// Link into any downloader project via:
/// <c>&lt;Compile Include="..\Shared\DownloaderUtilities.cs" Link="Shared\DownloaderUtilities.cs" /&gt;</c>
/// </summary>
internal static class DownloaderUtilities
{
    public const string DefaultUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex BracketedTagRegex = new(@"\[(?<tag>[^\[\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"[ \t\r\n]+", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://[^\s<>""'\)\]\}]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JsonLdScriptRegex = new(
        @"<script\b[^>]*type\s*=\s*['""]application/ld\+json['""][^>]*>(?<json>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public sealed record BracketedMetadata(string Title, string? Details, List<string> TagNames);

    public static string GetExtensionVersion(Type extensionType)
    {
        ArgumentNullException.ThrowIfNull(extensionType);

        var assembly = extensionType.Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Split('+', 2, StringSplitOptions.TrimEntries)[0];

        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return informationalVersion;

        return assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    }

    public static bool IsHttpUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsHost(Uri uri, string host)
    {
        var normalizedHost = host.Trim().TrimStart('.');
        return uri.Host.Equals(normalizedHost, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + normalizedHost, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHost(string? url, string host)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsHost(uri, host);
    }

    public static bool IsAnyHost(string? url, params ReadOnlySpan<string> hosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        foreach (var host in hosts)
        {
            if (IsHost(uri, host))
                return true;
        }

        return false;
    }

    public static string DeriveTitleFromUrl(string url, string fallback = "Downloaded Media")
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return fallback;

        var lastSegment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(lastSegment))
            return fallback;

        var withoutExt = Path.GetFileNameWithoutExtension(lastSegment);
        var candidate = string.IsNullOrWhiteSpace(withoutExt) ? lastSegment : withoutExt;
        var cleaned = Uri.UnescapeDataString(candidate.Replace('-', ' ').Replace('_', ' ')).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    public static string SanitizeFileName(string name, string fallback = "downloaded")
    {
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim().Trim('.');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    public static string EnsureExtensionFromUrl(string url, string fallbackExtension)
    {
        var normalizedFallback = fallbackExtension.StartsWith('.') ? fallbackExtension : "." + fallbackExtension;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var extension = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(extension) && extension.Length <= 6)
                return extension.ToLowerInvariant();
        }

        return normalizedFallback.ToLowerInvariant();
    }

    public static string? ExtractHtmlTitle(string html)
    {
        var ogTitle = ExtractMetaContent(html, "og:title") ?? ExtractMetaContent(html, "twitter:title");
        if (!string.IsNullOrWhiteSpace(ogTitle))
            return ogTitle;

        var h1 = ExtractFirstMatch(html, @"(?is)<h1[^>]*>(.*?)</h1>");
        if (!string.IsNullOrWhiteSpace(h1))
            return h1;

        return ExtractFirstMatch(html, @"(?is)<title[^>]*>(.*?)</title>");
    }

    public static string? ExtractMetaContent(string html, string propertyName)
    {
        foreach (Match metaTag in Regex.Matches(html, @"(?is)<meta\b[^>]*>"))
        {
            var attributes = ParseAttributes(metaTag.Value);
            var property = attributes.GetValueOrDefault("property")
                ?? attributes.GetValueOrDefault("name")
                ?? attributes.GetValueOrDefault("itemprop");
            if (propertyName.Equals(property, StringComparison.OrdinalIgnoreCase)
                && attributes.TryGetValue("content", out var content)
                && !string.IsNullOrWhiteSpace(content))
            {
                return WebUtility.HtmlDecode(content).Trim();
            }
        }

        return null;
    }

    public static IReadOnlyList<JsonDocument> ExtractJsonLdBlocks(string html)
    {
        var docs = new List<JsonDocument>();
        foreach (Match match in JsonLdScriptRegex.Matches(html))
        {
            var rawJson = match.Groups["json"].Value.Trim();
            if (string.IsNullOrWhiteSpace(rawJson))
                continue;

            try
            {
                docs.Add(JsonDocument.Parse(rawJson));
            }
            catch (JsonException)
            {
                // Ignore malformed JSON-LD blocks on target pages.
            }
        }

        return docs;
    }

    public static IReadOnlyList<string> ExtractUrls(string text)
    {
        var urls = new List<string>();
        var decoded = WebUtility.HtmlDecode(text);
        foreach (Match match in UrlRegex.Matches(decoded))
        {
            var url = match.Value.Trim().TrimEnd('.', ',', ';', ':');
            if (Uri.TryCreate(url, UriKind.Absolute, out _) && !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
                urls.Add(url);
        }

        return urls;
    }

    public static BracketedMetadata ExtractBracketedMetadata(string? title, string? details, string fallbackTitle)
    {
        var titleValue = string.IsNullOrWhiteSpace(title) ? fallbackTitle : title.Trim();
        var fallbackValue = string.IsNullOrWhiteSpace(fallbackTitle) ? "Untitled" : fallbackTitle.Trim();
        var tagNames = new List<string>();

        AddBracketedTags(titleValue, tagNames);
        if (!string.IsNullOrWhiteSpace(details))
            AddBracketedTags(details, tagNames);

        var cleanedTitle = StripBracketedText(titleValue);
        if (string.IsNullOrWhiteSpace(cleanedTitle))
            cleanedTitle = fallbackValue;

        var cleanedDetails = string.IsNullOrWhiteSpace(details) ? null : StripBracketedText(details);

        return new BracketedMetadata(
            cleanedTitle,
            string.IsNullOrWhiteSpace(cleanedDetails) ? null : cleanedDetails,
            tagNames);
    }

    public static string? ExtractFirstMatch(string html, string pattern)
    {
        var value = ExtractFirstRawMatch(html, pattern);
        return string.IsNullOrWhiteSpace(value) ? null : CleanHtmlFragment(value);
    }

    public static string? ExtractFirstRawMatch(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string CleanHtmlFragment(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        var withoutTags = HtmlTagRegex.Replace(decoded, string.Empty);
        return WhitespaceRegex.Replace(withoutTags, " ").Trim();
    }

    /// <summary>
    /// Streams a remote file to <paramref name="host"/>.TempDirectory while reporting progress on a 0..100 scale.
    /// </summary>
    public static async Task<string> DownloadToTempFileAsync(
        HttpClient client,
        string mediaUrl,
        IDownloaderHost host,
        string localFileName,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null,
        double startProgress = 10.0,
        double endProgress = 95.0)
    {
        Directory.CreateDirectory(host.TempDirectory);
        var sanitizedLocalName = SanitizeFileName(localFileName, "downloaded.bin");
        var destinationPath = Path.Combine(host.TempDirectory, sanitizedLocalName);

        using var request = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
        request.Headers.UserAgent.TryParseAdd(DefaultUserAgent);
        if (headers != null)
        {
            foreach (var (key, value) in headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(destinationPath);

        var buffer = new byte[64 * 1024];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            if (totalBytes is > 0)
            {
                var fraction = Math.Clamp((double)totalRead / totalBytes.Value, 0.0, 1.0);
                var progress = startProgress + (endProgress - startProgress) * fraction;
                host.ReportProgress(progress, $"Downloaded {totalRead / 1024:N0} KB of {totalBytes.Value / 1024:N0} KB");
            }
        }

        host.ReportProgress(endProgress, "Download complete.");
        return destinationPath;
    }

    /// <summary>
    /// Downloads multiple images into a staging folder and packs them into a .zip archive in <paramref name="host"/>.TempDirectory.
    /// </summary>
    public static async Task<string> CreateGalleryZipAsync(
        HttpClient client,
        IReadOnlyList<string> imageUrls,
        IDownloaderHost host,
        string archiveBaseName,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        if (imageUrls.Count == 0)
            throw new InvalidOperationException("Cannot create a gallery archive from an empty image URL list.");

        Directory.CreateDirectory(host.TempDirectory);
        var safeBaseName = SanitizeFileName(archiveBaseName, "gallery");
        var stagingDir = Path.Combine(host.TempDirectory, $"staging_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        try
        {
            for (var i = 0; i < imageUrls.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var imageUrl = imageUrls[i];
                var ext = EnsureExtensionFromUrl(imageUrl, ".jpg");
                var itemFileName = $"{i + 1:D3}{ext}";
                var itemPath = Path.Combine(stagingDir, itemFileName);

                using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                request.Headers.UserAgent.TryParseAdd(DefaultUserAgent);
                if (headers != null)
                {
                    foreach (var (key, value) in headers)
                        request.Headers.TryAddWithoutValidation(key, value);
                }

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(itemPath);
                await input.CopyToAsync(output, ct);

                var progress = 10.0 + (80.0 * (i + 1) / imageUrls.Count);
                host.ReportProgress(progress, $"Downloaded gallery image {i + 1}/{imageUrls.Count}");
            }

            var zipPath = Path.Combine(host.TempDirectory, $"{safeBaseName}.zip");
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            ZipFile.CreateFromDirectory(stagingDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            host.ReportProgress(98.0, "Packed gallery ZIP archive.");
            return zipPath;
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in Regex.Matches(tag, @"(?is)([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:(['""])(.*?)\2|([^\s>]+))"))
        {
            attributes[attribute.Groups[1].Value] = attribute.Groups[3].Success
                ? attribute.Groups[3].Value
                : attribute.Groups[4].Value;
        }

        return attributes;
    }

    private static void AddBracketedTags(string value, List<string> tagNames)
    {
        foreach (Match match in BracketedTagRegex.Matches(value))
        {
            var tagName = match.Groups["tag"].Value.Trim().TrimEnd('\\').Trim();
            if (!string.IsNullOrWhiteSpace(tagName) && !tagNames.Contains(tagName, StringComparer.OrdinalIgnoreCase))
                tagNames.Add(tagName);
        }
    }

    private static string StripBracketedText(string value)
    {
        var withoutBracketedTags = BracketedTagRegex.Replace(value, " ");
        return WhitespaceRegex.Replace(withoutBracketedTags, " ").Trim();
    }
}
