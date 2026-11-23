# pilot/: the restricted pilot release

This folder turns the `restricted-pilot-build` branch into the frozen submission package described by the pilot build brief: `WinDictate-Restricted-Pilot-<version>-Submission.zip`.

| Path | What |
|---|---|
| `build-release.ps1`, `build-release.sh` | Build the package (Windows / macOS or Linux). Both run `tools/ReleaseKit` |
| `tools/ReleaseKit/` | The release tool: clean-commit check, pinned SDK, locked restore, tests, publish, model check, source archive, package layout, SBOM, sanitisation, hashes, ZIP. Not shipped; no NuGet packages |
| `model.json` | The pinned speech model: source repository, revision, URL, size, SHA-256, licence. Must match `src/WinDictate/Core/PilotModel.cs` (a unit test checks) |
| `docs/` | Templates for `START-HERE.md`, `02-Source/BUILD_INSTRUCTIONS.md` and the ten documents in `03-Documentation/`. `{{PLACEHOLDERS}}` are filled from the build |
| `compliance/` | Licence texts (`licences/`, fetched at the exact versions used), `THIRD-PARTY-NOTICES.txt` (also shipped beside the program), and the AI factsheet and model record templates |
| `release/` | Templates for the release checklist and the pilot changelog |
| `scripts/` | `Install-WinDictate.ps1`, `Uninstall-WinDictate.ps1`, copied into `01-Runtime/` |
| `sanitise-allowed-hosts.txt` | Public web hosts that may appear in WinDictate's own documents; any other host fails the sanitisation scan |

## Build

```powershell
.\pilot\build-release.ps1 -Output D:\Releases\WinDictate -DenyFile D:\private\deny.txt
```

`-DenyFile` lists private terms (one per line, such as an organisation's name or internal acronyms) that must not appear anywhere in the package. The file and its terms are never written into the repository or the report.

The official build is `.github/workflows/pilot-release.yml`. It builds on a fresh Windows virtual machine and then tests the packaged program there: smoke tests, offline transcription, a network audit, install, uninstall and reinstall, and a Defender scan.

## Freezing

A package is frozen once built: the tool refuses to overwrite one. Any change (code, dependency, configuration, model) means a new version number in `src/WinDictate/WinDictate.csproj`, a new commit, and a new package with new hashes.
