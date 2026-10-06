# Architecture and local protocols

[Documentation index](README.md) · [Settings](settings-reference.md) · [Performance](performance-and-troubleshooting.md)

## Two independent directions

SayWhat? is a Windows WPF desktop application. It manages local processes, a subtitle window, settings and model downloads. It does not require a separately installed LM Studio or FoxTrans application in the normal built-in setup.

```text
Your voice
  Microphone → capture/mute gate → Voxtral realtime ASR (WSL)
             → local translation → OSC sentence proxy → VRChat chat

Other voices
  VRChat process playback → retained original mixed audio
    Quick / Normal → Voxtral realtime ASR → source text
    Groups         → VibeVoice streaming ASR → text per temporary voice ID
      source text → local translation → separate subtitle bubbles
      usable original audio → optional saved-person matching → bubble names/photos
```

ASR means converting audio into source-language text; translation converts that text to the chosen target language. These are distinct models and failure stages. A larger translator cannot reconstruct words the speech recognizer omitted.

The directions can run separately. When both run, they use independent speech services and audio state. By default they share one loaded translation model, but their requests do not share conversation text. Optional separate translators duplicate model residency; they are not a speaker-separation feature. Built-in profiles change operational settings without replacing chosen translators, languages or appearance. Saved custom profiles also restore those user choices.

## Your microphone

The capture host uses a local fork of FoxTrans Core. It starts muted until the VRChat process and current mute state are verified. Capture has a 300 ms pre-roll and opens an ASR turn after two consecutive voiced frames. The native microphone gate uses an RMS threshold of 0.006; this is an implementation policy, not a microphone-volume setting exposed in the dashboard.

A turn contains cumulative text for the current utterance. Actual captured silence, not unchanged text or a wall-clock guess, determines the configured finalization boundary. At end of input the host sends `input_audio.end`; it waits for **both** `transcript.final` and `session.completed` before using the definitive transcript. The server performs its required final padding rather than the client appending another full silence interval.

While recognition/translation drains, capture can retain two pending turns in order. Each unread turn is bounded to 15 seconds of PCM, with byte and frame-count limits. Once all pending slots are full, genuinely excess new audio is skipped with a reported backpressure outcome; capture resumes when a slot opens. An individual unread-buffer overflow fails only that turn, not already queued sentences. Empty finals, ordinary turn timeouts and translation-provider errors do not automatically discard a healthy queued next turn.

Ongoing voiced input without useful new recognition text has an eight-second progress deadline, requiring at least two seconds of additional voiced PCM. Duplicate, empty or punctuation-only callbacks do not rearm it; silence, mute and final padding do not trigger it. Recoverable turn failures use a short cooldown and do not restart the speech model process.

With **Show translations while I speak** on, speculative requests use the complete utterance heard so far. The scheduler keeps one active request and one latest pending revision, coalesces duplicates, and permits useful in-flight output while new ASR tokens arrive. New previews start no faster than every 1.5 seconds. The definitive final cancels/preempts speculative work; late previews cannot replace it.

The OSC proxy sends early translations as submitted chat bubbles, **not** as text typed into VRChat's editor. Updated cumulative bubbles may repeat or revise earlier wording. It coalesces output at a conservative 1.5-second cadence and gives the final priority. This is an app policy, not a documented VRChat rate-limit guarantee. If the final equals the latest displayed preview, clearing the typing indicator is sufficient; no duplicate bubble is needed.

## VRChat playback and subtitles

The default playback source is Windows per-process loopback capture for VRChat. It captures VRChat's mixed output, including sounds/music played **inside VRChat**. It does not create an individual stream for each player. Turning off VRChat-only capture opts into wider Windows playback capture.

Playback is resampled for 16 kHz recognition. Quiet-audio normalization affects the samples sent to recognition, not the user's speaker/headphone volume. The raw audio backlog is capped around two seconds; overloaded playback drops old chunks to retain recent speech rather than growing minutes behind live audio.

Subtitle segmentation tracks a watermark in cumulative recognition text. It commits a bubble on stable sentence punctuation, the configured settling interval, recognition finalization, or bounded clause/length/duration limits. Stable punctuation uses a configurable wait: 500 ms in Normal and 250 ms in Quick. Long unpunctuated speech has a seven-second soft duration limit and text bounds of about 220 characters or 80 CJK characters. Consequently, **Other voices: silence before finalizing** is not the only way a subtitle can become a new bubble. Useful stable interim text can revise the current bubble without resetting recognition.

Translation has a bounded eight-item queue and a single reader. Committed sentences wait for capacity with visible backlog feedback rather than being silently dropped to make room. Obsolete interim revisions can be skipped or canceled; newer drafts do not invalidate already committed sentences. Optional voice matching runs in a separate bounded background queue, so translation does not wait for identity inference. A late safe match changes the current bubble's suggested name/photo, not its translated words, and cannot overwrite a manual correction. From 0.1.26, suggested names are tentative until reviewed. They neither share saved-person translation context nor increment that person's translation total. Group text context remains scoped to its session track, independently of a guessed name.

In the Voxtral path, worker supervision, audio-device reprobes and a speech-progress watchdog attempt recovery when a recognizer connection or capture worker stops progressing. Subtitle retries require recent speech and use a 20/20/30/40-second capped cooldown rather than a permanently exhausted two-attempt allowance. Useful changed recognition text—not padding, duplicate snapshots or decoder-reset acknowledgments—rearms progress.

Voxtral final recognition can wait up to eight seconds when no newer turn is waiting. If newer speech queues during that wait, the finalization deadline shortens to 1.2 seconds so old stalled work does not consume the whole fresh-audio buffer. Queues remain bounded: this cannot restore audio the recognizer never processed, and audio drops are reported. A visible recovery state indicates an attempt, not proof that useful words returned. See [diagnostics](performance-and-troubleshooting.md).

### Groups: attributed text, not separated audio

Groups sends continuous mixed playback to a local **VibeVoice ASR Streaming** service. The model returns text with temporary voice labels. SayWhat? maintains separate sentence state, translation requests and bubbles for those labels. The labels belong to that recognition session; they are not saved People IDs or VRChat accounts.

SayWhat? retains original audio long enough to relate replies to it. The wrapper reports the span of the **whole input chunk**, not word-level or isolated-person timestamps. Several attributed voices in one chunk therefore do not provide separate clean recordings. Such audio is excluded from saved-person fingerprint learning. An unlabelled reply can still be translated, but does not create a shared person identity or train saved people.

When a chunk contains one labelled voice and passes the existing clear-audio checks, optional fingerprint matching can tentatively link its session label to a saved person. Explicit message assignments take precedence and remain useful with fingerprinting off. They do not confirm future messages on the track. Unmatched or unsafe identity results revoke old automatic links; rejected suggestions suppress that name on the temporary track, including late results. With fingerprinting off, future messages do not inherit named guesses. Mixed, short or ambiguous evidence stays unconfirmed; a label can update a bubble without saving a voice example. These checks are conservative heuristics, not proof that all overlap was detected.

The configured streaming chunk plus lookahead requires roughly **3.5 seconds of audio before the first decode**, before inference and translation time. This is not a 3.5-second end-to-end promise. Groups is optional and adds downloads, memory use and delay. It may miss or conflate voices during overlap, music or difficult audio. Switching profiles stops only the affected owned services; choosing Groups is not evidence of measured in-game performance.

## Local endpoints

| Endpoint | Purpose |
| --- | --- |
| `127.0.0.1:8080` HTTP/WebSocket | Your microphone's Voxtral server |
| `127.0.0.1:8081` HTTP/WebSocket | Incoming VRChat-playback Voxtral server |
| `127.0.0.1:8082` HTTP/WebSocket | Optional incoming VibeVoice Groups service |
| `127.0.0.1:1235/v1` HTTP | Built-in translator for your voice; shared by subtitles when sharing is on |
| `127.0.0.1:1236/v1` HTTP | Separate built-in subtitle translator when sharing is off |
| `127.0.0.1:9010` UDP | Internal OSC sentence proxy input |
| `127.0.0.1:9011` UDP | Internal microphone-host mute control |
| `127.0.0.1:9001` UDP | VRChat outgoing mute/state listener used by the controller |
| `127.0.0.1:9000` UDP | VRChat chat and typing output |

Voxtral and Groups readiness are checked through `/health`; Groups streams through `/v1/realtime`. Translation uses an OpenAI-compatible `/v1/models` and `/v1/chat/completions` interface. Managed speech services are local and unauthenticated, bound to loopback. Do not expose these endpoints to a network; loopback restriction is not authentication against other local processes.

## OSC connection versus delivery

The dashboard probe verifies a current VRChat process, a live OSCQuery response naming VRChat, readable `/avatar/parameters/MuteSelf`, and a VRChat-owned UDP endpoint at the expected port. It is read-only and does not send a chat test or start models. The probe distinguishes not running, unavailable, wrong port and connected states.

**Connected** is not a UDP delivery receipt. Avatar chat display, client settings, OSC enablement, UDP routing and output constraints can still affect visible messages. The app limits its own displayed text to 144 Unicode text elements and nine lines, retaining a readable newest tail when needed. Source translation context has a separate bound; display truncation does not silently truncate the text sent to the model.

### Reading other players' chat

VRChat's [public OSC input documentation](https://docs.vrchat.com/docs/osc-as-input-controller) describes `/chatbox/input` and `/chatbox/typing` for sending your own chat/typing state. It does not document a supported public endpoint/event for receiving other players' chatbox text. SayWhat?'s incoming feature therefore translates captured voice audio, not inbound text chat. Screen/OCR workarounds would be a separate feature, not an OSC chat-reading API, and are not implemented here.

## Ownership and cleanup

The controller retains the exact Windows processes it started and token-owned WSL wrappers. It does not stop an arbitrary process just because it occupies a familiar port. A conflicting listener causes startup to fail with an explanation. Shared translator reference counts keep the model loaded while either direction still uses it; stopping the last user releases it. Details are in [install, update and lifecycle](install-and-updates.md).
