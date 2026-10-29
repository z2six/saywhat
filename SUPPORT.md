# Help with SayWhat?

Open **Help** in the app’s sidebar for setup and troubleshooting, or use the [website FAQ](https://saywhat.pages.dev/#help). Report bugs and request features through [GitHub Issues](https://github.com/z2six/saywhat/issues).

## Get a first translation working

For your own voice, choose **I speak** and the output language or languages on Home, prepare the selected models, then choose **Start translating my voice**. Enable **OSC → Enabled** in VRChat’s Action Menu. With **Pause when my VRChat mic is muted** enabled, unmute in VRChat before speaking.

For other people’s voices, prepare and start **live subtitles** separately. **Speech model & language** on Home lets you select what they speak and which model recognizes it. For Chinese → English, choose Chinese with FireRedASR2 Fast or Accurate, and keep the subtitle language set to English. For automatic detection or other supported languages, choose a multilingual recognizer such as Nemotron. [Model and language details](docs/models-and-runtimes.md).

You do not need subtitles running to translate your own microphone.

## Report a problem

Include:

- Your SayWhat? version and Windows version.
- Which feature is affected: microphone translation, subtitles, setup, updates or another part of the app.
- What you expected, what happened and steps that reproduce the issue.
- The chosen speech recognizer, translator, spoken/output languages and profile, when relevant.
- The visible error or diagnostics summary. For speed problems, describe approximately how long it takes and whether it eventually catches up.

Open **Settings → Problems & logs → Open troubleshooting**, also available under **Advanced → Diagnostics**. **Recent issues** shows the stage where a failure occurred. Use **Copy diagnostics** first; it leaves out conversation text, names, file paths and raw exceptions.

Detailed logs and screenshots may contain private conversations, saved names, photos or paths. Review them before posting. Never share passwords, API tokens, people-profile files or recordings you do not have permission to share.

For an antivirus detection, leave protection enabled and report the release version, detection name and downloaded file’s checksum if available. Do not disable protection to make an installer run.

## Common questions

- [Slow translations, missed messages and reconnecting speech](docs/performance-and-troubleshooting.md)
- [What each setting changes](docs/settings-reference.md)
- [Models, acceleration and shared versus separate translators](docs/models-and-runtimes.md)
- [Saved people, corrections and overlapping voices](docs/people-and-voice-recognition.md)
- [Data locations and privacy](docs/privacy-and-storage.md)
- [Installation, updates, shutdown and uninstalling](docs/install-and-updates.md)

The app checks for updates on launch unless you turn that off in Settings. An accepted update downloads and verifies the installer, preserves your settings and reopens with translation stopped. Windows may still request permission. If an update cannot finish, include its visible error when reporting it. You can also download the installer from the [website](https://saywhat.pages.dev/#download).

## Questions about the code

The [documentation index](docs/README.md), [codebase guide](docs/codebase-guide.md) and [architecture](docs/architecture.md) explain the components and their design. For changes to the code or documentation, read [CONTRIBUTING.md](CONTRIBUTING.md). Security concerns should follow [SECURITY.md](SECURITY.md).
