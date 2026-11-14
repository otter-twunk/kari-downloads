using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Plugins;

namespace BatchScraper;

public sealed record BatchScraperBlendConfigDto
{
    public string SelectedScraperId { get; init; } = "__auto__";
    public string? FallbackScraperId { get; init; } = "";
    public string PerformersMode { get; init; } = "merge";
    public bool CreateMissingPerformers { get; init; } = true;
    public bool HydratePerformers { get; init; } = true;
    public string TagsMode { get; init; } = "merge";
    public bool CreateMissingTags { get; init; } = true;
    public string TitleStrategy { get; init; } = "if_empty";
    public string StudioStrategy { get; init; } = "if_empty";
    public bool CreateMissingStudio { get; init; } = true;
    public string DateStrategy { get; init; } = "if_empty";
    public string DetailsStrategy { get; init; } = "if_empty";
    public string CodeStrategy { get; init; } = "if_empty";
    public string DirectorStrategy { get; init; } = "if_empty";
    public string CoverStrategy { get; init; } = "if_missing";
    public string UrlsMode { get; init; } = "merge";
    public bool MarkOrganized { get; init; }
    public int Concurrency { get; init; } = 2;
}

public sealed record BatchScraperQueueItemDto
{
    public int VideoId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Filename { get; init; } = string.Empty;
    public string? ScraperName { get; init; }
    public string Status { get; init; } = "pending";
    public string? StatusMessage { get; init; }
    public string? MatchedTitle { get; init; }
    public List<string>? AppliedSummary { get; init; }
}

public sealed record BatchScraperQueueStateDto
{
    public string QueueState { get; init; } = "idle"; // "idle" | "running" | "paused"
    public bool FinishAfterCurrent { get; init; }
    public BatchScraperBlendConfigDto? Config { get; init; }
    public List<BatchScraperQueueItemDto> Items { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record BatchScraperAddRequest(List<int>? VideoIds);

public sealed record BatchScraperFinishAfterCurrentRequest(bool? Enabled);

public sealed class BatchScraperQueueService
{
    private const string StoreKey = "queue.v1";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private IExtensionStore? _store;
    private BatchScraperQueueStateDto? _memoryFallback;

    public void AttachStore(IExtensionStore store) => _store = store;

    public async Task<BatchScraperQueueStateDto> GetQueueAsync(
        bool recoverInFlight = false,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var state = await LoadUnlockedAsync(ct);
            if (!recoverInFlight)
                return state;

            var hadInFlight = false;
            var recoveredItems = state.Items.Select(item =>
            {
                if (IsInFlightStatus(item.Status))
                {
                    hadInFlight = true;
                    return item with
                    {
                        Status = "pending",
                        StatusMessage = "Paused — ready to resume",
                    };
                }
                return item;
            }).ToList();

            if (!hadInFlight && state.QueueState != "running")
                return state;

            var hasPending = recoveredItems.Any(i => string.Equals(i.Status, "pending", StringComparison.OrdinalIgnoreCase));
            var recovered = state with
            {
                QueueState = hasPending ? "paused" : "idle",
                FinishAfterCurrent = false,
                Items = recoveredItems,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await SaveUnlockedAsync(recovered, ct);
            return recovered;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<BatchScraperQueueStateDto> SaveQueueAsync(
        BatchScraperQueueStateDto incoming,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var normalized = incoming with
            {
                QueueState = NormalizeQueueState(incoming.QueueState),
                Items = incoming.Items ?? [],
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await SaveUnlockedAsync(normalized, ct);
            return normalized;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(BatchScraperQueueStateDto State, int AddedCount)> AddVideosAsync(
        IEnumerable<int>? videoIds,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var state = await LoadUnlockedAsync(ct);
            var items = new List<BatchScraperQueueItemDto>(state.Items);
            var existingById = items
                .Select((item, index) => (item, index))
                .GroupBy(x => x.item.VideoId)
                .ToDictionary(g => g.Key, g => g.First());

            var addedCount = 0;
            if (videoIds != null)
            {
                foreach (var id in videoIds.Where(id => id > 0).Distinct())
                {
                    if (existingById.TryGetValue(id, out var existing))
                    {
                        var status = existing.item.Status.ToLowerInvariant();
                        if (status is "error" or "no_match" or "skipped")
                        {
                            items[existing.index] = existing.item with
                            {
                                Status = "pending",
                                StatusMessage = "Re-queued",
                            };
                            addedCount++;
                        }
                    }
                    else
                    {
                        items.Add(new BatchScraperQueueItemDto
                        {
                            VideoId = id,
                            Title = $"Video #{id}",
                            Filename = string.Empty,
                            Status = "pending",
                        });
                        addedCount++;
                    }
                }
            }

            var updated = state with
            {
                Items = items,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await SaveUnlockedAsync(updated, ct);
            return (updated, addedCount);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<BatchScraperQueueStateDto> SkipItemAsync(int videoId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var state = await LoadUnlockedAsync(ct);
            var items = state.Items.Select(item =>
                item.VideoId == videoId && (string.Equals(item.Status, "pending", StringComparison.OrdinalIgnoreCase) || IsInFlightStatus(item.Status))
                    ? item with { Status = "skipped", StatusMessage = "Skipped by user" }
                    : item
            ).ToList();

            var updated = state with
            {
                Items = items,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await SaveUnlockedAsync(updated, ct);
            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<BatchScraperQueueStateDto> PauseQueueAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var state = await LoadUnlockedAsync(ct);
            var items = state.Items.Select(item =>
                IsInFlightStatus(item.Status)
                    ? item with { Status = "pending", StatusMessage = "Paused — ready to resume" }
                    : item
            ).ToList();

            var updated = state with
            {
                QueueState = "paused",
                FinishAfterCurrent = false,
                Items = items,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await SaveUnlockedAsync(updated, ct);
            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<BatchScraperQueueStateDto> SetFinishAfterCurrentAsync(bool? enabled, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var state = await LoadUnlockedAsync(ct);
            var nextValue = enabled ?? !state.FinishAfterCurrent;
            var updated = state with
            {
                FinishAfterCurrent = nextValue,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await SaveUnlockedAsync(updated, ct);
            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<BatchScraperQueueStateDto> ClearQueueAsync(bool onlyFinished, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var state = await LoadUnlockedAsync(ct);
            if (!onlyFinished)
            {
                var empty = new BatchScraperQueueStateDto
                {
                    QueueState = "idle",
                    FinishAfterCurrent = false,
                    Config = state.Config,
                    Items = [],
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                await SaveUnlockedAsync(empty, ct);
                return empty;
            }

            var remaining = state.Items
                .Where(item => string.Equals(item.Status, "pending", StringComparison.OrdinalIgnoreCase) || IsInFlightStatus(item.Status))
                .ToList();

            var updated = state with
            {
                QueueState = remaining.Count == 0 ? "idle" : state.QueueState,
                FinishAfterCurrent = remaining.Count == 0 ? false : state.FinishAfterCurrent,
                Items = remaining,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await SaveUnlockedAsync(updated, ct);
            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool IsInFlightStatus(string? status) =>
        status is not null &&
        (string.Equals(status, "fetching", StringComparison.OrdinalIgnoreCase)
         || string.Equals(status, "scraping", StringComparison.OrdinalIgnoreCase)
         || string.Equals(status, "applying", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeQueueState(string? queueState) =>
        queueState?.ToLowerInvariant() switch
        {
            "running" => "running",
            "paused" => "paused",
            _ => "idle",
        };

    private async Task<BatchScraperQueueStateDto> LoadUnlockedAsync(CancellationToken ct)
    {
        if (_store != null)
        {
            var raw = await _store.GetAsync(StoreKey, ct);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<BatchScraperQueueStateDto>(raw, JsonOpts);
                    if (parsed != null)
                        return parsed;
                }
                catch
                {
                    // Fall through to default state on corrupt JSON
                }
            }
        }

        if (_memoryFallback != null)
            return _memoryFallback;

        return new BatchScraperQueueStateDto();
    }

    private async Task SaveUnlockedAsync(BatchScraperQueueStateDto state, CancellationToken ct)
    {
        _memoryFallback = state;
        if (_store != null)
        {
            var json = JsonSerializer.Serialize(state, JsonOpts);
            await _store.SetAsync(StoreKey, json, ct);
        }
    }
}
