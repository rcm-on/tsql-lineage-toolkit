---
title: Índice de agente
description: Punto de entrada a la documentación — qué leer según lo que necesites, y su tamaño.
read_when: Siempre, como primer fichero de docs/ al empezar una sesión o tarea.
related: [AGENTS.md]
stability: durable
updated: 2026-08-19
---

# Índice de agente

Este es el único fichero que conviene leer siempre. Cada fila lleva su tamaño en líneas
para decidir si merece la pena abrirlo entero o solo la sección que toca. Todos los
ficheros de `docs/` llevan frontmatter YAML (`description`, `read_when`, `stability`) por
si tu herramienta puede filtrar por ahí en vez de leer esta tabla a mano.

| Si necesitas... | Lee | Líneas | Estabilidad |
|---|---|---:|---|
| Qué es el producto y su objetivo real | `docs/PROYECTO.md` | ~45 | durable |
| Qué proyecto depende de cuál, dónde va un fichero nuevo, el contrato del store, el MCP | `docs/ARQUITECTURA.md` | ~100 | durable |
| Vocabulario del grafo: nodos, aristas, ids, granularidad de Step, `resolution` | `docs/GLOSARIO-GRAFO.md` | ~27 | durable |
| Qué patrón de diseño va en cada sitio, y qué se descartó a propósito | `docs/PATRONES.md` | ~31 | durable |
| Los 4 comandos de 30s para validar un cambio, y las trampas del entorno | `docs/VERIFICACION.md` | ~37 | durable |
| Checklist completo de verificación (corpus, `validate`, capturas, higiene de git) | `docs/guia-de-verificacion.md` | ~370 | durable |
| Reglas de proceso: gates, mutación, checkpoints, bitácora, autoría | `docs/CONVENCIONES.md` | ~30 | durable |
| Qué pasó en cada sesión, lo más reciente arriba | `docs/BITACORA.md` | ~80 | volatile |
| Qué queda por hacer, en qué orden, con ruta y gate por paso | `docs/plan-arquitectura.md` | ~365 | volatile |

## Fuera de este índice

El resto de `docs/` son entregables y referencias puntuales, no arranque de sesión:
comparativas de parsers, informes de auditoría, capturas del dashboard, guiones de
ejecución de corpus concretos (`docs/ejecucion-canonica.md`, `docs/corpus-multibase.md`,
`docs/lineage-corpus.md`). Ábrelos solo si el fichero que estás leyendo te remite a ellos.
