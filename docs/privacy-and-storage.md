# Privacy and local storage

[Documentation index](README.md) · [People](people-and-voice-recognition.md) · [Install and uninstall](install-and-updates.md)

## Local inference is not “no network requests ever”

Audio recognition, text translation and optional voice identification run locally. SayWhat? does not upload microphone/playback audio, saved people or voice examples as part of inference, hardware advice, update checking or model download.

Online operations include downloading pinned model/runtime files, Windows/Linux setup dependencies, and checking release metadata/downloading an approved update. Those services can observe ordinary connection metadata such as IP address and request paths. The website does not use analytics; its language preference is remembered locally. Model license pages, public download hosts and VRChat itself have their own policies.

Advanced translation URLs are restricted to local loopback. A local third-party server is outside SayWhat?'s process ownership; its own plugins, logs and network behavior remain the user's responsibility. Loopback binding is not encryption or protection against every process running on the same Windows account.

## Data location

Ordinary packaged installations use **`%LOCALAPPDATA%\SayWhat`**, per Windows user even with an all-users installation. This is separate from the program install directory. If an existing legacy `%LOCALAPPDATA%\LocalTalk` directory is present and the current directory is absent, compatibility resolution can continue using that legacy location rather than silently merging them.

Technical installations can override the root with `SAYWHAT_DATA_ROOT` (legacy `LOCALTALK_DATA_ROOT` remains supported). This is not a recommended way to move a live model cache. Installer safety checks can refuse ambiguous/unsafe cleanup rather than guessing where redirected or externally owned data belongs.

| File / folder under the root | Contents |
| --- | --- |
| `vrchat-translation-settings.json` | App choices, model selections, language/theme and one-time help preferences |
| `provisioning.json` | Managed/imported asset registrations, runtime and WSL speech registration |
| `setup-progress.json` | Resumable setup stage/restart state |
| `incoming-speaker-feedback.json` | Names, photo leaf names, confirmed embeddings/model provenance and aggregate counters |
| `incoming-ai-speaker-profiles.json` | Anonymous model-specific voice profiles |
| `people-photos/` | Sanitized, size-limited user photo copies with generated names |
| `hidden-speech-shortcuts/` | Recoverable copies of verified internal speech-tool Windows shortcuts removed from Start search |
| `models/` | Managed speech/translation weights; `speaker-id/` and `separation/` hold optional models |
| `runtimes/` | Downloaded local translation-engine files |
| `downloads/`, `updates/` | Setup downloads and verified/staged application-update installers |
| `templates/` | Managed specialized-model task template files |
| `config.fast-chinese.jsonc`, `config.sentence-mode.jsonc` | Generated microphone-host compatibility configuration |
| `*.log` | Setup/runtime/provider/translation diagnostics |

These filenames are descriptive technical details, not a supported manual-edit API. WSL also owns a Linux filesystem and installed build components outside this Windows folder. Removing only the program executable is not equivalent to removing those components or every cached asset.

## Audio, embeddings and conversations

Live audio is buffered in memory with bounded queues; normal operation does not save raw audio recordings. Confirmed voice references are compact embeddings with their model identity and observation/quality metadata, not clips used for model-weight training. Embeddings can still be sensitive personal data; treat a copied profile database accordingly.

Subtitle history and transcripts are displayed in memory, and incoming diagnostic logs **can persist source and translated text**. Some matching logs also contain names. The microphone host uses state/timing/count metadata rather than recording raw speech text, but not every log in the whole application is transcript-free. **Copy diagnostics** generates a whitelist-based summary without conversation text, names, file paths or raw exceptions. Detailed log exports still need review before sharing.

Optional separator evaluation reads an explicitly selected local WAV. Choosing **Export anonymous sources** writes two estimated recordings to the user's selected files; that explicit export is different from ordinary live capture's no-recording behavior. Do not share somebody else's voice samples without appropriate permission.

## Pictures and cleanup

Photos are resized/cropped, metadata-stripped and compressed before saving. Originals, clipboard text/URLs, and remote profile-image URLs are not stored as a person's picture. Deleting/replacing a photo never removes the original file. See [picture limits](people-and-voice-recognition.md#pictures).

Deleting a saved person removes that person's saved references/photo association. **Reset unnamed voices** is narrower: it preserves named people. Uninstall/update data choices are explicit and keep data by default. Clearing saved people/history includes app-managed photos and the relevant records/logs; clearing downloaded model files is a separate choice and does not authorize deleting an imported file owned outside the app cache.

File deletion is not secure erasure. Backups, Windows restore mechanisms or previously exported diagnostics may retain copies. Ordinary local storage is protected by Windows account/filesystem permissions, not an app-specific encrypted vault.
