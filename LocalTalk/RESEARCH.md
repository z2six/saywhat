# SayWhat? architecture and measurement policy

Source review: 5 October 2026. No competing application, speech engine, model download, live audio capture, or GPU benchmark was started for this review.

## Product identity and evidence limits

The exact name **VRCSL** has not been identified as a speech translator. The public project using that abbreviation is [VRCSecureLogin](https://github.com/TheArmagan/VRCSecureLogin), a credential vault/API proxy, not a translation engine. **[Dreaminko/VRCS](https://github.com/Dreaminko/VRCS)** is a plausible subtitle-tool candidate, but this is not a confirmed identification. The user should provide the exact product link before attributing a subscription, model, speed, or accuracy claim to “VRCSL.” [VRCSubtitle](https://centauri.gumroad.com/l/rrqay) is another unrelated name: an authored Udon subtitle system for worlds, not automatic speech translation.

Neither reviewed translator publishes a controlled comparison against our local pipeline on the same audio, language pairs, endpoint timing, hardware, and concurrent game load. “Faster” or “more accurate” therefore remains an observation to investigate, not an established benchmark result.

## VRCS candidate

VRCS documents two independent audio streams, Windows process/system loopback and microphone capture, Silero VAD, optional Smart Turn endpointing, incremental subtitles, and final microphone Chatbox output gated by verified mute state. Recognition can use local **whisper.cpp** (CPU/CUDA) or streaming cloud services: Alibaba Qwen3 ASR/Fun-ASR and OpenAI Realtime. Translation can use DeepL, Microsoft, cloud LLMs, or an OpenAI-compatible local server such as LM Studio/Ollama. Local Whisper keeps speech local; cloud recognition sends detected speech to the selected provider, and cloud translation sends the relevant text/context. These are configuration choices, not a universally local or universally cloud architecture. [Official README](https://github.com/Dreaminko/VRCS), [Rust core architecture](https://github.com/Dreaminko/VRCS/tree/main/core).

Inference: a demonstration using streaming cloud recognition avoids competing with the game for local ASR compute. Voice gating, early incremental subtitles, and endpoint choices can also improve perceived responsiveness. None of those establish better final transcription or translation accuracy.

## VRCT

VRCT supports local Whisper and Google recognition, independently configurable microphone/speaker silence and phrase intervals, and CPU/CUDA precision choices. Its current Whisper helper uses **faster-whisper** with locally stored models. [Transcription documentation](https://misyaguziya.github.io/VRCT-Docs/docs/3.4.3/config-transcription/), [current Whisper implementation](https://github.com/misyaguziya/VRCT/blob/develop/src-python/models/transcription/transcription_whisper.py).

Local translation includes task-specific **M2M100/NLLB through CTranslate2**, while cloud APIs and local LM Studio/Ollama servers are alternatives. The current source lists M2M100 418M/1.2B and NLLB distilled 600M/1.3B/3.3B variants; its shared tokenizer/model access is locked to prevent microphone/speaker language-state races. [Translation source](https://github.com/misyaguziya/VRCT/blob/develop/src-python/models/translation/translation_translator.py), [translation settings](https://misyaguziya.github.io/VRCT-Docs/docs/3.4.3/config-translation/).

Inference: a smaller dedicated translation model, shorter input/output, and fewer repeated partial translations can finish sooner than a much larger general-purpose LLM. Language-pair fixtures are needed to determine whether that speed preserves the conversational quality we want.

## Price and privacy are separate from speed

VRCT's creator listing offers the application for **0 JPY** with optional donations. That page also contains older Google-only architecture text, so current source/docs are better evidence for available engines. A free application does not make every selected API free. VRCS explicitly warns that provider charges may apply. [Creator listing](https://misyaguziya.booth.pm/items/5155325), [VRCS installation/privacy documentation](https://github.com/Dreaminko/VRCS).

Provider pricing is service-, region-, tier-, and date-dependent. Google currently lists standard Speech-to-Text V2 recognition from **$0.016/minute** in its first usage tier; that is not proof VRCT's Google connector uses that billed API. DeepL's current help page describes legacy Free accounts with 500,000 characters/month but states that the Free/Pro plans can no longer be purchased. Do not promise that new users can obtain that old free allowance. [Google's pricing](https://cloud.google.com/speech-to-text/pricing), [DeepL's current plans](https://support.deepl.com/hc/en-us/articles/360021200939-DeepL-API-plans).

SayWhat? remains all-local: no cloud fallback, no unrequested download, and no content telemetry.

## Shared translation model versus two loaded models

One loaded translation model can serve independently constructed microphone and incoming requests. Model weights do not become a shared conversation history: each request must contain only its own explicitly selected messages/context, with independent attribution and cancellation. Server slots represent individual inference sequences; continuous batching shares the GPU work, not the meaning of their conversations. One residency saves weights/cache overhead. Two instances on the same GPU duplicate memory and still compete for the same compute; they may worsen latency. Incorrect application-level history/tokenizer sharing can cause contamination, so request/context isolation is required regardless of residency. [llama.cpp server internals](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README-dev.md), [LM Studio parallel requests](https://lmstudio.ai/docs/app/advanced/parallel-requests).

Default: one shared local translation residency, bounded request concurrency, separate stateless direction contexts. A second residency is an explicit option, not a presumed speed upgrade.

## Local catalog and honest recommendations

The initial same-family candidates are Qwen3 **1.7B, 4B, 8B**, with optional **14B**, using pinned Q4_K_M artifacts. These are size/residency candidates, not measured accuracy rankings. Use a supported hard non-thinking template setting for short translation requests; do not rely on hiding reasoning after spending tokens generating it. [Official Qwen3 GGUF model card](https://huggingface.co/Qwen/Qwen3-4B-GGUF), [official thinking-mode controls](https://github.com/QwenLM/Qwen3/blob/main/docs/source/getting_started/quickstart.md).

The advisor reserves game VRAM (default: at least 4 GiB or one quarter of dedicated VRAM), budgets both ASR instances and translation residency, and checks system RAM independently. Static estimates produce **provisional** advice only. Parameter count and VRAM capacity never imply a measured speed/accuracy result.

Certification requires matching hardware/driver, runtime/model artifact, language-pair fixtures, endpoint timing, context/output limits, and concurrency. It gates on quality fixtures, p95 last-voice-to-final latency, ASR real-time factor/lag, total-app peak memory, and zero dropped audio/runtime failures. Gaming recommendations require measurements alongside the game. Among passing configurations, measured final latency wins over model size.

Benchmarks require an explicit user Start action and already-installed local models/prerecorded fixtures. The current module supplies a runner contract and synthetic tests, not an automatic engine-loading implementation. No benchmark was run while the user was gaming.
