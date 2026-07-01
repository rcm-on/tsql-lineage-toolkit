# Plan de mejora estratégica de la base de datos — a partir de la lectura del NodeStore

Cada hallazgo de este plan viene de leer el NodeStore generado
(`out/graph_full.nodes/`) de WideWorldImporters — `index.json.stats`,
`model.json` y `object.json`/`shared/tables/*.json` puntuales — no de
intuición. Ordenado por **dependencias**: qué hay que confirmar/arreglar
antes de poder confiar en el siguiente paso.

---

## Fase 0 — Verificar antes de actuar (gating de todo lo demás)

### 0.1 — CERRADO (verificado 2026-06-23): `AstWalker.cs` sí resuelve `FOR SYSTEM_TIME`

**Hallazgo:** 13 de 47 objetos tienen `degree=0` en `model.json` (sin ningún
`CALLS`/`AFFECTS`/`WRITES_TO`/`READS_FROM` a nivel objeto):

```
Integration.GetCityUpdates, GetCustomerUpdates, GetEmployeeUpdates,
GetPaymentMethodUpdates, GetStockItemUpdates, GetSupplierUpdates,
GetTransactionTypeUpdates, GetTransactionUpdates, ...
```

Inspeccionando `objects/.../Integration.GetCityUpdates/object.json`: el
procedimiento sí lee de tablas reales (cursor sobre `City FOR SYSTEM_TIME
BETWEEN ...`), pero su `edges_out` solo tiene `BELONGS_TO`/`ACTION`/
`USES_VARIABLE`/`GOVERNS` — **ningún `READS_FROM`**. Confirmado en
`AstWalker.cs`: la única mención de `SYSTEM_TIME` es para detectar
`ADD/DROP PERIOD FOR SYSTEM_TIME` (DDL), no para resolver la cláusula
`FOR SYSTEM_TIME AS OF/BETWEEN` dentro de un `FROM`.

**Por qué esto bloqueaba todo lo demás (mientras estuvo abierto):** "degree
de una tabla/objeto" en el NodeStore estaría sub-contado para cualquier
procedimiento que consulte tablas temporales con `FOR SYSTEM_TIME` —
exactamente el patrón que usa toda la familia `Integration.Get*Updates` de
WideWorldImporters (es el mecanismo de captura de cambios de la BD de
ejemplo). Un análisis de impacto (Fase 1, 2) hecho sobre datos con este hueco
*subestimaría* quién lee `Application.Cities`, `Sales.Customers`, etc.

**Verificación (2026-06-23):** el código actual ya resuelve correctamente
`FOR SYSTEM_TIME`. No fue necesario tocar `AstWalker.cs` — el hueco real no
era la cláusula `SYSTEM_TIME` en sí (en el AST de ScriptDom sigue siendo un
`NamedTableReference` normal, con la propiedad `TemporalClause` añadida; el
nombre de tabla se resuelve igual con o sin ella), sino que los `READS_FROM`
dentro del `SELECT` de un cursor se perdían por completo — y los procedimientos
`Integration.Get*Updates` leen vía cursor. Ese fix más general ya está en
`AstWalker.cs` (ver el comentario en la rama `DeclareCursorStatement`, que
recorre cada `QuerySpecification` del cursor con `CollectTableRefs`/
`CollectTableRefsInto`), y arrastró consigo el caso `SYSTEM_TIME` sin cambio
dedicado. Confirmado con el test
`ReadsFrom_SurvivesCursorBodyAndTempTarget` (caso `insert-temp-systime`,
`tests/TSqlParser.Tests/NodeStoreUpdateTests.cs:227`) — pasa en verde junto
con el resto del Theory.

*Depende de:* nada. *Ya no bloquea* Fases 1 y 2 — pueden basarse en los
datos actuales del NodeStore sin necesidad de regenerarlo por este motivo
(a diferencia de lo que decía 2.1 más abajo).

### 0.2 — CERRADO (implementado y verificado 2026-06-23): tablas `_Archive` con degree=0 ya se clasifican

**Hallazgo:** 17 tablas tienen `degree=0` en `model.json`, todas con sufijo
`_Archive` (`Application.People_Archive`, `Sales.Customers_Archive`, etc.) +
una variable de sesión `#CurrentValue`. Ninguna aparece como destino de
`INSERT`/`UPDATE` en ningún procedimiento analizado.

**Lectura correcta (no es un bug del analizador ni de la BD):** son las
tablas de historial de *system-versioned temporal tables* de SQL Server — el
motor las puebla automáticamente en cada `UPDATE`/`DELETE` de la tabla
principal, nunca aparecen por nombre en T-SQL de aplicación. `degree=0` aquí
es la respuesta correcta, no una alarma.

**Corrección sobre la acción original:** el plan asumía que `dashboard/src/risks.js`
ya tenía una regla de "tabla huérfana/sin uso" que listaría estas tablas
junto a las verdaderamente huérfanas. Revisado: no existe tal regla en el
dashboard — la única cercana (`Tabla escrita pero nunca leída`, `risks.js:91`)
exige `writers.length > 0`, que las `_Archive` nunca cumplen, así que ni
siquiera aparecían ahí. El riesgo real estaba en el propio NodeStore: un
agente leyendo `model.json` directamente (su forma normal de consumirlo) ve
`degree=0` sin más contexto y puede confundirlo con dato muerto.

**Implementado:** `NodeStoreExporter.cs` (sección de `model.json`, rama
`Table`) ahora compara el degree de cada tabla `<Tabla>_Archive` contra el de
`<Tabla>`; si `<Tabla>_Archive` tiene degree=0 y `<Tabla>` existe con
degree>0, añade `"classification": "historial temporal, esperado"` al nodo.
Verificado con el test `Write_ModelJson_ClassifiesArchiveTableAsExpectedTemporalHistory`
(`tests/TSqlParser.Tests/NodeStoreUpdateTests.cs`) — confirma la clasificación
en la tabla `_Archive` y su ausencia en la tabla base. Suite completa: 75/75
en verde tras el cambio.

*Depende de:* nada. *No bloqueaba* nada — era paralelo a 0.1.

---

## Fase 1 — Auditoría de la base de datos real (depende de 0.1 + 0.2 cerrados)

### 1.1 — CERRADO (auditado 2026-06-23): 35/64 tablas sin `FK_TO` saliente, sin deuda real

**Hallazgo original:** de 51 tablas en el grafo, 22 no tienen ninguna arista
`FK_TO` saliente. Algunas serán legítimamente hoja (tablas de lookup
simples); otras pueden ser relaciones que existen en SQL Server pero que el
extractor no capturó, o relaciones que *debieran* existir y no están
declaradas como FK real (deuda de integridad referencial).

**Auditoría real, sobre el NodeStore regenerado tras 0.1** (ahora 64 tablas
en el grafo, 35 sin `FK_TO` saliente — la base creció porque 0.1 añadió
lecturas reales que antes no aparecían, no porque el problema haya
empeorado):

| Categoría | Tablas | Diagnóstico |
|---|---|---|
| `sys.*` (catálogo del motor) | 15 | No son tablas de negocio — aparecen porque los `Configuration_*` las consultan en SQL dinámico (`sys.tables`, `sys.procedures`, etc.). No aplica auditoría de FK. |
| `_Archive` (historial temporal) | 17 | Por diseño no llevan FK propia — replican la estructura de la tabla base sin sus constraints. No es deuda, confirma [[0.2]]. |
| Ruido (no es tabla real) | 1 | `#CurrentValue`: variable de sesión, se excluye del análisis. |
| Candidatas reales | 2 | `Warehouse.ColdRoomTemperatures`, `Warehouse.VehicleTemperatures` (ver abajo). |

Las 2 candidatas reales tienen `columns: []` en `shared/tables/...json` —
**no es que falte el FK, es que no se extrajo ningún DDL (`CREATE TABLE`)
para estas dos tablas**; solo se conocen porque `Configuration_EnableInMemory`
las referencia por nombre en SQL dinámico (`WRITES_TO`/`AFFECTS`). Sin DDL no
hay nada que clasificar en (a)/(b)/(c) — y no hay forma de obtenerlo sin una
conexión a la BD real o el script de creación completo, que no están
disponibles en este `input.json`.

**Conclusión: no hay deuda de integridad referencial real en este corpus.**
32 de las 35 candidatas iniciales quedan explicadas por diseño; las 2
restantes son un hueco de **datos de entrada**, no un bug de
`TableSchemaExtractor.cs` ni una FK real faltante en la base — queda anotado
como limitación conocida, no como acción pendiente.

*Depende de:* 0.1 (cerrado). *Bloqueaba:* nada más — 1.2 puede usar estos
números.

### 1.2 — Hotspots de tabla recalculados tras 0.1: candidatas a contrato explícito

**Hallazgo original** (antes de 0.1, degree sub-contado):

| Tabla | Degree |
|---|---|
| `Application.People` | 41 |
| `Sales.Customers` | 23 |
| `Warehouse.StockItems` | 22 |
| `Purchasing.Suppliers` | 15 |
| `Application.Cities` | 13 |

**Recalculado 2026-06-23, sobre el NodeStore regenerado tras 0.1**
(excluyendo `sys.*` y `_Archive`, que no son tablas de negocio — ver 1.1):

| Tabla | Degree | Δ vs antes de 0.1 |
|---|---|---|
| `Application.People` | 45 | +4 |
| `Sales.Customers` | 24 | +1 |
| `Warehouse.StockItems` | 23 | +1 |
| `Purchasing.Suppliers` | 16 | +1 |
| `Application.Cities` | 15 | +2 |

Los degree subieron como predecía el plan original (0.1 añadió lecturas
reales que antes faltaban — confirmado en `Integration.GetCityUpdates`, que
pasó de 0 a 6 `READS_FROM`, incluyendo `Application.Cities` vía su
`FOR SYSTEM_TIME AS OF`), pero **el orden de los hotspots no cambió**.
`Application.People` sigue siendo el hub real de la base de datos (toda
entidad de negocio referencia a una persona: empleado, cliente, proveedor de
contacto) y casi duplica a la segunda — un cambio de esquema en `People`
tiene el radio de impacto más amplio de todo el sistema, medible
directamente en `shared/tables/<People>.json`'s `refs` (ya agrupado por
objeto contribuyente).

Mediana de degree sobre las 31 tablas de negocio reales (excl. `sys.*`/
`_Archive`/`#CurrentValue`): **8**. Umbral 2x mediana = 16, así que las
candidatas a contrato explícito son las 4 que lo alcanzan o superan:
`People` (45), `Customers` (24), `StockItems` (23), `Suppliers` (16) —
`Cities` (15) queda justo debajo del umbral.

**Acción:** para esas 4 tablas, generar y mantener un contrato de esquema
explícito (qué columnas son estables, cuáles están en deprecación) y exigir
que cualquier PR que las toque incluya la lectura de
`shared/tables/<tabla>.json` en la descripción del PR — ya tienes la
información agregada y verificada, falta el proceso que la use. Esto es una
decisión de proceso del equipo, no una tarea de código pendiente en este
toolkit.

*Depende de:* 0.1 (cerrado, números ya recalculados arriba).

### 1.3 — CERRADO (recalculado 2026-06-23): mismos 5 procedimientos, sin acción de código pendiente

**Recalculado sobre el NodeStore regenerado tras 0.1**, vía
`cyclomatic_complexity` y `dynamic_sql_steps` en `model.json` (ya no hace
falta abrir cada `object.json` uno por uno — ver garantía de completitud en
`index.json.howto`):

| Procedimiento | CC | Steps | Steps dinámicos |
|---|---|---|---|
| `DataLoadSimulation.Configuration_ApplyDataLoadSimulationProcedures` | 21 | 42 | 20 |
| `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad` | 19 | 87 | 34 |
| `Application.Configuration_ApplyPartitioning` | 12 | 28 | 20 |
| `Application.Configuration_EnableInMemory` | 7 | 34 | 29 |
| `Application.Configuration_ApplyFullTextIndexing` | 7 | 20 | 15 |

El `cyclomatic_complexity` de cada uno es idéntico al hallazgo original (21,
19, 12, 7, 7) — no cambió con 0.1, como se esperaba (0.1 afecta lecturas de
tabla, no la complejidad de control de flujo). El conteo de `Steps` sí subió
(p. ej. 69→87 en `DeactivateTemporalTablesBeforeDataLoad`) por una mejora
previa, no de hoy: los pasos de ciclo de vida de cursor (`OPEN_CURSOR`,
`FETCH`, `CLOSE`, `DEALLOCATE`) ahora se cuentan como steps explícitos en vez
de quedar implícitos en el flag `HasCursor`.

**Corrección sobre la cifra original:** estos 5 concentran 118 de los 141
`dynamic_sql_steps` del corpus (**~84%**, no "90%+" como decía la versión
anterior — la cifra original era una estimación, esta es la suma real sobre
`model.json`).

Son los procedimientos de "configuración de features" de la demo (activar/
desactivar partitioning, in-memory, full-text, auditing) — generan DDL letra
a letra para iterar sobre N tablas, de ahí la complejidad: el patrón en sí
(DDL parametrizado por tabla) es razonable, pero
`Configuration_ApplyDataLoadSimulationProcedures` (CC=21) y
`DeactivateTemporalTablesBeforeDataLoad` (87 steps) son los candidatos reales
a romper en sub-procedimientos por tabla/responsabilidad si esto fuera
código de producción a mantener, no una demo.

*Depende de:* nada técnico (confirmado, era lectura directa). *Sin acción de
código en este toolkit* — el refactor en sí queda gateado por 2.2 (decisión
de negocio: solo aplica si esta BD deja de ser dataset de referencia).

*Depende de:* nada técnico — es lectura directa de `object.json`, ya
correcta hoy. Puede empezar en paralelo a la Fase 0.

---

## Fase 2 — Cierre de hallazgos (depende de 1.1-1.3 completados)

### 2.1 Re-ejecutar el análisis de impacto tras 0.1

Una vez resuelto `FOR SYSTEM_TIME` (0.1), regenerar el NodeStore
(`update-nodestore`) y repetir 1.1/1.2 — los números de degree de
`Integration.Get*Updates` y de las tablas que leen (`Application.Cities`,
`Sales.Customers`, etc.) cambiarán, y el contrato de tablas hotspot (1.2)
debe fijarse sobre datos correctos, no sobre el hueco conocido.

*Depende de:* 0.1 + 1.2.

### 2.2 Decidir si los `Configuration_*` con CC alto (1.3) se refactorizan

Solo aplica si esta base de datos deja de ser un dataset de referencia
(WideWorldImporters demo) y pasa a mantenerse como código de producción real
— en ese caso, 1.3 ya da la lista priorizada y el motivo (DDL repetitivo por
tabla → extraíble a un sub-procedimiento parametrizado por nombre de tabla).

*Depende de:* 1.3 + decisión de negocio (no técnica) sobre si se mantiene
este código como producción.

---

## Resumen de dependencias

```
0.1 (FOR SYSTEM_TIME en AstWalker) ──┬──→ 1.1 (auditar FKs faltantes)
                                      ├──→ 1.2 (contrato tablas hotspot) ──→ 2.1 (re-medir tras 0.1)
0.2 (clasificar _Archive) ───────────(paralelo, no bloquea nada)
1.3 (procs alta complejidad/dinámicos) ─────────────────────────→ 2.2 (refactor, gated por decisión de negocio)
```

**Regla de secuenciación:** no fijar contratos de tabla (1.2) ni cerrar la
auditoría de FKs (1.1) antes de 0.1 — el propio dato que usarías para decidir
está incompleto hasta entonces. Es la misma lección que ya costó cara en
[docs/nodestore-analysis.md](nodestore-analysis.md): medir/leer el dato real
antes de actuar sobre la intuición de qué tabla es "central" o qué FK "falta".

---

## Fase 3 — Evolución del NodeStore para Agentes (Agent-First Design)

El análisis en `docs/nodestore-analysis.md` demuestra que el diseño del NodeStore es fundamental para la eficiencia de un agente de IA. Los Casos 2 y 6 confirman que los campos pre-calculados (`condition_path`) y las vistas especializadas (`nav.json`) reducen drásticamente los loops y el contexto. Esta fase propone ir un paso más allá, evolucionando el NodeStore de ser un "grafo consultable" a una "API de conocimiento" para agentes.

### 3.1 — De Datos a Insights: `impact_summary` pre-calculado

**Problema:** Para responder "¿cuál es el impacto de cambiar X?", un agente debe leer `model.json` o `shared/tables/{slug}.json`, contar las referencias (`degree`, `refs`), y sintetizar una conclusión en lenguaje natural. Esto consume tokens y ciclos de razonamiento.

**Propuesta:** Añadir un campo `impact_summary` a los nodos clave en `model.json` y a los ficheros de objeto/tabla. Este campo contendría un resumen en lenguaje natural, pre-calculado en el momento de la exportación.

**Ejemplo para una tabla (`shared/tables/application.people.json`):**
```json
"impact_summary": "Alto impacto. Esta tabla es un hub central, referenciada por 45 objetos. Cambios en su esquema afectarán a procedimientos de Clientes, Proveedores y Empleados."
```

**Ejemplo para un procedimiento (`objects/.../usp_ProcessOrders/object.json`):**
```json
"impact_summary": "Este procedimiento escribe en 3 tablas ('Orders', 'OrderLines', 'AuditLog') y llama a 2 otros ('usp_CalculateTaxes', 'usp_SendNotification'). Contiene 1 riesgo de alta severidad (Transacción sin TRY/CATCH)."
```

**Ventaja para el agente:** Convierte una tarea de agregación y síntesis (múltiples pasos) en una lectura directa (un solo paso). Responde directamente a las preguntas más comunes de alto nivel.

### 3.2 — CERRADO (implementado y verificado 2026-06-30, Tarea I): De Navegación a Lineage: `lineage_path.json`

**⚠️ Esta sección quedó desactualizada — el formato de abajo (`"path": "A -> B -> C"`
como string) fue la "Opción 1" que se discutió y se **rechazó explícitamente** en
`docs/agent-collab.md` (no respeta la naturaleza DAG del lineage: una columna puede tener
varias fuentes inmediatas vía `UNION`/`JOIN`, un string lineal no lo representa). Se
implementó en su lugar la **"Opción 2 Refinada"**, con un formato distinto:

```json
// En objects/<slug>/lineage_path.json (formato REAL, no el de abajo)
{
  "FinalPrice": {
    "roots": ["Base.Products.Price"],
    "immediate": ["Staging.Calc.PreTaxPrice"],
    "depth": 2,
    "transformation_summary": null
  }
}
```

Spec completa: `docs/lineage-path-spec.md` (autoría Gemini). Medición que justificó pasar a
esta opción en vez de quedarse con la navegación `nav.json` a pelo: `docs/nodestore-analysis.md`
§ Caso 7. Implementación: `NodeStoreExporter.cs` (`TraceLineage`/`ColumnDisplayName`, sección
"Capa 4"). Verificado contra WWI real y un caso sintético de 3 vistas apiladas
(`eval/community-edge-cases/lineage-chain/`), sin regresión. Bitácora completa de la
implementación y la revisión cruzada de Gemini en `docs/agent-collab.md` (buscar "Tarea I").
**No reimplementar ni rediseñar el formato sin releer esa discusión primero.**

---

Texto original de la propuesta (histórico, formato YA NO vigente — ver arriba):

**Problema:** Trazar el lineage de una columna específica de principio a fin es la tarea más compleja, requiriendo que el agente recorra un grafo fino de aristas `DERIVES_FROM` a través de múltiples objetos. `nav.json` resuelve la navegación de objetos, pero no el lineage de columnas.

**Propuesta:** Crear un nuevo tipo de fichero especializado: `objects/{slug}/lineage_path.json`. Este fichero pre-calcularía y materializaría los caminos completos de lineage para las columnas de salida de un objeto (vista o procedimiento).

**Ventaja para el agente:** Hace que una de las preguntas más difíciles ("¿de dónde viene este dato?") sea una consulta O(1), eliminando la necesidad de que el agente implemente un algoritmo de recorrido de grafos.

*Depende de:* Nada. Puede implementarse en paralelo. *(histórico — ya implementado, ver arriba)*
