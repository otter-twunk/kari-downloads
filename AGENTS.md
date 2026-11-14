# AGENTS.md

> Operational contract for autonomous agents (**Antigravity**, **Claude Code**, **Codex**) operating in **`kari-downloads`** (`/Users/kari/dev/kari-downloads`). Read this **before taking any action**.

**Repo one-line:** Multi-extension repository for Kari's Cove desktop server plugins, tools, native UI extensions, media downloaders (`IDownloaderProvider`), and metadata scrapers (`IScraperProvider` and native YAML `scraper-pack` extensions), built against upstream `yourcove/cove` (`Cove.Sdk` / `Cove.Plugins` / `Cove.Core` `v1.7.0`) contracts.

---

## 1. Operating Context: Native Desktop Host

Cove runs on Kari's system as a native Windows desktop application (and not a separate custom multi-tenant server):
- The backend runs as an embedded local process exposing endpoints over a local loopback port (`/api/ext/...`).
- Native UI contributions are rendered directly inside the desktop application's WebView via ESM modules (`dist/bundle.js` + `dist/bundle.css`).
- Persistent storage (`IExtensionStore`), database migrations (`IDataExtension`), and file downloads interact directly with the local machine/OS environment.
- Extensions are packaged into `.zip` archives in `artifacts/packages/<id>-<version>.zip` and installed directly in Cove via **Settings › Extensions › Install from URL / File**.

---

## 2. Mission & Autonomous Extension Creation Protocol

Whenever Kari requests a plugin, tool, UI extension, downloader, or scraper, execute the following autonomous workflow end-to-end (or delegate to the **`cove-extension-builder`** subagent):

### Phase 1: Architecture & Feasibility
1. **For Plugins & Tools** (non-downloader/non-scraper, see [cove-plugin-authoring SKILL.md](.agents/plugins/cove-extension-builder/skills/cove-plugin-authoring/SKILL.md)):
   - Identify required host capabilities: UI pages/tabs/slots (`IUIExtension`), custom minimal APIs (`IApiExtension`), key-value state (`IStatefulExtension`, `IExtensionStore`), background jobs (`IJobExtension`), lifecycle events (`IEventExtension`), database models/migrations (`IDataExtension`), actions (`IActionExtension`), or list filters/sorts.
   - For UI contributions, plan native components using `@cove/runtime/components` and `@cove/runtime/api`, styled with Tailwind CSS v4 and Cove CSS variables (`var(--bg-surface)`, `var(--text-primary)`, `var(--border-subtle)`).
2. **For Downloaders** (see [cove-downloader-authoring SKILL.md](.agents/plugins/cove-extension-builder/skills/cove-downloader-authoring/SKILL.md)):
   - Fetch raw server responses (`curl -sL -A "Mozilla/5.0 ..."`).
   - Classify into an archetype: Direct stream, Wrapper diversion (`Divert = true`), Multi-Image Gallery ZIP, or external binary (`yt-dlp`/`ffmpeg`).
3. **For Scrapers** (see [cove-scraper-authoring SKILL.md](.agents/plugins/cove-extension-builder/skills/cove-scraper-authoring/SKILL.md)):
   - Inspect structured metadata in priority order (`application/ld+json` -> OpenGraph -> embedded JSON state -> XPath/DOM -> JSON API).
   - Choose compiled C# `IScraperProvider` or native YAML `scraper-pack`.

### Phase 2: Deterministic Scaffolding
Scaffold new extensions from the repo root using `scripts/new-extension.mjs`:

```bash
# Compiled C# Specialist Plugin / Tool / Native UI:
node scripts/new-extension.mjs \
  --kind plugin \
  --name <PluginName> \
  --title "<Plugin Title>" \
  [--categories "ui,tools"] \
  [--description "Description"]

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

This automatically generates the project/manifest under `extensions/<Name>/`, configures `.csproj` and `extension.json`, registers the entry in `extensions/catalog.json` and `MultiExtensionTemplate.slnx`, and creates an xUnit test file in `tests/KariDownloads.Tests/<Name>Tests.cs`. For plugins with UI, it also scaffolds `ui/<Name>.tsx` and `ui/<Name>Page.tsx`.

### Phase 3: Implementation Rules & Invariants
- **`extension.json` is the single source of truth** for `id`, `name`, `version`, `description`, `author`, `url`, `categories`, `minCoveVersion`, `entryDll`, `dependencies`, `permissions`, `settings`, and `externalDependencies`.
- **Never redeclare metadata properties in C#** on `CoveExtensionBase` subclasses — the host injects `extension.json` immediately after construction via `IManifestAware.ApplyManifest`.
- **Naming Conventions**:
  - Extension IDs: `karinauts.downloaders.<slug>` or `karinauts.scrapers.<slug>` (or `karinauts.scrapers.pack.<slug>`).
  - Tag prefixes in `extensions/catalog.json`: lowercase kebab-case ending with `/` (e.g., `site-downloader/`, `site-scraper/`).
- **Progress Reporting**: `IDownloaderHost.ReportProgress(double progress, string? message)` expects `progress` in **`0.0` to `100.0`** (not `0.0` to `1.0`).
- **Date Normalization**: All scraped dates (`ScrapedVideoDto.Date`, `ScrapedPerformerDto.Birthdate`, etc.) must be normalized to ISO **`yyyy-MM-dd`** (use `ScraperUtilities.NormalizeIsoDate`).
- **Studio Fallback**: Always populate a sensible `StudioName` fallback on scraped scenes/galleries so imported items are attributable in Cove.
- **Patch Version Bump on Every Release**: All extensions (downloaders, scrapers, UI, etc.) must receive a patch bump (`0.0.1`, e.g. `0.1.0` -> `0.1.1`) in `extension.json` whenever changes are made and a new release is created.
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
    ├── docs/
    │   ├── upstream-sync-guide.md
    │   └── release-distribution-guide.md
    ├── templates/
    │   └── github-workflows/build.yml # Multi-extension CI/CD & rolling latest workflow template
    └── skills/
        ├── cove-plugin-authoring/   # Non-downloader/non-scraper plugins, native UI, APIs, jobs
        ├── cove-downloader-authoring/
        └── cove-scraper-authoring/
```

### Upstream Reference Repositories
- **GitHub Organization**: `https://github.com/yourcove` (`cove`, `docs-site`, `cove-extensions-ui`, `AI.Extensions`, `recommendations`, `communitydownloaders`, `communityscrapers`, `officialextensionregistry`)
- **Local Read-Only Checkouts** (when running on Kari's workstation):
  - `/Users/kari/cove-dev/upstream/docs-site` (official Cove developer documentation)
  - `/Users/kari/cove-dev/upstream/cove` (`Cove.Sdk`, `Cove.Plugins`, `Cove.Core` `v1.7.0`)
  - `/Users/kari/cove-dev/upstream/cove-extensions-ui` (official UI extensions: `Audios`, `CustomHomePage`, `SceneAnalytics`)
  - `/Users/kari/cove-dev/upstream/AI.Extensions` (official AI, background jobs & embedding extensions)
  - `/Users/kari/cove-dev/upstream/recommendations` (official recommendation engines & custom list filters/sorts)
  - `/Users/kari/cove-dev/upstream/communitydownloaders`
  - `/Users/kari/cove-dev/upstream/communityscrapers`
  - `/Users/kari/cove-dev/forks/cove-scraper-logic`
  - `/Users/kari/dev/karinauts-cove-devops`

---

## 3. Release & Deployment Rules

- **Never push or tag a release without Kari's explicit approval.**
- **Patch Version Bump on Every Release**: Every modified extension (downloaders, scrapers, UI, etc.) must receive a patch bump (`0.0.1`) in `extension.json` when a new release is prepared.
- Release tags follow `<tagPrefix>v< MAJOR.MINOR.PATCH >` (e.g., `my-downloader/v0.1.1`), where `tagPrefix` matches `extensions/catalog.json` and the version matches the bumped `version` in `extension.json`.
- Pushing a matching tag triggers `.github/workflows/build.yml` to build, package, and attach `<id>-<version>.zip` to a GitHub Release, which can be installed in Cove via **Extensions › Install from URL**.
- **Static Rolling-Release URLs & `latest-manifest.json` Feed**:
  - Cove's `POST /api/extensions/install-from-url` expects one `extension.json` per ZIP archive and does not track direct URLs for future updates.
  - Alongside versioned releases, `.github/workflows/build.yml` maintains a rolling `latest` release (`--latest=false` so it never overrides the repository's primary release) providing:
    - **Per-extension static ZIP URL**: `https://github.com/otter-twunk/kari-downloads/releases/download/latest/<extensionId>-latest.zip`
    - **Repo-wide static manifest feed URL**: `https://github.com/otter-twunk/kari-downloads/releases/download/latest/latest-manifest.json`
  - In-app updaters (such as `ExtensionUpdater`) query `latest-manifest.json`, compare versions against installed extensions (`GET /api/extensions`), and trigger one-click installs/updates without requiring replacement of Cove's primary `ExtensionRegistryBaseUrl`.
