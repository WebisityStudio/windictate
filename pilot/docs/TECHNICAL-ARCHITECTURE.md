# Technical architecture

WinDictate Restricted Pilot {{VERSION}}, commit `{{COMMIT_SHORT}}`.

## Overview

```text
Keyboard (Ctrl+Alt held)
  -> low-level keyboard hook (observes keys only, on its own thread)
  -> hotkey state machine: start / end / cancel a session
Microphone
  -> in-memory audio buffer (16 kHz mono, 16-bit), while the shortcut is held
  -> local Whisper runtime (whisper.cpp) and the fixed, checksum-verified model
  -> transcript in application memory (audio buffer overwritten)
  -> verified insertion into the window and field where the dictation started
  -> user review in the destination application
  -> transient data disposed: clipboard put back, last dictation forgotten after 2 minutes
```

There is no server component and no network path. Everything runs in one per-user Windows process, `WinDictate.exe`, in the user's session with standard user rights (`asInvoker`).

## Components

| Component | Technology | Role |
|---|---|---|
| WinDictate | C#, .NET {{RUNTIME_VERSION}}, WPF and Windows Forms (tray icon) | The application: hotkey, recording, queue, insertion, indicator, settings |
| Speech engine | whisper.cpp 1.8.5 via Whisper.net 1.9.1 (native DLLs `whisper.dll`, `ggml-*.dll`, CPU with AVX2) | Speech-to-text on the CPU |
| Speech model | `{{MODEL_FILE}}`, Whisper {{MODEL_NAME}} (English), GGML format | Fixed model, verified by SHA-256 before loading |
| Audio capture | NAudio 2.2.1 (`NAudio.WinMM`), Windows waveIn API | Microphone input |
| .NET runtime | Self-contained, bundled | No separate .NET installation needed |
| C/C++ runtime | Microsoft Visual C++ 2015-2022 (x64), installed separately | Needed by the native speech engine |

Full inventory with versions, licences and files: `04-Compliance/COMPONENT-INVENTORY.md` and the SBOM.

## Process flow

1. **Start-up.** Single-instance check; processor (AVX, AVX2, FMA) and Visual C++ runtime check; settings load; first-run pilot notice. The keyboard hook is installed on a dedicated thread, and the tray icon appears. In the background, the model file's size and SHA-256 are checked against values compiled into the program, and the model is loaded. A failed check stops the program with an explanation.
2. **Hotkey down.** The hook sees Ctrl and Alt held (other keys are not recorded or logged). A session starts: the target window and focused control are noted, and the microphone opens on a worker thread.
3. **Recording.** Audio buffers (30 ms) are appended to a memory stream. The indicator appears once audio is really flowing. A watchdog ends a session whose key release never arrives (secure desktop, UAC prompt). Lock, sleep, a lost microphone or the 5-minute limit end it too.
4. **Hotkey up.** The microphone is stopped and closed. Recordings shorter than 0.5 s, cancelled ones and ones without voiced audio are discarded (and their buffers overwritten) without being transcribed.
5. **Transcription.** A queue processes one dictation at a time, in the order spoken, so a new recording can start while an earlier one is being recognised. The audio buffer is overwritten as soon as recognition finishes.
6. **Insertion.** Waits until no recording is in progress and no modifier key is held. Checks that keyboard focus is still where the dictation started, then pastes or types. A paste offers the text with Windows' delayed rendering, so WinDictate learns when the application reads it, and only then puts the user's clipboard back; if nothing reads it within 2 s, the text stays on the clipboard with a notice. If the clipboard holds anything other than plain text, the text is typed instead so the clipboard is not touched. If insertion is impossible, the text is left on the clipboard with a notice and removed after 2 minutes.
7. **Exit.** Tray > Exit or `WinDictate.exe --exit`: the hook is removed, the queue stops (dictations not yet inserted are discarded), undelivered text is removed from the clipboard, the model is freed, and the process ends.

## Threads

| Thread | Work |
|---|---|
| UI (WPF dispatcher) | Tray, windows, indicator |
| Keyboard hook | `WH_KEYBOARD_LL` callback only: must return within milliseconds |
| Worker threads | Microphone, transcription, insertion, model check and load |
| Short-lived clipboard threads | Each clipboard call, with a 2-second limit |

## Configuration

`%LOCALAPPDATA%\WinDictate\settings.json`, written only from the Settings window: shortcut keys, quick-tap threshold, insertion method (paste or type), indicator style, trailing space, microphone, custom words. The model, history, clipboard behaviour, network use and start-up behaviour are fixed in this build, and there are no settings for them. Unknown settings from other builds are ignored and dropped when settings are saved.

## Build and supply chain

- .NET SDK {{SDK_VERSION}} pinned (`global.json`); NuGet packages locked (`packages.lock.json`, restored in locked mode).
- Deterministic build (`ContinuousIntegrationBuild`, `Deterministic`), no debug symbols, the commit and build time recorded in the program.
- Native speech engine for x64 only (the package's x86 and ARM64 copies are excluded).
- Release assembled by `pilot/tools/ReleaseKit`, which uses only the .NET base library. Official build and checks: `.github/workflows/pilot-release.yml` on a fresh Windows virtual machine.

## Interfaces with other applications

| Interface | Direction | Detail |
|---|---|---|
| Keyboard hook | Observe | Ctrl, Alt, Shift and Win state to detect the shortcut; any other key cancels a session. AltGr's added Ctrl (scan code 0x21D) is ignored, so AltGr never counts as Ctrl+Alt. Keys are never recorded, changed or blocked. Injected keystrokes (including WinDictate's own) are ignored |
| Focus query | Read | `GetGUIThreadInfo` (window and focused control) when the shortcut goes down and before inserting |
| Clipboard | Read/write | Only to insert by paste and to put the user's content back. A paste uses delayed rendering (a short-lived message-only window owns the clipboard until the application has read the text). Writes are marked to stay out of clipboard history and cloud clipboard |
| `SendInput` | Write | Ctrl+V, or the text as Unicode keystrokes, to the focused application |
| Process name | Read | The product description of the target program, for the indicator ("Outlook"). Never the window title |
