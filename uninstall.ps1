$ErrorActionPreference = 'Stop'

$dest = Join-Path $env:LOCALAPPDATA 'OpenCodeGoTray'

Get-Process OpenCodeGoTray -ErrorAction SilentlyContinue | Stop-Process -Force

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $runKey -Name 'OpenCodeGoTray' -ErrorAction SilentlyContinue

$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'OpenCode Go Tray.lnk'
if (Test-Path $shortcutPath) { Remove-Item -LiteralPath $shortcutPath -Force }

if (Test-Path $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }

Write-Host 'OpenCode Go Tray удалён.'
