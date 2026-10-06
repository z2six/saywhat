# Licensing and technical contributions

[Documentation index](README.md) · [Project license](../LICENSE) · [Support](../SUPPORT.md)

## Documentation repository versus corresponding source

This GitHub repository is a public-facing home for product information, documentation, issues and release downloads. It deliberately does not contain the application source tree, personal configuration, model caches, or website deployment credentials/tooling.

That layout does **not** make the currently distributed application closed-source. SayWhat? includes FoxTrans GPL code, and its original contributions are distributed as GPL-3.0-only with matching source. The user-facing installer in each release is accompanied by **`SayWhat-Corresponding-Source.zip`** and component notices. Recipients have the rights granted by the applicable licenses, including inspection/modification/redistribution.

Download the source archive from the **same tagged release** as the installer. GitHub's automatic “Source code (zip/tar.gz)” only archives this documentation repository; it is not the corresponding application source. The complete distribution ZIP also groups release material for convenient redistribution.

Keeping the source out of the default repository tree is a presentation choice, not a way to revoke GPL rights from already distributed copies. Future licensing/business plans must account for third-party component obligations separately. Model licenses/terms are distinct and are not replaced by the app license.

## Component boundaries

- **Desktop shell:** WPF dashboard, first setup/tour, themes/localization, People, diagnostics and update UI.
- **Runtime controller/provisioner:** verified downloads, managed native Windows translation runtime, WSL speech registration, explicit Start/Stop and process ownership.
- **Microphone capture host:** application-local FoxTrans Core integration, realtime finite turns, mute capture gate, speculative/final translation scheduling.
- **OSC controller/proxy:** VRChat presence/mute state and bounded submitted chat output.
- **Incoming translator:** Windows playback loopback, segmentation, recovery, optional identity matching and subtitle display.
- **Models:** separately downloaded artifacts with their own model terms; weights are not bundled into the small application source archive.

Relevant upstream projects include [FoxTrans](https://github.com/MrShitFox/FoxTrans), [voxtral.cpp](https://github.com/MrShitFox/voxtral.cpp), [llama.cpp](https://github.com/ggml-org/llama.cpp), [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx), [NAudio](https://github.com/naudio/NAudio) and [ONNX Runtime](https://github.com/microsoft/onnxruntime). The release archive's notices govern the exact shipped versions/components.

## Useful technical reports

Open [an issue](https://github.com/z2six/saywhat/issues) for reproducible defects, documentation corrections or a model integration proposal. Include release version and the smallest evidence needed; never attach a full user data folder or recordings you do not have permission to share.

For performance, distinguish recognition lag, finalization wait, translation-only time, output latency and game frame time. Report model/runtime/backend, active directions, sharing mode, relevant timing choices and a redacted stage-log excerpt. A model “felt faster” is useful feedback, but a successful load or download does not establish in-game performance.

For speaker/model proposals, specify whether the model performs **identification**, **diarization**, or **waveform separation**. Include supported sample rates/languages, causal/streaming behavior, output-slot stability, inference dependencies, artifact provenance/license and measured latency/memory alongside VRChat. Results on clean English audiobook mixtures are not proof of Mandarin VRChat performance.

Offline application checks exercise configuration, layout/localization, protocol/cancellation/queues, photo limits, correction provenance, update parsing and installer contracts without opening capture devices or loading models. They do not replace live acoustic tests, long-session recovery, genuine fresh-PC installation or measured game performance. Release notes should distinguish these kinds of evidence.

This repository has no deployed website source or private build recipe to modify. Source-level work should use the matching release archive; do not infer that a missing source tree here changes the distributed license.
