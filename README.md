<img src="Website/public/assets/saywhat-icon-256.png" width="72" alt="SayWhat? app icon">

# SayWhat?

**Free, open-source voice translation for VRChat on Windows.** Translate your microphone into VRChat chat messages. An optional subtitle window translates the voices you hear from VRChat or another chosen application.

**[Download the installer](https://saywhat.pages.dev/#download)** · [Website](https://saywhat.pages.dev/) · [Releases](https://github.com/z2six/saywhat/releases) · [Get help](SUPPORT.md)

**Bitdefender review pending.** The previous 0.2.11 build was flagged and is still under review. Version 0.2.13 has no vendor verdict and may also be flagged. See the [0.2.13 release notes](https://github.com/z2six/saywhat/releases/tag/v0.2.13) for details.

There are no subscriptions or paid features. Speech recognition, translation and optional familiar-voice matching run locally on your PC. Downloads and update checks need an internet connection.

## Get started

1. Install SayWhat? and follow its setup screens.
2. Choose the language you speak and the language or languages your friends should read. Prepare the recommended models, or choose different ones.
3. Enable **OSC → Enabled** in VRChat’s Action Menu.
4. On Home, choose **Start translating my voice**.

No separate LM Studio or FoxTrans installation is needed. The default speech recognizer runs directly on Windows. Optional Voxtral and Groups modes use additional local tools; setup explains their requirements before preparing them. Nothing starts listening merely because you open the app.

SayWhat? accepts **Windows 10 1809 (build 17763) or newer, and Windows 11, x64** for native own-voice translation. VRChat-only and selected-application subtitles require Windows 11; on Windows 10, you can explicitly choose **All playback**, which also hears other applications. It never switches to that wider capture scope automatically.

Optional WSL speech engines need Windows 10 2004 (build 19041+) and WSL 2 version 0.48.2 or newer. CUDA speech in WSL needs Windows 10 21H2 (build 19044+) or Windows 11, plus a compatible NVIDIA GPU and driver. These requirements do not apply to the default native speech engine.

Windows 10 compatibility is a **preview and has not been verified on a real Windows 10 PC**. The recommended translation runtime needs compatible GPU acceleration, and downloaded models require additional disk space. The installer is unsigned; hardware compatibility and fresh-PC installation are still being tested. See the [requirements](https://saywhat.pages.dev/#requirements) and [installation guide](docs/install-and-updates.md).

## Features

- **Your voice → VRChat chat:** choose up to four output languages, a finishing pause and optional early messages. Outputs share VRChat’s 144-character message limit.
- **Other voices → Subtitles:** choose the application to hear, spoken and subtitle languages, and a separate speech recognizer. A visible Chinese-only filter can skip other languages. FireRedASR2 offers Chinese/English Fast and Accurate options; Nemotron offers multilingual recognition. [Model choices](docs/models-and-runtimes.md).
- **Saved people:** add names and pictures, review familiar-voice suggestions and correct them. Only reviewed, suitable voice examples improve matching; automatic guesses do not train themselves.
- **Adjustable controls:** change models and languages, use built-in or custom profiles and choose themes. Compact subtitles show newest messages first, with an adjustable visible count and clickable name controls; message text lets clicks pass through. A separate transcript supports selection and copying. [Subtitle controls](docs/subtitles.md).

Subtitles are prepared and started separately from your microphone translator. Normal and Quick use your selected subtitle recognizer; Groups uses the optional VibeVoice path. Identifying a familiar voice does **not** isolate every person’s audio or guarantee that all overlapping words are recovered. [How people and Groups work](docs/people-and-voice-recognition.md).

Home edits stay pending until you choose the card's **Apply changes**, **Download & apply** or **Apply subtitle choices**. When stopped, **Apply & start** can save own-voice choices and start translation. Start and Stop remain direct actions.

By default, microphone capture pauses when your VRChat mic is muted. Change **Pause when my VRChat mic is muted** on Home and apply it to alter that behavior. New settings translate completed sentences in both directions; early translations are an optional choice for each. Your timing and early-message controls are also on Home.

## Privacy

Audio processing stays on your device. Saved people, pictures and voice representations are stored locally. The app does not upload them for recognition or translation. Detailed logs can contain conversation text, so review them before sharing. [Storage and privacy](docs/privacy-and-storage.md).

## Browse the source

This repository contains the application, capture host, native capture source, installer scripts and website. Start with the [codebase guide](docs/codebase-guide.md) for the main entry points and a conceptual explanation of the pipelines, then read the [architecture](docs/architecture.md) or [settings reference](docs/settings-reference.md).

| Directory | Purpose |
| --- | --- |
| [LocalTalk](LocalTalk/) | SayWhat?’s WPF application, setup, model preparation, runtime ownership and UI checks. The directory retains its earlier internal name. |
| [IncomingTranslator](IncomingTranslator/) | Subtitle capture, recognition routing, translation, people matching and shared settings. |
| [FoxTransLocalHost](FoxTransLocalHost/) | The microphone translation host and its isolated protocol tests. |
| [FoxTransSource](FoxTransSource/) | Included FoxTrans core and native capture source, with upstream attribution. |
| [Packaging](Packaging/) | Installer, update/data lifecycle, provisioning scripts and license notices. |
| [Website](Website/) | Static download site, localization and website checks. |
| [eng](eng/) | Developer build and public-source audit commands. |

The [documentation index](docs/README.md) covers recognition versus translation, model choices, queue and recovery behavior, saved people, settings, installation and updates.

## Build and contribute

For the main Windows build, install the **.NET 8 and 9 SDKs**, **CMake 3.21+** and **Visual Studio 2022 or newer with Desktop development with C++**, including a Windows SDK. Use a CMake version that supports your Visual Studio release. **Node.js 22+** is needed for the website checks.

```powershell
git clone https://github.com/z2six/saywhat.git
cd saywhat
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\Build.ps1 -RunTests
```

The script builds the native capture library from the included C source, builds the app and host, and runs isolated checks. It does not install SayWhat?, load AI models, capture audio or send live OSC messages. NuGet restore can download build dependencies. The runnable development app is written to `artifacts/build/app/SayWhat.exe`; `-Publish` produces self-contained Windows output under `artifacts/publish/`. The separate directories prevent mixed runtime files when switching build modes.

Read [development and build instructions](docs/development.md) before building installers or changing runtime behavior, and [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Bug reports and feature requests belong in [Issues](https://github.com/z2six/saywhat/issues).

## License and releases

SayWhat?’s original code and modifications are licensed under [GPL-3.0-only](LICENSE). FoxTrans and other included components retain their own applicable licenses and notices. Model weights have separate terms and are downloaded separately; they are not committed here. See [source provenance](SOURCE-PROVENANCE.md) and [third-party notices](Packaging/THIRD-PARTY-NOTICES.md).

The Code tab contains current development source. For the exact source matching an existing installer, use **SayWhat-Corresponding-Source.zip** from that same [release](https://github.com/z2six/saywhat/releases). Older GitHub-generated “Source code” archives predate this repository’s source import and may contain only the earlier documentation site.

Independent project. Not affiliated with VRChat, Inc.
