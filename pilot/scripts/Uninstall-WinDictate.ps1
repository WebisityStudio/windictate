<#
.SYNOPSIS
    Removes WinDictate Restricted Pilot completely.

.DESCRIPTION
    1. Asks a running WinDictate to exit (WinDictate.exe --exit), and stops it
       if it has not exited within 10 seconds.
    2. Removes the program folder (program, libraries and speech model).
    3. Removes the Start Menu shortcut.
    4. Removes start-up entries for WinDictate if any exist (this build never
       creates one, but a deployment package or an earlier build might have).
    5. Removes the user's settings and log (%LOCALAPPDATA%\WinDictate), unless
       -KeepUserData is given. WinDictate never writes audio or transcripts, so
       there is no dictated content to remove.

    Use the same -AllUsers / -Destination as for the install.

.EXAMPLE
    .\Uninstall-WinDictate.ps1
.EXAMPLE
    .\Uninstall-WinDictate.ps1 -AllUsers
#>
[CmdletBinding()]
param(
    [switch] $AllUsers,
    [string] $Destination,
    [switch] $KeepUserData
)

$ErrorActionPreference = 'Stop'
$productName = 'WinDictate Restricted Pilot'
if (-not $Destination) {
    $Destination = if ($AllUsers) { Join-Path $env:ProgramFiles $productName }
                   else { Join-Path $env:LOCALAPPDATA "Programs\$productName" }
}
$exe = Join-Path $Destination 'WinDictate.exe'
$removed = New-Object System.Collections.Generic.List[string]

# 1. Exit the running copy.
if (Test-Path -LiteralPath $exe) {
    $exit = Start-Process -FilePath $exe -ArgumentList '--exit' -PassThru -Wait -WindowStyle Hidden
    if ($exit.ExitCode -ne 0) {
        Get-Process -Name 'WinDictate' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $exe } |
            Stop-Process -Force
    }
}

# 2. Program folder.
if (Test-Path -LiteralPath $Destination) {
    Remove-Item -LiteralPath $Destination -Recurse -Force
    $removed.Add("program folder $Destination")
}

# 3. Start Menu shortcut.
$programs = if ($AllUsers) { [Environment]::GetFolderPath('CommonPrograms') } else { [Environment]::GetFolderPath('Programs') }
$shortcut = Join-Path $programs "$productName.lnk"
if (Test-Path -LiteralPath $shortcut) {
    Remove-Item -LiteralPath $shortcut -Force
    $removed.Add("Start Menu shortcut $shortcut")
}

# 4. Start-up entries, if anything ever created one.
foreach ($key in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
                 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run') {
    # Test-Path and the key's own list of values never raise an error when nothing is there.
    if ((Test-Path -LiteralPath $key) -and ((Get-Item -LiteralPath $key).GetValueNames() -contains 'WinDictate')) {
        Remove-ItemProperty -LiteralPath $key -Name 'WinDictate'
        $removed.Add("start-up entry $key\WinDictate")
    }
}
$startupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) "$productName.lnk"
if (Test-Path -LiteralPath $startupShortcut) {
    Remove-Item -LiteralPath $startupShortcut -Force
    $removed.Add("start-up shortcut $startupShortcut")
}

# 5. Settings and log.
$data = Join-Path $env:LOCALAPPDATA 'WinDictate'
if (-not $KeepUserData -and (Test-Path -LiteralPath $data)) {
    Remove-Item -LiteralPath $data -Recurse -Force
    $removed.Add("settings and log $data")
}

if ($removed.Count -eq 0) { Write-Output "$productName was not installed for this scope; nothing to remove." }
else { $removed | ForEach-Object { Write-Output "Removed $_" } }

# Confirm nothing is left behind.
$mustBeGone = @($Destination, $shortcut)
if (-not $KeepUserData) { $mustBeGone += $data }
$left = @($mustBeGone | Where-Object { Test-Path -LiteralPath $_ })
if ($left.Count -gt 0) { throw "Still present after uninstall: $($left -join ', ')" }
Write-Output 'Uninstall complete. No audio or transcript files exist to remove: WinDictate never writes them.'
