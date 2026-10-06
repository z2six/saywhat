# SayWhat? 0.1.15 preview

## Translation options

- Optional own-voice output in two or three languages, disabled by default. Select a primary target and up to two additional targets; output uses separate lines in one submitted chat bubble, not keyboard drafts.
- One loaded model is reused for independent translation requests. More languages add inference work and share the 144-text-element chatbox budget. Completed languages can still be sent if an additional translation fails, with a diagnostic identifying the missing target.
- Optional **TranslateGemma 4B Q4_K_M** download with an explicit spoken-language choice and its model-specific text template in the built-in runtime. The GGUF is a bullerwins conversion, not an official Google quantization. Gemma terms apply. Overlong input is reported rather than silently clipped.
- Optional official **Liquid AI LFM2-350M-ENJP-MT Q4_K_M** download. Only English and Japanese source/target choices are allowed, including additional output languages and subtitles. LFM Open License 1.0 applies, including its commercial revenue threshold.
- Model source and terms links and required acceptance before downloading these optional models. No weights are bundled. Hy-MT2 remains the automatic-language default; existing selections are preserved.

## VRChat connection and updates

- A changing sidebar indicator checks local VRChat OSC availability and offers instructions when it cannot verify a connection or finds a different receiver port. Checks are read-only and tied to the current VRChat process. Availability is not an acknowledgment that a chat bubble was delivered or seen; no response does not conclusively mean OSC is disabled.
- One bounded update check on launch, with an option to turn it off. Manual checks remain available.
- An available update offers **Update now** or **Later**. Update now downloads the version-matched installer and verifies its exact size and SHA-256 before opening the normal update wizard.
- The updater accepts only the exact registered installation and matching per-user/all-users scope. It preserves data, rechecks registration before file changes, and leaves final license/update confirmation and any administrator consent visible. It does not install silently or force-close the app.

## Speaker recognition

Optional local AI compares voice fingerprints to associate clear speech with saved people. Users can add names and correct matches using confirmed isolated examples. Names and fingerprints stay on the device; disabling recognition releases its model while translation continues. Recognition does not separate simultaneous overlapping voices, and music or recordings can still produce wrong matches.

## Verification and limitations

Fresh dashboard and capture-host builds, offline UI/settings/localization checks, mocked specialized-model and multi-language requests, isolated capture/mute/recovery regressions, synthetic OSC checks and synthetic update-download/launcher checks passed. TranslateGemma task/role rendering was compared with its pinned template without loading weights. Tests do not use live speech, real VRChat OSC, real installers or GPU inference.

This is an unsigned preview. First-run installation, real update handoff/UAC, live new-model accuracy and speed, GPU headroom and long sessions still need acceptance testing. Neither model's size nor source compatibility is a promise of lower delay or better translation.

The installer, matching `SayWhat-Corresponding-Source.zip`, complete distribution, licenses and checksums are provided together. Original SayWhat? contributions are GPL-3.0-only; third-party and model terms remain separate. GitHub's automatically generated source archives contain this website/docs repository, not the full application source.
