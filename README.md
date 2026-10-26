# Kari Downloads — Cove Downloaders & Scrapers Repository

This repository houses custom **downloaders** (`IDownloaderProvider`) and **metadata scrapers** (`IScraperProvider` and native YAML `scraper-pack` extensions) for Kari's [Cove](https://github.com/yourcove/cove) server.

It is equipped with an autonomous agent (**`cove-extension-builder`**), domain skills (`cove-downloader-authoring`, `cove-scraper-authoring`), shared C# utilities, an offline xUnit fixture test harness, and one-command scaffolding and packaging scripts.

---

## Autonomous Agent Usage

Open this repository in **Antigravity** or **Claude Code** and ask for any downloader or scraper:

- *"Create a Cove video downloader and scraper for `<site-or-url>`"*
- *"Build a gallery downloader for `<site>` that packs images into a ZIP with inline metadata"*
- *"Create a metadata scraper for `<site>` supporting URL lookup and search"*

The agent follows [`AGENTS.md`](./AGENTS.md) and the bundled skills in [`.agents/plugins/cove-extension-builder/`](./.agents/plugins/cove-extension-builder/) to:
1. Inspect the target site's raw HTML/JSON, media streams, and structured metadata (`application/ld+json`, OpenGraph, embedded state, XPath, or REST APIs).
2. Scaffold the extension, `extension.json`, `catalog.json` entry, and xUnit test file via [`scripts/new-extension.mjs`](./scripts/new-extension.mjs).
3. Implement the downloader (`IDownloaderProvider`) or scraper (`IScraperProvider` / `scraper-pack`) using [`DownloaderUtilities.cs`](./extensions/Shared/DownloaderUtilities.cs) and [`ScraperUtilities.cs`](./extensions/Shared/ScraperUtilities.cs).
4. Write deterministic offline fixture unit tests in [`tests/KariDownloads.Tests/`](./tests/KariDownloads.Tests/) using [`FakeDownloaderHost`](./tests/KariDownloads.Tests/Infrastructure/FakeDownloaderHost.cs).
5. Run `npm run verify` to validate the repo catalog/manifests, execute all xUnit tests, and package `artifacts/packages/<id>-<version>.zip`.

---

## CLI Commands

```bash
# Scaffold a new compiled C# downloader (+ xUnit test):
node scripts/new-extension.mjs \
  --kind downloader \
  --name MySiteDownloader \
  --domain mysite.com \
  --entity Video \
  --title "MySite Downloader"

# Scaffold a new compiled C# scraper (+ xUnit test):
node scripts/new-extension.mjs \
  --kind scraper \
  --name MySiteScraper \
  --domain mysite.com \
  --entity Video \
  --title "MySite Scraper"

# Scaffold a new native YAML scraper-pack:
node scripts/new-extension.mjs \
  --kind scraper-pack \
  --name MySiteScraperPack \
  --domain mysite.com \
  --title "MySite Scraper Pack"

# Run the full verification pipeline (validate + dotnet test + package .zip):
npm run verify
```

---

## Extension Architecture & Build Wiring

- **Single Source of Truth (`extension.json`)**: Each extension's `extension.json` owns its identity and metadata (`id`, `name`, `version`, `description`, `author`, `url`, `categories`, `minCoveVersion`, `entryDll`, `dependencies`, `permissions`, `settings`, `externalDependencies`). Compiled extensions extend `CoveExtensionBase`, which reads these fields from `extension.json` at load time.
- **Central Host Contract Wiring**: [`Directory.Build.props`](./Directory.Build.props) and [`Directory.Build.targets`](./Directory.Build.targets) reference `Cove.Sdk` and `Cove.Core` (`v1.7.0`) **compile-only** and strip all host-provided assemblies (`Cove.Sdk`, `Cove.Core`, `Cove.Plugins`, `Microsoft.EntityFrameworkCore*`, `Npgsql*`, `Pgvector*`) from publish output.
- **Release Tags**: Each extension in [`extensions/catalog.json`](./extensions/catalog.json) has its own `tagPrefix` (e.g., `mysite-downloader/v0.1.0`). Pushing a matching tag triggers [`.github/workflows/build.yml`](./.github/workflows/build.yml) to build and publish only that extension's `.zip` archive to GitHub Releases for installation in Cove via **Extensions › Install from URL**.
