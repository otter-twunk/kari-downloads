using System;
using System.Collections.Generic;
using System.Net.Http;

namespace KariDownloads.Shared.Cloudflare;

public enum CloudflareOutcome
{
    Success,
    CloudflareChallenge,
    ChallengeSolved,
    ChallengeUnresolved,
    RateLimited,
    AuthRequired,
    AgeGateRequired,
    TransientFailure
}

public sealed record CloudflareRequestContext(
    Uri Url,
    string ExtensionId,
    string SiteKey,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyDictionary<string, string>? Cookies = null,
    string? FlareSolverrSessionId = null);

public sealed record CloudflareRequestResult(
    CloudflareOutcome Outcome,
    HttpResponseMessage? Response,
    string? Message,
    string? FlareSolverrSessionId,
    IReadOnlyDictionary<string, string> Cookies)
{
    public static CloudflareRequestResult Success(
        HttpResponseMessage response,
        IReadOnlyDictionary<string, string> cookies,
        string? sessionId) => new(
            CloudflareOutcome.Success,
            response,
            null,
            sessionId,
            cookies);

    public static CloudflareRequestResult Failure(
        CloudflareOutcome outcome,
        string message,
        string? sessionId = null,
        IReadOnlyDictionary<string, string>? cookies = null) => new(
            outcome,
            null,
            message,
            sessionId,
            cookies ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}
