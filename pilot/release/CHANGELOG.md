# Changelog: WinDictate Restricted Pilot

## {{VERSION}} (commit `{{COMMIT_SHORT}}`, built {{BUILD_DATE}})

First restricted pilot build, based on the open-source WinDictate after its reliability work (see "Included from WinDictate" below).

### Removed for the pilot

- **External AI cleanup**: the optional OpenAI-compatible clean-up service, its endpoint, model and API-key settings, and all network code.
- **Model download**: the program never downloads anything. One fixed model ships in the package.
- **Transcript history**: the option and the history file.
- **Model and language choice**, **keep dictations on the clipboard**, and **start with Windows**.

### Added for the pilot

- **Fixed, verified speech model**: `{{MODEL_FILE}}` in the program's `models` folder; size and SHA-256 checked at every start; a clear message and exit if it is missing or different.
- **Start-up checks**: processor (AVX, AVX2, FMA) and Microsoft Visual C++ runtime, each with a plain message.
- **Pilot-use notice** on first launch, which must be acknowledged; available again from About.
- **About window**: version, build date, source commit, model hash and check result, the disabled features, licences.
- **Clipboard always put back** after a paste. If the clipboard holds a picture, files or formatted text, the dictation is typed instead so nothing is lost. Dictated text is marked to stay out of Windows clipboard history and cloud clipboard. Text that could not be inserted is removed after 2 minutes and when WinDictate exits.
- **Paste confirmed before the clipboard is put back**: the dictation is offered with Windows' delayed rendering, and the user's clipboard comes back only after the application has read it, so a busy application never pastes the user's earlier clipboard instead. A paste that nothing reads (no text field under the cursor) keeps the dictation on the clipboard with a "check it arrived" notice.
- **AltGr no longer opens the microphone**: the Ctrl key event Windows adds to AltGr is ignored, so typing € and other AltGr characters is not Ctrl+Alt.
- **Copy last dictation** (tray menu, for 2 minutes, memory only) for a dictation that went somewhere unintended.
- **Target application shown** in the indicator while recording (application name only, never the window title).
- **Lost microphone handling**: an unplugged or silent device ends the recording at once with a notice; its audio is discarded.
- **Audio buffers overwritten** after use.
- **`WinDictate.exe --exit`** to close a running copy cleanly (for scripts); the tray item is now called **Exit**.
- **Data in Local AppData** (`%LOCALAPPDATA%\WinDictate`), not roaming.
- **Install and uninstall scripts**, the submission package, SBOM, licences, manifest, hashes and sanitisation report.

### Build

- .NET SDK {{SDK_VERSION}} pinned; .NET runtime {{RUNTIME_VERSION}} (security release of 10 November 2026); package versions locked.
- Microphone library reduced to the parts used (NAudio.WinMM); speech engine shipped for x64 only.
- Guard tests fail the build if network code, a cleanup endpoint, transcript history, audio files or a start-up entry come back.

### Included from WinDictate (open-source, after 0.1.3)

- Hotkey reliability: it no longer stays dead after a cancel; a watchdog handles key releases that never arrive; the keyboard hook runs on its own thread.
- Back-to-back dictation with an in-order queue; nothing is inserted while recording or while a modifier key is held.
- Text is inserted only into the window (and, where possible, field) where the dictation started; otherwise it goes to the clipboard with a notice.
- No dictation is dropped silently: undelivered text goes to the clipboard with a short notice.
- Words in brackets kept; a trailing space between dictations; quieter microphones handled; microphone choice survives docking.
- Lock and sleep cancel a recording; dictations finished before a lock are not typed after unlock.
- The quiet glass "pill" indicator (or edge glow), and the restyled Settings window.
- .NET 10 (long-term support).
