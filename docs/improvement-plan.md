# Plan de mejora estratégica de la base de datos — a partir de la lectura del NodeStore

Cada hallazgo de este plan viene de leer el NodeStore generado
(`out/graph_full.nodes/`) de WideWorldImporters — `index.json.stats`,
`model.json` y `object.json`/`shared/tables/*.json` puntuales — no de
intuición. Ordenado por **dependencias**: qué hay que confirmar/arreglar
antes de poder confiar en el siguiente paso.

---

## Fase 0 — Verificar antes de actuar (gating de todo lo demás)

### 0.1 Confirmar que `AstWalker.cs` no resuelve `FOR SYSTEM_TIME`

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

**Por qué esto bloquea todo lo demás:** mientras esto no se arregle, "degree
de una tabla/objeto" en el NodeStore está sub-contado para cualquier
procedimiento que consulte tablas temporales con `FOR SYSTEM_TIME` —
exactamente el patrón que usa toda la familia `Integration.Get*Updates` de
WideWorldImporters (es el mecanismo de captura de cambios de la BD de
ejemplo). Cualquier análisis de impacto (Fase 1, 2) hecho sobre estos datos
hoy *subestima* quién lee `Application.Cities`, `Sales.Customers`, etc.

**Acción:** extender `AstWalker.cs` para reconocer `FOR SYSTEM_TIME AS OF /
BETWEEN / FROM TO / CONTAINED IN / ALL` en una referencia de tabla del `FROM`
y emitir `READS_FROM` igual que una referencia normal (la tabla histórica
apunta a la misma tabla base, no a una entidad nueva).

*Depende de:* nada. *Bloquea:* Fases 1 y 2 enteras — son lecturas del mismo
NodeStore que hoy tiene este hueco.

### 0.2 Confirmar que las tablas `_Archive` con degree=0 son temporales del motor, no huérfanas

**Hallazgo:** 17 tablas tienen `degree=0` en `model.json`, todas con sufijo
`_Archive` (`Application.People_Archive`, `Sales.Customers_Archive`, etc.) +
una variable de sesión `#CurrentValue`. Ninguna aparece como destino de
`INSERT`/`UPDATE` en ningún procedimiento analizado.

**Lectura correcta (no es un bug del analizador ni de la BD):** son las
tablas de historial de *system-versioned temporal tables* de SQL Server — el
motor las puebla automáticamente en cada `UPDATE`/`DELETE` de la tabla
principal, nunca aparecen por nombre en T-SQL de aplicación. `degree=0` aquí
es la respuesta correcta, no una alarma.

**Acción (documentación, no código):** que el dashboard de riesgos no las
liste junto a tablas verdaderamente huérfanas — añadir una regla "si el
nombre coincide con `<Tabla>_Archive` y existe `<Tabla>` con degree>0,
clasificar como 'historial temporal, esperado' en vez de 'tabla sin uso
detectado'". Bajo esfuerzo, evita que el siguiente paso (auditoría de tablas
sin uso) pierda tiempo investigando falsos positivos ya conocidos.

*Depende de:* nada. *No bloquea* nada — es paralelo a 0.1.

---

## Fase 1 — Auditoría de la base de datos real (depende de 0.1 + 0.2 cerrados)

### 1.1 Auditar los 22/51 tablas (43%) sin `FK_TO` saliente

**Hallazgo:** de 51 tablas en el grafo, 22 no tienen ninguna arista `FK_TO`
saliente (no referencian a otra tabla por clave foránea detectada). Algunas
serán legítimamente hoja (tablas de lookup simples); otras pueden ser
relaciones que existen en SQL Server pero que el extractor no capturó, o
relaciones que *debieran* existir y no están declaradas como FK real (deuda
de integridad referencial).

**Acción:** cruzar esta lista contra el DDL real (`--tables` ya trae el
esquema) y clasificar cada una en: (a) lookup legítima sin FK saliente, (b)
FK real no detectada por el extractor (bug de extracción, arreglar en
`TableSchemaExtractor.cs`), (c) relación de negocio sin FK declarada en la BD
(deuda real de integridad, candidata a `ALTER TABLE ... ADD CONSTRAINT`).

*Depende de:* 0.1 (sin temporal-time leyendo bien, una tabla podría parecer
"sin relación" simplemente porque sus lectores reales no se ven todavía).

### 1.2 Revisar los hotspots de tabla como candidatos a contrato explícito

**Hallazgo**, top 5 tablas por degree (nº de procedimientos/aristas que las
tocan):

| Tabla | Degree |
|---|---|
| `Application.People` | 41 |
| `Sales.Customers` | 23 |
| `Warehouse.StockItems` | 22 |
| `Purchasing.Suppliers` | 15 |
| `Application.Cities` | 13 |

`Application.People` casi duplica a la segunda — es el hub real de la base de
datos (toda entidad de negocio referencia a una persona: empleado, cliente,
proveedor de contacto). Un cambio de esquema en `People` tiene el radio de
impacto más amplio de todo el sistema, medible directamente en
`shared/tables/<People>.json`'s `refs` (ya agrupado por objeto contribuyente).

**Acción:** para las tablas con degree por encima de, p.ej., 2x la mediana
(`People`, `Customers`, `StockItems`), generar y mantener un contrato de
esquema explícito (qué columnas son estables, cuáles están en deprecación) y
exigir que cualquier PR que las toque incluya la lectura de
`shared/tables/<tabla>.json` en la descripción del PR — ya tienes la
información agregada, falta el proceso que la use.

*Depende de:* 0.1 (el degree de `People`/`Customers` también está
sub-contado mientras los `Integration.Get*Updates` no resuelvan
`READS_FROM`; recalcular tras 0.1 antes de fijar el contrato).

### 1.3 Revisar los procedimientos con mayor complejidad ciclomática y SQL dinámico

**Hallazgo**, vía `cyclomatic_complexity` y conteo de steps con
`is_dynamic_sql=true` en cada `object.json`:

| Procedimiento | CC | Steps | Steps dinámicos |
|---|---|---|---|
| `DataLoadSimulation.Configuration_ApplyDataLoadSimulationProcedures` | 21 | 22 | 20 |
| `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad` | 19 | 69 | 34 |
| `Application.Configuration_ApplyPartitioning` | 12 | 22 | 20 |
| `Application.Configuration_EnableInMemory` | 7 | 31 | 29 |
| `Application.Configuration_ApplyFullTextIndexing` | 7 | 15 | 15 |

Estos 5 concentran la complejidad y casi toda la deuda de SQL dinámico de la
base (90%+ de los steps dinámicos del corpus). Son los procedimientos de
"configuración de features" de la demo (activar/desactivar partitioning,
in-memory, full-text, auditing) — generan DDL letra a letra para iterar sobre
N tablas, de ahí la complejidad: el patrón en sí (DDL parametrizado por
tabla) es razonable, pero `Configuration_ApplyDataLoadSimulationProcedures`
con CC=21 y `DeactivateTemporalTablesBeforeDataLoad` con 69 steps son los
candidatos reales a romper en sub-procedimientos por tabla/responsabilidad si
esto fuera código de producción a mantener, no una demo.

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
