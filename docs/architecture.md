# Architecture and local protocols

[Documentation index](README.md) · [Settings](settings-reference.md) · [Performance](performance-and-troubleshooting.md)

## Two independent directions

SayWhat? is a Windows WPF desktop application. It manages local processes, a subtitle window, settings and model downloads. It does not require a separately installed LM Studio or FoxTrans application in the normal built-in setup.

```text
Your voice
  Microphone → capture/mute gate → Nemotron streaming ASR (Windows, default)
                               or Voxtral realtime ASR (WSL, existing/optional)
             → local translation → OSC sentence proxy → VRChat chat

Other voices
  Chosen application playback → retained original mixed audio
    Quick / Normal → chosen subtitle recognizer → source text
                    Nemotron streaming / FireRedASR2 sections / legacy Voxtral
    Groups         → VibeVoice streaming ASR → text per temporary voice ID
      source text → local translation → separate subtitle bubbles
      usable original audio → optional saved-person matching → bubble names/photos
```

ASR means converting audio into source-language text; translation converts that text to the chosen target language. These are distinct models and failure stages. A larger translator cannot reconstruct words the speech recognizer omitted.

The directions can run separately. When both run, they use independent recognizers and audio state. By default they share one loaded translation model, but their requests do not share conversation text. Optional separate translators duplicate model residency; they are not a speaker-separation feature. Normal/Quick retain the chosen subtitle recognizer; Groups temporarily uses VibeVoice and remembers the non-Groups recognizer/source for return. Saved custom profiles also restore their recorded choices.

First-time setup prepares the microphone direction, not both directions. Subtitles have a separate preparation path for their own speech assets and optional People/Groups components. Keeping subtitles stopped does not require installing those components. Preparing files and inspecting hardware do not start inference or open the microphone.

## Your microphone

The capture host uses a local fork of FoxTrans Core. By default it starts muted until the VRChat process and current mute state are verified. The controller discovers current VRChat-owned local TCP status listeners directly, with current-game log hints only as a fallback for HTTP.sys listeners; a populated game log is not required for normal discovery. Unknown state is retried with bounded backoff. Fresh mute events take priority over a pending status query. Home's **Pause when my VRChat mic is muted** checkbox can disable mute-following explicitly: SayWhat can then capture and send while the VRChat mic is muted, but capture still stops when the game closes. The choice applies without a model reload. Capture has a 300 ms pre-roll and opens an ASR turn after two consecutive voiced frames. The native microphone gate uses an RMS threshold of 0.006; this is an implementation policy, not a microphone-volume setting exposed in the dashboard.

The new-install recognizer is native Windows Nemotron Streaming. The **I speak** choice conditions its recognition requests; Automatic remains available. Voxtral is retained for compatible existing installations and optional speech workflows. It detects the audio language automatically: a preserved explicit language in an older configuration can serve as a translator hint, but does not pin Voxtral's recognition language. Liquid and TranslateGemma still require a valid explicit translation-source language.

A turn contains cumulative text for the current utterance. Actual captured silence, not unchanged text or a wall-clock guess, determines the configured finalization boundary. Each recognition adapter maps its engine's partial/final protocol into the capture host. In the Voxtral path, the host sends `input_audio.end` and waits for **both** `transcript.final` and `session.completed` before using the definitive transcript. That server performs its required final padding rather than the client appending another full silence interval.

While recognition/translation drains, capture can retain two pending turns in order. Each unread turn is bounded to 15 seconds of PCM, with byte and frame-count limits. Once all pending slots are full, genuinely excess new audio is skipped with a reported backpressure outcome; capture resumes when a slot opens. An individual unread-buffer overflow fails only that turn, not already queued sentences. Empty finals, ordinary turn timeouts and translation-provider errors do not automatically discard a healthy queued next turn.

Ongoing voiced input without useful new recognition text has an eight-second progress deadline, requiring at least two seconds of additional voiced PCM. Duplicate, empty or punctuation-only callbacks do not rearm it; silence, mute and final padding do not trigger it. Recoverable turn failures use a short cooldown and do not restart the speech model process.

The default is finished-sentence translation: recognition remains continuous, but speculative text is not sent for translation or OSC output. **Show translations while I speak** is an explicit opt-in. When on, speculative requests use the complete utterance heard so far. The scheduler keeps one active request and one latest pending revision, coalesces duplicates, and permits useful in-flight output while new ASR tokens arrive. New previews start no faster than every 1.5 seconds. The definitive final cancels/preempts speculative work; late previews cannot replace it.

When early delivery is enabled, the OSC proxy sends those translations as submitted chat bubbles, **not** as text typed into VRChat's editor. Updated cumulative bubbles may repeat or revise earlier wording. It coalesces output at a conservative 1.5-second cadence and gives the final priority. This is an app policy, not a documented VRChat rate-limit guarantee. If the final equals the latest displayed preview, clearing the typing indicator is sufficient; no duplicate bubble is needed. With the option off, only finished-sentence output is submitted.

Home choice events update drafts only; they do not start a debounce timer that commits settings, downloads models or restarts translation. The own-voice card's **Apply changes** / **Download & apply** commits its scoped candidate. When stopped, **Apply & start** can explicitly commit that candidate and start the microphone direction. The subtitle card stages its own choices, including person recognition, until **Apply subtitle choices**. **Save & apply** elsewhere applies all drafts. Start/Stop remain explicit direct actions, while appearance/interface preferences and recognition switches outside Home retain their immediate behavior.

After an explicit apply, pause/early-message/mute-following changes can update the existing host without unloading speech or translation models. Language or microphone changes let captured work finish, then reconnect the affected capture path while compatible weights remain loaded. Model changes prepare missing files first; replacing a shared translator can briefly pause both active directions after confirmation. None of these paths makes two GPU models free of resource contention.

The dashboard persists the full settings library separately from a small derived `own-voice-runtime.json` worker view. Both the mute controller and capture host read that bounded view, so saved custom profiles cannot cause a large main settings file to exceed the worker's 64 KiB read limit. Legacy standalone roots can use the old main file only when no derived file exists; an invalid derived file fails visibly rather than restoring stale mute choices.

### Support evidence without conversation content

The controller and capture host emit strict `SAYWHAT_STATUS` lifecycle/progress records; diagnostics also classify known relay failures from its existing logs. These distinguish a child starting, its control listener becoming ready, actual captured frames, recognition finals, translation work and locally queued chat. Progress uses numeric counts/timings, never the words. A process being alive or control-ready does not establish successful capture, and local OSC submission is not a game delivery acknowledgment. Older nested stdout without reliable component provenance remains an unknown failure rather than receiving an invented microphone cause.

Diagnostics retain a bounded latest record per component/event and fixed known error categories/codes. **Save support report…** combines this with minimal effective settings, platform capabilities, expected-file checks and bounded health checks of retained, already-running owned local services. It does not start models, capture audio or test chat. A separate automatic last-failure snapshot performs file checks only and stays local. See [privacy and storage](privacy-and-storage.md) and [troubleshooting](performance-and-troubleshooting.md#useful-diagnostics).

## VRChat playback and subtitles

The default playback source is Windows per-process loopback capture for VRChat. The searchable application picker can instead select another running application; a separate All playback choice captures wider Windows audio. Process-specific loopback needs Windows build 20348+ (Windows 11 clients); this gate is independent of the Windows 10 build 17763+ installation floor for native own-voice translation. Optional WSL speech requires build 19041+ and WSL 2 version 0.48.2 or newer; CUDA in WSL requires build 19044+. Windows 10 users can explicitly choose All playback, but the app never silently broadens the capture scope. Selected-application capture waits for that application if it closes rather than falling back to unrelated audio. It includes sounds/music played **inside the selected application** and does not create an individual stream for each person. One-person mode assigns subtitles to a selected saved person and skips automatic voice matching; choosing that name is not a voice-learning example.

Playback is resampled for 16 kHz recognition. Quiet-audio normalization affects the samples sent to recognition, not the user's speaker/headphone volume. The raw audio backlog is capped around two seconds; overloaded playback drops old chunks to retain recent speech rather than growing minutes behind live audio.

**Home → Speech model & language** opens independent incoming **They speak**, target and recognizer choices. **Download & apply** prepares missing files; **Apply subtitle choices** uses prepared files. Changing the source creates a fresh subtitle audio/text context so old buffered audio is not interpreted under the new language. Changing recognizers pauses only the incoming pipeline, preserves displayed history, and attempts to restore the prior mode if the replacement cannot start. An unchanged compatible translator remains loaded. These actions do not change microphone recognition settings.

Native incoming Nemotron uses the same pinned Windows engine as the microphone, with its own process/session and managed loopback address. The selected explicit language is sent in `session.update`; Any language sends `auto`. Configuration requires `session.created` then `session.updated`. Audio remains continuous through silence. Empty progress deltas update transport activity but do not count as words. Authoritative completed text replaces speculative partial wording; a requested context handoff requires a final transcript and commit acknowledgment. The model is kept resident for a source-language session change, while the subtitle service and capture context restart.

FireRedASR2 CTC/AED are segment recognizers hosted inside the subtitle service. Capture continues while one CPU decode runs. Growing section drafts are coalesced, and a final section has priority over its drafts. Sections are bounded to eight seconds; the configured quiet interval can finish one sooner. Longer uninterrupted speech becomes another bounded section rather than one indefinitely growing audio request. The work queue retains at most three finished sections plus the latest draft and reports when overload skips an old section. These bounds favor staying near live audio; they cannot restore skipped speech or guarantee that a CPU can keep up.

FireRed's Chinese/English selection supplies translator context and checks compatibility; it does not force monolingual decoding. A Chinese sentence can retain real English words. No FireRed language-forcing field exists in this adapter. Original capture intervals are retained for optional person matching, but this recognizer still receives mixed audio and does not separate overlapping voices.

`IncomingChineseOnly` is a separate post-recognition filter, off for new unrestricted settings. It limits which recognized incoming text proceeds to translation, rather than changing a model's supported languages or forcing its audio decoder to Chinese. It can stay enabled through Groups' automatic-language selection. Filter-only changes use the live settings update and do not reload speech/translation models or alter the microphone direction. The recognition text is imperfect evidence of language: short/mixed speech can be rejected, and Han-only Japanese or ASR-hallucinated Chinese can pass. See the [migration and UI behavior](settings-reference.md#incoming-language-filter).

For the continuous Nemotron/Voxtral routes, subtitle segmentation tracks a watermark in cumulative recognition text. It commits a bubble when decoded audio confirms the configured finishing pause, recognition finalizes, or bounded clause/length/duration limits are reached. With early translations explicitly enabled, stable sentence punctuation can also commit a bubble; its configurable wait is 500 ms in Normal and 250 ms in Quick. Early mode also has a seven-second soft duration limit. Both modes keep hard text bounds of about 220 characters or 80 CJK characters; elapsed time alone never finishes a default-mode draft. Consequently, **Other voices: silence before finalizing** is not the only way a subtitle can become a new bubble. By default only completed sections are translated. The independent **Show early translations** opt-in allows useful stable interim text to translate and revise the current bubble without resetting recognition. FireRed uses its section identity for draft/final revisions of the same bubble, with draft translation likewise controlled by this preference.

Translation has a bounded eight-item queue and a single reader. Committed sentences wait for capacity with visible backlog feedback rather than being silently dropped to make room. Obsolete interim revisions can be skipped or canceled; newer drafts do not invalidate already committed sentences. Optional voice matching runs in a separate bounded background queue, so translation does not wait for identity inference. A late safe match changes the current bubble's suggested name/photo, not its translated words, and cannot overwrite a manual correction. From 0.1.26, suggested names are tentative until reviewed. They neither share saved-person translation context nor increment that person's translation total. Group text context remains scoped to its session track, independently of a guessed name.

Presentation separates compact message text from its toolbar and per-message name actions. Text lets clicks pass through; the name header retains interactive correction/review controls. The overlay is newest-first and shows three messages by default, with a saved range of one to ten; the selected count governs every legacy layout. An old one-message layout migrates to a count of one only when no explicit count exists. A separate ordinary, non-topmost Transcript window retains up to 200 recent messages, newest-first, for selection, copying, name correction and review. Legacy grouping applies only to Transcript and does not change that retention limit. Background opacity applies to the bubble backplate, leaving text opaque. Original recognized text can be shown above the translation, so a user can distinguish recognition errors from translation errors. Appearance preferences are atomically patched without rewriting microphone configuration or unrelated saved choices. These display/history limits do not change recognition context or audio buffering.

In the continuous speech paths, worker supervision, audio-device reprobes and a speech-progress watchdog attempt recovery when a recognizer connection or capture worker stops progressing. Subtitle retries require recent speech and use a 20/20/30/40-second capped cooldown rather than a permanently exhausted two-attempt allowance. Useful changed recognition text—not padding, duplicate snapshots or decoder-reset acknowledgments—rearms progress. FireRed also reprobes the selected capture application/device; it does not stop capture merely because a decode returns no words.

Voxtral final recognition can wait up to eight seconds when no newer turn is waiting. If newer speech queues during that wait, the finalization deadline shortens to 1.2 seconds so old stalled work does not consume the whole fresh-audio buffer. Queues remain bounded: this cannot restore audio the recognizer never processed, and audio drops are reported. A visible recovery state indicates an attempt, not proof that useful words returned. See [diagnostics](performance-and-troubleshooting.md).

### Groups: attributed text, not separated audio

Groups sends continuous mixed playback to a local **VibeVoice ASR Streaming** service. The model returns text with temporary voice labels. SayWhat? maintains separate sentence state, translation requests and bubbles for those labels. The labels belong to that recognition session; they are not saved People IDs or VRChat accounts.

SayWhat? retains original audio long enough to relate replies to it. The wrapper reports the span of the **whole input chunk**, not word-level or isolated-person timestamps. Several attributed voices in one chunk therefore do not provide separate clean recordings. Such audio is excluded from saved-person fingerprint learning. An unlabelled reply can still be translated, but does not create a shared person identity or train saved people.

When a chunk contains one labelled voice and passes the existing clear-audio checks, optional fingerprint matching can tentatively link its session label to a saved person. Explicit message assignments take precedence and remain useful with fingerprinting off. They do not confirm future messages on the track. Unmatched or unsafe identity results revoke old automatic links; rejected suggestions suppress that name on the temporary track, including late results. With fingerprinting off, future messages do not inherit named guesses. Mixed, short or ambiguous evidence stays unconfirmed; a label can update a bubble without saving a voice example. These checks are conservative heuristics, not proof that all overlap was detected.

The configured streaming chunk plus lookahead requires roughly **3.5 seconds of audio before the first decode**, before inference and translation time. This is not a 3.5-second end-to-end promise. Groups is optional and adds downloads, memory use and delay. It may miss or conflate voices during overlap, music or difficult audio. Switching profiles stops only the affected owned services; choosing Groups is not evidence of measured in-game performance.

## Local endpoints

Managed speech and translation services use automatically selected loopback TCP ports by default. Their effective addresses belong to the running services and may change after a restart; clients use those addresses rather than assuming the historical port numbers. This does not change a separately configured external translator URL or the OSC ports.

Advanced fixed-port mode disables automatic selection and uses the saved port values. The numbers below are that mode's initial defaults, not guaranteed addresses for an automatic session. See [connection settings](settings-reference.md#managed-local-connections).

| Service | Initial fixed-mode address | Purpose |
| --- | --- | --- |
| Microphone speech | `127.0.0.1:8080` HTTP/WebSocket | Native Nemotron by default, or legacy Voxtral |
| Incoming speech | `127.0.0.1:8081` HTTP/WebSocket | Incoming Nemotron or legacy Voxtral; FireRed runs in-process without this listener |
| Groups speech | `127.0.0.1:8082` HTTP/WebSocket | Optional incoming VibeVoice service |
| Own-voice translation | `127.0.0.1:1235/v1` HTTP | Built-in translator; shared by subtitles when sharing is on |
| Separate incoming translation | `127.0.0.1:1236/v1` HTTP | Built-in subtitle translator when sharing is off |

| Unchanged OSC endpoint | Purpose |
| --- | --- |
| `127.0.0.1:9010` UDP | Internal OSC sentence proxy input |
| `127.0.0.1:9011` UDP | Internal microphone-host mute control |
| `127.0.0.1:9001` UDP | VRChat outgoing mute/state listener used by the controller |
| `127.0.0.1:9000` UDP | VRChat chat and typing output |

Native Nemotron readiness is checked through `/ready`, with speech through `/v1/audio/transcriptions/realtime`. Voxtral and Groups readiness use `/health`; Groups streams through `/v1/realtime`. FireRed validates its cached files before the subtitle service loads them, without starting a speech server. Translation uses an OpenAI-compatible `/v1/models` and `/v1/chat/completions` interface. Managed speech services are local and unauthenticated, bound to loopback. Do not expose these endpoints to a network; loopback restriction is not authentication against other local processes.

## OSC connection versus delivery

The dashboard probe verifies a current VRChat process, a live OSCQuery response naming VRChat, readable `/avatar/parameters/MuteSelf`, and a VRChat-owned UDP endpoint at the expected port. It is read-only and does not send a chat test or start models. The probe distinguishes not running, unavailable, wrong port and connected states.

**Connected** is not a UDP delivery receipt. Avatar chat display, client settings, OSC enablement, UDP routing and output constraints can still affect visible messages. The app limits its own displayed text to 144 Unicode text elements and nine lines, retaining a readable newest tail when needed. Source translation context has a separate bound; display truncation does not silently truncate the text sent to the model.

### Reading other players' chat

VRChat's [public OSC input documentation](https://docs.vrchat.com/docs/osc-as-input-controller) describes `/chatbox/input` and `/chatbox/typing` for sending your own chat/typing state. It does not document a supported public endpoint/event for receiving other players' chatbox text. SayWhat?'s incoming feature therefore translates captured voice audio, not inbound text chat. Screen/OCR workarounds would be a separate feature, not an OSC chat-reading API, and are not implemented here.

## Ownership and cleanup

The controller retains the exact Windows processes it started and token-owned WSL wrappers. It does not stop or adopt an arbitrary process just because it occupies a familiar port. Automatic selection avoids occupied candidate ports; a conflict in fixed mode causes startup to fail with an explanation. Address selection does not guarantee successful backend startup or readiness. Shared translator reference counts keep the model loaded while either direction still uses it; stopping the last user releases it. Details are in [install, update and lifecycle](install-and-updates.md).

For native Windows services, startup obtains a bounded asynchronous snapshot from the standard Windows `netstat.exe -ano -p tcp` command. Before and after an accepted HTTP readiness response, it checks that the requested IPv4 loopback or wildcard listener belongs to the exact retained child PID, rejecting a competing foreign owner on that route. This uses the OS utility's text output instead of a custom native-memory TCP-table parser. Raw snapshots are never logged. These ownership checks run during startup/readiness validation, not as ongoing polling in the live speech loop.

For WSL Voxtral, HTTP health alone is not ownership evidence. The wrapper first verifies that its retained child's executable/birth identity owns the selected loopback TCP listener through its socket inode, then emits an owner-and-port receipt for the controller. WSL start wrappers also perform a bind-only conflict check without connecting to another service. This requires Python 3's standard library; managed Voxtral preparation includes it, while an older prepared distribution without Python reports that setup needs repair.
