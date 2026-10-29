# Changes

Installers, full notes and matching source archives are on
[GitHub Releases](https://github.com/z2six/saywhat/releases).

## 0.2.7

- Put the newest subtitles and transcript messages at the top.
- Added a Visible messages control (1–10, default 3) alongside the overlay’s
  font and transparency controls, saved without restarting translation.
- Restored Select a person and suggestion review controls directly on each
  overlay message. Only those small controls accept clicks; text stays click-through.
- Kept the selected message still while its person picker is open and shared
  correction ownership with Logs to prevent duplicate submissions.

## 0.2.6

- Replaced the large subtitle window with a compact toolbar and three recent,
  semi-transparent bubbles that let clicks pass through to other applications.
- Added immediate font, bubble-width, background and original-text controls.
  Logs opens a regular copyable Transcript window with up to 200 messages and
  the existing person-correction/review controls.
- Changed new/default voice and subtitle delivery to finished sections. Early
  translations are independent opt-ins; existing saved opt-ins are preserved.
- Filtered silence/control markers such as SIL before translation, and kept
  verified decoder progress separate from real words so valid silence updates
  can finish the preceding phrase without becoming subtitle text.

## 0.2.5

- Removed the Windows 11-only installer gate. Windows 10 21H2 and newer x64
  can install this compatibility preview for microphone-to-VRChat translation.
- Added early, visible checks for app-specific subtitle capture. On Windows 10,
  All playback is an explicit alternative; it is never enabled as a fallback.
- Updated requirements and added simulated OS-boundary coverage. A real Windows
  10 install and live model run remain acceptance checks.

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
