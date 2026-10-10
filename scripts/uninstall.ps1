<#
.SYNOPSIS
  Removes Insomnia for the current user, undoing anything it still has journaled.

.DESCRIPTION
  Quits the app (which ends any session), runs `Insomnia.exe --restore` to
  undo every journaled change (power-plan lid action, Energy Saver threshold,
  suspended apps, muted outputs), removes the recovery task, the sign-in
  entry, the Start menu shortcut and the program folder. Settings and logs
  in %LOCALAPPDATA%\Insomnia stay unless you pass -Purge. If the restore
  reports a failure, nothing is deleted.
#>
param([switch]$Purge)
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:LOCALAPPDATA 'Programs\Insomnia'
$exe = Join-Path $dir 'Insomnia.exe'
$data = Join-Path $env:LOCALAPPDATA 'Insomnia'

if (Test-Path $exe) {
    if (Get-Process -Name Insomnia -ErrorAction SilentlyContinue) {
        Write-Host 'Asking Insomnia to quit...'
        & $exe --quit | Out-Null
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Process -Name Insomnia -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
        if (Get-Process -Name Insomnia -ErrorAction SilentlyContinue) {
            throw 'Insomnia did not quit. Quit it from the tray menu (it may be asking about an incomplete restore), then run this again.'
        }
    }
    Write-Host 'Undoing anything still journaled...'
    $out = & $exe --restore | Out-String
    Write-Host $out.Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "The restore did not finish (exit $LASTEXITCODE). Nothing was removed. Fix the item above and run this again; state.json in $data lists what is left."
    }
} else {
    Write-Host "No Insomnia.exe at $exe; skipping the restore."
}

# Through cmd so a "not found" on stderr is not a terminating error under Windows PowerShell 5.
cmd.exe /c 'schtasks /Delete /TN "Insomnia Recovery" /F >nul 2>&1' | Out-Null
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Insomnia' -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('Programs')) 'Insomnia.lnk') -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Removed $dir, the recovery task, the sign-in entry and the shortcut."

if ($Purge) {
    Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Removed settings and logs ($data)."
} else {
    Write-Host "Settings and logs kept in $data (pass -Purge to remove them)."
}
