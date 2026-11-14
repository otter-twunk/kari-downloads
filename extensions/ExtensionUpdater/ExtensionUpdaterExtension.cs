using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ExtensionUpdater;

public sealed class ExtensionUpdaterExtension : CoveExtensionBase, IUIExtension, IApiExtension, IStatefulExtension
{
    private readonly ExtensionUpdaterService _service = new();

    public void SetStore(IExtensionStore store) => _service.AttachStore(store);

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        services.AddSingleton(_service);
    }

    public override UIManifest GetUIManifest()
    {
        return ManifestBuilder()
            .WithRuntimeVersion("v1")
            .WithJsBundle("dist/bundle.js")
            .WithCssBundle("dist/bundle.css")
            .AddPage(
                route: "extension-updater",
                label: "Extension Updater",
                componentName: "ExtensionUpdaterPage",
                icon: "refresh-cw",
                showInNav: true,
                navOrder: 60)
            .Build();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ext/extension-updater");

        group.MapGet("/sources", async (CancellationToken ct) =>
        {
            var sources = await _service.GetSourcesAsync(ct);
            return Results.Ok(sources);
        }).RequireCovePermission("extensions.read");

        group.MapPost("/sources", async ([FromBody] ExtensionSourceEntry entry, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(entry.Url))
                return Results.BadRequest(new { message = "Source URL or GitHub owner/repo is required." });

            var saved = await _service.UpsertSourceAsync(entry, ct);
            return Results.Ok(saved);
        }).RequireCovePermission("extensions.install");

        group.MapDelete("/sources/{sourceId}", async (string sourceId, CancellationToken ct) =>
        {
            var deleted = await _service.DeleteSourceAsync(sourceId, ct);
            return deleted ? Results.Ok(new { deleted = true }) : Results.NotFound();
        }).RequireCovePermission("extensions.install");

        group.MapPost("/sources/reset-defaults", async (CancellationToken ct) =>
        {
            var current = await _service.GetSourcesAsync(ct);
            var defaults = ExtensionUpdaterService.GetDefaultSources();
            foreach (var def in defaults)
            {
                if (!current.Any(s => string.Equals(s.Id, def.Id, StringComparison.OrdinalIgnoreCase)))
                    current.Insert(0, def);
            }
            await _service.SaveSourcesAsync(current, ct);
            return Results.Ok(current);
        }).RequireCovePermission("extensions.install");

        group.MapPost("/check", async ([FromQuery] bool? inspectStaticZips, CancellationToken ct) =>
        {
            var result = await _service.CheckSourcesAsync(inspectStaticZips ?? true, ct);
            return Results.Ok(result);
        }).RequireCovePermission("extensions.read");
    }
}
