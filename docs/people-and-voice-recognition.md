# People and voice recognition

[Documentation index](README.md) · [Voice-model research](voice-model-research.md) · [Privacy](privacy-and-storage.md)

## People records

A saved person has a stable local ID, display name, optional picture, aggregate translation count, last-used timestamps and confirmed voice examples. A name is not linked to a VRChat account or uploaded to a directory. The People list supports adding, searching, renaming, deleting and changing/removing pictures without opening audio or loading translation models.

Translation totals count successfully published committed subtitle chunks attributed to that person, not interim revisions. Repeated deliveries of the same chunk do not increment the count. A correction of recent attribution can move that count to another person; undo can reverse the correction. These totals began when the feature was introduced and do not reconstruct older sessions. A counter-save failure must not prevent the subtitle from displaying.

The store allows up to 64 saved people, with case-insensitively unique names of 1–64 characters and no control characters. It allows 64 confirmed examples per person per recognition model and 4,096 examples overall. Recent translation identities used for counting/correction are also bounded to 4,096; they contain opaque IDs and timestamps, not conversation text. The separate **Unnamed voice profile limit** only controls anonymous automatic profiles. It does not restrict the searchable People list to two names.

### Pictures

Choose a local PNG/JPEG file, or explicitly paste a copied bitmap with **Ctrl+V** in the picture editor. The app does not poll the clipboard in the background or interpret pasted text/URLs as remote images. Move/zoom the square crop, then save; canceling does not change the existing photo.

Picture handling rejects oversized input before storing a copy: file inputs are bounded to 10 MiB, each dimension to 8,192 pixels, and total pixels to 16 × 1,024 × 1,024. The editing preview is reduced to at most 2,048 pixels along its longest side. The selected crop is rendered into a **512 × 512** local PNG, with a **1 MiB stored-size ceiling**, fixed DPI and no source metadata. Transparency is flattened to white and PNG compression is lossless. The unbounded original image is not kept.

People uses the higher-resolution picture in a larger display; subtitle avatars use a real **64-pixel downsampled image**, shown at a smaller UI size. Missing/corrupt pictures fall back to initials rather than interrupting translation. Managed pictures use generated filenames under `people-photos`; records do not retain the original file path. Replacing/deleting a picture cleans up only app-managed files and does not delete the user's original.

## What “learning” means here

The recognition network is **not retrained** when a person is selected. SayWhat? extracts a compact normalized embedding—a numerical voice fingerprint—and stores a human-confirmed reference. Later fingerprints are compared to these references using cosine similarity. This can improve matching to a familiar voice without modifying the model's weights or running a training job.

Click **Select a person** under a subtitle. The small picker offers recent names and a search across saved people. Selecting a name labels the message immediately. On the first learning use, the app explains that an example should contain one clear, isolated voice. When recognition is enabled and usable evidence is still available, the label also adds or corrects that reference.

A message can be labeled without teaching the model. Learning is withheld when recognition was off, the evidence has expired, no valid embedding exists, audio is mixed/too short/clipped, or the bounded reference collection is full. Recent in-memory evidence is retained for at most five minutes, with at most 200 observations. The raw audio is not retained as a training dataset.

Recognition now runs in the background so a subtitle need not wait for its name. If a usable fingerprint is not yet available when you select a person, that correction can be label-only; the app reports whether a voice example was saved. Choosing a name is not a guarantee that every message becomes a training example.

Repeated revisions of the same voiced interval represent **one** example, not independent evidence. Re-labeling moves/replaces that contribution; it does not add duplicate reinforcement. Undo removes or restores the prior contribution. Special choices such as music/recording or unidentified voice do not teach a named person.

Automatic guesses do not train the manually confirmed references. Anonymous guessed centroids are deliberately frozen after enrollment, preventing a repeated mistaken singer match from gradually contaminating a person's stored voice.

## Recognition models and matching

The current built-in choices are **CAM++ Mandarin (fast)** and **ERes2Net Mandarin (larger)** from sherpa-onnx's speaker-recognition model releases. Compatible imported ONNX embedding models can be selected, but an arbitrary speech/transcription ONNX model is not a compatible substitute. The extractor currently runs on CPU with two threads; turning it off releases that extractor, not the translation models.

Model files are keyed by their SHA-256 identity for saved references. Examples from one model are not directly comparable to embeddings from a different model, even if both use the word “Mandarin.” Changing models retains names, photos and old examples, but the new model needs its own clear examples.

Matching is deliberately conservative:

- Samples need sufficient usable voiced audio; named matching needs the configured similarity threshold and separation from the runner-up.
- Short samples require a stricter threshold.
- Mixed/uncertain audio is withheld from confident identity assignment.
- The first anonymous voice can enroll from one sufficiently long clean observation; subsequent identities require repeated independent evidence.
- Near misses are left unidentified rather than automatically spawning another person.

The threshold is a similarity score, **not a probability of being correct**. The People familiarity labels report saved clear examples/readiness, not a measured percentage accuracy. A model can still confuse songs, similar voices, changed microphones, short utterances or overlapping speech.

## Identification, diarization and separation

These names describe different tasks:

| Task | Answers | Does it produce separate audio? |
| --- | --- | --- |
| Voice identification | “Does this clear sample sound like a saved person?” | No |
| Diarization | “Who spoke when; were several voices active?” | Usually labels/timestamps, not separate waveforms |
| Speech separation/extraction | “Estimate an individual voice from this mixture.” | Yes, subject to artifacts/errors |

**Quick/Normal** transcribe mixed VRChat playback through Voxtral. They do not independently recover each simultaneously overlapping voice. Saved names or a larger fingerprint model do not change that. Left/right position is not treated as a person's permanent identity.

Optional **Groups** uses VibeVoice streaming recognition to return text with temporary voice labels, then translates each route into its own bubbles. It can help recover more than one person's words from a chunk, but does not output isolated recordings. Its labels are session-scoped: “voice 0” is not permanently the same person after a recognition reset.

SayWhat? keeps the original mixed audio, but VibeVoice replies have **whole-chunk spans**, not individual-person or word timestamps. A chunk with multiple voices cannot safely teach a named person's fingerprint. Only a chunk with one labelled voice and usable clear-audio evidence is eligible for optional fingerprint matching. Unlabelled replies are translated without binding all unknown voices to a person. Clear matching or a manual assignment can connect a temporary voice to an existing saved person; manual choices take precedence and still label that session when automatic matching is off. Missed overlap can evade audio checks, so the app cannot promise every eligible example is truly isolated.

An experimental **MossFormer2 INT8 two-voice separator** is available for explicit local-WAV evaluation under advanced voice options. It produces two anonymous waveform estimates, not stable named people. Downloading it does **not** enable live separation. Evaluation uses a local 16 kHz WAV sample, CPU inference, warm-up and measured passes; exporting results saves audio only after a separate explicit action.

A future waveform separator would still need tests for real VRChat Mandarin/music/reverb, output-slot identity changes, extra recognition streams, recovery, latency and game performance—not merely whether its model loads. Groups is attributed transcription, not that separator. [Voice-model research](voice-model-research.md) distinguishes implemented features from remaining candidates.
