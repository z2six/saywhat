# Contributing to SayWhat?

SayWhat? is free and open source. Contributions can improve the application, model integrations, translations, documentation or tests. Start with the [codebase guide](docs/codebase-guide.md) and [development instructions](docs/development.md).

## Propose a change

Use an [issue](https://github.com/z2six/saywhat/issues) to describe a bug or discuss a larger change before implementing it. Include the expected behavior, the current behavior and why the change helps. Small fixes can go directly to a pull request.

Keep each pull request focused. Describe the behavior that changed, the relevant checks and any remaining limitations. Include UI screenshots when useful, using synthetic examples rather than private conversations or saved people.

## Build and check

From a Windows checkout with the [build prerequisites](docs/development.md):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\Build.ps1 -RunTests
```

This builds the native capture code, application and microphone host. The default checks use isolated settings, synthetic audio and fake protocols; they do not load AI models, open capture devices or send live OSC messages. Website checks run without a browser or network. Build dependency restore may use the network.

Keep live speech, GPU and VRChat testing separate and explicit. Report the tested model, relevant settings and hardware when those affect a result. Do not present a file-based smoke test as a live latency or accuracy benchmark.

Before submitting, review your staged changes and run the public-source audit:

```powershell
node eng/Check-PublicSource.mjs --self-test
git diff --cached
node eng/Check-PublicSource.mjs
```

The audit checks the Git index, so stage the files you intend to contribute first. Build outputs, downloaded models, recordings, personal settings, people data and credentials must stay out of Git. The audit is an additional check, not a substitute for reviewing what you publish.

## Runtime and UI changes

- Keep microphone translation and incoming subtitles independent. A subtitle-only change must not restart the microphone translator or replace its settings.
- Preserve process ownership and shutdown behavior. Stop only the helpers owned by the current runtime; release models and memory when their feature stops.
- Keep audio and text queues bounded, and provide useful feedback when recognition, translation or output falls behind. Do not silently discard a finished message or leave the UI appearing healthy after a worker fails.
- Do not load models, start capture or download large files simply because a settings page opens. Preparation and inference must remain explicit actions.
- Maintain compatibility with existing saved settings and internal names. Renaming `LocalTalk`, namespaces or data paths is not required to improve the SayWhat? branding and can break updates.
- Use the existing theme and localization systems. App copy is provided in English, Simplified Chinese, Japanese and Korean; website copy has its own catalogs. Avoid hard-coded colors or developer-oriented messages in user flows.

Add regression coverage for the behavior being changed, especially language/model compatibility, saved settings, queue recovery, cancellation and shutdown. Prefer existing fake protocol and isolated settings fixtures over tests that need a microphone, VRChat or a downloaded model.

## Model integrations and third-party code

Record the upstream source, immutable revision, download size, SHA-256 and applicable license for a new model or runtime. State its actual supported languages and processing limits. Recognition support is different from translation support; do not enable an unsupported language pair because another stage supports it.

Keep weights and downloaded runtime archives outside the repository. Preserve upstream licenses and attribution when changing included code. Do not assume a model’s license is the same as the inference library’s license. Contributions to SayWhat?’s own code should use the existing GPL-3.0-only license; see [licensing](docs/licensing-and-contributing.md).

## Privacy and security

Use **Copy diagnostics** in the app when reporting a problem. Raw logs, screenshots, recordings and people profiles may contain private information; review them before sharing and do not publish other people’s voices without permission.

Read [SECURITY.md](SECURITY.md) before reporting a vulnerability. Do not put credentials or private vulnerability details in a public issue.
