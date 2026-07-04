# Matriz de cobertura de extracción (papel de trabajo del auditor)

Insumo **A1** del plan de auditoría. Consolida en una sola tabla, por construcción
T-SQL, tres preguntas que hoy están dispersas en la suite de tests, en
[extraction-gaps.md](extraction-gaps.md) y en la bitácora `docs/agent-collab.md`:

1. **¿Extrae?** — ¿el motor produce el lineage/los nodos esperados?
2. **Test** — ¿hay un test automatizado que lo fija como invariante?
3. **Oráculo** — ¿está validado contra una fuente independiente (SQL Server vía
   DMVs / sqlglot / corpus), o solo contra la expectativa escrita a mano?

> **Leyenda de estado**
> ✅ extrae + cubierto por test + (idealmente) cruzado con oráculo ·
> 🟡 parcial o con limitación conocida documentada ·
> ❌ gap real confirmado (medido, no supuesto) ·
> ❓ no sondeado sistemáticamente (riesgo desconocido)

> **Regla de proceso (control C1):** ninguna celda pasa a ✅/❌ sin ejecución real
> del pipeline y artefacto en disco. Las celdas ❓ son honestas: marcan dónde el
> auditor aún no tiene evidencia, no dónde "seguramente funciona".

---

## 1. DML — lineage de columna y tabla

| Construcción | ¿Extrae? | Test | Oráculo | Estado |
|---|---|---|---|---|
| `INSERT ... SELECT` con lista de columnas → `DERIVES_FROM` | sí | `InsertSelectWithExpression_DerivesFromMultipleSourceColumns` | corpus | ✅ |
| `INSERT ... SELECT *` (expande con esquema conocido) | sí | `InsertSelectStar_WritesTargetWithNoColumns`, `SelectStar_WithKnownSchema_ExpandsToAllColumns` | corpus | ✅ |
| `INSERT ... SELECT` con `JOIN` (columna→tabla correcta) | sí | `InsertSelectWithJoin_DerivesFromCorrectSourceTablePerColumn` | corpus | ✅ |
| `INSERT ... SELECT` a través de `#temp`/`@var` (puente transitivo) | sí | `InsertSelect_ThroughTempTable_BridgesDerivesFromToRealSourceTable` | corpus | ✅ |
| `INSERT ... SELECT ... UNION` (lineage de columna) | **no** | `InsertSelectWithUnion_ProducesNoColumnLineage` (fija el gap) | — | 🟡 limitación conocida a nivel DML (en VISTAS sí resuelto, ver §3) |
| `UPDATE SET col = expr` → `DERIVES_FROM` | sí | `UpdateSetExpression_DerivesTargetColumnFromSourceColumns` | corpus | ✅ |
| `UPDATE ... FROM join` (columna deriva de otra tabla) | sí | `UpdateSetFromJoin_DerivesTargetColumnFromOtherTable` | corpus | ✅ |
| `UPDATE` auto-referencia (no crea self-loop) | sí | `UpdateSetSelfReference_DoesNotCreateSelfLoop` | corpus | ✅ |
| `UPDATE` `WHERE` → `CONDITIONED_BY`/`FiltersOn` | sí | `Update_FiltersOnWhereColumn` | corpus | ✅ |
| `MERGE` lectura de target+source (nivel tabla) | sí | `ReadsFrom_MergeCapturesSourceAndTarget`, `Merge_WritesToTarget` | DMVs | ✅ |
| `MERGE` `WHEN MATCHED UPDATE` / `WHEN NOT MATCHED INSERT` (columna) | sí | `CommunityEdgeCaseGateTests.Corpus_MatchesExpectedEdges(merge)` | DMVs + corpus | ✅ fijado por test 2026-07-03 |
| `MERGE` `OUTPUT INTO` (vía `inserted`/`deleted`) | sí (fix reciente) | `CommunityEdgeCaseGateTests.Corpus_MatchesExpectedEdges(merge-with-output)` | corpus | ✅ fijado por test 2026-07-03 |
| `DELETE ... FROM alias` (resuelve alias→tabla real) | sí | `DeleteFromAlias_ResolvesToRealTable` | corpus | ✅ |
| `TRUNCATE` → `WRITES_TO` | sí | `Truncate_WritesToTarget` | corpus | ✅ |

## 2. SELECT / lectura

| Construcción | ¿Extrae? | Test | Oráculo | Estado |
|---|---|---|---|---|
| `SELECT ... FROM t` → `READS_FROM`/`READS_COLUMN` | sí | `SelectFromTable_ReadsFromAndReadsColumns` | corpus | ✅ |
| `JOIN` (ambas tablas reciben reads) | sí | `SelectWithJoin_BothTablesGetReadsFromAndReadsColumn` | corpus | ✅ |
| `JOIN` con alias complejo (resolución alias→tabla) | parcial | `UpdateFromAlias_ResolvesToRealTable` | sqlglot, SQL Server | 🟡 gana a sqlglot en casos simples; **6 vistas AW fallan** (`p.Title`, `ct.Name`…) |
| Subconsulta `EXISTS` anidada (resuelve su tabla+filtros) | sí | `NestedExistsSubquery_ResolvesItsOwnTableAndFilterColumns` | corpus | ✅ |
| Subconsulta escalar en `WHERE`/`SET` | sí | `SetScalarSubquery_BecomesStep_WithReadsFromAndFiltersOn`, `TopLevelSelect_WhereInSubquery...` | corpus | ✅ |
| `CASE WHEN a THEN b ELSE c` (deriva de a,b,c) | sí | — | sqlglot | ✅ *(era falso positivo de gap; confirmado OK)* |
| `COUNT(DISTINCT a)` (deriva de a) | sí | — | sqlglot | ✅ *(falso positivo de gap; confirmado OK)* |
| Funciones de ventana `OVER(PARTITION BY/ORDER BY)` | sí | — | sqlglot | ✅ *(falso positivo de gap; mira dentro del OVER)* |

## 3. Set ops, CTE, vistas

| Construcción | ¿Extrae? | Test | Oráculo | Estado |
|---|---|---|---|---|
| `UNION` en cuerpo de **VISTA** (deriva de cada rama) | sí (fix) | `CommunityEdgeCaseGateTests.Corpus_MatchesExpectedEdges(union-view)` | sqlglot + corpus | ✅ fijado por test 2026-07-03 (cf. §1 UNION en DML sigue 🟡) |
| CTE no recursiva | sí | `CteAlias_IsNotEmittedAsTable` | corpus | ✅ |
| CTE **recursiva** (lineage desde tabla base) | sí (fix) | `CommunityEdgeCaseGateTests.Corpus_MatchesExpectedEdges(recursive-cte)` | DMVs + corpus | ✅ fijado por test 2026-07-03; **columna calculada (`Lvl`) → fantasma** sigue siendo limitación conocida (documentada en el `expected.json`) |
| VISTA: columnas de salida como nodos `DERIVES_FROM` | sí | `View_OutputColumnDerivesFromBaseColumns` | SQL Server (`sys.columns`) | ✅ gate WWI verde |
| Puente `SELECT ... FROM <vista>` → tablas base (`via_view`) | sí | `SelectFromAnalyzedView_BridgesToViewsRealBaseTable` | SQL Server | ✅ |

## 4. Control de flujo, dinámico, cursores, variables

| Construcción | ¿Extrae? | Test | Oráculo | Estado |
|---|---|---|---|---|
| `IF`/`WHILE`/`TRY-CATCH` anidados → `Rule`/`GOVERNS` | sí | `NestedIf_ProducesNestedRulesAndGoverns` | — | ✅ |
| Flags `HasTransaction`/`HasCursor`/`HasErrorHandling` | sí | `TransactionCursorErrorHandling_FlagsAreSet` | — | ✅ |
| SQL dinámico literal (`EXEC(@sql)` reconstruible) | sí | `DynamicSql_LiteralWhereClause_ResolvesToFiltersOnColumn` | — | ✅ |
| SQL dinámico construido por variable (rastrea `@sql`) | sí | `DynamicSql_TracksBuildsSqlFromVariable` | — | ✅ |
| SQL dinámico **no literal** (runtime) | no se re-parsea | — | — | 🟡 por diseño: se rastrea la construcción, no se ejecuta el lineage |
| `READS_FROM` dentro de cuerpo de cursor | sí | `ReadsFrom_SurvivesCursorBodyAndTempTarget` | corpus | ✅ *(cierra el gap `FOR SYSTEM_TIME`)* |
| `SELECT @var = col` → `ASSIGNED_FROM` (col→variable) | sí | `SelectAssignsVariable_ProducesAssignedFromColumn`, `SetVariableFromSubquery...` | corpus | ✅ |
| `@TableVar`/`#temp` no emitidos como `:Table` fantasma | sí | `TableVariable_IsNotEmittedAsTable` | corpus | ✅ |

## 5. DDL / esquema / constraints

| Construcción | ¿Extrae? | Test | Oráculo | Estado |
|---|---|---|---|---|
| `CREATE TABLE` columnas + `FOREIGN KEY` → `FK_TO`/`REFERENCES` | sí | `CreateTable_ColumnsAndForeignKeys_ProduceFkToAndReferences` | SQL Server | ✅ |
| Columna computada (`AS expr`) → `DERIVES_FROM` + op_kind | sí | `ComputedColumn_DerivesFromItsSourceColumns`, `..._CarriesArithmeticOpKind` | corpus | ✅ |
| Constraints `CHECK`/`DEFAULT`/`UNIQUE` → `:BusinessRule` | sí | — | `sys.check_constraints` etc. | 🟡 validado (7/7 por tipo); **sin test unitario** |
| `NOT NULL`/`PK` (atributo de columna, no BusinessRule) | sí | — | DMVs | ✅ decisión de modelado confirmada |
| `ALTER TABLE ADD/DROP COLUMN` → liga step a columna | sí | `AlterColumn_LinksAlterStepToAffectedColumn`, `DropColumn...` | — | ✅ |
| Promoción `:Schema`/`:Database` + `CONTAINS` | sí | — | `sys.schemas` | ✅ gate verde, dedup 1 DB / 10 schemas |
| `EXEC OtherDb.dbo.Proc` cross-database (`is_cross_database`) | sí | `ExecCall_CrossDatabase_ResolvesToCalleeAndTagsCrossDb` | — | ✅ |

## 6. No sondeado / gaps duros (frontera de alcance)

| Construcción | ¿Extrae? | Estado | Nota |
|---|---|---|---|
| `PIVOT` / `UNPIVOT` | no | ❌ gap real | rompe `Sales.vSalesPersonSalesByFiscalYears` (AW) |
| `XQuery .value()` / `.query()` | no | ❌ gap real | rompe `vProductModelCatalogDescription`, `vProductModelInstructions` (AW) |
| `OPENJSON` / `JSON_VALUE` | **sí** | ✅ **arreglado 2026-07-03** | `BuildXmlApplyMap` trata `OPENJSON(<col>) WITH(...) AS j` como el shredding XML: el alias `j` mapea a la columna JSON de origen, así que las columnas troceadas dan `READS_COLUMN` de `Payload` y las escritas `DERIVES_FROM` `Payload`. Test `OpenJson_ShreddedColumns_DeriveFromSourceJsonColumn` |
| `CROSS/OUTER APPLY` con TVF | **sí** | ✅ **arreglado 2026-07-03** | `FunctionCallCollector.Visit(SchemaObjectFunctionTableReference)` emite `CALLS` a la TVF; el impacto llega a la tabla base por la cadena (proc `CALLS` tvf `READS_FROM` base). Test `Tvf_InvokedViaCrossApply_ProducesCallsEdge`. WWI sin regresión |
| Sinónimos (`CREATE SYNONYM`) | **sí** | ✅ **arreglado 2026-07-03** | router `from-sql` reconoce `SYNONYM`; `object_type=SYNONYM`; referencias se resuelven a la tabla base (`READS_FROM`/`WRITES_TO` reales) + arista `ALIAS_OF`. Test: `Synonym_ReadThroughSynonym_ResolvesToBaseTable`. Gates WWI sin regresión |
| UDF escalar (`dbo.fn(...)` en `SELECT`) | **sí** | ✅ sondeado 2026-07-03 | genera `CALLS` a la función + lectura de las columnas argumento |
| TVF inline / multi-statement (lineage a través de la función) | sí (a nivel objeto) | ✅ 2026-07-03 | la TVF captura su propia lectura de tabla base + la invocación `APPLY tvf(...)` ya la enlaza con `CALLS`. Pendiente fino: mapear columnas de salida de la TVF a las del llamador (lineage de columna a través de la función) |
| Triggers `inserted`/`deleted` (más allá de `MERGE OUTPUT`) | parcial | ❓ no sondeado | la mecánica `inserted`/`deleted` existe por el fix de OUTPUT |

---

## Lectura del auditor

- **Núcleo DML + vistas + esquema: sólido y validado** (✅ dominantes en §1-§5).
- **Deuda de test (🟡 sin test unitario) — CERRADA 2026-07-03 para 3 de 4:** MERGE
  `WHEN MATCHED`/`OUTPUT INTO`, UNION en vista y CTE recursiva ahora tienen red de
  regresión unitaria (`CommunityEdgeCaseGateTests`, `[Theory]` in-process contra
  `expected.json` con aristas concretas — paso 2 de `docs/task-gates-dotnet.md`).
  Queda pendiente solo constraints `:BusinessRule` (fuera del corpus de
  community-edge-cases; no tocado por esta tarea).
- **Gaps reales acotados:** PIVOT y XQuery (frontera de alcance honesta; decisión de
  negocio si entran).
- **Riesgo desconocido (❓):** OPENJSON, APPLY+TVF, sinónimos, UDF/TVF, triggers.
  → *Acción A1.2: sondear con la metodología C1 (ejecutar + artefacto) antes de
  afirmar nada.*

*Próxima medición pendiente (insumo C):* esta matriz da cobertura **cualitativa**.
Falta la **cuantitativa** (precision/recall de aristas vs oráculo) — tarea A2.
