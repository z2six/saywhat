# SayWhat? technical documentation

[Project home](../README.md) · [Downloads](https://github.com/z2six/saywhat/releases) · [Quick help](../SUPPORT.md)

These pages explain the Windows application's behavior, configuration and limitations for technically interested users and developers. They describe the 0.1.24 preview. An older installed release may have different controls or defaults; a newer documentation page does not update your installation.

| Read this | To understand |
| --- | --- |
| [Architecture and local protocols](architecture.md) | Capture, realtime recognition, translation, queues and VRChat output |
| [Settings reference](settings-reference.md) | Defaults, supported ranges and trade-offs for individual controls |
| [Models and runtimes](models-and-runtimes.md) | Model catalog, language restrictions, GPU backends and memory sharing |
| [People and voice recognition](people-and-voice-recognition.md) | Names, photos, corrections, voice examples and overlap limitations |
| [Voice-model research](voice-model-research.md) | Additional identification, diarization and separation candidates; not a list of installed features |
| [Performance and troubleshooting](performance-and-troubleshooting.md) | End-to-end latency, dropped messages, audio routing and recovery |
| [Privacy and local storage](privacy-and-storage.md) | What is saved, where it is saved, and what network requests occur |
| [Install, update and lifecycle](install-and-updates.md) | First setup, update consent, installation scopes, shutdown and uninstall |
| [Licensing and technical contributions](licensing-and-contributing.md) | Corresponding source, component boundaries, bug reports and evidence |

## What SayWhat? does not claim

- Voice identification does **not** separate overlapping voices into independent recordings.
- Groups recognition can return multiple attributed transcripts, but does not supply isolated recordings or word-level timestamps. Saved-person matching remains a separate, optional step on usable original audio.
- Model size, available VRAM and a successful health check do **not** guarantee accurate or fast translation while gaming.
- The OSC connection indicator checks a live local VRChat service. UDP chat output has no delivery acknowledgment.
- All inference is local, but downloading tools/models and checking for updates are online operations.
- A settings change or app installation is not permission to start recording. Normal model/capture startup is an explicit **Start** action.

This repository contains documentation and downloads, not the application source tree or website deployment tooling. Matching application source remains available as a release asset because the distributed application includes GPL code. See [licensing](licensing-and-contributing.md).
