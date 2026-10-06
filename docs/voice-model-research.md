# Local voice models: identification and overlapping speech

Research checked on **6 October 2026**. This page distinguishes implemented options from possible future work. Version 0.1.24 adds optional VibeVoice streaming Groups recognition; the other research candidates below are not automatically installed or benchmarked in VRChat.

## What needs to improve

Three different jobs are involved:

1. **Recognize a familiar person:** compare a clear voice example against saved local voice features. SayWhat? currently does this with CAM++ or ERes2Net.
2. **Track who is talking over time:** detect when each voice is active, including moments with more than one active voice. This is usually called speaker diarization.
3. **Recover each person's speech during overlap:** estimate separate audio signals from the mixed playback. This is speech separation, or target-speaker extraction if a known person's voice is used as a reference.

A stronger identity model alone does not perform the third job. Diarization can report that two people are active without recovering either person's words. A separator estimates audio, but its output A/B can swap between chunks and is not automatically a persistent person identity.

## Candidate comparison

| Model / family | Main job | Useful role in SayWhat? | Important constraint | Current status |
| --- | --- | --- | --- | --- |
| CAM++ | Voice identity | Low-overhead local matching of clear examples | Not a waveform separator; ambiguous audio should remain unidentified | Available in the app |
| ERes2Net | Voice identity | Alternative identity model already selectable | More computation is not a guarantee of better VRChat results | Available in the app |
| ERes2NetV2 | Voice identity | Candidate for improved short-example matching with a more efficient architecture | Needs a verified compatible export and tests with VRChat compression and changing microphones | Research only |
| VibeVoice ASR Streaming 1.5B / 7B | Attributed speech transcription | Optional Groups mode: separate text routes and bubbles for temporary voices, with guarded matching to saved People | Whole-chunk spans only; no isolated audio or word timestamps, and extra buffering/memory | Available through explicit preparation in 0.1.24; live performance not established |
| NVIDIA Nemotron 3 Diarization | Streaming voice activity / diarization | Most interesting new candidate for stable temporal tracking and detecting overlap | Outputs activity probabilities for up to eight voices, **not separate audio** | Research only |
| NVIDIA Streaming Sortformer 4spk v2.1 | Streaming diarization | Older four-voice comparison baseline for Nemotron | Four output voice channels; still needs separate identification and overlap extraction | Research only |
| pyannote Community-1 | Diarization | Useful offline accuracy baseline and voice-count evaluation | Initial model access requires accepting conditions; the standard pipeline is file-oriented rather than a drop-in low-latency live component | Research only |
| MossFormer2 SS 16K | Blind speech separation | Actual overlapping-audio extraction candidate | Two estimated sources, noncausal processing and identity swaps require additional handling | Pinned INT8 ONNX **local-WAV evaluation only**, not live translation |
| SpeechBrain SepFormer WHAMR | Speech separation | Noise/reverberation comparison baseline | Published model expects 8 kHz input; validation is based on WHAMR rather than VRChat or Mandarin conversations | Research only |
| WeSep / target-speaker extraction | Extract a chosen known voice | Promising use of the user's clean labeled examples as enrollment references | Current upstream release is a research preview; official pretrained packages and deployment interfaces are not yet production-ready | Research only |

## The most useful new finding: Nemotron 3 Diarization

NVIDIA released this model on **23 September 2026**. Its official model card describes a 100-million-parameter streaming model with up to eight voice channels and a cache of previously observed voices. The lowest recommended input-buffer configuration is **0.32 seconds**; that is buffering, not total computation or translation latency. It emits per-voice activity probabilities, not separated waveforms or saved people's names. [Official model card](https://huggingface.co/nvidia/Nemotron-3-Diarization).

NVIDIA's native **NeMo-Speech.cpp** runtime now documents Nemotron and Sortformer diarization, a streaming C interface, Windows builds and CPU/CUDA/Vulkan backends. A native component could avoid bundling a large Python environment. Backend availability and documentation do not establish how fast the combined app runs while VRChat uses the same GPU. [Runtime](https://github.com/NVIDIA/NeMo-Speech.cpp), [build options](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/main/docs/build.md), [native SDK](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/main/docs/sdk.md).

**Assessment:** Nemotron remains a possible voice-activity/overlap prototype, not the Groups engine shipped in 0.1.24. Existing saved-person matching would still be needed to associate its temporary channels with names. It does not itself recover overlapping words. The implemented VibeVoice path and its evidence limits are described in [architecture](architecture.md#groups-attributed-text-not-separated-audio).

## Recovering overlapping words

MossFormer2 is the closest candidate to the missing audio-extraction stage already present in SayWhat?'s experimental tools. The official 16 kHz model is intended to separate mixed voices; its card lists Apache-2.0 terms. SayWhat?'s existing pinned INT8 ONNX export is about 91 MB and runs only through explicit local-file evaluation on CPU. It does not currently process live VRChat playback. [Upstream model](https://huggingface.co/alibabasglab/MossFormer2_SS_16K), [ClearerVoice-Studio](https://github.com/modelscope/ClearerVoice-Studio/blob/main/clearvoice/README.md).

SepFormer WHAMR supplies a useful alternative trained with noise and reverberation, but its published interface requires mono 8 kHz audio and the authors do not warrant quality on other datasets. It is not an automatic upgrade for Mandarin or a proven streaming solution. [Official model card](https://huggingface.co/speechbrain/sepformer-whamr).

Target-speaker extraction could make particularly good use of clean examples labeled by the user: extract a chosen person's voice rather than simply outputting two anonymous channels. The current WeSep toolkit describes this task, but labels its release a research preview and says official pretrained packages, automatic downloads and production deployment are still pending. It is a direction to monitor, not an appropriate automatic dependency today. [Upstream status](https://github.com/wenet-e2e/wesep).

## Lighter and heavier identity models

CAM++ remains the low-overhead starting point. ERes2Net is already an option. ERes2NetV2's authors specifically address short-duration verification and computational efficiency, so it is worth testing instead of assuming the biggest identity model is best. The 3D-Speaker project also supplies other verification architectures and recipes. These options compare identities; they do not unmix simultaneous speech. [3D-Speaker](https://github.com/modelscope/3D-Speaker), [ERes2NetV2 paper](https://arxiv.org/abs/2406.02167).

Community-1 improves speaker assignment/counting and can run fully offline after its gated initial download. Its regular output and its additional exclusive output have different uses: an exclusive timeline simplifies assigning transcript timestamps, but should not be mistaken for recovering two simultaneous transcripts. It is better suited to an offline comparison first than an unmeasured live replacement. [Official Community-1 card](https://huggingface.co/pyannote/speaker-diarization-community-1).

## Future waveform-separation evaluation

1. Keep current recognition and translation available as the baseline. Do not silently load a new GPU model or change saved voice profiles.
2. Test Nemotron tracking on explicitly provided recordings: two voices, alternating turns, genuine overlap, song playback, whispering and speaker re-entry. Verify that temporary channel changes do not create new people.
3. Compare MossFormer2 against SepFormer on the same overlap excerpts. Listen to the estimated audio **and** measure recognition of both people's words; separation timing alone does not establish useful translation quality.
4. Match separated channels back to clean local voice examples, handling A/B swaps and unknown voices. Do not learn from contaminated overlap or music.
5. Only then test two simultaneous speech-recognition streams and translation scheduling. Require bounded queues, cancellation and a clear low-resource fallback.
6. Measure total latency and VRChat frame-time impact with recognition, translation and the game running together. An RTX 5090 is a good test platform, but does not remove buffering or guarantee acceptable game performance.

No audio is uploaded as part of this proposal. Any sample export, model download or evaluation needs an explicit user action. A live implementation should remain optional and have a separate resource budget and stop/unload controls.

## What has not been measured

No candidate was live-benchmarked for this documentation update. The implemented Groups path has offline routing, safety and preparation checks, not an in-game GPU accuracy/latency result. There is no justified measured claim yet for peak GPU memory, final translation delay, Mandarin accuracy, song rejection or frame-rate impact in the combined Groups pipeline. Published model benchmarks use different datasets and must not be compared as if they were the same VRChat test.

See [the technical documentation index](README.md) for current settings and implementation details.
