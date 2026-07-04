# Informe de tortura: SQL Server First Responder Kit (FRK)

Fecha: 2026-07-04
Autor: sesión de caza de huecos (agente), sin arreglos aplicados.

## Metodología

1. Se clonó `https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit` (shallow, `--depth 1`) en
   `C:\temp\frk-torture\repo`, **fuera del repo**. No se copió ningún `.sql` del kit dentro de
   `tsql-lineage-toolkit` (licencia/tamaño).
2. Se compiló el toolkit en Release:
   `dotnet build src/TSqlParser/TSqlParser.csproj -c Release` → compilación correcta, 0 advertencias, 0 errores.
3. Se procesaron los 11 ficheros `sp_*.sql` de la raíz del kit (todos los que existen: no hay más en la
   raíz aparte de estos) con el pipeline estándar:
   ```
   dotnet src/TSqlParser/bin/Release/net10.0/TSqlParser.dll from-sql FRK <tmp>/input.json <11 ficheros>
   dotnet src/TSqlParser/bin/Release/net10.0/TSqlParser.dll <tmp>/input.json <tmp>/graph_full.json --columns
   ```
   Todas las salidas se escribieron en `C:\temp\frk-torture\` (nunca en `out/` del repo).
4. El grafo resultante (`graph_full.json`, JSON con `nodes`/`relationships`) se analizó con `jq` para
   contar nodos, relaciones, huecos de lineage y SQL dinámico.
5. Para aislar hallazgos concretos se crearon micro-repros de un único `CREATE PROCEDURE` en
   `C:\temp\frk-torture\repro\*.sql` (tampoco en el repo) y se pasaron por el mismo pipeline.

Ningún fichero de `src/`, `tests/`, `eval/` ni el resto del repo fue tocado. Este documento es el único
artefacto creado.

## Resumen ejecutivo (números clave)

- Ficheros procesados: **11/11**, todos parseados sin error.
- Errores de parseo: **0**.
- Crashes / timeouts: **0**.
- Tiempo total `from-sql` (11 ficheros, 44.390 líneas): **0.126 s** (`Measure-Command`, PowerShell).
- Tiempo total construcción de grafo con `--columns`: **1.742 s**.
- Grafo resultante: **5.727 nodos, 16.916 relaciones** (`graph_full.json`, 7.185.289 bytes).
- `CALLS` (proc→proc) detectadas: **1 de ~11 posibles** (el resto son vía EXEC dinámico con variable).
- Pasos con SQL dinámico (`is_dynamic_sql=true`): **241**, de los cuales **164 (68%)** no tienen ni un
  fragmento de texto reconstruido (`dynamic_sql=""`).
- **Ninguno** de los 241 pasos de SQL dinámico genera una arista `READS_FROM`/`WRITES_TO` (0 de 619
  `READS_FROM` y 0 de 43 `WRITES_TO` totales proviene de un paso dinámico).
- Tablas temporales (`#`/`##`) detectadas como nodo: **12**, de las cuales solo **1** (`#BlitzResults`)
  tiene alguna arista `WRITES_TO` (26), y **0 de las 12** tiene ninguna arista `READS_FROM`.

## Metodología de compilación y ejecución (salidas pegadas)

### Build

```
$ dotnet build src/TSqlParser/TSqlParser.csproj -c Release
  Determinando los proyectos que se van a restaurar...
  Todos los proyectos están actualizados para la restauración.
  TSqlParser -> C:\MisCosas\MisProyectos\sql-analyzer\tsql-lineage-toolkit\src\TSqlParser\bin\Release\net10.0\TSqlParser.dll

Compilación correcta.
    0 Advertencia(s)
    0 Errores

Tiempo transcurrido 00:00:01.38
```

### Clonado (tamaño de los ficheros objetivo)

```
$ cd /c/temp/frk-torture/repo && for f in sp_*.sql; do lines=$(wc -l < "$f"); printf "%-40s %s\n" "$f" "$lines"; done
sp_Blitz.sql                             10659
sp_BlitzAnalysis.sql                     893
sp_BlitzBackups.sql                      1734
sp_BlitzCache.sql                        8762
sp_BlitzFirst.sql                        5422
sp_BlitzIndex.sql                        7845
sp_BlitzLock.sql                         4816
sp_BlitzWho.sql                          1135
sp_DatabaseRestore.sql                   1784
sp_ineachdb.sql                          413
sp_kill.sql                              927
```

Total: 44.390 líneas en 11 ficheros (`wc -l sp_*.sql | tail -1` → `44390 total`).

### `from-sql` (extracción de los 11 objetos)

```
$ dotnet src/TSqlParser/bin/Release/net10.0/TSqlParser.dll from-sql FRK /c/temp/frk-torture/input.json <11 ficheros>
  + C:/temp/frk-torture/repo/sp_Blitz.sql -> FRK::dbo.sp_Blitz
  + C:/temp/frk-torture/repo/sp_BlitzAnalysis.sql -> FRK::dbo.sp_BlitzAnalysis
  + C:/temp/frk-torture/repo/sp_BlitzBackups.sql -> FRK::dbo.sp_BlitzBackups
  + C:/temp/frk-torture/repo/sp_BlitzCache.sql -> FRK::dbo.sp_BlitzCache
  + C:/temp/frk-torture/repo/sp_BlitzFirst.sql -> FRK::dbo.sp_BlitzFirst
  + C:/temp/frk-torture/repo/sp_BlitzIndex.sql -> FRK::dbo.sp_BlitzIndex
  + C:/temp/frk-torture/repo/sp_BlitzLock.sql -> FRK::dbo.sp_BlitzLock
  + C:/temp/frk-torture/repo/sp_BlitzWho.sql -> FRK::dbo.sp_BlitzWho
  + C:/temp/frk-torture/repo/sp_DatabaseRestore.sql -> FRK::dbo.sp_DatabaseRestore
  + C:/temp/frk-torture/repo/sp_ineachdb.sql -> FRK::dbo.sp_ineachdb
  + C:/temp/frk-torture/repo/sp_kill.sql -> FRK::dbo.sp_kill
Wrote 11 objects from 11 file(s) to C:/temp/frk-torture/input.json
```

Exit code: `0`. `stderr` vacío (verificado por separado redirigiendo a fichero: `stderr2.log` de 0 líneas).

Router: los 11 ficheros fueron reconocidos como `PROCEDURE` (`dbo.sp_*`) — ninguno cayó en una rama de
router equivocada ni generó "0 objetos".

### Construcción de grafo (`--columns`)

```
$ dotnet src/TSqlParser/bin/Release/net10.0/TSqlParser.dll /c/temp/frk-torture/input.json /c/temp/frk-torture/graph_full.json --columns
Analyzed 11 objects (11 ok, 0 parse errors)
Graph: 5727 nodes, 16916 relationships -> C:/temp/frk-torture/graph_full.json
```

Exit code: `0`. `stderr` vacío.

### Timing (`Measure-Command`, PowerShell — más fiable que `time` de Git Bash para procesos .NET)

```
PS> Measure-Command { dotnet ... from-sql FRKFULL ... (11 ficheros) | Out-Null }
from-sql TotalSeconds: 0.1261303

PS> Measure-Command { dotnet ... input_final.json graph_final.json --columns | Out-Null }
graph TotalSeconds: 1.7419148
```

Timing por fichero individual (cada invocación incluye ~100-150 ms de arranque de proceso .NET/JIT; el
coste de arranque domina sobre el de parseo real, por eso el tiempo no crece con el tamaño del fichero —
ver sección "Qué NO se pudo medir"):

```
sp_Blitz             elapsed_ms=148  (10.659 líneas)
sp_BlitzAnalysis      elapsed_ms=135  (893 líneas)
sp_BlitzBackups       elapsed_ms=136  (1.734 líneas)
sp_BlitzCache         elapsed_ms=146  (8.762 líneas)
sp_BlitzFirst         elapsed_ms=175  (5.422 líneas)
sp_BlitzIndex         elapsed_ms=167  (7.845 líneas)
sp_BlitzLock          elapsed_ms=133  (4.816 líneas)
sp_BlitzWho           elapsed_ms=172  (1.135 líneas)
sp_DatabaseRestore    elapsed_ms=139  (1.784 líneas)
sp_ineachdb           elapsed_ms=183  (413 líneas)
sp_kill               elapsed_ms=146  (927 líneas)
```

**Conclusión de rendimiento: no hay problema de rendimiento.** 44.390 líneas de T-SQL de producción real,
con SQL dinámico masivo, PIVOT, CROSS APPLY, OPENJSON, STRING_SPLIT, tablas de variable, se procesan en
menos de 2 segundos totales, sin excepciones ni degradación visible por tamaño de fichero.

## Tabla de resultados por fichero (grafo completo, `graph_full.json`)

Conteos obtenidos con `jq` filtrando por prefijo de id de objeto (`FRK::dbo.<nombre>#`), evitando el
error de coincidencia de prefijo entre `sp_Blitz` y `sp_BlitzCache` (se corrigió añadiendo el separador
`#` al prefijo tras detectar el problema en un primer pase).

| Objeto | Steps | dynamic_sql_calls (prop.) | pasos EXEC dinámico | …sin texto reconstruido | READS_FROM | WRITES_TO | CALLS | BUILDS_SQL_FROM |
|---|---|---|---|---|---|---|---|---|
| sp_Blitz | 1225 | 56 | 56 | 13 | 349 | 26 | 0 | 324 |
| sp_BlitzAnalysis | 42 | 6 | 6 | 6 | 3 | 0 | 0 | 6 |
| sp_BlitzBackups | 94 | 21 | 21 | 20 | 5 | 0 | 0 | 29 |
| sp_BlitzCache | 717 | 32 | 30 | 21 | 26 | 9 | 0 | 39 |
| sp_BlitzFirst | 335 | 52 | 52 | 30 | 123 | 3 | 0 | 90 |
| sp_BlitzIndex | 549 | 35 | 35 | 34 | 33 | 0 | 0 | 61 |
| sp_BlitzLock | 255 | 18 | 18 | 17 | 32 | 0 | 0 | 25 |
| sp_BlitzWho | 22 | 10 | 9 | 9 | 8 | 0 | 0 | 10 |
| sp_DatabaseRestore | 186 | 6 | 6 | 6 | 10 | 4 | 0 | 6 |
| sp_ineachdb | 30 | 2 | 2 | 2 | 25 | 0 | 0 | 3 |
| sp_kill | 71 | 6 | 6 | 6 | 5 | 1 | 0 | 11 |
| **TOTAL** | **3526** | **244** | **241** | **164** | **619** | **43** | **1 (global, sp_Blitz→sp_ineachdb)** | **604** |

(El único `CALLS` de todo el corpus: `sp_Blitz → sp_ineachdb` estático, capturado correctamente. Todas
las demás invocaciones proc-a-proc del kit se hacen vía `EXEC @variable_con_nombre_calculado`, invisibles
al análisis estático — ver hallazgo H4.)

Distribución de tipos de nodo en el grafo completo:
```
   3526 Step
    982 Rule
    855 IF
    454 Variable
    327 Parameter
    262 Column
    138 Table
    119 IFELSE
     18 Action
     11 SqlObject
     11 Process
      7 WHILE
      7 Schema
      1 Workflow
      1 Database
      1 CATCH
```

Distribución de tipos de relación:
```
   3526 HAS_STEP
   3526 ACTION
   2947 GOVERNS
   2758 USES_VARIABLE
   1161 NESTED_IN
    619 READS_FROM
    454 DECLARES
    344 BUILDS_SQL_FROM
    327 HAS_PARAMETER
    262 HAS_COLUMN
    224 DERIVES_FROM
    194 WRITES_COLUMN
    156 CONTAINS
    123 FILTERS_ON
    106 ASSIGNED_FROM
     86 READS_COLUMN
     43 WRITES_TO
     39 TARGETS
     11 BELONGS_TO
      7 WORKFLOW_WRITES_TO
      2 CONDITIONED_BY
      1 CALLS
```

## Objetos con degree=0

**A nivel del nodo `SqlObject` directo, los 11 objetos tienen degree=0** en `READS_FROM`/`WRITES_TO`/`CALLS`
(esas aristas salen de los nodos `Step` hijos, no del nodo del objeto — comportamiento esperado del
modelo de datos, no un hueco). Agregando por prefijo de todos los `Step` que cuelgan de cada objeto
(tabla arriba), **ningún objeto tiene degree=0 agregado**: todos tienen al menos algún `READS_FROM`. El
mínimo es `sp_BlitzAnalysis` con 3 lecturas — bajo para un proc de 893 líneas, pero no cero (motivo:
gran parte de su lógica también vive en SQL dinámico, ver H2).

## Top hallazgos (por severidad)

### H1 (CRÍTICO) — El 100% del SQL dinámico es invisible al lineage

241 pasos con `is_dynamic_sql=true` en todo el corpus. **Cero** de ellos origina una arista `READS_FROM`
o `WRITES_TO`, verificado agregando sobre el grafo completo:

```
jq -r '
  ([.nodes[] | select(.labels[]?=="Step" and .properties.is_dynamic_sql==true) | .id]) as $dynids
  | [.relationships[] | select(.type=="READS_FROM" and (.source as $s | $dynids | index($s)))] | length
' graph_full.json
# -> 0
```

Repro mínimo (fragmento real, `sp_Blitz.sql` línea 736, capturado como
`FRK::dbo.sp_Blitz#step300`-ish con `dynamic_sql:"INSERT INTO #BlitzResults (CheckID, ...) SELECT 8 AS CheckID, ..."`):

```sql
SET @StringToExecute = N'INSERT INTO #BlitzResults (...) SELECT 8 AS CheckID, ...';
EXEC(@StringToExecute);
```

El paso (`FRK::dbo.sp_Blitz#step292`) **sí** reconstruye el texto completo de la sentencia dinámica en la
propiedad `dynamic_sql`, incluyendo literalmente `INSERT INTO #BlitzResults`, pero solo genera estas
aristas:

```json
{"type":"BUILDS_SQL_FROM","source":"FRK::dbo.sp_Blitz#step292","target":"FRK::dbo.sp_Blitz#var:@StringToExecute"}
{"type":"USES_VARIABLE","source":"FRK::dbo.sp_Blitz#step292","target":"FRK::dbo.sp_Blitz#var:@StringToExecute"}
{"type":"ACTION","source":"FRK::dbo.sp_Blitz#step292","target":"FRK:action:EXEC"}
```

Ni `WRITES_TO #BlitzResults` ni ningún `READS_FROM`, pese a que el texto reconstruido lo hace evidente.

Impacto: dado que los `sp_Blitz*` (el objetivo central de esta tortura) implementan la inmensa mayoría de
sus checks reales precisamente mediante `EXEC(@StringToExecute)` (56 llamadas dinámicas solo en
`sp_Blitz`, 52 en `sp_BlitzFirst`), **el grafo de lineage de estos procs solo refleja una fracción minoritaria
de sus lecturas/escrituras reales**. Esto es exactamente el punto de estrés que motivó la tortura, y se
confirma cuantitativamente: 164/241 (68%) de los pasos dinámicos ni siquiera reconstruyen el texto, y de
los 77 que sí lo hacen, 0 se re-parsean para extraer lineage.

### H2 (ALTO) — Lineage de tablas temporales prácticamente ausente, e inconsistente incluso para escritura estática

De las 12 tablas temporales (`#`/`##`) detectadas como nodo en todo el corpus (`#StatementsToRun4FRKVersionCheck`,
`#BlitzResults`, `#ai_providers`, `#ai_prompts`, `#checkversion`, `#p`, `#configuration`,
`#checkversion_allsort`, `#PerfmonStats`, `#FileListParameters`, `#Headers`, `#SplitLogBackups`):

- **0 de 12** tienen ninguna arista `READS_FROM` en todo el corpus, pese a que el código las lee
  constantemente (p. ej. `sp_Blitz.sql` línea 10247: `SELECT * INTO ##BlitzResults FROM #BlitzResults;`,
  o los `WHERE EXISTS (SELECT * FROM #BlitzResults ...)` usados para deduplicar hallazgos).
- Solo **1 de 12** (`#BlitzResults`) tiene alguna arista `WRITES_TO` (26 en total), y ese conteo es
  parcial: el código fuente contiene **84** apariciones textuales de `INSERT INTO #BlitzResults`
  (`grep -c "INSERT INTO #BlitzResults" sp_Blitz.sql` → 84), pero solo 26 generan la arista — el resto
  o bien viven dentro de SQL dinámico (H1) o se pierden por el mismo motivo que en el repro de abajo.
- El caso de `sp_Blitz.sql:10247` (`SELECT * INTO ##BlitzResults FROM #BlitzResults;`, totalmente
  estático, sin SQL dinámico de por medio) se comprobó paso a paso en el grafo:
  ```
  FRK::dbo.sp_Blitz#step1193  action=DROP_TABLE target=##BlitzResults
  FRK::dbo.sp_Blitz#step1194  action=INSERT     target=##BlitzResults
  ```
  Las únicas relaciones que salen de `step1194` son:
  ```json
  {"type":"ACTION","source":"FRK::dbo.sp_Blitz#step1194","target":"FRK:action:INSERT"}
  ```
  **Ninguna** `WRITES_TO ##BlitzResults` ni `READS_FROM #BlitzResults`, pese a ser SQL 100% estático,
  sin variables, sin EXEC dinámico.

- Repro aislado y mínimo (proc de una sola sentencia, sin nada del kit) confirma que **cualquier**
  escritura sobre una tabla temporal (local `#` o global `##`) pierde su arista `WRITES_TO`,
  independientemente de:
  - usar `SELECT ... INTO #x` o `INSERT INTO #x SELECT ...`
  - declarar antes la tabla con `CREATE TABLE #x (...)`
  - anidar la sentencia dentro de varios `IF`/`BEGIN...END`
  - incluir o no lista explícita de columnas
  - incluir o no cláusula `FROM` en el `SELECT` origen

  ```sql
  CREATE PROCEDURE dbo.TestInsertLocalTempDeclared AS
  BEGIN
      CREATE TABLE #Foo (Col1 INT, Col2 INT);
      INSERT INTO #Foo (Col1, Col2) SELECT Col1, Col2 FROM dbo.Bar;
  END
  ```
  → grafo resultante: solo `READS_FROM dbo.Bar`, **ninguna arista hacia `#Foo`** (ni siquiera aparece
  `#Foo` como nodo `Table`).

  Sin embargo, en el grafo completo del kit **sí** existen 26 aristas `WRITES_TO #blitzresults`
  originadas por sentencias con la misma forma superficial (`INSERT INTO #BlitzResults (cols) SELECT
  literales_constantes;`, ver `sp_Blitz.sql:703-716`, capturado en `step672`). No se ha podido aislar
  qué condición exacta hace que esas 26 sí se capturen y las demás no — **la extracción de lineage de
  escritura hacia tablas temporales parece dependiente del contexto / no determinista**, y merece
  revisión de código por parte de quien mantiene el motor (candidatos: el visitor de `InsertStatement`
  puede estar resolviendo el nombre de tabla temporal solo cuando coincide con un patrón muy específico
  ya visto en el corpus principal usado para diseñar esa regla).

- Es plausible que este hueco explique buena parte de por qué `sp_BlitzCache` (717 steps) y
  `sp_BlitzIndex` (549 steps) — procs que existen únicamente para volcar decenas de DMVs a tablas
  temporales y post-procesarlas — solo muestran 26 y 33 `READS_FROM` respectivamente.

### H3 (MEDIO) — `CALLS` proc-a-proc casi invisible por diseño (SQL dinámico + nombre en variable)

Solo se detectó **1** arista `CALLS` en todo el corpus (`sp_Blitz → sp_ineachdb`, estática). El resto de
invocaciones entre procs del kit (`sp_BlitzIndex`, `sp_BlitzCache`, etc. se auto-invocan y delegan entre sí)
se hacen así en `sp_ineachdb.sql`:

```sql
DECLARE @sx nvarchar(18) = N'.sys.sp_executesql';
...
EXEC sys.sp_executesql @cmd;
EXEC @exec @cmd;
```

El nombre del procedimiento a ejecutar (`@exec`) es una variable calculada en tiempo de ejecución, no un
literal — este es un límite conocido/esperado del análisis estático (no reproducible como "bug" per se,
pero sí un techo de cobertura real que conviene documentar: cualquier kit de scripts que enrute llamadas
por variable escapará a `CALLS`).

### H4 (BAJO) — Normalización cosmética inconsistente de identificadores entre corchetes

9 de los 138 nodos `Table` conservan los corchetes T-SQL en la propiedad `name` (identidad del nodo va
por id normalizado en minúsculas, así que no hay fragmentación de identidad, pero sí inconsistencia
visual/de reporting):

```
[master].sys.database_mirroring
[#BlitzResults]
[sys].[dm_server_services]
[sys].[dm_server_memory_dumps]
[sys].[dm_os_windows_info]
[sys].[dm_os_process_memory]
[sys].[dm_server_registry]
[msdb].[dbo].[sysssispackages]
[BlitzFirst]
```
frente a los 129 restantes sin corchetes (`sys.dm_exec_query_stats`, `msdb.dbo.sysjobs`, etc.). No afecta
a la identidad del grafo (el `id` sí está normalizado), pero cualquier informe/dashboard que muestre
`properties.name` directamente mostrará inconsistencia visual.

### H5 (INFORMATIVO) — Cobertura de columnas relativamente baja pero no evidencia de bug

262 nodos `Column`, 194 `WRITES_COLUMN`, 86 `READS_COLUMN` en un corpus de 44K líneas con procs que
manipulan docenas de DMVs de 10-40 columnas cada una. No se ha podido determinar si esto es "correcto"
(porque la mayoría de columnas reales están ocultas dentro del SQL dinámico de H1, causa más probable) o
si hay un segundo hueco de extracción de columnas independiente — no se aisló un repro columna-específico
por límite de tiempo de esta sesión.

## Qué NO se pudo medir

- **Coste real de parseo aislado del arranque del proceso .NET**: cada invocación de
  `TSqlParser.dll` mide 110-190 ms de punta a punta, dominados por el arranque de proceso/JIT del propio
  ejecutable (`dotnet run` de un binario pequeño). No se instrumentó el propio parser con temporizadores
  internos, así que no se puede afirmar cuánto de ese tiempo es ScriptDOM parseando 10K líneas vs.
  arranque de .NET. Con estos números **no hay indicio de problema de escalado** (el fichero 12x más
  grande no tarda 12x más), pero tampoco hay una medición interna limpia.
- **Uso de memoria / pico de heap**: no se midió (no se usó ETW/dotnet-counters ni similar).
- **Corrección semántica exhaustiva del lineage estático**: se verificaron manualmente solo los casos que
  aparecieron como sospechosos (temp tables, SQL dinámico, CALLS). No se comparó columna a columna con un
  oráculo de SQL Server real para estos 11 procs (no hay estas bases de datos de FRK instaladas en la
  instancia SQLEXPRESS local usada como oráculo en otras auditorías del proyecto) — sería el siguiente
  paso natural si se decide invertir en arreglar H1/H2.
- **La causa raíz exacta de la inconsistencia en H2** (por qué 26 de ~84 `INSERT INTO #BlitzResults`
  estáticos sí generan `WRITES_TO` y un repro superficialmente idéntico no): se aisló el fenómeno y se
  descartaron varias hipótesis (columnas explícitas, `CREATE TABLE` previo, anidamiento, presencia de
  `FROM`), pero no se llegó a la línea de código del analizador responsable — requiere que alguien con
  contexto del visitor de `InsertStatement`/`SelectInto` revise el código fuente, cosa que esta sesión
  tenía prohibido tocar.
- **Otros ficheros del kit** (`Install-All-Scripts.sql`, `Uninstall.sql`, `OptionalScripts/*`,
  `Deprecated/*`) no se procesaron — el encargo pedía explícitamente los `sp_*.sql` de la raíz.
- **Sintaxis exótica específica**: se confirmó uso real en el corpus de `CROSS APPLY` (10/11 ficheros),
  `PIVOT` (2), `OPENJSON`/`FOR JSON` (2), `STRING_SPLIT` (1), variables de tabla (7), `sp_executesql`
  (11/11) — todo parseado sin error de sintaxis — pero no se verificó lineage específico de cada una de
  estas construcciones más allá de que no rompen el parser.

---

## Auditoría independiente (Fable, 2026-07-04) — correcciones al informe

Re-conteo contra **`graph_final.json` como grafo canónico** (el informe original
mezcló conteos de builds intermedios — quedaron 3 grafos en el temp). El agente
que redactó el informe no pudo aplicar estas correcciones (límite de sesión);
prevalece esta sección donde contradiga a lo anterior.

1. **Números corregidos (graph_final.json):** nodos `:Table` temporales = **11**
   (no 12), con **0 `WRITES_TO` y 0 `READS_FROM`** hacia ellos (no "1 con 26").
   Steps dinámicos = 241, de los cuales **77 con `dynamic_sql` reconstruido**.
   Los 11 nodos temp: `#FileListParameters, #Headers, #PerfmonStats,
   #SplitLogBackups, #StatementsToRun4FRKVersionCheck, #ai_prompts,
   #ai_providers, #checkversion, #checkversion_allsort, #configuration, #p`.

2. **H1 re-encuadrado (de "crítico: 100% del dinámico invisible" → "alto:
   pipelines #temp pierden lineage"):** el motor SÍ resuelve SQL dinámico
   (77/241 = 32% reconstruido en el código más hostil que existe; en WWI es
   100% con lineage completo — `unresolved_dynamic_sql_steps == 0`, ver
   agent-collab.md). La causa del lineage vacío es que el dinámico de FRK
   escribe casi siempre en `#temp`, que el motor excluye **por diseño**
   (`TableVariable_IsNotEmittedAsTable`); el puente estático existente
   (`InsertSelect_ThroughTempTable_BridgesDerivesFromToRealSourceTable`) no se
   aplica al dinámico reconstruido.

3. **H2 re-encuadrado — el bug de motor real:** el diseño dice que NO debe
   existir ningún nodo `:Table` temporal, y existen 11 → **algún camino de
   creación de tablas se salta la guarda `IsTempOrVariable`**. La política de
   temps hoy es inconsistente: ni exclusión total ni puente total.

4. **Tareas de motor derivadas (perfil Opus, con esta sección como spec):**
   - (a) Unificar política `#temp`: aplicar la guarda en TODOS los caminos de
     `GetOrCreateTable` (los 11 fantasmas → 0) o decidir puente universal.
   - (b) Extender el puente a través de `#temp` al SQL dinámico reconstruido —
     para código estilo FRK/mantenimiento es el hueco de mayor valor.

5. **Sin cambios:** H3 (1 solo `CALLS`; FRK invoca vía `EXEC @variable` —
   techo esperable del análisis estático), H4 (corchetes, cosmético), la
   validación de rendimiento (44K líneas / ~1,9 s, 0 errores de parseo — dato
   titular para el benchmark público) y la sección de "no medido".

---

**Tarea (a) RESUELTA (2026-07-04):** guarda `IsTempOrVariable` unificada en dos
caminos de `GraphExporter.cs` que se la saltaban:
  - **`ASSIGNED_FROM`** (`SELECT @var = Col FROM #temp`): no tenía guarda alguna
    → creaba un nodo `:Table` fantasma por cada temp leído hacia una variable.
    Origen de **los 11** fantasmas del corpus (`#StatementsToRun4FRKVersionCheck,
    #ai_prompts, #ai_providers, #checkversion, #checkversion_allsort,
    #configuration, #p, #PerfmonStats, #FileListParameters, #Headers,
    #SplitLogBackups`). Se añade `if (IsTempOrVariable(va.SourceTable)) continue;`.
  - **Nombres de temp con corchetes** (`INSERT INTO [#BlitzResults]`): `IsTempOrVariable`
    solo miraba `StartsWith('#')`, así que `[#...]` (identificador citado que emite
    ScriptDom) se colaba por la rama principal `WRITES_TO` (nodo `:Table` #12 +
    26 `WRITES_TO`). Se robustece `IsTempOrVariable` para normalizar (des-bracketar,
    último segmento de `tempdb..#x`) antes de comprobar `#`/`@`; ahora esos writes
    entran en `tempOrigin` (puente) en vez de materializar un temp.

  Verificación FRK (`input_final.json` → `graph_refixed.json`, binario nuevo,
  `--columns`): nodos `:Table` temporales **12 → 0** (los 11 + `[#BlitzResults]`);
  nodos `:Table` reales **126 → 126** (sin degradar), `READS_FROM` **619 → 619**
  (sin degradar). Deltas trazables 1:1 a la retirada de temps: `WRITES_TO` 43→17
  (−26, todos a `[#BlitzResults]`), `WRITES_COLUMN` 194→36 (−158, columnas de temp),
  `ASSIGNED_FROM` 106→35 (−71, orígenes temp), `DERIVES_FROM` 224→193 (−31, target
  en temp), `WORKFLOW_WRITES_TO` 7→6, `:Column` 262→204 (−58, columnas de temp).

  Suite **113/113** (`--filter "Category!=Oracle"`; incluye
  `TableVariable_IsNotEmittedAsTable`, `InsertSelect_ThroughTempTable_Bridges…`
  intactos + el nuevo `TempTablePolicy_NoTempTableNodeFromAnyPath`),
  `BadPracticesGateTests` 1/1, `eval/community-edge-cases/run.mjs` OK. Tarea (b)
  (puente a través de `#temp` para el SQL dinámico reconstruido) sigue pendiente.
