using Microsoft.Extensions.Configuration;

namespace XcomDownloader;

public static class YtDlpLocator
{
    public static string Resolve(IConfiguration? configuration, string? coveHome, string? assemblyLocation)
    {
        const string id = "karinauts.downloaders.xcom";
        foreach (var key in new[] { "ytDlpPath", $"{id}:ytDlpPath", $"Extensions:{id}:ytDlpPath" })
            if (!string.IsNullOrWhiteSpace(configuration?[key])) return configuration[key]!.Trim();

        var executable = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";
        var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(coveHome)) instances.Add(Path.GetFullPath(coveHome));
        // Native Cove can shadow-load an extension several directories below <instance>/extensions.
        var directory = string.IsNullOrWhiteSpace(assemblyLocation) ? null : Path.GetDirectoryName(assemblyLocation);
        for (var depth = 0; directory != null && depth < 10; depth++, directory = Path.GetDirectoryName(directory))
            if (File.Exists(Path.Combine(directory, "cove-config.json"))) instances.Add(directory);
        foreach (var instance in instances)
        {
            string[] candidates = [Path.Combine(instance, "tools", executable),
                Path.Combine(instance, "yt-dlp", executable),
                Path.Combine(Path.GetDirectoryName(instance) ?? instance, "tools", executable)];
            foreach (var candidate in candidates)
                if (File.Exists(candidate)) return candidate;
        }
        return executable;
    }
}
