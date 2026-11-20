$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'dist\OpenCodeGoTray.exe'
if (-not (Test-Path $exe)) {
    & (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась' }
}

$dest = Join-Path $env:LOCALAPPDATA 'OpenCodeGoTray'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Остановить запущенный виджет, иначе Windows не даст заменить exe
$running = Get-Process OpenCodeGoTray -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    foreach ($process in $running) {
        try { $process.WaitForExit(3000) | Out-Null } catch { }
    }
    Start-Sleep -Milliseconds 400
}

Copy-Item -LiteralPath $exe -Destination (Join-Path $dest 'OpenCodeGoTray.exe') -Force

# Автозапуск при входе
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Set-ItemProperty -Path $runKey -Name 'OpenCodeGoTray' -Value ('"' + (Join-Path $dest 'OpenCodeGoTray.exe') + '"')

# Ярлык в меню «Пуск»
$programs = [Environment]::GetFolderPath('Programs')
$shortcutPath = Join-Path $programs 'OpenCode Go Tray.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $dest 'OpenCodeGoTray.exe'
$shortcut.WorkingDirectory = $dest
$shortcut.Description = 'Лимиты подписки OpenCode Go в трее'
$shortcut.Save()

Write-Host "Установлено в: $dest"
Write-Host 'Автозапуск: включён (HKCU Run)'
Write-Host ''
Write-Host 'Дальше: запустите виджет командой:'
Write-Host ('  Start-Process "' + (Join-Path $dest 'OpenCodeGoTray.exe') + '"')

