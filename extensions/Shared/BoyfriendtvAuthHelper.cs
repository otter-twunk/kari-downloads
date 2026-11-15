using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Cove.Plugins;
using Microsoft.Extensions.Configuration;

namespace KariDownloads.Shared;

/// <summary>
/// Shared authentication, session caching, and age-gate / Cloudflare diagnostics
/// for BoyfriendTV extensions (<c>boyfriend.tv</c> and <c>boyfriendtv.com</c>).
/// </summary>
internal sealed class BoyfriendtvAuthHelper
{
    public static readonly string[] SupportedDomains = ["boyfriend.tv", "boyfriendtv.com"];

    private static readonly Regex FormRegex = new(
        @"(?is)<form\b(?<attrs>[^>]*)>(?<body>.*?)</form>",
        RegexOptions.Compiled);

    private static readonly Regex InputRegex = new(
        @"(?is)<input\b(?<attrs>[^>]*)>",
        RegexOptions.Compiled);

    private static readonly Regex AttrRegex = new(
        @"([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:(['""])(.*?)\2|([^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, CachedSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _failedCredentials = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    public async Task<Dictionary<string, string>> GetSessionHeadersAsync(
        HttpClient client,
        string targetUrl,
        IServiceProvider? services,
        IConfiguration? configuration,
        string extensionId,
        string extensionName,
        CancellationToken ct)
    {
        var origin = GetOrigin(targetUrl);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = origin + "/"
        };

        var cookie = await ResolveCookieAsync(client, targetUrl, origin, services, configuration, extensionId, extensionName, ct);
        headers["Cookie"] = EnsureAgeVerifiedCookie(cookie);
        return headers;
    }

    public static string GetOrigin(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            if (host.Equals("boyfriend.tv", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".boyfriend.tv", StringComparison.OrdinalIgnoreCase)
                || host.Equals("boyfriendtv.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".boyfriendtv.com", StringComparison.OrdinalIgnoreCase))
            {
                return $"{uri.Scheme}://{uri.Authority}";
            }
        }

        return "https://www.boyfriend.tv";
    }

    public static void ThrowIfBlockedOrAgeGated(HttpStatusCode statusCode, string html, string url)
    {
        if (IsCloudflareChallenge(statusCode, html))
        {
            throw new InvalidOperationException(
                $"BoyfriendTV returned a Cloudflare challenge for {url} (HTTP {(int)statusCode}). " +
                "If this persists, sign in via your browser and paste your Cookie header into the extension's Cookie Header setting.");
        }

        if (statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"BoyfriendTV denied access to {url} (HTTP {(int)statusCode}). " +
                "Verify your BoyfriendTV credentials in Cove Settings > Downloader Credentials or configure a Cookie Header.");
        }

        if (IsAgeVerificationOrLoginWall(html))
        {
            throw new InvalidOperationException(
                $"BoyfriendTV returned an age-verification or login wall for {url}. " +
                "Ensure valid BoyfriendTV login credentials are saved in Cove Settings > Downloader Credentials, " +
                "or set a Cookie Header in extension settings.");
        }
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

    public static bool IsAgeVerificationOrLoginWall(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return false;

        // If the page already has a video player or structured VideoObject/ProfilePage, it's not a wall.
        if (html.Contains("playerConfig", StringComparison.OrdinalIgnoreCase)
            || html.Contains("mediaDefinitions", StringComparison.OrdinalIgnoreCase)
            || html.Contains("\"@type\":\"VideoObject\"", StringComparison.OrdinalIgnoreCase)
            || html.Contains("\"@type\": \"VideoObject\"", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return html.Contains("Age verification required", StringComparison.OrdinalIgnoreCase)
            || html.Contains("verify your age", StringComparison.OrdinalIgnoreCase)
            || html.Contains("/age-verification", StringComparison.OrdinalIgnoreCase)
            || html.Contains("age-verification-modal", StringComparison.OrdinalIgnoreCase)
            || html.Contains("agegate", StringComparison.OrdinalIgnoreCase)
            || html.Contains("must be logged in to watch", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> ResolveCookieAsync(
        HttpClient client,
        string targetUrl,
        string origin,
        IServiceProvider? services,
        IConfiguration? configuration,
        string extensionId,
        string extensionName,
        CancellationToken ct)
    {
        // 1. Explicit cookie setting in extension configuration takes precedence if set
        var configuredCookie = GetConfiguredCookie(configuration, extensionId, extensionName);
        if (!string.IsNullOrWhiteSpace(configuredCookie))
            return configuredCookie;

        // 2. Live Cove Downloader Credentials (IDownloaderSiteLoginProvider)
        var login = services?.GetService<IDownloaderSiteLoginProvider>()?.FindForUrl(targetUrl);
        if (login == null)
            return null;

        // Allow pasting a raw Cookie header in Password as an escape hatch
        if (login.Password.Contains('=') && login.Password.Contains(';'))
            return login.Password.Trim();

        var credKey = $"{origin}|{login.Username}|{login.SavedAt?.Ticks ?? 0}";
        if (_sessions.TryGetValue(credKey, out var cached) && cached.Expiry > DateTime.UtcNow)
            return cached.CookieHeader;

        if (_failedCredentials.TryGetValue(credKey, out var retryAfter) && retryAfter > DateTime.UtcNow)
            return null;

        await _loginLock.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(credKey, out cached) && cached.Expiry > DateTime.UtcNow)
                return cached.CookieHeader;

            if (_failedCredentials.TryGetValue(credKey, out retryAfter) && retryAfter > DateTime.UtcNow)
                return null;

            var sessionCookie = await PerformLoginAsync(client, origin, login, ct);
            _sessions[credKey] = new CachedSession(sessionCookie, DateTime.UtcNow.AddMinutes(30));
            _failedCredentials.TryRemove(credKey, out _);
            return sessionCookie;
        }
        catch
        {
            _failedCredentials[credKey] = DateTime.UtcNow.AddMinutes(5);
            throw;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private static async Task<string> PerformLoginAsync(
        HttpClient client,
        string origin,
        DownloaderSiteLogin login,
        CancellationToken ct)
    {
        var loginPageUrl = $"{origin}/login/";
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["age_verified"] = "1"
        };

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, loginPageUrl);
        getRequest.Headers.UserAgent.TryParseAdd(DownloaderUtilities.DefaultUserAgent);
        getRequest.Headers.AcceptLanguage.TryParseAdd("en-US,en;q=0.9");
        getRequest.Headers.Referrer = new Uri(origin + "/");
        getRequest.Headers.TryAddWithoutValidation("Cookie", FormatCookieHeader(cookies));

        using var getResponse = await client.SendAsync(getRequest, ct);
        var loginHtml = await getResponse.Content.ReadAsStringAsync(ct);
        CollectResponseCookies(getResponse, cookies);

        if (IsCloudflareChallenge(getResponse.StatusCode, loginHtml))
        {
            throw new InvalidOperationException(
                "BoyfriendTV /login/ is protected by a Cloudflare challenge and cannot be logged in automatically. " +
                "Sign in using your browser and paste the resulting Cookie header into the extension's Cookie Header setting.");
        }

        getResponse.EnsureSuccessStatusCode();

        var (actionUrl, formFields) = ExtractLoginForm(loginHtml, loginPageUrl, login.Username, login.Password);

        using var postRequest = new HttpRequestMessage(HttpMethod.Post, actionUrl)
        {
            Content = new FormUrlEncodedContent(formFields)
        };
        postRequest.Headers.UserAgent.TryParseAdd(DownloaderUtilities.DefaultUserAgent);
        postRequest.Headers.AcceptLanguage.TryParseAdd("en-US,en;q=0.9");
        postRequest.Headers.Referrer = new Uri(loginPageUrl);
        postRequest.Headers.TryAddWithoutValidation("Origin", origin);
        postRequest.Headers.TryAddWithoutValidation("Cookie", FormatCookieHeader(cookies));

        using var postResponse = await client.SendAsync(postRequest, ct);
        var postHtml = await postResponse.Content.ReadAsStringAsync(ct);
        CollectResponseCookies(postResponse, cookies);

        if (IsCloudflareChallenge(postResponse.StatusCode, postHtml))
        {
            throw new InvalidOperationException(
                "BoyfriendTV login submission was blocked by a Cloudflare challenge. " +
                "Sign in using your browser and paste the resulting Cookie header into the extension's Cookie Header setting.");
        }

        // Verify we received at least one non-trivial session/auth cookie beyond age_verified
        var hasSessionCookie = cookies.Keys.Any(k =>
            !k.Equals("age_verified", StringComparison.OrdinalIgnoreCase)
            && !k.Equals("traffic_type", StringComparison.OrdinalIgnoreCase)
            && !k.Equals("mobileVersionWeb", StringComparison.OrdinalIgnoreCase));

        if (!hasSessionCookie || postHtml.Contains("Invalid username or password", StringComparison.OrdinalIgnoreCase)
            || postHtml.Contains("incorrect username or password", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "BoyfriendTV login failed. Verify your username and password in Cove Settings > Downloader Credentials.");
        }

        return FormatCookieHeader(cookies);
    }

    internal static (string ActionUrl, Dictionary<string, string> Fields) ExtractLoginForm(
        string html,
        string loginPageUrl,
        string username,
        string password)
    {
        var baseUri = new Uri(loginPageUrl);
        string? selectedAction = null;
        string? selectedBody = null;

        foreach (Match formMatch in FormRegex.Matches(html))
        {
            var attrs = ParseAttributes(formMatch.Groups["attrs"].Value);
            var body = formMatch.Groups["body"].Value;
            var action = attrs.GetValueOrDefault("action");

            if (body.Contains("password", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(action) && action.Contains("login", StringComparison.OrdinalIgnoreCase)))
            {
                selectedAction = action;
                selectedBody = body;
                break;
            }
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? usernameField = null;
        string? passwordField = null;

        if (!string.IsNullOrEmpty(selectedBody))
        {
            foreach (Match inputMatch in InputRegex.Matches(selectedBody))
            {
                var attrs = ParseAttributes(inputMatch.Groups["attrs"].Value);
                if (!attrs.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
                    continue;

                var type = attrs.GetValueOrDefault("type")?.ToLowerInvariant() ?? "text";
                var value = attrs.GetValueOrDefault("value") ?? string.Empty;

                if (type == "hidden")
                {
                    fields[name] = value;
                }
                else if (type == "password" && passwordField == null)
                {
                    passwordField = name;
                }
                else if (type is "text" or "email" && usernameField == null)
                {
                    usernameField = name;
                }
            }
        }

        fields[usernameField ?? "username"] = username;
        fields[passwordField ?? "pass"] = password;

        var actionUrl = loginPageUrl;
        if (!string.IsNullOrWhiteSpace(selectedAction) && Uri.TryCreate(baseUri, selectedAction, out var resolved))
            actionUrl = resolved.ToString();

        return (actionUrl, fields);
    }

    private static string? GetConfiguredCookie(IConfiguration? configuration, string extensionId, string extensionName)
    {
        if (configuration == null)
            return null;

        var raw = configuration["cookie"]
            ?? configuration[$"{extensionId}:cookie"]
            ?? configuration[$"{extensionName}:cookie"];

        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static string EnsureAgeVerifiedCookie(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
            return "age_verified=1";

        if (cookie.Contains("age_verified=", StringComparison.OrdinalIgnoreCase))
            return cookie.Trim();

        return $"{cookie.Trim().TrimEnd(';')}; age_verified=1";
    }

    private static void CollectResponseCookies(HttpResponseMessage response, Dictionary<string, string> cookies)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            return;

        foreach (var header in setCookies)
        {
            var semiIdx = header.IndexOf(';');
            var pair = (semiIdx >= 0 ? header[..semiIdx] : header).Trim();
            var eqIdx = pair.IndexOf('=');
            if (eqIdx <= 0)
                continue;

            var name = pair[..eqIdx].Trim();
            var val = pair[(eqIdx + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(name))
                cookies[name] = val;
        }
    }

    private static string FormatCookieHeader(Dictionary<string, string> cookies)
        => string.Join("; ", cookies.Select(kvp => $"{kvp.Key}={kvp.Value}"));

    private static Dictionary<string, string> ParseAttributes(string rawAttributes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttrRegex.Matches(rawAttributes))
        {
            var key = match.Groups[1].Value;
            var val = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
            if (!string.IsNullOrWhiteSpace(key))
                result[key] = WebUtility.HtmlDecode(val);
        }

        return result;
    }

    private sealed record CachedSession(string CookieHeader, DateTime Expiry);
}
