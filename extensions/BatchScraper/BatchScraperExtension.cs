using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BatchScraper;

public sealed class BatchScraperExtension : CoveExtensionBase, IUIExtension, IActionExtension, IApiExtension, IStatefulExtension
{
    private readonly BatchScraperQueueService _queueService = new();

    public void SetStore(IExtensionStore store) => _queueService.AttachStore(store);

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        services.AddSingleton(_queueService);
    }

    public IReadOnlyList<ExtensionAction> GetActions()
    {
        return
        [
            new ExtensionAction(
                Id: "batch-video-scrape",
                Label: "Scrape All",
                ExtensionId: Id,
                ActionType: "bulk",
                EntityTypes: ["video"],
                Icon: "sparkles",
                HandlerName: "openBatchScraper",
                Order: 50,
                SuppressSuccessAlert: true
            )
        ];
    }

    public override UIManifest GetUIManifest()
    {
        return ManifestBuilder()
            .WithRuntimeVersion("v1")
            .WithJsBundle("dist/bundle.js")
            .WithCssBundle("dist/bundle.css")
            .AddSlot(
                slot: "app-floating-ui",
                componentName: "BatchScraperModal"
            )
            .AddAction(
                id: "batch-video-scrape",
                label: "Scrape All",
                actionType: "bulk",
                entityTypes: ["video"],
                icon: "sparkles",
                handlerName: "openBatchScraper",
                order: 50,
                suppressSuccessAlert: true
            )
            .Build();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ext/batch-scraper")
            .WithTags("Batch Scraper");

        group.MapGet("/queue", async ([FromQuery] bool? recover, CancellationToken ct) =>
        {
            var state = await _queueService.GetQueueAsync(recover ?? false, ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Get persistent batch scrape queue")
        .WithDescription("Returns the persisted batch scraper queue state, blend configuration, and item statuses.");

        group.MapPut("/queue", async ([FromBody] BatchScraperQueueStateDto state, CancellationToken ct) =>
        {
            var saved = await _queueService.SaveQueueAsync(state, ct);
            return Results.Ok(saved);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Save persistent batch scrape queue")
        .WithDescription("Persists the current batch scraper queue state and metadata blend settings.");

        group.MapPost("/queue/add", async ([FromBody] BatchScraperAddRequest request, CancellationToken ct) =>
        {
            var (state, addedCount) = await _queueService.AddVideosAsync(request.VideoIds, ct);
            return Results.Ok(new { addedCount, state });
        })
        .AllowWithoutCovePermission()
        .WithSummary("Append videos to the persistent batch scrape queue")
        .WithDescription("Adds new video IDs to the persistent batch scrape queue (or re-queues previously failed/skipped videos).");

        group.MapPost("/queue/pause", async (CancellationToken ct) =>
        {
            var state = await _queueService.PauseQueueAsync(ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Pause the batch scrape queue")
        .WithDescription("Pauses the batch scrape queue and resets any interrupted in-flight items to pending.");

        group.MapPost("/queue/finish-after-current", async ([FromBody] BatchScraperFinishAfterCurrentRequest? request, CancellationToken ct) =>
        {
            var state = await _queueService.SetFinishAfterCurrentAsync(request?.Enabled, ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Toggle finish after current job")
        .WithDescription("Configures the batch scraper queue to pause automatically as soon as the currently running item(s) finish.");

        group.MapPost("/queue/skip/{videoId:int}", async (int videoId, CancellationToken ct) =>
        {
            var state = await _queueService.SkipItemAsync(videoId, ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Skip a video in the batch scrape queue")
        .WithDescription("Marks a pending or running video in the persistent batch scrape queue as skipped.");

        group.MapDelete("/queue", async ([FromQuery] bool? onlyFinished, CancellationToken ct) =>
        {
            var state = await _queueService.ClearQueueAsync(onlyFinished ?? false, ct);
            return Results.Ok(state);
        })
        .AllowWithoutCovePermission()
        .WithSummary("Clear persistent batch scrape queue")
        .WithDescription("Clears either finished items (onlyFinished=true) or all items from the persistent batch scrape queue.");
    }
}
