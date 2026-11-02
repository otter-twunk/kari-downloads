import React, { useState, useEffect, useMemo, useRef, useCallback } from "react";
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
  RotateCcw,
} from "lucide-react";
import {
  ScraperSummary,
  MetadataBlendConfig,
  BatchItemProgress,
  VideoInfo,
  fetchVideoDetails,
  processVideoScrape,
} from "./utils/batchScrapeRunner";

export function BatchScraperModal() {
  const [isOpen, setIsOpen] = useState(false);
  const [videoIds, setVideoIds] = useState<number[]>([]);
  const [scrapers, setScrapers] = useState<ScraperSummary[]>([]);
  const [loadingScrapers, setLoadingScrapers] = useState(false);

  // Wizard state
  const [activeStep, setActiveStep] = useState<"configure" | "progress">("configure");

  // Blend Configuration
  const [config, setConfig] = useState<MetadataBlendConfig>({
    selectedScraperId: "",
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
    coverStrategy: "skip",
    urlsMode: "merge",
    markOrganized: false,
    concurrency: 2,
  });

  // Batch progress state
  const [batchItems, setBatchItems] = useState<BatchItemProgress[]>([]);
  const [isRunning, setIsRunning] = useState(false);
  const [isCompleted, setIsCompleted] = useState(false);
  const abortControllerRef = useRef<AbortController | null>(null);

  // Listen for the custom open event triggered by Cove bulk action
  useEffect(() => {
    const handleOpen = (e: Event) => {
      const custom = e as CustomEvent<{ videoIds: number[] }>;
      if (custom.detail?.videoIds && custom.detail.videoIds.length > 0) {
        setVideoIds(custom.detail.videoIds);
        setIsOpen(true);
        setActiveStep("configure");
        setIsRunning(false);
        setIsCompleted(false);

        // Initialize batch item placeholders
        setBatchItems(
          custom.detail.videoIds.map((id) => ({
            videoId: id,
            title: `Video #${id}`,
            filename: "",
            status: "pending",
          }))
        );
      }
    };

    window.addEventListener("cove:open-batch-scraper", handleOpen);
    return () => window.removeEventListener("cove:open-batch-scraper", handleOpen);
  }, []);

  // Fetch registered video scrapers when modal opens
  useEffect(() => {
    if (!isOpen) return;

    let cancelled = false;
    setLoadingScrapers(true);

    fetch("/api/system/scrapers")
      .then((res) => (res.ok ? res.json() : []))
      .then((data: ScraperSummary[]) => {
        if (cancelled) return;
        const videoScrapers = data.filter((s) => s.entityType.toLowerCase() === "video");
        setScrapers(videoScrapers);
        if (videoScrapers.length > 0 && !config.selectedScraperId) {
          setConfig((prev) => ({ ...prev, selectedScraperId: videoScrapers[0].id }));
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
  }, [isOpen]);

  const selectedScraper = useMemo(
    () => scrapers.find((s) => s.id === config.selectedScraperId),
    [scrapers, config.selectedScraperId]
  );

  // Statistics
  const stats = useMemo(() => {
    const total = batchItems.length;
    const applied = batchItems.filter((i) => i.status === "applied").length;
    const noMatch = batchItems.filter((i) => i.status === "no_match").length;
    const error = batchItems.filter((i) => i.status === "error").length;
    const inProgress = batchItems.filter(
      (i) => i.status === "fetching" || i.status === "scraping" || i.status === "applying"
    ).length;
    const processed = applied + noMatch + error;
    const progressPercent = total > 0 ? Math.round((processed / total) * 100) : 0;
    return { total, applied, noMatch, error, inProgress, processed, progressPercent };
  }, [batchItems]);

  // Start the batch process
  const startBatch = useCallback(async () => {
    if (!selectedScraper || batchItems.length === 0) return;

    setActiveStep("progress");
    setIsRunning(true);
    setIsCompleted(false);

    const abortController = new AbortController();
    abortControllerRef.current = abortController;
    const signal = abortController.signal;

    // Concurrency queue
    const queue = [...videoIds];
    const concurrency = Math.max(1, Math.min(config.concurrency, 4));

    const updateItem = (id: number, patch: Partial<BatchItemProgress>) => {
      setBatchItems((prev) =>
        prev.map((item) => (item.videoId === id ? { ...item, ...patch } : item))
      );
    };

    const worker = async () => {
      while (queue.length > 0 && !signal.aborted) {
        const videoId = queue.shift();
        if (videoId == null) break;

        updateItem(videoId, { status: "fetching", statusMessage: "Fetching metadata..." });

        try {
          // Fetch video details
          const video = await fetchVideoDetails(videoId, signal);
          const displayTitle = video.title || video.files?.[0]?.basename || `Video #${videoId}`;
          updateItem(videoId, {
            title: displayTitle,
            filename: video.files?.[0]?.basename || "",
            status: "scraping",
            statusMessage: `Querying ${selectedScraper.name}...`,
          });

          // Run scrape & apply
          updateItem(videoId, { status: "applying", statusMessage: "Applying metadata..." });
          const { matchedTitle, appliedSummary } = await processVideoScrape(
            video,
            selectedScraper,
            config,
            signal
          );

          updateItem(videoId, {
            status: "applied",
            statusMessage: "Successfully applied",
            matchedTitle,
            appliedSummary,
          });
        } catch (err: any) {
          if (signal.aborted) {
            updateItem(videoId, { status: "pending", statusMessage: "Cancelled" });
            return;
          }

          const message = err?.message || "Unknown error";
          const isNoMatch =
            message.toLowerCase().includes("no match") ||
            message.toLowerCase().includes("no result") ||
            message.toLowerCase().includes("404");

          updateItem(videoId, {
            status: isNoMatch ? "no_match" : "error",
            statusMessage: message,
          });
        }
      }
    };

    const workers = Array.from({ length: concurrency }, () => worker());
    await Promise.all(workers);

    setIsRunning(false);
    setIsCompleted(true);
  }, [batchItems.length, config, selectedScraper, videoIds]);

  const handleStop = () => {
    abortControllerRef.current?.abort();
    setIsRunning(false);
  };

  const handleClose = () => {
    if (isRunning) {
      if (!window.confirm("A batch scrape is in progress. Are you sure you want to stop and close?")) {
        return;
      }
      handleStop();
    }
    setIsOpen(false);
  };

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/75 backdrop-blur-sm p-4 overflow-y-auto">
      <div className="w-full max-w-4xl bg-neutral-900 border border-neutral-800 rounded-xl shadow-2xl flex flex-col max-h-[90vh] overflow-hidden text-neutral-200">
        {/* Header */}
        <div className="flex items-center justify-between px-6 py-4 border-b border-neutral-800 bg-neutral-950/60">
          <div className="flex items-center gap-3">
            <div className="p-2 rounded-lg bg-blue-500/10 text-blue-400 border border-blue-500/20">
              <Sparkles className="w-5 h-5" />
            </div>
            <div>
              <div className="flex items-center gap-2">
                <h2 className="text-lg font-semibold text-white">Batch Video Scraper</h2>
                <span className="text-xs px-2 py-0.5 rounded-full bg-blue-500/10 text-blue-400 border border-blue-500/20 font-medium">
                  {videoIds.length} {videoIds.length === 1 ? "video" : "videos"} selected
                </span>
              </div>
              <p className="text-xs text-neutral-400 mt-0.5">
                Scrape multiple videos and customize field-level metadata blending
              </p>
            </div>
          </div>
          <button
            onClick={handleClose}
            className="p-1.5 text-neutral-400 hover:text-white rounded-lg hover:bg-neutral-800 transition-colors"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        {/* Wizard Steps Tabs */}
        <div className="flex border-b border-neutral-800 bg-neutral-950/30 text-xs px-6 font-medium">
          <button
            onClick={() => !isRunning && setActiveStep("configure")}
            disabled={isRunning}
            className={`py-3 px-4 border-b-2 transition-colors flex items-center gap-2 ${
              activeStep === "configure"
                ? "border-blue-500 text-blue-400"
                : "border-transparent text-neutral-400 hover:text-neutral-200"
            }`}
          >
            <Sliders className="w-4 h-4" />
            1. Scraper & Metadata Blender
          </button>
          <button
            onClick={() => setActiveStep("progress")}
            className={`py-3 px-4 border-b-2 transition-colors flex items-center gap-2 ${
              activeStep === "progress"
                ? "border-blue-500 text-blue-400"
                : "border-transparent text-neutral-400 hover:text-neutral-200"
            }`}
          >
            <Film className="w-4 h-4" />
            2. Execution & Progress
            {stats.processed > 0 && (
              <span className="ml-1 text-[11px] px-1.5 py-0.2 rounded-full bg-neutral-800 text-neutral-300">
                {stats.processed}/{stats.total}
              </span>
            )}
          </button>
        </div>

        {/* Content Body */}
        <div className="flex-1 overflow-y-auto p-6 space-y-6">
          {activeStep === "configure" ? (
            <>
              {/* Step 1: Choose Scraper */}
              <div className="space-y-3">
                <label className="text-sm font-semibold text-neutral-200 flex items-center gap-2">
                  <Search className="w-4 h-4 text-blue-400" />
                  Target Scraper
                </label>
                {loadingScrapers ? (
                  <div className="flex items-center gap-2 text-sm text-neutral-400 p-3 bg-neutral-950/40 rounded-lg border border-neutral-800">
                    <Loader2 className="w-4 h-4 animate-spin text-blue-400" />
                    Loading installed video scrapers...
                  </div>
                ) : scrapers.length === 0 ? (
                  <div className="p-4 bg-yellow-500/10 border border-yellow-500/20 text-yellow-300 rounded-lg text-sm flex items-start gap-2">
                    <AlertTriangle className="w-4 h-4 mt-0.5 flex-shrink-0" />
                    <span>No video scrapers found. Please install or enable a video scraper extension first.</span>
                  </div>
                ) : (
                  <div className="grid gap-2 sm:grid-cols-2">
                    <div className="sm:col-span-2">
                      <select
                        value={config.selectedScraperId}
                        onChange={(e) => setConfig({ ...config, selectedScraperId: e.target.value })}
                        className="w-full px-3 py-2.5 bg-neutral-950 border border-neutral-800 rounded-lg text-sm text-neutral-200 focus:outline-none focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
                      >
                        {scrapers.map((s) => (
                          <option key={s.id} value={s.id}>
                            {s.name} ({s.supportedScrapes.join(", ")})
                          </option>
                        ))}
                      </select>
                    </div>
                    {selectedScraper && (
                      <div className="sm:col-span-2 text-xs text-neutral-400 bg-neutral-950/40 p-2.5 rounded border border-neutral-800/80">
                        <span className="font-semibold text-neutral-300">Matching Strategy: </span>
                        Videos with recognized URLs will scrape by URL; all others will search by cleaned title/filename.
                      </div>
                    )}
                  </div>
                )}
              </div>

              {/* Step 2: Metadata Blender (Source & Strategy Matrix) */}
              <div className="space-y-4">
                <div className="flex items-center justify-between border-b border-neutral-800 pb-2">
                  <div>
                    <h3 className="text-sm font-semibold text-neutral-200 flex items-center gap-2">
                      <Sliders className="w-4 h-4 text-purple-400" />
                      Metadata Blender (Field Rules)
                    </h3>
                    <p className="text-xs text-neutral-400">
                      Configure what scraped metadata to merge into your library and what to keep unchanged
                    </p>
                  </div>
                </div>

                <div className="grid gap-4 md:grid-cols-2">
                  {/* Performers Card */}
                  <div className="p-4 bg-neutral-950/40 border border-neutral-800 rounded-xl space-y-3">
                    <div className="flex items-center gap-2 text-sm font-medium text-neutral-200">
                      <Users className="w-4 h-4 text-blue-400" />
                      <span>Performers</span>
                    </div>
                    <div className="flex gap-2">
                      {(["merge", "replace", "skip"] as const).map((mode) => (
                        <button
                          key={mode}
                          type="button"
                          onClick={() => setConfig({ ...config, performersMode: mode })}
                          className={`flex-1 py-1.5 px-3 text-xs font-medium rounded-lg border capitalize transition-colors ${
                            config.performersMode === mode
                              ? "bg-blue-600 text-white border-blue-500 shadow-sm"
                              : "bg-neutral-900 border-neutral-800 text-neutral-400 hover:text-white"
                          }`}
                        >
                          {mode === "merge" ? "Merge (Add)" : mode === "replace" ? "Replace" : "Keep (Skip)"}
                        </button>
                      ))}
                    </div>
                    {config.performersMode !== "skip" && (
                      <div className="space-y-2 pt-1 border-t border-neutral-800/60">
                        <label className="flex items-center gap-2 text-xs text-neutral-300 cursor-pointer">
                          <input
                            type="checkbox"
                            checked={config.createMissingPerformers}
                            onChange={(e) => setConfig({ ...config, createMissingPerformers: e.target.checked })}
                            className="rounded border-neutral-700 bg-neutral-900 text-blue-500 focus:ring-0"
                          />
                          Create missing performers
                        </label>
                        <label className="flex items-center gap-2 text-xs text-neutral-300 cursor-pointer">
                          <input
                            type="checkbox"
                            checked={config.hydratePerformers}
                            onChange={(e) => setConfig({ ...config, hydratePerformers: e.target.checked })}
                            className="rounded border-neutral-700 bg-neutral-900 text-blue-500 focus:ring-0"
                          />
                          Hydrate performer profiles (aliases, birthdate, images)
                        </label>
                      </div>
                    )}
                  </div>

                  {/* Tags Card */}
                  <div className="p-4 bg-neutral-950/40 border border-neutral-800 rounded-xl space-y-3">
                    <div className="flex items-center gap-2 text-sm font-medium text-neutral-200">
                      <Tag className="w-4 h-4 text-emerald-400" />
                      <span>Tags</span>
                    </div>
                    <div className="flex gap-2">
                      {(["merge", "replace", "skip"] as const).map((mode) => (
                        <button
                          key={mode}
                          type="button"
                          onClick={() => setConfig({ ...config, tagsMode: mode })}
                          className={`flex-1 py-1.5 px-3 text-xs font-medium rounded-lg border capitalize transition-colors ${
                            config.tagsMode === mode
                              ? "bg-emerald-600 text-white border-emerald-500 shadow-sm"
                              : "bg-neutral-900 border-neutral-800 text-neutral-400 hover:text-white"
                          }`}
                        >
                          {mode === "merge" ? "Merge (Add)" : mode === "replace" ? "Replace" : "Keep (Skip)"}
                        </button>
                      ))}
                    </div>
                    {config.tagsMode !== "skip" && (
                      <div className="pt-1 border-t border-neutral-800/60">
                        <label className="flex items-center gap-2 text-xs text-neutral-300 cursor-pointer">
                          <input
                            type="checkbox"
                            checked={config.createMissingTags}
                            onChange={(e) => setConfig({ ...config, createMissingTags: e.target.checked })}
                            className="rounded border-neutral-700 bg-neutral-900 text-emerald-500 focus:ring-0"
                          />
                          Create missing tags
                        </label>
                      </div>
                    )}
                  </div>

                  {/* Studio Card */}
                  <div className="p-4 bg-neutral-950/40 border border-neutral-800 rounded-xl space-y-3">
                    <div className="flex items-center gap-2 text-sm font-medium text-neutral-200">
                      <Building2 className="w-4 h-4 text-amber-400" />
                      <span>Studio</span>
                    </div>
                    <div className="flex gap-2">
                      {(["if_empty", "overwrite", "skip"] as const).map((mode) => (
                        <button
                          key={mode}
                          type="button"
                          onClick={() => setConfig({ ...config, studioStrategy: mode })}
                          className={`flex-1 py-1.5 px-3 text-xs font-medium rounded-lg border capitalize transition-colors ${
                            config.studioStrategy === mode
                              ? "bg-amber-600 text-white border-amber-500 shadow-sm"
                              : "bg-neutral-900 border-neutral-800 text-neutral-400 hover:text-white"
                          }`}
                        >
                          {mode === "if_empty" ? "If Empty" : mode === "overwrite" ? "Always" : "Skip"}
                        </button>
                      ))}
                    </div>
                    {config.studioStrategy !== "skip" && (
                      <div className="pt-1 border-t border-neutral-800/60">
                        <label className="flex items-center gap-2 text-xs text-neutral-300 cursor-pointer">
                          <input
                            type="checkbox"
                            checked={config.createMissingStudio}
                            onChange={(e) => setConfig({ ...config, createMissingStudio: e.target.checked })}
                            className="rounded border-neutral-700 bg-neutral-900 text-amber-500 focus:ring-0"
                          />
                          Create missing studio
                        </label>
                      </div>
                    )}
                  </div>

                  {/* Title & Code Card */}
                  <div className="p-4 bg-neutral-950/40 border border-neutral-800 rounded-xl space-y-3">
                    <div className="flex items-center gap-2 text-sm font-medium text-neutral-200">
                      <Film className="w-4 h-4 text-indigo-400" />
                      <span>Title</span>
                    </div>
                    <div className="flex gap-2">
                      {(["if_empty", "overwrite", "skip"] as const).map((mode) => (
                        <button
                          key={mode}
                          type="button"
                          onClick={() => setConfig({ ...config, titleStrategy: mode })}
                          className={`flex-1 py-1.5 px-3 text-xs font-medium rounded-lg border capitalize transition-colors ${
                            config.titleStrategy === mode
                              ? "bg-indigo-600 text-white border-indigo-500 shadow-sm"
                              : "bg-neutral-900 border-neutral-800 text-neutral-400 hover:text-white"
                          }`}
                        >
                          {mode === "if_empty" ? "If Empty" : mode === "overwrite" ? "Always" : "Keep (Skip)"}
                        </button>
                      ))}
                    </div>
                    <p className="text-[11px] text-neutral-400">
                      {config.titleStrategy === "skip"
                        ? "Preserves existing video title without modifications."
                        : config.titleStrategy === "if_empty"
                        ? "Only applies scraped title if current title is blank or raw filename."
                        : "Replaces current title with canonical scraped title."}
                    </p>
                  </div>

                  {/* Release Date & Synopsis */}
                  <div className="p-4 bg-neutral-950/40 border border-neutral-800 rounded-xl space-y-3">
                    <div className="flex items-center gap-2 text-sm font-medium text-neutral-200">
                      <Calendar className="w-4 h-4 text-sky-400" />
                      <span>Date & Synopsis</span>
                    </div>
                    <div className="flex gap-2">
                      {(["if_empty", "overwrite", "skip"] as const).map((mode) => (
                        <button
                          key={mode}
                          type="button"
                          onClick={() =>
                            setConfig({
                              ...config,
                              dateStrategy: mode,
                              detailsStrategy: mode,
                            })
                          }
                          className={`flex-1 py-1.5 px-3 text-xs font-medium rounded-lg border capitalize transition-colors ${
                            config.dateStrategy === mode
                              ? "bg-sky-600 text-white border-sky-500 shadow-sm"
                              : "bg-neutral-900 border-neutral-800 text-neutral-400 hover:text-white"
                          }`}
                        >
                          {mode === "if_empty" ? "If Empty" : mode === "overwrite" ? "Always" : "Skip"}
                        </button>
                      ))}
                    </div>
                  </div>

                  {/* URLs & Cover Image */}
                  <div className="p-4 bg-neutral-950/40 border border-neutral-800 rounded-xl space-y-3">
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-2 text-sm font-medium text-neutral-200">
                        <LinkIcon className="w-4 h-4 text-rose-400" />
                        <span>Source URLs</span>
                      </div>
                      <div className="flex gap-1.5">
                        <button
                          type="button"
                          onClick={() => setConfig({ ...config, urlsMode: "merge" })}
                          className={`py-1 px-2.5 text-xs rounded border transition-colors ${
                            config.urlsMode === "merge"
                              ? "bg-rose-600 text-white border-rose-500"
                              : "bg-neutral-900 border-neutral-800 text-neutral-400"
                          }`}
                        >
                          Append
                        </button>
                        <button
                          type="button"
                          onClick={() => setConfig({ ...config, urlsMode: "skip" })}
                          className={`py-1 px-2.5 text-xs rounded border transition-colors ${
                            config.urlsMode === "skip"
                              ? "bg-rose-600 text-white border-rose-500"
                              : "bg-neutral-900 border-neutral-800 text-neutral-400"
                          }`}
                        >
                          Skip
                        </button>
                      </div>
                    </div>

                    <div className="flex items-center justify-between pt-2 border-t border-neutral-800/60">
                      <div className="flex items-center gap-2 text-xs text-neutral-300">
                        <ImageIcon className="w-3.5 h-3.5 text-neutral-400" />
                        <span>Cover Art</span>
                      </div>
                      <select
                        value={config.coverStrategy}
                        onChange={(e) => setConfig({ ...config, coverStrategy: e.target.value as any })}
                        className="px-2 py-1 bg-neutral-900 border border-neutral-800 rounded text-xs text-neutral-200"
                      >
                        <option value="skip">Keep Current (Skip)</option>
                        <option value="if_missing">If Missing Cover</option>
                        <option value="overwrite">Overwrite Always</option>
                      </select>
                    </div>
                  </div>
                </div>

                {/* Additional Settings */}
                <div className="p-3 bg-neutral-950/40 border border-neutral-800 rounded-xl flex items-center justify-between flex-wrap gap-4 text-xs">
                  <label className="flex items-center gap-2 text-neutral-300 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={config.markOrganized}
                      onChange={(e) => setConfig({ ...config, markOrganized: e.target.checked })}
                      className="rounded border-neutral-700 bg-neutral-900 text-blue-500 focus:ring-0"
                    />
                    Mark videos as Organized after successful scrape
                  </label>

                  <div className="flex items-center gap-2">
                    <span className="text-neutral-400">Concurrency:</span>
                    <select
                      value={config.concurrency}
                      onChange={(e) => setConfig({ ...config, concurrency: Number(e.target.value) })}
                      className="px-2 py-1 bg-neutral-900 border border-neutral-800 rounded text-xs text-neutral-200"
                    >
                      <option value="1">1 (Gentle)</option>
                      <option value="2">2 (Balanced)</option>
                      <option value="3">3 (Fast)</option>
                      <option value="4">4 (Turbo)</option>
                    </select>
                  </div>
                </div>
              </div>
            </>
          ) : (
            /* Step 2: Progress & Logs */
            <div className="space-y-4">
              {/* Overall Progress Header */}
              <div className="p-4 bg-neutral-950/60 border border-neutral-800 rounded-xl space-y-3">
                <div className="flex items-center justify-between">
                  <div className="flex items-center gap-2">
                    {isRunning ? (
                      <Loader2 className="w-5 h-5 text-blue-400 animate-spin" />
                    ) : isCompleted ? (
                      <CheckCircle2 className="w-5 h-5 text-emerald-400" />
                    ) : (
                      <Sparkles className="w-5 h-5 text-neutral-400" />
                    )}
                    <span className="font-semibold text-white">
                      {isRunning
                        ? `Scraping batch with ${selectedScraper?.name}...`
                        : isCompleted
                        ? "Batch Scrape Completed"
                        : "Ready to start"}
                    </span>
                  </div>
                  <span className="text-sm font-semibold text-neutral-300">
                    {stats.processed} / {stats.total} ({stats.progressPercent}%)
                  </span>
                </div>

                {/* Progress Bar */}
                <div className="w-full h-2 bg-neutral-800 rounded-full overflow-hidden">
                  <div
                    className="h-full bg-gradient-to-r from-blue-500 to-emerald-500 transition-all duration-300 rounded-full"
                    style={{ width: `${stats.progressPercent}%` }}
                  />
                </div>

                {/* Summary Badges */}
                <div className="flex items-center gap-3 pt-1 text-xs">
                  <span className="flex items-center gap-1.5 px-2.5 py-1 rounded bg-emerald-500/10 text-emerald-400 border border-emerald-500/20 font-medium">
                    <CheckCircle2 className="w-3.5 h-3.5" />
                    {stats.applied} Applied
                  </span>
                  <span className="flex items-center gap-1.5 px-2.5 py-1 rounded bg-amber-500/10 text-amber-400 border border-amber-500/20 font-medium">
                    <AlertTriangle className="w-3.5 h-3.5" />
                    {stats.noMatch} No Match
                  </span>
                  <span className="flex items-center gap-1.5 px-2.5 py-1 rounded bg-red-500/10 text-red-400 border border-red-500/20 font-medium">
                    <AlertCircle className="w-3.5 h-3.5" />
                    {stats.error} Errors
                  </span>
                </div>
              </div>

              {/* Real-time Table */}
              <div className="border border-neutral-800 rounded-xl overflow-hidden bg-neutral-950/40">
                <div className="max-h-96 overflow-y-auto divide-y divide-neutral-800/80">
                  {batchItems.map((item) => (
                    <div
                      key={item.videoId}
                      className="px-4 py-3 flex items-center justify-between text-xs hover:bg-neutral-900/50 transition-colors"
                    >
                      <div className="space-y-0.5 max-w-[60%]">
                        <div className="font-medium text-neutral-200 truncate flex items-center gap-1.5">
                          <Film className="w-3.5 h-3.5 text-neutral-400 flex-shrink-0" />
                          <span className="truncate">{item.title}</span>
                        </div>
                        {item.matchedTitle && (
                          <div className="text-[11px] text-neutral-400 truncate flex items-center gap-1">
                            <span className="text-neutral-500">Match:</span>
                            <span className="text-emerald-400 font-medium truncate">{item.matchedTitle}</span>
                          </div>
                        )}
                        {item.appliedSummary && item.appliedSummary.length > 0 && (
                          <div className="flex flex-wrap gap-1 pt-0.5">
                            {item.appliedSummary.map((badge, i) => (
                              <span
                                key={i}
                                className="text-[10px] px-1.5 py-0.2 rounded bg-neutral-800 text-neutral-300 border border-neutral-700/60"
                              >
                                {badge}
                              </span>
                            ))}
                          </div>
                        )}
                      </div>

                      <div className="flex items-center gap-2">
                        {item.status === "pending" && (
                          <span className="px-2 py-0.5 rounded bg-neutral-800 text-neutral-400">Queued</span>
                        )}
                        {(item.status === "fetching" || item.status === "scraping" || item.status === "applying") && (
                          <span className="flex items-center gap-1.5 px-2 py-0.5 rounded bg-blue-500/10 text-blue-400 border border-blue-500/20">
                            <Loader2 className="w-3 h-3 animate-spin" />
                            {item.statusMessage || "Working"}
                          </span>
                        )}
                        {item.status === "applied" && (
                          <span className="flex items-center gap-1 px-2 py-0.5 rounded bg-emerald-500/10 text-emerald-400 border border-emerald-500/20 font-medium">
                            <CheckCircle2 className="w-3 h-3" />
                            Applied
                          </span>
                        )}
                        {item.status === "no_match" && (
                          <span className="flex items-center gap-1 px-2 py-0.5 rounded bg-amber-500/10 text-amber-400 border border-amber-500/20">
                            <AlertTriangle className="w-3 h-3" />
                            No Match
                          </span>
                        )}
                        {item.status === "error" && (
                          <span
                            className="flex items-center gap-1 px-2 py-0.5 rounded bg-red-500/10 text-red-400 border border-red-500/20"
                            title={item.statusMessage}
                          >
                            <AlertCircle className="w-3 h-3" />
                            Failed
                          </span>
                        )}
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="flex items-center justify-between px-6 py-4 border-t border-neutral-800 bg-neutral-950/60">
          <div>
            {activeStep === "progress" && !isRunning && !isCompleted && (
              <button
                onClick={() => setActiveStep("configure")}
                className="text-xs text-neutral-400 hover:text-white flex items-center gap-1"
              >
                <Sliders className="w-3.5 h-3.5" />
                Back to settings
              </button>
            )}
          </div>

          <div className="flex items-center gap-3">
            {activeStep === "configure" ? (
              <>
                <button
                  type="button"
                  onClick={handleClose}
                  className="px-4 py-2 text-xs font-medium text-neutral-400 hover:text-white transition-colors"
                >
                  Cancel
                </button>
                <button
                  type="button"
                  onClick={startBatch}
                  disabled={scrapers.length === 0 || !config.selectedScraperId}
                  className="flex items-center gap-2 px-5 py-2 text-xs font-semibold bg-blue-600 hover:bg-blue-500 disabled:opacity-50 text-white rounded-lg shadow-md transition-all"
                >
                  <Play className="w-3.5 h-3.5 fill-current" />
                  Start Batch Scrape ({videoIds.length})
                </button>
              </>
            ) : (
              <>
                {isRunning ? (
                  <button
                    type="button"
                    onClick={handleStop}
                    className="flex items-center gap-1.5 px-4 py-2 text-xs font-semibold bg-red-600 hover:bg-red-500 text-white rounded-lg transition-colors"
                  >
                    Stop Batch
                  </button>
                ) : (
                  <button
                    type="button"
                    onClick={handleClose}
                    className="flex items-center gap-1.5 px-5 py-2 text-xs font-semibold bg-neutral-800 hover:bg-neutral-700 text-white rounded-lg transition-colors"
                  >
                    Close
                  </button>
                )}
              </>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
