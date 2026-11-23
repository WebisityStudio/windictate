# Model source, licence and hash

The one speech recognition model in WinDictate Restricted Pilot {{VERSION}}.

| | |
|---|---|
| Model | OpenAI Whisper **{{MODEL_NAME}}** (small, English-only) |
| Format | GGML (the whisper.cpp file format) |
| File name | `{{MODEL_FILE}}` |
| Size | {{MODEL_SIZE}} bytes |
| SHA-256 | `{{MODEL_SHA256}}` |
| Location in the package | `01-Runtime/win-x64/models/{{MODEL_FILE}}` |
| Location when installed | `<install folder>\models\{{MODEL_FILE}}` |
| Upstream repository | {{MODEL_REPOSITORY}} |
| Pinned revision | `{{MODEL_REVISION}}` (a fixed commit of the repository, not "latest") |
| Download URL (pinned) | {{MODEL_URL}} |
| Licence | MIT. Model weights: OpenAI (MIT, `LICENCES/Model-LICENCE.txt` and `LICENCES/OpenAI-Whisper-LICENCE.txt`). GGML conversion: whisper.cpp project (MIT, `LICENCES/WhisperCpp-LICENCE.txt`). The upstream repository is tagged `license: mit` |
| Why this model | Balanced accuracy and speed on laptop processors; English-only models are more accurate for English than multilingual ones of the same size. Smaller models (base.en) are faster but less accurate; larger ones are too slow on typical laptops without a GPU |

## How the hash is established and enforced

1. The SHA-256 above is the Git LFS object ID that the upstream repository publishes for this file at the pinned revision (Git LFS identifies files by their SHA-256).
2. It is recorded in the source in two places: `pilot/model.json`, which the release tool uses, and `src/WinDictate/Core/PilotModel.cs`, compiled into the program. A unit test fails the build if they differ.
3. The release tool downloads the file from the pinned URL (or takes a local copy) and refuses to package it unless its size and SHA-256 match.
4. The packaged file is listed in `05-Release/SHA256SUMS.txt`.
5. WinDictate checks the size and SHA-256 again **every time it starts**, before loading the model. A missing or different file is not loaded: the program explains the problem and exits.

## Verify it yourself

Windows PowerShell:

```powershell
(Get-FileHash -Algorithm SHA256 '.\01-Runtime\win-x64\models\{{MODEL_FILE}}').Hash
```

macOS or Linux: `shasum -a 256 01-Runtime/win-x64/models/{{MODEL_FILE}}`

Expected (case does not matter): `{{MODEL_SHA256}}`
