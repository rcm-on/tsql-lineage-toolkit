# Ejecución canónica — WideWorldImporters

Ejecución de referencia del T-SQL Lineage Toolkit. **Todas las cifras del README,
del artículo del blog y del post de LinkedIn deben salir de aquí.** Si un dato no
aparece en este documento, no está medido.

Esta es la **segunda** ejecución canónica. La primera (2026-07-26, commit
`487e15c`) congeló el motor con 3 arreglos integrados. Entre esa fecha y esta se
integraron **9 arreglos más** (12 en total; el detalle de cada uno está en
[`docs/corpus-multibase.md`](corpus-multibase.md)), así que todas las cifras de
grafo se han vuelto a medir de cero. Ninguna cifra de este documento se ha
heredado de la ejecución anterior sin volver a correr el comando.

| | |
|---|---|
| **Fecha** | 2026-08-01 |
| **Commit** | `c9ccd56` (rama `docs/ejecucion-canonica`, sale de `master`) |
| **Instancia** | `PC-Mon\SQLEXPRESS` (indicada como `.\SQLEXPRESS`) |
| **SQL Server** | Microsoft SQL Server 2025 (RTM-GDR) (KB5102333) — 17.0.1125.2 (X64), Express Edition (64-bit) on Windows 11 Home 10.0 (Build 26200) |
| **Base de datos** | WideWorldImporters |
| **.NET SDK** | 10.0.300 |

> **Cómo se ejecutó.** Sobre un *worktree* dedicado (`C:\temp\canon-master`) en la
> rama `docs/ejecucion-canonica`, que ya contiene los 12 arreglos integrados
> (mergeados sucesivamente sobre `master`) y ningún cambio sin commitear. Todos los
> comandos de este documento se han vuelto a ejecutar en esta sesión, contra la
> base de datos viva.

---

## 1. Por qué el artículo dice 47 y el dashboard dice 64

**No son dos ejecuciones distintas: son dos escalas de conteo sobre la misma ejecución.**
Esto no ha cambiado respecto a la primera ejecución canónica; se reconfirma aquí.

Contra el catálogo de la instancia:

| `sys` | Cantidad |
|---|---|
| Módulos (`sys.sql_modules` con tipo `P`/`FN`/`IF`/`TF`/`TR`/`V`) | **47** |
| — de tipo `P` (procedimiento) | 42 |
| — de tipo `FN` (función escalar) | 1 |
| — de tipo `IF` (función inline de tabla) | 1 |
| — de tipo `V` (vista) | 3 |
| — de tipo `TR` (trigger) | **0** |
| Tablas (`sys.tables`) | **48** |

Es decir: **`extract` no filtra nada.** WideWorldImporters no tiene ningún trigger
persistido en el catálogo, así que la consulta que pide los seis tipos de módulo
devuelve 47. Las 48 tablas son todas las de `sys.tables` (31 de negocio + 17 de
historial temporal).

Los 64 objetos y las 69 tablas aparecen **después**, al construir el grafo:

| Escala | Objetos | Tablas | Dónde se ve |
|---|---|---|---|
| **Entrada** — lo que existe en el catálogo | **47** | **48** | consola de `extract`, consola de `Analyzed …` |
| **Grafo** — lo que el análisis descubre | **64** | **69** | cabecera del dashboard, `nodes_by_label` |

La diferencia de objetos es **+17 triggers que no existen en el catálogo**: los
crea en tiempo de ejecución `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad`
mediante SQL dinámico. `47 + 17 = 64`.

La diferencia de tablas es **+21 nodos `Table` que no salen del DDL extraído**,
desglosados (medido sobre `graph_full.nodes/model.json`):

| Origen | Nodos `Table` |
|---|---:|
| Las 48 tablas reales extraídas con `--tables` | 48 |
| Catálogo del sistema referenciado (`sys.tables`, `sys.procedures`, `sys.indexes`, `sys.objects`, `sys.sequences`, `sys.filegroups`, `sys.database_principals`…) | 15 |
| Las 3 vistas `Website.*`, que además del nodo `SqlObject` reciben un nodo `Table` | 3 |
| `Warehouse.ColdRoomTemperatures_Backup` y `Warehouse.VehicleTemperatures_Backup`, creadas en runtime | 2 |
| Pseudo-tabla `OPENJSON(@FullSensorDataArray)` (ver §6, decisión sin arreglar) | 1 |
| **Total** | **69** |

> **Novedad respecto a la primera ejecución (que decía 68).** La pseudo-tabla
> `OPENJSON(@FullSensorDataArray)` se cuenta como `Table` en la cabecera del
> dashboard. Es una decisión de modelado documentada como abierta (item **#17** del
> inventario), no un defecto de esta ejecución: ya estaba ahí, solo que la
> ejecución anterior no la había desglosado.

**Conclusión, igual que en la primera ejecución: la escala buena es la del grafo,
y hay que citarla.** "47 procedimientos/funciones/vistas + 48 tablas extraídos" y
"64 objetos · 69 tablas en el grafo" son ambas correctas y describen cosas
distintas.

---

## 2. Salidas literales

### 2.1 `extract`

```bash
cd src/TSqlParser
dotnet run -- extract WideWorldImporters ../../input.json --server .\SQLEXPRESS --tables
```

Línea final (log completo en [`ejecucion-canonica/01-extract.txt`](ejecucion-canonica/01-extract.txt)):

```
Wrote 47 objects from WideWorldImporters to ../../input.json
...
Appended 48 table definitions to ../../input.json
```

### 2.2 Construcción del grafo

```bash
dotnet run -- ../../input.json ../../out/graph_full.json ../../out/workflows_full.json \
             --columns --sqlite --nodestore --graphify
```

Log completo en [`ejecucion-canonica/02-graph.txt`](ejecucion-canonica/02-graph.txt):

```
Graphify: 1593 nodes, 4382 edges -> ../../out/graph_full.graphify.json
NodeStore: 64 objects, 783 shared nodes, 4382 edges -> ../../out/graph_full.nodes
SQLite: 1593 nodes, 4382 edges (db=WideWorldImporters, project=WideWorldImporters) -> ../../out/graph_full.db
Analyzed 47 objects (47 ok, 0 parse errors)
Analyzed 48 table schemas (48 ok, 0 errors)
Graph: 1593 nodes, 4382 relationships -> ../../out/graph_full.json
```

Los cinco formatos dan **1593 / 4382** — frente a los 1529/4151 de la primera
ejecución canónica: **+64 nodos, +231 aristas**, atribuibles a los 9 arreglos
nuevos (el más visible: los 19 nodos `:BusinessRule` que antes no existían, más
las aristas `CONSTRAINS`/`HAS_RULE` que los conectan — ver §3 y §5).

### 2.3 `dotnet test`

```bash
dotnet test
```

Línea final (log completo en [`ejecucion-canonica/03-dotnet-test.txt`](ejecucion-canonica/03-dotnet-test.txt)):

```
Correctas! - Con error:     0, Superado:   182, Omitido:     0, Total:   182, Duración: 34 s - TSqlParser.Tests.dll (net10.0)
```

**182 casos de prueba, 0 fallos** (frente a 136 en la primera ejecución
canónica). De esos 182, **179 corren como gate en CI**; los 3 restantes son
`Oracle` y solo corren en local contra SQL Server vivo (ver README).

### 2.4 `enrich-from-plans` (Paso 3 del artículo) — no reproducido en esta sesión

La primera ejecución canónica capturó 33 planes estimados (`SET SHOWPLAN_XML ON`)
y midió un grafo enriquecido de 1538 nodos / 4230 relaciones. Esos `.xml` no
sobrevivieron entre worktrees (no están bajo control de versiones) y
regenerarlos no forma parte de este encargo, así que **esa cifra no se ha vuelto
a medir aquí** y no debe citarse como vigente: con el grafo base ya en 1593/4382,
el resultado enriquecido sería distinto. Queda pendiente para quien retome el
Paso 3 del artículo del blog.

### 2.5 `validate` — contraste contra la base de datos viva

```bash
dotnet run -- validate ../../out/graph_full.json --server .\SQLEXPRESS
```

Log completo en [`ejecucion-canonica/05-validate.txt`](ejecucion-canonica/05-validate.txt):

```
FK_TO edges in graph: 81
CALLS edges in graph: 12

=== WideWorldImporters ===

FK relationships in DB restricted to tables present in graph: 81
  In DB but missing from graph: 0
  In graph but not in DB (within scope): 0

CALLS (EXEC) relationships in DB restricted to analyzed objects: 12
  In DB but missing from graph: 0
```

**Cero ausencias y cero aristas fantasma, en ambos sentidos** — idéntico
resultado a la primera ejecución canónica, y es el que hay que citar: no es
"detectamos mucho", es "detectamos exactamente lo que hay".

Sobre el 81 frente a los **98** `FK_TO` de las stats: `validate` compara **pares
de tablas distintos**, y WWI tiene varias FK entre el mismo par más 3
auto-referencias. `SELECT COUNT(*) FROM sys.foreign_keys` devuelve **98**, y el
grafo tiene **98** aristas `FK_TO` — una por constraint. Las dos granularidades
cuadran con el catálogo.

### 2.6 Los "nodos huérfanos" y las reglas de negocio, verificados

`audit_report.json` marca **8 tablas huérfanas** (sin `WRITES_TO`, `READS_FROM`,
`FK_TO` ni `REFERENCES`) — el mismo conjunto que en la primera ejecución, sin
cambios: 5 tablas `_Archive` con `temporal_type = HISTORY` y las 3 vistas
`Website.*` sin lectores en WWI.

**Novedad frente a la primera ejecución: `summary.business_rules` ya no es 0.**
Dos de los 12 arreglos (§10 y §11 en la lista del encargo) modelan cada `WHERE`
como un nodo `:BusinessRule` (aristas `HAS_RULE`/`CONSTRAINS`), incluidos los que
viven dentro de una CTE o de una rama de `UNION`. Medido en `audit_report.json`:

```
"business_rules": 19
```

Antes: **0** (la etiqueta no se emitía). Son conceptos distintos de los 44 nodos
`Rule` (constraints de columna vía `GOVERNS`) y de los 112 hallazgos del panel de
riesgos — no mezclarlos.

**Cobertura de lineage de columna: 32 de 32 columnas de salida — 100%**
(`lineage_coverage` en `audit_report.json`), sin cambios respecto a la primera
ejecución.

---

## 3. Composición del grafo

De `out/graph_full.nodes/index.json` → `stats` (no estimado, leído del fichero):

| Etiqueta | Nodos | | Tipo de arista | Aristas |
|---|---:|---|---|---:|
| Column | 616 | | HAS_COLUMN | 648 |
| Step | 601 | | HAS_STEP | 601 |
| Variable | 80 | | ACTION | 601 |
| Table | 69 | | USES_VARIABLE | 534 |
| Parameter | 65 | | READS_FROM | 206 |
| SqlObject | 64 | | GOVERNS | 230 |
| Rule | 44 | | READS_COLUMN | 171 |
| Action | 19 | | BUILDS_SQL_FROM | 141 |
| BusinessRule | 19 | | CONTAINS | 143 |
| Schema | 10 | | WRITES_COLUMN | 119 |
| Workflow | 5 | | FILTERS_ON | 119 |
| Database | 1 | | REFERENCES / FK_TO | 98 / 98 |
| **Total** | **1593** | | DERIVES_FROM | 101 |
| | | | WRITES_TO | 82 |
| | | | CONSTRAINS | 78 |
| | | | DECLARES | 80 |
| | | | HAS_PARAMETER | 65 |
| | | | BELONGS_TO | 47 |
| | | | TARGETS | 38 |
| | | | AFFECTS | 36 |
| | | | WORKFLOW_WRITES_TO | 29 |
| | | | NESTED_IN | 22 |
| | | | HAS_RULE | 22 |
| | | | CONDITIONED_BY | 19 |
| | | | ON / CREATES | 17 / 17 |
| | | | CALLS | 12 |
| | | | ASSIGNED_FROM | 8 |
| | | | **Total** | **4382** |

`orphan_edges: 0`, `unknown_edge_types: []`, `unknown_labels: []` — sin cambios
respecto a la primera ejecución: el vocabulario cerrado del NodeStore sigue
cubriendo el 100% de lo que emite el grafo.

Las etiquetas nuevas frente a la primera ejecución son **`BusinessRule`** (19
nodos) y sus aristas **`HAS_RULE`** (22) y **`CONSTRAINS`** (78, antes existía
pero con otro origen). `Action` baja de 18/19 a 19 y `Step` sube de 566 a 601 —
los pasos de `WHERE` dentro de CTE/`UNION` que antes se perdían (§11 del
encargo) ahora se cuentan.

### Los tres formatos de salida cuadran

| Salida | Nodos | Aristas |
|---|---:|---:|
| `out/graph_full.json` | 1593 | 4382 |
| `out/graph_full.db` (SQLite) | 1593 | 4382 |
| `out/graph_full.nodes` (`index.json` → `stats`) | 1593 | 4382 |

El desglose del NodeStore encaja exactamente: **810 nodos propios** (64
SqlObject + 65 Parameter + 80 Variable + 601 Step, embebidos en cada
`object.json`) + **783 nodos compartidos** (616 Column + 69 Table + 44 Rule + 19
Action + 19 BusinessRule + 10 Schema + 5 Workflow + 1 Database) = **1593**.

---

## 4. Capturas

Rehechas todas contra este mismo `out/graph_full.json`, con
`dashboard/e2e/shots-readme.js` y `dashboard/e2e/shots-diagrams.js`
(Playwright/Chromium, viewport 1440×1000, `deviceScaleFactor: 2`).

| Fichero | Contenido |
|---|---|
| `docs/readme-impact.png` | `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad`, pantalla de impacto |
| `docs/readme-impact-chain.png` | cadena por niveles de `Configuration_ConfigureForEnterpriseEdition`, profundidad 5 |
| `docs/readme-flow.png` | flujograma de `Application.Configuration_ApplyAuditing` |
| `docs/readme-overview.png` | resumen general |
| `docs/readme-risks.png` | panel de riesgos |

La cabecera del dashboard en las capturas nuevas dice, literalmente:
**`64 objetos · 69 tablas · WideWorldImporters`** (y "Todos 133" en el buscador:
64 objetos + 69 tablas).

### Cifras que salen de las capturas

**Panel de riesgos** (`readme-risks.png`), literal:

> Se detectaron **112** hallazgos: **1** crítico, **20** alto, **44** medio, **47** bajo.

Por categoría: Integridad 40, Diseño 29, Mantenibilidad 18, Seguridad 13,
Rendimiento 7, Robustez 5 (suma 112). Frente a los **110** de la primera
ejecución (1 crítico, 20 alto, 43 medio, 46 bajo): **+2 hallazgos**, ambos
`medio`/`bajo` — consistente con que ninguno de los 9 arreglos nuevos toca el
motor de riesgos (`RiskAnalyzer`) directamente; el movimiento viene de que el
grafo subyacente cambió (más `Step` recuperados en CTE/`UNION`).

El único **crítico** sigue siendo la inyección SQL en
`Application.Configuration_ApplyColumnstoreIndexing`.

**Pantalla de impacto** (`readme-impact.png`), para
`DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad` — objeto no tocado
por ninguno de los 12 arreglos, así que sus métricas se reconfirman idénticas a
la primera ejecución:

| Métrica | Valor |
|---|---:|
| Complejidad ciclomática | 19 |
| Flujos de control | 18 |
| Profundidad de anidación | 1 |
| Pasos | 87 |
| Sentencias de SQL dinámico | 34 |
| Variables | 12 |
| Tablas distintas en las que escribe | 17 |
| Riesgos del objeto | 5 (1 alto, 3 medio, 1 bajo) |
| Acciones por tipo | 35 EXEC, 34 ALTER, 18 SELECT |

---

## 5. Qué cambia respecto a la primera ejecución canónica (2026-07-26)

| Dato | Primera ejecución (3 arreglos) | Esta ejecución (12 arreglos) | Motivo |
|---|---|---|---|
| Nodos del grafo | 1.529 | **1.593** | 9 arreglos nuevos, sobre todo `BusinessRule` + `Step` recuperados |
| Relaciones | 4.151 | **4.382** | idem |
| Tablas en el grafo | 68 | **69** | desglose correcto: incluye la pseudo-tabla `OPENJSON` (§1, #17) |
| Pruebas unitarias | 136 | **182** | 46 pruebas nuevas cubriendo los 9 arreglos |
| Hallazgos de riesgo | 110 (1 crítico, 20 alto, 43 medio, 46 bajo) | **112** (1 crítico, 20 alto, 44 medio, 47 bajo) | grafo subyacente más completo |
| `business_rules` | 0 (etiqueta no emitida) | **19** | arreglos #10/#11 del encargo (`WHERE` como `:BusinessRule`) |
| Objetos / tablas de entrada | 47 + 48 | 47 + 48 (**sin cambios**) | ningún arreglo toca `extract` |
| Errores de parseo | 0 | 0 | sin cambios |
| `validate` FK / EXEC | 98/98 · 12/12 · 0 ausencias | 98/98 · 12/12 · 0 ausencias | sin cambios |
| Cobertura de lineage de columna | 32/32 (100%) | 32/32 (100%) | sin cambios |

---

## 6. Incidencias detectadas en esta ejecución

Ninguna. La construcción del grafo, `dotnet test`, `validate` y las capturas
salieron limpias a la primera. El detalle de los 12 arreglos que separan esta
ejecución de la anterior —dónde estaba cada bug, cómo se verificó, qué se dejó
abierto a propósito— vive en [`docs/corpus-multibase.md`](corpus-multibase.md),
que es donde se descubrieron.

Del inventario de decisiones abiertas (no bugs) que arrastra el proyecto, la
única que toca directamente a esta ejecución es la ya mencionada en §1: la
pseudo-tabla `OPENJSON(@FullSensorDataArray)` contada como `Table` en la
cabecera del dashboard. Las demás (IDs de paso posicionales, tablas temporales
sin nodo, `line_no` de SQL dinámico reconstruido) están documentadas en
`.claude/tareas/ESTADO-continuacion.md` y no cambian con esta publicación.

---

## 7. Reproducir

```bash
git worktree add ../canon docs/ejecucion-canonica   # rama creada desde master
cd ../canon/src/TSqlParser

dotnet run -- extract WideWorldImporters ../../input.json --server .\SQLEXPRESS --tables
dotnet run -- ../../input.json ../../out/graph_full.json ../../out/workflows_full.json \
             --columns --sqlite --nodestore --graphify
dotnet run -- validate ../../out/graph_full.json --server .\SQLEXPRESS
cd ../.. && dotnet test

# capturas
cd dashboard/e2e && npm install && npx playwright install chromium
node shots-readme.js && node shots-diagrams.js
```

Logs completos sin editar en [`ejecucion-canonica/`](ejecucion-canonica/).

---

## 8. Estado de publicación

La rama `docs/ejecucion-canonica` sale de `master` y contiene los 12 arreglos,
los artefactos regenerados, las 5 capturas y esta documentación. Pendiente en
esta misma sesión: `docs/corpus-multibase.md` (cifras de los otros tres corpus +
marcar arreglados los ítems 4 y 6-12), los dos artículos del blog, la imagen de
LinkedIn, y el `push` + merge a `master`.
