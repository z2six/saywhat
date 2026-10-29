# Optional overlapping-voice separation

Status: **real CPU ONNX inference and explicit download/evaluation implemented; live translation integration unavailable**.
The model has not been downloaded, loaded, or tested against real VRChat/Mandarin audio on this PC. All translation tools remain off.

## What this changes

CAM++ voice fingerprints answer “which known voice does this audio resemble?” They do not reconstruct two voices from a mixed recording. The existing Chinese CAM++ ONNX model is installed (28,281,138 bytes), and the existing model-keyed database contains two learned profiles. Neither file was modified by this work.

MossFormer2 is a different neural network: it estimates two separate waveform tracks from a single mono mixture. Those tracks could each go to a recognizer and translator. Its two output slots have arbitrary order, not stable identity. Matching their clean speech against enrolled voice fingerprints would restore names across windows; uncertain matches must remain unidentified, and processed/overlapping audio should not train the clean reference database.

Nothing here uses avatar positions, left/right panning, or microphone pitch as an identity.

## Selected experimental model

- Architecture/checkpoint: [Alibaba MossFormer2_SS_16K](https://huggingface.co/alibabasglab/MossFormer2_SS_16K), released through [ClearerVoice-Studio](https://github.com/modelscope/ClearerVoice-Studio).
- Windows-compatible export: [ipsilondev/MossFormer2-SS-16K-ONNX](https://huggingface.co/ipsilondev/MossFormer2-SS-16K-ONNX), a community INT8 export, not an official Alibaba ONNX build.
- Fixed revision: `abd8b1268dfded6b0e972ddae4c2dd6a3faf4f8a`.
- File size: 90,943,046 bytes. SHA-256: `1776c854784a2400feb7582ac4e9f922073cd706cb4fd216c98abad937d642b1`.
- Input: float32 mono 16 kHz waveform `inputs` shaped `[1,T]`. Outputs: `spk0`, `spk1`, each `[1,T]`.
- License: Apache-2.0 according to both model repositories. Preserve upstream attribution/license if packaging weights.

The model author reports separation across several benchmark datasets; the export author reports close agreement with the original PyTorch model on one short sample. That is evidence of an available algorithm and an export, **not proof of low-latency Mandarin/VRChat performance**. The export is tagged English. The upstream card mentions large-scale public and private training data without establishing Mandarin evaluation. This implementation therefore makes no accuracy or real-time guarantee.

## CPU-only evaluation contract

`OverlapModelCatalog.Models` supplies immutable download choices and the honest live-unavailable explanation. Downloading is explicit, goes to `<app-data>/models/separation`, checks the pinned size/hash, and never loads the model. Existing invalid files are rejected rather than overwritten. Interrupted unique partial downloads are cleaned up.

`OverlapSeparationService.LoadAsync(path)` explicitly loads the verified selected model. Only the ONNX CPU provider is used: two intra-operation threads, one inter-operation thread, sequential execution, idle spinning disabled. There is no GPU provider, automatic model load, microphone capture, server start, or cloud inference. The approximately 1 MB `Microsoft.ML.OnnxRuntime.Managed` 1.28.0 package works with the existing ONNX Runtime 1.28.2 native DLL supplied by the speaker-recognition runtime.

`SeparateAsync(mono16k, cancellationToken)` accepts 1–6 seconds, snapshots the samples, and returns two aligned waveform arrays plus compute duration/real-time factor. Calls do not accumulate a work backlog; a concurrent call is rejected. Cancellation signals ONNX Runtime termination. The common clipping gain preserves the relative energies instead of boosting each output independently. Highly correlated outputs are flagged, not assumed to be two people.

`SeparationWaveSample.ReadMono16k(path)` decodes an explicitly selected short 16 kHz mono/stereo PCM16/float32 WAV into memory. `EvaluateAsync(samples)` performs one warmup plus three measured runs and reports median/worst real-time factor. It neither plays nor saves the decoded/separated audio. The UI should dispose the service after evaluation so model memory is released.

An RTF of 0.3 would mean computation took 0.3 times the sample duration. It does **not** include waiting for the audio window, two recognition streams, embedding matching, translation, output rendering, or competition with VRChat. This is a noncausal finite-window model; naively running a four-second window introduces a significant context delay even when inference itself is fast.

## Required live integration before enabling

1. Maintain bounded overlapping audio windows only while speech is present; benchmark window size and stitching to avoid boundary losses/artifacts.
2. Run the actual separator, reject invalid/near-duplicate tracks, and match trusted voiced portions to the clean enrolled profiles. Account for source-order swaps without using spatial identity.
3. Route two estimated voices to independent ASR states. Reuse the incoming recognizer capacity through its managed session addresses rather than retaining an extra mixed recognizer as well. Own-voice recognition remains separate. Any backend without two simultaneous sessions must report unsupported rather than silently dropping a voice.
4. Measure end-to-end latency and CPU/GPU/VRAM impact while VRChat is running, then require explicit approval to enable. Backpressure must drop/log bounded stale work rather than grow the queue.
5. Show uncertain/failed separation honestly; do not claim that two clean sentences were recovered merely because a neural network returned two arrays. More than two concurrent talkers, music/singing, similar voices, reverb, and distorted network audio can defeat this model.

`OverlapModelCatalog.LivePipelineImplemented` deliberately remains false. Evaluation/download controls are functional; a live-enable switch must remain unavailable until the above exists and has been validated.

## Reference-conditioned extraction research

[WeSep](https://github.com/wenet-e2e/wesep) is relevant for extracting an enrolled target speaker using voice cues, as described in the [2024 toolkit paper](https://www.isca-archive.org/interspeech_2024/wang24fa_interspeech.pdf) and [2026 modular framework paper](https://arxiv.org/abs/2607.27436). Its current September 2026 master explicitly marks the validated local inference CLI, official pretrained packages and updated ONNX/C++ deployment as not yet released; the old deployment interface is legacy. It is not a trustworthy one-click default today. CAM++ embeddings cannot simply be plugged into a model trained for another encoder's conditioning representation. A supported pretrained extraction model with its matching encoder and enrollment contract is required.

Blind two-speaker separation followed by fingerprint matching is the practical experimental path implemented here. Genuine reference-conditioned extraction is a future alternative, not a renamed fingerprint checkbox.

## Offline verification

The offline tests in `LocalTalk/SeparationTests.cs` cover bounded input, privacy-safe local WAV decoding, pinned catalogue/checksum rejection, cancellation, no automatic loading, zero evaluation backlog, output alignment/validation, common clipping protection, identical-output warnings, and benchmark honesty. A tiny arithmetic ONNX graph verifies the real native CPU tensor adapter without downloading/loading the speech model. These tests prove plumbing, not voice-separation quality. No live audio, GPU, model download, or recognizer/translator service was used.
