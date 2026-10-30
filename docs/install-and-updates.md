# Install, update and lifecycle

[Documentation index](README.md) · [Privacy/storage](privacy-and-storage.md) · [Help](../SUPPORT.md)

## Installation and first setup

The Windows x64 installer supports current-user and all-users installations. All-users program changes need administrator permission; each user still has their own settings/People/cache data. Existing installations are detected so update/maintenance and uninstall choices can be presented instead of blindly creating another copy.

The minimum installation version is **Windows 10 1809, build 17763, x64**; Windows 11 is also accepted. Your microphone-to-VRChat translation uses native Windows recognition and translation by default. Windows 10 compatibility is a preview and has **not** been verified on a real Windows 10 PC.

Subtitles have a separate audio-capture requirement. **VRChat only** and **Another application** require the process-loopback API available on Windows 11 clients (build 20348+). On Windows 10, **All playback** can be selected explicitly instead; it includes audio from other applications. The app does not silently substitute All playback when application-only capture is unavailable.

Optional WSL speech engines need Windows 10 2004 (build 19041+) and WSL 2 version 0.48.2 or newer. CUDA speech in WSL needs Windows 10 21H2 (build 19044+) or Windows 11, plus a compatible NVIDIA GPU and driver. Native speech setup does not require WSL or these newer Windows versions.

The post-install **open SayWhat?** option is selected by default and opens the UI, not active translation. First setup chooses the interface language, the language you speak and one to four output languages. Theme selection is optional. It checks hardware locally, suggests models, reviews downloads/permissions and prepares your own voice translation. A nonrecommended translator asks for confirmation before the separate download review and preparation action.

The default microphone recognizer runs directly on Windows and does not require WSL. Windows runtime prerequisites may still need permission or a restart when missing. Optional subtitles and the legacy Voxtral engine have separate preparation; only those paths need the additional managed Linux components where applicable. Resumable progress retains setup choices, and the review reflects the actual permission/restart requirements.

Model-weight download sizes are not the whole setup size. In a selected Linux speech workflow, build tools and NVIDIA toolkit preparation can need several more gigabytes and at least 20 GB of free disk space; that is not a blanket requirement for native own-voice setup. Unknown-duration work shows stage/progress information rather than an invented overall percentage. Verified existing managed models can be reused without redownloading them.

When a selected speech workflow needs WSL2, setup creates an isolated app-managed Ubuntu environment. It does not take over, convert, unregister, or change the default of an unrelated existing Ubuntu installation. Existing supported speech registrations can be reused in advanced setups.

New subtitle settings use the native Windows recognizer too. Existing choices,
including Voxtral and older imported profiles, are preserved on update.

Starting in 0.2.12, new managed Linux speech components are imported from a
size- and SHA-256-pinned Canonical Ubuntu 24.04.5 image. Before the first boot,
setup sets `/etc/wsl.conf`'s `[general] guiApplications=false` in the image and
disables its shell shortcuts. Speech preparation therefore does not need WSLg,
RemoteApp or Remote Desktop's ActiveX control. It leaves global `.wslconfig`,
GPU access, unrelated Ubuntu environments and Windows security settings alone.

The setup receipt binds a completed import to its WSL registration GUID, exact
storage folder and unique owner marker. An old failed checkpoint or replaced
same-name environment is left intact while a new component is prepared. Setup
never boots an unconfirmed partial import just to see whether it works.
Both Windows backing-volume space and Linux space are checked before building.
CUDA driver and GPU compatibility checks run before toolkit downloads.

Windows downloads have separate connection and inactivity deadlines, with one
retry for a transient failure. Linux package output is drained into a private
temporary transcript even if a progress handle closes. A failed package manager
retains its real exit code; only its explicit interrupted-transaction condition
gets one repair attempt. Successful setup removes that package transcript.

These checks reduce known installation failures; they do not certify every
Windows build, driver or custom Windows installation. In particular, the lower
1809 installer target and GPU performance still need real-machine acceptance.

The internal speech environment is not another app to open. Version 0.1.22 hides its generated Start/Terminal entry and the CUDA toolkit's unused profiler launchers, without unregistering WSL or removing speech models. Cleanup is limited to verified app-owned registrations and exact generated shortcuts; unrelated Ubuntu and user-created shortcuts are left alone. Recoverable shortcut copies are kept under the app's data folder, and changed Linux desktop metadata retains original backups. The environment remains listed by WSL's technical inventory for troubleshooting.

Preparing files does not start capture or inference. At **Ready**, open Home and choose **Start translating my voice**. Subtitles have a separate preparation/Start action and can wait until needed. The short dashboard tour is skippable; setup is restartable from the top of Settings without deleting saved People or models.

## Start, stop, close and tray

The two Home direction controls start your microphone-to-chat or incoming-subtitle pipeline independently. Managed prerequisites and local connections are checked before starting owned tools. Internal speech/translation TCP ports are selected automatically by default; advanced fixed-port mode reports a conflicting listener instead of changing that address. External configured translator URLs and OSC ports are unchanged. Failing partway through startup triggers cleanup of that startup's processes; it does not kill an unknown listener occupying a port.

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
