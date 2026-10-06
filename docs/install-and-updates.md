# Install, update and lifecycle

[Documentation index](README.md) · [Privacy/storage](privacy-and-storage.md) · [Help](../SUPPORT.md)

## Installation and first setup

The Windows x64 installer supports current-user and all-users installations. All-users program changes need administrator permission; each user still has their own settings/People/cache data. Existing installations are detected so update/maintenance and uninstall choices can be presented instead of blindly creating another copy.

The optional post-install **open SayWhat?** action opens the UI, not active translation. The first-run flow chooses interface language, theme and translation targets, checks hardware locally, suggests a dedicated translator, reviews required downloads/permissions, and prepares files. Nonrecommended choices ask for confirmation. Required Windows/WSL/runtime steps may request permission or a restart; resumable progress keeps the selected setup choices.

Model-weight download sizes are not the entire fresh setup size. Linux packages/build tools and NVIDIA toolkit preparation can need several more gigabytes and at least 20 GB of free disk space. Unknown-duration work uses honest stage/progress information rather than an invented overall percentage. A verified existing managed model can be reused without redownloading it.

Setup creates an isolated app-managed WSL2 Ubuntu environment where needed. It does not take over, convert, unregister, or change the default of an unrelated existing Ubuntu installation. Existing supported speech registrations can be reused in advanced setups.

The internal speech environment is not another app to open. Version 0.1.22 hides its generated Start/Terminal entry and the CUDA toolkit's unused profiler launchers, without unregistering WSL or removing speech models. Cleanup is limited to verified app-owned registrations and exact generated shortcuts; unrelated Ubuntu and user-created shortcuts are left alone. Recoverable shortcut copies are kept under the app's data folder, and changed Linux desktop metadata retains original backups. The environment remains listed by WSL's technical inventory for troubleshooting.

Preparing files does not normally start capture or load inference models. At **Ready**, use the Home direction's explicit Start button. The short dashboard introduction is skippable; setup is restartable from the top of Settings without deleting saved People or models.

## Start, stop, close and tray

The two Home direction controls start your microphone-to-chat or incoming-subtitle pipeline independently. Managed prerequisites and conflicting listeners are checked before starting owned tools. Failing partway through startup triggers cleanup of that startup's processes; it does not kill an unknown listener occupying a port.

**Stop all** stops capture/recognition and releases app-owned managed translators. When a shared translator is still used by the other direction, stopping one direction keeps it alive. Turning **Voice recognition** off unloads only the optional identity extractor; speech translation continues.

Closing the main window defaults to **Ask**:

- **Quit and stop translation** performs cleanup and exits only after owned tools are released. If cleanup fails, the UI remains available for a retry instead of pretending that all model memory was freed.
- **Keep running in the tray** intentionally retains active translation/models. Restore the app or use its tray Stop/Quit actions.
- Remembering the choice changes subsequent close behavior; it can be changed in Settings.

Closing the subtitle window stops the incoming pipeline. Maximizing/minimizing a window or hiding the dashboard in the tray does not itself mean models have been unloaded. Normal cleanup targets retained Windows process objects and token-owned WSL sessions, not every process named “llama,” every WSL distribution, or another model application's residency.

## Update checks and consent

By default, each normal app startup performs one metadata-only check of `https://saywhat.pages.dev/release.json`. It has a five-second deadline and a 32 KiB body bound. Settings can disable this check; checking manually is also available. No audio or People data is included in it, and a failed network check does not grant permission to alter engines.

The manifest must describe a ready, newer numeric preview version and the expected GPL source/release metadata. Installer URLs are restricted to this project's GitHub release paths. A visible prompt/banner offers the update; downloading/installing is not automatic without user approval.

After approval, the app downloads the installer with progress and verifies expected size, SHA-256 and executable structure. It launches the verified installer in automatic-update mode, pinned to the current install scope, language, directory and downloaded version. After any required Windows permission is accepted, the installer requests graceful shutdown, waits for the app and its translation tools to stop, and shows compact installation progress without language/data/scope selection screens. Declining Windows permission cancels the update without first shutting down a working session. Safety failures remain visible. This path preserves settings, People, photos and cached models, then reopens the app **idle** after successful installation where safe automatic relaunch is supported. A manually opened installer still offers its normal maintenance and data choices.

If permissions/safety prevent automatic reopening, open SayWhat? from Start. It does not fall back to blindly force-closing processes or overwriting an app that failed to stop. Older installed versions may still use their older manual installer pages for the first upgrade into this flow.

## Trust boundaries

This remains an **unsigned preview**. SHA-256 verifies downloaded bytes against trusted manifest metadata; it is not independent publisher signing. HTTPS, restricted URLs and bounded parsing reduce risk but do not eliminate the trust placed in the project's website/GitHub release account. Keep Windows/antivirus protections enabled, review permission prompts, and report detections rather than disabling security.

GitHub holds actual release assets: installer, matching corresponding-source archive, complete distribution ZIP and checksums. The website/update manifest points at those assets. Changing the website's text is not an application release. Use the version-specific release notes to distinguish shipped features from future research.

## Uninstall and data choices

Uninstall through Windows Installed apps or the installer's uninstall option. The application is asked to stop gracefully before uninstall changes files; a live process/failed shutdown is not treated as permission for forced deletion.

Data is kept unless a data choice explicitly clears it. Settings, saved people/history/photos, and model cache are separate concerns. In an all-users maintenance operation, current-user data cleanup must be verified in the invoking user's context rather than accidentally using the elevated administrator's profile; safety failure halts the operation instead of guessing. Other Windows users' personal data is not implicitly cleared by replacing shared program files.

Imported files outside the managed cache and unrelated apps/Ubuntu remain outside cleanup scope. Installer receipts/data-safety validation protect against partial or mis-scoped mutations; a missing receipt is a failure to investigate, not a reason to manually erase arbitrary folders.
