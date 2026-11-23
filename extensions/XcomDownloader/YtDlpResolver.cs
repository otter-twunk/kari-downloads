using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace XcomDownloader;

public sealed record ResolverOutput(int ExitCode, string Json, string Error);

// A narrow boundary lets fixtures exercise the real JSON parser without installing yt-dlp.
public interface IXcomProcessRunner
{
    Task<ResolverOutput> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct);
}

public sealed class XcomProcessRunner : IXcomProcessRunner
{
    public async Task<ResolverOutput> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        }};
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException("External resolver is unavailable; install yt-dlp or configure ytDlpPath.");
        }
        using var registration = deadline.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        });
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, 8 * 1024 * 1024, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError, 64 * 1024, deadline.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("X media resolution timed out; retry later or update yt-dlp.");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
            if (result.Length < limit) result.Append(buffer, 0, Math.Min(read, limit - result.Length));
        return result.ToString();
    }
}

public sealed record XcomFormat(string Id, string Url, int? Width, int? Height, double Bitrate);
public sealed record XcomMedia(string Id, string Title, string? Details, string? Date, string Uploader, IReadOnlyList<XcomFormat> Formats);

public sealed class YtDlpResolver(IXcomProcessRunner runner)
{
    public async Task<IReadOnlyList<XcomMedia>> ResolveAsync(XStatusRef status, string executable, CancellationToken ct, string? cookiesFile = null)
    {
        string? temporaryCookies = null;
        ResolverOutput output;
        try
        {
            List<string> arguments = ["--ignore-config", "--no-cookies-from-browser", "--no-warnings",
                "--skip-download", "--dump-single-json", "--socket-timeout", "20", "--retries", "1", "--use-extractors", "twitter.*"];
            if (!string.IsNullOrWhiteSpace(cookiesFile))
            {
                if (!File.Exists(cookiesFile)) throw new InvalidOperationException("The configured X cookies file is unavailable; choose an existing Netscape-format cookies export.");
                // yt-dlp updates its cookie jar on exit; use a private temporary copy to preserve the user's export.
                temporaryCookies = Path.Combine(Path.GetTempPath(), "xcom-cookies-" + Guid.NewGuid().ToString("N") + ".txt");
                File.Copy(cookiesFile, temporaryCookies);
                arguments.AddRange(["--cookies", temporaryCookies]);
            }
            else arguments.Add("--no-cookies");
            arguments.AddRange(["--", status.CanonicalUrl]);
            output = await runner.RunAsync(executable, arguments, ct);
        }
        finally
        {
            if (temporaryCookies != null) File.Delete(temporaryCookies);
        }
        if (output.ExitCode != 0)
        {
            // Never echo raw stderr: upstream messages can contain signed URLs, cookies or tokens.
            var error = output.Error.ToLowerInvariant();
            throw new InvalidOperationException(error.Contains("429") || error.Contains("rate limit")
                ? "X rate limit reached; retry later."
                : error.Contains("no video") || error.Contains("no formats")
                    ? "No downloadable video found. The post may contain only photos or require an X login; configure an authorized X cookies file for login-gated media."
                    : error.Contains("login") || error.Contains("private") || error.Contains("protected") || error.Contains("not available") || error.Contains("404")
                        ? "This post is not publicly accessible (unavailable, deleted or login required)."
                        : "yt-dlp could not resolve this status; check network access and update yt-dlp.");
        }
        try
        {
            using var document = JsonDocument.Parse(output.Json);
            var root = document.RootElement;
            var entries = root.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().ToArray() : [root];
            return entries.Where(e => e.ValueKind == JsonValueKind.Object).Select(ParseMedia)
                .Where(m => m.Formats.Count > 0).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException("yt-dlp returned incompatible media metadata; update yt-dlp.");
        }
    }

    private static XcomMedia ParseMedia(JsonElement entry)
    {
        var formats = new List<XcomFormat>();
        if (entry.TryGetProperty("formats", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var format in array.EnumerateArray())
            {
                var url = Text(format, "url");
                if (!IsMediaUrl(url, "video.twimg.com") || Text(format, "ext") != "mp4"
                    || Text(format, "protocol") is not (null or "https" or "http")
                    || Text(format, "vcodec") == "none" || Text(format, "acodec") == "none"
                    || string.IsNullOrWhiteSpace(Text(format, "format_id"))) continue;
                formats.Add(new(Text(format, "format_id")!, url!, Number(format, "width"), Number(format, "height"),
                    format.TryGetProperty("tbr", out var rate) && rate.TryGetDouble(out var bitrate) ? bitrate : 0));
            }
        var date = Text(entry, "upload_date");
        if (DateTime.TryParseExact(date, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed)) date = parsed.ToString("yyyy-MM-dd");
        else date = null;
        return new(Text(entry, "id") ?? "video", Text(entry, "title") ?? "X video", Text(entry, "description"),
            date, Text(entry, "uploader") ?? "X / Twitter", formats);
    }

    public static bool IsMediaUrl(string? url, string host) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Host == host;
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static int? Number(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.TryGetInt32(out var number) ? number : null;
}
