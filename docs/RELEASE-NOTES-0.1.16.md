# SayWhat? 0.1.16 preview

**Superseded by [0.1.17](https://github.com/z2six/saywhat/releases/tag/v0.1.17).** All-users setup in this version can stop with “The data-safety helper did not return a receipt.” Use the newer installer; the failed preparation step does not apply data-reset choices.

## What’s new

- New installations use a **1.2-second finishing pause** for both your voice and subtitles. Existing custom timings are kept. Recognition and translation still take additional time.
- **Help** is now in the sidebar, with practical guidance for setup, slow translation, missing chat messages, subtitles, model choices and saved people. Its buttons open the relevant settings without starting translation.
- A short first-launch introduction explains where to prepare models and start translation. It follows your selected theme and interface language, and remembers dismissal. Existing completed installations do not show it again on update.
- Updating or uninstalling an all-users installation can now offer data choices for your verified Windows account. Settings, saved speakers/history and cached models have independent choices; leaving everything unchecked keeps it all. Other Windows users’ data is never selected.

If setup cannot safely verify the Windows account, or you use a custom data folder, it keeps personal data and explains why cleanup is unavailable. Start the installer normally from your own account rather than running it under another account’s credentials.

## Preview limitations

This installer is unsigned. Settings, interface and data-cleanup checks use isolated test files; full all-users installation and uninstall with different Windows accounts still need testing. No models are started by installing the app or reading Help.

The Windows installer, complete ZIP, matching **SayWhat-Corresponding-Source.zip**, licenses and checksums accompany this release. GitHub’s automatic “Source code” archives are not the application source.

SayWhat?’s original contributions use GPL-3.0-only; third-party components and models retain their own terms.
