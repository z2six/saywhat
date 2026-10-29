# SayWhat? documentation

SayWhat? is free, open-source Windows software for local speech translation.
The source is in this repository; installers and their matching source archives
are on [Releases](https://github.com/z2six/saywhat/releases).

## Understand or change the code

Start with the **[codebase guide](codebase-guide.md)**. It follows microphone and
incoming audio through recognition, translation, person matching and display,
then links the files responsible for each step.

| Guide | What it answers |
|---|---|
| [Codebase guide](codebase-guide.md) | Which component does what, in what order, and why? |
| [Development](development.md) | How do I build a clean checkout and run isolated checks? |
| [Architecture](architecture.md) | What owns audio, models, processes, queues and local endpoints? |
| [Settings reference](settings-reference.md) | Which saved value controls each behavior? |
| [Models and runtimes](models-and-runtimes.md) | Which recognizers/translators are supported and what are their limits? |
| [People and voice recognition](people-and-voice-recognition.md) | How do examples, suggestions, corrections and local voice matching work? |
| [Installer build](../Packaging/README.md) | How are self-contained files, native tools, notices and source packaged? |
| [Release distribution](../Packaging/Release-Distribution.md) | How are binaries, matching source, checksums and the update feed kept together? |
| [Website](../Website/README.md) | How are the static site, localization and download manifest maintained? |

See [Contributing](../CONTRIBUTING.md) for changes and pull requests, and
[source provenance](../SOURCE-PROVENANCE.md) for the initial import.

## Use or troubleshoot the app

| Guide | What it helps with |
|---|---|
| [Support](../SUPPORT.md) | Find the right next step or report a problem without sharing private conversations. |
| [Install and updates](install-and-updates.md) | Installation scope, updates, removal and retained data. |
| [Performance and troubleshooting](performance-and-troubleshooting.md) | Delayed messages, missing subtitles, OSC and model tradeoffs. |
| [Privacy and storage](privacy-and-storage.md) | What stays local, where files live and what diagnostic logs can contain. |
| [Licensing](licensing-and-contributing.md) | Project source, upstream attribution and separate model terms. |

The [model research notes](voice-model-research.md) distinguish potential future
models from implemented choices. Historical release notes remain for reference;
current downloads and release status are on GitHub Releases.
