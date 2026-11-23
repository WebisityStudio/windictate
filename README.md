# WinDictate: Restricted Pilot build

> **This branch (`restricted-pilot-build`) is the restricted pilot edition of WinDictate.** It is the open-source WinDictate with the network-related and retention-related features removed, for a controlled workplace pilot. The general version lives on `main`.

**Local, push-to-talk dictation for Windows 11.** Hold **Ctrl+Alt**, speak, let go: the speech is recognised on the PC by a fixed Whisper model, and the text is inserted where the cursor is, in any application. Review it there before saving or sending.

## What this edition does differently

| | Restricted pilot (this branch) | General version (`main`) |
|---|---|---|
| Speech recognition | On the PC, one fixed model shipped in the package and checked by SHA-256 at every start | On the PC; choice of models, downloaded on first use |
| Network | None: the program contains no network code | Model download; optional AI cleanup service |
| AI cleanup | Removed | Optional, off by default |
| Transcript history | Removed | Optional, off by default |
| Clipboard | Always put back after a paste; undelivered text removed after 2 minutes; excluded from clipboard history | Dictation kept on the clipboard by default |
| Start with Windows | Not offered | Optional |
| First run | Pilot-use notice to acknowledge | — |
| Data folder | `%LOCALAPPDATA%\WinDictate` | `%APPDATA%\WinDictate` |

## For pilot reviewers and IT

The release is a single package, `WinDictate-Restricted-Pilot-<version>-Submission.zip`. It holds the self-contained program and model, the exact source snapshot, documentation, SBOM, licences, manifest and SHA-256 hashes. Start with its `START-HERE.md`.

The documents are written from templates in [`pilot/docs`](pilot/docs) and [`pilot/compliance`](pilot/compliance), which you can read here:

- [Pilot readme](pilot/docs/README-PILOT.md), [user guide](pilot/docs/USER-GUIDE.md)
- [Installation](pilot/docs/INSTALLATION-INSTRUCTIONS.md), [uninstall](pilot/docs/UNINSTALL-INSTRUCTIONS.md)
- [Technical architecture](pilot/docs/TECHNICAL-ARCHITECTURE.md), [data flow and privacy](pilot/docs/DATA-FLOW-AND-PRIVACY.md), [security controls](pilot/docs/SECURITY-CONTROLS.md)
- [UAT test plan](pilot/docs/UAT-TEST-PLAN.md), [known limitations](pilot/docs/KNOWN-LIMITATIONS.md), [support and ownership template](pilot/docs/SUPPORT-AND-OWNERSHIP-TEMPLATE.md)
- [Operational AI factsheet](pilot/compliance/OPERATIONAL-AI-FACTSHEET.md), [model source, licence and hash](pilot/compliance/MODEL-SOURCE-LICENCE-AND-HASH.md)

## Build the package

Requirements: the .NET SDK pinned in [`global.json`](global.json), Git, and access to nuget.org (and once to huggingface.co for the model, unless you pass a local copy).

```powershell
.\pilot\build-release.ps1 -Output D:\Releases\WinDictate
```

On macOS or Linux: `pilot/build-release.sh --out ~/Releases/WinDictate`. See [`pilot/README.md`](pilot/README.md) and [`pilot/docs/BUILD_INSTRUCTIONS.md`](pilot/docs/BUILD_INSTRUCTIONS.md).

The official build, [`.github/workflows/pilot-release.yml`](.github/workflows/pilot-release.yml), runs on a fresh Windows virtual machine. It builds the package and then tests the packaged program there: smoke tests, offline transcription, a network audit, install, uninstall and reinstall, and a Defender scan.

## Development

```bash
dotnet test tests/WinDictate.Tests          # cross-platform unit tests, including the pilot guard tests
dotnet test tests/WinDictate.UiSmoke        # Windows only: real windows, clipboard, keyboard
```

## Licence

MIT. See [LICENSE](LICENSE) and, for bundled components, [`pilot/compliance/THIRD-PARTY-NOTICES.txt`](pilot/compliance/THIRD-PARTY-NOTICES.txt).
