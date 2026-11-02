# Cove Batch URL Downloader API Surface Specification

> **Audience**: Autonomous coding agents, LLM tool callers, and third-party client developers (e.g. iOS apps, macOS Shortcuts, Raycast extensions, CLI tools).  
> **Extension ID**: `karinauts.tools.batch-downloader`  
> **API Version**: `v1` (Extension `v0.1.1`+)  
> **Base Protocol**: HTTP/1.1 or HTTP/2 over HTTPS (or HTTP on local dev networks)

---

## 1. System Overview & Architecture

The **Batch URL Downloader** extension extends the upstream [Cove](https://github.com/yourcove/cove) media server with high-level endpoints for batch URL ingestion, automated downloader matching, initial metadata tagging (tags, performers, studio), and asynchronous background job execution.

```
┌────────────────────────────────────────────────────────┐
│               Third-Party Client (e.g. iOS)            │
│  - Shares URLs / raw clipboard text                    │
│  - Passes optional Tag IDs, Studio ID, Performer IDs   │
└──────────────────────────┬─────────────────────────────┘
                           │ POST /api/ext/batch-downloader/queue
                           ▼
┌────────────────────────────────────────────────────────┐
│         Cove Server (BatchDownloader Extension)        │
│  1. Ingests & sanitizes URLs (regex + URL parser)      │
│  2. Resolves IDownloaderProvider per URL               │
│  3. Pre-creates entities with initial metadata tags    │
│  4. Enqueues job to Cove IJobService                   │
│  5. Returns 202 Accepted with jobId                    │
└──────────────────────────┬─────────────────────────────┘
                           │ GET /api/jobs/{jobId}
                           ▼
┌────────────────────────────────────────────────────────┐
│                 Cove Native Job Engine                 │
│  - Executes downloads asynchronously                   │
│  - Reports progress (0.0 to 1.0) and status messages   │
└────────────────────────────────────────────────────────┘
```

---

## 2. Authentication & Authorization

### 2.1 Policy
All extension endpoints declare `.AllowWithoutCovePermission()`.
- **No special roles or permissions** (such as `SystemSettingsWrite` or `JobsRun`) are required.
- The caller must only be an **authenticated principal** on the Cove server.

### 2.2 Supported Auth Schemes
Clients must use **one** of the following two mechanisms:

1. **API Token (Recommended for native mobile, CLI, & Shortcuts)**:
   Pass the token generated in Cove via **Settings › API Tokens**:
   ```http
   Authorization: Bearer <cove_api_token>
   ```

2. **Session Cookie (For WebViews, Safari, & browser contexts)**:
   If the user has logged in via Cove's standard web authentication, requests automatically send the host's session cookie.

### 2.3 Common Request Headers
| Header | Required | Value |
| :--- | :--- | :--- |
| `Authorization` | Yes (if no cookie) | `Bearer <token>` |
| `Content-Type` | Yes (for POST) | `application/json` |
| `Accept` | No | `application/json` |

---

## 3. Endpoints

### 3.1 Preflight Downloader Inspection

Inspects raw text or a list of URLs to determine which installed `IDownloaderProvider` extensions match, what entity types are detected, and what labels or streams are discovered.

```http
POST /api/ext/batch-downloader/match
```

#### Request Schema (`application/json`)
```json
{
  "rawText": "string (optional)",
  "urls": ["string (optional)"]
}
```
*Note: At least one of `rawText` or `urls` must be provided.*

| Field | Type | Description | Example |
| :--- | :--- | :--- | :--- |
| `rawText` | string | Unstructured text containing URLs (e.g. from chat, markdown, notes). URLs will be regex-extracted and cleaned. | `"https://site.com/video1\nCheck https://site.com/video2"` |
| `urls` | string[] | Array of explicit URL strings. | `["https://site.com/video1"]` |

#### Response Schema (`200 OK`)
```json
{
  "count": 2,
  "matches": [
    {
      "url": "https://site.com/video1",
      "matched": true,
      "downloaderId": "karinauts.downloaders.sample-site",
      "downloaderName": "Sample Site Downloader",
      "supportedEntity": "Video",
      "normalizedUrl": "https://site.com/video1",
      "label": "Sample Video Title"
    },
    {
      "url": "https://unknown.com/page",
      "matched": false,
      "downloaderId": null,
      "downloaderName": null,
      "supportedEntity": null,
      "normalizedUrl": "https://unknown.com/page",
      "label": "https://unknown.com/page"
    }
  ]
}
```

---

### 3.2 Queue Batch Downloads

The primary workhorse endpoint. Extracts URLs, inspects downloaders, attaches initial metadata (Tags, Studio, Performers), and queues asynchronous download execution in Cove's background job engine.

```http
POST /api/ext/batch-downloader/queue
```

#### Request Schema (`application/json`)
```json
{
  "rawText": "string (optional)",
  "urls": ["string (optional)"],
  "entity": "string (optional, default: \"auto\")",
  "tagIds": [1, 2],
  "studioId": 10,
  "performerIds": [3, 4],
  "autoApplyMetadata": true,
  "allowDuplicates": false
}
```

#### Field Specifications
| Parameter | Type | Required | Default | Description |
| :--- | :--- | :---: | :---: | :--- |
| `rawText` | `string` | No* | `null` | Raw text pasted from clipboard/notes. URLs matching `https?://[^\s<>'\"{}|\\^`\]\)]+` are extracted and sanitized. |
| `urls` | `string[]` | No* | `null` | Array of discrete URLs. Merged and de-duplicated with any URLs found in `rawText`. |
| `entity` | `string` | No | `"auto"` | Target entity type. Enum: `"auto"`, `"Video"`, `"Gallery"`, `"Image"`, `"Audio"`. When `"auto"`, the matched downloader determines entity type. |
| `tagIds` | `int[]` | No | `null` | Array of existing Cove Tag IDs to attach to all items in the batch. |
| `studioId` | `int` | No | `null` | Existing Cove Studio ID to attach to all items in the batch. |
| `performerIds` | `int[]` | No | `null` | Array of existing Cove Performer IDs to attach to all items in the batch. |
| `autoApplyMetadata`| `bool` | No | `true` | When `true`, Cove runs configured metadata scrapers after each item finishes downloading. |
| `allowDuplicates` | `bool` | No | `false` | When `false`, URLs or files already existing in the Cove library are skipped. |

*\*At least one of `rawText` or `urls` must contain at least one valid HTTP/HTTPS URL.*

#### Response Schema (`202 Accepted`)
```json
{
  "jobId": "c92d5be3-a609-4ce4-8fe4-219d3fbc0179",
  "queuedCount": 2,
  "skippedCount": 1,
  "queued": [
    {
      "url": "https://site.com/video1",
      "entity": "Video",
      "label": "Sample Video Title 1",
      "entityId": 142
    },
    {
      "url": "https://site.com/video2",
      "entity": "Video",
      "label": "Sample Video Title 2",
      "entityId": 143
    }
  ],
  "skipped": [
    {
      "url": "https://unsupported.com/foo",
      "reason": "No compatible downloader matched this URL."
    }
  ]
}
```

#### Response Fields
- `jobId` (`string | null`): The ID of the queued background job in Cove. Use this ID to poll status on `/api/jobs/{jobId}`. If `queuedCount == 0`, `jobId` is `null`.
- `queuedCount` (`int`): Total count of successfully queued download items.
- `skippedCount` (`int`): Total count of skipped items (e.g. unsupported URLs or missing downloaders).
- `queued` (`object[]`): Detailed breakdown of each queued item, including pre-assigned `entityId`.
- `skipped` (`object[]`): Items that were rejected with specific failure reasons.

#### Error Responses
- `400 Bad Request`: When no valid URLs can be extracted.
  ```json
  { "error": "No valid URLs provided in request." }
  ```
- `401 Unauthorized`: When no valid API token or session cookie is provided.

---

### 3.3 Tracking Job Status (Native Cove Core Endpoint)

Once `jobId` is received from `/api/ext/batch-downloader/queue`, poll Cove's native job service to monitor progress.

```http
GET /api/jobs/{jobId}
```

#### Response Schema (`200 OK`)
```json
{
  "id": "c92d5be3-a609-4ce4-8fe4-219d3fbc0179",
  "title": "Downloading 2 items",
  "status": "Running",
  "progress": 0.5,
  "statusMessage": "Downloading (1/2): Sample Video Title 1",
  "createdAt": "2026-11-02T11:45:00.000Z",
  "updatedAt": "2026-11-02T11:45:30.000Z"
}
```

#### Status Lifecycle
```
Pending ──► Running ──► Completed
                │
                └─────► Failed / Cancelled
```
- `status`: `"Pending"` | `"Running"` | `"Completed"` | `"Failed"` | `"Cancelled"`
- `progress`: Floating point number from `0.0` to `1.0` (multiply by 100 for percentage).
- `statusMessage`: Real-time status string showing currently downloading item.

---

## 4. Entity Discovery Helpers (For Tagging & Studio Selection)

To look up `tagIds`, `studioId`, or `performerIds` prior to queueing, use Cove's native entity search endpoints:

| Action | Endpoint | Query Parameters |
| :--- | :--- | :--- |
| **Search Tags** | `GET /api/tags` | `?query={name}&perPage=20` |
| **Search Studios** | `GET /api/studios` | `?query={name}&perPage=20` |
| **Search Performers** | `GET /api/performers` | `?query={name}&perPage=20` |

---

## 5. Client Implementation Reference Code

### 5.1 Swift / iOS (URLSession)

```swift
import Foundation

public struct CoveBatchDownloaderClient {
    public let baseURL: URL
    public let apiToken: String

    public init(baseURL: URL, apiToken: String) {
        self.baseURL = baseURL
        self.apiToken = apiToken
    }

    public struct QueueRequest: Codable {
        public var rawText: String?
        public var urls: [String]?
        public var entity: String = "auto"
        public var tagIds: [Int]?
        public var studioId: Int?
        public var performerIds: [Int]?
        public var autoApplyMetadata: Bool = true
        public var allowDuplicates: Bool = false
    }

    public struct QueueResponse: Codable {
        public let jobId: String?
        public let queuedCount: Int
        public let skippedCount: Int
    }

    public func queueDownloads(
        text: String,
        tagIds: [Int]? = nil,
        studioId: Int? = nil
    ) async throws -> QueueResponse {
        let endpoint = baseURL.appendingPathComponent("api/ext/batch-downloader/queue")
        var request = URLRequest(url: endpoint)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer \(apiToken)", forHTTPHeaderField: "Authorization")

        let payload = QueueRequest(
            rawText: text,
            urls: nil,
            entity: "auto",
            tagIds: tagIds,
            studioId: studioId
        )
        request.httpBody = try JSONEncoder().encode(payload)

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let httpResponse = response as? HTTPURLResponse, (200...299).contains(httpResponse.statusCode) else {
            throw URLError(.badServerResponse)
        }
        return try JSONDecoder().decode(QueueResponse.self, from: data)
    }
}
```

### 5.2 TypeScript / JavaScript (`fetch`)

```typescript
interface QueuePayload {
  rawText?: string;
  urls?: string[];
  entity?: "auto" | "Video" | "Gallery" | "Image" | "Audio";
  tagIds?: number[];
  studioId?: number;
  performerIds?: number[];
  autoApplyMetadata?: boolean;
  allowDuplicates?: boolean;
}

interface QueueResult {
  jobId: string | null;
  queuedCount: number;
  skippedCount: number;
  queued: Array<{ url: string; entity: string; label: string; entityId?: number }>;
  skipped: Array<{ url: string; reason: string }>;
}

export async function queueCoveBatchDownload(
  coveHost: string,
  apiToken: string,
  payload: QueuePayload
): Promise<QueueResult> {
  const res = await fetch(`${coveHost.replace(/\/$/, "")}/api/ext/batch-downloader/queue`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Authorization": `Bearer ${apiToken}`,
    },
    body: JSON.stringify(payload),
  });

  if (!res.ok) {
    const errorBody = await res.json().catch(() => ({}));
    throw new Error(errorBody.error || `HTTP ${res.status}`);
  }

  return res.json();
}
```

### 5.3 cURL / Shell

```bash
# Queue URLs with tagging
curl -X POST "https://cove.local/api/ext/batch-downloader/queue" \
  -H "Authorization: Bearer <YOUR_API_TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{
    "rawText": "https://example.com/watch/video-1\nhttps://example.com/watch/video-2",
    "tagIds": [12, 34],
    "studioId": 5,
    "autoApplyMetadata": true
  }'
```
