/**
 * Cleans filenames by stripping video quality, codecs, release groups, and bracketed tags
 * to leave a clean title/query for scraper search.
 */
export function cleanVideoFilename(filename: string): string {
  if (!filename) return "";

  let name = filename;

  // Remove file extension
  name = name.replace(/\.[a-zA-Z0-9]{2,4}$/, "");

  // Replace underscores and dots with spaces (except when part of numbers/codes)
  name = name.replace(/[._]/g, " ");

  // Remove standard bracketed tags like [1080p], [SiteName], (2023), {Group}
  name = name.replace(/\[[^\]]*\]/g, " ");
  name = name.replace(/\([^)]*\)/g, " ");
  name = name.replace(/\{[^}]*\}/g, " ");

  // Remove common video resolution and encoding markers
  const junkPatterns = [
    /\b(2160p|1440p|1080p|720p|480p|360p|4k|8k|uhd|fhd|hd)\b/gi,
    /\b(x264|x265|h264|h265|hevc|avc|av1|xvid|divx|aac|ac3|mp3|dts|web-?dl|webrip|bluray|dvdrip|remux)\b/gi,
    /\b(xxx|sd|mp4|mkv|wmv|avi|mov)\b/gi,
  ];

  for (const pattern of junkPatterns) {
    name = name.replace(pattern, " ");
  }

  // Collapse multiple spaces and trim
  return name.replace(/\s+/g, " ").trim();
}
