# Security controls

WinDictate Restricted Pilot {{VERSION}}, commit `{{COMMIT_SHORT}}`. Each control lists how it is enforced and how it is verified. "Unit test" means the cross-platform tests that run in every build. "Windows test" means the smoke tests and checks that run on a fresh Windows virtual machine in the official build. "UAT" means the manual test plan.

## 1. No network access

| Control | Enforcement | Verification |
|---|---|---|
| No network code in the program | AI cleanup and model download removed; no HTTP, socket or web client code | Unit test `PilotGuardTests.NoNetworkCode` scans the source; Windows test `TheProgram_ReferencesNoNetworkingCode` inspects the compiled `WinDictate.dll` (no reference to `System.Net.Http`, sockets or the speech library's model downloader) |
| No endpoint or key can be configured | Settings have no endpoint, URL or key fields; unknown settings from other builds are dropped | Unit tests `NoRemoteCleanupEndpointOrKeys`, `NoWebAddresses_ExceptXamlAndManifestSchemas`, `SettingsFromAnotherBuild_DropCleanupAndHistory_WhenSaved` |
| No connection attempts at run time | — | Official build: the packaged program starts, verifies and loads the model, idles and exits, with Windows Filtering Platform connection auditing on; any connection event fails the build. UAT N-1 |
| Works offline from first run | The model ships in the package and is never downloaded | Official build: offline transcription test with the bundled model. UAT T-4 |

## 2. Fixed, verified model

| Control | Enforcement | Verification |
|---|---|---|
| Only the approved model is loaded | Size and SHA-256 compiled into the program (`{{MODEL_SHA256}}`), checked before every load | Unit tests `PilotModelTests.*`, `TranscriberTests.ModelThatIsNotTheApprovedFile_IsNotLoaded`; UAT M-1, M-2 |
| Missing or altered model fails clearly | The program shows the reason and exits | Unit test `ModelFailingItsCheck_IsReportedOnce_AndNothingIsInserted`; UAT M-1, M-2 |
| Model provenance recorded | `pilot/model.json`: repository, pinned revision, size, SHA-256, licence | Release tool verifies the file against it; `04-Compliance/MODEL-SOURCE-LICENCE-AND-HASH.md` |

## 3. No retention of dictated content

| Control | Enforcement | Verification |
|---|---|---|
| No transcript history | Feature, setting and file removed | Unit test `NoTranscriptHistory`; `NothingDictated_IsWrittenToDisk_NorToTheLog` |
| No audio files | Audio only in memory; no file-writing code outside settings and log | Unit tests `NoAudioFiles`, `FilesAreWrittenOnlyForSettingsAndTheLog`; official build lists new files after a run |
| Audio buffers overwritten | Buffers zeroed after recognition or discard | Unit tests `Audio_IsOverwritten_OnceItHasBeenTranscribed`, `…WhenADictationIsCancelled`, `MicrophoneUnplugged_…` |
| No dictated text in logs | Only counts, durations, types and codes are logged | Unit test `NothingDictated_IsWrittenToDisk_NorToTheLog`; UAT L-1 |
| Clipboard not left holding dictations | Restored after each paste; undelivered text removed after 2 minutes and at exit; excluded from history and cloud | Unit tests in `TextInserterTests` and `DictationControllerTests`; Windows tests `Clipboard_RoundTrips_WithoutTheUiThread` (markers present) and `TextWaitingOnTheClipboard_…`; UAT C-1 to C-5 |
| Clipboard content of the user preserved | Text restored; non-text content never replaced (dictation typed instead) | Unit tests `PictureOnTheClipboard_…`, `FormattedTextOnTheClipboard_…`; Windows test `APictureOnTheClipboard_SurvivesADictation`; UAT C-2 |

## 4. Text goes only where it was dictated

| Control | Enforcement | Verification |
|---|---|---|
| Target recorded at hotkey-down | Foreground window and focused control | Unit and Windows tests `ADictationStartedInOneWindow_IsNotInsertedIntoAnother` |
| Verified before inserting | Focus moved: the text goes to the clipboard with a notice; nothing is typed into the new window | Unit tests `FocusMovedToAnotherWindow_…`, `FocusMovedToAnotherField_…`; UAT F-1, F-2 |
| Target shown while recording | The indicator names the target application (never the window title) | Windows test `ThePill_NamesTheTargetApplication`; UAT I-2 |
| Lock, sleep, secure desktop | The session is cancelled and its audio discarded; dictations finished before a lock go to the clipboard instead of being typed after unlock | Unit tests `WorkstationLockedAfterADictation_…`, `KeysLost_DiscardsTheAudio_WithANotice`; UAT S-1, S-2 |
| No blind typing during key presses | Insertion waits until no modifier key is held (then the clipboard after 4 s) | Unit tests `NothingIsInserted_WhileModifierKeysAreHeld`, `ModifiersHeldTooLong_…` |

## 5. Recording only while the shortcut is held

| Control | Enforcement | Verification |
|---|---|---|
| Push-to-talk only | Recording starts when the shortcut goes down and stops when it is released; there is no continuous or background mode | Unit tests (`HotkeyMonitorTests`, controller tests); UAT H-1 |
| Missed key release cannot keep the microphone open | Watchdog ends the session within about 0.2 s when the keys are no longer held | `HotkeyWatchdogTests`; UAT S-2 |
| Lost microphone ends the recording | Device stop or 2 s without audio ends the session; audio discarded; notice shown | Unit test `MicrophoneUnplugged_…`; UAT D-1 |
| Cancel route | Esc (or any other key) before letting go | Unit tests; UAT H-2 |
| Hotkey failure is reported | A message if the keyboard hook cannot be installed; tray shows "Shortcut unavailable" | Code review; UAT H-4 |
| Indicator | Shows while the microphone captures; disappears when it stops | Unit and Windows tests; UAT I-1 |

## 6. Least privilege and footprint

- Runs as the signed-in user (`asInvoker` manifest); no elevation, service, driver, scheduled task or firewall rule.
- No start-up entry; no registry writes (it reads only the Windows light/dark setting).
- The keyboard hook is passive: it never blocks, changes or records keystrokes, and it ignores injected input.
- Single instance per session; `--exit` for clean shutdown in scripts.

## 7. Build and supply chain

| Control | Detail |
|---|---|
| Pinned toolchain | .NET SDK {{SDK_VERSION}} (`global.json`, roll-forward disabled); .NET runtime {{RUNTIME_VERSION}}, the latest security release at build time |
| Locked dependencies | `packages.lock.json` for every project; restore in locked mode |
| Minimal dependencies | NAudio reduced to the waveIn part; native speech engine for x64 only; the release tool uses no packages |
| Deterministic, symbol-free build | No PDBs or build-machine paths in the output |
| SBOM | CycloneDX 1.5 (`04-Compliance/SBOM.cdx.json`) and CSV; every shipped file mapped to a component |
| Hashes | SHA-256 of every file (`05-Release/SHA256SUMS.txt`) and of the ZIP |
| Sanitisation | Automated scan of every file and of the source snapshot; report in `05-Release/SANITISATION-REPORT.md` |
| Official build | Fresh GitHub-hosted Windows virtual machine; build log, test results and Microsoft Defender scan kept as run artefacts, with a signed build-provenance attestation |

## 8. Not provided (see KNOWN-LIMITATIONS.md)

The program and scripts are not code-signed. There is no centrally managed policy file, and no automatic updates. Security updates to the bundled .NET runtime need a new pilot build.
