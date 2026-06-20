# Análisis: `--nodestore` vs `graph_full.json` completo

Este documento recoge las pruebas hechas para validar el `--nodestore`
(`NodeStoreExporter.cs`): dos casos reales, midiendo **ficheros leídos,
bytes, nº de saltos/lookups y tiempo** para responder la misma pregunta con
(A) el grafo monolítico `graph_full.json` y (B) el nodestore (`*.nodes/`).

## Caso 1 — "¿Quién escribe en `Warehouse.StockItems`, directa e
indirectamente?" (WideWorldImporters, 47 objetos, 1384 nodos, 3365 relaciones)

### A) `graph_full.json` completo

1. Cargar y parsear el fichero entero (1384 nodos + 3365 relaciones).
2. Filtrar a mano las relaciones cuyo `StartNodeId`/`EndNodeId` mencionan
   `warehouse.stockitems` → 121 relaciones encontradas, sin agrupar por
   objeto ni resolver cadenas indirectas (`AFFECTS` vía otro procedimiento).

| ficheros | bytes leídos | tiempo |
|---|---|---|
| 1 (`graph_full.json`) | 1 514 721 (1.51 MB) | 194 ms |

### B) Nodestore

1. `index.json` (2 679 B) → esquema cerrado + stats + punteros.
2. `model.json` (71 923 B) → localiza `WideWorldImporters:table:warehouse.stockitems`
   (degree=23) y su `path`.
3. `shared/tables/..._stockitems_0814dcfd.json` (18 619 B) → `refs`
   particionados por los **12 objetos contribuyentes**, con `AFFECTS`
   indirectas ya resueltas (`via`, `hops`).

| ficheros | bytes leídos | tiempo |
|---|---|---|
| 3 | 93 221 (93 KB) | 30 ms |

**Resultado: 16.2x menos datos, ~6.5x más rápido**, y la respuesta ya viene
agrupada por objeto contribuyente (lo que en A habría que reconstruir
agrupando 121 relaciones planas a mano).

## Caso 2 — "¿Bajo qué condiciones se ejecuta `step18`?" (procedimiento
sintético `dbo.ProcessOrderWorkflow`, 100 nodos, 221 relaciones, anidamiento
de 4 niveles: `WHILE` → `IF_ELSE` → `IF` → `IF`)

`step18` es un `INSERT` (rama de backorder) anidado dentro de 4 condiciones:

```
WHILE: @@FETCH_STATUS = 0
  IF_ELSE: NOT (@AvailableQty IS NULL)
    IF: @AvailableQty < @RequiredQty
      IF: @ApprovalStatus = 'APPROVED'
        step18: INSERT ...
```

### A) `graph_full.json` completo

La condición está repartida en 4 nodos `:Rule` independientes, enlazados por
aristas `GOVERNS` (Step→Rule) y `NESTED_IN` (Rule→Rule). Reconstruir la
cadena completa requiere:

1. Cargar y parsear `graph.json` (100 nodos + 221 relaciones).
2. Buscar la arista `GOVERNS` cuyo `EndNodeId` es `#step18` → llega a
   `rule:IF:919e0771` ("@ApprovalStatus = 'APPROVED'"). **(1 lookup)**
3. Leer el nodo `Rule` 919e0771 para obtener su `expression`. **(1 lookup)**
4. Buscar la arista `NESTED_IN` que sale de 919e0771 → llega a
   `rule:IF:6a6b8490` ("@AvailableQty < @RequiredQty"). **(1 lookup)**
5. Leer el nodo `Rule` 6a6b8490. **(1 lookup)**
6. Repetir 2 veces más para `ca699726` ("NOT (@AvailableQty IS NULL)") y
   `4161cf00` ("@@FETCH_STATUS = 0", el `WHILE` raíz). **(4 lookups)**

| ficheros | bytes leídos | lookups/saltos | tiempo (1ª lectura, proceso frío) | tiempo medio (200 iter, en caliente) | RSS delta |
|---|---|---|---|---|---|
| 1 (`graph.json`) | 84 477 | **9** | 1.181 ms | 0.383 ms/iter | 1416 KB |

*Medido con Node 24 (`process.hrtime.bigint()` / `process.memoryUsage()`),
no estimado — script en `out/complex-sql/measure-caso2.js`. La "1ª lectura"
mide un proceso recién arrancado (sin cache de parseo ni JIT caliente); la
media en caliente sobre 200 iteraciones muestra el límite inferior una vez
amortizado el arranque.*

Resultado obtenido (en orden inverso, de dentro hacia fuera — hay que
invertirlo a mano para que tenga sentido como "ruta de condiciones"):

```
IF: @ApprovalStatus = 'APPROVED'
IF: @AvailableQty < @RequiredQty
IF_ELSE: NOT (@AvailableQty IS NULL)
WHILE: @@FETCH_STATUS = 0
```

### B) Nodestore

```
objects/TestWorkflowDb_dbo.ProcessOrderWorkflow/object.json
  -> owned.steps[] -> step18 -> properties.condition_path
```

Un único campo, ya en el orden correcto (de fuera hacia dentro):

```json
"condition_path": [
  "WHILE: @@FETCH_STATUS = 0",
  "IF_ELSE: NOT (@AvailableQty IS NULL)",
  "IF: @AvailableQty < @RequiredQty",
  "IF: @ApprovalStatus = 'APPROVED'"
]
```

| ficheros | bytes leídos | lookups/saltos | tiempo (1ª lectura, proceso frío) | tiempo medio (200 iter, en caliente) | RSS delta |
|---|---|---|---|---|---|
| 1 (`object.json`) | 73 355 | **1** | 0.676 ms | 0.287 ms/iter | 560 KB |

**Resultado medido: 9 saltos → 1, ~1.7x más rápido en frío / ~1.3x en
caliente, ~2.5x menos memoria (RSS delta)**, y el orden ya es el legible (de
la condición más externa a la más interna) sin necesidad de invertir la
cadena.

> Nota sobre las cifras anteriores de este caso (92 ms / 17 ms, "~5.4x más
> rápido"): eran una estimación, no una medición. Con tiempos reales en
> Node, la diferencia de **velocidad** en este caso concreto es modesta
> (ambos ficheros son pequeños y caben en cache de disco al instante) — la
> ventaja real del nodestore aquí no es la velocidad bruta, sino los
> **saltos** (9 → 1: un único campo ya resuelto en vez de recorrer 4 pares
> `GOVERNS`/`NESTED_IN` y reordenar el resultado a mano) y la **memoria**
> (~2.5x menos heap/RSS por no tener que cargar 221 relaciones cuando solo
> hacen falta los datos de un objeto).

### Medición con un agente real (no Node) resolviendo la misma pregunta

El nodestore existe para que lo lea un agente de IA, no un script — así que
la métrica que importa de verdad es cuántas llamadas a herramienta
(`Grep`/`Read`, "loops" de razonamiento) y cuánto contexto/tokens le cuesta a
un agente responder la pregunta, no microsegundos de E/S.

**Primer intento (descartado): contaminado.** Repetir la prueba con el
propio Claude que escribió este documento no es una medición válida — ya
tenía en su contexto de conversación los IDs exactos de la cadena de reglas
(`919e0771`, `6a6b8490`, `ca699726`, `4161cf00`) porque los acababa de
escribir él mismo en este mismo fichero. Sus "9 saltos" en el enfoque A eran
en realidad grep de *confirmación* sobre IDs ya conocidos, no un
descubrimiento real.

**Medición correcta: dos subagentes aislados, sin memoria de esta
conversación**, cada uno con una única instrucción ("encuentra la cadena de
condiciones de `#step18` en este fichero, sin conocimiento previo de su
contenido") y solo el fichero correspondiente:

| | Tool calls (loops) | Líneas de contenido leídas |
|---|---|---|
| A) `graph.json` completo, agente ciego | **8** (Read inicial + grep `#step18` + grep/lectura de cada Rule en la cadena, una por una) | ~120 |
| B) Nodestore, agente ciego | **1** (`Read` directo de `object.json`) | ~1018 (leyó el fichero entero de un golpe, en vez de grep dirigido a `condition_path`) |

**8x menos loops** — esa es la ventaja real y consistente: cada loop es una
ronda completa de razonamiento + llamada a herramienta + espera, el coste
caro de un agente. Pero el **contexto/líneas leídas no se mueve en la misma
dirección**: en esta ejecución el agente B leyó *más* líneas porque, al ser
un único fichero pequeño, simplemente lo cargó entero en una sola `Read` en
vez de hacer `Grep "condition_path"` (que habría bajado el coste a ~10
líneas). Conclusión honesta: el nodestore reduce de forma fiable el **número
de turnos** que necesita un agente cuando la respuesta vive en **un único
objeto** (un campo ya resuelto como `condition_path`); cuando la pregunta
obliga a abrir **varios objetos en cadena** (ver Caso 4, llamadas EXEC
anidadas), esa ventaja en turnos se diluye y el coste de contexto puede
incluso ir en contra del nodestore — un agente real tiende a leer cada
`object.json` completo en vez de extraer solo `edges_out`, y esos ficheros
son grandes porque incluyen todos los pasos/variables del procedimiento, no
solo sus llamadas.

## Caso 3 — Actualizar tras editar 1 de 47 objetos (coste de escritura, no de lectura; WideWorldImporters, mismo corpus que el Caso 1)

Los Casos 1 y 2 miden **coste de consulta** (responder una pregunta sobre un
grafo ya generado). Este caso mide algo distinto: **coste de actualización**
tras tocar el SQL de un único procedimiento, regenerando "las estadísticas".

### A) Regenerar `graph_full.json` completo

```
dotnet run -- input.json graph_full.json --columns
```

Reanaliza los 47 objetos (no hay parseo incremental de SQL) y vuelca el grafo
entero a un único fichero, sin importar que solo 1 de 47 haya cambiado.

| ficheros escritos | bytes escritos | tiempo |
|---|---|---|
| 1 (`graph_full.json`) | 1 538 349 (1.54 MB) | ~2.2-2.4 s* |

### B) `update-nodestore` incremental

```
dotnet run -- update-nodestore input.json graph_full.nodes --columns
```

También reanaliza los 47 objetos (mismo coste de parseo), pero compara el
`content_hash` de cada objeto/recurso compartido contra el manifest y
**solo reescribe lo que cambió de verdad**:

- Edición que no altera el análisis (p. ej. un comentario): `Updated: 0
  objects (47 unchanged)` → **0 bytes escritos** en `objects/`/`shared/`.
- Edición real (nuevo `SELECT`) en 1 objeto: `Updated: 1 objects (46
  unchanged)` → reescribe solo `object.json` de ese objeto (26.8 KB) +
  siempre refresca `manifest.json`/`index.json`/`model.json` (117.5 KB,
  son índices globales pequeños frente al grafo completo).

| ficheros escritos | bytes escritos | tiempo |
|---|---|---|
| 1 objeto + 3 índices | 144 386 (144 KB) | ~2.2-2.4 s* |

\* *Ambos modos tardan lo mismo en CPU: el cuello de botella es el arranque
de .NET y el reparseo completo de `input.json`, no la escritura. La ventaja
del nodestore aquí es de **I/O**, no de tiempo de proceso.*

| | Solo comentario (0 cambios de análisis) | Cambio real en 1/47 objetos |
|---|---|---|
| Bytes escritos A | 1.54 MB | 1.54 MB |
| Bytes escritos B | **0** | **144 KB** (26.8 KB el objeto en sí) |
| Mejora | ∞ (no se reescribe nada) | ~11x menos I/O total, ~57x si solo cuentas el fichero del objeto cambiado |

## Resumen Casos 1-3

| | Caso 1 (impacto en tabla, lectura) | Caso 2 (condición de un step, lectura) | Caso 3 (actualizar 1/47, escritura) |
|---|---|---|---|
| Ficheros A / B | 1 / 3 | 1 / 1 | 1 / 4 |
| Bytes A / B | 1.51 MB / 93 KB | 84.5 KB / 73.4 KB | 1.54 MB / 144 KB |
| Lookups/saltos A / B | (1 filtro sobre 3365) / 3 lecturas dirigidas | **9 / 1** | — |
| Tiempo A / B | 194 ms / 30 ms | 92 ms / 17 ms | ~2.3 s / ~2.3 s (igual; el ahorro es de I/O, no de CPU) |
| Mejora | 16.2x menos datos, 6.5x más rápido | 9x menos saltos, 5.4x más rápido | 11-57x menos bytes escritos |

En los Casos 1 y 2 el nodestore convierte una **búsqueda/reconstrucción
manual sobre el grafo plano** (filtrar relaciones, encadenar `NESTED_IN`
entre nodos `Rule`, invertir el orden) en una **lectura directa de un campo
ya resuelto** en el fichero del objeto o del nodo compartido correspondiente.
En el Caso 3 la ganancia es distinta: no se trata de leer menos para
responder una pregunta, sino de **escribir menos al actualizar**, porque
el nodestore solo persiste los ficheros cuyo contenido cambió de verdad
(detectado por hash), en vez de regenerar el grafo monolítico entero en
cada cambio.

## Caso 4 — Expandir la cadena de llamadas del SP más complejo real (lectura,
con el árbol de texto multi-nivel del dashboard ya corregido)

`DataLoadSimulation.Configuration_ApplyDataLoadSimulationProcedures` es el
procedimiento más complejo de WideWorldImporters (`cyclomatic_complexity=21`,
22 pasos, 125 869 caracteres de SQL real). Llama a 2 procedimientos que a su
vez llaman a otros, formando una cadena real de **4 niveles**:

```
Configuration_ApplyDataLoadSimulationProcedures
  → DeactivateTemporalTablesBeforeDataLoad
    → Application.Configuration_RemoveRowLevelSecurity
  → ReactivateTemporalTablesAfterDataLoad
    → Application.Configuration_ApplyRowLevelSecurity
```

Pregunta: "¿qué pasos se ejecutan en cadena al llamar a este procedimiento,
expandido hasta el 4º nivel?" — exactamente lo que resuelve el árbol de
texto (`FlowTree`, `dashboard/src/components.js`) tras la corrección que
expande cada `EXEC` recursivamente.

### A) `graph_full.json` completo

1. Cargar el fichero entero (1328 nodos, 3134 relaciones).
2. Filtrar `CALLS` cuyo `source` es la raíz → 2 destinos.
3. Para cada destino, repetir el filtro de `CALLS` (nivel 2) y luego filtrar
   `HAS_STEP` para listar los pasos de cada nivel — 4 pasadas más sobre el
   array de 3134 relaciones.

| ficheros | bytes leídos | pasadas de filtrado | tiempo |
|---|---|---|---|
| 1 (`graph_full.json`) | 1 538 349 (1.54 MB) | 5 (sobre 3134 relaciones cada vez) | ~2.1 s (parseo .NET) / <50 ms si ya está en memoria en JS |

### B) Nodestore

Cada `edges_out` de tipo `CALLS` ya trae el `path` del objeto destino — no
hay que buscar nada, solo seguir el puntero:

```
objects/.../Configuration_ApplyDataLoadSimulationProce/object.json
  edges_out[CALLS].path → objects/.../DeactivateTemporalTablesBeforeDataLoad/object.json
    edges_out[CALLS].path → objects/.../Configuration_RemoveRowLevelSecurity/object.json
  edges_out[CALLS].path → objects/.../ReactivateTemporalTablesAfterDataLoad/object.json
    edges_out[CALLS].path → objects/.../Configuration_ApplyRowLevelSecurity/object.json
```

| ficheros | bytes leídos | saltos | tiempo |
|---|---|---|---|
| 5 (1 raíz + 4 en la cadena) | 290 780 (284 KB) | **5** (uno por fichero, sin filtrar nada) | <20 ms |

**Resultado: 5.3x menos datos** (290 KB vs 1.54 MB) y los saltos pasan de
"filtrar repetidamente un array de 3134 relaciones" a "seguir 5 punteros
directos" — sin necesidad de buscar nada por id.

**Verificación en el dashboard real** (Playwright, `graph_full.json` de
WideWorldImporters, profundidad=4): el árbol de texto de
`Configuration_ApplyDataLoadSimulationProcedures` expande correctamente los
2 `EXEC` de nivel 1 (`DeactivateTemporalTablesBeforeDataLoad` y
`ReactivateTemporalTablesAfterDataLoad`) y, dentro de ellos, el `EXEC` de
nivel 2 hacia `Application.Configuration_RemoveRowLevelSecurity` — sin
errores JS y sin marcador de "nivel máximo alcanzado" (la cadena real solo
llega a profundidad 2, muy por debajo del límite de 4 elegido).

### Medición con agentes reales ciegos (sin contaminación de contexto)

Misma pregunta que el Caso 2 — pero aquí la respuesta no vive en *un* objeto,
sino que obliga a saltar de fichero en fichero siguiendo la cadena de
`CALLS`. Dos subagentes aislados, cada uno con una única instrucción
("reconstruye la cadena de llamadas transitiva hasta 4 niveles, sin
conocimiento previo") y solo el fichero/directorio correspondiente:

| | Tool calls (loops) | Líneas leídas |
|---|---|---|
| A) `graph_full.json` completo, agente ciego | **~6-9** (1 lectura inicial + greps dirigidos a `source=...` y `"CALLS"`) | ~1800-2000 |
| B) Nodestore, agente ciego | **~6-7** | ~3600 |

**Aquí la ventaja no se sostiene.** A diferencia del Caso 2 (8x menos loops),
con una cadena de llamadas los loops quedan **prácticamente iguales** entre
A y B, y el nodestore termina leyendo **más** líneas, no menos. Motivo: la
pregunta obliga a abrir 4-5 ficheros distintos en B (uno por nivel de la
cadena), y cada `object.json` es grande porque contiene **todos** los pasos
y variables del procedimiento, no solo sus `edges_out` de tipo `CALLS` — el
agente real lee el fichero completo en cada salto en vez de extraer solo el
campo que necesita (igual que en el Caso 2, pero aquí el efecto se repite
4-5 veces en cascada en vez de una sola). El JSON monolítico, en cambio, se
beneficia de un grep amplio (`source` + `"CALLS"`) que trae de una vez varias
aristas relevantes sin tener que abrir ficheros nuevos.

**Conclusión honesta del Caso 4:** la ventaja del nodestore para preguntas
de cadena de llamadas no es la reducción de loops/contexto que sí se observa
en una búsqueda de un único campo (Caso 2) — depende de que el consumidor
(humano o agente) siga los punteros `path` de forma quirúrgica en vez de
leer cada `object.json` entero.

### Intento de arreglo: ¿faltaba un dato, o faltaba documentar una garantía?

Antes de tocar el esquema, se verificó si realmente faltaba información.
Resultado: **no falta nada**. `model.json` ya consolida tanto las llamadas
estáticas (`CALLS`) como los efectos de SQL dinámico (`WRITES_TO`/
`READS_FROM`, deduplicados a escala objeto↔tabla incluso cuando el target
se infiere de un `EXEC(@SQL)` construido en tiempo de ejecución) — verificado
en `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad`: sus 34 pasos
`EXEC` dinámicos (`is_dynamic_sql=true`, `target_name='(dynamic SQL)'`)
colapsan en exactamente 17 aristas `WRITES_TO` en `model.json`, que cuadran
1:1 con el "Escribe en 17 tablas distintas" que muestra el dashboard.

Lo que sí faltaba era que `index.json.howto` **declarara explícitamente**
esa garantía. Se añadieron dos claves nuevas en `NodeStoreExporter.cs`:

- `completeness`: "`model.json` es exhaustivo para `CALLS`/`AFFECTS`/`FK_TO`
  y para `WRITES_TO`/`READS_FROM` a escala objeto/tabla — nunca hace falta
  abrir un `object.json` solo para buscar más aristas de estos tipos."
- `exec_resolution`: explica que un paso `EXEC` resuelve de dos formas (a
  una arista `CALLS` si es a un procedimiento con nombre, o a `WRITES_TO`/
  `READS_FROM` con `action_type` en `props` si es SQL dinámico) y que ambas
  ya están en `model.json`.

**Repetido el test ciego con el `howto` corregido: sin mejora.** Mismo
agente, misma pregunta, `index.json` ya con la garantía explícita — **9
tool calls, ~3500 líneas**, igual que antes (incluso ligeramente peor). El
agente **leyó y citó textualmente** la garantía ("let me verify this is
complete by cross-checking the model.json...") y aun así volvió a abrir cada
`object.json` uno por uno "para confirmarlo".

**Lección final, la más importante de este documento:** el problema no era
de datos (estaban completos) ni de documentación del esquema (ya se declaró
la garantía explícitamente) — es un patrón de comportamiento del agente:
ante una tarea enmarcada como "encuentra la cadena **completa**", verifica
por las dos vías disponibles aunque una ya le baste, sin que ningún texto en
el propio fichero pueda evitarlo. Documentar mejor el NodeStore no sustituye
a instruir mejor al agente (o a aceptar ese coste de verificación como
inherente a tareas que exigen certeza, no solo una respuesta plausible).

## Caso 5 — ¿El nº de pasos del JSON generado coincide con el SQL real y
entre `graph_full.json` y el nodestore?

Mismo procedimiento que el Caso 4 (`Configuration_ApplyDataLoadSimulationProcedures`,
22 pasos según el modelo):

| Fuente | Nº de pasos / instrucciones encontradas |
|---|---|
| `graph_full.json` (`HAS_STEP` desde la raíz) | **22** |
| `graph_full.nodes` (`object.json` → `owned.steps.length`) | **22** |
| Grep ingenuo `^\s*EXEC\b` sobre el SQL real (2770 líneas) | 24 |

`graph_full.json` y el nodestore coinciden exactamente (22 = 22) — son la
misma fuente de verdad, exportada en dos formatos distintos, sin pérdida de
información (consistente con el Caso de reconciliación de nodos/relaciones
documentado más abajo). El grep ingenuo sobre el texto SQL da **24**, dos de
más: el procedimiento construye SQL dinámico letra a letra en una variable
`@SQL` y ese texto dinámico contiene la palabra `EXEC` como parte del cuerpo
generado (no es una instrucción real del script exterior) — un grep no
distingue eso de un `EXEC` real, pero el parser AST (`ScriptDom`) sí, porque
entiende la gramática y solo cuenta las instrucciones que de verdad están en
el árbol de sintaxis del procedimiento. Las 22 instrucciones del modelo se
corresponden 1:1 con las 20 bloques `IF NOT EXISTS (...) BEGIN EXEC(@SQL)
END` + 2 `EXEC` sin condición (líneas 9 y 2769) que hay realmente en el
cuerpo del procedimiento.

## Conclusiones

| Caso | Pregunta | Métrica que de verdad cambia | Resultado |
|---|---|---|---|
| 1 | Impacto de una tabla (WideWorldImporters) | Bytes leídos | 16.2x menos datos, 6.5x más rápido |
| 2 | Condición de un step (campo en un único objeto) | **Loops de agente** (medido con dos subagentes ciegos) | **8x menos turnos** (8→1); tiempo/memoria reales son modestos (~1.3-1.7x) — el ahorro está en turnos, no en E/S |
| 3 | Actualizar tras editar 1 de 47 objetos | Bytes **escritos** | 11-57x menos I/O; 0 bytes si el cambio no altera el análisis. El tiempo de CPU es igual en ambos modos |
| 4 | Cadena de llamadas EXEC (4 niveles, varios objetos) | Loops de agente | **Sin ventaja** (6-9 vs 6-7); el nodestore incluso leyó más líneas porque cada salto abre un `object.json` completo |
| 5 | Integridad: ¿coincide el nº de pasos? | — | `graph_full.json` = nodestore = 22 exacto; el grep ingenuo sobre SQL sobreestima (24) porque no entiende sintaxis |

**Tres lecciones, no una sola cifra de marketing:**

1. **El nodestore no es uniformemente mejor — depende de la forma de la pregunta.** Gana de forma clara cuando la respuesta vive en *un* objeto (Casos 2, 3, 5: campo ya resuelto, hash de un solo fichero). Cuando la pregunta exige recorrer varios objetos en cadena (Caso 4), la ventaja se diluye o se invierte, porque cada salto reabre un fichero completo con todo el procedimiento, no solo el dato relevante.

2. **La métrica que importa depende de quién consume el dato.** Para un script (Node), lo que se mueve es tiempo de E/S y memoria — y ahí las diferencias son pequeñas porque los ficheros de este corpus son chicos. Para un **agente de IA** (el caso de uso real del nodestore), lo que de verdad cuesta es el **número de turnos de herramienta** — y ahí el nodestore gana 8x en el Caso 2 pero no gana nada en el Caso 4. Medir con Node cuando el consumidor real es un agente da una imagen equivocada de la ventaja.

3. **Las mediciones "a ojo" o contaminadas por contexto previo no son fiables.** La primera vuelta del Caso 2 (este mismo asistente repitiendo la búsqueda) dio un resultado artificialmente bueno porque ya conocía los IDs de la cadena de este mismo chat. Solo al aislar la prueba en subagentes sin memoria de la conversación los números se volvieron creíbles — y en el Caso 4 esa misma disciplina reveló que la ventaja no se sostiene, algo que las cifras estimadas originales (16-60x, sacadas de `docs/ai-agents.md`) no contemplaban.

**Implicación de diseño:** si el objetivo es que un agente resuelva preguntas de cadena de llamadas con el mismo ahorro de turnos que ya tiene la condición de un step, el NodeStore necesitaría un campo precomputado de "cadena de llamadas hasta N niveles" embebido en cada `object.json` (igual que ya existe `condition_path`), en vez de obligar al consumidor a seguir punteros `path` y abrir un objeto completo por cada salto.
