# Versalia para Windows 11

Letra de la canción que suena (Apple Music, Spotify, navegador…) con cada palabra iluminada justo cuando se canta,
pensada para **varias pantallas**. Es el port a Windows de [Versalia para macOS](https://github.com/diego233l/Versalia).

## Instalar (PowerShell, una sola línea)

1. En la página de *Releases* de este repositorio descarga `Versalia-Windows-x64.zip` desde el navegador
   (el repositorio es privado, así que `curl` no funciona).
2. Abre PowerShell y pega:

```powershell
cd ~\Downloads; Unblock-File .\Versalia-Windows-x64.zip; Expand-Archive .\Versalia-Windows-x64.zip -DestinationPath .\Versalia-Windows -Force; powershell -ExecutionPolicy Bypass -File .\Versalia-Windows\Instalar-Versalia.ps1
```

Aparece un icono verde con una **V** junto al reloj (pulsa `^` si está oculto).

## Cómo se usa

| Atajo | Qué hace |
|---|---|
| `Ctrl+Alt+V` (o clic en el icono) | **Escenario «Dúo»** en todas las pantallas. `Esc` para salir. |
| `Ctrl+Alt+L` | **Letra flotante** transparente sobre una pantalla (los clics la atraviesan). |

Clic derecho en el icono: colores, sincronía (±100 ms), pantalla de la letra flotante, iniciar con Windows, carpeta de registros.

## El modo «Dúo» (multipantalla)

Las pantallas se tratan como **un solo lienzo continuo**.

- **Pantalla grande (tu 4K):** portada que respira con la música, título, una *constelación* con las palabras de la frase
  (cada palabra cantada enciende una estrella y la une a la anterior) y polvo de estrellas.
- **Pantalla secundaria (tu 1080p):** la letra palabra a palabra, con la línea anterior y las siguientes difuminadas.
- **La magia:** cada palabra que se canta lanza un **cometa** desde su estrella en la 4K, que cruza el borde entre
  monitores y aterriza en esa palabra en la 1080p con una onda de luz.
- Los colores salen de la portada (o eliges una paleta). Con 1 monitor todo se junta; con 3 o más, los extras
  muestran el fondo y los cometas pasan por ellos.
- Si cambias la resolución o conectas/desconectas una pantalla, se recoloca solo.

## Diferencias con la versión de macOS

- **Pantalla de bloqueo:** Windows no permite a una app dibujar sobre la pantalla de bloqueo, así que no existe ese modo.
  Todo funciona con el escritorio desbloqueado.
- **Reproductor:** se lee del sistema (controles multimedia de Windows), así que sirve para Apple Music para Windows,
  Spotify, etc. sin permisos extra.
- Portados: Dúo, Aurora, Neón, Póster y Órbita (menú «Estilo de letra»). Pendiente: lente, muro 3D y letras locales `.lrc`/TTML.

## Registros

`%LOCALAPPDATA%\Versalia\logs\versalia.log` (también desde el menú del icono).

## Compilar

`dotnet test tests/Versalia.Core.Tests` y `dotnet publish src/Versalia.App -c Release -r win-x64`. Lo hace GitHub Actions
en cada push (`.github/workflows/build.yml`).
