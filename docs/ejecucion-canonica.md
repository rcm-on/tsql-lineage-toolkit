# Ejecución canónica — WideWorldImporters

Ejecución de referencia del T-SQL Lineage Toolkit. **Todas las cifras del README,
del artículo del blog y del post de LinkedIn deben salir de aquí.** Si un dato no
aparece en este documento, no está medido.

| | |
|---|---|
| **Fecha** | 2026-07-26 |
| **Commit** | `e9eba57` = **`master`**, la rama desde la que se publica |
| **Rama de trabajo** | `docs/ejecucion-canonica` (creada desde `master`) |
| **Instancia** | `PC-Mon\SQLEXPRESS` (indicada como `.\SQLEXPRESS`) |
| **SQL Server** | Microsoft SQL Server 2025 (RTM-GDR) (KB5102333) — 17.0.1125.2 (X64), Express Edition (64-bit) on Windows 10 Home 10.0 (Build 26200) |
| **Base de datos** | WideWorldImporters |
| **.NET SDK** | 10.0.300 |

> **Cómo se ejecutó.** Desde un *worktree* limpio creado a partir de **`master`**,
> contra la base de datos viva. El árbol de trabajo principal está a medio refactor
> (`src/Parser.Contracts`, `src/ParserGeneral`, `GlobalUsings.cs` sin commitear) y
> **no compila** (35 errores `CS0246`); esa rama no interviene aquí para nada.
>
> Sobre `master` se aplicó **un único cambio de código**: añadir `"CREATES"` y
> `"ON"` a `KnownEdgeTypes` en `NodeStoreExporter.cs` (§6, #3). No mueve ninguna
> cifra — `graph_full.json` sale byte a byte idéntico con y sin él, verificado con
> `Get-FileHash`, así que las capturas siguen siendo capturas de este mismo grafo.
>
> **Todo lo de este documento sale de la rama que se va a publicar.** No hay
> ninguna cifra tomada de código sin commitear.

---

## 1. Por qué el artículo dice 47 y el dashboard dice 64

**No son dos ejecuciones distintas: son dos escalas de conteo sobre la misma ejecución.**
Ninguna de las dos está mal; lo que faltaba era decir cuál es cuál.

Contra el catálogo de la instancia (medido con `sqlcmd`, 2026-07-26):

| `sys` | Cantidad |
|---|---|
| Módulos (`sys.sql_modules` con tipo `P`/`FN`/`IF`/`TF`/`TR`/`V`) | **47** |
| — de tipo `P` (procedimiento) | 42 |
| — de tipo `FN` (función escalar) | 1 |
| — de tipo `IF` (función inline de tabla) | 1 |
| — de tipo `V` (vista) | 3 |
| — de tipo `TR` (trigger) | **0** |
| Tablas (`sys.tables`) | **48** |

Es decir: **`extract` no filtra nada.** Su consulta pide los seis tipos de módulo
y la base entera devuelve 47, porque **WideWorldImporters no tiene ningún trigger
persistido en el catálogo**. Las 48 tablas son todas las de `sys.tables`
(31 de negocio + 17 de historial temporal). Ni flags distintos, ni filtro de
esquemas, ni la base creció: `47 + 48` es exactamente lo que imprime `extract` hoy.

Los 64 y los 68 aparecen **después**, al construir el grafo:

| Escala | Objetos | Tablas | Dónde se ve |
|---|---|---|---|
| **Entrada** — lo que existe en el catálogo | **47** | **48** | consola de `extract`, consola de `Analyzed …` |
| **Grafo** — lo que el análisis descubre | **64** | **68** | cabecera del dashboard, `nodes_by_label` |

La diferencia de objetos es **+17 triggers que no existen en el catálogo**:
los crea en tiempo de ejecución `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad`
mediante SQL dinámico. El grafo los materializa como nodos `SqlObject`/`Trigger`
colgando de 17 aristas `CREATES` desde ese procedimiento. `47 + 17 = 64`.

La diferencia de tablas es **+20 nodos `Table` que no salen del DDL extraído**,
desglosados (medido sobre `graph_full.db`):

| Origen | Nodos `Table` |
|---|---:|
| Las 48 tablas reales extraídas con `--tables` | 48 |
| Catálogo del sistema referenciado (`sys.tables`, `sys.procedures`, `sys.indexes`, `sys.objects`, `sys.sequences`, `sys.filegroups`, `sys.database_principals`…) | 15 |
| Las 3 vistas `Website.*`, que además del nodo `SqlObject` reciben un nodo `Table` | 3 |
| `Warehouse.ColdRoomTemperatures_Backup` y `Warehouse.VehicleTemperatures_Backup`, creadas en runtime | 2 |
| **Total** | **68** |

**Conclusión: la ejecución buena es la de abajo, y hay que citar la escala.**
"47 procedimientos/funciones/vistas + 48 tablas extraídos" y "64 objetos · 68 tablas
en el grafo" son ambas correctas y describen cosas distintas. Lo que no vale es
mezclarlas en el mismo párrafo, que es justo lo que hace hoy el artículo.

> Nota aparte: la captura antigua decía "64 objetos · **69** tablas". Esas 69
> venían del artefacto `out/graph_full.json` generado con el árbol de trabajo sin
> commitear, no con `487e15c`. Ver §6.

---

## 2. Salidas literales

### 2.1 `extract`

```bash
cd src/TSqlParser
dotnet run -- extract WideWorldImporters ../../input.json --server .\SQLEXPRESS --tables
```

```
Wrote 47 objects from WideWorldImporters to ../../input.json
  + Application.Cities: ok
  + Application.Cities_Archive: ok
  + Application.Countries: ok
  + Application.Countries_Archive: ok
  + Application.DeliveryMethods: ok
  + Application.DeliveryMethods_Archive: ok
  + Application.PaymentMethods: ok
  + Application.PaymentMethods_Archive: ok
  + Application.People: ok
  + Application.People_Archive: ok
  + Application.StateProvinces: ok
  + Application.StateProvinces_Archive: ok
  + Application.SystemParameters: ok
  + Application.TransactionTypes: ok
  + Application.TransactionTypes_Archive: ok
  + Purchasing.PurchaseOrderLines: ok
  + Purchasing.PurchaseOrders: ok
  + Purchasing.SupplierCategories: ok
  + Purchasing.SupplierCategories_Archive: ok
  + Purchasing.Suppliers: ok
  + Purchasing.Suppliers_Archive: ok
  + Purchasing.SupplierTransactions: ok
  + Sales.BuyingGroups: ok
  + Sales.BuyingGroups_Archive: ok
  + Sales.CustomerCategories: ok
  + Sales.CustomerCategories_Archive: ok
  + Sales.Customers: ok
  + Sales.Customers_Archive: ok
  + Sales.CustomerTransactions: ok
  + Sales.InvoiceLines: ok
  + Sales.Invoices: ok
  + Sales.OrderLines: ok
  + Sales.Orders: ok
  + Sales.SpecialDeals: ok
  + Warehouse.ColdRoomTemperatures: ok
  + Warehouse.ColdRoomTemperatures_Archive: ok
  + Warehouse.Colors: ok
  + Warehouse.Colors_Archive: ok
  + Warehouse.PackageTypes: ok
  + Warehouse.PackageTypes_Archive: ok
  + Warehouse.StockGroups: ok
  + Warehouse.StockGroups_Archive: ok
  + Warehouse.StockItemHoldings: ok
  + Warehouse.StockItems: ok
  + Warehouse.StockItems_Archive: ok
  + Warehouse.StockItemStockGroups: ok
  + Warehouse.StockItemTransactions: ok
  + Warehouse.VehicleTemperatures: ok

Appended 48 table definitions to ../../input.json
```

### 2.2 Construcción del grafo

Se emiten **todos** los formatos en una sola pasada, para que no quede en `out/`
ningún artefacto de otra ejecución (ver §6.5):

```bash
dotnet run -- ../../input.json ../../out/graph_full.json ../../out/workflows_full.json \
             --columns --sqlite --nodestore --graphify
```

```
Graphify: 1529 nodes, 4151 edges -> ../../out/graph_full.graphify.json
NodeStore: 64 objects, 754 shared nodes, 4151 edges -> ../../out/graph_full.nodes
SQLite: 1529 nodes, 4151 edges (db=WideWorldImporters, project=WideWorldImporters) -> ../../out/graph_full.db
Analyzed 47 objects (47 ok, 0 parse errors)
Analyzed 48 table schemas (48 ok, 0 errors)
Graph: 1529 nodes, 4151 relationships -> ../../out/graph_full.json
```

Los cinco formatos dan **1529 / 4151**.

### 2.3 `dotnet test`

```bash
dotnet test
```

Línea final (log completo en [`ejecucion-canonica/03-dotnet-test.txt`](ejecucion-canonica/03-dotnet-test.txt)):

```
Correctas! - Con error:     0, Superado:   136, Omitido:     0, Total:   136, Duración: 41 s - TSqlParser.Tests.dll (net10.0)
```

**136 casos de prueba, 0 fallos.** Ese es el número que va al README: los
`[Fact]`/`[Theory]` contados a mano dan menos porque un `[Theory]` expande a
varios casos.

### 2.4 `enrich-from-plans` (Paso 3 del artículo)

```bash
dotnet run -- enrich-from-plans ../../out/graph_full.json ../../out/graph_enriched.json <33 planes .xml>
```

Línea final (log completo en [`ejecucion-canonica/04-enrich-from-plans.txt`](ejecucion-canonica/04-enrich-from-plans.txt)):

```
Plans: 33  Procs matched: 30  Confirmed: 60  Discovered: 79 -> ../../out/graph_enriched.json
```

Grafo enriquecido: **1538 nodos, 4230 relaciones** (frente a 1529/4151 del estático).

**Cómo se obtuvieron los planes — y por qué no son los del artículo.** El artículo
describe ejecutar los procedimientos para poblar la caché de planes. Eso no se ha
hecho: los procedimientos clave de WWI son destructivos sobre la propia base
(`DeactivateTemporalTablesBeforeDataLoad` desactiva versionado temporal y borra
triggers, los `Configuration_*` alteran la configuración de la instancia). En su
lugar se capturaron **planes estimados** vía `SET SHOWPLAN_XML ON`, que compilan
sin ejecutar. `ExecutionPlanParser` los soporta explícitamente; la diferencia es
que traen `EstimateRows` y no `ActualRows` — todos los planes salen con
`actual=False`.

Cobertura: **33 planes de 35 intentos**, sobre los 15 procedimientos sin
parámetros, los 12 `Integration.Get*Updates`, los 5 `Website.SearchFor*` y las 3
vistas. Los 2 que fallan lo hacen con el error 13597 de SQL Server (restricción
sobre tablas temporales) al compilar
`DataLoadSimulation.ReactivateTemporalTablesAfterDataLoad`. Los `.xml` quedan en
`out/plans/`.

**Limitación honesta:** los procedimientos cuyo cuerpo es íntegramente SQL
dinámico (los `Configuration_*`) producen un plan sin accesos a tabla — el SQL
dinámico no se compila en tiempo de estimación. El enriquecimiento real viene de
los `Integration.Get*` y de las vistas. Con planes *reales* las cifras de
`Confirmed`/`Discovered` serían distintas (mayores).

### 2.5 `validate` — contraste contra la base de datos viva

El grafo no se cree a sí mismo: se contrasta contra el catálogo de SQL Server.

```bash
dotnet run -- validate ../../out/graph_full.json --server .\SQLEXPRESS
```

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

**Cero ausencias y cero aristas fantasma, en ambos sentidos.** Es el resultado más
fuerte de toda la ejecución y el que conviene citar: no es "detectamos mucho", es
"detectamos exactamente lo que hay".

Sobre el 81 frente a los **98** `FK_TO` de las stats: no es una discrepancia.
`validate` compara **pares de tablas distintos**, y WWI tiene varias FK entre el
mismo par (`Sales.Orders` referencia dos veces a `Application.People`) más 3
auto-referencias. Medido contra la base: `SELECT COUNT(*) FROM sys.foreign_keys`
devuelve **98**, y el grafo tiene **98** aristas `FK_TO` — una por constraint.
Las dos granularidades cuadran con el catálogo.

### 2.6 Los "nodos huérfanos", verificados uno a uno

`audit_report.json` marca **8 tablas huérfanas** (sin `WRITES_TO`, `READS_FROM`,
`FK_TO` ni `REFERENCES`). Comprobadas contra el catálogo, las 8 son correctas:

| Nodo huérfano | Comprobación en la base | ¿Correcto? |
|---|---|---|
| `Application.DeliveryMethods_Archive`, `Warehouse.ColdRoomTemperatures_Archive`, `Warehouse.Colors_Archive`, `Warehouse.PackageTypes_Archive`, `Warehouse.StockGroups_Archive` | `sys.tables.temporal_type = 1` (**HISTORY**) en las 5 | Sí — las gestiona SQL Server, ningún DML las referencia |
| `Website.Customers`, `Website.Suppliers`, `Website.VehicleTemperatures` | `sys.sql_expression_dependencies` devuelve **0** referencias a las tres | Sí — nadie las lee en WWI (son los *table twins* de §6) |

Ningún huérfano es un fallo de extracción. **Cobertura de lineage de columna: 32
de 32 columnas de salida con linaje resuelto — 100%** (`lineage_coverage` en
`audit_report.json`).

> Un aviso para no publicar un dato engañoso: `audit_report.json` trae
> `summary.business_rules: 0`. Es correcto pero confuso — cuenta nodos con etiqueta
> `BusinessRule`, que `master` todavía no emite. Lo que sí hay son **42 nodos
> `Rule`** (constraints que gobiernan columnas, vía `GOVERNS`) y **110 hallazgos**
> de riesgo. Tres conceptos distintos; no mezclarlos.

---

## 3. Composición del grafo

De `out/graph_full.nodes/index.json` → `stats` (no estimado, leído del fichero):

| Etiqueta | Nodos | | Tipo de arista | Aristas |
|---|---:|---|---|---:|
| Column | 611 | | HAS_COLUMN | 643 |
| Step | 566 | | HAS_STEP | 566 |
| Variable | 80 | | ACTION | 566 |
| Table | 68 | | USES_VARIABLE | 523 |
| Parameter | 65 | | READS_FROM | 205 |
| SqlObject | 64 | | GOVERNS | 198 |
| Rule | 42 | | READS_COLUMN | 171 |
| Action | 18 | | BUILDS_SQL_FROM | 141 |
| Schema | 9 | | CONTAINS | 141 |
| Workflow | 5 | | WRITES_COLUMN | 119 |
| Database | 1 | | FILTERS_ON | 114 |
| **Total** | **1529** | | REFERENCES / FK_TO | 98 / 98 |
| | | | DERIVES_FROM | 96 |
| | | | WRITES_TO | 82 |
| | | | DECLARES | 80 |
| | | | HAS_PARAMETER | 65 |
| | | | BELONGS_TO | 47 |
| | | | TARGETS | 38 |
| | | | AFFECTS | 36 |
| | | | WORKFLOW_WRITES_TO | 29 |
| | | | NESTED_IN | 22 |
| | | | CONDITIONED_BY | 19 |
| | | | ON / CREATES | 17 / 17 |
| | | | CALLS | 12 |
| | | | ASSIGNED_FROM | 8 |
| | | | **Total** | **4151** |

`orphan_edges: 0`, `unknown_edge_types: []`, `unknown_labels: []` — el vocabulario
cerrado del NodeStore cubre ahora el 100% de lo que emite el grafo (ver §6, #3).

### Los tres formatos de salida cuadran

| Salida | Nodos | Aristas |
|---|---:|---:|
| `out/graph_full.json` | 1529 | 4151 |
| `out/graph_full.db` (SQLite, `select count(*)`) | 1529 | 4151 |
| `out/graph_full.nodes` (`index.json` → `stats`) | 1529 | 4151 |

El desglose del NodeStore encaja exactamente: **775 nodos propios** (64 SqlObject
+ 65 Parameter + 80 Variable + 566 Step, embebidos en cada `object.json`) +
**754 nodos compartidos** (611 Column + 68 Table + 42 Rule + 18 Action +
9 Schema + 5 Workflow + 1 Database) = **1529**.

---

## 4. Capturas

Rehechas todas contra este mismo `out/graph_full.json`, con
`dashboard/e2e/shots-readme.js` y `dashboard/e2e/shots-diagrams.js`
(Playwright/Chromium, viewport 1440×1000, `deviceScaleFactor: 2`).

| Fichero | Contenido | Píxeles |
|---|---|---|
| `docs/readme-impact.png` | `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad`, pantalla de impacto | 2880×2012 |
| `docs/readme-impact-chain.png` | cadena por niveles de `Configuration_ConfigureForEnterpriseEdition`, profundidad 5 | 2092×508 |
| `docs/readme-flow.png` | flujograma de `Application.Configuration_ApplyAuditing` | 1058×4368 |
| `docs/readme-overview.png` | resumen general | 2880×2000 |
| `docs/readme-risks.png` | panel de riesgos | 2880×2012 |

La cabecera del dashboard en las capturas nuevas dice, literalmente:
**`64 objetos · 68 tablas · WideWorldImporters`**.

**Copia al blog — pendiente, no se puede hacer desde aquí.** El repositorio del
blog (`quartz/`) no está en esta máquina; se buscó bajo `C:\MisCosas` y en el
resto de `C:\`. Las cuatro imágenes quedan preparadas y **ya renombradas** en
[`docs/blog-labs-tsql/`](blog-labs-tsql/), así que la copia es un solo comando:

```bash
cp docs/blog-labs-tsql/*.png <blog>/quartz/static/labs/tsql/
# impacto.png  impacto-niveles.png  flujo.png  overview.png
```

### Cifras que salen de las capturas

**Panel de riesgos** (`readme-risks.png`), literal:

> Se detectaron **110** hallazgos: **1** crítico, **20** alto, **43** medio, **46** bajo.

Por categoría: Integridad 38, Diseño 29, Mantenibilidad 18, Seguridad 13,
Rendimiento 7, Robustez 5 (suma 110). El único **crítico** es una inyección SQL en
`Application.Configuration_ApplyColumnstoreIndexing` (`@SQL ← sys.indexes(name)`,
SQL dinámico construido desde datos de tabla).

> **Hallazgos ≠ nodos `Rule`.** El grafo tiene **42** nodos `Rule`; el panel
> reporta **110 hallazgos**. Son conceptos distintos y no deben mezclarse: un
> hallazgo es una instancia de regla aplicada a un componente.

**Pantalla de impacto** (`readme-impact.png`), para
`DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad` — todas las
afirmaciones del README quedan confirmadas en la ejecución canónica:

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

### El contraste AST vs. grep, medido

El README afirmaba que "un grep cuenta **3** flujos" en
`DeactivateTemporalTablesBeforeDataLoad`. Ese 3 no sale de ninguna medición y
además mezclaba dos métricas distintas (flujos de control con sentencias
dinámicas). Medido sobre el fuente real (706 líneas, tomado de `input.json`),
contando tokens en el texto crudo frente al texto con los literales de cadena
eliminados:

| Token | `grep` sobre el texto crudo | Fuera de los strings (real) | Lo que reporta el AST |
|---|---:|---:|---:|
| `EXECUTE (@SQL)` | 34 | 34 | **34** sentencias de SQL dinámico |
| `IF` | 52 | **18** | **18** flujos de control |
| `BEGIN` | 53 | 19 | — |

Ese es el contraste bueno, y ahora está medido: **34 de los 52 `IF` que ve un
grep viven dentro de los strings que el procedimiento está construyendo.** El AST
se queda con los 18 reales, que es exactamente lo que muestra la captura.
(De paso: el fuente no contiene ni un solo `EXEC(@SQL)` — usa la forma
`EXECUTE (@SQL)`, que un grep mal escrito se pierde entera.)

---

## 5. Qué se retira o se corrige en el README

| Dato | Antes | Ahora | Motivo |
|---|---|---|---|
| Nodos del grafo | 1.529 | 1.529 | correcto, se confirma |
| Relaciones | 4.151 | 4.151 | correcto, se confirma |
| Objetos analizados | 47 + 48 tablas | 47 + 48 tablas (**entrada**), 64 · 68 (**grafo**) | faltaba distinguir la escala |
| Errores de parseo | 0 | 0 | correcto, se confirma |
| Pruebas unitarias | 119 | **136** | lo que reporta `dotnet test` |
| Hallazgos de riesgo | 112 (1 crítico, 20 altos) | **110** (1 crítico, 20 altos, 43 medios, 46 bajos) | medido en la captura nueva |
| "un grep cuenta 3 flujos" | 3 | **52 tokens `IF` en crudo → 18 reales** | el 3 no salía de ninguna medición, y mezclaba flujos con sentencias dinámicas |
| Versión de SQL Server | "SQL Server 2025" | SQL Server 2025 (RTM-GDR) 17.0.1125.2, Express | precisión |

Cifras del **artículo del blog** (1.398 nodos / 3.476 relaciones) y del artefacto
que había en disco (1.593 / 4.282): **ambas se retiran.** La primera es de un
commit anterior; la segunda, de código sin commitear.

---

## 6. Incidencias detectadas

### Arregladas en esta ejecución

**#3 — `model.json` no declaraba `CREATES` ni `ON`.** El propio `index.json` lo
denunciaba: `"unknown_edge_types": ["CREATES", "ON"]`, 17 aristas cada uno, justo
la capa de triggers dinámicos que es el argumento principal de la herramienta.
Añadidos a `KnownEdgeTypes` en `NodeStoreExporter.cs` **sobre `master`** (y también
en `Parser.Contracts/Vocab.cs`, para que el refactor en curso no lo pierda).
**No mueve ninguna cifra**:
`graph_full.json` sale byte a byte idéntico (verificado con `Get-FileHash`), así
que las capturas siguen siendo válidas. Ahora `index.json` dice
`"unknown_edge_types": []` y `"unknown_labels": []`. `dotnet test` sigue en 136/136.

**#5 — artefactos huérfanos en `out/`.** `graph_full.graphify.json` y
`workflows_full.json` eran del 20/06/2026 y no los producía ningún comando
documentado: quien los abriera veía cifras que no cuadraban con el README — el
mismo fallo que este encargo venía a eliminar. La ejecución canónica emite ahora
**todos** los formatos en una sola pasada (`--graphify` + tercer argumento
posicional), y los cinco dan 1529/4151.

### No es una incidencia — decisión de modelado sin documentar

**#4 — las 3 vistas `Website.*` tienen además un nodo `Table`.** Parecía una
duplicación, pero es **deliberado y está razonado en el código**
(`GraphExporter.BuildViewLineage`, que lo llama el *"phantom table twin"* de la
vista): las columnas de salida conservan el id con esquema de tabla
(`:table:<vista>:column:<c>`) para que un `SELECT c FROM <vista>` aguas abajo
aterrice en **el mismo nodo `Column`** y el lineage no se corte. El `HAS_COLUMN`
desde el `SqlObject` se añadió después precisamente para que las columnas también
sean alcanzables desde el nodo de la vista.

En WWI nadie lee de esas vistas, así que el nodo parece inerte (solo `CONTAINS`
entrante y `HAS_COLUMN` saliente, 0 `READS_FROM`); en una base que sí las lea,
quitarlo **rompería el lineage de columna a través de vistas**. No se toca. Lo que
faltaba era decirlo: **un nodo `Table` no es siempre una tabla base** — el conteo
de "tablas" del dashboard incluye las vistas. Los 32 nodos `Column` **no** están
duplicados: son los mismos ids compartidos por ambos nodos.

### Abiertas, fuera del alcance de este encargo

1. **El árbol de trabajo no compila.** 35 errores `CS0246` (`GraphNode`,
   `GraphPayload`) por el refactor a medias hacia `src/Parser.Contracts`. Está
   **sin commitear**, así que no afecta a quien clone el repositorio, pero
   mientras siga así ningún artefacto regenerado en local es reproducible.
2. **`out/` no está bajo control de versiones**, así que nada avisó de que
   `graph_full.json` había derivado a 1593/4282 (y de ahí salió la captura con
   "69 tablas"). Merece un gate: regenerar y comparar contra lo commiteado.
   Según la convención del proyecto, ese gate va como prueba xUnit, no como script
   de Node.

---

## 7. Reproducir

```bash
git worktree add ../canon docs/ejecucion-canonica   # rama creada desde master
cd ../canon/src/TSqlParser

dotnet run -- extract WideWorldImporters ../../input.json --server .\SQLEXPRESS --tables
dotnet run -- ../../input.json ../../out/graph_full.json ../../out/workflows_full.json \
             --columns --sqlite --nodestore --graphify
dotnet run -- validate ../../out/graph_full.json --server .\SQLEXPRESS
dotnet run -- enrich-from-plans ../../out/graph_full.json ../../out/graph_enriched.json ../../out/plans/*.xml
cd ../.. && dotnet test

# capturas
cd dashboard/e2e && node shots-readme.js && node shots-diagrams.js
```

Logs completos sin editar en [`ejecucion-canonica/`](ejecucion-canonica/).

---

## 8. Estado de publicación

La rama `docs/ejecucion-canonica` sale de `master` y contiene **todo**: el arreglo
del `#3`, los artefactos regenerados, las 5 capturas y esta documentación. No
queda ningún paso de código pendiente.

**El blog también está hecho**, en
`C:\Users\Mon-Pc\OneDrive\Projects\rcm-on\quarz-blog`:

- Las 4 capturas copiadas a `quartz/static/labs/tsql/`.
- `content/02 Laboratorios/tsql-lineage-toolkit.md`: cifras de los pasos 1, 2 y 3
  corregidas, añadida la explicación del 47 vs 64 justo donde estaba la
  contradicción con la captura, el paso `validate` contra el catálogo y la nota
  honesta sobre planes estimados.
- `content/04 Arquitectura IA/datos-navegables-para-agentes.md`: **tenía las mismas
  cifras viejas** (1.398/3.476, "21 KB", "76 veces", "3,8 segundos"). Era una
  **cuarta** fuente que nadie había contado. Corregida.
- `private/linkedin/tsql-lineage-toolkit/texto-post.md`: cifras corregidas y
  reenfocado sobre los 17 triggers invisibles y el 98/98 contra el catálogo.
- `private/PENDIENTE-tsql-cifras.md`: cerrado, con el resumen de la causa.

Queda **una sola cosa**: regenerar `private/linkedin/tsql-lineage-toolkit/imagen.png`
con `_generador/imagen.template.py`; sus tres tarjetas llevan cifras viejas.
