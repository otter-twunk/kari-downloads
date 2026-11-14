using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KariDownloads.Shared;

/// <summary>
/// Shared HTML, JSON-LD, OpenGraph, and normalization utilities for Cove <see cref="Cove.Plugins.IScraperProvider"/>
/// implementations in this repository.
/// Link into any scraper project via:
/// <c>&lt;Compile Include="..\Shared\ScraperUtilities.cs" Link="Shared\ScraperUtilities.cs" /&gt;</c>
/// </summary>
internal static class ScraperUtilities
{
    public const string DefaultUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"[ \t\r\n]+", RegexOptions.Compiled);
    private static readonly Regex JsonLdScriptRegex = new(
        @"<script\b[^>]*type\s*=\s*['""]application/ld\+json['""][^>]*>(?<json>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MetaTagRegex = new(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AttributeRegex = new(
        @"([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:(['""])(.*?)\2|([^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TitleTagRegex = new(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DefaultCodeRegex = new(@"\b([A-Z]{2,6}-\d{2,5})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly SocketsHttpHandler FallbackSocketsHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    };

    public static HttpClient CreateFallbackHttpClient() => new(FallbackSocketsHandler, disposeHandler: false);

    private static readonly string[] CommonDateFormats =
    [
        "yyyy-MM-dd",
        "yyyyMMdd",
        "yyyy/MM/dd",
        "MM/dd/yyyy",
        "dd/MM/yyyy",
        "MMMM d, yyyy",
        "MMM d, yyyy",
        "d MMMM yyyy",
        "d MMM yyyy",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm:ss"
    ];

    public sealed record StructuredPageMetadata(
        string? Title,
        string? Description,
        string? Date,
        string? ImageUrl,
        string? StudioName,
        string? Director,
        IReadOnlyList<string> PerformerNames,
        IReadOnlyList<string> TagNames);

    public static async Task<string> FetchHtmlAsync(
        HttpClient client,
        string url,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.TryParseAdd(DefaultUserAgent);
        request.Headers.AcceptLanguage.TryParseAdd("en-US,en;q=0.9");
        if (headers != null)
        {
            foreach (var (key, value) in headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public static string? CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var decoded = WebUtility.HtmlDecode(value);
        var stripped = HtmlTagRegex.Replace(decoded, " ");
        var normalized = WhitespaceRegex.Replace(stripped, " ").Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static string? NormalizeIsoDate(string? rawDate)
    {
        if (string.IsNullOrWhiteSpace(rawDate))
            return null;

        var trimmed = rawDate.Trim();

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            // Distinguish 8-digit yyyyMMdd from Unix seconds/milliseconds
            if (trimmed.Length == 8 && DateTime.TryParseExact(trimmed, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compactDate))
                return compactDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            try
            {
                var dto = epoch > 10_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                    : DateTimeOffset.FromUnixTimeSeconds(epoch);
                return dto.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        if (DateTimeOffset.TryParseExact(trimmed, CommonDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var exactDto))
            return exactDto.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var parsedDto))
            return parsedDto.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return null;
    }

    public static string? ExtractCode(string? text, Regex? customRegex = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var regex = customRegex ?? DefaultCodeRegex;
        var match = regex.Match(text);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    public static List<string> DeduplicateStrings(IEnumerable<string?> values)
    {
        var result = new List<string>();
        foreach (var raw in values)
        {
            var cleaned = CleanText(raw);
            if (!string.IsNullOrWhiteSpace(cleaned) && !result.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                result.Add(cleaned);
        }

        return result;
    }

    public static string? ExtractMetaContent(string html, string propertyName)
    {
        foreach (Match metaTag in MetaTagRegex.Matches(html))
        {
            var attributes = ParseAttributes(metaTag.Value);
            var property = attributes.GetValueOrDefault("property")
                ?? attributes.GetValueOrDefault("name")
                ?? attributes.GetValueOrDefault("itemprop");
            if (propertyName.Equals(property, StringComparison.OrdinalIgnoreCase)
                && attributes.TryGetValue("content", out var content)
                && !string.IsNullOrWhiteSpace(content))
            {
                return CleanText(content);
            }
        }

        return null;
    }

    public static IReadOnlyList<string> ExtractAllMetaContent(string html, string propertyName)
    {
        var results = new List<string>();
        foreach (Match metaTag in MetaTagRegex.Matches(html))
        {
            var attributes = ParseAttributes(metaTag.Value);
            var property = attributes.GetValueOrDefault("property")
                ?? attributes.GetValueOrDefault("name")
                ?? attributes.GetValueOrDefault("itemprop");
            if (propertyName.Equals(property, StringComparison.OrdinalIgnoreCase)
                && attributes.TryGetValue("content", out var content))
            {
                var cleaned = CleanText(content);
                if (!string.IsNullOrWhiteSpace(cleaned) && !results.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                    results.Add(cleaned);
            }
        }

        return results;
    }

    /// <summary>
    /// Extracts structured metadata combining JSON-LD (<c>application/ld+json</c>) and OpenGraph/meta fallbacks.
    /// Uses a single-pass meta-tag cache for high performance.
    /// </summary>
    public static StructuredPageMetadata ExtractStructuredMetadata(string html, string? defaultStudioName = null)
    {
        string? title = null;
        string? description = null;
        string? date = null;
        string? imageUrl = null;
        string? studioName = null;
        string? director = null;
        var performers = new List<string>();
        var tags = new List<string>();

        foreach (Match match in JsonLdScriptRegex.Matches(html))
        {
            var rawJson = match.Groups["json"].Value.Trim();
            if (string.IsNullOrWhiteSpace(rawJson))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                InspectJsonLdElement(doc.RootElement, ref title, ref description, ref date, ref imageUrl, ref studioName, ref director, performers, tags);
            }
            catch (JsonException)
            {
                // Ignore malformed JSON-LD blocks
            }
        }

        // Single-pass extraction of all meta tags
        var metaMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match metaTag in MetaTagRegex.Matches(html))
        {
            var attributes = ParseAttributes(metaTag.Value);
            var property = attributes.GetValueOrDefault("property")
                ?? attributes.GetValueOrDefault("name")
                ?? attributes.GetValueOrDefault("itemprop");
            if (!string.IsNullOrWhiteSpace(property)
                && attributes.TryGetValue("content", out var content)
                && !string.IsNullOrWhiteSpace(content))
            {
                var cleaned = CleanText(content);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    if (!metaMap.TryGetValue(property, out var list))
                    {
                        list = new List<string>();
                        metaMap[property] = list;
                    }
                    list.Add(cleaned);
                }
            }
        }

        string? GetMeta(string name) => metaMap.TryGetValue(name, out var list) && list.Count > 0 ? list[0] : null;
        IReadOnlyList<string> GetAllMeta(string name) => metaMap.TryGetValue(name, out var list) ? list : Array.Empty<string>();

        title ??= GetMeta("og:title")
            ?? GetMeta("twitter:title")
            ?? CleanText(TitleTagRegex.Match(html).Groups[1].Value);

        description ??= GetMeta("og:description")
            ?? GetMeta("description");

        date ??= NormalizeIsoDate(
            GetMeta("video:release_date")
            ?? GetMeta("article:published_time")
            ?? GetMeta("datePublished"));

        imageUrl ??= GetMeta("og:image")
            ?? GetMeta("twitter:image");

        studioName ??= GetMeta("og:site_name") ?? defaultStudioName;

        foreach (var metaActor in GetAllMeta("video:actor"))
            performers.Add(metaActor);

        foreach (var metaTag in GetAllMeta("video:tag"))
            tags.Add(metaTag);

        var keywordsMeta = GetMeta("keywords");
        if (!string.IsNullOrWhiteSpace(keywordsMeta))
        {
            foreach (var kw in keywordsMeta.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                tags.Add(kw);
        }

        return new StructuredPageMetadata(
            Title: CleanText(title),
            Description: CleanText(description),
            Date: date,
            ImageUrl: imageUrl?.Trim(),
            StudioName: CleanText(studioName),
            Director: CleanText(director),
            PerformerNames: DeduplicateStrings(performers),
            TagNames: DeduplicateStrings(tags));
    }

    private static void InspectJsonLdElement(
        JsonElement element,
        ref string? title,
        ref string? description,
        ref string? date,
        ref string? imageUrl,
        ref string? studioName,
        ref string? director,
        List<string> performers,
        List<string> tags)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                InspectJsonLdElement(item, ref title, ref description, ref date, ref imageUrl, ref studioName, ref director, performers, tags);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        if (element.TryGetProperty("@graph", out var graph))
            InspectJsonLdElement(graph, ref title, ref description, ref date, ref imageUrl, ref studioName, ref director, performers, tags);

        title ??= TryGetString(element, "name") ?? TryGetString(element, "headline");
        description ??= TryGetString(element, "description");
        date ??= NormalizeIsoDate(TryGetString(element, "uploadDate") ?? TryGetString(element, "datePublished") ?? TryGetString(element, "dateCreated"));
        imageUrl ??= TryGetImageOrUrl(element, "thumbnailUrl") ?? TryGetImageOrUrl(element, "image");
        studioName ??= TryGetNestedName(element, "productionCompany") ?? TryGetNestedName(element, "publisher");
        director ??= TryGetNestedName(element, "director");

        ExtractNestedNames(element, "actor", performers);
        ExtractNestedNames(element, "actors", performers);
        ExtractNestedNames(element, "performer", performers);
        ExtractNestedNames(element, "performers", performers);

        if (element.TryGetProperty("keywords", out var keywords))
        {
            if (keywords.ValueKind == JsonValueKind.String)
            {
                foreach (var kw in (keywords.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    tags.Add(kw);
            }
            else if (keywords.ValueKind == JsonValueKind.Array)
            {
                foreach (var kw in keywords.EnumerateArray())
                {
                    if (kw.ValueKind == JsonValueKind.String)
                        tags.Add(kw.GetString()!);
                }
            }
        }
    }

    private static string? TryGetString(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static string? TryGetImageOrUrl(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Array => prop.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : TryGetString(x, "url")).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            JsonValueKind.Object => TryGetString(prop, "url") ?? TryGetString(prop, "contentUrl"),
            _ => null
        };
    }

    private static string? TryGetNestedName(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Object => TryGetString(prop, "name"),
            JsonValueKind.Array => prop.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : TryGetString(x, "name")).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            _ => null
        };
    }

    private static void ExtractNestedNames(JsonElement obj, string propertyName, List<string> target)
    {
        if (!obj.TryGetProperty(propertyName, out var prop))
            return;

        if (prop.ValueKind == JsonValueKind.String)
        {
            target.Add(prop.GetString()!);
        }
        else if (prop.ValueKind == JsonValueKind.Object)
        {
            var name = TryGetString(prop, "name");
            if (!string.IsNullOrWhiteSpace(name))
                target.Add(name);
        }
        else if (prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    target.Add(item.GetString()!);
                else if (item.ValueKind == JsonValueKind.Object && TryGetString(item, "name") is { } n)
                    target.Add(n);
            }
        }
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in AttributeRegex.Matches(tag))
        {
            attributes[attribute.Groups[1].Value] = attribute.Groups[3].Success
                ? attribute.Groups[3].Value
                : attribute.Groups[4].Value;
        }

        return attributes;
    }
}
