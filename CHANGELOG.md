# Changelog

## Unreleased

Reliability fixes, a quieter look and a supported runtime ahead of the pilot on
Windows 11 laptops. The default hotkey (Ctrl+Alt) and the
keep-transcript-on-clipboard default are unchanged. Privacy guarantees still hold: audio
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
- **No transcript is lost silently.** In paste mode, if the clipboard can't be
  used, the text is typed instead. If it can't be inserted at all (a key held for
  several seconds, keystrokes rejected by Windows, or the PC locked before
  insertion), it is left on the clipboard, whatever the "Keep
  transcript on the clipboard" setting says. A notice then says "Copied. Press
  Ctrl+V". Further undelivered dictations are
  added to it, and later dictations don't overwrite it, until you copy
  something else.
- **Keystrokes could lag or the hotkey could stop working.** The keyboard hook
  shared the UI thread with slow work (loading the speech model, clipboard
  retries), and Windows silently removes a hook that responds too slowly. The
  hook now has its own thread. The model loads in the background. Clipboard
  calls run on their own short-lived threads. The hook is re-installed after
  unlock and resume from sleep.
- **The microphone could stay open.** When Windows swallowed the hotkey's
  release (Ctrl+Alt+Del screen, UAC prompt, administrator window), recording
  went on for up to five minutes and was then inserted. A watchdog now cancels
  such a dictation within about 0.2 s ("Cancelled"; the audio is
  discarded). Recording is also cancelled when the PC goes to sleep.
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
- **The listening indicator appeared before the microphone was open.** It now appears once
  audio is really being captured. A slow device (for example a Bluetooth
  headset) shows "Opening microphone" first, and releasing before it starts
  says "Microphone wasn't ready".
- AI cleanup sends no `Authorization` header when the API key is blank
  (keyless gateways).
- The Settings window's custom-words examples no longer name a company.

### Added

- **A quieter listening indicator.** The dark box with status text is replaced
  by a small glass pill just above the taskbar: bars that move with your voice
  while you speak, three dots while it works, a tick when the text is in. Or
  choose **Edge glow** in Settings, where the taskbar edge lights up and
  breathes with your voice and there is no shape at all. Both are
  click-through, never take focus, appear on the screen of the window you are
  dictating into, follow Windows light and dark mode, and honour high contrast
  and the "Animation effects" setting. Words appear only for a problem or a
  wait ("Didn't catch that", "Copied. Press Ctrl+V", "Opening microphone").
- Setting **"Listening indicator"** (glass pill or edge glow) with a Preview
  button; `settings.json` key `Indicator` (`"pill"` or `"glow"`).
- Windows smoke tests (`tests/WinDictate.UiSmoke`, workflow `ui-smoke`): they
  open the real indicators and Settings window on a Windows desktop, type and
  paste into a test window with the app's own code, start and stop the
  keyboard hook, and upload pictures of what was drawn.
- Setting **"Add a space after each dictation"** (on by default).
- `settings.json` keys `AddTrailingSpace`, `MicDeviceName` and `MinVoicedMs`.
  Older settings files keep working: missing keys take their defaults, and a
  microphone saved by index only is still used. Select it once in Settings to
  store its name.
- Requirements in the README and quick start: Windows 11, the Microsoft Visual
  C++ 2022 x64 Redistributable, and a CPU with AVX2.
- [docs/TESTING.md](docs/TESTING.md): manual test checklist for Windows 11.

### Changed

- **Built on .NET 10** (long-term support, until November 2028). .NET 8, which
  earlier versions bundled, went out of support on 10 November 2026. The app is
  still self-contained: nothing extra to install on the PC.
- The unit tests target plain .NET 10 and run on any OS as well as in Windows CI.
- A dictation finished just before the PC is locked or goes to sleep is no
  longer typed into whatever has focus afterwards. It is left on the clipboard
  with a notice.
