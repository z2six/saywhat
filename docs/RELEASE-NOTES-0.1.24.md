# SayWhat? 0.1.24 preview

## Profiles

- **Quick:** shorter subtitle waits and Voxtral lookahead, with automatic familiar-voice matching off. Intended as a lower-load starting point for one-to-one conversation.
- **Normal:** the existing realtime speech path with familiar-voice matching available.
- **Groups:** optional VibeVoice streaming recognition with separate attributed text routes and subtitle bubbles. Review and approve its additional downloads before use.
- Choose profiles near the top of Settings. The subtitle window switches between Normal and Groups. Built-ins keep chosen translators, languages and appearance; custom profiles save current app preferences, including model choices, without duplicating downloaded files or saved People.
- Existing preferences migrate to a saved Current settings profile instead of being replaced by a new preset.

## Subtitle scheduling

Optional voice matching now runs in the background rather than delaying translation. Later safe matches update names/photos without replacing newer text or manual corrections. Committed sentences wait for space in the bounded translation queue with backlog feedback; obsolete interim revisions can be skipped instead of discarding finalized messages.

## Groups limitations

Groups returns attributed text, **not isolated recordings or word timestamps**. Wrapper spans identify whole audio chunks. Mixed or unlabelled chunks do not teach saved-person fingerprints; usable clear evidence can still link temporary voices to saved People. A manual label may be label-only when no valid voice example is available.

The streaming chunk/lookahead buffers roughly 3.5 seconds of audio before the first decode, before compute and translation. The optional 7B model needs more storage/memory and is not a proven speed improvement. Groups may still miss or conflate overlapping speech, music or difficult audio.

## Verification

Offline application, routing, protocol and packaging checks passed without loading models or capturing audio. Live GPU latency, overlap accuracy and VRChat frame-time impact still need testing. This remains an unsigned preview. Matching GPL corresponding source and component notices accompany the installer.
