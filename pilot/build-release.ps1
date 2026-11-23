<#
.SYNOPSIS
    Builds the WinDictate Restricted Pilot submission package from the current commit.

.DESCRIPTION
    Runs the release tool (pilot/tools/ReleaseKit), which:
      1. refuses to build from uncommitted changes (unless -AllowDirty, for dry runs);
      2. checks the .NET SDK is the one pinned in global.json;
      3. restores packages in locked mode (packages.lock.json);
      4. runs the unit tests, including the guards against network code,
         cleanup endpoints, transcript history and audio files;
      5. publishes the self-contained win-x64 program;
      6. adds the pinned speech model after checking its size and SHA-256
         (from -Model, the local cache .model-cache\, or a download from the
         pinned source recorded in pilot/model.json);
      7. archives the exact source of the commit;
      8. assembles the submission folder, documents, SBOM, licences and manifest;
      9. runs the sanitisation scan;
     10. writes SHA256SUMS.txt, creates the final ZIP and its .sha256.

    Output: <Output>\WinDictate-Restricted-Pilot-<version>-Submission\ and the ZIP beside it.
    The output folder must not already contain a package of the same version.

.PARAMETER Output
    Folder to write the package to (outside the repository).
.PARAMETER Model
    Path to an already downloaded ggml-small.en.bin (checked against the pinned hash).
.PARAMETER DenyFile
    Text file with private terms (one per line, for example an organisation's name)
    that must not appear anywhere in the package. Never commit this file.
.PARAMETER AllowDirty
    Build even with uncommitted changes. The package is then marked as a dry run.
.PARAMETER SkipTests
    Skip the unit tests (dry runs only).

.EXAMPLE
    .\pilot\build-release.ps1 -Output D:\Releases\WinDictate
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Output,
    [string] $Model,
    [string] $DenyFile,
    [string] $BuildDate,
    [switch] $AllowDirty,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$arguments = @('run', '--project', (Join-Path $root 'pilot/tools/ReleaseKit'), '-c', 'Release', '--', 'build', '--out', $Output)
if ($Model) { $arguments += @('--model', $Model) }
if ($DenyFile) { $arguments += @('--deny-file', $DenyFile) }
if ($BuildDate) { $arguments += @('--build-date', $BuildDate) }
if ($AllowDirty) { $arguments += '--allow-dirty' }
if ($SkipTests) { $arguments += '--skip-tests' }

Push-Location $root
try {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "The release build failed (exit code $LASTEXITCODE). Do not use any partial output." }
} finally {
    Pop-Location
}
