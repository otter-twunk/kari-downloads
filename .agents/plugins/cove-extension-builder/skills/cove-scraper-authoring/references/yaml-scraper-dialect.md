# Cove Native YAML Scraper Dialect Reference

Cove's `ScraperService` includes a built-in declarative YAML scraper engine for `kind: "scraper-pack"` extensions.

## 1. Supported vs Unsupported Features

| Feature | Status in Cove |
|---|---|
| `action: scrapeXPath` | Supported (HtmlAgilityPack XPath 1.0) |
| `action: scrapeJson` | Supported (dot-path JSON selectors, `#` for array iteration, `0` for index) |
| `action: script` | **Blocked** (rejected as `RemoteCode`; never use) |
| `postProcess: replace / parseDate / feetToCm / lbToKg / map` | Supported |
| `postProcess: subScraper / javascript` | **Unsupported** (silently ignored; never use) |
| `driver: headers / cookies` | Supported (static values only; no headless browser / CDP) |
| `queryURLReplace` | Supported in **fragment modes only** (`videoByFragment`, `videoByQueryFragment`) |

## 2. YAML Manifest Structure

```yaml
name: ExampleSite

videoByURL:
  - action: scrapeXPath
    url:
      - example.com/videos/
    scraper: videoScraper

videoByName:
  action: scrapeXPath
  queryURL: https://example.com/search?q={}
  scraper: searchScraper

videoByQueryFragment:
  action: scrapeJson
  queryURL: "{url}"
  queryURLReplace:
    url:
      - regex: example\.com/videos/(\d+).*$
        with: example.com/api/v1/videos/$1.json
  scraper: videoApiScraper

performerByURL:
  - action: scrapeXPath
    url:
      - example.com/models/
    scraper: performerScraper

driver:
  headers:
    - Key: User-Agent
      Value: Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36
  cookies:
    - CookieURL: https://example.com
      Cookies:
        - Name: age_verified
          Value: "1"

xPathScrapers:
  videoScraper:
    common:
      $info: //div[@id="video-info"]
    video:
      Title: //meta[@property='og:title']/@content
      Details: $info//div[@class="description"]
      Date:
        selector: //span[@class="release-date"]/text()
        postProcess:
          - replace:
              - regex: '^Released:\s*'
                with: ""
          - parseDate: January 2, 2006
      Image: //meta[@property='og:image']/@content
      Tags:
        selector: //div[@class="tags"]/a
        Name: .
      Performers:
        selector: //div[@class="models"]/a
        Name: .
      Studio:
        Name:
          fixed: ExampleSite

  searchScraper:
    video:
      Title: //div[@class="result-card"]/a/@title
      URL: //div[@class="result-card"]/a/@href
```

## 3. Mode Nuances in Cove

- **`byName` Auto-Enrichment**: `videoByName` / `performerByName` returns parallel lists of `Title` and `URL`. Cove zips them into search candidates and automatically enriches each candidate by running its `URL` through your `byURL` scraper.
- **`parseDate` Layouts**: Uses Go reference time (`2006-01-02`, `January 2, 2006`, `Jan 2, 2006`, `01/02/2006`) or `"unix"` / `"unixmilli"`, outputting ISO `yyyy-MM-dd`.
- **Regex Backreferences**: In `replace`, use .NET `$1`, `$2` syntax.
