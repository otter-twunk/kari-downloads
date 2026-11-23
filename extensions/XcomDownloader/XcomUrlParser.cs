using System.Text.RegularExpressions;

namespace XcomDownloader;

public sealed record XStatusRef(string StatusId, string CanonicalUrl, string? MediaId);

public static class XcomUrlParser
{
    private static readonly Regex StatusPath = new(@"^/(?:[A-Za-z0-9_]{1,50}|i/web)/status/(?<id>[0-9]{1,25})(?<selection>/(?:video|photo)/[1-9][0-9]?)?/?$", RegexOptions.CultureInvariant);

    public static bool TryParse(string? url, out XStatusRef status)
    {
        status = null!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            return false;
        var host = uri.Host.ToLowerInvariant();
        if (host is not ("x.com" or "www.x.com" or "m.x.com" or "mobile.x.com"
            or "twitter.com" or "www.twitter.com" or "m.twitter.com" or "mobile.twitter.com"))
            return false;
        var match = StatusPath.Match(uri.AbsolutePath);
        if (!match.Success) return false;
        var media = Regex.Match(uri.Fragment, @"^#cove-media=([0-9]{1,25})$");
        // Tracking queries and ordinary fragments are discarded, never forwarded to the process.
        status = new(match.Groups["id"].Value,
            "https://x.com" + uri.AbsolutePath.TrimEnd('/'), media.Success ? media.Groups[1].Value : null);
        return true;
    }
}
