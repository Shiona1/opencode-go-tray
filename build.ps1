$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Join-Path $root 'src\Program.cs'
$manifest = Join-Path $root 'src\app.manifest'
$icon = Join-Path $root 'assets\icon.ico'
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'OpenCodeGoTray.exe'

if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }
if (-not (Test-Path $src)) { throw "Source not found: $src" }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Web.Extensions.dll',
    'System.Net.Http.dll'
) | ForEach-Object { '/reference:' + (Join-Path $framework $_) }

$args = @(
    '/nologo',
    '/target:winexe',
    '/codepage:65001',
    '/optimize+',
    '/platform:anycpu',
    "/out:$out"
) + $refs

if (Test-Path $manifest) { $args += "/win32manifest:$manifest" }
if (Test-Path $icon) { $args += "/win32icon:$icon" }

$args += $src

& $csc @args
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

$info = Get-Item $out
Write-Host ("Built: {0} ({1:N0} bytes)" -f $out, $info.Length)
