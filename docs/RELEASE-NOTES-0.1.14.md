# SayWhat? 0.1.14 preview

## Own-voice responsiveness

- The default sentence-ending pause is now **1.2 seconds of real microphone silence**. A one-time migration updates the previous untouched 2.4-second default; customized timing remains unchanged.
- **Show translations while I speak** sends cumulative translated snapshots as useful speech arrives. Each request uses the full sentence heard so far. Early wording may change with later context.
- Changed messages are coalesced and spaced by at least 1.5 seconds. The finished sentence takes priority. These are submitted chat bubbles, not keyboard drafts.
- Removed redundant synthetic audio from the captured-turn speech-finishing path.
- A failed speech turn no longer discards the healthy next sentence already waiting.
- VRChat display copies are limited to 144 text elements and nine lines without clipping the model's source context. Long display text shows a readable tail.

Turn off early messages in Settings to translate only finished sentences and avoid speculative translation work. Actual latency also includes recognition finishing, translator work, GPU contention and chatbox send cadence: the 1.2-second setting is not a promise of a final bubble within 1.2 seconds.

## Distribution and updates

- Windows installer, exact corresponding-source ZIP, complete distribution ZIP, license notices and SHA-256 checksums are provided together.
- For application source, download `SayWhat-Corresponding-Source.zip`. GitHub's automatic "Source code" archives contain only the public website/docs repository.
- SayWhat?'s original contributions are GPL-3.0-only; third-party licenses remain in force.
- Settings has a manual **Check for updates** action. It reads bounded public metadata, offers the verified GitHub release page, and does not automatically install or interrupt translation.

## Verification and remaining limits

Fresh app and host builds, isolated synthetic speech/translation regressions, offline protocol checks, proxy state/packet checks and UI/localization/settings tests passed. These tests do not use a real microphone, send OSC to VRChat or load models.

Fresh-PC install/update/uninstall and long live sessions still need acceptance testing. This installer is unsigned. GPU/driver compatibility, live accuracy, frame-rate impact and first/final visible-message timing are not certified. Optional voice recognition can mislabel music or overlapping voices; independent live simultaneous-speaker separation is not provided.

The source includes the original native capture/vendor source and unchanged upstream native binary provenance. Reproducing that binary and fully reconstructing every transitive native build option remain release-review work. No model weights are mirrored in this release.
