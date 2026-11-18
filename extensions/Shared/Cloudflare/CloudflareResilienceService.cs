using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KariDownloads.Shared.Cloudflare;

public interface ICloudflareResilienceService
{
    Task<CloudflareRequestResult> GetAsync(CloudflareRequestContext context, CloudflareSitePolicy policy, CancellationToken ct);
    Task<CloudflareRequestResult> PostAsync(CloudflareRequestContext context, CloudflareSitePolicy policy, HttpContent content, CancellationToken ct);
}

public sealed class CloudflareResilienceService : ICloudflareResilienceService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CloudflareResilienceOptions _options;
    private readonly CloudflareSessionStore _sessions;
    private readonly IFlareSolverrGateway _resolver;
    private readonly SemaphoreSlim _solveLock;

    public CloudflareResilienceService(
        IHttpClientFactory httpClientFactory,
        CloudflareResilienceOptions options,
        CloudflareSessionStore sessions,
        IFlareSolverrGateway resolver)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _sessions = sessions;
        _resolver = resolver;
        _solveLock = new SemaphoreSlim(options.MaxConcurrentSolves, options.MaxConcurrentSolves);
    }

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
        EnsureAllowedHost(context.Url, policy);

        var first = await SendDirectAsync(context, method, content, ct);
        var firstHtml = await SafeReadHtmlAsync(first.Response!, ct);
        var firstOutcome = CloudflareChallengeClassifier.Classify(first.Response!, firstHtml);

        if (firstOutcome is CloudflareOutcome.Success)
            return first;

        if (firstOutcome is not CloudflareOutcome.CloudflareChallenge)
            return ToFailure(firstOutcome, first, context);

        var session = _sessions.Get(policy.SessionKey);

        if (session?.CooldownUntil > DateTimeOffset.UtcNow)
            return CloudflareRequestResult.Failure(
                CloudflareOutcome.RateLimited,
                $"Cloudflare resolver is cooling down for '{policy.SessionKey}' until {session.CooldownUntil:u}.",
                session.SessionId,
                session.Cookies);

        var solution = await SolveChallengeAsync(context.Url, session?.SessionId, policy, ct);

        if (solution is null)
            return CloudflareRequestResult.Failure(
                CloudflareOutcome.ChallengeUnresolved,
                $"FlareSolverr could not resolve the Cloudflare challenge for {context.Url}.",
                session?.SessionId,
                session?.Cookies);

        var cookies = MergeCookies(context.Cookies, session?.Cookies, solution.Cookies);
        var sessionId = solution.SessionId ?? session?.SessionId ?? string.Empty;

        _sessions.Save(policy.SessionKey, new CloudflareSessionState(sessionId, cookies, DateTimeOffset.UtcNow, null));

        var retryContext = context with { Cookies = cookies, FlareSolverrSessionId = sessionId };
        var retry = await SendDirectAsync(retryContext, method, content, ct);
        var retryHtml = await SafeReadHtmlAsync(retry.Response!, ct);
        var retryOutcome = CloudflareChallengeClassifier.Classify(retry.Response!, retryHtml);

        return retryOutcome is CloudflareOutcome.Success
            ? new CloudflareRequestResult(CloudflareOutcome.ChallengeSolved, retry.Response, "Cloudflare challenge resolved.", sessionId, cookies)
            : ToFailure(retryOutcome, retry, context);
    }

    private async Task<CloudflareRequestResult> SendDirectAsync(
        CloudflareRequestContext context,
        HttpMethod method,
        HttpContent? content,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("CloudflareResilience");
        using var request = new HttpRequestMessage(method, context.Url);

        foreach (var header in context.Headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (context.Cookies?.Count > 0)
            request.Headers.TryAddWithoutValidation("Cookie", FormatCookies(context.Cookies));

        if (content is not null)
            request.Content = content;

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return new CloudflareRequestResult(CloudflareOutcome.Success, response, null, context.FlareSolverrSessionId, context.Cookies ?? new Dictionary<string, string>());
    }

    private async Task<FlareSolverrSolution?> SolveChallengeAsync(Uri url, string? existingSessionId, CloudflareSitePolicy policy, CancellationToken ct)
    {
        await _solveLock.WaitAsync(ct);
        try
        {
            var sessionId = existingSessionId;
            if (string.IsNullOrWhiteSpace(sessionId))
                sessionId = await _resolver.CreateSessionAsync(ct);

            var solution = await _resolver.SolveAsync(url, sessionId, _options.FlareSolverrTimeoutSeconds, ct);

            if (solution is null)
            {
                await _resolver.DestroySessionAsync(sessionId, ct);
                _sessions.Save(policy.SessionKey, new CloudflareSessionState(string.Empty, new Dictionary<string, string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(policy.CooldownSeconds)));
                return null;
            }

            return solution;
        }
        finally
        {
            _solveLock.Release();
        }
    }

    private static CloudflareRequestResult ToFailure(CloudflareOutcome outcome, CloudflareRequestResult result, CloudflareRequestContext context)
    {
        var message = outcome switch
        {
            CloudflareOutcome.CloudflareChallenge => $"Cloudflare challenge detected for {context.Url}. Enable FlareSolverr for this site or provide a valid browser Cookie header.",
            CloudflareOutcome.RateLimited => $"The site rate-limited or temporarily blocked the request to {context.Url}.",
            CloudflareOutcome.AuthRequired => $"The site requires authentication for {context.Url}. Configure site credentials or a Cookie header.",
            CloudflareOutcome.AgeGateRequired => $"The site returned an age-verification or login wall for {context.Url}.",
            _ => $"The request to {context.Url} failed."
        };

        return CloudflareRequestResult.Failure(outcome, message, result.FlareSolverrSessionId, result.Cookies);
    }

    private static void EnsureAllowedHost(Uri url, CloudflareSitePolicy policy)
    {
        var host = url.Host;
        var allowed = policy.AllowedHosts.Any(candidate =>
            candidate.Equals(host, StringComparison.OrdinalIgnoreCase)
            || (candidate.StartsWith("*.", StringComparison.OrdinalIgnoreCase) && host.EndsWith(candidate[1..], StringComparison.OrdinalIgnoreCase)));

        if (!allowed)
            throw new InvalidOperationException($"Cloudflare resolver is not permitted for host '{host}'.");
    }

    private static IReadOnlyDictionary<string, string> MergeCookies(
        IReadOnlyDictionary<string, string>? requestCookies,
        IReadOnlyDictionary<string, string>? cachedCookies,
        IReadOnlyDictionary<string, string> solvedCookies)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in requestCookies ?? new Dictionary<string, string>())
            merged[pair.Key] = pair.Value;

        foreach (var pair in cachedCookies ?? new Dictionary<string, string>())
            merged[pair.Key] = pair.Value;

        foreach (var pair in solvedCookies)
            merged[pair.Key] = pair.Value;

        return merged;
    }

    private static string FormatCookies(IReadOnlyDictionary<string, string> cookies)
        => string.Join("; ", cookies.Select(pair => $"{pair.Key}={pair.Value}"));

    private static async Task<string> SafeReadHtmlAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return string.Empty;
        }
    }
}
