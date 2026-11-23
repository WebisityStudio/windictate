# Installation instructions

WinDictate Restricted Pilot {{VERSION}}. The program folder `01-Runtime/win-x64` is complete: it can be copied to any folder and run from there. The reference script below does that and adds a Start Menu shortcut. A deployment team can wrap the same steps in its own packaging (for example a Win32 app package).

## 1. Prerequisites

| Check | How |
|---|---|
| Windows 11 x64 | `winver` |
| Processor supports AVX2 and FMA | WinDictate checks at start-up and shows "This PC's processor is not supported" if not. In advance: any Intel Core processor from 4th generation (2013) on, or AMD from 2015 on |
| Microsoft Visual C++ 2015-2022 Redistributable (x64) | Settings > Apps > Installed apps, search "Visual C++ 2015-2022". If missing, deploy it through the approved software route. WinDictate names any missing file at start-up (`vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll`, `vcomp140.dll`) |
| Microphone access for desktop apps | Settings > Privacy & security > Microphone: "Microphone access" and "Let desktop apps access your microphone" on |
| Package integrity | See "Verify the package" in `START-HERE.md` |

## 2. Install

### With the reference script (per user, no administrator rights)

From the `01-Runtime` folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-WinDictate.ps1
```

| | |
|---|---|
| Destination folder | `%LOCALAPPDATA%\Programs\WinDictate Restricted Pilot` |
| Start Menu shortcut | `WinDictate Restricted Pilot` (current user's Start Menu) |
| What the script checks | Every program file against `05-Release/SHA256SUMS.txt` before copying (when present) |
| What it does not add | Start-up entry, service, scheduled task, registry settings, firewall rules |

For all users of the PC, run as administrator with `-AllUsers`. The destination becomes `%ProgramFiles%\WinDictate Restricted Pilot` and the shortcut goes in the common Start Menu. `-Destination <folder>` chooses another folder.

### Manually or with a deployment tool

1. Copy the whole `01-Runtime/win-x64` folder (including `models\`) to the destination.
2. Create a Start Menu shortcut to `WinDictate.exe` in that folder.
3. Do not add a start-up entry unless the pilot owners decide to (see below).

**Detection method:** the file `WinDictate.exe` in the destination folder, product version `{{VERSION}}`. Optionally also its SHA-256, from `05-Release/RELEASE-MANIFEST.json` (`executableSha256`).

**Uninstall command for a deployment tool:** `powershell -ExecutionPolicy Bypass -File Uninstall-WinDictate.ps1` (add `-AllUsers` to match the install).

## 3. Locations

| What | Where |
|---|---|
| Program and libraries | Destination folder (read-only for the user is fine) |
| Speech model | `<destination>\models\{{MODEL_FILE}}` ({{MODEL_SIZE}} bytes, SHA-256 `{{MODEL_SHA256}}`), checked at every start |
| Settings | `%LOCALAPPDATA%\WinDictate\settings.json`: shortcut, indicator style, microphone choice, insertion method, custom words, and whether the pilot notice was acknowledged. No dictated content |
| Log | `%LOCALAPPDATA%\WinDictate\windictate.log` (one older copy kept as `.old` after 1 MB). Operational events only; see `DATA-FLOW-AND-PRIVACY.md` |

The data folder is in Local, not Roaming, application data, so nothing is copied to a roaming-profile server.

## 4. First launch

1. Start **WinDictate Restricted Pilot** from the Start Menu. There is no main window: it runs in the notification area (system tray).
2. The pilot-use notice appears. **I understand** starts WinDictate; **Exit WinDictate** quits without starting. Only the fact that it was acknowledged is stored.
3. WinDictate checks the speech model's checksum and loads it (a few seconds). The tray icon's tooltip then says **Ready**.
4. Windows may ask for microphone access the first time the shortcut is held.

If the model is missing or fails its checksum, WinDictate shows why and exits. Reinstall from the package; never replace the model file by hand.

## 5. Tray process behaviour

- One copy runs per user session (a second start says it is already running).
- Right-click the tray icon for: **Enabled** (pause/resume), **Copy last dictation**, **Settings…**, **About…**, **Open log**, **Exit**.
- **Exit** ends the process completely: the keyboard hook is removed, the microphone is closed, any undelivered text is removed from the clipboard, and the process ends.
- `WinDictate.exe --exit` asks a running copy to exit in the same way. It waits up to 10 seconds and returns exit code 0 once no copy is running. Use it in scripts before replacing or removing files.

## 6. Starting with Windows

The pilot build has no option to start with Windows, and installation adds no start-up entry. If the pilot owners decide RMs should have it start automatically, add a shortcut to `WinDictate.exe` in the Startup folder (or the deployment tool's equivalent). The uninstall script removes it again.

## 7. Version check

About (tray menu > About…) shows the version, build date, source commit and speech model hash. Copy details puts them on the clipboard for a support request. The same values are in `05-Release/RELEASE-MANIFEST.json`.
