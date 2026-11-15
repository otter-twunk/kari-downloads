using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace KariDownloads.Shared;

/// <summary>
/// Finds an FFmpeg binary for extensions that remux HLS. Cove bundles FFmpeg under its instance
/// directory (<c>%COVE_HOME%\ffmpeg\ffmpeg.exe</c>) and records it as <c>ffmpegPath</c> in
/// <c>cove-config.json</c>, but neither is exposed through the extension's <see cref="IConfiguration"/>.
/// </summary>
internal static class FfmpegLocator
{
    private static readonly string ExeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    public static string Resolve(IConfiguration? configuration, string extensionId)
        => Resolve(configuration, extensionId, Environment.GetEnvironmentVariable("COVE_HOME"), typeof(FfmpegLocator).Assembly.Location);

    internal static string Resolve(IConfiguration? configuration, string extensionId, string? coveHome, string? assemblyLocation)
    {
        // 1. Explicit extension setting (Cove exposes extension settings as plain keys).
        foreach (var configured in new[]
                 {
                     configuration?["ffmpegPath"],
                     configuration?[$"{extensionId}:ffmpegPath"],
                     configuration?[$"Extensions:{extensionId}:ffmpegPath"]
                 })
        {
            if (Usable(configured))
                return configured!.Trim();
        }

        // 2. Cove instance directory: its cove-config.json ffmpegPath, then the bundled ffmpeg folder.
        foreach (var instanceDir in CandidateInstanceDirectories(coveHome, assemblyLocation))
        {
            var fromConfig = ReadCoveConfigFfmpegPath(Path.Combine(instanceDir, "cove-config.json"));
            if (fromConfig != null && File.Exists(fromConfig))
                return fromConfig;

            var bundled = Path.Combine(instanceDir, "ffmpeg", ExeName);
            if (File.Exists(bundled))
                return bundled;
        }

        // 3. PATH.
        var onPath = FindOnPath();
        if (onPath != null)
            return onPath;

        // 4. Common install locations.
        string[] wellKnown = OperatingSystem.IsWindows()
            ? [@"C:\ProgramData\chocolatey\bin\ffmpeg.exe", @"C:\ffmpeg\bin\ffmpeg.exe"]
            : ["/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg"];
        foreach (var candidate in wellKnown)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return ExeName;
    }

    private static bool Usable(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return false;
        var trimmed = configured.Trim();
        // A bare command name ("ffmpeg") is resolved via PATH by the OS; a path must exist.
        return File.Exists(trimmed)
            || (trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 && FindOnPath(trimmed) != null);
    }

    private static IEnumerable<string> CandidateInstanceDirectories(string? coveHome, string? assemblyLocation)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(coveHome) && Directory.Exists(coveHome) && seen.Add(coveHome))
            yield return coveHome;

        // Extensions load from <instance>\extensions\<id>\ or <instance>\extensions\.load-cache\<id>\...
        foreach (var start in new[] { assemblyLocation, AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(start))
                continue;
            var dir = File.Exists(start) ? Path.GetDirectoryName(start) : start;
            for (var depth = 0; dir != null && depth < 6; depth++, dir = Path.GetDirectoryName(dir))
            {
                if ((File.Exists(Path.Combine(dir, "cove-config.json")) || Directory.Exists(Path.Combine(dir, "ffmpeg")))
                    && seen.Add(dir))
                {
                    yield return dir;
                }
            }
        }
    }

    private static string? ReadCoveConfigFfmpegPath(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
                return null;
            using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("ffmpegPath", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? FindOnPath(string? name = null)
    {
        name ??= ExeName;
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        var names = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { name + ".exe", name }
            : new[] { name };

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var n in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), n);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry.
                }
            }
        }
        return null;
    }
}
