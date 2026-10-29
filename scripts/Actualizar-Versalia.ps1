# Descarga la última versión de Versalia desde GitHub (repositorio privado) y la instala encima de la actual.
param([string]$Tag = 'dev-latest')
$ErrorActionPreference = 'Stop'
$repo = 'diego233l/Versalia-Windows'

# 1) GitHub CLI: es lo que permite descargar de un repositorio privado.
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe' }
if (-not (Test-Path $gh)) {
    Write-Host 'Instalando GitHub CLI (solo la primera vez)...'
    winget install -e --id GitHub.cli --accept-source-agreements --accept-package-agreements
    $gh = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'
}

# 2) Iniciar sesión (solo la primera vez: se abre el navegador).
& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) { & $gh auth login --web --git-protocol https }

# 3) Descargar e instalar.
$tmp = Join-Path $env:TEMP 'versalia-update'
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
Write-Host "Descargando $Tag..."
& $gh release download $Tag -R $repo -p 'Versalia-Windows-x64.zip' -D $tmp --clobber
if ($LASTEXITCODE -ne 0) { throw 'No se pudo descargar la versión.' }
Expand-Archive (Join-Path $tmp 'Versalia-Windows-x64.zip') (Join-Path $tmp 'x') -Force
& (Join-Path $tmp 'x\Instalar-Versalia.ps1')
