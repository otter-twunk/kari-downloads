import React, { useState, useMemo, useEffect, useCallback } from "@cove/runtime/react";
import { extensionFetch } from "@cove/runtime/api";
import {
  Download,
  CheckCircle2,
  AlertCircle,
  AlertTriangle,
  Loader2,
  Trash2,
  ExternalLink,
  Layers,
  Tag,
  Film,
  Sparkles,
  RefreshCw,
  Sliders,
  CopyCheck,
  Play,
  Pause,
  SkipForward,
  Flag,
  Plus,
} from "@cove/runtime/lucide-react";
import {
  EntityReferenceMultiSelector,
  EntityReferenceSelector,
  useDocumentTitle,
  type EntityReferenceOption,
} from "@cove/runtime/components";

export type DownloadEntityType = "auto" | "Video" | "Gallery" | "Image" | "Audio";

export interface ParsedUrlItem {
  id: string;
  rawUrl: string;
  normalizedUrl: string;
  sourceUrl?: string;
  label?: string;
  downloaderId?: string;
  downloaderName?: string;
  detectedEntity?: string;
  targetEntity: "Video" | "Gallery" | "Image" | "Audio";
  manualEntityOverride?: boolean;
  qualityOptions?: Array<{ id: string; label: string }>;
  selectedQualityId?: string;
  status: "pending" | "inspecting" | "matched" | "unsupported" | "error";
  statusMessage?: string;
  isDuplicate?: boolean;
  duplicateReason?: string;
}

export interface PersistentQueueItem {
  id: string;
  url: string;
  rawUrl: string;
  sourceUrl?: string;
  label: string;
  downloaderId: string;
  downloaderName?: string;
  entity: "Video" | "Gallery" | "Image" | "Audio";
  entityId?: number | null;
  qualityId?: string | null;
  status: "Pending" | "Running" | "Completed" | "Skipped" | "Failed";
  progress: number;
  statusMessage?: string;
  importedEntityId?: number | null;
  addedAt: string;
  completedAt?: string | null;
}

export interface PersistentQueueState {
  runnerStatus: "Idle" | "Running" | "Paused";
  finishAfterCurrent: boolean;
  activeJobId?: string | null;
  totalCount: number;
  pendingCount: number;
  runningCount: number;
  completedCount: number;
  skippedCount: number;
  failedCount: number;
  items: PersistentQueueItem[];
  updatedAt?: string;
}

const LOCAL_CACHE_KEY = "cove.batchDownloader.queue.v1";

const EMPTY_QUEUE: PersistentQueueState = {
  runnerStatus: "Idle",
  finishAfterCurrent: false,
  totalCount: 0,
  pendingCount: 0,
  runningCount: 0,
  completedCount: 0,
  skippedCount: 0,
  failedCount: 0,
  items: [],
};

function loadCachedQueue(): PersistentQueueState {
  try {
    const raw = window.localStorage.getItem(LOCAL_CACHE_KEY);
    if (!raw) return EMPTY_QUEUE;
    const parsed = JSON.parse(raw) as PersistentQueueState;
    if (!parsed || !Array.isArray(parsed.items)) return EMPTY_QUEUE;
    return parsed;
  } catch {
    return EMPTY_QUEUE;
  }
}

export function BatchDownloadPage() {
  useDocumentTitle("Paste & Download");

  const [inputText, setInputText] = useState("");
  const [dismissedUrls, setDismissedUrls] = useState<Set<string>>(new Set());
  const [defaultEntity, setDefaultEntity] = useState<DownloadEntityType>("auto");
  const [autoApplyMetadata, setAutoApplyMetadata] = useState(true);
  const [allowDuplicates, setAllowDuplicates] = useState(false);
  const [showAdvancedGen, setShowAdvancedGen] = useState(false);

  // Metadata Tagging State
  const [selectedTags, setSelectedTags] = useState<EntityReferenceOption[]>([]);
  const [selectedStudioId, setSelectedStudioId] = useState<number | null>(null);
  const [selectedGroupId, setSelectedGroupId] = useState<number | null>(null);
  const [selectedPerformers, setSelectedPerformers] = useState<EntityReferenceOption[]>([]);

  // Generation options
  const [genThumbnails, setGenThumbnails] = useState(true);
  const [genPreviews, setGenPreviews] = useState(false);
  const [genSprites, setGenSprites] = useState(false);
  const [genPhashes, setGenPhashes] = useState(false);
  const [genMd5, setGenMd5] = useState(false);

  // Staging Items table (before adding to persistent queue)
  const [items, setItems] = useState<ParsedUrlItem[]>([]);
  const [isInspecting, setIsInspecting] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);

  // Persistent Download Queue State
  const [queueState, setQueueState] = useState<PersistentQueueState>(loadCachedQueue);
  const [submitSummary, setSubmitSummary] = useState<{
    queued: number;
    skipped: Array<{ url: string; reason: string }>;
  } | null>(null);

  const applyQueueState = useCallback((next: PersistentQueueState) => {
    setQueueState(next);
    try {
      window.localStorage.setItem(LOCAL_CACHE_KEY, JSON.stringify(next));
    } catch {
      // ignore storage errors
    }
  }, []);

  const fetchQueueState = useCallback(async () => {
    try {
      const res = await extensionFetch("/api/ext/batch-downloader/queue");
      if (!res.ok) return;
      const data: PersistentQueueState = await res.json();
      applyQueueState(data);
    } catch {
      // ignore transient fetch errors
    }
  }, [applyQueueState]);

  // Initial load on mount
  useEffect(() => {
    void fetchQueueState();
  }, [fetchQueueState]);

  // Poll persistent queue while Running
  useEffect(() => {
    if (queueState.runnerStatus !== "Running" && queueState.runningCount === 0) {
      return;
    }

    const interval = window.setInterval(() => {
      void fetchQueueState();
    }, 1500);

    return () => window.clearInterval(interval);
  }, [fetchQueueState, queueState.runnerStatus, queueState.runningCount]);

  // Extract URLs from raw text
  const extractedUrls = useMemo(() => {
    if (!inputText.trim()) return [];
    const urlRegex = /https?:\/\/[^\s<>"'{}|\\^`\]\)]+/g;
    const matches = inputText.match(urlRegex) ?? [];
    const cleaned = matches
      .map((u) => u.replace(/[.,;:!?)\]}>]+$/, "").trim())
      .filter((u) => {
        try {
          const parsed = new URL(u);
          return parsed.protocol === "http:" || parsed.protocol === "https:";
        } catch {
          return false;
        }
      });
    return Array.from(new Set(cleaned)).filter((u) => !dismissedUrls.has(u));
  }, [inputText, dismissedUrls]);

  // Sync extracted URLs to staging items without resurrecting dismissed rows or clobbering manual row overrides
  useEffect(() => {
    setItems((prevItems) => {
      const prevMap = new Map(prevItems.map((item) => [item.rawUrl, item]));
      return extractedUrls.map((url) => {
        const existing = prevMap.get(url);
        if (existing) {
          if (existing.manualEntityOverride) {
            return existing;
          }
          const effectiveTarget =
            defaultEntity === "auto"
              ? ((existing.detectedEntity as ParsedUrlItem["targetEntity"]) || "Video")
              : defaultEntity;
          return { ...existing, targetEntity: effectiveTarget };
        }
        return {
          id: url,
          rawUrl: url,
          normalizedUrl: url,
          targetEntity: defaultEntity === "auto" ? "Video" : defaultEntity,
          status: "pending",
        };
      });
    });
  }, [extractedUrls, defaultEntity]);

  // Preflight inspection with bounded concurrency + duplicate check
  const inspectUrls = async () => {
    if (items.length === 0) return;
    setIsInspecting(true);

    const snapshot = items.map((item) => ({ ...item, status: "inspecting" as const }));
    setItems(snapshot);

    const concurrency = Math.min(4, snapshot.length);
    let cursor = 0;

    const updateOne = (rawUrl: string, patch: Partial<ParsedUrlItem>) => {
      setItems((prev) =>
        prev.map((item) => (item.rawUrl === rawUrl ? { ...item, ...patch } : item))
      );
    };

    const worker = async () => {
      while (cursor < snapshot.length) {
        const index = cursor++;
        const item = snapshot[index];
        try {
          const res = await extensionFetch("/api/system/downloaders/match", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ url: item.rawUrl }),
          });

          if (!res.ok) {
            throw new Error(`HTTP ${res.status}`);
          }

          const matches = await res.json();
          if (Array.isArray(matches) && matches.length > 0) {
            const matched = matches[0];
            const detected = matched.supportedEntity || "Video";
            const normalizedUrl = matched.normalizedUrl || item.rawUrl;
            const targetEntity = item.manualEntityOverride
              ? item.targetEntity
              : defaultEntity === "auto"
              ? detected
              : defaultEntity;

            let isDuplicate = false;
            let duplicateReason: string | undefined;
            try {
              const preflightRes = await extensionFetch("/api/system/downloaders/preflight", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                  url: normalizedUrl,
                  entity: targetEntity,
                }),
              });
              if (preflightRes.ok) {
                const preflight = await preflightRes.json();
                isDuplicate = Boolean(preflight.isDuplicate);
                duplicateReason = preflight.duplicateReason || undefined;
              }
            } catch {
              // ignore preflight check failure
            }

            updateOne(item.rawUrl, {
              downloaderId: matched.downloaderId,
              downloaderName: matched.downloaderName,
              detectedEntity: detected,
              normalizedUrl,
              sourceUrl: matched.sourceUrl || undefined,
              label: matched.label || undefined,
              qualityOptions: matched.qualityOptions ?? [],
              selectedQualityId: item.selectedQualityId ?? "",
              targetEntity,
              isDuplicate,
              duplicateReason,
              status: "matched",
              statusMessage: undefined,
            });
          } else {
            updateOne(item.rawUrl, {
              status: "unsupported",
              statusMessage: "No downloader matched this URL",
            });
          }
        } catch (err: any) {
          updateOne(item.rawUrl, {
            status: "error",
            statusMessage: err?.message || "Failed to inspect URL",
          });
        }
      }
    };

    await Promise.all(Array.from({ length: concurrency }, () => worker()));
    setIsInspecting(false);
  };

  const removeItem = (rawUrl: string) => {
    setDismissedUrls((prev) => {
      const next = new Set(prev);
      next.add(rawUrl);
      return next;
    });
    setItems((prev) => prev.filter((item) => item.rawUrl !== rawUrl));
  };

  const removeUnsupported = () => {
    const toRemove = items
      .filter((i) => i.status === "unsupported" || i.status === "error")
      .map((i) => i.rawUrl);
    if (toRemove.length === 0) return;
    setDismissedUrls((prev) => {
      const next = new Set(prev);
      for (const u of toRemove) next.add(u);
      return next;
    });
    setItems((prev) => prev.filter((i) => i.status !== "unsupported" && i.status !== "error"));
  };

  const removeDuplicates = () => {
    const toRemove = items.filter((i) => i.isDuplicate).map((i) => i.rawUrl);
    if (toRemove.length === 0) return;
    setDismissedUrls((prev) => {
      const next = new Set(prev);
      for (const u of toRemove) next.add(u);
      return next;
    });
    setItems((prev) => prev.filter((i) => !i.isDuplicate));
  };

  const unsupportedCount = useMemo(
    () => items.filter((i) => i.status === "unsupported" || i.status === "error").length,
    [items]
  );

  const duplicateCount = useMemo(
    () => items.filter((i) => i.isDuplicate).length,
    [items]
  );

  // Add staged items to the persistent download queue
  const handleAddToQueue = async (autoStart = true) => {
    const eligibleItems = items.filter(
      (item) =>
        item.status !== "unsupported" &&
        item.status !== "error" &&
        (allowDuplicates || !item.isDuplicate)
    );

    if (eligibleItems.length === 0) return;
    setIsSubmitting(true);
    setSubmitSummary(null);

    try {
      const tagIds = selectedTags.map((t) => t.id);
      const performerIds = selectedPerformers.map((p) => p.id);

      const res = await extensionFetch("/api/ext/batch-downloader/queue", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          items: eligibleItems.map((item) => ({
            url: item.normalizedUrl,
            rawUrl: item.rawUrl,
            sourceUrl: item.sourceUrl || undefined,
            label: item.label || item.rawUrl,
            downloaderId: item.downloaderId || undefined,
            downloaderName: item.downloaderName || undefined,
            entity: item.manualEntityOverride ? item.targetEntity : defaultEntity === "auto" ? item.detectedEntity || "auto" : defaultEntity,
            qualityId: item.selectedQualityId || null,
          })),
          entity: defaultEntity,
          studioId: selectedStudioId,
          groupId: selectedGroupId,
          tagIds,
          performerIds,
          autoApplyMetadata,
          allowDuplicates,
          autoStart,
        }),
      });

      if (!res.ok) {
        const errJson = await res.json().catch(() => ({}));
        throw new Error(errJson.error || `Failed to queue downloads (${res.status})`);
      }

      const result = await res.json();
      if (result.queue) {
        applyQueueState(result.queue);
      } else {
        await fetchQueueState();
      }

      setSubmitSummary({
        queued: result.queuedCount ?? 0,
        skipped: result.skipped || [],
      });

      // Clear staging area so user can add more URLs anytime
      if ((result.queuedCount ?? 0) > 0) {
        setInputText("");
        setDismissedUrls(new Set());
        setItems([]);
      }
    } catch (err: any) {
      alert(`Error adding to download queue: ${err.message}`);
    } finally {
      setIsSubmitting(false);
    }
  };

  // Persistent Queue Control Handlers
  const handlePauseQueue = async () => {
    const res = await extensionFetch("/api/ext/batch-downloader/queue/pause", { method: "POST" });
    if (res.ok) applyQueueState(await res.json());
  };

  const handleResumeQueue = async () => {
    const res = await extensionFetch("/api/ext/batch-downloader/queue/resume", { method: "POST" });
    if (res.ok) applyQueueState(await res.json());
  };

  const handleToggleFinishAfterCurrent = async () => {
    const res = await extensionFetch("/api/ext/batch-downloader/queue/finish-after-current", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled: !queueState.finishAfterCurrent }),
    });
    if (res.ok) applyQueueState(await res.json());
  };

  const handleSkipCurrent = async () => {
    const res = await extensionFetch("/api/ext/batch-downloader/queue/skip-current", {
      method: "POST",
    });
    if (res.ok) applyQueueState(await res.json());
  };

  const handleSkipItem = async (itemId: string) => {
    const res = await extensionFetch(`/api/ext/batch-downloader/queue/skip/${encodeURIComponent(itemId)}`, {
      method: "POST",
    });
    if (res.ok) applyQueueState(await res.json());
  };

  const handleRetryItem = async (itemId: string) => {
    const res = await extensionFetch(`/api/ext/batch-downloader/queue/retry/${encodeURIComponent(itemId)}`, {
      method: "POST",
    });
    if (res.ok) applyQueueState(await res.json());
  };

  const handleRetryAllFailed = async () => {
    const res = await extensionFetch("/api/ext/batch-downloader/queue/retry-failed", {
      method: "POST",
    });
    if (res.ok) applyQueueState(await res.json());
  };

  const handleRemoveQueueItem = async (itemId: string) => {
    const res = await extensionFetch(`/api/ext/batch-downloader/queue/${encodeURIComponent(itemId)}`, {
      method: "DELETE",
    });
    if (res.ok) applyQueueState(await res.json());
  };

  const handleClearQueue = async (onlyFinished: boolean) => {
    const res = await extensionFetch(
      `/api/ext/batch-downloader/queue/clear?onlyFinished=${onlyFinished}`,
      { method: "POST" }
    );
    if (res.ok) applyQueueState(await res.json());
  };

  const processedQueueCount =
    queueState.completedCount + queueState.skippedCount + queueState.failedCount;
  const overallQueuePercent =
    queueState.totalCount > 0
      ? Math.round((processedQueueCount / queueState.totalCount) * 100)
      : 0;
  const isQueueRunning = queueState.runnerStatus === "Running";
  const isQueuePaused = queueState.runnerStatus === "Paused";

  return (
    <div className="max-w-6xl mx-auto p-6 space-y-6">
      {/* Header */}
      <div className="flex flex-col md:flex-row md:items-center justify-between gap-4 border-b border-border pb-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-foreground flex items-center gap-2">
            <Download className="w-6 h-6 text-accent" />
            Paste & Download
          </h1>
          <p className="text-sm text-muted">
            Paste media links from clipboard, inspect compatible Cove downloaders, apply metadata tags, and manage your persistent download queue.
          </p>
        </div>

        {items.length > 0 && (
          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={inspectUrls}
              disabled={isInspecting || isSubmitting}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium rounded-lg border border-border bg-card hover:bg-surface text-foreground transition-colors disabled:opacity-50 cursor-pointer"
            >
              {isInspecting ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <RefreshCw className="w-3.5 h-3.5" />}
              Inspect Downloaders
            </button>
            {!isQueueRunning && !isQueuePaused && (
              <button
                type="button"
                onClick={() => handleAddToQueue(false)}
                disabled={isSubmitting || items.length === 0}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium rounded-lg border border-border bg-card hover:bg-surface text-foreground transition-colors disabled:opacity-50 cursor-pointer"
              >
                <Plus className="w-3.5 h-3.5" />
                Add to Queue (Paused)
              </button>
            )}
            <button
              type="button"
              onClick={() => handleAddToQueue(true)}
              disabled={isSubmitting || items.length === 0}
              className="inline-flex items-center gap-1.5 px-4 py-1.5 text-xs font-semibold rounded-lg bg-accent text-white hover:bg-accent-hover transition-colors disabled:opacity-50 shadow-sm cursor-pointer"
            >
              {isSubmitting ? (
                <Loader2 className="w-3.5 h-3.5 animate-spin" />
              ) : isQueueRunning || isQueuePaused ? (
                <Plus className="w-3.5 h-3.5" />
              ) : (
                <Download className="w-3.5 h-3.5" />
              )}
              {isQueueRunning || isQueuePaused
                ? `Append ${items.length} to Queue`
                : `Queue & Start (${items.length})`}
            </button>
          </div>
        )}
      </div>

      {/* Persistent Download Queue Panel */}
      {queueState.totalCount > 0 && (
        <div className="rounded-xl border border-border bg-card overflow-hidden space-y-0">
          {/* Queue Control Header */}
          <div className="p-4 border-b border-border bg-surface/40 space-y-3">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div className="flex items-center gap-2.5">
                {isQueueRunning ? (
                  <Loader2 className="w-5 h-5 animate-spin text-accent" />
                ) : isQueuePaused ? (
                  <Pause className="w-5 h-5 text-amber-400" />
                ) : (
                  <CheckCircle2 className="w-5 h-5 text-emerald-400" />
                )}
                <div>
                  <div className="flex items-center gap-2 flex-wrap">
                    <h2 id="persistent-download-queue-heading" className="text-sm font-bold text-foreground">
                      Persistent Download Queue
                    </h2>
                    <span className="text-xs px-2 py-0.5 rounded-full bg-accent/15 text-accent border border-accent/25 font-semibold">
                      {processedQueueCount}/{queueState.totalCount} ({overallQueuePercent}%)
                    </span>
                    {isQueuePaused && (
                      <span className="text-xs px-2 py-0.5 rounded-full bg-amber-500/15 text-amber-400 border border-amber-500/30 font-semibold">
                        Paused
                      </span>
                    )}
                    {isQueueRunning && queueState.finishAfterCurrent && (
                      <span className="text-xs px-2 py-0.5 rounded-full bg-amber-500/15 text-amber-400 border border-amber-500/30 font-semibold">
                        Finishing after current job...
                      </span>
                    )}
                  </div>
                  <p className="text-xs text-muted">
                    {isQueueRunning
                      ? queueState.finishAfterCurrent
                        ? "Completing active download, then pausing automatically"
                        : `${queueState.runningCount} downloading • ${queueState.pendingCount} pending in queue`
                      : isQueuePaused
                      ? `Queue paused with ${queueState.pendingCount} pending item(s)`
                      : queueState.pendingCount > 0
                      ? `${queueState.pendingCount} pending item(s) ready to start`
                      : "All queued items processed — paste more URLs below to add to queue"}
                  </p>
                </div>
              </div>

              {/* Queue Action Controls: Pause/Resume, Skip, Finish After Current */}
              <div className="flex flex-wrap items-center gap-2">
                {isQueueRunning ? (
                  <>
                    <button
                      type="button"
                      onClick={handlePauseQueue}
                      className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-semibold rounded-lg border border-amber-500/30 bg-amber-500/15 text-amber-400 hover:bg-amber-500/25 transition-colors cursor-pointer"
                      title="Pause queue immediately and return active download to Pending"
                    >
                      <Pause className="w-3.5 h-3.5" />
                      Pause
                    </button>
                    <button
                      type="button"
                      onClick={handleSkipCurrent}
                      className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-semibold rounded-lg border border-border bg-card hover:bg-surface text-foreground transition-colors cursor-pointer"
                      title="Skip the currently downloading item and move to the next item"
                    >
                      <SkipForward className="w-3.5 h-3.5" />
                      Skip Current
                    </button>
                    <button
                      type="button"
                      onClick={handleToggleFinishAfterCurrent}
                      className={`inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-semibold rounded-lg border transition-colors cursor-pointer ${
                        queueState.finishAfterCurrent
                          ? "border-amber-500 bg-amber-500/20 text-amber-300"
                          : "border-border bg-card hover:bg-surface text-muted hover:text-foreground"
                      }`}
                      title="Let the current download finish importing, then pause the queue automatically"
                    >
                      <Flag className="w-3.5 h-3.5" />
                      {queueState.finishAfterCurrent ? "Finishing After Current ✓" : "Finish After Current"}
                    </button>
                  </>
                ) : (
                  queueState.pendingCount > 0 && (
                    <>
                      <button
                        type="button"
                        onClick={handleResumeQueue}
                        className="inline-flex items-center gap-1.5 px-3.5 py-1.5 text-xs font-bold rounded-lg bg-accent hover:bg-accent-hover text-white transition-colors cursor-pointer"
                      >
                        <Play className="w-3.5 h-3.5 fill-current" />
                        {isQueuePaused
                          ? `Resume Queue (${queueState.pendingCount})`
                          : `Start Queue (${queueState.pendingCount})`}
                      </button>
                      <button
                        type="button"
                        onClick={handleSkipCurrent}
                        className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-semibold rounded-lg border border-border bg-card hover:bg-surface text-foreground transition-colors cursor-pointer"
                        title="Skip the next pending item in the queue"
                      >
                        <SkipForward className="w-3.5 h-3.5" />
                        Skip Next
                      </button>
                    </>
                  )
                )}
              </div>
            </div>

            {/* Overall Progress Bar */}
            <div className="w-full bg-border rounded-full h-2 overflow-hidden">
              <div
                className="bg-accent h-2 transition-all duration-300"
                style={{ width: `${overallQueuePercent}%` }}
              />
            </div>

            {/* Summary Badges & Cleanup */}
            <div className="flex flex-wrap items-center justify-between gap-2 text-xs">
              <div className="flex flex-wrap items-center gap-2">
                <span className="px-2 py-0.5 rounded bg-card border border-border text-foreground font-medium">
                  {queueState.pendingCount} Pending
                </span>
                <span className="px-2 py-0.5 rounded bg-emerald-500/15 border border-emerald-500/25 text-emerald-400 font-medium">
                  {queueState.completedCount} Completed
                </span>
                {queueState.skippedCount > 0 && (
                  <span className="px-2 py-0.5 rounded bg-card border border-border text-muted font-medium">
                    {queueState.skippedCount} Skipped
                  </span>
                )}
                {queueState.failedCount > 0 && (
                  <span className="px-2 py-0.5 rounded bg-red-500/15 border border-red-500/25 text-red-400 font-medium">
                    {queueState.failedCount} Failed
                  </span>
                )}
              </div>

              <div className="flex items-center gap-2">
                {(queueState.failedCount > 0 || queueState.skippedCount > 0) && (
                  <button
                    type="button"
                    onClick={handleRetryAllFailed}
                    className="inline-flex items-center gap-1 px-2.5 py-1 rounded-md border border-border bg-card hover:bg-surface text-foreground transition-colors cursor-pointer"
                  >
                    <RefreshCw className="w-3 h-3" />
                    Retry Failed / Skipped ({queueState.failedCount + queueState.skippedCount})
                  </button>
                )}
                {processedQueueCount > 0 && (
                  <button
                    type="button"
                    onClick={() => handleClearQueue(true)}
                    className="px-2.5 py-1 rounded-md border border-border bg-card hover:bg-surface text-muted hover:text-foreground transition-colors cursor-pointer"
                  >
                    Clear Finished ({processedQueueCount})
                  </button>
                )}
                <button
                  type="button"
                  onClick={() => handleClearQueue(false)}
                  className="px-2.5 py-1 rounded-md border border-red-500/25 bg-red-500/10 hover:bg-red-500/20 text-red-400 transition-colors cursor-pointer"
                >
                  Clear All
                </button>
              </div>
            </div>
          </div>

          {/* Persistent Queue Items Table */}
          <div
            tabIndex={0}
            role="region"
            aria-labelledby="persistent-download-queue-heading"
            className="overflow-x-auto max-h-[380px] overflow-y-auto"
          >
            <table className="w-full text-left text-xs border-separate border-spacing-0">
              <thead className="sticky top-0 z-10 bg-surface text-muted uppercase text-[10px] tracking-wider border-b border-border">
                <tr>
                  <th scope="col" className="py-2.5 px-3 border-b border-border">Status</th>
                  <th scope="col" className="py-2.5 px-3 border-b border-border">Item / URL</th>
                  <th scope="col" className="py-2.5 px-3 border-b border-border">Downloader</th>
                  <th scope="col" className="py-2.5 px-3 border-b border-border">Entity</th>
                  <th scope="col" className="py-2.5 px-3 text-right border-b border-border">Controls</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {queueState.items.map((qItem) => (
                  <tr key={qItem.id} className="hover:bg-surface/30 transition-colors">
                    {/* Status */}
                    <td className="py-2.5 px-3 whitespace-nowrap">
                      {qItem.status === "Running" ? (
                        <div className="space-y-1 min-w-[110px]">
                          <span className="inline-flex items-center gap-1.5 text-accent font-semibold">
                            <Loader2 className="w-3.5 h-3.5 animate-spin" />
                            {Math.round(qItem.progress)}%
                          </span>
                          <div className="w-full bg-border rounded-full h-1.5 overflow-hidden">
                            <div
                              className="bg-accent h-1.5 transition-all duration-300"
                              style={{ width: `${Math.min(100, Math.max(0, qItem.progress))}%` }}
                            />
                          </div>
                        </div>
                      ) : qItem.status === "Completed" ? (
                        <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-emerald-500/15 text-emerald-400 border border-emerald-500/25 font-semibold">
                          <CheckCircle2 className="w-3 h-3" />
                          Completed
                        </span>
                      ) : qItem.status === "Skipped" ? (
                        <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-surface text-muted border border-border font-medium">
                          <SkipForward className="w-3 h-3" />
                          Skipped
                        </span>
                      ) : qItem.status === "Failed" ? (
                        <span
                          className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-red-500/15 text-red-400 border border-red-500/25 font-semibold"
                          title={qItem.statusMessage}
                        >
                          <AlertCircle className="w-3 h-3" />
                          Failed
                        </span>
                      ) : (
                        <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-surface text-muted border border-border">
                          Pending
                        </span>
                      )}
                    </td>

                    {/* Label & URL */}
                    <td className="py-2.5 px-3 max-w-sm truncate">
                      <div className="flex items-center gap-1.5">
                        <span className="font-medium text-foreground truncate" title={qItem.label}>
                          {qItem.label || qItem.url}
                        </span>
                        <a
                          href={qItem.url}
                          target="_blank"
                          rel="noreferrer"
                          className="text-muted hover:text-accent flex-shrink-0"
                        >
                          <ExternalLink className="w-3 h-3" />
                        </a>
                      </div>
                      {qItem.statusMessage && (
                        <span className="text-[10px] text-muted truncate block" title={qItem.statusMessage}>
                          {qItem.statusMessage}
                        </span>
                      )}
                    </td>

                    {/* Downloader */}
                    <td className="py-2.5 px-3 whitespace-nowrap text-muted">
                      {qItem.downloaderName || qItem.downloaderId}
                    </td>

                    {/* Entity */}
                    <td className="py-2.5 px-3 whitespace-nowrap">
                      <span className="px-2 py-0.5 rounded bg-surface border border-border text-foreground text-[11px]">
                        {qItem.entity}
                        {qItem.importedEntityId ? ` #${qItem.importedEntityId}` : ""}
                      </span>
                    </td>

                    {/* Controls */}
                    <td className="py-2.5 px-3 text-right whitespace-nowrap">
                      <div className="inline-flex items-center gap-1.5">
                        {(qItem.status === "Pending" || qItem.status === "Running") && (
                          <button
                            type="button"
                            onClick={() => handleSkipItem(qItem.id)}
                            className="inline-flex items-center gap-1 px-2 py-1 rounded border border-border bg-card hover:bg-surface text-muted hover:text-foreground transition-colors cursor-pointer"
                            title="Skip this download"
                          >
                            <SkipForward className="w-3 h-3" />
                            Skip
                          </button>
                        )}
                        {(qItem.status === "Failed" || qItem.status === "Skipped") && (
                          <button
                            type="button"
                            onClick={() => handleRetryItem(qItem.id)}
                            className="inline-flex items-center gap-1 px-2 py-1 rounded border border-border bg-card hover:bg-surface text-foreground transition-colors cursor-pointer"
                            title="Retry this download"
                          >
                            <RefreshCw className="w-3 h-3" />
                            Retry
                          </button>
                        )}
                        <button
                          type="button"
                          onClick={() => handleRemoveQueueItem(qItem.id)}
                          className="text-muted hover:text-red-500 transition-colors p-1 cursor-pointer"
                          title="Remove from persistent queue"
                          aria-label={`Remove ${qItem.label || qItem.url} from queue`}
                        >
                          <Trash2 className="w-3.5 h-3.5" />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* Submission Feedback */}
      {submitSummary && (
        <div className="rounded-xl border border-border bg-card p-4 space-y-2">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2 text-sm font-medium text-foreground">
              <CheckCircle2 className="w-4 h-4 text-green-500" />
              Added {submitSummary.queued} {submitSummary.queued === 1 ? "item" : "items"} to the persistent download queue.
            </div>
            <button
              type="button"
              onClick={() => setSubmitSummary(null)}
              className="text-xs text-muted hover:text-foreground cursor-pointer"
            >
              Dismiss
            </button>
          </div>
          {submitSummary.skipped.length > 0 && (
            <div className="space-y-1 mt-2">
              <span className="text-xs font-semibold uppercase text-muted">Issues / Skipped:</span>
              <ul className="text-xs text-muted space-y-1">
                {submitSummary.skipped.map((issue, idx) => (
                  <li key={idx} className="flex items-center gap-1.5 text-amber-500">
                    <AlertTriangle className="w-3 h-3 flex-shrink-0" />
                    <span>{issue.url}: {issue.reason}</span>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      )}

      {/* Input & Options Grid */}
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Left Column: Paste Area & Metadata Tagging */}
        <div className="lg:col-span-2 space-y-6">
          {/* Paste Card */}
          <div className="rounded-xl border border-border bg-card p-4 space-y-3">
            <div className="flex items-center justify-between">
              <label className="text-sm font-semibold text-foreground flex items-center gap-2">
                <Layers className="w-4 h-4 text-accent" />
                Add URLs to Queue
              </label>
              <div className="flex items-center gap-2">
                {items.length > 0 && (
                  <span className="text-xs px-2 py-0.5 rounded-full bg-accent/10 text-accent font-medium">
                    {items.length} {items.length === 1 ? "URL" : "URLs"} staged
                  </span>
                )}
                {(inputText || dismissedUrls.size > 0) && (
                  <button
                    type="button"
                    onClick={() => {
                      setInputText("");
                      setDismissedUrls(new Set());
                    }}
                    className="text-xs text-muted hover:text-foreground transition-colors cursor-pointer"
                  >
                    Clear
                  </button>
                )}
              </div>
            </div>

            <textarea
              value={inputText}
              onChange={(e) => {
                setInputText(e.target.value);
                setDismissedUrls(new Set());
              }}
              placeholder="Paste URLs here to stage and append to the persistent download queue (one per line, comma-separated, or pasted from Markdown/chat logs)..."
              rows={6}
              className="w-full rounded-lg border border-border bg-background p-3 text-foreground placeholder:text-muted focus:outline-none focus:ring-1 focus:ring-accent font-mono text-xs"
            />

            <p className="text-[11px] text-muted">
              Supports any direct stream, YouTube, JustTheGays, galleries, or hosts supported by installed Cove downloaders. You can append new URLs even while the queue is running or paused.
            </p>
          </div>

          {/* Metadata Tagging Card */}
          <div className="rounded-xl border border-border bg-card p-4 space-y-4">
            <div className="flex items-center justify-between">
              <h2 className="text-sm font-semibold text-foreground flex items-center gap-2">
                <Tag className="w-4 h-4 text-accent" />
                Initial Metadata Tagging
              </h2>
              <span className="text-xs text-muted">Applied to staged items when added to queue</span>
            </div>

            <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
              {/* Studio */}
              <div className="space-y-1.5">
                <label className="text-xs font-medium text-foreground">Studio</label>
                <EntityReferenceSelector
                  entityType="studio"
                  value={selectedStudioId}
                  onChange={(val) => setSelectedStudioId(val)}
                  placeholder="Select studio..."
                />
              </div>

              {/* Group / Collection */}
              <div className="space-y-1.5">
                <label className="text-xs font-medium text-foreground">Group / Series</label>
                <EntityReferenceSelector
                  entityType="group"
                  value={selectedGroupId}
                  onChange={(val) => setSelectedGroupId(val)}
                  placeholder="Add to group..."
                />
              </div>

              {/* Entity Type Override */}
              <div className="space-y-1.5">
                <label className="text-xs font-medium text-foreground">Entity Type</label>
                <select
                  value={defaultEntity}
                  onChange={(e) => setDefaultEntity(e.target.value as DownloadEntityType)}
                  className="w-full rounded-lg border border-border bg-background px-3 py-2 text-sm text-foreground focus:outline-none focus:ring-1 focus:ring-accent"
                >
                  <option value="auto">Auto-detect from Downloader</option>
                  <option value="Video">Video</option>
                  <option value="Gallery">Gallery</option>
                  <option value="Image">Image</option>
                  <option value="Audio">Audio</option>
                </select>
              </div>
            </div>

            {/* Tags Multi-Select */}
            <div className="space-y-1.5">
              <label className="text-xs font-medium text-foreground">Tags</label>
              <EntityReferenceMultiSelector
                entityType="tag"
                values={selectedTags}
                onChange={(vals) => setSelectedTags(vals)}
                placeholder="Search or add tags..."
              />
            </div>

            {/* Performers Multi-Select */}
            <div className="space-y-1.5">
              <label className="text-xs font-medium text-foreground">Performers</label>
              <EntityReferenceMultiSelector
                entityType="performer"
                values={selectedPerformers}
                onChange={(vals) => setSelectedPerformers(vals)}
                placeholder="Search or add performers..."
              />
            </div>
          </div>
        </div>

        {/* Right Column: Settings & Generation */}
        <div className="space-y-6">
          <div className="rounded-xl border border-border bg-card p-4 space-y-4">
            <h2 className="text-sm font-semibold text-foreground flex items-center gap-2">
              <Sliders className="w-4 h-4 text-accent" />
              Download & Scraping Settings
            </h2>

            <div className="space-y-3">
              <label className="flex items-start gap-2.5 cursor-pointer">
                <input
                  type="checkbox"
                  checked={autoApplyMetadata}
                  onChange={(e) => setAutoApplyMetadata(e.target.checked)}
                  className="rounded border-border mt-0.5 text-accent focus:ring-accent"
                />
                <div>
                  <span className="text-xs font-medium text-foreground block">Auto-apply metadata</span>
                  <span className="text-[11px] text-muted">Run metadata scrapers after download finishes</span>
                </div>
              </label>

              <label className="flex items-start gap-2.5 cursor-pointer">
                <input
                  type="checkbox"
                  checked={allowDuplicates}
                  onChange={(e) => setAllowDuplicates(e.target.checked)}
                  className="rounded border-border mt-0.5 text-accent focus:ring-accent"
                />
                <div>
                  <span className="text-xs font-medium text-foreground block">Allow duplicate downloads</span>
                  <span className="text-[11px] text-muted">Queue even if URL already exists in library</span>
                </div>
              </label>
            </div>

            <hr className="border-border/60" />

            {/* Post-Download Generation */}
            <div className="space-y-2">
              <button
                type="button"
                onClick={() => setShowAdvancedGen(!showAdvancedGen)}
                className="w-full flex items-center justify-between text-xs font-medium text-foreground hover:text-accent transition-colors cursor-pointer"
              >
                <span className="flex items-center gap-1.5">
                  <Sparkles className="w-3.5 h-3.5 text-accent" />
                  Post-Download Generation
                </span>
                <span className="text-[10px] text-muted">{showAdvancedGen ? "Hide" : "Show"}</span>
              </button>

              {showAdvancedGen && (
                <div className="space-y-2 pt-2 border-t border-border/40">
                  <label className="flex items-center gap-2 text-xs text-foreground cursor-pointer">
                    <input
                      type="checkbox"
                      checked={genThumbnails}
                      onChange={(e) => setGenThumbnails(e.target.checked)}
                      className="rounded border-border text-accent focus:ring-accent"
                    />
                    Generate covers & thumbnails
                  </label>
                  <label className="flex items-center gap-2 text-xs text-foreground cursor-pointer">
                    <input
                      type="checkbox"
                      checked={genPreviews}
                      onChange={(e) => setGenPreviews(e.target.checked)}
                      className="rounded border-border text-accent focus:ring-accent"
                    />
                    Generate video previews
                  </label>
                  <label className="flex items-center gap-2 text-xs text-foreground cursor-pointer">
                    <input
                      type="checkbox"
                      checked={genSprites}
                      onChange={(e) => setGenSprites(e.target.checked)}
                      className="rounded border-border text-accent focus:ring-accent"
                    />
                    Generate scrub sprites
                  </label>
                  <label className="flex items-center gap-2 text-xs text-foreground cursor-pointer">
                    <input
                      type="checkbox"
                      checked={genPhashes}
                      onChange={(e) => setGenPhashes(e.target.checked)}
                      className="rounded border-border text-accent focus:ring-accent"
                    />
                    Compute perceptual hashes
                  </label>
                  <label className="flex items-center gap-2 text-xs text-foreground cursor-pointer">
                    <input
                      type="checkbox"
                      checked={genMd5}
                      onChange={(e) => setGenMd5(e.target.checked)}
                      className="rounded border-border text-accent focus:ring-accent"
                    />
                    Compute MD5 checksums
                  </label>
                </div>
              )}
            </div>
          </div>
        </div>
      </div>

      {/* Staged URL Preflight Inspection Table */}
      {items.length > 0 && (
        <div className="rounded-xl border border-border bg-card overflow-hidden">
          <div className="p-4 border-b border-border flex flex-wrap items-center justify-between gap-2">
            <h3 id="download-staging-heading" className="text-sm font-semibold text-foreground flex items-center gap-2">
              <Film className="w-4 h-4 text-accent" />
              Staged URLs to Add ({items.length})
            </h3>
            <div className="flex items-center gap-2">
              {unsupportedCount > 0 && (
                <button
                  type="button"
                  onClick={removeUnsupported}
                  className="text-xs px-2.5 py-1 rounded-md border border-amber-500/30 bg-amber-500/10 text-amber-400 hover:bg-amber-500/20 transition-colors cursor-pointer"
                >
                  Remove Unsupported ({unsupportedCount})
                </button>
              )}
              {duplicateCount > 0 && (
                <button
                  type="button"
                  onClick={removeDuplicates}
                  className="text-xs px-2.5 py-1 rounded-md border border-border bg-surface text-muted hover:text-foreground transition-colors cursor-pointer"
                >
                  Remove Duplicates ({duplicateCount})
                </button>
              )}
              <span className="text-xs text-muted">Review staged items before adding to queue</span>
            </div>
          </div>

          <div
            tabIndex={0}
            role="region"
            aria-labelledby="download-staging-heading"
            className="overflow-x-auto max-h-[420px] overflow-y-auto"
          >
            <table className="w-full text-left text-xs border-separate border-spacing-0">
              <thead className="sticky top-0 z-10 bg-surface text-muted uppercase text-[10px] tracking-wider border-b border-border">
                <tr>
                  <th scope="col" className="py-2.5 px-3 border-b border-border">Status</th>
                  <th scope="col" className="py-2.5 px-3 border-b border-border">URL / Label</th>
                  <th scope="col" className="py-2.5 px-3 border-b border-border">Downloader</th>
                  <th scope="col" className="py-2.5 px-3 border-b border-border">Entity</th>
                  <th scope="col" className="py-2.5 px-3 border-b border-border">Quality</th>
                  <th scope="col" className="py-2.5 px-3 text-right border-b border-border">Action</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {items.map((item) => (
                  <tr key={item.id} className="hover:bg-surface/30 transition-colors">
                    {/* Status */}
                    <td className="py-2.5 px-3 whitespace-nowrap">
                      {item.status === "inspecting" ? (
                        <Loader2 className="w-4 h-4 animate-spin text-accent" />
                      ) : item.status === "matched" ? (
                        <div className="flex items-center gap-1.5">
                          <CheckCircle2 className="w-4 h-4 text-green-500" />
                          {item.isDuplicate && (
                            <span
                              className="inline-flex items-center gap-1 text-[10px] px-1.5 py-0.5 rounded bg-amber-500/15 text-amber-400 border border-amber-500/30"
                              title={item.duplicateReason || "URL already downloaded in library"}
                            >
                              <CopyCheck className="w-3 h-3" />
                              In Library
                            </span>
                          )}
                        </div>
                      ) : item.status === "unsupported" ? (
                        <span title={item.statusMessage}>
                          <AlertTriangle className="w-4 h-4 text-amber-500" />
                        </span>
                      ) : item.status === "error" ? (
                        <span title={item.statusMessage}>
                          <AlertCircle className="w-4 h-4 text-red-500" />
                        </span>
                      ) : (
                        <span className="w-2 h-2 rounded-full bg-muted block" />
                      )}
                    </td>

                    {/* URL */}
                    <td className="py-2.5 px-3 max-w-xs truncate">
                      <div className="flex items-center gap-1.5">
                        <span className="font-mono text-[11px] text-foreground truncate" title={item.rawUrl}>
                          {item.label || item.rawUrl}
                        </span>
                        <a
                          href={item.rawUrl}
                          target="_blank"
                          rel="noreferrer"
                          className="text-muted hover:text-accent flex-shrink-0"
                        >
                          <ExternalLink className="w-3 h-3" />
                        </a>
                      </div>
                      {item.label && (
                        <span className="font-mono text-[10px] text-muted truncate block" title={item.rawUrl}>
                          {item.rawUrl}
                        </span>
                      )}
                    </td>

                    {/* Downloader */}
                    <td className="py-2.5 px-3 whitespace-nowrap text-muted">
                      {item.downloaderName || (item.status === "matched" ? "Matched" : "—")}
                    </td>

                    {/* Entity */}
                    <td className="py-2.5 px-3 whitespace-nowrap">
                      <select
                        value={item.targetEntity}
                        onChange={(e) => {
                          const val = e.target.value as ParsedUrlItem["targetEntity"];
                          setItems((prev) =>
                            prev.map((i) =>
                              i.rawUrl === item.rawUrl
                                ? { ...i, targetEntity: val, manualEntityOverride: true }
                                : i
                            )
                          );
                        }}
                        className="rounded border border-border bg-background px-2 py-1 text-xs text-foreground focus:outline-none"
                      >
                        <option value="Video">Video</option>
                        <option value="Gallery">Gallery</option>
                        <option value="Image">Image</option>
                        <option value="Audio">Audio</option>
                      </select>
                    </td>

                    {/* Quality */}
                    <td className="py-2.5 px-3 whitespace-nowrap">
                      {item.qualityOptions && item.qualityOptions.length > 0 ? (
                        <select
                          value={item.selectedQualityId ?? ""}
                          onChange={(e) => {
                            const val = e.target.value;
                            setItems((prev) =>
                              prev.map((i) =>
                                i.rawUrl === item.rawUrl ? { ...i, selectedQualityId: val } : i
                              )
                            );
                          }}
                          className="rounded border border-border bg-background px-2 py-1 text-xs text-foreground focus:outline-none"
                        >
                          <option value="">Auto (Max Height)</option>
                          {item.qualityOptions.map((q) => (
                            <option key={q.id} value={q.id}>
                              {q.label}
                            </option>
                          ))}
                        </select>
                      ) : (
                        <span className="text-muted text-[11px]">Auto (Max Height)</span>
                      )}
                    </td>

                    {/* Action */}
                    <td className="py-2.5 px-3 text-right whitespace-nowrap">
                      <button
                        type="button"
                        onClick={() => removeItem(item.rawUrl)}
                        className="text-muted hover:text-red-500 transition-colors p-1 cursor-pointer"
                        title="Remove from staged list"
                        aria-label={`Remove ${item.label || item.rawUrl} from staged list`}
                      >
                        <Trash2 className="w-3.5 h-3.5" />
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  );
}
