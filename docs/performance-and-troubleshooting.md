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

1. Start only **your voice translation** on Home. Subtitles, People matching and Groups are optional and are not required for voice-to-chat.
2. Start with one output language and the smaller **Hy-MT2** translator card. Up to four outputs are supported, but extra languages use sequential translation requests, not free parallel work.
3. Check **I speak** on Home. With native Nemotron, choosing your actual language can reduce recognition mix-ups. Legacy Voxtral continues detecting speech automatically; its translator hint does not pin ASR.
4. Start with the default **1.2-second** pause. Home exposes shorter or longer waits: shorter can split a sentence, longer adds delay. Timing and early-message changes keep the compatible models loaded instead of unloading/reloading them.
5. Try **Show translations while I’m still speaking** for earlier visible chat. If speculative translations add too much load, turn it off and compare final-only behavior. Earlier output is not a guarantee of faster final translation.
6. If you also need subtitles, share one translator and consider **Quick** in Settings for one-to-one conversation. Quick skips familiar-voice matching and shortens subtitle waits/lookahead. Turn optional matching off if its extra CPU work is unwanted; that is distinct from unloading the GPU speech/translation models.

Larger translation models can improve a phrase but still be slower despite fitting in VRAM. Reducing idle caches or loading two models does not increase GPU compute capacity. Test in the same crowded world and with the same VRChat graphics settings before drawing conclusions.

Normal matching now runs alongside translation rather than delaying text until an identity result arrives. Groups is a different trade-off: attributed multi-voice recognition with roughly 3.5 seconds of first-decode audio buffering, plus inference and translation time. Its optional 7B speech model adds download/memory requirements and is not a proven latency improvement. Switch back to Normal if this delay or resource pressure is unsuitable; save tuned preferences as a custom profile.

## “Nothing appears after starting my voice”

With **Pause when my VRChat mic is muted** on (the default), SayWhat waits for a verified microphone mute state before capturing. Older versions could wait indefinitely when VRChat's log was empty. Version 0.2.2 discovers the current status endpoint without relying on that log and retries if it starts late. A visible waiting message explains this state. If needed, mute and unmute once inside VRChat; also check **OSC → Enabled** there.

The Home checkbox can turn mute-following off. This means SayWhat may capture and send translations even while VRChat itself is muted; it is not a troubleshooting action that should be enabled silently. Translation still stops when VRChat closes.

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

The incoming service logs captured versus sent bytes, audio levels/gain, transcript counts, voice/transcript ages, backlog and recovery attempts. This distinguishes no audio, audio without meaningful recognition, and text waiting on translation. Speech/device workers are supervised and reconnect after failure, but recurring recovery means a problem still exists. Version 0.1.23 rearms bounded retries after a cooldown and keeps an active warning until useful recognition text returns; empty output, duplicate snapshots and decoder-reset messages do not establish recovery.

Queues are intentionally bounded. The Voxtral incoming audio backlog retains recent speech and drops stale chunks. The eight-item translation queue keeps committed sentences with backpressure and a visible catching-up state; obsolete draft revisions can be skipped. The separate identity queue may discard old optional matching work without discarding its translation. Groups buffering also reports overload rather than silently accumulating unbounded audio. These bounds cannot make an overloaded pipeline keep up or restore audio already lost.

## “Wrong language or poor word choice”

Check the **direction's target language**, not the app's interface language. My voice and subtitles have independent targets. Check whether a shared specialized model supports **both** selected directions. Liquid EN–JP cannot translate Chinese; TranslateGemma requires explicit source selection and a compatible managed template.

For your microphone, **I speak** and the two model roles are directly on Home. Nemotron uses the chosen language for recognition; Automatic is useful when you switch languages. Voxtral remains automatic at the speech stage, even if an older configuration has an explicit translation hint. Do not assume switching a translator also changes the recognizer.

Inspect source recognition separately from translation. A glossary such as a product-name mapping helps only if a relevant recognizable source term is present; it cannot correct missing audio or restore words that were never transcribed. Wrong-script subtitle output can be rejected and retried, adding delay. A larger translator is only a plausible remedy when the source transcript is already useful.

Language and microphone changes may briefly hold new capture while an already captured sentence finishes. A model change may need a download before it switches; the current translation can continue while files prepare. Watch the status in the app rather than clicking repeatedly. If subtitles share that translator, changing it can pause both directions after confirmation.

## Useful diagnostics

Open **Settings → Problems & logs → Open troubleshooting**, or **Advanced → Diagnostics**. **Recent issues** gives stage-specific problem and recovery summaries. **Copy diagnostics** includes only fixed support codes, timings and safe status information—not conversation text, saved names, file paths or raw exceptions. The detailed-log view refreshes the selected log and follows its newest entries. Detailed exports can contain private content; review them before sharing.

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

Use [GitHub Issues](https://github.com/z2six/saywhat/issues), including app/Windows versions, profile and incoming speech engine, model/runtime, shared/separate mode, active directions, pause/lookahead choices, expected behavior and a small redacted log excerpt. Note whether it happens only during overlap, music, mute/unmute, audio-device changes, or crowded worlds. If possible compare final-only versus early previews and optional voice recognition on versus off without changing every setting at once.
