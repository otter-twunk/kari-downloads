using KariDownloads.Shared;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KariDownloads.Tests;

public sealed class FfmpegLocatorTests : IDisposable
{
    private const string ExtensionId = "karinauts.downloaders.boyfriendtv";
    private static readonly string ExeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ffmpeg-locator-" + Guid.NewGuid().ToString("N"));

    public FfmpegLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Resolve_UsesCoveConfigFfmpegPath_FromCoveHome()
    {
        var custom = Touch("tools", ExeName);
        var instance = Path.Combine(_root, "instance");
        Directory.CreateDirectory(instance);
        File.WriteAllText(Path.Combine(instance, "cove-config.json"),
            $$"""{"port":5073,"ffmpegPath":{{System.Text.Json.JsonSerializer.Serialize(custom)}}}""");

        Assert.Equal(custom, FfmpegLocator.Resolve(null, ExtensionId, instance, assemblyLocation: null));
    }

    [Fact]
    public void Resolve_FallsBackToBundledFfmpegUnderCoveHome()
    {
        var bundled = Touch("instance", "ffmpeg", ExeName);
        Assert.Equal(bundled, FfmpegLocator.Resolve(null, ExtensionId, Path.Combine(_root, "instance"), assemblyLocation: null));
    }

    [Fact]
    public void Resolve_FindsInstanceByWalkingUpFromExtensionLoadCache()
    {
        // No COVE_HOME: extension assemblies load from <instance>\extensions\.load-cache\<id>\<hash>\.
        var bundled = Touch("instance", "ffmpeg", ExeName);
        var dll = Touch("instance", "extensions", ".load-cache", ExtensionId, "abc123", "BoyfriendtvDownloader.dll");

        Assert.Equal(bundled, FfmpegLocator.Resolve(null, ExtensionId, coveHome: null, dll));
    }

    [Fact]
    public void Resolve_ExtensionSettingWins_AndMissingConfiguredPathIsIgnored()
    {
        var bundled = Touch("instance", "ffmpeg", ExeName);
        var custom = Touch("custom", ExeName);
        var instance = Path.Combine(_root, "instance");

        var withSetting = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ffmpegPath"] = custom }).Build();
        Assert.Equal(custom, FfmpegLocator.Resolve(withSetting, ExtensionId, instance, null));

        var stale = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ffmpegPath"] = Path.Combine(_root, "missing", ExeName) }).Build();
        Assert.Equal(bundled, FfmpegLocator.Resolve(stale, ExtensionId, instance, null));
    }
}
