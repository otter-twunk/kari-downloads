import { cleanVideoFilename } from "./filenameCleaner";

export interface ScraperSummary {
  id: string;
  name: string;
  entityType: string;
  supportedScrapes: string[];
  urls: string[];
  preferenceSites?: string[];
}

export type CollectionStrategy = "merge" | "replace" | "skip";
export type ScalarStrategy = "skip" | "if_empty" | "overwrite";

export interface MetadataBlendConfig {
  selectedScraperId: string;
  performersMode: CollectionStrategy;
  createMissingPerformers: boolean;
  hydratePerformers: boolean;
  tagsMode: CollectionStrategy;
  createMissingTags: boolean;
  titleStrategy: ScalarStrategy;
  studioStrategy: ScalarStrategy;
  createMissingStudio: boolean;
  dateStrategy: ScalarStrategy;
  detailsStrategy: ScalarStrategy;
  coverStrategy: "skip" | "if_missing" | "overwrite";
  urlsMode: "merge" | "skip";
  markOrganized: boolean;
  concurrency: number;
}

export interface VideoInfo {
  id: number;
  title?: string;
  date?: string;
  details?: string;
  studio?: { id: number; name: string };
  performers?: Array<{ id: number; name: string }>;
  tags?: Array<{ id: number; name: string }>;
  urls?: string[];
  files?: Array<{ basename: string; path: string }>;
  hasCover?: boolean;
}

export interface BatchItemProgress {
  videoId: number;
  title: string;
  filename: string;
  status: "pending" | "fetching" | "scraping" | "applying" | "applied" | "no_match" | "error";
  statusMessage?: string;
  matchedTitle?: string;
  appliedSummary?: string[];
  scrapeAttemptId?: string;
}

export interface ScrapeAttemptResponse {
  id: string;
  status: string;
  error?: string;
  candidateResultsJson?: string;
  resultJson?: string;
}

export async function fetchVideoDetails(videoId: number, signal?: AbortSignal): Promise<VideoInfo> {
  const res = await fetch(`/api/videos/${videoId}`, { signal });
  if (!res.ok) throw new Error(`Failed to fetch video ${videoId} details (${res.status})`);
  return await res.json();
}

/**
 * Checks if a video has any URL matching the scraper's declared URL patterns.
 */
export function findMatchingScraperUrl(video: VideoInfo, scraper: ScraperSummary): string | undefined {
  if (!video.urls || video.urls.length === 0 || !scraper.urls || scraper.urls.length === 0) {
    return undefined;
  }

  for (const rawPattern of scraper.urls) {
    // Basic regex conversion from glob pattern (e.g. *.example.com/*)
    const regexPattern = rawPattern
      .replace(/[.+?^${}()|[\]\\]/g, "\\$&")
      .replace(/\*/g, ".*");
    const regex = new RegExp(`^${regexPattern}$`, "i");

    const matchedUrl = video.urls.find((u) => regex.test(u));
    if (matchedUrl) return matchedUrl;
  }

  return undefined;
}

/**
 * Executes scrape and apply pipeline for a single video according to the metadata blend rules.
 */
export async function processVideoScrape(
  video: VideoInfo,
  scraper: ScraperSummary,
  config: MetadataBlendConfig,
  signal?: AbortSignal
): Promise<{ matchedTitle?: string; appliedSummary: string[] }> {
  // 1. Determine input kind (URL vs Name)
  const matchedUrl = findMatchingScraperUrl(video, scraper);
  const useUrl = Boolean(matchedUrl && scraper.supportedScrapes.includes("url"));
  const inputKind = useUrl ? "url" : "name";

  const defaultTitle = video.title && video.title.trim() ? video.title.trim() : "";
  const firstFilename = video.files && video.files[0]?.basename ? video.files[0].basename : "";
  const queryName = defaultTitle || cleanVideoFilename(firstFilename) || `Video ${video.id}`;

  // 2. Create Scrape Attempt
  const createAttemptRes = await fetch("/api/scrape-attempts", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      scraperId: scraper.id,
      entityType: "video",
      entityId: video.id,
      inputKind,
      url: useUrl ? matchedUrl : undefined,
      name: useUrl ? undefined : queryName,
    }),
    signal,
  });

  if (!createAttemptRes.ok) {
    const errData = await createAttemptRes.json().catch(() => ({}));
    throw new Error(errData.error || errData.message || `Scrape attempt failed (${createAttemptRes.status})`);
  }

  const attempt: ScrapeAttemptResponse = await createAttemptRes.json();
  if (attempt.status.toLowerCase() === "failure") {
    throw new Error(attempt.error || "No match found for this query.");
  }

  // Parse result to get matched title
  let matchedTitle: string | undefined;
  let parsedRaw: Record<string, unknown> = {};
  if (attempt.candidateResultsJson) {
    try {
      const candidates = JSON.parse(attempt.candidateResultsJson);
      if (Array.isArray(candidates) && candidates.length > 0) {
        parsedRaw = candidates[0] || {};
        matchedTitle = String(parsedRaw.Title || parsedRaw.Name || "");
      }
    } catch {
      // ignore JSON parse error
    }
  } else if (attempt.resultJson) {
    try {
      parsedRaw = JSON.parse(attempt.resultJson) || {};
      matchedTitle = String(parsedRaw.Title || parsedRaw.Name || "");
    } catch {
      // ignore
    }
  }

  // 3. Construct ApplyVideoScrapeAttemptDto based on MetadataBlendConfig
  const replaceFields: string[] = [];
  const appliedSummary: string[] = [];

  // Title
  if (config.titleStrategy === "overwrite" || (config.titleStrategy === "if_empty" && !video.title)) {
    replaceFields.push("title");
    appliedSummary.push("Title");
  }

  // Date
  if (config.dateStrategy === "overwrite" || (config.dateStrategy === "if_empty" && !video.date)) {
    replaceFields.push("date");
    appliedSummary.push("Date");
  }

  // Details
  if (config.detailsStrategy === "overwrite" || (config.detailsStrategy === "if_empty" && !video.details)) {
    replaceFields.push("details");
    appliedSummary.push("Details");
  }

  // Cover Image
  if (config.coverStrategy === "overwrite" || (config.coverStrategy === "if_missing" && !video.hasCover)) {
    replaceFields.push("image");
    appliedSummary.push("Cover");
  }

  // Collection Modes
  const collectionModes: Record<string, string> = {
    performers: config.performersMode,
    tags: config.tagsMode,
    studio: config.studioStrategy === "overwrite" || (config.studioStrategy === "if_empty" && !video.studio)
      ? "replace"
      : "skip",
    urls: config.urlsMode,
  };

  if (config.performersMode !== "skip") {
    appliedSummary.push(`Performers (${config.performersMode})`);
  }
  if (config.tagsMode !== "skip") {
    appliedSummary.push(`Tags (${config.tagsMode})`);
  }
  if (collectionModes.studio === "replace") {
    appliedSummary.push("Studio");
  }
  if (config.urlsMode === "merge") {
    appliedSummary.push("URL");
  }

  // 4. Apply Scrape Result
  const applyRes = await fetch(`/api/scrape-attempts/${attempt.id}/apply`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      replaceFields,
      collectionModes,
      createMissingTags: config.createMissingTags,
      createMissingPerformers: config.createMissingPerformers,
      createMissingStudio: config.createMissingStudio,
      markOrganized: config.markOrganized,
      hydratePerformers: config.hydratePerformers,
      selectedCandidateIndex: 0,
    }),
    signal,
  });

  if (!applyRes.ok) {
    const applyErr = await applyRes.json().catch(() => ({}));
    throw new Error(applyErr.error || applyErr.message || `Failed to apply metadata (${applyRes.status})`);
  }

  return { matchedTitle, appliedSummary };
}
