# Changelog

## [0.1.0] - 2026-11-14

Primera versión de SplatKit como paquete propio, a partir de gsplat-unity v1.4.0 (`a2bf458`, MIT; su changelog está en
`Documentation~/gsplat/`). Todos los cambios sobre gsplat llevan el comentario `SplatKit:` en el código.

First SplatKit release as its own package, based on gsplat-unity v1.4.0. Every change to gsplat is marked `SplatKit:`.

### Rendimiento / Performance
- `GsplatSortMode.OnCameraMove` (por defecto): solo ordena cuando la cámara que dibuja se movió (umbrales de posición y
  giro), cambió la selección o dibuja otra cámara. El original ordenaba todos los splats en cada cuadro.
  *Sorts only when the rendering camera moved, the selection changed or another camera rendered last.*
- La profundidad de ordenación solo se calcula para los renderers que ordenan ese cuadro.
- Claves de 16 bits cuantizadas (`SortKeyBits = 16`): 2 pasadas de radix en vez de 4.
- Claves radiales (`SortKey = Radial`): girar la cabeza en VR no cambia el orden.
- `GsplatQuality.MinPixelSize` / `MinContribution` (alfa × área en píxeles), como `minPixelSize` / `minContribution`
  de PlayCanvas.
- `GsplatRenderer.ExternalTick` + `Tick()`: el dueño decide cuándo se encola el dibujo (bancos, bucles propios).

### Calidad / Quality
- `GsplatRenderer.AntiAlias`: compensación de opacidad para escenas entrenadas con antialias (Mip-Splatting,
  LichtFeld `--enable-mip`).

### SplatKit
- `SplatLod` (API en inglés con `FormerlySerializedAs`): presupuesto 0 = sin límite, modo VR
  estable (margen de giro ancho, trozos del margen más gruesos, selección cada 20° de giro), contador de selecciones.
- `SplatQuality`: aplica un perfil de `Resources/SplatKitProfiles.json` (el mismo de la cadena python), escala de
  render de URP y calidad automática (presupuesto y resolución según el tiempo de GPU).
- `SplatProfileSet` / `SplatLang`: perfiles y textos en español e inglés.
- `SplatBench` (editor): banco en batchmode con perfiles, modo `-benchLegacy`, escala de resolución y recorrido en
  movimiento (p50/p95/p99, ordenaciones, selecciones, dibujos).
- `SplatKitPython` (editor): localiza la cadena `python -m splatkit` para ventanas de importación.
- `FlyCamera`: cámara de vuelo de escritorio.
