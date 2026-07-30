# Ejecución del T-SQL Lineage Toolkit contra Ola Hallengren SQL Server Maintenance Solution

Fecha: 2026-07-26
Rama del toolkit: `docs/ejecucion-canonica` (worktree ya existente reutilizado en `C:\temp\canon-master`, compila limpio: 0 errores, 0 advertencias).
Corpus: https://github.com/olahallengren/sql-server-maintenance-solution (clonado `--depth 1` en `C:\temp\corpus-ola-src`, licencia MIT).

Ficheros incluidos (7): `DatabaseBackup.sql`, `IndexOptimize.sql`, `DatabaseIntegrityCheck.sql`, `CommandExecute.sql`, `CommandLog.sql`, `Queue.sql`, `QueueDatabase.sql`.
Excluidos (agregados, para no duplicar objetos): `MaintenanceSolution.sql` (538 KB) y `MaintenanceSolutionAzureSQLDatabase.sql` (287 KB).

No se tocó el motor. No se hizo commit de nada. Todo offline vía `from-sql`.

## Cifras medidas

| Métrica | Valor |
|---|---|
| Objetos analizados | 7 (7 ok, **0 errores de parseo**) |
| Objetos tipo PROCEDURE | 4 (DatabaseBackup, IndexOptimize, DatabaseIntegrityCheck, CommandExecute) |
| Objetos CREATE TABLE reconocidos como tabla | 0 (ver Hallazgos — hay 3 en el fuente) |
| Nodos totales (graph_full.json / SQLite / NodeStore) | 2254 / 2254 / 2254 (coherente) |
| Aristas totales (graph_full.json / SQLite / NodeStore) | 6515 / 6515 / 6515 (coherente) |
| `unknown_labels`, `unknown_edge_types`, `orphan_edges` (index.json → stats) | `[]`, `[]`, `0` — todo limpio |
| Tamaño `graph_full.json` | 2684 KB |
| Tamaño `graph_full.db` (SQLite) | 2808 KB |
| Tamaño NodeStore (`graph_full.nodes/`) | 6421 KB en 1177 ficheros |
| Objeto más grande: `dbo.DatabaseBackup` | 541 pasos, complejidad ciclomática 542, 7 niveles de anidamiento, 2 pasos de SQL dinámico |
| `dbo.IndexOptimize` | 342 pasos, cc=234, 6 niveles, 8 pasos dinámicos |
| `dbo.DatabaseIntegrityCheck` | 248 pasos, cc=174, 9 niveles, 3 pasos dinámicos |
| `dbo.CommandExecute` | 39 pasos, cc=25, 2 niveles, 3 pasos dinámicos |
| Referencia WWI (procedimiento más gordo) | 706 líneas, cc=19, 87 pasos, 34 sentencias dinámicas |

**El objeto más grande de este corpus (`DatabaseBackup`, cc=542, 541 pasos) es ~28x más complejo ciclomáticamente que el más gordo de WideWorldImporters (cc=19).** El parser lo digiere sin un solo error.

### SQL dinámico: detectado por el motor vs. recuento textual

Conteo de pasos marcados `is_dynamic_sql=true` (columna `dynamic_sql_steps` en SQLite) frente a las llamadas reales de `EXECUTE sp_executesql` / `EXECUTE @variable_con_sp_executesql` encontradas a mano en el fuente (grep + inspección manual, descartando falsos positivos como nombres de variables que contienen la subcadena `sp_executesql`):

| Objeto | `dynamic_sql_steps` (motor) | Llamadas reales a `sp_executesql` en el fuente |
|---|---|---|
| `dbo.DatabaseBackup` | 2 | 2 (líneas 330, 3020) — coincide exacto |
| `dbo.CommandExecute` | 3 | 3 (líneas 239, 247, 264) — coincide exacto |
| `dbo.DatabaseIntegrityCheck` | 3 | 5 (líneas 225, 1596, 1677, 1762, 1847) — **el motor detecta 2 de menos** |
| `dbo.IndexOptimize` | 8 | ~19 (línea 375 + 18 apariciones de `EXECUTE @CurrentDatabase_sp_executesql @stmt=@CurrentCommand`) — **el motor detecta 11 de menos** |

Ver sección Hallazgos para el análisis de por qué.

## Salida literal de consola

### `dotnet build`
```
Determinando los proyectos que se van a restaurar...
Se ha restaurado C:\temp\canon-master\src\TSqlParser\TSqlParser.csproj (en 257 ms).
TSqlParser -> C:\temp\canon-master\src\TSqlParser\bin\Debug\net10.0\TSqlParser.dll

Compilación correcta.
    0 Advertencia(s)
    0 Errores

Tiempo transcurrido 00:00:02.31
```

### `dotnet run -- from-sql OlaMaintenance input.json <7 ficheros>`
```
  + C:/temp/corpus-ola-src/DatabaseBackup.sql -> OlaMaintenance::dbo.DatabaseBackup
  + C:/temp/corpus-ola-src/IndexOptimize.sql -> OlaMaintenance::dbo.IndexOptimize
  + C:/temp/corpus-ola-src/DatabaseIntegrityCheck.sql -> OlaMaintenance::dbo.DatabaseIntegrityCheck
  + C:/temp/corpus-ola-src/CommandExecute.sql -> OlaMaintenance::dbo.CommandExecute
  + C:/temp/corpus-ola-src/CommandLog.sql -> OlaMaintenance::dbo.CommandLog
  + C:/temp/corpus-ola-src/Queue.sql -> OlaMaintenance::dbo.Queue
  + C:/temp/corpus-ola-src/QueueDatabase.sql -> OlaMaintenance::dbo.QueueDatabase
Wrote 7 objects from 7 file(s) to C:/temp/corpus-ola/input.json
```

### `dotnet run -- input.json graph_full.json --columns --sqlite --nodestore`
```
NodeStore: 7 objects, 579 shared nodes, 6515 edges -> C:/temp/corpus-ola/out/graph_full.nodes
SQLite: 2254 nodes, 6515 edges (db=OlaMaintenance, project=OlaMaintenance) -> C:/temp/corpus-ola/out/graph_full.db
Analyzed 7 objects (7 ok, 0 parse errors)
Graph: 2254 nodes, 6515 relationships -> C:/temp/corpus-ola/out/graph_full.json
```
(Hubo un warning MSB3026 de MSBuild al reintentar copiar `TSqlParser.exe` porque el binario estaba bloqueado por la ejecución anterior — cosmético, de build, no del análisis; el reintento tuvo éxito.)

### `dotnet run -- report input.json`
```
===================== INFORME GENERAL DE LA BASE DE DATOS =====================
Base de datos          : OlaMaintenance
Objetos programables   : 7  (7 ok, 0 con error de parseo)
Tablas (CREATE TABLE)  : 0

--- Caracteristicas ---
  Con transaccion       : 3
  Con manejo de errores : 4
  Con cursor            : 4
  Con SQL dinamico      : 4

--- Top 10 por complejidad ciclomatica ---
  cc=542 dyn=2   pasos=541 dbo.DatabaseBackup
  cc=234 dyn=8   pasos=342 dbo.IndexOptimize
  cc=174 dyn=3   pasos=248 dbo.DatabaseIntegrityCheck
  cc=25  dyn=3   pasos=39  dbo.CommandExecute
  cc=1   dyn=0   pasos=0   dbo.CommandLog
  cc=1   dyn=0   pasos=0   dbo.Queue
  cc=1   dyn=0   pasos=0   dbo.QueueDatabase

--- Tablas mas escritas (INSERT/UPDATE/DELETE/MERGE) ---
  370 @Errors
  135 @Parameters
  27  @tmpDatabases
  15  dbo.QueueDatabase
  14  @CurrentAlterIndexWithClauseArguments
  13  @tmpIndexesStatistics
  12  @tmpAvailabilityGroups
  10  @CurrentDirectories
  8   @CurrentUpdateStatisticsWithClauseArguments
  7   @CurrentCleanupDates
```

## Hallazgos

Tres hallazgos concretos, con fragmento de T-SQL. Ninguno es un error de parseo (0 objetos fallaron); son gaps de interpretación semántica.

### 1. `CREATE TABLE` condicional (guardado por `IF NOT EXISTS...BEGIN...END`) no se reconoce como tabla

Los 3 ficheros de tabla del corpus (`CommandLog.sql`, `Queue.sql`, `QueueDatabase.sql`) siguen el mismo patrón:

```sql
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[QueueDatabase]') AND type in (N'U'))
BEGIN
CREATE TABLE [dbo].[QueueDatabase](
  [QueueID] [int] NOT NULL,
  ...
```

El motor ingiere el objeto sin error, pero lo clasifica como `object_type = UNKNOWN`, con `cyclomatic_complexity=1` y `total_steps=0` — no lo reconoce como una definición de tabla. Consecuencia directa: **el informe (`report`) dice "Tablas (CREATE TABLE): 0"** pese a que hay 3 `CREATE TABLE` reales en el corpus. Distinto del bug ya resuelto de "comentario líder rompe el router de `CREATE TABLE`" (memoria `from-sql-tables-lose-lineage.md`) — aquí el problema es el envoltorio condicional `IF NOT EXISTS(...) BEGIN ... END`, no un comentario.

Nota: pese a no clasificarse como tabla "propia", el motor sí acaba generando nodos `Table` para `dbo.CommandLog`, `dbo.Queue` y `dbo.QueueDatabase` — pero por el lado *referenciado* (cuando otro procedimiento hace `SELECT`/`UPDATE` contra ellas), con columnas extraídas del propio `CREATE TABLE`. Es decir: la tabla se "descubre" indirectamente vía sus usos, no vía su propia definición.

### 2. Identidad de tabla partida en dos nodos: `dbo.QueueDatabase` vs `QueueDatabase` (mismo `UPDATE`, dos tablas distintas en el grafo)

En `DatabaseBackup.sql` (y de forma idéntica en `IndexOptimize.sql` y `DatabaseIntegrityCheck.sql`, código compartido/copiado entre los tres scripts de Ola):

```sql
-- línea 2832
FROM dbo.QueueDatabase QueueDatabase
WHERE QueueID = @QueueID
...

-- línea 2838
UPDATE QueueDatabase
SET DatabaseStartTime = SYSDATETIME(),
    ...
FROM (SELECT TOP 1 DatabaseStartTime, ... FROM dbo.QueueDatabase ...) AS QueueDatabase
```

Es el patrón clásico `UPDATE <alias> ... FROM (subquery) AS <alias>`. El motor genera, **para el mismo `step421`**:
- una arista `READS_FROM` hacia el nodo `OlaMaintenance:table:dbo.queuedatabase` (resuelto correctamente, cualificado con schema, con sus columnas colgando del `CREATE TABLE`), y
- una arista `WRITES_TO` hacia un nodo **distinto** `OlaMaintenance:table:queuedatabase` (sin cualificar, `name="QueueDatabase"`, con columnas duplicadas pero sin el mismo id).

Es decir: la misma sentencia `UPDATE ... FROM` produce dos identidades de tabla diferentes para la misma tabla física, porque el nombre del *target* del `UPDATE` (el alias del `UPDATE`) no se resuelve contra el binding real de la cláusula `FROM`/subquery cuando el alias coincide textualmente con el nombre corto de la tabla. El mismo patrón se repite en `IndexOptimize#step211` y `DatabaseIntegrityCheck#step174` — 3 procedimientos, mismo bug, mismo par de nodos duplicados.

Impacto: el análisis de impacto/lineage subestima quién escribe en `dbo.QueueDatabase`, porque las escrituras "cuelgan" de un nodo fantasma paralelo que no está unido al nodo real de la tabla (aunque ambos sí cuelgan correctamente de `Schema:dbo` vía `CONTAINS`, así que no aparece como huérfano en `orphan_edges` — el índice de coherencia no lo detecta).

### 3. Subdetección de SQL dinámico en el patrón "ejecutar `sp_executesql` a través de una variable que contiene el nombre completo de la BD"

`IndexOptimize.sql` y `DatabaseIntegrityCheck.sql` usan un patrón de invocación indirecta cross-database:

```sql
SET @CurrentDatabase_sp_executesql = QUOTENAME(@CurrentDatabaseName) + '.sys.sp_executesql'
...
EXECUTE @CurrentDatabase_sp_executesql @stmt = @CurrentCommand, @params = N'@ParamDatabaseName nvarchar(max)', @ParamDatabaseName = @CurrentDatabaseName
```

Aquí el *nombre del procedimiento ejecutado* es en sí mismo una variable (para poder invocar `sp_executesql` en el contexto de la base de datos objetivo). El motor reconoce **algunas** de estas llamadas como `is_dynamic_sql=true` pero no todas:
- `IndexOptimize.sql`: ~19 apariciones reales de `EXECUTE @CurrentDatabase_sp_executesql @stmt=@CurrentCommand` en el fuente, pero `dynamic_sql_steps=8` — faltan ~11.
- `DatabaseIntegrityCheck.sql`: 5 apariciones reales, `dynamic_sql_steps=3` — faltan 2.
- En cambio, en `DatabaseBackup.sql` y `CommandExecute.sql` (que también usan el patrón, pero con muchas menos repeticiones, 2 y 3 respectivamente) el conteo coincide exacto.

Hipótesis (no verificada a fondo, no se tocó el motor): el detector de SQL dinámico parece disparar de forma fiable la primera vez que ve el patrón en cada objeto/rama de control, pero no en cada aparición subsiguiente dentro de bucles/ramas anidadas más profundas — o hay algún límite de deduplicación por variable de comando. Sería un buen caso de test aislado: un procedimiento sintético con 10+ invocaciones de `EXECUTE @var_con_sp_executesql @stmt=@cmd` dentro de bucles anidados, para ver en qué punto deja de contarlas.

## Lo que este corpus NO demuestra

- **No ejercita lineage sobre un esquema de negocio real.** Los 4 procedimientos de Ola Hallengren casi no tocan tablas de usuario: leen masivamente DMVs (`sys.dm_*`, `sys.databases`, `sys.indexes`, etc.), variables de tabla temporales (`@tmpDatabases`, `@tmpIndexesStatistics`...) y solo escriben de verdad en 3 tablas propias del framework (`CommandLog`, `Queue`, `QueueDatabase`), que son metadatos de auditoría/cola, no datos de negocio.
- Por tanto las 172 aristas `READS_FROM` y las 3 `WRITES_TO`/15 `WRITES_COLUMN` del grafo no dicen nada sobre calidad de resolución de joins de negocio, ni sobre profundidad de columnas en un esquema estrella o normalizado — son básicamente lecturas de catálogo del sistema.
- No se ha probado nada de `validate` (contraste contra `sys.foreign_keys`/`sys.sql_expression_dependencies` de una base real), porque no hay base de datos ni FKs de negocio en este corpus. Era intencional (paso 1 de este encargo dice offline, `from-sql` puro).
- No mide tiempos de ejecución (se excluyeron a propósito, se miden aparte en serie).

## Conclusión corta

El parser **no rompe con ningún fragmento** de este corpus real y denso (0 errores de parseo en 7 objetos, incluyendo el procedimiento más complejo que hemos visto hasta ahora: cc=542, 541 pasos, muy por encima del máximo previo de WWI). El grafo es internamente coherente (mismos nodos/aristas en JSON/SQLite/NodeStore, cero `unknown_labels`/`unknown_edge_types`/`orphan_edges`). Los 3 hallazgos son gaps de interpretación semántica, no crashes: `CREATE TABLE` condicional sin reconocer, duplicación de identidad de tabla en el patrón `UPDATE alias ... FROM (subquery) alias`, y subdetección parcial de SQL dinámico invocado vía variable-nombre-de-procedimiento en bucles profundos.
