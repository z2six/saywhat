# Installer languages

SayWhat? uses English (the compiler's standard `Default.isl`), Simplified Chinese, Japanese and Korean. English is the initial language-picker selection. Installer-internal `zh_CN` maps to the application language code `zh-CN`.

The three public `.isl` files retain their original translation credits and text. Japanese and Korean were copied from the verified Inno Setup 6.7.3 compiler distribution. Simplified Chinese was obtained on 2026-10-05 from the [official Inno Setup source repository](https://github.com/jrsoftware/issrc/blob/main/Files/Languages/ChineseSimplified.isl). The stored copies are the reproducible build inputs, rather than fetching a moving branch during release packaging.

SHA-256 of the stored UTF-8 files:

- `ChineseSimplified.isl`: `bf0751fa176569c6faa2f6e17ed2734617bef325d5cc06eae030fdd0258ee778`.
- `Japanese.isl`: `2be5949856da3c6c6f7dd3c9a8f33f4f8b34c3e073fd95b86ab117c95c65242c`.
- `Korean.isl`: `5683904284185ee7e5b5e9a5985ea56d3c1f0765853c5b13095d206ea9123889`.

The unmodified [Inno Setup license](https://github.com/jrsoftware/issrc/blob/main/license.txt) is included at `Packaging/licenses/Inno-Setup.txt`. Translator authorship notices remain in each file. SayWhat?'s own translated prompts are in `InstallerStrings.iss`; the translated practical notices are in `InstallerNotices`. System error details and diagnostic paths remain in their original language. The GPL license screen retains the authoritative English text.

Only the four public `InstallerDefaults` JSON files may enter the source archive through the installer-language exception. They contain a single UI-language code, not user preferences. Inno logs the selected one as `{app}\ui-language.json`, so uninstall removes that program default normally. Existing saved UI-language preferences take priority on ordinary launches. An explicitly selected optional launch passes only the UI language; it never changes translation target languages or starts translation.
