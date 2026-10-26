---
name: cove-scraper-authoring
description: >-
  Autonomous workflow and SDK/YAML reference for creating, testing, and packaging
  Cove metadata scraper extensions (both compiled C# IScraperProvider extensions
  and native YAML scraper-pack extensions) in kari-downloads. Activate this
  skill whenever the user asks to create, modify, debug, or package a metadata
  scraper for their Cove server.
---

# Cove Scraper Authoring Skill

Use this skill to autonomously design, scaffold, implement, test, and package a Cove metadata scraper in `/Users/kari/dev/kari-downloads`.

Read the companion reference docs as needed:
- **Compiled C# Scraper Contracts & DTOs (`Cove.Plugins` / `Cove.Core` `v1.7.0`)**: [references/scraper-contracts.md](references/scraper-contracts.md)
- **Native YAML Scraper Dialect (`ScraperService` specification)**: [references/yaml-scraper-dialect.md](references/yaml-scraper-dialect.md)

---

## Autonomous 6-Step Workflow

### Step 1: Target Site Reconnaissance

Inspect the target site's raw server response (`curl -sL -A "Mozilla/5.0 ..." <url>`, **not** browser DevTools after client JS execution):
1. **Entity & URL Patterns**: Identify canonical URLs for scenes/videos, performers, galleries, images, or groups, plus the GET search URL (`https://site.com/search?q={query}`).
2. **Metadata Priority Order**:
   1. **JSON-LD (`<script type="application/ld+json">`)**: Check for `VideoObject`, `Movie`, `Person`, `ImageGallery` (`name`, `description`, `uploadDate`/`datePublished`, `thumbnailUrl`, `actor`, `keywords`, `productionCompany`).
   2. **OpenGraph / Meta Tags**: `og:title`, `og:description`, `og:image`, `video:release_date`, `video:actor`, `video:tag`, `keywords`.
   3. **Embedded JSON State**: `__NEXT_DATA__`, `window.__INITIAL_STATE__`, player setup JSON.
   4. **Semantic XPath / DOM**: Stable IDs, `itemprop` attributes, `/models/` or `/tags/` anchor `href` patterns.
   5. **JSON API Endpoints**: Direct REST or GraphQL endpoints derived from the URL slug or ID.
3. **Anti-Bot / Age-Gate Requirements**: Check if the site requires an age-verification cookie (e.g. `age_verified=1`) or `Referer` / `User-Agent` header.

### Step 2: Architecture Decision (Compiled C# vs Native YAML Pack)

Choose the right implementation model:
- **Use a Compiled C# Scraper (`--kind scraper`, `IScraperProvider`)** when:
  - `ByUrl` scraping needs to rewrite the page URL into an API URL or parse embedded JS state.
  - Code-based lookup (e.g., JAV code `ABC-123` extracted from filename/title) or multi-step HTTP enrichment is required.
  - Multiple entities (`Video`, `Performer`, `Gallery`, `Image`, `Group`, `Audio`, `Text`) or shared network logic (like Aylo/FakeHub sites) are handled together.
  - Offline xUnit tests in `KariDownloads.Tests` are desired to regression-test HTML/JSON parsing in CI.
- **Use a Native YAML Scraper Pack (`--kind scraper-pack`)** when:
  - The site is 100% static server-rendered HTML or direct JSON where `scrapeXPath` / `scrapeJson` selectors and `replace` / `parseDate` post-processors extract all fields cleanly without JavaScript or custom code.
  - **Remember**: Cove blocks `action: script` and ignores `subScraper` / `javascript` post-processors.

### Step 3: Scaffold the Scraper

From `/Users/kari/dev/kari-downloads`, run:

```bash
# Compiled C# scraper (default & most flexible):
node scripts/new-extension.mjs \
  --kind scraper \
  --name <SiteName>Scraper \
  --domain <example.com> \
  --entity <Video|Performer|Gallery|Image|Group|Audio|Text> \
  --title "<Site Name> Scraper"

# OR Native YAML scraper-pack:
node scripts/new-extension.mjs \
  --kind scraper-pack \
  --name <SiteName>ScraperPack \
  --domain <example.com> \
  --title "<Site Name> Scraper Pack"
```

### Step 4: Implement Extraction Logic

#### For Compiled C# Scrapers (`IScraperProvider`):
- Inherit from `CoveExtensionBase, IScraperProvider`. Do **not** override metadata properties (`Id`, `Name`, `Version`, etc.) — `CoveExtensionBase` populates them from `extension.json`.
- Capture `IServiceProvider` in `InitializeAsync(IServiceProvider services, CancellationToken ct = default)` and obtain `HttpClient` via `_services?.GetService<IHttpClientFactory>()?.CreateClient(Id)`.
- Use `ScraperUtilities.ExtractStructuredMetadata(html, DefaultStudioName)` to automatically parse JSON-LD + OpenGraph + meta tags, then layer site-specific regex/DOM selectors for any remaining fields (`PerformerNames`, `TagNames`, `Code`, `Date`, `StudioName`, `Director`).
- Always normalize dates to ISO `yyyy-MM-dd` via `ScraperUtilities.NormalizeIsoDate(...)`.
- Always provide a `StudioName` fallback so scraped scenes are attributable in Cove.
- Keep `ParseVideoHtml` / `ParseSearchResultsHtml` `internal static` so xUnit tests can also test parsing directly alongside full `ScrapeVideoAsync` / `SearchVideosAsync` calls.

#### For Native YAML Scraper Packs (`kind: "scraper-pack"`):
- Edit `extensions/<Name>/scrapers/<Site>.yml` following [references/yaml-scraper-dialect.md](references/yaml-scraper-dialect.md).
- List every `.yml` file in `extension.json` under `"scraperFiles": ["scrapers/<Site>.yml"]`.
- Ensure `extensions/catalog.json` has `"manifestOnly": true, "contentOnly": true`.

### Step 5: Write Offline Fixture Tests

In `tests/KariDownloads.Tests/<SiteName>ScraperTests.cs`:
- Capture a representative (sanitized) HTML or JSON snippet from the target site for both detail page (`Scrape*Async`) and search results (`Search*Async`).
- Use `FakeDownloaderHost.MapHtml(url, html)` / `MapJson(url, json)` to verify `Title`, `Code`, `Date` (`yyyy-MM-dd`), `Details`, `ImageUrl`, `StudioName`, `PerformerNames`, `TagNames`, and search candidate `Urls`.

### Step 6: Validate, Test & Package

Run the full verification suite:

```bash
npm run verify
```

Confirm that `validate-extension-repo.mjs`, `dotnet test`, and `package-extension.mjs` all succeed with zero errors.
