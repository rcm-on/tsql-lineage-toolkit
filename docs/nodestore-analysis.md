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

| ficheros | bytes leídos | lookups/saltos | tiempo |
|---|---|---|---|
| 1 (`graph.json`) | 84 480 | **9** | 92 ms |

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

| ficheros | bytes leídos | lookups/saltos | tiempo |
|---|---|---|---|
| 1 (`object.json`) | 73 358 | **1** | 17 ms |

**Resultado: 9 saltos → 1, ~5.4x más rápido**, y el orden ya es el legible
(de la condición más externa a la más interna) sin necesidad de invertir la
cadena.

## Conclusión

| | Caso 1 (impacto en tabla) | Caso 2 (condición de un step) |
|---|---|---|
| Ficheros leídos A / B | 1 / 3 | 1 / 1 |
| Bytes A / B | 1.51 MB / 93 KB | 84.5 KB / 73.4 KB |
| Lookups/saltos A / B | (1 filtro sobre 3365) / 3 lecturas dirigidas | **9 / 1** |
| Tiempo A / B | 194 ms / 30 ms | 92 ms / 17 ms |
| Mejora | 16.2x menos datos, 6.5x más rápido | 9x menos saltos, 5.4x más rápido |

En ambos casos el nodestore convierte una **búsqueda/reconstrucción manual
sobre el grafo plano** (filtrar relaciones, encadenar `NESTED_IN` entre
nodos `Rule`, invertir el orden) en una **lectura directa de un campo ya
resuelto** en el fichero del objeto o del nodo compartido correspondiente.
Esto es justo el objetivo de diseño: que un agente resuelva preguntas de
varios saltos leyendo un puñado de ficheros pequeños, con la información ya
estructurada, en vez de cargar y recorrer `graph_full.json` entero.
