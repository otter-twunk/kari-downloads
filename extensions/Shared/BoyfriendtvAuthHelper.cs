using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Cove.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly ConcurrentDictionary<string, FailedLogin> _failedCredentials = new(StringComparer.Ordinal);
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
        targetUrl = CanonicalizePageUrl(targetUrl);
        var origin = GetOrigin(targetUrl);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = origin + "/"
        };

        var cookie = await ResolveCookieAsync(client, targetUrl, origin, services, configuration, extensionId, extensionName, ct);
        headers["Cookie"] = EnsureAgeVerifiedCookie(cookie);
        return headers;
    }

    /// <summary>
    /// Rewrites legacy <c>boyfriendtv.com</c> page URLs to <c>www.boyfriend.tv</c>.
    /// The legacy domain sits behind a Cloudflare bot challenge (HTTP 403 for non-browser
    /// clients), while the same <c>/videos/&lt;id&gt;/&lt;slug&gt;/</c> path on boyfriend.tv is served directly.
    /// </summary>
    public static string CanonicalizePageUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var host = uri.Host;
        if (!host.Equals("boyfriendtv.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("www.boyfriendtv.com", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Host = "www.boyfriend.tv", Port = -1 };
        return builder.Uri.AbsoluteUri;
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
                "Check the BoyfriendTV login on the Cove Downloads page. " + CookieInstructions);
        }

        if (IsAgeVerificationOrLoginWall(html))
        {
            throw new InvalidOperationException(
                $"BoyfriendTV returned an age-verification or login wall for {url}. " +
                "Videos are only served to signed-in, age-verified accounts. " + CookieInstructions);
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
            || html.Contains("must be logged in to watch", StringComparison.OrdinalIgnoreCase)
            || html.Contains("rta-section", StringComparison.OrdinalIgnoreCase)
            || html.Contains("rta_terms_accepted", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<HttpResponseMessage> SendWithRateLimitRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct,
        int maxRetries = 3)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            var response = await client.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= maxRetries)
                return response;

            var delay = response.Headers.RetryAfter?.Delta is { } retryAfter && retryAfter > TimeSpan.Zero
                ? retryAfter
                : TimeSpan.FromSeconds(Math.Min(8, 2 * (attempt + 1)));

            response.Dispose();
            await Task.Delay(delay, ct);
        }
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

        // 2. Live Cove Downloader Credentials (Downloads page > site login entry)
        var login = FindSiteLogin(services?.GetService<IDownloaderSiteLoginProvider>(), targetUrl);
        if (login == null)
            return null;

        // A browser Cookie header pasted into the Password field is used as-is. This is the
        // reliable path: BoyfriendTV's /login/ page sits behind a Cloudflare bot challenge.
        if (TryParseCookieHeader(login.Password, out var pastedCookie))
            return pastedCookie;

        if (string.IsNullOrWhiteSpace(login.Password))
            return null;

        var credKey = $"{origin}|{login.Username}|{login.SavedAt?.Ticks ?? 0}|{login.Password.GetHashCode()}";
        if (_sessions.TryGetValue(credKey, out var cached) && cached.Expiry > DateTime.UtcNow)
            return cached.CookieHeader;

        if (_failedCredentials.TryGetValue(credKey, out var failure) && failure.RetryAfter > DateTime.UtcNow)
            throw new InvalidOperationException(failure.Message);

        await _loginLock.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(credKey, out cached) && cached.Expiry > DateTime.UtcNow)
                return cached.CookieHeader;

            if (_failedCredentials.TryGetValue(credKey, out failure) && failure.RetryAfter > DateTime.UtcNow)
                throw new InvalidOperationException(failure.Message);

            var sessionCookie = await PerformLoginAsync(client, origin, login, ct);
            _sessions[credKey] = new CachedSession(sessionCookie, DateTime.UtcNow.AddMinutes(30));
            _failedCredentials.TryRemove(credKey, out _);
            return sessionCookie;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _failedCredentials[credKey] = new FailedLogin(DateTime.UtcNow.AddMinutes(5), ex.Message);
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
            ["age_verified"] = "1",
            ["rta_terms_accepted"] = "1"
        };

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, loginPageUrl);
        getRequest.Headers.UserAgent.TryParseAdd(DownloaderUtilities.DefaultUserAgent);
        getRequest.Headers.AcceptLanguage.TryParseAdd("en-US,en;q=0.9");
        getRequest.Headers.Referrer = new Uri(origin + "/");
        getRequest.Headers.TryAddWithoutValidation("Cookie", FormatCookieHeader(cookies));

        using var getResponse = await client.SendAsync(getRequest, ct);
        var loginHtml = await getResponse.Content.ReadAsStringAsync(ct);
        CollectResponseCookies(getResponse, cookies);

        if (IsCloudflareChallenge(getResponse.StatusCode, loginHtml) || IsCloudflareMitigated(getResponse))
        {
            throw new InvalidOperationException(
                "BoyfriendTV's /login/ page is protected by a Cloudflare bot challenge, so Cove cannot sign in with a " +
                "username and password. " + CookieInstructions);
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

        if (IsCloudflareChallenge(postResponse.StatusCode, postHtml) || IsCloudflareMitigated(postResponse))
        {
            throw new InvalidOperationException(
                "BoyfriendTV login submission was blocked by a Cloudflare bot challenge. " + CookieInstructions);
        }

        // Verify the POST itself issued a session/auth cookie (the seeded consent cookies don't count).
        var hasSessionCookie = postResponse.Headers.TryGetValues("Set-Cookie", out var postSetCookies)
            && postSetCookies
                .Select(h => h.Split(';', 2)[0].Split('=', 2)[0].Trim())
                .Any(name => !NonSessionCookieNames.Contains(name));

        if (!hasSessionCookie || postHtml.Contains("Invalid username or password", StringComparison.OrdinalIgnoreCase)
            || postHtml.Contains("incorrect username or password", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "BoyfriendTV login failed. Verify the username and password of the BoyfriendTV login on the Cove Downloads page, " +
                "or paste a browser Cookie header into its Password field instead.");
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
            return "age_verified=1; rta_terms_accepted=1";

        var result = cookie.Trim().TrimEnd(';');
        if (!result.Contains("age_verified=", StringComparison.OrdinalIgnoreCase))
            result += "; age_verified=1";

        if (!result.Contains("rta_terms_accepted=", StringComparison.OrdinalIgnoreCase))
            result += "; rta_terms_accepted=1";

        return result;
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

    /// <summary>
    /// Looks up the Downloads-page site login for a BoyfriendTV URL. Users commonly save the entry
    /// under either domain (e.g. <c>https://www.boyfriendtv.com/</c>) while page URLs are
    /// canonicalized to <c>www.boyfriend.tv</c>, so every host alias is tried.
    /// </summary>
    internal static DownloaderSiteLogin? FindSiteLogin(IDownloaderSiteLoginProvider? provider, string targetUrl)
    {
        if (provider == null)
            return null;

        var direct = provider.FindForUrl(targetUrl);
        if (direct != null)
            return direct;

        var pathAndQuery = Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) ? uri.PathAndQuery : "/";
        foreach (var host in LoginHostAliases)
        {
            var login = provider.FindForUrl($"https://{host}{pathAndQuery}")
                ?? provider.FindForUrl($"https://{host}/");
            if (login != null)
                return login;
        }

        return null;
    }

    /// <summary>
    /// Detects a browser Cookie header pasted into the login entry's Password field,
    /// e.g. <c>PHPSESSID=abc123</c> or <c>Cookie: PHPSESSID=abc; kt_member=xyz</c>.
    /// </summary>
    internal static bool TryParseCookieHeader(string? value, out string cookieHeader)
    {
        cookieHeader = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["Cookie:".Length..].Trim();

        if (!trimmed.Contains('=') || !CookiePairsRegex.IsMatch(trimmed))
            return false;

        cookieHeader = trimmed.TrimEnd(';', ' ');
        return true;
    }

    private static bool IsCloudflareMitigated(HttpResponseMessage response)
        => response.Headers.TryGetValues("Cf-Mitigated", out var values)
            && values.Any(v => v.Contains("challenge", StringComparison.OrdinalIgnoreCase));

    private static readonly string[] LoginHostAliases =
        ["www.boyfriend.tv", "boyfriend.tv", "www.boyfriendtv.com", "boyfriendtv.com"];

    private static readonly HashSet<string> NonSessionCookieNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "age_verified", "rta_terms_accepted", "cookies_accepted", "traffic_type", "mobileVersionWeb",
        "__cf_bm", "cf_clearance", "_cfuvid"
    };

    // One or more name=value pairs separated by ';' with no spaces inside the first value,
    // so ordinary passwords (which rarely look like "token=value") are not mistaken for cookies.
    private static readonly Regex CookiePairsRegex = new(
        @"^[!#$%&'*+.^_`|~0-9A-Za-z-]+=[^;\s]*(\s*;\s*[!#$%&'*+.^_`|~0-9A-Za-z-]+=[^;]*)*;?$",
        RegexOptions.Compiled);

    private const string CookieInstructions =
        "Sign in to boyfriend.tv in your browser with an age-verified account, copy the request Cookie header " +
        "(browser dev tools > Network > any boyfriend.tv page request > Request Headers > Cookie), and paste it into " +
        "the Password field of the BoyfriendTV login on the Cove Downloads page, or into the extension's Cookie Header setting.";

    private sealed record CachedSession(string CookieHeader, DateTime Expiry);

    private sealed record FailedLogin(DateTime RetryAfter, string Message);
}
