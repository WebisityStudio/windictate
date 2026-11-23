# WinDictate Restricted Pilot {{VERSION}}

## What it is

Local push-to-talk dictation for Windows 11. The user holds a keyboard shortcut (**Ctrl+Alt**), speaks, and lets go. The speech is recognised on the PC by a fixed speech recognition model, and the text is inserted where the cursor is, in any application. The user then reviews the text there before saving, sending or submitting it.

This restricted build is for a controlled workplace pilot: post-event dictation and drafting, such as notes after a meeting, e-mail drafts and record updates.

## Requirements

| | |
|---|---|
| Operating system | Windows 11, 64-bit (x64) |
| Processor | AVX, AVX2 and FMA instructions (Intel processors since 2013 and AMD since 2015, approximately). WinDictate checks this at start-up and explains if the PC is not supported |
| Runtime prerequisite | Microsoft Visual C++ 2015-2022 Redistributable (x64), **not included**; usually already present, otherwise deployed by IT. WinDictate checks for it at start-up and names any missing file |
| .NET | Not needed: the .NET {{RUNTIME_VERSION}} runtime is included in the program folder |
| Memory | About 1 GB free while a dictation is being recognised |
| Disk | About {{RUNTIME_SIZE_MB}} MB for the program folder (including the {{MODEL_SIZE_MB}} MB model) |
| Microphone | Any Windows input device; Windows' microphone privacy setting must allow desktop apps |
| Administrator rights | Not needed to run, or to install for the current user |

## How it stays local

- **Offline operation.** Recognition runs on the PC with the bundled model. The program contains no network code: no update check, no telemetry, no model download, no cleanup service. It works with networking switched off.
- **No external cleanup.** The optional AI clean-up feature of the open-source version is removed from this build: there is no code, no setting and no endpoint.
- **No transcript history.** Nothing dictated is saved. The history option of the open-source version is removed.
- **No audio files.** Audio is held in memory only while the shortcut is held, is overwritten once the dictation is finished, and is never written to disk.
- **Clipboard.** When text is pasted, the user's clipboard is put back straight afterwards. Dictated text is marked so that Windows clipboard history and cloud clipboard leave it out.

## The user stays in control

- Recording happens only while the shortcut is held, up to 5 minutes per dictation. Pressing **Esc** (or any other key) before letting go cancels it, and the audio is discarded.
- A small indicator above the taskbar shows when the microphone is recording, and names the application the text will go into.
- Text is only inserted into the window (and, where Windows exposes it, the field) where the dictation started. If the focus has moved, the text goes to the clipboard instead, with a notice.
- **Review everything.** Speech recognition can mishear names, numbers, amounts, acronyms and specialist terms. The first-run notice says so, and the user must acknowledge it before dictating.

## Permitted and prohibited pilot uses

| Permitted | Not permitted |
|---|---|
| Dictating notes and summaries **after** a meeting or call | Recording live conversations, calls or meetings |
| Drafting e-mails, messages and documents | Dictating another person's words without their knowledge |
| Updating records and fields, then reviewing the text | Submitting or sending dictated text without reading it |
| Short dictations in a private or quiet space | Use on Citrix or remote desktop sessions (not supported in this pilot) |

## Where to find more

`USER-GUIDE.md` for everyday use, `INSTALLATION-INSTRUCTIONS.md` and `UNINSTALL-INSTRUCTIONS.md` for IT, `DATA-FLOW-AND-PRIVACY.md` and `SECURITY-CONTROLS.md` for reviewers, `UAT-TEST-PLAN.md` for testers, `KNOWN-LIMITATIONS.md` before deciding scope.
