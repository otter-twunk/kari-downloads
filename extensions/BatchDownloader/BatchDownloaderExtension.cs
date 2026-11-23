using System.Text.RegularExpressions;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BatchDownloader;

public sealed partial class BatchDownloaderExtension : CoveExtensionBase, IUIExtension, IApiExtension, IStatefulExtension
{
    // Cove sorts by display name, placing its generic Web page/Text provider before X video.
    public static Cove.Core.DTOs.DownloaderMatchDto? SelectPreferredMatch(IReadOnlyList<Cove.Core.DTOs.DownloaderMatchDto> matches)
        => matches.FirstOrDefault(m => !string.Equals(m.SupportedEntity, "Text", StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault();
    private static readonly Regex UrlExtractRegex = new(@"https?://[^\s<>'""{}|\\^`\]\)]+", RegexOptions.Compiled);
    private readonly BatchDownloadQueueService _queueService = new();

    public BatchDownloadQueueService QueueService => _queueService;

    public void SetStore(IExtensionStore store) => _queueService.AttachStore(store);

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        services.AddSingleton(_queueService);
    }

    public override UIManifest GetUIManifest()
    {
        return ManifestBuilder()
            .WithRuntimeVersion("v1")
            .AddPage(
                route: "batch-download",
                label: "Paste & Download",
                componentName: "BatchDownloadPage",
                icon: "download",
                showInNav: true,
                navOrder: 45
            )
            .Build();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ext/batch-downloader")
            .WithTags("Batch Downloader");

        group.MapPost("/match", async (
            BatchMatchApiRequest request,
            IDownloaderService downloaderService,
            CancellationToken ct) =>
        {
            var urls = ExtractUrls(request.RawText, request.Urls);
            var results = new object[urls.Count];
            using var semaphore = new SemaphoreSlim(4, 4);

            var tasks = urls.Select(async (url, index) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var urlMatches = await downloaderService.MatchUrlAsync(url, ct);
                    var best = SelectPreferredMatch(urlMatches);
                    results[index] = new
                    {
                        url,
                        matched = best != null,
                        downloaderId = best?.DownloaderId,
                        downloaderName = best?.DownloaderName,
                        supportedEntity = best?.SupportedEntity,
                        normalizedUrl = best?.NormalizedUrl ?? url,
                        label = best?.Label ?? url,
                        sourceUrl = best?.SourceUrl,
                        qualityOptions = best?.QualityOptions
                    };
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            return Results.Ok(new { count = results.Length, matches = results });
        })
        .AllowWithoutCovePermission()
        .WithSummary("Inspect URLs against installed Cove downloaders")
        .WithDescription("Extracts HTTP/HTTPS URLs from rawText or urls and matches each URL against registered IDownloaderProvider extensions concurrently.");

        group.MapGet("/queue", async (CancellationToken ct) =>
        {
            var state = await _queueService.GetStateAsync(ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Get persistent batch download queue state")
        .WithDescription("Returns the persistent download queue status, finishAfterCurrent flag, counters, and all queued items.");

        group.MapPost("/queue", async (
            BatchQueueApiRequest request,
            IDownloaderService downloaderService,
            IExtensionServiceScopeFactory scopeFactory,
            IJobService jobService,
            CancellationToken ct) =>
        {
            var hasUrls = !string.IsNullOrWhiteSpace(request.RawText)
                || (request.Urls is { Count: > 0 })
                || (request.Items is { Count: > 0 });

            if (!hasUrls)
            {
                return Results.BadRequest(new { error = "No valid URLs provided in request." });
            }

            var result = await _queueService.EnqueueAsync(
                request,
                downloaderService,
                scopeFactory,
                jobService,
                ct);

            return Results.Accepted(value: new
            {
                jobId = result.JobId,
                queuedCount = result.QueuedCount,
                skippedCount = result.SkippedCount,
                queued = result.Queued.Select(q => new { q.Id, q.Url, entity = q.Entity, q.Label, q.EntityId }),
                skipped = result.Skipped,
                queue = result.Queue
            });
        })
        .AllowWithoutCovePermission()
        .WithSummary("Add URLs to the persistent batch download queue")
        .WithDescription("Matches URLs concurrently, appends them to the persistent download queue, and starts or continues background processing.");

        group.MapPost("/queue/pause", async (CancellationToken ct) =>
        {
            var state = await _queueService.PauseAsync(ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Pause the persistent download queue")
        .WithDescription("Pauses queue processing immediately and returns any interrupted download back to Pending so it can be resumed.");

        group.MapPost("/queue/resume", async (
            IExtensionServiceScopeFactory scopeFactory,
            IJobService jobService,
            CancellationToken ct) =>
        {
            var state = await _queueService.ResumeAsync(scopeFactory, jobService, ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Resume or start the persistent download queue")
        .WithDescription("Resumes processing Pending items in the persistent download queue.");

        group.MapPost("/queue/finish-after-current", async (
            [FromBody] BatchFinishAfterCurrentRequest? request,
            CancellationToken ct) =>
        {
            var state = await _queueService.SetFinishAfterCurrentAsync(request?.Enabled, ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Toggle finish after current job")
        .WithDescription("Configures the download queue to finish the currently active download and then pause before starting the next item.");

        group.MapPost("/queue/skip/{itemId}", async (string itemId, CancellationToken ct) =>
        {
            var state = await _queueService.SkipItemAsync(itemId, ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Skip a specific item in the download queue")
        .WithDescription("Skips a pending or currently downloading item and advances the queue to the next pending item.");

        group.MapPost("/queue/skip-current", async (CancellationToken ct) =>
        {
            var state = await _queueService.SkipCurrentAsync(ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Skip the currently active download job")
        .WithDescription("Cancels the currently running download item, marks it as Skipped, and advances immediately to the next pending item.");

        group.MapPost("/queue/retry/{itemId}", async (
            string itemId,
            IExtensionServiceScopeFactory scopeFactory,
            IJobService jobService,
            CancellationToken ct) =>
        {
            var state = await _queueService.RetryFailedOrSkippedAsync(itemId, true, scopeFactory, jobService, ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Retry a failed or skipped item in the download queue")
        .WithDescription("Resets a failed or skipped item to Pending and starts queue processing.");

        group.MapPost("/queue/retry-failed", async (
            IExtensionServiceScopeFactory scopeFactory,
            IJobService jobService,
            CancellationToken ct) =>
        {
            var state = await _queueService.RetryFailedOrSkippedAsync(null, true, scopeFactory, jobService, ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Retry all failed and skipped items in the download queue")
        .WithDescription("Resets all Failed and Skipped items to Pending and starts queue processing.");

        group.MapDelete("/queue/{itemId}", async (string itemId, CancellationToken ct) =>
        {
            var state = await _queueService.RemoveItemAsync(itemId, ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Remove an item from the persistent download queue")
        .WithDescription("Removes the specified item from the persistent download queue (cancelling it if currently running).");

        group.MapPost("/queue/clear", async ([FromQuery] bool? onlyFinished, CancellationToken ct) =>
        {
            var state = await _queueService.ClearAsync(onlyFinished ?? true, ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Clear finished or all items from the download queue")
        .WithDescription("Removes completed, skipped, and failed items (or all items when onlyFinished=false) from the persistent download queue.");
    }

    public static List<string> ExtractUrls(string? rawText, IEnumerable<string>? urls)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (urls != null)
        {
            foreach (var u in urls)
            {
                var cleaned = CleanUrl(u);
                if (!string.IsNullOrEmpty(cleaned))
                    set.Add(cleaned);
            }
        }

        if (!string.IsNullOrWhiteSpace(rawText))
        {
            foreach (Match m in UrlExtractRegex.Matches(rawText))
            {
                var cleaned = CleanUrl(m.Value);
                if (!string.IsNullOrEmpty(cleaned))
                    set.Add(cleaned);
            }
        }

        return [.. set];
    }

    private static string CleanUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '>');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : string.Empty;
    }

    public static async Task<int?> CreateEntityWithMetadataAsync(
        DownloaderEntity entity,
        string url,
        string title,
        int? studioId,
        List<int>? tagIds,
        List<int>? performerIds,
        IVideoRepository videoRepo,
        IGalleryRepository galleryRepo,
        IImageRepository imageRepo,
        IAudioRepository audioRepo,
        CancellationToken ct,
        int? groupId = null)
    {
        var distinctTags = tagIds?.Where(id => id > 0).Distinct().ToArray() ?? [];
        var distinctPerformers = performerIds?.Where(id => id > 0).Distinct().ToArray() ?? [];

        switch (entity)
        {
            case DownloaderEntity.Video:
            {
                var video = new Video
                {
                    Title = title,
                    Organized = false,
                    Urls = [new VideoUrl { Url = url }],
                    StudioId = studioId,
                    TagIds = distinctTags,
                    PerformerIds = distinctPerformers
                };
                foreach (var tid in distinctTags)
                    video.VideoTags.Add(new VideoTag { TagId = tid });
                foreach (var pid in distinctPerformers)
                    video.VideoPerformers.Add(new VideoPerformer { PerformerId = pid });
                if (groupId is > 0)
                {
                    video.GroupItems.Add(new GroupItem
                    {
                        GroupId = groupId.Value,
                        OrderIndex = 0,
                        Kind = GroupItemKind.Video,
                        HostType = "video",
                        Title = title
                    });
                }
                var created = await videoRepo.AddAsync(video, ct);
                return created.Id;
            }
            case DownloaderEntity.Gallery:
            {
                var gallery = new Gallery
                {
                    Title = title,
                    Organized = false,
                    Urls = [new GalleryUrl { Url = url }],
                    StudioId = studioId,
                    TagIds = distinctTags,
                    PerformerIds = distinctPerformers,
                    TagCount = distinctTags.Length,
                    PerformerCount = distinctPerformers.Length
                };
                foreach (var tid in distinctTags)
                    gallery.GalleryTags.Add(new GalleryTag { TagId = tid });
                foreach (var pid in distinctPerformers)
                    gallery.GalleryPerformers.Add(new GalleryPerformer { PerformerId = pid });
                var created = await galleryRepo.AddAsync(gallery, ct);
                return created.Id;
            }
            case DownloaderEntity.Image:
            {
                var image = new Image
                {
                    Title = title,
                    Organized = false,
                    Urls = [new ImageUrl { Url = url }],
                    StudioId = studioId,
                    TagIds = distinctTags,
                    PerformerIds = distinctPerformers,
                    TagCount = distinctTags.Length,
                    PerformerCount = distinctPerformers.Length
                };
                foreach (var tid in distinctTags)
                    image.ImageTags.Add(new ImageTag { TagId = tid });
                foreach (var pid in distinctPerformers)
                    image.ImagePerformers.Add(new ImagePerformer { PerformerId = pid });
                var created = await imageRepo.AddAsync(image, ct);
                return created.Id;
            }
            case DownloaderEntity.Audio:
            {
                var audio = new Audio
                {
                    Title = title,
                    Organized = false,
                    Urls = [new AudioUrl { Url = url }],
                    StudioId = studioId,
                    TagIds = distinctTags,
                    PerformerIds = distinctPerformers
                };
                foreach (var tid in distinctTags)
                    audio.AudioTags.Add(new AudioTag { TagId = tid });
                foreach (var pid in distinctPerformers)
                    audio.AudioPerformers.Add(new AudioPerformer { PerformerId = pid });
                var created = await audioRepo.AddAsync(audio, ct);
                return created.Id;
            }
            default:
                return null;
        }
    }
}

public sealed class BatchQueueApiRequest
{
    public string? RawText { get; set; }
    public List<string>? Urls { get; set; }
    public List<BatchQueueItemInput>? Items { get; set; }
    public string? Entity { get; set; }
    public string? QualityId { get; set; }
    public List<int>? TagIds { get; set; }
    public int? StudioId { get; set; }
    public int? GroupId { get; set; }
    public List<int>? PerformerIds { get; set; }
    public bool? AutoApplyMetadata { get; set; }
    public bool? AllowDuplicates { get; set; }
    public bool? AutoStart { get; set; }
}

public sealed class BatchMatchApiRequest
{
    public string? RawText { get; set; }
    public List<string>? Urls { get; set; }
}
