<#
.SYNOPSIS
  Installs Insomnia for the current user. No administrator rights needed.

.DESCRIPTION
  Copies Insomnia.exe to %LOCALAPPDATA%\Programs\Insomnia, adds a Start menu
  shortcut, optionally registers launch at sign-in, and starts the app.
  An upgrade asks a running copy to quit first (which ends its session and
  undoes everything), and refuses to continue if it will not.

.PARAMETER Exe
  Path to Insomnia.exe. Defaults to the one next to this script, then dist\release.

.PARAMETER LaunchAtLogin
  Start Insomnia when you sign in.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install.ps1 -LaunchAtLogin
#>
param(
    [string]$Exe,
    [switch]$LaunchAtLogin,
    [switch]$NoStart
)
$ErrorActionPreference = 'Stop'

if (-not $Exe) {
    foreach ($candidate in @("$PSScriptRoot\Insomnia.exe", "$PSScriptRoot\..\dist\release\Insomnia.exe")) {
        if (Test-Path $candidate) { $Exe = (Resolve-Path $candidate).Path; break }
    }
}
if (-not $Exe -or -not (Test-Path $Exe)) {
    throw "Insomnia.exe not found. Pass -Exe <path>, or build it first (scripts\build.ps1)."
}

$dir = Join-Path $env:LOCALAPPDATA 'Programs\Insomnia'
$target = Join-Path $dir 'Insomnia.exe'

# Upgrade: the running app must quit (ending its session) before the exe is replaced.
if (Get-Process -Name Insomnia -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target }) {
    Write-Host 'Asking the running Insomnia to quit...'
    $reply = & $target --quit | Out-String
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Process -Name Insomnia -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target }) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
    }
    if (Get-Process -Name Insomnia -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target }) {
        throw "Insomnia did not quit ($($reply.Trim())). Quit it from the tray menu, then run the installer again."
    }
}

New-Item -ItemType Directory -Force -Path $dir | Out-Null
$staging = "$target.new"
Copy-Item -LiteralPath $Exe -Destination $staging -Force
Unblock-File -LiteralPath $staging -ErrorAction SilentlyContinue
Move-Item -LiteralPath $staging -Destination $target -Force
Write-Host "Installed $target"

# Start menu shortcut
$programs = [Environment]::GetFolderPath('Programs')
$lnk = Join-Path $programs 'Insomnia.lnk'
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = $target
$s.WorkingDirectory = $dir
$s.Description = 'Keep this PC awake for a set time, lid closed or not'
$s.Save()
Write-Host "Start menu shortcut: $lnk"

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($LaunchAtLogin) {
    New-ItemProperty -Path $run -Name 'Insomnia' -Value "`"$target`"" -PropertyType String -Force | Out-Null
    Write-Host 'Launches at sign-in.'
}

# The recovery task from an older install points at the old path; the app re-creates it at the next session.
if (-not $NoStart) {
    Start-Process -FilePath $target
    Write-Host 'Insomnia is running: look for the eye in the system tray (you may need to drag it out of the ^ overflow).'
}
Write-Host ''
Write-Host 'Before the first session, open Settings from the tray icon''s right-click menu and review the lid-close actions.'
Write-Host "Command line: `"$target`" --start 4h | --extend 30m | --end | --status"
