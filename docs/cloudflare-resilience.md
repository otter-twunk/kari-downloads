# Shared Cloudflare resilience

This module provides an optional, shared Cloudflare fallback for Kari Downloads extensions. It attempts each request directly first and invokes FlareSolverr only when a response is classified as a Cloudflare challenge.

## Configuration

```json
{
  "CloudflareResilience": {
    "Enabled": true,
    "FlareSolverrBaseUrl": "http://flaresolverr:8191/v1",
    "FlareSolverrTimeoutSeconds": 60,
    "MaxConcurrentSolves": 1,
    "SessionIdleMinutes": 30,
    "ChallengeCooldownSeconds": 300,
    "MaxSolvesPerHour": 10
  }
}
```

## Deployment

Run FlareSolverr on an internal Docker network. Do not expose port 8191 publicly.

```yaml
services:
  flaresolverr:
    image: ghcr.io/flaresolverr/flaresolverr:latest
    restart: unless-stopped
    ports:
      - "127.0.0.1:8191:8191"
```

## Extension integration

Extensions call `ICloudflareResilienceService`, provide a site policy and allowed hosts, and handle site-specific parsing, credentials, age gates, and media extraction themselves.

## Security

- Keep FlareSolverr private.
- Restrict resolver use to declared site hosts.
- Namespace cookies and sessions by site key.
- Never log raw cookie values.
- Do not retry interactive challenges in a loop.
