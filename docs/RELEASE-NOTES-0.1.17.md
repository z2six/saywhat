# SayWhat? 0.1.17 preview

## Installer fix

- Fixes the all-users setup error **“The data-safety helper did not return a receipt.”** Windows was protecting the installer’s temporary folder from the account checking your personal data.
- Setup now prepares only the two result files that this check needs. The installer folder and scripts remain protected.
- Settings, saved people/history and cached models are still kept unless you explicitly choose to clear them. Other Windows users’ data is not selected.

If you encountered this error in 0.1.16, cancel that installer and download 0.1.17. The failed preparation step did not apply your data-reset choices.

The 1.2-second finishing-pause defaults, Help section and first-launch introduction from 0.1.16 are included.

## Preview limitations

This installer is unsigned. Isolated checks cover the protected temporary-folder permissions and data-choice handling; full fresh-PC installation and uninstall across different Windows accounts still need testing. Installing does not start models.

The matching **SayWhat-Corresponding-Source.zip**, complete ZIP, licenses and checksums accompany this release. GitHub’s automatic “Source code” archives are not the application source.

SayWhat?’s original contributions use GPL-3.0-only; third-party components and models retain their own terms.
