# Settings reference

[Documentation index](README.md) · [Model selection](models-and-runtimes.md) · [Latency guide](performance-and-troubleshooting.md)

Defaults below describe a new packaged installation before setup changes them. Existing preferences are preserved during ordinary updates. Interface language can inherit the installer's selection. Values are not measured hardware recommendations.

Prefer editing in the dashboard and choosing **Save & apply**. Changing a control is not the same as saving it. Saving unchanged settings avoids unnecessary engine changes; relevant changes can require stopping/restarting affected active tools. Pinning subtitles, changing interface language/theme, changing names/photos, and toggling voice recognition have targeted paths that do not need to restart translation models. Do not edit files during an active session: some settings are snapshots read at startup.

The persisted application settings file is `vrchat-translation-settings.json` in the [per-user data directory](privacy-and-storage.md). The tables give its key names for technical inspection. They are settings, not a stable third-party API. UI labels may change or be translated; compatibility keys still use some older `Speaker` names.

## Translation profiles

Choose a profile at the top of **Settings**, then **Apply profile**. The subtitle window offers a Normal/Groups switch; Quick and custom profiles are selected in Settings.

| Preset | Incoming speech | Subtitle settling / stable punctuation | Voxtral lookahead | Familiar-voice matching |
| --- | --- | --- | --- | --- |
| Quick | Voxtral | 800 / 250 ms | 160 ms | Off |
| Normal | Voxtral | 1,200 / 500 ms | 240 ms | On; optional and changeable |
| Groups | VibeVoice streaming | Keeps current values | Uses the selected streaming model's chunk/lookahead, not the Voxtral value | Keeps the current on/off choice |

Quick and Normal set the microphone final pause to 1,200 ms. Quick also selects Fast translation style. Presets do not replace chosen translation models, target/source languages or appearance. Groups changes the incoming speech engine only; your microphone remains on Voxtral.

**Save current settings as…** captures current app preferences, including models, languages, timing and appearance. It does not copy downloaded files, saved People, voice examples or one-time setup/help acknowledgments. Up to 64 custom profiles are allowed, with unique names of 1–60 characters. **Update saved profile** explicitly replaces its snapshot; changing settings does not silently overwrite it. Built-in presets cannot be renamed or deleted. Deleting an active custom profile keeps its current settings as unsaved custom settings.

Older settings migrate into **Current settings** without applying a new preset. Modified presets stay modified until reapplied or saved as a custom profile; restarting the app does not reset them to the template.

| Key | New-install default | Meaning |
| --- | --- | --- |
| `ActiveTranslationProfileId` | `normal` | `quick`, `normal`, `groups`, a custom ID, or unsaved `custom`. |
| `CustomTranslationProfiles` | Empty list | Named settings snapshots, not model/audio copies. |
| `IncomingSpeechEngine` | `voxtral` | `voxtral` for Quick/Normal; `vibevoice` for Groups. |
| `VibeVoiceModelId` | `microsoft/VibeVoice-ASR-Streaming-1.5B` | Optional 7B streaming checkpoint is also selectable under Groups model options. |
| `VibeVoiceEndpoint` | `http://127.0.0.1:8082` | Loopback HTTP only; no credentials, query or fragment. |

Groups preparation requires explicit approval for extra model/runtime downloads. Starting subtitles never silently installs missing Groups tools. Selecting or saving a profile while stopped does not itself load models or open capture.

## Languages and translator selection

| Setting / key | Default | Meaning and interactions |
| --- | --- | --- |
| Translate my voice to / `OwnVoiceTargetLanguage` | `Chinese` | Primary VRChat-chat target; Chinese output is Simplified Chinese. |
| Translate their voices to / `IncomingTargetLanguage` | `English` | Subtitle target, independent of the microphone target. |
| Spoken language / `OwnVoiceSourceLanguage`, `IncomingSourceLanguage` | `Automatic` | Source-language choice for each translation direction. Explicit sources are required by Liquid EN–JP and TranslateGemma adapters. |
| Translate my voice into more languages / `OwnVoiceAdditionalLanguagesEnabled` | `false` | Enables a second/third target for your chat output, not subtitles. |
| Extra targets / `OwnVoiceAdditionalTargetLanguages` | Empty list | At most two unique supported targets, excluding the primary. One line per successful translation in selected order; extra requests add latency and compete for the same chat-length budget. |
| Use the same model for both directions / `ShareTranslationModel` | `true` | One loaded model/endpoint for both directions. No shared conversation history. |
| Built-in translator / `UseManagedTranslator` | `true` in packaged app | Managed local runtime and downloads. Advanced external mode requires an already-running loopback server. |
| My voice model / `OwnManagedModelId` | `hy-mt2-1.8b-q4_k_m` | Managed translator catalog identifier. |
| Other voices model / `IncomingManagedModelId` | `hy-mt2-1.8b-q4_k_m` | Used only when sharing is off. |
| Translation engine / `ManagedRuntimeId` | `llama-vulkan` | `llama-vulkan`, `llama-cuda`, or `llama-cpu`; driver/runtime compatibility still needs checking. |
| Output style / `TranslationQuality` | `Fast` | Dashboard offers `Fast` and `Careful`. Careful allows more subtitle output tokens/time, not a different model. Older `Balanced`/`Quality` values remain recognized by subtitle request logic. |
| Glossary / `Glossary` | Empty | Up to 2,000 characters in the UI. Term mappings influence supported prompts only when a source term is present; they cannot restore missing ASR words. Dedicated Liquid/TranslateGemma adapters do not receive the generic glossary/context prompt. |

Supported target names are Chinese, English, Japanese, Korean, Spanish, French, German, Portuguese and Russian. The catalog's model capabilities may reduce these choices. A source-language restriction affects translation selection/prompting; it is not evidence that Voxtral can enforce an arbitrary custom recognition-language grammar.

## Timing, microphone and playback

| Setting / key | Default | Dashboard range / effect |
| --- | --- | --- |
| My voice: silence before finalizing / `OwnVoiceEndPauseMilliseconds` | `1200` ms | 900–3,500 ms, 100 ms UI steps. Shorter finalizes earlier but splits natural pauses; longer waits for more context. Actual PCM quiet must reach the threshold. |
| Other voices: silence before finalizing / `IncomingSettleMilliseconds` | `1200` ms | 600–2,400 ms. Subtitle punctuation/soft limits may commit sooner; see [segmentation](architecture.md). |
| Stable subtitle punctuation / `SubtitlePreviewStableMilliseconds` | `500` ms | Stored range 150–1,200 ms; Quick sets 250 ms. Separate from the silence pause. |
| Recognition lookahead / `RecognitionDelayMilliseconds` | `240` ms | 160, 240, 480 or 800 ms in the picker. Greater Voxtral lookahead adds recognition latency and may help accuracy; it is not the sentence-ending pause or VibeVoice chunk size. |
| Show translations while I speak / `OwnVoiceSendEarlyPreviews` | `true` | Cumulative submitted chat previews followed by an authoritative final. Off means final-only and fewer speculative translator requests. It does not open/type into the chatbox editor. |
| Finish captured speech after muting / `FinishCapturedSentenceOnMute` | `true` | New mic capture stops immediately; already-captured work may finish with one authorized final. False discards unfinished work. |
| Capture VRChat playback only / `CaptureVrChatOnly` | `true` | Per-process loopback by default. Off permits wider Windows playback, including unrelated applications. VRChat-hosted music remains part of VRChat output. |
| Playback device / `PlaybackDeviceId` | `auto-vrchat` | Automatic follows VRChat; explicit device IDs select a playback route. Changing devices may reconnect capture. |
| Boost quiet speech / `NormalizeQuietPlayback` | `true` | Incoming recognition-only gain. Does not raise playback volume or alter the microphone. |

These pauses exclude subsequent recognition-final drain, translator queue/computation, and output scheduling. **1.2 seconds is not an end-to-end delivery promise.** The old untouched 2,400 ms microphone default migrates to 1,200 ms when the newer early-preview preference is absent; customized saved pauses are not replaced just because the app updates.

## Optional voice recognition

| Setting / key | Default | Range / behavior |
| --- | --- | --- |
| Voice recognition / `UseAiSpeakerRecognition` | `true` | Optional local CPU identity model; loading is deferred until subtitles need a clear speech sample. Background matching does not block translation. Off unloads it and retains People. Translation/manual labeling remain available, including Groups session labels. |
| Voice recognition model / `SpeakerModelPath` | Managed CAM++ Mandarin ONNX | Choose CAM++ or ERes2Net in the UI, download it, or import a compatible ONNX embedding model. Arbitrary ONNX is not necessarily compatible. |
| Match threshold / `SpeakerMatchThreshold` | `0.50` | Dashboard/recognizer 0.35–0.85. Higher is stricter and leaves more voices unidentified; lower may merge different people. It is cosine similarity, not an accuracy probability. |
| Unnamed voice profile limit / `MaxSpeakerProfiles` | `2` | 2–8. Caps anonymous automatically learned identities; it does **not** cap all saved People. Increasing it can create more false identities. |

No setting here separates audio waveforms. Reset unnamed voices preserves manually saved people and their confirmed examples. A model change uses a different fingerprint space: names/photos remain, but old examples do not automatically become examples for the new model. See [People and voice recognition](people-and-voice-recognition.md).

## Subtitle display

| Setting / key | Default | Behavior |
| --- | --- | --- |
| Layout / `OverlayMode` | `Sentence bubbles` | Independent committed subtitle bubbles. `Per-speaker history` groups by attribution; legacy `History` maps to this mode. The service also recognizes legacy `Latest sentence`. |
| Visible history lines / `HistoryLinesPerSpeaker` | `12` | 3–40; limits visible retained entries, not ASR context/model memory. |
| `VisibleSpeakerRows` | `8` | Legacy/display setting clamped to 2–12, relevant to grouped history. Not the saved-People limit. |
| Always on top / `SubtitlesAlwaysOnTop` | `true` | Subtitle-window pin button toggles this immediately and saves it. Dashboard is not forced always on top. |

The subtitle history scrolls to recent output. Closing the subtitle window stops that direction; it is not simply hiding an invisible playback recorder. A first-time help offer and the **?** button describe names/corrections without altering capture behavior.

## App appearance, closing and updates

| Key | Default | Choices / behavior |
| --- | --- | --- |
| `UiLanguage` | `en`, or installer choice | `en`, `zh-CN`, `ja`, `ko`. Separate from translation targets. |
| `UiThemeId` | `coral-night` | Coral Night, Ink, Forest, Plum, Paper, Sand, Mint, High Contrast, Custom. |
| `CustomThemeBackground` | `#191817` | Custom `#RRGGBB` background color. |
| `CustomThemeTextPalette` | `auto` | Automatic, warm/cool light text, neutral/warm dark text. Shared text roles preserve different emphasis levels; contrast correction can adjust final colors. |
| `CustomThemeImagePath` | Empty | User-selected local background image; distinct from person photos. |
| `CustomThemeImageOpacity` | `0.16` | 0–0.6, bounded on load. |
| `CloseButtonBehavior` | `Ask` | Ask, Quit, Tray. Quit stops owned tools/unloads managed models; Tray intentionally keeps them running. |
| `CheckForUpdatesOnLaunch` | `true` | Once each normal app startup checks release metadata; no unattended installation without approval. |

`PauseOnMinimize` is retained as a legacy preference (default `false`); do not rely on it to release models. Use **Stop all** or **Quit**, not ordinary minimization, when you need GPU memory.

Setup/help bookkeeping keys are `FirstRunSetupCompleted`, `FirstRunIntroductionCompleted`, and `SubtitleGuidePromptSeen` (initially `false`). They control dedicated setup and one-time offers, not engine permission. Use **Settings → Run setup again** instead of manually changing them.

## Advanced local translator compatibility keys

| Keys | Initial fallback | Effect |
| --- | --- | --- |
| `OwnTranslatorBaseUrl` | `http://localhost:1234/v1` | External my-voice endpoint; managed startup replaces this with its planned loopback endpoint. |
| `TranslatorBaseUrl` | `http://127.0.0.1:1234/v1` | External incoming endpoint when sharing is off. |
| `OwnTranslatorModel`, `TranslatorModel` | `tencent/hy-mt2-1.8b` | OpenAI-compatible aliases; may differ from local GGUF filenames. Managed planning sets the catalog aliases. |
| `DetectAnySourceLanguage` | `true` | Incoming automatic-language behavior. |
| `SourceLanguages` | `[Chinese]` | Legacy allowed-language preference, relevant when automatic detection is disabled. It is not the translated target language. |

Advanced endpoints must be absolute loopback HTTP(S), without user-info credentials, query strings or fragments. Cloud URLs are refused. A model listed by `/models` is not sufficient if the server supports downloaded-but-unloaded listings: SayWhat? checks known local residency or a positive health acknowledgment rather than knowingly triggering an external just-in-time loader.

Generated `config.fast-chinese.jsonc` and `config.sentence-mode.jsonc` are compatibility files consumed by the capture host. Their historical filenames do **not** determine the current target language. Saved application choices rewrite the relevant generated endpoint/model/pause/prompt fields; use the dashboard as the source of truth.
