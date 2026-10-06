# SayWhat? 0.1.23 preview

## Speech recovery

- Subtitle recovery can retry after a cooldown rather than exhausting a two-attempt allowance indefinitely. Retries still require recent speech and remain rate-limited; models are not repeatedly unloaded/reloaded.
- Only useful new recognition text counts as recovered. Padding tokens, duplicate transcripts and a decoder-reset acknowledgment do not clear a stalled warning.
- When newer speech is waiting, the incoming recognizer stops waiting excessively for a stalled previous sentence to finish. Audio buffering remains bounded, with skipped work reported instead of silently accumulating minutes of delay.
- Old source text is not presented as proof of current recognition progress. The subtitle window and Home report stalled recognition, reconnection, backlog and translation failures.
- Microphone overload no longer invalidates already queued sentences. Two pending turns are kept in order, each with a finite audio limit; genuinely excess new audio is skipped with a reported reason and capture resumes when capacity returns.
- Ongoing microphone speech without useful new text now has a progress deadline. A failed recognition session can recover without restarting GPU model processes; silence, mute and duplicate callbacks do not masquerade as progress.

## Problems and logs

Open **Settings → Problems & logs → Open troubleshooting**. **Recent issues** shows the time, affected direction and stage, and recorded recovery events. The existing detailed-log view remains available under **Advanced → Diagnostics**.

**Copy diagnostics** produces a support summary without speech, translations, saved names, local file paths or raw exception details. Detailed logs can contain conversation text; review them before exporting or sharing.

## Verification and limitations

This update targets recovery and reporting, not a new recognition/translation model or live separation of overlapping voices. Mixed voices, music and resource pressure can still cause recognition to fail. A recovery acknowledgment alone is not proof of useful words, and OSC output is not confirmation that VRChat displayed a chat message.

Release checks are performed before publication without loading models or recording audio. In-game long-session acceptance testing remains necessary. This is an unsigned preview; keep security protections enabled. Matching GPL corresponding source and component notices accompany the installer.
