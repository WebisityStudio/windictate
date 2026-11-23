<#
.SYNOPSIS
    Installs WinDictate Restricted Pilot from this package.

.DESCRIPTION
    Copies the program folder (win-x64, next to this script) to the install
    folder and adds a Start Menu shortcut. Nothing else: no service, no
    scheduled task, no start-up entry, no registry settings, no network access.

    Default: for the signed-in user only, no administrator rights needed, into
    %LOCALAPPDATA%\Programs\WinDictate Restricted Pilot.
    -AllUsers: for everyone on the PC (run as administrator), into
    %ProgramFiles%\WinDictate Restricted Pilot.

    Before copying, each program file is checked against
    ..\05-Release\SHA256SUMS.txt when that file is present.

    Windows PowerShell 5.1 or PowerShell 7. If scripts are blocked on the PC:
        powershell -ExecutionPolicy Bypass -File .\Install-WinDictate.ps1

.EXAMPLE
    .\Install-WinDictate.ps1
.EXAMPLE
    .\Install-WinDictate.ps1 -AllUsers
#>
[CmdletBinding()]
param(
    [switch] $AllUsers,
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$productName = 'WinDictate Restricted Pilot'
$source = Join-Path $PSScriptRoot 'win-x64'

if (-not (Test-Path (Join-Path $source 'WinDictate.exe'))) {
    throw "WinDictate.exe was not found in $source. Run this script from the 01-Runtime folder of the package."
}
if (-not $Destination) {
    $Destination = if ($AllUsers) { Join-Path $env:ProgramFiles $productName }
                   else { Join-Path $env:LOCALAPPDATA "Programs\$productName" }
}

# 1. Check the files against the package's SHA-256 list, when it is there.
$sums = Join-Path $PSScriptRoot '..\05-Release\SHA256SUMS.txt'
if (Test-Path $sums) {
    $checked = 0
    foreach ($line in Get-Content -LiteralPath $sums) {
        if ($line -notmatch '^([0-9a-f]{64})  (01-Runtime/win-x64/.+)$') { continue }
        $expected = $Matches[1]
        $relative = $Matches[2].Substring('01-Runtime/win-x64/'.Length)
        $file = Join-Path $source ($relative -replace '/', '\')
        if (-not (Test-Path -LiteralPath $file)) { throw "Missing from the package: $relative" }
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw "Checksum mismatch, not installing: $relative" }
        $checked++
    }
    Write-Output "Checked $checked program files against SHA256SUMS.txt."
} else {
    Write-Output 'SHA256SUMS.txt not found next to the package: skipping the file check (WinDictate still checks its speech model at every start).'
}

# 2. Stop a running copy, so its files can be replaced.
$installedExe = Join-Path $Destination 'WinDictate.exe'
if (Test-Path -LiteralPath $installedExe) {
    & $installedExe --exit | Out-Null
}

# 3. Copy the program.
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $Destination -Recurse -Force

# 4. Start Menu shortcut.
$programs = if ($AllUsers) { [Environment]::GetFolderPath('CommonPrograms') } else { [Environment]::GetFolderPath('Programs') }
$shortcut = Join-Path $programs "$productName.lnk"
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $installedExe
$link.WorkingDirectory = $Destination
$link.Description = 'Local push-to-talk dictation (restricted pilot)'
$link.Save()

$version = (Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion
Write-Output "Installed $productName $version to $Destination"
Write-Output "Start Menu shortcut: $shortcut"
Write-Output 'Not added: start-up entry, service, scheduled task. Start WinDictate from the Start Menu.'
