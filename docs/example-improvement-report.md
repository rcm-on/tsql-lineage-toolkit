# Informe de mejora y estrategia — WideWorldImporters

> **Generado de forma determinista desde el NodeStore** (`out/graph_full.nodes/`)
> — 47 procedimientos, 1355 nodos, 3423 relaciones. Cada cifra sale de
> `model.json`/`object.json`, no de estimación. Reproducible: regenera el
> NodeStore y los números no cambian.

**Objetivo:** reducir acoplamiento entre SPs, anidamiento y complejidad,
priorizado por **criticidad** y ordenado por **dependencias** (qué tocar
antes de qué).

---

## 0. Hallazgos que cambian la estrategia (léelos antes del plan)

Dos cosas que los datos dicen y que evitan trabajo equivocado:

1. **El acoplamiento por SPs es BAJO, no un problema.** Solo **12 aristas
   `CALLS`** entre 47 procedimientos, y casi todas son orquestación legítima
   (un proc "fachada" que invoca sub-pasos), no dependencias enmarañadas. **No
   hay que romper este acoplamiento** — hay que *documentarlo y ordenarlo*. Un
   refactor agresivo aquí sería resolver un problema que no existe.

2. **La complejidad NO viene del anidamiento.** El `nesting_level` máximo de
   todo el corpus es **3**. La complejidad alta (cc hasta 21) viene del
   **volumen de SQL dinámico** (procs que construyen DDL tabla a tabla en un
   bucle plano), no de lógica profundamente anidada. Por tanto la palanca no es
   "aplanar IFs" — es **extraer el patrón de DDL dinámico repetido**.

---

## 1. Acoplamiento por SPs (`CALLS`)

Solo dos clústeres de orquestación reales:

**A) Fachada de configuración** — `Application.Configuration_ConfigureForEnterpriseEdition`
llama a 4 sub-procedimientos (fan-out=4, el más alto del corpus):

```
ConfigureForEnterpriseEdition
 ├─ Configuration_ApplyColumnstoreIndexing
 ├─ Configuration_ApplyFullTextIndexing
 ├─ Configuration_EnableInMemory
 └─ Configuration_ApplyPartitioning
```

**B) Cadena de carga de datos** — profundidad 3:

```
PopulateDataToCurrentDate
 └─ Configuration_ApplyDataLoadSimulationProcedures
     ├─ DeactivateTemporalTablesBeforeDataLoad
     │   └─ Application.Configuration_RemoveRowLevelSecurity
     └─ ReactivateTemporalTablesAfterDataLoad
         └─ Application.Configuration_ApplyRowLevelSecurity
```

Todo lo demás son llamadas sueltas (`ReseedAllSequences → ReseedSequenceBeyondTableValues`,
`InsertCustomerOrders → CalculateCustomerPrice`). **Acoplamiento sano.**

---

## 2. Complejidad y anidamiento

| Procedimiento | cc | steps | SQL dinámico | nesting |
|---|---|---|---|---|
| `DataLoadSimulation.Configuration_ApplyDataLoadSimulationProcedures` | **21** | 22 | 20 | 1 |
| `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad` | **19** | **69** | **34** | 1 |
| `Application.Configuration_ApplyPartitioning` | 12 | 22 | 20 | 2 |
| `Application.Configuration_EnableInMemory` | 7 | 31 | 29 | **3** |
| `Application.Configuration_ApplyFullTextIndexing` | 7 | 15 | 15 | 2 |

El patrón es claro: **complejidad = nº de pasos de DDL dinámico**, no
profundidad. `DeactivateTemporalTablesBeforeDataLoad` con **69 pasos / 34
dinámicos** es el outlier absoluto.

---

## 3. Criticidad

Score = `callers×3 + tablas_hotspot_tocadas×2 + complejidad + anidamiento`
(heurística explícita y ajustable; las tablas hotspot son las 5 de mayor
degree: `People` 41, `Customers` 23, `StockItems` 22, `Suppliers` 15, `Cities` 14).

| Procedimiento | crit | callers | hotspots | cc | escribe en N tablas |
|---|---|---|---|---|---|
| `DeactivateTemporalTablesBeforeDataLoad` | **15** | 1 | 5/5 | 19 | 17 |
| `ReactivateTemporalTablesAfterDataLoad` | **13** | 1 | 5/5 | 2 | 17 |
| `Website.CalculateCustomerPrice` | 8 | 1 | 2 | 5 | 0 |
| `Website.Customers` / `SearchForCustomers` / `Suppliers` … | 6 | 0 | 3 | 1 | 0 |

`DeactivateTemporalTablesBeforeDataLoad` es **el procedimiento más crítico de
la base**: toca las 5 tablas hotspot, escribe en 17 tablas, cc=19, y está en
la cadena de carga. Si algo se rompe ahí, el radio de impacto es máximo.

---

## 4. Plan de acción — por prioridad y dependencias

Prioridad = criticidad. Orden dentro de cada ítem = **dependencias primero**
(refactoriza la hoja antes que su llamador, para no mover el suelo bajo sus pies).

### P1 — `DeactivateTemporalTablesBeforeDataLoad` (crit 15) y su gemelo `Reactivate` (crit 13)

**Qué:** los dos procs más críticos, 69 y ~60 pasos de DDL dinámico casi
idéntico (desactivar/reactivar versionado temporal + RLS, tabla a tabla).
**Cómo:** extraer el bucle de DDL dinámico repetido a **un sub-procedimiento
parametrizado por nombre de tabla** (`@SchemaName, @TableName`), que ambos
invocan. Reduce 69 pasos a ~1 bucle + 1 llamada por tabla.
**Orden (dependencias):** estos dos llaman a `RemoveRowLevelSecurity` /
`ApplyRowLevelSecurity` → **refactoriza primero esas dos hojas** (P3), luego
estos. No al revés.
**Por qué primero:** máxima criticidad × máxima complejidad. El mayor retorno.

### P2 — `Configuration_ApplyDataLoadSimulationProcedures` (cc 21, fan-out 2)

**Qué:** la raíz de la cadena de carga (cc más alto del corpus), genera 20
`CREATE PROCEDURE` dinámicos.
**Cómo:** separar "qué procedimientos crear" (datos/metadatos) de "cómo
crearlos" (el motor de generación). No urge romper el `CALLS` a Deactivate/
Reactivate — es orquestación legítima.
**Orden:** **después** de P1, porque llama a los procs que P1 toca; si P1
cambia su firma, esta raíz se ajusta una sola vez.

### P3 — Hojas RLS: `Configuration_RemoveRowLevelSecurity` / `ApplyRowLevelSecurity`

**Qué:** las hojas de la cadena (cc 2-4, pocos pasos dinámicos).
**Cómo:** cambio pequeño — son las primeras en tocarse porque P1 depende de
ellas. Estabiliza su firma/contrato antes de refactorizar P1.
**Orden:** **lo primero en ejecutarse**, aunque su criticidad propia es baja:
son la base de la cadena P1→P3.

### P4 — Clúster fachada `ConfigureForEnterpriseEdition` + sus 4 hijos

**Qué:** `ApplyPartitioning` (cc 12), `EnableInMemory` (cc 7, nesting 3),
`ApplyFullTextIndexing`, `ApplyColumnstoreIndexing` — todos con el mismo
patrón de DDL dinámico por tabla.
**Cómo:** mismo refactor que P1 (extraer el generador de DDL dinámico
parametrizado), reutilizando el sub-proc que ya creaste en P1.
**Orden:** hijos antes que la fachada. La fachada (`ConfigureForEnterpriseEdition`)
no se toca hasta que sus 4 hijos estén estables.
**Por qué después:** criticidad menor (no tocan tantas hotspots, no están en
la cadena de carga).

### No-hacer (explícito)

- **No romper los `CALLS`.** El acoplamiento es orquestación sana (12 aristas).
- **No "aplanar anidamiento".** El máximo es 3; no es el problema.
- **No tocar los 30+ procs `Website.*`/`Integration.*` de baja complejidad**
  (cc 1, sin SQL dinámico). Están bien.

---

## 5. Orden de ejecución (resumen topológico)

```
P3 (hojas RLS)  ─→  P1 (Deactivate/Reactivate)  ─→  P2 (raíz ApplyDataLoadSimulation)
P4-hijos (4 Configuration_*)  ─→  P4-fachada (ConfigureForEnterpriseEdition)
```

Regla: **dentro de cada cadena, la hoja antes que el llamador.** La prioridad
entre cadenas la marca la criticidad (P1>P2 por radio de impacto; P4 al final
por menor criticidad). La complejidad total a reducir se concentra en **5
procedimientos** (cc≥7); los otros 42 no necesitan acción.

---

*Fuente: NodeStore de WideWorldImporters, generado por TSql Lineage Toolkit.
Métricas: `cyclomatic_complexity`, `total_steps`, `dynamic_sql_steps`,
`nesting_level`, aristas `CALLS`/`WRITES_TO`/`READS_FROM`, degree de tabla.
Heurística de criticidad explícita en §3 — ajústala y regenera.*
