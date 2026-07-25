# Informe de Auditoría — Claude

**Ejercicio:** `docs/auditor-challenge.md`. Generado en ciego (sin ver el informe de Gemini).
**Fuentes usadas (todas leídas de verdad, rutas exactas citadas en cada hallazgo):**
- `out/graph_full.nodes/` — NodeStore de WideWorldImporters (47 objetos), regenerado con la
  Tarea I (`lineage_path.json` ya incluido).
- `eval/bad-practices/graph_full.nodes/` — NodeStore del corpus sintético `BadPracticesDB`
  (16 objetos), generado para este ejercicio (`--columns --nodestore` sobre su `input.json`
  existente; no existía como NodeStore hasta ahora).
- `eval/bad-practices/expected-findings.json` — ground-truth del rule engine (severidad/
  categoría oficiales por hallazgo), usado para no inventar mi propia escala de severidad.

---

## Tarea 1: Plan de Mejora Estratégico

### 1. Hotspots (5)

| # | Componente | BD | Por qué |
|---|---|---|---|
| 1 | `dbo.usp_SearchCustomers_Injection` | BadPracticesDB | `crit`/Seguridad — inyección SQL (rule engine, `expected-findings.json`). Único `crit` de todo el corpus. |
| 2 | `dbo.usp_PurgeAll_NoWhere` | BadPracticesDB | `high`/Integridad — `UPDATE/DELETE sin WHERE`: blast radius de borrado masivo si se ejecuta sin filtro. |
| 3 | `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad` | WideWorldImporters | No está en el rule engine (no es código "sintético malo"), pero `model.json` lo marca con `cyclomatic_complexity=19`, `degree=21`. ~~`unresolved_dynamic_sql_steps=34` (100% opaco)~~ **ACTUALIZADO tras arreglo del parser (`AstWalker.ResolveLiteral`, ver nota): ahora 17/34 — las 17 sentencias `DROP TRIGGER` se resuelven a texto literal y confirman las MISMAS 17 tablas ya conocidas por las `ALTER` literales; el blind spot de "¿hay una tabla 18ª oculta?" queda cerrado. Lo que sigue opaco son los 17 cuerpos `CREATE TRIGGER` (listas de columnas vía `CASE`/`COALESCE`, sin riesgo de tabla nueva, riesgo de detalle de columna).** Justificación de negocio en la sección 3. |
| 4 | `dbo.usp_ProcessQueue_CursorTx` + `dbo.usp_TransferFunds_TxNoCatch` | BadPracticesDB | `high`/Robustez — transacción sin `TRY/CATCH`: una excepción a mitad de transacción deja locks/transacciones huérfanas. |
| 5 | Patrón repetido `dbo.Inventory` / `dbo.Notifications` / `dbo.Shipments` / `dbo.OrderAudit` | BadPracticesDB | Las 4 tablas acumulan el MISMO triple smell (`Tabla sin clave primaria` + `Tabla escrita pero nunca leída` + `Tabla totalmente anulable`, `expected-findings.json`). Patrón sistémico, no 4 errores aislados. |

### 2. Priorización de tareas (de más a menos urgente)

1. **Parametrizar `usp_SearchCustomers_Injection`** (crit/Seguridad) — explotable hoy, máxima prioridad sin discusión.
2. **Añadir guarda `WHERE`/confirmación explícita a `usp_PurgeAll_NoWhere`** (high/Integridad) — el coste de NO arreglarlo es irreversible (borrado masivo); el de arreglarlo es bajo.
3. **Auditar los 34 pasos de SQL dinámico no resueltos en `DeactivateTemporalTablesBeforeDataLoad`** — no es una regla del motor de malas prácticas, es un hueco del propio NodeStore (`unresolved_dynamic_sql_steps`), pero un equipo de auditoría no puede certificar "sabemos todo lo que toca este procedimiento" mientras siga en 34/34. Justificación de negocio abajo.
4. **Envolver `usp_ProcessQueue_CursorTx` y `usp_TransferFunds_TxNoCatch` en `TRY/CATCH`** (high/Robustez) — mismo patrón, mismo arreglo, una sola revisión cubre las dos.
5. **Revisar `usp_DynamicReport`** (high/Seguridad — SQL dinámico) — mismo patrón que #1 pero sin evidencia confirmada de inyección; revisar antes de que lo sea.
6. **Una única migración para el patrón #5 de hotspots** (`Inventory`/`Notifications`/`Shipments`/`OrderAudit`): añadir PK + revisar nulabilidad de las 4 a la vez, no 4 tickets sueltos — es el mismo defecto de diseño repetido.
7. **`usp_TruncateAudit`** (med/Integridad, `TRUNCATE` de tabla) y **`usp_MegaWorkflow_Complex`** (med/Mantenibilidad, complejidad 12 + anidación profunda — `model.json` confirma `cc=12`, `degree=6`, el SqlObject de mayor grado de todo `BadPracticesDB`): candidatos a refactor, no urgentes.
8. **Cola de bajo impacto** (perf/mantenibilidad): prefijo `sp_` en `sp_GetActiveCustomers`, `SELECT *` en `usp_DumpCustomers_SelectStar`, tabla ancha `dbo.WideProductCatalog` (única tabla con `degree=0` — sin relaciones, candidata a revisar si sigue en uso), código muerto en `ufn_CalcDiscount`, variable sin uso en `usp_ArchiveOldOrders_UnusedVars`.

### 3. Justificación "Agent-First" (caso destacado: #3)

Esto es lo que el `lineage_path.json` de la Tarea I hace trivial y que, sin él, habría exigido recorrer el grafo a mano: conectar un procedimiento interno de carga de datos con las pantallas que ve un cliente/proveedor externo.

`DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad` escribe (`ALTER` — desactiva el
system-versioning temporal, no es un INSERT/UPDATE de datos) en 17 tablas
(`out/graph_full.nodes/objects/WideWorldImporters_DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad/nav.json`).
Cruzando esas 17 tablas contra los `roots` de los `lineage_path.json` de las 3 vistas de
`Website` (las únicas con columnas de salida en este NodeStore):

- **`Website.Customers`**: **14 de sus 14 columnas de salida** tienen una raíz dentro de esas
  17 tablas (`sales.customers`, `application.people`, `application.cities`,
  `sales.buyinggroups`, `sales.customercategories`, `application.deliverymethods`).
- **`Website.Suppliers`**: **12 de sus 12 columnas de salida**, igual (`purchasing.suppliers`,
  `application.people`, `application.cities`, `application.deliverymethods`,
  `purchasing.suppliercategories`).
- **`Website.VehicleTemperatures`**: **0 columnas afectadas** — sus raíces son
  `warehouse.vehicletemperatures`, una tabla DISTINTA de la `warehouse.coldroomtemperatures`
  que sí toca el procedimiento (confirmado leyendo ambos `lineage_path.json`, no asumido por
  similitud de nombre).

Es decir: **el 100% de las columnas de los dos portales de autoservicio orientados a cliente
(`Website.Customers`/`Website.Suppliers`) dependen, en última instancia, de tablas que este
procedimiento altera** — y el procedimiento tiene 34 pasos de SQL dinámico cuyo destino real el
NodeStore no puede confirmar (`unresolved_dynamic_sql_steps=34`). Si alguno de esos 34 pasos
dinámicos toca una tabla distinta a las 17 ya conocidas (p.ej. por un cambio futuro en el script
que genera el ALTER dinámico), ese impacto sería invisible tanto para este informe como para
el propio NodeStore — de ahí que la tarea #3 priorice **auditar/resolver esos 34 pasos**, no
solo "es complejo, revisar".

---

## Tarea 2: Análisis de Impacto de un Cambio

**Cambio propuesto:** modificar `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad`.

### 1. Impacto funcional (Upstream/Downstream)

Vía `objects/WideWorldImporters_DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad/nav.json`
y `model.json` (aristas `CALLS`):

- **Upstream (quién lo llama):** un único llamador,
  `DataLoadSimulation.Configuration_ApplyDataLoadSimulationProcedures` — el orquestador de
  carga de datos más complejo del corpus (`cyclomatic_complexity=21`, el más alto de WWI).
- **Downstream (a quién llama):** un único llamado,
  `Application.Configuration_RemoveRowLevelSecurity`.

Cadena funcional completa: 1 nivel arriba, 1 nivel abajo — no es un nodo intermedio de una
cadena larga de `EXEC`, así que el riesgo de "romper una cadena de llamadas" es bajo. El riesgo
real está en el efecto sobre datos (sección 2), no en la cadena de invocación.

### 2. Impacto en datos (Efecto dominó)

**a. Tablas escritas directamente (17, vía `nav.json`, `action_type=ALTER` en todas — esto
desactiva el system-versioning temporal, no escribe filas):**

`application.cities`, `application.countries`, `application.deliverymethods`,
`application.paymentmethods`, `application.people`, `application.stateprovinces`,
`application.transactiontypes`, `purchasing.suppliercategories`, `purchasing.suppliers`,
`sales.buyinggroups`, `sales.customercategories`, `sales.customers`,
`warehouse.coldroomtemperatures`, `warehouse.colors`, `warehouse.packagetypes`,
`warehouse.stockgroups`, `warehouse.stockitems`.

**b. Vistas/informes afectados indirectamente (vía `lineage_path.json` de cada vista — ver
detalle y metodología en la Tarea 1, sección 3):**

| Vista | Columnas afectadas | Columnas totales |
|---|---|---|
| `Website.Customers` | **14** | 14 (100%) |
| `Website.Suppliers` | **12** | 12 (100%) |
| `Website.VehicleTemperatures` | **0** | 6 (tabla raíz distinta, verificado) |

Nota de alcance: el NodeStore actual solo tiene 3 vistas con columnas de salida (las 3 de
`Website`, WWI) — `AdventureWorks2019` sigue sin procesar (Tarea A, aún abierta), así que esta
tabla es exhaustiva para lo que hoy existe en el store, no para "todas las vistas posibles de
la base de datos real".

### 3. Riesgos clave

1. **Punto ciego de SQL dinámico — ACTUALIZADO, ya no es 34/34.** Se arregló un hueco real del
   parser (`AstWalker.ResolveLiteral` no entendía `QUOTENAME(@var)` dentro de una concatenación
   dinámica, aunque `@var` viniera de un `SET @var = N'literal'` resoluble — un patrón muy común
   en DDL generado dinámicamente). Tras el fix: **17/34 resueltos** — las 17 sentencias
   `DROP TRIGGER` ahora dan el texto literal completo y confirman, columna por columna, las
   MISMAS 17 tablas ya conocidas por las `ALTER` estáticas. **El riesgo "¿hay una tabla 18ª
   oculta?" queda descartado, no solo mitigado.** Lo que sigue sin resolver son los 17 cuerpos
   `CREATE TRIGGER` (bloqueados por un `CASE WHEN ... THEN QUOTENAME(...) ELSE ...` — evaluar
   booleans estáticamente es una extensión mayor, no incluida en este arreglo): ahí el riesgo
   residual es de detalle de columna (qué columnas exactas copia el trigger AFTER INSERT/UPDATE),
   no de alcance de tablas.
2. **Las dos vistas de cara al cliente dependen al 100% de este procedimiento.** No es solo
   "afecta a una vista" — `Website.Customers` y `Website.Suppliers` (los dos portales de
   autoservicio externos del corpus) no tienen NINGUNA columna de salida independiente de las 17
   tablas tocadas. Un fallo a mitad de la desactivación de versionado temporal en cualquiera de
   esas tablas puede dejar el portal de clientes/proveedores sirviendo datos inconsistentes
   durante la ventana de carga.
3. **Es una operación de esquema (`ALTER ... SET SYSTEM_VERSIONING = OFF`), no de datos — pero
   el riesgo no es menor por eso.** Mientras el versionado está desactivado (entre este
   procedimiento y su contraparte `ReactivateTemporalTablesAfterDataLoad`), esas 17 tablas
   pierden temporalmente la garantía de auditoría histórica que ofrece SQL Server. Si el cambio
   propuesto introduce una ruta de fallo entre desactivar y reactivar (p.ej. una excepción no
   capturada a mitad de los 87 pasos), las tablas podrían quedar sin re-versionar — un problema
   de integridad silencioso que no aparece en ningún log de errores de aplicación.

---

## Ronda 2 (`docs/auditor-challenge.md` § 5) — re-test tras Tarea I + fix de `QUOTENAME`

NodeStore regenerado de cero (`out/` y `eval/bad-practices/graph_full.nodes/`) con el binario
actual antes de repetir el análisis. Verificación cruzada con `eval/auditor-challenge/verify.mjs`
(TODOS OK) más una segunda pasada manual sobre `model.json`/`object.json` para cubrir métricas
que el test no comprueba (ranking completo de hotspots, `fk_out_count`, degree de tablas).

### Delta medido vs Ronda 1

| Métrica | Ronda 1 | Ronda 2 | ¿Cambió? |
|---|---|---|---|
| Ranking de hotspots (1-5) | igual | igual | **NO** |
| `DeactivateTemporalTablesBeforeDataLoad`: `cc` / `degree` | 19 / 21 | 19 / 21 | NO |
| `DeactivateTemporalTablesBeforeDataLoad`: `unresolved_dynamic_sql_steps` | **34/34** | **17/34** | **SÍ** |
| Las 17 tablas `WRITES_TO` | mismas 17 | mismas 17 | NO |
| `Website.Customers` cobertura (`lineage_path.json`) | 14/14 | 14/14 | NO |
| `Website.Suppliers` cobertura | 12/12 | 12/12 | NO |
| `Website.VehicleTemperatures` (caso negativo) | 0/6 | 0/6 | NO |
| Hotspots `BadPracticesDB` (degree/cc/`unresolved_dyn`, las 16 SqlObject + 8 tablas) | iguales | iguales (comparado campo a campo) | NO |

### Conclusión de la Ronda 2

**El único cambio real es exactamente el predicho por el motivo técnico (2) del enunciado de
Ronda 2 — nada más, nada menos.** Esto no es un resultado trivial, es la confirmación de que el
fix de `QUOTENAME` fue quirúrgico:

- El fix es **descriptivo** (`AstWalker.ResolveLiteral` solo mejora el texto reconstruido de
  `dynamic_sql`; per su propio comentario de código, "the result is NOT re-parsed into
  READS/WRITES/CALLS edges"). Por diseño, no podía tocar ninguna arista del grafo — y no lo
  hizo: ni `degree`, ni `WRITES_TO`, ni `lineage_path.json` se movieron.
- Las 17 tablas ya eran conocidas vía los `ALTER` literales del procedimiento (no dependían del
  SQL dinámico para nada) — el fix solo corrobora ese conocimiento con texto literal de las
  sentencias `DROP TRIGGER`, no añade ni quita ninguna tabla.
- Tarea I (`lineage_path.json`) ya estaba implementada y desplegada ANTES de la Ronda 1, así
  que no había nada pendiente de cambiar ahí entre rondas — su presencia en ambas rondas es
  consistencia, no novedad.

**Lo que esto aporta:** no es que "el plan de mejora cambiara" — es la prueba de que un cambio
acotado en el parser se propaga al informe de auditoría de forma predecible y nada más: cambia
exactamente el campo que debía cambiar (`unresolved_dynamic_sql_steps`, que ya estaba marcado en
la Ronda 1 como el principal riesgo de "puede haber algo oculto") y dejó intacto todo lo que no
debía cambiar (ranking, degree, lineage). Es el mismo tipo de garantía que ya buscábamos con
`eval/auditor-challenge/verify.mjs`, pero extendida más allá de lo que ese script comprueba
automáticamente (ranking completo, métricas de `BadPracticesDB`) — confirmado a mano esta vez,
candidato a añadir al script si se repite este ejercicio una tercera vez.

---

## Nota posterior: gap 5.2 cerrado — `unresolved_dynamic_sql_steps` 17 → 0

Después de la Ronda 2 se cerró también el gap 5.2 (`docs/extraction-gaps.md` § 5.2): el fix
de `AstWalker.ResolveLiteral` se extendió a `NCHAR(n)`/`CHAR(n)`, `CoalesceExpression` y
`SearchedCaseExpression` (con un evaluador booleano estático nuevo, `ResolveBoolean`). El
bloqueador que quedaba no era el `CASE`/`COALESCE` que suponía el diagnóstico, sino
`@CrLf = NCHAR(13) + NCHAR(10)`, concatenado en todos los cuerpos `CREATE TRIGGER`.

**Efecto sobre este informe:** `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad`
pasa de `unresolved_dynamic_sql_steps = 17` a **0** (los 34 pasos resuelven a texto literal).
El "riesgo residual de lógica de los 17 `CREATE TRIGGER` sin resolver" mencionado en la
Ronda 2 queda **cerrado**: ahora cada cuerpo de trigger es texto literal inspeccionable. Sigue
sin cambiar el alcance de tablas (las mismas 17, sin tabla 18ª) ni ninguna arista del grafo —
el fix es descriptivo, igual que el de `QUOTENAME`. `eval/auditor-challenge/verify.mjs` endurece
su assert de `<= 17` a `== 0` para que este informe no vuelva a quedar por detrás del código.
