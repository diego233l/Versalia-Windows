Get-Process -Name Versalia -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA 'Programs\Versalia') -ErrorAction SilentlyContinue
Remove-Item -Force (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Versalia.lnk') -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Versalia -ErrorAction SilentlyContinue
Write-Host 'Versalia desinstalado. Tus ajustes y la caché siguen en %LOCALAPPDATA%\Versalia (puedes borrar esa carpeta).'
