using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KariDownloads.Shared.Cloudflare;

public sealed class DirectOnlyCloudflareResilienceService : ICloudflareResilienceService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public DirectOnlyCloudflareResilienceService(IHttpClientFactory httpClientFactory)
        => _httpClientFactory = httpClientFactory;

    public Task<CloudflareRequestResult> GetAsync(CloudflareRequestContext context, CloudflareSitePolicy policy, CancellationToken ct)
        => SendAsync(context, policy, HttpMethod.Get, null, ct);

    public Task<CloudflareRequestResult> PostAsync(CloudflareRequestContext context, CloudflareSitePolicy policy, HttpContent content, CancellationToken ct)
        => SendAsync(context, policy, HttpMethod.Post, content, ct);

    private async Task<CloudflareRequestResult> SendAsync(
        CloudflareRequestContext context,
        CloudflareSitePolicy policy,
        HttpMethod method,
        HttpContent? content,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("CloudflareResilience");
        using var request = new HttpRequestMessage(method, context.Url);

        foreach (var header in context.Headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (context.Cookies?.Count > 0)
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", context.Cookies.Select(pair => $"{pair.Key}={pair.Value}")));

        if (content is not null)
            request.Content = content;

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        var outcome = CloudflareChallengeClassifier.Classify(response, html);

        return outcome is CloudflareOutcome.Success
            ? CloudflareRequestResult.Success(response, context.Cookies ?? new Dictionary<string, string>(), null)
            : CloudflareRequestResult.Failure(outcome, $"Cloudflare resilience is disabled; {outcome} detected for {context.Url}.", null, context.Cookies);
    }
}
