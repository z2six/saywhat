# SayWhat? 0.1.26 preview

Automatic person matches are now suggestions, not confirmed identities.

- Subtitles show **Maybe [name]**, a picture and inline ✓ / ✕ buttons. Translations still appear immediately.
- Confirm a name to teach a usable clear example. Reject it to mark the message unknown and, when safe, remember that this voice is not that person.
- Choosing a different person can correct both sides of a mistaken match. Undo restores the previous decision and its saved evidence.
- People shows confirmed and rejected reviewed-suggestion counts. Unreviewed guesses are not counted as correct or used for learning.
- Matching now requires more consistent positive evidence and can veto close repeats of rejected examples.
- Groups prevents rejected names from returning through delayed callbacks. Confirming one message does not confirm all future messages on its voice track.
- Unconfirmed named guesses do not share person-specific translation context or increment that person's translation total.
- Updated the subtitle guide and Help in all four interface languages.

Existing people, photos, voice examples, models and settings are retained. New review evidence stays on this PC and uses the same clear-audio safety checks; no new AI model or training job is required.

Preview limitations remain: similar voices, songs and overlap can still cause mistakes. This update does not add audio separation or guarantee recognition accuracy. Installer and corresponding source are supplied together under the existing licenses.
