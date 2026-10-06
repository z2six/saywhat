# Architecture and local protocols

[Documentation index](README.md) · [Settings](settings-reference.md) · [Performance](performance-and-troubleshooting.md)

## Two independent directions

SayWhat? is a Windows WPF desktop application. It manages local processes, a subtitle window, settings and model downloads. It does not require a separately installed LM Studio or FoxTrans application in the normal built-in setup.

```text
Your voice
  Microphone → capture/mute gate → Voxtral realtime ASR (WSL)
             → local translation → OSC sentence proxy → VRChat chat

Other voices
  VRChat process playback → Voxtral realtime ASR (separate server process in WSL)
                          → optional local voice identification
                          → local translation → subtitle bubbles
```

ASR means converting audio into source-language text; translation converts that text to the chosen target language. These are distinct models and failure stages. A larger translator cannot reconstruct words the speech recognizer omitted.

The directions can run separately. When both run, they use separate recognition-server instances and audio state. By default they share one loaded translation model, but their requests do not share conversation text. Optional separate translators duplicate model residency; they are not a speaker-separation feature.

## Your microphone

The capture host uses a local fork of FoxTrans Core. It starts muted until the VRChat process and current mute state are verified. Capture has a 300 ms pre-roll and opens an ASR turn after two consecutive voiced frames. The native microphone gate uses an RMS threshold of 0.006; this is an implementation policy, not a microphone-volume setting exposed in the dashboard.

A turn contains cumulative text for the current utterance. Actual captured silence, not unchanged text or a wall-clock guess, determines the configured finalization boundary. At end of input the host sends `input_audio.end`; it waits for **both** `transcript.final` and `session.completed` before using the definitive transcript. The server performs its required final padding rather than the client appending another full silence interval.

While recognition/translation drains, capture can retain one pending next turn. Each unread turn is bounded to 15 seconds of PCM. A queue overflow or capture-device failure is a visible session failure, not an unbounded memory backlog. Empty finals, ordinary turn timeouts and translation-provider errors do not automatically discard a healthy queued next turn.

With **Show translations while I speak** on, speculative requests use the complete utterance heard so far. The scheduler keeps one active request and one latest pending revision, coalesces duplicates, and permits useful in-flight output while new ASR tokens arrive. New previews start no faster than every 1.5 seconds. The definitive final cancels/preempts speculative work; late previews cannot replace it.

The OSC proxy sends early translations as submitted chat bubbles, **not** as text typed into VRChat's editor. Updated cumulative bubbles may repeat or revise earlier wording. It coalesces output at a conservative 1.5-second cadence and gives the final priority. This is an app policy, not a documented VRChat rate-limit guarantee. If the final equals the latest displayed preview, clearing the typing indicator is sufficient; no duplicate bubble is needed.

## VRChat playback and subtitles

The default playback source is Windows per-process loopback capture for VRChat. It captures VRChat's mixed output, including sounds/music played **inside VRChat**. It does not create an individual stream for each player. Turning off VRChat-only capture opts into wider Windows playback capture.

Playback is resampled for 16 kHz recognition. Quiet-audio normalization affects the samples sent to recognition, not the user's speaker/headphone volume. The raw audio backlog is capped around two seconds; overloaded playback drops old chunks to retain recent speech rather than growing minutes behind live audio.

Subtitle segmentation tracks a watermark in cumulative recognition text. It commits a bubble on stable sentence punctuation, the configured settling interval, recognition finalization, or bounded clause/length/duration limits. Stable punctuation can commit after 500 ms; long unpunctuated speech has a seven-second soft duration limit and text bounds of about 220 characters or 80 CJK characters. Consequently, **Other voices: silence before finalizing** is not the only way a subtitle can become a new bubble. Useful stable interim text can revise the current bubble without resetting recognition.

Translation has a bounded four-item queue with oldest-first dropping under overload. It uses a single reader and rejects superseded revisions. Worker supervision, audio-device reprobes and a speech-progress watchdog attempt recovery when a recognizer connection or capture worker stops progressing. A visible **Reconnecting speech** state indicates recovery, not proof that the audio was successfully recognized. See [diagnostics](performance-and-troubleshooting.md).

## Local endpoints

| Endpoint | Purpose |
| --- | --- |
| `127.0.0.1:8080` HTTP/WebSocket | Your microphone's Voxtral server |
| `127.0.0.1:8081` HTTP/WebSocket | Incoming VRChat-playback Voxtral server |
| `127.0.0.1:1235/v1` HTTP | Built-in translator for your voice; shared by subtitles when sharing is on |
| `127.0.0.1:1236/v1` HTTP | Separate built-in subtitle translator when sharing is off |
| `127.0.0.1:9010` UDP | Internal OSC sentence proxy input |
| `127.0.0.1:9011` UDP | Internal microphone-host mute control |
| `127.0.0.1:9001` UDP | VRChat outgoing mute/state listener used by the controller |
| `127.0.0.1:9000` UDP | VRChat chat and typing output |

Voxtral readiness is checked through `/health`. Translation uses an OpenAI-compatible `/v1/models` and `/v1/chat/completions` interface. Local Voxtral sessions are unauthenticated and bound to loopback. Do not expose these endpoints to a network; loopback restriction is not authentication against other local processes.

## OSC connection versus delivery

The dashboard probe verifies a current VRChat process, a live OSCQuery response naming VRChat, readable `/avatar/parameters/MuteSelf`, and a VRChat-owned UDP endpoint at the expected port. It is read-only and does not send a chat test or start models. The probe distinguishes not running, unavailable, wrong port and connected states.

**Connected** is not a UDP delivery receipt. Avatar chat display, client settings, OSC enablement, UDP routing and output constraints can still affect visible messages. The app limits its own displayed text to 144 Unicode text elements and nine lines, retaining a readable newest tail when needed. Source translation context has a separate bound; display truncation does not silently truncate the text sent to the model.

### Reading other players' chat

VRChat's [public OSC input documentation](https://docs.vrchat.com/docs/osc-as-input-controller) describes `/chatbox/input` and `/chatbox/typing` for sending your own chat/typing state. It does not document a supported public endpoint/event for receiving other players' chatbox text. SayWhat?'s incoming feature therefore translates captured voice audio, not inbound text chat. Screen/OCR workarounds would be a separate feature, not an OSC chat-reading API, and are not implemented here.

## Ownership and cleanup

The controller retains the exact Windows processes it started and token-owned WSL wrappers. It does not stop an arbitrary process just because it occupies a familiar port. A conflicting listener causes startup to fail with an explanation. Shared translator reference counts keep the model loaded while either direction still uses it; stopping the last user releases it. Details are in [install, update and lifecycle](install-and-updates.md).
