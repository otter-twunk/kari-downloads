using System;
using System.Net;
using System.Net.Http;

namespace KariDownloads.Shared.Cloudflare;

public static class CloudflareChallengeClassifier
{
    public static CloudflareOutcome Classify(HttpResponseMessage response, string html)
    {
        ArgumentNullException.ThrowIfNull(response);

        var status = response.StatusCode;

        if (IsCloudflareChallenge(status, html))
            return CloudflareOutcome.CloudflareChallenge;

        if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            return CloudflareOutcome.RateLimited;

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return CloudflareOutcome.AuthRequired;

        if (status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.GatewayTimeout)
            return CloudflareOutcome.TransientFailure;

        return response.IsSuccessStatusCode
            ? CloudflareOutcome.Success
            : CloudflareOutcome.TransientFailure;
    }

    public static bool IsCloudflareChallenge(HttpStatusCode statusCode, string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return statusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable;

        return html.Contains("cf_chl_opt", StringComparison.OrdinalIgnoreCase)
            || html.Contains("/cdn-cgi/challenge-platform/", StringComparison.OrdinalIgnoreCase)
            || (html.Contains("<title>Just a moment...</title>", StringComparison.OrdinalIgnoreCase)
                && html.Contains("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase));
    }
}
