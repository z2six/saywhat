# SayWhat?

Local voice translation for VRChat on Windows.

[Website & download](https://saywhat.pages.dev/) · [Preview releases](https://github.com/z2six/saywhat/releases) · [Report a problem](https://github.com/z2six/saywhat/issues)

SayWhat? translates your microphone into VRChat chat and other players' voices into a separate subtitle window. Start either direction independently and choose their translation languages separately.

## Install

1. Download `SayWhat-0.1.14-Setup.exe` from the [0.1.14 preview release](https://github.com/z2six/saywhat/releases/tag/v0.1.14).
2. Install for yourself or all users, then open SayWhat? from Start.
3. Follow Setup to choose languages and prepare the local tools and models. Downloads and Windows permissions require your approval; WSL setup may require a restart.
4. Enable OSC in VRChat, then choose Start in SayWhat?. Setup alone does not start listening.

No separate LM Studio or FoxTrans installation is needed for automatic setup. Model weights are downloaded during setup rather than shipped in the installer.

This is an **unsigned preview**, not a fully fresh-PC-certified release. Review the [requirements](https://saywhat.pages.dev/#requirements) and [release notes](docs/RELEASE-NOTES-0.1.14.md) first. Do not disable antivirus protections if a download is flagged; report the detection instead.

## Features

- Realtime Voxtral recognition and local Hy-MT2 translation, with smaller and larger translator downloads in the app.
- Optional cumulative submitted chat messages while you speak, followed by the finished sentence. They do not open VRChat's keyboard.
- Microphone capture pauses when your VRChat mic is muted; an already-captured sentence can finish.
- Subtitles capture VRChat rather than all Windows audio, with a toggle for always on top.
- Optional locally saved people and voice recognition. Recognizing a voice is not the same as separating simultaneous overlapping speakers.
- English, Simplified Chinese, Japanese and Korean interface languages, themes, diagnostics and manual update checks.

## Requirements and privacy

Windows 11 x64, VRChat, free space for several gigabytes of tools/models and supported GPU acceleration. Speech setup uses an app-owned WSL2 Ubuntu environment. Virtualization, administrator permission and a restart may be needed. The app checks acceleration support; availability and performance vary by hardware and driver.

Speech recognition and translation run locally. Saved people and voice fingerprints stay on your PC. Setup downloads tools/models from third-party providers; manual update checks request public release metadata. Logs may contain conversation text—review them before sharing an issue report. This website contains no analytics scripts or tracking cookies; hosting/download providers still receive normal connection metadata.

## Source and licenses

This repository contains the public website and documentation, not the entire development history. Each release includes its **matching corresponding-source ZIP**, build scripts, license texts, notices and checksums beside the installer. Download the source from the same versioned release as your installer.

SayWhat?'s original contributions are provided under **GPL-3.0-only**. FoxTrans GPLv3 code and third-party components retain their copyright and license notices. Recipients may inspect, modify and redistribute under the applicable licenses; the source ZIP is not confidential. Model weights have separate licenses. See [LICENSE](LICENSE), the release's `SOURCE-AND-LICENSES.md` and third-party notices for details.

## Website maintenance

The dependency-free Cloudflare Pages site lives in [Website](Website/README.md). Only `Website/public/` is deployed; installers and source archives are GitHub release assets. Downloads stay disabled unless the public manifest names verified version-matched assets with checksums.
