# Help with SayWhat?

Open **Help** in the app’s sidebar for setup and troubleshooting, or use the [website FAQ](https://saywhat.pages.dev/#help). Report bugs and request features through [GitHub Issues](https://github.com/z2six/saywhat/issues).

## Get a first translation working

For your own voice, choose **I speak** and the output languages on Home, then **Apply changes** or **Download & apply**. When stopped, **Apply & start** can save pending own-voice choices and begin translation. Enable **OSC → Enabled** in VRChat’s Action Menu. With **Pause when my VRChat mic is muted** enabled, unmute before speaking; changing that checkbox also needs Apply.

For other people’s voices, choose and apply **live subtitle** settings separately, then start subtitles. Home's person-recognition checkbox is included in **Apply subtitle choices**. For Chinese → English, choose Chinese with FireRedASR2 Fast or Accurate and an English subtitle target. For automatic detection or other supported languages, choose a multilingual recognizer such as Nemotron. [Model and language details](docs/models-and-runtimes.md).

You do not need subtitles running to translate your own microphone.

## Report a problem

Open **Settings → Problems & logs → Open troubleshooting**, also available under **Advanced → Diagnostics**, and choose **Save support report…**. Attach that text file to an issue with a short description of what failed. The report includes the app/Windows versions, built-in model and language choices, known error codes and recent pipeline progress. It leaves out speech, conversation text, saved names, device names, file paths and raw exception messages. **Copy diagnostics** remains available for a shorter summary.

Saving the report does not record audio, start models, send a test chat or upload anything. It checks expected app files and briefly checks only local services already running under SayWhat?'s ownership. A stopped service is not itself a failure. A report can show the last observed stage; it cannot prove that VRChat displayed a UDP chat message.

The app also keeps one local metadata-only failure snapshot, `saywhat-support-last-failure.txt`, in its [data folder](docs/privacy-and-storage.md). It is replaced after a new recent microphone failure and does not contact services. This can preserve evidence if the worker stops before you open troubleshooting.

If useful, add whether the issue follows muting, an audio-device change, music or several people talking. For speed problems, an approximate delay and whether it catches up help. You do not need to collect every detailed log before reporting a problem.

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
