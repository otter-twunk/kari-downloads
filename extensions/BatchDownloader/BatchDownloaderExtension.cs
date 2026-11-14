using System.Text.RegularExpressions;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BatchDownloader;

public sealed partial class BatchDownloaderExtension : CoveExtensionBase, IUIExtension, IApiExtension
{
    private static readonly Regex UrlExtractRegex = new(@"https?://[^\s<>'""{}|\\^`\]\)]+", RegexOptions.Compiled);

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
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
        endpoints.MapPost("/api/ext/batch-downloader/match", async (
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
                    var best = urlMatches.FirstOrDefault();
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
        .WithTags("Batch Downloader")
        .WithSummary("Inspect URLs against installed Cove downloaders")
        .WithDescription("Extracts HTTP/HTTPS URLs from rawText or urls and matches each URL against registered IDownloaderProvider extensions concurrently.");

        endpoints.MapPost("/api/ext/batch-downloader/queue", async (
            BatchQueueApiRequest request,
            IDownloaderService downloaderService,
            IExtensionServiceScopeFactory scopeFactory,
            IJobService jobService,
            IVideoRepository videoRepo,
            IGalleryRepository galleryRepo,
            IImageRepository imageRepo,
            IAudioRepository audioRepo,
            CancellationToken ct) =>
        {
            var urls = ExtractUrls(request.RawText, request.Urls);
            if (urls.Count == 0)
            {
                return Results.BadRequest(new { error = "No valid URLs provided in request." });
            }

            // Match all URLs concurrently with bounded parallelism while preserving input order
            var matchedByIndex = new Cove.Core.DTOs.DownloaderMatchDto?[urls.Count];
            using (var matchSemaphore = new SemaphoreSlim(4, 4))
            {
                var matchTasks = urls.Select(async (url, index) =>
                {
                    await matchSemaphore.WaitAsync(ct);
                    try
                    {
                        var matches = await downloaderService.MatchUrlAsync(url, ct);
                        matchedByIndex[index] = matches.FirstOrDefault();
                    }
                    finally
                    {
                        matchSemaphore.Release();
                    }
                });
                await Task.WhenAll(matchTasks);
            }

            var queuedItems = new List<QueuedItem>();
            var skippedItems = new List<object>();

            var hasMetadata = (request.TagIds != null && request.TagIds.Count > 0)
                || request.StudioId.HasValue
                || (request.PerformerIds != null && request.PerformerIds.Count > 0);

            for (var idx = 0; idx < urls.Count; idx++)
            {
                var url = urls[idx];
                var match = matchedByIndex[idx];
                if (match == null)
                {
                    skippedItems.Add(new { url, reason = "No compatible downloader matched this URL." });
                    continue;
                }

                var entityStr = !string.IsNullOrWhiteSpace(request.Entity) && !string.Equals(request.Entity, "auto", StringComparison.OrdinalIgnoreCase)
                    ? request.Entity
                    : match.SupportedEntity;

                if (!Enum.TryParse<DownloaderEntity>(entityStr, true, out var entityType))
                {
                    entityType = DownloaderEntity.Video;
                }

                var effectiveUrl = string.IsNullOrWhiteSpace(match.NormalizedUrl) ? url : match.NormalizedUrl;
                var label = string.IsNullOrWhiteSpace(match.Label) ? url : match.Label;

                int? entityId = null;
                if (hasMetadata)
                {
                    try
                    {
                        entityId = await CreateEntityWithMetadataAsync(
                            entityType,
                            effectiveUrl,
                            label,
                            request.StudioId,
                            request.TagIds,
                            request.PerformerIds,
                            videoRepo,
                            galleryRepo,
                            imageRepo,
                            audioRepo,
                            ct);
                    }
                    catch
                    {
                        // Proceed without entityId if metadata pre-creation fails
                    }
                }

                var qualityId = string.IsNullOrWhiteSpace(request.QualityId) ? null : request.QualityId.Trim();

                queuedItems.Add(new QueuedItem(
                    match.DownloaderId,
                    effectiveUrl,
                    entityType,
                    entityId,
                    label,
                    qualityId, // null allows Cove 1.8+ MaxDownloadHeight to govern stream resolution
                    match.SourceUrl));
            }

            if (queuedItems.Count == 0)
            {
                return Results.Ok(new
                {
                    jobId = (string?)null,
                    queuedCount = 0,
                    skippedCount = skippedItems.Count,
                    skipped = skippedItems
                });
            }

            var allowDuplicates = request.AllowDuplicates ?? false;
            var jobId = jobService.Enqueue(
                "download-batch",
                $"Downloading {queuedItems.Count} item{(queuedItems.Count == 1 ? "" : "s")}",
                async (progress, jobCt) =>
                {
                    progress.DeclareUnitCount(queuedItems.Count);
                    var batchResult = await jobService.RunBatchAsync(
                        queuedItems,
                        maxInFlight: 2,
                        async (item, unit, unitCt) =>
                        {
                            await using var scope = scopeFactory.CreateAsyncScope();
                            var scopedDownloader = scope.ServiceProvider.GetRequiredService<IDownloaderService>();
                            unit.Report(0.0, $"Downloading {item.Label}");

                            var result = await scopedDownloader.DownloadAndImportAsync(
                                new DownloaderImportRequest(
                                    item.DownloaderId,
                                    item.Url,
                                    item.Entity,
                                    item.EntityId,
                                    item.QualityId,
                                    item.SourceUrl)
                                {
                                    AllowDuplicateDownload = allowDuplicates
                                },
                                progress: null,
                                unitCt);

                            if (result == null)
                            {
                                unit.Complete(JobUnitOutcome.Skipped, "Downloader returned no result.");
                            }
                            else
                            {
                                unit.Complete(JobUnitOutcome.Succeeded, $"Imported {item.Label}");
                            }
                        },
                        progress,
                        unitIdFactory: (item, i) => $"{i}:{item.Url}",
                        labelFactory: item => item.Label,
                        ct: jobCt);

                    progress.SetSummary(batchResult.Summary);
                },
                exclusive: false);

            return Results.Accepted(value: new
            {
                jobId,
                queuedCount = queuedItems.Count,
                skippedCount = skippedItems.Count,
                queued = queuedItems.Select(q => new { q.Url, entity = q.Entity.ToString(), q.Label, q.EntityId }),
                skipped = skippedItems
            });
        })
        .AllowWithoutCovePermission()
        .WithTags("Batch Downloader")
        .WithSummary("Queue a batch of URLs for background download and import")
        .WithDescription("Matches URLs concurrently, optionally pre-creates tagged library entities, and enqueues a background download-batch job respecting Cove MaxDownloadHeight.");
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
        CancellationToken ct)
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

    private sealed record QueuedItem(
        string DownloaderId,
        string Url,
        DownloaderEntity Entity,
        int? EntityId,
        string Label,
        string? QualityId,
        string? SourceUrl);
}

public sealed class BatchQueueApiRequest
{
    public string? RawText { get; set; }
    public List<string>? Urls { get; set; }
    public string? Entity { get; set; }
    public string? QualityId { get; set; }
    public List<int>? TagIds { get; set; }
    public int? StudioId { get; set; }
    public List<int>? PerformerIds { get; set; }
    public bool? AutoApplyMetadata { get; set; }
    public bool? AllowDuplicates { get; set; }
}

public sealed class BatchMatchApiRequest
{
    public string? RawText { get; set; }
    public List<string>? Urls { get; set; }
}
