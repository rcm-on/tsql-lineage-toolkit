# Test de auditoría (regresión ejecutable de `docs/auditor-challenge.md`)

`docs/claude-audit-report.md` y `docs/gemini-audit-report.md` son prosa: afirmaciones con
cifras citadas a mano sobre `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad` (WWI)
y su impacto en `Website.Customers`/`Website.Suppliers` vía `lineage_path.json`. `verify.mjs`
re-deriva esas mismas cifras del NodeStore real cada vez que se ejecuta, así que un cambio
futuro (Tarea A/AdventureWorks, el fix del gap 5.2 de `docs/extraction-gaps.md`, una
regeneración de `out/`) lo rompe de forma audible en vez de dejar los informes desactualizados
en silencio — el mismo problema que ya tuvimos una vez con `docs/column-lineage-measurement.md`.

## Ejecutar

```bash
# desde tsql-lineage-toolkit/ (requiere out/graph_full.nodes ya generado con --nodestore)
node eval/auditor-challenge/verify.mjs [ruta-al-nodestore]   # por defecto out/graph_full.nodes
```

## Qué comprueba

| Check | Qué valida |
|---|---|
| `cyclomatic_complexity == 19` | El hotspot #3 del informe sigue siendo el mismo procedimiento con la misma complejidad. |
| `unresolved_dynamic_sql_steps == 0` | Los fixes de SQL dinámico (gap 5.1 `QUOTENAME` + gap 5.2 `NCHAR`/`CASE`/`COALESCE`, `AstWalker.ResolveLiteral`) no retroceden: los 34 pasos resuelven a texto literal. |
| `WRITES_TO` == exactamente las 17 tablas conocidas | El riesgo "¿hay una tabla 18ª oculta en SQL dinámico?" sigue descartado. |
| `Website.Customers` 14/14, `Website.Suppliers` 12/12 | El 100% del impacto en los portales externos, vía `lineage_path.json`, se mantiene. |
| `Website.VehicleTemperatures` 0/6 | El caso negativo (vista NO afectada, raíz `warehouse.vehicletemperatures` distinta de `coldroomtemperatures`) sigue siendo cierto. |

Si alguno de estos checks falla tras un cambio, **el informe de auditoría está desactualizado**
y hay que revisar `docs/claude-audit-report.md`/`docs/gemini-audit-report.md`, no solo el código.
