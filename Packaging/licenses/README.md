# License-text provenance

Keep these texts unchanged with the distribution. They are notices for individual components, not a blanket SayWhat? license.

| File | Reviewed source |
| --- | --- |
| NAudio-MIT.txt | [NAudio 2.3.0 NuGet source commit c89fee940ee6f8d7374d18714a6b85d8b7a18ab0](https://raw.githubusercontent.com/naudio/NAudio/c89fee940ee6f8d7374d18714a6b85d8b7a18ab0/license.txt) |
| sherpa-onnx-Apache-2.0.txt | [sherpa-onnx 1.13.8 NuGet source commit dc5583f49917e4c95f6e7d862bb378e4ed5e9076](https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/dc5583f49917e4c95f6e7d862bb378e4ed5e9076/LICENSE) |
| ONNX-Runtime-ThirdPartyNotices.txt | [Official ONNX Runtime v1.28.2 notices](https://raw.githubusercontent.com/microsoft/onnxruntime/v1.28.2/ThirdPartyNotices.txt), corresponding to the bundled native DLL's reported version; not a claim all optional components are enabled |
| dotnet-System-Numerics-Tensors-MIT.txt | [System.Numerics.Tensors 9.0.0 NuGet source commit 9d5a6a9aa463d6d10b0b0ba6d5982cc82f363dc3](https://raw.githubusercontent.com/dotnet/runtime/9d5a6a9aa463d6d10b0b0ba6d5982cc82f363dc3/LICENSE.TXT) |
| libfvad-BSD-3-Clause.txt | Exact `FoxTransSource/native/vendor/libfvad/LICENSE` in the pinned local FoxTrans source |
| miniaudio-LICENSE.txt | Exact `FoxTransSource/native/vendor/miniaudio/LICENSE` in the pinned local FoxTrans source; alternative licenses preserved |
| llama.cpp-MIT.txt | [llama.cpp build b11401](https://raw.githubusercontent.com/ggml-org/llama.cpp/b11401/LICENSE) |
| voxtral.cpp-MIT.txt | Pinned voxtral.cpp source license |
| Inno-Setup.txt | Reviewed Inno Setup 6.7.3 license |
| OpenMDW-1.1.txt | [OpenMDW License Agreement 1.1](https://openmdw.ai/license/1-1/), as declared by the pinned official NVIDIA Nemotron 3.5 streaming model card; weights are downloaded directly, not bundled |

Build packaging also copies the FoxTrans GPL text, libfvad AUTHORS/PATENTS and self-contained .NET/ONNX package notices. The runtime archive's LLVM/OpenMP text stays with that runtime and is copied to exported recipient notices. The export manifest hashes every delivered notice so later changes are visible.

Upstream NAudio's license text has a 2020 copyright line. Its pinned NuGet metadata additionally identifies Mark Heath 2026 (WinMM 2023); both are retained in the release inventory rather than rewriting upstream license text. sherpa's NuGet metadata identifies Copyright 2019–2023 Xiaomi Corporation. Neither notice collection nor SPDX labels certify all transitive native dependencies or model-weight rights; see `Release-Distribution.md`.

Unicode script-data attribution for the Chinese subtitle filter: `Unicode-License-V3.txt` was retrieved from https://www.unicode.org/license.txt on 2026-10-29; SHA-256 `e7a93b009565cfce55919a381437ac4db883e9da2126fa28b91d12732bc53d96`. The source uses Unicode Character Database 17.0.0 Han ranges.
