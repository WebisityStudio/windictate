# UAT test plan

WinDictate Restricted Pilot {{VERSION}}, commit `{{COMMIT_SHORT}}`. Run on a Windows 11 laptop of the kind pilot users have, with a headset, signed in as an ordinary user. Use only invented content when dictating (for example "Example Company Ltd", "Test User", made-up amounts and dates).

Record each result as **Pass**, **Fail** (with what happened) or **N/A**. The official build already runs the automated checks marked *(automated)* on a fresh Windows virtual machine; repeat them here on real hardware.

Tester: ______________________  Date: ____________  Laptop model and Windows build (`winver`): ______________________

## Install and first run

| ID | Test | Steps | Expected | Result |
|---|---|---|---|---|
| INS-1 | Package integrity | Verify the package as in `START-HERE.md` | No mismatches; the ZIP's hash matches `{{ZIP_NAME}}.sha256` | |
| INS-2 | Install for the current user *(automated)* | Run `Install-WinDictate.ps1` from `01-Runtime` | "Checked N program files"; installed to `%LOCALAPPDATA%\Programs\WinDictate Restricted Pilot`; Start Menu shortcut present; no start-up entry (Task Manager > Startup apps does not list it) | |
| INS-3 | First launch and pilot notice | Start from the Start Menu | The notice appears with the exact pilot text; **Exit WinDictate** quits without starting; on the next start **I understand** starts it; the tray tooltip shows "Ready" within about 10 s | |
| INS-4 | Microphone permission | Settings > Privacy & security > Microphone: desktop apps off, then dictate; then turn it on | Off: a microphone message or "Didn't catch that" (Windows supplies silence); nothing inserted. On: dictation works | |
| INS-5 | About | Tray > About… | Version {{VERSION}}, build date, commit `{{COMMIT_SHORT}}…`, model `{{MODEL_FILE}}` with "checksum verified", history and AI cleanup "Disabled", network "Not used"; Licence and Third-party notices open locally | |

## Hotkey and indicator

| ID | Test | Steps | Expected | Result |
|---|---|---|---|---|
| H-1 | Basic dictation | In Notepad, hold Ctrl+Alt, say a sentence, let go | Pill with moving bars and "Notepad" while speaking; dots, then a tick; the sentence appears followed by one space | |
| H-2 | Cancel | Hold Ctrl+Alt, speak, press Esc, let go | "Cancelled"; nothing inserted | |
| H-3 | Quick tap | Tap Ctrl+Alt briefly | Nothing appears, nothing inserted | |
| H-4 | Shortcut available | At every start in this plan | No "could not register its keyboard shortcut" message; if it appears, record which security software is installed | |
| H-5 | Keyboard with AltGr (UK/European layout) | Type € (AltGr+4) and other AltGr characters in Word | The characters type normally; nothing is dictated or inserted. Note whether Windows' microphone icon flickers (see KNOWN-LIMITATIONS) | |
| H-6 | Shortcut clash | If Ctrl+Alt clashes with another program, change it in Settings > Advanced and dictate | The new shortcut works; the tray menu shows it | |
| I-1 | Indicator follows the microphone | Hold Ctrl+Alt for 10 s while speaking | The pill is visible for the whole recording and disappears after insertion | |
| I-2 | Target shown | Dictate into Outlook, Word, Teams and Edge | The pill names each app; never a document or e-mail title | |
| I-3 | Edge glow | Settings: Edge glow, Preview, Save, dictate | The taskbar's top edge glows while recording; no words except for problems | |
| I-4 | Dark mode, high contrast, reduced motion | Switch each on in Windows Settings and dictate | The indicator and windows are readable; states change without animation when animation effects are off | |

## Transcription

| ID | Test | Steps | Expected | Result |
|---|---|---|---|---|
| T-1 | Accuracy | Dictate: "Please call Test User at Example Company Ltd about the invoice for one thousand five hundred pounds due on the twelfth of March." | Text close to what was said; note every error, especially names and numbers | |
| T-2 | Speed | Dictate about 15 seconds of speech; time from letting go to the text appearing | Record the time: ______ s (guide: a few seconds on a recent laptop) | |
| T-3 | Back-to-back | Dictate a sentence, then immediately another | Both appear, in order, separated by a space | |
| T-4 | Offline *(automated)* | Turn on airplane mode (or unplug the network), restart WinDictate, dictate | Works exactly as online | |
| T-5 | Long dictation | Hold for about 60 s, speaking | The whole text appears; the pill shows dots while working | |

## Applications

| ID | Application and field | Expected | Result |
|---|---|---|---|
| A-1 | Outlook (classic): new e-mail body | Text inserted at the cursor | |
| A-2 | New Outlook: new e-mail body | Text inserted at the cursor | |
| A-3 | Word: document body | Text inserted; formatting of the surrounding text unaffected | |
| A-4 | Teams: chat message box | Text inserted; not sent automatically | |
| A-5 | Edge or Chrome: a web form field (for example a search box or a CRM field) | Text inserted | |
| A-6 | A line-of-business application used in the pilot | Text inserted, or "Copied. Press Ctrl+V" | |

## Focus safety

| ID | Test | Steps | Expected | Result |
|---|---|---|---|---|
| F-1 | Switch window *(automated)* | Dictate a long sentence into Notepad, let go, immediately click into Word | Nothing typed into Word; "Focus moved. Press Ctrl+V"; Ctrl+V in Notepad pastes it | |
| F-2 | Switch field | In classic Outlook, dictate into the body, let go, immediately click Subject | Nothing typed into Subject; "Focus moved. Press Ctrl+V" | |
| F-3 | Single-window apps | In Edge, dictate into one field, let go, immediately click another field on the same page | Record what happens (WinDictate cannot tell fields apart inside a browser; see KNOWN-LIMITATIONS) | |

## Clipboard

| ID | Test | Steps | Expected | Result |
|---|---|---|---|---|
| C-1 | Clipboard restored *(automated)* | Copy a word from Notepad; dictate into Word; press Ctrl+V somewhere | Ctrl+V pastes the copied word, not the dictation | |
| C-2 | Picture preserved *(automated)* | Copy a picture (Win+Shift+S); dictate; paste into Paint or Word | The dictation was typed in; the picture is still on the clipboard | |
| C-3 | Failed insertion recovery | Dictate, let go and keep Shift held for over 5 s | "Copied. Press Ctrl+V"; Ctrl+V pastes the dictation | |
| C-4 | Recovery removed after 2 minutes | After C-3, wait 2½ minutes, press Ctrl+V | The dictation is no longer pasted; what was on the clipboard before is back (if it was plain text) | |
| C-5 | Clipboard history | Turn on clipboard history (Win+V), dictate several times | Win+V never shows dictated text | |
| C-6 | Copy last dictation | Dictate with the cursor on the desktop (no text field); within 2 minutes use tray > Copy last dictation; paste into Notepad | The dictation is pasted; after 2 minutes the menu item is greyed out | |

## Security transitions and devices

| ID | Test | Steps | Expected | Result |
|---|---|---|---|---|
| S-1 | Lock right after dictating | Dictate a long sentence, let go, press Win+L at once; unlock | The text is not typed after unlock; "Copied. Press Ctrl+V" appears | |
| S-2 | Secure desktop while holding | Hold Ctrl+Alt and press Delete | "Cancelled"; nothing inserted after returning | |
| S-3 | Sleep | Dictate, close the lid within a second, reopen | Nothing typed unexpectedly; dictation works afterwards | |
| D-1 | Microphone disconnected | Hold Ctrl+Alt with a USB or Bluetooth headset, unplug or switch it off mid-sentence | Within about 2 s the pill shows "Microphone disconnected"; nothing inserted | |
| D-2 | Docking | Choose the headset in Settings > Advanced; undock and redock | Dictation uses the headset when present and the default microphone otherwise | |
| D-3 | No microphone | Disable all input devices; dictate | "No microphone found"; WinDictate keeps running | |

## Model, processor and runtime

| ID | Test | Steps | Expected | Result |
|---|---|---|---|---|
| M-1 | Missing model | Exit; rename `models\{{MODEL_FILE}}` in the install folder; start | A message that the model file is missing; WinDictate exits; nothing else happens. Rename back | |
| M-2 | Altered model | Exit; replace the model with a copy where one byte is changed (keep the original); start | A message that the model failed its checksum; WinDictate exits. Restore the original | |
| U-1 | Unsupported processor | Only if a PC without AVX2 is available: start WinDictate | "This PC's processor is not supported…"; exits cleanly (no crash) | |
| U-2 | Visual C++ runtime missing | On a clean Windows 11 virtual machine without the Redistributable: start WinDictate | A message naming the missing files; exits. After installing the Redistributable it works | |

## Logs, files and network

| ID | Test | Steps | Expected | Result |
|---|---|---|---|---|
| L-1 | Log has no dictated text | Dictate a distinctive invented phrase; Tray > Open log; search for a word from it | Not found; the log shows only durations, word counts and events | |
| L-2 | No other files | Open `%LOCALAPPDATA%\WinDictate` | Only `settings.json` and `windictate.log` (and `windictate.log.old`) | |
| L-3 | No audio or history files | After a session of dictating, search the user profile and `%TEMP%` for files changed today ending in `.wav`, `.pcm`, `.tsv` | None created by WinDictate | |
| N-1 | No network activity *(automated)* | Resource Monitor > Network: watch WinDictate.exe while dictating several times | No network activity for WinDictate.exe | |

## Exit, uninstall, reinstall

| ID | Test | Steps | Expected | Result |
|---|---|---|---|---|
| X-1 | Exit *(automated)* | Tray > Exit | The icon disappears; Task Manager shows no WinDictate.exe within a few seconds | |
| X-2 | Uninstall *(automated)* | Run `Uninstall-WinDictate.ps1` | Lists what it removed; the program folder, shortcut and `%LOCALAPPDATA%\WinDictate` are gone; ends with "Uninstall complete" | |
| X-3 | Reinstall *(automated)* | Install again, start | Works; the pilot notice is shown again | |

## Sign-off

| Outcome | Name | Date |
|---|---|---|
| All tests passed / passed with noted exceptions / failed | | |
