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
            var matches = new List<object>();

            foreach (var url in urls)
            {
                var urlMatches = await downloaderService.MatchUrlAsync(url, ct);
                var best = urlMatches.FirstOrDefault();
                matches.Add(new
                {
                    url,
                    matched = best != null,
                    downloaderId = best?.DownloaderId,
                    downloaderName = best?.DownloaderName,
                    supportedEntity = best?.SupportedEntity,
                    normalizedUrl = best?.NormalizedUrl ?? url,
                    label = best?.Label ?? url
                });
            }

            return Results.Ok(new { count = matches.Count, matches });
        }).AllowWithoutCovePermission();

        endpoints.MapPost("/api/ext/batch-downloader/queue", async (
            BatchQueueApiRequest request,
            IDownloaderService downloaderService,
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

            var queuedItems = new List<QueuedItem>();
            var skippedItems = new List<object>();

            foreach (var url in urls)
            {
                var matches = await downloaderService.MatchUrlAsync(url, ct);
                var match = matches.FirstOrDefault();
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
                var hasMetadata = (request.TagIds != null && request.TagIds.Count > 0)
                    || request.StudioId.HasValue
                    || (request.PerformerIds != null && request.PerformerIds.Count > 0);

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

                queuedItems.Add(new QueuedItem(
                    match.DownloaderId,
                    effectiveUrl,
                    entityType,
                    entityId,
                    label,
                    match.QualityOptions?.FirstOrDefault()?.Id,
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
                    for (var i = 0; i < queuedItems.Count; i++)
                    {
                        if (jobCt.IsCancellationRequested) break;
                        var item = queuedItems[i];
                        progress.Report((double)i / queuedItems.Count, $"Downloading ({i + 1}/{queuedItems.Count}): {item.Label}");
                        try
                        {
                            await downloaderService.DownloadAndImportAsync(
                                new DownloaderImportRequest(item.DownloaderId, item.Url, item.Entity, item.EntityId, item.QualityId, item.SourceUrl)
                                {
                                    AllowDuplicateDownload = allowDuplicates
                                },
                                progress,
                                jobCt);
                        }
                        catch
                        {
                            // continue with remaining items
                        }
                    }
                    progress.Report(1.0, $"Finished downloading {queuedItems.Count} items");
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
        }).AllowWithoutCovePermission();
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

    private static async Task<int?> CreateEntityWithMetadataAsync(
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
                    TagIds = tagIds?.ToArray() ?? [],
                    PerformerIds = performerIds?.ToArray() ?? []
                };
                if (tagIds != null)
                {
                    foreach (var tid in tagIds)
                        video.VideoTags.Add(new VideoTag { TagId = tid });
                }
                if (performerIds != null)
                {
                    foreach (var pid in performerIds)
                        video.VideoPerformers.Add(new VideoPerformer { PerformerId = pid });
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
                    StudioId = studioId
                };
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
                    StudioId = studioId
                };
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
                    StudioId = studioId
                };
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
