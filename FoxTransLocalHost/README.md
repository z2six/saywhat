# Local FoxTrans capture-gate fork

This is a GPL-3.0 local headless host using the pinned FoxTrans v2.0.0 Core source
in `../FoxTransSource` (upstream commit `515802db30f979d9b1dacca786b3c33827dc0b92`).
Upstream: https://github.com/MrShitFox/FoxTrans. The original executable is retained.
Local modifications were made on 2026-10-04/05. All modified source is present beside
the application; retain the GPL license and corresponding source when sharing.
The unchanged native DLL comes from the already-installed v2.0.0 executable;
its original native/vendor sources and license notices remain in the source tree.

## Behavior

- Starts safely muted. Loopback-only OSC control port 9011 accepts
  `/foxtrans/control/muted` with boolean T/F.
- While unmuted, native capture keeps a 300 ms rolling pre-roll buffer. ASR opens
  only after two consecutive voice frames (RMS threshold 0.006); no ASR connection
  or endless silent stream is held during idle microphone silence. Level, frame and
  gate counts are logged every five seconds for quiet-voice diagnostics.
- Actual PCM silence for `newUtteranceAfterMs` ends each finite realtime ASR turn.
  Capture stays active during normal server/LLM drain so resumed speech is held as
  one bounded pending turn. Each unread turn stores at most 15 seconds of PCM;
  overload stops capture with a clear status, not an ever-growing backlog.
- Muting closes the native microphone callback gate before waiting for anything.
  All captured PCM, including the normalizer's sub-20ms carry, may drain. The
  production finite-turn path sends exactly that captured PCM, with no redundant
  1.2-second client silence tail. `input_audio.end` delegates the required model
  right-padding and final flush to the server. No new samples are captured while muted.
- `input_audio.end` requests real `transcript.final` + `session.completed`.
  The full transcript is translated with the unchanged configured provider,
  model and prompt. No chatbox editor is opened by early or final output.
- Every sentence, including normal unmuted sentences, waits for **both** a real
  `transcript.final` and `session.completed` before final translation output.
  Neither unchanged recognizer text nor a late final without the completion
  acknowledgment can promote a stale fragment into a complete sentence.
- `OwnVoiceSendEarlyPreviews` chooses cumulative early submitted chat bubbles or
  final-only output. It defaults false when missing; malformed/unreadable preferences
  also fail safely to final-only. An explicitly saved true value opts into previews.
  The application timing default is 1.2 seconds of real quiet; the host follows its
  supplied configuration and does not alter settings or a running session itself.
- `OwnVoiceAdditionalLanguagesEnabled` is off by default. When enabled, the primary
  target plus up to two `OwnVoiceAdditionalTargetLanguages` appear in one chat bubble,
  in the selected order, one native-script translation per line. Supported names/ISO
  aliases are normalized, duplicates are ignored, and disabled/malformed preferences
  retain the exact existing single-target provider path. One shared model receives
  sequential single-target requests, preserving Hy-MT's dedicated user task format.
  Extra requests reuse the selected glossary and do not load another model. The group
  is bounded to 7.5 seconds, plus at most 250 ms to let cancelled provider work retire,
  inside the existing 8-second final wait. Successful targets survive a failed target;
  missing target names and safe reason codes are visible in Diagnostics, without any
  response bodies or speech text. Parent cancellation never publishes a partial group.
  Speculative starts are throttled to 3/4.5 seconds for two/three targets to avoid
  multiplying the normal speculative request rate. More languages can take longer.
  Display truncation shares the 144-character budget fairly among successful target
  lines, donates unused space from shorter targets, and keeps whole Unicode graphemes.
- In early mode, a useful first phrase (at least three lexical words or six non-ASCII
  lexical characters) can be translated while speaking. Snapshots contain the whole
  current utterance, not independent chunks. The existing scheduler keeps only one
  active request and one latest pending revision, with at least 1500 ms between
  speculative starts; duplicates are coalesced. This conservative cadence is an app
  policy, not a claimed documented VRChat rate limit. A newer ASR token does not
  starve a useful in-flight snapshot; older/duplicate delivered revisions are rejected.
- Actual `transcript.final` immediately closes preview output, cancels speculative
  work and invalidates pending preview revisions. Only after `session.completed`
  can the authoritative final take priority. Slow speculative work does not create
  concurrent LLM requests; cancelled/late previews cannot appear after that final.
  Mute blocks previews without spending the one permitted captured-final allowance.
  A failed speculative translation does not poison a later successful definitive one.
- Both modes retain the complete source up to a 2048-text-element safety cap, rather
  than silently translating an old 800-character suffix window. Over-cap previews
  stop, and an oversized definitive sentence reports `asr-context-limit` without
  translating a clipped source. Display output independently keeps the newest
  readable tail (with ellipsis) within the 144-text-element and nine-line chatbox
  limits; display CRLF/lone-CR breaks are normalized without changing model context.
  Final-only mode submits just one LLM request after final/completed and avoids
  speculative GPU work. Typing OSC never edits/opens the chatbox.
  Empty ASR finals, bounded ASR/translation timeouts and provider failures end only
  the affected turn: healthy microphone capture and its one queued next sentence
  survive. The next turn opens a fresh server connection. Capture/device failure,
  queue overflow and explicit session cancellation still stop the capture session.
  If a provider fails while speech is still active, the next fresh voice frames
  reopen the bounded gate without waiting for a long silence; failed audio is not
  replayed, and muted/draining capture cannot open a replacement turn.
- The `FinishCapturedSentenceOnMute` options setting defaults true. False discards
  unfinished work instead. No raw audio or source/translation text is persisted by
  this host; logs contain timing, counts, state and error metadata only. Turn IDs,
  queue age, real captured/voiced duration, last-voice age and ASR/translation/output
  stages distinguish waiting, slow processing and failed recognition. Compatible
  server completion diagnostics retain numeric realtime factor and backlog only;
  arbitrary server messages and provider response bodies are not copied to turn logs.
- Drain acknowledgment and translation waits each have an 8-second budget; the
  entire muted drain is cancelled after at most 20 seconds. No timeout promotes a
  stale partial as if it were a complete sentence. A rapid unmute waits for the old
  drain to release the one-stream local ASR slot before opening a fresh microphone.
  The muted permission allows one captured final. An additional already-captured
  queued turn is explicitly logged/discarded after that final, never silently lost.
  Transient server-busy lease release retries for about five seconds; individual
  health requests time out after 900 ms. Capture and control/timer failures surface
  and close the microphone rather than silently freezing.
- The controller checks VRChat process presence every 500 ms. VRChat exit stops
  the microphone host entirely. On return it stays muted until OSCQuery or a fresh
  MuteSelf packet verifies the current state; old queued packets are discarded.
- Captured-turn OSC starts with `/foxtrans/control/captured-start` `,s` identity
  before typing; previews use `/foxtrans/control/captured-preview` `,sis` identity,
  positive int32 revision and cumulative translated text. The opaque turn identity
  is the same for previews and `/foxtrans/control/captured-final` `,ss` identity/text,
  independent of internal scheduler epochs. Failed/cancelled turns emit
  `/foxtrans/control/captured-cancel` `,s`, never after successful queued finals.
  The sentence proxy permits ordinary unmuted finals, or one explicitly granted
  pre-mute final for at most 20 seconds, and deduplicates identities. It submits
  previews as chat (`send=true`), coalesces output at a conservative 1.5-second
  cadence, and prioritizes the definitive final in the next available slot. An
  unchanged final after the latest displayed snapshot just clears typing.

## Build and checks

Requires the already-installed .NET 9 runtime/SDK. No new NuGet dependencies.

`dotnet build FoxTransLocalHost.csproj -c Release`

`dotnet publish FoxTransLocalHost.csproj -c Release -o publish`

`dotnet publish\FoxTrans.LocalHost.dll --self-test --root C:\path\to\isolated-test-directory`

The self-test uses synthetic PCM, a fake WebSocket server, and fake translation.
Its health-retry tests use a mock HTTP handler and perform no network requests.
It does not open audio devices, contact any server, send OSC, or load any models.
It requires an existing absolute test directory without live settings and writes
its metadata test log only there, never to an inferred installation/workspace root.
Checks cover exact PCM without redundant padding, both mandatory completion events,
queued-sentence survival after empty finals and typed timeouts, capture-pump/overflow
fatality, parent cancellation, typing cleanup and at-most-one captured muted final.
Controlled-clock early-mode checks cover cadence/coalescing while ASR keeps growing,
complete cumulative context, final preemption, no concurrent LLM requests, no stale
post-final preview, mute/cancel isolation and explicit context-limit outcomes.
Display-only fixtures check exactly nine versus ten lines, CRLF/lone CR, newest-tail
length clipping and preservation of Unicode graphemes/surrogate pairs.
Multi-language mock checks cover default-off preference parsing, canonical aliases,
deduplication, shared local endpoint/model and per-target Hy-MT task bodies, ordered
native output, primary/extra failures, bounded uncooperative providers, serial
cancelled-preview/final handoff, fair language budgets and grapheme-safe clipping.
Specialized model checks cover explicit source requirements, every selected
language pair, exact Liquid EN–JP system prompts, TranslateGemma's rendered text
task, independent directions, same-language pass-through, cancellation and bounded
source length. TranslateGemma uses the app's managed text-only Gemma wrapper;
arbitrary external chat templates are rejected. Its 2K context reserves 256 output
tokens and limits source text to 1024 UTF-8 bytes; oversized sentences fail visibly
instead of silently clipping. Liquid supports English and Japanese only.
The self-test also writes a safe Jinja template fixture into its isolated root.
The rendered managed wrapper was compared against the pinned GGUF template without
loading weights. These are protocol checks, not model quality or latency benchmarks.
An integrated captured-tail fixture checks that all targets receive the full source
only after final/completed, share one final bubble and clear typing after mute.
The 1.2-second sentence boundary also has a PCM-only regression: 1180/1190 ms quiet
cannot finish a turn, fresh voice resets the count, and exactly 1200 ms consecutive
quiet (including sub-frame carry) completes it without a wall-clock silence sleep.
The timeout queue fixture injects the typed failure; the provider's separate protocol
tests exercise its actual eight-second deadline. These checks do not measure live
recognition accuracy or guarantee a particular latency improvement. New builds are
not applied to an active translation session until the user approves a restart.

Normal launch (through the existing launcher/controller, not manually while gaming):

`publish\FoxTrans.LocalHost.exe run --config ..\config.fast-chinese.jsonc --root ..`

`check --config ...` runs the original Core readiness checks. Do not run a live
readiness check or open the microphone until the user asks to resume the tools.
