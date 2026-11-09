# Changelog

## Unreleased

Reliability fixes ahead of the pilot on Windows 11 laptops. No new features.
The default hotkey (Ctrl+Alt), the keep-transcript-on-clipboard default and the
target framework (.NET 8) are unchanged. Privacy guarantees still hold: audio
stays in memory, no transcript or clipboard content is logged, no telemetry,
no new network calls.

### Fixed

- **Hotkey stayed dead after a cancel.** After pressing Esc (or any other key)
  during a dictation, holding Ctrl+Alt again did nothing until an unrelated key
  was typed. The release check asked Windows for the state of the key being
  released, which inside the keyboard hook still reads as pressed.
- **Words in brackets were deleted.** "[GBP]" and "(not the current account)"
  disappeared from transcripts. Only Whisper's own non-speech annotations
  ([BLANK_AUDIO], [MUSIC], (coughing), *sighs* and similar) are removed now,
  without leaving a space before the punctuation.
- **Back-to-back dictations were lost.** A second dictation started while the
  first was still being transcribed recorded nothing, and a transcript was
  dropped if a modifier key was still held when it was ready. Dictations now
  queue: a new recording can start at any time, results are inserted in the
  order spoken, and nothing is inserted while you are recording or holding a
  modifier key.
- **Dictations are only inserted where they were started.** If focus has moved
  to another window, or to another field in apps where Windows exposes it, by the
  time a dictation is ready, it goes to the clipboard ("Focus moved · … in
  clipboard — press Ctrl+V") instead of being typed into the new place. This
  matters more now that dictations queue. Browsers and other single-window apps
  cannot be checked field by field.
- **No transcript is lost silently.** In paste mode, if the clipboard can't be
  used, the text is typed instead. If it can't be inserted at all (a key held for
  several seconds, keystrokes rejected by Windows, or the PC locked before
  insertion), it is left on the clipboard, whatever the "Keep
  transcript on the clipboard" setting says. The overlay then says "Not
  inserted · … in clipboard — press Ctrl+V". Further undelivered dictations are
  added to it, and later dictations are typed rather than pasted so they don't
  overwrite it, until you copy something else. Text that Windows stopped
  accepting part-way through is reported as "Partly typed". A failed restore of
  your own clipboard text is reported too.
- **The queue cannot stall.** A microphone that never opens is given up after
  10 seconds ("Microphone not responding"). A missed resume-from-sleep event no
  longer holds dictations back, because unlocking or starting a new dictation
  clears it.
- **Choosing a model that cannot be downloaded no longer breaks dictation.**
  The new model is loaded in the background; until it is available (for
  example offline), dictation keeps using the model already loaded instead of
  trying to download on every dictation.
- **Keystrokes could lag or the hotkey could stop working.** The keyboard hook
  shared the UI thread with slow work (loading the speech model, clipboard
  retries), and Windows silently removes a hook that responds too slowly. The
  hook now has its own thread. The model loads in the background. Clipboard
  calls run on their own short-lived threads. The hook is re-installed after
  unlock and resume from sleep.
- **The microphone could stay open.** When Windows swallowed the hotkey's
  release (Ctrl+Alt+Del screen, UAC prompt, administrator window), recording
  went on for up to five minutes and was then inserted. A watchdog now cancels
  such a dictation within about 0.2 s ("Cancelled · hotkey interrupted"; the
  audio is discarded). Recording is also cancelled when the PC goes to sleep.
- **Transcription could hang forever** after an attempt with no speech model
  loaded (a lock was never released).
- **Consecutive dictations ran together** ("Hello there.How are you?"). Each
  dictation is now followed by a space.
- **Quiet microphones lost whole dictations** ("No speech detected"). The check
  now looks for any voiced audio instead of the average level of the whole
  clip, so quiet input and long pauses no longer fail it. Silence is still
  rejected.
- **The chosen microphone changed after docking.** The microphone is now
  remembered by name and found again after devices are added or removed. If it
  is unplugged, the Windows default is used until it is back.
- **"Listening…" appeared before the microphone was open.** It now appears once
  audio is really being captured. A slow device (for example a Bluetooth
  headset) shows "Opening microphone…" first, and releasing before it starts
  says "Microphone not ready".
- AI cleanup sends no `Authorization` header when the API key is blank
  (keyless gateways).
- The Settings window's custom-words examples no longer name a company.

### Added

- Setting **"Add a space after each dictation"** (on by default).
- `settings.json` keys `AddTrailingSpace`, `MicDeviceName` and `MinVoicedMs`.
  Older settings files keep working: missing keys take their defaults, and a
  microphone saved by index only is still used. Select it once in Settings to
  store its name.
- Requirements in the README and quick start: Windows 11, the Microsoft Visual
  C++ 2022 x64 Redistributable, and a CPU with AVX2.
- [docs/TESTING.md](docs/TESTING.md): manual test checklist for Windows 11.

### Changed

- The unit tests target plain .NET 8 and run on any OS as well as in Windows CI.
- A dictation finished just before the PC is locked or goes to sleep is no
  longer typed into whatever has focus afterwards. It is left on the clipboard
  with a notice.
