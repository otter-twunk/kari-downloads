---
name: cove-downloader-authoring
description: >-
  Autonomous workflow and C# SDK reference for creating, testing, and packaging
  Cove downloader extensions (IDownloaderProvider) in kari-downloads. Activate
  this skill whenever the user asks to create, modify, debug, or package a
  downloader for a website or media host for their Cove server.
---

# Cove Downloader Authoring Skill

Use this skill to autonomously design, scaffold, implement, test, and package a Cove downloader extension (`IDownloaderProvider`) in `/Users/kari/dev/kari-downloads`.

For full C# contract signatures (`Cove.Plugins` / `Cove.Sdk` `v1.7.0`) and real-world patterns from `https://github.com/yourcove/communitydownloaders`, read [references/downloader-contracts.md](references/downloader-contracts.md).

---

## Autonomous 6-Step Workflow

### Step 1: Target Site Reconnaissance

Before writing code, inspect how the target site serves media:
1. **URL Anatomy**: Identify canonical item URLs (e.g., `https://site.com/watch/123`, `https://site.com/gallery/abc`) and supported domains/subdomains.
2. **Media Delivery Mechanism** (pick one of the 4 Cove downloader archetypes):
   - **Archetype A — Direct / HTML-Embedded Media Stream**: The page HTML (`og:video`, `<video src>`, `<audio src>`, JSON-LD `contentUrl`, or inline JSON state) exposes a direct `.mp4`, `.m4a`, `.mp3`, `.jpg`, `.epub`, or `.pdf` URL. Use `DownloaderUtilities.DownloadToTempFileAsync`.
   - **Archetype B — Wrapper / Link Diversion (`Divert = true`)**: The URL is a wrapper post (like a Reddit post or aggregator link) that points to an external media host (e.g., RedGifs, Imgur, or a `yt-dlp`-supported site). Return `new DownloaderUrlMatch(DownloaderId, externalUrl, Label: ..., SourceUrl: originalPostUrl, Divert: true)` from `MatchAsync` / `MatchAllAsync` so Cove's `DownloaderService` diverts to the target host's downloader while preserving `SourceUrl`.
   - **Archetype C — Multi-Image Gallery ZIP (`DownloaderEntity.Gallery`)**: The page contains a gallery of images. Download the images and pack them into a `.zip` in `host.TempDirectory` using `DownloaderUtilities.CreateGalleryZipAsync`, returning `InlineGalleryMetadata` (`ScrapedGalleryDto`).
   - **Archetype D — External Binary (`yt-dlp` / `ffmpeg`)**: The site uses complex adaptive streams (HLS `.m3u8` / DASH `.mpd` with separate video and audio tracks). Declare `yt-dlp` or `ffmpeg` in `extension.json` `externalDependencies`, support `request.QualityId` and `request.MaxHeight`, and parse stdout progress into `host.ReportProgress(0..100)`.
3. **Inline Metadata**: Identify where `Title`, `Details`, `Date` (`yyyy-MM-dd`), `StudioName`, `PerformerNames`, and `TagNames` can be extracted during download so `DownloaderResult` can populate `InlineVideoMetadata` / `InlineGalleryMetadata` / `InlineImageMetadata`.

### Step 2: Scaffold the Extension

Run the repository scaffolding script from `/Users/kari/dev/kari-downloads`:

```bash
node scripts/new-extension.mjs \
  --kind downloader \
  --name <SiteName>Downloader \
  --domain <example.com> \
  --entity <Video|Image|Gallery|Audio|Text> \
  --title "<Site Name> Downloader"
```

This creates:
- `extensions/<SiteName>Downloader/extension.json`
- `extensions/<SiteName>Downloader/<SiteName>Downloader.csproj` (with `DownloaderUtilities.cs` linked)
- `extensions/<SiteName>Downloader/<SiteName>DownloaderExtension.cs`
- Entry in `extensions/catalog.json` and `MultiExtensionTemplate.slnx`
- Offline xUnit test file in `tests/KariDownloads.Tests/<SiteName>DownloaderTests.cs`

### Step 3: Implement `IDownloaderProvider`

Edit `extensions/<SiteName>Downloader/<SiteName>DownloaderExtension.cs`:
- Inherit from `CoveExtensionBase, IDownloaderProvider`.
- **Never** redeclare `Id`, `Name`, `Version`, `Description`, `Author`, `Url`, `Categories`, `MinCoveVersion`, or `Dependencies` in C# — `CoveExtensionBase` reads them from `extension.json`.
- Capture `IServiceProvider` in `InitializeAsync(IServiceProvider services, CancellationToken ct = default)`.
- If site login credentials may be configured in Cove (`v1.7.0+`), resolve `IDownloaderSiteLoginProvider` via `_services?.GetService<IDownloaderSiteLoginProvider>()?.FindForUrl(url)`.
- In `GetDownloaders()`, return one `DownloaderDescriptor` per entity type (`Video`, `Image`, `Gallery`, `Audio`, `Text`) with accurate `SupportedUrlPatterns` and `DownloaderCapabilities` (`InlineMetadata`, `MultiQuality`, `ResumeSupported`).
- In `MatchAsync` / `MatchAllAsync`:
  - Return `null` (or `[]`) immediately for unsupported hosts/paths.
  - Populate `QualityOptions` (`DownloaderQualityOption` with `Id`, `Label`, `Width`, `Height`) if `MultiQuality` is supported.
- In `DownloadAsync(DownloaderRequest request, IDownloaderHost host, CancellationToken ct)`:
  - Report progress on a **`0.0` to `100.0`** scale via `host.ReportProgress(percent, message)`.
  - Write the downloaded file into `host.TempDirectory` (or use `DownloaderUtilities.DownloadToTempFileAsync` / `CreateGalleryZipAsync`).
  - Return `new DownloaderResult(LocalPath: fileNameOrFullPath, OriginalFilename: sanitizedOriginalName, InlineVideoMetadata: ...)`.

### Step 4: Configure `extension.json`

Verify `extensions/<SiteName>Downloader/extension.json`:
- Include all required domains (including CDN domains used for media streams) in `permissions.network` and `permissions.downloaderRuntime`.
- If the extension uses settings, use **only** `{ "name": "...", "type": "STRING|NUMBER|BOOLEAN", "displayName": "...", "description": "..." }` (never legacy `key`, `label`, `defaultValue`, or `scope`).
- If the extension uses `externalDependencies`, use `required` (boolean) and `configurationKeys` (string array), never legacy `optional` or `settingsKey`.

### Step 5: Write Offline Fixture Tests

In `tests/KariDownloads.Tests/<SiteName>DownloaderTests.cs`:
- Use `FakeDownloaderHost` (`MapHtml`, `MapJson`, `MapBytes`) to test:
  1. `MatchAsync` positive match (and `QualityOptions` / `Divert` if applicable) + negative match for unrelated URLs.
  2. `DownloadAsync` end-to-end file creation in `host.TempDirectory`, `OriginalFilename` sanitization, progress reporting, and `Inline*Metadata` fields.

### Step 6: Validate, Test & Package

Run the full verification pipeline:

```bash
npm run verify
```

Confirm that:
1. `node scripts/validate-extension-repo.mjs` passes.
2. `dotnet test MultiExtensionTemplate.slnx -c Release` passes all xUnit tests.
3. `node scripts/package-extension.mjs` produces `artifacts/packages/<id>-<version>.zip` with `extension.json` at the root and zero leaked host assemblies.
