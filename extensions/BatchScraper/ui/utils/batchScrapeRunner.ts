import { extensionFetch } from "@cove/runtime/api";
import { cleanVideoFilename } from "./filenameCleaner";

export const AUTO_MATCH_SCRAPER_ID = "__auto__";

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
  fallbackScraperId?: string;
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
  codeStrategy: ScalarStrategy;
  directorStrategy: ScalarStrategy;
  coverStrategy: "skip" | "if_missing" | "overwrite";
  urlsMode: "merge" | "skip";
  markOrganized: boolean;
  concurrency: number;
}

export interface VideoInfo {
  id: number;
  title?: string | null;
  code?: string | null;
  director?: string | null;
  date?: string | null;
  details?: string | null;
  studioId?: number | null;
  studioName?: string | null;
  studio?: { id: number; name: string } | null;
  performers?: Array<{ id: number; name: string }>;
  tags?: Array<{ id: number; name: string }>;
  urls?: string[];
  files?: Array<{ basename: string; path: string }>;
  imagePath?: string | null;
  hasCover?: boolean;
}

export interface BatchItemProgress {
  videoId: number;
  title: string;
  filename: string;
  scraperName?: string;
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
  const res = await extensionFetch(`/api/videos/${videoId}`, { signal });
  if (!res.ok) throw new Error(`Failed to fetch video ${videoId} details (${res.status})`);
  return await res.json();
}

function isCatchAllPattern(pattern: string): boolean {
  let rest = pattern.trim().toLowerCase();
  for (const scheme of ["https://", "http://", "//"]) {
    if (rest.startsWith(scheme)) {
      rest = rest.slice(scheme.length);
      break;
    }
  }
  return [...rest].every((ch) => ch === "*" || ch === "/" || ch === "." || ch === ":");
}

/**
 * Mirrors Cove's ScraperService.UrlMatchesPattern:
 * - Patterns without '*' use case-insensitive substring matching.
 * - Patterns with '*' match ordered non-empty fragments.
 */
export function urlMatchesPattern(url: string, pattern: string): boolean {
  if (!url || !pattern || !pattern.trim()) return false;
  const loweredUrl = url.trim().toLowerCase();
  const loweredPattern = pattern.trim().toLowerCase();
  if (isCatchAllPattern(loweredPattern)) return false;

  if (!loweredPattern.includes("*")) {
    return loweredUrl.includes(loweredPattern);
  }

  const fragments = loweredPattern.split("*").filter((f) => f.length > 0);
  if (fragments.length === 0) return false;

  let index = 0;
  for (const fragment of fragments) {
    const found = loweredUrl.indexOf(fragment, index);
    if (found < 0) return false;
    index = found + fragment.length;
  }
  return true;
}

function normalizeHost(rawUrlOrSite: string): string {
  let trimmed = rawUrlOrSite.trim().toLowerCase();
  if (!trimmed) return "";
  if (!trimmed.includes("://")) {
    trimmed = `https://${trimmed}`;
  }
  try {
    const parsed = new URL(trimmed);
    return parsed.hostname.replace(/^(\*\.|www\.)+/i, "");
  } catch {
    return rawUrlOrSite
      .trim()
      .toLowerCase()
      .replace(/^https?:\/\//, "")
      .replace(/^(\*\.|www\.)+/, "")
      .split(/[/?#*]/)[0];
  }
}

function urlMatchesPreferenceSite(url: string, site: string): boolean {
  const normalizedSite = normalizeHost(site);
  if (!normalizedSite || normalizedSite === "*") return false;
  const host = normalizeHost(url);
  return host.length > 0 && (host === normalizedSite || host.endsWith(`.${normalizedSite}`));
}

/**
 * Checks if a video has any URL matching the scraper's declared URL patterns or preferenceSites.
 */
export function findMatchingScraperUrl(video: VideoInfo, scraper: ScraperSummary): string | undefined {
  if (!video.urls || video.urls.length === 0) {
    return undefined;
  }

  const patterns = scraper.urls ?? [];
  for (const videoUrl of video.urls) {
    if (!videoUrl) continue;
    for (const rawPattern of patterns) {
      if (urlMatchesPattern(videoUrl, rawPattern)) {
        return videoUrl;
      }
    }
    if (scraper.preferenceSites) {
      for (const site of scraper.preferenceSites) {
        if (urlMatchesPreferenceSite(videoUrl, site)) {
          return videoUrl;
        }
      }
    }
  }

  return undefined;
}

/**
 * Resolves the best scraper for a video when Auto-Match mode is selected,
 * ranking URL pattern matches by specificity and falling back to fallbackScraperId.
 */
export function resolveScraperForVideo(
  video: VideoInfo,
  scrapers: ScraperSummary[],
  config: MetadataBlendConfig
): ScraperSummary | undefined {
  if (config.selectedScraperId && config.selectedScraperId !== AUTO_MATCH_SCRAPER_ID) {
    return scrapers.find((s) => s.id === config.selectedScraperId);
  }

  // Rank scrapers that support URL scraping and match one of the video's URLs
  let bestScraper: ScraperSummary | undefined;
  let bestStrength = -1;

  for (const scraper of scrapers) {
    const supportsUrl = scraper.supportedScrapes.some((s) => s.toLowerCase() === "url");
    if (!supportsUrl) continue;

    const matchedUrl = findMatchingScraperUrl(video, scraper);
    if (!matchedUrl) continue;

    const allPatterns = [...(scraper.urls ?? []), ...(scraper.preferenceSites ?? [])];
    let strength = 0;
    for (const p of allPatterns) {
      if (urlMatchesPattern(matchedUrl, p) || urlMatchesPreferenceSite(matchedUrl, p)) {
        strength = Math.max(strength, p.trim().length);
      }
    }

    if (strength > bestStrength) {
      bestStrength = strength;
      bestScraper = scraper;
    }
  }

  if (bestScraper) return bestScraper;

  if (config.fallbackScraperId) {
    return scrapers.find((s) => s.id === config.fallbackScraperId);
  }

  return undefined;
}

/**
 * Determines whether a video's current title is blank or still set to its raw filename.
 */
export function isTitleBlankOrRawFilename(video: VideoInfo): boolean {
  const title = video.title?.trim();
  if (!title) return true;

  const basename = video.files?.[0]?.basename?.trim();
  if (!basename) return false;

  const lowerTitle = title.toLowerCase();
  const lowerBasename = basename.toLowerCase();
  const stem = lowerBasename.replace(/\.[a-z0-9]{2,5}$/i, "");

  return lowerTitle === lowerBasename || lowerTitle === stem;
}

/**
 * Executes scrape and apply pipeline for a single video according to the metadata blend rules.
 */
export async function processVideoScrape(
  video: VideoInfo,
  scraper: ScraperSummary,
  config: MetadataBlendConfig,
  signal?: AbortSignal,
  onStageChange?: (stage: "scraping" | "applying", scraperName: string) => void
): Promise<{ matchedTitle?: string; appliedSummary: string[]; scraperName: string }> {
  // 1. Determine input kind (URL vs Name)
  const supportsUrl = scraper.supportedScrapes.some((s) => s.toLowerCase() === "url");
  const supportsName = scraper.supportedScrapes.some((s) => s.toLowerCase() === "name");
  const matchedUrl = findMatchingScraperUrl(video, scraper);
  const useUrl = Boolean(matchedUrl && supportsUrl);

  if (!useUrl && !supportsName) {
    throw new Error(`No matching URL on video (${scraper.name} only supports URL scraping).`);
  }

  const inputKind = useUrl ? "url" : "name";

  const defaultTitle = video.title && video.title.trim() ? video.title.trim() : "";
  const firstFilename = video.files && video.files[0]?.basename ? video.files[0].basename : "";
  const queryName =
    (!isTitleBlankOrRawFilename(video) && defaultTitle) ||
    cleanVideoFilename(firstFilename) ||
    defaultTitle ||
    `Video ${video.id}`;

  onStageChange?.("scraping", scraper.name);

  // 2. Create Scrape Attempt
  const createAttemptRes = await extensionFetch("/api/scrape-attempts", {
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
  if (
    config.titleStrategy === "overwrite" ||
    (config.titleStrategy === "if_empty" && isTitleBlankOrRawFilename(video))
  ) {
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

  // Scene Code
  if (config.codeStrategy === "overwrite" || (config.codeStrategy === "if_empty" && !video.code)) {
    replaceFields.push("code");
    appliedSummary.push("Code");
  }

  // Director
  if (config.directorStrategy === "overwrite" || (config.directorStrategy === "if_empty" && !video.director)) {
    replaceFields.push("director");
    appliedSummary.push("Director");
  }

  // Cover Image (VideoDto uses imagePath; keep hasCover as fallback)
  const hasExistingCover = Boolean(video.imagePath || video.hasCover);
  if (config.coverStrategy === "overwrite" || (config.coverStrategy === "if_missing" && !hasExistingCover)) {
    replaceFields.push("image");
    appliedSummary.push("Cover");
  }

  // Studio (VideoDto uses studioId / studioName; keep studio object as fallback)
  const hasExistingStudio = Boolean(video.studioId || video.studioName || video.studio);
  const studioMode =
    config.studioStrategy === "overwrite" || (config.studioStrategy === "if_empty" && !hasExistingStudio)
      ? "replace"
      : "skip";

  // Collection Modes
  const collectionModes: Record<string, string> = {
    performers: config.performersMode,
    tags: config.tagsMode,
    studio: studioMode,
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

  const hasAnyCollectionChange = Object.values(collectionModes).some((m) => m !== "skip");
  if (replaceFields.length === 0 && !hasAnyCollectionChange && !config.markOrganized) {
    return {
      matchedTitle,
      appliedSummary: ["Skipped (all fields already populated)"],
      scraperName: scraper.name,
    };
  }

  onStageChange?.("applying", scraper.name);

  // 4. Apply Scrape Result
  // Note: Do NOT pass removedPerformerIds or removedTagIds here. Cove's ScrapeAttemptService
  // natively clears and replaces the collection when collectionModes is "replace", whereas
  // removedPerformerIds / removedTagIds are post-scrape manual removals that strip newly attached matches.
  const applyRes = await extensionFetch(`/api/scrape-attempts/${attempt.id}/apply`, {
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

  return { matchedTitle, appliedSummary, scraperName: scraper.name };
}
