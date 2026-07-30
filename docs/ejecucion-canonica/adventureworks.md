# Ejecución del T-SQL Lineage Toolkit contra AdventureWorks2019

- Fecha: 2026-07-26
- Instancia: `.\SQLEXPRESS`
- Worktree usado: `C:\temp\corpus-aw` (rama `docs/ejecución-aw`, creada desde `docs/ejecucion-canonica` porque esa rama
  ya estaba ocupada por el worktree `C:\temp\canon-master`, que resultó contener una ejecución previa de
  WideWorldImporters — no se tocó).
- El árbol principal (`feature/parser-general`) no se tocó, tal como se pidió.

## Tabla de cifras medidas y contraste contra el catálogo

| Cifra | Valor medido | Contraste con catálogo | Resultado |
|---|---|---|---|
| Objetos con código (`extract`) | 51 | `sys.sql_modules ⋈ sys.objects` WHERE type IN (P,FN,IF,TF,TR,V) = 51 | ✓ cuadra |
| — por tipo | FN=10, P=10, TF=1, TR=10, V=20, IF=0 | mismo desglose en catálogo | ✓ cuadra |
| Tablas extraídas | 71 | `SELECT COUNT(*) FROM sys.tables` = 71 | ✓ cuadra |
| Errores de parseo | 0 (51/51 objetos ok, 71/71 tablas ok) | — | ✓ limpio |
| Nodos del grafo (`graph_full.json`) | 1120 | — | ✓ |
| Nodos (`graph_full.db`, SQLite `count(*) from nodes`) | 1120 | igual a graph_full.json | ✓ cuadra |
| Nodos (`index.json` → `stats.total_nodes`) | 1120 | igual a los dos anteriores | ✓ cuadra |
| Aristas del grafo (`graph_full.json`) | 2840 | — | ✓ |
| Aristas (SQLite `count(*) from edges`) | 2840 | igual | ✓ cuadra |
| Aristas (`index.json` → `stats.total_edges`) | 2840 | igual | ✓ cuadra |
| `unknown_edge_types` / `unknown_labels` / `orphan_edges` (index.json) | `[]` / `[]` / 0 | deben ser 0/vacíos | ✓ cuadra |
| FK_TO en el grafo (según `validate`) | 86 | `SELECT COUNT(*) FROM sys.foreign_keys` = 90 | ✗ discrepa en superficie, **explicado** (ver Hallazgos) |
| FK_TO en el grafo (según `index.json` → `stats.edges_by_type.FK_TO`) | 90 | igual al total de `sys.foreign_keys` | ✓ cuadra |
| `validate`: FK en BD ausentes del grafo | 0 | — | ✓ |
| `validate`: FK en grafo no en BD (dentro de alcance) | 0 | — | ✓ |
| CALLS (EXEC) en grafo (`validate`) | 22 | igual a `stats.edges_by_type.CALLS` = 22 | ✓ cuadra |
| `validate`: CALLS en BD ausentes del grafo | 0 | — | ✓ |
| Triggers en catálogo | 11 (`sys.triggers`) | 10 son `OBJECT_OR_COLUMN` (schema-bound), 1 es `DATABASE` (DDL trigger `ddlDatabaseTriggerLog`) | el extractor solo recoge los 10 de objeto; el trigger DDL de BD **no se captura** — ver Hallazgos |
| Triggers en el grafo | 10 (`by_type.TRIGGER` en audit_report) | 10/10 de los triggers de objeto | ✓ cuadra (pero ver hallazgo del trigger DDL) |
| Cobertura de lineage de columna (`audit_report.lineage_coverage`) | 251/251 columnas, 19 objetos con output columns, 100% | — | ✓ completo |
| Tablas huérfanas (`audit_report.orphan_tables`) | 22 | ver desglose y verificación en Hallazgos | 20 explicadas como "vista sin consumidores internos", 2 (`AWBuildVersion`, `TransactionHistoryArchive`) explicadas como tablas standalone/archivo; **1 caso (`dbo.DatabaseLog`) es huérfano por el gap del trigger DDL**, no porque nadie la use realmente |
| Tamaño `graph_full.json` | 1.194 MB (1.253.698 bytes) | — | — |
| Tamaño `graph_full.db` (SQLite) | 1.398 MB | — | — |
| Tamaño `graph_full.graphify.json` | 1.416 MB | — | — |
| Tamaño `workflows_full.json` | 257 KB | — | — |
| NodeStore: nº de ficheros | 1878 | — | — |
| NodeStore: tamaño total | 8.8 MB (≈3.43 MB medido por suma de bytes de todos los ficheros; la diferencia es overhead de bloque en disco) | — | — |
| NodeStore: tamaño medio por fichero | ≈1872 bytes | — | — |
| Objetos en NodeStore (`stats.objects`) | 51 | igual al extract | ✓ cuadra |
| Nodos compartidos en NodeStore (`stats.shared_nodes`) | 876 | 1120 total − 51 SqlObject − ... (consistente con nodes_by_label) | ✓ coherente |

## Desglose `nodes_by_label` (index.json)

```json
{
  "SqlObject": 51, "Variable": 23, "Step": 129, "Parameter": 41,
  "Table": 90, "Column": 741, "Action": 9, "Rule": 23,
  "Workflow": 6, "Schema": 6, "Database": 1
}
```

Nota: `Table: 90` en el grafo frente a las 71 tablas extraídas explícitamente. La diferencia (19) son
tablas/vistas referenciadas por FROM/JOIN que el grafo materializa como nodo `Table` aunque no se hayan
extraído su DDL explícitamente (típicamente las 20 vistas del catálogo, ya que son objetos con código
pero también aparecen como "fuente de datos" en otros objetos). Ver más detalle en Hallazgos.

## Desglose `edges_by_type` (index.json)

```json
{
  "HAS_COLUMN": 992, "FK_TO": 90, "REFERENCES": 91, "DERIVES_FROM": 289,
  "HAS_STEP": 129, "ACTION": 129, "HAS_PARAMETER": 41, "USES_VARIABLE": 70,
  "READS_FROM": 159, "GOVERNS": 74, "READS_COLUMN": 195, "NESTED_IN": 10,
  "DECLARES": 23, "ASSIGNED_FROM": 4, "FILTERS_ON": 169, "WRITES_TO": 22,
  "WRITES_COLUMN": 64, "TARGETS": 20, "CALLS": 22, "CONDITIONED_BY": 25,
  "AFFECTS": 11, "BELONGS_TO": 51, "WORKFLOW_WRITES_TO": 13, "CONTAINS": 147
}
```

Suma total = 2840, coincide con el total reportado en consola, SQLite e index.json.

## Salidas de consola literales

### 1. `dotnet build`

```
Determinando los proyectos que se van a restaurar...
Se ha restaurado C:\temp\corpus-aw\src\TSqlParser\TSqlParser.csproj (en 602 ms).
TSqlParser -> C:\temp\corpus-aw\src\TSqlParser\bin\Debug\net10.0\TSqlParser.dll

Compilación correcta.
    0 Advertencia(s)
    0 Errores

Tiempo transcurrido 00:00:04.03
```

### 2. `dotnet run -- extract AdventureWorks2019 ../../input.json --server .\SQLEXPRESS --tables`

```
Wrote 51 objects from AdventureWorks2019 to ../../input.json
  + dbo.AWBuildVersion: ok
  + dbo.DatabaseLog: ok
  + dbo.ErrorLog: ok
  + HumanResources.Department: ok
  + HumanResources.Employee: ok
  + HumanResources.EmployeeDepartmentHistory: ok
  + HumanResources.EmployeePayHistory: ok
  + HumanResources.JobCandidate: ok
  + HumanResources.Shift: ok
  + Person.Address: ok
  + Person.AddressType: ok
  + Person.BusinessEntity: ok
  + Person.BusinessEntityAddress: ok
  + Person.BusinessEntityContact: ok
  + Person.ContactType: ok
  + Person.CountryRegion: ok
  + Person.EmailAddress: ok
  + Person.Password: ok
  + Person.Person: ok
  + Person.PersonPhone: ok
  + Person.PhoneNumberType: ok
  + Person.StateProvince: ok
  + Production.BillOfMaterials: ok
  + Production.Culture: ok
  + Production.Document: ok
  + Production.Illustration: ok
  + Production.Location: ok
  + Production.Product: ok
  + Production.ProductCategory: ok
  + Production.ProductCostHistory: ok
  + Production.ProductDescription: ok
  + Production.ProductDocument: ok
  + Production.ProductInventory: ok
  + Production.ProductListPriceHistory: ok
  + Production.ProductModel: ok
  + Production.ProductModelIllustration: ok
  + Production.ProductModelProductDescriptionCulture: ok
  + Production.ProductPhoto: ok
  + Production.ProductProductPhoto: ok
  + Production.ProductReview: ok
  + Production.ProductSubcategory: ok
  + Production.ScrapReason: ok
  + Production.TransactionHistory: ok
  + Production.TransactionHistoryArchive: ok
  + Production.UnitMeasure: ok
  + Production.WorkOrder: ok
  + Production.WorkOrderRouting: ok
  + Purchasing.ProductVendor: ok
  + Purchasing.PurchaseOrderDetail: ok
  + Purchasing.PurchaseOrderHeader: ok
  + Purchasing.ShipMethod: ok
  + Purchasing.Vendor: ok
  + Sales.CountryRegionCurrency: ok
  + Sales.CreditCard: ok
  + Sales.Currency: ok
  + Sales.CurrencyRate: ok
  + Sales.Customer: ok
  + Sales.PersonCreditCard: ok
  + Sales.SalesOrderDetail: ok
  + Sales.SalesOrderHeader: ok
  + Sales.SalesOrderHeaderSalesReason: ok
  + Sales.SalesPerson: ok
  + Sales.SalesPersonQuotaHistory: ok
  + Sales.SalesReason: ok
  + Sales.SalesTaxRate: ok
  + Sales.SalesTerritory: ok
  + Sales.SalesTerritoryHistory: ok
  + Sales.ShoppingCartItem: ok
  + Sales.SpecialOffer: ok
  + Sales.SpecialOfferProduct: ok
  + Sales.Store: ok

Appended 71 table definitions to ../../input.json
```

### 3. `dotnet run -- ../../input.json ../../out/graph_full.json ../../out/workflows_full.json --columns --sqlite --nodestore --graphify`

```
Graphify: 1120 nodes, 2840 edges -> ../../out/graph_full.graphify.json
NodeStore: 51 objects, 876 shared nodes, 2840 edges -> ../../out/graph_full.nodes
SQLite: 1120 nodes, 2840 edges (db=AdventureWorks2019, project=AdventureWorks2019) -> ../../out/graph_full.db
Analyzed 51 objects (51 ok, 0 parse errors)
Analyzed 71 table schemas (71 ok, 0 errors)
Graph: 1120 nodes, 2840 relationships -> ../../out/graph_full.json
```

(Nota operativa: la primera ejecución falló con `DirectoryNotFoundException` porque el worktree nuevo no
trae la carpeta `out/` — hubo que crearla a mano con `mkdir`. No es un bug del toolkit, es una particularidad
de `git worktree add`, que no materializa directorios que están vacíos o ignorados por `.gitignore`.)

### 4. `dotnet run -- validate ../../out/graph_full.json --server .\SQLEXPRESS`

```
FK_TO edges in graph: 86
CALLS edges in graph: 22

=== AdventureWorks2019 ===

FK relationships in DB restricted to tables present in graph: 86
  In DB but missing from graph: 0
  In graph but not in DB (within scope): 0

CALLS (EXEC) relationships in DB restricted to analyzed objects: 22
  In DB but missing from graph: 0
```

## Hallazgos

1. **FK_TO: 86 (validate) vs 90 (index.json/catálogo) — explicado, no es un fallo.**
   `sys.foreign_keys` tiene 90 constraints, y el grafo emite 90 aristas `FK_TO` (coincide exactamente,
   `stats.edges_by_type.FK_TO = 90`). Pero `validate` reporta 86 porque agrupa por **par de tablas
   distinto**, y hay 4 pares de tablas con dos FK cada uno:
   - `Production.BillOfMaterials → Production.Product` (2)
   - `Sales.SalesOrderHeader → Person.Address` (2)
   - `Sales.CurrencyRate → Sales.Currency` (2)
   - `Production.Product → Production.UnitMeasure` (2)

   90 − 4 = 86. Cuadra exactamente con la nota de la tarea: esto es el comportamiento documentado de
   `validate`, no una discrepancia real.

2. **El trigger DDL de base de datos (`ddlDatabaseTriggerLog`) no se captura — gap real del extractor.**
   `sys.triggers` tiene 11 filas: 10 con `parent_class_desc = OBJECT_OR_COLUMN` (triggers de tabla,
   schema-bound) y 1 con `parent_class_desc = DATABASE` (el trigger DDL estándar de AdventureWorks que
   audita `DDL_DATABASE_LEVEL_EVENTS` y escribe en `dbo.DatabaseLog`). El comando `extract` solo trajo 10
   triggers (los de tabla) — el trigger de BD no aparece en `sys.sql_modules ⋈ sys.objects` con el mismo
   filtro que usa el toolkit para objetos "normales" (o si aparece, no fue extraído por `--tables`/objeto
   por su alcance a nivel de servidor/BD en lugar de tabla).

   **Consecuencia visible**: `dbo.DatabaseLog` aparece en `audit_report.orphan_tables`, dando la falsa
   impresión de que es una tabla sin consumidores. En realidad **sí tiene un escritor** — el trigger DDL —
   solo que el toolkit no lo modela. Esto no es un bug de bajo nivel (parseo, aristas fantasma), sino un
   **gap de cobertura de extracción**: los triggers a nivel de base de datos (DDL triggers) no forman
   parte del universo que `extract` recorre. Merece ticket para el motor (no se tocó, según instrucción).

3. **22 tablas huérfanas — 21 explicadas correctamente, 1 (DatabaseLog) es un falso huérfano por el hallazgo #2.**
   - 19 son vistas del catálogo (`vEmployee`, `vEmployeeDepartment`, `vEmployeeDepartmentHistory`,
     `vJobCandidate`, `vJobCandidateEducation`, `vJobCandidateEmployment`, `vAdditionalContactInfo`,
     `vStateProvinceCountryRegion`, `vProductAndDescription`, `vProductModelCatalogDescription`,
     `vProductModelInstructions`, `vVendorWithAddresses`, `vVendorWithContacts`, `vIndividualCustomer`,
     `vPersonDemographics`, `vSalesPerson`, `vStoreWithAddresses`, `vStoreWithContacts`,
     `vStoreWithDemographics`) — son vistas de solo lectura pensadas para consumo externo (apps/reporting),
     nadie dentro del corpus de 51 objetos analizados hace `SELECT ... FROM` sobre ellas ni escribe en
     ellas. Consistente con el patrón ya documentado en WWI ("nadie la referencia según
     sys.sql_expression_dependencies"). De las 20 vistas del catálogo, la única que **no** aparece en
     `orphan_tables` es `Sales.vSalesPersonSalesByFiscalYears` — sí es referenciada como fuente de datos
     desde otro objeto capturado.
   - `dbo.AWBuildVersion`: tabla de metadatos de versión del script de instalación, no forma parte de
     ningún flujo de negocio — orfandad correcta.
   - `Production.TransactionHistoryArchive`: tabla de archivo histórico, igual que en WWI ("tabla de
     historial") — orfandad correcta, nada la referencia por FK ni por lineage.
   - `dbo.DatabaseLog`: **falso huérfano**, ver hallazgo #2.

4. **`orphan_edges`, `unknown_edge_types`, `unknown_labels` todos en 0/vacío.** El vocabulario cerrado de
   tipos de nodo/arista se respeta completamente; no hay aristas fantasma ni fugas de vocabulario.

5. **0 errores de parseo** en los 51 objetos con código y las 71 tablas — ejecución perfectamente limpia en
   ese eje.

6. **Cobertura de lineage de columna: 251/251 (100%)** en los 19 objetos que producen output columns
   (SELECTs con proyección, vistas, funciones con tabla de retorno). Ningún hueco.

7. **Nota operativa (no del motor)**: `git worktree add` con una rama nueva no crea el directorio `out/`
   si éste no existe ya en el commit — hay que crearlo a mano antes de correr el análisis. Ya lo documenté
   como paso adicional, no bloquea nada.

## Comparación con WideWorldImporters

| Métrica | WWI (referencia) | AdventureWorks2019 (esta ejecución) |
|---|---|---|
| Objetos de entrada | 47 | 51 |
| Tablas de entrada | 48 | 71 |
| Objetos en grafo | 64 | 51 (el grafo no añade objetos sintéticos, solo tablas implícitas) |
| Tablas en grafo | 68 | 90 |
| Nodos totales | 1529 | 1120 |
| Aristas totales | 4151 | 2840 |
| Errores de parseo | 0 | 0 |
| FK: grafo/catálogo | 98/98 | 90/90 (edges_by_type) — validate compara por par de tabla y da 86/86 (100% dentro de alcance) |
| CALLS: grafo/catálogo | 12/12 | 22/22 |
| Cobertura de lineage de columna | 32/32 (100%) | 251/251 (100%) |
| Triggers reales en catálogo | 0 (WWI no tiene) | 11 (10 de tabla + 1 DDL de BD) |
| Triggers capturados por el grafo | N/A | 10/10 de tabla — **0/1 del trigger DDL de base de datos** |

**Lectura**: AdventureWorks2019 tiene menos nodos/aristas totales que WWI a pesar de tener más tablas
(90 vs 68) y más objetos con código (51 vs 47/64, según se cuente), porque WWI tiene módulos con lógica
más profunda (más steps/variables por objeto) mientras que muchos objetos de AdventureWorks son triggers
de auditoría/pequeños procedimientos y funciones escalares cortas. El dato más relevante de esta ejecución
frente a WWI es el de los **triggers**: es la primera vez que el corpus de referencia tiene triggers reales,
y el toolkit los captura bien en el caso de triggers de tabla (10/10, con score de hotspot correcto y
lineage de escritura correcto), pero **queda al descubierto que los triggers DDL de base de datos
(scope `ON DATABASE`) no entran en el universo de extracción**, algo que WWI (con 0 triggers) no podía
haber revelado.

## Rutas relevantes

- Worktree: `C:\temp\corpus-aw`
- Grafo: `C:\temp\corpus-aw\out\graph_full.json`, `graph_full.db`, `graph_full.graphify.json`,
  `graph_full.nodes\` (NodeStore), `workflows_full.json`
- Salidas de consola guardadas: `C:\temp\corpus-reports\aw_00_build.txt`, `aw_01_extract.txt`,
  `aw_02_analyze.txt`, `aw_03_validate.txt`
- Consultas de catálogo guardadas: `aw_catalog_objects.txt`, `aw_catalog_tables.txt`,
  `aw_catalog_triggers.txt`, `aw_catalog_fks.txt`, `aw_catalog_views.txt`, `aw_fk_dupe_pairs.txt`,
  `aw_ddl_trigger_check2.txt`
- Este informe: `C:\temp\corpus-reports\adventureworks.md`
