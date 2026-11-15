# Plan de Wami Splat Optimizer / roadmap

Objetivo: el runtime de Gaussian Splatting más rápido posible en Unity (y portable a otros motores), con VR como caso
principal (Quest 2 autónomo y PC VR), tomando como referencia lo que hace rápido a PlayCanvas
(`engine/src/scene/gsplat-unified/` y `supersplat-viewer`).

*Goal: the fastest possible Gaussian Splatting runtime in Unity (portable to other engines), VR first.*

## Por qué PlayCanvas iba más rápido / Why PlayCanvas was faster

| Causa | gsplat original | PlayCanvas | Estado en SplatKit |
|---|---|---|---|
| Ordenar | todos los splats, **cada cuadro** (`SortMode.Always`) | solo si la cámara se movió | ✅ `OnCameraMove` por cámara |
| Profundidad para ordenar | cada cuadro aunque no se ordene | — | ✅ solo al ordenar |
| Bits de la clave | 32 (4 pasadas) | 10-20 | ✅ 16 (2 pasadas) |
| Girar la cabeza (VR) | reordena; el LOD rehacía la selección cada 2° | orden radial; LOD sin depender del giro en XR | ✅ orden radial + LOD VR estable (20°) |
| Primer orden | aleatorio (orden sin inicializar al calcular profundidad) | — | ✅ corregido |
| Píxeles | escala 1,0 + HDR | 0,8 en visores autónomos, 0,5 en "rendimiento" | ✅ escala por perfil + automática |
| Descartes | < 2 px | `minPixelSize`, `minContribution`, foveación | ✅ tamaño y contribución mínimos · ⏳ foveación |
| SH y covarianza | por vértice ×4, cada cuadro | *work buffer* por splat, color por ángulo | ⏳ F5 |
| Antialias (Mip-Splatting) | no | sí | ✅ compensación de opacidad |

## Fases / Phases

| Fase | Contenido | Estado |
|---|---|---|
| F0 | Banco reproducible: legacy vs perfiles, recorridos en movimiento (p50/p95/p99), giro de cabeza, PSNR/SSIM contra referencia | ✅ (`SplatBench`, `splatkit comparar`) · ⏳ medir en build y en el visor |
| F1 | Extracción: paquete Python + paquete UPM, independiente de cualquier proyecto | ✅ |
| F2 | Perfiles de calidad compartidos + español/inglés | ✅ |
| F3 | Arreglos de orden y LOD para VR | ✅ |
| F4 | Pase de splats a resolución reducida **solo para los splats** + composición en gamma (quita los halos de `Gamma To Linear`) | ⏳ hoy la resolución se baja para todo el cuadro (como el visor de PlayCanvas) |
| F5 | *Work buffer* por splat (compute): covarianza 2D y color SH una vez por splat, color en caché por ángulo; foveación | ⏳ |
| F6 | Formato por trozos con origen local (precisión), carga por zonas y presupuesto de VRAM; sin el límite de 2 GB | ⏳ |
| F7 | Orden en CPU (Burst, por conteo, asíncrono) frente a GPU, por plataforma | ⏳ |
| F8 | Calidad automática por tiempo de GPU | ✅ primera versión (`SplatQuality`) · ⏳ afinar en el visor |
| F9 | Calidad offline: destilar los niveles de la pirámide, informe por perfil | ⏳ |
| F10 | Validación en Quest 2 (APK) y Vive Pro + comparación final con PlayCanvas + publicación | ⏳ |
| F11 | Otros motores (lector web, Godot) | opcional |

## Qué dicen las medidas (2026-11-15) / What the measurements say

- El tiempo de GPU lo domina el **relleno** (píxeles × capas), no la ordenación: a 600 k splats ordenar con 16 bits
  ahorra ~1 ms; bajar la resolución al 70 % ahorra ~35-40 % del cuadro (~65 % del coste de los splats).
- Los niveles gruesos de la pirámide engordan los splats: 150 k gruesos ≈ 600 k finos en coste. Prioridad para Quest:
  **F9** (destilar los niveles con límite de tamaño) y **F4** (pase de splats reducido sin
  emborronar los objetos).
- **F5** (*work buffer*) importa sobre todo en el Quest (vértices ×4 y SH por vértice).
- Medir siempre con la GPU libre: RealityScan abierto dobló los tiempos de una tanda.

## Pruebas con escenas pesadas / Heavy test scenes

- **Iglesia** de internet (9,98 M splats, SH3, interior; licencia CC BY-NC-ND: solo local, nunca en el repo).
- **Árbol del Jardín Botánico** (1 M splats, SH3, entrenado con antialias; vegetación = mucho relleno sin núcleos
  opacos).

Antes de cada banco: que no haya otro trabajo de GPU (entrenamientos, RealityScan).
