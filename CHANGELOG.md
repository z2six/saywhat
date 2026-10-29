# Changes

Installers, full notes and matching source archives are on
[GitHub Releases](https://github.com/z2six/saywhat/releases).

## Repository source migration

- Published the application, capture host, native C dependencies, installer and
  website source in the repository under the existing GPLv3 project license.
- Added a clean source-build script, read-only CI, staged-source safety checks,
  a Visual Studio solution, conceptual code guide and contribution documentation.
- Preserved existing release history and binaries. This repository change does
  not install or publish a different app version; 0.2.3 remains the current preview.

## 0.2.3

- Added FireRedASR2 Fast/Accurate and native Nemotron choices for live subtitles.
- Added explicit incoming spoken-language controls and model downloads/apply UI.
- Preserved Normal recognition choices across Groups changes and kept microphone
  settings independent.

## 0.2.2

- Added the Home option to pause translation with the VRChat microphone mute.
- Fixed initial mute-status discovery when VRChat's current log was empty.

See each release's notes for verification limits and older changes.
