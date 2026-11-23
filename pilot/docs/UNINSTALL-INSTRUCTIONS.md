# Uninstall instructions

WinDictate Restricted Pilot {{VERSION}}.

## With the reference script

From the package's `01-Runtime` folder (or wherever the script was kept):

```powershell
powershell -ExecutionPolicy Bypass -File .\Uninstall-WinDictate.ps1
```

Use `-AllUsers` (as administrator) or `-Destination <folder>` to match how it was installed. Add `-KeepUserData` to keep the settings and log, for example for a support case.

The script removes, in this order, and lists what it removed:

| Item | Location |
|---|---|
| Running program | Asked to exit (`WinDictate.exe --exit`); stopped if still running after 10 seconds |
| Executable, libraries and speech model | `%LOCALAPPDATA%\Programs\WinDictate Restricted Pilot` (or `%ProgramFiles%\…` for all users) |
| Start Menu shortcut | `WinDictate Restricted Pilot.lnk` in the user's (or common) Start Menu |
| Start-up entries, if any | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WinDictate`, the matching `StartupApproved` value, and a Startup-folder shortcut. This build creates none, but a deployment package or an older build might have |
| Settings | `%LOCALAPPDATA%\WinDictate\settings.json` (and `settings.json.bad` if a damaged file was ever replaced) |
| Logs | `%LOCALAPPDATA%\WinDictate\windictate.log` and `windictate.log.old` |

It then checks that none of the folders and shortcuts are left, and fails if any are.

## Manually

1. Right-click the tray icon and choose **Exit** (or run `WinDictate.exe --exit`).
2. Delete the program folder.
3. Delete the Start Menu shortcut, and any Startup-folder shortcut or Run entry your deployment added.
4. Delete `%LOCALAPPDATA%\WinDictate`.

## What does not need removing

- **No transcript or audio files exist.** WinDictate never writes audio to disk and keeps no history of what was dictated. The automated tests check that dictating leaves no such file, and that the log contains no dictated text.
- **No temporary files.** WinDictate creates none, in `%TEMP%` or elsewhere.
- **No registry settings, services, scheduled tasks or firewall rules** are created.
- **Clipboard.** When WinDictate exits, it removes any undelivered dictation it left on the clipboard. Dictated text is marked so Windows clipboard history and cloud clipboard never keep it.
- **Windows' own records.** Like any program, Windows may record that WinDictate ran (for example in the Start Menu's recently used list) and that it used the microphone (Settings > Privacy & security > Microphone > recent activity). These contain no dictated content.

## Reinstalling

Uninstall first, then install as normal. The pilot notice is shown again after a reinstall without `-KeepUserData`.
