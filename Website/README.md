# SayWhat? download website

A dependency-free static site for the `saywhat` Cloudflare Pages project. Deploy
only `public/`; no build step or Pages Functions are needed. The site uses the
approved brand artwork and fictional conversation examples, not private app
screenshots, settings, logs or model files. It has no analytics scripts, tracking
cookies, external fonts or remote inference service.

## Publish a release

The website and app update checker share `public/release.json`, schema version 1.
Keep `ready: false` until the matching public GitHub release and its assets have
been published and verified. An offline app build is not evidence that an
installer is publicly available or that a fresh-PC installation works.

1. Prepare the installer, matching corresponding source ZIP, complete release ZIP,
   license notices and checksums. Exclude user settings, logs, recordings,
   downloaded model weights, secrets and private screenshots.
2. Publish the assets to `https://github.com/z2six/saywhat` under tag
   `v<version>`. The installer filename must be `SayWhat-<version>-Setup.exe`.
3. Verify the public asset URLs, file sizes and SHA-256 digests. Populate the
   manifest with those exact values; do not substitute a local-build path or an
   assumed future URL.
4. Set `ready: true` only after verification, deploy the website, and check its
   public installer/source/complete-ZIP links and displayed checksum.

Required manifest fields:

- `schemaVersion`: `1`; `channel`: `preview`
- `ready`: whether this complete release is publicly available
- `version`: the matching published version; `publishedAt`: its publication time
- `repositoryUrl`: `https://github.com/z2six/saywhat`
- `releaseUrl`: the exact `/releases/tag/v<version>` URL
- `source.url`, `source.sha256`: matching source asset in that release
- `distribution.url`, `distribution.sha256`: complete release ZIP alternative
- `sourceLicense`: `GPL-3.0-only`
- `installer.url`, `installer.sha256`, `installer.sizeBytes`: actual asset and digest

Client-side validation requires all asset URLs to use the same version tag in
the approved GitHub repository. Source and distribution assets must be ZIP files;
the installer must use the exact versioned filename. Credentials, alternative
ports, query strings, fragments, missing digests or inconsistent versions keep
download links unavailable. This gate validates metadata, not the remote file's
contents: maintainers must verify uploaded artifacts and their checksums before
publishing the manifest.

Initial load and the explicit refresh button read only same-origin
`release.json`. The complete request/body read has a five-second timeout and a
32 KiB metadata limit, including responses without a Content-Length header.
Failure keeps links unavailable. JavaScript-disabled visitors receive an unavailable notice.
Checksum copy is optional and has a manual-copy fallback.

## Deploy

From this directory, with authenticated Wrangler 4 or later:

```text
wrangler pages deploy ./public --project-name saywhat --branch main
```

Confirm the account and project before deployment. The project address is
`saywhat.pages.dev`; verify the deployed site and release links rather than
treating a successful upload as proof of a working installation.

[Cloudflare's Direct Upload documentation](https://developers.cloudflare.com/pages/get-started/direct-upload/)
documents directory upload and the inability to later turn a Direct Upload
project into Git integration. The site's [Wrangler configuration](https://developers.cloudflare.com/pages/functions/wrangler-configuration/)
uses a current compatibility date and a static output directory, with no bindings.
Static [response headers](https://developers.cloudflare.com/pages/configuration/headers/)
deny camera/microphone permissions and embedding, restrict assets/scripts to this
origin, and make the release manifest non-cacheable. No secrets belong here.

## Validate changes

Run the dependency-free offline checks:

```text
node --check public/release.js
node check.mjs
```

The tests use synthetic metadata and a fake browser/clipboard. They check local
assets, navigation targets, page structure, release URL/digest gates and bounded
metadata reading without contacting any server or running the app.

Before deployment, also check:

- Desktop/tablet/mobile layout, 200% zoom and keyboard focus.
- All navigation anchors and native FAQ disclosure controls work.
- Pending, invalid, oversized and network-failed manifests never enable an installer link.
- A complete synthetic manifest updates version, size, checksum and exact links.
- No app/model launch, microphone request, analytics request or external font.
- Privacy copy distinguishes local voice processing from online downloads and logs.
- Limits remain honest: unsigned preview, fresh-PC testing outstanding, uncertain
  GPU compatibility and latency, no independent live overlap separation.

Browser mocks and offline fixtures are not live-download, speech-accuracy or
fresh-install acceptance tests.
