# Registro de Gaps de Extracción (Tarea F)

Este documento es el entregable de la Tarea F (Caza de Huecos). Su propósito es registrar de forma sistemática las discrepancias encontradas entre el comportamiento esperado del analizador y el resultado real, a medida que se sondea con construcciones T-SQL complejas.

Cada entrada debe incluir:
- La construcción T-SQL bajo prueba.
- Un enlace al caso de prueba en `eval/community-edge-cases/`.
- El comportamiento esperado (ground-truth).
- El comportamiento real observado tras pasar el caso por el pipeline.
- Un análisis del gap.

---

## 1. DML Avanzado

### 1.1. `MERGE` con `OUTPUT`

- **Caso de prueba**: `eval/community-edge-cases/dml-advanced/merge-with-output/`

- **Comportamiento Esperado**:
  - **Lecturas de Tabla**: El procedimiento `usp_SyncProducts` debe tener aristas `READS_FROM` hacia `SourceProducts` y `TargetProducts`.
  - **Escrituras de Tabla**: Debe tener aristas `WRITES_TO` hacia `TargetProducts` y `ProductMergeLog`.
  - **Lineage de Columna (Update/Insert)**: La columna `TargetProducts.Price` debe tener una arista `DERIVES_FROM` que apunte a `SourceProducts.Price`.
  - **Lineage de Columna (Output)**: La columna `ProductMergeLog.NewPrice` debe derivar de `TargetProducts.Price` (a través de la pseudo-tabla `inserted`), y `ProductMergeLog.OldPrice` debe derivar de `TargetProducts.Price` (a través de `deleted`).

- **Comportamiento Real**:
  - **Éxito parcial (Fix de Claude, verificado):** El lineage de columna para las cláusulas `UPDATE SET` y `WHEN NOT MATCHED INSERT` ahora se extrae correctamente. El `model.json` muestra 3 aristas `DERIVES_FROM` (`ProductID`, `ProductName`, `Price`) desde `SourceProducts` hacia `TargetProducts`.
  - **Gap restante (confirmado):** El lineage a través de la cláusula `OUTPUT INTO` sigue sin extraerse. Las columnas en la tabla de log (`ProductMergeLog`) no tienen aristas `DERIVES_FROM`.

- **Análisis del Gap**:
  - **PARCIALMENTE RESUELTO.** El fix principal ha sido implementado y verificado. El gap restante en la cláusula `OUTPUT` es un caso de lineage más complejo que involucra las pseudo-tablas `inserted`/`deleted` y queda como un punto de mejora futuro.

---

## 2. CTEs (Common Table Expressions)

### 2.1. CTE Recursiva

- **Caso de prueba**: `eval/community-edge-cases/cte-recursive/employee-hierarchy/`

- **Comportamiento Esperado**:
  - El lineage de las columnas de la vista (`EmployeeID`, `ManagerID`, etc.) debe trazarse hasta la tabla base (`dbo.Employees`) tanto desde el *miembro ancla* como desde el *miembro recursivo* de la CTE.
  - Idealmente, el grafo debería modelar la auto-referencia de la CTE.
  - El lineage de la columna calculada `Level` debería, como mínimo, no ser erróneo.

- **Comportamiento Real**:
  - **Éxito parcial (Fix de Claude, verificado):** El parser ahora traza correctamente el lineage desde la tabla base. El `model.json` muestra 1 `READS_FROM` (vista → tabla) y 3 `DERIVES_FROM` (columnas de la vista → columnas de la tabla).
  - **Gap restante (limitación conocida, confirmada):** La columna calculada (`Level`), que nace de un literal y una expresión, sigue sin tener un origen de lineage trazable en el grafo.

- **Análisis del Gap**: **PARCIALMENTE RESUELTO.** El fix principal, que consiste en trazar el lineage desde las tablas base a través de toda la CTE, está implementado y verificado. El gap restante en las columnas calculadas es una limitación conocida y un problema más complejo de análisis de flujo de datos.

---

## 3. Funciones de Ventana (Falso Positivo - CERRADO)

### 3.1. `SUM(...) OVER (...)` y `ROW_NUMBER() OVER (...)`

- **Análisis del Gap**: **NO-GAP / FALSO POSITIVO.** Mi informe inicial indicaba que el lineage se perdía. La validación cruzada de Claude demostró que el parser **SÍ funciona correctamente** para funciones de ventana, incluyendo `SUM()` y `ROW_NUMBER()`. El lineage se traza desde la columna resultante hasta las columnas en las cláusulas `PARTITION BY` y `ORDER BY`, además de la columna agregada si aplica. Este gap queda cerrado como un error en mi análisis inicial.

---

## 4. Lineage de Columna (Validado con `sqlglot`)

Los siguientes casos fueron propuestos usando el corpus de `sqlglot` como oráculo. La validación real de Claude determinó que solo uno de ellos era un gap real en nuestro motor.

### 4.1. Condición en `CASE` (Falso Positivo - CERRADO)

- **Caso de prueba**: `SELECT CASE WHEN a > 10 THEN b ELSE c END AS x FROM t`

- **Comportamiento Esperado**: La columna de salida `x` debe derivar de todas las columnas que participan en la expresión: `a` (en la condición `WHEN`), `b` (en el `THEN`) y `c` (en el `ELSE`).

- **Análisis del Gap**: **NO-GAP / FALSO POSITIVO.** Mi informe inicial, basado en una ejecución no materializada, indicaba que el lineage de la columna en la condición `WHEN` se perdía. La ejecución real de Claude demostró que el parser **SÍ funciona correctamente** y extrae el lineage de las tres columnas (`a`, `b` y `c`). Este gap queda cerrado.

### 4.2. `UNION` (RESUELTO)

- **Caso de prueba**: `SELECT a FROM t1 UNION SELECT b FROM t2`

- **Comportamiento Esperado**: La columna de salida del `UNION` (que toma el nombre de la columna del primer `SELECT`, en este caso `a`) debe derivar de las columnas correspondientes en *todos* los `SELECT` que forman parte del `UNION`. El lineage correcto es `output.a ← t1.a` y `output.a ← t2.b`.

- **Comportamiento Real (medido por Claude, antes del fix)**: No se generaba **ninguna** arista `DERIVES_FROM`. El lineage se perdía por completo para ambas ramas del `UNION`.

- **Análisis del Gap**: **RESUELTO.** El fix de Claude (refactorización recursiva de `BuildViewColumnLineage`) ha solucionado el problema. La validación independiente con el corpus de `sqlglot` confirma que el lineage ahora se traza correctamente a través de todas las ramas del `UNION`.

### 4.3. Agregación con `DISTINCT` (Falso Positivo - CERRADO)

- **Caso de prueba**: `SELECT COUNT(DISTINCT a) as c FROM t`

- **Comportamiento Esperado**: La columna calculada `c` debe derivar de la columna `a` sobre la que se realiza la agregación.

- **Análisis del Gap**: **NO-GAP / FALSO POSITIVO.** Mi informe inicial indicaba que el lineage se perdía. La ejecución real de Claude verificó que el parser **SÍ funciona correctamente** y genera la arista `DERIVES_FROM` desde la columna `a` hacia la columna de salida `c`. Este gap queda cerrado.

---

## 5. Resolución estática de SQL dinámico (`AstWalker.ResolveLiteral`)

Distinto de las secciones 1-4 (lineage de columna sobre SQL estático): esto es sobre
`is_dynamic_sql`/`dynamic_sql` — el campo que reconstruye a texto literal el SQL que arma
un `EXEC(@var)`/`EXEC sp_executesql @var` cuando todas sus piezas son resolubles, y que
queda en `""` cuando no (ver `index.json.howto.exec_resolution`, `NodeStoreExporter.cs`).

### 5.1. `QUOTENAME(@var)` en una concatenación (RESUELTO)

- **Hallazgo real:** `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad` (WWI) tiene
  34 pasos `EXEC(@SQL)` (`DROP`/`CREATE TRIGGER` por tabla), con `@SQL` construido por
  concatenación de variables SIEMPRE asignadas por `SET @x = N'literal'` (17 bloques, sin
  cursor ni `SELECT INTO` — verificado leyendo `out/input.json`). Pese a eso, los 34 daban
  `dynamic_sql=""` — `model.json` marcaba `unresolved_dynamic_sql_steps=34/34`.
- **Causa raíz:** `ResolveLiteral` (`AstWalker.cs`) ya resolvía `StringLiteral`,
  `ParenthesisExpression`, `VariableReference` (vía `ctx.ResolvedVars`) y `BinaryExpression`
  `+` (concatenación), pero NO tenía caso para `FunctionCall` — así que cualquier
  `QUOTENAME(@x)` en medio de la cadena devolvía `null`, y `ConcatLiterals` aborta toda la
  concatenación en el primer `null`.
- **Fix (implementado, verificado):** caso nuevo en `ResolveLiteral` para
  `FunctionCall` con `FunctionName.Value == "QUOTENAME"` (1 o 2 parámetros), resolviendo el
  argumento recursivamente y aplicando el quoting real (`[`/`]` por defecto; también `'`/`"`/
  `` ` ``). **Resultado medido:** `unresolved_dynamic_sql_steps` de ese procedimiento baja de
  **34 a 17**; las 17 `DROP TRIGGER` ahora dan texto literal completo y confirman las mismas
  17 tablas ya conocidas por las `ALTER` estáticas. Sin regresión (`community-edge-cases`
  TODOS OK, `bad-practices` OK=38, gate de vistas WWI 3/3).

### 5.2. `CASE`/`COALESCE` + `NCHAR(n)` en una concatenación (RESUELTO)

- **Caso de prueba (materializado, ejecutado):**
  `eval/community-edge-cases/dynamic-sql/quotename-case-coalesce.sql` — repro mínima del
  patrón `CASE`/`COALESCE` real de WWI (los 17 `CREATE TRIGGER` que quedaron sin resolver
  tras 5.1 usan `CASE WHEN COALESCE(@LastEditedByColumnName, N'') <> N'' THEN
  QUOTENAME(@LastEditedByColumnName) + N', ' ELSE N'' END` dentro de la concatenación de
  `@SQL`).
- **Comportamiento esperado:** el paso 2 del caso de prueba resuelve a
  `SELECT [LastEditedBy] FROM [Orders];` — `@LastEditedByColumnName` es literal
  (`N'LastEditedBy'`), la condición del `WHEN` es estáticamente evaluable
  (`COALESCE('LastEditedBy', '') <> ''` → `true`), la rama `THEN` se toma.
- **Comportamiento real (medido, pipeline ejecutado de verdad):** el paso 2 ahora da
  exactamente `SELECT [LastEditedBy] FROM [Orders];`. Y sobre el procedimiento real de WWI,
  `unresolved_dynamic_sql_steps` baja de **17 a 0** (los 34 pasos resuelven a texto literal
  completo).
- **Análisis — TRES bloqueadores, no dos (el tercero no estaba en el diagnóstico inicial):**
  1. `COALESCE(a, b)` NO es un `FunctionCall` en ScriptDom sino un nodo propio
     `CoalesceExpression` (`Expressions : IList<ScalarExpression>`) — confirmado por reflexión
     sobre `Microsoft.SqlServer.TransactSql.ScriptDom 180.18.1`. Fix: caso nuevo en
     `ResolveLiteral` que devuelve el primer `Expressions[i]` que resuelva a no-null.
  2. `CASE WHEN ... THEN ... ELSE ... END` es `SearchedCaseExpression`
     (`WhenClauses : IList<SearchedWhenClause>` con `WhenExpression : BooleanExpression` +
     `ThenExpression : ScalarExpression`, y `ElseExpression`). Fix: caso nuevo en
     `ResolveLiteral` + un evaluador booleano estático nuevo, `ResolveBoolean(BooleanExpression)`,
     que cubre `BooleanComparisonExpression` (`=`/`<>` sobre operandos `ResolveLiteral`-
     resolubles), `BooleanIsNullExpression`, `BooleanParenthesisExpression` y `AND`/`OR`
     (`BooleanBinaryExpression`). Falla cerrado (devuelve `null`) ante cualquier comparación de
     orden (`<`, `>`, …) o cualquier operando no resoluble: nunca adivina una rama.
  3. **El bloqueador que faltaba en el diagnóstico de 5.1:** `@CrLf = NCHAR(13) + NCHAR(10)`
     (declarado al principio del procedimiento y concatenado en **todos** los cuerpos
     `CREATE TRIGGER`). `NCHAR(n)` es un `FunctionCall` sin caso → `@CrLf` nunca resolvía →
     toda concatenación que lo usara fallaba cerrado. Esto, no el `CASE`/`COALESCE`, es la
     razón por la que tras 5.1 quedaban *exactamente* los 17 `CREATE TRIGGER` (que usan
     `@CrLf`) y resolvían los 17 `DROP TRIGGER` (que no lo usan). Fix: caso nuevo para
     `NCHAR(n)`/`CHAR(n)` con `n` literal entero en el BMP no-surrogate → su carácter.
- **Verificación:** `community-edge-cases` TODOS OK, `bad-practices` OK=38, vistas WWI 3/3,
  `auditor-challenge/verify.mjs` TODOS OK con `unresolved_dynamic_sql_steps == 0` (assert
  endurecido de `<= 17` a `== 0`). Los 17 `CREATE TRIGGER` resueltos NO añaden tablas nuevas
  (siguen siendo las mismas 17, el riesgo de "tabla 18ª oculta" ya estaba cerrado por 5.1);
  aportan el detalle de columnas/lógica de cada trigger, antes opaco.

## 6. Triggers: `inserted`/`deleted` emitidos como tablas fantasma (GAP REAL, medido 2026-07-04)

- **Cómo se encontró:** primera pasada del pipeline sobre **AdventureWorks2019
  COMPLETA** (51 objetos + 71 tablas vía `extract --tables`, 0 errores de parseo)
  cruzada contra el oráculo `sys.sql_expression_dependencies` (152 pares
  objeto→dependencia). Comparador ad-hoc (scratchpad `aw-gap-detector.mjs`).
- **Resultado global:** RECALL a nivel objeto→dependencia = **152/152 (100%)** —
  ningún par del oráculo falta en el grafo. El hueco está en la otra dirección:
- **Comportamiento real:** 10 pares SOBRAN, todos con la misma forma: los cuerpos
  de los 9 triggers DML de AW (`Person.iuPerson`, `Production.iWorkOrder`,
  `Purchasing.uPurchaseOrderDetail`, `Sales.iduSalesOrderDetail`, …) referencian
  las pseudo-tablas `inserted`/`deleted` y el extractor las materializa como
  nodos `:Table` reales (`READS_FROM` a "inserted"/"deleted").
- **Esperado:** `inserted`/`deleted` dentro de un trigger son filas virtuales de
  la tabla del `ON` — deberían resolverse a esa tabla base (exactamente como ya
  hace el fix de `MERGE ... OUTPUT INTO`, que mapea inserted/deleted al target
  del MERGE) o, como mínimo, no emitir un `:Table` fantasma compartido que
  contamina el impacto (cualquier análisis "quién lee inserted" mezcla todos los
  triggers de la BD entre sí).
- **Severidad:** media — no pierde dependencias (el ON ya enlaza trigger↔tabla),
  pero ensucia el grafo con 2 tablas fantasma globales y desvía el lineage de
  columna de los triggers hacia ellas.
- **Nota de alcance:** el oráculo es a nivel TABLA y por nombre; este cruce no
  mide recall de columna (eso es la métrica A2, pendiente). El gap de PIVOT
  (1 vista AW) encontrado por el gate Oracle del Paso 3 es independiente y ya
  está documentado allí.
