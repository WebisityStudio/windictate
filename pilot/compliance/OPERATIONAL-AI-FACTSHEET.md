# Operational AI factsheet

WinDictate Restricted Pilot {{VERSION}}.

| | |
|---|---|
| Function | Automatic speech recognition (speech-to-text) for dictation |
| AI component | OpenAI Whisper, **{{MODEL_NAME}}** (English-only "small" model, about 244 million parameters), in the whisper.cpp GGML format |
| Model file | `{{MODEL_FILE}}`, {{MODEL_SIZE}} bytes, SHA-256 `{{MODEL_SHA256}}` |
| Model source | {{MODEL_REPOSITORY}}, revision `{{MODEL_REVISION}}` (see MODEL-SOURCE-LICENCE-AND-HASH.md) |
| Inference engine | whisper.cpp 1.8.5 (through Whisper.net 1.9.1), on the CPU |
| Where inference runs | **Locally, on the user's PC.** No cloud service, no server, no network |
| Input | Audio from the user's microphone, captured only while the user holds the shortcut (up to 5 minutes), 16 kHz mono, held in memory |
| Output | Text, inserted where the user's cursor is. Optional user-supplied "custom words" are passed to the model as a spelling hint |
| Post-processing | Removal of non-speech annotations (for example `[BLANK_AUDIO]`, `(coughing)`) and whitespace tidying. No other AI step: external AI cleanup is not included in this build |

## What it does not do

- **No autonomous decisions.** It produces draft text only. It does not send, save, submit, classify, score or act on anything; the user decides what to do with the text.
- **No training or learning.** The model is fixed and read-only. Nothing the user says is used to train, tune or update any model, locally or elsewhere.
- **No external transmission.** Audio and text never leave the PC through WinDictate; the program has no network code.
- **No retention.** No audio files, no transcript history (see DATA-FLOW-AND-PRIVACY.md).
- **No profiling.** The model does not identify the speaker or infer anything about them.

## Expected errors

Speech recognition is probabilistic and will make mistakes, especially with:

- names of people, companies and places;
- numbers, amounts, dates, account and reference numbers;
- acronyms and specialist terminology;
- accents and speech patterns under-represented in the model's training data;
- background noise, cross-talk, distance from the microphone, very quiet or fast speech;
- homophones ("their", "there") and punctuation.

Like other Whisper models, it can occasionally produce text that was not said, especially from silence or noise: repeated phrases, or plausible but invented words. WinDictate reduces this by discarding clips without voiced audio and stripping non-speech annotations, but it cannot rule it out.

## Human review (mandatory)

Users must read every dictated text before saving, sending or submitting it. The first-run notice states this and must be acknowledged; the user guide repeats it. The text appears in the destination application as an ordinary draft, never sent automatically.

## Hardware and performance

| | |
|---|---|
| Processor | x64 with AVX, AVX2 and FMA (checked at start-up); uses up to 8 threads |
| Memory | About 1 GB while recognising |
| Speed | Depends on the processor; a few seconds for a typical sentence on a recent laptop. Measured in UAT (T-2) |
| Battery | Recognition uses the processor fully for a few seconds per dictation; idle use is negligible |
| GPU | Not used |

## Model governance

- **Change control:** the model is fixed for this release. Any different model needs a new release, with a new hash, SBOM and review.
- **Integrity:** checked against the pinned SHA-256 at every start; a mismatch stops the program.
- **Licence:** MIT (weights by OpenAI; GGML conversion by the whisper.cpp project).
- **Known model documentation:** the Whisper paper and model card in the OpenAI Whisper repository describe training data, evaluation and limitations.
