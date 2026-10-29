# Licensing and contributing

SayWhat? is free and open source. Original project code and project-owned
modifications are **GPL-3.0-only**, copyright 2026 z2six and SayWhat? contributors.
Read the complete [LICENSE](../LICENSE) and
[source declaration](../Packaging/SOURCE-AND-LICENSES.md).

The public repository contains the app, subtitles, modified capture host,
native/vendor source, helpers, build/installer scripts and static website.
You can inspect it, build it and propose changes. Start with
[the codebase guide](codebase-guide.md), [development](development.md) and
[Contributing](../CONTRIBUTING.md).

## Upstream components and models

FoxTrans and its modifications retain their GPL attribution. Native dependencies,
NuGet packages, inference runtimes and other upstream components retain their
own applicable licenses and notices. See
[THIRD-PARTY-NOTICES.md](../Packaging/THIRD-PARTY-NOTICES.md) and
[the license directory](../Packaging/licenses/README.md).

Model weights are separate downloads. The app's license does not relicense
them: some optional models have terms beyond a permissive open-source license.
Their revisions, artifact hashes and applicable terms are part of the download
catalog. Do not commit or redistribute weights merely because the integration
code is public. See [models and runtimes](models-and-runtimes.md).

## Releases and source

Each installer keeps its exact corresponding-source archive and notices beside
the binary. The moving `main` branch is the development source, not a substitute
for the exact source of an older release. Historical release tags and downloads
were not rewritten when the code was imported. See
[source provenance](../SOURCE-PROVENANCE.md) and
[release distribution](../Packaging/Release-Distribution.md).

Contributions should retain third-party attribution, identify newly introduced
dependencies and avoid private settings, recordings, people data and credentials.
No separate contributor license agreement is requested by this repository.
