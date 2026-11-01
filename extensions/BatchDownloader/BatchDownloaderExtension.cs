using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace BatchDownloader;

public sealed class BatchDownloaderExtension : CoveExtensionBase, IUIExtension
{
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
}
