# SayWhat?

Voice translation for VRChat on Windows. Translate your microphone into VRChat chat, or read other players’ voices in a separate subtitle window. Use either feature on its own or both together.

**[Download SayWhat?](https://saywhat.pages.dev/#download)** · [Website](https://saywhat.pages.dev/) · [Release notes](https://github.com/z2six/saywhat/releases) · [Help](SUPPORT.md)

Want to understand what runs on your PC or tune a specific setting? Read the [technical documentation](docs/README.md): [architecture](docs/architecture.md), [settings reference](docs/settings-reference.md), [models](docs/models-and-runtimes.md), [voice recognition](docs/people-and-voice-recognition.md), and [performance troubleshooting](docs/performance-and-troubleshooting.md).

## Get started

1. Download and run the Windows installer.
2. Open SayWhat? and follow its setup screens to choose languages, a theme and a translator. Review the downloads, then prepare translation.
3. Enable **OSC → Enabled** in VRChat’s Action Menu.
4. Start your voice translation, subtitles, or both in SayWhat?.

No separate LM Studio or FoxTrans installation is needed. Setup may ask for Windows administrator permission or a restart while preparing speech recognition.

## What it does

- Translates your voice into one language, with optional second and third languages.
- Shows translated VRChat voices in a movable subtitle window.
- Saves names and photos in **People**, with translation counts and clear voice examples. People without a photo get locally generated artwork based on their first confirmed voice example. Choose a picture file or paste an image to override it. Smaller pictures appear beside subtitles.
- Optionally recognizes familiar voices and attaches saved names to subtitles. Correct a name below a message to help matching learn from a usable clear example.
- Offers an optional **Groups** speech model that returns separate attributed text for multiple voices. It can help with overlapping conversation, but does not produce isolated recordings or guarantee that every voice is recovered.
- Pauses new microphone capture when your VRChat mic is muted.
- Offers local translation models, themes, four app interface languages and in-app updates.

Need guidance? Open **Help** in the sidebar, or **?** in the subtitle window. **Settings → Run setup again** lets you repeat setup without removing downloaded files or saved people.

## Translation profiles

Choose a profile near the top of **Settings**, then **Apply profile**:

- **Quick:** shorter subtitle waits and familiar-voice matching off. A lower-load starting point for one-to-one conversation.
- **Normal:** the existing realtime speech path with optional familiar-voice matching.
- **Groups:** optional VibeVoice streaming recognition for multiple attributed voices. Adds buffering and memory use; review and approve its extra downloads first.

These presets keep your chosen translator, languages and appearance. The subtitle window switches between Normal and Groups; Quick is selected in Settings. **Save current settings as…** creates a custom profile, including model and language choices, without copying model files or saved people. [Profile details and limits](docs/settings-reference.md#translation-profiles).

## Before installing

SayWhat? currently requires **Windows 11 x64**, supported GPU acceleration and several gigabytes of free disk space. It is not a standalone Quest or iPad app. See the [system requirements](https://saywhat.pages.dev/#requirements).

This is an **unsigned preview**. Fresh-PC installation, hardware compatibility and long-session performance are still being tested. If antivirus flags a download, keep protection enabled and [report it](SUPPORT.md).

## Privacy

Speech recognition and translation run on your PC. Saved names, photos and voice examples remain on your device. Downloads and update checks need an internet connection; they do not upload microphone recordings or your saved people. Logs may contain conversation text, so review them before sharing.

The website has no analytics or tracking cookies. Its language picker remembers only your language preference. [Privacy details](https://saywhat.pages.dev/#privacy).

## Source and licenses

This repository is the public download and support page. The application’s matching source and license notices are provided with each [release](https://github.com/z2six/saywhat/releases): use **SayWhat-Corresponding-Source.zip** from the same release as your installer, not GitHub’s automatic “Source code” archives.

SayWhat?’s original contributions use [GPL-3.0-only](LICENSE). FoxTrans and other components retain their respective notices; models have separate terms. Recipients can inspect, modify and redistribute the application under the applicable licenses.

Independent project. Not affiliated with VRChat, Inc.
