#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import process from "node:process";

const root = path.resolve(import.meta.dirname, "..");
const catalogPath = path.join(root, "extensions", "catalog.json");
const slnxPath = path.join(root, "MultiExtensionTemplate.slnx");
const testsProjPath = path.join(root, "tests", "KariDownloads.Tests", "KariDownloads.Tests.csproj");

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i++) {
    const token = argv[i];
    if (token.startsWith("--")) {
      const key = token.slice(2);
      const next = argv[i + 1];
      if (!next || next.startsWith("--")) {
        args[key] = true;
      } else {
        args[key] = next;
        i++;
      }
    }
  }
  return args;
}

function toKebabCase(str) {
  return str
    .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
    .replace(/[^a-zA-Z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .toLowerCase();
}

function toPascalCase(str) {
  return str
    .replace(/[^a-zA-Z0-9]+/g, " ")
    .split(" ")
    .filter(Boolean)
    .map(part => part[0].toUpperCase() + part.slice(1))
    .join("");
}

function usage() {
  console.error(`Usage:
  node scripts/new-extension.mjs \\
    --kind <downloader|scraper|scraper-pack> \\
    --name <PascalCaseExtensionName> \\
    --domain <example.com> \\
    [--id <karinauts.downloaders.slug>] \\
    [--title "Human Readable Name"] \\
    [--entity <Video|Image|Gallery|Audio|Text|Performer>] \\
    [--tag-prefix <slug/>] \\
    [--min-cove-version <1.6.0>]
`);
  process.exit(1);
}

const args = parseArgs(process.argv.slice(2));
const kind = (args.kind ?? "").toLowerCase();
if (!["downloader", "scraper", "scraper-pack"].includes(kind) || !args.name || !args.domain) {
  usage();
}

const name = toPascalCase(args.name);
const domain = args.domain.replace(/^https?:\/\//i, "").replace(/\/.*$/, "").toLowerCase();
const baseSlug = toKebabCase(
  name.replace(/(Downloader|Scraper|Pack|Extension)$/i, "") || domain.split(".")[0]
);
const id =
  args.id ??
  (kind === "downloader"
    ? `karinauts.downloaders.${baseSlug}`
    : kind === "scraper-pack"
      ? `karinauts.scrapers.pack.${baseSlug}`
      : `karinauts.scrapers.${baseSlug}`);

const tagPrefix = (args["tag-prefix"] ?? `${toKebabCase(name)}/`).replace(/\/?$/, "/");
const displayName =
  args.title ??
  name.replace(/([a-z0-9])([A-Z])/g, "$1 $2");
const entity = args.entity ?? "Video";
const minCoveVersion = args["min-cove-version"] ?? "1.6.0";
const repoUrl = "https://github.com/otter-twunk/kari-downloads";
const author = args.author ?? "Kari";

const extensionRelPath = `extensions/${name}`;
const extensionDir = path.join(root, "extensions", name);
if (fs.existsSync(extensionDir)) {
  console.error(`ERROR: Extension directory already exists: ${extensionRelPath}`);
  process.exit(1);
}

const catalog = JSON.parse(fs.readFileSync(catalogPath, "utf8"));
if (catalog.extensions.some(e => e.id === id)) {
  console.error(`ERROR: Extension id '${id}' already exists in extensions/catalog.json`);
  process.exit(1);
}
if (catalog.extensions.some(e => e.tagPrefix === tagPrefix)) {
  console.error(`ERROR: tagPrefix '${tagPrefix}' already exists in extensions/catalog.json`);
  process.exit(1);
}

fs.mkdirSync(extensionDir, { recursive: true });

if (kind === "scraper-pack") {
  const scrapersDir = path.join(extensionDir, "scrapers");
  fs.mkdirSync(scrapersDir, { recursive: true });
  const yamlFileName = `${toPascalCase(baseSlug)}.yml`;
  const manifest = {
    id,
    name: displayName,
    version: "0.1.0",
    kind: "scraper-pack",
    description: args.description ?? `Cove-native YAML scraper pack for ${domain}.`,
    author,
    url: repoUrl,
    categories: ["scraper", "metadata", "yaml-scraper"],
    minCoveVersion,
    scraperFiles: [`scrapers/${yamlFileName}`],
    permissions: {
      network: [domain, `*.${domain}`]
    }
  };
  fs.writeFileSync(path.join(extensionDir, "extension.json"), JSON.stringify(manifest, null, 2) + "\n");

  const yamlContent = `# Site: ${domain}
# Modes: videoByURL, videoByName
# Verified: ${new Date().toISOString().slice(0, 10)}
name: ${toPascalCase(baseSlug)}

videoByURL:
  - action: scrapeXPath
    url:
      - ${domain}/
    scraper: videoScraper

videoByName:
  action: scrapeXPath
  queryURL: https://${domain}/search?q={}
  scraper: searchScraper

xPathScrapers:
  videoScraper:
    video:
      Title: //meta[@property='og:title']/@content
      Details: //meta[@property='og:description']/@content
      Date:
        selector: //meta[@property='video:release_date']/@content
        postProcess:
          - parseDate: 2006-01-02
      Image: //meta[@property='og:image']/@content
      Tags:
        selector: //meta[@property='video:tag']/@content
        Name: .
      Performers:
        selector: //meta[@property='video:actor']/@content
        Name: .
      Studio:
        Name:
          fixed: ${displayName.replace(/\s*Scraper.*$/i, "")}

  searchScraper:
    video:
      Title: //a[contains(@href, '/video')]/@title
      URL: //a[contains(@href, '/video')]/@href
`;
  fs.writeFileSync(path.join(scrapersDir, yamlFileName), yamlContent);

  catalog.extensions.push({
    name,
    id,
    path: extensionRelPath,
    tagPrefix,
    manifestOnly: true,
    contentOnly: true
  });
  fs.writeFileSync(catalogPath, JSON.stringify(catalog, null, 2) + "\n");
  console.log(`Scaffolded YAML scraper-pack extension '${name}' (${id}) at ${extensionRelPath}`);
  process.exit(0);
}

const csprojContent = `<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <AssemblyName>${name}</AssemblyName>
    <RootNamespace>${name}</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="..\\Shared\\${kind === "downloader" ? "DownloaderUtilities.cs" : "ScraperUtilities.cs"}" Link="Shared\\${kind === "downloader" ? "DownloaderUtilities.cs" : "ScraperUtilities.cs"}" />
  </ItemGroup>

</Project>
`;
fs.writeFileSync(path.join(extensionDir, `${name}.csproj`), csprojContent);

const manifest = {
  id,
  name: displayName,
  version: "0.1.0",
  description:
    args.description ??
    (kind === "downloader"
      ? `Downloads ${entity.toLowerCase()} media and inline metadata from ${domain}.`
      : `Scrapes ${entity.toLowerCase()} metadata and search candidates from ${domain}.`),
  author,
  url: repoUrl,
  categories:
    kind === "downloader"
      ? ["downloader", entity.toLowerCase(), "metadata"]
      : ["scraper", "metadata", entity.toLowerCase()],
  minCoveVersion,
  entryDll: `${name}.dll`,
  dependencies: {},
  permissions: {
    network: [domain, `*.${domain}`],
    scraperRuntime: kind === "scraper" ? [domain, `*.${domain}`] : [],
    downloaderRuntime: kind === "downloader" ? [domain, `*.${domain}`] : []
  }
};
fs.writeFileSync(path.join(extensionDir, "extension.json"), JSON.stringify(manifest, null, 2) + "\n");

if (kind === "downloader") {
  const downloaderId = `${id}.${entity.toLowerCase()}`;
  const csContent = `using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace ${name};

public sealed class ${name}Extension : CoveExtensionBase, IDownloaderProvider
{
    public const string PrimaryDownloaderId = "${downloaderId}";
    private static readonly string[] SupportedDomains = ["${domain}"];

    private IServiceProvider? _services;

    private static readonly DownloaderDescriptor PrimaryDescriptor = new(
        PrimaryDownloaderId,
        "${displayName}",
        DownloaderEntity.${entity},
        ["https://${domain}/*", "https://*.${domain}/*"],
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
            Label: pageInfo?.Title ?? DownloaderUtilities.DeriveTitleFromUrl(url, "${displayName}"));
    }

    public async Task<DownloaderResult?> DownloadAsync(
        DownloaderRequest request,
        IDownloaderHost host,
        CancellationToken ct)
    {
        if (!string.Equals(request.DownloaderId, PrimaryDescriptor.Id, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!IsSupportedUrl(request.Url))
            throw new InvalidOperationException($"Unsupported URL for ${displayName}: {request.Url}");

        host.ReportProgress(5.0, "Inspecting media page...");
        var pageInfo = await InspectPageAsync(request.Url, ct);

        var extension = DownloaderUtilities.EnsureExtensionFromUrl(pageInfo.MediaUrl, ".mp4");
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
                Details = pageInfo.Description,
                Urls = [request.SourceUrl ?? request.Url],
                StudioName = "${displayName.replace(/\s*Downloader.*$/i, "")}",
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
        var fallbackTitle = DownloaderUtilities.DeriveTitleFromUrl(url, "${displayName}");
        var rawTitle = DownloaderUtilities.ExtractHtmlTitle(html);
        var rawDescription = DownloaderUtilities.ExtractMetaContent(html, "og:description")
            ?? DownloaderUtilities.ExtractMetaContent(html, "description");
        var metadata = DownloaderUtilities.ExtractBracketedMetadata(rawTitle, rawDescription, fallbackTitle);

        var mediaUrl = ExtractMediaUrl(html, url)
            ?? throw new InvalidOperationException($"Could not locate a direct media stream on {url}");

        return new ResolvedMediaPage(url, metadata.Title, mediaUrl, metadata.Details, metadata.TagNames);
    }

    internal static string? ExtractMediaUrl(string html, string pageUrl)
    {
        var candidate = DownloaderUtilities.ExtractMetaContent(html, "og:video:secure_url")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:video:url")
            ?? DownloaderUtilities.ExtractMetaContent(html, "og:video")
            ?? DownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<video[^>]+src\s*=\s*['""]([^'""]+)['""]")
            ?? DownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<source[^>]+src\s*=\s*['""]([^'""]+)['""]")
            ?? DownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)(https?://[^'""<>\s]+\.(?:mp4|m4v|webm|mov)(?:\?[^'""<>\s]+)?)");

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
        string MediaUrl,
        string? Description,
        List<string> TagNames);
}
`;
  fs.writeFileSync(path.join(extensionDir, `${name}Extension.cs`), csContent);
} else {
  const scraperId = `${id}.${entity.toLowerCase()}`;
  const csContent = `using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Cove.Sdk;
using KariDownloads.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace ${name};

public sealed class ${name}Extension : CoveExtensionBase, IScraperProvider
{
    public const string PrimaryScraperId = "${scraperId}";
    public const string DefaultStudioName = "${displayName.replace(/\s*Scraper.*$/i, "")}";
    private static readonly string[] SupportedDomains = ["${domain}"];

    private IServiceProvider? _services;

    private static readonly ScraperDescriptor PrimaryDescriptor = new(
        PrimaryScraperId,
        "${displayName}",
        ScraperEntity.${entity},
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
        ["${domain}/"]);

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _services = services;
        return Task.CompletedTask;
    }

    public IReadOnlyList<ScraperDescriptor> GetScrapers() => [PrimaryDescriptor];

    public async Task<ScrapedVideoDto?> ScrapeVideoAsync(
        ScraperRequest<VideoScrapeInput> request,
        CancellationToken ct)
    {
        var sourceUrl = request.Input.Url ?? request.Input.Urls.FirstOrDefault();
        if (!IsSupportedUrl(sourceUrl))
            return null;

        var html = await ScraperUtilities.FetchHtmlAsync(CreateHttpClient(), sourceUrl!, ct);
        return ParseVideoHtml(html, sourceUrl!, request.Input);
    }

    public async Task<IReadOnlyList<ScrapedVideoDto>> SearchVideosAsync(
        ScraperRequest<string> request,
        CancellationToken ct)
    {
        var query = request.Input?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var searchUrl = $"https://${domain}/search?q={Uri.EscapeDataString(query)}";
        var html = await ScraperUtilities.FetchHtmlAsync(CreateHttpClient(), searchUrl, ct);
        return ParseSearchResultsHtml(html, searchUrl);
    }

    public static bool IsSupportedUrl(string? url)
    {
        return ScraperUtilities.CleanText(url) is { } cleaned
            && Uri.TryCreate(cleaned, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            && SupportedDomains.Any(d =>
                uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    internal static ScrapedVideoDto ParseVideoHtml(string html, string sourceUrl, VideoScrapeInput? fallbackInput = null)
    {
        var structured = ScraperUtilities.ExtractStructuredMetadata(html, DefaultStudioName);
        var code = ScraperUtilities.ExtractCode(sourceUrl)
            ?? ScraperUtilities.ExtractCode(structured.Title)
            ?? fallbackInput?.Code;

        return new ScrapedVideoDto
        {
            SourceScraperId = PrimaryScraperId,
            Title = structured.Title ?? fallbackInput?.Title ?? "Untitled",
            Code = code,
            Date = structured.Date ?? fallbackInput?.Date,
            Details = structured.Description ?? fallbackInput?.Details,
            Director = structured.Director ?? fallbackInput?.Director,
            ImageUrl = structured.ImageUrl,
            Urls = [sourceUrl],
            StudioName = structured.StudioName ?? DefaultStudioName,
            PerformerNames = structured.PerformerNames.ToList(),
            TagNames = structured.TagNames.ToList()
        };
    }

    internal static IReadOnlyList<ScrapedVideoDto> ParseSearchResultsHtml(string html, string baseSearchUrl)
    {
        var results = new List<ScrapedVideoDto>();
        var baseUri = new Uri(baseSearchUrl);

        foreach (Match match in Regex.Matches(
            html,
            @"(?is)<a\b[^>]*href\s*=\s*['""](?<href>[^'""]+)['""][^>]*>(?<inner>.*?)</a>"))
        {
            var href = match.Groups["href"].Value.Trim();
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Uri.TryCreate(baseUri, href, out var fullUri) || !IsSupportedUrl(fullUri.ToString()))
                continue;

            var title = ScraperUtilities.CleanText(match.Groups["inner"].Value);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            if (results.Any(r => r.Urls.Contains(fullUri.ToString(), StringComparer.OrdinalIgnoreCase)))
                continue;

            results.Add(new ScrapedVideoDto
            {
                SourceScraperId = PrimaryScraperId,
                Title = title,
                Urls = [fullUri.ToString()],
                StudioName = DefaultStudioName
            });
        }

        return results;
    }

    private HttpClient CreateHttpClient()
    {
        var factory = _services?.GetService<IHttpClientFactory>();
        return factory != null ? factory.CreateClient(PrimaryScraperId) : new HttpClient();
    }
}
`;
  fs.writeFileSync(path.join(extensionDir, `${name}Extension.cs`), csContent);
}

// 1. Update extensions/catalog.json
catalog.extensions.push({
  name,
  id,
  path: extensionRelPath,
  tagPrefix
});
fs.writeFileSync(catalogPath, JSON.stringify(catalog, null, 2) + "\n");

// 2. Update MultiExtensionTemplate.slnx
if (fs.existsSync(slnxPath)) {
  let slnx = fs.readFileSync(slnxPath, "utf8");
  const projectLine = `    <Project Path="extensions/${name}/${name}.csproj" />`;
  if (!slnx.includes(projectLine)) {
    slnx = slnx.replace(
      /(<Folder Name="\/extensions\/">\r?\n)/,
      `$1${projectLine}\n`
    );
    fs.writeFileSync(slnxPath, slnx);
  }
}

// 3. Wire into tests/KariDownloads.Tests if present
if (fs.existsSync(testsProjPath)) {
  let testsProj = fs.readFileSync(testsProjPath, "utf8");
  const refLine = `    <ProjectReference Include="..\\..\\extensions\\${name}\\${name}.csproj" />`;
  if (!testsProj.includes(refLine)) {
    testsProj = testsProj.replace(
      /(<!-- EXTENSION_PROJECT_REFERENCES -->)/,
      `${refLine}\n    $1`
    );
    fs.writeFileSync(testsProjPath, testsProj);
  }

  const testFilePath = path.join(path.dirname(testsProjPath), `${name}Tests.cs`);
  if (!fs.existsSync(testFilePath)) {
    const testContent =
      kind === "downloader"
        ? `using Cove.Plugins;
using KariDownloads.Tests.Infrastructure;
using ${name};

namespace KariDownloads.Tests;

public sealed class ${name}Tests
{
    [Fact]
    public async Task MatchAsync_And_DownloadAsync_WorkWithFixture()
    {
        const string pageUrl = "https://${domain}/videos/sample-clip";
        const string mediaUrl = "https://${domain}/media/sample-clip.mp4";
        const string sampleHtml = """
            <!doctype html>
            <html>
            <head>
                <meta property="og:title" content="Sample Clip [HD] [TagOne]" />
                <meta property="og:description" content="Sample description [TagTwo]" />
                <meta property="og:video" content="https://${domain}/media/sample-clip.mp4" />
            </head>
            <body></body>
            </html>
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(pageUrl, sampleHtml)
            .MapBytes(mediaUrl, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], "video/mp4");

        var extension = new ${name}Extension();
        await extension.InitializeAsync(harness.Services);

        var match = await extension.MatchAsync(pageUrl, CancellationToken.None);
        Assert.NotNull(match);
        Assert.Equal(${name}Extension.PrimaryDownloaderId, match.DownloaderId);
        Assert.Equal("Sample Clip", match.Label);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                ${name}Extension.PrimaryDownloaderId,
                pageUrl,
                DownloaderEntity.${entity},
                new DownloaderPermissions(["${domain}"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Sample Clip.mp4", result.OriginalFilename);
        Assert.True(File.Exists(Path.Combine(harness.TempDirectory, result.LocalPath)));
        Assert.NotNull(result.InlineVideoMetadata);
        Assert.Equal("Sample Clip", result.InlineVideoMetadata.Title);
        Assert.Contains("TagOne", result.InlineVideoMetadata.TagNames);
        Assert.Contains("TagTwo", result.InlineVideoMetadata.TagNames);
    }
}
`
        : `using Cove.Core.DTOs;
using Cove.Plugins;
using KariDownloads.Tests.Infrastructure;
using ${name};

namespace KariDownloads.Tests;

public sealed class ${name}Tests
{
    [Fact]
    public async Task ScrapeVideoAsync_ExtractsStructuredMetadataFromFixture()
    {
        const string pageUrl = "https://${domain}/videos/ABC-123-sample-scene";
        const string sampleHtml = """
            <!doctype html>
            <html>
            <head>
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@type": "VideoObject",
                  "name": "Sample Scene Title",
                  "description": "Full scene synopsis.",
                  "uploadDate": "2026-05-14",
                  "thumbnailUrl": "https://${domain}/thumbs/abc-123.jpg",
                  "actor": [{"@type": "Person", "name": "Alex Performer"}],
                  "keywords": "Action, Feature"
                }
                </script>
            </head>
            <body></body>
            </html>
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(pageUrl, sampleHtml);

        var extension = new ${name}Extension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                ${name}Extension.PrimaryScraperId,
                new VideoScrapeInput { Url = pageUrl },
                new ScraperPermissions(["${domain}"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Sample Scene Title", scraped.Title);
        Assert.Equal("ABC-123", scraped.Code);
        Assert.Equal("2026-05-14", scraped.Date);
        Assert.Equal("Full scene synopsis.", scraped.Details);
        Assert.Equal("https://${domain}/thumbs/abc-123.jpg", scraped.ImageUrl);
        Assert.Contains("Alex Performer", scraped.PerformerNames);
        Assert.Contains("Action", scraped.TagNames);
    }
}
`;
    fs.writeFileSync(testFilePath, testContent);
  }
}

console.log(`Scaffolded ${kind} extension '${name}' (${id}) at ${extensionRelPath}`);
