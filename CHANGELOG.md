# Changes

Installers, full notes and matching source archives are on
[GitHub Releases](https://github.com/z2six/saywhat/releases).

## 0.2.4

- Added full live subtitle controls directly to Home: spoken and subtitle
  languages, speech and translation models, audio source, finishing pause and
  person matching. Pending changes have one explicit apply/download action.
- Kept subtitle changes separate from microphone choices, with model/language
  compatibility checks and clear feedback during preparation.
- Published the application source and technical guides; release source archives
  now include the developer build tools, website and documentation.

## Repository source migration

- Published the application, capture host, native C dependencies, installer and
  website source in the repository under the existing GPLv3 project license.
- Added a clean source-build script, read-only CI, staged-source safety checks,
  a Visual Studio solution, conceptual code guide and contribution documentation.
- Preserved existing release history and binaries. Source publication does not
  change the bytes of historical installers or their paired source archives.

## 0.2.3

- Added FireRedASR2 Fast/Accurate and native Nemotron choices for live subtitles.
- Added explicit incoming spoken-language controls and model downloads/apply UI.
- Preserved Normal recognition choices across Groups changes and kept microphone
  settings independent.

## 0.2.2

- Added the Home option to pause translation with the VRChat microphone mute.
- Fixed initial mute-status discovery when VRChat's current log was empty.

See each release's notes for verification limits and older changes.
