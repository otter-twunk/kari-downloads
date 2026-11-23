# X / Twitter Downloader

`karinauts.downloaders.xcom` requires Cove 1.8.0 or later. Install the local
`artifacts/packages/karinauts.downloaders.xcom-0.1.2.zip` through Cove's Settings →
Extensions → Install from File. This package contains the extension, not yt-dlp.

Install [yt-dlp](https://github.com/yt-dlp/yt-dlp#installation) separately on the
Cove host. Set the extension's `ytDlpPath` to the executable's full path, or make
`yt-dlp` available on the Cove process's PATH. Version 0.1.2 also discovers existing
native tools under `<COVE_HOME>/tools`, `<COVE_HOME>/yt-dlp`, and the sibling
`tools` directory used by this host's community downloader. Explicit settings take
precedence. Restarting Cove may be needed after
changing the system PATH. Do not put shell commands or arguments in this setting.

Paste `https://x.com/<handle>/status/<id>` or its `twitter.com` equivalent into
Cove's downloader intake. `www`, `m`, `mobile`, `i/web/status`, HTTP links and
`/video/N` or `/photo/N` selections are accepted. All resolution uses HTTPS.
Tracking queries are removed. Profiles, feeds, search URLs, arbitrary subdomains
and nonstandard ports are rejected.

Videos are resolved by a child yt-dlp process. Cove's host HTTP client streams the
selected progressive MP4 from `video.twimg.com` into an isolated temporary
directory. No FFmpeg is required: adaptive HLS/DASH and separate audio/video
formats are excluded. Best chooses the highest available height, then bitrate.
Only real source formats appear in the quality picker. The host's maximum height
applies when no specific quality is selected; unavailable selections fail rather
than exceeding the limit. Silent MP4 animations can be supported when the
extractor does not mark them as a separate video-only track.

Multiple resolved videos produce separate matches through `MatchAllAsync`, with
stable media IDs in `#cove-media=<id>` fragments. The fragment stays local to the
extension. Clients calling only `MatchAsync` receive the first video. Explicit
`/video/N` links are passed to yt-dlp as source selections. Video metadata includes
title, description, ISO date, source URL and uploader as studio attribution.

## Optional public photos

yt-dlp's Twitter extractor excludes photos. Enable the extension setting
`enablePublicPhotoFallback` to inspect the public status page's OpenGraph or
Twitter image metadata when video resolution fails or finds no progressive MP4.
Only HTTPS `pbs.twimg.com/media/` assets are accepted. One exposed image uses
image intake; multiple exposed images (up to four) use a numbered gallery ZIP.
The fallback uses Cove's HTTP client and adds title/source metadata, with X/Twitter
studio attribution for galleries.

This fallback is best effort. X often returns a login/error page or omits photo
metadata, and HTML can expose fewer photos than the post actually contains.
The extension downloads only the assets actually exposed; it cannot guarantee a
complete photo set. It does not interpret a video thumbnail as a photo and does
not invent CDN URLs. Mixed posts resolve videos first; photos are not additionally
imported from a successful video match. `/photo/N` selects the corresponding
exposed image where present.

## Failures and privacy

- Missing executable: install yt-dlp or configure `ytDlpPath`.
- Incompatible metadata or extractor failure: update yt-dlp and retry.
- Rate limit: wait before retrying.
- Login, protected, deleted or unavailable post: resolution fails cleanly.
- No supported format: this version requires a progressive MP4 or exposed public photos.

User yt-dlp configuration and automatic browser cookie extraction are disabled.
Cookie files are disabled unless `cookiesFile` explicitly names a user-supplied
Netscape-format X cookies export. Use an authorized session that can access the
post. The export is copied temporarily for yt-dlp, preserving the original, and
the temporary copy is deleted after resolution, including failures/cancellation.
Cookies and raw tool output are never logged. Without a configured export,
login-gated posts cannot resolve. Restricted/blurred previews are never treated
as photo downloads. Only yt-dlp's Twitter extractors are
enabled, so linked external sites are not followed into other downloaders. Raw
stderr is classified into safe errors instead of logged, to avoid exposing signed
URLs or credentials. Process output is bounded and resolution times out after two
minutes. Cancellation kills the child process tree. Failed/partial downloads
remove only their own temporary directory. Resume is not advertised.

Use media you are authorized to download. The extension does not implement DRM,
private-post or authentication bypasses.

## Verification and release

Offline tests use a fake process boundary with realistic yt-dlp JSON plus
`FakeDownloaderHost` HTTP fixtures. They cover matching, format filtering, actual
file output, metadata, qualities, multiple videos, photos/gallery ZIPs, failure
messages and cleanup. Run `npm run verify` for repository validation, all tests,
UI builds and extension packaging with host-DLL leak checks.

A live smoke test on 2026-11-24 used yt-dlp 2026.10.26 and the compiled provider
against a public Prime Video UK post. It resolved two videos with three progressive
qualities each, downloaded a 180,356-byte MP4 at the 320p height limit, verified the
MP4 `ftyp` signature, and reached 100% progress. The test ran in a temporary console
host and removed its media afterward; installation and intake inside the running
Cove application remain untested. Public photo extraction was tested offline only.

The repository catalog registers tag prefix `xcom-downloader/`. Publishing a
version tag or updating the rolling release requires separate approval. Building
the ZIP does not install it into the running Cove instance.
