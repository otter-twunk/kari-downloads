import React, { useState, useEffect, useMemo, useRef, useCallback } from "@cove/runtime/react";
import { extensionFetch } from "@cove/runtime/api";
import { useQueryClient } from "@cove/runtime/react-query";
import {
  Sparkles,
  Search,
  CheckCircle2,
  AlertCircle,
  AlertTriangle,
  Loader2,
  X,
  Sliders,
  Film,
  Tag,
  Users,
  Calendar,
  Building2,
  Image as ImageIcon,
  Link as LinkIcon,
  Play,
  Pause,
  SkipForward,
  Flag,
  Plus,
  Trash2,
  Minimize2,
  Check,
  RefreshCw,
} from "@cove/runtime/lucide-react";
import {
  AUTO_MATCH_SCRAPER_ID,
  ScraperSummary,
  MetadataBlendConfig,
  BatchItemProgress,
  BatchQueueRunnerState,
  PersistedBatchScraperQueue,
  isInFlightItemStatus,
  mergeQueueVideoIds,
  parseVideoIdsInput,
  fetchVideoDetails,
  processVideoScrape,
  resolveScraperForVideo,
} from "./utils/batchScrapeRunner";

const STORAGE_KEY = "cove.batchScraper.queue.v1";

const DEFAULT_CONFIG: MetadataBlendConfig = {
  selectedScraperId: AUTO_MATCH_SCRAPER_ID,
  fallbackScraperId: "",
  performersMode: "merge",
  createMissingPerformers: true,
  hydratePerformers: true,
  tagsMode: "merge",
  createMissingTags: true,
  titleStrategy: "if_empty",
  studioStrategy: "if_empty",
  createMissingStudio: true,
  dateStrategy: "if_empty",
  detailsStrategy: "if_empty",
  codeStrategy: "if_empty",
  directorStrategy: "if_empty",
  coverStrategy: "if_missing",
  urlsMode: "merge",
  markOrganized: false,
  concurrency: 2,
};

function loadLocalQueue(): PersistedBatchScraperQueue | null {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as PersistedBatchScraperQueue;
    if (!parsed || !Array.isArray(parsed.items)) return null;
    const items = parsed.items.map((item) =>
      isInFlightItemStatus(item.status)
        ? { ...item, status: "pending" as const, statusMessage: "Paused — ready to resume" }
        : item
    );
    const hasPending = items.some((i) => i.status === "pending");
    const queueState: BatchQueueRunnerState =
      parsed.queueState === "running" || parsed.queueState === "paused"
        ? hasPending
          ? "paused"
          : "idle"
        : "idle";
    return {
      queueState,
      finishAfterCurrent: false,
      config: parsed.config ? { ...DEFAULT_CONFIG, ...parsed.config } : DEFAULT_CONFIG,
      items,
      updatedAt: parsed.updatedAt,
    };
  } catch {
    return null;
  }
}

export function BatchScraperModal() {
  const queryClient = useQueryClient();
  const [isOpen, setIsOpen] = useState(false);
  const [scrapers, setScrapers] = useState<ScraperSummary[]>([]);
  const [loadingScrapers, setLoadingScrapers] = useState(false);

  // Wizard / Modal Tab state
  const [activeStep, setActiveStep] = useState<"configure" | "progress">("configure");

  // Blend Configuration
  const [config, setConfig] = useState<MetadataBlendConfig>(DEFAULT_CONFIG);
  const configRef = useRef<MetadataBlendConfig>(DEFAULT_CONFIG);
  useEffect(() => {
    configRef.current = config;
  }, [config]);

  // Persistent Queue State
  const [batchItems, setBatchItemsState] = useState<BatchItemProgress[]>([]);
  const batchItemsRef = useRef<BatchItemProgress[]>([]);

  const [queueState, setQueueStateInternal] = useState<BatchQueueRunnerState>("idle");
  const queueStateRef = useRef<BatchQueueRunnerState>("idle");

  const [finishAfterCurrent, setFinishAfterCurrentState] = useState(false);
  const finishAfterCurrentRef = useRef(false);

  // Manual Add Input & Toast Feedback
  const [manualAddInput, setManualAddInput] = useState("");
  const [queueNotice, setQueueNotice] = useState<string | null>(null);

  // Worker & Per-Item Cancellation Tracking
  const itemAbortControllersRef = useRef<Map<number, AbortController>>(new Map());
  const skipRequestedIdsRef = useRef<Set<number>>(new Set());
  const pauseRequestedRef = useRef(false);
  const claimedIdsRef = useRef<Set<number>>(new Set());
  const activeWorkerCountRef = useRef(0);
  const hydratedRef = useRef(false);
  const syncTimerRef = useRef<number | null>(null);

  const setBatchItems = useCallback(
    (updater: BatchItemProgress[] | ((prev: BatchItemProgress[]) => BatchItemProgress[])) => {
      setBatchItemsState((prev) => {
        const next = typeof updater === "function" ? updater(prev) : updater;
        batchItemsRef.current = next;
        return next;
      });
    },
    []
  );

  const setQueueState = useCallback((next: BatchQueueRunnerState) => {
    queueStateRef.current = next;
    setQueueStateInternal(next);
  }, []);

  const setFinishAfterCurrent = useCallback((next: boolean) => {
    finishAfterCurrentRef.current = next;
    setFinishAfterCurrentState(next);
  }, []);

  // Persist queue state to localStorage + backend IExtensionStore
  const persistQueueSnapshot = useCallback(
    (
      itemsSnapshot: BatchItemProgress[],
      stateSnapshot: BatchQueueRunnerState,
      finishSnapshot: boolean,
      configSnapshot: MetadataBlendConfig
    ) => {
      const payload: PersistedBatchScraperQueue = {
        queueState: stateSnapshot,
        finishAfterCurrent: finishSnapshot,
        config: configSnapshot,
        items: itemsSnapshot,
        updatedAt: new Date().toISOString(),
      };
      try {
        window.localStorage.setItem(STORAGE_KEY, JSON.stringify(payload));
      } catch {
        // ignore storage quota errors
      }

      if (syncTimerRef.current !== null) {
        window.clearTimeout(syncTimerRef.current);
      }
      syncTimerRef.current = window.setTimeout(() => {
        extensionFetch("/api/ext/batch-scraper/queue", {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload),
        }).catch(() => {
          // ignore transient sync errors
        });
      }, 200);
    },
    []
  );

  // Hydrate persistent queue on mount from localStorage and backend store
  useEffect(() => {
    const local = loadLocalQueue();
    if (local) {
      setBatchItems(local.items);
      setQueueState(local.queueState);
      setFinishAfterCurrent(false);
      if (local.config) setConfig(local.config);
    }

    let cancelled = false;
    extensionFetch("/api/ext/batch-scraper/queue?recover=true")
      .then((res) => (res.ok ? res.json() : null))
      .then((remote: PersistedBatchScraperQueue | null) => {
        if (cancelled || !remote || !Array.isArray(remote.items)) return;
        // Use remote state if local had no items or remote is newer
        const localCount = batchItemsRef.current.length;
        if (remote.items.length > 0 || localCount === 0) {
          setBatchItems(remote.items);
          const hasPending = remote.items.some((i) => i.status === "pending");
          const recoveredState: BatchQueueRunnerState =
            remote.queueState === "paused" && hasPending ? "paused" : "idle";
          setQueueState(recoveredState);
          setFinishAfterCurrent(false);
          if (remote.config) {
            setConfig((prev) => ({ ...prev, ...remote.config }));
          }
        }
      })
      .catch(() => {
        // fallback to localStorage
      })
      .finally(() => {
        if (!cancelled) hydratedRef.current = true;
      });

    return () => {
      cancelled = true;
    };
  }, [setBatchItems, setFinishAfterCurrent, setQueueState]);

  // Sync changes after hydration
  useEffect(() => {
    if (!hydratedRef.current) return;
    persistQueueSnapshot(batchItems, queueState, finishAfterCurrent, config);
  }, [batchItems, queueState, finishAfterCurrent, config, persistQueueSnapshot]);

  // Load scrapers whenever modal opens or queue has items
  useEffect(() => {
    if (!isOpen && scrapers.length > 0) return;
    if (!isOpen && batchItems.length === 0) return;

    let cancelled = false;
    setLoadingScrapers(true);

    extensionFetch("/api/system/scrapers")
      .then((res) => (res.ok ? res.json() : []))
      .then((data: ScraperSummary[]) => {
        if (cancelled) return;
        const videoScrapers = data.filter((s) => s.entityType?.toLowerCase() === "video");
        setScrapers(videoScrapers);
        if (videoScrapers.length > 0) {
          setConfig((prev) => {
            const validSelected =
              prev.selectedScraperId === AUTO_MATCH_SCRAPER_ID ||
              videoScrapers.some((s) => s.id === prev.selectedScraperId);
            const validFallback =
              prev.fallbackScraperId === "" ||
              videoScrapers.some((s) => s.id === prev.fallbackScraperId);
            return {
              ...prev,
              selectedScraperId: validSelected ? prev.selectedScraperId : AUTO_MATCH_SCRAPER_ID,
              fallbackScraperId: validFallback ? prev.fallbackScraperId : videoScrapers[0].id,
            };
          });
        }
      })
      .catch((err) => {
        console.error("Failed to load scrapers:", err);
      })
      .finally(() => {
        if (!cancelled) setLoadingScrapers(false);
      });

    return () => {
      cancelled = true;
    };
  }, [isOpen, batchItems.length, scrapers.length]);

  const scrapersRef = useRef<ScraperSummary[]>([]);
  useEffect(() => {
    scrapersRef.current = scrapers;
  }, [scrapers]);

  const updateItem = useCallback(
    (videoId: number, patch: Partial<BatchItemProgress>) => {
      setBatchItems((prev) =>
        prev.map((item) => (item.videoId === videoId ? { ...item, ...patch } : item))
      );
    },
    [setBatchItems]
  );

  // Claim next pending video ID in queue order
  const claimNextPendingVideoId = useCallback((): number | undefined => {
    for (const item of batchItemsRef.current) {
      if (item.status === "pending" && !claimedIdsRef.current.has(item.videoId)) {
        claimedIdsRef.current.add(item.videoId);
        return item.videoId;
      }
    }
    return undefined;
  }, []);

  // Spawn worker loop up to configured concurrency
  const ensureWorkersRunning = useCallback(() => {
    if (queueStateRef.current !== "running") return;
    if (pauseRequestedRef.current || finishAfterCurrentRef.current) return;

    const currentConfig = configRef.current;
    const targetConcurrency = Math.max(1, Math.min(currentConfig.concurrency || 2, 4));
    const needed = targetConcurrency - activeWorkerCountRef.current;
    if (needed <= 0) return;

    let anyAppliedInRun = false;

    const runSingleWorker = async () => {
      activeWorkerCountRef.current += 1;
      try {
        while (
          queueStateRef.current === "running" &&
          !pauseRequestedRef.current &&
          !finishAfterCurrentRef.current
        ) {
          const videoId = claimNextPendingVideoId();
          if (videoId === undefined) break;

          const itemAbort = new AbortController();
          itemAbortControllersRef.current.set(videoId, itemAbort);

          updateItem(videoId, { status: "fetching", statusMessage: "Fetching metadata..." });

          try {
            const video = await fetchVideoDetails(videoId, itemAbort.signal);
            const displayTitle = video.title || video.files?.[0]?.basename || `Video #${videoId}`;
            const targetScraper = resolveScraperForVideo(
              video,
              scrapersRef.current,
              configRef.current
            );

            if (!targetScraper) {
              updateItem(videoId, {
                title: displayTitle,
                filename: video.files?.[0]?.basename || "",
                status: "no_match",
                statusMessage: "No matching URL scraper or fallback configured",
              });
              continue;
            }

            updateItem(videoId, {
              title: displayTitle,
              filename: video.files?.[0]?.basename || "",
              scraperName: targetScraper.name,
              status: "scraping",
              statusMessage: `Querying ${targetScraper.name}...`,
            });

            const { matchedTitle, appliedSummary, scraperName } = await processVideoScrape(
              video,
              targetScraper,
              configRef.current,
              itemAbort.signal,
              (stage, sName) => {
                if (stage === "scraping") {
                  updateItem(videoId, {
                    status: "scraping",
                    scraperName: sName,
                    statusMessage: `Querying ${sName}...`,
                  });
                } else {
                  updateItem(videoId, {
                    status: "applying",
                    scraperName: sName,
                    statusMessage: "Applying metadata...",
                  });
                }
              }
            );

            anyAppliedInRun = true;
            updateItem(videoId, {
              status: "applied",
              statusMessage: "Successfully applied",
              scraperName,
              matchedTitle,
              appliedSummary,
            });
          } catch (err: any) {
            if (itemAbort.signal.aborted) {
              if (skipRequestedIdsRef.current.has(videoId)) {
                skipRequestedIdsRef.current.delete(videoId);
                updateItem(videoId, {
                  status: "skipped",
                  statusMessage: "Skipped by user",
                });
                continue;
              }

              // Pause requested -> return interrupted item to pending so it resumes cleanly
              updateItem(videoId, {
                status: "pending",
                statusMessage: "Paused — ready to resume",
              });
              break;
            }

            const message = err?.message || "Unknown error";
            const isNoMatch =
              message.toLowerCase().includes("no match") ||
              message.toLowerCase().includes("no result") ||
              message.toLowerCase().includes("only supports url") ||
              message.toLowerCase().includes("404");

            updateItem(videoId, {
              status: isNoMatch ? "no_match" : "error",
              statusMessage: message,
            });
          } finally {
            itemAbortControllersRef.current.delete(videoId);
            claimedIdsRef.current.delete(videoId);
          }

          // Check if user toggled "Finish after current job" while this item was running
          if (finishAfterCurrentRef.current) {
            break;
          }
        }
      } finally {
        activeWorkerCountRef.current -= 1;

        if (activeWorkerCountRef.current <= 0) {
          activeWorkerCountRef.current = 0;
          const hasPending = batchItemsRef.current.some((i) => i.status === "pending");

          if (pauseRequestedRef.current) {
            pauseRequestedRef.current = false;
            setFinishAfterCurrent(false);
            setQueueState(hasPending ? "paused" : "idle");
          } else if (finishAfterCurrentRef.current) {
            setFinishAfterCurrent(false);
            setQueueState(hasPending ? "paused" : "idle");
            setQueueNotice(
              hasPending
                ? "Finished current job and paused queue."
                : "Finished current job — queue complete."
            );
          } else if (!hasPending) {
            setQueueState("idle");
          }

          if (anyAppliedInRun) {
            try {
              await queryClient.invalidateQueries({ queryKey: ["videos"] });
            } catch {
              // ignore query invalidation errors
            }
          }
        }
      }
    };

    for (let i = 0; i < needed; i++) {
      void runSingleWorker();
    }
  }, [claimNextPendingVideoId, queryClient, setFinishAfterCurrent, setQueueState, updateItem]);

  // Add video IDs to the persistent queue (used by Cove bulk action AND manual input)
  const addVideosToQueue = useCallback(
    (incomingIds: number[], openModal = true) => {
      const { items: nextItems, addedCount } = mergeQueueVideoIds(
        batchItemsRef.current,
        incomingIds
      );
      setBatchItems(nextItems);

      if (addedCount > 0) {
        setQueueNotice(
          `Added ${addedCount} ${addedCount === 1 ? "video" : "videos"} to the persistent queue.`
        );
      } else if (incomingIds.length > 0) {
        setQueueNotice("Selected videos are already in the queue.");
      }

      if (openModal) {
        setIsOpen(true);
        if (queueStateRef.current === "running" || queueStateRef.current === "paused") {
          setActiveStep("progress");
        }
      }

      // If queue is actively running (and not draining), ensure workers pick up newly added items
      if (queueStateRef.current === "running" && !finishAfterCurrentRef.current) {
        window.setTimeout(() => ensureWorkersRunning(), 0);
      }
    },
    [ensureWorkersRunning, setBatchItems]
  );

  // Listen for the custom open event triggered by Cove bulk action ("Scrape All")
  useEffect(() => {
    const handleOpen = (e: Event) => {
      const custom = e as CustomEvent<{ videoIds?: number[] }>;
      if (custom.detail?.videoIds && custom.detail.videoIds.length > 0) {
        addVideosToQueue(custom.detail.videoIds, true);
      } else {
        setIsOpen(true);
      }
    };

    window.addEventListener("cove:open-batch-scraper", handleOpen);
    return () => window.removeEventListener("cove:open-batch-scraper", handleOpen);
  }, [addVideosToQueue]);

  // Auto-dismiss transient queue notice after 4s
  useEffect(() => {
    if (!queueNotice) return;
    const t = window.setTimeout(() => setQueueNotice(null), 4000);
    return () => window.clearTimeout(t);
  }, [queueNotice]);

  const selectedScraper = useMemo(
    () => scrapers.find((s) => s.id === config.selectedScraperId),
    [scrapers, config.selectedScraperId]
  );

  // Queue Statistics
  const stats = useMemo(() => {
    const total = batchItems.length;
    const pending = batchItems.filter((i) => i.status === "pending").length;
    const applied = batchItems.filter((i) => i.status === "applied").length;
    const skipped = batchItems.filter((i) => i.status === "skipped").length;
    const noMatch = batchItems.filter((i) => i.status === "no_match").length;
    const error = batchItems.filter((i) => i.status === "error").length;
    const inProgress = batchItems.filter((i) => isInFlightItemStatus(i.status)).length;
    const processed = applied + skipped + noMatch + error;
    const progressPercent = total > 0 ? Math.round((processed / total) * 100) : 0;
    return {
      total,
      pending,
      applied,
      skipped,
      noMatch,
      error,
      inProgress,
      processed,
      progressPercent,
    };
  }, [batchItems]);

  const isRunning = queueState === "running";
  const isPaused = queueState === "paused";
  const isCompleted =
    queueState === "idle" && stats.total > 0 && stats.pending === 0 && stats.inProgress === 0;

  // Start or Resume processing pending items in the persistent queue
  const handleStartOrResume = useCallback(() => {
    if (scrapersRef.current.length === 0) return;

    // If no pending items exist, re-queue all items when user clicks Start from Configure tab
    const hasPending = batchItemsRef.current.some((i) => i.status === "pending");
    if (!hasPending && batchItemsRef.current.length > 0) {
      setBatchItems((prev) =>
        prev.map((item) => ({ ...item, status: "pending", statusMessage: undefined }))
      );
    }

    pauseRequestedRef.current = false;
    setFinishAfterCurrent(false);
    setQueueState("running");
    setActiveStep("progress");
    window.setTimeout(() => ensureWorkersRunning(), 0);
  }, [ensureWorkersRunning, setBatchItems, setFinishAfterCurrent, setQueueState]);

  // Pause Queue: immediately pauses active work and returns in-flight items to pending
  const handlePause = useCallback(() => {
    pauseRequestedRef.current = true;
    setFinishAfterCurrent(false);
    setQueueState("paused");

    for (const [videoId, controller] of itemAbortControllersRef.current.entries()) {
      skipRequestedIdsRef.current.delete(videoId);
      controller.abort("pause");
    }

    extensionFetch("/api/ext/batch-scraper/queue/pause", { method: "POST" }).catch(() => {});
  }, [setFinishAfterCurrent, setQueueState]);

  // Toggle "Finish After Current Job": lets in-flight items complete, then pauses before next item
  const handleToggleFinishAfterCurrent = useCallback(() => {
    const nextVal = !finishAfterCurrentRef.current;
    setFinishAfterCurrent(nextVal);
    extensionFetch("/api/ext/batch-scraper/queue/finish-after-current", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled: nextVal }),
    }).catch(() => {});
  }, [setFinishAfterCurrent]);

  // Skip a specific item (either currently running or pending in queue)
  const handleSkipItem = useCallback(
    (videoId: number) => {
      const activeController = itemAbortControllersRef.current.get(videoId);
      if (activeController) {
        skipRequestedIdsRef.current.add(videoId);
        activeController.abort("skip");
      } else {
        updateItem(videoId, {
          status: "skipped",
          statusMessage: "Skipped by user",
        });
      }
      extensionFetch(`/api/ext/batch-scraper/queue/skip/${videoId}`, {
        method: "POST",
      }).catch(() => {});
    },
    [updateItem]
  );

  // Skip currently active item (or first pending item when paused)
  const handleSkipCurrent = useCallback(() => {
    const inFlight = batchItemsRef.current.filter((i) => isInFlightItemStatus(i.status));
    if (inFlight.length > 0) {
      for (const item of inFlight) {
        handleSkipItem(item.videoId);
      }
      return;
    }
    const nextPending = batchItemsRef.current.find((i) => i.status === "pending");
    if (nextPending) {
      handleSkipItem(nextPending.videoId);
    }
  }, [handleSkipItem]);

  // Remove a single item from the queue
  const handleRemoveItem = useCallback(
    (videoId: number) => {
      const activeController = itemAbortControllersRef.current.get(videoId);
      if (activeController) {
        skipRequestedIdsRef.current.add(videoId);
        activeController.abort("skip");
      }
      setBatchItems((prev) => prev.filter((i) => i.videoId !== videoId));
    },
    [setBatchItems]
  );

  // Retry unmatched, failed, or skipped items
  const retryUnmatchedOrFailed = useCallback(() => {
    const hasRetryable = batchItemsRef.current.some(
      (i) => i.status === "error" || i.status === "no_match" || i.status === "skipped"
    );
    if (!hasRetryable) return;

    setBatchItems((prev) =>
      prev.map((item) =>
        item.status === "error" || item.status === "no_match" || item.status === "skipped"
          ? { ...item, status: "pending", statusMessage: "Queued for retry" }
          : item
      )
    );

    pauseRequestedRef.current = false;
    setFinishAfterCurrent(false);
    setQueueState("running");
    window.setTimeout(() => ensureWorkersRunning(), 0);
  }, [ensureWorkersRunning, setBatchItems, setFinishAfterCurrent, setQueueState]);

  // Clear finished items or entire queue
  const handleClearQueue = useCallback(
    (onlyFinished: boolean) => {
      if (!onlyFinished && isRunning) {
        handlePause();
      }
      if (onlyFinished) {
        setBatchItems((prev) =>
          prev.filter((i) => i.status === "pending" || isInFlightItemStatus(i.status))
        );
      } else {
        setBatchItems([]);
        setQueueState("idle");
        setFinishAfterCurrent(false);
      }
      extensionFetch(`/api/ext/batch-scraper/queue?onlyFinished=${onlyFinished}`, {
        method: "DELETE",
      }).catch(() => {});
    },
    [handlePause, isRunning, setBatchItems, setFinishAfterCurrent, setQueueState]
  );

  const handleManualAddSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    const ids = parseVideoIdsInput(manualAddInput);
    if (ids.length === 0) return;
    addVideosToQueue(ids, false);
    setManualAddInput("");
  };

  // Minimize / close modal without losing the persistent queue
  const handleClose = useCallback(() => {
    setIsOpen(false);
  }, []);

  // Dismiss on Escape key
  useEffect(() => {
    if (!isOpen) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.stopPropagation();
        handleClose();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [isOpen, handleClose]);

  // When modal is minimized/closed and persistent queue has items, render floating status pill
  if (!isOpen) {
    if (batchItems.length === 0) return null;

    return (
      <div
        className="fixed bottom-4 right-4 z-[75] flex items-center gap-3 px-4 py-2.5 rounded-xl shadow-xl border border-border bg-card text-xs"
        style={{ pointerEvents: "auto" }}
      >
        <div className="flex items-center gap-2">
          {isRunning ? (
            <Loader2 className="w-4 h-4 text-accent animate-spin" />
          ) : isPaused ? (
            <Pause className="w-4 h-4 text-amber-400" />
          ) : (
            <Sparkles className="w-4 h-4 text-emerald-400" />
          )}
          <div>
            <div className="font-bold text-foreground flex items-center gap-1.5">
              <span>Scrape Queue</span>
              <span className="px-1.5 py-0.2 rounded-full bg-surface text-secondary border border-border text-[10px]">
                {stats.processed}/{stats.total}
              </span>
            </div>
            <div className="text-[10px] text-muted">
              {isRunning
                ? finishAfterCurrent
                  ? "Finishing after current job..."
                  : `${stats.inProgress} active • ${stats.pending} pending`
                : isPaused
                ? `Paused • ${stats.pending} pending`
                : stats.pending > 0
                ? `${stats.pending} pending in queue`
                : `Completed (${stats.applied} applied)`}
            </div>
          </div>
        </div>

        <div className="flex items-center gap-1.5">
          {isRunning ? (
            <>
              <button
                type="button"
                onClick={handlePause}
                className="p-1.5 rounded-lg border border-border bg-surface hover:bg-card-hover text-amber-400 cursor-pointer"
                title="Pause Queue"
              >
                <Pause className="w-3.5 h-3.5" />
              </button>
              <button
                type="button"
                onClick={handleSkipCurrent}
                className="p-1.5 rounded-lg border border-border bg-surface hover:bg-card-hover text-foreground cursor-pointer"
                title="Skip Current Video"
              >
                <SkipForward className="w-3.5 h-3.5" />
              </button>
            </>
          ) : stats.pending > 0 ? (
            <button
              type="button"
              onClick={handleStartOrResume}
              className="p-1.5 rounded-lg bg-accent text-white hover:bg-accent-hover cursor-pointer"
              title="Resume Queue"
            >
              <Play className="w-3.5 h-3.5 fill-current" />
            </button>
          ) : null}

          <button
            type="button"
            onClick={() => setIsOpen(true)}
            className="px-2.5 py-1 rounded-lg bg-surface hover:bg-card-hover border border-border text-foreground font-semibold cursor-pointer"
          >
            Open
          </button>
        </div>
      </div>
    );
  }

  return (
    <div
      className="batch-scraper-backdrop fixed inset-0 z-[80] flex items-center justify-center p-4 overflow-y-auto"
      style={{ pointerEvents: "auto" }}
      onClick={(e) => {
        if (e.target === e.currentTarget) handleClose();
      }}
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="batch-scraper-dialog-title"
        className="batch-scraper-dialog w-full max-w-4xl rounded-xl shadow-2xl flex flex-col max-h-[90vh] overflow-hidden border border-border"
        style={{ pointerEvents: "auto" }}
      >
        {/* Header */}
        <div className="flex items-center justify-between px-6 py-4 border-b border-border bg-card">
          <div className="flex items-center gap-3">
            <div className="p-2 rounded-lg bg-accent/15 text-accent border border-accent/20">
              <Sparkles className="w-5 h-5" />
            </div>
            <div>
              <div className="flex items-center gap-2 flex-wrap">
                <h2 id="batch-scraper-dialog-title" className="text-lg font-bold text-foreground">
                  Batch Video Scraper Queue
                </h2>
                <span className="text-xs px-2.5 py-0.5 rounded-full bg-accent/15 text-accent border border-accent/25 font-semibold">
                  {stats.total} {stats.total === 1 ? "video" : "videos"} in queue
                </span>
                {stats.pending > 0 && (
                  <span className="text-xs px-2 py-0.5 rounded-full bg-surface text-secondary border border-border font-medium">
                    {stats.pending} pending
                  </span>
                )}
                {isPaused && (
                  <span className="text-xs px-2 py-0.5 rounded-full bg-amber-500/15 text-amber-400 border border-amber-500/30 font-semibold">
                    Paused
                  </span>
                )}
                {isRunning && finishAfterCurrent && (
                  <span className="text-xs px-2 py-0.5 rounded-full bg-amber-500/15 text-amber-400 border border-amber-500/30 font-semibold">
                    Finishing after current job...
                  </span>
                )}
              </div>
              <p className="text-xs text-muted mt-0.5">
                Persistent scrape queue — add more videos anytime, pause, skip, or finish after the current job
              </p>
            </div>
          </div>
          <div className="flex items-center gap-1.5">
            <button
              type="button"
              onClick={handleClose}
              className="p-1.5 text-muted hover:text-foreground rounded-lg hover:bg-surface transition-colors cursor-pointer"
              title="Minimize to corner (queue stays active)"
              aria-label="Minimize Batch Video Scraper"
            >
              <Minimize2 className="w-4 h-4" />
            </button>
            <button
              type="button"
              onClick={handleClose}
              className="p-1.5 text-muted hover:text-foreground rounded-lg hover:bg-surface transition-colors cursor-pointer"
              title="Close"
              aria-label="Close Batch Video Scraper"
            >
              <X className="w-5 h-5" />
            </button>
          </div>
        </div>

        {/* Add Videos to Persistent Queue Bar + Notice */}
        <div className="px-6 py-2.5 border-b border-border bg-surface/50 flex flex-wrap items-center justify-between gap-2 text-xs">
          <form onSubmit={handleManualAddSubmit} className="flex items-center gap-2 flex-1 min-w-[260px]">
            <Plus className="w-3.5 h-3.5 text-accent flex-shrink-0" />
            <input
              type="text"
              value={manualAddInput}
              onChange={(e) => setManualAddInput(e.target.value)}
              placeholder="Add video IDs or /videos/123 URLs to queue (e.g. 42, 43)..."
              className="flex-1 px-2.5 py-1 rounded-md border border-border bg-card text-foreground placeholder:text-muted focus:outline-none focus:border-accent text-xs"
            />
            <button
              type="submit"
              disabled={!manualAddInput.trim()}
              className="px-3 py-1 rounded-md bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 font-semibold disabled:opacity-40 cursor-pointer"
            >
              Add to Queue
            </button>
          </form>

          {queueNotice && (
            <span className="text-xs text-accent font-medium">{queueNotice}</span>
          )}
        </div>

        {/* Wizard Steps Tabs */}
        <div className="flex border-b border-border bg-card/50 text-xs px-6 font-semibold">
          <button
            type="button"
            onClick={() => setActiveStep("configure")}
            className={`py-3 px-4 border-b-2 transition-colors flex items-center gap-2 cursor-pointer ${
              activeStep === "configure"
                ? "border-accent text-accent"
                : "border-transparent text-muted hover:text-foreground"
            }`}
          >
            <Sliders className="w-4 h-4" />
            1. Scraper & Metadata Blender
          </button>
          <button
            type="button"
            onClick={() => setActiveStep("progress")}
            className={`py-3 px-4 border-b-2 transition-colors flex items-center gap-2 cursor-pointer ${
              activeStep === "progress"
                ? "border-accent text-accent"
                : "border-transparent text-muted hover:text-foreground"
            }`}
          >
            <Film className="w-4 h-4" />
            2. Persistent Queue & Controls
            <span className="ml-1 text-[11px] px-1.5 py-0.2 rounded-full bg-surface text-secondary border border-border">
              {stats.processed}/{stats.total}
            </span>
          </button>
        </div>

        {/* Content Body */}
        <div className="flex-1 overflow-y-auto p-6 space-y-6">
          {activeStep === "configure" ? (
            <>
              {/* Step 1: Choose Scraper */}
              <div className="space-y-3">
                <label className="text-sm font-semibold text-foreground flex items-center gap-2">
                  <Search className="w-4 h-4 text-accent" />
                  Target Scraper
                </label>
                {loadingScrapers ? (
                  <div className="flex items-center gap-2 text-sm text-muted p-3 bg-card rounded-lg border border-border">
                    <Loader2 className="w-4 h-4 animate-spin text-accent" />
                    Loading installed video scrapers...
                  </div>
                ) : scrapers.length === 0 ? (
                  <div className="p-4 bg-amber-500/10 border border-amber-500/20 text-amber-400 rounded-lg text-sm flex items-start gap-2">
                    <AlertTriangle className="w-4 h-4 mt-0.5 flex-shrink-0" />
                    <span>No video scrapers found. Please install or enable a video scraper extension first.</span>
                  </div>
                ) : (
                  <div className="space-y-2.5">
                    <div className="grid gap-3 md:grid-cols-2">
                      <div>
                        <span className="block text-[11px] text-muted mb-1">Primary Strategy</span>
                        <select
                          value={config.selectedScraperId}
                          onChange={(e) => setConfig({ ...config, selectedScraperId: e.target.value })}
                          className="w-full px-3.5 py-2.5 bg-card border border-border rounded-lg text-sm text-foreground focus:outline-none focus:border-accent cursor-pointer"
                        >
                          <option value={AUTO_MATCH_SCRAPER_ID}>
                            ✨ Auto-Match by Video URL (Recommended)
                          </option>
                          {scrapers.map((s) => (
                            <option key={s.id} value={s.id}>
                              {s.name} ({s.supportedScrapes.join(", ")})
                            </option>
                          ))}
                        </select>
                      </div>

                      {config.selectedScraperId === AUTO_MATCH_SCRAPER_ID && (
                        <div>
                          <span className="block text-[11px] text-muted mb-1">
                            Fallback Scraper (when video has no matching URL)
                          </span>
                          <select
                            value={config.fallbackScraperId ?? ""}
                            onChange={(e) => setConfig({ ...config, fallbackScraperId: e.target.value })}
                            className="w-full px-3.5 py-2.5 bg-card border border-border rounded-lg text-sm text-foreground focus:outline-none focus:border-accent cursor-pointer"
                          >
                            <option value="">None (Skip videos without matching URL)</option>
                            {scrapers.map((s) => (
                              <option key={s.id} value={s.id}>
                                {s.name} ({s.supportedScrapes.join(", ")})
                              </option>
                            ))}
                          </select>
                        </div>
                      )}
                    </div>

                    <div className="text-xs text-muted bg-card/60 p-2.5 rounded-lg border border-border">
                      <span className="font-semibold text-foreground">Matching Strategy: </span>
                      {config.selectedScraperId === AUTO_MATCH_SCRAPER_ID
                        ? "Each video's URLs are matched against all installed scrapers automatically. Videos without a matching URL use the selected fallback scraper."
                        : `Videos with URLs matching ${selectedScraper?.name ?? "the scraper"} scrape directly by URL; others search by cleaned title or filename.`}
                    </div>
                  </div>
                )}
              </div>

              {/* Step 2: Metadata Blender (Source & Strategy Matrix) */}
              <div className="space-y-4">
                <div className="flex items-center justify-between border-b border-border pb-2">
                  <div>
                    <h3 className="text-sm font-bold text-foreground flex items-center gap-2">
                      <Sliders className="w-4 h-4 text-accent" />
                      Metadata Blender (Field Rules)
                    </h3>
                    <p className="text-xs text-muted">
                      Pre-select what scraped metadata to merge into your library and what to keep unchanged
                    </p>
                  </div>
                </div>

                <div className="grid gap-4 md:grid-cols-2">
                  {/* Performers Card */}
                  <div className="batch-scraper-card p-4 rounded-xl border border-border space-y-3">
                    <div className="flex items-center gap-2 text-sm font-semibold text-foreground">
                      <Users className="w-4 h-4 text-accent" />
                      <span>Performers</span>
                    </div>
                    <div className="flex gap-2">
                      {(["merge", "replace", "skip"] as const).map((mode) => {
                        const active = config.performersMode === mode;
                        return (
                          <button
                            key={mode}
                            type="button"
                            onClick={() => setConfig({ ...config, performersMode: mode })}
                            className={`flex-1 py-2 px-3 text-xs font-semibold rounded-lg border transition-all flex items-center justify-center gap-1.5 cursor-pointer ${
                              active ? "batch-scraper-btn-active" : "batch-scraper-btn-inactive"
                            }`}
                          >
                            {active && <Check className="w-3.5 h-3.5" />}
                            {mode === "merge" ? "Merge (Add)" : mode === "replace" ? "Replace" : "Keep (Skip)"}
                          </button>
                        );
                      })}
                    </div>
                    {config.performersMode !== "skip" && (
                      <div className="space-y-2 pt-2 border-t border-border">
                        <label className="flex items-center gap-2 text-xs text-secondary hover:text-foreground cursor-pointer">
                          <input
                            type="checkbox"
                            checked={config.createMissingPerformers}
                            onChange={(e) => setConfig({ ...config, createMissingPerformers: e.target.checked })}
                            className="rounded border-border bg-surface text-accent focus:ring-0 cursor-pointer"
                          />
                          Create missing performers
                        </label>
                        <label className="flex items-center gap-2 text-xs text-secondary hover:text-foreground cursor-pointer">
                          <input
                            type="checkbox"
                            checked={config.hydratePerformers}
                            onChange={(e) => setConfig({ ...config, hydratePerformers: e.target.checked })}
                            className="rounded border-border bg-surface text-accent focus:ring-0 cursor-pointer"
                          />
                          Hydrate performer profiles (aliases, birthdate, images)
                        </label>
                      </div>
                    )}
                  </div>

                  {/* Tags Card */}
                  <div className="batch-scraper-card p-4 rounded-xl border border-border space-y-3">
                    <div className="flex items-center gap-2 text-sm font-semibold text-foreground">
                      <Tag className="w-4 h-4 text-emerald-400" />
                      <span>Tags</span>
                    </div>
                    <div className="flex gap-2">
                      {(["merge", "replace", "skip"] as const).map((mode) => {
                        const active = config.tagsMode === mode;
                        return (
                          <button
                            key={mode}
                            type="button"
                            onClick={() => setConfig({ ...config, tagsMode: mode })}
                            className={`flex-1 py-2 px-3 text-xs font-semibold rounded-lg border transition-all flex items-center justify-center gap-1.5 cursor-pointer ${
                              active ? "batch-scraper-btn-active" : "batch-scraper-btn-inactive"
                            }`}
                          >
                            {active && <Check className="w-3.5 h-3.5" />}
                            {mode === "merge" ? "Merge (Add)" : mode === "replace" ? "Replace" : "Keep (Skip)"}
                          </button>
                        );
                      })}
                    </div>
                    {config.tagsMode !== "skip" && (
                      <div className="pt-2 border-t border-border">
                        <label className="flex items-center gap-2 text-xs text-secondary hover:text-foreground cursor-pointer">
                          <input
                            type="checkbox"
                            checked={config.createMissingTags}
                            onChange={(e) => setConfig({ ...config, createMissingTags: e.target.checked })}
                            className="rounded border-border bg-surface text-accent focus:ring-0 cursor-pointer"
                          />
                          Create missing tags
                        </label>
                      </div>
                    )}
                  </div>

                  {/* Studio Card */}
                  <div className="batch-scraper-card p-4 rounded-xl border border-border space-y-3">
                    <div className="flex items-center gap-2 text-sm font-semibold text-foreground">
                      <Building2 className="w-4 h-4 text-amber-400" />
                      <span>Studio</span>
                    </div>
                    <div className="flex gap-2">
                      {(["if_empty", "overwrite", "skip"] as const).map((mode) => {
                        const active = config.studioStrategy === mode;
                        return (
                          <button
                            key={mode}
                            type="button"
                            onClick={() => setConfig({ ...config, studioStrategy: mode })}
                            className={`flex-1 py-2 px-3 text-xs font-semibold rounded-lg border transition-all flex items-center justify-center gap-1.5 cursor-pointer ${
                              active ? "batch-scraper-btn-active" : "batch-scraper-btn-inactive"
                            }`}
                          >
                            {active && <Check className="w-3.5 h-3.5" />}
                            {mode === "if_empty" ? "If Empty" : mode === "overwrite" ? "Always" : "Skip"}
                          </button>
                        );
                      })}
                    </div>
                    {config.studioStrategy !== "skip" && (
                      <div className="pt-2 border-t border-border">
                        <label className="flex items-center gap-2 text-xs text-secondary hover:text-foreground cursor-pointer">
                          <input
                            type="checkbox"
                            checked={config.createMissingStudio}
                            onChange={(e) => setConfig({ ...config, createMissingStudio: e.target.checked })}
                            className="rounded border-border bg-surface text-accent focus:ring-0 cursor-pointer"
                          />
                          Create missing studio
                        </label>
                      </div>
                    )}
                  </div>

                  {/* Title Card */}
                  <div className="batch-scraper-card p-4 rounded-xl border border-border space-y-3">
                    <div className="flex items-center gap-2 text-sm font-semibold text-foreground">
                      <Film className="w-4 h-4 text-sky-400" />
                      <span>Title</span>
                    </div>
                    <div className="flex gap-2">
                      {(["if_empty", "overwrite", "skip"] as const).map((mode) => {
                        const active = config.titleStrategy === mode;
                        return (
                          <button
                            key={mode}
                            type="button"
                            onClick={() => setConfig({ ...config, titleStrategy: mode })}
                            className={`flex-1 py-2 px-3 text-xs font-semibold rounded-lg border transition-all flex items-center justify-center gap-1.5 cursor-pointer ${
                              active ? "batch-scraper-btn-active" : "batch-scraper-btn-inactive"
                            }`}
                          >
                            {active && <Check className="w-3.5 h-3.5" />}
                            {mode === "if_empty" ? "If Empty" : mode === "overwrite" ? "Always" : "Keep (Skip)"}
                          </button>
                        );
                      })}
                    </div>
                    <p className="text-[11px] text-muted">
                      {config.titleStrategy === "skip"
                        ? "Preserves existing video title without modifications."
                        : config.titleStrategy === "if_empty"
                        ? "Applies scraped title only if current title is blank or raw filename."
                        : "Replaces current title with canonical scraped title."}
                    </p>
                  </div>

                  {/* Release Date, Synopsis, Code & Director */}
                  <div className="batch-scraper-card p-4 rounded-xl border border-border space-y-3">
                    <div className="flex items-center gap-2 text-sm font-semibold text-foreground">
                      <Calendar className="w-4 h-4 text-purple-400" />
                      <span>Date, Synopsis, Code & Director</span>
                    </div>
                    <div className="flex gap-2">
                      {(["if_empty", "overwrite", "skip"] as const).map((mode) => {
                        const active = config.dateStrategy === mode;
                        return (
                          <button
                            key={mode}
                            type="button"
                            onClick={() =>
                              setConfig({
                                ...config,
                                dateStrategy: mode,
                                detailsStrategy: mode,
                                codeStrategy: mode,
                                directorStrategy: mode,
                              })
                            }
                            className={`flex-1 py-2 px-3 text-xs font-semibold rounded-lg border transition-all flex items-center justify-center gap-1.5 cursor-pointer ${
                              active ? "batch-scraper-btn-active" : "batch-scraper-btn-inactive"
                            }`}
                          >
                            {active && <Check className="w-3.5 h-3.5" />}
                            {mode === "if_empty" ? "If Empty" : mode === "overwrite" ? "Always" : "Skip"}
                          </button>
                        );
                      })}
                    </div>
                  </div>

                  {/* URLs & Cover Image */}
                  <div className="batch-scraper-card p-4 rounded-xl border border-border space-y-3">
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-2 text-sm font-semibold text-foreground">
                        <LinkIcon className="w-4 h-4 text-rose-400" />
                        <span>Source URLs</span>
                      </div>
                      <div className="flex gap-2">
                        {(["merge", "skip"] as const).map((mode) => {
                          const active = config.urlsMode === mode;
                          return (
                            <button
                              key={mode}
                              type="button"
                              onClick={() => setConfig({ ...config, urlsMode: mode })}
                              className={`py-1.5 px-3 text-xs font-semibold rounded-lg border transition-all flex items-center justify-center gap-1 cursor-pointer ${
                                active ? "batch-scraper-btn-active" : "batch-scraper-btn-inactive"
                              }`}
                            >
                              {active && <Check className="w-3.5 h-3" />}
                              {mode === "merge" ? "Append" : "Skip"}
                            </button>
                          );
                        })}
                      </div>
                    </div>

                    <div className="flex items-center justify-between pt-2 border-t border-border">
                      <div className="flex items-center gap-2 text-xs font-medium text-foreground">
                        <ImageIcon className="w-3.5 h-3.5 text-muted" />
                        <span>Cover Art</span>
                      </div>
                      <select
                        value={config.coverStrategy}
                        onChange={(e) => setConfig({ ...config, coverStrategy: e.target.value as any })}
                        className="px-2.5 py-1.5 bg-card border border-border rounded-lg text-xs text-foreground focus:outline-none focus:border-accent cursor-pointer"
                      >
                        <option value="skip">Keep Current (Skip)</option>
                        <option value="if_missing">If Missing Cover</option>
                        <option value="overwrite">Overwrite Always</option>
                      </select>
                    </div>
                  </div>
                </div>

                {/* Additional Settings */}
                <div className="batch-scraper-card p-3.5 rounded-xl border border-border flex items-center justify-between flex-wrap gap-4 text-xs">
                  <label className="flex items-center gap-2 text-secondary hover:text-foreground cursor-pointer">
                    <input
                      type="checkbox"
                      checked={config.markOrganized}
                      onChange={(e) => setConfig({ ...config, markOrganized: e.target.checked })}
                      className="rounded border-border bg-surface text-accent focus:ring-0 cursor-pointer"
                    />
                    Mark videos as Organized after successful scrape
                  </label>

                  <div className="flex items-center gap-2">
                    <span className="text-muted">Concurrency:</span>
                    <select
                      value={config.concurrency}
                      onChange={(e) => setConfig({ ...config, concurrency: Number(e.target.value) })}
                      className="px-2.5 py-1 bg-card border border-border rounded text-xs text-foreground cursor-pointer"
                    >
                      <option value="1">1 (Sequential Queue)</option>
                      <option value="2">2 (Balanced)</option>
                      <option value="3">3 (Fast)</option>
                      <option value="4">4 (Turbo)</option>
                    </select>
                  </div>
                </div>
              </div>
            </>
          ) : (
            /* Step 2: Persistent Queue, Controls & Live Progress */
            <div className="space-y-4">
              {/* Overall Progress & Queue Control Header */}
              <div className="batch-scraper-card p-4 rounded-xl border border-border space-y-3">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div className="flex items-center gap-2">
                    {isRunning ? (
                      <Loader2 className="w-5 h-5 text-accent animate-spin" />
                    ) : isPaused ? (
                      <Pause className="w-5 h-5 text-amber-400" />
                    ) : isCompleted ? (
                      <CheckCircle2 className="w-5 h-5 text-emerald-400" />
                    ) : (
                      <Sparkles className="w-5 h-5 text-muted" />
                    )}
                    <div>
                      <span className="font-bold text-foreground block">
                        {isRunning
                          ? finishAfterCurrent
                            ? "Finishing current job before pausing..."
                            : config.selectedScraperId === AUTO_MATCH_SCRAPER_ID
                            ? "Scraping queue (Auto-Match URLs)..."
                            : `Scraping queue with ${selectedScraper?.name ?? "selected scraper"}...`
                          : isPaused
                          ? `Queue Paused (${stats.pending} pending)`
                          : isCompleted
                          ? "Queue Completed"
                          : stats.pending > 0
                          ? `Queue Ready (${stats.pending} pending)`
                          : "Queue Empty"}
                      </span>
                    </div>
                  </div>

                  {/* Queue Control Bar: Pause/Resume, Skip Current, Finish After Current */}
                  <div className="flex flex-wrap items-center gap-2">
                    {isRunning ? (
                      <>
                        <button
                          type="button"
                          onClick={handlePause}
                          className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-semibold rounded-lg border border-amber-500/30 bg-amber-500/15 text-amber-400 hover:bg-amber-500/25 transition-colors cursor-pointer"
                          title="Pause queue immediately and return active item to pending"
                        >
                          <Pause className="w-3.5 h-3.5" />
                          Pause
                        </button>
                        <button
                          type="button"
                          onClick={handleSkipCurrent}
                          className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-semibold rounded-lg border border-border bg-surface hover:bg-card-hover text-foreground transition-colors cursor-pointer"
                          title="Skip the currently running job and advance to the next item"
                        >
                          <SkipForward className="w-3.5 h-3.5" />
                          Skip Current
                        </button>
                        <button
                          type="button"
                          onClick={handleToggleFinishAfterCurrent}
                          className={`inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-semibold rounded-lg border transition-colors cursor-pointer ${
                            finishAfterCurrent
                              ? "border-amber-500 bg-amber-500/20 text-amber-300"
                              : "border-border bg-surface hover:bg-card-hover text-secondary hover:text-foreground"
                          }`}
                          title="Let currently active job(s) finish, then pause the queue automatically"
                        >
                          <Flag className="w-3.5 h-3.5" />
                          {finishAfterCurrent ? "Finishing After Current ✓" : "Finish After Current"}
                        </button>
                      </>
                    ) : (
                      stats.pending > 0 && (
                        <>
                          <button
                            type="button"
                            onClick={handleStartOrResume}
                            className="inline-flex items-center gap-1.5 px-3.5 py-1.5 text-xs font-bold rounded-lg bg-accent hover:bg-accent-hover text-white transition-colors cursor-pointer"
                          >
                            <Play className="w-3.5 h-3.5 fill-current" />
                            {isPaused ? `Resume Queue (${stats.pending})` : `Start Queue (${stats.pending})`}
                          </button>
                          <button
                            type="button"
                            onClick={handleSkipCurrent}
                            className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-semibold rounded-lg border border-border bg-surface hover:bg-card-hover text-foreground transition-colors cursor-pointer"
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

                {/* Progress Bar */}
                <div className="space-y-1">
                  <div className="flex items-center justify-between text-xs text-muted">
                    <span>Overall Progress</span>
                    <span className="font-semibold text-secondary">
                      {stats.processed} / {stats.total} ({stats.progressPercent}%)
                    </span>
                  </div>
                  <div className="w-full h-2.5 bg-surface rounded-full overflow-hidden border border-border">
                    <div
                      className="h-full bg-accent transition-all duration-300 rounded-full"
                      style={{ width: `${stats.progressPercent}%` }}
                    />
                  </div>
                </div>

                {/* Summary Badges & Queue Cleanup Actions */}
                <div className="flex flex-wrap items-center justify-between gap-2 pt-1 text-xs">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="flex items-center gap-1.5 px-2.5 py-1 rounded-md bg-surface text-secondary border border-border font-semibold">
                      {stats.pending} Pending
                    </span>
                    <span className="flex items-center gap-1.5 px-2.5 py-1 rounded-md bg-emerald-500/15 text-emerald-400 border border-emerald-500/25 font-semibold">
                      <CheckCircle2 className="w-3.5 h-3.5" />
                      {stats.applied} Applied
                    </span>
                    {stats.skipped > 0 && (
                      <span className="flex items-center gap-1.5 px-2.5 py-1 rounded-md bg-surface text-muted border border-border font-semibold">
                        <SkipForward className="w-3.5 h-3.5" />
                        {stats.skipped} Skipped
                      </span>
                    )}
                    <span className="flex items-center gap-1.5 px-2.5 py-1 rounded-md bg-amber-500/15 text-amber-400 border border-amber-500/25 font-semibold">
                      <AlertTriangle className="w-3.5 h-3.5" />
                      {stats.noMatch} No Match
                    </span>
                    <span className="flex items-center gap-1.5 px-2.5 py-1 rounded-md bg-red-500/15 text-red-400 border border-red-500/25 font-semibold">
                      <AlertCircle className="w-3.5 h-3.5" />
                      {stats.error} Errors
                    </span>
                  </div>

                  <div className="flex items-center gap-2">
                    {stats.processed > 0 && (
                      <button
                        type="button"
                        onClick={() => handleClearQueue(true)}
                        className="text-xs px-2.5 py-1 rounded-md border border-border bg-surface hover:bg-card-hover text-muted hover:text-foreground transition-colors cursor-pointer"
                      >
                        Clear Finished ({stats.processed})
                      </button>
                    )}
                    {stats.total > 0 && (
                      <button
                        type="button"
                        onClick={() => handleClearQueue(false)}
                        className="text-xs px-2.5 py-1 rounded-md border border-red-500/25 bg-red-500/10 hover:bg-red-500/20 text-red-400 transition-colors cursor-pointer"
                      >
                        Clear All
                      </button>
                    )}
                  </div>
                </div>
              </div>

              {/* Real-time Persistent Queue Table */}
              {batchItems.length === 0 ? (
                <div className="p-8 text-center border border-border rounded-xl bg-card text-muted text-sm">
                  Queue is empty. Select videos in your library and click <strong>Scrape All</strong>, or enter video IDs above to add to the queue.
                </div>
              ) : (
                <div className="border border-border rounded-xl overflow-hidden bg-card">
                  <div className="max-h-96 overflow-y-auto divide-y divide-border">
                    {batchItems.map((item) => {
                      const inFlight = isInFlightItemStatus(item.status);
                      return (
                        <div
                          key={item.videoId}
                          className="px-4 py-3 flex items-center justify-between gap-3 text-xs hover:bg-card-hover transition-colors"
                        >
                          <div className="space-y-0.5 max-w-[60%] min-w-0">
                            <div className="font-semibold text-foreground truncate flex items-center gap-1.5">
                              <Film className="w-3.5 h-3.5 text-muted flex-shrink-0" />
                              <span className="truncate">{item.title}</span>
                              <span className="text-[10px] text-muted font-mono flex-shrink-0">
                                #{item.videoId}
                              </span>
                              {item.scraperName && (
                                <span className="text-[10px] px-1.5 py-0.2 rounded bg-surface text-muted border border-border flex-shrink-0">
                                  {item.scraperName}
                                </span>
                              )}
                            </div>
                            {item.matchedTitle && (
                              <div className="text-[11px] text-muted truncate flex items-center gap-1">
                                <span className="text-secondary">Match:</span>
                                <span className="text-accent font-semibold truncate">{item.matchedTitle}</span>
                              </div>
                            )}
                            {item.statusMessage && item.status !== "applied" && (
                              <div className="text-[11px] text-muted truncate">
                                {item.statusMessage}
                              </div>
                            )}
                            {item.appliedSummary && item.appliedSummary.length > 0 && (
                              <div className="flex flex-wrap gap-1 pt-0.5">
                                {item.appliedSummary.map((badge, i) => (
                                  <span
                                    key={i}
                                    className="text-[10px] px-1.5 py-0.2 rounded bg-surface text-secondary border border-border"
                                  >
                                    {badge}
                                  </span>
                                ))}
                              </div>
                            )}
                          </div>

                          <div className="flex items-center gap-2 flex-shrink-0">
                            {item.status === "pending" && (
                              <span className="px-2 py-0.5 rounded bg-surface text-muted border border-border">
                                Queued
                              </span>
                            )}
                            {inFlight && (
                              <span className="flex items-center gap-1.5 px-2 py-0.5 rounded bg-accent/15 text-accent border border-accent/25 font-semibold">
                                <Loader2 className="w-3 h-3 animate-spin" />
                                {item.statusMessage || "Working"}
                              </span>
                            )}
                            {item.status === "applied" && (
                              <span className="flex items-center gap-1 px-2 py-0.5 rounded bg-emerald-500/15 text-emerald-400 border border-emerald-500/25 font-semibold">
                                <CheckCircle2 className="w-3 h-3" />
                                Applied
                              </span>
                            )}
                            {item.status === "skipped" && (
                              <span className="flex items-center gap-1 px-2 py-0.5 rounded bg-surface text-muted border border-border font-semibold">
                                <SkipForward className="w-3 h-3" />
                                Skipped
                              </span>
                            )}
                            {item.status === "no_match" && (
                              <span
                                className="flex items-center gap-1 px-2 py-0.5 rounded bg-amber-500/15 text-amber-400 border border-amber-500/25 font-semibold"
                                title={item.statusMessage}
                              >
                                <AlertTriangle className="w-3 h-3" />
                                No Match
                              </span>
                            )}
                            {item.status === "error" && (
                              <span
                                className="flex items-center gap-1 px-2 py-0.5 rounded bg-red-500/15 text-red-400 border border-red-500/25 font-semibold"
                                title={item.statusMessage}
                              >
                                <AlertCircle className="w-3 h-3" />
                                Failed
                              </span>
                            )}

                            {/* Per-Item Skip Button for Pending or Running Items */}
                            {(item.status === "pending" || inFlight) && (
                              <button
                                type="button"
                                onClick={() => handleSkipItem(item.videoId)}
                                className="px-2 py-1 rounded border border-border bg-surface hover:bg-card-hover text-muted hover:text-foreground transition-colors flex items-center gap-1 cursor-pointer"
                                title="Skip this video"
                              >
                                <SkipForward className="w-3 h-3" />
                                Skip
                              </button>
                            )}

                            {/* Per-Item Remove Button */}
                            {!inFlight && (
                              <button
                                type="button"
                                onClick={() => handleRemoveItem(item.videoId)}
                                className="p-1 text-muted hover:text-red-400 transition-colors cursor-pointer"
                                title="Remove from queue"
                                aria-label={`Remove video ${item.videoId} from queue`}
                              >
                                <Trash2 className="w-3.5 h-3.5" />
                              </button>
                            )}
                          </div>
                        </div>
                      );
                    })}
                  </div>
                </div>
              )}
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="flex items-center justify-between px-6 py-4 border-t border-border bg-card">
          <div>
            {activeStep === "progress" && (
              <button
                type="button"
                onClick={() => setActiveStep("configure")}
                className="text-xs text-muted hover:text-foreground flex items-center gap-1 cursor-pointer font-medium"
              >
                <Sliders className="w-3.5 h-3.5" />
                Scraper & Blender Settings
              </button>
            )}
          </div>

          <div className="flex items-center gap-3">
            {activeStep === "configure" ? (
              <>
                <button
                  type="button"
                  onClick={handleClose}
                  className="px-4 py-2 text-xs font-semibold text-muted hover:text-foreground transition-colors cursor-pointer"
                >
                  Minimize
                </button>
                <button
                  type="button"
                  onClick={handleStartOrResume}
                  disabled={scrapers.length === 0 || !config.selectedScraperId || stats.total === 0}
                  className="flex items-center gap-2 px-5 py-2 text-xs font-bold bg-accent hover:bg-accent-hover text-white rounded-lg shadow-md transition-all cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  <Play className="w-3.5 h-3.5 fill-current" />
                  {isRunning
                    ? `View Running Queue (${stats.pending} pending)`
                    : isPaused && stats.pending > 0
                    ? `Resume Scrape Queue (${stats.pending})`
                    : `Start Scrape Queue (${stats.pending > 0 ? stats.pending : stats.total})`}
                </button>
              </>
            ) : (
              <>
                {(stats.noMatch > 0 || stats.error > 0 || stats.skipped > 0) && !isRunning && (
                  <button
                    type="button"
                    onClick={retryUnmatchedOrFailed}
                    className="flex items-center gap-1.5 px-4 py-2 text-xs font-semibold border border-border bg-surface hover:bg-card-hover text-foreground rounded-lg transition-colors cursor-pointer"
                  >
                    <RefreshCw className="w-3.5 h-3.5" />
                    Retry Unmatched / Skipped ({stats.noMatch + stats.error + stats.skipped})
                  </button>
                )}
                <button
                  type="button"
                  onClick={handleClose}
                  className="flex items-center gap-1.5 px-5 py-2 text-xs font-bold bg-accent hover:bg-accent-hover text-white rounded-lg transition-colors cursor-pointer"
                >
                  {isRunning ? "Minimize (Keep Running)" : "Done"}
                </button>
              </>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
