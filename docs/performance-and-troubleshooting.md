# Performance and troubleshooting

[Documentation index](README.md) · [Settings reference](settings-reference.md) · [Quick help](../SUPPORT.md)

## Where delay comes from

```text
Capture/recognition lag
 + sentence-ending or segment wait
 + recognition final/completion drain
 + translation queue and generation
 + OSC output cadence or subtitle rendering
 = visible end-to-end delay
```

These stages overlap in early-preview mode, so summing isolated timings is not a precise benchmark. Nevertheless, a 1,200 ms pause only controls **one** stage. A fast text-only translator can coexist with slow recognition, overloaded GPU work or an output slot waiting to send.

Compare at least these outcomes separately:

- Time from speaking to useful source text.
- Time from stopping speech to an authoritative final translation.
- Time to the first visible early chat bubble, if previews are enabled.
- VRChat frame-time stability and GPU/RAM pressure while both directions run.

Use several realistic conversational phrases, not a single “hello.” Word choices, slang, accents, sentence length and music affect both quality and delay. A p95 timing is more useful for recurring stalls than an average that hides the slowest messages.

## Low-load starting point

1. Share one **Hy-MT2 1.8B** translator for both directions and use Fast style.
2. Keep extra target languages off. They use sequential translation requests, not free parallel outputs.
3. Run only the directions needed. Each adds capture and a recognition-server instance.
4. If game performance suffers, disable optional voice recognition and compare again. It is separate CPU work, not the large GPU speech/translation model.
5. Keep default 1.2-second pauses initially. Shortening the microphone pause trades sentence coherence for earlier finals; shortening lookahead trades recognition context for latency.
6. If early previews add too much translator load, turn them off and compare final-only behavior. Turning them on can improve first-visible output, but is not guaranteed to improve final latency.

Larger translation models can improve a phrase but still be slower despite fitting in VRAM. Reducing idle caches or loading two models does not increase GPU compute capacity. Test in the same crowded world and with the same VRChat graphics settings before drawing conclusions.

## “Typing dots, but no new chat”

The typing indicator is a processing state, not proof of a successful translation or delivered chat packet. Possible reasons include:

- Recognition produced an empty final or did not acknowledge completion.
- A turn/provider timeout or cancellation prevented an authoritative final.
- Muting discarded the unfinished sentence because finish-after-mute was off.
- A captured final was identical to the latest early bubble, so no duplicate was sent.
- Newer work superseded an obsolete preview.
- The output reached its context/display safety limits.
- The VRChat OSC service was unavailable or UDP delivery/display was affected.

Do not infer the cause just from the dots. The capture-host and proxy logs distinguish a queued final, rejected stale revision, cancellation and a provider failure. A Connected indicator is a useful local readiness check, not a delivery acknowledgment.

## “Subtitles stop, then catch up”

The source can fail before translation: wrong capture route, VRChat moved to another device, quiet audio below recognition thresholds, no lexical progress, a busy speech server, or a disconnected capture worker. Songs are a particularly poor automatic speech/identity test; their audio can be audible but not recognized as reliable conversational speech.

The incoming service logs captured versus sent bytes, audio levels/gain, transcript counts, voice/transcript ages, backlog and recovery attempts. This distinguishes no audio, audio without meaningful recognition, and text waiting on translation. Speech/device workers are supervised and reconnect after failure, but recurring recovery means a problem still exists.

Queues are intentionally bounded. The incoming audio backlog retains recent speech and drops stale chunks; the four-item translation queue can drop older items during overload. Seeing drops means the pipeline cannot keep up with that session. Increasing queue sizes would conceal the problem as steadily growing lag.

## “Wrong language or poor word choice”

Check the **direction's target language**, not the app's interface language. My voice and subtitles have independent targets. Check whether a shared specialized model supports **both** selected directions. Liquid EN–JP cannot translate Chinese; TranslateGemma requires explicit source selection and a compatible managed template.

Inspect source recognition separately from translation. A glossary such as a product-name mapping helps only if a relevant recognizable source term is present; it cannot correct missing audio or restore words that were never transcribed. Wrong-script subtitle output can be rejected and retried, adding delay. A larger translator is only a plausible remedy when the source transcript is already useful.

## Useful diagnostics

Open **Advanced → Diagnostics**. The view refreshes the selected log and follows its newest entries. Export only after reviewing private content.

| Log | Useful for |
| --- | --- |
| `saywhat.log` | Startup/stop/setup/update stages, selected targets, health results, owned-process cleanup |
| `foxtrans-local-host.log` | Microphone voice-gate/turn IDs, queued turn age, ASR and translator stage timing/error metadata |
| `foxtrans-mute-sync.log` | VRChat presence, current mute state and microphone permission |
| `foxtrans-osc-sentence.log` | Proxy output cadence, identity deduplication and captured-final permission |
| `foxtrans-provider-errors.log` | Provider readiness/failures, when present |
| `incoming-translator.log` | Playback routing, audio levels/backlog, recognition progress, voice matching, translated text and queue/recovery errors |

Some logs contain names and source/translated conversation text. The incoming log may also include provider error details. **Do not upload your entire data directory.** Redact private conversation, names, paths and credentials; include the relevant time window, app version, model/runtime, direction and symptom.

The explicit local health check does not start engines; an unused stopped endpoint being unavailable is expected. The four-sample translation test uses the currently running translator and adds real work. It measures text-only latency, not total speech-to-chat delay.

## Report a reproducible problem

Use [GitHub Issues](https://github.com/z2six/saywhat/issues), including app/Windows versions, model/runtime, shared/separate mode, active directions, pause/lookahead choices, expected behavior and a small redacted log excerpt. Note whether it happens only during overlap, music, mute/unmute, audio-device changes, or crowded worlds. If possible compare final-only versus early previews and optional voice recognition on versus off without changing every setting at once.
