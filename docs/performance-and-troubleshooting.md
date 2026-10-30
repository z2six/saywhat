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
5. Finished-sentence translation is the default in both directions. Optionally try **Show translations while I’m still speaking** for earlier VRChat chat, or **Show early translations** for subtitles. Each can revise wording as more speech arrives and add speculative translator work; leave it off if that is distracting or costly. Earlier output is not a guarantee of faster final translation.
6. If you also need subtitles, share one translator and consider **Quick** in Settings for one-to-one conversation. Quick skips familiar-voice matching and shortens subtitle waits/lookahead. Turn optional matching off if its extra CPU work is unwanted; that is distinct from unloading the GPU speech/translation models.

Make the Home changes you want, then choose **Apply changes** on your voice card or **Apply subtitle choices** on the subtitle card. Editing several choices does not apply them one by one. **Download & apply** prepares missing files; **Apply & start** can save pending own-voice choices and start them when stopped. Home's person-recognition and mute-following checkboxes also wait for their card's Apply action.

Larger translation models can improve a phrase but still be slower despite fitting in VRAM. Reducing idle caches or loading two models does not increase GPU compute capacity. Test in the same crowded world and with the same VRChat graphics settings before drawing conclusions.

Normal matching now runs alongside translation rather than delaying text until an identity result arrives. Groups is a different trade-off: attributed multi-voice recognition with roughly 3.5 seconds of first-decode audio buffering, plus inference and translation time. Its optional 7B speech model adds download/memory requirements and is not a proven latency improvement. Switch back to Normal if this delay or resource pressure is unsuitable; save tuned preferences as a custom profile.

## “Nothing appears after starting my voice”

With **Pause when my VRChat mic is muted** on (the default), SayWhat waits for a verified microphone mute state before capturing. Older versions could wait indefinitely when VRChat's log was empty. Version 0.2.2 discovers the current status endpoint without relying on that log and retries if it starts late. A visible waiting message explains this state. If needed, mute and unmute once inside VRChat; also check **OSC → Enabled** there.

To turn mute-following off, change the Home checkbox and select **Apply changes**. SayWhat may then capture and send translations even while VRChat itself is muted; editing the checkbox alone does not change the running translator. Translation still stops when VRChat closes.

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

Queues are intentionally bounded. Continuous incoming audio retains recent speech and drops stale chunks. FireRed's section queue keeps at most three finished sections and the newest draft; an overload warning reports skipped old sections. The eight-item translation queue keeps committed sentences with backpressure and a visible catching-up state; obsolete draft revisions can be skipped. The separate identity queue may discard old optional matching work without discarding its translation. Groups buffering also reports overload rather than silently accumulating unbounded audio. These bounds cannot make an overloaded pipeline keep up or restore audio already lost.

If FireRed is falling behind, compare **FireRedASR2 · Fast** against **Accurate** before shortening every pause. Accurate has an extra decoder and costs more processing. This implementation uses CPU, so free GPU memory alone is not evidence that it can decode faster. Its short-section waits, computation and text translation are separate costs. Normal/Quick source choices are remembered when returning from Groups; switching to Normal does not silently discard a chosen FireRed/Nemotron model.

## “Chinese subtitles use the wrong words”

1. On **Home**, choose **Speech model & language** on the subtitle card.
2. Under **They speak**, choose **Chinese**. Choose **FireRedASR2 · Fast** as a Chinese/English specialist, or compare **Accurate** if Fast misses important words.
3. Choose **Download & apply**, or **Apply subtitle choices** if the files are ready. Preparing files keeps stopped subtitles stopped; choose Start when needed.
4. Turn **Show original text** on in the overlay's **Appearance** controls. Compare the source words with the English translation, in the overlay or **Logs → Transcript**. Wrong source words need recognition work; a correct Chinese transcript with poor English wording points to the translator. Neither stage is a verified transcript.

FireRedASR2 is bilingual. Selecting Chinese chooses compatible recognition and translation context, but does not force a hidden Chinese-only decoding token. Real English words in the conversation are still valid. For a multilingual room, choose **Any language** with **Nemotron** instead. Its explicit Chinese selection reaches the recognizer itself when you know the conversation's language. Choosing an incorrect forced language can make results worse.

If only Chinese conversations should appear, enable **Only show Chinese speech** beside the spoken-language choices and apply. Switching to Chinese enables it, but a manual opt-out is remembered when Chinese was already selected. The filter skips other recognized languages without pausing listening; changing only this filter keeps models loaded. It applies only to incoming subtitles. It can miss short or mixed speech and cannot correct a recognizer that has already produced the wrong Chinese words. For a multilingual room, leave it off.

FireRed's upstream system is called [FireRedASR2S](https://github.com/FireRedTeam/FireRedASR2S). The [CTC/AED models used here](https://k2-fsa.github.io/sherpa/onnx/FireRedAsr/pretrained.html) do not separate overlapping voices. Use the optional Groups route to compare attributed multi-voice recognition, accepting its additional buffering and different model. Person matching only attaches familiar names; it cannot reconstruct words missing from the recognition transcript.

Subtitle model/source choices do not change **I speak** or the microphone recognizer. No separate speech application is needed for FireRed or Nemotron subtitles, and their preparation does not require setting up the legacy WSL speech component.

## “Wrong language or poor word choice”

Check the **direction's target language**, not the app's interface language. My voice and subtitles have independent targets. Check whether a shared specialized model supports **both** selected directions. Liquid EN–JP cannot translate Chinese; TranslateGemma requires explicit source selection and a compatible managed template.

For your microphone, **I speak** and the two model roles are directly on Home. Nemotron uses the chosen language for recognition; Automatic is useful when you switch languages. Voxtral remains automatic at the speech stage, even if an older configuration has an explicit translation hint. Do not assume switching a translator also changes the recognizer.

For subtitles, **They speak** and the recognition model are under **Home → Speech model & language**. Choose a source supported by the speech model. FireRed is Chinese/English only; a translator supporting Japanese or Korean does not extend its audio-recognition languages. Existing Voxtral selections remain automatic until you deliberately choose another recognizer.

Inspect source recognition separately from translation. A glossary such as a product-name mapping helps only if a relevant recognizable source term is present; it cannot correct missing audio or restore words that were never transcribed. Wrong-script subtitle output can be rejected and retried, adding delay. A larger translator is only a plausible remedy when the source transcript is already useful.

After you apply, language and microphone changes may briefly hold capture while an already captured sentence finishes. A model change may need a download before switching; the current translation can continue while files prepare. Watch the status in the app rather than clicking repeatedly. If subtitles share that translator, applying its replacement can pause both directions after confirmation.

## Useful diagnostics

Open **Settings → Problems & logs → Open troubleshooting**, or **Advanced → Diagnostics**. **Recent issues** gives stage-specific problem and recovery summaries. Choose **Save support report…** for one shareable text file, or **Copy diagnostics** for the shorter summary. Both exclude speech, conversation text, saved/device names, file paths and raw exception messages. The report includes effective built-in model/language choices, Windows/CPU capabilities, known failure codes and recent numeric pipeline progress.

These stages narrow the investigation without assuming the green running status means success: process started → control ready → actual audio frames → recognized text length → translation completion → local chat queued. Not every silence produces recognition; a queued chat is not proof that VRChat displayed it. Unknown startup failures stay unknown when the available evidence cannot establish a cause.

Saving checks expected app files and briefly probes only already-running, app-owned local health endpoints. It does not start engines, record audio, send test chat or upload the report. The app also keeps `saywhat-support-last-failure.txt` in its data folder after a recent microphone failure; this automatic snapshot makes no health requests. Detailed-log views remain available, refresh the selected log and follow its newest entries. Those detailed exports can contain private content; review them before sharing.

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

Managed speech and translation ports are automatic by default. When checking an address manually, use the running service's address rather than assuming ports 8080/8081 or 1235/1236. In advanced fixed-port mode, choose an unused port if another program is already listening; do not close an unrelated program just to reclaim a historical port. External translator URLs remain your configured addresses, and VRChat's OSC output still uses port 9000.

## Report a reproducible problem

Use [GitHub Issues](https://github.com/z2six/saywhat/issues), attach a saved support report and briefly describe what you expected and what happened. Add timing or a triggering action if you know it. Requesting or sharing detailed conversation logs should be a separate decision, not a prerequisite for reporting a failure. If comparing settings, change one factor at a time, such as final-only versus early previews or optional person matching on versus off.
