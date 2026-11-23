# Known limitations

WinDictate Restricted Pilot {{VERSION}}. Known before the pilot, stated plainly so the pilot scope can account for them.

## Recognition

- **Accuracy is not guaranteed.** The small English model makes mistakes, most often with names, numbers, amounts, acronyms, specialist terms and strong accents. Every dictation must be reviewed. **Custom words** help somewhat.
- **English only.** The model is English-only; other languages are not recognised.
- **Speed depends on the laptop.** Recognition uses the processor (no GPU). Expect a few seconds for a typical sentence on a recent laptop, longer on older or busy machines. UAT test T-2 measures it.
- **Background noise and distance** from the microphone reduce accuracy; a headset is recommended.

## Insertion

- **Fields inside browsers and some apps cannot be told apart.** Edge, Chrome, Teams, new Outlook and other single-window apps report one control for the whole window. If the user clicks into another field of the same app before the text is ready, it goes into the new field (other windows are protected). UAT F-3.
- **A paste nothing reads is reported, not guessed.** WinDictate puts the user's clipboard back only after the application has read the dictation. If nothing reads it within 2 seconds (usually because no text field had the cursor, or the application is extremely busy), the dictation stays on the clipboard and a notice says to check and press Ctrl+V. An application that reads after that still gets the dictation, never the user's earlier clipboard.
- **Clipboard managers that read every new clipboard content** hide the paste's own read from WinDictate; it then waits 1.5 s before putting the user's clipboard back. Windows clipboard history does not do this (dictations are marked to be left out).
- **Typing instead of pasting.** When the clipboard holds a picture, files or formatted text, the dictation is typed (so the clipboard is not lost). Typing into an area that is not a text field can trigger that application's single-key shortcuts. This is also true of the optional "Typing" mode. Always dictate with the cursor in a text field.
- **Text that could not be inserted is removed from the clipboard after 2 minutes**, by design. If the clipboard held a picture or formatted content before the failure, that earlier content is not restored.
- **Dictations not yet inserted when WinDictate exits are discarded.**
- **Remote sessions (Citrix, Remote Desktop) are not supported.** Insertion into remote windows is not tested, and clipboard redirection may be disabled there.
- **Administrator windows.** Windows does not let an ordinary program type into a window running as administrator; the text goes to the clipboard instead.

## Hotkey and indicator

- **AltGr.** To Windows, AltGr is Ctrl+Alt: it adds a marked left-Ctrl key event to every AltGr press. WinDictate ignores that marked event, so typing AltGr characters (for example € with AltGr+4) never starts a dictation or opens the microphone. Holding the real Ctrl key together with AltGr still counts as the shortcut. This is checked by unit tests; it can only be confirmed with a physical UK or European keyboard (UAT H-5).
- **The indicator appears 0.18 s after the microphone starts delivering audio**, so brushing the keys never flashes it. Recordings shorter than 0.5 s are discarded without being transcribed. Windows' own microphone icon shows from the first moment.
- **The keyboard hook can be blocked** by some security software. WinDictate then says so at start-up, and dictation is unavailable.
- **The Edge glow style does not name the target application** (it has no words by design). The glass pill does.

## Platform and operations

- **Windows 11 x64 only**, on processors with AVX2 and FMA. Older processors get a clear message instead of working.
- **The Microsoft Visual C++ 2015-2022 Redistributable (x64) is required and not included.**
- **Not code-signed.** Windows SmartScreen or application control may warn about or block the unsigned program and scripts until IT signs or approves them.
- **No automatic updates.** Every fix, including .NET security updates to the bundled runtime ({{RUNTIME_VERSION}} at build time), needs a new pilot build with a new version number and new hashes.
- **No central policy settings.** Settings are per user; the pilot's fixed behaviours (no history, no cleanup, no network, fixed model) are built into the program rather than enforced by policy.
- **No accessibility announcements.** The indicator is visual (it follows high contrast and reduced-motion settings) but is not announced by screen readers.
- **Crash dumps** collected by Windows Error Reporting follow the PC's policy and could contain in-memory data from a dictation in progress (see DATA-FLOW-AND-PRIVACY.md).

## Tested where

The automated checks run on a fresh GitHub-hosted Windows Server 2025 virtual machine (same Windows code base as Windows 11 24H2) without a microphone, so recognition there is tested with synthesised speech from a file. Real microphones, real laptops, real applications, Bluetooth headsets, docking and AltGr keyboards are covered only by the UAT test plan.
