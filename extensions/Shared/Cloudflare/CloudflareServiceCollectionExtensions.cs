using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KariDownloads.Shared.Cloudflare;

public static class CloudflareServiceCollectionExtensions
{
    public static IServiceCollection AddCloudflareResilience(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("CloudflareResilience").Get<CloudflareResilienceOptions>() ?? new CloudflareResilienceOptions();

        services.AddSingleton(options);
        services.AddSingleton<CloudflareSessionStore>();

        services.AddHttpClient("CloudflareResilience", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(options.FlareSolverrTimeoutSeconds + 15);
        });

        if (options.Enabled)
        {
            services.AddHttpClient("FlareSolverr", client =>
            {
                client.BaseAddress = new Uri(options.FlareSolverrBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(options.FlareSolverrTimeoutSeconds + 15);
            });

            services.AddSingleton<IFlareSolverrGateway>(sp => new FlareSolverrGateway(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("FlareSolverr")));

            services.AddSingleton<ICloudflareResilienceService, CloudflareResilienceService>();
        }
        else
        {
            services.AddSingleton<ICloudflareResilienceService, DirectOnlyCloudflareResilienceService>();
        }

        return services;
    }
}
