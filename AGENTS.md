# AGENTS.md

> Operational contract for autonomous agents (**Antigravity**, **Claude Code**, **Codex**) operating in **`kari-downloads`** (`/Users/kari/dev/kari-downloads`). Read this **before taking any action**.

**Repo one-line:** Multi-extension repository for Kari's Cove server downloaders (`IDownloaderProvider`) and metadata scrapers (`IScraperProvider` and native YAML `scraper-pack` extensions), built against the upstream `yourcove/cove` (`Cove.Sdk` / `Cove.Plugins` / `Cove.Core` `v1.7.0`) contracts.

---

## 1. Mission & Autonomous Extension Creation Protocol

Whenever Kari requests a downloader, a scraper, or both for a target website or media source, execute the following **6-phase autonomous workflow** end-to-end (or delegate to the **`cove-extension-builder`** subagent):

### Phase 1: Target Site Reconnaissance
1. Fetch raw server responses using `curl -sL -A "Mozilla/5.0 ..."` (inspect raw HTML/JSON, **not** client-rendered DevTools DOM).
2. Identify canonical URLs per entity (`Video`, `Image`, `Gallery`, `Audio`, `Text`, `Performer`, `Group`), supported domains/CDN hosts, search endpoints (`https://site/search?q={query}`), and any required headers or age-gate cookies.
3. **For Downloaders**, select the matching archetype (see [SKILL.md](.agents/plugins/cove-extension-builder/skills/cove-downloader-authoring/SKILL.md)):
   - **Direct / HTML-Embedded Stream**: Extract direct media URL from `<meta property="og:video">`, `<video>`, `<audio>`, JSON-LD `contentUrl`, or embedded JSON state and stream via `DownloaderUtilities.DownloadToTempFileAsync`.
   - **Wrapper / Link Diversion (`Divert = true`)**: If a wrapper post links to an external media host (e.g., RedGifs, Imgur, YouTube), return `new DownloaderUrlMatch(DownloaderId, externalUrl, Label: ..., SourceUrl: originalUrl, Divert: true)`.
   - **Multi-Image Gallery ZIP (`DownloaderEntity.Gallery`)**: Download gallery images and pack into a `.zip` in `host.TempDirectory` via `DownloaderUtilities.CreateGalleryZipAsync`, returning `InlineGalleryMetadata`.
   - **External Binary (`yt-dlp` / `ffmpeg`)**: Declare `externalDependencies` in `extension.json`, respect `request.QualityId` and `request.MaxHeight`, and report progress (`0..100`).
4. **For Scrapers**, inspect structured metadata in priority order (see [SKILL.md](.agents/plugins/cove-extension-builder/skills/cove-scraper-authoring/SKILL.md)):
   - `application/ld+json` (`VideoObject`, `Person`, `ImageGallery`) -> OpenGraph/meta (`og:*`, `video:*`) -> embedded JSON state -> semantic XPath/DOM -> JSON API.
   - Choose **Compiled C# `IScraperProvider`** (`--kind scraper`) when URL rewriting in `ByUrl` mode, code extraction, multi-request enrichment, or xUnit HTML/JSON fixture tests are needed.
   - Choose **Native YAML `scraper-pack`** (`--kind scraper-pack`) when static `scrapeXPath` / `scrapeJson` rules suffice (`action: script` and `subScraper` are blocked by Cove).

### Phase 2: Deterministic Scaffolding
Scaffold new extensions from the repo root using `scripts/new-extension.mjs`:

```bash
# Compiled C# Downloader:
node scripts/new-extension.mjs \
  --kind downloader \
  --name <SiteName>Downloader \
  --domain <example.com> \
  --entity <Video|Image|Gallery|Audio|Text> \
  --title "<Site Name> Downloader"

# Compiled C# Scraper:
node scripts/new-extension.mjs \
  --kind scraper \
  --name <SiteName>Scraper \
  --domain <example.com> \
  --entity <Video|Performer|Gallery|Image|Group|Audio|Text> \
  --title "<Site Name> Scraper"

# Native YAML Scraper Pack:
node scripts/new-extension.mjs \
  --kind scraper-pack \
  --name <SiteName>ScraperPack \
  --domain <example.com> \
  --title "<Site Name> Scraper Pack"
```

This automatically generates the project/manifest under `extensions/<Name>/`, links `extensions/Shared/DownloaderUtilities.cs` or `ScraperUtilities.cs`, registers the entry in `extensions/catalog.json` and `MultiExtensionTemplate.slnx`, and creates an xUnit test file in `tests/KariDownloads.Tests/<Name>Tests.cs`.

### Phase 3: Implementation Rules & Invariants
- **`extension.json` is the single source of truth** for `id`, `name`, `version`, `description`, `author`, `url`, `categories`, `minCoveVersion`, `entryDll`, `dependencies`, `permissions`, `settings`, and `externalDependencies`.
- **Never redeclare metadata properties in C#** on `CoveExtensionBase` subclasses — the host injects `extension.json` immediately after construction via `IManifestAware.ApplyManifest`.
- **Naming Conventions**:
  - Extension IDs: `karinauts.downloaders.<slug>` or `karinauts.scrapers.<slug>` (or `karinauts.scrapers.pack.<slug>`).
  - Tag prefixes in `extensions/catalog.json`: lowercase kebab-case ending with `/` (e.g., `site-downloader/`, `site-scraper/`).
- **Progress Reporting**: `IDownloaderHost.ReportProgress(double progress, string? message)` expects `progress` in **`0.0` to `100.0`** (not `0.0` to `1.0`).
- **Date Normalization**: All scraped dates (`ScrapedVideoDto.Date`, `ScrapedPerformerDto.Birthdate`, etc.) must be normalized to ISO **`yyyy-MM-dd`** (use `ScraperUtilities.NormalizeIsoDate`).
- **Studio Fallback**: Always populate a sensible `StudioName` fallback on scraped scenes/galleries so imported items are attributable in Cove.
- **Version Increment on Scraper Changes**: For any changes or fixes made to scrapers, always advance the version in `extension.json` by `0.0.1` (patch bump).
- **Host-Provided Assemblies**: Never explicitly reference or copy-local `Cove.Sdk`, `Cove.Core`, `Cove.Plugins`, `Microsoft.EntityFrameworkCore*`, `Npgsql*`, or `Pgvector*` inside individual extension `.csproj` files — `Directory.Build.targets` wires them compile-only and strips them from publish output.

### Phase 4: Offline Fixture Testing
Every compiled downloader and scraper must have unit tests in `tests/KariDownloads.Tests/<Name>Tests.cs` using `FakeDownloaderHost` (`tests/KariDownloads.Tests/Infrastructure/FakeDownloaderHost.cs`):
- Map sanitized HTML/JSON/binary payloads with `.MapHtml(url, html)`, `.MapJson(url, json)`, and `.MapBytes(url, bytes, contentType)`.
- Test URL matching (`MatchAsync` / `IsSupportedUrl`), full `DownloadAsync` or `Scrape*Async` / `Search*Async` execution, file creation in `host.TempDirectory`, filename sanitization, and extracted metadata fields.

### Phase 5: Mandatory Verification Gate
Before finishing any task, run:

```bash
npm run verify
```

All three stages must pass with **zero errors**:
1. `node scripts/validate-extension-repo.mjs` — validates `extensions/catalog.json`, every `extension.json`, setting/dependency schemas, and version floors.
2. `dotnet test MultiExtensionTemplate.slnx -c Release` — runs all xUnit tests in `KariDownloads.Tests`.
3. `node scripts/package-extension.mjs` — publishes Release builds, verifies zero leaked host DLLs, and packages `artifacts/packages/<id>-<version>.zip`.

---

## 2. Repository Map & Upstream References

```text
kari-downloads/
├── AGENTS.md                        # Primary agent operational contract
├── CLAUDE.md                        # Claude Code pointer & quick reference
├── Directory.Build.props            # .NET 10 + Cove SDK/Core version wiring (1.7.0)
├── Directory.Build.targets          # Compile-only Cove host contract wiring & DLL stripping
├── MultiExtensionTemplate.slnx      # .NET 10 XML solution file (.slnx)
├── extensions/
│   ├── catalog.json                 # Single source of truth for all extensions & tag prefixes
│   ├── Shared/                      # Shared C# helpers linked into extensions
│   │   ├── DownloaderUtilities.cs   # Streaming download, progress, gallery ZIP, HTML/meta helpers
│   │   └── ScraperUtilities.cs      # JSON-LD, OpenGraph, ISO date, code & HTML helpers
│   └── <ExtensionName>/             # Individual extension directories
├── tests/
│   └── KariDownloads.Tests/         # Offline xUnit test suite & FakeDownloaderHost harness
├── scripts/
│   ├── new-extension.mjs            # Deterministic extension & test scaffolder
│   ├── validate-extension-repo.mjs  # Catalog, manifest, and YAML scraper-pack validator
│   └── package-extension.mjs        # Local Release packager & host-DLL leak verifier
└── .agents/plugins/cove-extension-builder/
    ├── plugin.json                  # Workspace plugin manifest
    ├── agents/
    │   └── cove-extension-builder.md
    └── skills/
        ├── cove-downloader-authoring/
        └── cove-scraper-authoring/
```

### Upstream Reference Repositories
- **GitHub Organization**: `https://github.com/yourcove` (`cove`, `communitydownloaders`, `communityscrapers`, `officialextensionregistry`, `docs-site`)
- **Local Read-Only Checkouts** (when running on Kari's Mac):
  - `/Users/kari/cove-dev/upstream/communitydownloaders`
  - `/Users/kari/cove-dev/upstream/communityscrapers`
  - `/Users/kari/cove-dev/forks/cove-scraper-logic`
  - `/Users/kari/dev/karinauts-cove-devops`

---

## 3. Release & Deployment Rules

- **Never push or tag a release without Kari's explicit approval.**
- Release tags follow `<tagPrefix>v< MAJOR.MINOR.PATCH >` (e.g., `my-downloader/v0.1.0`), where `tagPrefix` matches `extensions/catalog.json`.
- Pushing a matching tag triggers `.github/workflows/build.yml` to build, package, and attach `<id>-<version>.zip` to a GitHub Release, which can be installed in Cove via **Extensions › Install from URL**.
