using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace KariDownloads.Shared.Cloudflare;

public interface IFlareSolverrGateway
{
    Task<FlareSolverrSolution?> SolveAsync(Uri url, string? sessionId, int timeoutSeconds, CancellationToken ct);
    Task<string> CreateSessionAsync(CancellationToken ct);
    Task DestroySessionAsync(string sessionId, CancellationToken ct);
}

public sealed record FlareSolverrSolution(
    string? SessionId,
    IReadOnlyDictionary<string, string> Cookies,
    string? Html);

public sealed class FlareSolverrGateway : IFlareSolverrGateway
{
    private readonly HttpClient _httpClient;

    public FlareSolverrGateway(HttpClient httpClient)
        => _httpClient = httpClient;

    public async Task<string> CreateSessionAsync(CancellationToken ct)
    {
        var response = await PostAsync(new { cmd = "sessions.create" }, ct);
        return response.Session ?? throw new InvalidOperationException("FlareSolverr did not return a session ID.");
    }

    public async Task DestroySessionAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        await PostAsync(new { cmd = "sessions.destroy", session = sessionId }, ct);
    }

    public async Task<FlareSolverrSolution?> SolveAsync(Uri url, string? sessionId, int timeoutSeconds, CancellationToken ct)
    {
        var response = await PostAsync(new
        {
            cmd = "request.get",
            url = url.ToString(),
            session = sessionId,
            maxTimeout = timeoutSeconds * 1000
        }, ct);

        if (!string.Equals(response.Status, "ok", StringComparison.OrdinalIgnoreCase) || response.Solution is null)
            return null;

        var cookies = response.Solution.Cookies?
            .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name))
            .ToDictionary(
                cookie => cookie.Name!,
                cookie => cookie.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return new FlareSolverrSolution(response.Solution.Session ?? sessionId, cookies, response.Solution.Response);
    }

    private async Task<FlareSolverrResponse> PostAsync(object payload, CancellationToken ct)
    {
        using var response = await _httpClient.PostAsJsonAsync(string.Empty, payload, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<FlareSolverrResponse>(cancellationToken: ct);
        return result ?? throw new InvalidOperationException("FlareSolverr returned an invalid response.");
    }
}

internal sealed class FlareSolverrResponse
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("session")] public string? Session { get; set; }
    [JsonPropertyName("solution")] public FlareSolverrSolutionPayload? Solution { get; set; }
}

internal sealed class FlareSolverrSolutionPayload
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("status")] public int? Status { get; set; }
    [JsonPropertyName("session")] public string? Session { get; set; }
    [JsonPropertyName("response")] public string? Response { get; set; }
    [JsonPropertyName("cookies")] public List<FlareSolverrCookie>? Cookies { get; set; }
}

internal sealed class FlareSolverrCookie
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("domain")] public string? Domain { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
}
