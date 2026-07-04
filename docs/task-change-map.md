# Tarea: `change_map.json` — workflows + impacto precalculado (Capa 6)

**Spec de autoridad:** Tarea J en `agent-collab.md` (P1-P7, diseño consolidado
Claude+Gemini). Este doc la aterriza a implementación con las discrepancias
spec↔grafo real resueltas. Ejecuta: **Fable** (motor). Estado: EN CURSO 2026-07-04.

## Qué es

Fichero en la raíz del nodestore (junto a `audit_report.json`) con dos secciones:

- **`workflows`**: caminos dirigidos por `CALLS` desde entry points (in-degree=0
  en el subgrafo CALLS SqlObject→SqlObject) hasta hojas (out-degree=0), un path
  por rama, con conditionalidad por hop (P1, P2).
- **`impact`**: por SqlObject, clausura transitiva `via_calls` (con depth y
  condición) y `via_data` (tabla escrita → SqlObjects lectores) (P3).

Objetivo agent-first: responder "¿qué se ejecuta desde aquí?" y "¿a quién
impacto?" en **una lectura**, sin encadenar nav.json (mismo precedente que
`lineage_path.json` / Caso 7 de nodestore-analysis).

## Discrepancias spec ↔ grafo real (resueltas aquí, prevalece esto)

1. **P2 dice `BusinessRule --GOVERNS-->`**: en el grafo real quien gobierna
   steps es `:Rule` (control de flujo IF/WHILE); `:BusinessRule` son constraints
   DDL y NO gobiernan steps. → La conditionalidad se deriva del **Step**, no de
   Rule: un hop A→B es condicional si el step de A que origina la llamada tiene
   `condition_path` no vacío. `condition` = última entrada del path;
   `condition_stack` = path completo. (El texto ya está en el Step; no hay que
   tocar Rule/GOVERNS.)
2. **P2 dice `CONDITIONED_BY` para el stack**: en el grafo real esa arista es
   columna→columna (filtros). Ignorar; el stack sale de `condition_path`.
3. **Ligar hop CALLS → step**: las aristas `CALLS` no llevan step (verificado,
   GraphExporter ~781-825). Resolución: buscar steps del caller con arista
   `TARGETS` → callee (cualquier action). Si hay varios steps al mismo callee,
   el hop es condicional solo si TODOS lo son (elige el de menor
   `condition_path` como representativo — la llamada "más incondicional" manda).
   Si no hay step con TARGETS (típico de `kind=FUNCTION`, invocadas dentro de un
   SELECT): `conditional=false, condition=null` (default honesto, documentado).
4. **P1/P5, triggers**: entry points válidos como *etiqueta* pero excluidos del
   subgrafo workflows v1 — leer P5 literal: workflows/via_calls solo
   PROCEDURE/FUNCTION (filtrar por `object_type`).

## Implementación (patrón AuditExporter, P7)

- `src/TSqlParser/ChangeMapExporter.cs`:
  `Generate(GraphPayload, lineageCache, jsonOptions) → string`.
- `NodeStoreExporter.Build()`: Capa 6 tras `AuditExporter.Generate()`;
  `BuildResult.ChangeMapJson`; `WriteAll()` y `Update()` lo escriben
  incondicionalmente (caché denormalizada, igual que audit_report).
- `AuditVerifier` (con `--verify-audit`): `workflows` array con
  `entry`/`entry_type`/`paths`; `impact` object.
- Ciclos (P4): DFS con visited-in-path; back-edge → corta path y marca
  `cycle_back_to`; en `via_calls`, primera recurrencia → `cycle_entry: true`.
- P6: rollup total Steps→owner; ningún `#step_N` en el JSON.
- ⚠️ `change_map.json` lleva timestamp NO: sin `generated_at` propio (lección
  del flaky del Paso 0 — el timestamp global ya vive en index.json).

## Tests previstos

1. Cadena A→B→C incondicional: 1 workflow, 2 hops, conditional=false.
2. Llamada bajo IF: hop conditional=true con condition/condition_stack del Step.
3. Ciclo A→B→A: path cortado con `cycle_back_to`, `cycle_entry` en via_calls.
4. `via_data`: A escribe T, B y C leen T → consumers [B, C].
5. Trigger: no aparece como entry de workflows v1; sí en impact si procede.
6. Update incremental: change_map.json se refresca (mismo patrón que audit).
7. Gate de humo sobre WWI real: entry points esperados > 0, `Website.InvoiceCustomerOrders`
   presente con via_calls no vacío.

## Verificación de cierre

- Suite completa en verde (hoy 108: 106 + 2 Oracle) + los nuevos.
- Gates: bad-practices OK=38, community-edge-cases, Oracle WWI.
- `change_map.json` real de WWI generado e inspeccionado (números en el CERRADO).
- Sin regresión de ids ni de ficheros existentes del nodestore (invariante 4-Q4).
