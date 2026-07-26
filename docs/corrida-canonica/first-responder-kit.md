# T-SQL Lineage Toolkit contra SQL Server First Responder Kit (Brent Ozar)

Fecha: 2026-07-26
Entorno: worktree `C:\Temp\canon-master` (branch `docs/corrida-canonica`, commit `3a85c9b`), motor SIN tocar.
Fuente FRK: `C:\temp\corpus-frk-src` (clon `--depth 1` de BrentOzarULTD/SQL-Server-First-Responder-Kit, no copiado al repo del toolkit).

## Resumen ejecutivo

**0 errores de parseo en los 12 objetos**, incluido `sp_Blitz` (480 KB, complejidad ciclomática 706 —
37x el máximo observado en WideWorldImporters). Los tres formatos de salida (`graph_full.json`, SQLite,
NodeStore) coinciden exactamente en nodos y aristas, y `index.json.stats` reporta `unknown_labels`,
`unknown_edge_types` y `orphan_edges` todos vacíos/cero. El parser aguanta el salto de escala sin romperse.

El hallazgo real no es un error de parseo sino un **límite honesto y ya instrumentado**: de 241 pasos de
SQL dinámico detectados, 164 (68%) quedan sin resolver a texto literal — mayoritariamente construcción de
nombres de objeto cross-database vía `QUOTENAME(@DatabaseName)`, que no puede resolverse estáticamente. El
propio índice del NodeStore documenta esto como "fail closed" y lo cuenta por objeto
(`unresolved_dynamic_sql_steps`), así que no es un gap oculto.

## Cifras

### Selección de ficheros (12 objetos, exclusión de Install-All-Scripts.sql, Install-Azure.sql, Uninstall.sql)

| Fichero | Tamaño | Líneas | EXEC/EXECUTE (regex) | sp_executesql / EXEC(@..) (regex) |
|---|---|---|---|---|
| sp_Blitz.sql | 480 KB | 10.659 | 132 | 59 |
| sp_BlitzIndex.sql | 428 KB | 7.845 | 59 | 46 |
| sp_BlitzCache.sql | 400 KB | 8.762 | 67 | 24 |
| sp_BlitzFirst.sql | 292 KB | 5.422 | 74 | 52 |
| sp_BlitzLock.sql | 180 KB | 4.816 | 27 | 20 |
| sp_BlitzBackups.sql | 84 KB | 1.734 | 45 | 38 |
| sp_DatabaseRestore.sql | 76 KB | 1.784 | 113 | 7 |
| sp_BlitzWho.sql | 52 KB | 1.135 | 12 | 9 |
| sp_BlitzAnalysis.sql | 32 KB | 893 | 9 | 7 |
| sp_kill.sql | 40 KB | 927 | 16 | 6 |
| SqlServerVersions.sql | 92 KB | 583 | 30 | 0 |
| sp_ineachdb.sql | 16 KB | 417 | 8 | 2 |

Total fuente analizado: ~2,15 MB, ~35.000 líneas de T-SQL en 12 ficheros.

### Objetos analizados

| Objeto | Tipo | Resultado |
|---|---|---|
| dbo.sp_Blitz | PROCEDURE | ok |
| dbo.sp_BlitzIndex | PROCEDURE | ok |
| dbo.sp_BlitzCache | PROCEDURE | ok |
| dbo.sp_BlitzFirst | PROCEDURE | ok |
| dbo.sp_BlitzLock | PROCEDURE | ok |
| dbo.sp_BlitzBackups | PROCEDURE | ok |
| dbo.sp_DatabaseRestore | PROCEDURE | ok |
| dbo.sp_BlitzWho | PROCEDURE | ok |
| dbo.sp_ineachdb | PROCEDURE | ok |
| dbo.sp_BlitzAnalysis | PROCEDURE | ok |
| dbo.sp_kill | PROCEDURE | ok |
| dbo.SqlServerVersions | SCRIPT | ok |

**12/12 ok, 0 parse errors** (confirmado en consola y en `audit_report.json.summary.parse_errors`).

### Complejidad (consulta SQLite, `nodes` donde `label='SqlObject'`, orden por `cyclomatic_complexity`)

Esquema real de la tabla (`PRAGMA table_info(nodes)`) incluye entre otras: `cyclomatic_complexity`,
`total_steps`, `dynamic_sql_steps`, `max_nesting`, `has_error_handling`, `has_cursor`, `has_transaction`.

| name | tipo | cyclomatic | steps | dyn_sql_steps | max_nesting | err_handling | cursor | transacción |
|---|---|---|---|---|---|---|---|---|
| dbo.sp_Blitz | PROCEDURE | **706** | 1229 | 56 | 9 | sí | sí | no |
| dbo.sp_BlitzCache | PROCEDURE | 342 | 717 | 30 | 4 | sí | sí | no |
| dbo.sp_DatabaseRestore | PROCEDURE | 264 | 186 | 6 | 6 | sí | no | no |
| dbo.sp_BlitzFirst | PROCEDURE | 209 | 350 | 52 | 12 | sí | no | no |
| dbo.sp_BlitzIndex | PROCEDURE | 199 | 549 | 35 | 8 | sí | sí | **sí** |
| dbo.sp_BlitzLock | PROCEDURE | 82 | 255 | 18 | 3 | sí | no | no |
| dbo.sp_BlitzBackups | PROCEDURE | 55 | 94 | 21 | 2 | no | no | no |
| dbo.sp_kill | PROCEDURE | 54 | 71 | 6 | 5 | sí | sí | no |
| dbo.sp_BlitzAnalysis | PROCEDURE | 33 | 42 | 6 | 2 | no | no | no |
| dbo.sp_BlitzWho | PROCEDURE | 31 | 22 | 9 | 2 | no | no | no |
| dbo.sp_ineachdb | PROCEDURE | 17 | 30 | 2 | 3 | sí | sí | no |
| dbo.SqlServerVersions | SCRIPT | 1 | 12 | 0 | 0 | no | no | no |

Contexto de escala: el procedimiento más complejo conocido en WideWorldImporters tiene complejidad
ciclomática 19. `sp_Blitz` con **706** es ~37x eso — el parser lo procesa sin fallo ni degradación
visible.

### SQL dinámico: detectado por el motor vs. contado por texto

| Objeto | dyn_sql_steps (motor) | resuelto a literal | sin resolver | sp_executesql/EXEC(@..) por regex |
|---|---|---|---|---|
| sp_Blitz | 56 | 43 | 13 | 59 |
| sp_BlitzCache | 30 | 9 | 21 | 24 |
| sp_DatabaseRestore | 6 | 0 | 6 | 7 |
| sp_BlitzFirst | 52 | 22 | 30 | 52 |
| sp_BlitzIndex | 35 | 1 | **34** | 46 |
| sp_BlitzLock | 18 | 1 | 17 | 20 |
| sp_BlitzBackups | 21 | 1 | 20 | 38 |
| sp_kill | 6 | 0 | 6 | 6 |
| sp_BlitzAnalysis | 6 | 0 | 6 | 7 |
| sp_BlitzWho | 9 | 0 | 9 | 9 |
| sp_ineachdb | 2 | 0 | 2 | 2 |
| SqlServerVersions | 0 | 0 | 0 | 0 |
| **Total** | **241** | **77** | **164 (68%)** | — |

El recuento por regex (`sp_executesql`, `EXEC(@...)`) es una aproximación textual cruda (cuenta también
ocurrencias en comentarios/cadenas); no coincide exacto con `dyn_sql_steps` del motor, que cuenta pasos
reales de ejecución en el AST — la correlación de orden de magnitud confirma que el motor no se está
comiendo instrucciones dinámicas por lote.

**Interpretación**: sólo un 32% del SQL dinámico se resuelve a una tabla concreta. El resto (68%) queda
marcado como `unresolved_dynamic_sql_steps`, que el propio NodeStore documenta como "fail closed" (no
adivina un target incorrecto). No es un fallo del parser sino un límite reconocido y contabilizado: el
`WRITES_TO`/`READS_FROM` de esos objetos es, por diseño, un subconjunto real, no la imagen completa.

### Coherencia entre formatos

| Fuente | Nodos | Aristas |
|---|---|---|
| Consola (`NodeStore:`) | 1354 nodos compartidos + 12 objetos | 16.900 |
| Consola (`SQLite:`) | 5705 | 16.900 |
| Consola (`Graph:`) | 5705 | 16.900 |
| `index.json.stats.total_nodes/total_edges` | 5705 | 16.900 |

Los tres formatos coinciden exactamente. `index.json.stats`:
- `unknown_labels`: `[]`
- `unknown_edge_types`: `[]`
- `orphan_edges`: `0`

`nodes_by_label`: SqlObject=12, Step=3557, Parameter=328, Variable=454, Table=126, Column=212, Action=18,
Rule=988, Workflow=1, Schema=8, Database=1.

### Tamaño

| Artefacto | Tamaño |
|---|---|
| `input.json` (fuente serializado) | 2,3 MB |
| `graph_full.json` (monolítico) | 7,5 MB |
| `graph_full.db` (SQLite) | 7,6 MB |
| `graph_full.nodes/` (NodeStore, 12 objetos + 7 categorías compartidas) | 19 MB |

## Salida literal de consola

`from-sql`:
```
+ C:/temp/corpus-frk-src/sp_Blitz.sql -> FirstResponderKit::dbo.sp_Blitz
+ C:/temp/corpus-frk-src/sp_BlitzIndex.sql -> FirstResponderKit::dbo.sp_BlitzIndex
+ C:/temp/corpus-frk-src/sp_BlitzCache.sql -> FirstResponderKit::dbo.sp_BlitzCache
+ C:/temp/corpus-frk-src/sp_BlitzFirst.sql -> FirstResponderKit::dbo.sp_BlitzFirst
+ C:/temp/corpus-frk-src/sp_BlitzLock.sql -> FirstResponderKit::dbo.sp_BlitzLock
+ C:/temp/corpus-frk-src/sp_BlitzBackups.sql -> FirstResponderKit::dbo.sp_BlitzBackups
+ C:/temp/corpus-frk-src/sp_DatabaseRestore.sql -> FirstResponderKit::dbo.sp_DatabaseRestore
+ C:/temp/corpus-frk-src/sp_BlitzWho.sql -> FirstResponderKit::dbo.sp_BlitzWho
+ C:/temp/corpus-frk-src/sp_ineachdb.sql -> FirstResponderKit::dbo.sp_ineachdb
+ C:/temp/corpus-frk-src/sp_BlitzAnalysis.sql -> FirstResponderKit::dbo.sp_BlitzAnalysis
+ C:/temp/corpus-frk-src/sp_kill.sql -> FirstResponderKit::dbo.sp_kill
+ C:/temp/corpus-frk-src/SqlServerVersions.sql -> FirstResponderKit::dbo.SqlServerVersions
Wrote 12 objects from 12 file(s) to C:/temp/input.json
```

Construcción del grafo (`--columns --sqlite --nodestore`):
```
NodeStore: 12 objects, 1354 shared nodes, 16900 edges -> C:/temp/out/graph_full.nodes
SQLite: 5705 nodes, 16900 edges (db=FirstResponderKit, project=FirstResponderKit) -> C:/temp/out/graph_full.db
Analyzed 12 objects (12 ok, 0 parse errors)
Graph: 5705 nodes, 16900 relationships -> C:/temp/out/graph_full.json
```

`audit_report.json.summary`:
```json
{
  "objects": 12,
  "tables": 126,
  "columns": 212,
  "business_rules": 0,
  "schemas": 8,
  "databases": 1,
  "parse_errors": 0,
  "by_type": { "PROCEDURE": 11, "SCRIPT": 1 },
  "lineage_coverage": { "objects_with_output_columns": 0, "columns_total": 0, "columns_with_lineage": 0, "coverage_pct": 100 },
  "risk_patterns": []
}
```

## Hallazgos

### 1. Cero errores de parseo — el torture test no rompe el parser

Con 12/12 objetos ok incluyendo `sp_Blitz` (10.659 líneas, complejidad 706), el motor no mostró ningún
fallo de parseo, excepción no controlada ni artefacto truncado. Esto es en sí mismo el resultado más
importante del ejercicio: el salto de escala de 10x sobre el corpus de referencia (WWI, 706 líneas máx.)
no encontró el límite del parser T-SQL.

### 2. SQL dinámico cross-database: el 68% de los pasos dinámicos no se resuelve — límite honesto, no bug

Ejemplo real, `sp_BlitzIndex.sql` líneas 231-251 (el objeto con más ratio sin resolver: 34/35):

```sql
/* If the target isn't in the current database, then use dynamic T-SQL*/
IF (@DatabaseName <> DB_NAME())
  BEGIN
       /*first make sure only one row is returned from sys.objects*/
       SET @dsql = N'SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
            SELECT @RowcountOUT = COUNT(1) FROM ' + QUOTENAME(@DatabaseName) + N'.[sys].[objects]
            WHERE [name] = @TableName_IN AND [type] IN (''U'',''V'') OPTION  (RECOMPILE);';
        SET @params = N'@TableName_IN NVARCHAR(128), @RowcountOUT BIGINT OUTPUT';
        EXEC sp_executesql @dsql, @params, @TableName_IN = @TableName, @RowcountOUT = @Rowcount OUTPUT;
        ...
```

El nombre de base de datos (`@DatabaseName`, un parámetro de entrada del procedimiento) se inyecta vía
`QUOTENAME()` en la cadena de SQL dinámico. No hay forma de resolver `[sys].[objects]` de qué base de
datos en tiempo de análisis estático — depende de un valor de entrada en tiempo de ejecución. El motor lo
marca correctamente como `is_dynamic_sql=true`, `dynamic_sql=''` (sin resolver), en vez de adivinar un
target erróneo. Esto es el comportamiento documentado ("fail closed") en
`graph_full.nodes/index.json.howto.exec_resolution`, y se contabiliza por objeto en
`unresolved_dynamic_sql_steps` (34 para `sp_BlitzIndex`, 30 para `sp_BlitzFirst`, 21 para
`sp_BlitzCache`...). **No es un hallazgo de bug, es una limitación estructural inherente (SQL dinámico
cuyo target depende de datos de entrada) que el motor ya declara y cuantifica** — pero conviene que quede
registrado aquí porque afecta directamente a cuánto lineage real se puede confiar en estos 5 procedimientos
más afectados.

Contraste con un caso que sí resuelve (`sp_Blitz.sql`, línea 1646, dentro de un `INSERT INTO
#BlitzResults ... SELECT ...` construido dinámicamente pero con literales fijos, no basado en parámetros
de entrada): el motor lo resuelve al texto completo y genera lineage normal.

### 3. BOM UTF-8 en todos los JSON de salida (fricción de consumo, no bug de parseo T-SQL)

`graph_full.json`, y todos los ficheros del NodeStore (`manifest.json`, `index.json`, `model.json`,
`audit_report.json`, `change_map.json`, `objects/*/object.json`) se escriben con BOM UTF-8
(`EF BB BF`). `input.json` (la entrada intermedia de `from-sql`) NO lleva BOM. Consecuencia real
comprobada en esta misma corrida: `json.load()` de Python falla con
`Unexpected UTF-8 BOM (decode using utf-8-sig)` si no se especifica `encoding='utf-8-sig'` explícitamente.
Node.js/`JSON.parse` normalmente tolera el BOM al leer como string, pero cualquier consumidor que use un
parser estricto (Python estándar, algunas herramientas de CI) tropezará. Es un detalle menor de
serialización, no afecta al lineage, pero es una inconsistencia (input sin BOM, todos los outputs con
BOM) que vale la pena limar.

### 4. `risk_patterns` vacío y `lineage_coverage` en 100% — por ausencia de superficie, no por cobertura real

`audit_report.json.risk_patterns` = `[]` y `lineage_coverage.coverage_pct` = 100%, pero
`objects_with_output_columns` = 0 y `columns_total` = 0. El 100% es un porcentaje sobre un denominador
vacío (0/0), no una señal de que el lineage de columnas esté probado. Ver siguiente sección.

## Lo que este corpus NO demuestra

- **Lineage de columnas y de vistas**: ninguno de los 12 objetos tiene columnas de salida catalogadas
  (`objects_with_output_columns: 0`) porque son procedimientos que hacen `SELECT` directo al cliente o
  vuelcan a tablas temporales (`#BlitzResults`, etc.), no vistas ni funciones con esquema de salida
  persistido. El path `lineage_path.json` (para trazar una columna de salida a sus columnas raíz) no
  tiene ninguna entrada útil aquí.
- **Lineage contra un esquema de usuario real**: el 100% de las tablas leídas son catálogo del sistema
  (`sys.*`, `INFORMATION_SCHEMA.*`, DMVs `sys.dm_*`) o metadatos de `msdb`/`master`/`model`/`tempdb`. Solo
  `sp_BlitzFirst` escribe a dos tablas de usuario reales (`BlitzFirst` / `DBAtools.dbo.BlitzFirst`), y
  ningún objeto tiene FK reales que seguir (126 "tablas" son en su mayoría vistas de catálogo sin FK).
  No hay, por tanto, un oráculo externo (un catálogo real con FKs, vistas, procesos ETL) contra el que
  contrastar el resultado — a diferencia de WideWorldImporters/AdventureWorks, donde sí existe esa base
  de comparación.
- **Persistencia real / escritura en tablas de negocio**: `WRITES_TO` = solo 2 aristas únicas en todo el
  corpus (12 procedimientos). Esto confirma que el FRK es, por diseño, de solo lectura sobre DMVs con
  volcado a temporales — no ejercita en absoluto el camino de escritura/actualización de esquema que sí
  aparece en corpus transaccionales reales.
- **Tiempos de ejecución**: no se han medido (se miden en otro proceso aparte, según instrucción).

## Archivos generados (fuera del repo del toolkit, en C:\temp)

- `C:\temp\corpus-frk-src\` — clon del FRK (no copiado al repo del toolkit).
- `C:\temp\input.json` — 12 objetos serializados desde `from-sql`.
- `C:\temp\out\graph_full.json`, `graph_full.db`, `graph_full.nodes\` — salidas del grafo.
- `C:\temp\from-sql.log`, `C:\temp\build-graph.log` — logs de consola.
