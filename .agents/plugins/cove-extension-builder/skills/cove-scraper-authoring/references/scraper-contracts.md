# Cove Compiled C# Scraper Contracts & DTOs (`v1.7.0`)

## 1. `IScraperProvider` & Descriptors (`Cove.Plugins`)

```csharp
namespace Cove.Plugins;

[Flags]
public enum ScraperCapabilities
{
    None = 0,
    ByUrl = 1 << 0,
    ByName = 1 << 1,
    ByFragment = 1 << 2,
    ByQueryFragment = 1 << 3
}

public enum ScraperRiskLevel
{
    None,
    NetworkOnly,
    RemoteCode
}

public enum ScraperEntity
{
    Video,
    Performer,
    Gallery,
    Image,
    Group,
    Audio,
    Text
}

public sealed record ScraperPermissions(
    IReadOnlyList<string>? AllowNetworkHosts = null,
    bool AllowJavaScript = false,
    bool AllowCdp = false);

public sealed record ScraperDescriptor(
    string Id,
    string Name,
    ScraperEntity Entity,
    ScraperCapabilities Capabilities,
    IReadOnlyList<string> SupportedUrls,
    ScraperRiskLevel Risk,
    IReadOnlyList<string>? PreferenceSites)
{
    public ScraperDescriptor(
        string id,
        string name,
        ScraperEntity entity,
        ScraperCapabilities capabilities,
        IReadOnlyList<string> supportedUrls,
        ScraperRiskLevel risk = ScraperRiskLevel.NetworkOnly)
        : this(id, name, entity, capabilities, supportedUrls, risk, null)
    {
    }
}

public sealed record ScraperRequest<TInput>(
    string ScraperId,
    TInput Input,
    ScraperPermissions Permissions);

public interface IScraperProvider : IExtension
{
    IReadOnlyList<ScraperDescriptor> GetScrapers();

    Task<ScrapedVideoDto?> ScrapeVideoAsync(ScraperRequest<VideoScrapeInput> request, CancellationToken ct)
        => Task.FromResult<ScrapedVideoDto?>(null);
    Task<IReadOnlyList<ScrapedVideoDto>> SearchVideosAsync(ScraperRequest<string> request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScrapedVideoDto>>([]);

    Task<ScrapedPerformerDto?> ScrapePerformerAsync(ScraperRequest<PerformerScrapeInput> request, CancellationToken ct)
        => Task.FromResult<ScrapedPerformerDto?>(null);
    Task<IReadOnlyList<ScrapedPerformerDto>> SearchPerformersAsync(ScraperRequest<string> request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScrapedPerformerDto>>([]);

    Task<ScrapedGalleryDto?> ScrapeGalleryAsync(ScraperRequest<GalleryScrapeInput> request, CancellationToken ct)
        => Task.FromResult<ScrapedGalleryDto?>(null);
    Task<IReadOnlyList<ScrapedGalleryDto>> SearchGalleriesAsync(ScraperRequest<string> request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScrapedGalleryDto>>([]);

    Task<ScrapedImageDto?> ScrapeImageAsync(ScraperRequest<ImageScrapeInput> request, CancellationToken ct)
        => Task.FromResult<ScrapedImageDto?>(null);
    Task<IReadOnlyList<ScrapedImageDto>> SearchImagesAsync(ScraperRequest<string> request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScrapedImageDto>>([]);

    Task<ScrapedGroupDto?> ScrapeGroupAsync(ScraperRequest<GroupScrapeInput> request, CancellationToken ct)
        => Task.FromResult<ScrapedGroupDto?>(null);
    Task<IReadOnlyList<ScrapedGroupDto>> SearchGroupsAsync(ScraperRequest<string> request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScrapedGroupDto>>([]);

    Task<ScrapedAudioDto?> ScrapeAudioAsync(ScraperRequest<AudioScrapeInput> request, CancellationToken ct)
        => Task.FromResult<ScrapedAudioDto?>(null);
    Task<IReadOnlyList<ScrapedAudioDto>> SearchAudiosAsync(ScraperRequest<string> request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScrapedAudioDto>>([]);

    Task<ScrapedTextDto?> ScrapeTextAsync(ScraperRequest<TextScrapeInput> request, CancellationToken ct)
        => Task.FromResult<ScrapedTextDto?>(null);
    Task<IReadOnlyList<ScrapedTextDto>> SearchTextsAsync(ScraperRequest<string> request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScrapedTextDto>>([]);
}
```

> **Important Contract Note**:
> - `Scrape*Async` and `Search*Async` take `(ScraperRequest<T>, CancellationToken)` — there is **no** `IScraperHost` parameter.
> - There is no `ScraperEntity.Studio`; studio attribution is returned via `StudioName` on `ScrapedVideoDto`, `ScrapedGalleryDto`, `ScrapedImageDto`, `ScrapedGroupDto`, `ScrapedAudioDto`, and `ScrapedTextDto`.

---

## 2. Input & Output DTOs (`Cove.Core.DTOs`)

### Inputs
- **`VideoScrapeInput`**: `Guid? LocalVideoId`, `string? Url`, `List<string> Urls`, `string? Title`, `string? Code`, `string? Date`, `string? Details`, `string? Director`, `List<VideoScrapeFile> Files` (`Path`, `Format`, `Width`, `Height`, `DurationSeconds`, `VideoCodec`, `AudioCodec`, `FrameRate`, `BitRate`, `SizeBytes`, `List<VideoScrapeFingerprint> Fingerprints`).
- **`PerformerScrapeInput`**: `Guid? LocalPerformerId`, `string? Url`, `List<string> Urls`, `string? Name`, `string? Disambiguation`, `string? Gender`, `string? Birthdate`, `string? Country`, `string? Ethnicity`, `string? EyeColor`, `string? HairColor`, `int? HeightCm`, `int? Weight`, `string? Measurements`, `string? Details`, `List<string> Aliases`.
- **`GalleryScrapeInput`**: `Guid? LocalGalleryId`, `string? Url`, `List<string> Urls`, `string? Title`, `string? Code`, `string? Date`, `string? Details`, `string? Photographer`.
- **`ImageScrapeInput`**: `Guid? LocalImageId`, `string? Url`, `List<string> Urls`, `string? Title`, `string? Date`, `string? Details`, `string? Photographer`.
- **`GroupScrapeInput`**: `Guid? LocalGroupId`, `string? Url`, `List<string> Urls`, `string? Name`, `string? Aliases`, `int? Duration`, `string? Date`, `string? Director`, `string? Details`, `string? Synopsis`.
- **`AudioScrapeInput` / `TextScrapeInput`**: `Guid? LocalAudioId`/`LocalTextId`, `string? Url`, `List<string> Urls`, `string? Title`, `string? Code`, `string? Date`, `string? Details`.

### Outputs
- **`ScrapedVideoDto`**:
  - `string? SourceScraperId`
  - `string? Title`
  - `string? Code`
  - `string? Details`
  - `string? Director`
  - `string? Date` (ISO `yyyy-MM-dd`)
  - `string? ImageUrl`
  - `List<string> Urls`
  - `string? StudioName`
  - `List<string> PerformerNames`
  - `List<string> TagNames`
- **`ScrapedPerformerDto`**:
  - `string? SourceScraperId`, `string? Name`, `string? Disambiguation`, `string? Gender`, `string? Birthdate` (`yyyy-MM-dd`), `string? Country`, `string? Ethnicity`, `string? EyeColor`, `string? HairColor`, `int? HeightCm`, `int? Weight`, `string? Measurements`, `string? Tattoos`, `string? Piercings`, `string? Details`, `string? ImageUrl`, `List<string> Urls`, `List<string> Aliases`, `List<string> TagNames`
- **`ScrapedGalleryDto`**:
  - `string? Title`, `string? Code`, `string? Date`, `string? Details`, `string? Photographer`, `string? ImageUrl`, `List<string> Urls`, `string? StudioName`, `List<string> PerformerNames`, `List<string> TagNames` *(no `SourceScraperId` property on `ScrapedGalleryDto`)*
- **`ScrapedImageDto`**:
  - `string? SourceScraperId`, `string? Title`, `string? Date`, `string? Details`, `string? Photographer`, `string? ImageUrl`, `List<string> Urls`, `string? StudioName`, `List<string> PerformerNames`, `List<string> TagNames`, `string? GalleryTitle`
- **`ScrapedGroupDto`**:
  - `string? SourceScraperId`, `string? Name`, `List<string> Aliases`, `int? Duration`, `string? Date`, `string? Director`, `string? Details`, `string? Synopsis`, `int? Rating`, `string? ImageUrl`, `List<string> Urls`, `string? StudioName`, `List<string> TagNames`
- **`ScrapedAudioDto` / `ScrapedTextDto`**:
  - `string? Title`, `string? Code`, `string? Date`, `string? Details`, `string? ImageUrl`, `List<string> Urls`, `string? StudioName`, `List<string> PerformerNames`, `List<string> TagNames`
