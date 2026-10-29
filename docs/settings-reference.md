# Settings reference

[Documentation index](README.md) · [Model selection](models-and-runtimes.md) · [Latency guide](performance-and-troubleshooting.md)

Defaults below describe a new packaged installation before setup changes them. Existing preferences are preserved during ordinary updates. Interface language can inherit the installer's selection. Values are not measured hardware recommendations.

Home's everyday voice controls apply their changes directly. Other settings pages use **Save & apply**; changing an Advanced control is not the same as saving it. Saving unchanged settings avoids unnecessary engine changes. Pinning subtitles, changing interface language/theme, changing names/photos, and toggling voice recognition have targeted paths that do not need to restart translation models. Do not edit files during an active session: some settings are snapshots read at startup.

The persisted application settings file is `vrchat-translation-settings.json` in the [per-user data directory](privacy-and-storage.md). The tables give its key names for technical inspection. They are settings, not a stable third-party API. UI labels may change or be translated; compatibility keys still use some older `Speaker` names.

## Everyday voice controls on Home

Click **I speak**, a target-language chip, a speech-model card or a translator card directly. Model choices show short explanations instead of hiding their names in an unlabeled dropdown. The microphone follows the Windows default unless you select another device.

Pause and early-message changes apply without reloading the speech/translation models. Language or microphone changes may briefly pause new capture while the current sentence finishes, then reconnect it with compatible weights still loaded. Model changes ask for missing downloads before switching; a shared translator change can pause both active directions after confirmation. Stopped features stay stopped when saving choices.

The optional subtitle card has its own source, target, preparation and Start action. **Speech model & language** opens **They speak**, target and recognition-model choices. Choose **Download & apply** when files are missing, or **Apply subtitle choices** when ready. Its settings do not need to be configured before using your own voice.

## Translation profiles

Choose a profile at the top of **Settings**, then **Apply profile**. The subtitle window offers a Normal/Groups switch; Quick and custom profiles are selected in Settings.

| Preset | Incoming speech | Subtitle settling / stable punctuation | Voxtral lookahead | Familiar-voice matching |
| --- | --- | --- | --- | --- |
| Quick | Chosen non-Groups recognizer | 800 / 250 ms | 160 ms when using Voxtral | Off |
| Normal | Chosen non-Groups recognizer | 1,200 / 500 ms | 240 ms when using Voxtral | On; optional and changeable |
| Groups | VibeVoice streaming | Keeps current values | Uses the selected streaming model's chunk/lookahead, not the Voxtral value | Keeps the current on/off choice |

Quick and Normal set the microphone final pause to 1,200 ms. Quick also selects Fast translation style. They preserve the selected subtitle recognizer and spoken language. Groups uses VibeVoice with automatic recognition and remembers the prior non-Groups recognizer/source; returning restores those choices. Translation models, targets, appearance and explicit early-translation choices are preserved. A fresh configuration starts final-only in every built-in profile. Your selected microphone recognizer is unchanged.

**Save current settings as…** captures current app preferences, including models, languages, timing and appearance. It does not copy downloaded files, saved People, voice examples or one-time setup/help acknowledgments. Up to 64 custom profiles are allowed, with unique names of 1–60 characters. **Update saved profile** explicitly replaces its snapshot; changing settings does not silently overwrite it. Built-in presets cannot be renamed or deleted. Deleting an active custom profile keeps its current settings as unsaved custom settings.

Older settings migrate into **Current settings** without applying a new preset. Modified presets stay modified until reapplied or saved as a custom profile; restarting the app does not reset them to the template.

| Key | New-install default | Meaning |
| --- | --- | --- |
| `ActiveTranslationProfileId` | `normal` | `quick`, `normal`, `groups`, a custom ID, or unsaved `custom`. |
| `CustomTranslationProfiles` | Empty list | Named settings snapshots, not model/audio copies. |
| `IncomingSpeechEngine` | `voxtral` | `voxtral`, `nemotron`, `firered2-ctc` or `firered2-aed` for Quick/Normal; `vibevoice` for Groups. Updates preserve a working existing selection. |
| `IncomingNormalSpeechEngine` | `voxtral` | Remembers the non-Groups recognizer when switching to Groups and back. |
| `IncomingNormalSourceLanguage` | `Automatic` | Remembers that recognizer's spoken language when switching to Groups and back. |
| `VibeVoiceModelId` | `microsoft/VibeVoice-ASR-Streaming-1.5B` | Optional 7B streaming checkpoint is also selectable under Groups model options. |
| `VibeVoiceEndpoint` | `http://127.0.0.1:8082` | Loopback HTTP only; no credentials, query or fragment. |

Groups preparation requires explicit approval for extra model/runtime downloads. Starting subtitles never silently installs missing Groups tools. Selecting or saving a profile while stopped does not itself load models or open capture.

## Languages and translator selection

| Setting / key | Default | Meaning and interactions |
| --- | --- | --- |
| Translate my voice to / `OwnVoiceTargetLanguage` | `Chinese` | Primary VRChat-chat target; Chinese output is Simplified Chinese. |
| Translate their voices to / `IncomingTargetLanguage` | `English` | Subtitle target, independent of the microphone target. |
| I speak / `OwnVoiceSourceLanguage` | `English` | Conditions native Nemotron recognition and the translator's source selection. Automatic is available for frequent language switching. Existing Voxtral setups retain automatic ASR; an explicit saved value is only a translation hint there. |
| They speak / `IncomingSourceLanguage` | `Automatic` | Subtitle source, independent of I speak. Nemotron sends the chosen language to recognition; Any language sends `auto`. FireRed allows Chinese/English and uses that choice for compatibility and translation context, not a monolingual ASR token. Voxtral and Groups remain automatic at recognition. Specialized text translators may require an explicit source. |
| Speech recognition / `OwnRecognitionEngine` | `nemotron` | Native Windows streaming recognition. `voxtral` retains the managed WSL path. An old configuration without this field migrates to Voxtral instead of silently replacing its working recognizer. |
| Translate my voice into more languages / `OwnVoiceAdditionalLanguagesEnabled` | `false` | Enables up to three additional chat targets, not subtitle targets. Home's chips update this choice automatically. |
| Extra targets / `OwnVoiceAdditionalTargetLanguages` | Empty list | At most three unique supported targets, excluding the primary: four outputs total. One line per successful translation in selected order; extra requests add latency and share the same chat-length budget. |
| Use the same model for both directions / `ShareTranslationModel` | `true` | One loaded model/endpoint for both directions. No shared conversation history. |
| Built-in translator / `UseManagedTranslator` | `true` in packaged app | Managed local runtime and downloads. Advanced external mode requires an already-running loopback server. |
| My voice model / `OwnManagedModelId` | `hy-mt2-1.8b-q4_k_m` | Managed translator catalog identifier. |
| Other voices model / `IncomingManagedModelId` | `hy-mt2-1.8b-q4_k_m` | Used only when sharing is off. |
| Translation engine / `ManagedRuntimeId` | `llama-vulkan` | `llama-vulkan`, `llama-cuda`, or `llama-cpu`; driver/runtime compatibility still needs checking. |
| Output style / `TranslationQuality` | `Fast` | Dashboard offers `Fast` and `Careful`. Careful allows more subtitle output tokens/time, not a different model. Older `Balanced`/`Quality` values remain recognized by subtitle request logic. |
| Glossary / `Glossary` | Empty | Up to 2,000 characters in the UI. Term mappings influence supported prompts only when a source term is present; they cannot restore missing ASR words. Dedicated Liquid/TranslateGemma adapters do not receive the generic glossary/context prompt. |

Supported target names are Chinese, English, Japanese, Korean, Spanish, French, German, Portuguese, Russian and Thai. The catalog's model capabilities may reduce these choices. Thai can be the only chat output or one of up to four ordered outputs; it does not need another Hy-MT2 model download. Liquid remains English/Japanese only. **I speak** guides Nemotron recognition and currently offers the other nine languages, not Thai: the pinned recognizer is not ready to transcribe Thai speech out of the box. Voxtral still detects speech automatically. The advanced **Translation language hint** field for a legacy specialized translator does not change that. The interface explains incompatible model/language pairs rather than silently selecting a different language.

For subtitles, **Any language** requires a compatible multilingual recognizer such as Nemotron. FireRedASR2 Fast and Accurate support Chinese and English, including switching between those languages; their picker deliberately does not offer Any language or Japanese/Korean. Choosing Chinese does not remove real English words from FireRed's transcript. Changing a spoken language refreshes the subtitle session so old buffered audio is not assigned to the new language; changing the incoming model does not change the microphone model. See [speech-model choices](models-and-runtimes.md#choose-a-subtitle-recognizer).

## Timing, microphone and playback

| Setting / key | Default | Dashboard range / effect |
| --- | --- | --- |
| My voice: silence before finalizing / `OwnVoiceEndPauseMilliseconds` | `1200` ms | 900–3,500 ms, 100 ms UI steps. Shorter finalizes earlier but splits natural pauses; longer waits for more context. Actual PCM quiet must reach the threshold. |
| Other voices: silence before finalizing / `IncomingSettleMilliseconds` | `1200` ms | 600–2,400 ms. Continuous subtitle punctuation/soft limits may commit sooner; FireRed uses quiet time or its bounded section limit. See [segmentation](architecture.md). |
| Stable subtitle punctuation / `SubtitlePreviewStableMilliseconds` | `500` ms | Stored range 150–1,200 ms; Quick sets 250 ms. Controls punctuation/draft stability when early translations are enabled; final-only mode uses finishing-pause, finalization and bounded-section rules. |
| Recognition lookahead / `RecognitionDelayMilliseconds` | `240` ms | 160, 240, 480 or 800 ms in the picker. Greater Voxtral lookahead adds recognition latency and may help accuracy; it is not the sentence-ending pause or VibeVoice chunk size. |
| Show translations while I speak / `OwnVoiceSendEarlyPreviews` | `false` | Off waits for finished sentences. Opting in sends cumulative chat previews followed by an authoritative final; wording may change as more speech arrives. It does not open/type into the chatbox editor. |
| Show early translations / `IncomingSendEarlyPreviews` | `false` | Off translates completed subtitle sections. Opting in can show and revise a translation before its section is finished. This is independent of microphone preview delivery. |
| Microphone / `OwnMicrophoneDeviceId` | `default` | Follows the Windows default capture device. The picker can save another microphone's unique display name; ambiguous or missing devices are not silently replaced by an unrelated one. |
| Finish captured speech after muting / `FinishCapturedSentenceOnMute` | `true` | New mic capture stops immediately; already-captured work may finish with one authorized final. False discards unfinished work. |
| Pause when my VRChat mic is muted / `OwnVoicePauseWhenVrchatMuted` | `true` | Home checkbox; applies live without reloading models. On follows verified VRChat mute state and keeps capture closed while state is unknown. Off explicitly allows SayWhat to capture and send translations while your VRChat mic is muted. Capture still stops when VRChat closes. Finish-captured-speech behavior applies when following mute is on. |
| Listen to / `IncomingAudioMode` | `vrchat` after normalization | `vrchat`, `application` or `desktop`. Choose an application with the searchable picker, or explicitly opt into All playback. A selected app closing pauses that capture rather than falling back to other apps. |
| Selected application / `IncomingApplicationPath`, `IncomingApplicationName` | Empty | Saves the chosen executable and display label, not a permanent process ID. An updated executable path may require selecting the application again. |
| Just one person / `IncomingSinglePerson`, `IncomingPersonId` | `false`, empty | Assigns subtitles to a selected saved person and bypasses automatic fingerprint matching. It does not teach a voice example by itself. |
| Legacy capture preference / `CaptureVrChatOnly` | `true` | Retained for old settings migration. Use the source picker rather than editing this compatibility key. Music played inside a selected application is still part of its audio. |
| Playback device / `PlaybackDeviceId` | `auto-vrchat` | Relevant to the wider playback mode. Application capture selects the process instead of an unrelated desktop device. Changing capture choices may reconnect subtitles. |
| Boost quiet speech / `NormalizeQuietPlayback` | `true` | Incoming recognition-only gain. Does not raise playback volume or alter the microphone. |

New settings wait for completed sentences in both directions. Recognition still listens continuously: final-only delivery does not stop listening between messages or wait for an entire conversation to end. Each early-translation option is an explicit opt-in; existing saved true/false choices are preserved. Built-in profile switches keep those choices, and custom profiles save them.

These pauses exclude subsequent recognition-final drain, translator queue/computation, and output scheduling. **1.2 seconds is not an end-to-end delivery promise.** The old untouched 2,400 ms microphone default migrates to 1,200 ms when the newer early-preview preference is absent; customized saved pauses are not replaced just because the app updates. A missing preview flag defaults off and does not infer consent from an older profile.

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
| Layout / `OverlayMode` | `Sentence bubbles` | The compact overlay shows the latest three messages. Retained `Per-speaker history` groups the Transcript by attribution and shows up to three recent voices in the compact surface; legacy `History` maps to this mode. `Latest sentence` shows one compact message. Transcript retention stays bounded to 200 messages in every layout. |
| Visible history lines / `HistoryLinesPerSpeaker` | `12` | Retained legacy preference, clamped to 3–40. It no longer controls compact display or Transcript retention, and never changes recognition context. |
| `VisibleSpeakerRows` | `8` | Retained legacy preference, clamped to 2–12. It no longer controls Transcript retention and is not a saved-People limit. |
| Always on top / `SubtitlesAlwaysOnTop` | `true` | The toolbar pin button saves this immediately for the compact toolbar and subtitle surface. Transcript and the dashboard are not forced always on top. |
| Font size / `SubtitlesFontSize` | `16` | 12–30 device-independent pixels for translated text in the compact overlay and Transcript. Original text uses a smaller size. |
| Bubble width / `SubtitlesBubbleWidth` | `420` | 240–900 device-independent pixels for the compact subtitle surface. |
| Background / `SubtitlesBackgroundOpacity` | `0.65` | 0–1 opacity for compact bubble backplates. Text remains opaque. |
| Original text / `SubtitlesShowOriginalText` | `true` | Show recognized source words above translations in the compact overlay and Transcript. Hiding them does not change recognition or translation. |

The compact subtitle body is click-through. **Logs** opens the separate interactive **Transcript** window, with selectable text, copying, name review and a **Follow latest** toggle. Closing Transcript hides that reading window; closing the compact subtitle toolbar stops incoming translation. A first-time help offer and **?** open the quick guide in Transcript without changing capture behavior.

Appearance controls apply without restarting services. Their scoped save patches only the four appearance values, preserving microphone configuration and unrelated model/language preferences. Ordinary dashboard saves and built-in profile selections merge the newest saved live-window appearance rather than reverting it or treating it as an unsaved dashboard draft. Explicitly applying a custom profile can restore that profile's appearance. Invalid or nonfinite font/width/opacity values fall back to defaults, and finite out-of-range values clamp to the bounds above. [Subtitle controls](subtitles.md).

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
| `DetectAnySourceLanguage` | `true` | Legacy text-prompt compatibility preference. Use They speak / `IncomingSourceLanguage` for current subtitle selection. This field does not itself configure an audio recognizer. |
| `SourceLanguages` | `[Chinese]` | Legacy text-prompt allowed-language list. It is neither the subtitle target nor a language-forcing control for FireRed/Voxtral. |

Advanced endpoints must be absolute loopback HTTP(S), without user-info credentials, query strings or fragments. Cloud URLs are refused. A model listed by `/models` is not sufficient if the server supports downloaded-but-unloaded listings: SayWhat? checks known local residency or a positive health acknowledgment rather than knowingly triggering an external just-in-time loader.

Generated `config.fast-chinese.jsonc` and `config.sentence-mode.jsonc` are compatibility files consumed by the capture host. Their historical filenames do **not** determine the current target language. Saved application choices rewrite the relevant generated endpoint/model/pause/prompt fields; use the dashboard as the source of truth.
