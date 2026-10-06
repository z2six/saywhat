# Help with SayWhat?

Open **Help** in SayWhat?’s sidebar for setup and troubleshooting advice, or read the [website FAQ](https://saywhat.pages.dev/#help). To report a bug, [open an issue](https://github.com/z2six/saywhat/issues).

Please include:

- Your SayWhat? version and Windows version.
- Whether the problem affects your voice, subtitles, setup, or something else.
- What you expected and what happened instead.
- The selected model and any visible error message, if relevant.

Open **Settings → Problems & logs → Open troubleshooting** (also available under **Advanced → Diagnostics**). **Recent issues** shows where a failure occurred. Use **Copy diagnostics** for a summary without conversation text, names, file paths or raw exceptions. Detailed logs and screenshots may contain private conversations, saved names or file paths. Review those before posting; never share passwords, tokens or recordings you do not have permission to share.

For a detailed explanation rather than a quick fix, use the [technical documentation](docs/README.md). In particular:

- [Latency, missed messages and reconnecting speech](docs/performance-and-troubleshooting.md)
- [What each setting changes](docs/settings-reference.md)
- [Models, acceleration and shared versus separate translators](docs/models-and-runtimes.md)
- [Saved people, corrections and overlapping voices](docs/people-and-voice-recognition.md)
- [Data locations and privacy](docs/privacy-and-storage.md)
- [Installation, updates and shutdown](docs/install-and-updates.md)

If a download is flagged by antivirus, leave protection enabled. Include the release version and detection name rather than changing security settings.

## Updates and uninstalling

SayWhat? checks for updates when it opens unless you turn this off in Settings. Choose **Update now** to download and verify the installer, update with visible progress and reopen the app with translation stopped. Settings, people and models are kept. Windows may still ask for permission; safety errors remain visible. If automatic reopening is unavailable, open SayWhat? from Start. You can also download the latest installer from the [website](https://saywhat.pages.dev/#download).

## People and subtitles

Open **People** to add a name, choose a picture file or paste an image, crop a picture, or view saved voice examples and translation counts. Counts begin with the version that introduced them; they are not reconstructed from old conversations. Voice familiarity describes saved clear examples, not a measured accuracy percentage.

Under a subtitle, choose **Select a person** to name the person who spoke. Clear examples can improve future recognition when **Voice recognition** is on. Turn it off in Settings if you only want translation or manual names. Songs, overlapping voices and unclear speech can still cause incorrect matches. The subtitle window's **?** button explains this briefly.

To uninstall, use Windows **Settings → Apps → Installed apps**, or run the installer and choose its uninstall option. Review the data choices before removing saved settings or models.
