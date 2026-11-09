# WinDictate - Notes for IT / Security

A one-page summary for the team being asked to evaluate or deploy this.

## What it is

A Windows desktop dictation utility for staff who type a lot (notes, emails, CRM
entries). The user holds a hotkey, speaks, and the spoken text is inserted into
the focused application. Speech-to-text runs on-device.

## Data flow

| Stage              | Where it runs        | Data involved                       |
|--------------------|----------------------|-------------------------------------|
| Hotkey detection   | Local (user-mode hook) | Key up/down events, not logged    |
| Microphone capture | Local                | Audio in memory only, never on disk |
| Transcription      | Local (whisper.cpp)  | Audio → text, on-device             |
| Cleanup (optional) | Network, if enabled  | Transcript text to chosen endpoint  |
| Insertion          | Local                | Text via clipboard or keystrokes    |

With cleanup **off** (the default), the application makes **no network calls**
after the one-time model download - and that download can be eliminated entirely
by pre-staging the model file (see below).

## Privacy posture (defaults)

- No audio persisted. Captured audio lives in a memory buffer and is released after transcription.
- No transcript history. Off by default; when on, it is a local file only.
- No telemetry. The app phones home to nothing.
- The event log records operational events (start/stop, errors) but **never transcript or clipboard contents** unless history is explicitly enabled.
- No API keys are required for core functionality.

## Network

- **Outbound, one-time:** model download from `huggingface.co` on first run. Can be avoided by placing `ggml-<model>.bin` in `%APPDATA%\WinDictate\models` via your software distribution system.
- **Outbound, optional/recurring:** if AI cleanup is enabled, one HTTPS POST per dictation to the configured endpoint. Point this at an approved internal gateway, or leave cleanup disabled.
- No inbound listeners. No background sync.

## Deployment

- Self-contained build - no .NET runtime install needed on endpoints.
- Ships as a folder (zip). Can be wrapped in MSI/Intune for managed rollout.
- Settings live in `%APPDATA%\WinDictate\settings.json` (per-user). A managed default
  can be pushed by deploying a pre-populated `settings.json`.
- Runs as a normal user (`asInvoker`); no elevation required.

## Things a security review will want to confirm

- The global keyboard hook (`WH_KEYBOARD_LL`) is **passive** - it observes modifier
  keys to detect the hotkey and never swallows or records keystrokes. Injected
  (synthetic) events are ignored. It runs on its own thread, separate from the UI,
  and a watchdog ends a dictation whose key release Windows never delivered
  (secure desktop, UAC prompt) so the microphone cannot stay open.
  Source: `src/WinDictate/Core/KeyboardHook.cs`, `HotkeyMonitor.cs`, `HotkeyWatchdog.cs`.
- Clipboard handling never logs clipboard contents. By default the transcript is
  left on the clipboard after insertion so the user can re-paste it; the
  "keep on clipboard" setting can be turned off, in which case the prior clipboard
  is saved in memory only and restored after the paste. A dictation that cannot be
  inserted at all is left on the clipboard regardless of that setting (and the user
  is told), so it is not lost.
  Source: `src/WinDictate/Core/TextInserter.cs`, `WindowsTextTarget.cs`, `ClipboardAccess.cs`.
- Text is only typed or pasted if keyboard focus is still in the window (and, where
  Windows exposes it, the control) in which the dictation was started; otherwise it
  is left on the clipboard. This guards against a dictation landing in a different
  application, such as a chat window. Single-window apps such as browsers cannot be
  checked field by field.
- Model files are fetched over HTTPS from the official whisper.cpp distribution.
- The optional cleanup endpoint, model, and key are operator-configured; nothing is hard-coded.

## Suggested policy options (future hardening)

These are not yet enforced by config policy but are straightforward to add:

- Lock cleanup to an internal gateway only (disallow public endpoints).
- Disable transcript history org-wide.
- Pin an approved model and pre-stage it; disable on-demand download.
- Emit audit events (counts/timings) without content to a SIEM.
- Custom dictionary of approved acronyms/product names pushed centrally.

See [ROADMAP.md](ROADMAP.md) for where these fit.
