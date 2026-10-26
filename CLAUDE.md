# CLAUDE.md

**Pointer file for Claude Code.**

Full autonomous agent contract lives in **[`AGENTS.md`](./AGENTS.md)**. Read it before doing anything.

## What to read first (in order)

1. **[`AGENTS.md`](./AGENTS.md)** — operational contract for autonomous downloader and scraper creation
2. **[`.agents/plugins/cove-extension-builder/skills/cove-downloader-authoring/SKILL.md`](./.agents/plugins/cove-extension-builder/skills/cove-downloader-authoring/SKILL.md)** — when creating or modifying a Cove downloader (`IDownloaderProvider`)
3. **[`.agents/plugins/cove-extension-builder/skills/cove-scraper-authoring/SKILL.md`](./.agents/plugins/cove-extension-builder/skills/cove-scraper-authoring/SKILL.md)** — when creating or modifying a Cove scraper (`IScraperProvider` or native YAML `scraper-pack`)

## Quick-reference commands

```bash
# Scaffold a new compiled C# downloader (+ xUnit test):
node scripts/new-extension.mjs --kind downloader --name <SiteName>Downloader --domain <example.com> --entity Video

# Scaffold a new compiled C# scraper (+ xUnit test):
node scripts/new-extension.mjs --kind scraper --name <SiteName>Scraper --domain <example.com> --entity Video

# Scaffold a new native YAML scraper-pack:
node scripts/new-extension.mjs --kind scraper-pack --name <SiteName>ScraperPack --domain <example.com>

# Validate catalog & manifests:
npm run validate

# Build & run offline xUnit fixture tests:
npm run test

# Full verification gate (validate + test + package & verify .zip):
npm run verify
```

## Key guardrails

- Keep all extension metadata (`id`, `name`, `version`, `description`, `categories`, `minCoveVersion`, `entryDll`, `permissions`, `settings`, `externalDependencies`) in `extension.json` — never redeclare metadata properties in C# subclasses of `CoveExtensionBase`.
- Always run `npm run verify` and confirm 0 errors before finishing a task.
- Never tag a release or push without explicit approval from Kari.
