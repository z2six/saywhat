# SayWhat? 0.1.24 preview

## Translation profiles

At the top of **Settings**, choose **Quick**, **Normal** or **Groups**, then select **Apply profile**. Quick uses an 800 ms incoming pause, 250 ms subtitle stability, shorter recognition lookahead and no automatic person matching. Normal uses 1.2 seconds and retains the familiar-person workflow, with matching now outside the translation's critical path. Presets do not replace your selected models or languages.

**Save current settings as…** creates a named snapshot of your editable preferences. You can apply, update, rename or delete it later. Saved People, voice examples and downloaded model files remain shared, not duplicated into each profile. Existing configurations are retained as a **Current settings** profile on upgrade.

The subtitle window offers only **Normal / Groups**. Quick and saved profiles are selected in Settings. Switching incoming speech engines keeps the window and its subtitle history; a failed start attempts to restore the previous mode. Cancellation stops incoming speech rather than loading another model unexpectedly.

Groups is optional and experimental. Its first preparation offers a local VibeVoice streaming checkpoint and required tools, with download progress. Start with 1.5B; 7B is a larger optional download. Recognition runs in the already configured speech WSL distribution, on CUDA. Inference remains local. Imported Ubuntu distributions need Python's virtual-environment support and Git; managed setup already permits their preparation.

Groups routes the recognizer's separate voice-labelled text to individual subtitles. It does not produce isolated audio. Existing voice fingerprints can attach saved names from clear, single-voice chunks; mixed or unlabelled chunks do not train a person's voice. Temporary voice mappings reset when the recognizer's conversation context rolls over; saved People remain.

This profile release has passed offline settings, queue, routing, transport and UI checks. Actual Groups recognition quality, Windows↔WSL startup and GPU/game latency have not yet been verified with real weights. No performance guarantees are inferred from model size or offline tests.

SayWhat? translates your voice into VRChat's chatbox and shows translated speech from other people in a movable, closable subtitle window. You do not need to install FoxTrans, .NET or LM Studio/Bionic separately.

This is an unsigned preview. The complete first-run workflow has not yet been verified on a fresh Windows PC; see the limitations below.

## Get started

1. Open `SayWhat-0.1.15-Setup.exe`. Choose English (default), Simplified Chinese, Japanese or Korean for the installer and app interface. **Just me** is recommended; **All users** installs shared app files with administrator permission. Review the detected installation and choose Install, Update or Repair. Optionally select **Open SayWhat?** when installation finishes, or open it from Start. The program file is `SayWhat.exe`; Windows filenames omit the question mark.
2. In **Setup**, choose the language for your voice and the language you want to read. The defaults are Chinese for your voice and English for other people's speech.
3. Select **Set up SayWhat?**. Review the download and Windows permission notice, then approve setup. The app prepares its translation files and speech support.
4. If Windows needs a restart, restart when convenient, reopen SayWhat? and select **Continue setup**. Your language choices and completed setup steps are retained.
5. Open VRChat. On **Home**, select **Start both**, or start only your voice translation or subtitles.

Setup does not start listening or translating when it finishes. It needs internet access to download files; speech and translations are processed on your computer. Model weights are not included in the installer.

## What's changed in 0.1.15

**Languages → Translate my voice into more languages** offers optional own-voice output in two or three languages, disabled by default. Choose the primary language and up to two distinct additional languages. Completed translations appear on separate lines in the same submitted VRChat chat bubble; they do not edit or open the keyboard. One resident translator is reused, but every additional language needs more inference time and shares the chatbox's 144-text-element limit. Turning this off retains single-language behavior.

The download catalog adds **TranslateGemma 4B Q4_K_M** and **Liquid AI LFM2 350M EN–JP Q4_K_M**, with their source and license notices before download. Both require an explicit spoken language. Liquid supports only English and Japanese: unsupported own-voice, additional and subtitle language choices are blocked before startup. TranslateGemma uses a model-specific template in SayWhat?'s built-in runtime, not an arbitrary external endpoint. Hy-MT2 remains the automatic-language default; existing model selections are not replaced. New adapters are offline-tested, not benchmarked with actual weights or VRChat. TranslateGemma's short-input safety bound reports overlong sentences rather than truncating their translation context.

The sidebar shows VRChat's OSC availability and offers instructions when the game is not running, has no verified local OSC response, or uses another port. This uses read-only OSCQuery checks tied to the VRChat process. It does not submit chat messages or prove that another player can see them. No response is not conclusive proof that OSC is disabled.

The app checks for updates once on launch unless disabled in Application settings. An available update offers **Update now** or **Later**. Update now downloads the version-matched installer, checks its size and SHA-256, and opens the normal update wizard for the exact registered installation. Settings and models are kept; final license/update confirmation and Windows administrator permission remain visible when needed. No executable is installed silently, and automatic checks never load models. The manual check remains available.

## What's changed in 0.1.14

New setups use 1.2 seconds of quiet to finish your sentence. Existing explicit pause settings are preserved. **Advanced → Show translations while I speak** can submit cumulative translated chat bubbles before the final sentence is ready, without opening VRChat's chat-input editor. Each revision uses the whole captured sentence so far, not disconnected fragments. Turning it off uses final-only output and avoids speculative translation work.

Early translations are limited to one active request and one latest pending revision, with at least 1.5 seconds between speculative starts. The definitive transcript cancels previews and takes priority after the recognizer confirms completion. Mute and bounded-queue safeguards remain in place. Very long source sentences report a limit rather than silently losing context; chatbox display independently keeps a readable newest tail within its size and line limits. These changes passed offline checks, not a live speed benchmark.

**Settings → Application → Check for updates** performs a manual check against the public release manifest. It does not load models, install an update, or send speech. The original SayWhat? contributions now have an explicit GPL-3.0-only grant with matching source and retained third-party notices. Audited previous releases remain unchanged.

## What changed in 0.1.13

Speech turns no longer add a redundant 1.2 seconds of artificial audio before finalizing. The recognizer still performs its required finishing work, and messages still wait for a real final transcript. A failed or empty sentence can recover without discarding the next captured sentence; fresh speech can resume after an early provider failure. Microphone mute, bounded queues and timeout safeguards remain in place.

The sentence pause remains 2.4 seconds and existing model/settings choices are preserved. The removed audio is not a measured 1.2-second reduction in waiting time; speed still depends on recognition, translation and queue pressure. Diagnostics now include clearer numerical timing/recovery details without recording spoken text in the capture host's operational logs. The installer also includes additional dependency license texts directly. This update has passed offline regression checks; live performance and fresh-PC acceptance testing remain necessary.

## Appearance

Open **Settings → Themes** to choose Coral Night, Ink, Forest, Plum, Paper, Sand, Mint or High Contrast. Choose **Custom** for background swatches or a six-digit color, a coordinated text palette, and an optional local PNG/JPEG image with adjustable opacity. Regular text, hints, status colors and speaker labels change together; shades adapt to keep text readable. There are no separate font-color pickers.

Appearance changes are saved immediately without restarting translation or clearing subtitle history. The dashboard, subtitles, controls, menus, dialogs, scrollbars and title bars share the palette. Photos stay behind opaque text panels. Images are referenced locally, decoded once at a bounded display size, and never included in the installer. Keep the selected file in place; unavailable images fall back to a solid background. Windows-owned file pickers and pre-startup safety errors follow Windows, while setup retains SayWhat?'s installer branding.

The recommended starting model is **Hy-MT2 1.8B**, chosen to leave more room for your game. **Hy-MT2 7B** is an optional larger model, not a guaranteed improvement in every conversation. In Setup, expand **Change the recommended translator** and choose **Larger translator** to try it before setup begins. Advanced also has detailed model choices. Existing saved choices are preserved rather than automatically upgraded because a GPU has more memory.

## Everyday use

Start and stop translation from the dashboard. Detailed audio, model and troubleshooting options are under **Advanced**.

Home choices stay pending while you edit. Use **Apply changes** or **Download & apply** on your voice card, and **Apply subtitle choices** on the subtitle card. **Apply & start** can save pending own-voice choices and start when stopped. Home's mute-following and person-recognition checkboxes are staged too. **Save & apply** elsewhere saves all pending choices; Start/Stop remain direct actions. Appearance and interface-language changes keep their immediate behavior.

The interface-language picker is at the bottom of the sidebar, above the version number. Changes apply without restarting translation. This does not change either translation language or load a model. Existing saved interface choices are kept on updates unless an explicitly chosen installer launch applies a new choice. Shared installations provide a default for new profiles without editing other users' settings.

- **Home**: start your voice, subtitles or both. **Stop all** stops translation and releases the app's own models without closing VRChat.
- **Languages**: change the two translation languages independently, choose a concise or more careful style, and add names or special words. Select **Save & apply** to apply edits.
- **People**: add names and pictures for people you talk to. In the subtitle **Transcript**, use **Select a person** to assign the correct name; optional voice recognition can learn from suitable clear examples.
- **Settings**: choose what the close button does.
- **Advanced**: adjust sentence timing and audio, choose models, reuse an existing installation, measure translation delay or view troubleshooting details.

Your microphone translation pauses when your VRChat microphone is muted. An already-captured sentence can still finish and send, unless you turn that option off in Advanced. New settings translate completed sentences in both directions. Each direction has its own early-translation opt-in: microphone previews can send cumulative messages while you speak; subtitle previews can revise a bubble as more words arrive. Existing explicit choices are kept on updates. Neither mode repeatedly opens VRChat's chat input to write drafts.

Subtitles normally capture only VRChat; another application or All playback can be selected explicitly. New compact messages appear at the top. The default visible count is three; **Appearance → Visible messages** adjusts it from one to ten. Message text lets clicks pass through, while each message's name header keeps **Select a person** and ✓ / ✕ interactive. Use the separate toolbar for dragging, **Appearance**, **Always on top**, **Logs** and the quick menu. Appearance changes save immediately; background opacity does not fade the text. **Logs** opens a normal **Transcript** window with up to 200 recent messages, newest-first, selectable original/translated text, copy controls, name corrections and **Follow latest**. The transcript is separate from diagnostic logs. [Subtitle controls](../docs/subtitles.md).

**Only show Chinese speech** is beside **They speak** on Home and in Listening. It filters recognized incoming words before translation and leaves your microphone translator alone. Apply the choice while subtitles run; changing only this filter keeps models loaded. It can miss short or mixed speech and cannot repair an incorrect Chinese recognition result. New unrestricted settings leave it off; saved Chinese-only choices migrate to on unless you explicitly turned it off.

## Update, reset or uninstall

Open the installer again to update or repair a registered copy, or choose **Uninstall** to remove it without installing a replacement. It shows the detected version and path first. If an old app folder has no uninstall registration, setup reports it rather than pretending it is an installed copy. A copy in another installation scope must be managed or removed before creating a conflicting second copy.

Everything is kept by default. Normal **Just me** installation/removal offers three independent, unchecked choices: reset app preferences; remove saved speakers and translation log history; delete downloaded model files in the app cache. Review the summary and final confirmation before proceeding. Deleting cached models means those files need downloading again. Imported files outside the cache and Windows speech support remain unchanged.

All-users or already-elevated operations keep every user's personal settings and models, with private cleanup disabled. Each Windows user still has a separate profile. Custom data-folder locations that cannot be safely cleaned are also kept. Windows Installed apps and the Start menu uninstall shortcut offer the standalone removal workflow too.

Cancellation before removal changes nothing. If app removal or optional cleanup cannot be verified, personal data is kept where possible and setup reports the problem; it does not claim that a partial operation wiped everything or was rolled back.

Compatible 0.1.11+ apps, including tray-hidden ones, are asked to quit before updates or removal; setup waits for actual cleanup and exit. A pre-0.1.11 installation needs one manual **Quit SayWhat?** before updating. Other Windows users, unverified/custom elevated locations or incomplete cleanup require manual quit/retry rather than force-killing or proceeding unsafely.

## Close, keep running or quit

**Save & apply** briefly pauses active translation and restarts it with the selected settings. Its footer shows the actual stopping, saving, model-loading and speech-connection stages. Running tasks display elapsed time; after 30 seconds without a new stage, **Troubleshooting** provides access to details. Unknown-duration work has no estimated percentage. Theme changes apply separately and do not restart translation.

The first time you press **X**, SayWhat? asks whether to **Quit and release memory** or **Keep running in the tray**. You can remember either choice and change it later in Settings.

Keeping it in the tray hides the main window and leaves translation running. Double-click its icon beside the clock to reopen it. The right-click menu has **Stop translation** and **Quit SayWhat?**. Quitting waits for setup and translation tasks to stop, then releases only the app's own engines and model memory. Models loaded by an external application are not unloaded.

## Help the app recognize people

AI voice recognition is optional. On **Speakers**, select **Enable recognition** or **Turn off recognition**, without stopping either translation direction. Enabling it loads its model only when suitable clear speech arrives. Turning it off releases the voice-recognition model, not the speech or translation models. While it is off, the app does not guess speakers from their voices or position; you can still assign a name to a subtitle yourself.

Stopping recognition keeps saved names and voice fingerprints so you can use them again later. Use the saved-person controls on Speakers to delete a person and their confirmed voice examples.

With recognition enabled, choose a subtitle with one clear voice, then change its speaker to a saved person. The first learning correction asks you to confirm that just one person was speaking clearly. Later corrections learn automatically from suitable examples.

Short, unclear, overlapping or old examples may change only that sentence's label. **Music / recording** and **Unidentified speaker** never teach a person's voice. You can undo a recent correction from the same subtitle while its temporary history is still available.

Speech, saved names and voice fingerprints are processed on this PC and are not uploaded. Speaker learning saves local fingerprints, not audio files or transcript text. Deleting a saved person removes their confirmed examples; resetting unnamed voices preserves saved people. Changing the recognition model retains names but does not mix fingerprints from incompatible models. Translation logs can contain spoken or translated text: review them before exporting or sharing.

Recognizing a voice is not the same as separating overlapping speech. Live translation of two overlapping speakers as independently extracted voices is not enabled. Advanced offers an experimental local recording test, not a promise of live separation.

## Setup limits and troubleshooting

The app accepts Windows 10 21H2 (build 19044) or newer, and Windows 11, x64. Native Nemotron recognition and local translation do not require WSL for your own voice-to-VRChat workflow. FireRedASR2 subtitles also run natively. The optional Voxtral and Groups speech paths use app-managed WSL2 Ubuntu and may require administrator approval, virtualization support, a restart and additional downloads. Backend compatibility still depends on the selected engine and hardware.

VRChat-only and selected-application subtitles need the process-loopback API available on Windows 11 clients (Windows build 20348 or newer). Windows 10 can use subtitles only after explicitly choosing All playback, which includes sounds from other applications. The app never falls back to that wider capture scope automatically. Windows 10 compatibility is a preview and has not been verified on a real Windows 10 PC. Advanced manual setup remains available for existing compatible installations.

If translation stops, use **Stop all**, then start it again. Open **Advanced → Diagnostics** for connection and recovery details. Preview recovery safeguards reduce known stalls, but recognition accuracy, long-session reliability and game performance still need real-world testing.

Fresh installations store downloaded models, settings, saved people and logs in `%LOCALAPPDATA%\SayWhat`. An existing SayWhat folder takes precedence; otherwise an existing `%LOCALAPPDATA%\LocalTalk` folder is reused in place, without moving or duplicating models. A configured data-folder override still takes precedence over both defaults. Data is kept by default when uninstalling; optional just-me cleanup changes only the categories you confirm. Logs may contain spoken or translated text: review them before exporting or sharing.

Existing registered WSL engines and internal ownership markers remain compatible. The rebrand does not move caches or rebuild speech engines. Source project paths such as `LocalTalk/LocalTalk.csproj` are unchanged.

## Preview status

Automatic setup, cancellation/retry, restart checkpoints, tray choices, ownership rules and selected data-cleanup paths have offline tests. These do not prove a clean-PC install or all GPU combinations work. The installer is unsigned and may trigger a Windows reputation warning. Real just-me/all-users install, upgrade/removal, elevation/reboot behavior, GPU compatibility and longer live VRChat sessions still need acceptance testing before a wider release.

Source and third-party notices are included. See the research documents for model and overlapping-speech limitations. Advanced startup flags and the optional `Start-SayWhat.bat` launcher can explicitly start saved tools; opening `SayWhat.exe` normally remains idle.
