# WinDictate

[![build](https://github.com/WebisityStudio/windictate/actions/workflows/build.yml/badge.svg)](https://github.com/WebisityStudio/windictate/actions/workflows/build.yml)

**Local, push-to-talk voice dictation for Windows.** Hold a hotkey anywhere on your
PC, speak, release - your words are typed into whatever app has focus (Outlook,
Word, Teams, a CRM field, a browser). Speech-to-text runs **entirely on the
device** using an on-board Whisper model. No cloud account, no API key, no audio
leaving the machine.

It is built as an internal-friendly alternative to the dictation features in
commercial apps: the sensitive actions (hotkey, microphone, text insertion) stay
on the endpoint, and an *optional* cleanup step can route through an
organisation-approved AI gateway if one is available.

---

## Why this exists

Plenty of people type all day - meeting notes, emails, CRM entries - when they
could dictate far faster. The built-in Windows dictation is clumsy and the good
commercial tools send your audio to a vendor cloud, which is a non-starter in a
regulated environment. WinDictate keeps the whole speech path on the laptop.

## What it does

- **Global hold-to-talk hotkey** (default **Ctrl + Alt**). Hold, speak, release.
- **Fully local transcription** via [whisper.cpp](https://github.com/ggerganov/whisper.cpp) (through the Whisper.net bindings). Works with no internet once the model is present.
- **Pastes into the focused app** using the clipboard or, optionally, by simulating keystrokes. By default the transcript **stays on the clipboard** afterwards, so you can paste it again anywhere with Ctrl+V - even if the cursor wasn't in a text box when you released the hotkey. A setting switches this off to restore your previous clipboard instead.
- **A small recording overlay** shows it is listening, a live mic level, and which window it will insert into.
- **System-tray app** - runs quietly, no main window. Right-click for settings.
- **Optional AI cleanup** (off by default): fix punctuation, capitalisation and filler words via any OpenAI-compatible endpoint - the public API *or* an internal gateway URL. If it fails or times out, the raw local transcript is used.
- **Privacy-first defaults**: no audio is ever written to disk; no transcript history unless you switch it on.

## How it works

```
Hold Ctrl+Alt
  -> overlay appears, microphone records (16 kHz mono, in memory only)
Release Ctrl+Alt
  -> local Whisper transcribes the audio on-device
  -> (optional) cleanup model fixes punctuation/formatting
  -> text is inserted into the focused app
  -> transcript stays on the clipboard for re-pasting (or the old clipboard is restored, if you prefer)
```

The keyboard, microphone and insertion all happen locally. Only the optional
cleanup step can involve a network call, and only if you enable it and provide an
endpoint.

## Install

1. Download `WinDictate-win-x64.zip` from the [Releases](../../releases) page (or the latest CI build under **Actions**).
2. Unzip anywhere (e.g. `C:\Tools\WinDictate`). It is self-contained - **no .NET install required**.
3. Run `WinDictate.exe`. A microphone icon appears in the system tray.
4. On first use it downloads the speech model once (default `small.en`, ~466 MB) into `%APPDATA%\WinDictate\models`. After that it works offline.

> **Air-gapped / no-internet machines:** copy the matching `ggml-<model>.bin`
> file (e.g. `ggml-small.en.bin`) into `%APPDATA%\WinDictate\models` manually.
> Models come from the official whisper.cpp repository on Hugging Face. See
> [docs/QUICKSTART.md](docs/QUICKSTART.md).

## Use

- **Hold Ctrl + Alt**, speak a sentence or two, then **release**. The text appears where your cursor is.
- Press any other key while holding to **cancel** the current dictation.
- Right-click the tray icon for **Settings**: hotkey, model, microphone, insertion mode, custom vocabulary, and the optional cleanup endpoint.

## Models

| Model            | Size    | Speed      | Accuracy | Notes                          |
|------------------|---------|------------|----------|--------------------------------|
| `tiny.en`        | ~75 MB  | Fastest    | Low      | Very modest hardware           |
| `base.en`        | ~142 MB | Fast       | OK       | Light laptops                  |
| `small.en`       | ~466 MB | Balanced   | Good     | **Default / recommended**      |
| `medium.en`      | ~1.5 GB | Slower     | High     | When accuracy matters          |
| `large-v3-turbo` | ~1.6 GB | Moderate   | Best     | Strong CPU/GPU                  |

`.en` models are English-only and more accurate for English. For other
languages, set the language to `auto` in settings and use a non-`.en` model.

## Privacy & enterprise notes

- **Audio** is held in memory only and discarded after transcription - never written to disk.
- **Transcript history** is off by default. When enabled it is a local TSV file in `%APPDATA%\WinDictate`; nothing leaves the machine.
- **Cleanup** is opt-in. With it off, WinDictate makes **no network calls at all** (after the one-time model download).
- **No API keys are required** for the core product. A key is only needed if you point cleanup at an endpoint that requires one.
- By default the transcript is **kept on the clipboard** after insertion as a safety net (so a dictation is never lost if the focus wasn't a text box). For stricter clipboard hygiene, untick *"Keep transcript on the clipboard afterwards"* in Settings: paste mode then saves your previous clipboard in memory and restores it within a few hundred milliseconds, and its contents are never logged. Only text is restored in that mode: if the clipboard held an image or files, it is cleared after insertion rather than left holding the transcript. Combine it with the "type" insertion mode to avoid the clipboard entirely.

See [docs/ENTERPRISE.md](docs/ENTERPRISE.md) for a deployment and governance summary
aimed at IT / security teams.

## Build from source

Requires the .NET 8 SDK.

```powershell
dotnet publish src/WinDictate/WinDictate.csproj -c Release -r win-x64 --self-contained true -o publish/WinDictate
```

Run `publish/WinDictate/WinDictate.exe`.

## Status

Early but functional proof of concept (v0.1). Built to be handed to an IT team as
a starting point, not yet a hardened production release. See
[docs/ROADMAP.md](docs/ROADMAP.md).

## Licence

MIT - see [LICENSE](LICENSE).

Speech recognition by [whisper.cpp](https://github.com/ggerganov/whisper.cpp) /
[Whisper.net](https://github.com/sandrohanea/whisper.net); audio capture by
[NAudio](https://github.com/naudio/NAudio). WinDictate is not affiliated with
OpenAI or any model provider.
