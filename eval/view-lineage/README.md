# Cross-check de lineage de VISTAS (oráculo = SQL Server)

Valida la extracción de lineage de **vistas** del toolkit contra un **ground-truth
autoritativo calculado por el propio SQL Server** sobre las bases de datos de
muestra ya restauradas (`database/*.bak`: WideWorldImporters, AdventureWorks2019).

A diferencia de [`bad-practices`](../bad-practices/) (corpus sintético para el rule
engine), aquí el corpus son **vistas reales** y la respuesta correcta la da SQL
Server, no nosotros.

## Por qué SQL Server y no un parser externo

El primer intento usó `node-sql-parser` (gramática JS independiente) como oráculo:
**solo parseó 11 de 24** vistas — se atraganta justo con lo más jugoso de T-SQL
(`CROSS/OUTER APPLY`, `PIVOT`, métodos XML, `UNION`). No es un oráculo fiable para
T-SQL. SQL Server resuelve dependencias de todo el dialecto vía DMV nativas, así que
es el oráculo correcto:

| Métrica | Fuente (oráculo) | Debe igualar en el toolkit |
|---|---|---|
| `out_cols`   columnas de SALIDA | `sys.columns`                      | nodos `:Column` propios de la vista |
| `src_cols`   columnas FUENTE    | `sys.dm_sql_referenced_entities` (minor_id>0) | `READS_COLUMN` + `FILTERS_ON` |
| `src_tables` tablas base        | idem (entidades distintas)         | `READS_FROM` |

## Archivos

| Archivo | Qué es |
|---|---|
| `extract-truth.sql` | Genera el ground-truth desde SQL Server (cursor sobre `sys.views`). |
| `ground-truth.csv`  | Ground-truth ya extraído: 23 vistas (3 WWI + 20 AdventureWorks). |
| `crosscheck.mjs`    | Compara el nodestore (`out/graph_full.nodes`) vs `ground-truth.csv`. Sale `!=0` si hay discrepancias. |

## Cómo ejecutarlo

```bash
# (opcional) regenerar ground-truth si cambian las BD de muestra:
sqlcmd -S localhost\SQLEXPRESS -E -C -d WideWorldImporters -h-1 -s"," -W -i extract-truth.sql
sqlcmd -S localhost\SQLEXPRESS -E -C -d AdventureWorks2019  -h-1 -s"," -W -i extract-truth.sql

# correr el cross-check contra el nodestore actual:
node crosscheck.mjs            # o: node crosscheck.mjs <ruta_al_nodestore>
```

## Estado actual (lo que destapó este eval)

```
VIEW                          src(tk/gt)   tbl(tk/gt)   out(tk/gt)
Website.Customers             OK  24/24    OK  6/6      DIFF 0/14
Website.Suppliers             OK  20/20    OK  5/5      DIFF 0/12
Website.VehicleTemperatures   OK  8/8      OK  1/1      DIFF 0/6
```

**Veredicto:**

- ✅ **Extracción de columnas FUENTE: completa y exacta.** `READS_COLUMN + FILTERS_ON`
  iguala al milímetro lo que el propio resolvedor de dependencias de SQL Server
  reporta (52/52 en las 3 vistas de WWI). La ingesta no pierde lecturas.
- ❌ **Columnas de SALIDA: 0 modeladas de 32 esperadas (WWI); 290 en total con
  AdventureWorks.** El toolkit no materializa las columnas de salida de la vista
  como nodos `:Column`, ni emite `DERIVES_FROM (via_view)` salida→fuente. La feature
  existe en código ([`AstWalker.ViewColumnLineage`](../../src/TSqlParser/AstWalker.cs),
  [`GraphExporter.BuildViewLineage`](../../src/TSqlParser/GraphExporter.cs)) — el grafo
  de demo tiene 34 aristas `via_view` — pero **no dispara para estas vistas reales**
  (6 `LEFT OUTER JOIN`, schemas con corchetes `[Application]`, `CROSS APPLY`...).

**Impacto en el motor de impacto (prioridad nº1):** sin columnas de salida no hay
provenance a nivel columna en vistas. P.ej. `Website.Customers.PrimaryContact` y
`AlternateContact` salen **ambas** de `Application.People.FullName` (vía dos JOINs con
alias `pp`/`ap`); sin nodo de salida, `@col_provenance` no las distingue ni resuelve.

## Siguiente paso

1. Depurar `ViewColumnLineage` con JOINs múltiples / `APPLY` / corchetes hasta que
   `crosscheck.mjs` quede en verde para WWI (out 14/12/6).
2. Procesar AdventureWorks2019 por el pipeline para activar las otras 20 filas del
   ground-truth (cobertura: 290 columnas de salida).
3. Decidir la jerarquía del contenedor de esas columnas (HAS_COLUMN desde el
   `SqlObject` de la vista, sin crear un `:Table` paralelo).
