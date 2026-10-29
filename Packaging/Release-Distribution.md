# Release distribution

The repository is the public source home. Each binary release must still carry
its exact corresponding source; a link to a moving `main` branch is not a
replacement. Historical downloads and their matching source archives remain
unchanged. See [source provenance](../SOURCE-PROVENANCE.md).

## Release contents

Keep these together in each GitHub release:

- The versioned Windows installer.
- Its exact `SayWhat-Corresponding-Source.zip`.
- `THIRD-PARTY-NOTICES.md`, `SOURCE-AND-LICENSES.md` and dependency license texts.
- SHA-256 checksums and the complete recipient ZIP produced by the exporter.

Project-owned code is GPL-3.0-only, including the dashboard, subtitles, capture
host changes, helpers and build/install scripts. Keep upstream FoxTrans and
native/vendor source, original attribution and license notices. Optional model
weights have separate terms; they are not bundled or relicensed by the app.
See [the declaration](SOURCE-AND-LICENSES.md), [notices](THIRD-PARTY-NOTICES.md)
and [model documentation](../docs/models-and-runtimes.md).

## Build, verify, publish

1. Build and test a reviewed source commit using the documented developer and
   [packaging](README.md) workflow. Publish both app and host self-contained.
2. Inspect the generated source archive and payload. Exclude private settings,
   voice fingerprints, people photos, logs, recordings, model weights, credentials
   and generated test output. Include the code, build scripts and required notices.
3. Record the installer and staged source archive hashes. Run
   `Export-Release.ps1` with that exact version, stage, installer and expected
   hashes. Its checks validate source/payload parity, paths, privacy exclusions,
   language defaults and notices before export.
4. Upload the verified files to a versioned GitHub release. Download the public
   installer/source/complete ZIP and compare hashes and sizes with the local
   artifacts. Do not replace the bytes of an already-published version.
5. Only after those checks, update `Website/public/release.json` with the exact
   publication time, URLs, installer size and digests. Run both website checks,
   deploy the static site, and verify the production manifest and links.

The app and website use that manifest for downloads and update checks. Appending
source commits to Git does not itself publish a new Windows installer.

## Developer credentials

Publishing uses a maintainer's GitHub account and a Cloudflare Pages account.
Authenticate their CLIs locally; no credentials are part of this repository.
The regular CI workflow only builds and runs offline checks, with a read-only
token. It has no model inference, release upload or website deployment step.

See [Website/README.md](../Website/README.md) for static-site validation and
deployment. Keep local authentication files and `.wrangler` state out of Git.

## Verification limits

Compiler success, privacy scans and isolated fixtures do not establish real
speech accuracy, end-to-end latency, GPU compatibility or every Windows lifecycle
path. Document the tests actually performed and leave fresh-PC/live acceptance
checks explicit. The original FoxTrans convenience DLL used by historical
releases was not shown to be byte-for-byte reproducible; clean source builds now
compile the included native C code. Matching source does not imply identical
compiler output across machines.
