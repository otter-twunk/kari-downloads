using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace BatchDownloader;

public sealed record BatchDownloadQueueItemDto
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Url { get; init; } = string.Empty;
    public string RawUrl { get; init; } = string.Empty;
    public string? SourceUrl { get; init; }
    public string Label { get; init; } = string.Empty;
    public string DownloaderId { get; init; } = string.Empty;
    public string? DownloaderName { get; init; }
    public string Entity { get; init; } = "Video";
    public int? EntityId { get; init; }
    public string? QualityId { get; init; }
    public int? StudioId { get; init; }
    public int? GroupId { get; init; }
    public List<int>? TagIds { get; init; }
    public List<int>? PerformerIds { get; init; }
    public bool AutoApplyMetadata { get; init; } = true;
    public bool AllowDuplicates { get; init; }
    public string Status { get; init; } = "Pending"; // "Pending" | "Running" | "Completed" | "Skipped" | "Failed"
    public double Progress { get; init; }
    public string? StatusMessage { get; init; }
    public int? ImportedEntityId { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record BatchDownloadQueueStateDto
{
    public string RunnerStatus { get; init; } = "Idle"; // "Idle" | "Running" | "Paused"
    public bool FinishAfterCurrent { get; init; }
    public string? ActiveJobId { get; init; }
    public int TotalCount { get; init; }
    public int PendingCount { get; init; }
    public int RunningCount { get; init; }
    public int CompletedCount { get; init; }
    public int SkippedCount { get; init; }
    public int FailedCount { get; init; }
    public IReadOnlyList<BatchDownloadQueueItemDto> Items { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record BatchDownloadQueuePersistedState
{
    public string RunnerStatus { get; init; } = "Idle";
    public bool FinishAfterCurrent { get; init; }
    public List<BatchDownloadQueueItemDto> Items { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class BatchQueueItemInput
{
    public string? Url { get; set; }
    public string? RawUrl { get; set; }
    public string? SourceUrl { get; set; }
    public string? Label { get; set; }
    public string? DownloaderId { get; set; }
    public string? DownloaderName { get; set; }
    public string? Entity { get; set; }
    public int? EntityId { get; set; }
    public string? QualityId { get; set; }
}

public sealed record BatchFinishAfterCurrentRequest(bool? Enabled);

public sealed record BatchQueueEnqueueResult(
    string? JobId,
    int QueuedCount,
    int SkippedCount,
    IReadOnlyList<BatchDownloadQueueItemDto> Queued,
    IReadOnlyList<object> Skipped,
    BatchDownloadQueueStateDto Queue);

public sealed class BatchDownloadQueueService
{
    private const string StoreKey = "queue.v1";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private enum ItemCancelReason
    {
        None,
        Skip,
        Pause,
        Remove,
    }

    private sealed class ActiveItemExecution(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public ItemCancelReason Reason { get; set; } = ItemCancelReason.None;
    }

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly Dictionary<string, ActiveItemExecution> _activeExecutions = new(StringComparer.OrdinalIgnoreCase);

    private IExtensionStore? _store;
    private bool _hydrated;
    private string _runnerStatus = "Idle";
    private bool _finishAfterCurrent;
    private string? _activeJobId;
    private bool _workerLoopActive;
    private List<BatchDownloadQueueItemDto> _items = [];

    public void AttachStore(IExtensionStore store)
    {
        _store = store;
        _hydrated = false;
    }

    public async Task<BatchDownloadQueueStateDto> GetStateAsync(CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            await EnsureHydratedUnlockedAsync(ct);
            return BuildStateSnapshotUnlocked();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<BatchQueueEnqueueResult> EnqueueAsync(
        BatchQueueApiRequest request,
        IDownloaderService downloaderService,
        IExtensionServiceScopeFactory? scopeFactory,
        IJobService? jobService,
        CancellationToken ct = default)
    {
        var skippedItems = new List<object>();
        var preparedItems = new List<BatchDownloadQueueItemDto>();

        if (request.Items is { Count: > 0 })
        {
            var inputs = request.Items;
            var resolved = new BatchDownloadQueueItemDto?[inputs.Count];
            var skipByIdx = new object?[inputs.Count];
            using var sem = new SemaphoreSlim(4, 4);

            var tasks = inputs.Select(async (input, idx) =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var rawUrl = input.RawUrl ?? input.Url ?? string.Empty;
                    var cleanedUrl = BatchDownloaderExtension.ExtractUrls(null, [rawUrl]).FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(cleanedUrl))
                    {
                        skipByIdx[idx] = new { url = rawUrl, reason = "Invalid URL." };
                        return;
                    }

                    var downloaderId = input.DownloaderId;
                    var downloaderName = input.DownloaderName;
                    var effectiveUrl = string.IsNullOrWhiteSpace(input.Url) ? cleanedUrl : input.Url!;
                    var sourceUrl = input.SourceUrl;
                    var label = string.IsNullOrWhiteSpace(input.Label) ? effectiveUrl : input.Label!;
                    var entityStr = input.Entity ?? request.Entity;

                    if (string.IsNullOrWhiteSpace(downloaderId))
                    {
                        var matches = await downloaderService.MatchUrlAsync(cleanedUrl, ct);
                        var best = matches.FirstOrDefault();
                        if (best == null)
                        {
                            skipByIdx[idx] = new { url = cleanedUrl, reason = "No compatible downloader matched this URL." };
                            return;
                        }
                        downloaderId = best.DownloaderId;
                        downloaderName = best.DownloaderName;
                        effectiveUrl = string.IsNullOrWhiteSpace(best.NormalizedUrl) ? cleanedUrl : best.NormalizedUrl;
                        sourceUrl ??= best.SourceUrl;
                        if (string.IsNullOrWhiteSpace(input.Label) && !string.IsNullOrWhiteSpace(best.Label))
                            label = best.Label;
                        if (string.IsNullOrWhiteSpace(entityStr) || string.Equals(entityStr, "auto", StringComparison.OrdinalIgnoreCase))
                            entityStr = best.SupportedEntity;
                    }

                    if (!Enum.TryParse<DownloaderEntity>(entityStr, true, out var parsedEntity))
                        parsedEntity = DownloaderEntity.Video;

                    var qualityId = !string.IsNullOrWhiteSpace(input.QualityId)
                        ? input.QualityId.Trim()
                        : !string.IsNullOrWhiteSpace(request.QualityId)
                            ? request.QualityId.Trim()
                            : null;

                    resolved[idx] = new BatchDownloadQueueItemDto
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        Url = effectiveUrl,
                        RawUrl = cleanedUrl,
                        SourceUrl = sourceUrl,
                        Label = label,
                        DownloaderId = downloaderId!,
                        DownloaderName = downloaderName,
                        Entity = parsedEntity.ToString(),
                        EntityId = input.EntityId,
                        QualityId = qualityId,
                        StudioId = request.StudioId,
                        GroupId = request.GroupId,
                        TagIds = request.TagIds?.Where(id => id > 0).Distinct().ToList(),
                        PerformerIds = request.PerformerIds?.Where(id => id > 0).Distinct().ToList(),
                        AutoApplyMetadata = request.AutoApplyMetadata ?? true,
                        AllowDuplicates = request.AllowDuplicates ?? false,
                        Status = "Pending",
                        Progress = 0,
                        AddedAt = DateTimeOffset.UtcNow,
                    };
                }
                finally
                {
                    sem.Release();
                }
            });

            await Task.WhenAll(tasks);

            for (var i = 0; i < inputs.Count; i++)
            {
                if (resolved[i] != null)
                    preparedItems.Add(resolved[i]!);
                else if (skipByIdx[i] != null)
                    skippedItems.Add(skipByIdx[i]!);
            }
        }
        else
        {
            var urls = BatchDownloaderExtension.ExtractUrls(request.RawText, request.Urls);
            var matchedByIndex = new Cove.Core.DTOs.DownloaderMatchDto?[urls.Count];
            using (var matchSemaphore = new SemaphoreSlim(4, 4))
            {
                var matchTasks = urls.Select(async (url, index) =>
                {
                    await matchSemaphore.WaitAsync(ct);
                    try
                    {
                        var matches = await downloaderService.MatchUrlAsync(url, ct);
                        matchedByIndex[index] = matches.FirstOrDefault();
                    }
                    finally
                    {
                        matchSemaphore.Release();
                    }
                });
                await Task.WhenAll(matchTasks);
            }

            for (var idx = 0; idx < urls.Count; idx++)
            {
                var url = urls[idx];
                var match = matchedByIndex[idx];
                if (match == null)
                {
                    skippedItems.Add(new { url, reason = "No compatible downloader matched this URL." });
                    continue;
                }

                var entityStr = !string.IsNullOrWhiteSpace(request.Entity) && !string.Equals(request.Entity, "auto", StringComparison.OrdinalIgnoreCase)
                    ? request.Entity
                    : match.SupportedEntity;

                if (!Enum.TryParse<DownloaderEntity>(entityStr, true, out var entityType))
                    entityType = DownloaderEntity.Video;

                var effectiveUrl = string.IsNullOrWhiteSpace(match.NormalizedUrl) ? url : match.NormalizedUrl;
                var label = string.IsNullOrWhiteSpace(match.Label) ? url : match.Label;
                var qualityId = string.IsNullOrWhiteSpace(request.QualityId) ? null : request.QualityId.Trim();

                preparedItems.Add(new BatchDownloadQueueItemDto
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Url = effectiveUrl,
                    RawUrl = url,
                    SourceUrl = match.SourceUrl,
                    Label = label,
                    DownloaderId = match.DownloaderId,
                    DownloaderName = match.DownloaderName,
                    Entity = entityType.ToString(),
                    QualityId = qualityId,
                    StudioId = request.StudioId,
                    GroupId = request.GroupId,
                    TagIds = request.TagIds?.Where(id => id > 0).Distinct().ToList(),
                    PerformerIds = request.PerformerIds?.Where(id => id > 0).Distinct().ToList(),
                    AutoApplyMetadata = request.AutoApplyMetadata ?? true,
                    AllowDuplicates = request.AllowDuplicates ?? false,
                    Status = "Pending",
                    Progress = 0,
                    AddedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        var addedList = new List<BatchDownloadQueueItemDto>();
        BatchDownloadQueueStateDto snapshot;
        var shouldStartRunner = false;

        await _stateLock.WaitAsync(ct);
        try
        {
            await EnsureHydratedUnlockedAsync(ct);

            foreach (var candidate in preparedItems)
            {
                var existingIdx = _items.FindIndex(x =>
                    string.Equals(x.Url, candidate.Url, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Entity, candidate.Entity, StringComparison.OrdinalIgnoreCase));

                if (existingIdx >= 0 && !candidate.AllowDuplicates)
                {
                    var existing = _items[existingIdx];
                    if (existing.Status is "Failed" or "Skipped")
                    {
                        var requeued = candidate with { Id = existing.Id, EntityId = existing.EntityId ?? candidate.EntityId };
                        _items[existingIdx] = requeued;
                        addedList.Add(requeued);
                    }
                    else if (existing.Status is "Pending" or "Running")
                    {
                        skippedItems.Add(new { url = candidate.Url, reason = "Already pending or running in download queue." });
                    }
                    else
                    {
                        _items.Add(candidate);
                        addedList.Add(candidate);
                    }
                }
                else
                {
                    _items.Add(candidate);
                    addedList.Add(candidate);
                }
            }

            var autoStart = request.AutoStart ?? true;
            if (addedList.Count > 0 && autoStart && _runnerStatus != "Paused")
            {
                _runnerStatus = "Running";
                shouldStartRunner = !_workerLoopActive;
            }
            else if (_runnerStatus == "Running" && !_workerLoopActive && HasPendingUnlocked())
            {
                shouldStartRunner = true;
            }

            await PersistUnlockedAsync(ct);
            snapshot = BuildStateSnapshotUnlocked();
        }
        finally
        {
            _stateLock.Release();
        }

        if (shouldStartRunner && scopeFactory != null && jobService != null)
        {
            StartBackgroundWorkerLoop(scopeFactory, jobService);
            snapshot = await GetStateAsync(ct);
        }

        return new BatchQueueEnqueueResult(
            snapshot.ActiveJobId,
            addedList.Count,
            skippedItems.Count,
            addedList,
            skippedItems,
            snapshot);
    }

    public async Task<BatchDownloadQueueStateDto> PauseAsync(CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            await EnsureHydratedUnlockedAsync(ct);
            _runnerStatus = "Paused";
            _finishAfterCurrent = false;

            foreach (var exec in _activeExecutions.Values)
            {
                exec.Reason = ItemCancelReason.Pause;
                try { exec.Cts.Cancel(); } catch { }
            }

            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].Status == "Running")
                {
                    _items[i] = _items[i] with
                    {
                        Status = "Pending",
                        Progress = 0,
                        StatusMessage = "Paused — ready to resume",
                    };
                }
            }

            await PersistUnlockedAsync(ct);
            return BuildStateSnapshotUnlocked();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<BatchDownloadQueueStateDto> ResumeAsync(
        IExtensionServiceScopeFactory? scopeFactory = null,
        IJobService? jobService = null,
        CancellationToken ct = default)
    {
        var shouldStart = false;
        BatchDownloadQueueStateDto snapshot;

        await _stateLock.WaitAsync(ct);
        try
        {
            await EnsureHydratedUnlockedAsync(ct);
            _finishAfterCurrent = false;

            if (HasPendingUnlocked())
            {
                _runnerStatus = "Running";
                shouldStart = !_workerLoopActive;
            }
            else
            {
                _runnerStatus = "Idle";
            }

            await PersistUnlockedAsync(ct);
            snapshot = BuildStateSnapshotUnlocked();
        }
        finally
        {
            _stateLock.Release();
        }

        if (shouldStart && scopeFactory != null && jobService != null)
        {
            StartBackgroundWorkerLoop(scopeFactory, jobService);
            snapshot = await GetStateAsync(ct);
        }

        return snapshot;
    }

    public async Task<BatchDownloadQueueStateDto> SetFinishAfterCurrentAsync(
        bool? enabled,
        CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            await EnsureHydratedUnlockedAsync(ct);
            var nextVal = enabled ?? !_finishAfterCurrent;

            if (_runnerStatus != "Running" && _activeExecutions.Count == 0)
            {
                _finishAfterCurrent = false;
            }
            else
            {
                _finishAfterCurrent = nextVal;
            }

            await PersistUnlockedAsync(ct);
            return BuildStateSnapshotUnlocked();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<BatchDownloadQueueStateDto> SkipItemAsync(string itemId, CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            await EnsureHydratedUnlockedAsync(ct);
            SkipItemUnlocked(itemId);
            await PersistUnlockedAsync(ct);
            return BuildStateSnapshotUnlocked();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<BatchDownloadQueueStateDto> SkipCurrentAsync(CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            await EnsureHydratedUnlockedAsync(ct);
            var runningIds = _items.Where(i => i.Status == "Running").Select(i => i.Id).ToList();
            if (runningIds.Count > 0)
            {
                foreach (var id in runningIds)
                    SkipItemUnlocked(id);
            }
            else
            {
                var firstPending = _items.FirstOrDefault(i => i.Status == "Pending");
                if (firstPending != null)
                    SkipItemUnlocked(firstPending.Id);
            }

            await PersistUnlockedAsync(ct);
            return BuildStateSnapshotUnlocked();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<BatchDownloadQueueStateDto> RetryFailedOrSkippedAsync(
        string? itemId = null,
        bool autoStart = true,
        IExtensionServiceScopeFactory? scopeFactory = null,
        IJobService? jobService = null,
        CancellationToken ct = default)
    {
        var shouldStart = false;
        BatchDownloadQueueStateDto snapshot;

        await _stateLock.WaitAsync(ct);
        try
        {
            await EnsureHydratedUnlockedAsync(ct);
            var anyReset = false;

            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                var matchesId = string.IsNullOrWhiteSpace(itemId) || string.Equals(item.Id, itemId, StringComparison.OrdinalIgnoreCase);
                if (matchesId && item.Status is "Failed" or "Skipped")
                {
                    _items[i] = item with
                    {
                        Status = "Pending",
                        Progress = 0,
                        StatusMessage = "Queued for retry",
                        CompletedAt = null,
                    };
                    anyReset = true;
                }
            }

            if (anyReset && autoStart)
            {
                _runnerStatus = "Running";
                _finishAfterCurrent = false;
                shouldStart = !_workerLoopActive;
            }

            await PersistUnlockedAsync(ct);
            snapshot = BuildStateSnapshotUnlocked();
        }
        finally
        {
            _stateLock.Release();
        }

        if (shouldStart && scopeFactory != null && jobService != null)
        {
            StartBackgroundWorkerLoop(scopeFactory, jobService);
            snapshot = await GetStateAsync(ct);
        }

        return snapshot;
    }

    public async Task<BatchDownloadQueueStateDto> RemoveItemAsync(string itemId, CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            await EnsureHydratedUnlockedAsync(ct);
            if (_activeExecutions.TryGetValue(itemId, out var exec))
            {
                exec.Reason = ItemCancelReason.Remove;
                try { exec.Cts.Cancel(); } catch { }
            }

            _items.RemoveAll(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
            if (!HasPendingUnlocked() && _activeExecutions.Count == 0)
            {
                _runnerStatus = "Idle";
                _finishAfterCurrent = false;
            }

            await PersistUnlockedAsync(ct);
            return BuildStateSnapshotUnlocked();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<BatchDownloadQueueStateDto> ClearAsync(bool onlyFinished, CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            await EnsureHydratedUnlockedAsync(ct);
            if (onlyFinished)
            {
                _items.RemoveAll(i => i.Status is "Completed" or "Skipped" or "Failed");
            }
            else
            {
                foreach (var exec in _activeExecutions.Values)
                {
                    exec.Reason = ItemCancelReason.Remove;
                    try { exec.Cts.Cancel(); } catch { }
                }
                _items.Clear();
                _runnerStatus = "Idle";
                _finishAfterCurrent = false;
            }

            await PersistUnlockedAsync(ct);
            return BuildStateSnapshotUnlocked();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private void SkipItemUnlocked(string itemId)
    {
        if (_activeExecutions.TryGetValue(itemId, out var exec))
        {
            exec.Reason = ItemCancelReason.Skip;
            try { exec.Cts.Cancel(); } catch { }
        }

        var idx = _items.FindIndex(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && _items[idx].Status is "Pending" or "Running")
        {
            _items[idx] = _items[idx] with
            {
                Status = "Skipped",
                Progress = 0,
                StatusMessage = "Skipped by user",
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    private void StartBackgroundWorkerLoop(
        IExtensionServiceScopeFactory scopeFactory,
        IJobService jobService)
    {
        var jobId = jobService.Enqueue(
            "download-batch",
            "Processing persistent download queue",
            async (jobProgress, jobCt) =>
            {
                await RunQueueProcessorLoopAsync(scopeFactory, jobProgress, jobCt);
            },
            exclusive: false);

        _stateLock.Wait();
        try
        {
            _activeJobId = jobId;
            _workerLoopActive = true;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task RunQueueProcessorLoopAsync(
        IExtensionServiceScopeFactory scopeFactory,
        Cove.Core.Interfaces.IJobProgress? jobProgress,
        CancellationToken jobCt)
    {
        await _stateLock.WaitAsync(CancellationToken.None);
        try
        {
            _workerLoopActive = true;
        }
        finally
        {
            _stateLock.Release();
        }

        try
        {
            while (!jobCt.IsCancellationRequested)
            {
                BatchDownloadQueueItemDto? nextItem = null;
                ActiveItemExecution? execution = null;

                await _stateLock.WaitAsync(CancellationToken.None);
                try
                {
                    if (_runnerStatus != "Running")
                    {
                        break;
                    }

                    if (_finishAfterCurrent)
                    {
                        _finishAfterCurrent = false;
                        _runnerStatus = HasPendingUnlocked() ? "Paused" : "Idle";
                        await PersistUnlockedAsync(CancellationToken.None);
                        break;
                    }

                    var nextIdx = _items.FindIndex(i => i.Status == "Pending");
                    if (nextIdx < 0)
                    {
                        _finishAfterCurrent = false;
                        _runnerStatus = "Idle";
                        await PersistUnlockedAsync(CancellationToken.None);
                        break;
                    }

                    var itemCts = CancellationTokenSource.CreateLinkedTokenSource(jobCt);
                    execution = new ActiveItemExecution(itemCts);
                    nextItem = _items[nextIdx] with
                    {
                        Status = "Running",
                        Progress = 0,
                        StatusMessage = $"Downloading {_items[nextIdx].Label}...",
                    };
                    _items[nextIdx] = nextItem;
                    _activeExecutions[nextItem.Id] = execution;
                    await PersistUnlockedAsync(CancellationToken.None);
                }
                finally
                {
                    _stateLock.Release();
                }

                if (nextItem == null || execution == null)
                    break;

                var itemId = nextItem.Id;
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var sp = scope.ServiceProvider;
                    var scopedDownloader = sp.GetRequiredService<IDownloaderService>();

                    if (!Enum.TryParse<DownloaderEntity>(nextItem.Entity, true, out var entityType))
                        entityType = DownloaderEntity.Video;

                    var entityId = nextItem.EntityId;
                    var hasMetadata = nextItem.StudioId.HasValue
                        || nextItem.GroupId.HasValue
                        || (nextItem.TagIds is { Count: > 0 })
                        || (nextItem.PerformerIds is { Count: > 0 });

                    if (hasMetadata && !entityId.HasValue)
                    {
                        var videoRepo = sp.GetService<IVideoRepository>();
                        var galleryRepo = sp.GetService<IGalleryRepository>();
                        var imageRepo = sp.GetService<IImageRepository>();
                        var audioRepo = sp.GetService<IAudioRepository>();

                        if (videoRepo != null && galleryRepo != null && imageRepo != null && audioRepo != null)
                        {
                            try
                            {
                                entityId = await BatchDownloaderExtension.CreateEntityWithMetadataAsync(
                                    entityType,
                                    nextItem.Url,
                                    nextItem.Label,
                                    nextItem.StudioId,
                                    nextItem.TagIds,
                                    nextItem.PerformerIds,
                                    videoRepo,
                                    galleryRepo,
                                    imageRepo,
                                    audioRepo,
                                    execution.Cts.Token,
                                    nextItem.GroupId);

                                if (entityId.HasValue)
                                {
                                    await UpdateItemProgressAsync(itemId, 0, $"Created placeholder #{entityId.Value}, starting download...", entityId);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch
                            {
                                // Proceed without pre-created entity if repository creation fails
                            }
                        }
                    }

                    var itemProgress = new QueueItemJobProgress((pct, msg) =>
                    {
                        _ = UpdateItemProgressAsync(itemId, pct, msg);
                        jobProgress?.Report(pct / 100.0, msg ?? $"Downloading {nextItem.Label}");
                    });

                    var result = await scopedDownloader.DownloadAndImportAsync(
                        new DownloaderImportRequest(
                            nextItem.DownloaderId,
                            nextItem.Url,
                            entityType,
                            entityId,
                            nextItem.QualityId,
                            nextItem.SourceUrl)
                        {
                            AllowDuplicateDownload = nextItem.AllowDuplicates,
                        },
                        progress: itemProgress,
                        execution.Cts.Token);

                    await _stateLock.WaitAsync(CancellationToken.None);
                    try
                    {
                        var idx = _items.FindIndex(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0)
                        {
                            if (execution.Reason == ItemCancelReason.Skip)
                            {
                                _items[idx] = _items[idx] with
                                {
                                    Status = "Skipped",
                                    Progress = 0,
                                    StatusMessage = "Skipped by user",
                                    CompletedAt = DateTimeOffset.UtcNow,
                                };
                            }
                            else if (result == null)
                            {
                                _items[idx] = _items[idx] with
                                {
                                    Status = "Skipped",
                                    Progress = 0,
                                    StatusMessage = "Downloader returned no result.",
                                    CompletedAt = DateTimeOffset.UtcNow,
                                };
                            }
                            else
                            {
                                var finalEntityId = result.EntityId ?? entityId;
                                _items[idx] = _items[idx] with
                                {
                                    EntityId = finalEntityId,
                                    ImportedEntityId = finalEntityId,
                                    Status = "Completed",
                                    Progress = 100,
                                    StatusMessage = finalEntityId.HasValue
                                        ? $"Imported into {entityType} #{finalEntityId.Value}"
                                        : $"Imported {nextItem.Label}",
                                    CompletedAt = DateTimeOffset.UtcNow,
                                };
                            }
                            await PersistUnlockedAsync(CancellationToken.None);
                        }
                    }
                    finally
                    {
                        _stateLock.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    await _stateLock.WaitAsync(CancellationToken.None);
                    try
                    {
                        var idx = _items.FindIndex(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0)
                        {
                            if (execution.Reason == ItemCancelReason.Skip)
                            {
                                _items[idx] = _items[idx] with
                                {
                                    Status = "Skipped",
                                    Progress = 0,
                                    StatusMessage = "Skipped by user",
                                    CompletedAt = DateTimeOffset.UtcNow,
                                };
                            }
                            else if (execution.Reason == ItemCancelReason.Remove)
                            {
                                _items.RemoveAt(idx);
                            }
                            else
                            {
                                // Pause or host cancellation -> return item to Pending so it can be resumed
                                _items[idx] = _items[idx] with
                                {
                                    Status = "Pending",
                                    Progress = 0,
                                    StatusMessage = "Paused — ready to resume",
                                };
                                _runnerStatus = "Paused";
                                _finishAfterCurrent = false;
                            }
                            await PersistUnlockedAsync(CancellationToken.None);
                        }

                        if (execution.Reason == ItemCancelReason.Pause || jobCt.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                    finally
                    {
                        _stateLock.Release();
                    }
                }
                catch (Exception ex)
                {
                    await _stateLock.WaitAsync(CancellationToken.None);
                    try
                    {
                        var idx = _items.FindIndex(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0)
                        {
                            _items[idx] = _items[idx] with
                            {
                                Status = "Failed",
                                Progress = 0,
                                StatusMessage = ex.Message,
                                CompletedAt = DateTimeOffset.UtcNow,
                            };
                            await PersistUnlockedAsync(CancellationToken.None);
                        }
                    }
                    finally
                    {
                        _stateLock.Release();
                    }
                }
                finally
                {
                    await _stateLock.WaitAsync(CancellationToken.None);
                    try
                    {
                        _activeExecutions.Remove(itemId);
                    }
                    finally
                    {
                        _stateLock.Release();
                    }
                    execution.Cts.Dispose();
                }
            }
        }
        finally
        {
            await _stateLock.WaitAsync(CancellationToken.None);
            try
            {
                _workerLoopActive = false;
                _activeJobId = null;
                if (_runnerStatus == "Running" && !HasPendingUnlocked())
                {
                    _runnerStatus = "Idle";
                    _finishAfterCurrent = false;
                }
                await PersistUnlockedAsync(CancellationToken.None);
                var snap = BuildStateSnapshotUnlocked();
                jobProgress?.SetSummary($"{snap.CompletedCount} completed, {snap.SkippedCount} skipped, {snap.FailedCount} failed");
            }
            finally
            {
                _stateLock.Release();
            }
        }
    }

    private async Task UpdateItemProgressAsync(string itemId, double rawProgress, string? message, int? entityId = null)
    {
        var normalized = rawProgress <= 1.0 && rawProgress > 0.0
            ? Math.Round(rawProgress * 100.0, 1)
            : Math.Clamp(Math.Round(rawProgress, 1), 0.0, 100.0);

        await _stateLock.WaitAsync(CancellationToken.None);
        try
        {
            var idx = _items.FindIndex(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && _items[idx].Status == "Running")
            {
                _items[idx] = _items[idx] with
                {
                    Progress = normalized,
                    StatusMessage = string.IsNullOrWhiteSpace(message) ? _items[idx].StatusMessage : message,
                    EntityId = entityId ?? _items[idx].EntityId,
                };
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private bool HasPendingUnlocked() => _items.Any(i => i.Status == "Pending");

    private BatchDownloadQueueStateDto BuildStateSnapshotUnlocked()
    {
        var pending = 0;
        var running = 0;
        var completed = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var item in _items)
        {
            switch (item.Status)
            {
                case "Pending": pending++; break;
                case "Running": running++; break;
                case "Completed": completed++; break;
                case "Skipped": skipped++; break;
                case "Failed": failed++; break;
            }
        }

        return new BatchDownloadQueueStateDto
        {
            RunnerStatus = _runnerStatus,
            FinishAfterCurrent = _finishAfterCurrent,
            ActiveJobId = _activeJobId,
            TotalCount = _items.Count,
            PendingCount = pending,
            RunningCount = running,
            CompletedCount = completed,
            SkippedCount = skipped,
            FailedCount = failed,
            Items = _items.ToList(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private async Task EnsureHydratedUnlockedAsync(CancellationToken ct)
    {
        if (_hydrated) return;
        _hydrated = true;

        if (_store == null) return;

        var raw = await _store.GetAsync(StoreKey, ct);
        if (string.IsNullOrWhiteSpace(raw)) return;

        try
        {
            var persisted = JsonSerializer.Deserialize<BatchDownloadQueuePersistedState>(raw, JsonOpts);
            if (persisted?.Items == null) return;

            var hadRunning = false;
            _items = persisted.Items.Select(item =>
            {
                if (string.Equals(item.Status, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    hadRunning = true;
                    return item with
                    {
                        Status = "Pending",
                        Progress = 0,
                        StatusMessage = "Paused — ready to resume",
                    };
                }
                return item;
            }).ToList();

            var hasPending = HasPendingUnlocked();
            _runnerStatus = (hadRunning || string.Equals(persisted.RunnerStatus, "Paused", StringComparison.OrdinalIgnoreCase) || string.Equals(persisted.RunnerStatus, "Running", StringComparison.OrdinalIgnoreCase)) && hasPending
                ? "Paused"
                : "Idle";
            _finishAfterCurrent = false;
        }
        catch
        {
            // Ignore malformed JSON in store
        }
    }

    private async Task PersistUnlockedAsync(CancellationToken ct)
    {
        if (_store == null) return;

        var payload = new BatchDownloadQueuePersistedState
        {
            RunnerStatus = _runnerStatus,
            FinishAfterCurrent = _finishAfterCurrent,
            Items = _items,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        await _store.SetAsync(StoreKey, json, ct);
    }

    private sealed class QueueItemJobProgress(Action<double, string?> onReport) : Cove.Core.Interfaces.IJobProgress
    {
        public void Report(double progress, string? subTask = null) => onReport(progress, subTask);
    }
}
