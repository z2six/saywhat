# Local translation-focused models

Research checked 2026-10-06. This document is an offline shortlist, not a benchmark or an automatic model change. Only public documentation, licenses, configuration, tokenizer templates, runtime source, and Hugging Face API metadata were read for the model research. No model weights, even ranged weight-file headers, were fetched and no inference/GPU workload was started. Adapter tests use synthetic text and mocked endpoints, not real models. Checksums below are publisher/HF metadata checksums, not locally recomputed checksums.

## Practical answer

Yes: purpose-trained translation models are worth comparing with general Qwen for English–Chinese. A general model's coding ability is not a switch that must be turned off; different training and a task-specific request matter. Specialization does not prove that a particular quantized model is faster or better for conversational subtitles on this PC.

My initial evaluation order is Hy-MT2-1.8B Q4_K_M, Hy-MT2-7B Q4_K_M, then TranslateGemma-4B Q4_K_M. This is a proposed experiment order: the smallest official translation model first, a larger same-family comparison second, and an independent family third. Keep Qwen as a measured baseline. No default should change until both translation directions, latency under game load, and memory headroom pass the same fixtures.

| Candidate | Why investigate | Local integration status | License boundary |
| --- | --- | --- | --- |
| Hy-MT2 1.8B / 7B | Newer dedicated Tencent translation family, including English and Chinese | Official standard GGUF; hunyuan-dense and Q4_K_M supported in pinned llama.cpp source; actual load/output not tested | Actual pinned LICENSE.txt is Apache-2.0 |
| TranslateGemma 4B / 12B / 27B | Independent Google translation fine-tunes, 55-language evaluation | Optional 4B download; explicit source language and model-specific managed template; real output/latency still require acceptance testing | Gemma terms, acceptance and distribution/use obligations; not Apache |
| LFM2-350M-ENJP-MT | Small translation-specific Liquid AI model | Optional official GGUF; **English↔Japanese only**; unsupported language pairs rejected | LFM Open License 1.0; commercial revenue threshold, not Apache |
| MADLAD-400 3B | Older encoder–decoder translation baseline with broad language coverage | T5 is in llama.cpp; do not assume every existing GGUF is llama.cpp-compatible | Apache-2.0 model; separate converter/runtime integration |
| M2M100 418M | Small conventional many-to-many translator covering English, Chinese, Japanese and Korean | Separate encoder–decoder backend and explicit language tokens; potential future CTranslate2 comparison, not offered as a chat-model download | MIT model; separately review backend/dependencies |
| NLLB-200 distilled 600M | Small conventional translation baseline | Separate encoder–decoder/tokenizer integration, not a Qwen chat replacement | CC-BY-NC-4.0 and research/non-production model-card warnings |
| HY-MT1.5 / original Hunyuan-MT-7B | Older specialized Tencent generations | Standard GGUFs exist for 1.5, but not the preferred new-app choice | Old licenses exclude EU, UK and South Korea; do not substitute for new Apache Hy-MT2 |

Hy-MT2 was released May 21, 2026. Tencent supplies standard GGUFs plus experimental extreme-low-bit variants. The authors' reported translation results make the family a relevant candidate, not proof of SayWhat? translation quality or latency. [Official 1.8B card](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF), [technical report](https://arxiv.org/abs/2605.22064).

## Pinned download metadata — no downloads performed

Each download link contains the full repository revision instead of mutable main. A future, explicitly requested installer must verify the byte count and SHA-256 after download, retain the applicable license, and reject mismatches. File size is not total VRAM: KV cache, compute buffers, both speech engines, and the game need additional memory.

### Official Tencent standard GGUFs

1. Repository tencent/Hy-MT2-1.8B-GGUF; revision a0c709d9fac510f2c807aa3af52872340dc37a4a.

   File Hy-MT2-1.8B-Q4_K_M.gguf; 1,133,080,448 bytes; SHA-256 dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699.

   [Pinned download](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/a0c709d9fac510f2c807aa3af52872340dc37a4a/Hy-MT2-1.8B-Q4_K_M.gguf), [pinned license](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/blob/a0c709d9fac510f2c807aa3af52872340dc37a4a/LICENSE.txt), [API metadata](https://huggingface.co/api/models/tencent/Hy-MT2-1.8B-GGUF?blobs=true).

2. Repository tencent/Hy-MT2-7B-GGUF; revision ab8472660ac61fac25f1af43fac2599d52a8a775.

   File Hy-MT2-7B-Q4_K_M.gguf; 4,624,648,896 bytes; SHA-256 9f96256500f3fc1ab4d64336b58f52a949a95ad7516b0c229476eef782f9f77b.

   [Pinned download](https://huggingface.co/tencent/Hy-MT2-7B-GGUF/resolve/ab8472660ac61fac25f1af43fac2599d52a8a775/Hy-MT2-7B-Q4_K_M.gguf), [pinned license](https://huggingface.co/tencent/Hy-MT2-7B-GGUF/blob/ab8472660ac61fac25f1af43fac2599d52a8a775/LICENSE.txt), [API metadata](https://huggingface.co/api/models/tencent/Hy-MT2-7B-GGUF?blobs=true).

Important unresolved metadata discrepancy: HF's aggregated 7B GGUF metadata reports eos_token as "$", while the pinned base tokenizer names <|eos|> (token 127960). This could be HF parsing rather than an artifact defect. No weight/header read or inference was performed to establish the cause. Keep the 7B candidate experimental and verify actual termination in its future acceptance test. The 1.8B API/base EOS names agree. [Pinned 7B tokenizer](https://huggingface.co/tencent/Hy-MT2-7B/blob/9b0eb4e8f001def3e5ff6469a0ac96fdb39ec223/tokenizer_config.json).

### TranslateGemma — third-party GGUF conversions

These are bullerwins conversions of Google's model, not Google-published GGUFs. Converter metadata identifies gemma3 for the 4B artifact. Acceptance and attribution obligations survive conversion. [Converter card](https://huggingface.co/bullerwins/translategemma-4b-it-GGUF).

| Model | Revision | File size (bytes) | SHA-256 |
| --- | --- | ---: | --- |
| 4B | 7c938465a870d8624bcfa98a8e4a3510053c19a8 | 2,489,909,312 | 7f7357c14abd9da4eb200b38b05da502cd6e10d7e1d403fbc9f78c19f3209b72 |
| 12B | d7d1d8cc4ff53d4bc883ef33eae3894f07833b63 | 7,300,793,664 | 9196d728812afbf5efc10b539298585725edc3a4ecc092c22fdde5bbaf41879e |
| 27B | 648ab470301440606384471ef4edb1ffc2a4b88c | 16,546,704,032 | 475bb629b999b4a197f5f0165503bc935a28e39a34cbeb32f73fc8b683deb5fa |

[4B pinned Q4_K_M download](https://huggingface.co/bullerwins/translategemma-4b-it-GGUF/resolve/7c938465a870d8624bcfa98a8e4a3510053c19a8/translategemma-4b-it-Q4_K_M.gguf), [12B pinned Q4_K_M download](https://huggingface.co/bullerwins/translategemma-12b-it-GGUF/resolve/d7d1d8cc4ff53d4bc883ef33eae3894f07833b63/translategemma-12b-it-Q4_K_M.gguf), [27B pinned Q4_K_M download](https://huggingface.co/bullerwins/translategemma-27b-it-GGUF/resolve/648ab470301440606384471ef4edb1ffc2a4b88c/translategemma-27b-it-Q4_K_M.gguf).

Metadata sources: [4B API](https://huggingface.co/api/models/bullerwins/translategemma-4b-it-GGUF?blobs=true), [12B API](https://huggingface.co/api/models/bullerwins/translategemma-12b-it-GGUF?blobs=true), [27B API](https://huggingface.co/api/models/bullerwins/translategemma-27b-it-GGUF?blobs=true). Larger tiers are optional experiments, not recommendations based only on available memory.

## Pinned llama.cpp compatibility

Runtime tag b11401 resolves to commit a7fb71fab83b474a0892b9a05aaa3a8ddca2729b. Both official Hy-MT2 GGUF APIs report architecture hunyuan-dense. Base configs name HunYuanDenseV1ForCausalLM. The pinned converter registers that model class, the runtime recognizes its architecture, and its graph implementation exists. Ordinary Q4_K_M is a supported file type. This establishes source compatibility, not a successful load or backend-specific correctness result.

- [Architecture mapping](https://github.com/ggml-org/llama.cpp/blob/a7fb71fab83b474a0892b9a05aaa3a8ddca2729b/src/llama-arch.cpp)
- [Hunyuan conversion](https://github.com/ggml-org/llama.cpp/blob/a7fb71fab83b474a0892b9a05aaa3a8ddca2729b/conversion/hunyuan.py)
- [Dense graph implementation](https://github.com/ggml-org/llama.cpp/blob/a7fb71fab83b474a0892b9a05aaa3a8ddca2729b/src/models/hunyuan-dense.cpp)
- [Q4_K_M loader](https://github.com/ggml-org/llama.cpp/blob/a7fb71fab83b474a0892b9a05aaa3a8ddca2729b/src/llama-model-loader.cpp)
- [Jinja template application](https://github.com/ggml-org/llama.cpp/blob/a7fb71fab83b474a0892b9a05aaa3a8ddca2729b/common/chat.cpp)

Use the model's embedded template with --jinja, not Qwen/ChatML special tokens. The 1.8B and 7B Hy-MT2 templates are different. [Pinned 1.8B template](https://huggingface.co/tencent/Hy-MT2-1.8B/blob/9a341cd1b679d3efd23b46e847b01745a71ed792/chat_template.jinja), [pinned 7B template](https://huggingface.co/tencent/Hy-MT2-7B/blob/9b0eb4e8f001def3e5ff6469a0ac96fdb39ec223/chat_template.jinja).

Do not select the advertised 440-MB/1.25-bit AngelSlim variant as a drop-in replacement. Its STQ1_0 support PR is still open, and standard runtime compatibility is not established. Standard Q4_K_M does not require that new ternary format. [Upstream PR 22836](https://github.com/ggml-org/llama.cpp/pull/22836).

## Request adapters must be model-specific

### Hy-MT2

Send one user message containing the translation task; no general-assistant/coding/Qwen system message or reasoning toggle. The documented English form is:

    Translate the following text into TARGET_LANGUAGE. Note that you should only output the translated result without any additional explanation:
    SOURCE_TEXT

Use full names such as Chinese or English, not zh/en. Chinese-language prompts use 中文/英语. Source-language inference is implicit: an app setting Any must not be inserted as a language name. The official glossary pattern puts source/target terminology examples before the task. Keep the request independent for each utterance and direction. [Official 7B instructions](https://huggingface.co/tencent/Hy-MT2-7B-GGUF).

For 1.8B/7B the card recommends temperature 0.7, top_p 0.6, top_k 20 and repetition_penalty 1.05. These are the authors' settings, not measured optimal SayWhat? settings. Temperature 0 would be a separate deterministic comparison. Bound output tokens for short subtitles instead of copying the card's 4096-token ceiling. Chinese is the documented default name; an explicit simplified-script instruction is an extension to test, not a validated new language identifier. Traditional Chinese is separately listed by the card.

### TranslateGemma

Google's template only accepts user and assistant roles. A user content list contains exactly one text/image entry with source_lang_code and target_lang_code; text contains the raw source, not generic translation instructions. Source Any is not a supported code. Google states a 2K input context. [Google model card](https://huggingface.co/google/translategemma-4b-it).

Transformers text shape:

    {"role":"user","content":[{"type":"text","source_lang_code":"en","target_lang_code":"zh","text":"SOURCE_TEXT"}]}

The upstream llama.cpp language-input fix documented this OpenAI-style shape:

    {"messages":[{"role":"user","content":"SOURCE_TEXT"}],"chat_template_kwargs":{"source_lang_code":"en","target_lang_code":"zh"}}

Reverse the codes for Chinese→English. [Merged upstream PR 19052](https://github.com/ggml-org/llama.cpp/pull/19052). The pinned b11401 chat parser no longer supplies those per-content language fields to the embedded template. SayWhat? therefore pre-renders Google's exact text translation task and uses a dedicated text-only Gemma role wrapper in its managed server. The model is not offered through an arbitrary external endpoint whose template cannot be verified. Do not treat Google's automatic HF library examples about image descriptions as the actual translation protocol. Source language must be explicitly selected for this option; automatic language selection remains available with Hy-MT2.

### Liquid AI LFM2-350M-ENJP-MT

This is a specialized **English↔Japanese** model, not a smaller general multilingual translator. One request uses the exact system message `Translate to Japanese.` or `Translate to English.` followed by the source as a user message. Same-language input can pass through; Chinese, Korean and other source/target selections are not allowed. [Official model card](https://huggingface.co/LiquidAI/LFM2-350M-ENJP-MT), [official GGUF card](https://huggingface.co/LiquidAI/LFM2-350M-ENJP-MT-GGUF).

The official Q4_K_M artifact is 229,310,240 bytes at revision `889c88c3d3681b4f342ae210a1d2a1369aba0e10`. Its small weight size makes it worth testing for this specific pair, but does not establish faster end-to-end subtitles or better translation on this PC. Speech recognition, sentence timing and contention still contribute to latency. The LFM Open License 1.0 is not an unrestricted Apache grant: its commercial permission has an annual-revenue threshold. Model selection presents its terms before download. [License](https://huggingface.co/LiquidAI/LFM2-350M-ENJP-MT-GGUF/blob/889c88c3d3681b4f342ae210a1d2a1369aba0e10/LICENSE).

### Conventional encoder–decoder models

MADLAD prepends a target-language token such as <2zh> or <2en> to the source; it is not an instruction-chat model. Its official repo includes a Candle-oriented model-q4k.gguf (revision fa184c675da0b5c9e1c8694fccd4e12e2d422094; 1,654,597,280 bytes; SHA-256 ea6e5531a3e95213c7f0635988d119e078a655c09306e47851e15d4c0c3f9c37). This is not a verified llama.cpp artifact, so it is deliberately excluded from the download shortlist. The llama.cpp maintainers' discussion documents the old GGUF compatibility distinction. [Google MADLAD card](https://huggingface.co/google/madlad400-3b-mt), [runtime discussion](https://github.com/ggml-org/llama.cpp/discussions/8300).

NLLB needs its source/target language-token conventions and a seq2seq path; its published weights are not an Apache general chat model. Its model card explicitly describes research use, excludes production deployment, and warns about sequences above the trained 512-token input length. [Meta NLLB card](https://huggingface.co/facebook/nllb-200-distilled-600M). Neither conventional model should be sent to the existing Qwen system/user adapter merely because a conversion ends in .gguf.

M2M100 418M is another translation-only candidate with an MIT license and all four main app languages. It needs explicit source and target language tokens and a separate encoder–decoder runtime. CTranslate2 documents M2M100 conversion/inference, but that does not make it a drop-in replacement for the existing llama.cpp server. It is a future measured comparison, not a claim of superior quality or a downloaded app option. [Official model](https://huggingface.co/facebook/m2m100_418M), [CTranslate2 Transformers guide](https://opennmt.net/CTranslate2/guides/transformers.html), [backend license](https://github.com/OpenNMT/CTranslate2/blob/master/LICENSE).

## Redistribution and future acceptance gates

Apache Hy-MT2 and MADLAD require the appropriate license/attribution and any applicable NOTICE handling. TranslateGemma carries Google's Gemma restrictions and distribution obligations, including passing on terms and restrictions; a third-party GGUF is not a license bypass. [Gemma terms](https://ai.google.dev/gemma/terms).

The old HY-MT1.5 and original Hunyuan-MT license grants explicitly exclude the EU, UK and South Korea. Do not recommend those artifacts for this Berlin installation under that grant. This restriction is not in the inspected new Hy-MT2 Apache licenses. [Pinned HY-MT1.5 license](https://huggingface.co/tencent/HY-MT1.5-1.8B-GGUF/blob/265b2e615a7dc9b06c435dc878829ad99a512ba2/License.txt), [pinned original Hunyuan license](https://huggingface.co/tencent/Hunyuan-MT-7B/blob/9305c78383f0bcc94358e08667ee2c76107877e3/License.txt).

Future tests require explicit user consent and an already installed local model. Use fixed English↔Chinese fixtures covering short speech, questions that must be translated rather than answered, slang, negation, names/glossary, script choice, mixed-language input, and empty/near-empty ASR. Verify template rendering, EOS and bounded completion, no extra commentary, independent requests, p95 final latency, game headroom, and simultaneous incoming/outgoing contention. One resident model can serve two independent request contexts; it saves duplicate weight memory but does not remove GPU compute contention. No cloud fallback, benchmark, load or download should happen silently.
