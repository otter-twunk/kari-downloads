using System;
using System.Collections.Generic;

namespace KariDownloads.Shared.Cloudflare;

public sealed class CloudflareResilienceOptions
{
    public bool Enabled { get; set; }

    public string FlareSolverrBaseUrl { get; set; } = "http://flaresolverr:8191/v1";

    public int FlareSolverrTimeoutSeconds { get; set; } = 60;

    public int MaxConcurrentSolves { get; set; } = 1;

    public int SessionIdleMinutes { get; set; } = 30;

    public int ChallengeCooldownSeconds { get; set; } = 300;

    public int MaxSolvesPerHour { get; set; } = 10;

    public int MaxDirectRetries { get; set; } = 1;
}

public sealed class CloudflareSitePolicy
{
    public bool ResolverEnabled { get; set; }

    public string SessionKey { get; set; } = string.Empty;

    public IReadOnlyList<string> AllowedHosts { get; set; } = Array.Empty<string>();

    public int MaxSolvesPerHour { get; set; } = 10;

    public int CooldownSeconds { get; set; } = 300;
}
