# WinDictate - Roadmap

## v0.1 (current) - proof of concept
- Global hold-to-talk hotkey (low-level keyboard hook)
- Local microphone capture (NAudio, 16 kHz mono, in memory)
- Local transcription (whisper.cpp via Whisper.net), model auto-download
- Clipboard-paste and keystroke insertion modes; transcript kept on the clipboard for re-pasting (optional clipboard restore instead)
- Quiet listening indicator (glass pill or edge glow) with a live waveform
- System-tray app, settings window, JSON config
- Optional AI cleanup via OpenAI-compatible endpoint
- Privacy defaults: no audio on disk, no history, no telemetry
- Unit tests for the hotkey state machine and watchdog, the dictation queue, insertion fallbacks and transcript clean-up; they run on any OS and in Windows CI

## v0.2 - usability
- Configurable indicator position
- Per-app insertion preference (paste vs type)
- Retry / re-insert last transcript
- Better silence/VAD trimming before transcription
- First-run wizard (mic test, model pick, hotkey choice)
- Optional GPU acceleration (CUDA / Vulkan whisper runtimes) where available

## v0.3 - enterprise controls
- Machine-level policy file (HKLM / ProgramData) overriding user settings
- "Internal gateway only" lock for cleanup
- Disable-history and disable-download enforcement
- Centrally managed custom dictionary
- Audit events (counts, durations - no content) to a log/SIEM target
- MSI packaging + Intune/SCCM guidance
- Code signing

## v0.4 - advanced cleanup & formatting
- Formatting presets (email, meeting notes, CRM note, bullet list)
- Tone presets via the cleanup model
- Streaming/partial transcription for lower perceived latency
- Punctuation-only local cleanup (no network) as a middle option

## Known limitations today
- Unsigned build (SmartScreen warning on first run)
- Endpoint-security tools may block the low-level keyboard hook
- No GPU acceleration in the default CPU runtime
- Latency scales with model size and CPU; `small.en` is the balance point
- Insertion targets whatever has focus - no per-field validation
