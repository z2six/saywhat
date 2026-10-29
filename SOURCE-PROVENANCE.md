# Source provenance

The initial public source import is based on the exact corresponding-source
archive distributed with [SayWhat? 0.2.3](https://github.com/z2six/saywhat/releases/tag/v0.2.3).
Its SHA-256 is:

```text
7154b3b4f462a9889d55e8269d0509b94c1fc8efab8e36a3be6a7022d69fa8f3
```

The import preserves application behavior, original copyright notices, the
FoxTrans-derived code and its native dependencies. The repository adds a clean
developer build, automated checks and documentation. The generated convenience
`FoxTrans.Native.dll` is built from the included C sources instead of being
tracked in Git. Machine-specific publishing notes and old verification outputs
are omitted. The static website source is included too.

Historical GitHub tags originally contained documentation and downloads; they
have not been moved or rewritten. For those binaries, use their unchanged
matching source ZIP. The `main` branch is the source home for ongoing work.

Project-owned code is GPL-3.0-only. Upstream code, libraries, artwork notices and
model weights retain their applicable licenses. See [LICENSE](LICENSE),
[the source declaration](Packaging/SOURCE-AND-LICENSES.md) and
[third-party notices](Packaging/THIRD-PARTY-NOTICES.md).

No model weights, recordings, personal settings, saved people, credentials or
generated application logs belong in this repository.
