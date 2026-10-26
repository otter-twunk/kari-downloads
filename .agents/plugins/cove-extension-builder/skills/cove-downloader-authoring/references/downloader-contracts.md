# Cove Downloader SDK Contracts & Reference Patterns (`v1.7.0`)

## 1. Exact C# Downloader Contracts (`Cove.Plugins`)

```csharp
namespace Cove.Plugins;

[Flags]
public enum DownloaderCapabilities
{
    None = 0,
    ResumeSupported = 1 << 0,
    RangeRequests = 1 << 1,
    MultiQuality = 1 << 2,
    InlineMetadata = 1 << 3
}

public enum DownloaderEntity
{
    Video,
    Image,
    Gallery,
    Audio,
    Text
}

public sealed record DownloaderPermissions(
    IReadOnlyList<string>? AllowNetworkHosts = null);

public sealed record DownloaderDescriptor(
    string Id,
    string Name,
    DownloaderEntity SupportedEntity,
    IReadOnlyList<string> SupportedUrlPatterns,
    DownloaderCapabilities Capabilities = DownloaderCapabilities.None);

public sealed record DownloaderQualityOption(
    string Id,
    string Label,
    string? Description = null)
{
    public int? Width { get; init; }
    public int? Height { get; init; }
}

public sealed record DownloaderUrlMatch(
    string DownloaderId,
    string NormalizedUrl,
    IReadOnlyList<DownloaderQualityOption>? QualityOptions = null,
    string? Label = null,
    string? SourceUrl = null,
    bool Divert = false);

public sealed record DownloaderRequest(
    string DownloaderId,
    string Url,
    DownloaderEntity Entity,
    DownloaderPermissions Permissions,
    string? QualityId = null,
    string? SourceUrl = null)
{
    public int? MaxHeight { get; init; }
}

public sealed record DownloaderResult(
    string LocalPath,
    string? OriginalFilename = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    ScrapedVideoDto? InlineVideoMetadata = null,
    ScrapedGalleryDto? InlineGalleryMetadata = null,
    ScrapedImageDto? InlineImageMetadata = null);

public interface IDownloaderHost
{
    string TempDirectory { get; }
    IHttpClientFactory HttpClients { get; }
    ILogger CreateLogger(string categoryName);
    void ReportProgress(double progress, string? message = null); // 0.0 to 100.0
}

public interface IDownloaderProvider : IExtension
{
    IReadOnlyList<DownloaderDescriptor> GetDownloaders();

    Task<DownloaderUrlMatch?> MatchAsync(string url, CancellationToken ct)
        => Task.FromResult<DownloaderUrlMatch?>(null);

    async Task<IReadOnlyList<DownloaderUrlMatch>> MatchAllAsync(string url, CancellationToken ct)
    {
        var match = await MatchAsync(url, ct);
        return match == null ? [] : [match];
    }

    Task<DownloaderResult?> DownloadAsync(
        DownloaderRequest request,
        IDownloaderHost host,
        CancellationToken ct)
        => Task.FromResult<DownloaderResult?>(null);
}
```

### Host Site Login Provider (`Cove.Plugins` `v1.7.0`)
```csharp
public sealed record DownloaderSiteLogin(
    string Site,
    string Username,
    string Password)
{
    public DateTime? SavedAt { get; init; }
}

public interface IDownloaderSiteLoginProvider
{
    DownloaderSiteLogin? FindForUrl(string url);
}
```
Resolve from `IServiceProvider` in `InitializeAsync`:
```csharp
var loginProvider = _services?.GetService<IDownloaderSiteLoginProvider>();
var credentials = loginProvider?.FindForUrl(request.Url);
```

---

## 2. How Cove's `DownloaderService` Invokes Your Extension

1. **URL Matching & Diversion (`MatchAllAsync` / `MatchAsync`)**:
   - Cove calls `MatchAllAsync(url, ct)` across registered `IDownloaderProvider`s.
   - If a match has `Divert = true`, Cove does **not** call `DownloadAsync` on your provider; instead it recursively matches `match.NormalizedUrl` against all other providers (up to depth 4) while carrying `match.SourceUrl ?? originalUrl` forward so the imported item retains the original wrapper URL.
   - If `Divert = false`, Cove verifies `DownloaderDescriptor.SupportedUrlPatterns` (`*` wildcard or substring matching).
2. **Downloading (`DownloadAsync`)**:
   - Cove creates a unique per-job `host.TempDirectory`.
   - `host.ReportProgress(progress, message)` clamps `progress` between `0` and `100`.
   - `result.LocalPath` can be either a filename relative to `host.TempDirectory` or an absolute path inside `host.TempDirectory`.
   - If `InlineVideoMetadata`, `InlineGalleryMetadata`, or `InlineImageMetadata` is provided on `DownloaderResult`, Cove applies the metadata immediately upon importing the downloaded file.

---

## 3. Reference Patterns from `yourcove/communitydownloaders`

### Pattern A: Multi-Entity Provider + External Link Diversion (`RedditDownloader` pattern)
A single `IDownloaderProvider` can expose multiple `DownloaderDescriptor`s (e.g., `.video`, `.image`, `.gallery`):
- In `MatchAllAsync(url, ct)`, inspect the target post/page JSON or HTML:
  - If it embeds an external video link (e.g. RedGifs, YouTube, Vimeo), return a `DownloaderUrlMatch` with `Divert: true`, `NormalizedUrl: externalUrl`, `SourceUrl: url`.
  - If it is a multi-image gallery, return a `DownloaderUrlMatch` targeting the `.gallery` descriptor ID.
  - If it is a single image, return a `DownloaderUrlMatch` targeting the `.image` descriptor ID.
  - If it is a native video with multiple resolutions, populate `QualityOptions` with `new DownloaderQualityOption($"height:{h}", $"{h}p") { Width = w, Height = h }` and return a `DownloaderUrlMatch` targeting the `.video` descriptor ID.

### Pattern B: Quality Selection & `MaxHeight` (`v1.7.0` pattern)
When `request.QualityId` or `request.MaxHeight` is passed on `DownloaderRequest`:
- If `request.QualityId` starts with `"height:"` (e.g. `"height:720"`), select the highest representation `<= 720`.
- Otherwise, if `request.MaxHeight` is set, select the highest representation `<= request.MaxHeight` (falling back to the lowest available representation if all exceed `MaxHeight`).
