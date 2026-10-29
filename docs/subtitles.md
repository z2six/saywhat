# Live subtitles

[Documentation index](README.md) · [People](people-and-voice-recognition.md) · [Troubleshooting](performance-and-troubleshooting.md)

Subtitles translate the voices you hear. They are optional and start separately
from your microphone-to-VRChat translation. On **Home**, choose what to listen to,
the spoken language, subtitle language and speech model, then start subtitles.

By default, subtitles wait for a completed sentence or bounded section. **Show
early translations** is optional: it can display a translation before the sentence
is finished and revise it as more words arrive. This is separate from early
VRChat messages for your own microphone. Listening continues in either mode.

## Compact overlay

New messages appear **at the top**. The overlay shows **three messages** by default;
choose **Appearance → Visible messages** to show between one and ten, such as five.
Each bubble can show the original recognized words above their translation.
Clicks on message text pass through to the window underneath. The name controls
in each message's header remain clickable. Use the separate **toolbar** to move
the overlay or change its options.

Open **Appearance** on the toolbar to adjust:

- **Font size:** make subtitles easier to read.
- **Visible messages:** show more or fewer recent messages without changing the
  transcript's retained history.
- **Bubble width:** choose how much horizontal space each message uses.
- **Background:** change the bubble background's opacity. The default is 65%;
  text remains opaque.
- **Original text:** show or hide the words heard by speech recognition.

These changes save immediately without restarting recognition or translation.
**Always on top** determines whether the overlay stays in front of other windows;
the dashboard and transcript are ordinary windows.

## Read or copy earlier messages

Choose **Logs** on the toolbar to open **Transcript**. It retains up to **200 recent
messages**, newest first, for the current subtitle session, rather than putting the whole
conversation on top of the game. This is a message transcript, separate from the
diagnostic logs in Troubleshooting.

Select visible original or translated text to copy it, use a message's **Copy** action, or
choose **Copy all**. **Follow latest** keeps the transcript at the top with the newest messages;
turn it off when reading earlier ones. The transcript is not forced always on top.
Review copied text before sharing: it can contain private conversation and names.

The **original text** is what the speech model recognized, not a verified record
of what someone said. The **translation** is generated from those words. When a
subtitle seems wrong, compare the two: wrong original words point to recognition,
while useful original words with a poor translation point to the translator.
Neither stage is guaranteed accurate.

## Names and recognition

Use the clickable name header on an overlay message, or open **Transcript**, for
name corrections and ✓ / ✕ review controls. Choose **Select a person** to search or pick a saved name. A
**Maybe [name]** label is a suggestion: ✓ confirms it and ✕ marks it unknown.
Guesses do not teach themselves.

With optional voice recognition enabled, a reviewed clear example can help future
matches. Mixed, short, unclear or expired audio may change only the message's
name. This updates saved examples rather than retraining the AI model. Names and
voice examples stay on your device. See [People](people-and-voice-recognition.md)
for eligibility, undo and storage limits.

## Profiles and settings

Open **Appearance** or Transcript to switch **Normal / Groups**. Transcript also
has **Settings**; use **?** to open the guide there. **Normal** uses the selected speech model. **Groups** uses
an additional local model to recognize separate voices; it adds delay and can
still miss overlapping speech. A saved name does not isolate that person's audio.

**Quick** is available in **Settings → Translation profiles** for a lower-load
one-to-one starting point, with automatic voice matching off. Custom profiles
save your chosen settings. Switching the incoming speech path can briefly pause
subtitles while the selected tools prepare; it keeps saved People and recent
messages. Your microphone translation has its own controls.
