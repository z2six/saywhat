# SayWhat? 0.1.22 preview

## People photos

- Choose a local PNG/JPEG file or paste a copied image with **Ctrl+V**.
- Position the square crop by dragging or using arrow keys; adjust zoom with the slider, mouse wheel or keyboard.
- Preview the photo before saving. Cancel keeps the current picture unchanged.
- Photos are resized to a metadata-free 512 × 512 compressed PNG, capped at 1 MiB. Oversized input is rejected before storage; the original is unchanged.
- People cards use a larger photo. Subtitle bubbles use a separate lightweight thumbnail.
- The editor follows the selected theme and English, Chinese, Japanese or Korean interface language.

## One app to find

The app-managed speech environment and unused CUDA profiler launchers are hidden from normal Start/Terminal discovery. SayWhat? and its uninstall action remain. This does not remove Ubuntu, unregister WSL or delete models. Only verified generated entries belonging to the app's internal environment are affected, with recoverable backups.

## Technical documentation

The [documentation index](README.md) now covers architecture, local protocols, all persisted settings, models/runtimes, People/voice examples, performance/recovery, storage/privacy and installation/update/shutdown. The public repository contains documentation, not the app's source tree or website deployment tooling.

[Voice-model research](voice-model-research.md) compares identification, streaming voice tracking and actual overlapping-audio separation candidates. It distinguishes proposals from installed features. No new voice model was enabled, and live separation is not introduced in this release.

## Verification and limitations

Offline checks cover synthetic file/clipboard actions, crop geometry, bounded image storage, clean cancellation, themes/languages, managed-shortcut ownership and desktop-metadata preservation. They do not load models or capture microphones/playback. Packaged application and release checks are required before publication; fresh-PC installation, real clipboard application interoperability and in-game performance still need live acceptance testing.

This is an unsigned preview. Keep security protections enabled. The release's matching GPL corresponding-source archive and component notices accompany the installer.
