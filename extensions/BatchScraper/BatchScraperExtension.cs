using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace BatchScraper;

public sealed class BatchScraperExtension : CoveExtensionBase, IUIExtension, IActionExtension
{
    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
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
}
