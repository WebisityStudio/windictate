# Build instructions

How to rebuild WinDictate Restricted Pilot {{VERSION}} from `{{SOURCE_ZIP}}` (commit `{{COMMIT}}`).

## What you need

- Windows 10/11 x64 (the official build), or macOS / Linux (the Windows program is cross-compiled; the result is the same program).
- The .NET SDK **{{SDK_VERSION}}** exactly. `global.json` pins it and the build refuses any other version. It brings the .NET {{RUNTIME_VERSION}} runtime that is bundled with the program.
- Git (the release tool records the commit and archives the source from Git).
- Internet access to nuget.org for the locked packages, and once to huggingface.co for the speech model (or pass a local copy with `-Model`).

## Steps

1. Get the exact source: either clone `{{REPO_URL}}` and `git checkout {{COMMIT}}`, or unpack `{{SOURCE_ZIP}}` and run `git init`, `git add -A` and `git commit -m snapshot` inside it (the release tool builds from a commit). With the unpacked snapshot the commit hash differs, so the About window and manifest show the new hash.
2. From the repository root, on Windows:

   ```powershell
   .\pilot\build-release.ps1 -Output D:\Releases\WinDictate
   ```

   On macOS or Linux: `pilot/build-release.sh --out ~/Releases/WinDictate`.

   Optional: `-Model <path to ggml-small.en.bin>` to use a copy you already have, and `-DenyFile <file>` with private terms (one per line) that must not appear in the package. Do not commit that file.
3. The tool prints each step and stops at the first failure. On success it writes `{{PACKAGE_NAME}}\`, `{{ZIP_NAME}}` and `{{ZIP_NAME}}.sha256` to the output folder.

## What the release tool does

| Step | Detail |
|---|---|
| Clean commit | Refuses uncommitted changes (`-AllowDirty` marks a dry run) |
| Pinned SDK | `dotnet --version` must equal `global.json` |
| Locked restore | `dotnet restore --locked-mode`: every package version must match `packages.lock.json` |
| Tests | Unit tests, including guards that fail if network code, a cleanup endpoint, transcript history, audio files or a start-up entry appear in the source |
| Publish | `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:ContinuousIntegrationBuild=true -p:Deterministic=true`, with the commit and build time recorded in the program |
| Model | `pilot/model.json` pins the source revision, size and SHA-256; the file is checked before it is added |
| Source | `git archive` of the commit: tracked files only, no `.git` history |
| Package | Folder structure, documents (filled in from `pilot/docs`), SBOM, licences, manifest |
| Sanitisation | Personal and network paths, e-mail addresses, unreviewed web addresses, credential-like strings, private terms; the build fails on any blocking finding |
| Hashes | `05-Release/SHA256SUMS.txt` for every file, then the ZIP (entries sorted and time-stamped with the commit time) and its `.sha256` |

## Reproducibility

Same commit, same SDK, same locked packages and same model give the same program files: the build is deterministic and records no build-machine paths. Two things differ by design: the build time recorded in `WinDictate.dll` (shown in About) and in the manifest and documents. To reproduce a package byte for byte, pass the original build time: `-BuildDate {{BUILD_DATE}}` (ISO 8601).

Compare `01-Runtime/win-x64` file by file with `05-Release/SHA256SUMS.txt` of the original package.

## Official build

`.github/workflows/pilot-release.yml` builds this package on a fresh GitHub-hosted Windows virtual machine. It then tests the packaged program there: Windows smoke tests, offline transcription with the bundled model, a network audit, and install, uninstall and reinstall. It also runs a Microsoft Defender scan, after removing the hosted image's scan exclusions and proving detection with the standard antivirus test file; if either is not possible there, the scan is reported as inconclusive. In every case, scan the package with your organisation's endpoint scanner as well. The run's log and artefacts are the build record.
