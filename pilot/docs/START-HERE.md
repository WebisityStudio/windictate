# WinDictate Restricted Pilot {{VERSION}}: start here
{{DIRTY_WARNING}}
WinDictate is a push-to-talk dictation tool for Windows 11. Hold **Ctrl+Alt**, speak, let go: the words are recognised on the PC and inserted where the cursor is. This package is a fixed, restricted build for a controlled workplace pilot.

## What it does, and what is deliberately switched off

| | |
|---|---|
| Speech recognition | On the PC only, with one fixed English model shipped in the package |
| Network | Not used. The program contains no network code; nothing is downloaded at first run |
| External AI cleanup | Removed from this build (no endpoint, no API key, no settings) |
| Transcript history | Removed: no setting, no file |
| Audio | Held in memory while the shortcut is held; never written to disk; overwritten after use |
| Clipboard | Put back after every paste; text that could not be inserted is removed after 2 minutes |
| Start with Windows | Not offered; installation adds no start-up entry |
| First run | Shows a pilot-use notice that must be acknowledged |

## This build

| | |
|---|---|
| Version | {{VERSION}} |
| Source | {{REPO_URL}}, commit `{{COMMIT}}` ({{COMMIT_DATE}}) |
| Built | {{BUILD_DATE}} with .NET SDK {{SDK_VERSION}}; self-contained .NET {{RUNTIME_VERSION}} runtime for win-x64 |
| Speech model | `{{MODEL_FILE}}` (Whisper {{MODEL_NAME}}), {{MODEL_SIZE}} bytes, SHA-256 `{{MODEL_SHA256}}` |
| Program folder | `01-Runtime/win-x64`: {{RUNTIME_FILE_COUNT}} files, {{RUNTIME_SIZE_MB}} MB including the model |

## Where things are

| Folder | Contents |
|---|---|
| `01-Runtime/win-x64` | The program, ready to run: no installer needed. `models/` holds the speech model |
| `01-Runtime/Install-WinDictate.ps1`, `Uninstall-WinDictate.ps1` | Reference install and removal scripts (per user, or all users) |
| `02-Source` | `{{SOURCE_ZIP}}` (the exact source of the commit), `SOURCE_COMMIT.txt`, `BUILD_INSTRUCTIONS.md` |
| `03-Documentation` | Pilot readme, installation, removal, architecture, data flow and privacy, security controls, user guide, UAT test plan, known limitations, support template |
| `04-Compliance` | SBOM (`SBOM.cdx.json` CycloneDX 1.5, `SBOM.csv`), `COMPONENT-INVENTORY.md`, `THIRD-PARTY-NOTICES.txt`, `LICENCES/`, operational AI factsheet, model source and hash record |
| `05-Release` | `RELEASE-MANIFEST.json`, `CHANGELOG.md`, `RELEASE-CHECKLIST.md`, `SANITISATION-REPORT.md`, `BUILD-LOG.txt`, `SHA256SUMS.txt` |

## Requirements

Windows 11 x64; a processor with AVX, AVX2 and FMA; the Microsoft Visual C++ 2015-2022 Redistributable (x64), which is **not** included and should come through the normal software deployment route; about 1 GB of free memory and 700 MB of disk. Details: `03-Documentation/INSTALLATION-INSTRUCTIONS.md`.

## Verify the package

Every file is listed with its SHA-256 in `05-Release/SHA256SUMS.txt`; the ZIP's own hash is in `{{ZIP_NAME}}.sha256` beside the ZIP.

Windows PowerShell, from this folder:

```powershell
Get-Content .\05-Release\SHA256SUMS.txt | ForEach-Object {
  $hash, $file = $_ -split '  ', 2
  if ((Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash -ne $hash) { "MISMATCH $file" }
}
(Get-FileHash -Algorithm SHA256 ..\{{ZIP_NAME}}).Hash
```

No output from the first command means every file matches. On macOS or Linux: `shasum -a 256 -c 05-Release/SHA256SUMS.txt`.

## Build it yourself

`02-Source/BUILD_INSTRUCTIONS.md` rebuilds this package from the source snapshot. The release tool checks the commit, the pinned SDK, the locked package versions and the model hash, and runs the tests and the sanitisation scan.

## Test it

`03-Documentation/UAT-TEST-PLAN.md` lists the checks for a Windows 11 laptop. `05-Release/RELEASE-CHECKLIST.md` maps every requirement of the build brief to its evidence.
