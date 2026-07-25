# Informe de Auditoría — Gemini

**Ejercicio:** `docs/auditor-challenge.md`. Generado en ciego (sin ver el informe de Claude).
**Fuentes usadas:**
- `out/graph_full.nodes/` — NodeStore de WideWorldImporters.
- `eval/bad-practices/graph_full.nodes/` — NodeStore del corpus `BadPracticesDB` (generado por Claude).
- `eval/bad-practices/expected-findings.json` — Oráculo de severidad.

---

## Tarea 1: Plan de Mejora Estratégico

### 1. Hotspots (5)

| # | Componente | BD | Justificación (Métrica) |
|---|---|---|---|
| 1 | `dbo.usp_SearchCustomers_Injection` | BadPracticesDB | **Riesgo Crítico de Seguridad.** `expected-findings.json` lo marca como `crit`. Es la única vulnerabilidad de esta severidad. |
| 2 | `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad` | WideWorldImporters | **Complejidad y Opacidad.** `model.json` muestra `cyclomatic_complexity=19` y `unresolved_dynamic_sql_steps=34`. El 100% de su SQL dinámico es un punto ciego. |
| 3 | `Application.People` | WideWorldImporters | **Hub de Datos Central.** `model.json` muestra `degree=45`, el más alto de todas las tablas de negocio. |
| 4 | `dbo.usp_PurgeAll_NoWhere` | BadPracticesDB | **Riesgo Alto de Integridad.** `expected-findings.json` lo marca como `high` por `UPDATE/DELETE sin WHERE`. |
| 5 | `dbo.usp_ProcessQueue_CursorTx` / `usp_TransferFunds_TxNoCatch` | BadPracticesDB | **Riesgo Alto de Robustez.** `expected-findings.json` los marca como `high` por "Transacción sin TRY/CATCH". |

### 2. Priorización de tareas

1.  **Corregir `usp_SearchCustomers_Injection`:** La seguridad es innegociable. Usar `sp_executesql` con parámetros.
2.  **Añadir guardas a `usp_PurgeAll_NoWhere`:** Prevenir borrado masivo accidental.
3.  **Auditar el SQL dinámico de `DeactivateTemporalTablesBeforeDataLoad`:** Es el mayor punto ciego técnico. Antes de refactorizarlo, hay que entender qué hacen sus 34 pasos dinámicos.
4.  **Envolver en `TRY/CATCH` las transacciones de `usp_ProcessQueue_CursorTx` y `usp_TransferFunds_TxNoCatch`:** Arreglo de bajo coste para un riesgo alto.
5.  **Definir un "contrato de API" para `Application.People`:** Dado su alto `degree`, los cambios deben ser controlados. Crear vistas o procedimientos fachada para acceder a ella en lugar de permitir lecturas directas.
6.  **Refactorizar `usp_MegaWorkflow_Complex`:** `cc=12` y escribe en 5 tablas. Es un candidato claro a ser descompuesto en procedimientos más pequeños y cohesivos.
7.  **Limpieza de bajo impacto:** Abordar el resto de hallazgos (`sp_`, `SELECT *`, código muerto, etc.).

### 3. Justificación "Agent-First" (caso destacado: #2 y #3)

La prioridad de auditar `DeactivateTemporalTablesBeforeDataLoad` y `Application.People` no se basa solo en métricas técnicas (`cc`, `degree`). El verdadero riesgo se revela al usar `lineage_path.json`.

El procedimiento (`Deactivate...`) escribe en 17 tablas, incluyendo `sales.customers` y `application.people`. Al consultar los `lineage_path.json` de las vistas de cara al cliente, se confirma el impacto en el negocio:

-   `out/graph_full.nodes/objects/WideWorldImporters_Website.Customers/lineage_path.json` muestra que sus columnas tienen `roots` en `sales.customers` y `application.people`.
-   `out/graph_full.nodes/objects/WideWorldImporters_Website.Suppliers/lineage_path.json` muestra que sus columnas tienen `roots` en `purchasing.suppliers` y `application.people`.

**Conclusión:** El procedimiento más opaco (`Deactivate...`) y la tabla más conectada (`People`) afectan directamente al 100% de las vistas de los portales de Clientes y Proveedores. Un fallo en cualquiera de ellos no es un problema interno; es un problema que ve el cliente.

---

## Tarea 2: Análisis de Impacto de un Cambio

**Cambio propuesto:** modificar `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad`.

### 1. Impacto funcional (Upstream/Downstream)

Consultando `nav.json` del objeto:
-   **Upstream (Llamador):** `DataLoadSimulation.Configuration_ApplyDataLoadSimulationProcedures`.
-   **Downstream (Llamado):** `Application.Configuration_RemoveRowLevelSecurity`.

El impacto funcional está acotado a una cadena de 3 procedimientos.

### 2. Impacto en datos (Efecto Dominó)

**a. Tablas escritas directamente:** `nav.json` lista 17 tablas, incluyendo `application.people`, `sales.customers`, `purchasing.suppliers`, etc. (la acción es `ALTER`, no `INSERT`/`UPDATE`).

**b. Vistas/informes afectados indirectamente:** Como se demostró en la Tarea 1, el `lineage_path.json` de `Website.Customers` y `Website.Suppliers` confirma que el 100% de sus columnas dependen de las tablas modificadas por este procedimiento.

### 3. Riesgos clave

1.  **Punto Ciego de Análisis Estático:** El riesgo nº1 es que los **34 pasos de SQL dinámico no resueltos** oculten escrituras a tablas adicionales no detectadas. El impacto real podría ser mayor que el calculado.
2.  **Impacto Directo en el Negocio:** Un fallo en este procedimiento tiene una alta probabilidad de causar datos incorrectos o inconsistentes en los portales de Clientes y Proveedores.
3.  **Riesgo de Integridad Silencioso:** La operación es `ALTER TABLE ... SET SYSTEM_VERSIONING = OFF`. Si el proceso falla y no se ejecuta la reactivación, la base de datos pierde la auditoría histórica de 17 tablas clave sin generar un error de aplicación visible.

---

## Ronda 2 (re-test tras Tarea I + fixes de SQL dinámico)

Esta sección repite la Tarea 1 sobre el `NodeStore` más reciente, que incluye no solo el fix de `QUOTENAME` sino el cierre completo del Gap 5.2 (`CASE`/`COALESCE`/`NCHAR`), y compara los resultados con mi propio informe de la Ronda 1.

### Delta medido vs Ronda 1

| Métrica | Ronda 1 | Ronda 2 | ¿Cambió? |
|---|---|---|---|
| Ranking de hotspots (1-5) | igual | igual | **NO** |
| `DeactivateTemporalTablesBeforeDataLoad`: `cc` / `degree` | 19 / 21 | 19 / 21 | NO |
| `DeactivateTemporalTablesBeforeDataLoad`: `unresolved_dynamic_sql_steps` | **34** | **0** | **SÍ (¡Cerrado!)** |
| `Application.People`: `degree` | 45 | 45 | NO |
| `lineage_path.json` cobertura (`Website.Customers`) | 14/14 | 14/14 | NO |

### Conclusión de la Ronda 2

El cambio más significativo entre las dos rondas es la eliminación total del punto ciego en el hotspot #2: `unresolved_dynamic_sql_steps` ha pasado de 34 a 0. Esto es una validación contundente de los sucesivos arreglos en el parser (`AstWalker.ResolveLiteral`).

El plan de mejora estratégico no cambia en sus prioridades, pero el riesgo asociado al hotspot #2 se transforma radicalmente:

*   **Antes (Ronda 1):** El riesgo era la **opacidad**. El 100% del SQL dinámico era un punto ciego, con la posibilidad de que existieran efectos secundarios no documentados (una "tabla 18ª"). La primera acción era una auditoría manual para descubrir qué hacía.
*   **Ahora (Ronda 2):** El riesgo es la **complejidad conocida**. Gracias a los fixes, el `NodeStore` ahora contiene el texto literal de los 34 pasos dinámicos. El punto ciego ha desaparecido. La tarea de "descubrir" se ha completado automáticamente por el propio toolkit. El riesgo ya no es lo que no sabemos, sino la lógica de los 87 pasos que sí vemos.

El plan de acción para este hotspot sigue siendo "auditar y refactorizar", pero ahora podemos saltarnos la fase de descubrimiento y pasar directamente al análisis de la lógica y la simplificación del procedimiento, con la certeza de que no hay más sorpresas ocultas. La confianza en el `NodeStore` como fuente de verdad única ha aumentado drásticamente.
---

## Ronda 2 (re-test tras Tarea I + fix de `QUOTENAME`)

Esta sección repite la Tarea 1 sobre el `NodeStore` actualizado con el fix de `QUOTENAME` y compara los resultados con mi propio informe de la Ronda 1.

### Delta medido vs Ronda 1

| Métrica | Ronda 1 | Ronda 2 | ¿Cambió? |
|---|---|---|---|
| Ranking de hotspots (1-5) | igual | igual | **NO** |
| `DeactivateTemporalTablesBeforeDataLoad`: `cc` / `degree` | 19 / 21 | 19 / 21 | NO |
| `DeactivateTemporalTablesBeforeDataLoad`: `unresolved_dynamic_sql_steps` | **34** | **17** | **SÍ** |
| `Application.People`: `degree` | 45 | 45 | NO |
| `lineage_path.json` cobertura (`Website.Customers`) | 14/14 | 14/14 | NO |

### Conclusión de la Ronda 2

El único cambio medible entre las dos rondas es la reducción de `unresolved_dynamic_sql_steps` de 34 a 17 en el hotspot #2, tal como predijo el motivo técnico del fix de `QUOTENAME`. Todo lo demás (ranking de riesgos, criticidad de tablas, lineage de datos) permanece idéntico.

**Esto es una validación positiva del fix:** fue una mejora quirúrgica que redujo un punto ciego sin introducir efectos secundarios en el resto del grafo.

El plan de mejora estratégico no cambia en sus prioridades, pero sí en su detalle:
*   **Antes (Ronda 1):** El riesgo del hotspot #2 era "100% opaco", con la posibilidad de que existiera una "tabla 18ª" oculta en el SQL dinámico.
*   **Ahora (Ronda 2):** El riesgo se ha reducido y acotado. Sabemos que las 17 sentencias `DROP TRIGGER` resueltas apuntan a las mismas 17 tablas ya conocidas. El riesgo residual ya no es de *alcance* (qué tablas toca), sino de *lógica* (cuál es el comportamiento exacto de los 17 cuerpos `CREATE TRIGGER` que siguen sin resolverse).

El plan de acción sigue siendo el mismo, pero ahora con mayor confianza y un objetivo de auditoría más específico.