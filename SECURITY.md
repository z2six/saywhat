# Security reports

Please report vulnerabilities using [GitHub's private vulnerability reporting](https://github.com/z2six/saywhat/security/advisories/new).
If that route is unavailable, open an issue requesting a private contact without
describing the exploit or including sensitive files.

Include the version, affected component and a minimal reproduction using
synthetic data. Do not attach raw conversation logs, recordings, saved voice
profiles, credentials or another person's information. Ordinary bug reports and
translation-quality feedback belong in [Issues](https://github.com/z2six/saywhat/issues).

SayWhat? is a preview maintained on a best-effort basis. Reports should be
checked against the current release; older builds may not receive fixes.

Speech processing runs locally. Downloading runtimes/models and checking for
updates still use the network. Download verification, process ownership and
bounded inputs are implementation safeguards, not a guarantee that a machine,
third-party dependency or AI output is safe in every situation. See
[privacy and storage](docs/privacy-and-storage.md) and
[the architecture](docs/architecture.md).
