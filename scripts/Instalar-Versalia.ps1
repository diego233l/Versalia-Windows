# Instala Versalia para tu usuario (no hace falta ser administrador).
$ErrorActionPreference = 'Stop'
$origen = Join-Path $PSScriptRoot 'Versalia.exe'
if (-not (Test-Path $origen)) { throw "No encuentro Versalia.exe junto a este script." }

$destino = Join-Path $env:LOCALAPPDATA 'Programs\Versalia'
Get-Process -Name Versalia -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force -Path $destino | Out-Null
Copy-Item $origen (Join-Path $destino 'Versalia.exe') -Force
Unblock-File (Join-Path $destino 'Versalia.exe')
$actualizador = Join-Path $PSScriptRoot 'Actualizar-Versalia.ps1'
if (Test-Path $actualizador) { Copy-Item $actualizador (Join-Path $destino 'Actualizar-Versalia.ps1') -Force }

$menuInicio = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Versalia.lnk'
$shell = New-Object -ComObject WScript.Shell
$acceso = $shell.CreateShortcut($menuInicio)
$acceso.TargetPath = Join-Path $destino 'Versalia.exe'
$acceso.WorkingDirectory = $destino
$acceso.Save()

Start-Process (Join-Path $destino 'Versalia.exe')
Write-Host ''
Write-Host 'Versalia instalado. Busca el icono verde con una V junto al reloj (pulsa ^ si no lo ves).'
Write-Host 'Atajos: Ctrl+Alt+V escenario en todas las pantallas (Esc para salir), Ctrl+Alt+L letra flotante.'
