# Models and runtimes

[Documentation index](README.md) · [Settings](settings-reference.md) · [Voice-model research](voice-model-research.md)

## Three different tasks

| Task | Current component | Input → output |
| --- | --- | --- |
| Speech recognition: microphone, Quick/Normal subtitles | Voxtral Mini 4B Realtime 2602 Q4_K_M | Audio → source text |
| Speech recognition: optional Groups subtitles | VibeVoice ASR Streaming 1.5B or 7B | Mixed audio → text attributed to temporary voices |
| Translation | Selected local GGUF translator | Source text → chosen language |
| Optional voice recognition | Compatible sherpa-onnx voice-embedding model | Clear audio sample → fingerprint for matching |

Fingerprint-based voice recognition does not translate, identify a VRChat account, or extract individual voices from a mixture. Groups is a separate speech recognizer: its attributed text is not isolated audio, and its temporary voice labels are not saved People identities. [These distinctions](people-and-voice-recognition.md#identification-diarization-and-separation) matter when learning from examples.

The automatic-setup CAM++ artifact is size/hash pinned. The current optional voice-model download button uses HTTPS and completeness checks, but does not provide the same per-artifact pinned SHA-256 verification as the translation catalog. Imported ONNX files are user-selected artifacts. Do not treat every optional voice model as independently verified just because it appears in the picker.

## Translation catalog

Sizes below are approximate **download sizes in decimal GB/MB**, not peak RAM or VRAM. Context buffers, compute workspaces and simultaneous speech engines also need memory. Catalog downloads are pinned by revision, expected size and SHA-256. Imported models must match the expected model/architecture; compatible architecture alone cannot prove that an imported file contains the same fine-tuning or prompt template.

| Model / managed ID | Download | Role / restriction |
| --- | --- | --- |
| Hy-MT2 1.8B / `hy-mt2-1.8b-q4_k_m` | 1.13 GB | Speed-first default; dedicated translation model. |
| Hy-MT2 7B / `hy-mt2-7b-q4_k_m` | 4.62 GB | Larger translation candidate; more memory/compute, not guaranteed better end-to-end delay. |
| TranslateGemma 4B / `translategemma-4b-q4_k_m` | 2.49 GB | Dedicated translation; explicit source language and SayWhat?'s built-in template/runtime required. |
| Liquid LFM2 350M EN–JP / `lfm2-350m-enjp-mt-q4_k_m` | 229 MB | English ↔ Japanese only, explicit source. Not available for Chinese/Korean target pairs. |
| Qwen3 1.7B / `qwen3-1.7b-q4_k_m` | 1.28 GB | Optional general-purpose alternative; not a default recommendation. |
| Qwen3 4B / `qwen3-4b-q4_k_m` | 2.50 GB | Optional general-purpose alternative. |
| Qwen3 8B / `qwen3-8b-q4_k_m` | 5.03 GB | Optional general-purpose alternative. |
| Qwen3 14B / `qwen3-14b-q4_k_m` | 9.00 GB | Optional larger alternative; substantial gaming-memory impact. |

Upstream model information: [Tencent Hy-MT2](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF), [Google TranslateGemma](https://huggingface.co/google/translategemma-4b-it), [Liquid EN–JP GGUF](https://huggingface.co/LiquidAI/LFM2-350M-ENJP-MT-GGUF), [Qwen3](https://huggingface.co/Qwen/Qwen3-8B-GGUF). Model terms are separate from the application's GPL license; the download flow asks for explicit acceptance where required. The TranslateGemma GGUF in this catalog is a third-party conversion, not an official Google GGUF artifact.

## Prompt and language adapters

Hy-MT2 uses its dedicated user-task translation prompt rather than the generic general-purpose system prompt. Generic Qwen translation disables thinking and requests only the translation. Additional microphone targets reuse the selected model and glossary through sequential single-target requests; they do not load an additional model for each language.

TranslateGemma uses a managed text-only wrapper that renders the translation task's explicit source/target fields. External arbitrary templates are refused for this adapter. Its managed context is 2,048 tokens with output reservation; source input is capped at 1,024 UTF-8 bytes. Oversized source fails visibly rather than silently translating a clipped sentence. Liquid uses explicit English/Japanese task prompts. Dedicated Liquid/TranslateGemma adapters do not receive generic conversation context/glossary or Qwen reasoning toggles.

The subtitle **Careful** style permits 192 output tokens and a 22-second request deadline; **Fast** permits 128 output tokens and a 12-second deadline. These are ceilings, not intentional delays. Legacy Balanced uses 128/14 seconds, and legacy Quality uses 192/22 seconds plus prior-context behavior. A style change is not model replacement or proof of improved accuracy.

## Shared versus separate translators

Sharing is recommended because one model is loaded once and used by both directions. Each request still carries its own task/context. Sharing does not blend microphone text into incoming subtitles. The managed server has one inference slot, so simultaneous directions compete for compute/queue time.

Turning sharing off runs two server instances, normally on ports 1235 and 1236. Even selecting the **same** model separately duplicates residency. Two instances on one GPU do not create twice the hardware throughput, and can hurt VRChat frame time. Separate selection is useful when genuinely different language pairs or quality requirements justify it, not as a way to isolate people.

## Translation acceleration

Built-in translation uses pinned `llama.cpp` server release **b11401** on native Windows:

- **Vulkan:** normal packaged starting engine, supporting compatible NVIDIA/AMD/Intel hardware.
- **CUDA 13.4:** optional NVIDIA runtime; detected NVIDIA hardware alone does not prove compatible driver/runtime support.
- **CPU:** testing/fallback option with GPU offload disabled; usually competes more directly with the game's CPU budget.

The managed server uses a 4,096-token context (2,048 for TranslateGemma), one parallel slot, four processing/batch threads, batch 128, microbatch 64, automatic flash attention, and reasoning off. GPU backends request GPU layer offload; actual supported residency remains a runtime/hardware matter. These are implementation defaults, not all exposed dashboard knobs.

## Speech runtime

SayWhat? uses [MrShitFox/voxtral.cpp](https://github.com/MrShitFox/voxtral.cpp), pinned to commit `3d1ed513debc88b4a383ad83e0cd0bcb84889a60` with application-local changes. The model is the **Realtime** variant, not offline Voxtral Mini. Its Q4_K_M download is about 2.90 GB.

Speech runs under WSL2 Ubuntu. Automatic setup installs/builds the speech component after consent; NVIDIA setup selects the managed CUDA path where supported, while other/manual configurations can use registered supported backends. Native Windows translation acceleration and WSL speech acceleration are **separate choices**. A Windows Vulkan translator working does not prove that WSL Vulkan speech works.

Running both directions needs independent recognition services: microphone Voxtral plus incoming Voxtral or VibeVoice. Model file size understates their live memory requirements. Normal Stop/Quit releases app-owned instances; it does not remove cached weights.

### Optional Groups runtime

Groups uses **VibeVoice-ASR-Streaming**, not offline VibeVoice ASR or text-to-speech. **Groups model options** offers 1.5B (about 6 GB of model downloads) and 7B (about 18 GB), plus several gigabytes of local runtime dependencies. Download size is not a peak-VRAM estimate. Start with 1.5B; a larger model is not a speed fix.

Preparation is a separate consented operation using the registered speech WSL distribution. The app manages the local Python service and cached model files; no separate user-installed translation UI is required. Weights are not bundled in the installer. Inference runs offline through the loopback service, and ordinary Start refuses missing preparation rather than installing files in the background.

The streaming model buffers roughly 3.5 seconds of audio for the first decode before compute/translation time. It returns attributed text, not separated audio or word timestamps. Its output feeds the **same chosen translator** used for Normal subtitles; it does not load a translator per voice. Optional saved-person fingerprints still use the original captured audio only when suitable evidence is available. See [Groups architecture and evidence limits](architecture.md#groups-attributed-text-not-separated-audio).

Groups recovery, routing and preparation have offline checks. Actual GPU performance, overlap accuracy and frame-time impact with VRChat require live testing; no measured speed advantage is claimed.

## Hardware advice and measurement

Hardware inspection is local and metadata-only. It inventories CPU, system RAM and DXGI GPU memory/driver identity without loading models. Advice reserves game memory and budgets all active speech/translation instances, not just translation weights. It recommends dedicated Hy-MT candidates rather than automatically choosing a large general-purpose model.

Without matching in-game measurements, advice is provisional. The larger model fitting in VRAM is not evidence that it meets latency goals. The dashboard's explicit translation test sends four neutral text samples to an **already-running** local translator and reports translation-only timing; it does not measure recognition, OSC delivery, game frame time, or translation quality. Do not mistake this for a full-pipeline benchmark.

No claim of “fastest” or “best Mandarin translation” is made for an unmeasured catalog choice. Start with Hy-MT2 1.8B, compare Hy-MT2 7B on real phrases, and assess delay **while VRChat is running**.
