using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace KariDownloads.Shared.Cloudflare;

public sealed record CloudflareSessionState(
    string SessionId,
    IReadOnlyDictionary<string, string> Cookies,
    DateTimeOffset LastUsed,
    DateTimeOffset? CooldownUntil);

public sealed class CloudflareSessionStore
{
    private readonly ConcurrentDictionary<string, CloudflareSessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public CloudflareSessionState? Get(string siteKey)
        => _sessions.TryGetValue(siteKey, out var state) ? state : null;

    public void Save(string siteKey, CloudflareSessionState state)
        => _sessions[siteKey] = state;

    public void Clear(string siteKey)
        => _sessions.TryRemove(siteKey, out _);

    public IReadOnlyCollection<string> ActiveSessionKeys => _sessions.Keys.ToArray();
}
