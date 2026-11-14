using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace ExampleUi;

public sealed class ExampleUiExtension : CoveExtensionBase, IUIExtension
{
    // Identity & metadata live in extension.json (the single source of truth); CoveExtensionBase surfaces them.

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
    }

    public override UIManifest GetUIManifest()
    {
        return ManifestBuilder()
            .WithRuntimeVersion("v1")
            .Build();
    }
}
