# Local Groups speech backend

This is SayWhat?'s loopback adapter for **VibeVoice-ASR-Streaming**, not the
non-streaming VibeVoice ASR model or VibeVoice text-to-speech.

## Provenance and licenses

The adapter uses Microsoft's documented public streaming methods:
`init_streaming_state`, `encode_speech`, and `streaming_generate_step`.
Upstream source is fetched only during explicit preparation and pinned to
`1541f590c7099820f10ea012f48d2399282df69f` from
<https://github.com/microsoft/VibeVoice>. See its
[streaming guide](https://github.com/microsoft/VibeVoice/blob/1541f590c7099820f10ea012f48d2399282df69f/docs/vibevoice-asr-streaming.md)
and
[official demo](https://github.com/microsoft/VibeVoice/blob/1541f590c7099820f10ea012f48d2399282df69f/demo/vibevoice_asr_streaming_fastapi_demo.py).
Upstream and the two public checkpoints are MIT licensed; preserve their
downloaded LICENSE/README notices when packaging an offline runtime. This
adapter is part of SayWhat?'s existing GPLv3 source distribution. Dependencies
retain their own licenses. Upstream code/weights are not copied into this source
directory, and no model weights are bundled in the installer.

The default checkpoint is `microsoft/VibeVoice-ASR-Streaming-1.5B`, pinned to
`4262d23d8a539a6530cf64fbd0b1751ef9a30853`. Optional 7B is pinned to
`60d858b518b4e19d404af3737f848fc185b30177`. Storage estimates are not VRAM claims.

## Runtime boundaries

Only an explicit Prepare action installs dependencies and downloads public
weights. The runtime uses the already registered speech WSL distribution,
without changing its default user or driver. Python/source stay Linux-native in
an app-owned versioned directory; model files stay in the app's Windows model
cache. There is no separate user-installed UI/service to manage.

Start and Stop use a retained random owner token. Stop validates the PID birth
time, interpreter, adapter path, owner and port before terminating anything.
The service binds **127.0.0.1:8082 only**. Inference forces HuggingFace/Transformers
offline mode. No hosted inference or fallback is implemented. The adapter never
writes raw captured audio or text transcripts to disk. Logs contain startup,
timing and error information, not normal recognised conversation text.

Preparation runs in a separately owned process group, so cancellation stops its
package/download children too. Downloads report actual bytes, including resumed
partial files; the HTTP download path is used instead of sparse Xet writes so
disk size is meaningful progress rather than a preallocated file size.

## Protocol v1

`GET /health` (also `/healthz`) reports `engine:"vibevoice"`, protocol, owner,
`status` (`unloaded`, `loading`, `ready`, `error`), selected model, trained chunk
and lookahead, and active session count. A live HTTP server is not synonymous
with a loaded model. The app must verify engine/protocol/owner/model and ready.

Open `ws://127.0.0.1:8082/v1/realtime`, send:

```json
{"type":"session.start","protocol":1,"sample_rate":16000,"channels":1,"format":"pcm_s16le","max_tokens":256}
```

Then send little-endian mono PCM16 binary packets, normally 80–160 ms. The server
statefully upsamples to the checkpoint's 24 kHz. Send silence too: pauses do not
close or reset the recogniser's continuous conversation state. `session.ping`
is accepted; `session.end` flushes only when ending the complete session.

`session.started` reports the new session ID/epoch and checkpoint timing. Each
`transcript.chunk` contains:

```json
{"type":"transcript.chunk","session_id":"example","epoch":0,"chunk_index":1,
 "audio_start_ms":0,"audio_end_ms":2933.333,
 "raw_text":"Speaker 0: Hello. Speaker 1: 你好。",
 "segments":[{"voice_id":"0","text":"Hello."},{"voice_id":"1","text":"你好。"}],
 "word_timestamps":false,"inference_ms":200}
```

Text and segments are **deltas**, not the full transcript. A continuation without
another `Speaker N:` marker inherits the preceding temporary voice. A genuinely
unlabelled first segment uses null, not an invented speaker identity.

Audio spans describe chunk processing boundaries **only**. They are not word or
individual-speaker timestamps, and the adapter does not output separated audio.
The app cannot fingerprint a multi-voice chunk as an isolated example. Temporary
identity keys must include `session_id`, `epoch`, and `voice_id`.

At approximately 450 seconds, `session.rollover` announces a fresh epoch/cache
without reconnecting, retaining unconsumed lookahead. The app must finalise its
old text routes and forget temporary-to-person mappings, not saved People.
`session.done` completes an explicit end. Error events and bounded buffering
make overload visible; the service does not silently discard old audio.

## Offline verification

`python -m unittest discover -s Packaging/vibevoice -p test_protocol.py`
tests trained chunk configuration, packet-boundary-safe resampling, labels,
continuations, rollover and incomplete setup. These checks download nothing and
do not use a microphone, GPU, WSL or any inference dependencies. Runtime GPU
accuracy, latency, Windows↔WSL forwarding and coexistence with VRChat still need
an explicit real-model test; offline protocol checks cannot establish those.
