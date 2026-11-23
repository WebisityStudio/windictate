# Data flow and privacy

WinDictate Restricted Pilot {{VERSION}}. Everything below happens on the user's PC; nothing is sent anywhere.

## Summary

| Data | Where it exists | How long | Written to disk |
|---|---|---|---|
| Microphone audio | Process memory | While the shortcut is held, until recognition ends; then overwritten | Never |
| Transcript | Process memory; the target application | Until inserted; the last one stays in memory for 2 minutes for "Copy last dictation" | Never by WinDictate (the user saves it in the target application) |
| Clipboard | Windows clipboard | Milliseconds during a paste; undelivered text up to 2 minutes | No (excluded from clipboard history and cloud clipboard) |
| Settings | `%LOCALAPPDATA%\WinDictate\settings.json` | Until uninstalled | Yes: preferences only, no dictated content |
| Log | `%LOCALAPPDATA%\WinDictate\windictate.log` | Rotated at 1 MB, one older copy kept | Yes: operational events only |
| Speech model | Program folder | Until uninstalled | Yes: read-only, shipped in the package |

## Microphone data

- The microphone is opened when the shortcut is pressed and closed when it is released, cancelled, interrupted or ends at 5 minutes. WinDictate never listens in the background.
- Audio is captured as 16 kHz mono 16-bit samples into a memory stream, in 30 ms buffers.
- Each audio buffer WinDictate owns is overwritten with zeros once it is no longer needed: after recognition, or as soon as a recording is discarded. Discard happens when a recording is cancelled, too short (under 0.5 s), silent, or when the microphone was lost. Buffers inside the speech engine and the audio library are released to the .NET garbage collector, which frees but does not wipe them.
- The level shown by the indicator and the Settings microphone test is a loudness number only. The Settings test listens for up to 6 seconds, keeps nothing and transcribes nothing.
- No audio is ever written to a file. The source has no file-writing code outside settings and the log, and a guard test fails the build if any appears.

## Transcript lifecycle

1. Produced in memory by the speech engine; non-speech annotations such as `[BLANK_AUDIO]` are removed.
2. Inserted into the target application by paste or keystrokes.
3. Kept in memory for 2 minutes, so the user can use **Copy last dictation** if the text went nowhere (for example the cursor was not in a text field). Then forgotten.
4. Never logged, never saved by WinDictate, never sent anywhere.

Dictations still in the queue when WinDictate exits are discarded.

## Clipboard lifecycle

| Situation | What happens |
|---|---|
| Paste (default) | The current clipboard is read; the transcript is offered on the clipboard with Windows' delayed rendering, so Windows tells WinDictate the moment an application reads it; Ctrl+V is sent; once the application has read the text (plus 0.3 s for apps that read several formats), the previous content is put back, unless something else has replaced the transcript in the meantime. A busy application therefore never pastes the user's earlier clipboard content instead of the dictation |
| Paste that nothing reads within 2 s | Usually the cursor was not in a text field. The transcript stays on the clipboard as undelivered text, with a notice to check and press Ctrl+V; it is removed after 2 minutes like any undelivered text |
| Clipboard holds a picture, files, formatted text or anything besides plain text | WinDictate cannot restore that exactly, so it types the text instead and does not touch the clipboard |
| Clipboard cannot be read | Text is typed instead |
| Another program reads the clipboard before the paste (for example a clipboard manager) | The paste's own read cannot be seen; the previous content is put back after 1.5 s |
| Type mode (setting) | The clipboard is never used, unless insertion fails |
| Insertion fails (focus moved, keys held, input blocked, PC locked since the dictation) | The text is left on the clipboard and a notice says "Press Ctrl+V". Later dictations leave it alone for 30 s. After 2 minutes it is removed and the previous plain-text content is put back (or the clipboard is cleared). If the user copies something else meanwhile, WinDictate leaves the clipboard alone |
| WinDictate exits | Any undelivered text still on the clipboard is removed |
| Every clipboard write | Marked with `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory = 0` and `CanUploadToCloudClipboard = 0`, so Windows clipboard history (Win+V) and cloud clipboard leave it out. Well-behaved clipboard managers honour the same marker |

The previous clipboard content is held in memory only for the moment of the paste, and is never logged.

## Settings

`settings.json` holds the shortcut, quick-tap threshold, insertion method, indicator style, trailing-space choice, microphone name and index, custom words, recording limit, sensitivity values, and the version of the pilot notice acknowledged. **Custom words** are entered by the user and passed to the speech engine as a hint; ask pilot users not to enter client names there. Settings from other builds of WinDictate (for example a cleanup endpoint or API key) are ignored and removed on the next save.

## Model storage

`<program folder>\models\{{MODEL_FILE}}`, {{MODEL_SIZE}} bytes, SHA-256 `{{MODEL_SHA256}}`. Read-only, checked at every start, never downloaded or replaced by the program, never trained or updated with user data.

## Logs

`windictate.log` records versions, start and exit, the model check result, the keyboard hook's state, microphone problems, the duration and word count of each dictation, how it was inserted (paste or type), and error types and codes with stack traces. It never records audio, transcripts, clipboard contents, text sent to other applications, window titles or document names. A unit test dictates distinctive words and fails if they appear in the log or anywhere in the data folder.

## Crash dumps

WinDictate creates no crash dumps. .NET writes one only when the `DOTNET_DbgEnableMiniDump` environment variable is set. Windows Error Reporting (WER) may collect a dump of any crashing process according to the PC's WER policy, and such a dump could contain whatever was in memory at the time, including audio or text of a dictation in progress. Organisations that restrict WER for other applications should apply the same policy to `WinDictate.exe`.

## Network behaviour

None. The program has no network code: no update check, telemetry, licence check, model download or cleanup service. Unit tests on the source and Windows tests on the compiled program check for networking code. The official build runs the packaged program with Windows' connection auditing on and fails if any connection attempt is recorded. The .NET runtime bundled with the program includes network libraries (as every .NET application does), but WinDictate never calls them.

## Absence of transcript history and audio files

There is no history feature, setting or file in this build. The guard tests fail the build if `transcription-history`, a history setting, or any audio-file writing appears in the source. The uninstall script confirms there is nothing of the kind to remove.

## Windows' own records

Windows itself may note that WinDictate used the microphone (Settings > Privacy & security > Microphone > recent activity) and that it ran. These contain no dictated content.
