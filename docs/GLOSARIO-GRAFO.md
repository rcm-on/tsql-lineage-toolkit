---
title: Glosario del grafo
description: Nodos, aristas, ids, dirección actor->recurso, granularidad de Step y valores de resolution.
read_when: Al leer o escribir código que construye o consulta el grafo (exportadores, MCP, dashboard).
related: [docs/ARQUITECTURA.md]
stability: durable
updated: 2026-08-21
---

# Glosario del grafo

- **Nodos**: `SqlObject`, `Table`, `Column`, `Step`, `Action`, `Variable`, `Rule`,
  `Workflow`... más el lado de aplicación (`AppMethod`, `EntryPoint`, `ExternalTarget`...).
  El vocabulario cerrado está en `Vocab.KnownNodeLabels`.
- **Aristas**: apuntan **actor → recurso** (llamador→llamado, lector→tabla, escritor→tabla,
  columna derivada→columna fuente). De ahí que "downstream" (qué se rompe si cambio esto)
  camine las aristas hacia atrás y "upstream" (de qué depende) hacia delante.
- **Ids**: `SqlObject` = `Db::esquema.objeto`; `Table` = `Db:table:esquema.tabla` (minúsculas);
  `Column` = `...:column:Col`; `Step` = `<objId>#stepN`.
- **Granularidad de Step**: `READS_FROM`, `WRITES_TO`, `READS_COLUMN` y `WRITES_COLUMN`
  salen de un `Step`, no del `SqlObject`. Cualquier agregación por objeto tiene que
  enrollar el step a su dueño (`StoreSchema.RollUpStep`). `CALLS` es la excepción: sale
  directamente del `SqlObject`.
- **Valores de `resolution`**: `direct` / `star_expanded` / `via_view`. El contrato
  completo entre productor (`SqliteExporter`) y consumidor (`McpTools`,
  `scripts/lineage-queries.sql`) vive en `Parser.Contracts/StoreSchema.cs` — ver
  `docs/ARQUITECTURA.md` §3.
